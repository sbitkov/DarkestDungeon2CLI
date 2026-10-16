using System;
using System.Collections.Generic;
using System.Reflection;

using Assets.Code.Actor;
using Assets.Code.Game;
using Assets.Code.Item;
using Assets.Code.Item.Events;
using Assets.Code.Library;
using Assets.Code.Trinket;
using Assets.Code.Utils;

using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        private static object GetEquipmentFieldValue(object instance, string fieldName)
        {
            if (instance == null || string.IsNullOrEmpty(fieldName))
            {
                return null;
            }

            Type type = instance.GetType();

            while (type != null)
            {
                FieldInfo field = type.GetField(
                    fieldName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);

                if (field != null)
                {
                    return field.GetValue(instance);
                }

                type = type.BaseType;
            }

            return null;
        }

        private static string GetEquipmentItemId(ItemDefinition definition)
        {
            return definition == null ? null : definition.m_id;
        }

        private static bool GetEquipmentItemUnequipInvalid(ItemDefinition definition)
        {
            object value = GetEquipmentFieldValue(
                definition,
                "m_IsUnequipInvalid");

            return value is bool && (bool)value;
        }

        private static object GetEquipmentItemType(ItemDefinition definition)
        {
            return GetEquipmentFieldValue(definition, "m_type");
        }

        private static bool EquipmentItemTypeIs(
            ItemDefinition definition,
            ItemType expected)
        {
            object actual = GetEquipmentItemType(definition);

            return actual != null &&
                expected != null &&
                actual.Equals(expected);
        }

        private bool IsStableEquipmentContext()
        {
            if (GameModeMgr.CurrentMode != GameModeType.DRIVING)
            {
                return false;
            }

            GameModeMgr mgr = GameModeMgr.Instance;

            if (mgr == null || mgr.IsChangingState())
            {
                return false;
            }

            return GetStableStagecoachReadyVehicle() != null;
        }

        private ActorInstance GetEquipmentPartyActor(uint actorGuid)
        {
            ActorInstance actor =
                SingletonMonoBehaviour<Library<uint, ActorInstance>>.Instance
                    .GetLibraryElement(actorGuid);

            if (actor == null || !actor.IsInParty)
            {
                return null;
            }

            return actor;
        }

        private JObject BuildEquipmentItemJson(
            ItemInventory inventory,
            int slot)
        {
            JObject result = new JObject
            {
                ["slot"] = slot
            };

            if (inventory == null ||
                slot < 0 ||
                slot >= inventory.GetNumberOfTotalSlots())
            {
                result["empty"] = true;
                return result;
            }

            IReadOnlyItemInstance item = inventory.GetItem(slot);

            if (!ItemUtils.IsValid(item))
            {
                result["empty"] = true;
                return result;
            }

            ItemDefinition definition = item.GetItemDefinition();

            result["empty"] = false;
            result["item_id"] = GetEquipmentItemId(definition);
            result["display_name"] = item.GetName();
            result["qty"] = item.GetQty();
            result["unequip_invalid"] =
                GetEquipmentItemUnequipInvalid(definition);

            return result;
        }

        private JArray BuildEquipmentInventoryJson(
            ItemInventory inventory,
            bool relevantPlayerItemsOnly)
        {
            JArray result = new JArray();

            if (inventory == null)
            {
                return result;
            }

            int slotCount = inventory.GetNumberOfTotalSlots();

            for (int slot = 0; slot < slotCount; slot++)
            {
                IReadOnlyItemInstance item = inventory.GetItem(slot);

                if (relevantPlayerItemsOnly)
                {
                    if (!ItemUtils.IsValid(item))
                    {
                        continue;
                    }

                    ItemDefinition definition = item.GetItemDefinition();

                    if (!EquipmentItemTypeIs(
                            definition,
                            ItemType.TRINKET) &&
                        !EquipmentItemTypeIs(
                            definition,
                            ItemType.COMBAT))
                    {
                        continue;
                    }
                }

                JObject itemJson =
                    BuildEquipmentItemJson(inventory, slot);

                if (ItemUtils.IsValid(item))
                {
                    ItemDefinition definition = item.GetItemDefinition();

                    if (EquipmentItemTypeIs(
                            definition,
                            ItemType.TRINKET))
                    {
                        itemJson["item_type"] = "trinket";
                    }
                    else if (EquipmentItemTypeIs(
                            definition,
                            ItemType.COMBAT))
                    {
                        itemJson["item_type"] = "combat";
                    }
                }

                result.Add(itemJson);
            }

            return result;
        }

        private JObject BuildEquipmentSnapshot()
        {
            JObject result = new JObject
            {
                ["player_inventory"] =
                    BuildEquipmentInventoryJson(
                        Singleton<GameTypeMgr>.Instance.PlayerInventory,
                        true)
            };

            JArray heroes = new JArray();
            result["heroes"] = heroes;

            if (Singleton<GameTypeMgr>.Instance.RosterManager == null)
            {
                return result;
            }

            foreach (ActorInstance actor in
                Singleton<GameTypeMgr>.Instance.RosterManager.GetPartyActors())
            {
                if (actor == null)
                {
                    continue;
                }

                JObject hero = new JObject
                {
                    ["actor_guid"] = actor.ActorGuid,
                    ["actor_name"] = actor.ActorName,
                    ["actor_data_id"] = actor.ActorDataId,
                    ["trinkets"] =
                        BuildEquipmentInventoryJson(
                            actor.GetTrinketInventory(),
                            false),
                    ["combat_items"] =
                        BuildEquipmentInventoryJson(
                            actor.GetCombatSkillInventory(),
                            false)
                };

                heroes.Add(hero);
            }

            return result;
        }

        private void AugmentStagecoachReadyWithEquipment(JObject message)
        {
            if (message == null ||
                (string)message["frontend_state"] != "stagecoach_ready")
            {
                return;
            }

            try
            {
                message["equipment"] = BuildEquipmentSnapshot();

                JArray actions =
                    message["available_actions"] as JArray;

                if (actions == null)
                {
                    actions = new JArray();
                    message["available_actions"] = actions;
                }

                actions.Add(
                    new JObject
                    {
                        ["action"] = "equipment_state"
                    });

                actions.Add(
                    new JObject
                    {
                        ["action"] = "equip_trinket",
                        ["parameters"] =
                            "actor_guid, player_slot, trinket_slot"
                    });

                actions.Add(
                    new JObject
                    {
                        ["action"] = "unequip_trinket",
                        ["parameters"] =
                            "actor_guid, trinket_slot"
                    });

                actions.Add(
                    new JObject
                    {
                        ["action"] = "equip_combat_item",
                        ["parameters"] =
                            "actor_guid, player_slot"
                    });

                actions.Add(
                    new JObject
                    {
                        ["action"] = "unequip_combat_item",
                        ["parameters"] = "actor_guid"
                    });
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    $"EQUIPMENT: could not augment stagecoach READY: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private void QueueEquipmentState()
        {
            QueueOutgoingMessage(
                new JObject
                {
                    ["type"] = "equipment_state",
                    ["equipment"] = BuildEquipmentSnapshot()
                });
        }

        private bool TryParseEquipmentArgs(
            string args,
            int expectedCount,
            out string[] parts)
        {
            parts = (args ?? string.Empty)
                .Split(
                    new[] { ' ' },
                    StringSplitOptions.RemoveEmptyEntries);

            return parts.Length == expectedCount;
        }

        private bool TryGetPlayerInventorySource(
            int playerSlot,
            out ItemInventory playerInventory,
            out IReadOnlyItemInstance sourceItem,
            out string error)
        {
            playerInventory = Singleton<GameTypeMgr>.Instance.PlayerInventory;
            sourceItem = null;
            error = null;

            if (playerInventory == null)
            {
                error = "player inventory is unavailable";
                return false;
            }

            if (playerSlot < 0 ||
                playerSlot >= playerInventory.GetNumberOfTotalSlots())
            {
                error = $"player inventory slot {playerSlot} is out of range";
                return false;
            }

            sourceItem = playerInventory.GetItem(playerSlot);

            if (!ItemUtils.IsValid(sourceItem))
            {
                error = $"player inventory slot {playerSlot} is empty";
                return false;
            }

            return true;
        }

        private void HandleEquipmentState(BridgeRequest request)
        {
            if (!IsStableEquipmentContext())
            {
                request.Response =
                    "ERROR: equipment is only available at stagecoach_ready";
                return;
            }

            QueueEquipmentState();
            request.Response = "OK";
        }

        private void HandleEquipTrinket(
            BridgeRequest request,
            string args)
        {
            if (!IsStableEquipmentContext())
            {
                request.Response =
                    "ERROR: equip_trinket requires stagecoach_ready";
                return;
            }

            string[] parts;
            if (!TryParseEquipmentArgs(args, 3, out parts))
            {
                request.Response =
                    "ERROR: equip_trinket requires actor_guid player_slot trinket_slot";
                return;
            }

            uint actorGuid;
            int playerSlot;
            int trinketSlot;

            if (!uint.TryParse(parts[0], out actorGuid) ||
                actorGuid == 0u ||
                !int.TryParse(parts[1], out playerSlot) ||
                playerSlot < 0 ||
                !int.TryParse(parts[2], out trinketSlot) ||
                trinketSlot < 0)
            {
                request.Response =
                    "ERROR: invalid equip_trinket arguments";
                return;
            }

            ActorInstance actor = GetEquipmentPartyActor(actorGuid);

            if (actor == null)
            {
                request.Response =
                    $"ERROR: actor {actorGuid} is not a party hero";
                return;
            }

            ItemInventory playerInventory;
            IReadOnlyItemInstance sourceItem;
            string error;

            if (!TryGetPlayerInventorySource(
                    playerSlot,
                    out playerInventory,
                    out sourceItem,
                    out error))
            {
                request.Response = "ERROR: " + error;
                return;
            }

            ItemDefinition definition = sourceItem.GetItemDefinition();

            if (!EquipmentItemTypeIs(definition, ItemType.TRINKET))
            {
                request.Response =
                    $"ERROR: player slot {playerSlot} is not a trinket";
                return;
            }

            TrinketItemInventory trinketInventory =
                actor.GetTrinketInventory() as TrinketItemInventory;

            if (trinketInventory == null)
            {
                request.Response =
                    $"ERROR: actor {actorGuid} has no trinket inventory";
                return;
            }

            if (trinketSlot >=
                trinketInventory.GetNumberOfTotalSlots())
            {
                request.Response =
                    $"ERROR: trinket slot {trinketSlot} is out of range";
                return;
            }

            IReadOnlyItemInstance destinationItem =
                trinketInventory.GetItem(trinketSlot);

            if (ItemUtils.IsValid(destinationItem) &&
                GetEquipmentItemUnequipInvalid(
                    destinationItem.GetItemDefinition()))
            {
                request.Response =
                    $"ERROR: trinket slot {trinketSlot} cannot be unequipped";
                return;
            }

            if (!trinketInventory.GetCanEquip(
                    definition,
                    checkInventory: true,
                    checkConditions: true))
            {
                request.Response =
                    $"ERROR: trinket cannot be equipped by actor {actorGuid}";
                return;
            }

            trinketInventory.SwapItems(
                playerInventory,
                playerSlot,
                trinketSlot,
                false);

            EventInventoryItemSwapped.Trigger(
                definition,
                playerInventory,
                trinketInventory);

            Logger.LogInfo(
                $"EQUIPMENT equip_trinket actor={actorGuid} " +
                $"player_slot={playerSlot} trinket_slot={trinketSlot} " +
                $"item={GetEquipmentItemId(definition)}");

            QueueEquipmentState();
            request.Response = "OK";
        }

        private void HandleUnequipTrinket(
            BridgeRequest request,
            string args)
        {
            if (!IsStableEquipmentContext())
            {
                request.Response =
                    "ERROR: unequip_trinket requires stagecoach_ready";
                return;
            }

            string[] parts;
            if (!TryParseEquipmentArgs(args, 2, out parts))
            {
                request.Response =
                    "ERROR: unequip_trinket requires actor_guid trinket_slot";
                return;
            }

            uint actorGuid;
            int trinketSlot;

            if (!uint.TryParse(parts[0], out actorGuid) ||
                actorGuid == 0u ||
                !int.TryParse(parts[1], out trinketSlot) ||
                trinketSlot < 0)
            {
                request.Response =
                    "ERROR: invalid unequip_trinket arguments";
                return;
            }

            ActorInstance actor = GetEquipmentPartyActor(actorGuid);

            if (actor == null)
            {
                request.Response =
                    $"ERROR: actor {actorGuid} is not a party hero";
                return;
            }

            ItemInventory trinketInventory =
                actor.GetTrinketInventory();

            if (trinketSlot >=
                trinketInventory.GetNumberOfTotalSlots())
            {
                request.Response =
                    $"ERROR: trinket slot {trinketSlot} is out of range";
                return;
            }

            IReadOnlyItemInstance item =
                trinketInventory.GetItem(trinketSlot);

            if (!ItemUtils.IsValid(item))
            {
                request.Response =
                    $"ERROR: trinket slot {trinketSlot} is empty";
                return;
            }

            ItemDefinition definition = item.GetItemDefinition();

            if (GetEquipmentItemUnequipInvalid(definition))
            {
                request.Response =
                    $"ERROR: trinket slot {trinketSlot} cannot be unequipped";
                return;
            }

            ItemInventory playerInventory =
                Singleton<GameTypeMgr>.Instance.PlayerInventory;

            int qty = item.GetQty();

            if (!playerInventory.CanAdd(definition, qty))
            {
                request.Response =
                    "ERROR: player inventory has no room for this trinket";
                return;
            }

            playerInventory.AddItems(
                definition,
                qty,
                isPurchase: false);

            trinketInventory.TakeItemQty(trinketSlot, qty);

            Logger.LogInfo(
                $"EQUIPMENT unequip_trinket actor={actorGuid} " +
                $"trinket_slot={trinketSlot} " +
                $"item={GetEquipmentItemId(definition)}");

            QueueEquipmentState();
            request.Response = "OK";
        }

        private void HandleEquipCombatItem(
            BridgeRequest request,
            string args)
        {
            if (!IsStableEquipmentContext())
            {
                request.Response =
                    "ERROR: equip_combat_item requires stagecoach_ready";
                return;
            }

            string[] parts;
            if (!TryParseEquipmentArgs(args, 2, out parts))
            {
                request.Response =
                    "ERROR: equip_combat_item requires actor_guid player_slot";
                return;
            }

            uint actorGuid;
            int playerSlot;

            if (!uint.TryParse(parts[0], out actorGuid) ||
                actorGuid == 0u ||
                !int.TryParse(parts[1], out playerSlot) ||
                playerSlot < 0)
            {
                request.Response =
                    "ERROR: invalid equip_combat_item arguments";
                return;
            }

            ActorInstance actor = GetEquipmentPartyActor(actorGuid);

            if (actor == null)
            {
                request.Response =
                    $"ERROR: actor {actorGuid} is not a party hero";
                return;
            }

            ItemInventory playerInventory;
            IReadOnlyItemInstance sourceItem;
            string error;

            if (!TryGetPlayerInventorySource(
                    playerSlot,
                    out playerInventory,
                    out sourceItem,
                    out error))
            {
                request.Response = "ERROR: " + error;
                return;
            }

            ItemDefinition definition = sourceItem.GetItemDefinition();

            // Match the game's own PlayerInventoryItemBhv.TryAutoTransferCombatItem
            // gate: ItemType.COMBAT is sufficient here. IsUsedInCombat() is not
            // consulted by that native auto-transfer path and rejects some valid
            // COMBAT inventory items.
            if (!EquipmentItemTypeIs(definition, ItemType.COMBAT))
            {
                request.Response =
                    $"ERROR: player slot {playerSlot} is not a combat item";
                return;
            }

            ItemInventory combatInventory =
                actor.GetCombatSkillInventory();

            if (combatInventory == null ||
                combatInventory.GetNumberOfTotalSlots() <= 0)
            {
                request.Response =
                    $"ERROR: actor {actorGuid} has no combat item slot";
                return;
            }

            const int combatSlot = 0;

            IReadOnlyItemInstance destinationItem =
                combatInventory.GetItem(combatSlot);

            if (ItemUtils.IsValid(destinationItem) &&
                GetEquipmentItemUnequipInvalid(
                    destinationItem.GetItemDefinition()))
            {
                request.Response =
                    "ERROR: equipped combat item cannot be unequipped";
                return;
            }

            combatInventory.SwapItems(
                playerInventory,
                playerSlot,
                combatSlot,
                false);

            EventInventoryItemSwapped.Trigger(
                definition,
                playerInventory,
                combatInventory);

            Logger.LogInfo(
                $"EQUIPMENT equip_combat_item actor={actorGuid} " +
                $"player_slot={playerSlot} " +
                $"item={GetEquipmentItemId(definition)}");

            QueueEquipmentState();
            request.Response = "OK";
        }

        private void HandleUnequipCombatItem(
            BridgeRequest request,
            string args)
        {
            if (!IsStableEquipmentContext())
            {
                request.Response =
                    "ERROR: unequip_combat_item requires stagecoach_ready";
                return;
            }

            string[] parts;
            if (!TryParseEquipmentArgs(args, 1, out parts))
            {
                request.Response =
                    "ERROR: unequip_combat_item requires actor_guid";
                return;
            }

            uint actorGuid;

            if (!uint.TryParse(parts[0], out actorGuid) ||
                actorGuid == 0u)
            {
                request.Response =
                    "ERROR: invalid unequip_combat_item actor_guid";
                return;
            }

            ActorInstance actor = GetEquipmentPartyActor(actorGuid);

            if (actor == null)
            {
                request.Response =
                    $"ERROR: actor {actorGuid} is not a party hero";
                return;
            }

            ItemInventory combatInventory =
                actor.GetCombatSkillInventory();

            if (combatInventory == null ||
                combatInventory.GetNumberOfTotalSlots() <= 0)
            {
                request.Response =
                    $"ERROR: actor {actorGuid} has no combat item slot";
                return;
            }

            IReadOnlyItemInstance item = combatInventory.GetItem(0);

            if (!ItemUtils.IsValid(item))
            {
                request.Response =
                    "ERROR: combat item slot is empty";
                return;
            }

            ItemDefinition definition = item.GetItemDefinition();

            if (GetEquipmentItemUnequipInvalid(definition))
            {
                request.Response =
                    "ERROR: equipped combat item cannot be unequipped";
                return;
            }

            ItemInventory playerInventory =
                Singleton<GameTypeMgr>.Instance.PlayerInventory;

            int qty = item.GetQty();

            if (!playerInventory.CanAdd(definition, qty))
            {
                request.Response =
                    "ERROR: player inventory has no room for this combat item";
                return;
            }

            playerInventory.AddItems(
                definition,
                qty,
                isPurchase: false);

            combatInventory.TakeItemQty(0, qty);

            Logger.LogInfo(
                $"EQUIPMENT unequip_combat_item actor={actorGuid} " +
                $"item={GetEquipmentItemId(definition)}");

            QueueEquipmentState();
            request.Response = "OK";
        }
    }
}
