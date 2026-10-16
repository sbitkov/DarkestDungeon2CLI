using System;
using System.Collections.Generic;

using Assets.Code.Game;
using Assets.Code.Inn;
using Assets.Code.Item;
using Assets.Code.Library;
using Assets.Code.Locale;
using Assets.Code.Loot;
using Assets.Code.Map.Generation.Biome;
using Assets.Code.Run;
using Assets.Code.Utils;

using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        private bool CanUseInnRouteChoice()
        {
            if (!IsStableInn())
            {
                return false;
            }

            InnBhv inn = Singleton<InnBhv>.Instance;
            return inn != null && inn.GetBiomeChoices() != null;
        }

        private static string SafeLocalizedString(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            try
            {
                return Singleton<Localization>.Instance.GetString(key) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private bool IsInnBiomeChoiceSelectable(
            BiomeChoice choice,
            out string blockedReason,
            out string requiredSlotType)
        {
            blockedReason = string.Empty;
            requiredSlotType = string.Empty;

            if (choice == null || choice.m_BiomeGenerationData == null)
            {
                blockedReason = "invalid_choice";
                return false;
            }

            BiomeType biomeType = choice.m_BiomeGenerationData.GetBiomeType();
            if (biomeType == null || biomeType.m_SubType != BiomeSubType.END)
            {
                return true;
            }

            if (!SingletonMonoBehaviour<RunBhv>.HasInstance() ||
                SingletonMonoBehaviour<RunBhv>.Instance.RunManager == null ||
                SingletonMonoBehaviour<RunBhv>.Instance.RunManager.Boss == null)
            {
                return true;
            }

            ItemSlotType required =
                SingletonMonoBehaviour<RunBhv>.Instance
                    .GetEndBiomeRequiredStageCoachItemSlotType();

            if (required == null)
            {
                // The first confession uses this path: the end biome has no
                // required stagecoach item slot, so the Mountain is directly
                // selectable without a trophy.
                return true;
            }

            requiredSlotType = required.GetName();

            GameTypeMgr gameType = Singleton<GameTypeMgr>.Instance;
            if (gameType == null || gameType.StageCoach == null)
            {
                blockedReason = "stagecoach_unavailable";
                return false;
            }

            ItemInventory equipped = gameType.StageCoach.GetSlotInventory(required);
            bool hasEquippedRequiredItem =
                equipped != null && equipped.GetNumberOfFilledSlots() > 0;

            if (!hasEquippedRequiredItem)
            {
                blockedReason = "required_stagecoach_item_not_equipped";
                return false;
            }

            return true;
        }

        private JArray BuildInnRouteGoalRewards(BiomeChoice choice)
        {
            JArray rewards = new JArray();
            if (choice == null ||
                choice.m_BiomeGoalReward == null ||
                choice.m_BiomeGoalReward.LootPreRoll == null)
            {
                return rewards;
            }

            foreach (Reward<LootType> reward in
                choice.m_BiomeGoalReward.LootPreRoll.LootRewards)
            {
                if (reward == null)
                {
                    continue;
                }

                JObject rewardJson = new JObject
                {
                    ["qty"] = reward.m_qty,
                    ["loot_type"] = reward.m_type.ToString().ToLowerInvariant(),
                    ["id"] = reward.m_id ?? string.Empty
                };

                try
                {
                    if (reward.m_type == LootType.PROVISION &&
                        CustomEnum<RunValueType>.Cast(reward.m_id) ==
                            RunValueType.HERO_UPGRADE_POINTS)
                    {
                        rewardJson["text"] = string.Format(
                            SafeLocalizedString(
                                "loot_rewards_hero_points_textless_label"),
                            reward.m_qty);
                    }
                    else if (reward.m_type == LootType.ITEM)
                    {
                        ItemDefinition definition =
                            SingletonMonoBehaviour<Library<string, ItemDefinition>>
                                .Instance.GetLibraryElement(reward.m_id);
                        ItemInstance item = new ItemInstance(definition, reward.m_qty);
                        rewardJson["text"] =
                            ItemDescription.GetTitle(definition, reward.m_qty) ??
                            item.GetName() ?? reward.m_id;
                    }
                }
                catch
                {
                }

                rewards.Add(rewardJson);
            }

            return rewards;
        }

        private JObject BuildInnRouteChoiceJson(
            BiomeChoice choice,
            int index,
            int selectedIndex)
        {
            BiomeGenerationData generation = choice.m_BiomeGenerationData;
            BiomeType biomeType = generation.GetBiomeType();
            bool isEnd = biomeType != null && biomeType.m_SubType == BiomeSubType.END;

            string blockedReason;
            string requiredSlotType;
            bool selectable = IsInnBiomeChoiceSelectable(
                choice,
                out blockedReason,
                out requiredSlotType);

            string biomeId = biomeType == null ? string.Empty : biomeType.GetName();
            string biomeName = biomeType == null
                ? string.Empty
                : SafeLocalizedString("biome_name_" + biomeType.ToString());

            JObject result = new JObject
            {
                ["option_index"] = index,
                ["biome_id"] = biomeId,
                ["biome_name"] = biomeName,
                ["run_length"] = generation.GetRunLength(),
                ["is_end_biome"] = isEnd,
                ["selected"] = index == selectedIndex,
                ["selectable"] = selectable
            };

            if (!string.IsNullOrEmpty(blockedReason))
            {
                result["blocked_reason"] = blockedReason;
            }
            if (!string.IsNullOrEmpty(requiredSlotType))
            {
                result["required_stagecoach_slot_type"] = requiredSlotType;
            }

            if (choice.m_BiomeModifier != null)
            {
                string modifierId = choice.m_BiomeModifier.m_Id ?? string.Empty;
                result["modifier_id"] = modifierId;
                result["modifier_name"] =
                    SafeLocalizedString("biome_mutator_" + modifierId);
                try
                {
                    result["modifier_description"] =
                        BiomeDescription.GetBiomeModifierDescription(
                            choice.m_BiomeModifier) ?? string.Empty;
                }
                catch
                {
                }
            }

            if (choice.m_BiomeGoal != null)
            {
                string goalId = choice.m_BiomeGoal.m_Id ?? string.Empty;
                result["goal_id"] = goalId;
                result["goal_name"] =
                    SafeLocalizedString("biome_goal_" + goalId);
                result["goal_description"] =
                    SafeLocalizedString("biome_goal_description_" + goalId);
                result["goal_rewards"] = BuildInnRouteGoalRewards(choice);
            }

            return result;
        }

        private JObject BuildInnRouteChoiceState()
        {
            InnBhv inn = Singleton<InnBhv>.Instance;
            IReadOnlyList<BiomeChoice> choices = inn.GetBiomeChoices();
            int selectedIndex = inn.GetSelectedBiomeChoiceIndex();

            JArray choiceArray = new JArray();
            JArray actions = new JArray();

            if (choices != null)
            {
                for (int i = 0; i < choices.Count; i++)
                {
                    BiomeChoice choice = choices[i];
                    JObject choiceJson =
                        BuildInnRouteChoiceJson(choice, i, selectedIndex);
                    choiceArray.Add(choiceJson);

                    if (choiceJson.Value<bool>("selectable"))
                    {
                        actions.Add(
                            new JObject
                            {
                                ["action"] = "inn_route_choose",
                                ["option_index"] = i,
                                ["biome_id"] =
                                    choiceJson.Value<string>("biome_id") ??
                                    string.Empty
                            });
                    }
                }
            }

            actions.Add(new JObject { ["action"] = "inn_route_back" });

            return new JObject
            {
                ["type"] = "inn_route_choice_state",
                ["frontend_state"] = "inn_route_choice",
                ["current_mode"] = "inn",
                ["selected_index"] = selectedIndex,
                ["choices"] = choiceArray,
                ["available_actions"] = actions
            };
        }

        private void HandleInnRouteChoiceState(BridgeRequest request)
        {
            if (!CanUseInnRouteChoice())
            {
                request.Response = "ERROR: inn route choice is unavailable";
                return;
            }

            Logger.LogInfo("COMMAND inn: route");
            QueueOutgoingMessage(BuildInnRouteChoiceState());
            request.Response = "OK";
        }

        private void HandleInnRouteChoose(
            BridgeRequest request,
            string argument)
        {
            if (!CanUseInnRouteChoice())
            {
                request.Response = "ERROR: inn route choice is unavailable";
                return;
            }

            int index;
            if (!int.TryParse(argument, out index))
            {
                request.Response = "ERROR: invalid inn route option index";
                return;
            }

            InnBhv inn = Singleton<InnBhv>.Instance;
            IReadOnlyList<BiomeChoice> choices = inn.GetBiomeChoices();
            if (choices == null || index < 0 || index >= choices.Count)
            {
                request.Response = "ERROR: inn route option is out of range";
                return;
            }

            string blockedReason;
            string requiredSlotType;
            if (!IsInnBiomeChoiceSelectable(
                    choices[index],
                    out blockedReason,
                    out requiredSlotType))
            {
                request.Response =
                    "ERROR: inn route option is not selectable: " +
                    blockedReason;
                QueueOutgoingMessage(BuildInnRouteChoiceState());
                return;
            }

            BiomeType biomeType =
                choices[index].m_BiomeGenerationData.GetBiomeType();

            Logger.LogInfo(
                $"COMMAND inn_route_choice: choose_route option={index} " +
                $"biome={biomeType?.GetName()}");

            // This is the same model mutation used by
            // SubScreenBiomeChoiceBhv.SetSelectedBiome().  It also emits
            // EventNextBiomeSelected and saves the current game mode.
            inn.SetSelectedBiomeChoiceIndex(index);

            _innReadySent = true;
            _hostReadyCatchupRequested = false;
            QueueOutgoingMessage(BuildInnReadyMessage());
            request.Response = "OK";
        }

        private void HandleInnRouteBack(BridgeRequest request)
        {
            if (!IsStableInn())
            {
                request.Response = "ERROR: inn is unavailable";
                return;
            }

            Logger.LogInfo("COMMAND inn_route_choice: back -> inn");
            _innReadySent = true;
            _hostReadyCatchupRequested = false;
            QueueOutgoingMessage(BuildInnReadyMessage());
            request.Response = "OK";
        }
    }
}
