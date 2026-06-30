# Pathfinding Integration Plan (v3 — Breadcrumb-first)

> **What changed from v1/v2 and why.** v1 proposed Theta* on every leader move. v2 fixed unit and
> threading bugs but kept per-move search. After reviewing Radar's `PathFinder`, the better answer is
> to **not search on every move at all**: the leader is a real player walking a valid route, so the
> leader *generates* a correct path for free. We follow that trail (O(1) per tick) and fall back to a
> background A* search only for initial acquisition and unreachability confirmation.

---

## Units: world, not tiles

**Every distance, threshold, setting, and position comparison is in in-game world units** — the same
units as `followTarget.Pos`, `playerPosition`, and the existing `KeepWithinDistance` (200) and
`TransitionDistance` (500). This matches how Radar operates when it is not drawing on the minimap.

The terrain **grid** (and therefore the A* search and the LOS raycast) is inherently grid-indexed, so
grid coordinates appear in exactly two inner places: the A* fallback and the `HasLineOfSightRaw`
call. We convert world→grid only at those boundaries and convert the A* result back to world
immediately. Nothing the user configures, and nothing in the follow logic, is expressed in tiles.

### Conversion (from Radar — authoritative)
`Radar.cs`: `TileToWorldConversion = 250`, `TileToGridConversion = 23`,
`GridToWorldMultiplier = 250/23 ≈ 10.8696`.

```csharp
const float GridToWorldMultiplier = 250f / 23f; // ≈ 10.87 world units per grid cell

// world -> grid  (only for A* and the LOS raycast)
Vector2i ToGrid(Vector3 w) => new((int)(w.X / GridToWorldMultiplier), (int)(w.Y / GridToWorldMultiplier));

// grid -> world  (z read directly from the height array, indexed [Y][X], like Radar.cs:271)
SharpDX.Vector3 ToWorld(Vector2i g)
{
    float z = GameController.IngameState.Data.RawTerrainHeightData[g.Y][g.X];
    return new(g.X * GridToWorldMultiplier, g.Y * GridToWorldMultiplier, z);
}
```

All terrain arrays here are indexed **`[y][x]`** (`LineOfSight._terrainData[y][x]`, Radar
`_heightData[y][x]`). Index everything the same way.

---

## Why not a flow-field, and why not per-move A*

- **Radar's `PathFinder` (target-rooted Dijkstra flow-field)** floods the reachable area *from the
  target tile*, then answers unlimited start→target queries cheaply. Ideal for fixed landmarks;
  **wrong for a constantly-moving leader**, because the target moves every tick and forces a full
  re-scan.
- **A* on every leader move** is far better than re-rooting a flow-field, but still does work each
  move.
- **Breadcrumb following** does ~zero work per move and yields guaranteed-walkable paths, because the
  leader validated each segment by walking it. Search is reserved for the rare no-trail cases.

---

## Component 1 — Leader breadcrumb trail (primary, all world units)

### State
```csharp
private readonly List<Vector3> _trail = new();   // leader WORLD positions, oldest -> newest
private Vector3 _lastLeaderPos;
```
Storing world positions means following and clicking need no conversion — `TaskNode.WorldPosition`
consumes them directly. Grid is touched only by the LOS check below.

### Recording (main thread, each tick while followTarget != null)
```
if (_trail empty) { _trail.Add(leader.Pos); _lastLeaderPos = leader.Pos; }
else {
    moved = Vector3.Distance(leader.Pos, _trail[^1]);   // world units
    if (moved >= TrailPointSpacing)            // world units, e.g. ~40
        _trail.Add(leader.Pos);
    if (moved >= TransitionDistance)           // existing world setting (500) -> portal jump (Comp. 3)
        flag PortalSuspected at _trail[^1];
}
// Safety cap: if (_trail.Count > MaxTrailPoints) _trail.RemoveRange(0, _trail.Count - MaxTrailPoints);
```

