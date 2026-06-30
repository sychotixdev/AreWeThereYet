# Pathfinding Integration Plan (Revised)

> Revision notes are inline as **[REV]** callouts. The biggest corrections are around
> **coordinate units**, **terrain-readiness**, and the **"unreachable leader = zone transition"**
> inference, which as originally written fires in the wrong branch.

## Algorithm: Theta* with LOS-target goal

**Theta\*** is A* with one modification: during edge relaxation, if the grandparent node has direct
line-of-sight to the candidate node, skip the intermediate parent. This produces any-angle paths —
smooth movement with far fewer waypoints than grid A*. It plugs into the existing DDA raycast in
`LineOfSight`.

**[REV] Use a raycast that does not mutate shared state.** `LineOfSight.HasLineOfSight()` calls
`RefreshTerrainData()` (which re-reads process memory) and writes `_debugVisiblePoints`/`_debugRays`
on every call. Calling it from a Theta* inner loop would re-read memory thousands of times per path
and corrupt the debug overlay. The inner loop must call a pure `HasLineOfSightRaw()` (see below).

**LOS-target goal:** Rather than pathing to the leader's exact grid tile, find the nearest walkable
tile near the leader that satisfies:
- Grid distance to leader ≤ `KeepWithinDistanceGrid` (see units note)
- `HasLineOfSightRaw(tile, leaderGrid)` == true

Path to *that* tile. The follower holds at the edge of sight range rather than stacking on the leader.

---

## [REV] Coordinate units — the single most important correction

The original plan labels every threshold "tiles" and reuses `KeepWithinDistance` as if it were a
tile count. **It is not.** In the current code `KeepWithinDistance` (default **200**) is compared
against **world-space** distances:

```csharp
// AutoPilot.cs, existing code
var distanceToLeader = Vector3.Distance(AreWeThereYet.Instance.playerPosition, followTarget.Pos);
if (distanceToLeader >= ...KeepWithinDistance.Value) ...
```

ExileCore's grid↔world scale is ~**10.87 world units per grid tile** (`GridToWorld` multiplier
≈ 250/23). So the existing default of `KeepWithinDistance = 200` is **~18 grid tiles**, not 200.

Theta* operates entirely in **grid space**. Mixing a world-space setting into a grid-space goal
condition will make the follower hold ~10× too far away (or recompute constantly). Two ways to fix
it; pick one and be consistent:

1. **Keep new pathfinding thresholds in grid tiles** and convert the existing world-space
   `KeepWithinDistance` once at the boundary:
   ```csharp
   const float GridToWorld = 250f / 23f;        // ≈ 10.87
   int KeepWithinDistanceGrid = (int)(KeepWithinDistance.Value / GridToWorld);
   ```
2. Or express all new thresholds in **world units** to match the existing setting, and convert to
   grid only inside Theta*.

This revision uses **option 1** (grid tiles internally). Every threshold below is therefore stated
with explicit units, and the defaults are re-scaled to be sane in *tiles*.

---

## Path Management Strategy

Design principle, unchanged and correct: **a path only becomes wrong at its end, not its beginning.**
If the leader steps forward, the first ~90% of our path is still valid; only act when the *endpoint*
stops pointing at the right place.

### Three configurable thresholds (corrected units)

| Setting | Suggested Default | Units | Meaning |
|---|---|---|---|
| `EndpointToleranceGrid` | 15 tiles | grid | Leader drift from our endpoint before we act |
| `ExtensionThresholdGrid` | 60 tiles | grid | Beyond this from endpoint → full recompute instead of extend |
| `KeepWithinDistance` | 200 | **world** (existing) | Converted to ~18 tiles for the goal test |

**[REV]** Defaults were dropped from 40/150 *(misread as tiles)* to 15/60 tiles. 40 tiles ≈ 435
world units would let the leader walk most of a screen away before reacting.

### Per-tick decision tree

```
1. LOS check (cheapest, every tick)
   └─ HasLineOfSightRaw(player, leader) AND worldDist ≤ KeepWithinDistance?
      ├─ YES → Idle. No path work.
      └─ NO  → step 2.

2. Terrain ready?  [REV — new guard]
   └─ GetTerrainData() == null → return Idle (caller falls back to direct-click movement).
   └─ ready → step 3.

3. Active path?
   └─ NO → compute fresh Theta* path → LOS-target near leader. Done.
   └─ YES → step 4.

4. Leader's grid distance to our path's last waypoint?
   └─ ≤ EndpointToleranceGrid → keep following. No change.
   └─ > EndpointToleranceGrid → Theta*: endpoint → LOS-target near leader
        Result A: path found, length ≤ ExtensionThresholdGrid → append extension.
        Result B: path found, length  > ExtensionThresholdGrid → discard, full recompute
                  from playerCurrentPos.
        Result C: no path found → see "Unreachable leader" below. [REV — NOT auto-transition]
```

