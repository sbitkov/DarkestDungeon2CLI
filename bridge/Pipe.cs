using BepInEx;
using HarmonyLib;

using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;

using Assets.Code.Game;
using Assets.Code.Game.StageCoach;
using Assets.Code.Map;
using Assets.Code.Combat;
using Assets.Code.Combat.Events;
using Assets.Code.Actor;
using Assets.Code.Actor.Queries;
using Assets.Code.Actor.ActorController;
using Assets.Code.Actor.Events;
using Assets.Code.Affinity.Events;
using Assets.Code.Library;
using Assets.Code.Skill;
using Assets.Code.Skill.Events;
using Assets.Code.Skill.Queries;
using Assets.Code.Utils;
using Assets.Code.UI;
using Assets.Code.UI.Events;
using Assets.Code.Events;
using Assets.Code.Effect;
using Assets.Code.Token;
using Assets.Code.UI.Tooltips;
using Assets.Code.UI.Screens;
using Assets.Code.UI.Widgets;
using Assets.Code.UI.Managers;
using Assets.Code.Campaign;

using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        private sealed class BridgeRequest
        {
            public string Command;
            public string Response;
            public readonly ManualResetEventSlim Completed =
                new ManualResetEventSlim(false);
        }

        private readonly ConcurrentQueue<BridgeRequest> _requests =
            new ConcurrentQueue<BridgeRequest>();
            
        private readonly ConcurrentQueue<string> _outgoingMessages =
            new ConcurrentQueue<string>();

        private void QueueOutgoingMessage(JObject message)
        {
            AugmentStagecoachReadyWithEquipment(message);

            _outgoingMessages.Enqueue(
                message.ToString(Newtonsoft.Json.Formatting.None));
        }

        private void OnApplicationQuit()
        {
            _pipeRunning = false;

            try
            {
                if (_activePipe != null && !_activePipe.IsConnected)
                {
                    using (NamedPipeClientStream wakeup = new NamedPipeClientStream(
                        ".",
                        "dd2-agent",
                        PipeDirection.Out))
                    {
                        wakeup.Connect(500);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    $"SHUTDOWN: pipe stop failed: {ex.Message}");
            }
        }

        private void ProcessNextRequest()
        {
            BridgeRequest request;

            if (!_requests.TryDequeue(out request))
            {
                return;
            }

            try
            {
                HandleRequest(request);

                if (request.Response != null &&
                    request.Response.StartsWith("ERROR:"))
                {
                    string errorMessage =
                        request.Response.Substring(
                            "ERROR:".Length).Trim();

                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "command_rejected",
                            ["message"] = errorMessage
                        });
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    $"COMMAND execution failed: {ex}");

                QueueOutgoingMessage(
                    new JObject
                    {
                        ["type"] = "error",
                        ["code"] = "command_exception",
                        ["message"] = ex.Message
                    });
            }
            finally
            {
                request.Completed.Set();
            }
        }

        private void HandleRequest(BridgeRequest request)
        {
            if (request.Command == "battle-json")
            {
                HandleBattleJson(request);
            }
            else if (request.Command == "current-actor-json")
            {
                HandleCurrentActorJson(request);
            }
            else if (request.Command == "team-guids")
            {
                HandleTeamGuids(request);
            }
            else if (request.Command == "combat-actors-json")
            {
                HandleCombatActorsJson(request);
            }
            else if (request.Command == "current-actor-stats")
            {
                HandleCurrentActorStats(request);
            }
            else if (request.Command == "current-actor-resists")
            {
                HandleCurrentActorResists(request);
            }
            else if (request.Command == "current-available-actions")
            {
                HandleCurrentAvailableActions(request);
            }
            else if (request.Command == "current-state")
            {
                HandleCurrentState(request);
            }
            else if (request.Command == "node-entry-enter")
            {
                HandleNodeEntryEnter(request);
            }
            else if (request.Command == "altar-of-hope-leave")
            {
                HandleAltarOfHopeLeave(request);
            }
            else if (request.Command.StartsWith("boss-select-select "))
            {
                HandleBossSelect(
                    request,
                    request.Command.Substring("boss-select-select ".Length));
            }
            else if (request.Command.StartsWith("hero-management-inspect "))
            {
                HandleHeroManagementInspect(
                    request,
                    request.Command.Substring("hero-management-inspect ".Length));
            }
            else if (request.Command.StartsWith("hero-select-set-path "))
            {
                HandleHeroSetPath(
                    request,
                    request.Command.Substring("hero-select-set-path ".Length));
            }
            else if (request.Command.StartsWith("hero-management-set-skills "))
            {
                HandleHeroManagementSetSkills(
                    request,
                    request.Command.Substring("hero-management-set-skills ".Length));
            }
            else if (request.Command.StartsWith("hero-select-set-party-member "))
            {
                HandleHeroSetPartyMember(
                    request,
                    request.Command.Substring("hero-select-set-party-member ".Length));
            }
            else if (request.Command.StartsWith("hero-select-clear-party-slot "))
            {
                HandleHeroClearPartySlot(
                    request,
                    request.Command.Substring("hero-select-clear-party-slot ".Length));
            }
            else if (request.Command == "hero-select-confirm-party")
            {
                HandleHeroConfirmParty(request);
            }
            else if (request.Command == "confirmation-confirm")
            {
                HandleConfirmationChoice(request, true);
            }
            else if (request.Command == "confirmation-cancel")
            {
                HandleConfirmationChoice(request, false);
            }
            else if (request.Command == "inn-embark")
            {
                HandleInnEmbark(request);
            }
            else if (request.Command.StartsWith("embark-reveal-relationship "))
            {
                HandleEmbarkRevealRelationship(
                    request,
                    request.Command.Substring(
                        "embark-reveal-relationship ".Length));
            }
            else if (request.Command == "embark-continue")
            {
                HandleEmbarkContinue(request);
            }
            else if (request.Command.StartsWith("post-combat-loot-take-item "))
            {
                HandlePostCombatLootTakeItem(
                    request,
                    request.Command.Substring(
                        "post-combat-loot-take-item ".Length));
            }
            else if (request.Command == "post-combat-loot-take-all")
            {
                HandlePostCombatLootTakeAll(request);
            }
            else if (request.Command == "inventory-state")
            {
                HandleInventoryState(request);
            }
            else if (request.Command == "map-state")
            {
                HandleMapState(request);
            }
            else if (request.Command.StartsWith("inventory-discard "))
            {
                HandleInventoryDiscard(
                    request,
                    request.Command.Substring("inventory-discard ".Length));
            }
            else if (request.Command == "inventory-close")
            {
                HandleInventoryClose(request);
            }
            else if (request.Command == "inn-travelogue-state")
            {
                HandleInnTravelogueState(request);
            }
            else if (request.Command == "inn-end-expedition-state")
            {
                HandleInnEndExpeditionState(request);
            }
            else if (request.Command == "inn-end-expedition-take-hope")
            {
                HandleInnEndExpeditionTakeHope(request);
            }
            else if (request.Command == "inn-end-expedition-back")
            {
                HandleInnEndExpeditionBack(request);
            }
            else if (request.Command == "inn-store-state")
            {
                HandleInnStoreState(request);
            }
            else if (request.Command.StartsWith("inn-store-buy "))
            {
                HandleInnStoreBuy(
                    request,
                    request.Command.Substring("inn-store-buy ".Length));
            }
            else if (request.Command.StartsWith("inn-store-discard "))
            {
                HandleInnStoreDiscard(
                    request,
                    request.Command.Substring("inn-store-discard ".Length));
            }
            else if (request.Command == "inn-store-back")
            {
                HandleInnStoreBack(request);
            }
            else if (request.Command == "inn-mastery-state")
            {
                HandleInnMasteryState(request);
            }
            else if (request.Command.StartsWith("inn-mastery-upgrade-skill "))
            {
                HandleInnMasteryUpgradeSkill(
                    request,
                    request.Command.Substring(
                        "inn-mastery-upgrade-skill ".Length));
            }
            else if (request.Command == "inn-mastery-back")
            {
                HandleInnMasteryBack(request);
            }
            else if (request.Command == "inn-wainwright-state")
            {
                HandleInnWainwrightState(request);
            }
            else if (request.Command.StartsWith("inn-wainwright-equip "))
            {
                HandleInnWainwrightEquip(
                    request,
                    request.Command.Substring(
                        "inn-wainwright-equip ".Length));
            }
            else if (request.Command.StartsWith("inn-wainwright-unequip "))
            {
                HandleInnWainwrightUnequip(
                    request,
                    request.Command.Substring(
                        "inn-wainwright-unequip ".Length));
            }
            else if (request.Command == "inn-wainwright-back")
            {
                HandleInnWainwrightBack(request);
            }
            else if (request.Command == "inn-route-state")
            {
                HandleInnRouteChoiceState(request);
            }
            else if (request.Command.StartsWith("inn-route-choose "))
            {
                HandleInnRouteChoose(
                    request,
                    request.Command.Substring("inn-route-choose ".Length));
            }
            else if (request.Command == "inn-route-back")
            {
                HandleInnRouteBack(request);
            }
            else if (request.Command == "inn-items-state")
            {
                HandleInnItemsState(request);
            }
            else if (request.Command.StartsWith("inn-item-use "))
            {
                HandleInnItemUse(request, request.Command.Substring("inn-item-use ".Length));
            }
            else if (request.Command == "inn-items-back")
            {
                HandleInnItemsBack(request);
            }
            else if (request.Command == "semantic-continue")
            {
                if (!TryHandleInnTravelogueContinue(request))
                {
                    HandleSemanticContinue(request);
                }
            }
            else if (request.Command == "expedition-results-continue")
            {
                HandleExpeditionResultsContinue(request);
            }
            else if (request.Command == "stagecoach-start-driving")
            {
                HandleStagecoachStartDriving(request);
            }
            else if (request.Command == "stagecoach-stop-driving")
            {
                HandleStagecoachStopDriving(request);
            }
            else if (request.Command.StartsWith("route-choice-select "))
            {
                HandleRouteChoice(
                    request,
                    request.Command.Substring(
                        "route-choice-select ".Length));
            }
            else if (request.Command.StartsWith("story-choice-select "))
            {
                HandleStoryChoice(
                    request,
                    request.Command.Substring(
                        "story-choice-select ".Length));
            }
            else if (request.Command.StartsWith("stagecoach-move-party-member "))
            {
                HandleStagecoachMovePartyMember(
                    request,
                    request.Command.Substring(
                        "stagecoach-move-party-member ".Length));
            }
            else if (request.Command == "equipment-state")
            {
                HandleEquipmentState(request);
            }
            else if (request.Command.StartsWith("equipment-equip-trinket "))
            {
                HandleEquipTrinket(
                    request,
                    request.Command.Substring(
                        "equipment-equip-trinket ".Length));
            }
            else if (request.Command.StartsWith("equipment-unequip-trinket "))
            {
                HandleUnequipTrinket(
                    request,
                    request.Command.Substring(
                        "equipment-unequip-trinket ".Length));
            }
            else if (request.Command.StartsWith("equipment-equip-combat-item "))
            {
                HandleEquipCombatItem(
                    request,
                    request.Command.Substring(
                        "equipment-equip-combat-item ".Length));
            }
            else if (request.Command.StartsWith("equipment-unequip-combat-item "))
            {
                HandleUnequipCombatItem(
                    request,
                    request.Command.Substring(
                        "equipment-unequip-combat-item ".Length));
            }
            else if (request.Command == "hospital-state")
            {
                HandleHospitalState(request);
            }
            else if (request.Command.StartsWith("hospital-select-actor "))
            {
                HandleHospitalSelectActor(
                    request,
                    request.Command.Substring("hospital-select-actor ".Length));
            }
            else if (request.Command.StartsWith("hospital-tab "))
            {
                HandleHospitalTab(
                    request,
                    request.Command.Substring("hospital-tab ".Length));
            }
            else if (request.Command.StartsWith("hospital-select-quirk "))
            {
                HandleHospitalSelectQuirk(
                    request,
                    request.Command.Substring("hospital-select-quirk ".Length));
            }
            else if (request.Command == "hospital-minor-heal")
            {
                HandleHospitalMinorHeal(request);
            }
            else if (request.Command == "hospital-full-heal")
            {
                HandleHospitalFullHeal(request);
            }
            else if (request.Command == "hospital-cure-disease")
            {
                HandleHospitalCureDisease(request);
            }
            else if (request.Command == "hospital-lock-quirk")
            {
                HandleHospitalLockQuirk(request);
            }
            else if (request.Command == "hospital-remove-quirk")
            {
                HandleHospitalRemoveQuirk(request);
            }
            else if (request.Command.StartsWith("hospital-buy "))
            {
                HandleHospitalBuy(
                    request,
                    request.Command.Substring("hospital-buy ".Length));
            }
            else if (request.Command == "hospital-leave")
            {
                HandleHospitalLeave(request);
            }
            else if (request.Command == "store-state")
            {
                HandleStoreState(request);
            }
            else if (request.Command.StartsWith("store-buy "))
            {
                HandleStoreBuy(
                    request,
                    request.Command.Substring("store-buy ".Length));
            }
            else if (request.Command == "store-leave")
            {
                HandleStoreLeave(request);
            }
            else if (request.Command == "main-menu-new-expedition")
            {
                HandleMainMenuAction(request, "new_expedition");
            }
            else if (request.Command == "main-menu-continue-expedition")
            {
                HandleMainMenuAction(request, "continue_expedition");
            }
            else if (request.Command.StartsWith(
                "test-execute-skill "))
            {
                HandleTestExecuteSkill(request);
            }
            else
            {
                request.Response = "ERROR: unknown command";
            }
        }

        private void PipeServerLoop()
        {
            while (_pipeRunning)
            {
                try
                {
                    // CodexPipeAclV3
                    // v2 proved that Mono accepts the PipeSecurity overload but
                    // silently leaves the Windows pipe with the default DACL.
                    // v3 therefore asks the SERVER HANDLE for WRITE_DAC
                    // (ChangePermissions), then edits the DACL on that actual
                    // handle and verifies the resulting descriptor before
                    // WaitForConnection().
                    const string RemiliaSid =
                        "S-1-5-21-926465663-700626426-2787390607-1001";
                    const string CodexSandboxSid =
                        "S-1-5-21-926465663-700626426-2787390607-1003";

                    Logger.LogInfo(
                        "PIPE ACL v3: preparing requested security descriptor");

                    PipeSecurity requestedSecurity = new PipeSecurity();

                    SecurityIdentifier remiliaUser =
                        new SecurityIdentifier(RemiliaSid);

                    SecurityIdentifier codexSandboxUser =
                        new SecurityIdentifier(CodexSandboxSid);

                    requestedSecurity.AddAccessRule(
                        new PipeAccessRule(
                            remiliaUser,
                            PipeAccessRights.FullControl,
                            AccessControlType.Allow));

                    requestedSecurity.AddAccessRule(
                        new PipeAccessRule(
                            codexSandboxUser,
                            PipeAccessRights.ReadWrite,
                            AccessControlType.Allow));

                    Logger.LogInfo(
                        "PIPE ACL v3: creating server handle with ChangePermissions");

                    _activePipe = new NamedPipeServerStream(
                        "dd2-agent",
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        0,
                        0,
                        requestedSecurity,
                        HandleInheritability.None,
                        PipeAccessRights.ChangePermissions);

                    Logger.LogInfo(
                        "PIPE ACL v3: server created; reading actual DACL");

                    // Preserve whatever Windows/Mono actually put on the pipe
                    // (SYSTEM, Administrators, etc.) and add only the two rules
                    // we need. This avoids replacing the default ACL wholesale.
                    PipeSecurity effectiveSecurity =
                        _activePipe.GetAccessControl();

                    Logger.LogInfo(
                        "PIPE ACL v3: adding explicit Remilia/Codex rules to actual DACL");

                    effectiveSecurity.AddAccessRule(
                        new PipeAccessRule(
                            remiliaUser,
                            PipeAccessRights.FullControl,
                            AccessControlType.Allow));

                    effectiveSecurity.AddAccessRule(
                        new PipeAccessRule(
                            codexSandboxUser,
                            PipeAccessRights.ReadWrite,
                            AccessControlType.Allow));

                    Logger.LogInfo(
                        "PIPE ACL v3: applying actual DACL via server handle");

                    _activePipe.SetAccessControl(effectiveSecurity);

                    Logger.LogInfo(
                        "PIPE ACL v3: DACL applied; verifying");

                    PipeSecurity verifiedSecurity =
                        _activePipe.GetAccessControl();

                    string verifiedSddl =
                        verifiedSecurity.GetSecurityDescriptorSddlForm(
                            AccessControlSections.Access);

                    Logger.LogInfo(
                        $"PIPE ACL v3: effective DACL={verifiedSddl}");

                    bool codexReadWritePresent = false;

                    AuthorizationRuleCollection verifiedRules =
                        verifiedSecurity.GetAccessRules(
                            true,
                            true,
                            typeof(SecurityIdentifier));

                    foreach (AuthorizationRule rule in verifiedRules)
                    {
                        PipeAccessRule pipeRule =
                            rule as PipeAccessRule;

                        if (pipeRule == null ||
                            pipeRule.AccessControlType !=
                                AccessControlType.Allow)
                        {
                            continue;
                        }

                        SecurityIdentifier ruleSid =
                            pipeRule.IdentityReference
                                as SecurityIdentifier;

                        if (ruleSid != null &&
                            ruleSid.Value == CodexSandboxSid &&
                            (pipeRule.PipeAccessRights &
                                PipeAccessRights.ReadWrite) ==
                                PipeAccessRights.ReadWrite)
                        {
                            codexReadWritePresent = true;
                            break;
                        }
                    }

                    if (!codexReadWritePresent)
                    {
                        throw new InvalidOperationException(
                            "PIPE ACL v3 verification failed: " +
                            "Codex sandbox ReadWrite ACE is absent");
                    }

                    Logger.LogInfo(
                        "PIPE ACL v3: Codex sandbox ReadWrite ACE verified");


                    Logger.LogInfo("PIPE: waiting for agent host");

                    _activePipe.WaitForConnection();

                    if (!_pipeRunning)
                    {
                        break;
                    }

                    Logger.LogInfo("PIPE: agent host connected");

                    // A new transport session must not inherit messages that
                    // were queued for a previous disconnected host.
                    string staleOutgoing;
                    int staleCount = 0;

                    while (_outgoingMessages.TryDequeue(out staleOutgoing))
                    {
                        staleCount++;
                    }

                    if (staleCount > 0)
                    {
                        Logger.LogInfo(
                            $"PIPE: discarded {staleCount} stale outgoing message(s) on reconnect");
                    }

                    // This connection has not seen the current boundary,
                    // even if a previous host connection had.
                    _readySentForCurrentTurnSelect = false;
                    _mainMenuReadySent = false;
                    _nodeEntryReadySent = false;
                    _altarReadySent = false;
                    _routeChoiceReadySent = false;
                    _storyChoiceReadySent = false;
                    _storeReadySent = false;
                    _doomResetReadySent = false;
                    _hospitalReadySent = false;
                    _hostReadyCatchupRequested = true;

                    using (StreamReader reader =
                        new StreamReader(_activePipe))
                    using (StreamWriter writer =
                        new StreamWriter(_activePipe))
                    {
                        writer.AutoFlush = true;

                        // Transport handshake: report the version of the bridge
                        // that is actually loaded in the game before any READY
                        // catch-up can reach the newly connected host.
                        writer.WriteLine(
                            new JObject
                            {
                                ["type"] = "bridge_info",
                                ["version"] = BridgeVersion
                            }.ToString(Newtonsoft.Json.Formatting.None));

                        Logger.LogInfo(
                            $"PIPE: reported DD2Bridge version {BridgeVersion} to host");

                        var readTask = reader.ReadLineAsync();

                        while (_pipeRunning && _activePipe.IsConnected)
                        {
                            string outgoing;

                            while (_outgoingMessages.TryDequeue(out outgoing))
                            {
                                writer.WriteLine(outgoing);
                            }

                            if (readTask.IsCompleted)
                            {
                                string line =
                                    readTask.GetAwaiter().GetResult();

                                if (line == null)
                                {
                                    break;
                                }

                                HandleIncomingPipeMessage(line);

                                readTask = reader.ReadLineAsync();
                            }

                            Thread.Sleep(10);
                        }
                    }

                    Logger.LogInfo("PIPE: agent host disconnected");
                }
                catch (ObjectDisposedException)
                {
                    if (_pipeRunning)
                    {
                        Logger.LogError(
                            "Pipe was unexpectedly disposed.");
                    }
                }
                catch (IOException ex)
                {
                    if (_pipeRunning)
                    {
                        Logger.LogError(
                            $"Pipe I/O failed: {ex}");
                    }
                }
                catch (Exception ex)
                {
                    if (_pipeRunning)
                    {
                        Logger.LogError(
                            $"Pipe session failed: {ex}");

                        // Never spin the game in a hot exception loop if pipe
                        // creation/security fails again.
                        Thread.Sleep(1000);
                    }
                }
                finally
                {
                    if (_activePipe != null)
                    {
                        _activePipe.Dispose();
                    }

                    _activePipe = null;
                }
            }
        }
        
        private void ProcessHostReadyCatchup()
        {
            if (!_hostReadyCatchupRequested)
            {
                return;
            }

            _hostReadyCatchupRequested = false;

            // Catch-up uses the same foreground ownership rule as passive
            // discovery. Never rediscover dormant DRIVING UI through a combat.
            if (IsCombatForegroundActive())
            {
                ResetStagecoachReadyBoundary();

                if (_readySentForCurrentTurnSelect)
                {
                    Logger.LogInfo(
                        "READY catch-up suppressed: current turn-select boundary was already announced.");
                    return;
                }

                GameModeMgr combatModeMgr = GameModeMgr.Instance;
                CombatBhv combat = CombatBhv.Instance;

                if (combatModeMgr == null ||
                    combatModeMgr.IsChangingState() ||
                    combat == null ||
                    combat.CurrentBattleState != BattleState.IN_TURN_SELECT)
                {
                    return;
                }

                ActorInstance currentCombatActor =
                    combat.GetCurrentActor();

                if (currentCombatActor == null ||
                    !IsFriendlyCombatActor(currentCombatActor.ActorGuid))
                {
                    return;
                }

                JObject combatReady =
                    BuildCombatReadyMessage();

                if (combatReady == null)
                {
                    return;
                }

                _readySentForCurrentTurnSelect = true;

                Logger.LogInfo(
                    $"READY catch-up: actor={combatReady["current_actor_guid"]}");

                QueueOutgoingMessage(combatReady);
                return;
            }

            if (TryProcessEmbarkRelationshipsCatchup())
            {
                return;
            }

            if (TryProcessInventoryCatchup())
            {
                return;
            }

            if (TryProcessInnTravelogueCatchup())
            {
                return;
            }

            if (TryProcessDoomResetCatchup())
            {
                return;
            }
            
            if (TryProcessPostCombatLootCatchup())
            {
                return;
            }
            
            if (TryProcessHospitalCatchup())
            {
                return;
            }

            if (TryProcessStoreCatchup())
            {
                return;
            }

            // In INN, a supported native confirmation is foreground and must
            // outrank the generic inn catch-up (notably take_hope).
            if (GameModeMgr.CurrentMode == GameModeType.INN)
            {
                ConfirmationDialogBhv innConfirmation;
                JObject innConfirmationReady;
                if (TryGetSupportedActiveConfirmation(
                        out innConfirmation,
                        out innConfirmationReady))
                {
                    _confirmationReadySent = true;
                    Logger.LogInfo(
                        $"READY catch-up: {innConfirmationReady["frontend_state"]}");
                    QueueOutgoingMessage(innConfirmationReady);
                    return;
                }
            }

            if (TryProcessInnCatchup())
            {
                return;
            }

            ConfirmationDialogBhv activeConfirmation;
            JObject confirmationReady;
            if (TryGetSupportedActiveConfirmation(
                    out activeConfirmation,
                    out confirmationReady))
            {
                _confirmationReadySent = true;
                Logger.LogInfo(
                    $"READY catch-up: {confirmationReady["frontend_state"]}");
                QueueOutgoingMessage(confirmationReady);
                return;
            }

            GameOverScreenWidgetBhv results =
                GetStableExpeditionResultsScreen();
            if (results != null)
            {
                _expeditionResultsReadySent = true;
                Logger.LogInfo("READY catch-up: expedition_results");
                QueueOutgoingMessage(BuildExpeditionResultsReadyMessage());
                return;
            }

            if (TryProcessStoryChoiceCatchup())
            {
                return;
            }

            UiScreenBhv nodeScreen;
            EnterNodeScreenWidgetBhv nodeWidget;
            EnterNodeScreenPushParams nodeParams;
            if (TryGetStableNodeEntry(
                out nodeScreen,
                out nodeWidget,
                out nodeParams))
            {
                _nodeEntryReadySent = true;
                Logger.LogInfo(
                    $"READY catch-up: node_entry " +
                    $"{NodeTypeFromButtonKey(nodeParams.m_buttonString)}");
                QueueOutgoingMessage(BuildNodeEntryReadyMessage(nodeParams));
                return;
            }

            AltarOfHopeUiBhv altar = GetStableAltarOfHopeUi();
            if (altar != null)
            {
                _altarReadySent = true;
                Logger.LogInfo("READY catch-up: altar_of_hope");
                QueueOutgoingMessage(BuildAltarOfHopeReadyMessage());
                return;
            }

            BossSelectWidgetBhv bossSelect = GetStableBossSelectWidget();
            if (bossSelect != null)
            {
                JObject bossReady = BuildBossSelectReadyMessage(bossSelect);
                JArray actions = bossReady["available_actions"] as JArray;
                if (actions != null && actions.Count > 0)
                {
                    _bossSelectReadySent = true;
                    Logger.LogInfo(
                        $"READY catch-up: boss_select choices={actions.Count}");
                    QueueOutgoingMessage(bossReady);
                    return;
                }
            }

            UnityEngine.MonoBehaviour heroSelect = GetStableHeroSelectBhv();
            if (heroSelect != null)
            {
                JObject heroReady = BuildHeroSelectReadyMessage(heroSelect);
                JArray heroes = heroReady["available_heroes"] as JArray;
                if (heroes != null && heroes.Count > 0)
                {
                    _heroSelectReadySent = true;
                    Logger.LogInfo(
                        $"READY catch-up: hero_select heroes={heroes.Count}");
                    QueueOutgoingMessage(heroReady);
                    return;
                }
            }

            if (TryProcessRouteChoiceCatchup())
            {
                return;
            }

            StageCoachVehicleControlBhv stagecoach =
                GetStableStagecoachReadyVehicle();
            if (stagecoach != null)
            {
                _stagecoachReadySent = true;
                Logger.LogInfo("READY catch-up: stagecoach_ready");
                QueueOutgoingMessage(BuildStagecoachReadyMessage());
                return;
            }

            MainMenuUiScreenBhv mainMenu = GetStableMainMenuScreen();
            if (mainMenu != null)
            {
                _mainMenuReadySent = true;
                Logger.LogInfo("READY catch-up: main_menu");
                QueueOutgoingMessage(BuildMainMenuReadyMessage());
                return;
            }

            if (_readySentForCurrentTurnSelect)
            {
                Logger.LogInfo(
                    "READY catch-up suppressed: current turn-select boundary was already announced.");
                return;
            }

            if (GameModeMgr.CurrentMode != GameModeType.COMBAT)
            {
                return;
            }

            if (GameModeMgr.Instance.IsChangingState())
            {
                return;
            }

            if (CombatBhv.Instance == null)
            {
                return;
            }

            if (CombatBhv.Instance.CurrentBattleState !=
                BattleState.IN_TURN_SELECT)
            {
                return;
            }

            ActorInstance currentActor =
                CombatBhv.Instance.GetCurrentActor();

            if (currentActor == null ||
                !IsFriendlyCombatActor(currentActor.ActorGuid))
            {
                return;
            }

            JObject ready = BuildCombatReadyMessage();

            if (ready == null)
            {
                return;
            }

            _readySentForCurrentTurnSelect = true;

            Logger.LogInfo(
                $"READY catch-up: actor={ready["current_actor_guid"]}");

            QueueOutgoingMessage(ready);
        }
        
        private void HandleIncomingPipeMessage(string line)
        {
            //Logger.LogInfo($"PIPE RX: {line}");
            
            JObject message;

            try
            {
                message = JObject.Parse(line);
            }
            catch (Exception)
            {
                QueueOutgoingMessage(
                    new JObject
                    {
                        ["type"] = "error",
                        ["code"] = "invalid_json",
                        ["message"] = "Message is not valid JSON"
                    });

                return;
            }

            string type = (string)message["type"];

            if (type != "command")
            {
                QueueOutgoingMessage(
                    new JObject
                    {
                        ["type"] = "error",
                        ["code"] = "invalid_message_type",
                        ["message"] =
                            "Client messages must have type=command"
                    });

                return;
            }

            string action = (string)message["action"];

            if (action == "map")
            {
                Logger.LogInfo("COMMAND driving: map");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "map-state"
                    });
                return;
            }

            if (action == "state")
            {
                Logger.LogInfo("COMMAND state");

                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "current-state"
                    });

                return;
            }

            if (action == "enter_node")
            {
                Logger.LogInfo("COMMAND node_entry: enter_node");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "node-entry-enter"
                    });
                return;
            }

            if (action == "leave_altar")
            {
                Logger.LogInfo("COMMAND altar_of_hope: leave_altar");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "altar-of-hope-leave"
                    });
                return;
            }

            if (action == "select_boss")
            {
                JToken bossIdToken = message["boss_id"];
                string bossId = bossIdToken == null
                    ? null
                    : bossIdToken.Value<string>();

                if (string.IsNullOrEmpty(bossId))
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] = "select_boss requires boss_id"
                        });
                    return;
                }

                Logger.LogInfo(
                    $"COMMAND boss_select: select_boss boss_id='{bossId}'");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "boss-select-select " + bossId
                    });
                return;
            }

            if (action == "inspect_hero")
            {
                JToken actorGuidToken = message["actor_guid"];
                uint actorGuid;

                if (actorGuidToken == null ||
                    !uint.TryParse(actorGuidToken.ToString(), out actorGuid) ||
                    actorGuid == 0u)
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] = "inspect_hero requires actor_guid"
                        });
                    return;
                }

                Logger.LogInfo(
                    $"COMMAND hero_management: inspect_hero actor={actorGuid}");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "hero-management-inspect " + actorGuid
                    });
                return;
            }

            if (action == "confirm_party")
            {
                Logger.LogInfo(
                    "COMMAND hero_select: confirm_party");

                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "hero-select-confirm-party"
                    });
                return;
            }

            if (action == "confirm")
            {
                Logger.LogInfo("COMMAND confirmation: confirm");

                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "confirmation-confirm"
                    });
                return;
            }

            if (action == "cancel")
            {
                Logger.LogInfo("COMMAND confirmation: cancel");

                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "confirmation-cancel"
                    });
                return;
            }

            if (action == "set_party_member")
            {
                JToken slotToken = message["slot"];
                JToken actorGuidToken = message["actor_guid"];
                int slotIndex;
                uint actorGuid;

                if (slotToken == null ||
                    actorGuidToken == null ||
                    !int.TryParse(slotToken.ToString(), out slotIndex) ||
                    !uint.TryParse(actorGuidToken.ToString(), out actorGuid) ||
                    actorGuid == 0u)
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] =
                                "set_party_member requires slot and actor_guid"
                        });
                    return;
                }

                Logger.LogInfo(
                    $"COMMAND hero_select: set_party_member slot={slotIndex} actor={actorGuid}");

                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command =
                            "hero-select-set-party-member " +
                            slotIndex +
                            " " +
                            actorGuid
                    });
                return;
            }

            if (action == "clear_party_slot")
            {
                JToken slotToken = message["slot"];
                int slotIndex;

                if (slotToken == null ||
                    !int.TryParse(slotToken.ToString(), out slotIndex))
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] =
                                "clear_party_slot requires slot"
                        });
                    return;
                }

                Logger.LogInfo(
                    $"COMMAND hero_select: clear_party_slot slot={slotIndex}");

                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command =
                            "hero-select-clear-party-slot " +
                            slotIndex
                    });
                return;
            }

            if (action == "set_hero_path")
            {
                JToken actorGuidToken = message["actor_guid"];
                JToken pathIdToken = message["path_id"];
                uint actorGuid;
                string pathId =
                    pathIdToken == null
                        ? null
                        : pathIdToken.Value<string>();

                if (actorGuidToken == null ||
                    !uint.TryParse(actorGuidToken.ToString(), out actorGuid) ||
                    actorGuid == 0u ||
                    string.IsNullOrEmpty(pathId))
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] =
                                "set_hero_path requires actor_guid and path_id"
                        });
                    return;
                }

                Logger.LogInfo(
                    $"COMMAND hero_select: set_hero_path actor={actorGuid} path='{pathId}'");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command =
                            "hero-select-set-path " +
                            actorGuid +
                            " " +
                            pathId
                    });
                return;
            }

            if (action == "set_hero_skills")
            {
                JToken actorGuidToken = message["actor_guid"];
                JArray skillIdsToken = message["skill_ids"] as JArray;
                uint actorGuid;

                if (actorGuidToken == null ||
                    !uint.TryParse(actorGuidToken.ToString(), out actorGuid) ||
                    actorGuid == 0u ||
                    skillIdsToken == null)
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] =
                                "set_hero_skills requires actor_guid and skill_ids array"
                        });
                    return;
                }

                List<string> skillIds = new List<string>();
                foreach (JToken token in skillIdsToken)
                {
                    string requestedSkillId = token.Value<string>();
                    if (string.IsNullOrEmpty(requestedSkillId))
                    {
                        QueueOutgoingMessage(
                            new JObject
                            {
                                ["type"] = "error",
                                ["code"] = "invalid_command",
                                ["message"] =
                                    "set_hero_skills skill_ids must contain non-empty strings"
                            });
                        return;
                    }

                    skillIds.Add(requestedSkillId);
                }

                Logger.LogInfo(
                    $"COMMAND hero_management: set_hero_skills actor={actorGuid} count={skillIds.Count}");

                string command =
                    "hero-management-set-skills " +
                    actorGuid;

                if (skillIds.Count > 0)
                {
                    command += " " + string.Join(" ", skillIds.ToArray());
                }

                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = command
                    });
                return;
            }

            if (action == "hospital_state")
            {
                Logger.LogInfo("COMMAND hospital: hospital_state");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "hospital-state"
                    });
                return;
            }

            if (action == "select_hospital_actor")
            {
                JToken actorToken = message["actor_guid"];
                uint actorGuid;
                if (actorToken == null ||
                    !uint.TryParse(actorToken.ToString(), out actorGuid) ||
                    actorGuid == 0u)
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] =
                                "select_hospital_actor requires actor_guid"
                        });
                    return;
                }

                Logger.LogInfo(
                    $"COMMAND hospital: select_actor actor={actorGuid}");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "hospital-select-actor " + actorGuid
                    });
                return;
            }

            if (action == "hospital_tab")
            {
                string tab = (string)message["tab"];
                if (string.IsNullOrEmpty(tab))
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] = "hospital_tab requires tab"
                        });
                    return;
                }

                Logger.LogInfo($"COMMAND hospital: tab '{tab}'");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "hospital-tab " + tab
                    });
                return;
            }

            if (action == "select_hospital_quirk")
            {
                string quirkId = (string)message["quirk_id"];
                if (string.IsNullOrEmpty(quirkId))
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] =
                                "select_hospital_quirk requires quirk_id"
                        });
                    return;
                }

                Logger.LogInfo(
                    $"COMMAND hospital: select_quirk '{quirkId}'");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "hospital-select-quirk " + quirkId
                    });
                return;
            }

            if (action == "hospital_minor_heal")
            {
                _requests.Enqueue(
                    new BridgeRequest { Command = "hospital-minor-heal" });
                return;
            }

            if (action == "hospital_full_heal")
            {
                _requests.Enqueue(
                    new BridgeRequest { Command = "hospital-full-heal" });
                return;
            }

            if (action == "hospital_cure_disease")
            {
                _requests.Enqueue(
                    new BridgeRequest { Command = "hospital-cure-disease" });
                return;
            }

            if (action == "hospital_lock_quirk")
            {
                _requests.Enqueue(
                    new BridgeRequest { Command = "hospital-lock-quirk" });
                return;
            }

            if (action == "hospital_remove_quirk")
            {
                _requests.Enqueue(
                    new BridgeRequest { Command = "hospital-remove-quirk" });
                return;
            }

            if (action == "hospital_buy")
            {
                JToken slotToken = message["slot"];
                int slot;
                if (slotToken == null ||
                    !int.TryParse(slotToken.ToString(), out slot) ||
                    slot < 0)
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] =
                                "hospital_buy requires a non-negative slot"
                        });
                    return;
                }

                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "hospital-buy " + slot
                    });
                return;
            }

            if (action == "leave_hospital")
            {
                _requests.Enqueue(
                    new BridgeRequest { Command = "hospital-leave" });
                return;
            }

            if (action == "store_state")
            {
                Logger.LogInfo("COMMAND store: store_state");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "store-state"
                    });
                return;
            }

            if (action == "buy")
            {
                JToken slotToken = message["slot"];
                int slot;

                if (slotToken == null ||
                    !int.TryParse(slotToken.ToString(), out slot) ||
                    slot < 0)
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] = "buy requires a non-negative slot"
                        });
                    return;
                }

                Logger.LogInfo($"COMMAND store: buy slot={slot}");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "store-buy " + slot
                    });
                return;
            }

            if (action == "leave")
            {
                Logger.LogInfo("COMMAND store: leave");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "store-leave"
                    });
                return;
            }

            if (action == "take_item")
            {
                JToken slotToken = message["slot"];
                int slot;

                if (slotToken == null ||
                    !int.TryParse(slotToken.ToString(), out slot) ||
                    slot < 0)
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] = "take_item requires a non-negative slot"
                        });
                    return;
                }

                Logger.LogInfo(
                    $"COMMAND post_combat_loot: take_item slot={slot}");

                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "post-combat-loot-take-item " + slot
                    });
                return;
            }

            if (action == "take_all")
            {
                Logger.LogInfo("COMMAND post_combat_loot: take_all");

                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "post-combat-loot-take-all"
                    });
                return;
            }

            if (action == "inventory")
            {
                Logger.LogInfo("COMMAND semantic: inventory");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "inventory-state"
                    });
                return;
            }

            if (action == "discard_item")
            {
                JToken slotToken = message["slot"];
                int slot;
                if (slotToken == null ||
                    !int.TryParse(slotToken.ToString(), out slot) ||
                    slot < 0)
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] = "discard_item requires slot"
                        });
                    return;
                }

                Logger.LogInfo($"COMMAND inventory: discard_item slot={slot}");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "inventory-discard " + slot
                    });
                return;
            }

            if (action == "close_inventory")
            {
                Logger.LogInfo("COMMAND inventory: close_inventory");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "inventory-close"
                    });
                return;
            }

            if (action == "start_driving")
            {
                Logger.LogInfo("COMMAND stagecoach_ready: start_driving");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "stagecoach-start-driving"
                    });
                return;
            }

            if (action == "stop_driving")
            {
                Logger.LogInfo("COMMAND driving: stop_driving");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "stagecoach-stop-driving"
                    });
                return;
            }

            if (action == "choose_story")
            {
                JToken actorGuidToken = message["actor_guid"];
                uint actorGuid;

                if (actorGuidToken == null ||
                    !uint.TryParse(
                        actorGuidToken.ToString(),
                        out actorGuid) ||
                    actorGuid == 0u)
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] =
                                "choose_story requires actor_guid"
                        });
                    return;
                }

                Logger.LogInfo(
                    $"COMMAND story_choice: choose_story actor={actorGuid}");

                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command =
                            "story-choice-select " +
                            actorGuid
                    });
                return;
            }

            if (action == "choose_route")
            {
                JToken optionIndexToken = message["option_index"];
                int optionIndex;

                if (optionIndexToken == null ||
                    !int.TryParse(
                        optionIndexToken.ToString(),
                        out optionIndex) ||
                    optionIndex < 0)
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] =
                                "choose_route requires non-negative option_index"
                        });
                    return;
                }

                Logger.LogInfo(
                    $"COMMAND route_choice: choose_route option={optionIndex}");

                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command =
                            "route-choice-select " +
                            optionIndex
                    });
                return;
            }

            if (action == "move_party_member")
            {
                JToken actorGuidToken = message["actor_guid"];
                JToken slotToken = message["slot"];
                uint actorGuid;
                int slot;

                if (actorGuidToken == null ||
                    slotToken == null ||
                    !uint.TryParse(actorGuidToken.ToString(), out actorGuid) ||
                    actorGuid == 0u ||
                    !int.TryParse(slotToken.ToString(), out slot) ||
                    slot < 0)
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] =
                                "move_party_member requires actor_guid and non-negative slot"
                        });
                    return;
                }

                Logger.LogInfo(
                    $"COMMAND stagecoach_ready: move_party_member " +
                    $"actor={actorGuid} slot={slot}");

                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command =
                            "stagecoach-move-party-member " +
                            actorGuid +
                            " " +
                            slot
                    });
                return;
            }

            if (action == "equipment_state")
            {
                Logger.LogInfo("COMMAND stagecoach_ready: equipment_state");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "equipment-state"
                    });
                return;
            }

            if (action == "equip_trinket")
            {
                JToken actorGuidToken = message["actor_guid"];
                JToken playerSlotToken = message["player_slot"];
                JToken trinketSlotToken = message["trinket_slot"];
                uint actorGuid;
                int playerSlot;
                int trinketSlot;

                if (actorGuidToken == null ||
                    playerSlotToken == null ||
                    trinketSlotToken == null ||
                    !uint.TryParse(actorGuidToken.ToString(), out actorGuid) ||
                    actorGuid == 0u ||
                    !int.TryParse(playerSlotToken.ToString(), out playerSlot) ||
                    playerSlot < 0 ||
                    !int.TryParse(trinketSlotToken.ToString(), out trinketSlot) ||
                    trinketSlot < 0)
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] =
                                "equip_trinket requires actor_guid, non-negative player_slot and trinket_slot"
                        });
                    return;
                }

                Logger.LogInfo(
                    $"COMMAND stagecoach_ready: equip_trinket " +
                    $"actor={actorGuid} player_slot={playerSlot} " +
                    $"trinket_slot={trinketSlot}");

                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command =
                            "equipment-equip-trinket " +
                            actorGuid + " " +
                            playerSlot + " " +
                            trinketSlot
                    });
                return;
            }

            if (action == "unequip_trinket")
            {
                JToken actorGuidToken = message["actor_guid"];
                JToken trinketSlotToken = message["trinket_slot"];
                uint actorGuid;
                int trinketSlot;

                if (actorGuidToken == null ||
                    trinketSlotToken == null ||
                    !uint.TryParse(actorGuidToken.ToString(), out actorGuid) ||
                    actorGuid == 0u ||
                    !int.TryParse(trinketSlotToken.ToString(), out trinketSlot) ||
                    trinketSlot < 0)
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] =
                                "unequip_trinket requires actor_guid and non-negative trinket_slot"
                        });
                    return;
                }

                Logger.LogInfo(
                    $"COMMAND stagecoach_ready: unequip_trinket " +
                    $"actor={actorGuid} trinket_slot={trinketSlot}");

                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command =
                            "equipment-unequip-trinket " +
                            actorGuid + " " +
                            trinketSlot
                    });
                return;
            }

            if (action == "equip_combat_item")
            {
                JToken actorGuidToken = message["actor_guid"];
                JToken playerSlotToken = message["player_slot"];
                uint actorGuid;
                int playerSlot;

                if (actorGuidToken == null ||
                    playerSlotToken == null ||
                    !uint.TryParse(actorGuidToken.ToString(), out actorGuid) ||
                    actorGuid == 0u ||
                    !int.TryParse(playerSlotToken.ToString(), out playerSlot) ||
                    playerSlot < 0)
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] =
                                "equip_combat_item requires actor_guid and non-negative player_slot"
                        });
                    return;
                }

                Logger.LogInfo(
                    $"COMMAND stagecoach_ready: equip_combat_item " +
                    $"actor={actorGuid} player_slot={playerSlot}");

                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command =
                            "equipment-equip-combat-item " +
                            actorGuid + " " +
                            playerSlot
                    });
                return;
            }

            if (action == "unequip_combat_item")
            {
                JToken actorGuidToken = message["actor_guid"];
                uint actorGuid;

                if (actorGuidToken == null ||
                    !uint.TryParse(actorGuidToken.ToString(), out actorGuid) ||
                    actorGuid == 0u)
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] =
                                "unequip_combat_item requires actor_guid"
                        });
                    return;
                }

                Logger.LogInfo(
                    $"COMMAND stagecoach_ready: unequip_combat_item " +
                    $"actor={actorGuid}");

                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command =
                            "equipment-unequip-combat-item " +
                            actorGuid
                    });
                return;
            }

            if (action == "embark")
            {
                Logger.LogInfo("COMMAND inn: embark");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "inn-embark"
                    });
                return;
            }

            if (action == "reveal_relationship")
            {
                JArray actorGuids = message["actor_guids"] as JArray;
                uint actorGuidA;
                uint actorGuidB;

                if (actorGuids == null ||
                    actorGuids.Count != 2 ||
                    !uint.TryParse(actorGuids[0].ToString(), out actorGuidA) ||
                    !uint.TryParse(actorGuids[1].ToString(), out actorGuidB) ||
                    actorGuidA == 0u ||
                    actorGuidB == 0u ||
                    actorGuidA == actorGuidB)
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] =
                                "reveal_relationship requires exactly two actor_guids"
                        });
                    return;
                }

                Logger.LogInfo(
                    $"COMMAND embark_relationships: reveal_relationship " +
                    $"actors={actorGuidA},{actorGuidB}");

                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command =
                            "embark-reveal-relationship " +
                            actorGuidA + " " + actorGuidB
                    });
                return;
            }

            if (action == "continue_embark")
            {
                Logger.LogInfo(
                    "COMMAND embark_relationships: continue_embark");

                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "embark-continue"
                    });
                return;
            }

            if (action == "travelogue")
            {
                Logger.LogInfo("COMMAND inn: travelogue");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "inn-travelogue-state"
                    });
                return;
            }

            if (action == "end_expedition")
            {
                Logger.LogInfo("COMMAND inn: end_expedition");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "inn-end-expedition-state"
                    });
                return;
            }

            if (action == "take_hope")
            {
                Logger.LogInfo("COMMAND inn_end_expedition: take_hope");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "inn-end-expedition-take-hope"
                    });
                return;
            }

            if (action == "back")
            {
                Logger.LogInfo("COMMAND inn_end_expedition: back");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "inn-end-expedition-back"
                    });
                return;
            }

            if (action == "store")
            {
                Logger.LogInfo("COMMAND inn: store");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "inn-store-state"
                    });
                return;
            }

            if (action == "inn_store_state")
            {
                Logger.LogInfo("COMMAND inn_store: state");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "inn-store-state"
                    });
                return;
            }

            if (action == "inn_store_buy")
            {
                JToken slotToken = message["slot"];
                int slot;
                if (slotToken == null ||
                    !int.TryParse(slotToken.ToString(), out slot) ||
                    slot < 0)
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] =
                                "inn_store_buy requires a non-negative slot"
                        });
                    return;
                }

                Logger.LogInfo($"COMMAND inn_store: buy slot={slot}");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "inn-store-buy " + slot
                    });
                return;
            }

            if (action == "inn_store_discard")
            {
                JToken slotToken = message["slot"];
                int slot;
                if (slotToken == null ||
                    !int.TryParse(slotToken.ToString(), out slot) ||
                    slot < 0)
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] =
                                "inn_store_discard requires a non-negative slot"
                        });
                    return;
                }

                Logger.LogInfo($"COMMAND inn_store: discard slot={slot}");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "inn-store-discard " + slot
                    });
                return;
            }

            if (action == "inn_store_back")
            {
                Logger.LogInfo("COMMAND inn_store: back");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "inn-store-back"
                    });
                return;
            }

            if (action == "mastery")
            {
                Logger.LogInfo("COMMAND inn: mastery");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "inn-mastery-state"
                    });
                return;
            }

            if (action == "wainwright" || action == "inn_wainwright_state")
            {
                Logger.LogInfo("COMMAND inn: wainwright");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "inn-wainwright-state"
                    });
                return;
            }

            if (action == "inn_wainwright_equip")
            {
                JToken playerSlotToken = message["player_slot"];
                JToken slotTypeToken = message["slot_type"];
                JToken stagecoachSlotToken = message["stagecoach_slot"];
                int playerSlot;
                int stagecoachSlot;
                string slotType = slotTypeToken == null
                    ? string.Empty
                    : slotTypeToken.ToString();

                if (playerSlotToken == null ||
                    !int.TryParse(playerSlotToken.ToString(), out playerSlot) ||
                    playerSlot < 0 ||
                    string.IsNullOrWhiteSpace(slotType) ||
                    stagecoachSlotToken == null ||
                    !int.TryParse(stagecoachSlotToken.ToString(), out stagecoachSlot) ||
                    stagecoachSlot < 0)
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] =
                                "inn_wainwright_equip requires player_slot, slot_type and stagecoach_slot"
                        });
                    return;
                }

                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command =
                            "inn-wainwright-equip " +
                            playerSlot + " " + slotType + " " + stagecoachSlot
                    });
                return;
            }

            if (action == "inn_wainwright_unequip")
            {
                JToken slotTypeToken = message["slot_type"];
                JToken stagecoachSlotToken = message["stagecoach_slot"];
                int stagecoachSlot;
                string slotType = slotTypeToken == null
                    ? string.Empty
                    : slotTypeToken.ToString();

                if (string.IsNullOrWhiteSpace(slotType) ||
                    stagecoachSlotToken == null ||
                    !int.TryParse(stagecoachSlotToken.ToString(), out stagecoachSlot) ||
                    stagecoachSlot < 0)
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] =
                                "inn_wainwright_unequip requires slot_type and stagecoach_slot"
                        });
                    return;
                }

                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command =
                            "inn-wainwright-unequip " +
                            slotType + " " + stagecoachSlot
                    });
                return;
            }

            if (action == "inn_wainwright_back")
            {
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "inn-wainwright-back"
                    });
                return;
            }

            if (action == "inn_items" || action == "inn_items_state")
            {
                Logger.LogInfo("COMMAND inn: inn_items");
                _requests.Enqueue(new BridgeRequest { Command = "inn-items-state" });
                return;
            }

            if (action == "inn_item_use")
            {
                JToken slotToken = message["slot"];
                int slot;
                if (slotToken == null || !int.TryParse(slotToken.ToString(), out slot) || slot < 0)
                {
                    QueueOutgoingMessage(new JObject { ["type"]="error", ["code"]="invalid_command", ["message"]="inn_item_use requires a non-negative slot" });
                    return;
                }
                string command = "inn-item-use " + slot;
                JArray targetGuids = message["target_guids"] as JArray;
                if (targetGuids != null)
                    foreach (JToken target in targetGuids) command += " " + target.ToString();
                _requests.Enqueue(new BridgeRequest { Command = command });
                return;
            }

            if (action == "inn_items_back")
            {
                _requests.Enqueue(new BridgeRequest { Command = "inn-items-back" });
                return;
            }

            if (action == "route" || action == "inn_route_state")
            {
                Logger.LogInfo("COMMAND inn: route");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "inn-route-state"
                    });
                return;
            }

            if (action == "inn_route_choose")
            {
                JToken optionToken = message["option_index"];
                int optionIndex;
                if (optionToken == null ||
                    !int.TryParse(optionToken.ToString(), out optionIndex) ||
                    optionIndex < 0)
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] =
                                "inn_route_choose requires a non-negative option_index"
                        });
                    return;
                }

                Logger.LogInfo(
                    $"COMMAND inn_route_choice: choose_route option={optionIndex}");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "inn-route-choose " + optionIndex
                    });
                return;
            }

            if (action == "inn_route_back")
            {
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "inn-route-back"
                    });
                return;
            }

            if (action == "inn_mastery_state")
            {
                Logger.LogInfo("COMMAND inn_mastery: state");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "inn-mastery-state"
                    });
                return;
            }

            if (action == "inn_mastery_upgrade_skill")
            {
                JToken actorToken = message["actor_guid"];
                JToken masterySkillToken = message["skill_id"];
                uint actorGuid;
                string masterySkillId =
                    masterySkillToken == null ? string.Empty : masterySkillToken.ToString();

                if (actorToken == null ||
                    !uint.TryParse(actorToken.ToString(), out actorGuid) ||
                    string.IsNullOrEmpty(masterySkillId))
                {
                    QueueOutgoingMessage(
                        new JObject
                        {
                            ["type"] = "error",
                            ["code"] = "invalid_command",
                            ["message"] =
                                "inn_mastery_upgrade_skill requires actor_guid and skill_id"
                        });
                    return;
                }

                Logger.LogInfo(
                    $"COMMAND inn_mastery: upgrade_skill actor={actorGuid} " +
                    $"skill='{masterySkillId}'");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command =
                            "inn-mastery-upgrade-skill " +
                            actorGuid + " " + masterySkillId
                    });
                return;
            }

            if (action == "inn_mastery_back")
            {
                Logger.LogInfo("COMMAND inn_mastery: back");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "inn-mastery-back"
                    });
                return;
            }

            if (action == "continue_travelogue")
            {
                Logger.LogInfo(
                    "COMMAND inn_travelogue: continue_travelogue");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "semantic-continue"
                    });
                return;
            }

            if (action == "continue")
            {
                Logger.LogInfo("COMMAND semantic: continue");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "semantic-continue"
                    });
                return;
            }

            if (action == "new_expedition")
            {
                Logger.LogInfo("COMMAND main_menu: new_expedition");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "main-menu-new-expedition"
                    });
                return;
            }

            if (action == "continue_expedition")
            {
                Logger.LogInfo("COMMAND main_menu: continue_expedition");
                _requests.Enqueue(
                    new BridgeRequest
                    {
                        Command = "main-menu-continue-expedition"
                    });
                return;
            }

            if (action != "skill")
            {
                QueueOutgoingMessage(
                    new JObject
                    {
                        ["type"] = "error",
                        ["code"] = "unknown_action",
                        ["message"] =
                            $"Unknown action: {action ?? "<null>"}"
                    });

                return;
            }

            JToken performerToken = message["performer_guid"];
            JToken skillToken = message["skill_id"];
            JToken targetToken = message["target_guid"];

            if (performerToken == null ||
                skillToken == null ||
                targetToken == null)
            {
                QueueOutgoingMessage(
                    new JObject
                    {
                        ["type"] = "error",
                        ["code"] = "invalid_command",
                        ["message"] =
                            "skill requires performer_guid, skill_id and target_guid"
                    });

                return;
            }

            uint performerGuid;
            uint targetGuid;

            if (!uint.TryParse(
                    performerToken.ToString(),
                    out performerGuid) ||
                !uint.TryParse(
                    targetToken.ToString(),
                    out targetGuid))
            {
                QueueOutgoingMessage(
                    new JObject
                    {
                        ["type"] = "error",
                        ["code"] = "invalid_guid",
                        ["message"] =
                            "performer_guid and target_guid must be uint"
                    });

                return;
            }

            string skillId = skillToken.Value<string>();

            if (string.IsNullOrEmpty(skillId))
            {
                QueueOutgoingMessage(
                    new JObject
                    {
                        ["type"] = "error",
                        ["code"] = "invalid_skill_id",
                        ["message"] =
                            "skill_id must not be empty"
                    });

                return;
            }

            Logger.LogInfo(
                $"COMMAND skill: performer={performerGuid}, " +
                $"skill={skillId}, target={targetGuid}");

            BridgeRequest request =
                new BridgeRequest
                {
                    Command =
                        $"test-execute-skill " +
                        $"{performerGuid} " +
                        $"{skillId} " +
                        $"{targetGuid}"
                };

            _requests.Enqueue(request);
        }
    }
}



