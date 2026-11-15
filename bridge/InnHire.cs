using System;
using System.Collections.Generic;

using Assets.Code.Actor;
using Assets.Code.Game;
using Assets.Code.Inn;
using Assets.Code.Inn.Presentation;
using Assets.Code.Options;
using Assets.Code.UI.Options;
using Assets.Code.Roster;
using Assets.Code.Utils;

using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        // This is the temporary bounty-hunter hire advertised by the inn
        // poster, not the separate reserve/replacement roster screen.
        // Only the game's already-rolled HIRE candidates are exposed.
        private bool CanUseInnHire()
        {
            return IsStableInn() &&
                !OptionsGame.Instance.ProfileOptions.GetBoolValue(
                    OptionsValue.MOUNTAIN_ENABLED_IN_ALL_INNS_CONFESSIONS) &&
                Singleton<GameTypeMgr>.Instance != null &&
                Singleton<GameTypeMgr>.Instance.CurrentGameType == GameType.EXPEDITION &&
                Singleton<GameTypeMgr>.Instance.RosterManager != null &&
                Singleton<InnBhv>.Instance != null &&
                Singleton<InnBhv>.Instance.GetHireActorGuids() != null;
        }

        private JObject BuildInnHireState()
        {
            JArray candidates = new JArray();
            JArray party = new JArray();
            JArray actions = new JArray();

            if (CanUseInnHire())
            {
                IReadOnlyList<uint> offered =
                    Singleton<InnBhv>.Instance.GetHireActorGuids();
                // The native hunter hire screen exposes and hires only
                // the FIRST rolled candidate. Later internal GUIDs are hidden.
                if (offered.Count > 0)
                {
                    uint guid = offered[0];
                    ActorInstance actor = GetHeroActorInstance(guid);
                    if (actor != null)
                    {
                        candidates.Add(new JObject
                        {
                            ["actor_guid"] = guid,
                            ["actor_name"] = actor.ActorName ?? "",
                            ["actor_data_id"] = actor.ActorDataId ?? ""
                        });
                        actions.Add(new JObject
                        {
                            ["action"] = "inn_hire_select",
                            ["actor_guid"] = guid
                        });
                    }
                }

                IReadOnlyList<uint> partyGuids =
                    Singleton<GameTypeMgr>.Instance.RosterManager
                        .GetActorGuids(RosterStatusType.PARTY);
                for (int slot = 0; slot < partyGuids.Count; slot++)
                {
                    ActorInstance actor = GetHeroActorInstance(partyGuids[slot]);
                    party.Add(new JObject
                    {
                        ["slot"] = slot,
                        ["actor_guid"] = partyGuids[slot],
                        ["actor_name"] = actor == null ? "" : actor.ActorName ?? "",
                        ["actor_data_id"] = actor == null ? "" : actor.ActorDataId ?? ""
                    });
                }

                if (candidates.Count > 0)
                {
                    foreach (JToken candidate in candidates)
                    {
                        uint hireGuid = (uint)candidate["actor_guid"];
                        if (partyGuids.Count == 4)
                        {
                            foreach (uint replaced in partyGuids)
                            {
                                actions.Add(new JObject
                                {
                                    ["action"] = "inn_hire_confirm",
                                    ["actor_guid"] = hireGuid,
                                    ["replace_actor_guid"] = replaced
                                });
                            }
                        }
                        else
                        {
                            actions.Add(new JObject
                            {
                                ["action"] = "inn_hire_confirm",
                                ["actor_guid"] = hireGuid,
                                ["replace_actor_guid"] = 0
                            });
                        }
                    }
                }
            }

            actions.Add(new JObject { ["action"] = "inn_hire_state" });
            actions.Add(new JObject { ["action"] = "inn_hire_back" });

            return new JObject
            {
                ["type"] = "inn_hire_state",
                ["frontend_state"] = "inn_hire",
                ["current_mode"] = "inn",
                ["candidates"] = candidates,
                ["party"] = party,
                ["party_full"] = party.Count == 4,
                ["available_actions"] = actions
            };
        }

        private void HandleInnHireState(BridgeRequest request)
        {
            if (!CanUseInnHire())
            {
                request.Response = "ERROR: inn hire is not available in this mode";
                return;
            }
            QueueOutgoingMessage(BuildInnHireState());
            request.Response = "OK";
        }

        private void HandleInnHireConfirm(BridgeRequest request, string args)
        {
            if (!CanUseInnHire())
            {
                request.Response = "ERROR: inn hire is not available";
                return;
            }

            string[] parts = (args ?? "").Split(
                new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            uint hireGuid;
            uint replaceGuid;
            if (parts.Length != 2 ||
                !uint.TryParse(parts[0], out hireGuid) || hireGuid == 0 ||
                !uint.TryParse(parts[1], out replaceGuid))
            {
                request.Response = "ERROR: hire requires candidate_guid replace_actor_guid";
                return;
            }

            var roster = Singleton<GameTypeMgr>.Instance.RosterManager;
            IReadOnlyList<uint> offered = Singleton<InnBhv>.Instance.GetHireActorGuids();
            bool isOffered = offered.Count > 0 && offered[0] == hireGuid;
            if (!isOffered)
            {
                request.Response = "ERROR: candidate is no longer offered at this inn";
                QueueOutgoingMessage(BuildInnHireState());
                return;
            }

            IReadOnlyList<uint> partyGuids = roster.GetActorGuids(RosterStatusType.PARTY);
            if (partyGuids.Count == 4)
            {
                bool found = false;
                foreach (uint guid in partyGuids)
                    if (guid == replaceGuid) found = true;
                if (!found)
                {
                    request.Response = "ERROR: choose a current party hero to replace";
                    return;
                }
            }
            else if (replaceGuid != 0)
            {
                request.Response = "ERROR: no replacement needed when the party is not full";
                return;
            }

            // Exactly the native roster mutation used by
            // HunterHireScreenWidgetBhv.OnHirePressed. This also emits the
            // normal roster events and removes the offer from the inn.
            roster.Hire(hireGuid, replaceGuid);

            InnPresentationBhv presentation =
                SingletonMonoBehaviour<InnPresentationBhv>.Instance;
            if (presentation != null)
            {
                presentation.RebuildParty();
                presentation.HideHunterPoster();
            }

            QueueOutgoingMessage(new JObject
            {
                ["type"] = "inn_hire_result",
                ["hired_actor_guid"] = hireGuid,
                ["replaced_actor_guid"] = replaceGuid,
                ["party"] = BuildInnHireState()["party"].DeepClone()
            });
            QueueOutgoingMessage(BuildInnHireState());
            request.Response = "OK";
        }

        private void HandleInnHireBack(BridgeRequest request)
        {
            _innReadySent = true;
            QueueOutgoingMessage(BuildInnReadyMessage());
            request.Response = "OK";
        }
    }
}
