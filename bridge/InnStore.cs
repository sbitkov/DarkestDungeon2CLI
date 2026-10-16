using Assets.Code.Cost;
using Assets.Code.Game;
using Assets.Code.Inn;
using Assets.Code.Item;
using Assets.Code.Item.Events;
using Assets.Code.Run;
using Assets.Code.Source;
using Assets.Code.Utils;

using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        private bool CanUseInnStore()
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

            return innBhv.GetInnInstance().StoreInventory != null;
        }

        private ItemInventory GetInnStoreInventory()
        {
            InnBhv innBhv = Singleton<InnBhv>.Instance;
            if (innBhv == null)
            {
                return null;
            }

            InnInstance inn = innBhv.GetInnInstance();
            return inn == null ? null : inn.StoreInventory;
        }

        private JObject BuildInnStoreItemJson(ItemInventory inventory, int slot)
        {
            JObject itemJson = BuildStoreItemJson(inventory, slot);
            if (itemJson == null)
            {
                return null;
            }

            IReadOnlyItemInstance item = inventory.GetItem(slot);
            ItemDefinition definition = item.GetItemDefinition();

            string name = string.Empty;
            string description = string.Empty;

            try
            {
                name = ItemDescription.GetTitle(definition, item.GetQty()) ?? string.Empty;
            }
            catch
            {
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

            itemJson["name"] = name;
            itemJson["description"] = description;
            return itemJson;
        }

        private JObject BuildInnStoreStateMessage()
        {
            if (!CanUseInnStore())
            {
                return null;
            }

            ItemInventory storeInventory = GetInnStoreInventory();
            JObject inventoryState = BuildInventoryStateMessage();

            JArray storeItems = new JArray();
            JArray actions = new JArray();

            for (int slot = 0;
                 slot < storeInventory.GetNumberOfTotalSlots();
                 slot++)
            {
                JObject itemJson = BuildInnStoreItemJson(storeInventory, slot);
                if (itemJson == null)
                {
                    continue;
                }

                storeItems.Add(itemJson);

                if ((bool)itemJson["can_afford"])
                {
                    actions.Add(
                        new JObject
                        {
                            ["action"] = "inn_store_buy",
                            ["slot"] = slot,
                            ["item_id"] = (string)itemJson["item_id"],
                            ["consumes_boundary"] = false
                        });
                }
            }

            JArray playerItems = (JArray)inventoryState["items"];
            foreach (JToken token in playerItems)
            {
                JObject item = token as JObject;
                if (item == null || item["can_discard"] == null)
                {
                    continue;
                }

                if ((bool)item["can_discard"])
                {
                    actions.Add(
                        new JObject
                        {
                            ["action"] = "inn_store_discard",
                            ["slot"] = (int)item["slot"],
                            ["item_id"] = (string)item["item_id"],
                            ["consumes_boundary"] = false
                        });
                }
            }

            actions.Add(
                new JObject
                {
                    ["action"] = "inn_store_state",
                    ["consumes_boundary"] = false
                });

            actions.Add(
                new JObject
                {
                    ["action"] = "inn_store_back",
                    ["consumes_boundary"] = false
                });

            return new JObject
            {
                ["type"] = "inn_store_state",
                ["frontend_state"] = "inn_store",
                ["current_mode"] = "inn",
                ["store_items"] = storeItems,
                ["inventory_limit"] = inventoryState["inventory_limit"],
                ["filled_slots"] = inventoryState["filled_slots"],
                ["overfilled_count"] = inventoryState["overfilled_count"],
                ["is_overfilled"] = inventoryState["is_overfilled"],
                ["inventory_items"] = inventoryState["items"],
                ["available_actions"] = actions
            };
        }

        private void QueueInnStoreState()
        {
            JObject state = BuildInnStoreStateMessage();
            if (state != null)
            {
                QueueOutgoingMessage(state);
            }
        }

        private void HandleInnStoreState(BridgeRequest request)
        {
            JObject state = BuildInnStoreStateMessage();
            if (state == null)
            {
                request.Response = "ERROR: inn store is unavailable";
                return;
            }

            Logger.LogInfo(
                $"COMMAND inn: store items={((JArray)state["store_items"]).Count}");

            QueueOutgoingMessage(state);
            request.Response = "OK";
        }

        private void HandleInnStoreBuy(BridgeRequest request, string slotText)
        {
            if (!CanUseInnStore())
            {
                request.Response = "ERROR: inn store is unavailable";
                return;
            }

            int slot;
            if (!int.TryParse(slotText, out slot) || slot < 0)
            {
                request.Response = "ERROR: buy requires a valid store slot";
                return;
            }

            ItemInventory storeInventory = GetInnStoreInventory();
            if (slot >= storeInventory.GetNumberOfTotalSlots())
            {
                request.Response = $"ERROR: inn store slot {slot} is out of range";
                return;
            }

            IReadOnlyItemInstance item = storeInventory.GetItem(slot);
            if (!ItemUtils.IsValid(item))
            {
                request.Response = $"ERROR: inn store slot {slot} is empty";
                return;
            }

            float buyMultiplier =
                Singleton<GameTypeMgr>.Instance.RunDataManager.GetStatValue(
                    RunStatType.STORE_COST_BUY_MULTIPLIER,
                    item.GetItemType().GetName());

            CostDefinition cost = item.BuyCostDefinition;
            if (!CostCalculation.CanAffordCost(cost, buyMultiplier))
            {
                request.Response =
                    $"ERROR: inn store slot {slot} cannot currently be afforded";
                return;
            }

            ItemInventory playerInventory = GetPlayerInventory();
            if (playerInventory == null)
            {
                request.Response = "ERROR: player inventory is unavailable";
                return;
            }

            if (cost != null &&
                !CostCalculation.AttemptSpendCost(
                    cost,
                    buyMultiplier,
                    SourceType.STORE))
            {
                request.Response = "ERROR: failed to spend store purchase cost";
                return;
            }

            ItemDefinition definition = item.GetItemDefinition();
            ItemInstance purchased = storeInventory.TakeItemQty(slot, 1);
            if (purchased == null)
            {
                request.Response = "ERROR: failed to take item from inn store";
                return;
            }

            purchased.SetQty(1, 1);
            playerInventory.AddItemsWithOverflow(
                purchased.GetItemDefinition(),
                1,
                isPurchase: true);

            EventInventoryItemPurchased.Trigger(definition, isEntireStack: false);

            Logger.LogInfo(
                $"COMMAND inn_store: buy slot={slot} " +
                $"item='{definition.m_id}'");

            QueueInnStoreState();
            request.Response = "OK";
        }

        private void HandleInnStoreDiscard(
            BridgeRequest request,
            string slotText)
        {
            if (!CanUseInnStore())
            {
                request.Response = "ERROR: inn store is unavailable";
                return;
            }

            HandleInventoryDiscard(request, slotText);
            if (request.Response != "OK")
            {
                return;
            }

            Logger.LogInfo(
                $"COMMAND inn_store: discard_item slot={slotText}");

            // For ordinary discardable items OnDiscardItem() mutates the inventory
            // synchronously. If DD2 opens one of its special native confirmation
            // dialogs (for example the last required trophy), the unchanged state
            // remains faithful and the dialog can be wired as its own boundary.
            QueueInnStoreState();
        }

        private void HandleInnStoreBack(BridgeRequest request)
        {
            if (!IsStableInn())
            {
                request.Response = "ERROR: inn is unavailable";
                return;
            }

            Logger.LogInfo("COMMAND inn_store: back -> inn");
            QueueOutgoingMessage(BuildInnReadyMessage());
            request.Response = "OK";
        }
    }
}
