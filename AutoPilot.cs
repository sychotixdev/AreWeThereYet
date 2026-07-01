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
    
    private bool IsLeaderZoneInfoReliable(PartyElementWindow leaderPartyElement)
    {
        try
        {
            // Check if zone name looks valid (not empty, not obviously stale)
            var zoneName = leaderPartyElement.ZoneName;
            var currentZone = AreWeThereYet.Instance.GameController?.Area.CurrentArea.DisplayName;
            
            // Invalid if empty or same as current zone when leader should be elsewhere
            if (string.IsNullOrEmpty(zoneName) || zoneName.Equals(currentZone))
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

    private LabelOnGround GetBestPortalLabel(PartyElementWindow leaderPartyElement)
    {
        try
        {
            var currentZoneName = AreWeThereYet.Instance.GameController?.Area.CurrentArea.DisplayName;
            var isHideout = (bool)AreWeThereYet.Instance?.GameController?.Area?.CurrentArea?.IsHideout;
            var realLevel = AreWeThereYet.Instance.GameController?.Area?.CurrentArea?.RealLevel ?? 0;

            // Enhanced logic: differentiate between leveling zones and endgame content
            if (isHideout || realLevel >= 68)
            {
                // ENDGAME/HIDEOUT: Any portal is fine (maps, hideout transitions)
                var portalLabels = AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels
                    .Where(x => x != null && x.IsVisible && x.Label != null && x.Label.IsValid &&
                            x.Label.IsVisible && x.ItemOnGround != null &&
                            (x.ItemOnGround.Metadata.ToLower().Contains("areatransition") ||
                                x.ItemOnGround.Metadata.ToLower().Contains("portal")))
                    .OrderBy(x => Vector3.Distance(lastTargetPosition, x.ItemOnGround.Pos))
                    .ToList();

                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    AreWeThereYet.Instance.LogMessage($"Endgame/Hideout portal search: Found {portalLabels?.Count ?? 0} portals");
                }

                return isHideout && portalLabels?.Count > 0
                    ? portalLabels[random.Next(portalLabels.Count)] // Random portal in hideout
                    : portalLabels?.FirstOrDefault(); // Closest portal in endgame
            }
            else
            {
                // LEVELING ZONES: Must find portal that leads to leader's specific zone
                var portalLabels = AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels
                    .Where(x => x != null && x.IsVisible && x.Label != null && x.Label.IsValid &&
                            x.Label.IsVisible && x.ItemOnGround != null &&
                            (x.ItemOnGround.Metadata.ToLower().Contains("areatransition") ||
                                x.ItemOnGround.Metadata.ToLower().Contains("portal")) &&
                            x.Label.Text.ToLower().Contains(leaderPartyElement.ZoneName.ToLower())) // IMPORTANT KEY IMPROVEMENT: Check portal text
                    .OrderBy(x => Vector3.Distance(lastTargetPosition, x.ItemOnGround.Pos))
                    .ToList();

                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                {
                    var allPortals = AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels
                        .Where(x => x != null && x.IsVisible && x.Label != null && x.Label.IsValid &&
                                x.Label.IsVisible && x.ItemOnGround != null &&
                                (x.ItemOnGround.Metadata.ToLower().Contains("transition") ||
                                    x.ItemOnGround.Metadata.ToLower().Contains("portal")))
                        .ToList();

                    AreWeThereYet.Instance.LogMessage($"Leveling zone portal search:");
                    AreWeThereYet.Instance.LogMessage($"  - Leader zone: '{leaderPartyElement.ZoneName}'");
                    AreWeThereYet.Instance.LogMessage($"  - All portals: {allPortals?.Count ?? 0}");
                    AreWeThereYet.Instance.LogMessage($"  - Matching portals: {portalLabels?.Count ?? 0}");

                    if (allPortals != null)
                    {
                        foreach (var portal in allPortals)
                        {
                            var matches = portal.Label.Text.Contains(leaderPartyElement.ZoneName);
                            AreWeThereYet.Instance.LogMessage($"    Portal: '{portal.Label.Text}' -> {(matches ? "MATCH" : "No match")}");
                        }
                    }
                }

                // EXPLICIT NULL CHECK: If no matching portals found in leveling zone, return null for teleport fallback
                if (portalLabels == null || portalLabels.Count == 0)
                {
                    if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                    {
                        AreWeThereYet.Instance.LogMessage($"No matching portal found for leader zone '{leaderPartyElement.ZoneName}' - will use teleport button fallback");
                    }
                    return null; // Force teleport button usage
                }

                return portalLabels.FirstOrDefault(); // Return closest matching portal
            }
        }
        catch (Exception ex)
        {
            AreWeThereYet.Instance.LogError($"GetBestPortalLabel failed: {ex.Message}");
            return null; // Exception fallback
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

            if (ui.Children[0].Children[0].Children[0].Text.Equals("Are you sure you want to teleport to this player's location?"))
                return ui.Children[0].Children[0].Children[3].Children[0];

            return null;
        }
        catch
        {
            return null;
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
            if (leaderPartyElement != null && followTarget != null && leaderPartyElement.ZoneName.Equals(currentAreaName))
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
        // Check for the "Same Zone, Different Instance" problem.
        if (finalLeaderPartyElement != null && finalLeaderPartyElement.ZoneName.Equals(finalCurrentAreaName))
        {
            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
            {
                AreWeThereYet.Instance.LogMessage($"[GracePeriod] DEADLOCK DETECTED: In same zone ('{finalCurrentAreaName}') but different instance. Forcing UI teleport to sync instances.", 10, Color.Red);
            }

            // Check for and click the "Are you sure?" confirmation box if it's open.
            var tpConfirmation = GetTpConfirmation();
            if (tpConfirmation != null)
            {
                yield return Mouse.SetCursorPosHuman(tpConfirmation.GetClientRect().Center);
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

            if (!AreWeThereYet.Instance.Settings.Enable.Value || !AreWeThereYet.Instance.Settings.AutoPilot.Enabled.Value || AreWeThereYet.Instance.localPlayer == null || !AreWeThereYet.Instance.localPlayer.IsAlive ||
                !AreWeThereYet.Instance.GameController.IsForeGroundCache || MenuWindow.IsOpened || AreWeThereYet.Instance.GameController.IsLoading || !AreWeThereYet.Instance.GameController.InGame)
            {
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

            if (followTarget == null && !_isTransitioning && leaderPartyElement != null &&
                !leaderPartyElement.ZoneName.Equals(AreWeThereYet.Instance.GameController?.Area.CurrentArea.DisplayName))
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

                    var portal = GetBestPortalLabel(leaderPartyElement);
                    if (portal != null)
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
                                AreWeThereYet.Instance.LogMessage($"Found reliable portal: {portal.ItemOnGround.Metadata}");
                            }
                            // Drop any trail-walk movement tasks: the Transition task now
                            // self-navigates to the portal, so it should be the only task.
                            tasks.RemoveAll(t => t.Type == TaskNodeType.Movement);
                            tasks.Add(new TaskNode(portal, AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value, TaskNodeType.Transition));
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
                                yield return Mouse.SetCursorPosHuman(tpConfirmation.GetClientRect().Center);
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
            else if (followTarget != null)
            {
                // Reset zone tracking when leader is found in this zone
                _lastKnownLeaderZone = "";
                _leaderZoneChangeTime = DateTime.MinValue;

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

                            // If we're close to the suspected portal location, look for a portal label
                            var distToPortal = Vector3.Distance(playerPos, followResult.WorldPosition);
                            if (distToPortal < AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value)
                            {
                                var transition = GetBestPortalLabel(leaderPartyElement);
                                if (transition != null && transition.ItemOnGround.DistancePlayer < 80)
                                {
                                    tasks.RemoveAll(t => t.Type == TaskNodeType.Movement);
                                    tasks.Add(new TaskNode(transition, 200, TaskNodeType.Transition));
                                }
                            }
                            break;
                    }
                }
                else
                {
                    // Pathfinding disabled: simple direct-follow fallback
                    var distanceToLeader = Vector3.Distance(playerPos, leaderPos);
                    if (distanceToLeader >= AreWeThereYet.Instance.Settings.AutoPilot.KeepWithinDistance.Value)
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
                                var movedSinceLastLog = _lastMoveDiagPos == Vector3.Zero
                                    ? 0f
                                    : Vector3.Distance(curPos, _lastMoveDiagPos);
                                _lastMoveDiagPos = curPos;

                                AreWeThereYet.Instance.LogMessage(
                                    $"[ATY-PF] MoveExec | player world({curPos.X:F0},{curPos.Y:F0}) | " +
                                    $"target world({currentTask.WorldPosition.X:F0},{currentTask.WorldPosition.Y:F0}) " +
                                    $"taskDist={taskDistance:F0} | screenClick=({screenPos.X:F0},{screenPos.Y:F0}) | " +
                                    $"movedSinceLastLog={movedSinceLastLog:F1}");
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

                        if (taskDistance <= AreWeThereYet.Instance.Settings.AutoPilot.Pathfinding.ReachedBounds.Value)
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
                            // Re-validate portal exists and is still valid before attempting to use it
                            if (currentTask.LabelOnGround?.Label?.IsValid != true ||
                                currentTask.LabelOnGround?.IsVisible != true ||
                                currentTask.LabelOnGround?.ItemOnGround == null)
                            {
                                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                                {
                                    AreWeThereYet.Instance.LogMessage("Portal became invalid - removing transition task, will re-evaluate in main loop");
                                }

                                tasks.RemoveAt(0);
                                yield return null;
                                continue;
                            }

                            // ---------------------------------------------------------------
                            // STEP 1: PATH TO THE TRANSITION.
                            // The click-to-move that a label click triggers uses the GAME'S
                            // navigation, which snags on walls (the "rubbing against a wall
                            // that wasn't at the transition" symptom). So if we aren't already
                            // within clicking range, walk toward the transition ourselves using
                            // the SAME breadcrumb + A* pathing that follows the leader - this
                            // finishes the leader's trail to the portal, routing around walls.
                            // Do NOT flag a transition yet - we haven't clicked anything.
                            // ---------------------------------------------------------------
                            var portalPos = currentTask.WorldPosition;
                            var distToPortal = currentTask.LabelOnGround.ItemOnGround.DistancePlayer;
                            var clickDistance = AreWeThereYet.Instance.Settings.AutoPilot.TransitionClickDistance.Value;
                            var pfEnabled = AreWeThereYet.Instance.Settings.AutoPilot.Pathfinding.Enabled.Value;

                            if (distToPortal > clickDistance)
                            {
                                // Ask the shared navigator for the next waypoint toward the
                                // portal (trail-follow enabled: the trail leads to it). Falls
                                // back to walking straight at the portal if pathfinding is off.
                                var moveTarget = portalPos;
                                if (pfEnabled)
                                {
                                    var nav = AreWeThereYet.Instance.leaderFollower.NavigateTo(
                                        AreWeThereYet.Instance.playerPosition, portalPos,
                                        allowTrailFollow: true, arriveWithin: clickDistance);

                                    // Idle == the navigator considers us arrived (in range + LOS);
                                    // fall through to the click. Otherwise walk to its waypoint.
                                    if (nav.Type != FollowResultType.Idle)
                                        moveTarget = nav.WorldPosition;
                                    else
                                        goto DoTransitionClick;
                                }

                                if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                                {
                                    AreWeThereYet.Instance.LogMessage($"Transition {distToPortal:F0} away (> {clickDistance}) - walking to it before clicking");
                                }

                                if (AreWeThereYet.Instance.Settings.AutoPilot.DashEnabled &&
                                    ShouldUseDash(moveTarget.WorldToGrid()))
                                {
                                    yield return Mouse.SetCursorPosHuman(Helper.WorldToValidScreenPosition(moveTarget));
                                    yield return new WaitTime(random.Next(25) + 30);
                                    Keyboard.KeyPress(AreWeThereYet.Instance.Settings.AutoPilot.DashKey);
                                    yield return new WaitTime(random.Next(25) + 30);
                                }
                                else
                                {
                                    yield return Mouse.SetCursorPosHuman(Helper.WorldToValidScreenPosition(moveTarget));
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
                            // STEP 2: CLICK THE LABEL.
                            // We're in range. Flag the transition (so AreaChange() spawns the
                            // grace period) and click.
                            // ---------------------------------------------------------------
                            currentTask.AttemptCount++;
                            _isTransitioning = true;
                            _transitioningStartTime = DateTime.Now;

                            if (AreWeThereYet.Instance.Settings.Debug.ShowDetailedDebug?.Value == true)
                            {
                                AreWeThereYet.Instance.LogMessage($"Clicking transition (attempt {currentTask.AttemptCount}/{AreWeThereYet.Instance.Settings.AutoPilot.MaxTransitionAttempts.Value})");
                            }

                            Keyboard.KeyUp(AreWeThereYet.Instance.Settings.AutoPilot.MoveKey);
                            yield return new WaitTime(60);
                            // Absolute-screen position (window offset applied) - a bare
                            // GetClientRect().Center is window-relative and lands off-window.
                            yield return Mouse.SetCursorPosAndLeftClickHuman(GetLabelClickPosition(currentTask.LabelOnGround), 100);

                            // ---------------------------------------------------------------
                            // STEP 3: WAIT FOR THE TRANSITION.
                            // A successful click starts a zone load; AreaChange() then fires,
                            // which clears the flag (via the grace period) and resets the task
                            // list. Poll for either signal up to the configured timeout.
                            // ---------------------------------------------------------------
                            var waited = 0;
                            var transitionWait = AreWeThereYet.Instance.Settings.AutoPilot.TransitionWaitTime.Value;
                            while (waited < transitionWait)
                            {
                                if (AreWeThereYet.Instance.GameController.IsLoading || !tasks.Contains(currentTask))
                                    break;
                                yield return new WaitTime(100);
                                waited += 100;
                            }

                            // Success: a load started or AreaChange() already cleared our task.
                            if (AreWeThereYet.Instance.GameController.IsLoading || !tasks.Contains(currentTask))
                            {
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

                            var fallbackLeader = GetLeaderPartyElement();
                            if (fallbackLeader != null)
                            {
                                // Flag the transition again: clicking the party TP button also
                                // triggers a zone load / AreaChange().
                                _isTransitioning = true;
                                _transitioningStartTime = DateTime.Now;

                                // A confirmation may already be up.
                                var tpConfirmPre = GetTpConfirmation();
                                if (tpConfirmPre != null)
                                {
                                    yield return Mouse.SetCursorPosHuman(tpConfirmPre.GetClientRect().Center);
                                    yield return new WaitTime(200);
                                    yield return Mouse.LeftClick();
                                    yield return new WaitTime(1000);
                                }

                                var fallbackTpButton = GetTpButton(fallbackLeader);
                                if (!fallbackTpButton.Equals(Vector2.Zero))
                                {
                                    yield return Mouse.SetCursorPosHuman(fallbackTpButton, false);
                                    yield return new WaitTime(200);
                                    yield return Mouse.LeftClick();
                                    yield return new WaitTime(500);

                                    // Accept the "travel to this player?" confirmation.
                                    var tpConfirmPost = GetTpConfirmation();
                                    if (tpConfirmPost != null)
                                    {
                                        yield return Mouse.SetCursorPosHuman(tpConfirmPost.GetClientRect().Center);
                                        yield return new WaitTime(200);
                                        yield return Mouse.LeftClick();
                                        yield return new WaitTime(500);
                                    }
                                }
                                else
                                {
                                    // No TP button available - let the flag self-heal so
                                    // detection resumes rather than staying wedged.
                                    _isTransitioning = false;
                                }
                            }

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
            var portalLabels =
                AreWeThereYet.Instance.GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels.Where(x =>
                    x != null && x.IsVisible && x.Label != null && x.Label.IsValid && x.Label.IsVisible &&
                    x.ItemOnGround != null &&
                    (x.ItemOnGround.Metadata.ToLower().Contains("areatransition") ||
                     x.ItemOnGround.Metadata.ToLower().Contains("portal"))).ToList();

            foreach (var portal in portalLabels)
            {
                AreWeThereYet.Instance.Graphics.DrawLine(portal.Label.GetClientRectCache.TopLeft, portal.Label.GetClientRectCache.TopRight, 2f, Color.Firebrick);
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
