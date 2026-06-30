using System;
using System.Collections.Generic;
using System.Text;
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

    // Reachability / portal state. Portals are inferred from PATH EXISTENCE, not from
    // any distance heuristic: when no walkable route to the leader exists we assume a
    // portal and fall back to the last location from which the leader WAS reachable.
    private bool _portalSuspected;
    private Vector3? _lastReachablePos;

    // Backstop teleport detection: a large player-position jump with no area change
    // (checkpoint release, town portal, lab transfer) should drop the stale trail.
    private Vector3? _lastPlayerPos;

    // Background A* task
    private Task<List<Vector2i>?>? _searchTask;
    private CancellationTokenSource _cts = new();

    // Goal grid of the most recent A* request — used to label a result as reaching the
    // leader vs a partial (budget-limited) path that stops short.
    private Vector2i _lastAstarGoal;

    // Throttle for invalid-position diagnostics (avoid per-frame log spam)
    private DateTime _lastInvalidPosLog = DateTime.MinValue;

    // Throttle for the per-tick debug summary
    private DateTime _lastTickLog = DateTime.MinValue;

    // ---- Debug logging ----------------------------------------------------

    private static bool LogEnabled =>
        AreWeThereYet.Instance.Settings.Debug.LogPathfinding.Value;

    private static void DebugLog(string msg) =>
        AreWeThereYet.Instance.LogMessage("[ATY-PF] " + msg);

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
        _lastReachablePos = null;
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

    /// <summary>
    /// One concise line per interval: result type, our position, the leader's position,
    /// distance, trail length, and the waypoint we're about to move to — each in both
    /// world and grid space with the terrain value at that cell (0=wall, 2=dashable,
    /// 5=walkable). A target with a very low X or a terrain value of 0 is the smoking
    /// gun for the "runs into the wall on the left" symptom.
    /// </summary>
    private void LogTickSummary(Vector3 player, Vector3 leader, FollowResult r)
    {
        int interval = AreWeThereYet.Instance.Settings.Debug.PathfindingLogInterval.Value;
        if ((DateTime.Now - _lastTickLog).TotalMilliseconds < interval) return;
        _lastTickLog = DateTime.Now;

        var pg = Helper.ToGrid(player);
        var lg = Helper.ToGrid(leader);
        float dist = Vector3.Distance(player, leader);

        string target = "-";
        if (r.Type != FollowResultType.Idle)
        {
            var tg = Helper.ToGrid(r.WorldPosition);
            target = $"world({r.WorldPosition.X:F0},{r.WorldPosition.Y:F0}) grid({tg.X},{tg.Y}) " +
                     $"tVal={LineOfSight.GetTerrainValueAt(tg)}";
        }

        DebugLog(
            $"{r.Type} | player world({player.X:F0},{player.Y:F0}) grid({pg.X},{pg.Y}) " +
            $"tVal={LineOfSight.GetTerrainValueAt(pg)} | " +
            $"leader world({leader.X:F0},{leader.Y:F0}) grid({lg.X},{lg.Y}) " +
            $"tVal={LineOfSight.GetTerrainValueAt(lg)} | " +
            $"dist={dist:F0} trail={_trail.Count} target={target}");
    }

    /// <summary>
    /// Formats an A* result for the log: raw/smoothed node counts plus the smoothed
    /// waypoints (capped) in grid space, so we can see exactly where the path heads.
    /// The smoothed path is small (a handful of points), so this stays bounded.
    /// </summary>
    private static string FormatGridPath(List<Vector2i> raw, List<Vector2i> smoothed)
    {
        const int cap = 24;
        var sb = new StringBuilder();
        sb.Append($"raw={raw.Count} smoothed={smoothed.Count}: ");
        int n = Math.Min(smoothed.Count, cap);
        for (int i = 0; i < n; i++)
            sb.Append($"({smoothed[i].X},{smoothed[i].Y})");
        if (smoothed.Count > cap) sb.Append("...");
        return sb.ToString();
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
        var result = TickInternal(playerPos, leaderPos);
        if (LogEnabled) LogTickSummary(playerPos, leaderPos, result);
        return result;
    }

    private FollowResult TickInternal(Vector3 playerPos, Vector3 leaderPos)
    {
        // 0a. Validate positions BEFORE anything touches them. A failed memory read of
        //     Entity.Pos returns Vector3.Zero, which Helper.ToGrid maps to grid (0,0) —
        //     the map's top-left corner. Recording that as a breadcrumb, or pathing to
        //     it, produces a waypoint thousands of units off-map. On a bad read we skip
        //     the frame entirely (no trail mutation, no A*) and re-acquire next tick.
        if (!IsValidPosition(playerPos) || !IsValidPosition(leaderPos))
        {
            if (LogEnabled && (DateTime.Now - _lastInvalidPosLog).TotalMilliseconds >= 500)
            {
                _lastInvalidPosLog = DateTime.Now;
                DebugLog(
                    $"INVALID POS — skipping tick (player={playerPos}, leader={leaderPos}). " +
                    $"A failed Pos read defaults to Vector3.Zero => grid (0,0) = map corner.");
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

        // 1. Record breadcrumb trail. No portal heuristic here any more — whether the
        //    leader is behind a portal is decided purely by whether a path to them
        //    exists (steps 5–8), not by how far they jumped.
        RecordTrail(leaderPos);

        float distToLeader = Vector3.Distance(playerPos, leaderPos);

        // 2. In position with line-of-sight → nothing to do; leader is reachable.
        if (distToLeader <= KeepWithinDistance &&
            LineOfSight.HasLineOfSightRaw(playerPos, leaderPos))
        {
            _lastReachablePos = leaderPos;
            _portalSuspected = false;
            return FollowResult.Idle;
        }

        // 3. Consume a completed A* search. A path means the leader is reachable (seed
        //    the trail with it); a null result means NO walkable route exists right now
        //    — the leader most likely took a portal/transition.
        if (_searchTask is { IsCompleted: true })
        {
            if (_searchTask.IsCompletedSuccessfully && _searchTask.Result is { Count: > 0 } gridPath)
            {
                var smoothed = SmoothGridPath(gridPath);
                _trail.Clear();
                foreach (var g in smoothed)
                    _trail.Add(Helper.ToWorld(g));

                _portalSuspected = false;
                if (LogEnabled)
                {
                    var end = gridPath[^1];
                    bool reached = end.X == _lastAstarGoal.X && end.Y == _lastAstarGoal.Y;
                    DebugLog($"A* PATH {(reached ? "FOUND (reaches leader)" : "PARTIAL (budget; closest approach — will re-path)")}: "
                             + FormatGridPath(gridPath, smoothed));
                }
            }
            else if (_searchTask.IsCompletedSuccessfully && _searchTask.Result == null)
            {
                _portalSuspected = true;
                if (LogEnabled) DebugLog("A* NO PATH — leader unreachable (portal suspected)");
            }
            _searchTask = null;
        }

        // 4. Prune trail points we've already reached.
        int reachedBounds = PfSettings.ReachedBounds.Value;
        while (_trail.Count > 0 && Vector3.Distance(playerPos, _trail[0]) <= reachedBounds)
            _trail.RemoveAt(0);

        // 5. Is any part of the route reachable by a straight WALKABLE line? Walkability
        //    (not line-of-sight) is required: LOS permits see-through/dashable gaps that
        //    aren't actually walkable. The furthest reachable breadcrumb is both our next
        //    waypoint AND our "last known valid (reachable) location" toward the leader.
        int bestIdx = -1;
        for (int i = _trail.Count - 1; i >= 0; i--)
        {
            if (LineOfSight.HasWalkableLineRaw(playerPos, _trail[i]))
            {
                bestIdx = i;
                break;
            }
        }

        if (bestIdx >= 0)
        {
            // A valid path exists → follow it. The leader is reachable, so clear any
            // portal suspicion and remember this reachable spot.
            _lastReachablePos = _trail[bestIdx];
            _portalSuspected = false;
            if (bestIdx > 0)
                _trail.RemoveRange(0, bestIdx); // discard skipped breadcrumbs

            // If the leader has outrun the breadcrumb trail, refresh it with a real A*
            // path in the background before we run out of reachable points.
            if (distToLeader > PfSettings.AcquireDistance.Value &&
                (_searchTask == null || _searchTask.IsCompleted))
                RequestAstar(playerPos, leaderPos);

            return FollowResult.MoveTo(_trail[0]);
        }

        // 6. Nothing on the trail is reachable. Ask A* whether ANY route to the leader
        //    exists — it may find one the breadcrumb trail doesn't capture.
        if (_searchTask == null || _searchTask.IsCompleted)
            RequestAstar(playerPos, leaderPos);

        // 7. No reachable route. If we have a last known reachable location, head there:
        //    that's where the leader was before we lost the path (i.e. by the portal).
        //    PortalSuspected lets AutoPilot look for the portal label once we arrive;
        //    if A* hasn't answered yet we still walk there as a plain move.
        if (_lastReachablePos is Vector3 lastReachable)
            return _portalSuspected
                ? FollowResult.PortalSuspected(lastReachable)
                : FollowResult.MoveTo(lastReachable);

        // 8. No path and no last known location → wait. The existing AutoPilot zone-change
        //    logic clicks the leader's portal once they fully change areas.
        return FollowResult.Idle;
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

        // Add a new breadcrumb if the leader has moved far enough. (No portal heuristic
        // here — a large jump no longer implies a portal; path existence decides that.)
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
        _lastAstarGoal = goalGrid;

        Func<int, bool> isPathable = v => v is 1 or 5;
        int nodeBudget = PfSettings.NodeBudget.Value;
        var ct = _cts.Token;

        if (LogEnabled)
            DebugLog($"A* request: start grid({startGrid.X},{startGrid.Y}) " +
                     $"goal grid({goalGrid.X},{goalGrid.Y}) " +
                     $"startVal={LineOfSight.GetTerrainValueAt(startGrid)} " +
                     $"goalVal={LineOfSight.GetTerrainValueAt(goalGrid)}");

        // Background thread: pure A* computation, no GameController access
        _searchTask = Task.Run(
            () => AStar.FindPath(terrain, startGrid, goalGrid, isPathable, nodeBudget, ct),
            ct);
    }
}