**[REV] Result ordering changed.** The original made "no path found" *Result A* and tied it to zone
transitions. That inference is unsafe here (see next section), so it is demoted to a fallback, and
the common cases (extend / recompute) come first.

### Why this works for a moving leader

- Leader usually near the endpoint → no work.
- Past tolerance → try a cheap *extension* (short Theta* from endpoint), reusing early segments.
- Extension and recompute are both Theta*. **[REV] They are NOT guaranteed <5ms.** A full recompute
  toward a far or unreachable goal can expand thousands of nodes over a large zone grid. Add a hard
  **node-expansion budget** (e.g. 20–40k nodes); if exceeded, abort and return Idle so the caller
  uses direct-click movement that frame. Without a cap, a single bad goal can stall the AutoPilot
  coroutine.

### Early exit while following

Every movement tick:
```
if HasLineOfSightRaw(player, leader) AND worldDist(player, leader) ≤ KeepWithinDistance:
    stop path, go Idle
```
Follower stops the moment it regains sight, even with waypoints remaining.

---

## [REV] "Unreachable leader" — corrected logic (was: Zone Transition Detection)

The original claim: *"if `path.LastWaypoint → leader` returns no path, the leader took a portal
there."* This is geometrically appealing but **fires in the wrong branch**:

`LeaderPathFinder.Evaluate()` is only called when **`followTarget != null`** — i.e. the leader's
entity is loaded, which means **the leader is in our current zone**. Zone transitions are precisely
the case where `followTarget == null`, which the existing code already handles via the party-UI
zone-name mismatch (AutoPilot.cs ~line 398). So a null path while the leader is present almost never
means "they portaled" — it means one of:
- terrain data is briefly stale/null (door just changed, mid-refresh),
- the LOS-target search found no qualifying tile near the leader,
- the leader is standing on a tile our combined terrain marks blocked,
- the goal is across a gap our 8-connected grid can't cross.

Treating any of those as a portal would make the follower abandon the leader and start portal-hunting
while the leader is still right there.

**Corrected behavior for Result C (no path, leader present):**
1. Return `PathUpdateKind.Unreachable` (not `TransitionSuspected`).
2. Caller falls back to **direct-click movement toward the leader** (current pre-pathfinding
   behavior) — degrade gracefully, don't portal-hunt.
3. Keep a short failure counter; only if Theta* fails for N consecutive ticks *and* the leader's
   world distance keeps growing past `TransitionDistance` do we let the existing transition logic
   take over (it already keys off `distanceMoved > TransitionDistance`).

The genuine zone-transition path is left to the existing, working signal (`followTarget == null` +
zone-name check). Both signals remain active; we are simply not overloading "no path" to mean
"portal" while the entity is loaded.

---

## Files to Add

### 1. `PathFinder/Theta.cs` (new)

```csharp
// Inputs:  int[][] terrain  (indexed [y][x] — matches LineOfSight._terrainData!)
//          Vector2i start, Vector2i goal
//          Func<Vector2i, Vector2i, bool> hasLineOfSight   (delegate to HasLineOfSightRaw)
//          int nodeBudget                                   [REV] expansion cap
// Output:  List<Vector2i>? — null if no path or budget exceeded
//
// Walkability: terrain value 0 = blocked; 2 = dashable; 5 = walkable; 1 = fallback.
//   Treat >0 as walkable for movement (mirror IsTerrainPassable, minus the dash-gating).
// Neighbors: 8-directional. [REV] Reject diagonal corner-cutting: a diagonal step
//   (±1,±1) is only allowed if BOTH orthogonal neighbors are walkable.
// Heuristic: Euclidean to goal.
// Theta* relax: for neighbor N with current parent P,
//   if hasLineOfSight(P.parent, N): parent[N] = P.parent; g[N] = g[P.parent] + dist(P.parent, N)
//   else: standard A* relaxation through P.
```

**[REV] `int[][]` is indexed `[y][x]`**, not `[x][y]` — `LineOfSight.GetTerrainValue` does
`_terrainData[y][x]`. Index Theta* the same way or paths will be transposed.

**PriorityQueue:** `.NET` (net8.0 target — confirmed in csproj) provides
`PriorityQueue<Vector2i, float>`. **[REV] It has no decrease-key.** Use **lazy deletion**: re-enqueue
on improvement and skip dequeued entries whose stored cost is stale (`if (dequeuedCost > g[node])
continue;`). Track a `closed`/best-cost dictionary to detect staleness.

