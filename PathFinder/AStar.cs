using System;
using System.Collections.Generic;
using System.Threading;
using GameOffsets.Native;

namespace AreWeThereYet.PathFinder;

/// <summary>
/// Self-contained A* pathfinder operating entirely in grid space.
/// Thread-safe: operates only on the supplied terrain snapshot, no GameController access.
/// </summary>
public static class AStar
{
    // 8-direction offsets: diagonals FIRST so that, among equal-cost paths, the search
    // front expands diagonals before cardinals. Combined with the heuristic tie-break
    // below this biases results toward straight diagonals instead of "right then up"
    // L-shapes. (Paths are also string-pulled downstream, but a straighter raw path
    // means fewer nodes expanded and cleaner smoothing.)
    private static readonly (int dx, int dy)[] NeighborOffsets =
    [
        (-1, -1), (-1,  1), ( 1, -1), ( 1,  1),   // diagonals
        ( 0, -1), ( 0,  1), (-1,  0), ( 1,  0),    // cardinals
    ];

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
    /// Finds a grid-space path from <paramref name="startGrid"/> to <paramref name="goalGrid"/>.
    /// Returns null when unreachable or when the node budget is exhausted.
    /// </summary>
    /// <param name="terrain">Terrain snapshot (indexed [y][x], never mutated).</param>
    /// <param name="startGrid">Start cell in grid coordinates.</param>
    /// <param name="goalGrid">Goal cell in grid coordinates.</param>
    /// <param name="isPathable">Predicate: true = cell can be entered.</param>
    /// <param name="nodeBudget">Maximum cells to expand before giving up.</param>
    /// <param name="ct">Cancellation token checked on each expansion.</param>
    public static List<Vector2i>? FindPath(
        int[][] terrain,
        Vector2i startGrid,
        Vector2i goalGrid,
        Func<int, bool> isPathable,
        int nodeBudget,
        CancellationToken ct)
    {
        if (terrain == null || terrain.Length == 0) return null;

        int rows = terrain.Length;
        int cols = terrain[0].Length;

        if (!InBounds(startGrid, rows, cols) || !InBounds(goalGrid, rows, cols)) return null;

        // Snap start/goal onto walkable terrain when they land on an unpathable cell.
        // (int) truncation in Helper.ToGrid can quantise the leader onto an adjacent
        // wall cell even while it genuinely stands on walkable ground, and a leader at
        // the edge of walkable terrain has the same effect. Returning null here makes
        // the follower falsely assume a portal, so instead we pull the endpoint to the
        // nearest pathable cell within a small radius.
        if (!isPathable(terrain[startGrid.Y][startGrid.X]) &&
            !TrySnapToPathable(terrain, ref startGrid, isPathable, rows, cols, SnapRadius))
            return null;
        if (!isPathable(terrain[goalGrid.Y][goalGrid.X]) &&
            !TrySnapToPathable(terrain, ref goalGrid, isPathable, rows, cols, SnapRadius))
            return null;

        if (startGrid.X == goalGrid.X && startGrid.Y == goalGrid.Y)
            return new List<Vector2i> { startGrid };

        // Lazy-deletion priority queue: enqueue (node, version) to skip stale entries
        var queue    = new PriorityQueue<(Vector2i pos, int ver), float>();
        var gScore   = new Dictionary<Vector2i, float>();
        var cameFrom = new Dictionary<Vector2i, Vector2i>();
        var versions = new Dictionary<Vector2i, int>();

        gScore[startGrid] = 0f;
        versions[startGrid] = 0;
        queue.Enqueue((startGrid, 0), Heuristic(startGrid, goalGrid));

        int explored = 0;

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var (current, ver) = queue.Dequeue();

            // Lazy deletion: stale entry if version doesn't match
            if (versions.TryGetValue(current, out int curVer) && ver != curVer)
                continue;

            if (current.X == goalGrid.X && current.Y == goalGrid.Y)
                return ReconstructPath(cameFrom, current);

            if (++explored > nodeBudget)
                return null; // Budget exhausted

            float g = gScore[current];

            foreach (var (dx, dy) in NeighborOffsets)
            {
                var neighbor = new Vector2i { X = current.X + dx, Y = current.Y + dy };
                if (!InBounds(neighbor, rows, cols)) continue;
                if (!isPathable(terrain[neighbor.Y][neighbor.X])) continue;

                // Corner-cut guard: a diagonal step is only allowed when both
                // orthogonal neighbours are pathable (no squeezing past walls).
                bool isDiag = dx != 0 && dy != 0;
                if (isDiag)
                {
                    if (!isPathable(terrain[current.Y][current.X + dx])) continue;
                    if (!isPathable(terrain[current.Y + dy][current.X])) continue;
                }

                float tentativeG = g + (isDiag ? DiagCost : 1f);

                if (!gScore.TryGetValue(neighbor, out float existingG) || tentativeG < existingG)
                {
                    gScore[neighbor] = tentativeG;
                    cameFrom[neighbor] = current;

                    int newVer = versions.TryGetValue(neighbor, out int oldVer) ? oldVer + 1 : 0;
                    versions[neighbor] = newVer;

                    queue.Enqueue((neighbor, newVer), tentativeG + Heuristic(neighbor, goalGrid));
                }
            }
        }

        return null; // Open set exhausted — goal unreachable
    }

    // Octile distance: admissible heuristic for 8-direction movement
    private static float Heuristic(Vector2i a, Vector2i b)
    {
        float dx = MathF.Abs(a.X - b.X);
        float dy = MathF.Abs(a.Y - b.Y);
        return (Math.Max(dx, dy) + (DiagCost - 1f) * Math.Min(dx, dy)) * TieBreak;
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
