using System;

using Assets.Code.Game;
using Assets.Code.Item;
using Assets.Code.UI.Items;
using Assets.Code.UI.Managers;
using Assets.Code.UI.Screens;
using Assets.Code.Utils;

using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        private bool _inventoryReadySent;
        private UiScreenBhv _inventoryReadyScreen;

        private ItemInventory GetPlayerInventory()
        {
            GameTypeMgr gameTypeMgr = Singleton<GameTypeMgr>.Instance;
            return gameTypeMgr == null ? null : gameTypeMgr.PlayerInventory;
        }

        private bool IsPlayerInventoryOverfilled()
        {
            ItemInventory inventory = GetPlayerInventory();
            return inventory != null && inventory.IsOverfilled;
        }

        private InventoryUiBhv GetStablePlayerInventoryUi()
        {
            if (GameModeMgr.CurrentMode == GameModeType.COMBAT)
            {
                ResetInventoryBoundary();
                return null;
            }

            GameModeMgr modeMgr = GameModeMgr.Instance;
            if (modeMgr == null || modeMgr.IsChangingState())
            {
                ResetInventoryBoundary();
                return null;
            }

            CommonUiBhv commonUi = SingletonMonoBehaviour<CommonUiBhv>.Instance;
            if (commonUi == null)
            {
                ResetInventoryBoundary();
                return null;
            }

            UiScreenBhv screen = commonUi.GetActiveInventoryInstance();
            if (screen == null ||
                screen.gameObject == null ||
                !screen.gameObject.activeInHierarchy ||
                screen.ScreenState != UiScreenState.Open)
            {
                _inventoryReadyScreen = null;
                return null;
            }

            InventoryUiBhv widget = screen.GetWidget<InventoryUiBhv>();
            if (widget == null ||
                widget.ItemContainerBhv == null ||
                widget.ItemContainerBhv.Inventory == null ||
                !ReferenceEquals(widget.ItemContainerBhv.Inventory, GetPlayerInventory()))
            {
                ResetInventoryBoundary();
                return null;
            }

            if (!ReferenceEquals(screen, _inventoryReadyScreen))
            {
                _inventoryReadyScreen = screen;
                _inventoryReadySent = false;
            }

            return widget;
        }

        private void ResetInventoryBoundary()
        {
            _inventoryReadyScreen = null;
            _inventoryReadySent = false;
        }

        private JObject BuildInventoryReadyMessage()
        {
            ItemInventory inventory = GetPlayerInventory();
            bool overfilled = inventory != null && inventory.IsOverfilled;
            bool uiOpen = GetStablePlayerInventoryUi() != null;

            JArray actions = new JArray
            {
                new JObject { ["action"] = "inventory" }
            };

            if (uiOpen)
            {
                actions.Add(
                    new JObject
                    {
                        ["action"] = "discard_item",
                        ["parameters"] = "slot"
                    });

                if (!overfilled)
                {
                    actions.Add(new JObject { ["action"] = "close_inventory" });
                }
            }

            return new JObject
            {
                ["type"] = "ready",
                ["frontend_state"] =
                    overfilled ? "inventory_overfilled" : "inventory",
                ["inventory_limit"] = inventory == null ? 0 : inventory.InventoryLimit,
                ["filled_slots"] = inventory == null ? 0 : inventory.GetNumberOfFilledSlots(),
                ["overfilled_count"] = inventory == null ? 0 : inventory.OverfilledCount,
                ["inventory_ui_open"] = uiOpen,
                ["available_actions"] = actions
            };
        }

        private bool HasInventoryForegroundBoundary()
        {
            if (IsCombatForegroundActive())
            {
                ResetInventoryBoundary();
                return false;
            }

            // Overfill itself is authoritative even for the few frames while
            // the game's forced inventory screen is opening.
            if (IsPlayerInventoryOverfilled())
            {
                return true;
            }

            // In the Inn the player inventory is one tab of the persistent Inn
            // hub, not a modal foreground decision surface.  The InventoryUiBhv
            // may remain active/open while the Inn itself still owns the semantic
            // frontend.  Do not let that tab mask State: inn.
            //
            // An overfilled inventory was handled above and remains authoritative
            // because it genuinely blocks progression.
            if (GameModeMgr.CurrentMode == GameModeType.INN)
            {
                return false;
            }

            return GetStablePlayerInventoryUi() != null;
        }

        private bool ProcessInventoryReady()
        {
            if (!HasInventoryForegroundBoundary())
            {
                ResetInventoryBoundary();
                return false;
            }

            if (_inventoryReadySent)
            {
                return true;
            }

            JObject ready = BuildInventoryReadyMessage();
            _inventoryReadySent = true;
            _hostReadyCatchupRequested = false;

            Logger.LogInfo(
                $"READY {ready["frontend_state"]}: " +
                $"filled={ready["filled_slots"]}/" +
                $"{ready["inventory_limit"]} " +
                $"over={ready["overfilled_count"]}");

            QueueOutgoingMessage(ready);
            return true;
        }

        private bool TryProcessInventoryCatchup()
        {
            if (!HasInventoryForegroundBoundary())
            {
                return false;
            }

            JObject ready = BuildInventoryReadyMessage();
            _inventoryReadySent = true;

            Logger.LogInfo(
                $"READY catch-up: {ready["frontend_state"]}");

            QueueOutgoingMessage(ready);
            return true;
        }

        private JObject BuildInventoryStateMessage()
        {
            ItemInventory inventory = GetPlayerInventory();
            JArray items = new JArray();

            if (inventory != null)
            {
                int slots = inventory.GetNumberOfTotalSlots();
                for (int slot = 0; slot < slots; slot++)
                {
                    IReadOnlyItemInstance item = inventory.GetItem(slot);
                    if (!ItemUtils.IsValid(item))
                    {
                        continue;
                    }

                    ItemDefinition definition = item.GetItemDefinition();
                    if (definition == null)
                    {
                        continue;
                    }

                    string title = string.Empty;
                    try
                    {
                        title = ItemDescription.GetTitle(definition.m_id, item.GetQty()) ?? string.Empty;
                    }
                    catch
                    {
                    }

                    items.Add(
                        new JObject
                        {
                            ["slot"] = slot,
                            ["item_id"] = definition.m_id,
                            ["name"] = title,
                            ["qty"] = item.GetQty(),
                            ["item_type"] = definition.m_type.ToString().ToLowerInvariant(),
                            ["can_discard"] = definition.m_canDiscard,
                            ["overflow"] = slot >= inventory.InventoryLimit
                        });
                }
            }

            return new JObject
            {
                ["type"] = "inventory_state",
                ["inventory_limit"] = inventory == null ? 0 : inventory.InventoryLimit,
                ["filled_slots"] = inventory == null ? 0 : inventory.GetNumberOfFilledSlots(),
                ["overfilled_count"] = inventory == null ? 0 : inventory.OverfilledCount,
                ["is_overfilled"] = inventory != null && inventory.IsOverfilled,
                ["items"] = items
            };
        }

        private void HandleInventoryState(BridgeRequest request)
        {
            if (IsCombatForegroundActive())
            {
                request.Response = "ERROR: inventory is unavailable during combat";
                return;
            }

            ItemInventory inventory = GetPlayerInventory();
            if (inventory == null)
            {
                request.Response = "ERROR: player inventory is unavailable";
                return;
            }

            QueueOutgoingMessage(BuildInventoryStateMessage());
            request.Response = "OK";
        }

        private PlayerInventoryItemBhv FindPlayerInventoryItemBhvForSlot(
            PlayerInventoryItemContainerBhv container,
            int slot)
        {
            if (container == null)
            {
                return null;
            }

            int count = container.GetElementCount();
            for (int i = 0; i < count; i++)
            {
                PlayerInventoryItemBhv itemBhv =
                    container.GetElement(i) as PlayerInventoryItemBhv;

                if (itemBhv != null && itemBhv.ItemIndex == slot)
                {
                    return itemBhv;
                }
            }

            return null;
        }

        private void HandleInventoryDiscard(
            BridgeRequest request,
            string slotText)
        {
            int slot;
            if (!int.TryParse(slotText, out slot) || slot < 0)
            {
                request.Response = "ERROR: discard_item requires a valid slot";
                return;
            }

            ItemInventory inventory = GetPlayerInventory();
            if (inventory == null || slot >= inventory.GetNumberOfTotalSlots())
            {
                request.Response = $"ERROR: inventory slot {slot} is out of range";
                return;
            }

            IReadOnlyItemInstance item = inventory.GetItem(slot);
            if (!ItemUtils.IsValid(item))
            {
                request.Response = $"ERROR: inventory slot {slot} is empty";
                return;
            }

            ItemDefinition definition = item.GetItemDefinition();
            if (definition == null || !definition.m_canDiscard)
            {
                request.Response = $"ERROR: inventory slot {slot} cannot be discarded";
                return;
            }

            InventoryUiBhv inventoryUi = GetStablePlayerInventoryUi();
            if (inventoryUi == null)
            {
                request.Response =
                    "ERROR: native player inventory UI is not open; use inventory and wait for the inventory boundary";
                return;
            }

            PlayerInventoryItemBhv itemBhv =
                FindPlayerInventoryItemBhvForSlot(
                    inventoryUi.ItemContainerBhv,
                    slot);

            if (itemBhv == null)
            {
                request.Response =
                    $"ERROR: native player inventory widget for slot {slot} was not found";
                return;
            }

            Logger.LogInfo(
                $"COMMAND inventory: discard_item slot={slot} " +
                $"item='{definition.m_id}' qty={item.GetQty()}");

            itemBhv.OnDiscardItem();

            _inventoryReadySent = false;
            _hostReadyCatchupRequested = false;
            request.Response = "OK";
        }

        private void HandleInventoryClose(BridgeRequest request)
        {
            InventoryUiBhv inventoryUi = GetStablePlayerInventoryUi();
            if (inventoryUi == null)
            {
                request.Response = "ERROR: player inventory UI is not open";
                return;
            }

            if (IsPlayerInventoryOverfilled())
            {
                request.Response =
                    "ERROR: inventory cannot be closed while overfilled";
                return;
            }

            CommonUiBhv commonUi = SingletonMonoBehaviour<CommonUiBhv>.Instance;
            if (commonUi == null)
            {
                request.Response = "ERROR: common UI is unavailable";
                return;
            }

            Logger.LogInfo("COMMAND consumes READY boundary: inventory close_inventory");

            _inventoryReadySent = true;
            _hostReadyCatchupRequested = false;
            commonUi.HidePlayerInventory();
            QueueActivityStatus("closing inventory");
            request.Response = "OK";
        }
    }
}
