using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using GameOffsets.Native;

namespace AreWeThereYet.PathFinder;

/// <summary>
/// Outcome classification for a single pathfinding request.
/// </summary>
public enum PathOutcome
{
    /// <summary>A complete path to the goal was found.</summary>
    Reached,
    /// <summary>Node budget hit before reaching the goal — Path is the closest approach.</summary>
    Partial,
    /// <summary>Open set drained (or budget hit with no progress) — goal not reachable.</summary>
    Unreachable,
    /// <summary>Bad input (null terrain, out-of-bounds, un-snappable endpoint).</summary>
    Invalid,
}

/// <summary>
/// Result of a pathfinding request: the path (null when none) plus lightweight
/// diagnostics. Returned by value so the search stays free of any GameController /
/// logging access and remains safe to run on a background thread — the caller logs
/// the stats on the main thread.
/// </summary>
public readonly struct PathResult
{
    /// <summary>Sparse waypoint path (jump points), start→goal, or null when none.</summary>
    public List<Vector2i>? Path { get; init; }
    public PathOutcome Outcome { get; init; }
    /// <summary>Jump points dequeued and expanded.</summary>
    public int Expanded { get; init; }
    /// <summary>Jump points enqueued (successors generated).</summary>
    public int Generated { get; init; }
    /// <summary>g-cost (grid units) of the returned path's final node.</summary>
    public float Cost { get; init; }
    /// <summary>Wall-clock search time.</summary>
    public double ElapsedMs { get; init; }
}

/// <summary>
/// Self-contained Jump Point Search pathfinder operating entirely in grid space.
///
/// JPS is an optimisation of A* for uniform-cost 8-connected grids: instead of
/// expanding every cell it "jumps" over open runs, recording only the cells where the
/// path could bend (jump points). On open terrain this expands orders of magnitude
/// fewer nodes than plain grid A*, which is exactly the case the follower hits when
/// re-acquiring a far-off leader.
///
/// Diagonal rule is strict "no corner cutting": a diagonal step is legal only when
/// BOTH orthogonally-adjacent cells are pathable. This matches the rule the previous
/// grid A* used and the MoveDiagonallyIfNoObstacles variant from the JPS literature.
///
/// Thread-safe: operates only on the supplied terrain snapshot, no GameController access.
/// </summary>
public static class AStar
{
    private const float DiagCost = 1.41421356f; // √2

    // Max Chebyshev radius (in cells) to search when snapping an unpathable start/goal
    // onto walkable terrain. ~10 cells ≈ 108 world units — enough to recover from a
    // truncation/edge quantisation without silently teleporting the goal across a wall.
    private const int SnapRadius = 10;

    // Tiny tie-break factor applied to the heuristic. Breaks ties between equal-cost
    // paths in favour of the one nearer the goal, nudging the search toward straight
    // lines. Kept small so path length stays effectively optimal.
    private const float TieBreak = 1.0f + 1.0f / 1024f;

