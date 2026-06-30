# Pathfinding Fixes — Plan

Three issues, root-caused against the current code. Each has a primary fix plus a
verification step. File references use current line numbers.

---

## Issue 1 — Pathfinding does not reset on death / release to checkpoint

### Root cause
`leaderFollower.Reset()` is only invoked from `AreWeThereYet.AreaChange()`
(`AreWeThereYet.cs:70`). Releasing to checkpoint keeps you in the same area
instance, so **no `AreaChangeEvent` fires** and the breadcrumb `_trail` is never
cleared. On respawn the player is teleported to the checkpoint, but:

- `AutoPilotLogic` only *skips* while `!IsAlive` (`AutoPilot.cs:379`) — it does no
  cleanup on the dead→alive transition.
- The stale `_trail` still describes the pre-death route. Because the trail head may
  still be within `AcquireDistance`, step 6 of `Tick` won't trigger the A* fallback,
  so the follower string-pulls along a trail that no longer connects to its new
  position instead of "falling back to backup."

### Fix
Add a **death / teleport detector** that calls `leaderFollower.Reset()` so the next
tick starts with an empty trail and naturally falls through to the A* fallback.

1. In `AutoPilot`, track liveness across ticks:
   - Add `private bool _wasAlive = true;`
   - At the top of `AutoPilotLogic`, before the `continue` guard, detect the
     transition. When `localPlayer.IsAlive == false` and `_wasAlive == true`, record
     death. When `IsAlive` returns to `true` after a death, call
     `AreWeThereYet.Instance.leaderFollower.Reset();` and `ResetPathing();` once,
     then set `_wasAlive = true`.
2. Add a **position-jump guard** as a backstop (covers checkpoint teleport, GG
   teleports, lab, etc. — anything that moves the player far without an area change).
   In `LeaderFollower.Tick`, before `RecordTrail`, compare `playerPos` to a stored
   `_lastPlayerPos`; if the jump exceeds `TransitionDistance` (the same threshold
   already used for leader portal detection at `LeaderFollower.cs:190`), call
   `Reset()` and re-seed. This makes reset robust even if the liveness hook is missed.
3. Optionally have `AutoPilot.ResetPathing()` (`AutoPilot.cs:37`) also call
   `leaderFollower.Reset()` so the two reset paths can't drift apart. (Right now
   `AreaChange` resets both separately, which is a latent inconsistency.)

### Verify
- Die in an open map, release to checkpoint. Confirm (debug log) that `Reset()` is
  hit on respawn and the first post-respawn `Tick` reports an empty trail and issues
  an A* request rather than a `MoveTo` along the old trail.
- Confirm a normal town→map area change still resets exactly once (no double reset
  regressions).

---

## Issue 2 — A* fallback produces "right then up" instead of a diagonal

### Root cause
In `AStar.cs`:
- `NeighborOffsets` lists the four cardinals **before** the four diagonals
  (`AStar.cs:15-19`).
- The octile `Heuristic` (`AStar.cs:117-122`) is *exact* on open terrain, so every
  shortest path shares the same `f = g + h`. With nothing to break ties, expansion
  follows neighbor-insertion order — cardinals first — so the reconstructed path
  comes out as an L / staircase (e.g. all-right then all-up).

The raw grid path is then seeded directly into `_trail`
(`LeaderFollower.cs:122-128`) with no grid-level smoothing, so the staircase
survives whenever LOS to the far endpoint is even partially obstructed.

### Fix (two complementary changes)
1. **Smooth the A* path before seeding the trail (primary).** After A* returns a
   grid path, run a line-of-sight string-pull *in grid space*: keep the start, then
   repeatedly skip to the furthest later cell reachable by a straight walkable line,
   dropping intermediate cells. On open ground a pure diagonal collapses to a single
   start→end segment; staircases collapse to clean diagonals. Use the **walkable**
   line test from Issue 3 (not ranged LOS) so smoothing never cuts a corner that
   isn't walkable.
