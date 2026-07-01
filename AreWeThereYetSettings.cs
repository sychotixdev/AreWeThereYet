using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using SharpDX;

namespace AreWeThereYet;

public static class ColorExtensions
{
    public static Color ToSharpDx(this System.Drawing.Color color)
    {
        return new Color(color.R, color.G, color.B, color.A);
    }
}

public class AreWeThereYetSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(false);
    public AutoPilotSettings AutoPilot { get; set; } = new();
    public DebugSettings Debug { get; set; } = new();
}

[Submenu(CollapsedByDefault = false)]
public class AutoPilotSettings
{
    public ToggleNode Enabled { get; set; } = new(false);
    public ToggleNode RemoveGracePeriod { get; set; } = new(true);
    public TextNode LeaderName { get; set; } = new("");
    public ToggleNode DashEnabled { get; set; } = new(false);

    public HotkeyNode DashKey { get; set; } = new(Keys.W);
    public HotkeyNode MoveKey { get; set; } = new(Keys.Q);
    public HotkeyNode ToggleKey { get; set; } = new(Keys.NumPad9);

    // public RangeNode<int> RandomClickOffset { get; set; } = new(10, 1, 100);  -- not implemented right now
    public RangeNode<int> InputFrequency { get; set; } = new(50, 1, 100);
    public RangeNode<int> KeepWithinDistance { get; set; } = new(200, 10, 1000);
    public RangeNode<int> TransitionDistance { get; set; } = new(500, 100, 5000);

    [Menu("Transition Click Distance (world units)", "How close we must be to a transition label before clicking it. If further, we walk toward it first.")]
    public RangeNode<int> TransitionClickDistance { get; set; } = new(150, 20, 500);

    [Menu("Max Transition Click Attempts", "How many times to click the transition label before falling back to the party teleport button.")]
    public RangeNode<int> MaxTransitionAttempts { get; set; } = new(3, 1, 10);

    [Menu("Transition Wait Time (ms)", "How long to wait for a zone load after clicking a transition before treating the click as failed.")]
    public RangeNode<int> TransitionWaitTime { get; set; } = new(2500, 500, 10000);

    [Menu("Zone Update Buffer (ms)")]
    public RangeNode<int> ZoneUpdateBuffer { get; set; } = new(1000, 500, 5000);

    public VisualSettings Visual { get; set; } = new();

    [Submenu(CollapsedByDefault = true)]
    public class VisualSettings
    {
        public RangeNode<int> TaskLineWidth { get; set; } = new(3, 0, 10);
        public ColorNode TaskLineColor { get; set; } = new(System.Drawing.Color.Green.ToSharpDx());
    }
    
    public DashSettings Dash { get; set; } = new();
    public PathfindingSettings Pathfinding { get; set; } = new();
    
    [Submenu(CollapsedByDefault = true)]
    public class PathfindingSettings
    {
        [Menu("Enable Pathfinding (breadcrumb + A* fallback)")]
        public ToggleNode Enabled { get; set; } = new(true);

        [Menu("Trail Point Spacing (world units, ~40)")]
        public RangeNode<int> TrailPointSpacing { get; set; } = new(40, 10, 200);

        [Menu("Reached Bounds (world units, ~50)")]
        public RangeNode<int> ReachedBounds { get; set; } = new(50, 10, 300);

        [Menu("Acquire Distance (world units, ~1500)")]
        public RangeNode<int> AcquireDistance { get; set; } = new(1500, 200, 5000);

        [Menu("Max Trail Points (safety cap)")]
        public RangeNode<int> MaxTrailPoints { get; set; } = new(256, 64, 1024);

        [Menu("A* Node Budget (cap per search)")]
        public RangeNode<int> NodeBudget { get; set; } = new(50000, 1000, 500000);

        [Menu("Path Clearance (grid cells, character half-width, ~1)")]
        public RangeNode<int> PathClearance { get; set; } = new(1, 0, 6);

        [Menu("A* Recompute Cooldown (ms, min time between background searches)")]
        public RangeNode<int> AstarRecomputeIntervalMs { get; set; } = new(750, 100, 5000);

        // Camera.WorldToScreen() produces garbage (wildly out-of-range, direction-
        // incorrect coordinates) for world points far from the player - confirmed via
        // [ATY-PF] ScreenClamp logging: rawScreen values in the thousands, with a
        // constant/wrong bearing regardless of the true target direction, for any
        // click target beyond roughly this range. TrailScan's backward scan otherwise
        // greedily picks the FARTHEST walkable-line-reachable trail point, which on an
        // open map can be thousands of units away - clicking a target the camera can't
        // reliably project sends the character in an arbitrary (often reversed)
        // direction. Capping the scan keeps every click target within a range the
        // projection is known to handle correctly.
        [Menu("Max Click Distance (world units, cap on TrailScan target selection)")]
        public RangeNode<int> MaxClickDistance { get; set; } = new(1200, 200, 3000);

