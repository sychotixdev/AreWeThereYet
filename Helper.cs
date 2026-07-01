using System;
using SharpDX;
using ExileCore.PoEMemory.MemoryObjects;
using GameOffsets.Native;
using NumVec2 = System.Numerics.Vector2;

namespace AreWeThereYet;

public static class Helper
{
    // ---------------------------------------------------------------------------
    // Grid ↔ World conversion  (authoritative values from Radar.cs)
    // World units = grid index * (250 / 23) ≈ 10.8696
    // ---------------------------------------------------------------------------
    public const float GridToWorldMultiplier = 250f / 23f;

    /// <summary>
    /// World position → grid cell index (integer). Used by A* and LOS raycasts.
    /// Only the horizontal X/Y plane is used; Z (height) is ignored.
    /// </summary>
    public static Vector2i ToGrid(Vector3 worldPos)
        => new Vector2i
        {
            X = (int)(worldPos.X / GridToWorldMultiplier),
            Y = (int)(worldPos.Y / GridToWorldMultiplier),
        };

    /// <summary>
    /// Grid cell → world position (SharpDX.Vector3).
    /// Z is read from the area height map. Must be called on the main thread.
    /// </summary>
    public static Vector3 ToWorld(Vector2i gridPos)
    {
        var gridVec = new NumVec2(gridPos.X, gridPos.Y);
        float z = AreWeThereYet.Instance.GameController.IngameState.Data.GetTerrainHeightAt(gridVec);
        return new Vector3(
            gridPos.X * GridToWorldMultiplier,
            gridPos.Y * GridToWorldMultiplier,
            z);
    }


    internal static Random random = new Random();
    private static Camera Camera => AreWeThereYet.Instance.GameController.Game.IngameState.Camera;
        
    internal static float MoveTowards(float cur, float tar, float max)
    {
        if (Math.Abs(tar - cur) <= max)
            return tar;
        return cur + Math.Sign(tar - cur) * max;
    }
    
    /// <summary>
    /// Projects a world position to a screen click point, guaranteed to land inside
    /// the game window (inset by <paramref name="edgeBounds"/> pixels).
    ///
    /// A target far from the player (e.g. a distant, otherwise-valid trail waypoint)
    /// commonly projects OFF screen. The old implementation clamped X and Y
    /// independently against the window rect, which collapses any off-screen point
    /// to the same fixed corner (windowRect.TopLeft + edgeBounds) regardless of which
    /// direction the target actually was — sending the character in an arbitrary,
    /// often wrong, direction. Instead, when the projected point falls outside the
    /// safe area we pull it straight back along the line from the window CENTER
    /// (a stand-in for the player's on-screen position, since the camera follows
    /// them) toward the target, stopping at the safe-area boundary. That keeps the
    /// click's direction faithful to the real target while guaranteeing it's
    /// clickable on screen.
    /// </summary>
    internal static Vector2 WorldToValidScreenPosition(Vector3 worldPos)
    {
        var windowRect = AreWeThereYet.Instance.GameController.Window.GetWindowRectangle();
        var screenPos = Camera.WorldToScreen(worldPos);
        var result = screenPos + windowRect.Location;

        const int edgeBounds = 50;
        var safeLeft = windowRect.Left + edgeBounds;
        var safeTop = windowRect.Top + edgeBounds;
        var safeRight = windowRect.Right - edgeBounds;
        var safeBottom = windowRect.Bottom - edgeBounds;

        // Already within the safe area — click it as-is, no adjustment needed.
        if (result.X >= safeLeft && result.X <= safeRight &&
            result.Y >= safeTop && result.Y <= safeBottom)
            return result;

        var center = new Vector2(
            (safeLeft + safeRight) / 2f,
            (safeTop + safeBottom) / 2f);
        var halfWidth = (safeRight - safeLeft) / 2f;
        var halfHeight = (safeBottom - safeTop) / 2f;

        var dir = result - center;
        if (dir.X == 0f && dir.Y == 0f)
            return center; // degenerate: target projects onto our own screen position

        // Largest t in (0,1] that keeps (center + dir * t) inside the safe box —
        // i.e. where the ray from center toward the target first crosses the
        // boundary. Using the smaller of the two axis limits means we stop at
        // whichever edge (vertical or horizontal) we'd hit first.
        var tx = dir.X != 0f ? halfWidth / Math.Abs(dir.X) : float.PositiveInfinity;
        var ty = dir.Y != 0f ? halfHeight / Math.Abs(dir.Y) : float.PositiveInfinity;
        var t = Math.Min(1f, Math.Min(tx, ty));

        return center + dir * t;
    }
}
