using System;
using System.Collections.Generic;

using Assets.Code.Game;
using Assets.Code.Actor;
using Assets.Code.Library;
using Assets.Code.UI.Screens;
using Assets.Code.UI.Widgets;
using Assets.Code.Utils;
using Assets.Code.Roster;

using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        // Shared expedition hero-management layer. Frontends decide whether a
        // hero is currently addressable; mutation/inspection below is frontend-
        // agnostic so the same actions can later be reused by the Inn.

        private IReadOnlyList<uint> GetCurrentPartyActorGuids()
        {
            GameTypeMgr gameTypeMgr = Singleton<GameTypeMgr>.Instance;
            if (gameTypeMgr == null || gameTypeMgr.RosterManager == null)
            {
                return new uint[0];
            }

            IReadOnlyList<uint> actorGuids =
                gameTypeMgr.RosterManager.GetActorGuids(RosterStatusType.PARTY);

            return actorGuids ?? new uint[0];
        }

        private bool IsCurrentPartyActor(uint actorGuid)
        {
            if (actorGuid == 0u)
            {
                return false;
            }

            IReadOnlyList<uint> actorGuids = GetCurrentPartyActorGuids();
            for (int i = 0; i < actorGuids.Count; i++)
            {
                if (actorGuids[i] == actorGuid)
                {
                    return true;
                }
            }

            return false;
        }

        private ActorInstance GetHeroActorInstance(uint actorGuid)
        {
            Library<uint, ActorInstance> library =
                SingletonMonoBehaviour<Library<uint, ActorInstance>>.Instance;

            return library == null
                ? null
                : library.GetLibraryElement(actorGuid);
        }

        private JObject BuildCurrentPartyManagementPayload()
        {
            JArray party = new JArray();
            JArray heroes = new JArray();
            JArray actions = new JArray();

            IReadOnlyList<uint> actorGuids = GetCurrentPartyActorGuids();

            for (int i = 0; i < actorGuids.Count; i++)
            {
                uint actorGuid = actorGuids[i];
                ActorInstance actor = GetHeroActorInstance(actorGuid);

                party.Add(
                    new JObject
                    {
                        ["slot"] = i,
                        ["actor_guid"] = actorGuid
                    });

                if (actor == null)
                {
                    continue;
                }

                heroes.Add(
                    new JObject
                    {
                        ["actor_guid"] = actorGuid,
                        ["actor_data_id"] = actor.ActorDataId ?? "",
                        ["actor_name"] = actor.ActorName ?? ""
                    });

                actions.Add(
                    new JObject
                    {
                        ["action"] = "inspect_hero",
                        ["actor_guid"] = actorGuid,
                        ["consumes_boundary"] = false
                    });

                actions.Add(
                    new JObject
                    {
                        ["action"] = "set_hero_skills",
                        ["actor_guid"] = actorGuid,
                        ["consumes_boundary"] = false
                    });
            }

            return new JObject
            {
                ["party"] = party,
                ["available_heroes"] = heroes,
                ["available_actions"] = actions
            };
        }

        private bool TryResolveHeroManagementActor(
            uint actorGuid,
            out ActorInstance actor,
            out string frontendState,
            out string error)
        {
            actor = null;
            frontendState = null;
            error = null;

            UnityEngine.MonoBehaviour heroSelect = GetStableHeroSelectBhv();
            if (heroSelect != null)
            {
                if (!HeroSelectContainsActorGuid(heroSelect, actorGuid))
                {
                    error =
                        $"actor {actorGuid} is not available on this Crossroads screen";
                    return false;
                }

                frontendState = "hero_select";
            }
            else
            {
                UiScreenBhv nodeScreen;
                EnterNodeScreenWidgetBhv nodeWidget;
                EnterNodeScreenPushParams nodeParams;

                if (TryGetStableNodeEntry(
                        out nodeScreen,
                        out nodeWidget,
                        out nodeParams))
                {
                    if (!IsCurrentPartyActor(actorGuid))
                    {
                        error =
                            $"actor {actorGuid} is not in the current expedition party";
                        return false;
                    }

                    frontendState = "node_entry";
                }
                else
                {
                    error =
                        "hero management is not at a supported interactive boundary";
                    return false;
                }
            }

            actor = GetHeroActorInstance(actorGuid);
            if (actor == null)
            {
                error = $"actor {actorGuid} was not found";
                return false;
            }

            return true;
        }

        private void HandleHeroManagementInspect(
            BridgeRequest request,
            string actorGuidText)
        {
            uint actorGuid;
            if (!uint.TryParse(actorGuidText, out actorGuid) || actorGuid == 0u)
            {
                request.Response =
                    "ERROR: inspect_hero requires a valid actor_guid";
                return;
            }

            ActorInstance actor;
            string frontendState;
            string error;

            if (!TryResolveHeroManagementActor(
                    actorGuid,
                    out actor,
                    out frontendState,
                    out error))
            {
                request.Response = "ERROR: " + error;
                return;
            }

            QueueOutgoingMessage(
                BuildHeroInspection(actor, frontendState));

            Logger.LogInfo(
                $"HERO inspect: frontend={frontendState} actor={actorGuid} id={actor.ActorDataId}");

            request.Response = "OK";
        }

        private void HandleHeroManagementSetSkills(
            BridgeRequest request,
            string arguments)
        {
            string[] parts =
                (arguments ?? string.Empty).Split(
                    new[] { ' ' },
                    StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 1)
            {
                request.Response =
                    "ERROR: set_hero_skills requires actor_guid and zero or more base skill ids";
                return;
            }

            uint actorGuid;
            if (!uint.TryParse(parts[0], out actorGuid) || actorGuid == 0u)
            {
                request.Response =
                    "ERROR: set_hero_skills requires a valid actor_guid";
                return;
            }

            ActorInstance actor;
            string frontendState;
            string error;

            if (!TryResolveHeroManagementActor(
                    actorGuid,
                    out actor,
                    out frontendState,
                    out error))
            {
                request.Response = "ERROR: " + error;
                return;
            }

            List<string> requested = new List<string>();
            HashSet<string> requestedSet =
                new HashSet<string>(StringComparer.Ordinal);

            for (int i = 1; i < parts.Length; i++)
            {
                string skillId = parts[i];
                if (string.IsNullOrEmpty(skillId))
                {
                    continue;
                }

                if (!requestedSet.Add(skillId))
                {
                    request.Response =
                        $"ERROR: duplicate base skill id '{skillId}'";
                    return;
                }

                requested.Add(skillId);
            }

            int equippedLimit =
                actor.ActorDataClass == null
                    ? 0
                    : actor.ActorDataClass.m_EquippedCombatSkillLimit;

            if (equippedLimit > 0 && requested.Count > equippedLimit)
            {
                request.Response =
                    $"ERROR: requested {requested.Count} skills, equip limit is {equippedLimit}";
                return;
            }

            HashSet<string> availableBaseIds =
                new HashSet<string>(
                    GetUnlockedCharacterSheetBaseSkillIds(actor),
                    StringComparer.Ordinal);

            foreach (string requestedId in requested)
            {
                if (!availableBaseIds.Contains(requestedId))
                {
                    request.Response =
                        $"ERROR: base skill '{requestedId}' is not currently available for actor {actorGuid}";
                    return;
                }
            }

            HashSet<string> immutableEquippedBaseIds =
                GetImmutableEquippedBaseSkillIds(actor);

            immutableEquippedBaseIds.RemoveWhere(
                skillId => !availableBaseIds.Contains(skillId));

            HashSet<string> finalRequestedSet =
                new HashSet<string>(requestedSet, StringComparer.Ordinal);
            finalRequestedSet.UnionWith(immutableEquippedBaseIds);

            if (equippedLimit > 0 && finalRequestedSet.Count > equippedLimit)
            {
                request.Response =
                    $"ERROR: requested loadout plus forced skills needs {finalRequestedSet.Count} slots, equip limit is {equippedLimit}";
                return;
            }

            actor.UnequipAllPossibleCombatSkills();
            actor.SetBaseCombatSkillIdsEquipped(requested);

            IReadOnlyList<string> actualReadOnly =
                actor.GetBaseEquippedCombatSkillIds();

            List<string> actual =
                actualReadOnly == null
                    ? new List<string>()
                    : new List<string>(actualReadOnly);

            HashSet<string> actualSet =
                new HashSet<string>(actual, StringComparer.Ordinal);

            foreach (string requestedId in requested)
            {
                if (!actualSet.Contains(requestedId))
                {
                    request.Response =
                        $"ERROR: skill loadout postcondition failed; '{requestedId}' was not equipped";
                    return;
                }
            }

            foreach (string actualId in actual)
            {
                if (!requestedSet.Contains(actualId) &&
                    !immutableEquippedBaseIds.Contains(actualId))
                {
                    request.Response =
                        $"ERROR: skill loadout postcondition failed; unexpected equipped skill '{actualId}'";
                    return;
                }
            }

            List<string> expectedMutableOrder = new List<string>();
            foreach (string requestedId in requested)
            {
                if (!immutableEquippedBaseIds.Contains(requestedId))
                {
                    expectedMutableOrder.Add(requestedId);
                }
            }

            List<string> actualMutableOrder = new List<string>();
            foreach (string actualId in actual)
            {
                if (requestedSet.Contains(actualId) &&
                    !immutableEquippedBaseIds.Contains(actualId))
                {
                    actualMutableOrder.Add(actualId);
                }
            }

            if (expectedMutableOrder.Count != actualMutableOrder.Count)
            {
                request.Response =
                    "ERROR: skill loadout postcondition failed; mutable equipped order length differs";
                return;
            }

            for (int i = 0; i < expectedMutableOrder.Count; i++)
            {
                if (!string.Equals(
                        expectedMutableOrder[i],
                        actualMutableOrder[i],
                        StringComparison.Ordinal))
                {
                    request.Response =
                        "ERROR: skill loadout postcondition failed; equipped order differs from requested order";
                    return;
                }
            }

            Logger.LogInfo(
                $"COMMAND {frontendState}: set_hero_skills actor={actorGuid} count={requested.Count}");

            QueueOutgoingMessage(
                BuildHeroInspection(actor, frontendState));
            request.Response = "OK";
        }
    }
}