### Following with LOS string-pulling (main thread, each tick)
```
1. Early exit: HasLineOfSightRaw(player, leader) AND Vector3.Distance(player, leader) <= KeepWithinDistance
   -> clear Movement tasks, Idle.  (In position.)

2. Prune: drop _trail[0..] points already reached (Vector3.Distance(player, point) <= ReachedBounds).

3. Far-behind check: if Vector3.Distance(player, _trail[0]) is large (e.g. > AcquireDistance, ~ a few
   screens) -> the trail head is out of reach (late zone-in / lost trail). Request A* acquisition
   (Component 2) instead of replaying stale history; resume trail-following once it returns.

4. String-pull: find the FURTHEST trail index i with HasLineOfSightRaw(player, _trail[i]) == true
   (convert both to grid only for this raycast). Emit ONE Movement TaskNode at _trail[i] (world);
   discard _trail[0..i-1]. LOS smoothing = straight-line shortcuts with zero search.

5. No trail at all -> request A* acquisition to the leader.
```

**Why string-pulling works:** the trail is already walkable. The raycast lets the follower cut
straight to the furthest visible breadcrumb — the same corners A*/Theta* would produce — using the
LOS you already have, and entirely in world space except the one raycast.

---

## Component 2 — Background A* fallback (rare)