**[REV] Do NOT depend on Radar.** The original references "Radar's `PathFinder`" and
"Radar's `BinaryHeap.cs`". There is no Radar reference in `AreWeThereYet.csproj`; only ExileCore and
GameOffsets. Keep `Theta.cs` fully self-contained (the built-in `PriorityQueue` already removes the
need for Radar's heap).

### 2. `PathFinder/LeaderPathFinder.cs` (new)

```csharp
public class LeaderPathFinder
{
    private List<Vector2i> _currentPath;
    private Vector2i _pathEndpoint;
    private int _unreachableTicks;            // [REV] for graceful fallback

    public PathUpdate Evaluate(Vector2i playerGrid, Vector2i leaderGrid, float worldDistToLeader);
    public void Reset(int[][] terrainData);   // called on area change

    private Vector2i? FindLosTarget(Vector2i leaderGrid);
    private List<Vector2i>? RunTheta(Vector2i from, Vector2i to);
}

public enum PathUpdateKind { Idle, FollowExisting, PathUpdated, Unreachable }  // [REV] renamed
public record PathUpdate(PathUpdateKind Kind, List<Vector2i>? Path = null);
```

**[REV]** `Evaluate` also takes `worldDistToLeader` so the LOS/range check can compare in world units
against the existing `KeepWithinDistance` without re-deriving the scale.

---

## Files to Modify

### 3. `Utils/LineOfSight.cs`

```csharp
public int[][] GetTerrainData() => _terrainData;          // may be null during load
public bool IsTerrainReady() => _terrainData != null;     // [REV]

// [REV] Pure overload: no RefreshTerrainData(), no _debugVisiblePoints / _debugRays writes.
public bool HasLineOfSightRaw(Vector2 a, Vector2 b) { /* DDA over _terrainData only */ }
```

**[REV] Terrain-refresh starvation.** `_terrainData` is only refreshed by `UpdateArea()` (on area
change) and `RefreshTerrainData()`, and the latter runs **only inside `HandleRender` (gated on
`ShowTerrainDebug`)** or inside the state-mutating `HasLineOfSight`. If `HasLineOfSightRaw` skips
refresh (correct) and the user has terrain debug off, terrain may never refresh after a null initial
load — pathfinding would stay dead the whole zone. Fix: expose
`EnsureTerrainData()` that runs the same 500ms-throttled refresh and have `LeaderPathFinder` (or the
AutoPilot tick) call it once per tick before pathing. This decouples pathfinding from the debug-render
path.

### 4. `AreWeThereYet.cs`

```csharp
internal LeaderPathFinder leaderPathFinder = new();

public override void AreaChange(AreaInstance area)
{
    base.AreaChange(area);
    EventBus.Instance.Publish(new AreaChangeEvent());     // synchronous → LineOfSight.UpdateArea runs now
    leaderPathFinder.Reset(lineOfSight.GetTerrainData()); // [REV] may be null mid-load — that's fine,
                                                          // EnsureTerrainData() will populate later
    autoPilot.AreaChange();
}
```

**[REV]** `EventBus.Publish` is synchronous (confirmed), so terrain *is* updated before
`GetTerrainData()` here — **but** on a fresh zone the game is often still loading and the read returns
null. `Reset(null)` must be safe, and `LeaderPathFinder` must re-pull terrain via `EnsureTerrainData()`
on later ticks rather than assuming the area-change snapshot was valid.

### 5. `AutoPilot.cs`

**[REV] This is a surgical change, not a wholesale replacement.** The leader-follow block
(lines ~471–552) is interleaved with **Transition**, **Loot**, and **MercenaryOptIn** task creation
and with the `TransitionDistance`/`distanceMoved` heuristics. Only the *movement* task generation
(the `tasks.Add(new TaskNode(followTarget.Pos, ...))` calls) should be replaced. Leave transition,
loot, mercenary, and the existing `followTarget == null` zone logic intact.

```csharp
// In the followTarget != null branch, replacing ONLY the movement-task additions:
var playerGrid = AreWeThereYet.Instance.playerPosition.WorldToGrid().ToVector2i();
var leaderGrid = followTarget.Pos.WorldToGrid().ToVector2i();
var update = AreWeThereYet.Instance.leaderPathFinder.Evaluate(playerGrid, leaderGrid, distanceToLeader);

switch (update.Kind)
{
    case PathUpdateKind.Idle:
        tasks.RemoveAll(t => t.Type == TaskNodeType.Movement);   // type-filtered — keeps loot/transition
        break;

    case PathUpdateKind.FollowExisting:
        break;

    case PathUpdateKind.PathUpdated:
        tasks.RemoveAll(t => t.Type == TaskNodeType.Movement);
        foreach (var wp in DecimateWaypoints(update.Path))
            tasks.Add(new TaskNode(Helper.GridToWorldPosition(wp), WaypointBoundsWorld));
        break;

    case PathUpdateKind.Unreachable:                              // [REV] graceful fallback
        // Fall back to original direct-click movement toward the leader.
        tasks.RemoveAll(t => t.Type == TaskNodeType.Movement);
        tasks.Add(new TaskNode(followTarget.Pos, AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance));
        break;
}
```

**[REV] Waypoint bounds / decimation.** Intermediate waypoints need a *tight* completion bound, not
the leader-follow `KeepWithinDistance * 1.5` used at line 588 (≈ 300 world units ≈ 27 tiles), which
would let the follower skip whole waypoints and cut corners. Give path waypoints a small bound
(e.g. `WaypointSpacing` worth of world units) so each is actually visited. `DecimateWaypoints` keeps
only direction-change vertices or every `WaypointSpacing` tiles, whichever is fewer.

### 6. `AreWeThereYetSettings.cs`

```csharp
public PathfindingSettings Pathfinding { get; set; } = new();

[Submenu(CollapsedByDefault = true)]
public class PathfindingSettings
{
    [Menu("Enable Pathfinding")]
    public ToggleNode Enabled { get; set; } = new(true);

    [Menu("Endpoint Tolerance (grid tiles)")]                          // [REV] units fixed
    public RangeNode<int> EndpointTolerance { get; set; } = new(15, 4, 60);

    [Menu("Full Recompute Threshold (grid tiles)")]
    public RangeNode<int> ExtensionThreshold { get; set; } = new(60, 20, 200);

    [Menu("Waypoint Spacing (grid tiles)")]
    public RangeNode<int> WaypointSpacing { get; set; } = new(12, 1, 50);

    [Menu("Max Node Expansions (perf cap)")]                           // [REV] new
    public RangeNode<int> NodeBudget { get; set; } = new(30000, 2000, 100000);
}
```

### 7. `Helper.cs`

**[REV] Disambiguate Vector3.** `Helper` already `using SharpDX`, and `TaskNode.WorldPosition` is
`SharpDX.Vector3`, so return `SharpDX.Vector3`. `GridToWorld()` returns `System.Numerics.Vector2`.

```csharp
internal static SharpDX.Vector3 GridToWorldPosition(GameOffsets.Native.Vector2i gridPos)
{
    var worldXY = new System.Numerics.Vector2(gridPos.X, gridPos.Y).GridToWorld();
    var z = AreWeThereYet.Instance.GameController.IngameState.Data
                .GetTerrainHeightAt(new System.Numerics.Vector2(gridPos.X, gridPos.Y));
    return new SharpDX.Vector3(worldXY.X, worldXY.Y, z);
}

internal static GameOffsets.Native.Vector2i ToVector2i(this System.Numerics.Vector2 v)
    => new GameOffsets.Native.Vector2i((int)v.X, (int)v.Y);
```

`Vector2i` lives in `GameOffsets.Native` (as used in `LineOfSight.cs`). New files need that `using`.

---

## Execution Order

1. `PathfindingSettings` in `AreWeThereYetSettings.cs`.
2. `GetTerrainData()`, `IsTerrainReady()`, `HasLineOfSightRaw()`, **`EnsureTerrainData()`** in
   `LineOfSight.cs`.
3. `GridToWorldPosition` / `ToVector2i` in `Helper.cs`.
4. `PathFinder/Theta.cs` — self-contained (node budget, lazy deletion, no-corner-cut, `[y][x]`).
5. `PathFinder/LeaderPathFinder.cs`.
6. Wire into `AreWeThereYet.cs`.
7. Surgically replace **only** movement-task generation in `AutoPilot.cs`.

---

## Fallback & Safety

- `Pathfinding.Enabled == false` → `Evaluate` returns `Idle`; AutoPilot uses original direct-click.
- `GetTerrainData()` null (area mid-load) → `Idle`; direct movement until `EnsureTerrainData()`
  populates terrain. **[REV]** Pathfinding no longer depends on debug rendering being on.
- **[REV]** Node-budget exceeded → abort Theta*, return `Idle`/`Unreachable`, direct-click that frame.
- **[REV]** "No path while leader present" → `Unreachable` → direct-click fallback, **not** portal
  hunting. Genuine transitions still go through the existing `followTarget == null` + zone-name signal.
- Both transition signals (UI zone name; entity absence) remain active.
```
