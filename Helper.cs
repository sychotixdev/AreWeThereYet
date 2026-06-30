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
    
    internal static Vector2 WorldToValidScreenPosition(Vector3 worldPos)
    {
        var windowRect = AreWeThereYet.Instance.GameController.Window.GetWindowRectangle();
        var screenPos = Camera.WorldToScreen(worldPos);
        var result = screenPos + windowRect.Location;

        var edgeBounds = 50;
        if (!windowRect.Intersects(new RectangleF(result.X, result.Y, edgeBounds, edgeBounds)))
        {
            if (result.X < windowRect.TopLeft.X) result.X = windowRect.TopLeft.X + edgeBounds;
            if (result.Y < windowRect.TopLeft.Y) result.Y = windowRect.TopLeft.Y + edgeBounds;
            if (result.X > windowRect.BottomRight.X) result.X = windowRect.BottomRight.X - edgeBounds;
            if (result.Y > windowRect.BottomRight.Y) result.Y = windowRect.BottomRight.Y - edgeBounds;
        }
        return result;
    }
}
