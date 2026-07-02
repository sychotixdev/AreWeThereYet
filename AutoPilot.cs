using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using SharpDX;
using AreWeThereYet.Utils;
using AreWeThereYet.PathFinder;
using System.Windows.Forms;
using System.Threading;

namespace AreWeThereYet;

public class AutoPilot
{
    private Coroutine autoPilotCoroutine;
    private readonly Random random = new Random();
        
    private Vector3 lastTargetPosition;
    private Vector3 lastPlayerPosition;
    private Entity followTarget;

    private List<TaskNode> tasks = new List<TaskNode>();

    private LineOfSight LineOfSight => AreWeThereYet.Instance.lineOfSight;

    private string _lastKnownLeaderZone = "";
    private DateTime _leaderZoneChangeTime = DateTime.MinValue;
    
    private bool _isTransitioning = false;
    private DateTime _transitioningStartTime = DateTime.MinValue;
    private static readonly TimeSpan TransitioningStuckTimeout = TimeSpan.FromSeconds(8);

    // Movement-execution diagnostics: confirms whether input we send is actually
    // landing in-game (player position changing) or whether we're clicking/holding
    // the move key into a spot the character can't actually reach (blocked geometry,
    // an off-screen click target, etc.) as opposed to a pathing/logic issue upstream.
    private Vector3 _lastMoveDiagPos = Vector3.Zero;
    private DateTime _lastMoveDiagLog = DateTime.MinValue;

    // Tracks liveness across ticks so we can detect death → respawn (e.g. release to
    // checkpoint), which does not fire an area change and would otherwise leave a
    // stale breadcrumb trail in place.
    private bool _wasAlive = true;

    // Death-in-zone tracking for the "wait at entrance" failsafe (leveling zones only).
    // Dying repeatedly to the same zone usually means we're getting killed trying to
    // path through content the leader has already cleared, so instead of feeding the
    // character back into the same fight we park it and wait for the leader to either
    // move on (normal zone-transition/portal logic takes over) or come back on screen.
    private int _deathCountThisZone = 0;
    private string _deathTrackedZone = "";
    private bool _waitingAtZoneEntrance = false;

    // The "Resurrect at Checkpoint" button can report IsVisible == true for a frame or two
    // before the panel has actually finished laying out (rect still zeroed/mid-animation),
    // which sends the click to a stale/off-screen position. Require the button to have been
    // continuously visible for this long before we trust its rect enough to click it.
    private static readonly TimeSpan ResurrectButtonVisibleDelay = TimeSpan.FromMilliseconds(300);
    private DateTime? _resurrectButtonVisibleSince = null;

    private void ResetPathing()
    {
        tasks = new List<TaskNode>();
        followTarget = null;
        lastTargetPosition = Vector3.Zero;
        lastPlayerPosition = Vector3.Zero;
        // Keep the breadcrumb trail/A* search in lockstep with task reset.
        AreWeThereYet.Instance.leaderFollower.Reset();
    }


    public void AreaChange()
    {
        // If we triggered this area change ourselves...
        if (_isTransitioning)
        {
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                AreWeThereYet.Instance.LogMessage($"We are transitioning, starting pause coroutine.");
            }

            // ...start a new coroutine to handle the post-transition grace period.
            var gracePeriodCoroutine = new Coroutine(PostTransitionGracePeriod(), AreWeThereYet.Instance, "PostTransitionGracePeriod");
            Core.ParallelRunner.Run(gracePeriodCoroutine);
        }
        ResetPathing();

