using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AreWeThereYet.Utils;
using GameOffsets.Native;
using SharpDX;

namespace AreWeThereYet.PathFinder;

// ---------------------------------------------------------------------------
// Result type returned by LeaderFollower.Tick each frame
// ---------------------------------------------------------------------------

public enum FollowResultType
{
    /// <summary>Already in position — no movement needed.</summary>
    Idle,
    /// <summary>Move to the given world position along the trail.</summary>
    MoveTo,
    /// <summary>Leader jumped (portal). Walk to WorldPosition (last trail point).</summary>
    PortalSuspected,
}

public readonly struct FollowResult
{
    public FollowResultType Type { get; init; }
    public Vector3 WorldPosition { get; init; }

    public static readonly FollowResult Idle = new() { Type = FollowResultType.Idle };

    public static FollowResult MoveTo(Vector3 pos) =>
        new() { Type = FollowResultType.MoveTo, WorldPosition = pos };

    public static FollowResult PortalSuspected(Vector3 pos) =>
        new() { Type = FollowResultType.PortalSuspected, WorldPosition = pos };
}

// ---------------------------------------------------------------------------
// LeaderFollower: breadcrumb trail + LOS string-pull + background A* fallback
// ---------------------------------------------------------------------------

public class LeaderFollower
{
    // Breadcrumb trail: world positions, oldest [0] → newest [^1]
    private readonly List<Vector3> _trail = new();

    // Portal detection state
    private bool _portalSuspected;
    private Vector3 _portalPos;

    // Backstop teleport detection: a large player-position jump with no area change
    // (checkpoint release, town portal, lab transfer) should drop the stale trail.
    private Vector3? _lastPlayerPos;

    // Background A* task
    private Task<List<Vector2i>?>? _searchTask;
    private CancellationTokenSource _cts = new();

    // Throttle for invalid-position diagnostics (avoid per-frame log spam)
    private DateTime _lastInvalidPosLog = DateTime.MinValue;

    // ---- Accessors --------------------------------------------------------

    private LineOfSight LineOfSight => AreWeThereYet.Instance.lineOfSight;

    private AutoPilotSettings.PathfindingSettings PfSettings =>
        AreWeThereYet.Instance.Settings.AutoPilot.Pathfinding;

    private int KeepWithinDistance =>
        AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value;

    private int TransitionDistance =>
        AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value;

    // -----------------------------------------------------------------------

    /// <summary>
    /// Call on area change: clears trail, cancels any in-flight search.
    /// </summary>
    public void Reset()
    {
        _trail.Clear();
        _portalSuspected = false;
        _portalPos = Vector3.Zero;
        _lastPlayerPos = null;
        CancelSearch();
    }