2. **Bias A* toward straight paths (secondary, cheap).** Either:
   - Reorder `NeighborOffsets` to put diagonals first, **and/or**
   - Add a tiny tie-breaker so equal-cost nodes nearer the straight line to the goal
     win — e.g. nudge the heuristic by a cross-product term, or multiply `h` by
     `(1 + epsilon)` with a very small epsilon. This reduces nodes expanded and
     yields straighter paths even before smoothing.

Smoothing is the robust fix; the tie-breaker just makes the raw path nicer and the
search a bit cheaper.

### Verify
- Add a debug render of the raw A* grid path vs. the smoothed path. Stand diagonally
  from a target on open ground with the trail forced to empty (so A* runs) and
  confirm the smoothed path is a single diagonal segment, not an L.
- Unit-test `AStar.FindPath` on a synthetic open grid: assert a (0,0)→(n,n) request
  returns a strictly diagonal cell sequence after smoothing.

---

## Issue 3 — Breadcrumb trail cuts the path short, walking across a cliff (CRITICAL)

### Root cause
The string-pull in `Tick` (`LeaderFollower.cs:157-169`) chooses the furthest trail
point for which `HasLineOfSightRaw` is true. That call resolves to
`IsTerrainPassable` (`LineOfSight.cs:465-490`), which treats terrain value **2
(dashable / ranged-passable)** as passable when dash is enabled. A cliff gap is
exactly this case: melee layer = blocked (not walkable) but ranged layer passes, so
`CombineTerrainLayers` yields `2`. The straight line to the leader's breadcrumb is
therefore "visible," and the follower abandons the real ground route and walks into
the cliff.

**Line-of-sight is being used as a stand-in for walkability, and they are not the
same.** The trail represents a route the leader physically *walked*; any shortcut
across it must also be walkable, not merely see-through.

### Fix
Introduce a **walkability-only** straight-line test and use it for breadcrumb
shortcutting.

1. Add `LineOfSight.HasWalkableLineRaw(Vector3 start, Vector3 end)` — a DDA identical
   to `HasLineOfSightInternal` but with a strict walkable predicate: only values that
   are physically walkable (melee `5`, and `1` if you keep the default-walkable
   bucket) pass. **Value `2` (dashable) and `0` are NOT walkable** for this test,
   regardless of the `DashEnabled` setting.
2. In `LeaderFollower.Tick`, replace the `HasLineOfSightRaw` call in the string-pull
   loop (`LeaderFollower.cs:161`) with `HasWalkableLineRaw`. The trail may now only
   be shortcut along genuinely walkable straight lines; across a cliff it will keep
   following the breadcrumbs the leader actually left.
3. Leave the **early-exit "in position" check** (`LeaderFollower.cs:109-110`) on
   `HasLineOfSightRaw` — there, see-the-leader semantics are correct; we only want to
   skip moving when we can both see and are already close.
4. If you want dash-gap shortcutting later, make it explicit: detect a value-`2` gap,
   confirm dash is off cooldown, and emit a dedicated dash action — don't let plain
   "move" commands silently path across non-walkable tiles.

### Verify
- Reproduce the cliff scenario: leader walks up a ledge with a non-walkable
  see-through gap between. Confirm the follower now tracks the breadcrumb trail
  around/up rather than cutting straight across.
- Add a debug overlay distinguishing the two tests for the current
  player→furthest-breadcrumb line: walkable (green) vs. LOS-only (yellow) vs. blocked
  (red). The string-pull target must always sit on a green line.
- Regression: on fully open ground the walkable test must still collapse the trail to
  the furthest point (no over-conservative stalling).

---

## Suggested order of work
1. **Issue 3** first — it's the correctness/safety bug and it also produces the
   walkable-line primitive that Issue 2's smoothing depends on.
2. **Issue 2** — reuse the walkable-line test to smooth A* output; add the tie-break.
3. **Issue 1** — wire in death/teleport reset; it relies on A* fallback already
   behaving well (Issues 2/3) so the post-reset re-acquire is clean.

## Shared test pass
After all three: full loop — follow leader across mixed terrain, leader goes up a
cliff (Issue 3), take a diagonal across open ground (Issue 2), die and release to
checkpoint mid-route (Issue 1) — and confirm the follower re-acquires via A* and
resumes cleanly each time.
