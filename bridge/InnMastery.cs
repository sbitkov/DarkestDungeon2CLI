using System;
using System.Collections.Generic;

using Assets.Code.Actor;
using Assets.Code.Analytics;
using Assets.Code.Cost;
using Assets.Code.Game;
using Assets.Code.Inn;
using Assets.Code.Item.Events;
using Assets.Code.Library;
using Assets.Code.Run;
using Assets.Code.Skill;
using Assets.Code.Skill.Events;
using Assets.Code.Source;
using Assets.Code.UI;
using Assets.Code.Unlock;
using Assets.Code.Utils;

using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        private bool CanUseInnMastery()
        {
            if (!IsStableInn())
            {
                return false;
            }

            InnBhv innBhv = Singleton<InnBhv>.Instance;
            if (innBhv == null || innBhv.GetInnInstance() == null)
            {
                return false;
            }

            return innBhv.GetInnInstance().GetIsInnFeatureEnabled(
                InnFeatureType.TRAINER);
        }

        private JObject BuildInnMasterySkillJson(
            ActorInstance actor,
            string skillId)
        {
            if (actor == null || string.IsNullOrEmpty(skillId))
            {
                return null;
            }

            SkillInstance skillInstance = actor.GetCombatSkillInstance(skillId);
            string baseSkillId = skillId;
            bool upgraded = false;

            if (skillInstance != null)
            {
                string resolvedBase = actor.GetCombatBaseSkillId(skillInstance);
                if (!string.IsNullOrEmpty(resolvedBase))
                {
                    baseSkillId = resolvedBase;
                }
                upgraded = skillInstance.GetIsUpgraded();
            }

            UnlockDefinition upgradeUnlock =
                SkillUtils.GetUnlockFromSkillId(skillId);

            string upgradeSkillId =
                upgradeUnlock == null ? string.Empty : upgradeUnlock.m_Id;

            bool canAfford =
                !upgraded &&
                upgradeUnlock != null &&
                CostCalculation.CanAffordCost(upgradeUnlock.CostDefinition) &&
                Singleton<GameTypeMgr>.Instance.RunValues.GetValue(
                    RunValueType.HERO_UPGRADE_POINTS) > 0f;

            string costText = string.Empty;
            if (upgradeUnlock != null && upgradeUnlock.CostDefinition != null)
            {
                try
                {
                    costText = CostDescription.GetStoreBuyDescription(
                        upgradeUnlock.CostDefinition,
                        1f,
                        showStrikethrough: false) ?? string.Empty;
                }
                catch
                {
                }
            }

            return new JObject
            {
                ["skill_id"] = skillId,
                ["base_skill_id"] = baseSkillId,
                ["upgrade_skill_id"] = upgradeSkillId,
                ["upgraded"] = upgraded,
                ["can_upgrade"] = canAfford,
                ["cost_text"] = costText,
                ["current"] = BuildSkillInfo(actor, skillId),
                ["upgrade"] =
                    string.IsNullOrEmpty(upgradeSkillId)
                        ? null
                        : BuildSkillInfo(actor, upgradeSkillId)
            };
        }

        private JObject BuildInnMasteryStateMessage()
        {
            if (!CanUseInnMastery())
            {
                return null;
            }

            GameTypeMgr gameType = Singleton<GameTypeMgr>.Instance;
            JArray heroes = new JArray();
            JArray actions = new JArray();

            if (gameType.RosterManager != null)
            {
                foreach (ActorInstance actor in
                    gameType.RosterManager.GetPartyActors())
                {
                    if (actor == null)
                    {
                        continue;
                    }

                    JArray skills = new JArray();
                    List<string> skillIds =
                        SkillSelectionUtils.GetSkillIdsFromSelectionType(
                            actor,
                            SkillSelectionType.UPGRADE_COMBAT_SKILLS);

                    foreach (string skillId in skillIds)
                    {
                        JObject skill =
                            BuildInnMasterySkillJson(actor, skillId);
                        if (skill == null)
                        {
                            continue;
                        }

                        skills.Add(skill);

                        if ((bool)skill["can_upgrade"])
                        {
                            actions.Add(
                                new JObject
                                {
                                    ["action"] = "inn_mastery_upgrade_skill",
                                    ["actor_guid"] = actor.ActorGuid,
                                    ["skill_id"] = skillId,
                                    ["consumes_boundary"] = false
                                });
                        }
                    }

                    heroes.Add(
                        new JObject
                        {
                            ["actor_guid"] = actor.ActorGuid,
                            ["actor_name"] = actor.ActorName ?? string.Empty,
                            ["actor_data_id"] = actor.ActorDataId ?? string.Empty,
                            ["path_id"] =
                                actor.ActorDataPath == null
                                    ? string.Empty
                                    : actor.ActorDataPath.Id,
                            ["is_hire_class"] =
                                actor.ActorDataClass != null &&
                                actor.ActorDataClass.IsHireClass,
                            ["skills"] = skills
                        });
                }
            }

            actions.Add(
                new JObject
                {
                    ["action"] = "inn_mastery_state",
                    ["consumes_boundary"] = false
                });
            actions.Add(
                new JObject
                {
                    ["action"] = "inn_mastery_back",
                    ["consumes_boundary"] = false
                });

            return new JObject
            {
                ["type"] = "inn_mastery_state",
                ["frontend_state"] = "inn_mastery",
                ["current_mode"] = "inn",
                ["mastery_points"] =
                    (int)gameType.RunValues.GetValue(
                        RunValueType.HERO_UPGRADE_POINTS),
                ["heroes"] = heroes,
                ["available_actions"] = actions
            };
        }

        private void HandleInnMasteryState(BridgeRequest request)
        {
            JObject state = BuildInnMasteryStateMessage();
            if (state == null)
            {
                request.Response = "ERROR: inn mastery trainer is unavailable";
                return;
            }

            Logger.LogInfo(
                $"COMMAND inn: mastery points={state["mastery_points"]}");
            QueueOutgoingMessage(state);
            request.Response = "OK";
        }

        private bool ApplyInnMasteryUpgrade(
            ActorInstance actor,
            string skillId,
            bool spendCost)
        {
            if (actor == null || string.IsNullOrEmpty(skillId))
            {
                return false;
            }

            SkillInstance current = actor.GetCombatSkillInstance(skillId);
            if (current == null || current.GetIsUpgraded())
            {
                return false;
            }

            UnlockDefinition unlock = SkillUtils.GetUnlockFromSkillId(skillId);
            if (unlock == null)
            {
                return false;
            }

            if (spendCost &&
                (unlock.CostDefinition == null ||
                 !CostCalculation.AttemptSpendCost(
                     unlock.CostDefinition,
                     SourceType.STORE)))
            {
                return false;
            }

            actor.UnlockSkill(unlock, SourceType.INN);
            EventSkillUpgradeCommitted.Trigger(skillId, actor.ActorGuid);

            try
            {
                SingletonMonoBehaviour<AnalyticsBhv>.Instance.CacheSkillUpgrade(
                    actor.ActorDataId,
                    skillId);
            }
            catch
            {
            }

            return true;
        }

        private void HandleInnMasteryUpgradeSkill(
            BridgeRequest request,
            string arguments)
        {
            if (!CanUseInnMastery())
            {
                request.Response = "ERROR: inn mastery trainer is unavailable";
                return;
            }

            string[] parts = (arguments ?? string.Empty).Split(
                new[] { ' ' },
                2,
                StringSplitOptions.RemoveEmptyEntries);

            uint actorGuid;
            if (parts.Length != 2 ||
                !uint.TryParse(parts[0], out actorGuid) ||
                string.IsNullOrEmpty(parts[1]))
            {
                request.Response =
                    "ERROR: mastery upgrade requires actor_guid and skill_id";
                return;
            }

            string skillId = parts[1];
            ActorInstance actor =
                SingletonMonoBehaviour<Library<uint, ActorInstance>>
                    .Instance
                    .GetLibraryElement(actorGuid);

            if (actor == null)
            {
                request.Response = $"ERROR: actor {actorGuid} was not found";
                return;
            }

            bool actorInParty = false;
            foreach (ActorInstance partyActor in
                Singleton<GameTypeMgr>.Instance.RosterManager.GetPartyActors())
            {
                if (partyActor != null && partyActor.ActorGuid == actorGuid)
                {
                    actorInParty = true;
                    break;
                }
            }

            if (!actorInParty ||
                (actor.ActorDataClass != null && actor.ActorDataClass.IsHireClass))
            {
                request.Response =
                    $"ERROR: actor {actorGuid} cannot use the inn mastery trainer";
                return;
            }

            List<string> visibleSkills =
                SkillSelectionUtils.GetSkillIdsFromSelectionType(
                    actor,
                    SkillSelectionType.UPGRADE_COMBAT_SKILLS);

            if (!visibleSkills.Contains(skillId))
            {
                request.Response =
                    $"ERROR: skill '{skillId}' is not offered for actor {actorGuid}";
                return;
            }

            if (Singleton<GameTypeMgr>.Instance.RunValues.GetValue(
                    RunValueType.HERO_UPGRADE_POINTS) <= 0f)
            {
                request.Response = "ERROR: no mastery points remain";
                return;
            }

            JObject before = BuildInnMasterySkillJson(actor, skillId);
            if (before == null || !(bool)before["can_upgrade"])
            {
                request.Response =
                    $"ERROR: skill '{skillId}' cannot currently be upgraded";
                return;
            }

            ActorDataSkill skillData =
                SingletonMonoBehaviour<Library<string, ActorDataSkill>>
                    .Instance
                    .GetLibraryElement(skillId);

            if (!ApplyInnMasteryUpgrade(actor, skillId, spendCost: true))
            {
                request.Response =
                    $"ERROR: failed to upgrade skill '{skillId}'";
                return;
            }

            if (skillData != null && skillData.ModeLinkedActorDataSkill != null)
            {
                ApplyInnMasteryUpgrade(
                    actor,
                    skillData.ModeLinkedActorDataSkill.Id,
                    spendCost: false);
            }

            EventSkillUpgradeExecuted.Trigger(skillId, actor.ActorGuid);
            EventUpdatePlayerCurrency.Trigger();

            Logger.LogInfo(
                $"COMMAND inn_mastery: upgrade_skill actor={actorGuid} " +
                $"skill='{skillId}'");

            QueueOutgoingMessage(BuildInnMasteryStateMessage());
            request.Response = "OK";
        }

        private void HandleInnMasteryBack(BridgeRequest request)
        {
            if (!IsStableInn())
            {
                request.Response = "ERROR: inn is unavailable";
                return;
            }

            Logger.LogInfo("COMMAND inn_mastery: back -> inn");
            QueueOutgoingMessage(BuildInnReadyMessage());
            request.Response = "OK";
        }
    }
}
