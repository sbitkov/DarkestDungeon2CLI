using System;

using Assets.Code.Cost;
using Assets.Code.Game;
using Assets.Code.Item;
using Assets.Code.Run;
using Assets.Code.UI.Items;
using Assets.Code.UI.Managers;
using Assets.Code.UI.Screens;
using Assets.Code.Utils;

using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        private StoreUiBhv _storeReadyWidget;
        private bool _storeReadySent;

        private sealed class StoreBoundary
        {
            public UiScreenBhv Screen;
            public StoreUiBhv Store;
            public StoreInventoryItemContainerBhv Container;
        }

        private StoreBoundary GetStableStoreBoundary()
        {
            GameModeMgr mgr = GameModeMgr.Instance;
            if (mgr == null || mgr.IsChangingState())
            {
                ResetStoreBoundary();
                return null;
            }

            // Deliberately scoped to the ordinary store. Hospital and Inn store
            // containers are handled separately later.
            if (!SingletonMonoBehaviour<CommonUiBhv>.HasInstance())
            {
                ResetStoreBoundary();
                return null;
            }

            UiScreenBhv screen =
                SingletonMonoBehaviour<CommonUiBhv>.Instance.GetActiveStoreInstance();

            if (screen == null ||
                screen.gameObject == null ||
                !screen.gameObject.activeInHierarchy ||
                screen.ScreenState != UiScreenState.Open)
            {
                ResetStoreBoundary();
                return null;
            }

            StoreUiBhv store = screen.GetWidget<StoreUiBhv>();
            if (store == null ||
                store.gameObject == null ||
                !store.gameObject.activeInHierarchy ||
                !store.enabled ||
                !store.IsInitialized())
            {
                ResetStoreBoundary();
                return null;
            }

            StoreInventoryItemContainerBhv container = store.ItemContainerBhv;

            // Boundary detection only needs the store's own initialized inventory.
            // The native buy path additionally requires an active player inventory;
            // validate that at command execution time instead of hiding the store.
            if (container == null ||
                container.Inventory == null)
            {
                return null;
            }

            if (!ReferenceEquals(store, _storeReadyWidget))
            {
                _storeReadyWidget = store;
                _storeReadySent = false;

                Logger.LogInfo(
                    $"READY boundary detected: store " +
                    $"path='{GetTransformPath(store.transform)}'");
            }

            return new StoreBoundary
            {
                Screen = screen,
                Store = store,
                Container = container
            };
        }

        private void ResetStoreBoundary()
        {
            _storeReadyWidget = null;
            _storeReadySent = false;
        }

        private JObject BuildStoreItemJson(ItemInventory inventory, int slot)
        {
            IReadOnlyItemInstance item = inventory.GetItem(slot);
            if (!ItemUtils.IsValid(item))
            {
                return null;
            }

            ItemDefinition definition = item.GetItemDefinition();
            if (definition == null)
            {
                return null;
            }

            float buyMultiplier =
                Singleton<GameTypeMgr>.Instance.RunDataManager.GetStatValue(
                    RunStatType.STORE_COST_BUY_MULTIPLIER,
                    item.GetItemType().GetName());

            bool canAfford =
                CostCalculation.CanAffordCost(
                    item.BuyCostDefinition,
                    buyMultiplier);

            string priceText =
                CostDescription.GetStoreBuyDescription(
                    item.BuyCostDefinition,
                    buyMultiplier);

            return new JObject
            {
                ["slot"] = slot,
                ["item_id"] = definition.m_id,
                ["qty"] = item.GetQty(),
                ["item_type"] = item.GetItemType().GetName(),
                ["price_text"] = priceText,
                ["can_afford"] = canAfford
            };
        }

        private JObject BuildStoreState(StoreBoundary boundary)
        {
            JArray items = new JArray();
            JArray actions = new JArray();
            ItemInventory inventory = boundary.Container.Inventory;

            for (int slot = 0;
                 slot < inventory.GetNumberOfTotalSlots();
                 slot++)
            {
                JObject itemJson = BuildStoreItemJson(inventory, slot);
                if (itemJson == null)
                {
                    continue;
                }

                items.Add(itemJson);

                if ((bool)itemJson["can_afford"])
                {
                    actions.Add(
                        new JObject
                        {
                            ["action"] = "buy",
                            ["slot"] = slot,
                            ["item_id"] = (string)itemJson["item_id"],
                            ["consumes_boundary"] = false
                        });
                }
            }

            actions.Add(
                new JObject
                {
                    ["action"] = "store_state",
                    ["consumes_boundary"] = false
                });

            actions.Add(
                new JObject
                {
                    ["action"] = "leave",
                    ["consumes_boundary"] = true
                });

            return new JObject
            {
                ["items"] = items,
                ["available_actions"] = actions
            };
        }

        private JObject BuildStoreReadyMessage(StoreBoundary boundary)
        {
            JObject state = BuildStoreState(boundary);

            return new JObject
            {
                ["type"] = "ready",
                ["frontend_state"] = "store",
                ["items"] = state["items"],
                ["available_actions"] = state["available_actions"]
            };
        }

        private bool ProcessStoreReady()
        {
            StoreBoundary boundary = GetStableStoreBoundary();
            if (boundary == null)
            {
                return false;
            }

            if (!_storeReadySent)
            {
                JObject ready = BuildStoreReadyMessage(boundary);
                _storeReadySent = true;
                _hostReadyCatchupRequested = false;

                Logger.LogInfo(
                    $"READY store: items={((JArray)ready["items"]).Count}");

                QueueOutgoingMessage(ready);
            }

            return true;
        }

        private bool TryProcessStoreCatchup()
        {
            StoreBoundary boundary = GetStableStoreBoundary();
            if (boundary == null)
            {
                return false;
            }

            _storeReadySent = true;
            JObject ready = BuildStoreReadyMessage(boundary);

            Logger.LogInfo(
                $"READY catch-up: store items={((JArray)ready["items"]).Count}");

            QueueOutgoingMessage(ready);
            return true;
        }

        private void QueueStoreState(StoreBoundary boundary)
        {
            JObject state = BuildStoreState(boundary);

            QueueOutgoingMessage(
                new JObject
                {
                    ["type"] = "store_state",
                    ["items"] = state["items"],
                    ["available_actions"] = state["available_actions"]
                });
        }

        private StoreInventoryItemBhv FindStoreItemBhvForSlot(
            StoreInventoryItemContainerBhv container,
            int slot,
            IReadOnlyItemInstance expectedItem)
        {
            for (int i = 0; i < container.GetElementCount(); i++)
            {
                StoreInventoryItemBhv itemBhv =
                    container.GetElement(i) as StoreInventoryItemBhv;

                if (itemBhv == null)
                {
                    continue;
                }

                if (itemBhv.ItemIndex == slot ||
                    ReferenceEquals(itemBhv.Item, expectedItem))
                {
                    return itemBhv;
                }
            }

            return null;
        }

        private void HandleStoreState(BridgeRequest request)
        {
            StoreBoundary boundary = GetStableStoreBoundary();
            if (boundary == null)
            {
                request.Response =
                    "ERROR: store is not at an interactive boundary";
                return;
            }

            QueueStoreState(boundary);
            request.Response = "OK";
        }

        private void HandleStoreBuy(BridgeRequest request, string slotText)
        {
            StoreBoundary boundary = GetStableStoreBoundary();
            if (boundary == null)
            {
                request.Response =
                    "ERROR: store is not at an interactive boundary";
                return;
            }

            int slot;
            if (!int.TryParse(slotText, out slot) || slot < 0)
            {
                request.Response = "ERROR: buy requires a valid store slot";
                return;
            }

            ItemInventory inventory = boundary.Container.Inventory;
            if (slot >= inventory.GetNumberOfTotalSlots())
            {
                request.Response =
                    $"ERROR: store slot {slot} is out of range";
                return;
            }

            IReadOnlyItemInstance item = inventory.GetItem(slot);
            if (!ItemUtils.IsValid(item))
            {
                request.Response =
                    $"ERROR: store slot {slot} is empty";
                return;
            }

            float buyMultiplier =
                Singleton<GameTypeMgr>.Instance.RunDataManager.GetStatValue(
                    RunStatType.STORE_COST_BUY_MULTIPLIER,
                    item.GetItemType().GetName());

            if (!CostCalculation.CanAffordCost(
                    item.BuyCostDefinition,
                    buyMultiplier))
            {
                request.Response =
                    $"ERROR: store slot {slot} cannot currently be afforded";
                return;
            }

            if (boundary.Container.ActivePlayerItemContainer == null)
            {
                request.Response =
                    "ERROR: player inventory is not active for store purchase";
                return;
            }

            StoreInventoryItemBhv itemBhv =
                FindStoreItemBhvForSlot(
                    boundary.Container,
                    slot,
                    item);

            if (itemBhv == null)
            {
                request.Response =
                    $"ERROR: native store item widget for slot {slot} was not found";
                return;
            }

            ItemDefinition definition = item.GetItemDefinition();

            Logger.LogInfo(
                $"COMMAND store: buy slot={slot} " +
                $"item='{definition.m_id}' qty_before={item.GetQty()}");

            itemBhv.OnTryBuyItem();

            QueueStoreState(boundary);
            request.Response = "OK";
        }

        private void HandleStoreLeave(BridgeRequest request)
        {
            StoreBoundary boundary = GetStableStoreBoundary();
            if (boundary == null)
            {
                request.Response =
                    "ERROR: store is not at an interactive boundary";
                return;
            }

            Logger.LogInfo("COMMAND consumes READY boundary: store leave");

            _storeReadySent = true;
            _hostReadyCatchupRequested = false;

            boundary.Store.HandleDoneButton();
            request.Response = "OK";
        }
    }
}