    /// <summary>
    /// Finds a grid-space path from <paramref name="startGrid"/> to <paramref name="goalGrid"/>
    /// using Jump Point Search. Consecutive points in the returned path are connected by a
    /// straight (cardinal or pure-diagonal) walkable run, so the caller may smooth/string-pull
    /// them directly.
    /// </summary>
    /// <param name="terrain">Terrain snapshot (indexed [y][x], never mutated).</param>
    /// <param name="startGrid">Start cell in grid coordinates.</param>
    /// <param name="goalGrid">Goal cell in grid coordinates.</param>
    /// <param name="isPathable">Predicate: true = cell can be entered.</param>
    /// <param name="nodeBudget">Maximum jump points to expand before giving up.</param>
    /// <param name="ct">Cancellation token checked on each expansion.</param>
    public static PathResult FindPath(
        int[][] terrain,
        Vector2i startGrid,
        Vector2i goalGrid,
        Func<int, bool> isPathable,
        int nodeBudget,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (terrain == null || terrain.Length == 0)
            return Invalid(sw);

        int rows = terrain.Length;
        int cols = terrain[0].Length;

        if (!InBounds(startGrid, rows, cols) || !InBounds(goalGrid, rows, cols))
            return Invalid(sw);

        // Snap start/goal onto walkable terrain when they land on an unpathable cell.
        // (int) truncation in Helper.ToGrid can quantise the leader onto an adjacent
        // wall cell even while it genuinely stands on walkable ground, and a leader at
        // the edge of walkable terrain has the same effect. Returning null here makes
        // the follower falsely assume a portal, so instead we pull the endpoint to the
        // nearest pathable cell within a small radius.
        if (!isPathable(terrain[startGrid.Y][startGrid.X]) &&
            !TrySnapToPathable(terrain, ref startGrid, isPathable, rows, cols, SnapRadius))
            return Invalid(sw);
        if (!isPathable(terrain[goalGrid.Y][goalGrid.X]) &&
            !TrySnapToPathable(terrain, ref goalGrid, isPathable, rows, cols, SnapRadius))
            return Invalid(sw);

        if (startGrid.X == goalGrid.X && startGrid.Y == goalGrid.Y)
            return new PathResult
            {
                Path = new List<Vector2i> { startGrid },
                Outcome = PathOutcome.Reached,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
            };

        int gx = goalGrid.X, gy = goalGrid.Y;

        // ---- Local grid helpers (capture terrain/bounds/predicate) --------------------

        bool Walkable(int x, int y)
            => x >= 0 && x < cols && y >= 0 && y < rows && isPathable(terrain[y][x]);

        // Walk straight in a cardinal direction (exactly one of dx,dy is non-zero).
        // Returns the first jump point reached, or null if the run dead-ends. Mirrors the
        // straight branch of JPS _jump: stop at the goal or at a forced neighbour created
        // by a wall beside the previous cell.
        Vector2i? JumpStraight(int sx, int sy, int dx, int dy)
        {
            int x = sx, y = sy;
            while (true)
            {
                if (!Walkable(x + dx, y + dy)) return null;
                x += dx; y += dy;

                if (x == gx && y == gy) return new Vector2i { X = x, Y = y };

                if (dx != 0) // horizontal
                {
                    if ((Walkable(x, y - 1) && !Walkable(x - dx, y - 1)) ||
                        (Walkable(x, y + 1) && !Walkable(x - dx, y + 1)))
                        return new Vector2i { X = x, Y = y };
                }
                else // vertical
                {
                    if ((Walkable(x - 1, y) && !Walkable(x - 1, y - dy)) ||
                        (Walkable(x + 1, y) && !Walkable(x + 1, y - dy)))
                        return new Vector2i { X = x, Y = y };
                }
            }
        }

        // Walk diagonally (both dx,dy non-zero). At each diagonal cell, probe the two
        // cardinal directions for jump points; if either exists, the current diagonal cell
        // is itself a jump point. No corner cutting: a diagonal step is only taken when
        // both orthogonal cells (and the target) are walkable.
        Vector2i? JumpDiagonal(int sx, int sy, int dx, int dy)
        {
            int x = sx, y = sy;
            while (true)
            {
                // No-corner-cut guard for the diagonal step + target walkability.
                if (!(Walkable(x + dx, y) && Walkable(x, y + dy))) return null;
                if (!Walkable(x + dx, y + dy)) return null;
                x += dx; y += dy;

                if (x == gx && y == gy) return new Vector2i { X = x, Y = y };

                if (JumpStraight(x, y, dx, 0).HasValue) return new Vector2i { X = x, Y = y };
                if (JumpStraight(x, y, 0, dy).HasValue) return new Vector2i { X = x, Y = y };
            }
        }

        // Pruned successor directions for a node, given the direction it was entered from
        // (pdx,pdy). Start node (0,0) considers all eight. Matches the neighbour pruning of
        // the no-corner-cutting JPS variant.
        void CollectDirections(int x, int y, int pdx, int pdy, List<(int dx, int dy)> outDirs)
        {
            outDirs.Clear();

            if (pdx == 0 && pdy == 0)
            {
                outDirs.Add((1, 0)); outDirs.Add((-1, 0));
                outDirs.Add((0, 1)); outDirs.Add((0, -1));
                outDirs.Add((1, 1)); outDirs.Add((1, -1));
                outDirs.Add((-1, 1)); outDirs.Add((-1, -1));
                return;
            }

            if (pdx != 0 && pdy != 0) // diagonal
            {
                bool wy = Walkable(x, y + pdy);
                bool wx = Walkable(x + pdx, y);
                if (wy) outDirs.Add((0, pdy));
                if (wx) outDirs.Add((pdx, 0));
                if (wy && wx) outDirs.Add((pdx, pdy));
            }
            else if (pdx != 0) // horizontal
            {
                bool isNext = Walkable(x + pdx, y);
                bool isTop = Walkable(x, y + 1);
                bool isBottom = Walkable(x, y - 1);
                if (isNext)
                {
                    outDirs.Add((pdx, 0));
                    if (isTop) outDirs.Add((pdx, 1));
                    if (isBottom) outDirs.Add((pdx, -1));
                }
                if (isTop) outDirs.Add((0, 1));
                if (isBottom) outDirs.Add((0, -1));
            }
            else // vertical (pdy != 0)
            {
                bool isNext = Walkable(x, y + pdy);
                bool isRight = Walkable(x + 1, y);
                bool isLeft = Walkable(x - 1, y);
                if (isNext)
                {
                    outDirs.Add((0, pdy));
                    if (isRight) outDirs.Add((1, pdy));
                    if (isLeft) outDirs.Add((-1, pdy));
                }
                if (isRight) outDirs.Add((1, 0));
                if (isLeft) outDirs.Add((-1, 0));
            }
        }

        // ---- A* over jump points ------------------------------------------------------

        // Lazy-deletion priority queue: enqueue (node, version) to skip stale entries.
        var queue    = new PriorityQueue<(Vector2i pos, int ver), float>();
        var gScore   = new Dictionary<Vector2i, float>();
        var cameFrom = new Dictionary<Vector2i, Vector2i>();
        var versions = new Dictionary<Vector2i, int>();
        var dirs     = new List<(int dx, int dy)>(8);

        gScore[startGrid] = 0f;
        versions[startGrid] = 0;
        queue.Enqueue((startGrid, 0), Heuristic(startGrid, goalGrid));

        int expanded = 0;
        int generated = 0;

        // Track the closest-to-goal jump point expanded so far so a budget-limited search
        // can still return useful forward progress (Partial) rather than nothing.
        Vector2i closest = startGrid;
        float closestH = Heuristic(startGrid, goalGrid);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var (current, ver) = queue.Dequeue();

            // Lazy deletion: stale entry if version doesn't match.
            if (versions.TryGetValue(current, out int curVer) && ver != curVer)
                continue;

            if (current.X == gx && current.Y == gy)
                return Done(ReconstructPath(cameFrom, current), PathOutcome.Reached,
                            gScore[current], expanded, generated, sw);

            float hCur = Heuristic(current, goalGrid);
            if (hCur < closestH)
            {
                closestH = hCur;
                closest = current;
            }

            if (++expanded > nodeBudget)
            {
                // Budget exhausted — hand back the best partial path, or signal unreachable
                // if we never advanced past the start.
                return (closest.X == startGrid.X && closest.Y == startGrid.Y)
                    ? Done(null, PathOutcome.Unreachable, 0f, expanded, generated, sw)
                    : Done(ReconstructPath(cameFrom, closest), PathOutcome.Partial,
                           gScore[closest], expanded, generated, sw);
            }

            float g = gScore[current];

            // Direction this node was entered from (start node => all directions).
            int pdx = 0, pdy = 0;
            if (cameFrom.TryGetValue(current, out var parent))
            {
                pdx = Math.Sign(current.X - parent.X);
                pdy = Math.Sign(current.Y - parent.Y);
            }

            CollectDirections(current.X, current.Y, pdx, pdy, dirs);

            foreach (var (dx, dy) in dirs)
            {
                Vector2i? jp = (dx != 0 && dy != 0)
                    ? JumpDiagonal(current.X, current.Y, dx, dy)
                    : JumpStraight(current.X, current.Y, dx, dy);

                if (jp is not Vector2i j) continue;

                // Cost of a straight (cardinal/diagonal) run is exact octile distance.
                float tentativeG = g + StraightLineCost(current, j);

                if (!gScore.TryGetValue(j, out float existingG) || tentativeG < existingG)
                {
                    gScore[j] = tentativeG;
                    cameFrom[j] = current;

                    int newVer = versions.TryGetValue(j, out int oldVer) ? oldVer + 1 : 0;
                    versions[j] = newVer;

                    queue.Enqueue((j, newVer), tentativeG + Heuristic(j, goalGrid));
                    generated++;
                }
            }
        }

