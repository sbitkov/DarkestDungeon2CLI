using System;
using System.Collections.Generic;

using Assets.Code.Actor;
using Assets.Code.Data;
using Assets.Code.Game;
using Assets.Code.Item;
using Assets.Code.Library;
using Assets.Code.Quirk;
using Assets.Code.Roster;
using Assets.Code.UI.Events;
using Assets.Code.UI.Items;
using Assets.Code.UI.Managers;
using Assets.Code.UI.Screens;
using Assets.Code.Utils;

using Newtonsoft.Json.Linq;

using UnityEngine;
using UnityEngine.UI;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        private HospitalScreenBhv _hospitalReadyWidget;
        private bool _hospitalReadySent;

        private sealed class HospitalBoundary
        {
            public UiScreenBhv Screen;
            public HospitalScreenBhv Hospital;
            public DataContextBhv DataContext;
            public ActorInstance SelectedActor;
        }

        private HospitalBoundary GetStableHospitalBoundary()
        {
            if (GameModeMgr.CurrentMode != GameModeType.DRIVING)
            {
                ResetHospitalBoundary();
                return null;
            }

            GameModeMgr mgr = GameModeMgr.Instance;
            if (mgr == null || mgr.IsChangingState())
            {
                ResetHospitalBoundary();
                return null;
            }

            if (!SingletonMonoBehaviour<CommonUiBhv>.HasInstance())
            {
                ResetHospitalBoundary();
                return null;
            }

            UiScreenBhv screen =
                SingletonMonoBehaviour<CommonUiBhv>.Instance
                    .GetActiveHospitalInstance();

            if (screen == null ||
                screen.gameObject == null ||
                !screen.gameObject.activeInHierarchy ||
                screen.ScreenState != UiScreenState.Open)
            {
                ResetHospitalBoundary();
                return null;
            }

            HospitalScreenBhv hospital =
                screen.GetComponent<HospitalScreenBhv>();

            if (hospital == null ||
                hospital.gameObject == null ||
                !hospital.gameObject.activeInHierarchy ||
                !hospital.enabled)
            {
                ResetHospitalBoundary();
                return null;
            }

            DataContextBhv dataContext =
                GetMemberValue(hospital, "m_dataContextBhv") as DataContextBhv;

            ActorInstance selectedActor =
                GetMemberValue(hospital, "m_selectedActor") as ActorInstance;

            // PartyBrowser.Initialize() selects the first hero synchronously.  If
            // the screen is still settling and no hero exists yet, wait instead
            // of announcing a half-initialized hospital boundary.
            if (dataContext == null || selectedActor == null)
            {
                return null;
            }

            if (!ReferenceEquals(hospital, _hospitalReadyWidget))
            {
                _hospitalReadyWidget = hospital;
                _hospitalReadySent = false;

                Logger.LogInfo(
                    $"READY boundary detected: hospital " +
                    $"path='{GetTransformPath(hospital.transform)}'");
            }

            return new HospitalBoundary
            {
                Screen = screen,
                Hospital = hospital,
                DataContext = dataContext,
                SelectedActor = selectedActor
            };
        }

        private void ResetHospitalBoundary()
        {
            _hospitalReadyWidget = null;
            _hospitalReadySent = false;
        }

        private static string GetHospitalActiveTab(HospitalScreenBhv hospital)
        {
            object value = GetMemberValue(hospital, "m_activeTab");
            return value == null
                ? ""
                : value.ToString().ToLowerInvariant();
        }

        private static bool IsHospitalButtonAvailable(Button button)
        {
            return button != null &&
                   button.gameObject != null &&
                   button.gameObject.activeInHierarchy &&
                   button.enabled &&
                   button.interactable;
        }

        private static JObject BuildHospitalQuirkJson(
            QuirkInstance quirk,
            ActorInstance actor,
            QuirkInstance selected)
        {
            if (quirk == null || quirk.Definition == null)
            {
                return null;
            }

            QuirkDefinition definition = quirk.Definition;

            return new JObject
            {
                ["quirk_id"] = definition.Id ?? "",
                ["name"] = QuirkDescription.GetNameString(definition, actor) ?? "",
                ["is_positive"] = definition.IsPositive,
                ["is_negative"] = definition.IsNegative,
                ["is_disease"] = definition.IsDisease,
                ["is_curse"] = definition.IsCurse,
                ["locked"] = quirk.IsLocked(),
                ["selected"] = ReferenceEquals(quirk, selected)
            };
        }

        private JObject BuildHospitalState(HospitalBoundary boundary)
        {
            HospitalScreenBhv hospital = boundary.Hospital;
            ActorInstance actor = boundary.SelectedActor;
            string activeTab = GetHospitalActiveTab(hospital);

            JArray party = new JArray();
            JArray quirks = new JArray();
            JArray storeItems = new JArray();
            JArray actions = new JArray();

            IReadOnlyList<uint> partyGuids =
                Singleton<GameTypeMgr>.Instance.RosterManager
                    .GetActorGuids(RosterStatusType.PARTY);

            if (partyGuids != null)
            {
                foreach (uint guid in partyGuids)
                {
                    ActorInstance partyActor = null;
                    if (SingletonMonoBehaviour<Library<uint, ActorInstance>>
                        .HasInstance())
                    {
                        partyActor =
                            SingletonMonoBehaviour<Library<uint, ActorInstance>>
                                .Instance.GetLibraryElement(guid);
                    }

                    party.Add(
                        new JObject
                        {
                            ["actor_guid"] = guid,
                            ["actor_name"] = partyActor == null
                                ? ""
                                : partyActor.ActorName,
                            ["actor_data_id"] = partyActor == null
                                ? ""
                                : partyActor.ActorDataId,
                            ["selected"] = actor != null &&
                                actor.ActorGuid == guid
                        });

                    if (actor == null || actor.ActorGuid != guid)
                    {
                        actions.Add(
                            new JObject
                            {
                                ["action"] = "select_hospital_actor",
                                ["actor_guid"] = guid,
                                ["consumes_boundary"] = false
                            });
                    }
                }
            }

            actions.Add(
                new JObject
                {
                    ["action"] = "hospital_tab",
                    ["tab"] = "treatment",
                    ["consumes_boundary"] = false
                });
            actions.Add(
                new JObject
                {
                    ["action"] = "hospital_tab",
                    ["tab"] = "quirks",
                    ["consumes_boundary"] = false
                });
            actions.Add(
                new JObject
                {
                    ["action"] = "hospital_tab",
                    ["tab"] = "store",
                    ["consumes_boundary"] = false
                });

            JObject treatment = new JObject();
            JObject quirkTreatment = new JObject();

            Button minorHealButton =
                GetMemberValue(hospital, "m_minorHealBtn") as Button;
            Button fullHealButton =
                GetMemberValue(hospital, "m_fullHealBtn") as Button;
            Button cureDiseaseButton =
                GetMemberValue(hospital, "m_cureDiseaseButton") as Button;
            Button lockQuirkButton =
                GetMemberValue(hospital, "m_lockQuirksButton") as Button;
            Button removeQuirkButton =
                GetMemberValue(hospital, "m_removeQuirksButton") as Button;

            QuirkInstance activeDisease =
                GetMemberValue(hospital, "m_ActiveDiseaseQuirk")
                    as QuirkInstance;
            QuirkInstance selectedQuirk =
                GetMemberValue(hospital, "m_selectedQuirkToTreat")
                    as QuirkInstance;

            treatment["hp_current"] = actor.HpRaw;
            treatment["hp_max"] = actor.CurrentHpMax;
            treatment["is_wounded"] = actor.IsWounded;
            treatment["minor_heal_amount_text"] =
                boundary.DataContext.GetStringValue("minor_heal_amt") ?? "";
            treatment["minor_heal_cost_text"] =
                boundary.DataContext.GetStringValue("minor_heal_cost") ?? "";
            treatment["minor_heal_available"] =
                IsHospitalButtonAvailable(minorHealButton);
            treatment["full_heal_description"] =
                boundary.DataContext.GetStringValue("physician_full_heal") ?? "";
            treatment["full_heal_cost_text"] =
                boundary.DataContext.GetStringValue("full_heal_cost") ?? "";
            treatment["full_heal_available"] =
                IsHospitalButtonAvailable(fullHealButton);
            treatment["disease_name"] =
                boundary.DataContext.GetStringValue("disease_name") ?? "";
            treatment["cure_disease_cost_text"] =
                boundary.DataContext.GetStringValue("cure_disease_cost") ?? "";
            treatment["cure_disease_available"] =
                IsHospitalButtonAvailable(cureDiseaseButton);

            if (activeDisease != null && activeDisease.Definition != null)
            {
                treatment["disease_id"] = activeDisease.Definition.Id ?? "";
                treatment["disease_is_curse"] =
                    activeDisease.Definition.IsCurse;
            }
            else
            {
                treatment["disease_id"] = "";
                treatment["disease_is_curse"] = false;
            }

            if (activeTab == "treatment")
            {
                if (IsHospitalButtonAvailable(minorHealButton))
                {
                    actions.Add(
                        new JObject
                        {
                            ["action"] = "hospital_minor_heal",
                            ["consumes_boundary"] = false
                        });
                }

                if (IsHospitalButtonAvailable(fullHealButton))
                {
                    actions.Add(
                        new JObject
                        {
                            ["action"] = "hospital_full_heal",
                            ["consumes_boundary"] = false
                        });
                }

                if (IsHospitalButtonAvailable(cureDiseaseButton))
                {
                    actions.Add(
                        new JObject
                        {
                            ["action"] = "hospital_cure_disease",
                            ["consumes_boundary"] = false
                        });
                }
            }

            List<QuirkInstance> negativeQuirks =
                GetMemberValue(hospital, "m_negativeQuirks")
                    as List<QuirkInstance>;
            List<QuirkInstance> positiveQuirks =
                GetMemberValue(hospital, "m_positiveQuirks")
                    as List<QuirkInstance>;

            Action<List<QuirkInstance>> addQuirks = delegate(
                List<QuirkInstance> source)
            {
                if (source == null)
                {
                    return;
                }

                foreach (QuirkInstance quirk in source)
                {
                    JObject quirkJson =
                        BuildHospitalQuirkJson(quirk, actor, selectedQuirk);

                    if (quirkJson == null)
                    {
                        continue;
                    }

                    quirks.Add(quirkJson);

                    if (activeTab == "quirks")
                    {
                        actions.Add(
                            new JObject
                            {
                                ["action"] = "select_hospital_quirk",
                                ["quirk_id"] = (string)quirkJson["quirk_id"],
                                ["consumes_boundary"] = false
                            });
                    }
                }
            };

            addQuirks(negativeQuirks);
            addQuirks(positiveQuirks);

            quirkTreatment["selected_quirk_id"] =
                selectedQuirk == null || selectedQuirk.Definition == null
                    ? ""
                    : selectedQuirk.Definition.Id ?? "";
            quirkTreatment["lock_cost_text"] =
                boundary.DataContext.GetStringValue("lock_quirk_cost") ?? "";
            quirkTreatment["remove_cost_text"] =
                boundary.DataContext.GetStringValue("remove_quirk_cost") ?? "";
            quirkTreatment["lock_available"] =
                IsHospitalButtonAvailable(lockQuirkButton);
            quirkTreatment["remove_available"] =
                IsHospitalButtonAvailable(removeQuirkButton);

            if (activeTab == "quirks" && selectedQuirk != null)
            {
                if (IsHospitalButtonAvailable(lockQuirkButton))
                {
                    actions.Add(
                        new JObject
                        {
                            ["action"] = "hospital_lock_quirk",
                            ["quirk_id"] = selectedQuirk.Definition.Id ?? "",
                            ["consumes_boundary"] = false
                        });
                }

                if (IsHospitalButtonAvailable(removeQuirkButton))
                {
                    actions.Add(
                        new JObject
                        {
                            ["action"] = "hospital_remove_quirk",
                            ["quirk_id"] = selectedQuirk.Definition.Id ?? "",
                            ["consumes_boundary"] = false
                        });
                }
            }

            if (activeTab == "store")
            {
                StoreUiBhv store = hospital.GetStoreBhv();
                if (store != null &&
                    store.gameObject != null &&
                    store.gameObject.activeInHierarchy &&
                    store.IsInitialized() &&
                    store.ItemContainerBhv != null &&
                    store.ItemContainerBhv.Inventory != null)
                {
                    ItemInventory inventory = store.ItemContainerBhv.Inventory;

                    for (int slot = 0;
                         slot < inventory.GetNumberOfTotalSlots();
                         slot++)
                    {
                        JObject itemJson = BuildStoreItemJson(inventory, slot);
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
                                    ["action"] = "hospital_buy",
                                    ["slot"] = slot,
                                    ["item_id"] = (string)itemJson["item_id"],
                                    ["consumes_boundary"] = false
                                });
                        }
                    }
                }
            }

            actions.Add(
                new JObject
                {
                    ["action"] = "hospital_state",
                    ["consumes_boundary"] = false
                });

            actions.Add(
                new JObject
                {
                    ["action"] = "leave_hospital",
                    ["consumes_boundary"] = true
                });

            return new JObject
            {
                ["active_tab"] = activeTab,
                ["selected_actor_guid"] = actor.ActorGuid,
                ["selected_actor_name"] = actor.ActorName ?? "",
                ["party"] = party,
                ["treatment"] = treatment,
                ["quirks"] = quirks,
                ["quirk_treatment"] = quirkTreatment,
                ["store_items"] = storeItems,
                ["available_actions"] = actions
            };
        }

        private JObject BuildHospitalReadyMessage(HospitalBoundary boundary)
        {
            JObject state = BuildHospitalState(boundary);

            return new JObject
            {
                ["type"] = "ready",
                ["frontend_state"] = "hospital",
                ["active_tab"] = state["active_tab"],
                ["selected_actor_guid"] = state["selected_actor_guid"],
                ["selected_actor_name"] = state["selected_actor_name"],
                ["party"] = state["party"],
                ["treatment"] = state["treatment"],
                ["quirks"] = state["quirks"],
                ["quirk_treatment"] = state["quirk_treatment"],
                ["store_items"] = state["store_items"],
                ["available_actions"] = state["available_actions"]
            };
        }

        private bool ProcessHospitalReady()
        {
            HospitalBoundary boundary = GetStableHospitalBoundary();
            if (boundary == null)
            {
                return false;
            }

            if (!_hospitalReadySent)
            {
                _hospitalReadySent = true;
                _hostReadyCatchupRequested = false;

                JObject ready = BuildHospitalReadyMessage(boundary);

                Logger.LogInfo(
                    $"READY hospital: actor={ready["selected_actor_guid"]} " +
                    $"tab={ready["active_tab"]}");

                QueueOutgoingMessage(ready);
            }

            return true;
        }

        private bool TryProcessHospitalCatchup()
        {
            HospitalBoundary boundary = GetStableHospitalBoundary();
            if (boundary == null)
            {
                return false;
            }

            _hospitalReadySent = true;
            JObject ready = BuildHospitalReadyMessage(boundary);

            Logger.LogInfo(
                $"READY catch-up: hospital actor={ready["selected_actor_guid"]} " +
                $"tab={ready["active_tab"]}");

            QueueOutgoingMessage(ready);
            return true;
        }

        private void QueueHospitalState(HospitalBoundary boundary)
        {
            JObject state = BuildHospitalState(boundary);

            QueueOutgoingMessage(
                new JObject
                {
                    ["type"] = "hospital_state",
                    ["active_tab"] = state["active_tab"],
                    ["selected_actor_guid"] = state["selected_actor_guid"],
                    ["selected_actor_name"] = state["selected_actor_name"],
                    ["party"] = state["party"],
                    ["treatment"] = state["treatment"],
                    ["quirks"] = state["quirks"],
                    ["quirk_treatment"] = state["quirk_treatment"],
                    ["store_items"] = state["store_items"],
                    ["available_actions"] = state["available_actions"]
                });
        }

        private void HandleHospitalState(BridgeRequest request)
        {
            HospitalBoundary boundary = GetStableHospitalBoundary();
            if (boundary == null)
            {
                request.Response =
                    "ERROR: hospital is not at an interactive boundary";
                return;
            }

            QueueHospitalState(boundary);
            request.Response = "OK";
        }

        private void HandleHospitalSelectActor(
            BridgeRequest request,
            string actorGuidText)
        {
            HospitalBoundary boundary = GetStableHospitalBoundary();
            if (boundary == null)
            {
                request.Response =
                    "ERROR: hospital is not at an interactive boundary";
                return;
            }

            uint actorGuid;
            if (!uint.TryParse(actorGuidText, out actorGuid) || actorGuid == 0u)
            {
                request.Response =
                    "ERROR: select_hospital_actor requires actor_guid";
                return;
            }

            IReadOnlyList<uint> party =
                Singleton<GameTypeMgr>.Instance.RosterManager
                    .GetActorGuids(RosterStatusType.PARTY);

            bool found = false;
            if (party != null)
            {
                foreach (uint guid in party)
                {
                    if (guid == actorGuid)
                    {
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                request.Response =
                    $"ERROR: actor {actorGuid} is not in the current party";
                return;
            }

            EventHeroRibbonSelect.Trigger(actorGuid);

            HospitalBoundary updated = GetStableHospitalBoundary();
            if (updated != null)
            {
                QueueHospitalState(updated);
            }

            request.Response = "OK";
        }

        private void HandleHospitalTab(
            BridgeRequest request,
            string tabText)
        {
            HospitalBoundary boundary = GetStableHospitalBoundary();
            if (boundary == null)
            {
                request.Response =
                    "ERROR: hospital is not at an interactive boundary";
                return;
            }

            int index;
            string tab = (tabText ?? "").Trim().ToLowerInvariant();
            if (tab == "treatment")
            {
                index = 0;
            }
            else if (tab == "quirks")
            {
                index = 1;
            }
            else if (tab == "store")
            {
                index = 2;
            }
            else
            {
                request.Response =
                    "ERROR: hospital_tab requires treatment, quirks, or store";
                return;
            }

            Button[] tabs =
                GetMemberValue(boundary.Hospital, "m_tabs") as Button[];

            if (tabs == null || index < 0 || index >= tabs.Length ||
                tabs[index] == null)
            {
                request.Response =
                    $"ERROR: hospital tab '{tab}' is unavailable";
                return;
            }

            tabs[index].onClick?.Invoke();

            HospitalBoundary updated = GetStableHospitalBoundary();
            if (updated != null)
            {
                QueueHospitalState(updated);
            }

            request.Response = "OK";
        }

        private void HandleHospitalSelectQuirk(
            BridgeRequest request,
            string quirkId)
        {
            HospitalBoundary boundary = GetStableHospitalBoundary();
            if (boundary == null)
            {
                request.Response =
                    "ERROR: hospital is not at an interactive boundary";
                return;
            }

            if (GetHospitalActiveTab(boundary.Hospital) != "quirks")
            {
                request.Response =
                    "ERROR: open the hospital quirks tab before selecting a quirk";
                return;
            }

            if (string.IsNullOrEmpty(quirkId))
            {
                request.Response =
                    "ERROR: select_hospital_quirk requires quirk_id";
                return;
            }

            List<QuirkInstance> negative =
                GetMemberValue(boundary.Hospital, "m_negativeQuirks")
                    as List<QuirkInstance>;
            List<QuirkInstance> positive =
                GetMemberValue(boundary.Hospital, "m_positiveQuirks")
                    as List<QuirkInstance>;
            Transform negativeParent =
                GetMemberValue(boundary.Hospital, "m_negativeQuirksParent")
                    as Transform;
            Transform positiveParent =
                GetMemberValue(boundary.Hospital, "m_positiveQuirksParent")
                    as Transform;

            RectTransform target = null;

            Action<List<QuirkInstance>, Transform> find = delegate(
                List<QuirkInstance> list,
                Transform parent)
            {
                if (target != null || list == null || parent == null)
                {
                    return;
                }

                for (int i = 0; i < list.Count && i < parent.childCount; i++)
                {
                    QuirkInstance quirk = list[i];
                    if (quirk != null &&
                        quirk.Definition != null &&
                        string.Equals(
                            quirk.Definition.Id,
                            quirkId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        target = parent.GetChild(i) as RectTransform;
                        return;
                    }
                }
            };

            find(negative, negativeParent);
            find(positive, positiveParent);

            if (target == null)
            {
                request.Response =
                    $"ERROR: quirk '{quirkId}' is not selectable for the current actor";
                return;
            }

            boundary.Hospital.HandleOnClickedQuirkToggle(target);

            HospitalBoundary updated = GetStableHospitalBoundary();
            if (updated != null)
            {
                QueueHospitalState(updated);
            }

            request.Response = "OK";
        }

        private void HandleHospitalMinorHeal(BridgeRequest request)
        {
            HospitalBoundary boundary = GetStableHospitalBoundary();
            if (boundary == null)
            {
                request.Response =
                    "ERROR: hospital is not at an interactive boundary";
                return;
            }

            Button button =
                GetMemberValue(boundary.Hospital, "m_minorHealBtn") as Button;

            if (GetHospitalActiveTab(boundary.Hospital) != "treatment" ||
                !IsHospitalButtonAvailable(button))
            {
                request.Response =
                    "ERROR: minor heal is not currently available";
                return;
            }

            boundary.Hospital.HandleOnClickedMinorHealButton();
            QueueHospitalState(boundary);
            request.Response = "OK";
        }

        private void HandleHospitalFullHeal(BridgeRequest request)
        {
            HospitalBoundary boundary = GetStableHospitalBoundary();
            if (boundary == null)
            {
                request.Response =
                    "ERROR: hospital is not at an interactive boundary";
                return;
            }

            Button button =
                GetMemberValue(boundary.Hospital, "m_fullHealBtn") as Button;

            if (GetHospitalActiveTab(boundary.Hospital) != "treatment" ||
                !IsHospitalButtonAvailable(button))
            {
                request.Response =
                    "ERROR: full heal is not currently available";
                return;
            }

            boundary.Hospital.HandleOnClickedFullHealButton();
            QueueHospitalState(boundary);
            request.Response = "OK";
        }

        private void HandleHospitalCureDisease(BridgeRequest request)
        {
            HospitalBoundary boundary = GetStableHospitalBoundary();
            if (boundary == null)
            {
                request.Response =
                    "ERROR: hospital is not at an interactive boundary";
                return;
            }

            Button button =
                GetMemberValue(boundary.Hospital, "m_cureDiseaseButton")
                    as Button;

            if (GetHospitalActiveTab(boundary.Hospital) != "treatment" ||
                !IsHospitalButtonAvailable(button))
            {
                request.Response =
                    "ERROR: disease cure is not currently available";
                return;
            }

            boundary.Hospital.HandleOnClickedDiseaseBuyButton();
            QueueHospitalState(boundary);
            request.Response = "OK";
        }

        private void HandleHospitalLockQuirk(BridgeRequest request)
        {
            HandleHospitalTreatSelectedQuirk(request, true);
        }

        private void HandleHospitalRemoveQuirk(BridgeRequest request)
        {
            HandleHospitalTreatSelectedQuirk(request, false);
        }

        private void HandleHospitalTreatSelectedQuirk(
            BridgeRequest request,
            bool lockQuirk)
        {
            HospitalBoundary boundary = GetStableHospitalBoundary();
            if (boundary == null)
            {
                request.Response =
                    "ERROR: hospital is not at an interactive boundary";
                return;
            }

            if (GetHospitalActiveTab(boundary.Hospital) != "quirks")
            {
                request.Response =
                    "ERROR: hospital quirks tab is not active";
                return;
            }

            QuirkInstance selected =
                GetMemberValue(boundary.Hospital, "m_selectedQuirkToTreat")
                    as QuirkInstance;
            Button button =
                GetMemberValue(
                    boundary.Hospital,
                    lockQuirk ? "m_lockQuirksButton" : "m_removeQuirksButton")
                    as Button;

            if (selected == null || !IsHospitalButtonAvailable(button))
            {
                request.Response = lockQuirk
                    ? "ERROR: selected quirk cannot currently be locked"
                    : "ERROR: selected quirk cannot currently be removed";
                return;
            }

            if (lockQuirk)
            {
                boundary.Hospital.HandleOnClickedQuirksLockButton();
            }
            else
            {
                boundary.Hospital.HandleOnClickedQuirksRemoveButton();
            }

            QueueHospitalState(boundary);
            request.Response = "OK";
        }

        private void HandleHospitalBuy(
            BridgeRequest request,
            string slotText)
        {
            HospitalBoundary boundary = GetStableHospitalBoundary();
            if (boundary == null)
            {
                request.Response =
                    "ERROR: hospital is not at an interactive boundary";
                return;
            }

            if (GetHospitalActiveTab(boundary.Hospital) != "store" ||
                !boundary.Hospital.IsStoreScreenVisible)
            {
                request.Response =
                    "ERROR: hospital store tab is not active";
                return;
            }

            int slot;
            if (!int.TryParse(slotText, out slot) || slot < 0)
            {
                request.Response =
                    "ERROR: hospital_buy requires a valid store slot";
                return;
            }

            StoreUiBhv store = boundary.Hospital.GetStoreBhv();
            StoreInventoryItemContainerBhv container =
                store == null ? null : store.ItemContainerBhv;

            if (store == null ||
                !store.IsInitialized() ||
                container == null ||
                container.Inventory == null ||
                container.ActivePlayerItemContainer == null)
            {
                request.Response =
                    "ERROR: hospital store inventory is not ready";
                return;
            }

            ItemInventory inventory = container.Inventory;
            if (slot >= inventory.GetNumberOfTotalSlots())
            {
                request.Response =
                    $"ERROR: hospital store slot {slot} is out of range";
                return;
            }

            IReadOnlyItemInstance expectedItem = inventory.GetItem(slot);
            if (!ItemUtils.IsValid(expectedItem))
            {
                request.Response =
                    $"ERROR: hospital store slot {slot} is empty";
                return;
            }

            JObject itemJson = BuildStoreItemJson(inventory, slot);
            if (itemJson == null || !(bool)itemJson["can_afford"])
            {
                request.Response =
                    $"ERROR: hospital store slot {slot} cannot currently be bought";
                return;
            }

            StoreInventoryItemBhv itemBhv =
                FindStoreItemBhvForSlot(container, slot, expectedItem);

            if (itemBhv == null)
            {
                request.Response =
                    $"ERROR: native hospital store item widget for slot {slot} was not found";
                return;
            }

            itemBhv.OnTryBuyItem();
            QueueHospitalState(boundary);
            request.Response = "OK";
        }

        private void HandleHospitalLeave(BridgeRequest request)
        {
            HospitalBoundary boundary = GetStableHospitalBoundary();
            if (boundary == null)
            {
                request.Response =
                    "ERROR: hospital is not at an interactive boundary";
                return;
            }

            if (boundary.Screen.IsAnimating)
            {
                request.Response =
                    "ERROR: hospital screen is still animating";
                return;
            }

            Logger.LogInfo("COMMAND consumes READY boundary: hospital leave");

            _hospitalReadySent = true;
            _hostReadyCatchupRequested = false;

            boundary.Hospital.HandleCloseButton();
            request.Response = "OK";
        }
    }
}
