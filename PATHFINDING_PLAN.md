# Pathfinding Integration Plan

## Algorithm: Theta* with LOS-target goal

**Theta\*** is A* with one modification: during edge relaxation, if the grandparent node has direct
line-of-sight to the candidate node, skip the intermediate parent. This produces any-angle paths —
smooth, natural-looking movement with far fewer waypoints than grid A*. It plugs directly into the
existing `LineOfSight.HasLineOfSight()` DDA raycast.

**LOS-target goal:** Rather than pathing to the leader's exact grid tile, we first find the nearest
walkable tile near the leader that satisfies:
- Distance to leader ≤ `KeepWithinDistance`
- `HasLineOfSight(tile, leaderPos)` == true

We path to *that* tile. The follower naturally holds at the edge of sight range rather than stacking
on the leader.

---

## Path Management Strategy

The key design principle: **a path only becomes wrong at its end, not its beginning.** If the leader
takes one more step forward, the first 90% of our existing path is still perfectly valid. We should
only act when the *endpoint* is no longer pointing at the right place.

### Three configurable thresholds

| Setting | Suggested Default | Meaning |
|---|---|---|
| `EndpointTolerance` | 40 tiles | How far the leader can stray from our path endpoint before we act |
| `ExtensionThreshold` | 150 tiles | Beyond this distance from endpoint → full recompute instead of extension |
| `KeepWithinDistance` | 200 tiles | Maximum acceptable distance to leader (existing setting) |

### Per-tick path management decision tree

```
1. LOS check (cheapest, runs every tick)
   └─ Has LOS to leader AND distance ≤ KeepWithinDistance?
      ├─ YES → idle/stop. No path work needed.
      └─ NO  → proceed to step 2.

2. Do we have an active path?
   └─ NO → compute fresh path from player to LOS-target near leader. Done.
   └─ YES → proceed to step 3.

3. How far is the leader from our path's last waypoint?
   └─ ≤ EndpointTolerance
      └─ Keep following current path. No changes needed.
   └─ > EndpointTolerance
      └─ Try Theta*: path.LastWaypoint → LOS-target near leader

         Result A: No path found (leader unreachable from endpoint)
         └─ Leader likely took a zone transition near the endpoint.
            → Finish walking current path to endpoint.
            → At endpoint, begin zone transition search (existing portal/label logic).

         Result B: Path found, distance ≤ ExtensionThreshold
         └─ Append extension to current path.
            → Continue following (now the combined path).

         Result C: Path found, distance > ExtensionThreshold
         └─ Leader has moved very far. Full recompute.
            → Discard current path.
            → Theta*: playerCurrentPos → LOS-target near leader.
            → Follow new path.
```

### Why this works for a moving leader

- The leader is usually near the endpoint (within tolerance) → no work done.
- When they drift past the tolerance, we first try an *extension* so the early path segments are
  reused. This is a short A* call from endpoint to new goal, not a full map scan.
- Extension and full recompute are both Theta* calls. They're fast (typically <5ms) and synchronous,
  so no background Task is needed.
- The no-path-found branch turns what was an awkward edge case (zone detection while mid-path) into
  a natural, geometrically correct trigger: "if I can't reach where you were, you must have gone
  through a portal there."

### Early exit while path following

On every movement tick while following a path, check:
```
if HasLineOfSight(player, leader) AND distance(player, leader) ≤ KeepWithinDistance:
    stop path, go idle
```
This means the follower stops the moment it regains sight of the leader, even if waypoints remain.
No wasted movement overrunning the leader.

---

## Zone Transition Detection via Unreachable Leader

The current approach relies on the party UI zone name and a timer buffer. The new approach adds a
geometric complement:

When `path.LastWaypoint → leader` returns no valid path (Result A above):
1. The follower finishes walking to the endpoint — arriving near where the leader last was.
2. At the endpoint, the existing portal/transition search logic activates. It's now searching in
   exactly the right location (near the transition the leader used), rather than scanning from
   wherever the follower happened to be standing.

This is an improvement over the current behavior, where the follower might be far from the transition
when it decides to search. It also gives us a second, reliable signal for zone transitions that
doesn't depend on party UI data freshness.

Both signals (UI zone name mismatch + no valid path) remain active. Either one can trigger
transition-seeking behavior.

---

## Files to Add

### 1. `PathFinder/Theta.cs` (new)

Theta* implementation, ~150 lines:

```csharp
// Inputs:  int[][] terrainData, Vector2i start, Vector2i goal, int[] pathableValues
//          Func<Vector2i, Vector2i, bool> hasLineOfSight (delegate to LineOfSight)
// Output:  List<Vector2i>? — null if no path exists
//
// Key difference from A*: during relaxation of neighbor N with parent P,
//   if HasLineOfSight(P.parent, N): use P.parent as N's parent directly (skip P)
//   else: standard A* relaxation through P
//
// Heuristic: Euclidean distance to goal
// Neighbor offsets: 8-directional (same as Radar's PathFinder)
// Goal condition: tile satisfies LOS to leader AND distance ≤ KeepWithinDistance
//   (the "LOS-target" tile is found before calling Theta*, passed in as `goal`)
```

**BinaryHeap / PriorityQueue:** Use .NET 6's built-in `PriorityQueue<Vector2i, float>` rather than
sourcing Radar's `BinaryHeap.cs`. The API maps cleanly: `Enqueue(element, priority)` and
`TryDequeue(out element, out priority)`.

### 2. `PathFinder/LeaderPathFinder.cs` (new)

Orchestrates everything described in the decision tree above:

