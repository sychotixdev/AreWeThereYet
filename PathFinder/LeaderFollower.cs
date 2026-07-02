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

    // Reachability / portal state. Portals are inferred from PATH EXISTENCE, not from
    // any distance heuristic: when no walkable route to the leader exists we assume a
    // portal and fall back to the last location from which the leader WAS reachable.
    private bool _portalSuspected;
    private Vector3? _lastReachablePos;

    // Backstop teleport detection: a large player-position jump with no area change
    // (checkpoint release, town portal, lab transfer) should drop the stale trail.
    private Vector3? _lastPlayerPos;

    // Background JPS task
    private Task<PathResult>? _searchTask;
    private CancellationTokenSource _cts = new();

    // Goal grid of the most recent A* request — used to label a result as reaching the
    // leader vs a partial (budget-limited) path that stops short.
    private Vector2i _lastAstarGoal;

    // Throttle for background A* requests: prevents a fresh search firing (and the
    // trail being re-anchored) nearly every tick when the target is far away.
    private DateTime _lastAstarRequestTime = DateTime.MinValue;

    // The target world position used for the last background search request. Compared
    // against the current target in ShouldRefreshAstar so a healthy trail isn't
    // re-searched just because time passed - only when the target actually moved
    // meaningfully (or the trail is running low).
    private Vector3? _lastAstarRequestGoal;

    // Set at the end of the current tick's trail scan (step 5): true when NOTHING on
    // the trail was reachable, even after the clearance=0 fallback. Read at the START
    // of the NEXT tick's step 3 to force an A* refresh — otherwise the "only refresh
    // when the trail is nearly exhausted" guard (added to stop constant re-anchoring)
    // would leave us stuck re-scanning the same dead trail forever.
    private bool _trailUnreachableThisTick;

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

    /// <summary>True when we still have breadcrumb points to consume.</summary>
    public bool HasTrail => _trail.Count > 0;

    /// <summary>
    /// The newest breadcrumb (where the leader was last recorded). After the leader zones
    /// this is effectively the portal location, so it's a valid approach target even before
    /// the transition label becomes visible.
    /// </summary>
    public Vector3? TrailEnd => _trail.Count > 0 ? _trail[^1] : (Vector3?)null;

    /// <summary>
    /// Sum of segment lengths from playerPos through every remaining trail point, in order.
    /// This is the actual walking distance left on the breadcrumb path - not the straight-line
    /// distance to TrailEnd, which understates a winding route (stairs, doorways, terrain that
    /// forced the trail to bend). Used to decide whether the remaining path to a leader who just
    /// changed zones is long enough to skip walking it and teleport instead.
    /// </summary>
    public float TrailRemainingDistance(Vector3 playerPos)
    {
        if (_trail.Count == 0)
            return 0f;

        var total = Vector3.Distance(playerPos, _trail[0]);
        for (int i = 0; i < _trail.Count - 1; i++)
            total += Vector3.Distance(_trail[i], _trail[i + 1]);

        return total;
    }

    /// <summary>
    /// Call on area change: clears trail, cancels any in-flight search.
    /// </summary>
    public void Reset()
    {
        _trail.Clear();
        _portalSuspected = false;
        _lastReachablePos = null;
        _lastPlayerPos = null;
        _lastAstarRequestGoal = null;
        _trailUnreachableThisTick = false;
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
    /// One concise, fixed-shape line per JPS search — easy to grep/parse and bounded in
    /// size (no per-waypoint dump). Fields:
    ///   outcome  = Reached | Partial | Unreachable | Invalid
    ///   exp/gen  = jump points expanded / generated (JPS efficiency — expect exp far
    ///              below the old A* cell counts in open areas)
    ///   raw      = jump points returned by JPS
    ///   smooth   = waypoints after string-pull
    ///   cost     = path length in grid units
    ///   t        = search time (ms)
    ///   goal     = goal cell
    /// </summary>
    private string FormatSearch(PathResult res, int rawCount, int smoothCount) =>
        $"JPS {res.Outcome} exp={res.Expanded} gen={res.Generated} " +
        $"raw={rawCount} smooth={smoothCount} cost={res.Cost:F0} " +
        $"t={res.ElapsedMs:F1}ms goal({_lastAstarGoal.X},{_lastAstarGoal.Y})";

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
        //    exists, not by how far they jumped.
        RecordTrail(leaderPos);

        // 2. Delegate to the shared navigation core with the leader as the target.
        return NavigateInternal(playerPos, leaderPos, allowTrailFollow: true, arriveWithin: KeepWithinDistance);
    }

    // -----------------------------------------------------------------------
    // Generic navigation entry point — path to ANY static world target using the
    // exact same trail + LOS string-pull + A* machinery that follows the leader.
    // Used to walk to an area transition (finishing the leader's breadcrumb trail
    // to the portal) and can be used for loot or any other point target.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Navigate toward an arbitrary static world <paramref name="targetPos"/> (an area
    /// transition, a loot item, …) reusing the leader-follow pathing. This does NOT record
    /// a trail — the caller owns trail recording via <see cref="Tick"/>.
    ///
    /// When <paramref name="allowTrailFollow"/> is true the existing breadcrumb trail is
    /// consumed toward the target. That is exactly what we want for an area transition:
    /// the trail is the leader's recorded route and it ends at the portal they took, so
    /// finishing the trail walks us to the transition (around walls) just like following
    /// the leader. For an OFF-trail target such as loot, pass false so we path straight to
    /// it via A*/LOS instead of chasing the leader's now-stale trail.
    ///
    /// Returns <see cref="FollowResultType.Idle"/> once we are within
    /// <paramref name="arriveWithin"/> world units AND have line of sight to the target.
    /// </summary>
    public FollowResult NavigateTo(Vector3 playerPos, Vector3 targetPos,
        bool allowTrailFollow, int arriveWithin)
    {
        if (!IsValidPosition(playerPos) || !IsValidPosition(targetPos))
            return FollowResult.Idle;

        LineOfSight.EnsureTerrainData();

        var result = NavigateInternal(playerPos, targetPos, allowTrailFollow, arriveWithin);
        if (LogEnabled) LogTickSummary(playerPos, targetPos, result);
        return result;
    }

    /// <summary>
    /// The shared navigation core: given our position and a target, return the next follow
    /// action (Idle when arrived, MoveTo the next waypoint, or PortalSuspected when no
    /// walkable route exists). Trail recording is the caller's responsibility so this can
    /// serve both the moving leader and static targets.
    /// </summary>
    private FollowResult NavigateInternal(Vector3 playerPos, Vector3 targetPos,
        bool allowTrailFollow, int arriveWithin)
    {
        float distToTarget = Vector3.Distance(playerPos, targetPos);

        // 2. Arrived: within range AND line of sight to the target → nothing to do.
        if (distToTarget <= arriveWithin &&
            LineOfSight.HasLineOfSightRaw(playerPos, targetPos))
        {
            _lastReachablePos = targetPos;
            _portalSuspected = false;
            return FollowResult.Idle;
        }

        // 3. Consume a completed JPS search. A path means the target is reachable (seed
        //    the trail with it); a null path means NO walkable route exists right now
        //    — the target is most likely behind a portal/transition.
        if (_searchTask is { IsCompleted: true })
        {
            if (_searchTask.IsCompletedSuccessfully)
            {
                var res = _searchTask.Result;
                if (res.Path is { Count: > 0 } gridPath)
                {
                    // Only replace the trail wholesale when we don't already have a
                    // usable route. An A* refresh is meant to top up a trail that's
                    // about to run out (see the comment at the RequestAstar call
                    // below), not to constantly re-anchor a perfectly good one. In
                    // narrow corridors the greedy string-pull in SmoothGridPath picks
                    // a different set of anchor points almost every search — a small
                    // change in the start cell flips which waypoints survive — so
                    // applying every refresh re-aimed the follower at a new point
                    // nearly every tick. That's the "zigzag then stall" symptom seen
                    // in narrow passages: the trail was being clobbered every ~150ms
                    // even though the previous trail was still perfectly walkable.
                    bool needsRefresh = _trail.Count <= 1 || _portalSuspected || _trailUnreachableThisTick;
                    if (needsRefresh)
                    {
                        if (LogEnabled && gridPath.Count >= 2)
                        {
                            var rf = gridPath[0]; var rs = gridPath[1];
                            var rl = gridPath[^2]; var re = gridPath[^1];
                            DebugLog($"RawPath first({rf.X},{rf.Y}) second({rs.X},{rs.Y}) " +
                                     $"last({rl.X},{rl.Y}) end({re.X},{re.Y}) " +
                                     $"[refresh: count<=1={_trail.Count <= 1} portal={_portalSuspected} unreachable={_trailUnreachableThisTick}]");
                        }

                        var smoothed = SmoothGridPath(gridPath);
                        var oldTarget = _trail.Count > 0 ? _trail[0] : (Vector3?)null;

                        if (LogEnabled && smoothed.Count > 0)
                        {
                            var a0 = smoothed[0];
                            var a1 = smoothed[Math.Min(1, smoothed.Count - 1)];
                            var brg = Math.Atan2(a1.Y - a0.Y, a1.X - a0.X) * 180.0 / Math.PI;
                            DebugLog($"Anchor1({a1.X},{a1.Y}) brg={brg:F0}");
                        }

                        _trail.Clear();
                        foreach (var g in smoothed)
                            _trail.Add(Helper.ToWorld(g));

                        // SmoothGridPath's string-pull deliberately collapses long straight,
                        // open stretches into a single anchor-to-anchor jump - exactly the
                        // segment TrailScan's MaxClickDistance cap has no in-range candidate
                        // for. Without subdividing, that gap would just stall (bestIdx=-1
                        // every tick) instead of walking through it. Fill any over-length gap
                        // with evenly spaced intermediate waypoints before pruning/consuming.
                        int subdivided = SubdivideLongSegments();
                        if (LogEnabled && subdivided > 0)
                            DebugLog($"Subdivided {subdivided} long segment(s) -> trailCount={_trail.Count}");

                        // The path (and its leading point, path[0]) was computed from the
                        // player's grid position AT REQUEST TIME. The search runs on a
                        // background thread, so by the time it completes the player has
                        // usually moved further along - leaving _trail[0] sitting BEHIND
                        // the player's current position. Left in place, that stale point
                        // is trivially "reachable" by a straight line (it's right next to
                        // us) and step 5 below would happily send us back to it - the
                        // "snaps back to where we already were" bug. Prune with the same
                        // reached/passed logic as step 4, but against the CURRENT player
                        // position, immediately after installing the new trail.
                        PruneTrail(playerPos);

                        _portalSuspected = false;
                        if (LogEnabled)
                        {
                            DebugLog(FormatSearch(res, gridPath.Count, smoothed.Count));
                            if (oldTarget is Vector3 ot && _trail.Count > 0)
                            {
                                var newBrg = Math.Atan2(_trail[0].Y - playerPos.Y, _trail[0].X - playerPos.X) * 180.0 / Math.PI;
                                DebugLog($"Trail replaced: old target world({ot.X:F0},{ot.Y:F0}) -> " +
                                          $"new target world({_trail[0].X:F0},{_trail[0].Y:F0}) newBrg={newBrg:F0}");
                            }
                        }
                    }
                    else if (LogEnabled)
                    {
                        DebugLog(FormatSearch(res, gridPath.Count, 0) +
                                 " (discarded - existing trail still usable)");
                    }
                }
                else
                {
                    // No walkable route → assume the target is behind a portal/transition.
                    _portalSuspected = true;
                    if (LogEnabled) DebugLog(FormatSearch(res, 0, 0));
                }
            }
            _searchTask = null;
        }

        // 4. Prune trail points we've already reached OR passed (see PruneTrail).
        //    Guards against walking backward to a stale leading waypoint - either the
        //    previous tick's start cell, or (now also handled at the step-3 call site)
        //    a background search's request-time start cell.
        PruneTrail(playerPos);

        // 5. Is any part of the route reachable by a straight WALKABLE line? Walkability
        //    (not line-of-sight) is required: LOS permits see-through/dashable gaps that
        //    aren't actually walkable. The furthest reachable breadcrumb is both our next
        //    waypoint AND our "last known valid (reachable) location" toward the target.
        //    Skipped entirely for off-trail targets (loot): the trail is the leader's
        //    route, not a route to an arbitrary point.
        if (!allowTrailFollow)
        {
            // Off-trail (static-target) navigation doesn't use the leader trail, so it
            // shouldn't inherit a stale "unreachable" flag from a previous leader-follow
            // tick and force a spurious refresh in NavigateInternal's step 3.
            _trailUnreachableThisTick = false;
        }
        else
        {
            int clearance = PfSettings.PathClearance.Value;
            int maxClickDistance = PfSettings.MaxClickDistance.Value;

            // Leash: only close all the way in on the trail when we already have line of
            // sight to the live target. If LOS is currently blocked (leader just went
            // round a corner, behind an obstruction, etc.) the leash is ignored below so
            // we keep closing in until sight is restored — this is what makes the "within
            // KeepWithinDistance AND in line of sight" requirement actually hold, instead
            // of a distance-only leash that could strand us out of sight of the leader.
            bool hasLosToTarget = LineOfSight.HasLineOfSightRaw(playerPos, targetPos);

            int bestIdx = -1;
            int usedClearance = clearance;
            bool leashRespected = false;

            if (hasLosToTarget)
            {
                bestIdx = FindBestTrailIndex(playerPos, targetPos, maxClickDistance, clearance,
                    respectLeash: true, arriveWithin);
                if (bestIdx < 0 && clearance > 0)
                {
                    bestIdx = FindBestTrailIndex(playerPos, targetPos, maxClickDistance, 0,
                        respectLeash: true, arriveWithin);
                    if (bestIdx >= 0) usedClearance = 0;
                }
                leashRespected = bestIdx >= 0;
            }

            // No LOS, or every leash-respecting candidate failed the walkable-line /
            // clearance check (e.g. a very short trail right after a zone change, where
            // every remaining point is already inside the leash radius) — fall back to
            // the unrestricted scan. Fallback found the deadlock: JPS only guarantees
            // per-cell walkability (isPathable checks value 1/5, no clearance band), and
            // SmoothGridPath's single-step fallback ("adjacent cells are walkable") never
            // verifies clearance either — only its multi-cell shortcuts do. So in a
            // corridor narrower than 2*clearance+1 cells, EVERY point on an otherwise-
            // valid trail can fail the clearance-aware check, giving bestIdx=-1 forever.
            // That's a hard stall: no MoveTo is ever returned again (confirmed by the
            // "did not move at all" test run - bestIdx=-1 on every single tick). Retry
            // with clearance=0 (raw walkability, exactly what JPS guarantees) before
            // giving up — a wall-hugging move through a tight gap beats a permanent
            // freeze.
            if (bestIdx < 0)
            {
                usedClearance = clearance;
                bestIdx = FindBestTrailIndex(playerPos, targetPos, maxClickDistance, clearance,
                    respectLeash: false, arriveWithin);
                if (bestIdx < 0 && clearance > 0)
                {
                    bestIdx = FindBestTrailIndex(playerPos, targetPos, maxClickDistance, 0,
                        respectLeash: false, arriveWithin);
                    if (bestIdx >= 0) usedClearance = 0;
                }
            }

            _trailUnreachableThisTick = bestIdx < 0;

            if (LogEnabled)
                DebugLog($"TrailScan bestIdx={bestIdx} trailCount={_trail.Count} " +
                         $"clearance={clearance} usedClearance={usedClearance} " +
                         $"hasLos={hasLosToTarget} leashRespected={leashRespected}");

            if (bestIdx >= 0)
            {
                // A valid path exists → follow it. The target is reachable, so clear any
                // portal suspicion and remember this reachable spot.
                _lastReachablePos = _trail[bestIdx];
                _portalSuspected = false;
                if (bestIdx > 0)
                    _trail.RemoveRange(0, bestIdx); // discard skipped breadcrumbs

                // If the target has outrun the breadcrumb trail, refresh it with a real A*
                // path in the background before we run out of reachable points. Gated by
                // ShouldRefreshAstar so a healthy, still-relevant trail isn't re-searched
                // on a blanket timer (see its comment).
                if ((_searchTask == null || _searchTask.IsCompleted) &&
                    ShouldRefreshAstar(distToTarget, targetPos))
                    RequestAstar(playerPos, targetPos);

                return FollowResult.MoveTo(_trail[0]);
            }
        }

        // 6. Nothing on the trail is reachable (or trail-follow is disabled). Ask A*
        //    whether ANY route to the target exists — it may find one the trail doesn't.
        if (_searchTask == null || _searchTask.IsCompleted)
            RequestAstar(playerPos, targetPos);

        // 7. No reachable route yet. If we have a last known reachable location, head
        //    there: that's where the target was before we lost the path (i.e. by the
        //    portal). PortalSuspected lets AutoPilot look for the portal label once we
        //    arrive; if A* hasn't answered yet we still walk there as a plain move.
        if (_lastReachablePos is Vector3 lastReachable)
            return _portalSuspected
                ? FollowResult.PortalSuspected(lastReachable)
                : FollowResult.MoveTo(lastReachable);

        // 8. No path and no last known location → wait.
        return FollowResult.Idle;
    }

    /// <summary>
    /// Scans the trail from newest to oldest for the furthest point reachable from
    /// <paramref name="playerPos"/> by a straight walkable line, i.e. the greedy
    /// closest-approach-to-target candidate. When <paramref name="respectLeash"/> is
    /// true, candidates within <paramref name="arriveWithin"/> world units of
    /// <paramref name="targetPos"/> are skipped — this is what keeps the follower from
    /// walking all the way up to the leader instead of stopping at the configured
    /// KeepWithinDistance. Returns -1 if no candidate satisfies the constraints.
    /// </summary>
    private int FindBestTrailIndex(Vector3 playerPos, Vector3 targetPos, int maxClickDistance,
        int clearance, bool respectLeash, int arriveWithin)
    {
        for (int i = _trail.Count - 1; i >= 0; i--)
        {
            // Cap the click target's distance: Camera.WorldToScreen() produces
            // wrong-direction garbage for points beyond reliable projection range (see
            // ScreenClamp diagnostics/MaxClickDistance comment), so a distant waypoint
            // must never be selected here even when the straight line to it is fully
            // walkable on an open map.
            if (Vector3.Distance(playerPos, _trail[i]) > maxClickDistance)
                continue;

            if (respectLeash && Vector3.Distance(_trail[i], targetPos) < arriveWithin)
                continue;

            if (LineOfSight.HasWalkableLineRaw(playerPos, _trail[i], clearance))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Drops trail points that are already reached or passed relative to
    /// <paramref name="playerPos"/>:
    ///   (a) Reached: within ReachedBounds of us.
    ///   (b) Passed: we're already closer to the NEXT point than _trail[0] is to it,
    ///       meaning we're on the far side of _trail[0] along the route.
    /// Shared by the normal per-tick trail advance AND by fresh-trail installation
    /// (a background JPS result can be seeded from a stale, now-behind-us start
    /// point - see the call site in step 3) so a newly replaced trail can never
    /// lead with a waypoint that's already behind the player.
    /// </summary>
    private void PruneTrail(Vector3 playerPos)
    {
        int reachedBounds = PfSettings.ReachedBounds.Value;
        while (_trail.Count > 0 && Vector3.Distance(playerPos, _trail[0]) <= reachedBounds)
            _trail.RemoveAt(0);
        while (_trail.Count >= 2 &&
               Vector3.Distance(playerPos, _trail[1]) <= Vector3.Distance(_trail[0], _trail[1]))
            _trail.RemoveAt(0);
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
    // Trail densification (cap segment length for click-target safety)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Inserts evenly spaced intermediate waypoints into any consecutive _trail pair
    /// whose distance exceeds PfSettings.MaxClickDistance. TrailScan refuses to select a
    /// click target beyond that cap (Camera.WorldToScreen() is unreliable past it - see
    /// MaxClickDistance's setting comment), so an anchor-to-anchor gap longer than the
    /// cap must be filled here or it becomes a dead zone with no valid candidate at all.
    /// Returns the number of segments that needed subdividing (0 = no-op), purely for
    /// diagnostics.
    /// </summary>
    private int SubdivideLongSegments()
    {
        int maxDist = PfSettings.MaxClickDistance.Value;
        int subdividedSegments = 0;

        for (int i = 0; i < _trail.Count - 1; i++)
        {
            var a = _trail[i];
            var b = _trail[i + 1];
            float segDist = Vector3.Distance(a, b);
            if (segDist <= maxDist) continue;

            subdividedSegments++;
            int steps = (int)Math.Ceiling(segDist / maxDist);
            for (int s = 1; s < steps; s++)
            {
                float t = (float)s / steps;
                var interpXY = new Vector3(
                    a.X + (b.X - a.X) * t,
                    a.Y + (b.Y - a.Y) * t,
                    0);
                var g = Helper.ToGrid(interpXY);
                _trail.Insert(i + s, Helper.ToWorld(g));
            }
            i += steps - 1; // skip past the points we just inserted
        }

        return subdividedSegments;
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
        int clearance = PfSettings.PathClearance.Value;

        while (anchor < path.Count - 1)
        {
            int next = anchor + 1; // guaranteed progress (adjacent cells are walkable)
            for (int j = path.Count - 1; j > anchor; j--)
            {
                if (LineOfSight.HasWalkableLineGrid(path[anchor], path[j], clearance))
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

    /// <summary>
    /// Decides whether a healthy trail is actually worth re-searching. Previously any
    /// tick with distToTarget > AcquireDistance requested a fresh background search
    /// (subject only to the AstarRecomputeIntervalMs cooldown), which fired on a
    /// near-constant timer whenever the leader was far away - almost every result came
    /// back "discarded - existing trail still usable" because nothing had actually
    /// changed. A search is only useful here if either (a) the trail is running low and
    /// will need topping up soon, or (b) the target has moved far enough since the last
    /// search that the old route may no longer be the best one.
    /// </summary>
    private bool ShouldRefreshAstar(float distToTarget, Vector3 targetPos)
    {
        if (distToTarget <= PfSettings.AcquireDistance.Value) return false;

        // Trail almost exhausted — worth topping up regardless of how far the target moved.
        if (_trail.Count <= PfSettings.TrailRefillThreshold.Value) return true;

        // Trail still has plenty of usable points. Only bother if the target itself has
        // relocated meaningfully since our last search (e.g. the leader moved on) -
        // otherwise the existing route is still the right one and re-running JPS just
        // burns CPU for a result we'll throw away.
        if (_lastAstarRequestGoal is not Vector3 lastGoal) return true; // never searched yet
        return Vector3.Distance(lastGoal, targetPos) > PfSettings.RecomputeMoveThreshold.Value;
    }

    private void RequestAstar(Vector3 startWorld, Vector3 goalWorld)
    {
        // Cooldown: a search can complete in well under 1ms. Kept as a hard floor even
        // now that ShouldRefreshAstar gates most redundant requests, in case the target
        // is oscillating right at the RecomputeMoveThreshold boundary.
        var now = DateTime.Now;
        if ((now - _lastAstarRequestTime).TotalMilliseconds < PfSettings.AstarRecomputeIntervalMs.Value)
            return;
        _lastAstarRequestTime = now;
        _lastAstarRequestGoal = goalWorld;

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
            DebugLog($"JPS request: start grid({startGrid.X},{startGrid.Y}) " +
                     $"goal grid({goalGrid.X},{goalGrid.Y}) " +
                     $"startVal={LineOfSight.GetTerrainValueAt(startGrid)} " +
                     $"goalVal={LineOfSight.GetTerrainValueAt(goalGrid)}");

        // Background thread: pure JPS computation, no GameController access
        _searchTask = Task.Run(
            () => AStar.FindPath(terrain, startGrid, goalGrid, isPathable, nodeBudget, ct),
            ct);
    }
}
