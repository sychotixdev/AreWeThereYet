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
        if (!isPathable(terrain[startGrid.Y][startGrid.X])) return null;
        if (!isPathable(terrain[goalGrid.Y][goalGrid.X])) return null;

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
}
