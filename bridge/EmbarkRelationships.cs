using System;
using System.Collections.Generic;
using System.Linq;

using Assets.Code.Actor;
using Assets.Code.Affinity;
using Assets.Code.Affinity.Presentation;
using Assets.Code.Embark;
using Assets.Code.Game;
using Assets.Code.Inn;
using Assets.Code.Inn.Presentation;
using Assets.Code.Library;
using Assets.Code.Locale;
using Assets.Code.Map.Generation.Biome;
using Assets.Code.Skill;
using Assets.Code.UI;
using Assets.Code.Utils;

using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        private bool _embarkReadySent;
        private bool _innEmbarkReturnPending;

        private const string ConfirmationContextInnEmbark =
            "inn_embark_departure";

        private EmbarkUiBhv GetStableEmbarkUi()
        {
            GameModeMgr mgr = GameModeMgr.Instance;

            if (mgr == null ||
                mgr.IsChangingState() ||
                GameModeMgr.CurrentMode != GameModeType.EMBARK)
            {
                return null;
            }

            EmbarkUiBhv ui =
                UnityEngine.Object.FindObjectOfType<EmbarkUiBhv>(
                    includeInactive: true);

            if (ui == null ||
                ui.gameObject == null ||
                !ui.gameObject.activeInHierarchy)
            {
                return null;
            }

            if (ui.IsApplying)
            {
                return null;
            }

            if (GetMemberValue(ui, "m_embarkButtonBhv") == null ||
                GetMemberValue(ui, "m_mouseEmbarkButtonBhv") == null)
            {
                return null;
            }

            // EmbarkUiBhv exists before its intro/Init pipeline has necessarily
            // populated the relationship cards.  Do not publish a decision
            // boundary until the native UI has initialized at least one card
            // when the AffinityManager says cards should be present.
            List<AffinityConnection> expectedConnections =
                GetEmbarkRelationshipConnections();

            if (expectedConnections.Count > 0)
            {
                bool hasInitializedRelationshipButton = false;

                foreach (EmbarkRelationshipBtnBhv button in
                    ui.GetComponentsInChildren<EmbarkRelationshipBtnBhv>(
                        includeInactive: true))
                {
                    if (GetMemberValue(button, "m_affinityConnection") != null)
                    {
                        hasInitializedRelationshipButton = true;

                        if (button.IsPlayingIntro)
                        {
                            return null;
                        }
                    }
                }

                if (!hasInitializedRelationshipButton)
                {
                    return null;
                }
            }

            if (Singleton<GameTypeMgr>.Instance != null &&
                Singleton<GameTypeMgr>.Instance.AffinityManager != null &&
                Singleton<GameTypeMgr>.Instance.AffinityManager
                    .GetHasConnectionWithPendingRelationship())
            {
                return null;
            }

            if (SingletonMonoBehaviour<RelationshipTestPresentation>
                    .HasInstance() &&
                SingletonMonoBehaviour<RelationshipTestPresentation>
                    .Instance.IsShowing)
            {
                return null;
            }

            return ui;
        }

        private List<AffinityConnection> GetEmbarkRelationshipConnections()
        {
            List<AffinityConnection> result =
                new List<AffinityConnection>();

            if (Singleton<GameTypeMgr>.Instance == null ||
                Singleton<GameTypeMgr>.Instance.AffinityManager == null)
            {
                return result;
            }

            AffinityManager manager =
                Singleton<GameTypeMgr>.Instance.AffinityManager;

            int count = manager.GetNumberOfConnections();

            for (int i = 0; i < count; i++)
            {
                AffinityConnection connection =
                    manager.GetConnectionAtIndex(i);

                if (connection == null || !connection.IsActive)
                {
                    continue;
                }

                bool shouldShow;

                if (!connection.GetHasCurrentRelationship())
                {
                    shouldShow =
                        connection.GetHasCalculatedRelationship();
                }
                else
                {
                    shouldShow =
                        manager.GetDoesConnectionHaveNewRelationship(
                            connection);
                }

                if (shouldShow)
                {
                    result.Add(connection);
                }
            }

            return result;
        }

        private JObject BuildEmbarkActorJson(uint actorGuid)
        {
            ActorInstance actor =
                SingletonMonoBehaviour<Library<uint, ActorInstance>>
                    .Instance
                    .GetLibraryElement(actorGuid);

            if (actor == null)
            {
                return new JObject
                {
                    ["actor_guid"] = actorGuid
                };
            }

            return new JObject
            {
                ["actor_guid"] = actorGuid,
                ["actor_name"] = actor.ActorName ?? string.Empty,
                ["actor_data_id"] = actor.ActorDataId ?? string.Empty
            };
        }

        private JObject BuildEmbarkModifiedSkillJson(
            ActorInstance actor,
            AffinityConnection connection)
        {
            if (actor == null || connection == null)
            {
                return null;
            }

            SkillInstance skill =
                actor.GetModifiedCombatSkillInstance(connection);

            if (skill == null)
            {
                return null;
            }

            JObject result = new JObject
            {
                ["skill_id"] = skill.SkillId ?? string.Empty
            };

            JObject skillInfo =
                BuildSkillInfo(actor, skill.SkillId);

            if (skillInfo != null)
            {
                result["skill_info"] = skillInfo;
            }

            return result;
        }

        private JObject BuildEmbarkRelationshipCardJson(
            int index,
            AffinityConnection connection)
        {
            JArray actors = new JArray();

            foreach (uint actorGuid in connection.ActorGuids)
            {
                actors.Add(BuildEmbarkActorJson(actorGuid));
            }

            bool revealed =
                connection.GetHasCurrentRelationship();

            JObject card = new JObject
            {
                ["index"] = index,
                ["revealed"] = revealed,
                ["actors"] = actors
            };

            if (!revealed)
            {
                return card;
            }

            AffinityRelationshipDefinition relationship =
                connection.GetCurrentRelationship();

            if (relationship == null)
            {
                return card;
            }

            bool isPositive =
                relationship.m_Tags != null &&
                relationship.m_Tags.Contains("positive");

            bool isNegative =
                relationship.m_Tags != null &&
                relationship.m_Tags.Contains("negative");

            card["relationship_id"] = relationship.m_Id ?? string.Empty;
            card["relationship_name"] =
                Singleton<Localization>.Instance.GetString(
                    relationship.m_Id) ?? string.Empty;
            card["positive"] = isPositive;
            card["negative"] = isNegative;

            JArray modifiedSkills = new JArray();

            foreach (uint actorGuid in connection.ActorGuids)
            {
                ActorInstance actor =
                    SingletonMonoBehaviour<Library<uint, ActorInstance>>
                        .Instance
                        .GetLibraryElement(actorGuid);

                JObject skill =
                    BuildEmbarkModifiedSkillJson(
                        actor,
                        connection);

                JObject entry = new JObject
                {
                    ["actor_guid"] = actorGuid
                };

                if (skill != null)
                {
                    entry["skill"] = skill;
                }

                modifiedSkills.Add(entry);
            }

            card["modified_skills"] = modifiedSkills;

            return card;
        }

        private JObject BuildEmbarkRelationshipsReadyMessage()
        {
            List<AffinityConnection> connections =
                GetEmbarkRelationshipConnections();

            JArray cards = new JArray();
            JArray actions = new JArray();

            bool allRevealed = true;

            for (int i = 0; i < connections.Count; i++)
            {
                AffinityConnection connection = connections[i];
                JObject card =
                    BuildEmbarkRelationshipCardJson(i, connection);

                cards.Add(card);

                bool revealed =
                    card["revealed"]?.Value<bool>() ?? false;

                if (!revealed)
                {
                    allRevealed = false;

                    JArray actorGuids = new JArray();
                    foreach (uint actorGuid in connection.ActorGuids)
                    {
                        actorGuids.Add(actorGuid);
                    }

                    actions.Add(
                        new JObject
                        {
                            ["action"] = "reveal_relationship",
                            ["actor_guids"] = actorGuids,
                            ["consumes_boundary"] = true
                        });
                }
            }

            if (allRevealed)
            {
                actions.Add(
                    new JObject
                    {
                        ["action"] = "continue_embark",
                        ["consumes_boundary"] = true
                    });
            }

            JObject ready = new JObject
            {
                ["type"] = "ready",
                ["frontend_state"] = "embark_relationships",
                ["current_mode"] = "embark",
                ["relationships"] = cards,
                ["all_relationships_revealed"] = allRevealed,
                ["available_actions"] = actions
            };

            if (SingletonMonoBehaviour<EmbarkBhv>.HasInstance())
            {
                BiomeChoice nextBiome =
                    SingletonMonoBehaviour<EmbarkBhv>.Instance.NextBiomeChoice;

                if (nextBiome != null &&
                    nextBiome.m_BiomeGenerationData != null)
                {
                    BiomeType biomeType =
                        nextBiome.m_BiomeGenerationData.GetBiomeType();

                    if (biomeType != null)
                    {
                        ready["next_biome_id"] = biomeType.ToString();
                        ready["next_biome_name"] =
                            Singleton<Localization>.Instance.GetString(
                                "biome_name_" + biomeType.ToString()) ??
                            string.Empty;
                    }
                }
            }

            return ready;
        }

        private bool ProcessEmbarkRelationshipsReady()
        {
            EmbarkUiBhv ui = GetStableEmbarkUi();

            if (ui == null)
            {
                _embarkReadySent = false;
                return false;
            }

            if (_embarkReadySent)
            {
                return true;
            }

            JObject ready =
                BuildEmbarkRelationshipsReadyMessage();

            _embarkReadySent = true;
            _hostReadyCatchupRequested = false;

            Logger.LogInfo(
                $"READY embark_relationships " +
                $"cards={(ready["relationships"] as JArray)?.Count ?? 0} " +
                $"all_revealed={ready["all_relationships_revealed"]}");

            QueueOutgoingMessage(ready);
            return true;
        }

        private bool TryProcessEmbarkRelationshipsCatchup()
        {
            EmbarkUiBhv ui = GetStableEmbarkUi();

            if (ui == null)
            {
                return false;
            }

            _embarkReadySent = true;

            Logger.LogInfo(
                "READY catch-up: embark_relationships");

            QueueOutgoingMessage(
                BuildEmbarkRelationshipsReadyMessage());

            return true;
        }

        private AffinityConnection FindEmbarkConnection(
            uint actorGuidA,
            uint actorGuidB)
        {
            if (actorGuidA == 0u ||
                actorGuidB == 0u ||
                actorGuidA == actorGuidB ||
                Singleton<GameTypeMgr>.Instance == null ||
                Singleton<GameTypeMgr>.Instance.AffinityManager == null)
            {
                return null;
            }

            return Singleton<GameTypeMgr>
                .Instance
                .AffinityManager
                .GetConnection(actorGuidA, actorGuidB);
        }

        private EmbarkRelationshipBtnBhv FindEmbarkRelationshipButton(
            EmbarkUiBhv ui,
            AffinityConnection connection)
        {
            if (ui == null || connection == null)
            {
                return null;
            }

            foreach (EmbarkRelationshipBtnBhv button in
                ui.GetComponentsInChildren<EmbarkRelationshipBtnBhv>(
                    includeInactive: true))
            {
                object boundConnection =
                    GetMemberValue(button, "m_affinityConnection");

                if (ReferenceEquals(boundConnection, connection))
                {
                    return button;
                }
            }

            return null;
        }

        private void HandleEmbarkRevealRelationship(
            BridgeRequest request,
            string args)
        {
            EmbarkUiBhv ui = GetStableEmbarkUi();

            if (ui == null)
            {
                request.Response =
                    "ERROR: embark relationship screen is not ready";
                return;
            }

            string[] parts =
                (args ?? string.Empty)
                    .Split(
                        new[] { ' ' },
                        StringSplitOptions.RemoveEmptyEntries);

            uint actorGuidA;
            uint actorGuidB;

            if (parts.Length != 2 ||
                !uint.TryParse(parts[0], out actorGuidA) ||
                !uint.TryParse(parts[1], out actorGuidB))
            {
                request.Response =
                    "ERROR: embark-reveal-relationship requires two actor guids";
                return;
            }

            AffinityConnection connection =
                FindEmbarkConnection(actorGuidA, actorGuidB);

            if (connection == null || !connection.IsActive)
            {
                request.Response =
                    "ERROR: relationship connection is not active";
                return;
            }

            bool shown = false;
            foreach (AffinityConnection candidate in
                GetEmbarkRelationshipConnections())
            {
                if (ReferenceEquals(candidate, connection))
                {
                    shown = true;
                    break;
                }
            }

            if (!shown)
            {
                request.Response =
                    "ERROR: that relationship is not on the embark screen";
                return;
            }

            if (connection.GetHasCurrentRelationship())
            {
                request.Response =
                    "ERROR: relationship is already revealed";
                return;
            }

            if (!connection.GetHasCalculatedRelationship())
            {
                request.Response =
                    "ERROR: relationship has no unrevealed result";
                return;
            }

            if (Singleton<GameTypeMgr>.Instance.AffinityManager
                    .GetHasConnectionWithPendingRelationship())
            {
                request.Response =
                    "ERROR: another relationship reveal is already in progress";
                return;
            }

            EmbarkRelationshipBtnBhv button =
                FindEmbarkRelationshipButton(ui, connection);

            if (button == null)
            {
                request.Response =
                    "ERROR: native relationship button is unavailable";
                return;
            }

            Logger.LogInfo(
                $"COMMAND embark_relationships: reveal " +
                $"actors={actorGuidA},{actorGuidB}");

            // Use the exact native relationship-card action.  It moves the
            // calculated relationship to pending and lets AffinityPresentation
            // run the normal reveal timeline/application pipeline.
            button.OnRelationshipButtonPressed();

            _embarkReadySent = false;
            QueueActivityStatus("revealing relationship");

            request.Response = "OK";
        }

        private void HandleEmbarkContinue(BridgeRequest request)
        {
            EmbarkUiBhv ui = GetStableEmbarkUi();

            if (ui == null)
            {
                request.Response =
                    "ERROR: embark relationship screen is not ready";
                return;
            }

            if (!ui.HasRelationshipsApplied)
            {
                request.Response =
                    "ERROR: reveal all relationships before continuing";
                return;
            }

            Logger.LogInfo(
                "COMMAND embark_relationships: continue_embark");

            _embarkReadySent = true;
            QueueActivityStatus("departing for next region");
            ui.OnEmbark();

            request.Response = "OK";
        }

        private void HandleInnEmbark(BridgeRequest request)
        {
            if (GameModeMgr.CurrentMode != GameModeType.INN ||
                Singleton<InnBhv>.Instance == null)
            {
                request.Response =
                    "ERROR: not currently in an inn";
                return;
            }

            if (!Singleton<InnBhv>.Instance.GetCanEmbark())
            {
                request.Response =
                    "ERROR: no route is selected for embark";
                return;
            }

            if (Singleton<GameTypeMgr>.Instance.PlayerInventory != null &&
                Singleton<GameTypeMgr>.Instance.PlayerInventory.IsOverfilled)
            {
                request.Response =
                    "ERROR: inventory is overfilled";
                return;
            }

            if (!SingletonMonoBehaviour<InnPresentationBhv>.HasInstance())
            {
                request.Response =
                    "ERROR: inn presentation is unavailable";
                return;
            }

            Logger.LogInfo("COMMAND inn: embark");

            SetPendingConfirmationContext(
                ConfirmationContextInnEmbark);

            SingletonMonoBehaviour<InnPresentationBhv>
                .Instance
                .OnEmbark();

            _innReadySent = true;
            request.Response = "OK";
        }
    }
}
