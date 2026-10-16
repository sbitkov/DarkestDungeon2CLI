using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Code.Actor;
using Assets.Code.Game;
using Assets.Code.Item;
using Assets.Code.Inn.Presentation;
using Assets.Code.Utils;
using Assets.Code.UI.Widgets;
using Assets.Code.UI;
using Assets.Code.UI.Items;
using Assets.Code.UI.Screens;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        private JArray _activeInnItemRelationshipConsequences;

        private sealed class InnItemActorSnapshot
        {
            public uint Guid;
            public float Hp;
            public float Stress;
            public JArray Quirks;
            public JArray Buffs;
            public JArray Tokens;
        }

        private JObject BuildInnItemsState()
        {
            JArray items = new JArray();
            JArray actions = new JArray();
            JArray party = new JArray();

            var roster = Singleton<GameTypeMgr>.Instance.RosterManager;
            IReadOnlyList<ActorInstance> actors = roster.GetPartyActors();
            foreach (ActorInstance actor in actors)
            {
                if (actor == null) continue;
                party.Add(new JObject
                {
                    ["actor_guid"] = actor.ActorGuid,
                    ["actor_name"] = actor.ActorName ?? "",
                    ["actor_id"] = actor.ActorDataId ?? "",
                    ["hp"] = actor.HpRounded,
                    ["hp_max"] = actor.CurrentHpMax,
                    ["stress"] = actor.Stress
                });
            }

            ItemInventory inventory = GetPlayerInventory();
            if (inventory != null)
            {
                int total = inventory.GetNumberOfTotalSlots();
                for (int slot = 0; slot < total; slot++)
                {
                    IReadOnlyItemInstance item = inventory.GetItem(slot);
                    if (!ItemUtils.IsValid(item)) continue;
                    ItemDefinition def = item.GetItemDefinition();
                    if (def == null || !def.IsUsedInInn()) continue;

                    JObject itemJson = BuildInnItemDescription(slot, item, def, actors, actions);
                    items.Add(itemJson);
                }
            }

            actions.Add(new JObject { ["action"] = "inn_items_state" });
            actions.Add(new JObject { ["action"] = "inn_items_back" });

            return new JObject
            {
                ["type"] = "inn_items_state",
                ["frontend_state"] = "inn_items",
                ["current_mode"] = "inn",
                ["party"] = party,
                ["items"] = items,
                ["available_actions"] = actions
            };
        }

        private JObject BuildInnItemDescription(
            int slot,
            IReadOnlyItemInstance item,
            ItemDefinition def,
            IReadOnlyList<ActorInstance> actors,
            JArray actions)
        {
            int selectableTargets = def.GetNumberOfSelectableRestItemTargets();
            JArray targets = new JArray();
            List<uint> validTargets = new List<uint>();

            foreach (ActorInstance actor in actors)
            {
                if (actor == null) continue;
                string blockedReason = null;
                string blockedBy = null;
                ItemBlockDefinition blockDef;
                if (actor.GetIsItemBlocked(def, out blockDef))
                {
                    blockedReason = "item_blocked";
                    blockedBy = blockDef == null ? "" : blockDef.m_Id ?? "";
                }
                else if (!actor.GetIsItemConditionsMet(def))
                {
                    blockedReason = "condition_not_met";
                }
                else if (!actor.GetIsItemUnderUseLimit(def))
                {
                    blockedReason = "use_limit_reached";
                }
                else
                {
                    validTargets.Add(actor.ActorGuid);
                }

                JObject target = new JObject
                {
                    ["actor_guid"] = actor.ActorGuid,
                    ["actor_name"] = actor.ActorName ?? "",
                    ["available"] = blockedReason == null
                };
                if (blockedReason != null) target["blocked_reason"] = blockedReason;
                if (!string.IsNullOrEmpty(blockedBy)) target["blocked_by_id"] = blockedBy;
                targets.Add(target);
            }

            if (selectableTargets == 1)
            {
                foreach (uint guid in validTargets)
                    actions.Add(new JObject { ["action"]="inn_item_use", ["slot"]=slot, ["target_guids"]=new JArray(guid) });
            }
            else if (selectableTargets == 2)
            {
                for (int i=0;i<validTargets.Count;i++)
                    for (int j=i+1;j<validTargets.Count;j++)
                        actions.Add(new JObject { ["action"]="inn_item_use", ["slot"]=slot, ["target_guids"]=new JArray(validTargets[i], validTargets[j]) });
            }
            else if (selectableTargets == 4 && validTargets.Count > 0)
            {
                actions.Add(new JObject { ["action"]="inn_item_use", ["slot"]=slot, ["target_guids"]=new JArray() });
            }

            return new JObject
            {
                ["slot"] = slot,
                ["item_id"] = def.m_id ?? "",
                ["display_name"] = GetInnItemName(item, def),
                ["quantity"] = item.GetQty(),
                ["selectable_targets"] = selectableTargets,
                ["random_target"] = def.m_isRandomTarget,
                ["targets"] = targets,
                ["tooltip"] = GetInnItemDescription(item)
            };
        }


        private string GetInnItemName(IReadOnlyItemInstance item, ItemDefinition def)
        {
            try { return ItemDescription.GetTitle(def, item.GetQty()) ?? string.Empty; }
            catch { return def == null ? string.Empty : def.m_id ?? string.Empty; }
        }

        private string GetInnItemDescription(IReadOnlyItemInstance item)
        {
            try
            {
                return ItemDescription.GetDescription(
                    item,
                    includeRunStatModification: true,
                    canSell: false,
                    showDiscard: false,
                    hideTitle: true) ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private RestWidgetBhv FindInnRestWidget()
        {
            InnPresentationBhv presentation = SingletonMonoBehaviour<InnPresentationBhv>.Instance;
            if (presentation == null) return null;
            FieldInfo field = typeof(InnPresentationBhv).GetField(
                "m_restWidgetBhv",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(presentation) as RestWidgetBhv;
        }

        private List<RestItemSlotBhv> GetRestSlots(RestWidgetBhv widget)
        {
            FieldInfo field = typeof(RestWidgetBhv).GetField("m_heroItemSlots", BindingFlags.Instance|BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(widget) as List<RestItemSlotBhv>;
        }

        private Dictionary<uint, InnItemActorSnapshot> CaptureInnItemParty()
        {
            var result = new Dictionary<uint, InnItemActorSnapshot>();
            foreach (ActorInstance actor in Singleton<GameTypeMgr>.Instance.RosterManager.GetPartyActors())
            {
                if (actor == null) continue;
                result[actor.ActorGuid] = new InnItemActorSnapshot {
                    Guid=actor.ActorGuid, Hp=actor.HpRounded, Stress=actor.Stress,
                    Quirks=CaptureNormalizedQuirks(actor), Buffs=CaptureNormalizedBuffs(actor), Tokens=CaptureNormalizedTokens(actor)
                };
            }
            return result;
        }

        private JArray BuildInnItemConsequences(Dictionary<uint, InnItemActorSnapshot> before)
        {
            JArray result = new JArray();
            foreach (ActorInstance actor in Singleton<GameTypeMgr>.Instance.RosterManager.GetPartyActors())
            {
                if (actor == null || !before.ContainsKey(actor.ActorGuid)) continue;
                InnItemActorSnapshot old = before[actor.ActorGuid];
                if (old.Hp != actor.HpRounded)
                    result.Add(new JObject { ["type"]="hp_changed", ["actor_guid"]=actor.ActorGuid, ["before"]=old.Hp, ["after"]=actor.HpRounded, ["delta"]=actor.HpRounded-old.Hp });
                if (old.Stress != actor.Stress)
                    result.Add(new JObject { ["type"]="stress_changed", ["actor_guid"]=actor.ActorGuid, ["before"]=old.Stress, ["after"]=actor.Stress, ["delta"]=actor.Stress-old.Stress });
                JArray newQuirks = CaptureNormalizedQuirks(actor);
                int quirkStart = result.Count;
                AppendQuirkConsequences(result, old.Quirks, newQuirks);
                for (int qi = quirkStart; qi < result.Count; qi++)
                {
                    JObject quirkConsequence = result[qi] as JObject;
                    if (quirkConsequence != null) quirkConsequence["actor_guid"] = actor.ActorGuid;
                }
                JArray newBuffs = CaptureNormalizedBuffs(actor);
                if (!JToken.DeepEquals(old.Buffs,newBuffs)) result.Add(new JObject { ["type"]="buffs_changed", ["actor_guid"]=actor.ActorGuid, ["before"]=old.Buffs, ["after"]=newBuffs });
                JArray newTokens = CaptureNormalizedTokens(actor);
                if (!JToken.DeepEquals(old.Tokens,newTokens)) result.Add(new JObject { ["type"]="tokens_changed", ["actor_guid"]=actor.ActorGuid, ["before"]=old.Tokens, ["after"]=newTokens });
            }
            return result;
        }

        private void HandleInnItemsState(BridgeRequest request)
        {
            if (!IsStableInn()) { request.Response="ERROR: inn items are only available in the inn"; return; }
            QueueOutgoingMessage(BuildInnItemsState()); request.Response="OK";
        }

        private void HandleInnItemUse(BridgeRequest request, string args)
        {
            if (!IsStableInn()) { request.Response="ERROR: inn items are only available in the inn"; return; }
            string[] parts=(args??"").Split(new[]{' '}, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1) { request.Response="ERROR: inn-item-use requires slot [actor_guid ...]"; return; }
            int slot; if (!int.TryParse(parts[0],out slot)) { request.Response="ERROR: invalid inventory slot"; return; }
            ItemInventory inv=GetPlayerInventory();
            IReadOnlyItemInstance item=inv==null?null:inv.GetItem(slot);
            if (!ItemUtils.IsValid(item) || !item.GetItemDefinition().IsUsedInInn()) { request.Response="ERROR: slot does not contain an inn item"; return; }

            RestWidgetBhv rest=FindInnRestWidget();
            InventoryUiBhv inventoryUi = GetStablePlayerInventoryUi();
            PlayerInventoryItemBhv itemBhv = inventoryUi == null
                ? null
                : FindPlayerInventoryItemBhvForSlot(inventoryUi.ItemContainerBhv, slot);
            List<RestItemSlotBhv> restSlots=GetRestSlots(rest);
            if (rest==null || itemBhv==null || restSlots==null) { request.Response="ERROR: native inn-item UI is unavailable"; return; }

            var before=CaptureInnItemParty();
            _activeInnItemRelationshipConsequences = new JArray();
            rest.SetItem(itemBhv);
            int needed=item.GetItemDefinition().GetNumberOfSelectableRestItemTargets();
            bool applied=false;
            if (needed==4)
            {
                foreach (RestItemSlotBhv rs in restSlots) if (rs!=null && rs.IsActive) { applied=rs.OnItemInput(); break; }
            }
            else
            {
                for (int p=1;p<parts.Length;p++)
                {
                    uint guid; if (!uint.TryParse(parts[p],out guid)) continue;
                    RestItemSlotBhv target=restSlots.Find(x=>x!=null && x.GetTargetActorGuid()==guid);
                    if (target==null) { request.Response="ERROR: requested inn-item target is unavailable"; return; }
                    applied=target.OnItemInput();
                }
            }
            if (!applied)
            {
                _activeInnItemRelationshipConsequences = null;
                request.Response="ERROR: native inn-item action was rejected";
                return;
            }

            JArray consequences = BuildInnItemConsequences(before);
            if (_activeInnItemRelationshipConsequences != null)
            {
                foreach (JToken relationship in _activeInnItemRelationshipConsequences)
                    consequences.Add(relationship.DeepClone());
            }
            _activeInnItemRelationshipConsequences = null;

            JObject result=new JObject {
                ["type"]="inn_item_result", ["slot"]=slot, ["item_id"]=item.GetItemDefinition().m_id??"",
                ["consequences"]=consequences
            };
            QueueOutgoingMessage(result);
            QueueOutgoingMessage(BuildInnItemsState());
            request.Response="OK";
        }

        private void HandleInnItemsBack(BridgeRequest request)
        {
            _innReadySent=true; QueueOutgoingMessage(BuildInnReadyMessage()); request.Response="OK";
        }
    }
}