        // Open set exhausted — goal genuinely unreachable from start.
        return Done(null, PathOutcome.Unreachable, 0f, expanded, generated, sw);
    }

    // ---- Result helpers -------------------------------------------------------

    private static PathResult Invalid(Stopwatch sw) => new()
    {
        Path = null,
        Outcome = PathOutcome.Invalid,
        ElapsedMs = sw.Elapsed.TotalMilliseconds,
    };

    private static PathResult Done(
        List<Vector2i>? path, PathOutcome outcome, float cost,
        int expanded, int generated, Stopwatch sw) => new()
    {
        Path = path,
        Outcome = outcome,
        Cost = cost,
        Expanded = expanded,
        Generated = generated,
        ElapsedMs = sw.Elapsed.TotalMilliseconds,
    };

    // Octile distance, tie-broken toward the goal. Admissible heuristic for 8-direction
    // movement (the tie-break inflation is negligible and only orders equal-cost nodes).
    private static float Heuristic(Vector2i a, Vector2i b)
    {
        float dx = MathF.Abs(a.X - b.X);
        float dy = MathF.Abs(a.Y - b.Y);
        return (Math.Max(dx, dy) + (DiagCost - 1f) * Math.Min(dx, dy)) * TieBreak;
    }

    // Exact cost of a straight cardinal/diagonal run between two cells (no tie-break).
    private static float StraightLineCost(Vector2i a, Vector2i b)
    {
        int dx = Math.Abs(a.X - b.X);
        int dy = Math.Abs(a.Y - b.Y);
        return Math.Max(dx, dy) + (DiagCost - 1f) * Math.Min(dx, dy);
    }

    private static List<Vector2i> ReconstructPath(Dictionary<Vector2i, Vector2i> cameFrom, Vector2i goal)
    {
        var path = new List<Vector2i>();
        var current = goal;
        while (cameFrom.TryGetValue(current, out var prev))
        {
            path.Add(current);
            current = prev;
        }
        path.Add(current); // start node
        path.Reverse();
        return path;
    }

    private static bool InBounds(Vector2i pos, int rows, int cols)
        => pos.X >= 0 && pos.X < cols && pos.Y >= 0 && pos.Y < rows;

    /// <summary>
    /// Searches outward in expanding Chebyshev rings for the nearest pathable cell and,
    /// if found within <paramref name="maxRadius"/>, rewrites <paramref name="cell"/> to
    /// it and returns true. Within a ring the Euclidean-closest candidate wins so the
    /// snap is as small as possible. Returns false when no pathable cell is in range.
    /// </summary>
    private static bool TrySnapToPathable(
        int[][] terrain,
        ref Vector2i cell,
        Func<int, bool> isPathable,
        int rows,
        int cols,
        int maxRadius)
    {
        for (int r = 1; r <= maxRadius; r++)
        {
            bool found = false;
            Vector2i best = default;
            int bestDistSq = int.MaxValue;

            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                // Only the perimeter of the current ring (closer rings already scanned).
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;

                var c = new Vector2i { X = cell.X + dx, Y = cell.Y + dy };
                if (!InBounds(c, rows, cols)) continue;
                if (!isPathable(terrain[c.Y][c.X])) continue;

                int distSq = dx * dx + dy * dy;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = c;
                    found = true;
                }
            }

            if (found)
            {
                cell = best;
                return true;
            }
        }

        return false;
    }
}