        // Previously, any tick where distToTarget > AcquireDistance requested a fresh
        // background search (subject only to the AstarRecomputeIntervalMs cooldown) -
        // regardless of whether the existing trail already had plenty of usable points
        // left. With a far leader that's most ticks for most of a run, so JPS fired on
        // a near-constant timer and almost every result was logged "discarded - existing
        // trail still usable". These two settings let a healthy, still-relevant trail
        // be left alone: only top it up when it's actually running low, or when the
        // target has moved far enough that the old search is stale.
        [Menu("Trail Refill Threshold (points remaining before requesting a background search)")]
        public RangeNode<int> TrailRefillThreshold { get; set; } = new(4, 1, 20);

        [Menu("Recompute Move Threshold (world units the target must move to justify a re-search)")]
        public RangeNode<int> RecomputeMoveThreshold { get; set; } = new(400, 100, 2000);
    }

    [Submenu(CollapsedByDefault = true)]
    public class DashSettings
    {
        // public RangeNode<int> TerrainValueForCollision { get; set; } = new(3, 0, 5);

        [Menu("Minimum Dash Distance")]
        public RangeNode<int> DashMinDistance { get; set; } = new(10, 0, 1000);

        [Menu("Maximum Dash Distance")]
        public RangeNode<int> DashMaxDistance { get; set; } = new(200, 0, 1000);
    }
}

[Submenu(CollapsedByDefault = true)]
public class DebugSettings
{
    public ToggleNode EnableRendering { get; set; } = new(true);
    public ToggleNode ShowTerrainDebug { get; set; } = new(false);
    public ToggleNode ShowDetailedDebug { get; set; } = new(false);

    [Menu("Log Pathfinding (follower decisions to debug log)")]
    public ToggleNode LogPathfinding { get; set; } = new(false);

    [Menu("Pathfinding Log Interval (ms)")]
    public RangeNode<int> PathfindingLogInterval { get; set; } = new(1000, 100, 5000);
    
    public RaycastSettings Raycast { get; set; } = new();
    public TerrainSettings Terrain { get; set; } = new();

    [Submenu(CollapsedByDefault = false)]
    public class RaycastSettings
    {
        public ToggleNode CastRayToWorldCursorPos { get; set; } = new(true);
        public ToggleNode DrawAtPlayerPlane { get; set; } = new(true);
        public RangeNode<int> TerrainValueForCollision { get; set; } = new(2, 0, 5);
    }

    [Submenu(CollapsedByDefault = false)]
    public class TerrainSettings
    {
        public ToggleNode ReplaceValuesWithDots { get; set; } = new(false);
        public RangeNode<float> DotSize { get; set; } = new(3.0f, 1.0f, 100.0f);
        public RangeNode<int> DotSegments { get; set; } = new(16, 3, 6);
        public RangeNode<int> RefreshInterval { get; set; } = new(500, 100, 2000);

        public TerrainColor Colors { get; set; } = new();

        [Submenu(CollapsedByDefault = false)]
        public class TerrainColor
        {
            // Red - Impassable
            [Menu("Tile0 - Impassable")]
            public ColorNode Tile0 { get; set; } = new(System.Drawing.Color.FromArgb(200, 255, 100, 50).ToSharpDx());

            // Light Green - Basic walkable
            [Menu("Tile1 - Basic walkable")]
            public ColorNode Tile1 { get; set; } = new(System.Drawing.Color.FromArgb(0, 100, 255, 100).ToSharpDx());

            // Yellow - Static objects (dashable)
            [Menu("Tile2 - Static objects (dashable)")]
            public ColorNode Tile2 { get; set; } = new(System.Drawing.Color.FromArgb(180, 255, 255, 0).ToSharpDx());

            // Blue - Reserved
            [Menu("Tile3 - Reserved")]
            public ColorNode Tile3 { get; set; } = new(System.Drawing.Color.FromArgb(0, 0, 0, 255).ToSharpDx());

            // Purple - Reserved
            [Menu("Tile4 - Reserved")]
            public ColorNode Tile4 { get; set; } = new(System.Drawing.Color.FromArgb(0, 128, 0, 128).ToSharpDx());

            // Dark Green - Open walkable space
            [Menu("Tile5 - Open walkable space")]
            public ColorNode Tile5 { get; set; } = new(System.Drawing.Color.FromArgb(160, 0, 200, 0).ToSharpDx());

            // Gray - Unknown - EntityColors.Shadow.Value ?
            [Menu("TileUnknown - Unknown")]
            public ColorNode TileUnknown { get; set; } = new(System.Drawing.Color.FromArgb(160, 128, 128, 128).ToSharpDx());
        }
    }
}
