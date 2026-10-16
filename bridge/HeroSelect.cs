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
        // v19: Crossroads / HERO_SELECT boundary. Keep this reflection-backed for
        // now so the bridge does not gain a hard compile-time dependency on the
        // HeroSelect UI namespace. The native HeroSelectBhv remains the authority.
        private UnityEngine.MonoBehaviour _heroSelectReadyBhv;
        private bool _heroSelectReadySent;


        private UnityEngine.MonoBehaviour GetStableHeroSelectBhv()
        {
            if (GameModeMgr.CurrentMode != GameModeType.HERO_SELECT)
            {
                ResetHeroSelectReadyBoundary();
                return null;
            }

            GameModeMgr modeMgr = GameModeMgr.Instance;
            if (modeMgr == null || modeMgr.IsChangingState())
            {
                ResetHeroSelectReadyBoundary();
                return null;
            }

            if (_heroSelectReadyBhv != null &&
                _heroSelectReadyBhv.gameObject != null &&
                _heroSelectReadyBhv.gameObject.activeInHierarchy &&
                _heroSelectReadyBhv.enabled &&
                string.Equals(
                    _heroSelectReadyBhv.GetType().Name,
                    "HeroSelectBhv",
                    StringComparison.Ordinal))
            {
                if (GetBooleanMember(
                        _heroSelectReadyBhv,
                        "SelectingRosterSlot",
                        false))
                {
                    return null;
                }

                return _heroSelectReadyBhv;
            }

            UnityEngine.MonoBehaviour active = null;
            UnityEngine.MonoBehaviour[] behaviours =
                UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.MonoBehaviour>();

            foreach (UnityEngine.MonoBehaviour candidate in behaviours)
            {
                if (candidate == null ||
                    candidate.gameObject == null ||
                    !candidate.gameObject.activeInHierarchy ||
                    !candidate.enabled ||
                    !string.Equals(
                        candidate.GetType().Name,
                        "HeroSelectBhv",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                bool selectingRosterSlot =
                    GetBooleanMember(candidate, "SelectingRosterSlot", false);

                if (selectingRosterSlot)
                {
                    // The human UI is in the middle of a two-step slot selection.
                    // Do not announce a fresh semantic boundary until it settles.
                    return null;
                }

                active = candidate;
                break;
            }

            if (active == null)
            {
                ResetHeroSelectReadyBoundary();
                return null;
            }

            if (!ReferenceEquals(active, _heroSelectReadyBhv))
            {
                _heroSelectReadyBhv = active;
                _heroSelectReadySent = false;

                Logger.LogInfo(
                    $"READY boundary detected: hero_select " +
                    $"path='{GetTransformPath(active.transform)}'");
            }

            return active;
        }

        private void ResetHeroSelectReadyBoundary()
        {
            _heroSelectReadyBhv = null;
            _heroSelectReadySent = false;
        }

        private JObject BuildHeroSelectReadyMessage(
            UnityEngine.MonoBehaviour heroSelect)
        {
            JArray party = new JArray();
            HashSet<uint> selected = new HashSet<uint>();

            IList rosterSlots =
                GetMemberValue(heroSelect, "m_HeroSelectRosterObjects") as IList;

            MethodInfo getGuidAtRosterIndex =
                heroSelect.GetType().GetMethod(
                    "GetGuidAtRosterIndex",
                    BindingFlags.Instance | BindingFlags.Public);

            int slotCount = rosterSlots == null ? 0 : rosterSlots.Count;

            for (int i = 0; i < slotCount; i++)
            {
                uint actorGuid = 0u;

                if (getGuidAtRosterIndex != null)
                {
                    object value =
                        getGuidAtRosterIndex.Invoke(
                            heroSelect,
                            new object[] { i });

                    actorGuid = ToUInt32(value);
                }

                if (actorGuid != 0u)
                {
                    selected.Add(actorGuid);
                }

                party.Add(
                    new JObject
                    {
                        ["slot"] = i,
                        ["actor_guid"] = actorGuid
                    });
            }

            JArray heroes = new JArray();
            JArray actions = new JArray();

            IEnumerable heroGuids =
                GetMemberValue(heroSelect, "m_HeroActorGuids") as IEnumerable;

            if (heroGuids != null)
            {
                foreach (object value in heroGuids)
                {
                    uint actorGuid = ToUInt32(value);
                    if (actorGuid == 0u)
                    {
                        continue;
                    }

                    ActorInstance actor =
                        SingletonMonoBehaviour<Library<uint, ActorInstance>>
                            .Instance
                            .GetLibraryElement(actorGuid);

                    if (actor == null)
                    {
                        continue;
                    }

                    heroes.Add(
                        new JObject
                        {
                            ["actor_guid"] = actorGuid,
                            ["actor_data_id"] = actor.ActorDataId ?? "",
                            ["actor_name"] = actor.ActorName ?? "",
                            ["selected"] = selected.Contains(actorGuid)
                        });

                    actions.Add(
                        new JObject
                        {
                            ["action"] = "inspect_hero",
                            ["actor_guid"] = actorGuid,
                            ["consumes_boundary"] = false
                        });

                    JObject pathInfo = BuildHeroPathInspection(actor);
                    JArray availablePaths =
                        pathInfo["available_path_ids"] as JArray ?? new JArray();

                    foreach (JToken pathToken in availablePaths)
                    {
                        string pathId = pathToken.Value<string>();
                        if (string.IsNullOrEmpty(pathId))
                        {
                            continue;
                        }

                        actions.Add(
                            new JObject
                            {
                                ["action"] = "set_hero_path",
                                ["actor_guid"] = actorGuid,
                                ["path_id"] = pathId,
                                ["consumes_boundary"] = false
                            });
                    }

                    actions.Add(
                        new JObject
                        {
                            ["action"] = "set_hero_skills",
                            ["actor_guid"] = actorGuid,
                            ["consumes_boundary"] = false
                        });
                }
            }

            // Party composition is slot-addressable. Keep READY compact:
            // one set_party_member capability per slot with all currently legal
            // hero GUIDs, plus clear_party_slot only for occupied slots.
            JArray availableActorGuids = new JArray();
            foreach (JToken heroToken in heroes)
            {
                JObject heroObject = heroToken as JObject;
                if (heroObject == null)
                {
                    continue;
                }

                JToken guidToken = heroObject["actor_guid"];
                if (guidToken != null)
                {
                    availableActorGuids.Add(guidToken.DeepClone());
                }
            }

            bool partyComplete = party.Count > 0;

            foreach (JToken slotToken in party)
            {
                JObject slotObject = slotToken as JObject;
                if (slotObject == null)
                {
                    partyComplete = false;
                    continue;
                }

                int slotIndex = slotObject["slot"] == null
                    ? -1
                    : slotObject["slot"].Value<int>();

                uint slotActorGuid = slotObject["actor_guid"] == null
                    ? 0u
                    : slotObject["actor_guid"].Value<uint>();

                if (slotActorGuid == 0u)
                {
                    partyComplete = false;
                }

                actions.Add(
                    new JObject
                    {
                        ["action"] = "set_party_member",
                        ["slot"] = slotIndex,
                        ["actor_guids"] = availableActorGuids.DeepClone(),
                        ["consumes_boundary"] = false
                    });

                if (slotActorGuid != 0u)
                {
                    actions.Add(
                        new JObject
                        {
                            ["action"] = "clear_party_slot",
                            ["slot"] = slotIndex,
                            ["consumes_boundary"] = false
                        });
                }
            }

            if (partyComplete)
            {
                actions.Add(
                    new JObject
                    {
                        ["action"] = "confirm_party",
                        ["consumes_boundary"] = true
                    });
            }

            return new JObject
            {
                ["type"] = "ready",
                ["frontend_state"] = "hero_select",
                ["party"] = party,
                ["party_complete"] = partyComplete,
                ["available_heroes"] = heroes,
                ["available_actions"] = actions
            };
        }

        private void ProcessHeroSelectReady()
        {
            ConfirmationDialogBhv activeConfirmation;
            JObject confirmationReady;
            if (TryGetSupportedActiveConfirmation(
                    out activeConfirmation,
                    out confirmationReady))
            {
                return;
            }

            UnityEngine.MonoBehaviour heroSelect = GetStableHeroSelectBhv();

            if (heroSelect == null || _heroSelectReadySent)
            {
                return;
            }

            JObject ready = BuildHeroSelectReadyMessage(heroSelect);
            JArray heroes = ready["available_heroes"] as JArray;

            if (heroes == null || heroes.Count == 0)
            {
                return;
            }

            _heroSelectReadySent = true;
            _hostReadyCatchupRequested = false;

            Logger.LogInfo(
                $"READY hero_select: heroes={heroes.Count}");
            QueueOutgoingMessage(ready);
        }

        private void HandleHeroInspect(
            BridgeRequest request,
            string actorGuidText)
        {
            // Compatibility wrapper. inspect_hero is now a shared expedition
            // management action and may run at HERO_SELECT or NODE_ENTRY.
            HandleHeroManagementInspect(request, actorGuidText);
        }

        private bool HeroSelectContainsActorGuid(
            UnityEngine.MonoBehaviour heroSelect,
            uint actorGuid)
        {
            IEnumerable heroGuids =
                GetMemberValue(heroSelect, "m_HeroActorGuids") as IEnumerable;

            if (heroGuids != null)
            {
                foreach (object value in heroGuids)
                {
                    if (ToUInt32(value) == actorGuid)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool IsHeroSelectPartyComplete(
            UnityEngine.MonoBehaviour heroSelect,
            out int slotCount)
        {
            slotCount = 0;

            IList rosterSlots =
                GetMemberValue(
                    heroSelect,
                    "m_HeroSelectRosterObjects") as IList;

            slotCount = rosterSlots == null ? 0 : rosterSlots.Count;
            if (slotCount <= 0)
            {
                return false;
            }

            for (int i = 0; i < slotCount; i++)
            {
                uint actorGuid;
                int ignoredSlotCount;

                if (!TryGetHeroSelectSlotGuid(
                        heroSelect,
                        i,
                        out actorGuid,
                        out ignoredSlotCount) ||
                    actorGuid == 0u)
                {
                    return false;
                }
            }

            return true;
        }

        private void HandleHeroConfirmParty(BridgeRequest request)
        {
            UnityEngine.MonoBehaviour heroSelect = GetStableHeroSelectBhv();
            if (heroSelect == null)
            {
                request.Response =
                    "ERROR: hero select is not at an interactive boundary";
                return;
            }

            int slotCount;
            if (!IsHeroSelectPartyComplete(heroSelect, out slotCount))
            {
                request.Response =
                    $"ERROR: party is incomplete; all {slotCount} slots must be occupied";
                return;
            }

            MethodInfo confirmRosterSelection =
                FindMethodByNameAndParameterCount(
                    heroSelect.GetType(),
                    "ConfirmRosterSelection",
                    0);

            if (confirmRosterSelection == null)
            {
                request.Response =
                    "ERROR: HeroSelectBhv.ConfirmRosterSelection was not found";
                return;
            }

            SetPendingConfirmationContext(
                ConfirmationContextHeroSelectIncompleteSkills);

            Logger.LogInfo("COMMAND hero_select: confirm_party");

            confirmRosterSelection.Invoke(
                heroSelect,
                new object[0]);

            // ShowConfirmationDialog is synchronous in the current build. If no
            // dialog appeared, confirmation took the direct path.
            ConfirmationDialogBhv dialog = FindActiveConfirmationDialog();
            if (dialog == null)
            {
                ClearPendingConfirmationContext();
            }

            request.Response = "OK";
        }

        private bool TryGetHeroSelectSlotGuid(
            UnityEngine.MonoBehaviour heroSelect,
            int slotIndex,
            out uint actorGuid,
            out int slotCount)
        {
            actorGuid = 0u;
            slotCount = 0;

            if (heroSelect == null)
            {
                return false;
            }

            IList rosterSlots =
                GetMemberValue(
                    heroSelect,
                    "m_HeroSelectRosterObjects") as IList;

            slotCount = rosterSlots == null ? 0 : rosterSlots.Count;

            if (slotIndex < 0 || slotIndex >= slotCount)
            {
                return false;
            }

            MethodInfo getGuidAtRosterIndex =
                heroSelect.GetType().GetMethod(
                    "GetGuidAtRosterIndex",
                    BindingFlags.Instance | BindingFlags.Public);

            if (getGuidAtRosterIndex == null)
            {
                return false;
            }

            actorGuid =
                ToUInt32(
                    getGuidAtRosterIndex.Invoke(
                        heroSelect,
                        new object[] { slotIndex }));

            return true;
        }

        private void QueueHeroSelectState(
            UnityEngine.MonoBehaviour heroSelect)
        {
            JObject state = BuildHeroSelectReadyMessage(heroSelect);
            state["type"] = "state";
            QueueOutgoingMessage(state);
        }

        private void HandleHeroSetPartyMember(
            BridgeRequest request,
            string arguments)
        {
            UnityEngine.MonoBehaviour heroSelect = GetStableHeroSelectBhv();
            if (heroSelect == null)
            {
                request.Response =
                    "ERROR: hero select is not at an interactive boundary";
                return;
            }

            string[] parts =
                (arguments ?? string.Empty).Split(
                    new[] { ' ' },
                    StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 2)
            {
                request.Response =
                    "ERROR: set_party_member requires slot and actor_guid";
                return;
            }

            int slotIndex;
            uint actorGuid;

            if (!int.TryParse(parts[0], out slotIndex) ||
                !uint.TryParse(parts[1], out actorGuid) ||
                actorGuid == 0u)
            {
                request.Response =
                    "ERROR: set_party_member requires valid slot and actor_guid";
                return;
            }

            uint previousGuid;
            int slotCount;
            if (!TryGetHeroSelectSlotGuid(
                    heroSelect,
                    slotIndex,
                    out previousGuid,
                    out slotCount))
            {
                request.Response =
                    $"ERROR: party slot {slotIndex} is out of range (slot count {slotCount})";
                return;
            }

            if (!HeroSelectContainsActorGuid(heroSelect, actorGuid))
            {
                request.Response =
                    $"ERROR: actor {actorGuid} is not available on this Crossroads screen";
                return;
            }

            MethodInfo setHeroSelection =
                FindMethodByNameAndParameterCount(
                    heroSelect.GetType(),
                    "SetHeroSelection",
                    3);

            if (setHeroSelection == null)
            {
                request.Response =
                    "ERROR: HeroSelectBhv.SetHeroSelection was not found";
                return;
            }

            // This is the same native seam used by OnRosterSlotSubmit and
            // SetPartyLoadout. playNarration=false avoids presentation-only bark
            // while preserving roster callbacks/events and replacement behavior.
            setHeroSelection.Invoke(
                heroSelect,
                new object[]
                {
                    actorGuid,
                    slotIndex,
                    false
                });

            uint actualGuid;
            int ignoredSlotCount;
            if (!TryGetHeroSelectSlotGuid(
                    heroSelect,
                    slotIndex,
                    out actualGuid,
                    out ignoredSlotCount) ||
                actualGuid != actorGuid)
            {
                request.Response =
                    $"ERROR: party postcondition failed; slot {slotIndex} contains actor {actualGuid}, expected {actorGuid}";
                return;
            }

            Logger.LogInfo(
                $"COMMAND hero_select: set_party_member slot={slotIndex} actor={actorGuid} previous={previousGuid}");

            QueueHeroSelectState(heroSelect);
            request.Response = "OK";
        }

        private void HandleHeroClearPartySlot(
            BridgeRequest request,
            string slotText)
        {
            UnityEngine.MonoBehaviour heroSelect = GetStableHeroSelectBhv();
            if (heroSelect == null)
            {
                request.Response =
                    "ERROR: hero select is not at an interactive boundary";
                return;
            }

            int slotIndex;
            if (!int.TryParse(slotText, out slotIndex))
            {
                request.Response =
                    "ERROR: clear_party_slot requires a valid slot";
                return;
            }

            uint actorGuid;
            int slotCount;
            if (!TryGetHeroSelectSlotGuid(
                    heroSelect,
                    slotIndex,
                    out actorGuid,
                    out slotCount))
            {
                request.Response =
                    $"ERROR: party slot {slotIndex} is out of range (slot count {slotCount})";
                return;
            }

            if (actorGuid == 0u)
            {
                QueueHeroSelectState(heroSelect);
                request.Response = "OK";
                return;
            }

            MethodInfo clearRosterSlot =
                FindMethodByNameAndParameterCount(
                    heroSelect.GetType(),
                    "ClearRosterSlotWithActorGuid",
                    1);

            if (clearRosterSlot == null)
            {
                request.Response =
                    "ERROR: HeroSelectBhv.ClearRosterSlotWithActorGuid was not found";
                return;
            }

            // ClearRosterSlotWithActorGuid is preferable to ClearRosterIndex:
            // the former also repopulates the visible roster slot with actor 0.
            clearRosterSlot.Invoke(
                heroSelect,
                new object[] { actorGuid });

            uint actualGuid;
            int ignoredSlotCount;
            if (!TryGetHeroSelectSlotGuid(
                    heroSelect,
                    slotIndex,
                    out actualGuid,
                    out ignoredSlotCount) ||
                actualGuid != 0u)
            {
                request.Response =
                    $"ERROR: party clear postcondition failed; slot {slotIndex} still contains actor {actualGuid}";
                return;
            }

            Logger.LogInfo(
                $"COMMAND hero_select: clear_party_slot slot={slotIndex} actor={actorGuid}");

            QueueHeroSelectState(heroSelect);
            request.Response = "OK";
        }

        private void HandleHeroSetPath(
            BridgeRequest request,
            string arguments)
        {
            UnityEngine.MonoBehaviour heroSelect = GetStableHeroSelectBhv();
            if (heroSelect == null)
            {
                request.Response =
                    "ERROR: hero select is not at an interactive boundary";
                return;
            }

            int separator = arguments == null
                ? -1
                : arguments.IndexOf(' ');

            if (separator <= 0 || separator >= arguments.Length - 1)
            {
                request.Response =
                    "ERROR: set_hero_path requires actor_guid and path_id";
                return;
            }

            uint actorGuid;
            string actorGuidText = arguments.Substring(0, separator);
            string pathId = arguments.Substring(separator + 1);

            if (!uint.TryParse(actorGuidText, out actorGuid) ||
                actorGuid == 0u ||
                string.IsNullOrEmpty(pathId))
            {
                request.Response =
                    "ERROR: set_hero_path requires valid actor_guid and path_id";
                return;
            }

            if (!HeroSelectContainsActorGuid(heroSelect, actorGuid))
            {
                request.Response =
                    $"ERROR: actor {actorGuid} is not available on this Crossroads screen";
                return;
            }

            ActorInstance actor =
                SingletonMonoBehaviour<Library<uint, ActorInstance>>
                    .Instance
                    .GetLibraryElement(actorGuid);

            if (actor == null)
            {
                request.Response = $"ERROR: actor {actorGuid} was not found";
                return;
            }

            object selectedPath = FindUnlockedHeroPath(actor, pathId);
            if (selectedPath == null)
            {
                request.Response =
                    $"ERROR: path '{pathId}' is not currently available for actor {actorGuid}";
                return;
            }

            MethodInfo setActorPath =
                FindMethodByNameAndParameterCount(
                    actor.GetType(),
                    "SetActorPath",
                    2);

            if (setActorPath == null)
            {
                request.Response =
                    "ERROR: ActorInstance.SetActorPath was not found";
                return;
            }

            setActorPath.Invoke(
                actor,
                new object[]
                {
                    selectedPath,
                    false
                });

            // Native HeroSelectBhv.SetSelectedActorPath() fires this after
            // ActorInstance.SetActorPath(). Preserve that semantic notification
            // without driving the Character Sheet or path-panel UI.
            TriggerHeroSelectPathChange(pathId);

            string currentPathId =
                GetStableObjectId(
                    GetMemberValue(actor, "ActorDataPath"));

            if (!string.Equals(
                    currentPathId,
                    pathId,
                    StringComparison.Ordinal))
            {
                request.Response =
                    $"ERROR: path postcondition failed; requested '{pathId}', current '{currentPathId}'";
                return;
            }

            Logger.LogInfo(
                $"COMMAND hero_select: set_hero_path actor={actorGuid} path='{pathId}'");

            // The Crossroads boundary remains open. Return the refreshed hero
            // inspection as the command result so host/agent immediately see
            // path-dependent stats and skill availability.
            QueueOutgoingMessage(BuildHeroInspection(actor, "hero_select"));
            request.Response = "OK";
        }

        private void HandleHeroSetSkills(
            BridgeRequest request,
            string arguments)
        {
            // Compatibility wrapper. Skill loadout mutation is shared by
            // Crossroads and node-entry preparation windows.
            HandleHeroManagementSetSkills(request, arguments);
        }

        private List<string> GetUnlockedCharacterSheetBaseSkillIds(
            ActorInstance actor)
        {
            List<string> result = new List<string>();
            HashSet<string> seen =
                new HashSet<string>(StringComparer.Ordinal);

            if (actor == null)
            {
                return result;
            }

            IReadOnlyList<string> unlockedCurrentIds =
                actor.GetUnlockedCharacterSheetCombatSkillIds();

            HashSet<string> unlockedCurrentSet =
                unlockedCurrentIds == null
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : new HashSet<string>(
                        unlockedCurrentIds,
                        StringComparer.Ordinal);

            IEnumerable combatSkills =
                GetMemberValue(actor, "m_CombatSkills") as IEnumerable;

            if (combatSkills == null)
            {
                return result;
            }

            // Preserve native m_CombatSkills order for presentation and numbered
            // CLI selection. Deduplicate only after canonicalizing to base IDs.
            foreach (object value in combatSkills)
            {
                SkillInstance skill = value as SkillInstance;
                if (skill == null ||
                    !unlockedCurrentSet.Contains(skill.SkillId))
                {
                    continue;
                }

                string baseId = actor.GetCombatBaseSkillId(skill);
                if (!string.IsNullOrEmpty(baseId) && seen.Add(baseId))
                {
                    result.Add(baseId);
                }
            }

            return result;
        }

        private HashSet<string> GetImmutableEquippedBaseSkillIds(
            ActorInstance actor)
        {
            HashSet<string> result =
                new HashSet<string>(StringComparer.Ordinal);

            if (actor == null)
            {
                return result;
            }

            IEnumerable combatSkills =
                GetMemberValue(actor, "m_CombatSkills") as IEnumerable;

            if (combatSkills == null)
            {
                return result;
            }

            foreach (object value in combatSkills)
            {
                SkillInstance skill = value as SkillInstance;
                if (skill == null ||
                    !skill.GetIsEquipped() ||
                    skill.GetCanChangeEquip(actor.ActorGuid, false))
                {
                    continue;
                }

                string baseId = actor.GetCombatBaseSkillId(skill);
                if (!string.IsNullOrEmpty(baseId))
                {
                    result.Add(baseId);
                }
            }

            return result;
        }

        private object FindUnlockedHeroPath(
            ActorInstance actor,
            string pathId)
        {
            if (actor == null || string.IsNullOrEmpty(pathId))
            {
                return null;
            }

            try
            {
                Type calculationType =
                    FindLoadedTypeBySimpleName("ActorPathCalculation");
                Type unlockUtilsType =
                    FindLoadedTypeBySimpleName("UnlockUtils");

                object unlockContext =
                    GetStaticMemberValue(
                        unlockUtilsType,
                        "ExpeditionProfileNotExpeditionUnlocked");

                MethodInfo getPaths = null;
                if (calculationType != null)
                {
                    foreach (MethodInfo method in calculationType.GetMethods(
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.NonPublic))
                    {
                        ParameterInfo[] parameters = method.GetParameters();
                        if (method.Name == "GetActorDataPaths" &&
                            parameters.Length == 2)
                        {
                            getPaths = method;
                            break;
                        }
                    }
                }

                IEnumerable paths =
                    getPaths == null
                        ? null
                        : getPaths.Invoke(
                            null,
                            new object[] { actor, true }) as IEnumerable;

                if (paths == null)
                {
                    return null;
                }

                foreach (object path in paths)
                {
                    if (path == null ||
                        !string.Equals(
                            GetStableObjectId(path),
                            pathId,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    bool unlocked = true;
                    MethodInfo isUnlocked =
                        FindMethodByNameAndParameterCount(
                            path.GetType(),
                            "GetIsUnlocked",
                            1);

                    if (isUnlocked != null && unlockContext != null)
                    {
                        unlocked =
                            Convert.ToBoolean(
                                isUnlocked.Invoke(
                                    path,
                                    new object[] { unlockContext }));
                    }

                    if (unlocked)
                    {
                        return path;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    $"HERO set path lookup failed: {ex.GetType().Name}: {ex.Message}");
            }

            return null;
        }

        private void TriggerHeroSelectPathChange(string pathId)
        {
            try
            {
                Type eventType =
                    FindLoadedTypeBySimpleName("EventHeroSelectPathChange");

                MethodInfo trigger =
                    FindMethodByNameAndParameterCount(
                        eventType,
                        "Trigger",
                        1);

                if (trigger != null)
                {
                    trigger.Invoke(
                        null,
                        new object[] { pathId });
                }
                else
                {
                    Logger.LogWarning(
                        "HERO set path: EventHeroSelectPathChange.Trigger was not found");
                }
            }
            catch (Exception ex)
            {
                // The actor mutation has already succeeded. Keep the authoritative
                // postcondition, but make a missing UI notification visible in log.
                Logger.LogWarning(
                    $"HERO set path event failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private JObject BuildHeroInspection(ActorInstance actor, string frontendState)
        {
            JObject result = new JObject
            {
                ["type"] = "hero_inspection",
                ["frontend_state"] = frontendState ?? "",
                ["actor_guid"] = actor.ActorGuid,
                ["actor_data_id"] = actor.ActorDataId ?? "",
                ["actor_name"] = actor.ActorName ?? "",
                ["path"] = BuildHeroPathInspection(actor),
                ["stats"] = BuildHeroStatsInspection(actor),
                ["skills"] = BuildHeroSkillsInspection(actor),
                ["quirks"] = BuildHeroQuirksInspection(actor),
                ["memories"] = BuildHeroMemoriesInspection(actor)
            };

            return result;
        }

        private JObject BuildHeroPathInspection(ActorInstance actor)
        {
            object currentPath = GetMemberValue(actor, "ActorDataPath");
            string currentPathId = GetStableObjectId(currentPath);
            JArray available = new JArray();

            try
            {
                Type calculationType = FindLoadedTypeBySimpleName("ActorPathCalculation");
                Type unlockUtilsType = FindLoadedTypeBySimpleName("UnlockUtils");

                object unlockContext =
                    GetStaticMemberValue(
                        unlockUtilsType,
                        "ExpeditionProfileNotExpeditionUnlocked");

                MethodInfo getPaths = null;
                if (calculationType != null)
                {
                    foreach (MethodInfo method in calculationType.GetMethods(
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        ParameterInfo[] parameters = method.GetParameters();
                        if (method.Name == "GetActorDataPaths" &&
                            parameters.Length == 2)
                        {
                            getPaths = method;
                            break;
                        }
                    }
                }

                IEnumerable paths =
                    getPaths == null
                        ? null
                        : getPaths.Invoke(
                            null,
                            new object[] { actor, true }) as IEnumerable;

                if (paths != null)
                {
                    foreach (object path in paths)
                    {
                        bool unlocked = true;
                        MethodInfo isUnlocked =
                            FindMethodByNameAndParameterCount(
                                path == null ? null : path.GetType(),
                                "GetIsUnlocked",
                                1);

                        if (isUnlocked != null && unlockContext != null)
                        {
                            unlocked =
                                Convert.ToBoolean(
                                    isUnlocked.Invoke(
                                        path,
                                        new object[] { unlockContext }));
                        }

                        if (!unlocked)
                        {
                            continue;
                        }

                        string pathId = GetStableObjectId(path);
                        if (!string.IsNullOrEmpty(pathId))
                        {
                            available.Add(pathId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    $"HERO inspect path list failed: {ex.GetType().Name}: {ex.Message}");
            }

            return new JObject
            {
                ["current_path_id"] = currentPathId,
                ["available_path_ids"] = available
            };
        }

        private JObject BuildHeroStatsInspection(ActorInstance actor)
        {
            JObject resistances = new JObject();

            try
            {
                string[] keys = actor.GetSubstatKeys(ActorStatType.RESISTANCE);
                if (keys != null)
                {
                    foreach (string key in keys)
                    {
                        resistances[key] =
                            actor.GetClampedStatValue(
                                ActorStatType.RESISTANCE,
                                key);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    $"HERO inspect resistances failed: {ex.GetType().Name}: {ex.Message}");
            }

            return new JObject
            {
                ["hp_max"] = actor.DisplayedHpMax,
                ["speed"] = actor.GetClampedStatValue(ActorStatType.SPEED),
                ["resistances"] = resistances
            };
        }

        private JObject BuildHeroSkillsInspection(ActorInstance actor)
        {
            JArray available = new JArray();
            JArray availableBase = new JArray();
            JArray equipped = new JArray();
            JArray debug = new JArray();

            IReadOnlyList<string> availableIds =
                actor.GetUnlockedCharacterSheetCombatSkillIds();

            if (availableIds != null)
            {
                foreach (string skillId in availableIds)
                {
                    available.Add(skillId);
                }
            }

            foreach (string baseId in
                GetUnlockedCharacterSheetBaseSkillIds(actor))
            {
                availableBase.Add(baseId);
            }

            IReadOnlyList<string> equippedIds =
                actor.GetBaseEquippedCombatSkillIds();

            if (equippedIds != null)
            {
                foreach (string skillId in equippedIds)
                {
                    equipped.Add(skillId);
                }
            }

            IEnumerable combatSkills =
                GetMemberValue(actor, "m_CombatSkills") as IEnumerable;

            if (combatSkills != null)
            {
                foreach (object value in combatSkills)
                {
                    SkillInstance skill = value as SkillInstance;
                    if (skill == null || !skill.GetIsEquipped())
                    {
                        continue;
                    }

                    string baseId = actor.GetCombatBaseSkillId(skill);

                    bool isAlwaysEquipped =
                        Convert.ToBoolean(
                            GetMemberValue(skill, "m_IsAlwaysEquipped")
                            ?? false);

                    SkillModifierInstance activeModifier =
                        skill.GetActiveSkillModifier(actor.ActorGuid);

                    bool hasActiveModifier = activeModifier != null;
                    bool forceEquip =
                        activeModifier != null &&
                        activeModifier.GetIsForceEquip();

                    debug.Add(
                        new JObject
                        {
                            ["current_skill_id"] = skill.SkillId,
                            ["base_skill_id"] = baseId ?? "",
                            ["is_always_equipped"] = isAlwaysEquipped,
                            ["has_active_modifier"] = hasActiveModifier,
                            ["force_equip"] = forceEquip,
                            ["can_unequip"] =
                                skill.GetCanChangeEquip(
                                    actor.ActorGuid,
                                    false)
                        });
                }
            }

            int equippedLimit =
                actor.ActorDataClass == null
                    ? 0
                    : actor.ActorDataClass.m_EquippedCombatSkillLimit;

            return new JObject
            {
                ["equipped_limit"] = equippedLimit,
                ["equipped"] = equipped,
                ["available"] = available,
                ["available_base"] = availableBase,
                ["debug_equipped"] = debug
            };
        }

        private JObject BuildHeroQuirksInspection(ActorInstance actor)
        {
            JArray positive = new JArray();
            JArray negative = new JArray();
            JArray diseaseOrCurse = new JArray();
            JArray unclassified = new JArray();

            if (actor.QuirkContainer == null)
            {
                return new JObject
                {
                    ["positive"] = positive,
                    ["negative"] = negative,
                    ["disease_or_curse"] = diseaseOrCurse,
                    ["unclassified"] = unclassified
                };
            }

            foreach (var instance in actor.QuirkContainer.GetInstances())
            {
                if (instance == null || instance.Definition == null)
                {
                    continue;
                }

                object definition = instance.Definition;
                string quirkId = GetStableObjectId(definition);
                bool isDisease = GetBooleanMember(definition, "IsDisease", false);
                bool isCurse = GetBooleanMember(definition, "IsCurse", false);
                JArray tags = BuildStringArray(GetMemberValue(definition, "Tags") as IEnumerable);

                JObject quirk = new JObject
                {
                    ["quirk_id"] = quirkId,
                    ["is_disease"] = isDisease,
                    ["is_curse"] = isCurse,
                    ["tags"] = tags
                };

                if (isDisease || isCurse)
                {
                    diseaseOrCurse.Add(quirk);
                }
                else if (StringArrayContains(tags, "positive"))
                {
                    positive.Add(quirk);
                }
                else if (StringArrayContains(tags, "negative"))
                {
                    negative.Add(quirk);
                }
                else
                {
                    unclassified.Add(quirk);
                }
            }

            return new JObject
            {
                ["positive"] = positive,
                ["negative"] = negative,
                ["disease_or_curse"] = diseaseOrCurse,
                ["unclassified"] = unclassified
            };
        }

        private JArray BuildHeroMemoriesInspection(ActorInstance actor)
        {
            JArray result = new JArray();

            try
            {
                foreach (var item in actor.GetMemoryInventory().GetValidItems())
                {
                    if (item == null)
                    {
                        continue;
                    }

                    object definition = item.GetItemDefinition();
                    JObject memory = new JObject
                    {
                        ["item_id"] = GetStableObjectId(definition),
                        ["definition_type"] =
                            definition == null
                                ? ""
                                : definition.GetType().FullName ?? definition.GetType().Name
                    };

                    string display = TryGetDisplayString(definition);
                    if (!string.IsNullOrEmpty(display))
                    {
                        memory["display"] = display;
                    }

                    result.Add(memory);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    $"HERO inspect memories failed: {ex.GetType().Name}: {ex.Message}");
            }

            return result;
        }
    }
}
