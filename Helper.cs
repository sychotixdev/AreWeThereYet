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
    private static DateTime _lastScreenClampLog = DateTime.MinValue;
        
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
    internal static Vector2 WorldToValidScreenPosition(Vector3 worldPos) => WorldToValidScreenPosition(worldPos, out _);

    /// <summary>
    /// Same as <see cref="WorldToValidScreenPosition(Vector3)"/>, but also reports whether
    /// the raw projection needed to be pulled in from off (or right at the edge of) the
    /// safe area. A caller that needs the cursor to land ON a specific world thing (an
    /// entity's hitbox, not just "roughly the right direction") must treat wasClamped=true
    /// as "don't trust this point" - clamping guarantees on-screen, not accurate.
    /// </summary>
    internal static Vector2 WorldToValidScreenPosition(Vector3 worldPos, out bool wasClamped)
    {
        wasClamped = false;
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

        wasClamped = true;

        var center = new Vector2(
            (safeLeft + safeRight) / 2f,
            (safeTop + safeBottom) / 2f);
        var halfWidth = (safeRight - safeLeft) / 2f;
        var halfHeight = (safeBottom - safeTop) / 2f;

        var dir = result - center;
        if (dir.X == 0f && dir.Y == 0f)
        {
            if (AreWeThereYet.Instance.Settings.Debug.LogPathfinding.Value)
                AreWeThereYet.Instance.LogMessage(
                    $"[ATY-PF] ScreenClamp DEGENERATE rawScreen({screenPos.X:F0},{screenPos.Y:F0}) -> center");
            return center; // degenerate: target projects onto our own screen position
        }

        // Largest t in (0,1] that keeps (center + dir * t) inside the safe box —
        // i.e. where the ray from center toward the target first crosses the
        // boundary. Using the smaller of the two axis limits means we stop at
        // whichever edge (vertical or horizontal) we'd hit first.
        var tx = dir.X != 0f ? halfWidth / Math.Abs(dir.X) : float.PositiveInfinity;
        var ty = dir.Y != 0f ? halfHeight / Math.Abs(dir.Y) : float.PositiveInfinity;
        var t = Math.Min(1f, Math.Min(tx, ty));
        var clamped = center + dir * t;

        if (AreWeThereYet.Instance.Settings.Debug.LogPathfinding.Value)
        {
            var now = DateTime.Now;
            var interval = AreWeThereYet.Instance.Settings.Debug.PathfindingLogInterval.Value;
            if ((now - _lastScreenClampLog).TotalMilliseconds >= interval)
            {
                _lastScreenClampLog = now;
                var brg = Math.Atan2(dir.Y, dir.X) * 180.0 / Math.PI;
                AreWeThereYet.Instance.LogMessage(
                    $"[ATY-PF] ScreenClamp rawScreen({screenPos.X:F0},{screenPos.Y:F0}) " +
                    $"clamped({clamped.X:F0},{clamped.Y:F0}) dirBrg={brg:F0} t={t:F2}");
            }
        }

        return clamped;
    }

    /// <summary>
    /// Finds a point on the line from <paramref name="fromWorldPos"/> (the player) to
    /// <paramref name="toWorldPos"/> (the real target) whose screen projection is reliable -
    /// i.e. lands in the safe area on its own, without WorldToValidScreenPosition needing to
    /// pull it in from off-screen. Off-screen or edge-clamped points are still "clickable"
    /// (clamping guarantees that), but the clamped point can end up nowhere near the actual
    /// target direction-wise once the true point is far enough outside the window - and if
    /// the target is elevated well above/below the player (stairs, cliffs, ladders), even a
    /// close-by target can project off-screen due to camera angle, not distance.
    ///
    /// Mirrors LeaderFollower's MaxClickDistance-capped waypoint selection (never click a
    /// breadcrumb whose projection can't be trusted), but uses the actual screen projection
    /// as the test instead of a fixed world-distance threshold, and searches along the
    /// direct line rather than a precomputed trail.
    ///
    /// Walks the interpolation fraction down from the target (t=1) toward the player (t=0)
    /// in <paramref name="steps"/> increments, returning the first reliable point found. If
    /// none are reliable (player's own position isn't clickable, or off-window), falls back
    /// to the target's clamped projection so callers always get an on-screen result.
    /// </summary>
    internal static (Vector3 WorldPos, Vector2 ScreenPos) GetReliableClickPoint(Vector3 fromWorldPos, Vector3 toWorldPos, int steps = 8)
    {
        for (var i = 0; i <= steps; i++)
        {
            var t = 1f - i / (float)steps;
            var candidate = Vector3.Lerp(fromWorldPos, toWorldPos, t);
            var candidateScreen = WorldToValidScreenPosition(candidate, out var candidateClamped);
            if (!candidateClamped)
                return (candidate, candidateScreen);
        }

        // Nothing along the line was clean - fall back to the target's clamped projection
        // so the caller still gets a valid on-screen point (just an untrustworthy one).
        return (toWorldPos, WorldToValidScreenPosition(toWorldPos));
    }
}
