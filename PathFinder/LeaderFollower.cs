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

    // Background A* task
    private Task<List<Vector2i>?>? _searchTask;
    private CancellationTokenSource _cts = new();

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
        CancelSearch();
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
        // Ensure terrain is populated (throttled to 500 ms, no-op if fresh)
        LineOfSight.EnsureTerrainData();

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
                // Convert grid → world on main thread, seed trail
                _trail.Clear();
                foreach (var g in gridPath)
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

        // 7. String-pull: find the furthest visible trail point (LOS shortcut)
        int bestIdx = -1;
        for (int i = _trail.Count - 1; i >= 0; i--)
        {
            if (LineOfSight.HasLineOfSightRaw(playerPos, _trail[i]))
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