Used only when there is **no usable trail**: late zone-in with the leader far, lost trail, or to
confirm unreachability. Runs on a background `Task` (Radar's pattern) so it never hitches the
AutoPilot coroutine.

```csharp
public Task<List<Vector3>?> RequestPathAsync(
    int[][] terrainSnapshot, Vector3 startWorld, Vector3 goalWorld, CancellationToken ct);
// Converts to grid internally, runs A*, converts the result back to WORLD before returning.
```

- **Algorithm:** plain **A\*** with Euclidean heuristic. 8-neighbour offsets (same as Radar's
  `NeighborOffsets`). Reject diagonal corner-clipping: a diagonal step is allowed only if both
  orthogonal neighbours are pathable (your rule: walkable tiles are fine, no squeezing a diagonal
  past unwalkable terrain).
- **PriorityQueue:** .NET `PriorityQueue<Vector2i,float>` with **lazy deletion** (skip stale dequeues),
  or reuse Radar's `BinaryHeap.cs`.
- **Pathable predicate — injected, dash-ready:**
  ```csharp
  // Today: walkable only. Value 2 (dashable door) is treated as blocked for planning.
  Func<int,bool> isPathable = v => v is 1 or 5;     // default
  ```
  The predicate is a constructor parameter, not hard-coded. **When dash-aware routing is added later**,
  pass `v => v is 1 or 2 or 5` (gated on `DashEnabled`) and tag value-2 segments so the mover knows to
  dash — no structural change required. The same predicate backs the string-pull LOS so both agree on
  what "walkable" means.
- **Unreachability:** open set exhausts without reaching goal → leader unreachable → `Unreachable`
  (feeds Component 3). Safe because it runs in the background with cancellation; also bounded by a
  `NodeBudget` cap so a pathological scan can't run unbounded.
- **On completion:** convert grid path to world, seed `_trail`, hand back to Component 1.

### Thread safety (the one real hazard)
1. **Atomic publish in `LineOfSight.UpdateTerrainData`:** build into a local `int[][] newData`, fully
   populate, then assign `_terrainData = newData` once. (Today it assigns the outer array first, then
   fills rows — a background reader could see null rows.)
2. **Snapshot the reference** at job start and use only that local. Never touch
   `GameController`/entities from the background thread; do world↔grid height lookups on the **main
   thread** when seeding the trail / building tasks.
3. **Cancel on area change** (Radar's `StopPathFinding` CTS-reset pattern).

---

## Component 3 — In-zone portal detection (your answer #2)

Far follower, portal label not yet visible, leader teleported within the zone. Detection falls out of
the trail:

- The leader's breadcrumbs **stop at the portal** and resume elsewhere with a **position jump** ≥
  `TransitionDistance` (cheap world-unit pre-trigger, reusing the existing setting).
- The follower walks to the **end of the trail** (the portal location). Once in label range, the
  **existing `GetBestPortalLabel` / Transition task logic clicks it** — no new portal-click code.
- **Confirmation backup:** `followTarget != null` (leader still in-zone) but the background A* returns
  `Unreachable` corroborates "leader is in a pocket reachable only via a portal." This is v1's idea,
  now firing in the right place — a present-but-unreachable leader — not during normal following.

Distinct from the existing **zone**-transition path (`followTarget == null` + party-UI zone-name
mismatch), which is untouched. Both signals stay active.

---

## Removed: CloseFollow (your answer #4 from the prior round)

`CloseFollow` and its separate handling go away. **`KeepWithinDistance` is the single target:** stay
within that world distance of the leader and in line of sight (Component 1, step 1). `TransitionDistance`
is retained only as the cheap world-unit leader-jump pre-trigger for Component 3.

---

## Files

### Add
- `PathFinder/AStar.cs` — self-contained: grid in / grid out, injected pathable predicate, 8-dir,
  corner-cut guard, lazy-deletion heap, `NodeBudget` cap, unreachability return.
- `PathFinder/LeaderFollower.cs` — owns the world-space `_trail`, recording, string-pulling, and the
  background A* fallback. `Evaluate(playerPos, leaderPos, worldDistToLeader)` → next move target /
  Idle / PortalSuspected. `Reset(terrain)` on area change.

### Modify
- `Utils/LineOfSight.cs`: add `GetTerrainData()`, `IsTerrainReady()`, `EnsureTerrainData()`
  (500 ms-throttled refresh decoupled from debug rendering), and a **pure** `HasLineOfSightRaw()`
  (no `RefreshTerrainData()`, no `_debugVisiblePoints`/`_debugRays` writes). Make `UpdateTerrainData()`
  publish the array atomically.
- `Helper.cs`: `ToWorld(Vector2i)` (SharpDX.Vector3, height from `RawTerrainHeightData`) and
  `ToGrid(Vector3)`. Disambiguate SharpDX vs System.Numerics `Vector3`.
- `AreWeThereYet.cs`: instantiate `LeaderFollower`; on `AreaChange`, after publishing
  `AreaChangeEvent`, call `Reset(lineOfSight.GetTerrainData())` (null-safe) and cancel any in-flight
  search.
- `AutoPilot.cs`: replace **only** the movement-task generation in the `followTarget != null` branch
  (lines ~471–552) with `LeaderFollower.Evaluate(...)` + a small switch (Idle / move-to-world-pos /
  PortalSuspected). **Leave Transition, Loot, MercenaryOptIn, and the `followTarget == null` zone
  logic intact.** Use type-filtered `tasks.RemoveAll(t => t.Type == TaskNodeType.Movement)`. Give
  movement tasks a **tight** world-unit bound (≈ `ReachedBounds`), not the loose
  `KeepWithinDistance * 1.5`, so waypoints aren't skipped.
- `AreWeThereYetSettings.cs`: new `PathfindingSettings` submenu, **all world units** —
  `Enabled`, `TrailPointSpacing` (default ~40), `ReachedBounds` (default ~50), `AcquireDistance`
  (default ~1500), `MaxTrailPoints` (safety cap, e.g. 256), `NodeBudget`. Reuse existing
  `KeepWithinDistance` and `TransitionDistance`. Drop `CloseFollow`.

### Execution order
1. `PathfindingSettings` + remove `CloseFollow` usage.
2. `LineOfSight`: getters + `EnsureTerrainData` + `HasLineOfSightRaw` + atomic publish.
3. `Helper`: `ToWorld` / `ToGrid`.
4. `PathFinder/AStar.cs` (testable in isolation).
5. `PathFinder/LeaderFollower.cs` — trail + string-pull first (no search), verify, then add background
   fallback.
6. `AreWeThereYet.cs` wiring + area-change reset/cancel.
7. `AutoPilot.cs` surgical replacement.

## Fallback & safety
- `Pathfinding.Enabled == false` → original direct-click follow.
- Terrain null/not ready → Idle/direct movement until `EnsureTerrainData` populates (no dependence on
  debug rendering).
- Background A*: area-change cancellation, `NodeBudget` cap, terrain snapshot + atomic publish.
- Unreachable leader (present, no path) → PortalSuspected → walk to trail end → existing portal logic.
  Genuine zone transitions still handled by the existing entity-absence + zone-name signal.

## Trail management (recommendation, decided)
Self-pruning queue: consume breadcrumbs from the front as reached (`ReachedBounds`), so the trail
stays short while you keep pace. `MaxTrailPoints` bounds memory in long zones. If the follower falls
far behind the trail head (`AcquireDistance`, e.g. late zone-in), skip replaying history and run the
A* fallback straight to the leader, then resume trail-following. This keeps the common case at
zero-search while still recovering cleanly when you start far away.
