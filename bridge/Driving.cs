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
using Assets.Code.Roster;
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
using Assets.Code.UI.Banter;
using Assets.Code.Campaign;

using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        // v16: discrete pre-driving frontend boundary. DD2's normal tap-W
        // behavior latches StageCoachInternalState.m_inputAlwaysMoveForward.
        // The bridge exposes that native semantic operation as start_driving,
        // but deliberately does not implement realtime steering yet.
        private bool _stagecoachReadySent;
        private StageCoachVehicleControlBhv _stagecoachReadyVehicle;
        private StageCoachVehicleControlBhv _stagecoachVehicleCache;

        // v0.0.46: road combat can coexist with the still-loaded DRIVING
        // scene. A live MapMgr/stagecoach therefore does not prove that the
        // stagecoach owns the player's foreground interaction.
        private bool IsCombatForegroundActive()
        {
            try
            {
                CombatBhv combat = CombatBhv.Instance;
                return combat != null && combat.IsBattleRunning;
            }
            catch (Exception)
            {
                // Be conservative only about the supplementary probe itself;
                // teardown-time singleton churn must not break Update().
                return false;
            }
        }

        private StageCoachVehicleControlBhv GetStableStagecoachReadyVehicle()
        {
            if (GameModeMgr.CurrentMode != GameModeType.DRIVING)
            {
                ResetStagecoachReadyBoundary();
                return null;
            }

            GameModeMgr modeMgr = GameModeMgr.Instance;
            if (modeMgr == null || modeMgr.IsChangingState())
            {
                ResetStagecoachReadyBoundary();
                return null;
            }

            // The dormant road/stagecoach scene remains alive underneath
            // combat. While the native Battle is running, combat owns the
            // foreground and stagecoach_ready must not exist.
            if (IsCombatForegroundActive())
            {
                ResetStagecoachReadyBoundary();
                return null;
            }

            // expedition_results can be displayed over the already-loaded
            // driving scene. Never leak the underlying stagecoach boundary
            // while that modal frontend owns the player's next decision.
            if (_expeditionResultsScreen != null &&
                _expeditionResultsScreen.gameObject != null &&
                _expeditionResultsScreen.gameObject.activeInHierarchy)
            {
                ResetStagecoachReadyBoundary();
                return null;
            }

            MapMgrBhv mapMgr = SingletonMonoBehaviour<MapMgrBhv>.Instance;
            if (mapMgr == null || !mapMgr.IsInDrivingState())
            {
                ResetStagecoachReadyBoundary();
                return null;
            }

            CommonUiBhv commonUi = SingletonMonoBehaviour<CommonUiBhv>.Instance;
            if (commonUi == null ||
                commonUi.IsEnterNodeScreenActive ||
                commonUi.IsDrivingInputBlockerActive)
            {
                ResetStagecoachReadyBoundary();
                return null;
            }

            // BOSS_SELECT is presented over the already-loaded DRIVING scene.
            // Never leak the underlying stagecoach boundary while that modal UI
            // owns the player's next decision.
            if (GetStableBossSelectWidget() != null)
            {
                ResetStagecoachReadyBoundary();
                return null;
            }

            StageCoachVehicleControlBhv vehicle = _stagecoachVehicleCache;

            if (vehicle == null ||
                vehicle.gameObject == null ||
                !vehicle.gameObject.activeInHierarchy)
            {
                vehicle =
                    UnityEngine.Object.FindObjectOfType<StageCoachVehicleControlBhv>();
                _stagecoachVehicleCache = vehicle;
            }

            if (vehicle == null ||
                vehicle.gameObject == null ||
                !vehicle.gameObject.activeInHierarchy ||
                !vehicle.enabled ||
                !vehicle.IsPlayable() ||
                !vehicle.IsPlayerInputEnabled ||
                vehicle.GetAlwaysMoveForward())
            {
                ResetStagecoachReadyBoundary();
                return null;
            }

            StageCoachInternalState state =
                vehicle.GetInternalState() as StageCoachInternalState;

            if (state == null ||
                !state.IsListeningForPlayerInput() ||
                UnityEngine.Mathf.Abs(state.GetSpeed()) >= 0.01f)
            {
                ResetStagecoachReadyBoundary();
                return null;
            }

            if (!ReferenceEquals(vehicle, _stagecoachReadyVehicle))
            {
                _stagecoachReadyVehicle = vehicle;
                _stagecoachReadySent = false;
            }

            return vehicle;
        }

        private void ResetStagecoachReadyBoundary()
        {
            _stagecoachReadyVehicle = null;
            _stagecoachReadySent = false;
        }

        private List<HeroRibbonBhv> GetStagecoachPartyRibbons()
        {
            IReadOnlyList<uint> partyGuids =
                Singleton<GameTypeMgr>.Instance.RosterManager
                    .GetActorGuids(RosterStatusType.PARTY);

            HashSet<uint> partyGuidSet = new HashSet<uint>(partyGuids);
            HeroRibbonBhv[] found =
                UnityEngine.Object.FindObjectsOfType<HeroRibbonBhv>();

            List<HeroRibbonBhv> ribbons = new List<HeroRibbonBhv>();
            HashSet<uint> seenGuids = new HashSet<uint>();
            HashSet<int> seenSlots = new HashSet<int>();

            foreach (HeroRibbonBhv ribbon in found)
            {
                if (ribbon == null ||
                    ribbon.gameObject == null ||
                    !ribbon.gameObject.activeInHierarchy)
                {
                    continue;
                }

                uint actorGuid = ribbon.ActorGuid;
                int slot = ribbon.GetSlot();

                if (actorGuid == 0u ||
                    !partyGuidSet.Contains(actorGuid) ||
                    slot < 0)
                {
                    continue;
                }

                // Ambiguous duplicate live ribbons/slots would make a semantic
                // reorder unsafe. Fail closed rather than selecting one.
                if (!seenGuids.Add(actorGuid) || !seenSlots.Add(slot))
                {
                    return null;
                }

                ribbons.Add(ribbon);
            }

            if (ribbons.Count != partyGuids.Count || ribbons.Count == 0)
            {
                return null;
            }

            ribbons.Sort((left, right) =>
                left.GetSlot().CompareTo(right.GetSlot()));

            // The native ribbon reorder algorithm assumes a contiguous slot
            // domain. Verify that assumption before exposing the action.
            for (int i = 0; i < ribbons.Count; i++)
            {
                if (ribbons[i].GetSlot() != i)
                {
                    return null;
                }
            }

            return ribbons;
        }

        private static HeroRibbonBhv FindRibbonByActorGuid(
            List<HeroRibbonBhv> ribbons,
            uint actorGuid)
        {
            foreach (HeroRibbonBhv ribbon in ribbons)
            {
                if (ribbon.ActorGuid == actorGuid)
                {
                    return ribbon;
                }
            }

            return null;
        }

        private static HeroRibbonBhv FindRibbonBySlot(
            List<HeroRibbonBhv> ribbons,
            int slot)
        {
            foreach (HeroRibbonBhv ribbon in ribbons)
            {
                if (ribbon.GetSlot() == slot)
                {
                    return ribbon;
                }
            }

            return null;
        }

        private JObject BuildStagecoachReadyMessage()
        {
            JArray actions = new JArray
            {
                new JObject
                {
                    ["action"] = "start_driving"
                },
                new JObject
                {
                    ["action"] = "inventory"
                },
                new JObject
                {
                    ["action"] = "map"
                }
            };

            JArray party = new JArray();
            List<HeroRibbonBhv> ribbons = GetStagecoachPartyRibbons();

            if (ribbons != null)
            {
                JArray validSlots = new JArray();
                for (int i = 0; i < ribbons.Count; i++)
                {
                    validSlots.Add(i);
                }

                bool reorderUnlocked = true;
                foreach (HeroRibbonBhv ribbon in ribbons)
                {
                    if (ribbon.IsLocked())
                    {
                        reorderUnlocked = false;
                    }

                    party.Add(
                        new JObject
                        {
                            ["slot"] = ribbon.GetSlot(),
                            ["actor_guid"] = ribbon.ActorGuid,
                            ["locked"] = ribbon.IsLocked()
                        });
                }

                if (reorderUnlocked)
                {
                    foreach (HeroRibbonBhv ribbon in ribbons)
                    {
                        actions.Add(
                            new JObject
                            {
                                ["action"] = "move_party_member",
                                ["actor_guid"] = ribbon.ActorGuid,
                                ["current_slot"] = ribbon.GetSlot(),
                                ["valid_slots"] = validSlots.DeepClone()
                            });
                    }
                }
            }

            return new JObject
            {
                ["type"] = "ready",
                ["frontend_state"] = "stagecoach_ready",
                ["party"] = party,
                ["available_actions"] = actions
            };
        }

        private void ProcessStagecoachReady()
        {
            StageCoachVehicleControlBhv vehicle =
                GetStableStagecoachReadyVehicle();

            if (vehicle == null || _stagecoachReadySent)
            {
                return;
            }

            _stagecoachReadySent = true;
            _hostReadyCatchupRequested = false;

            Logger.LogInfo("READY stagecoach_ready");
            QueueOutgoingMessage(BuildStagecoachReadyMessage());
        }

        private void HandleStagecoachStartDriving(
            BridgeRequest request)
        {
            StageCoachVehicleControlBhv vehicle =
                GetStableStagecoachReadyVehicle();

            if (vehicle == null)
            {
                request.Response =
                    "ERROR: stagecoach is not at the ready-to-drive boundary";
                return;
            }

            _stagecoachReadySent = true;
            _hostReadyCatchupRequested = false;

            Logger.LogInfo(
                "COMMAND consumes READY boundary: stagecoach_ready start_driving");

            // This is the native latch set by ordinary positive Move input when
            // HOLD_TO_DRIVE is disabled: subsequent UpdateSpeed() calls force
            // accelMult=1 and the game's own physics/controller continues travel.
            vehicle.SetInputAlwaysMoveForward(true);

            // No agent decision exists while realtime travel is in progress.
            // Announce a reusable presentation-only activity; the next READY
            // boundary (node entry, combat, etc.) ends it on the host side.
            QueueActivityStatus("driving");

            request.Response = "OK";
        }

        private void HandleStagecoachMovePartyMember(
            BridgeRequest request,
            string args)
        {
            StageCoachVehicleControlBhv vehicle =
                GetStableStagecoachReadyVehicle();

            if (vehicle == null)
            {
                request.Response =
                    "ERROR: party positions can only be changed at stagecoach_ready";
                return;
            }

            string[] parts = (args ?? string.Empty)
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            uint actorGuid;
            int targetSlot;

            if (parts.Length != 2 ||
                !uint.TryParse(parts[0], out actorGuid) ||
                actorGuid == 0u ||
                !int.TryParse(parts[1], out targetSlot))
            {
                request.Response =
                    "ERROR: move_party_member requires actor_guid and slot";
                return;
            }

            List<HeroRibbonBhv> ribbons = GetStagecoachPartyRibbons();
            if (ribbons == null)
            {
                request.Response =
                    "ERROR: active stagecoach party ribbons are unavailable or ambiguous";
                return;
            }

            if (targetSlot < 0 || targetSlot >= ribbons.Count)
            {
                request.Response =
                    $"ERROR: target slot must be between 0 and {ribbons.Count - 1}";
                return;
            }

            foreach (HeroRibbonBhv ribbon in ribbons)
            {
                if (ribbon.IsLocked())
                {
                    request.Response =
                        "ERROR: party reordering is currently locked by the game UI";
                    return;
                }
            }

            HeroRibbonBhv draggedRibbon =
                FindRibbonByActorGuid(ribbons, actorGuid);

            if (draggedRibbon == null)
            {
                request.Response =
                    "ERROR: actor is not present in the active stagecoach party ribbon";
                return;
            }

            int sourceSlot = draggedRibbon.GetSlot();
            if (sourceSlot == targetSlot)
            {
                // Idempotent move: return the authoritative unchanged boundary.
                QueueOutgoingMessage(BuildStagecoachReadyMessage());
                request.Response = "OK";
                return;
            }

            // Mirror HeroRibbonContainerBhv.OnHeroRibbonDrag(): moving one hero
            // inserts it at the target slot and shifts every intermediate hero by
            // one slot. HeroRibbonBhv.SetSlot() is the game's own mutation seam;
            // it updates TeamPosition, FrontRank, and RefreshStats().
            if (targetSlot < sourceSlot)
            {
                for (int slot = sourceSlot - 1; slot >= targetSlot; slot--)
                {
                    HeroRibbonBhv shifted = FindRibbonBySlot(ribbons, slot);
                    if (shifted == null)
                    {
                        request.Response =
                            $"ERROR: no active party ribbon occupies slot {slot}";
                        return;
                    }

                    shifted.SetSlot(slot + 1);
                }
            }
            else
            {
                for (int slot = sourceSlot + 1; slot <= targetSlot; slot++)
                {
                    HeroRibbonBhv shifted = FindRibbonBySlot(ribbons, slot);
                    if (shifted == null)
                    {
                        request.Response =
                            $"ERROR: no active party ribbon occupies slot {slot}";
                        return;
                    }

                    shifted.SetSlot(slot - 1);
                }
            }

            draggedRibbon.SetSlot(targetSlot);

            IReadOnlyList<uint> partyGuids =
                Singleton<GameTypeMgr>.Instance.RosterManager
                    .GetActorGuids(RosterStatusType.PARTY);

            if (partyGuids.Count != ribbons.Count ||
                partyGuids[targetSlot] != actorGuid)
            {
                request.Response =
                    "ERROR: party reorder postcondition failed";
                return;
            }

            Logger.LogInfo(
                $"COMMAND stagecoach_ready: move_party_member " +
                $"actor={actorGuid} {sourceSlot}->{targetSlot}");

            // This is a non-consuming management action. Publish a refreshed
            // authoritative boundary so the host sees the new order immediately.
            _stagecoachReadySent = true;
            _hostReadyCatchupRequested = false;
            QueueOutgoingMessage(BuildStagecoachReadyMessage());

            request.Response = "OK";
        }

        private void HandleStagecoachStopDriving(
            BridgeRequest request)
        {
            if (GameModeMgr.CurrentMode != GameModeType.DRIVING)
            {
                request.Response =
                    "ERROR: stagecoach is not currently driving";
                return;
            }

            GameModeMgr modeMgr = GameModeMgr.Instance;
            if (modeMgr == null || modeMgr.IsChangingState())
            {
                request.Response =
                    "ERROR: driving mode is currently transitioning";
                return;
            }

            MapMgrBhv mapMgr = SingletonMonoBehaviour<MapMgrBhv>.Instance;
            if (mapMgr == null || !mapMgr.IsInDrivingState())
            {
                request.Response =
                    "ERROR: stagecoach is not in the active driving state";
                return;
            }

            StageCoachVehicleControlBhv vehicle = _stagecoachVehicleCache;

            if (vehicle == null ||
                vehicle.gameObject == null ||
                !vehicle.gameObject.activeInHierarchy)
            {
                vehicle =
                    UnityEngine.Object.FindObjectOfType<StageCoachVehicleControlBhv>();
                _stagecoachVehicleCache = vehicle;
            }

            if (vehicle == null ||
                vehicle.gameObject == null ||
                !vehicle.gameObject.activeInHierarchy ||
                !vehicle.enabled ||
                !vehicle.IsPlayable())
            {
                request.Response =
                    "ERROR: playable stagecoach vehicle is not available";
                return;
            }

            if (!vehicle.GetAlwaysMoveForward())
            {
                request.Response =
                    "ERROR: stagecoach is not currently auto-driving";
                return;
            }

            Logger.LogInfo("COMMAND driving: stop_driving");

            // Native stop seam used by DD2's own driving UI: clear the
            // always-move-forward latch. The game's controller/physics performs
            // the actual deceleration; ProcessStagecoachReady() will publish the
            // next READY only after speed has reached the existing stable-stop
            // threshold.
            vehicle.SetInputAlwaysMoveForward(false);

            // Re-arm the boundary explicitly. This also covers the edge case
            // where stop_driving arrives before the moving-state poll had a
            // chance to clear the READY latch consumed by start_driving.
            ResetStagecoachReadyBoundary();
            _hostReadyCatchupRequested = false;

            request.Response = "OK";
        }

        // v14: main-menu equivalent of the combat decision boundary. Do not
        // announce while the disclaimer/opening timeline is active, during a
        // GameMode transition, or after the user/agent already chose an action.
    }
}