        // New zone: the per-zone death counter and any "wait at entrance" state from
        // the previous zone no longer apply. Note that releasing at a checkpoint does
        // NOT fire AreaChange() (see _wasAlive tracking below), so this only clears on
        // an actual zone transition - which is exactly when we want a fresh start.
        _deathCountThisZone = 0;
        _deathTrackedZone = "";
        _waitingAtZoneEntrance = false;
    }

    /// <summary>
    /// Absolute-screen click position for the "Resurrect at Checkpoint" button on the
    /// death panel. Same window-offset pattern as GetLabelClickPosition/GetTpButton -
    /// GetClientRect() is window-relative, Mouse.SetCursorPos needs an absolute point.
    /// </summary>
    private Vector2? GetResurrectAtCheckpointPosition()
    {
        try
        {
            var resurrectButton = AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ResurrectPanel?.ResurrectAtCheckpoint;
            if (resurrectButton == null || !resurrectButton.IsVisible)
                return null;

            var windowOffset = AreWeThereYet.Instance.GameController.Window.GetWindowRectangle().TopLeft;
            return resurrectButton.GetClientRect().Center + windowOffset;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Debounced visibility check for the "Resurrect at Checkpoint" button. IsVisible can
    /// flip true for a frame or two before the death panel has finished laying out, which
    /// would otherwise hand GetResurrectAtCheckpointPosition a stale/zeroed rect and send
    /// the click off screen. Tracks how long the button has been continuously visible via
    /// _resurrectButtonVisibleSince and only reports "ready" once that holds for at least
    /// ResurrectButtonVisibleDelay. Must be polled every tick (including while dead) so the
    /// visible-since timer resets promptly when the button disappears.
    /// </summary>
    private bool IsResurrectButtonReadyToClick()
    {
        bool visible;
        try
        {
            var resurrectButton = AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ResurrectPanel?.ResurrectAtCheckpoint;
            visible = resurrectButton != null && resurrectButton.IsVisible;
        }
        catch
        {
            visible = false;
        }

        if (!visible)
        {
            _resurrectButtonVisibleSince = null;
            return false;
        }

        _resurrectButtonVisibleSince ??= DateTime.Now;
        return DateTime.Now - _resurrectButtonVisibleSince.Value >= ResurrectButtonVisibleDelay;
    }

    public void StartCoroutine()
    {
        autoPilotCoroutine = new Coroutine(AutoPilotLogic(), AreWeThereYet.Instance, "AutoPilot");
        Core.ParallelRunner.Run(autoPilotCoroutine);
    }

    private PartyElementWindow GetLeaderPartyElement()
    {
        try
        {
            foreach (var partyElementWindow in PartyElements.GetPlayerInfoElementList())
            {
                if (string.Equals(partyElementWindow?.PlayerName?.ToLower(), AreWeThereYet.Instance.Settings.AutoPilot.LeaderName.Value.ToLower(), StringComparison.CurrentCultureIgnoreCase))
                {
                    return partyElementWindow;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
    
    /// <summary>
    /// Strips the trailing "(N)" zone-level suffix the party UI appends to a leader's
    /// ZoneName (e.g. "The Upper Prison (9)" - the "9" is the zone's monster level, not
    /// an instance id). GameController.Area.CurrentArea.DisplayName never carries this
    /// suffix, so a raw string comparison between the two ALWAYS mismatches even when
    /// we're standing in the exact same zone as the leader - it only goes unnoticed
    /// because that comparison is normally skipped whenever the leader's entity is still
    /// visible (followTarget != null). It surfaces the moment the entity disappears
    /// (e.g. an intra-zone transition to a sub-area like "The Warden's Quarters" that
    /// doesn't change the zone's display name at all), incorrectly telling us the leader
    /// zoned to a different area.
    /// </summary>
    private static string StripZoneLevelSuffix(string zoneName)
    {
        if (string.IsNullOrEmpty(zoneName)) return zoneName ?? "";
        var match = System.Text.RegularExpressions.Regex.Match(zoneName.Trim(), @"^(.*?)\s*\(\d+\)$");
        return (match.Success ? match.Groups[1].Value : zoneName.Trim()).ToLowerInvariant();
    }

    private bool IsLeaderZoneInfoReliable(PartyElementWindow leaderPartyElement)
    {
        try
        {
            // Check if zone name looks valid (not empty, not obviously stale)
            var zoneName = leaderPartyElement.ZoneName;
            var currentZone = AreWeThereYet.Instance.GameController?.Area.CurrentArea.DisplayName;

            // Invalid if empty or same as current zone when leader should be elsewhere
            if (string.IsNullOrEmpty(zoneName) || StripZoneLevelSuffix(zoneName).Equals(StripZoneLevelSuffix(currentZone)))
                return false;
                
            // Check if zone name changed very recently (might still be updating)
            var timeSinceChange = DateTime.Now - _leaderZoneChangeTime;
            if (timeSinceChange < TimeSpan.FromMilliseconds(AreWeThereYet.Instance.Settings.AutoPilot.ZoneUpdateBuffer.Value))
                return false;
                
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the AreaTransition entity to use, instead of relying on ground labels
    /// (which can be missing, stale, or mismatched with the actual transition entity).
    /// The entity's own RenderName is checked against the leader's zone name, and its
    /// Targetable component (isTargeted) is what tells us we're actually aiming at it
    /// once we're close enough to click.
    /// </summary>
    private Entity GetBestAreaTransitionEntity(PartyElementWindow leaderPartyElement)
    {
        try
        {
            var isHideout = (bool)AreWeThereYet.Instance?.GameController?.Area?.CurrentArea?.IsHideout;
            var realLevel = AreWeThereYet.Instance.GameController?.Area?.CurrentArea?.RealLevel ?? 0;

            var allTransitions = AreWeThereYet.Instance.GameController?.EntityListWrapper?.ValidEntitiesByType[EntityType.AreaTransition]
                ?.Where(x => x != null && x.IsValid)
                .ToList() ?? new List<Entity>();

            // Enhanced logic: differentiate between leveling zones and endgame content
            if (isHideout || realLevel >= 68)
            {
                // ENDGAME/HIDEOUT: Any transition is fine (maps, hideout transitions)
                var transitions = allTransitions
                    .OrderBy(x => Vector3.Distance(lastTargetPosition, x.Pos))
                    .ToList();

                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"Endgame/Hideout area transition search: Found {transitions.Count} transitions");
                }

                return isHideout && transitions.Count > 0
                    ? transitions[random.Next(transitions.Count)] // Random transition in hideout
                    : transitions.FirstOrDefault(); // Closest transition in endgame
            }
            else
            {
                // LEVELING ZONES: Must find the transition that leads to leader's specific zone.
                // Match against the zone name with its "(N)" zone-level suffix stripped
                // (see StripZoneLevelSuffix) - RenderName never includes that suffix, so
                // matching the raw ZoneName (e.g. "The Upper Prison (9)") would never find
                // the entity for "The Upper Prison".
                var leaderZone = StripZoneLevelSuffix(leaderPartyElement.ZoneName ?? "");
                var transitions = allTransitions
                    .Where(x => !string.IsNullOrEmpty(x.RenderName) &&
                            x.RenderName.ToLower().Contains(leaderZone)) // IMPORTANT KEY IMPROVEMENT: Check entity RenderName
                    .OrderBy(x => Vector3.Distance(lastTargetPosition, x.Pos))
                    .ToList();

                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"Leveling zone transition search:");
                    AreWeThereYet.Instance.LogMessage($"  - Leader zone: '{leaderZone}'");
                    AreWeThereYet.Instance.LogMessage($"  - All transitions: {allTransitions.Count}");
                    AreWeThereYet.Instance.LogMessage($"  - Matching transitions: {transitions.Count}");

                    foreach (var t in allTransitions)
                    {
                        var matches = t.RenderName?.ToLower().Contains(leaderZone) == true;
                        AreWeThereYet.Instance.LogMessage($"    Transition: '{t.RenderName}' -> {(matches ? "MATCH" : "No match")}");
                    }
                }

                // EXPLICIT NULL CHECK: If no matching transition found in leveling zone, return null for teleport fallback
                if (transitions.Count == 0)
                {
                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        AreWeThereYet.Instance.LogMessage($"No matching area transition found for leader zone '{leaderZone}' - will use teleport button fallback");
                    }
                    return null; // Force teleport button usage
                }

                return transitions.FirstOrDefault(); // Return closest matching transition
            }
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"GetBestAreaTransitionEntity failed: {ex.Message}");
            return null; // Exception fallback
        }
    }

    /// <summary>
    /// Finds the closest AreaTransition entity to a given position, with no zone-name
    /// filtering. Used for the "same zone, leader missing" case: an intra-zone area
    /// transition can move the leader to a different physical area/instance without
    /// changing the reported zone name, so the name-matching in
    /// GetBestAreaTransitionEntity can't identify the right one there. Once we've
    /// finished walking the leader's last breadcrumb trail, the nearest transition to
    /// where that trail ended is assumed to be the one they took.
    /// </summary>
    private Entity GetClosestAreaTransitionEntity(Vector3 fromPos)
    {
        try
        {
            var allTransitions = AreWeThereYet.Instance.GameController?.EntityListWrapper?.ValidEntitiesByType[EntityType.AreaTransition]
                ?.Where(x => x != null && x.IsValid)
                .ToList() ?? new List<Entity>();

            return allTransitions
                .OrderBy(x => Vector3.Distance(fromPos, x.Pos))
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"GetClosestAreaTransitionEntity failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Probes whether <paramref name="clickedWorldPos"/> is still walkably connected to
    /// our current position. This is the success signal for an intra-zone transition
    /// (a sub-area whose display name never changes): those don't reload the terrain
    /// buffer (confirmed same Area, same array before/after) and never fire
    /// AreaChange(), so IsLoading and tasks.Contains(currentTask) never flip - the
    /// normal STEP 3 success check has nothing to observe. But the sub-room we land in
    /// is a disconnected pocket of that same terrain (only joined by the scripted
    /// transition, not by walkable ground), so once we've actually gone through, A* can
    /// no longer path from here back to the point we clicked. Returns false (treat as
    /// "still connected" - keep waiting/retrying) whenever terrain isn't available or
    /// the search is inconclusive, so we never falsely claim success.
    /// </summary>
    private bool IsDisconnectedFromClickPoint(Vector3 clickedWorldPos)
    {
        try
        {
            var lineOfSight = AreWeThereYet.Instance.lineOfSight;
            lineOfSight.EnsureTerrainData();
            var terrain = lineOfSight.GetTerrainData();
            if (terrain == null) return false;

            var startGrid = Helper.ToGrid(AreWeThereYet.Instance.playerPosition);
            var goalGrid = Helper.ToGrid(clickedWorldPos);
            Func<int, bool> isPathable = v => v is 1 or 5;

            var result = AStar.FindPath(
                terrain, startGrid, goalGrid, isPathable,
                AreWeThereYet.Instance.Settings.AutoPilot.Pathfinding.NodeBudget.Value,
                CancellationToken.None);

            return result.Outcome == PathOutcome.Unreachable;
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"IsDisconnectedFromClickPoint failed: {ex.Message}");
            return false;
        }
    }

    private LabelOnGround GetMercenaryOptInButton()
    {
        try
        {
            // Better null checking to prevent the exception
            if (AreWeThereYet.Instance?.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels == null)
                return null;

            var mercenaryLabels = AreWeThereYet.Instance.GameController.Game.IngameState.IngameUi.ItemsOnGroundLabels
                .Where(x => x != null && x.IsVisible && x.Label != null && x.Label.IsValid && 
                        x.Label.IsVisible && x.ItemOnGround != null &&
                        !string.IsNullOrEmpty(x.ItemOnGround.Metadata) &&
                        x.ItemOnGround.Metadata.ToLower().Contains("mercenary") &&
                        x.Label.Children?.Count > 2 && x.Label.Children[2] != null &&
                        x.Label.Children[2].IsVisible)
                .OrderBy(x => Vector3.Distance(AreWeThereYet.Instance.playerPosition, x.ItemOnGround.Pos))
                .ToList();

            return mercenaryLabels?.FirstOrDefault();
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"GetMercenaryOptInButton failed: {ex.Message}");
            return null;
        }
    }

    private Vector2 GetMercenaryOptInButtonPosition(LabelOnGround mercenaryLabel)
    {
        try
        {
            if (mercenaryLabel?.Label?.Children?.Count > 2 && mercenaryLabel.Label.Children[2] != null)
            {
                var windowOffset = AreWeThereYet.Instance.GameController.Window.GetWindowRectangle().TopLeft;
                var optInButton = mercenaryLabel.Label.Children[2];
                var buttonCenter = optInButton.GetClientRectCache.Center;
                var finalPos = new Vector2(buttonCenter.X + windowOffset.X, buttonCenter.Y + windowOffset.Y);

                return finalPos;
            }
            return Vector2.Zero;
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"GetMercenaryOptInButtonPosition failed: {ex.Message}");
            return Vector2.Zero;
        }
    }
    
    /// <summary>
    /// Absolute-screen click position for a ground label. Element.GetClientRect() is in
    /// WINDOW-relative coordinates; Mouse.SetCursorPos is an absolute Win32 call, so we
    /// must add the window's top-left (the same offset GetTpButton and
    /// Helper.WorldToValidScreenPosition apply). Without this the cursor lands off by the
    /// window origin - outside the game window in windowed mode - and the click misses.
    /// The result is clamped to the window so we never click outside it.
    /// </summary>
    private Vector2 GetLabelClickPosition(LabelOnGround label)
    {
        var windowRect = AreWeThereYet.Instance.GameController.Window.GetWindowRectangle();
        var center = label.Label.GetClientRect().Center;
        var pos = new Vector2(center.X + windowRect.TopLeft.X, center.Y + windowRect.TopLeft.Y);

        // Clamp inside the window (small margin) so an odd label rect can never send the
        // cursor off-window.
        const float margin = 5f;
        pos.X = Math.Clamp(pos.X, windowRect.Left + margin, windowRect.Right - margin);
        pos.Y = Math.Clamp(pos.Y, windowRect.Top + margin, windowRect.Bottom - margin);
        return pos;
    }

    private Vector2 GetTpButton(PartyElementWindow leaderPartyElement)
    {
        try
        {
            var windowOffset = AreWeThereYet.Instance.GameController.Window.GetWindowRectangle().TopLeft;
            var elemCenter = (Vector2)leaderPartyElement?.TpButton?.GetClientRectCache.Center;
            var finalPos = new Vector2(elemCenter.X + windowOffset.X, elemCenter.Y + windowOffset.Y);

            return finalPos;
        }
        catch
        {
            return Vector2.Zero;
        }
    }

    private Element GetTpConfirmation()
    {
        try
        {
            var ui = AreWeThereYet.Instance.GameController?.IngameState?.IngameUi?.PopUpWindow;

            if (ui.GetChildFromIndices(0,0,0)?.Text.Equals("Are you sure you want to teleport to this player's location?") == true)
                return ui.TwoButtonWindowOk;

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finds a click point for whatever confirmation popup is currently open, if any - the
    /// known "are you sure you want to teleport" Yes/No dialog, or a generic single-button
    /// (e.g. "OK") area-entry confirmation. While either is open the game won't register
    /// mouseover/targeting on world entities, so this must be checked and dismissed before
    /// transition-targeting logic can make progress.
    ///
    /// Returns the click position (not the Element) computed entirely inside the try/catch:
    /// the popup element can go stale between "we found it" and "we read its rect" (it can
    /// close mid-tick, e.g. from a previous click landing late), and GetClientRect() on a
    /// stale/freed element is an unguarded memory read that can throw and kill the whole
    /// AutoPilot coroutine - which is worse than missing one dismiss attempt.
    /// </summary>
    private Vector2? GetOpenPopupClickPosition()
    {
        try
        {
            // GetClientRect() is WINDOW-relative; Mouse.SetCursorPos is an absolute Win32
            // call, so the window's top-left must be added (same as GetTpButton /
            // GetLabelClickPosition) or clicks land off-target in windowed mode.
            var windowOffset = AreWeThereYet.Instance.GameController.Window.GetWindowRectangle().TopLeft;

            var tpConfirm = GetTpConfirmation();
            if (tpConfirm != null)
                return tpConfirm.GetClientRect().Center + windowOffset;

            var ui = AreWeThereYet.Instance.GameController?.IngameState?.IngameUi?.PopUpWindow;
            if (ui == null || !ui.IsVisible)
                return null;

            var okButton = FindButtonByText(ui, "OK");
            return okButton != null ? okButton.GetClientRect().Center + windowOffset : (Vector2?)null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Recursively searches an element tree for a visible descendant whose text matches
    /// (case-insensitive). Used to find popup buttons without depending on a fixed sibling
    /// index, since different popup types (Yes/No vs. single OK) lay their buttons out
    /// differently.
    /// </summary>
    private static Element FindButtonByText(Element root, string text)
    {
        if (root?.Children == null)
            return null;

        foreach (var child in root.Children)
        {
            if (child == null)
                continue;

            if (child.IsVisible && !string.IsNullOrEmpty(child.Text) &&
                child.Text.Trim().Equals(text, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }

            var found = FindButtonByText(child, text);
            if (found != null)
                return found;
        }

        return null;
    }

    /// <summary>
    /// Fallback used when we can't get a transition label/entity to cooperate: click the
    /// leader's party teleport icon and accept the resulting "are you sure" confirmation.
    /// Shared by the "out of click attempts" path and the "entity never became targeted" path.
    /// </summary>
    private IEnumerator FallbackToPartyTeleport()
    {
        var fallbackLeader = GetLeaderPartyElement();
        if (fallbackLeader == null)
            yield break;

        // Flag the transition again: clicking the party TP button also triggers a
        // zone load / AreaChange().
        _isTransitioning = true;
        _transitioningStartTime = DateTime.Now;

        // A confirmation may already be up.
        var prePos = GetOpenPopupClickPosition();
        if (prePos != null)
        {
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                AreWeThereYet.Instance.LogMessage("FallbackToPartyTeleport: dismissing pre-existing popup");

            yield return Mouse.SetCursorPosHuman(prePos.Value);
            yield return new WaitTime(200);
            yield return Mouse.LeftClick();
            yield return new WaitTime(1000);
        }

        var fallbackTpButton = GetTpButton(fallbackLeader);
        if (!fallbackTpButton.Equals(Vector2.Zero))
        {
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                AreWeThereYet.Instance.LogMessage("FallbackToPartyTeleport: clicking party TP button");

            yield return Mouse.SetCursorPosHuman(fallbackTpButton, false);
            yield return new WaitTime(200);
            yield return Mouse.LeftClick();

            // Accept the "travel to this player?" confirmation. Poll for a bit rather
            // than checking once - the dialog can take a moment to render, and a missed
            // check here leaves it open, silently blocking world-entity targeting for
            // everything afterward.
            var confirmWaited = 0;
            const int confirmTimeoutMs = 2000;
            while (confirmWaited < confirmTimeoutMs)
            {
                yield return new WaitTime(150);
                confirmWaited += 150;

                var postPos = GetOpenPopupClickPosition();
                if (postPos == null)
                    continue;

                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    AreWeThereYet.Instance.LogMessage("FallbackToPartyTeleport: dismissing post-click confirmation");

                yield return Mouse.SetCursorPosHuman(postPos.Value);
                yield return new WaitTime(200);
                yield return Mouse.LeftClick();
                yield return new WaitTime(500);
                break;
            }
        }
        else
        {
            // No TP button available - let the flag self-heal so detection resumes
            // rather than staying wedged.
            _isTransitioning = false;
        }
    }

    private IEnumerator MouseoverItem(Entity item)
    {
        var uiLoot = AreWeThereYet.Instance.GameController.IngameState.IngameUi.ItemsOnGroundLabels.FirstOrDefault(I => I.IsVisible && I.ItemOnGround.Id == item.Id);
        if (uiLoot == null) yield return null;
        if (uiLoot?.Label != null)
        {
            // Window offset applied (see GetLabelClickPosition) so the hover lands on the
            // label rather than off-window in windowed mode.
            var clickPos = GetLabelClickPosition(uiLoot);
            Mouse.SetCursorPos(new Vector2(
                clickPos.X + random.Next(-15, 15),
                clickPos.Y + random.Next(-10, 10)));
        }
	        
        yield return new WaitTime(30 + random.Next(AreWeThereYet.Instance.Settings.AutoPilot.InputFrequency));
    }
    
        private IEnumerator PostTransitionGracePeriod()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        const int TIMEOUT_MS = 10000; // 10-second timeout.

        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
        {
            AreWeThereYet.Instance.LogMessage("[GracePeriod] Entered post-transition grace period. Waiting for leader entity to sync...");
        }

        while (stopwatch.ElapsedMilliseconds < TIMEOUT_MS)
        {
            var leaderPartyElement = GetLeaderPartyElement();
            var followTarget = GetFollowingTarget();
            var currentAreaName = AreWeThereYet.Instance.GameController.Area.CurrentArea.DisplayName;

            // Success Condition: The leader's entity is found and they are in the same zone as us.
            // Compare with the "(N)" zone-level suffix stripped (see StripZoneLevelSuffix) -
            // leaderPartyElement.ZoneName always carries it (e.g. "The Upper Prison (9)") while
            // CurrentArea.DisplayName never does, so a raw comparison here never matched and
            // this loop used to burn the full 10-second TIMEOUT_MS on every single transition
            // before falling through to the timeout path below.
            if (leaderPartyElement != null && followTarget != null &&
                StripZoneLevelSuffix(leaderPartyElement.ZoneName).Equals(StripZoneLevelSuffix(currentAreaName)))
            {
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"[GracePeriod] SUCCESS: Leader entity found and synced in '{currentAreaName}'. Resuming normal logic.");
                }
                stopwatch.Stop();
                _isTransitioning = false; // Unlock the main logic.
                yield break;              // Exit the coroutine.
            }

            yield return new WaitTime(100);
        }

        // If we reach here, the loop timed out. Now we must determine why.
        var finalLeaderPartyElement = GetLeaderPartyElement();
        var finalCurrentAreaName = AreWeThereYet.Instance.GameController.Area.CurrentArea.DisplayName;

        // --- THE NEW FAILSAFE LOGIC ---
        // Check for the "Same Zone, Different Instance" problem. Same stripped comparison
        // as the success condition above - otherwise the always-present zone-level suffix
        // would make this branch fire as a false "deadlock" on every timeout.
        if (finalLeaderPartyElement != null &&
            StripZoneLevelSuffix(finalLeaderPartyElement.ZoneName).Equals(StripZoneLevelSuffix(finalCurrentAreaName)))
        {
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                AreWeThereYet.Instance.LogMessage($"[GracePeriod] DEADLOCK DETECTED: In same zone ('{finalCurrentAreaName}') but different instance. Forcing UI teleport to sync instances.", 10, Color.Red);
            }

            // Check for and click the "Are you sure?" confirmation box if it's open.
            var tpConfirmation = GetTpConfirmation();
            if (tpConfirmation != null)
            {
                // GetClientRect() is window-relative - add the window's top-left, same as
                // GetTpButton, so the click lands correctly in windowed mode.
                var confirmWindowOffset = AreWeThereYet.Instance.GameController.Window.GetWindowRectangle().TopLeft;
                yield return Mouse.SetCursorPosHuman(tpConfirmation.GetClientRect().Center + confirmWindowOffset);
                yield return new WaitTime(200);
                yield return Mouse.LeftClick();
                yield return new WaitTime(1000);
            }

            // Click the teleport button on the party UI to force an instance sync.
            var tpButton = GetTpButton(finalLeaderPartyElement);
            if (!tpButton.Equals(Vector2.Zero))
            {
                yield return Mouse.SetCursorPosHuman(tpButton, false);
                yield return new WaitTime(200);
                yield return Mouse.LeftClick();
                yield return new WaitTime(200);
            }
        }
        else
        {
            // The timeout was for a different reason (e.g., leader zoned again). Let the main logic handle it.
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                AreWeThereYet.Instance.LogMessage("[GracePeriod] TIMEOUT: Leader entity did not sync. Resuming logic with fallback.", 5, Color.Orange);
            }
        }

        _isTransitioning = false; // Unlock the main logic in all timeout cases.
    }
    
    private IEnumerator AutoPilotLogic()
    {
        while (true)
        {
            // =================================================================
            // SECTION 1: INITIAL CHECKS & UI CLEANUP
            // =================================================================

            // Death detection: releasing to checkpoint does NOT fire an area change,
            // so reset pathing the moment we die. On respawn the empty trail forces a
            // fresh A* re-acquisition (the backup) from wherever we come back.
            var deathCheckPlayer = AreWeThereYet.Instance.localPlayer;
            bool isAliveNow = deathCheckPlayer != null && deathCheckPlayer.IsAlive;
            if (_wasAlive && !isAliveNow)
            {
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    AreWeThereYet.Instance.LogMessage("Death detected - resetting pathfinding to fall back to A*.");
                ResetPathing();

                // Count deaths per leveling zone (maps/hideout are excluded - same
                // isHideout/RealLevel>=68 split used by GetBestAreaTransitionEntity to
                // distinguish leveling content from endgame). Too many deaths in the
                // same zone usually means we're dying to something the leader already
                // cleared, so past the configured threshold we stop chasing and just
                // wait (see _waitingAtZoneEntrance handling below).
                var deathZoneIsHideout = (bool)(AreWeThereYet.Instance.GameController?.Area?.CurrentArea?.IsHideout ?? false);
                var deathZoneRealLevel = AreWeThereYet.Instance.GameController?.Area?.CurrentArea?.RealLevel ?? 0;
                if (!deathZoneIsHideout && deathZoneRealLevel < 68)
                {
                    var currentZoneName = AreWeThereYet.Instance.GameController?.Area?.CurrentArea?.DisplayName ?? "";
                    if (!string.Equals(_deathTrackedZone, currentZoneName, StringComparison.OrdinalIgnoreCase))
                    {
                        _deathTrackedZone = currentZoneName;
                        _deathCountThisZone = 0;
                    }
                    _deathCountThisZone++;

                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                        AreWeThereYet.Instance.LogMessage($"Death #{_deathCountThisZone} in leveling zone '{currentZoneName}'.");

                    if (AreWeThereYet.Instance.Settings.AutoPilot.WaitAtEntranceAfterDeaths.Value &&
                        _deathCountThisZone >= AreWeThereYet.Instance.Settings.AutoPilot.MaxDeathsBeforeWaiting.Value)
                    {
                        _waitingAtZoneEntrance = true;

                        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            AreWeThereYet.Instance.LogMessage($"Death threshold ({AreWeThereYet.Instance.Settings.AutoPilot.MaxDeathsBeforeWaiting.Value}) reached in '{currentZoneName}' - waiting at entrance instead of following.", 5, Color.Orange);
                    }
                }
            }
            _wasAlive = isAliveNow;

            // FAILSAFE: _isTransitioning is set optimistically right before we click a
            // portal/transition, and is normally cleared by PostTransitionGracePeriod once
            // the engine's AreaChange() callback confirms the zone actually changed. If the
            // click misses (player out of range, stale label rect, etc.) AreaChange() never
            // fires, the grace period never starts, and this flag is stuck true forever -
            // permanently disabling the zone/portal detection block below (and surviving an
            // AutoPilot coroutine restart, since this is instance state, not coroutine state).
            // Self-heal after a timeout so we don't get permanently wedged.
            if (_isTransitioning && DateTime.Now - _transitioningStartTime > TransitioningStuckTimeout)
            {
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"_isTransitioning stuck true for over {TransitioningStuckTimeout.TotalSeconds:F0}s with no AreaChange - forcing reset.");
                }
                _isTransitioning = false;
            }

            if (!AreWeThereYet.Instance.Settings.Enable.Value || !AreWeThereYet.Instance.Settings.AutoPilot.Enabled.Value || AreWeThereYet.Instance.localPlayer == null ||
                !AreWeThereYet.Instance.GameController.IsForeGroundCache || MenuWindow.IsOpened || AreWeThereYet.Instance.GameController.IsLoading || !AreWeThereYet.Instance.GameController.InGame)
            {
                yield return new WaitTime(100);
                continue;
            }

            // Dead: nothing to path while waiting to respawn. Click "Resurrect at
            // Checkpoint" the moment the panel offers it rather than releasing
            // manually - the panel can take a moment to appear, so this just keeps
            // polling every tick until it does.
            if (!isAliveNow)
            {
                // Only trust the button's rect once it's been visible for a sustained
                // period - clicking the moment IsVisible flips true risks a stale/zeroed
                // rect from the panel still animating in, which sends the click off screen.
                if (IsResurrectButtonReadyToClick())
                {
                    var resurrectPos = GetResurrectAtCheckpointPosition();
                    if (resurrectPos != null)
                    {
                        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            AreWeThereYet.Instance.LogMessage("Clicking Resurrect at Checkpoint.");

                        yield return Mouse.SetCursorPosHuman(resurrectPos.Value);
                        yield return new WaitTime(200);
                        yield return Mouse.LeftClick();
                        yield return new WaitTime(500);
                    }
                }

                yield return new WaitTime(100);
                continue;
            }

            // TODO: custom settings if user want automatically close all ui shits.
            // var ingameUi = AreWeThereYet.Instance.GameController.IngameState.IngameUi;

            // if (new List<Element> { ingameUi.TreePanel, ingameUi.AtlasTreePanel, ingameUi.OpenLeftPanel, ingameUi.OpenRightPanel, ingameUi.InventoryPanel, ingameUi.SettingsPanel, ingameUi.ChatPanel.Children.FirstOrDefault() }.Any(panel => panel != null && panel.IsVisible))
            // {
            //     Keyboard.KeyPress(Keys.Escape);
            //     yield return new WaitTime(150);
            //     continue;
            // }

            followTarget = GetFollowingTarget();
            var leaderPartyElement = GetLeaderPartyElement();

            // Compare zone names with the "(N)" zone-level suffix stripped off (see
            // StripZoneLevelSuffix) - the party UI's ZoneName always carries it, our own
            // CurrentArea.DisplayName never does, so a raw comparison would always treat
            // us as being in a different zone from the leader even when we're not.
            var currentZoneDisplayName = AreWeThereYet.Instance.GameController?.Area.CurrentArea.DisplayName;
            var leaderInSameZone = leaderPartyElement != null &&
                StripZoneLevelSuffix(leaderPartyElement.ZoneName).Equals(StripZoneLevelSuffix(currentZoneDisplayName));

            if (followTarget == null && !_isTransitioning && leaderPartyElement != null && !leaderInSameZone)
            {
                // Track zone changes for buffer timing
                if (!_lastKnownLeaderZone.Equals(leaderPartyElement.ZoneName))
                {
                    // Leader zone changed - start buffer timer
                    _lastKnownLeaderZone = leaderPartyElement.ZoneName;
                    _leaderZoneChangeTime = DateTime.Now;

                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        AreWeThereYet.Instance.LogMessage($"Leader zone change detected: '{_lastKnownLeaderZone}' - starting reliability check");
                    }
                }

                // Use smarter zone detection to check if leader zone info is reliable
                if (IsLeaderZoneInfoReliable(leaderPartyElement))
                {
                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        AreWeThereYet.Instance.LogMessage($"Leader zone info reliable: '{leaderPartyElement.ZoneName}' - proceeding with portal/teleport logic");
                    }

                    var transitionEntity = GetBestAreaTransitionEntity(leaderPartyElement);
                    if (transitionEntity != null)
                    {
                        // Only queue one transition at a time. Without this guard we re-add a
                        // Transition task every loop iteration (this branch runs whenever the
                        // leader is in another zone and we aren't mid-transition), piling up
                        // duplicates - especially now that a failed click clears _isTransitioning
                        // so we can retry.
                        if (!tasks.Exists(t => t.Type == TaskNodeType.Transition))
                        {
                            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            {
                                AreWeThereYet.Instance.LogMessage($"Found reliable area transition: {transitionEntity.RenderName}");
                            }
                            // Drop any trail-walk movement tasks: the Transition task now
                            // self-navigates to the transition, so it should be the only task.
                            tasks.RemoveAll(t => t.Type == TaskNodeType.Movement);
                            tasks.Add(new TaskNode(transitionEntity, AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value, TaskNodeType.Transition));
                        }
                    }
                    else
                    {
                        // No transition label is visible yet. Before falling back to the
                        // party teleport, finish walking the leader's breadcrumb trail toward
                        // where they vanished (the portal) - just like following the leader.
                        // The label becomes visible as we close in, and the Transition task
                        // above takes over. Only teleport once the trail is exhausted (we've
                        // arrived but still see no label) or pathfinding is disabled.
                        var pfEnabled = AreWeThereYet.Instance.Settings.AutoPilot.Pathfinding.Enabled.Value;
                        var reachedBounds = AreWeThereYet.Instance.Settings.AutoPilot.Pathfinding.ReachedBounds.Value;

                        var walkedTrail = false;
                        if (pfEnabled && AreWeThereYet.Instance.leaderFollower.HasTrail &&
                            AreWeThereYet.Instance.leaderFollower.TrailEnd is Vector3 trailEnd)
                        {
                            var nav = AreWeThereYet.Instance.leaderFollower.NavigateTo(
                                AreWeThereYet.Instance.playerPosition, trailEnd,
                                allowTrailFollow: true,
                                arriveWithin: AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value);

                            if (nav.Type != FollowResultType.Idle)
                            {
                                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                                {
                                    AreWeThereYet.Instance.LogMessage("No portal label yet - walking breadcrumb trail toward the transition");
                                }
                                tasks.RemoveAll(t => t.Type == TaskNodeType.Movement);
                                tasks.Add(new TaskNode(nav.WorldPosition, reachedBounds, TaskNodeType.Movement));
                                walkedTrail = true;
                            }
                        }

                        if (!walkedTrail)
                        {
                            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            {
                                AreWeThereYet.Instance.LogMessage("No suitable portal found and no trail to follow - using teleport button fallback");
                            }

                            var tpConfirmation = GetTpConfirmation();
                            if (tpConfirmation != null)
                            {
                                // GetClientRect() is window-relative - add the window's
                                // top-left, same as GetTpButton, so the click lands
                                // correctly in windowed mode.
                                var confirmWindowOffset = AreWeThereYet.Instance.GameController.Window.GetWindowRectangle().TopLeft;
                                yield return Mouse.SetCursorPosHuman(tpConfirmation.GetClientRect().Center + confirmWindowOffset);
                                yield return new WaitTime(200);
                                yield return Mouse.LeftClick();
                                yield return new WaitTime(1000);
                            }

                            var tpButton = GetTpButton(leaderPartyElement);
                            if (!tpButton.Equals(Vector2.Zero))
                            {
                                yield return Mouse.SetCursorPosHuman(tpButton, false);
                                yield return new WaitTime(200);
                                yield return Mouse.LeftClick();
                                yield return new WaitTime(200);
                            }
                        }
                    }
                }
                else
                {
                    // Leader zone info not reliable yet, wait for it to stabilize
                    var timeSinceChange = DateTime.Now - _leaderZoneChangeTime;
                    var bufferTime = TimeSpan.FromMilliseconds(AreWeThereYet.Instance.Settings.AutoPilot.ZoneUpdateBuffer?.Value ?? 2000);

                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        var remaining = bufferTime - timeSinceChange;
                        AreWeThereYet.Instance.LogMessage($"Zone info not reliable yet - waiting {remaining.TotalMilliseconds:F0}ms more (Current: '{leaderPartyElement.ZoneName}')");
                    }

                    yield return new WaitTime(200); // Wait a bit longer for zone info to stabilize
                }
            }
            else if (followTarget == null && !_isTransitioning && leaderPartyElement != null && leaderInSameZone)
            {
                // SAME ZONE, LEADER ENTITY MISSING: the leader is reported to be in THIS
                // zone (otherwise the cross-zone branch above would have handled it), but
                // their entity can't be found. That's the signature of an intra-zone area
                // transition: a transition to a physically separate sub-area (e.g. "The
                // Warden's Quarters") that does NOT change the zone's displayed name, so
                // the name-matching in GetBestAreaTransitionEntity can't tell us which
                // transition it was. Finish whatever breadcrumb trail we still have (the
                // leader's last valid recorded path) first, then once that's exhausted,
                // assume the closest area transition to where the trail ended is the one
                // they took.
                _lastKnownLeaderZone = "";
                _leaderZoneChangeTime = DateTime.MinValue;

                // While waiting at the entrance we don't chase the leader through an
                // intra-zone transition either - that's still "pathing to the leader".
                // Only a reported zone change (the branch above) or the leader coming
                // back into view (below) should pull us out of this state.
                if (!_waitingAtZoneEntrance)
                {
                var pfEnabled = AreWeThereYet.Instance.Settings.AutoPilot.Pathfinding.Enabled.Value;
                var reachedBounds = AreWeThereYet.Instance.Settings.AutoPilot.Pathfinding.ReachedBounds.Value;

                var walkedTrail = false;
                if (pfEnabled && AreWeThereYet.Instance.leaderFollower.HasTrail &&
                    AreWeThereYet.Instance.leaderFollower.TrailEnd is Vector3 trailEnd)
                {
                    var nav = AreWeThereYet.Instance.leaderFollower.NavigateTo(
                        AreWeThereYet.Instance.playerPosition, trailEnd,
                        allowTrailFollow: true,
                        arriveWithin: AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value);

                    if (nav.Type != FollowResultType.Idle)
                    {
                        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                        {
                            AreWeThereYet.Instance.LogMessage("Leader missing in same zone - finishing last valid path before assuming an intra-zone transition");
                        }
                        tasks.RemoveAll(t => t.Type == TaskNodeType.Movement);
                        tasks.Add(new TaskNode(nav.WorldPosition, reachedBounds, TaskNodeType.Movement));
                        walkedTrail = true;
                    }
                }

                if (!walkedTrail && !tasks.Exists(t => t.Type == TaskNodeType.Transition))
                {
                    // Trail exhausted (or there was none / pathfinding disabled) - the
                    // leader vanished at roughly this point, so assume the nearest area
                    // transition is the intra-zone one they took.
                    var fromPos = AreWeThereYet.Instance.leaderFollower.TrailEnd ?? AreWeThereYet.Instance.playerPosition;
                    var closestTransition = GetClosestAreaTransitionEntity(fromPos);
                    if (closestTransition != null)
                    {
                        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                        {
                            AreWeThereYet.Instance.LogMessage($"Same-zone transition assumed: closest transition '{closestTransition.RenderName}' at distance {Vector3.Distance(fromPos, closestTransition.Pos):F0}");
                        }
                        tasks.RemoveAll(t => t.Type == TaskNodeType.Movement);
                        tasks.Add(new TaskNode(closestTransition, AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value, TaskNodeType.Transition));
                    }
                }
                }
            }
            else if (followTarget != null)
            {
                // Reset zone tracking when leader is found in this zone
                _lastKnownLeaderZone = "";
                _leaderZoneChangeTime = DateTime.MinValue;

                // Waiting-at-entrance exit condition: the leader is on screen (a
                // reliable, non-clamped screen projection - see WorldToValidScreenPosition),
                // i.e. actually "close enough that they are visible on our screen".
                // Once that's true we drop out of the waiting state and fall through to
                // normal follow logic below this tick.
                if (_waitingAtZoneEntrance)
                {
                    Helper.WorldToValidScreenPosition(followTarget.Pos, out var leaderOffScreen);
                    if (!leaderOffScreen)
                    {
                        _waitingAtZoneEntrance = false;
                        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            AreWeThereYet.Instance.LogMessage("Leader visible on screen - resuming normal follow.");
                    }
                }

                if (_waitingAtZoneEntrance)
                {
                    // Still waiting: stay put, don't path toward the leader.
                    tasks.RemoveAll(t => t.Type == TaskNodeType.Movement);
                    if (followTarget?.Pos != null)
                        lastTargetPosition = followTarget.Pos;
                }
                else
                {
                var playerPos     = AreWeThereYet.Instance.playerPosition;
                var leaderPos     = followTarget.Pos;
                var pfSettings    = AreWeThereYet.Instance.Settings.AutoPilot.Pathfinding;
                var reachedBounds = pfSettings.ReachedBounds.Value;

                // --- Movement task generation ---
                if (pfSettings.Enabled.Value)
                {
                    // Breadcrumb-first pathfinding: trail recording + LOS string-pull + A* fallback
                    var followResult = AreWeThereYet.Instance.leaderFollower.Tick(playerPos, leaderPos);

                    switch (followResult.Type)
                    {
                        case FollowResultType.Idle:
                            // In position — clear any stale movement tasks
                            tasks.RemoveAll(t => t.Type == TaskNodeType.Movement);
                            break;

                        case FollowResultType.MoveTo:
                            // Replace movement tasks with the next waypoint
                            tasks.RemoveAll(t => t.Type == TaskNodeType.Movement);
                            tasks.Add(new TaskNode(followResult.WorldPosition, reachedBounds, TaskNodeType.Movement));
                            break;

                        case FollowResultType.PortalSuspected:
                            // Walk to the last trail point (portal location).
                            // Once in label range, the existing Transition task logic clicks it.
                            if (!tasks.Exists(t => t.Type == TaskNodeType.Movement))
                                tasks.Add(new TaskNode(followResult.WorldPosition, reachedBounds, TaskNodeType.Movement));

                            // If we're close to the suspected portal location, look for the area
                            // transition entity. followTarget != null here means the leader's
                            // entity is still tracked in OUR entity list, which is Area-scoped -
                            // so we're already in the same physical Area as them. That rules out
                            // name-matching against leaderPartyElement.ZoneName (GetBestAreaTransitionEntity):
                            // an intra-zone transition like "The Warden's Quarters" never renames
                            // the zone, so its RenderName will never contain the zone name we'd be
                            // matching against. Just take the closest transition to where the
                            // trail went unreachable - the same heuristic used for the "leader
                            // entity missing entirely" case below.
                            var distToPortal = Vector3.Distance(playerPos, followResult.WorldPosition);
                            if (distToPortal < AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value)
                            {
                                var transitionEntity = GetClosestAreaTransitionEntity(followResult.WorldPosition);
                                if (transitionEntity != null && transitionEntity.DistancePlayer < 80)
                                {
                                    tasks.RemoveAll(t => t.Type == TaskNodeType.Movement);
                                    tasks.Add(new TaskNode(transitionEntity, 200, TaskNodeType.Transition));
                                }
                            }
                            break;
                    }
                }
                else
                {
                    // Pathfinding disabled: simple direct-follow fallback. Stop only once
                    // BOTH within KeepWithinDistance AND in line of sight of the leader,
                    // matching the pathfinding-enabled behaviour in LeaderFollower.
                    var distanceToLeader = Vector3.Distance(playerPos, leaderPos);
                    // Terrain data is normally kept fresh by LeaderFollower.Tick, which only
                    // runs when pathfinding is enabled — populate it here too so the LOS
                    // check below doesn't silently see stale/absent terrain and report "no
                    // LOS" (which would force movement) every tick.
                    AreWeThereYet.Instance.lineOfSight.EnsureTerrainData();
                    var hasLosToLeader = AreWeThereYet.Instance.lineOfSight.HasLineOfSightRaw(playerPos, leaderPos);
                    if (distanceToLeader >= AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value ||
                        !hasLosToLeader)
                    {
                        tasks.RemoveAll(t => t.Type == TaskNodeType.Movement);
                        tasks.Add(new TaskNode(leaderPos, AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value));
                    }
                    else
                    {
                        tasks.RemoveAll(t => t.Type == TaskNodeType.Movement);
                    }
                }

                // --- Loot and MercenaryOptIn (unchanged) ---
                var isHideout = (bool)AreWeThereYet.Instance?.GameController?.Area?.CurrentArea?.IsHideout;
                if (!isHideout)
                {
                    var questLoot = GetQuestItem();
                    if (questLoot != null &&
                        Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.Pos) < AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value &&
                        tasks.Find(I => I.Type == TaskNodeType.Loot) == null)
                    {
                        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                        {
                            var distance = Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.Pos);
                            AreWeThereYet.Instance.LogMessage($"Adding quest loot task - Distance: {distance:F1}, Item: {questLoot.Metadata}");
                        }
                        tasks.Add(new TaskNode(questLoot.Pos, AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance, TaskNodeType.Loot));
                    }
                    else if (questLoot != null && AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        var distance = Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.Pos);
                        var hasLootTask = tasks.Find(I => I.Type == TaskNodeType.Loot) != null;
                        AreWeThereYet.Instance.LogMessage($"Quest loot NOT added - Distance: {distance:F1}, TooFar: {distance >= AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value}, HasLootTask: {hasLootTask}");
                    }

                    var mercenaryOptIn = GetMercenaryOptInButton();
                    if (mercenaryOptIn != null &&
                        Vector3.Distance(AreWeThereYet.Instance.playerPosition, mercenaryOptIn.ItemOnGround.Pos) < AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value &&
                        tasks.Find(I => I.Type == TaskNodeType.MercenaryOptIn) == null)
                    {
                        if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            AreWeThereYet.Instance.LogMessage("Found mercenary OPT-IN button - adding to tasks");
                        tasks.Add(new TaskNode(mercenaryOptIn, AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance, TaskNodeType.MercenaryOptIn));
                    }
                }

                if (followTarget?.Pos != null)
                    lastTargetPosition = followTarget.Pos;
                }
            }

            if (tasks?.Count > 0)
            {
                var currentTask = tasks.First();
                var taskDistance = Vector3.Distance(AreWeThereYet.Instance.playerPosition, currentTask.WorldPosition);
                var playerDistanceMoved = Vector3.Distance(AreWeThereYet.Instance.playerPosition, lastPlayerPosition);

                if (currentTask.Type == TaskNodeType.Transition &&
                    playerDistanceMoved >= AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value)
                {
                    tasks.RemoveAt(0);
                    lastPlayerPosition = AreWeThereYet.Instance.playerPosition;
                    yield return null;
                    continue;
                }
                switch (currentTask.Type)
                {
                    case TaskNodeType.Movement:
                        if (AreWeThereYet.Instance.Settings.Debug.LogPathfinding.Value)
                        {
                            var now = DateTime.Now;
                            if ((now - _lastMoveDiagLog).TotalMilliseconds >= AreWeThereYet.Instance.Settings.Debug.PathfindingLogInterval.Value)
                            {
                                _lastMoveDiagLog = now;
                                var curPos = AreWeThereYet.Instance.playerPosition;
                                var screenPos = Helper.WorldToValidScreenPosition(currentTask.WorldPosition);

                                // Actual movement heading (where we really went) vs. the bearing
                                // to the intended target (where we meant to go). A large gap
                                // between the two — despite a sane target — points at click/
                                // execution/game-movement, not our pathing logic.
                                var movedSinceLastLog = 0f;
                                var headingDiff = "n/a";
                                if (_lastMoveDiagPos != Vector3.Zero)
                                {
                                    var delta = curPos - _lastMoveDiagPos;
                                    movedSinceLastLog = delta.Length();
                                    if (movedSinceLastLog > 5f)
                                    {
                                        var actualHeading = Math.Atan2(delta.Y, delta.X) * 180.0 / Math.PI;
                                        var targetBrg = Math.Atan2(
                                            currentTask.WorldPosition.Y - curPos.Y,
                                            currentTask.WorldPosition.X - curPos.X) * 180.0 / Math.PI;
                                        var diff = Math.Abs(actualHeading - targetBrg);
                                        if (diff > 180) diff = 360 - diff;
                                        headingDiff = $"{diff:F0}(act={actualHeading:F0},want={targetBrg:F0})";
                                    }
                                }
                                _lastMoveDiagPos = curPos;

                                AreWeThereYet.Instance.LogMessage(
                                    $"[ATY-PF] MoveExec | player world({curPos.X:F0},{curPos.Y:F0}) | " +
                                    $"target world({currentTask.WorldPosition.X:F0},{currentTask.WorldPosition.Y:F0}) " +
                                    $"taskDist={taskDistance:F0} | screenClick=({screenPos.X:F0},{screenPos.Y:F0}) | " +
                                    $"movedSinceLastLog={movedSinceLastLog:F1} | headingDiff={headingDiff}");
                            }
                        }

                        if (AreWeThereYet.Instance.Settings.AutoPilot.DashEnabled &&
                        ShouldUseDash(currentTask.WorldPosition.WorldToGrid()))
                        {
                            yield return Mouse.SetCursorPosHuman(Helper.WorldToValidScreenPosition(currentTask.WorldPosition));
                            yield return new WaitTime(random.Next(25) + 30);
                            Keyboard.KeyPress(AreWeThereYet.Instance.Settings.AutoPilot.DashKey);
                            yield return new WaitTime(random.Next(25) + 30);
                        }
                        else
                        {
                            yield return Mouse.SetCursorPosHuman(Helper.WorldToValidScreenPosition(currentTask.WorldPosition));
                            yield return new WaitTime(random.Next(25) + 30);
                            Keyboard.KeyDown(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                            yield return new WaitTime(random.Next(25) + 30);
                            Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                        }

                        // Use the task's own Bounds, not a hardcoded ReachedBounds: pathfinding-
                        // generated waypoints are created with the small ReachedBounds value
                        // (they're intermediate trail points), but the no-pathfinding direct-
                        // follow fallback creates its Movement task with KeepWithinDistance as
                        // Bounds — that leash was previously being ignored here, so the bot
                        // walked all the way down to ReachedBounds (~50 units) regardless of
                        // the configured KeepWithinDistance.
                        if (taskDistance <= currentTask.Bounds)
                            tasks.RemoveAt(0);
                        yield return null;
                        yield return null;
                        continue;

                    case TaskNodeType.Loot:
                        {
                            currentTask.AttemptCount++;
                            var questLoot = GetQuestItem();
                            if (questLoot == null
                                || currentTask.AttemptCount > 2
                                || Vector3.Distance(AreWeThereYet.Instance.playerPosition, questLoot.Pos) >=
                                AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value)
                            {
                                tasks.RemoveAt(0);
                                yield return null;
                            }

                            Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                            yield return new WaitTime(AreWeThereYet.Instance.Settings.AutoPilot.InputFrequency);
                            if (questLoot != null)
                            {
                                var targetInfo = questLoot.GetComponent<Targetable>();
                                switch (targetInfo.isTargeted)
                                {
                                    case false:
                                        yield return MouseoverItem(questLoot);
                                        break;
                                    case true:
                                        yield return Mouse.LeftClick();
                                        yield return new WaitTime(1000);
                                        break;
                                }
                            }

                            break;
                        }

                    case TaskNodeType.Transition:
                        {
                            var transitionEntity = currentTask.TransitionEntity;

                            // Re-validate the transition entity still exists before attempting to use it
                            if (transitionEntity == null || !transitionEntity.IsValid)
                            {
                                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                                {
                                    AreWeThereYet.Instance.LogMessage("Area transition entity became invalid - removing transition task, will re-evaluate in main loop");
                                }

                                tasks.RemoveAt(0);
                                yield return null;
                                continue;
                            }

                            // ---------------------------------------------------------------
                            // STEP 0: DISMISS ANY BLOCKING POPUP.
                            // While a confirmation dialog (the "are you sure you want to
                            // teleport" prompt, or a generic OK-only area-entry prompt) is open,
                            // the game won't register mouseover/targeting on world entities - so
                            // the "not targeted" wait in STEP 2 would spin forever without this.
                            // ---------------------------------------------------------------
                            var popupClickPos = GetOpenPopupClickPosition();
                            if (popupClickPos != null)
                            {
                                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                                {
                                    AreWeThereYet.Instance.LogMessage("Dismissing open popup before continuing transition");
                                }

                                yield return Mouse.SetCursorPosHuman(popupClickPos.Value);
                                yield return new WaitTime(150);
                                yield return Mouse.LeftClick();
                                yield return new WaitTime(300);
                                yield return null;
                                continue;
                            }

                            // ---------------------------------------------------------------
                            // STEP 1: PATH TO THE TRANSITION.
                            // Click-to-move relying on a UI label uses the GAME'S navigation,
                            // which snags on walls (the "rubbing against a wall that wasn't at
                            // the transition" symptom). So if we aren't already within clicking
                            // range of the entity, walk toward it ourselves using the SAME
                            // breadcrumb + A* pathing that follows the leader - this finishes
                            // the leader's trail to the transition, routing around walls.
                            // Do NOT flag a transition yet - we haven't clicked anything.
                            // ---------------------------------------------------------------
                            var portalPos = currentTask.WorldPosition;
                            var entityHoverPos = transitionEntity.BoundsCenterPos;
                            var distToPortal = transitionEntity.DistancePlayer;
                            var clickDistance = AreWeThereYet.Instance.Settings.AutoPilot.TransitionClickDistance.Value;
                            var pfEnabled = AreWeThereYet.Instance.Settings.AutoPilot.Pathfinding.Enabled.Value;

                            // Screen-projection reliability for the entity's own hover point,
                            // checked up front: a vertical zone (stairs/cliffs/ladders) can put
                            // the entity within clickDistance while its projection still lands
                            // off-screen because of camera angle/elevation, not distance. Both
                            // gates below (whether to walk closer, and whether "arrived" from
                            // the navigator is trustworthy) need this.
                            Helper.WorldToValidScreenPosition(entityHoverPos, out var entityScreenUnreliable);

                            if (distToPortal > clickDistance || entityScreenUnreliable)
                            {
                                // Ask the shared navigator for the next waypoint toward the
                                // portal (trail-follow enabled: the trail leads to it). Falls
                                // back to walking straight at the portal if pathfinding is off.
                                var moveTarget = portalPos;
                                if (pfEnabled && distToPortal > clickDistance)
                                {
                                    var nav = AreWeThereYet.Instance.leaderFollower.NavigateTo(
                                        AreWeThereYet.Instance.playerPosition, portalPos,
                                        allowTrailFollow: true, arriveWithin: clickDistance);

                                    // Idle == the navigator considers us arrived (in range + LOS);
                                    // only fall through to the click if the projection is ALSO
                                    // reliable - "arrived" by distance doesn't mean "on screen".
                                    if (nav.Type != FollowResultType.Idle)
                                        moveTarget = nav.WorldPosition;
                                    else if (!entityScreenUnreliable)
                                        goto DoTransitionClick;
                                }
                                // else: already within clickDistance, so we're only here because
                                // the entity's projection is unreliable - approach it directly
                                // (moveTarget stays portalPos) to get a cleaner camera angle.

                                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                                {
                                    AreWeThereYet.Instance.LogMessage(distToPortal > clickDistance
                                        ? $"Transition {distToPortal:F0} away (> {clickDistance}) - walking to it before clicking"
                                        : "Transition in range but projects off-screen (likely elevation change) - approaching for a cleaner angle");
                                }

                                // Click an intermediate point that's reliably on-screen rather
                                // than the raw (possibly off-screen, clamped-to-the-wrong-spot)
                                // moveTarget. Mirrors LeaderFollower's MaxClickDistance-capped
                                // waypoint selection, but driven by the actual screen
                                // projection instead of a fixed world-distance threshold.
                                var (_, moveClickScreenPos) = Helper.GetReliableClickPoint(
                                    AreWeThereYet.Instance.playerPosition, moveTarget);

                                if (AreWeThereYet.Instance.Settings.AutoPilot.DashEnabled &&
                                    ShouldUseDash(moveTarget.WorldToGrid()))
                                {
                                    yield return Mouse.SetCursorPosHuman(moveClickScreenPos);
                                    yield return new WaitTime(random.Next(25) + 30);
                                    Keyboard.KeyPress(AreWeThereYet.Instance.Settings.AutoPilot.DashKey);
                                    yield return new WaitTime(random.Next(25) + 30);
                                }
                                else
                                {
                                    yield return Mouse.SetCursorPosHuman(moveClickScreenPos);
                                    yield return new WaitTime(random.Next(25) + 30);
                                    Keyboard.KeyDown(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                                    yield return new WaitTime(random.Next(25) + 30);
                                    Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                                }

                                yield return null;
                                continue;
                            }

                            DoTransitionClick:;

                            // ---------------------------------------------------------------
                            // STEP 2: TARGET AND CLICK THE ENTITY.
                            // We're in range. Move the cursor onto the transition's own world
                            // position (same conversion used for movement/leader targets) and
                            // check its Targetable component - isTargeted tells us the game is
                            // actually aiming at this entity rather than the ground next to it.
                            // Only flag the transition (so AreaChange() spawns the grace period)
                            // and click once we're confirmed targeted.
                            // ---------------------------------------------------------------
                            Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                            yield return new WaitTime(60);

                            var transitionScreenPos = Helper.WorldToValidScreenPosition(transitionEntity.BoundsCenterPos, out var transitionScreenClamped);
                            var targetable = transitionEntity.GetComponent<Targetable>();

                            // We can arrive here via the goto above without having walked at
                            // all this tick, so the projection can still be unreliable (e.g.
                            // the entity shifted position between checks, or the earlier
                            // "reliable" read this tick was a hair off). Fold that into the
                            // same wait/timeout path as "not targeted" rather than clicking
                            // a point we already know we can't trust.
                            if (transitionScreenClamped || targetable == null || !targetable.isTargeted)
                            {
                                // Not targeted yet. Track how long we've been waiting - if the
                                // entity never becomes targetable (bad hover point, occluded,
                                // or a popup we didn't catch), give up after a timeout instead
                                // of hovering forever, and fall back to the party TP button.
                                currentTask.TargetWaitStartTime ??= DateTime.Now;
                                var targetWaitElapsed = DateTime.Now - currentTask.TargetWaitStartTime.Value;
                                var targetingTimeout = TimeSpan.FromMilliseconds(
                                    AreWeThereYet.Instance.Settings.AutoPilot.TransitionTargetingTimeout.Value);

                                if (targetWaitElapsed > targetingTimeout)
                                {
                                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                                    {
                                        AreWeThereYet.Instance.LogMessage($"Transition never became targeted after {targetWaitElapsed.TotalSeconds:F1}s - falling back to party teleport button");
                                    }

                                    tasks.RemoveAt(0); // give up on the label
                                    yield return FallbackToPartyTeleport();
                                    yield return null;
                                    continue;
                                }

                                // Still within the timeout - mouseover and try again next tick.
                                yield return Mouse.SetCursorPosHuman(transitionScreenPos);
                                yield return new WaitTime(30 + random.Next(AreWeThereYet.Instance.Settings.AutoPilot.InputFrequency));
                                yield return null;
                                continue;
                            }

                            currentTask.TargetWaitStartTime = null;
                            currentTask.AttemptCount++;
                            _isTransitioning = true;
                            _transitioningStartTime = DateTime.Now;

                            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            {
                                AreWeThereYet.Instance.LogMessage($"Clicking transition (attempt {currentTask.AttemptCount}/{AreWeThereYet.Instance.Settings.AutoPilot.MaxTransitionAttempts.Value})");
                            }

                            yield return Mouse.SetCursorPosAndLeftClickHuman(transitionScreenPos, 100);

                            // ---------------------------------------------------------------
                            // STEP 3: WAIT FOR THE TRANSITION.
                            // A cross-zone click starts a real load; AreaChange() then fires,
                            // which clears the flag (via the grace period) and resets the task
                            // list. An intra-zone click (sub-area, same display name) does
                            // neither of those - it never triggers AreaChange() at all - so we
                            // also probe reachability back to the click point as a third signal
                            // (see IsDisconnectedFromClickPoint).
                            // ---------------------------------------------------------------
                            var waited = 0;
                            var transitionWait = AreWeThereYet.Instance.Settings.AutoPilot.TransitionWaitTime.Value;
                            var clickedWorldPos = transitionEntity.BoundsCenterPos;
                            while (waited < transitionWait)
                            {
                                if (AreWeThereYet.Instance.GameController.IsLoading || !tasks.Contains(currentTask) ||
                                    IsDisconnectedFromClickPoint(clickedWorldPos))
                                    break;
                                yield return new WaitTime(100);
                                waited += 100;
                            }

                            // Success: a load started, AreaChange() already cleared our task, or
                            // we're now in a disconnected pocket of the same terrain (intra-zone).
                            if (AreWeThereYet.Instance.GameController.IsLoading || !tasks.Contains(currentTask))
                            {
                                yield return null;
                                continue;
                            }

                            if (IsDisconnectedFromClickPoint(clickedWorldPos))
                            {
                                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                                {
                                    AreWeThereYet.Instance.LogMessage("Transition succeeded (intra-zone - click point now unreachable; AreaChange() won't fire for this one)");
                                }

                                // AreaChange()/PostTransitionGracePeriod never runs for this case,
                                // so nothing else will clear the flag or drop this task - do it here.
                                _isTransitioning = false;
                                tasks.RemoveAt(0);
                                yield return null;
                                continue;
                            }

                            // ---------------------------------------------------------------
                            // STEP 4: CLICK FAILED. Clear the optimistic flag immediately so
                            // zone detection isn't wedged waiting on the 8s self-heal.
                            // ---------------------------------------------------------------
                            _isTransitioning = false;

                            if (currentTask.AttemptCount < AreWeThereYet.Instance.Settings.AutoPilot.MaxTransitionAttempts.Value)
                            {
                                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                                {
                                    AreWeThereYet.Instance.LogMessage("Transition click did not change zones - retrying");
                                }
                                yield return null;
                                continue;
                            }

                            // ---------------------------------------------------------------
                            // STEP 5: OUT OF RETRIES. Fall back to the party UI: click the
                            // leader's teleport/portal icon and accept the travel confirmation.
                            // ---------------------------------------------------------------
                            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            {
                                AreWeThereYet.Instance.LogMessage("Transition clicks exhausted - falling back to party teleport button");
                            }

                            tasks.RemoveAt(0); // give up on the label
                            yield return FallbackToPartyTeleport();

                            yield return null;
                            continue;
                        }

                    case TaskNodeType.MercenaryOptIn:
                        {
                            currentTask.AttemptCount++;
                            var mercenaryOptIn = GetMercenaryOptInButton();

                            // Remove task if button disappeared, too many attempts, or we're too far
                            if (mercenaryOptIn == null ||
                                currentTask.AttemptCount > 3 ||
                                Vector3.Distance(AreWeThereYet.Instance.playerPosition, mercenaryOptIn.ItemOnGround.Pos) >=
                                AreWeThereYet.Instance.Settings.AutoPilot.TransitionDistance.Value)
                            {
                                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                                {
                                    var reason = mercenaryOptIn == null ? "button disappeared" :
                                                currentTask.AttemptCount > 3 ? "too many attempts" : "too far away";
                                    AreWeThereYet.Instance.LogMessage($"Removing mercenary OPT-IN task: {reason}");
                                }

                                tasks.RemoveAt(0);
                                yield return null;
                                continue;
                            }

                            // Stop movement and click the button
                            Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                            yield return new WaitTime(AreWeThereYet.Instance.Settings.AutoPilot.InputFrequency);

                            var buttonPos = GetMercenaryOptInButtonPosition(mercenaryOptIn);
                            if (!buttonPos.Equals(Vector2.Zero))
                            {
                                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                                {
                                    AreWeThereYet.Instance.LogMessage($"Clicking mercenary OPT-IN button at {buttonPos}");
                                }

                                yield return Mouse.SetCursorPosHuman(buttonPos, false);
                                yield return new WaitTime(200);
                                yield return Mouse.LeftClick();
                                yield return new WaitTime(500); // Wait for button to process click

                                // Remove task after clicking (button should disappear)
                                tasks.RemoveAt(0);
                            }
                            else
                            {
                                // Couldn't get button position, remove task
                                tasks.RemoveAt(0);
                            }

                            break;
                        }
                }
            }

            // =================================================================
            // SECTION 4: MANDATORY END-OF-LOOP HOUSEKEEPING
            // =================================================================
            // This block is OUTSIDE all other logic and will run on EVERY
            // single iteration of the while loop, guaranteeing correctness.
            lastPlayerPosition = AreWeThereYet.Instance.playerPosition;
            yield return new WaitTime(50);
        }
    }

    private bool ShouldUseDash(Vector2 targetPosition)
    {
        try
        {
            // 1. Initial checks
            if (LineOfSight == null ||
                AreWeThereYet.Instance?.GameController?.Player?.GridPos == null ||
                AreWeThereYet.Instance?.Settings?.AutoPilot?.DashEnabled?.Value != true)
                return false;

            var playerPos = AreWeThereYet.Instance.GameController.Player.GridPos;
            var distance = Vector2.Distance(playerPos, targetPosition);

            var minDistance = AreWeThereYet.Instance.Settings.AutoPilot.Dash.DashMinDistance.Value;
            var maxDistance = AreWeThereYet.Instance.Settings.AutoPilot.Dash.DashMaxDistance.Value;

            if (distance < minDistance || distance > maxDistance)
            {
                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    AreWeThereYet.Instance.LogMessage($"ShouldUseDash: Distance {distance:F1} outside dash range ({minDistance}-{maxDistance})");
                return false;
            }

            // 2. Convert to System.Numerics.Vector2
            var playerPosNumerics = new System.Numerics.Vector2(playerPos.X, playerPos.Y);
            var targetPosNumerics = new System.Numerics.Vector2(targetPosition.X, targetPosition.Y);

            // 3. THE FIX: Call the new method and check the result
            var pathStatus = LineOfSight.GetPathStatus(playerPosNumerics, targetPosNumerics);

            // The new logic: only dash if the path is specifically blocked by a dashable obstacle.
            var shouldDash = pathStatus == PathStatus.Dashable;

            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                AreWeThereYet.Instance.LogMessage($"ShouldUseDash: RESULT = {shouldDash} (distance: {distance:F1}, pathStatus: {pathStatus})");
            }

            return shouldDash;
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"ShouldUseDash failed: {ex.Message}");
            return false; // Safe fallback - don't dash if terrain check fails
        }
    }

    private Entity GetFollowingTarget()
    {
        try
        {
            string leaderName = AreWeThereYet.Instance.Settings.AutoPilot.LeaderName.Value.ToLower();
            return AreWeThereYet.Instance.GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Player].FirstOrDefault(x => string.Equals(x.GetComponent<Player>()?.PlayerName.ToLower(), leaderName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static Entity GetQuestItem()
    {
        try
        {
            var questItemLabels = AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels
                .Where(x => x != null && x.IsVisible && x.Label != null && x.Label.IsValid && 
                        x.Label.IsVisible && x.ItemOnGround != null && 
                        x.ItemOnGround.Type == EntityType.WorldItem && 
                        x.ItemOnGround.IsTargetable && x.ItemOnGround.HasComponent<WorldItem>())
                .Where(x =>
                {
                    try
                    {
                        var itemEntity = x.ItemOnGround.GetComponent<WorldItem>().ItemEntity;
                        return AreWeThereYet.Instance.GameController.Files.BaseItemTypes.Translate(itemEntity.Path).ClassName == "QuestItem";
                    }
                    catch
                    {
                        return false;
                    }
                })
                .OrderBy(x => Vector3.Distance(AreWeThereYet.Instance.playerPosition, x.ItemOnGround.Pos))
                .ToList();

            // Return the Entity from the closest quest item label
            return questItemLabels?.FirstOrDefault()?.ItemOnGround;
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"GetQuestItem failed: {ex.Message}");
            return null;
        }
    }

    public void Render()
    {
        if (AreWeThereYet.Instance.Settings.AutoPilot.ToggleKey.PressedOnce())
        {
            AreWeThereYet.Instance.Settings.AutoPilot.Enabled.SetValueNoEvent(!AreWeThereYet.Instance.Settings.AutoPilot.Enabled.Value);
            tasks = new List<TaskNode>();
        }

        if (!AreWeThereYet.Instance.Settings.AutoPilot.Enabled || AreWeThereYet.Instance.GameController.IsLoading || !AreWeThereYet.Instance.GameController.InGame)
            return;

        try
        {
            var transitions =
                AreWeThereYet.Instance.GameController?.EntityListWrapper?.ValidEntitiesByType[EntityType.AreaTransition]
                    ?.Where(x => x != null && x.IsValid).ToList();

            if (transitions != null)
            {
                foreach (var transition in transitions)
                {
                    var screenPos = Helper.WorldToValidScreenPosition(transition.Pos);
                    AreWeThereYet.Instance.Graphics.DrawText(transition.RenderName ?? "AreaTransition", screenPos, Color.Firebrick);
                }
            }
        }
        catch (Exception)
        {
        }

        // Quest Item rendering
        try
        {
            var questItemLabels =
                AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels.Where(x =>
                    x != null && x.IsVisible && x.Label != null && x.Label.IsValid && x.Label.IsVisible &&
                    x.ItemOnGround != null && x.ItemOnGround.Type == EntityType.WorldItem &&
                    x.ItemOnGround.IsTargetable && x.ItemOnGround.HasComponent<WorldItem>()).Where(x =>
                {
                    try
                    {
                        var itemEntity = x.ItemOnGround.GetComponent<WorldItem>().ItemEntity;
                        return AreWeThereYet.Instance.GameController.Files.BaseItemTypes.Translate(itemEntity.Path).ClassName == "QuestItem";
                    }
                    catch
                    {
                        return false;
                    }
                }).ToList();

            foreach (var questItem in questItemLabels)
            {
                AreWeThereYet.Instance.Graphics.DrawLine(questItem.Label.GetClientRectCache.TopLeft, questItem.Label.GetClientRectCache.TopRight, 4f, Color.Lime);
            }
        }
        catch (Exception)
        {
        }

        // Mercenary OPT-IN button rendering (simple version)
        try
        {
            var mercenaryLabels =
                AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels.Where(x =>
                    x != null && x.IsVisible && x.Label != null && x.Label.IsValid && x.Label.IsVisible &&
                    x.ItemOnGround != null &&
                    x.ItemOnGround.Metadata.ToLower().Contains("mercenary") &&
                    x.Label.Children?.Count > 2 && x.Label.Children[2] != null &&
                    x.Label.Children[2].IsVisible).ToList();

            foreach (var mercenary in mercenaryLabels)
            {
                var optInButton = mercenary.Label.Children[2];
                AreWeThereYet.Instance.Graphics.DrawLine(optInButton.GetClientRectCache.TopLeft, optInButton.GetClientRectCache.TopRight, 3f, Color.Cyan);
            }
        }
        catch (Exception)
        {
        }

        try
        {
            var taskCount = 0;
            var dist = 0f;
            var cachedTasks = tasks;

            var lineWidth = (float)AreWeThereYet.Instance.Settings.AutoPilot.Visual.TaskLineWidth.Value;
            var lineColor = AreWeThereYet.Instance.Settings.AutoPilot.Visual.TaskLineColor.Value;
            if (cachedTasks?.Count > 0)
            {
                var taskTypeName = cachedTasks[0].Type == TaskNodeType.MercenaryOptIn ? "Mercenary OPT-IN" : cachedTasks[0].Type.ToString();
                AreWeThereYet.Instance.Graphics.DrawText(
                    "Current Task: " + taskTypeName,
                    new Vector2(500, 180));
                foreach (var task in cachedTasks.TakeWhile(task => task?.WorldPosition != null))
                {
                    if (taskCount == 0)
                    {
                        AreWeThereYet.Instance.Graphics.DrawLine(
                            Helper.WorldToValidScreenPosition(AreWeThereYet.Instance.playerPosition),
                            Helper.WorldToValidScreenPosition(task.WorldPosition), lineWidth, lineColor);
                        dist = Vector3.Distance(AreWeThereYet.Instance.playerPosition, task.WorldPosition);
                    }
                    else
                    {
                        AreWeThereYet.Instance.Graphics.DrawLine(Helper.WorldToValidScreenPosition(task.WorldPosition),
                            Helper.WorldToValidScreenPosition(cachedTasks[taskCount - 1].WorldPosition), lineWidth, lineColor);
                    }

                    taskCount++;
                }
            }
            if (AreWeThereYet.Instance.localPlayer != null)
            {
                var targetDist = Vector3.Distance(AreWeThereYet.Instance.playerPosition, lastTargetPosition);
                AreWeThereYet.Instance.Graphics.DrawText(
                    $"Follow Enabled: {AreWeThereYet.Instance.Settings.AutoPilot.Enabled.Value}", new System.Numerics.Vector2(500, 120));
                AreWeThereYet.Instance.Graphics.DrawText(
                    $"Task Count: {taskCount:D} Next WP Distance: {dist:F} Target Distance: {targetDist:F}",
                    new System.Numerics.Vector2(500, 140));
            }
        }
        catch (Exception)
        {
        }

        AreWeThereYet.Instance.Graphics.DrawText("AutoPilot: Active", new System.Numerics.Vector2(350, 120));
        if (_waitingAtZoneEntrance)
        {
            AreWeThereYet.Instance.Graphics.DrawText(
                $"Waiting at zone entrance (deaths: {_deathCountThisZone})",
                new System.Numerics.Vector2(350, 100), Color.Orange);
        }
        AreWeThereYet.Instance.Graphics.DrawText("Coroutine: " + (autoPilotCoroutine.Running ? "Active" : "Dead"), new System.Numerics.Vector2(350, 140));
        AreWeThereYet.Instance.Graphics.DrawText("Leader: " + "[ " + AreWeThereYet.Instance.Settings.AutoPilot.LeaderName.Value + " ] " + (followTarget != null ? "Found" : "Null"), new System.Numerics.Vector2(500, 160));
        AreWeThereYet.Instance.Graphics.DrawLine(new System.Numerics.Vector2(490, 110), new System.Numerics.Vector2(490, 210), 1, Color.White);

        // --- WATCHDOG & RESTART LOGIC ---
        // Check if the coroutine is null (hasn't started yet) or if it has stopped running.
        if (autoPilotCoroutine == null || !autoPilotCoroutine.Running)
        {
            // Log a message so you know a restart is happening.
            AreWeThereYet.Instance.LogMessage("[AutoPilot] Coroutine is dead or not started. Restarting...");

            // Call your existing method to start it.
            StartCoroutine();
        }
        // --- END OF WATCHDOG LOGIC ---    
    }
}