```csharp
public class LeaderPathFinder
{
    // State
    private List<Vector2i> _currentPath;  // active waypoints (world-facing code pops from front)
    private int _currentWaypointIndex;
    private Vector2i _pathEndpoint;       // last waypoint of current path

    // Called each tick from AutoPilot
    public PathUpdate Evaluate(Vector2i playerPos, Vector2i leaderPos);

    // Called on area change
    public void Reset(int[][] terrainData);

    // Internal
    private Vector2i? FindLosTarget(Vector2i nearLeader);
    private List<Vector2i>? RunTheta(Vector2i from, Vector2i to);
}

public enum PathUpdateKind { Idle, FollowExisting, PathUpdated, TransitionSuspected }

public record PathUpdate(PathUpdateKind Kind, List<Vector2i>? Path = null);
```

---

## Files to Modify

### 3. `Utils/LineOfSight.cs`

Expose terrain data and a thread-safe LOS delegate:

```csharp
public int[][] GetTerrainData() => _terrainData;

// Overload that doesn't touch debug state — safe to call from Theta* inner loop
public bool HasLineOfSightRaw(Vector2i a, Vector2i b) { /* DDA, no _debugVisiblePoints */ }
```

### 4. `AreWeThereYet.cs`

```csharp
internal LeaderPathFinder leaderPathFinder = new();

public override void AreaChange(AreaInstance area)
{
    base.AreaChange(area);
    EventBus.Instance.Publish(new AreaChangeEvent());  // updates lineOfSight terrain data
    leaderPathFinder.Reset(lineOfSight.GetTerrainData());
    autoPilot.AreaChange();
}
```

### 5. `AutoPilot.cs`

Replace the movement task generation block with calls to `LeaderPathFinder.Evaluate()`:

```csharp
// Inside AutoPilotLogic(), where followTarget != null and distance > KeepWithinDistance:

var playerGrid = playerPosition.WorldToGrid().ToVector2i();
var leaderGrid = followTarget.Pos.WorldToGrid().ToVector2i();
var update = AreWeThereYet.Instance.leaderPathFinder.Evaluate(playerGrid, leaderGrid);

switch (update.Kind)
{
    case PathUpdateKind.Idle:
        // Clear movement tasks, we're in range and visible
        tasks.RemoveAll(t => t.Type == TaskNodeType.Movement);
        break;

    case PathUpdateKind.FollowExisting:
        // No change to tasks needed — already following
        break;

    case PathUpdateKind.PathUpdated:
        // Replace movement tasks with new waypoints (decimated)
        tasks.RemoveAll(t => t.Type == TaskNodeType.Movement);
        foreach (var waypoint in DecimateWaypoints(update.Path))
            tasks.Add(new TaskNode(GridToWorldPosition(waypoint), waypointBounds));
        break;

    case PathUpdateKind.TransitionSuspected:
        // Finish current path; portal search will activate once we arrive
        // (existing transition logic handles this once followTarget == null)
        break;
}
```

**`DecimateWaypoints`**: emit only the waypoints where direction changes significantly, or every
`WaypointSpacing` tiles — whichever is fewer. Avoids flooding `tasks` with single-tile steps.

### 6. `AreWeThereYetSettings.cs`

Add under `AutoPilotSettings`:

```csharp
public PathfindingSettings Pathfinding { get; set; } = new();

[Submenu(CollapsedByDefault = true)]
public class PathfindingSettings
{
    [Menu("Enable Pathfinding")]
    public ToggleNode Enabled { get; set; } = new(true);

    [Menu("Endpoint Tolerance (tiles) — act if leader drifts past this from our endpoint")]
    public RangeNode<int> EndpointTolerance { get; set; } = new(40, 10, 200);

    [Menu("Full Recompute Threshold (tiles) — recompute whole path if extension is this long")]
    public RangeNode<int> ExtensionThreshold { get; set; } = new(150, 50, 500);

    [Menu("Waypoint Spacing (tiles)")]
    public RangeNode<int> WaypointSpacing { get; set; } = new(12, 1, 50);
}
```

### 7. `Helper.cs`

```csharp
internal static Vector3 GridToWorldPosition(Vector2i gridPos)
{
    var worldXY = new System.Numerics.Vector2(gridPos.X, gridPos.Y).GridToWorld();
    var z = AreWeThereYet.Instance.GameController.IngameState.Data
                .GetTerrainHeightAt(new System.Numerics.Vector2(gridPos.X, gridPos.Y));
    return new SharpDX.Vector3(worldXY.X, worldXY.Y, z);
}

internal static Vector2i ToVector2i(this System.Numerics.Vector2 v)
    => new Vector2i((int)v.X, (int)v.Y);
```

---

## Execution Order

1. Add `PathfindingSettings` to `AreWeThereYetSettings.cs` — needed before anything else compiles.
2. Add `HasLineOfSightRaw()` and `GetTerrainData()` to `LineOfSight.cs` — minimal, no risk.
3. Add `GridToWorldPosition` / `ToVector2i` helpers to `Helper.cs`.
4. Implement `PathFinder/Theta.cs` — self-contained, no dependencies on the above yet.
5. Implement `PathFinder/LeaderPathFinder.cs` — depends on Theta* and LineOfSight changes.
6. Wire into `AreWeThereYet.cs` — instantiate, connect area change.
7. Update `AutoPilot.cs` — the largest change; replace movement task generation.

---

## Fallback & Safety

- If `Pathfinding.Enabled` is false, `LeaderPathFinder.Evaluate()` returns `PathUpdateKind.Idle`
  and `AutoPilot` falls back to the original direct-click movement.
- If `GetTerrainData()` returns null (area change mid-load), Theta* returns null and the follower
  falls back to direct movement until terrain is ready.
- The zone transition detection adds a complementary geometric signal alongside the existing
  party UI zone-name check — both remain active.
