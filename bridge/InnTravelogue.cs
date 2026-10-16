using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;

using Assets.Code.Actor;
using Assets.Code.Data;
using Assets.Code.Game;
using Assets.Code.Inn;
using Assets.Code.Item;
using Assets.Code.Library;
using Assets.Code.Locale;
using Assets.Code.Quirk;
using Assets.Code.Run;
using Assets.Code.Run.RunLogEntry;
using Assets.Code.Source;
using Assets.Code.UI.RunLog;
using Assets.Code.UI.Screens;
using Assets.Code.UI.Widgets;
using Assets.Code.Utils;

using Newtonsoft.Json.Linq;

using UnityEngine;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        private SubScreenBiomeResultsBhv _innTravelogueReadyWidget;
        private bool _innTravelogueReadySent;

        private SubScreenBiomeResultsBhv GetStableInnTravelogueBoundary()
        {
            if (GameModeMgr.CurrentMode != GameModeType.INN)
            {
                ResetInnTravelogueBoundary();
                return null;
            }

            GameModeMgr modeMgr = GameModeMgr.Instance;
            if (modeMgr == null || modeMgr.IsChangingState())
            {
                ResetInnTravelogueBoundary();
                return null;
            }

            InnBhv inn = Singleton<InnBhv>.Instance;
            if (inn == null ||
                inn.GetInnState() != InnState.WAIT_TO_CLOSE_RESULTS)
            {
                ResetInnTravelogueBoundary();
                return null;
            }

            if (Singleton<GameTypeMgr>.Instance.CurrentGameType !=
                GameType.EXPEDITION)
            {
                ResetInnTravelogueBoundary();
                return null;
            }

            SubScreenBiomeResultsBhv cached =
                TryBuildStableInnTravelogueBoundary(
                    _innTravelogueReadyWidget);

            if (cached != null)
            {
                return cached;
            }

            SubScreenBiomeResultsBhv[] widgets =
                UnityEngine.Resources
                    .FindObjectsOfTypeAll<SubScreenBiomeResultsBhv>();

            foreach (SubScreenBiomeResultsBhv widget in widgets)
            {
                SubScreenBiomeResultsBhv boundary =
                    TryBuildStableInnTravelogueBoundary(widget);

                if (boundary == null)
                {
                    continue;
                }

                if (!ReferenceEquals(
                    boundary,
                    _innTravelogueReadyWidget))
                {
                    _innTravelogueReadyWidget = boundary;
                    _innTravelogueReadySent = false;

                    Logger.LogInfo(
                        $"READY boundary detected: inn_travelogue " +
                        $"path='{GetTransformPath(boundary.transform)}'");
                }

                return boundary;
            }

            ResetInnTravelogueBoundary();
            return null;
        }

        private SubScreenBiomeResultsBhv
            TryBuildStableInnTravelogueBoundary(
                SubScreenBiomeResultsBhv widget)
        {
            if (widget == null ||
                widget.gameObject == null ||
                !widget.gameObject.activeInHierarchy ||
                !widget.enabled ||
                widget.ScreenState != UiScreenState.Open)
            {
                return null;
            }

            GameObject continueBtn =
                GetMemberValue(widget, "m_continueBtn") as GameObject;

            if (continueBtn == null || !continueBtn.activeSelf)
            {
                return null;
            }

            UiRunLogBhv runLog =
                GetMemberValue(widget, "m_RunLogBhv") as UiRunLogBhv;

            if (runLog == null)
            {
                return null;
            }

            // OnScreenPushed populates this list synchronously before the
            // screen becomes the player's interactive travelogue.
            IEnumerable spawned =
                GetMemberValue(runLog, "m_SpawnedLogEntryTags")
                    as IEnumerable;

            if (spawned == null)
            {
                return null;
            }

            return widget;
        }

        private void ResetInnTravelogueBoundary()
        {
            _innTravelogueReadyWidget = null;
            _innTravelogueReadySent = false;
        }

        private static string GetContextString(
            DataContextBhv context,
            string key)
        {
            if (context == null)
            {
                return "";
            }

            try
            {
                return context.GetStringValue(key) ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string GetQuirkCategory(
            QuirkDefinition definition)
        {
            if (definition == null)
            {
                return "";
            }

            if (definition.IsDisease)
            {
                return "disease";
            }

            if (definition.IsCurse)
            {
                return "curse";
            }

            if (definition.IsNegative)
            {
                return "negative";
            }

            return "positive";
        }

        private JArray BuildTravelogueItemLinks(string rawText)
        {
            JArray result = new JArray();

            if (string.IsNullOrEmpty(rawText))
            {
                return result;
            }

            MatchCollection matches = Regex.Matches(
                rawText,
                "<link=\"(?<id>[^\"]+)_QTY_(?<qty>-?\\d+)\">" +
                "(?<label>.*?)</link>",
                RegexOptions.Singleline);

            foreach (Match match in matches)
            {
                string itemId =
                    match.Groups["id"].Value ?? "";
                string label =
                    match.Groups["label"].Value ?? "";

                int qty;
                if (!int.TryParse(
                    match.Groups["qty"].Value,
                    out qty))
                {
                    qty = 0;
                }

                string tooltip = "";

                try
                {
                    ItemDefinition definition =
                        SingletonMonoBehaviour<
                            Library<string, ItemDefinition>>
                            .Instance
                            .GetLibraryElement(itemId);

                    if (definition != null)
                    {
                        // Exact hover-description seam used by
                        // HoverTextItemTooltipBhv.
                        tooltip =
                            ItemDescription.GetDescription(
                                definition,
                                qty,
                                true,
                                -1,
                                false,
                                false) ?? "";
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(
                        $"Inn travelogue item tooltip failed " +
                        $"for '{itemId}': {ex.Message}");
                }

                result.Add(
                    new JObject
                    {
                        ["item_id"] = itemId,
                        ["qty"] = qty,
                        ["label"] = label,
                        ["tooltip"] = tooltip
                    });
            }

            return result;
        }

        private JObject BuildTravelogueQuirkDetails(
            QuirkLogEntry entry,
            DataContextBhv context)
        {
            if (entry == null)
            {
                return null;
            }

            QuirkDefinition definition =
                GetMemberValue(entry, "m_QuirkDefinition")
                    as QuirkDefinition;

            ActorLogData actorLogData =
                entry.ActorLogData;

            ActorDataClass actorClass = null;

            if (actorLogData != null &&
                !string.IsNullOrEmpty(
                    actorLogData.ActorDataClassId))
            {
                actorClass =
                    SingletonMonoBehaviour<
                        Library<string, ActorDataClass>>
                        .Instance
                        .GetLibraryElement(
                            actorLogData.ActorDataClassId);
            }

            string description = "";

            if (definition != null)
            {
                try
                {
                    // Same description seam populated by QuirkTooltipBhv.
                    description =
                        QuirkDescription.GetDescriptionString(
                            definition,
                            actorClass) ?? "";
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(
                        $"Inn travelogue quirk tooltip failed: " +
                        ex.Message);
                }
            }

            string source = "";
            string gender =
                actorClass == null
                    ? null
                    : actorClass.m_LocalizationGender;

            try
            {
                if (definition != null &&
                    entry.SourceType != null)
                {
                    source =
                        SourceDescription.GetSourceDescription(
                            entry.SourceType,
                            entry.SourceId,
                            gender) ?? "";
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    $"Inn travelogue quirk source failed: " +
                    ex.Message);
            }

            string actorName =
                actorLogData == null
                    ? ""
                    : actorLogData.ActorName;

            try
            {
                if (actorLogData != null)
                {
                    ActorInstance liveActor =
                        SingletonMonoBehaviour<
                            Library<uint, ActorInstance>>
                            .Instance
                            .GetLibraryElement(
                                actorLogData.ActorGuid);

                    if (liveActor != null &&
                        !string.IsNullOrEmpty(liveActor.ActorName))
                    {
                        actorName = liveActor.ActorName;
                    }

                    if (string.IsNullOrEmpty(actorName) &&
                        !string.IsNullOrEmpty(
                            actorLogData.ActorDataClassId))
                    {
                        actorName =
                            Singleton<Localization>.Instance.GetString(
                                actorLogData.ActorDataClassId) ?? "";
                    }
                }
            }
            catch
            {
            }

            string gainedLabel =
                GetContextString(context, "gained_label");
            string quirkLabel =
                GetContextString(context, "quirk_label");
            string sourceLabel =
                GetContextString(context, "source_label");

            try
            {
                Localization localization =
                    Singleton<Localization>.Instance;

                if (localization != null)
                {
                    if (string.IsNullOrEmpty(gainedLabel))
                    {
                        gainedLabel =
                            localization.GetSubstitutedText(
                                localization.GetString(
                                    "run_log_actor_label"),
                                "${hero_name}",
                                actorName) ?? "";
                    }

                    if (string.IsNullOrEmpty(quirkLabel) &&
                        definition != null)
                    {
                        string quirkName =
                            QuirkDescription.GetNameString(
                                definition,
                                gender) ?? "";

                        quirkLabel =
                            localization.GetSubstitutedText(
                                localization.GetString(
                                    "run_log_quirk_label"),
                                "${quirk_id}",
                                quirkName) ?? "";
                    }

                    if (string.IsNullOrEmpty(sourceLabel) &&
                        !string.IsNullOrEmpty(source))
                    {
                        sourceLabel =
                            localization.GetSubstitutedText(
                                localization.GetString(
                                    "run_log_source_label"),
                                "${source_id}",
                                source) ?? "";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    $"Inn travelogue quirk labels failed: " +
                    ex.Message);
            }

            JObject result =
                new JObject
                {
                    ["actor_guid"] =
                        actorLogData == null
                            ? 0u
                            : actorLogData.ActorGuid,
                    ["actor_name"] =
                        actorLogData == null
                            ? ""
                            : actorLogData.ActorName,
                    ["quirk_id"] =
                        definition == null
                            ? ""
                            : definition.m_Id,
                    ["category"] =
                        GetQuirkCategory(definition),
                    ["gained_label"] = gainedLabel,
                    ["quirk_label"] = quirkLabel,
                    ["source_label"] = sourceLabel,
                    ["source"] = source,
                    ["description"] = description
                };

            return result;
        }

        private List<RunLogEntryBase>
            GetCurrentTravelogueEntries()
        {
            List<RunLogEntryBase> result =
                new List<RunLogEntryBase>();

            try
            {
                IEnumerable entries =
                    Singleton<GameTypeMgr>
                        .Instance
                        .RunLogManager
                        .CurrentChapter
                        .GetAllEntries()
                        as IEnumerable;

                if (entries == null)
                {
                    return result;
                }

                foreach (object raw in entries)
                {
                    RunLogEntryBase entry =
                        raw as RunLogEntryBase;

                    if (entry != null)
                    {
                        result.Add(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    $"Inn travelogue entry read failed: " +
                    ex.Message);
            }

            return result;
        }

        private JArray BuildRenderedTravelogueEntries(
            SubScreenBiomeResultsBhv widget)
        {
            JArray result = new JArray();

            UiRunLogBhv runLog =
                GetMemberValue(widget, "m_RunLogBhv")
                    as UiRunLogBhv;

            if (runLog == null)
            {
                return result;
            }

            IEnumerable spawned =
                GetMemberValue(
                    runLog,
                    "m_SpawnedLogEntryTags")
                    as IEnumerable;

            if (spawned == null)
            {
                return result;
            }

            List<Component> rendered =
                new List<Component>();

            foreach (object raw in spawned)
            {
                Component component = raw as Component;
                if (component != null)
                {
                    rendered.Add(component);
                }
            }

            List<RunLogEntryBase> entries =
                GetCurrentTravelogueEntries();

            for (int index = 0;
                 index < rendered.Count;
                 index++)
            {
                Component component = rendered[index];

                DataContextBhv context =
                    component.GetComponent<DataContextBhv>();

                RunLogEntryBase entry =
                    index < entries.Count
                        ? entries[index]
                        : null;

                string body =
                    GetContextString(
                        context,
                        "body_text");

                string bonus =
                    GetContextString(
                        context,
                        "bonus_text");

                JObject item =
                    new JObject
                    {
                        ["index"] = index,
                        ["entry_type"] =
                            entry == null ||
                            entry.m_RunLogEntryType == null
                                ? ""
                                : entry.m_RunLogEntryType.GetName(),
                        ["entry_class"] =
                            entry == null
                                ? ""
                                : entry.GetType().Name,
                        ["text"] = body
                    };

                if (!string.IsNullOrEmpty(bonus))
                {
                    item["bonus_text"] = bonus;
                }

                JArray links =
                    BuildTravelogueItemLinks(body);

                if (!string.IsNullOrEmpty(bonus))
                {
                    JArray bonusLinks =
                        BuildTravelogueItemLinks(bonus);

                    foreach (JToken bonusLink in bonusLinks)
                    {
                        links.Add(bonusLink);
                    }
                }

                if (links.Count > 0)
                {
                    item["item_links"] = links;
                }

                QuirkLogEntry quirk =
                    entry as QuirkLogEntry;

                if (quirk != null)
                {
                    JObject quirkDetails =
                        BuildTravelogueQuirkDetails(
                            quirk,
                            context);

                    if (quirkDetails != null)
                    {
                        item["quirk"] = quirkDetails;
                    }
                }

                result.Add(item);
            }

            return result;
        }

        private DataContextBhv CreateTravelogueReplayContext(
            out GameObject gameObject)
        {
            gameObject =
                new GameObject(
                    "DD2Bridge.TravelogueReplayContext");

            DataContextBhv context =
                gameObject.AddComponent<DataContextBhv>();

            // DataContextBhv normally receives this serialized list from a
            // prefab. A bridge-only temporary component needs the same empty
            // backing collection before PopulateDataContext() can use it.
            FieldInfo valuesField =
                typeof(DataContextBhv).GetField(
                    "m_Values",
                    BindingFlags.Instance |
                    BindingFlags.NonPublic);

            if (valuesField != null)
            {
                valuesField.SetValue(
                    context,
                    new List<DataContextValue>());
            }

            return context;
        }

        private JArray BuildStoredTravelogueEntries()
        {
            JArray result = new JArray();
            List<RunLogEntryBase> entries =
                GetCurrentTravelogueEntries();

            for (int index = 0; index < entries.Count; index++)
            {
                RunLogEntryBase entry = entries[index];
                GameObject contextObject = null;

                try
                {
                    DataContextBhv context =
                        CreateTravelogueReplayContext(
                            out contextObject);

                    entry.PopulateDataContext(context);

                    string body =
                        GetContextString(
                            context,
                            "body_text");
                    string bonus =
                        GetContextString(
                            context,
                            "bonus_text");

                    JObject item =
                        new JObject
                        {
                            ["index"] = index,
                            ["entry_type"] =
                                entry.m_RunLogEntryType == null
                                    ? ""
                                    : entry.m_RunLogEntryType.GetName(),
                            ["entry_class"] =
                                entry.GetType().Name,
                            ["text"] = body
                        };

                    if (!string.IsNullOrEmpty(bonus))
                    {
                        item["bonus_text"] = bonus;
                    }

                    JArray links =
                        BuildTravelogueItemLinks(body);

                    if (!string.IsNullOrEmpty(bonus))
                    {
                        foreach (JToken bonusLink in
                            BuildTravelogueItemLinks(bonus))
                        {
                            links.Add(bonusLink);
                        }
                    }

                    if (links.Count > 0)
                    {
                        item["item_links"] = links;
                    }

                    QuirkLogEntry quirk =
                        entry as QuirkLogEntry;

                    if (quirk != null)
                    {
                        JObject quirkDetails =
                            BuildTravelogueQuirkDetails(
                                quirk,
                                context);

                        if (quirkDetails != null)
                        {
                            item["quirk"] = quirkDetails;
                        }
                    }

                    result.Add(item);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(
                        $"Inn travelogue replay entry {index} " +
                        $"failed: {ex.Message}");

                    result.Add(
                        new JObject
                        {
                            ["index"] = index,
                            ["entry_type"] =
                                entry == null ||
                                entry.m_RunLogEntryType == null
                                    ? ""
                                    : entry.m_RunLogEntryType.GetName(),
                            ["entry_class"] =
                                entry == null
                                    ? ""
                                    : entry.GetType().Name,
                            ["text"] = "",
                            ["error"] = ex.Message
                        });
                }
                finally
                {
                    if (contextObject != null)
                    {
                        UnityEngine.Object.Destroy(
                            contextObject);
                    }
                }
            }

            return result;
        }

        private string GetCurrentInnName()
        {
            try
            {
                InnBhv inn = Singleton<InnBhv>.Instance;

                if (inn != null &&
                    inn.GetInnInstance() != null)
                {
                    return inn.GetInnInstance().Name ?? "";
                }
            }
            catch
            {
            }

            return "";
        }

        private JObject BuildStoredInnTravelogueMessage()
        {
            return new JObject
            {
                ["type"] = "inn_travelogue_state",
                ["inn_name"] = GetCurrentInnName(),
                ["journal"] = BuildStoredTravelogueEntries()
            };
        }

        private void HandleInnTravelogueState(
            BridgeRequest request)
        {
            if (!IsStableInn())
            {
                request.Response =
                    "ERROR: travelogue is only available at the inn";
                return;
            }

            JObject state =
                BuildStoredInnTravelogueMessage();

            Logger.LogInfo(
                $"QUERY inn travelogue: " +
                $"entries={((JArray)state["journal"]).Count}");

            QueueOutgoingMessage(state);
            request.Response = "OK";
        }

        private JObject BuildInnTravelogueReadyMessage(
            SubScreenBiomeResultsBhv widget)
        {
            string innName = GetCurrentInnName();

            return new JObject
            {
                ["type"] = "ready",
                ["frontend_state"] = "inn_travelogue",
                ["current_mode"] = "inn",
                ["inn_name"] = innName,
                ["journal"] =
                    BuildRenderedTravelogueEntries(widget),
                ["available_actions"] =
                    new JArray
                    {
                        new JObject
                        {
                            ["action"] = "continue_travelogue"
                        }
                    }
            };
        }

        private bool ProcessInnTravelogueReady()
        {
            SubScreenBiomeResultsBhv widget =
                GetStableInnTravelogueBoundary();

            if (widget == null)
            {
                ResetInnTravelogueBoundary();
                return false;
            }

            if (_innTravelogueReadySent)
            {
                return true;
            }

            JObject ready =
                BuildInnTravelogueReadyMessage(widget);

            _innTravelogueReadySent = true;
            _hostReadyCatchupRequested = false;

            Logger.LogInfo(
                $"READY inn_travelogue: " +
                $"entries={((JArray)ready["journal"]).Count}");

            QueueOutgoingMessage(ready);
            return true;
        }

        private bool TryProcessInnTravelogueCatchup()
        {
            SubScreenBiomeResultsBhv widget =
                GetStableInnTravelogueBoundary();

            if (widget == null)
            {
                return false;
            }

            JObject ready =
                BuildInnTravelogueReadyMessage(widget);

            _innTravelogueReadySent = true;

            Logger.LogInfo(
                "READY catch-up: inn_travelogue");

            QueueOutgoingMessage(ready);
            return true;
        }

        private bool TryHandleInnTravelogueContinue(
            BridgeRequest request)
        {
            SubScreenBiomeResultsBhv widget =
                GetStableInnTravelogueBoundary();

            if (widget == null)
            {
                return false;
            }

            Logger.LogInfo(
                "COMMAND consumes READY boundary: " +
                "inn_travelogue continue");

            _innTravelogueReadySent = true;
            _hostReadyCatchupRequested = false;

            // Native UI seam. OnSubScreenPop() advances InnBhv from
            // WAIT_TO_CLOSE_RESULTS into the rest of the Inn state machine.
            widget.HandleContinueButton();

            QueueActivityStatus(
                "continuing inn travelogue");

            request.Response = "OK";
            return true;
        }
    }
}

