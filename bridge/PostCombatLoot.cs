using System;

using Assets.Code.Game;
using Assets.Code.Item;
using Assets.Code.UI.Controllers;
using Assets.Code.UI.Items;
using Assets.Code.UI.Screens;
using Assets.Code.UI.Widgets;
using Assets.Code.Utils;

using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        private LootScreenWidgetBhv _postCombatLootReadyWidget;
        private bool _postCombatLootReadySent;
        private bool _postCombatLootLeaveConfirmationPending;


        private sealed class PostCombatLootBoundary
        {
            public UiScreenBhv Screen;
            public LootScreenWidgetBhv Widget;
            public LootUiControllerBhv Controller;
            public LootInventoryItemContainerBhv Container;
        }

        private PostCombatLootBoundary GetStablePostCombatLootBoundary()
        {
            // LootUiControllerBhv is a generic loot screen used by post-combat
            // rewards, road/cache rewards, and other contexts. Do not gate this
            // boundary on RESULTS: the active/open loot screen itself is the
            // authoritative frontend signal.
            GameModeMgr modeMgr = GameModeMgr.Instance;
            if (modeMgr == null || modeMgr.IsChangingState())
            {
                ResetPostCombatLootBoundary();
                return null;
            }

            PostCombatLootBoundary cached =
                TryBuildStablePostCombatLootBoundary(_postCombatLootReadyWidget);

            if (cached != null)
            {
                return cached;
            }

            LootScreenWidgetBhv[] widgets =
                UnityEngine.Resources.FindObjectsOfTypeAll<LootScreenWidgetBhv>();

            foreach (LootScreenWidgetBhv widget in widgets)
            {
                PostCombatLootBoundary boundary =
                    TryBuildStablePostCombatLootBoundary(widget);

                if (boundary == null)
                {
                    continue;
                }

                if (!ReferenceEquals(widget, _postCombatLootReadyWidget))
                {
                    _postCombatLootReadyWidget = widget;
                    _postCombatLootReadySent = false;
                    _postCombatLootLeaveConfirmationPending = false;

                    Logger.LogInfo(
                        $"READY boundary detected: post_combat_loot " +
                        $"path='{GetTransformPath(widget.transform)}'");
                }

                return boundary;
            }

            ResetPostCombatLootBoundary();
            return null;
        }

        private PostCombatLootBoundary TryBuildStablePostCombatLootBoundary(
            LootScreenWidgetBhv widget)
        {
            if (widget == null ||
                widget.gameObject == null ||
                !widget.gameObject.activeInHierarchy ||
                !widget.enabled)
            {
                return null;
            }

            try
            {
                if (widget.IsPresenting())
                {
                    return null;
                }
            }
            catch
            {
                return null;
            }

            UiScreenBhv screen =
                GetMemberValue(widget, "m_screenBhv") as UiScreenBhv;

            LootUiControllerBhv controller =
                GetMemberValue(widget, "m_lootController") as LootUiControllerBhv;

            LootInventoryItemContainerBhv container =
                GetMemberValue(widget, "m_itemContainerBhv") as LootInventoryItemContainerBhv;

            if (screen == null ||
                controller == null ||
                container == null ||
                container.Inventory == null ||
                screen.gameObject == null ||
                !screen.gameObject.activeInHierarchy ||
                screen.ScreenState != UiScreenState.Open)
            {
                return null;
            }

            return new PostCombatLootBoundary
            {
                Screen = screen,
                Widget = widget,
                Controller = controller,
                Container = container
            };
        }

        private void ResetPostCombatLootBoundary()
        {
            _postCombatLootReadyWidget = null;
            _postCombatLootReadySent = false;
        }

        private JObject BuildPostCombatLootReadyMessage(
            PostCombatLootBoundary boundary)
        {
            JArray loot = new JArray();
            JArray actions = new JArray();

            ItemInventory lootInventory = boundary.Container.Inventory;
            ItemInventory playerInventory =
                Singleton<GameTypeMgr>.Instance.PlayerInventory;

            bool hasValidLoot = false;
            int slotCount = lootInventory.GetNumberOfTotalSlots();

            for (int slot = 0; slot < slotCount; slot++)
            {
                IReadOnlyItemInstance item = lootInventory.GetItem(slot);

                if (!ItemUtils.IsValid(item))
                {
                    continue;
                }

                ItemDefinition definition = item.GetItemDefinition();
                if (definition == null)
                {
                    continue;
                }

                int qty = item.GetQty();
                bool canTake =
                    playerInventory != null &&
                    playerInventory.CanAdd(definition, qty);

                hasValidLoot = true;

                loot.Add(
                    new JObject
                    {
                        ["slot"] = slot,
                        ["item_id"] = definition.m_id,
                        ["qty"] = qty,
                        ["can_take"] = canTake
                    });

                if (canTake)
                {
                    actions.Add(
                        new JObject
                        {
                            ["action"] = "take_item",
                            ["slot"] = slot,
                            ["item_id"] = definition.m_id,
                            ["qty"] = qty,
                            ["consumes_boundary"] = false
                        });
                }
            }

            if (hasValidLoot)
            {
                actions.Add(
                    new JObject
                    {
                        ["action"] = "take_all",
                        ["consumes_boundary"] = true
                    });
            }

            actions.Add(
                new JObject
                {
                    ["action"] = "continue",
                    ["leaves_items"] = hasValidLoot,
                    ["consumes_boundary"] = true
                });

            object pushParams = boundary.Screen.PushParams;
            object rawReason = GetMemberValue(pushParams, "m_reason");

            return new JObject
            {
                ["type"] = "ready",
                // Preserve the existing wire state for host compatibility while
                // allowing this detector to cover every native loot screen.
                ["frontend_state"] = "post_combat_loot",
                ["loot_reason"] = rawReason == null ? null : rawReason.ToString(),
                ["game_mode"] = GameModeMgr.CurrentMode == null
                    ? null
                    : GameModeMgr.CurrentMode.ToString(),
                ["loot"] = loot,
                ["available_actions"] = actions
            };
        }

        private void ProcessPostCombatLootReady()
        {
            PostCombatLootBoundary boundary =
                GetStablePostCombatLootBoundary();

            if (boundary == null || _postCombatLootReadySent)
            {
                return;
            }

            JObject ready = BuildPostCombatLootReadyMessage(boundary);

            _postCombatLootReadySent = true;
            _hostReadyCatchupRequested = false;
            
            Logger.LogInfo(
                $"READY post_combat_loot: items={((JArray)ready["loot"]).Count}");

            QueueOutgoingMessage(ready);
        }

        private bool TryProcessPostCombatLootCatchup()
        {
            PostCombatLootBoundary boundary =
                GetStablePostCombatLootBoundary();

            if (boundary == null)
            {
                return false;
            }

            _postCombatLootReadySent = true;

            JObject ready = BuildPostCombatLootReadyMessage(boundary);

            Logger.LogInfo(
                $"READY catch-up: post_combat_loot items={((JArray)ready["loot"]).Count}");

            QueueOutgoingMessage(ready);
            return true;
        }

        private void HandlePostCombatLootTakeItem(
            BridgeRequest request,
            string slotText)
        {
            PostCombatLootBoundary boundary =
                GetStablePostCombatLootBoundary();

            if (boundary == null)
            {
                request.Response =
                    "ERROR: post-combat loot is not at an interactive boundary";
                return;
            }

            int slot;
            if (!int.TryParse(slotText, out slot) || slot < 0)
            {
                request.Response =
                    "ERROR: take_item requires a valid loot slot";
                return;
            }

            ItemInventory lootInventory = boundary.Container.Inventory;

            if (slot >= lootInventory.GetNumberOfTotalSlots())
            {
                request.Response =
                    $"ERROR: loot slot {slot} is out of range";
                return;
            }

            IReadOnlyItemInstance expectedItem = lootInventory.GetItem(slot);

            if (!ItemUtils.IsValid(expectedItem))
            {
                request.Response =
                    $"ERROR: loot slot {slot} is empty";
                return;
            }

            ItemDefinition definition = expectedItem.GetItemDefinition();
            int qty = expectedItem.GetQty();

            ItemInventory playerInventory =
                Singleton<GameTypeMgr>.Instance.PlayerInventory;

            if (playerInventory == null ||
                !playerInventory.CanAdd(definition, qty))
            {
                request.Response =
                    $"ERROR: loot slot {slot} cannot currently be taken";
                return;
            }

            LootInventoryItemBhv targetItemBhv =
                FindLootItemBhvForSlot(
                    boundary.Container,
                    slot,
                    expectedItem);

            if (targetItemBhv == null)
            {
                request.Response =
                    $"ERROR: native loot item widget for slot {slot} was not found";
                return;
            }

            Logger.LogInfo(
                $"COMMAND post_combat_loot: take_item " +
                $"slot={slot} item='{definition.m_id}' qty={qty}");

            targetItemBhv.OnSubmit(null);

            _postCombatLootReadySent = false;
            _hostReadyCatchupRequested = false;

            request.Response = "OK";
        }

        private LootInventoryItemBhv FindLootItemBhvForSlot(
            LootInventoryItemContainerBhv container,
            int slot,
            IReadOnlyItemInstance expectedItem)
        {
            int elementCount = container.GetElementCount();

            for (int i = 0; i < elementCount; i++)
            {
                LootInventoryItemBhv itemBhv =
                    container.GetElement(i) as LootInventoryItemBhv;

                if (itemBhv == null)
                {
                    continue;
                }

                object rawIndex =
                    GetMemberValue(itemBhv, "ItemIndex") ??
                    GetMemberValue(itemBhv, "m_ItemIndex") ??
                    GetMemberValue(itemBhv, "m_itemIndex");

                if (rawIndex != null)
                {
                    try
                    {
                        if (Convert.ToInt32(rawIndex) == slot)
                        {
                            return itemBhv;
                        }
                    }
                    catch
                    {
                    }
                }

                object rawItem =
                    GetMemberValue(itemBhv, "Item") ??
                    GetMemberValue(itemBhv, "m_Item") ??
                    GetMemberValue(itemBhv, "m_item");

                if (rawItem != null &&
                    ReferenceEquals(rawItem, expectedItem))
                {
                    return itemBhv;
                }
            }

            return null;
        }

        private void HandlePostCombatLootTakeAll(
            BridgeRequest request)
        {
            PostCombatLootBoundary boundary =
                GetStablePostCombatLootBoundary();

            if (boundary == null)
            {
                request.Response =
                    "ERROR: post-combat loot is not at an interactive boundary";
                return;
            }

            Logger.LogInfo(
                "COMMAND consumes READY boundary: post_combat_loot take_all");

            _postCombatLootReadySent = true;
            _hostReadyCatchupRequested = false;

            boundary.Controller.ButtonTakeAll();
            request.Response = "OK";
        }

        private void HandlePostCombatLootContinue(
            BridgeRequest request)
        {
            PostCombatLootBoundary boundary =
                GetStablePostCombatLootBoundary();

            if (boundary == null)
            {
                request.Response =
                    "ERROR: post-combat loot is not at an interactive boundary";
                return;
            }

            bool hasItems =
                boundary.Container.Inventory.GetValidItems().Count > 0;

            Logger.LogInfo(
                "COMMAND consumes READY boundary: " +
                $"post_combat_loot continue leaves_items={hasItems}");

            _postCombatLootReadySent = true;
            _hostReadyCatchupRequested = false;
            _postCombatLootLeaveConfirmationPending = hasItems;

            boundary.Controller.ButtonClose();
            request.Response = "OK";
        }

        private void HandleSemanticContinue(BridgeRequest request)
        {
            if (TryHandleDoomResetContinue(request))
            {
                return;
            }

            if (GetStablePostCombatLootBoundary() != null)
            {
                HandlePostCombatLootContinue(request);
                return;
            }

            HandleExpeditionResultsContinue(request);
        }
    }
}