    /// <summary>
    /// Rejects positions that indicate a failed/garbage memory read: NaN components,
    /// an exact Vector3.Zero, or any value that quantises to grid (0,0). These are the
    /// signatures of a transient read failure, which would otherwise be baked into the
    /// breadcrumb trail as an off-map waypoint. A legitimate position at the extreme
    /// map-edge corner would also be rejected, but that just harmlessly skips one tick.
    /// </summary>
    private static bool IsValidPosition(Vector3 p)
    {
        if (float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z)) return false;
        if (p == Vector3.Zero) return false;
        var g = Helper.ToGrid(p);
        return g.X > 0 && g.Y > 0;
    }

    private void CancelSearch()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        _searchTask = null;
    }

    // -----------------------------------------------------------------------
    // Main entry point — call once per tick while followTarget != null
    // -----------------------------------------------------------------------

    /// <summary>
    /// Records the leader's trail point and returns the next follow action.
    /// Must be called on the main thread.
    /// </summary>
    public FollowResult Tick(Vector3 playerPos, Vector3 leaderPos)
    {
        // 0a. Validate positions BEFORE anything touches them. A failed memory read of
        //     Entity.Pos returns Vector3.Zero, which Helper.ToGrid maps to grid (0,0) —
        //     the map's top-left corner. Recording that as a breadcrumb, or pathing to
        //     it, produces a waypoint thousands of units off-map. On a bad read we skip
        //     the frame entirely (no trail mutation, no A*) and re-acquire next tick.
        if (!IsValidPosition(playerPos) || !IsValidPosition(leaderPos))
        {
            if ((DateTime.Now - _lastInvalidPosLog).TotalMilliseconds >= 500)
            {
                _lastInvalidPosLog = DateTime.Now;
                AreWeThereYet.Instance.LogMessage(
                    $"LeaderFollower: invalid position read — skipping tick " +
                    $"(player={playerPos}, leader={leaderPos}). A failed Pos read " +
                    $"defaults to Vector3.Zero => grid (0,0) = map corner.");
            }
            return FollowResult.Idle;
        }

        // Ensure terrain is populated (throttled to 500 ms, no-op if fresh)
        LineOfSight.EnsureTerrainData();

        // 0. Teleport backstop: if the player jumped further than a normal step in a
        //    single tick (e.g. respawned at a checkpoint without an area change), the
        //    existing trail no longer connects to us — drop it and re-acquire via A*.
        if (_lastPlayerPos is Vector3 lastPos &&
            Vector3.Distance(lastPos, playerPos) >= TransitionDistance)
        {
            Reset();
        }
        _lastPlayerPos = playerPos;

        // 1. Record trail
        RecordTrail(leaderPos);

        float distToLeader = Vector3.Distance(playerPos, leaderPos);

        // 2. Early-exit: in position and have line-of-sight
        if (distToLeader <= KeepWithinDistance &&
            LineOfSight.HasLineOfSightRaw(playerPos, leaderPos))
        {
            return FollowResult.Idle;
        }

        // 3. Portal suspected: walk to the portal location
        if (_portalSuspected)
            return FollowResult.PortalSuspected(_portalPos);

        // 4. Consume completed A* result (main thread, safe to call GetTerrainHeightAt)
        if (_searchTask is { IsCompleted: true })
        {
            if (_searchTask.IsCompletedSuccessfully && _searchTask.Result is { Count: > 0 } gridPath)
            {
                // Smooth the raw grid path (collapse staircases into straight/diagonal
                // segments), then convert grid → world on main thread and seed trail.
                var smoothed = SmoothGridPath(gridPath);
                _trail.Clear();
                foreach (var g in smoothed)
                    _trail.Add(Helper.ToWorld(g));
            }
            else if (_searchTask.IsCompletedSuccessfully && _searchTask.Result == null)
            {
                // Unreachable — corroborates portal hypothesis
                if (_trail.Count > 0)
                {
                    _portalSuspected = true;
                    _portalPos = _trail[^1];
                    _searchTask = null;
                    return FollowResult.PortalSuspected(_portalPos);
                }
            }
            _searchTask = null;
        }

        // 5. Prune trail points that have been reached
        int reachedBounds = PfSettings.ReachedBounds.Value;
        while (_trail.Count > 0 && Vector3.Distance(playerPos, _trail[0]) <= reachedBounds)
            _trail.RemoveAt(0);

        // 6. Far-behind check: trail head too distant → A* acquisition
        if (_trail.Count == 0 ||
            Vector3.Distance(playerPos, _trail[0]) > PfSettings.AcquireDistance.Value)
        {
            if (_searchTask == null || _searchTask.IsCompleted)
                RequestAstar(playerPos, leaderPos);
            return FollowResult.Idle;
        }

        // 7. String-pull: find the furthest trail point reachable by a straight
        //    WALKABLE line. We must use walkability (not line-of-sight) here: LOS
        //    permits see-through/dashable gaps (e.g. a cliff), and shortcutting the
        //    breadcrumb trail across one would send the follower off the real route.
        int bestIdx = -1;
        for (int i = _trail.Count - 1; i >= 0; i--)
        {
            if (LineOfSight.HasWalkableLineRaw(playerPos, _trail[i]))
            {
                bestIdx = i;
                break;
            }
        }

        if (bestIdx > 0)
            _trail.RemoveRange(0, bestIdx); // discard skipped breadcrumbs

        // Return the closest visible point (or trail head if no LOS anywhere)
        return FollowResult.MoveTo(_trail[0]);
    }

    // -----------------------------------------------------------------------
    // Trail recording
    // -----------------------------------------------------------------------

    private void RecordTrail(Vector3 leaderPos)
    {
        if (_trail.Count == 0)
        {
            _trail.Add(leaderPos);
            return;
        }

        float moved = Vector3.Distance(leaderPos, _trail[^1]);

        // Large jump → leader teleported through a portal
        if (moved >= TransitionDistance && !_portalSuspected)
        {
            _portalSuspected = true;
            _portalPos = _trail[^1]; // position just before the jump
        }

        // Add a new breadcrumb if the leader has moved far enough
        if (moved >= PfSettings.TrailPointSpacing.Value)
        {
            _trail.Add(leaderPos);

            // Safety cap: keep only the tail of the trail
            int max = PfSettings.MaxTrailPoints.Value;
            if (_trail.Count > max)
                _trail.RemoveRange(0, _trail.Count - max);
        }
    }

    // -----------------------------------------------------------------------
    // Path smoothing (string-pull the raw A* grid path)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Collapses a dense grid path into a minimal set of waypoints by skipping to the
    /// furthest later cell still reachable by a straight WALKABLE line. Removes the
    /// "right then up" staircases A* produces on equal-cost open terrain; a pure
    /// diagonal collapses to a single start→end segment.
    /// </summary>
    private List<Vector2i> SmoothGridPath(List<Vector2i> path)
    {
        if (path.Count <= 2) return path;

        var result = new List<Vector2i> { path[0] };
        int anchor = 0;

        while (anchor < path.Count - 1)
        {
            int next = anchor + 1; // guaranteed progress (adjacent cells are walkable)
            for (int j = path.Count - 1; j > anchor; j--)
            {
                if (LineOfSight.HasWalkableLineGrid(path[anchor], path[j]))
                {
                    next = j;
                    break;
                }
            }
            result.Add(path[next]);
            anchor = next;
        }

        return result;
    }

    // -----------------------------------------------------------------------
    // Background A* fallback
    // -----------------------------------------------------------------------

    private void RequestAstar(Vector3 startWorld, Vector3 goalWorld)
    {
        var terrain = LineOfSight.GetTerrainData();
        if (terrain == null) return;

        // Snapshot grid coords on main thread (simple arithmetic, safe either way)
        var startGrid = Helper.ToGrid(startWorld);
        var goalGrid  = Helper.ToGrid(goalWorld);

        Func<int, bool> isPathable = v => v is 1 or 5;
        int nodeBudget = PfSettings.NodeBudget.Value;
        var ct = _cts.Token;

        // Background thread: pure A* computation, no GameController access
        _searchTask = Task.Run(
            () => AStar.FindPath(terrain, startGrid, goalGrid, isPathable, nodeBudget, ct),
            ct);
    }
}
