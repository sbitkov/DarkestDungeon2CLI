using System;
using System.Collections.Generic;

using Assets.Code.Campaign;
using Assets.Code.Game;
using Assets.Code.Game.StageCoach;
using Assets.Code.Inn;
using Assets.Code.Item;
using Assets.Code.Item.Events;
using Assets.Code.Locale;
using Assets.Code.Source;
using Assets.Code.UI.Managers;
using Assets.Code.Utils;
using Assets.Code.UI.Widgets;

using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        private const string ConfirmationContextInnWainwrightPermanentEquip =
            "inn_wainwright_permanent_equip";

        private bool _innWainwrightReturnPending;

        private bool CanUseInnWainwright()
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

            if (!innBhv.GetInnInstance().GetIsInnFeatureEnabled(
                    InnFeatureType.WAINWRIGHT))
            {
                return false;
            }

            GameTypeMgr gameType = Singleton<GameTypeMgr>.Instance;
            return gameType != null && gameType.StageCoach != null;
        }

        private static string GetStageCoachSlotName(ItemSlotType slotType)
        {
            if (slotType == ItemSlotType.GENERAL)
            {
                return "general";
            }
            if (slotType == ItemSlotType.TROPHY)
            {
                return "trophy";
            }
            if (slotType == ItemSlotType.PET)
            {
                return "pet";
            }
            if (slotType == ItemSlotType.FLAME)
            {
                return "flame";
            }
            return "unknown";
        }

        private static ItemSlotType ParseStageCoachSlotType(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "general":
                    return ItemSlotType.GENERAL;
                case "trophy":
                    return ItemSlotType.TROPHY;
                case "pet":
                    return ItemSlotType.PET;
                case "flame":
                    return ItemSlotType.FLAME;
                default:
                    return null;
            }
        }

        private JObject BuildInnWainwrightItemJson(
            ItemInventory inventory,
            int slot,
            ItemSlotType slotType,
            bool includeEmpty)
        {
            JObject result = new JObject
            {
                ["slot"] = slot,
                ["slot_type"] = GetStageCoachSlotName(slotType)
            };

            if (inventory == null ||
                slot < 0 ||
                slot >= inventory.GetNumberOfTotalSlots())
            {
                result["empty"] = true;
                return includeEmpty ? result : null;
            }

            IReadOnlyItemInstance item = inventory.GetItem(slot);
            if (!ItemUtils.IsValid(item))
            {
                result["empty"] = true;
                return includeEmpty ? result : null;
            }

            ItemDefinition definition = item.GetItemDefinition();
            string title = string.Empty;
            string description = string.Empty;

            try
            {
                title = ItemDescription.GetTitle(definition, item.GetQty()) ?? string.Empty;
            }
            catch
            {
                title = item.GetName() ?? string.Empty;
            }

            try
            {
                description = ItemDescription.GetDescription(
                    item,
                    includeRunStatModification: true,
                    canSell: false,
                    showDiscard: false,
                    hideTitle: true) ?? string.Empty;
            }
            catch
            {
            }

            result["empty"] = false;
            result["item_id"] = definition == null ? string.Empty : definition.m_id;
            result["name"] = title;
            result["qty"] = item.GetQty();
            result["description"] = description;
            result["unequip_invalid"] =
                definition != null && definition.m_IsUnequipInvalid;
            result["can_unequip"] =
                definition != null && !definition.m_IsUnequipInvalid;

            return result;
        }

        private JArray BuildStageCoachSlotArray(ItemSlotType slotType)
        {
            JArray result = new JArray();
            StageCoach stageCoach = Singleton<GameTypeMgr>.Instance.StageCoach;
            ItemInventory inventory = stageCoach.GetSlotInventory(slotType);

            if (inventory == null)
            {
                return result;
            }

            for (int slot = 0; slot < inventory.GetNumberOfTotalSlots(); slot++)
            {
                result.Add(
                    BuildInnWainwrightItemJson(
                        inventory,
                        slot,
                        slotType,
                        includeEmpty: true));
            }

            return result;
        }

        private bool CanEquipStageCoachItemIntoSlot(
            ItemDefinition definition,
            int targetSlot)
        {
            if (definition == null ||
                definition.m_type != ItemType.STAGE_COACH_UPGRADE)
            {
                return false;
            }

            ItemSlotType slotType = definition.m_slot;
            StageCoach stageCoach = Singleton<GameTypeMgr>.Instance.StageCoach;
            ItemInventory destination = stageCoach.GetSlotInventory(slotType);

            if (destination == null ||
                targetSlot < 0 ||
                targetSlot >= destination.GetNumberOfTotalSlots())
            {
                return false;
            }

            IReadOnlyItemInstance current = destination.GetItem(targetSlot);
            if (ItemUtils.IsValid(current))
            {
                ItemDefinition currentDefinition = current.GetItemDefinition();
                if (currentDefinition != null && currentDefinition.m_IsUnequipInvalid)
                {
                    return false;
                }
            }
            else
            {
                InnInstance inn = Singleton<InnBhv>.Instance.GetInnInstance();
                if (inn != null && !inn.GetCanEquipStageCoachItem(definition))
                {
                    return false;
                }
            }

            return true;
        }

        private JObject BuildInnWainwrightStateMessage()
        {
            if (!CanUseInnWainwright())
            {
                return null;
            }

            GameTypeMgr gameType = Singleton<GameTypeMgr>.Instance;
            ItemInventory playerInventory = gameType.PlayerInventory;
            StageCoach stageCoach = gameType.StageCoach;
            JArray inventoryItems = new JArray();
            JArray actions = new JArray();

            if (playerInventory != null)
            {
                for (int slot = 0;
                     slot < playerInventory.GetNumberOfTotalSlots();
                     slot++)
                {
                    IReadOnlyItemInstance item = playerInventory.GetItem(slot);
                    if (!ItemUtils.IsValid(item))
                    {
                        continue;
                    }

                    ItemDefinition definition = item.GetItemDefinition();
                    if (definition == null ||
                        definition.m_type != ItemType.STAGE_COACH_UPGRADE)
                    {
                        continue;
                    }

                    JObject itemJson = BuildInnWainwrightItemJson(
                        playerInventory,
                        slot,
                        definition.m_slot,
                        includeEmpty: false);
                    itemJson["player_slot"] = slot;
                    itemJson["permanent_when_equipped"] =
                        definition.m_IsUnequipInvalid;
                    inventoryItems.Add(itemJson);

                    ItemInventory destination =
                        stageCoach.GetSlotInventory(definition.m_slot);
                    if (destination == null)
                    {
                        continue;
                    }

                    for (int targetSlot = 0;
                         targetSlot < destination.GetNumberOfTotalSlots();
                         targetSlot++)
                    {
                        if (!CanEquipStageCoachItemIntoSlot(
                                definition,
                                targetSlot))
                        {
                            continue;
                        }

                        actions.Add(
                            new JObject
                            {
                                ["action"] = "inn_wainwright_equip",
                                ["player_slot"] = slot,
                                ["slot_type"] =
                                    GetStageCoachSlotName(definition.m_slot),
                                ["stagecoach_slot"] = targetSlot,
                                ["item_id"] = definition.m_id,
                                ["requires_confirmation"] =
                                    definition.m_IsUnequipInvalid,
                                ["consumes_boundary"] = false
                            });
                    }
                }
            }

            foreach (ItemSlotType slotType in new[]
            {
                ItemSlotType.GENERAL,
                ItemSlotType.TROPHY,
                ItemSlotType.PET,
                ItemSlotType.FLAME
            })
            {
                ItemInventory inventory = stageCoach.GetSlotInventory(slotType);
                if (inventory == null)
                {
                    continue;
                }

                for (int slot = 0;
                     slot < inventory.GetNumberOfTotalSlots();
                     slot++)
                {
                    IReadOnlyItemInstance item = inventory.GetItem(slot);
                    if (!ItemUtils.IsValid(item))
                    {
                        continue;
                    }

                    ItemDefinition definition = item.GetItemDefinition();
                    if (definition == null || definition.m_IsUnequipInvalid)
                    {
                        continue;
                    }

                    actions.Add(
                        new JObject
                        {
                            ["action"] = "inn_wainwright_unequip",
                            ["slot_type"] = GetStageCoachSlotName(slotType),
                            ["stagecoach_slot"] = slot,
                            ["item_id"] = definition.m_id,
                            ["consumes_boundary"] = false
                        });
                }
            }

            actions.Add(
                new JObject
                {
                    ["action"] = "inn_wainwright_state",
                    ["consumes_boundary"] = false
                });
            actions.Add(
                new JObject
                {
                    ["action"] = "inn_wainwright_back",
                    ["consumes_boundary"] = false
                });

            return new JObject
            {
                ["type"] = "inn_wainwright_state",
                ["frontend_state"] = "inn_wainwright",
                ["current_mode"] = "inn",
                ["stagecoach_name"] = stageCoach.GetStageCoachName() ?? string.Empty,
                ["general_slots"] = BuildStageCoachSlotArray(ItemSlotType.GENERAL),
                ["trophy_slots"] = BuildStageCoachSlotArray(ItemSlotType.TROPHY),
                ["pet_slots"] = BuildStageCoachSlotArray(ItemSlotType.PET),
                ["flame_slots"] = BuildStageCoachSlotArray(ItemSlotType.FLAME),
                ["inventory_items"] = inventoryItems,
                ["available_actions"] = actions
            };
        }

        private void QueueInnWainwrightState()
        {
            JObject state = BuildInnWainwrightStateMessage();
            if (state != null)
            {
                QueueOutgoingMessage(state);
            }
        }

        private void HandleInnWainwrightState(BridgeRequest request)
        {
            JObject state = BuildInnWainwrightStateMessage();
            if (state == null)
            {
                request.Response = "ERROR: inn wainwright is unavailable";
                return;
            }

            Logger.LogInfo("COMMAND inn: wainwright");
            QueueOutgoingMessage(state);
            request.Response = "OK";
        }

        private bool ApplyInnWainwrightEquip(
            int playerSlot,
            ItemSlotType expectedSlotType,
            int targetSlot,
            out string error)
        {
            error = null;

            if (!CanUseInnWainwright())
            {
                error = "inn wainwright is unavailable";
                return false;
            }

            GameTypeMgr gameType = Singleton<GameTypeMgr>.Instance;
            ItemInventory playerInventory = gameType.PlayerInventory;
            if (playerInventory == null ||
                playerSlot < 0 ||
                playerSlot >= playerInventory.GetNumberOfTotalSlots())
            {
                error = "player inventory slot is invalid";
                return false;
            }

            IReadOnlyItemInstance source = playerInventory.GetItem(playerSlot);
            if (!ItemUtils.IsValid(source))
            {
                error = "player inventory slot is empty";
                return false;
            }

            ItemDefinition definition = source.GetItemDefinition();
            if (definition == null ||
                definition.m_type != ItemType.STAGE_COACH_UPGRADE ||
                definition.m_slot != expectedSlotType)
            {
                error = "item no longer matches the requested stagecoach slot type";
                return false;
            }

            if (!CanEquipStageCoachItemIntoSlot(definition, targetSlot))
            {
                error = "item cannot currently be equipped into that stagecoach slot";
                return false;
            }

            ItemInventory destination =
                gameType.StageCoach.GetSlotInventory(expectedSlotType);

            destination.SwapItems(
                playerInventory,
                playerSlot,
                targetSlot,
                isPurchase: false);

            EventInventoryItemSwapped.Trigger(
                definition,
                playerInventory,
                destination);

            playerInventory.RefreshStackQtysAndAddToOverflow();
            playerInventory.Condense();
            playerInventory.RemoveEmptyOverlimitItems();

            Logger.LogInfo(
                $"COMMAND inn_wainwright: equip " +
                $"player_slot={playerSlot} " +
                $"slot_type={GetStageCoachSlotName(expectedSlotType)} " +
                $"stagecoach_slot={targetSlot} item='{definition.m_id}'");

            return true;
        }

        private void HandleInnWainwrightEquip(
            BridgeRequest request,
            string arguments)
        {
            string[] parts = (arguments ?? string.Empty).Split(
                new[] { ' ' },
                StringSplitOptions.RemoveEmptyEntries);

            int playerSlot;
            int targetSlot;
            ItemSlotType slotType;
            if (parts.Length != 3 ||
                !int.TryParse(parts[0], out playerSlot) ||
                playerSlot < 0 ||
                (slotType = ParseStageCoachSlotType(parts[1])) == null ||
                !int.TryParse(parts[2], out targetSlot) ||
                targetSlot < 0)
            {
                request.Response =
                    "ERROR: equip_stagecoach requires player_slot, slot_type and stagecoach_slot";
                return;
            }

            ItemInventory playerInventory =
                Singleton<GameTypeMgr>.Instance.PlayerInventory;
            if (playerInventory == null ||
                playerSlot >= playerInventory.GetNumberOfTotalSlots())
            {
                request.Response = "ERROR: player inventory slot is invalid";
                return;
            }

            IReadOnlyItemInstance source = playerInventory.GetItem(playerSlot);
            if (!ItemUtils.IsValid(source))
            {
                request.Response = "ERROR: player inventory slot is empty";
                return;
            }

            ItemDefinition definition = source.GetItemDefinition();
            if (definition == null ||
                definition.m_type != ItemType.STAGE_COACH_UPGRADE ||
                definition.m_slot != slotType)
            {
                request.Response =
                    "ERROR: item is not a compatible stagecoach upgrade";
                return;
            }

            if (!CanEquipStageCoachItemIntoSlot(definition, targetSlot))
            {
                request.Response =
                    "ERROR: item cannot currently be equipped into that stagecoach slot";
                return;
            }

            if (definition.m_IsUnequipInvalid)
            {
                Action confirmAction = delegate
                {
                    string ignored;
                    if (!ApplyInnWainwrightEquip(
                            playerSlot,
                            slotType,
                            targetSlot,
                            out ignored))
                    {
                        Logger.LogWarning(
                            "WAINWRIGHT permanent equip confirmation failed: " +
                            (ignored ?? "unknown error"));
                    }
                };

                SetPendingConfirmationContext(
                    ConfirmationContextInnWainwrightPermanentEquip);
                _innWainwrightReturnPending = true;

                try
                {
                    SingletonMonoBehaviour<CommonUiBhv>.Instance
                        .ShowConfirmationDialog(
                            CommonUiBhv.ConfirmationDialogType.StagecoachUnequippable,
                            Singleton<Localization>.Instance.GetString(
                                "unequippable_item_equip_prompt_title"),
                            Singleton<Localization>.Instance.GetString(
                                "unequippable_item_equip_prompt_desc"),
                            confirmAction,
                            Singleton<Localization>.Instance.GetString(
                                "unequippable_item_equip_prompt_yes"),
                            FunctionUtils.DoNothing,
                            Singleton<Localization>.Instance.GetString(
                                "unequippable_item_equip_prompt_no"));
                }
                catch
                {
                    _innWainwrightReturnPending = false;
                    ClearPendingConfirmationContext();
                    throw;
                }

                Logger.LogInfo(
                    $"COMMAND inn_wainwright: permanent equip confirmation " +
                    $"player_slot={playerSlot} item='{definition.m_id}'");
                request.Response = "OK";
                return;
            }

            string error;
            if (!ApplyInnWainwrightEquip(
                    playerSlot,
                    slotType,
                    targetSlot,
                    out error))
            {
                request.Response = "ERROR: " + error;
                return;
            }

            QueueInnWainwrightState();
            request.Response = "OK";
        }

        private void HandleInnWainwrightUnequip(
            BridgeRequest request,
            string arguments)
        {
            string[] parts = (arguments ?? string.Empty).Split(
                new[] { ' ' },
                StringSplitOptions.RemoveEmptyEntries);

            ItemSlotType slotType;
            int slot;
            if (parts.Length != 2 ||
                (slotType = ParseStageCoachSlotType(parts[0])) == null ||
                !int.TryParse(parts[1], out slot) ||
                slot < 0)
            {
                request.Response =
                    "ERROR: unequip_stagecoach requires slot_type and stagecoach_slot";
                return;
            }

            if (!CanUseInnWainwright())
            {
                request.Response = "ERROR: inn wainwright is unavailable";
                return;
            }

            GameTypeMgr gameType = Singleton<GameTypeMgr>.Instance;
            ItemInventory sourceInventory =
                gameType.StageCoach.GetSlotInventory(slotType);
            if (sourceInventory == null ||
                slot >= sourceInventory.GetNumberOfTotalSlots())
            {
                request.Response = "ERROR: stagecoach slot is invalid";
                return;
            }

            IReadOnlyItemInstance item = sourceInventory.GetItem(slot);
            if (!ItemUtils.IsValid(item))
            {
                request.Response = "ERROR: stagecoach slot is empty";
                return;
            }

            ItemDefinition definition = item.GetItemDefinition();
            if (definition == null || definition.m_IsUnequipInvalid)
            {
                request.Response =
                    "ERROR: this stagecoach item cannot be unequipped";
                return;
            }

            ItemInventory playerInventory = gameType.PlayerInventory;
            int qty = item.GetQty();
            if (playerInventory == null ||
                !playerInventory.CanAdd(definition, qty))
            {
                request.Response =
                    "ERROR: player inventory cannot accept the unequipped item";
                return;
            }

            sourceInventory.TakeItemQty(slot, qty);
            playerInventory.AddItemsWithOverflow(
                definition,
                qty,
                isPurchase: false);
            playerInventory.RefreshStackQtysAndAddToOverflow();
            playerInventory.Condense();
            playerInventory.RemoveEmptyOverlimitItems();
            EventUpdatePlayerCurrency.Trigger();

            Logger.LogInfo(
                $"COMMAND inn_wainwright: unequip " +
                $"slot_type={GetStageCoachSlotName(slotType)} " +
                $"stagecoach_slot={slot} item='{definition.m_id}'");

            QueueInnWainwrightState();
            request.Response = "OK";
        }

        private void HandleInnWainwrightBack(BridgeRequest request)
        {
            if (!IsStableInn())
            {
                request.Response = "ERROR: inn is unavailable";
                return;
            }

            _innWainwrightReturnPending = false;
            _innReadySent = true;
            _hostReadyCatchupRequested = false;

            Logger.LogInfo("COMMAND inn_wainwright: back -> inn");
            QueueOutgoingMessage(BuildInnReadyMessage());
            request.Response = "OK";
        }

        private bool ProcessInnWainwrightReturnReady()
        {
            if (!_innWainwrightReturnPending)
            {
                return false;
            }

            if (!CanUseInnWainwright())
            {
                if (GameModeMgr.CurrentMode != GameModeType.INN)
                {
                    _innWainwrightReturnPending = false;
                }
                return false;
            }

            if (_confirmationChoiceInFlight != null &&
                _confirmationChoiceInFlight.gameObject != null &&
                _confirmationChoiceInFlight.gameObject.activeInHierarchy)
            {
                return false;
            }

            ConfirmationDialogBhv activeDialog;
            JObject activeReady;
            if (TryGetSupportedActiveConfirmation(
                    out activeDialog,
                    out activeReady))
            {
                return false;
            }

            JObject state = BuildInnWainwrightStateMessage();
            if (state == null)
            {
                return false;
            }

            _innWainwrightReturnPending = false;
            _innReadySent = true;
            _hostReadyCatchupRequested = false;
            state["type"] = "ready";

            Logger.LogInfo(
                "READY inn_wainwright: returned after permanent-item confirmation");
            QueueOutgoingMessage(state);
            return true;
        }
    }
}
