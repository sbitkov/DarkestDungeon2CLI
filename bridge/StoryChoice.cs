using System;
using System.Collections.Generic;
using System.Reflection;

using Assets.Code.Actor;
using Assets.Code.Game;
using Assets.Code.Library;
using Assets.Code.Locale;
using Assets.Code.Story;
using Assets.Code.UI.Screens;
using Assets.Code.UI.Story;
using Assets.Code.Utils;

using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        private bool _storyChoiceReadySent;

        private static readonly BindingFlags StoryChoiceInstanceFlags =
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic;

        private static readonly FieldInfo StoryChoiceButtonsField =
            typeof(StoryScreenBhv).GetField(
                "m_StoryChoiceButtons",
                StoryChoiceInstanceFlags);

        private static readonly FieldInfo StoryTitleTextField =
            typeof(StoryScreenBhv).GetField(
                "m_storyTitleText",
                StoryChoiceInstanceFlags);

        private static readonly FieldInfo StoryButtonTypeField =
            typeof(StoryChoiceButtonBhv).GetField(
                "m_StoryType",
                StoryChoiceInstanceFlags);

        private static readonly FieldInfo StoryButtonBarkField =
            typeof(StoryChoiceButtonBhv).GetField(
                "m_barkString",
                StoryChoiceInstanceFlags);

        private static readonly FieldInfo StoryButtonQuirkField =
            typeof(StoryChoiceButtonBhv).GetField(
                "m_quirkChoiceId",
                StoryChoiceInstanceFlags);

        private static readonly MethodInfo StoryButtonSelectMethod =
            typeof(StoryChoiceButtonBhv).GetMethod(
                "OnChoiceSelected",
                StoryChoiceInstanceFlags);

        private bool ProcessStoryChoiceReady()
        {
            StoryScreenBhv screen;
            List<StoryChoiceButtonBhv> choices;

            if (!TryGetStableStoryChoice(out screen, out choices))
            {
                _storyChoiceReadySent = false;
                return false;
            }

            if (_storyChoiceReadySent)
            {
                return true;
            }

            JObject ready =
                BuildStoryChoiceReadyMessage(screen, choices);

            JArray actions =
                ready["available_actions"] as JArray;

            if (actions == null || actions.Count == 0)
            {
                _storyChoiceReadySent = false;
                return false;
            }

            _storyChoiceReadySent = true;

            Logger.LogInfo(
                $"READY story_choice: choices={actions.Count}");

            QueueOutgoingMessage(ready);
            return true;
        }

        private bool TryProcessStoryChoiceCatchup()
        {
            StoryScreenBhv screen;
            List<StoryChoiceButtonBhv> choices;

            if (!TryGetStableStoryChoice(out screen, out choices))
            {
                return false;
            }

            JObject ready =
                BuildStoryChoiceReadyMessage(screen, choices);

            JArray actions =
                ready["available_actions"] as JArray;

            if (actions == null || actions.Count == 0)
            {
                return false;
            }

            _storyChoiceReadySent = true;

            Logger.LogInfo(
                $"READY catch-up: story_choice choices={actions.Count}");

            QueueOutgoingMessage(ready);
            return true;
        }

        private bool TryGetStableStoryChoice(
            out StoryScreenBhv selectedScreen,
            out List<StoryChoiceButtonBhv> selectedChoices)
        {
            selectedScreen = null;
            selectedChoices = null;

            if (GameModeMgr.CurrentMode != GameModeType.DRIVING)
            {
                return false;
            }

            GameModeMgr mgr = GameModeMgr.Instance;

            if (mgr == null || mgr.IsChangingState())
            {
                return false;
            }

            if (StoryChoiceButtonsField == null ||
                StoryButtonSelectMethod == null)
            {
                return false;
            }

            StoryScreenBhv[] screens =
                UnityEngine.Resources
                    .FindObjectsOfTypeAll<StoryScreenBhv>();

            foreach (StoryScreenBhv screen in screens)
            {
                if (screen == null ||
                    !screen.gameObject.activeInHierarchy ||
                    !screen.enabled)
                {
                    continue;
                }

                List<StoryChoiceButtonBhv> buttons =
                    StoryChoiceButtonsField.GetValue(screen)
                        as List<StoryChoiceButtonBhv>;

                if (buttons == null || buttons.Count == 0)
                {
                    continue;
                }

                List<StoryChoiceButtonBhv> available =
                    new List<StoryChoiceButtonBhv>();

                foreach (StoryChoiceButtonBhv button in buttons)
                {
                    if (button == null ||
                        !button.gameObject.activeInHierarchy ||
                        button.Selectable == null ||
                        !button.Selectable.enabled ||
                        !button.Hoverable ||
                        button.ActorGuid == 0u)
                    {
                        continue;
                    }

                    StoryChoiceDefinition definition =
                        GetStoryChoiceForActor(
                            button.ActorGuid);

                    if (definition == null)
                    {
                        continue;
                    }

                    available.Add(button);
                }

                if (available.Count == 0)
                {
                    continue;
                }

                selectedScreen = screen;
                selectedChoices = available;
                return true;
            }

            return false;
        }

        private JObject BuildStoryChoiceReadyMessage(
            StoryScreenBhv screen,
            List<StoryChoiceButtonBhv> choices)
        {
            JArray choiceArray = new JArray();
            JArray actions = new JArray();

            object storyType = null;

            foreach (StoryChoiceButtonBhv button in choices)
            {
                StoryChoiceDefinition definition =
                    GetStoryChoiceForActor(
                        button.ActorGuid);

                if (definition == null)
                {
                    continue;
                }

                if (storyType == null &&
                    StoryButtonTypeField != null)
                {
                    storyType =
                        StoryButtonTypeField.GetValue(button);
                }

                ActorInstance actor = null;

                if (SingletonMonoBehaviour<
                        Library<uint, ActorInstance>>.HasInstance())
                {
                    actor =
                        SingletonMonoBehaviour<
                            Library<uint, ActorInstance>>
                            .Instance
                            .GetLibraryElement(
                                button.ActorGuid);
                }

                string quirkId =
                    StoryButtonQuirkField == null
                        ? null
                        : StoryButtonQuirkField
                            .GetValue(button) as string;

                string bark =
                    StoryButtonBarkField == null
                        ? null
                        : StoryButtonBarkField
                            .GetValue(button) as string;

                JObject choice = new JObject
                {
                    ["actor_guid"] = button.ActorGuid,
                    ["actor_name"] =
                        actor == null
                            ? ""
                            : actor.ActorName,
                    ["actor_data_id"] =
                        actor == null
                            ? ""
                            : actor.ActorDataId,
                    ["choice_id"] =
                        GetStoryChoiceDefinitionId(
                            definition),
                    ["bark"] = bark ?? "",
                    ["quirk_id"] = quirkId ?? "",
                    ["quirk_name"] =
                        GetStoryChoiceQuirkName(
                            quirkId),
                    ["player_previews"] =
                        BuildStoryChoicePreviews(
                            definition
                                .m_PlayerStoryChoicePreviewIds,
                            definition
                                .m_PlayerStoryChoicePreviewValues,
                            definition
                                .m_PlayerStoryChoicePreviewShowNumbers),
                    ["enemy_previews"] =
                        BuildStoryChoicePreviews(
                            definition
                                .m_EnemyStoryChoicePreviewIds,
                            definition
                                .m_EnemyStoryChoicePreviewValues,
                            definition
                                .m_EnemyStoryChoicePreviewShowNumbers)
                };

                choiceArray.Add(choice);

                actions.Add(
                    new JObject
                    {
                        ["action"] = "choose_story",
                        ["actor_guid"] = button.ActorGuid
                    });
            }

            return new JObject
            {
                ["type"] = "ready",
                ["frontend_state"] = "story_choice",
                ["story_type"] =
                    FormatStoryChoiceValue(storyType),
                ["title"] =
                    GetStoryChoiceTitle(screen),
                ["choices"] = choiceArray,
                ["available_actions"] = actions
            };
        }

        private static JArray BuildStoryChoicePreviews(
            IReadOnlyList<string> ids,
            IReadOnlyList<int> values,
            IReadOnlyList<bool> showNumbers)
        {
            JArray result = new JArray();

            if (ids == null)
            {
                return result;
            }

            for (int i = 0; i < ids.Count; i++)
            {
                string previewId = ids[i] ?? "";
                int value =
                    values != null && i < values.Count
                        ? values[i]
                        : 0;
                bool showNumber =
                    showNumbers != null &&
                    i < showNumbers.Count &&
                    showNumbers[i];

                result.Add(
                    new JObject
                    {
                        ["preview_id"] = previewId,
                        ["value"] = value,
                        ["show_number"] = showNumber,
                        ["description"] =
                            GetStoryChoicePreviewDescription(
                                previewId,
                                value)
                    });
            }

            return result;
        }

        private static string GetStoryChoicePreviewDescription(
            string previewId,
            int value)
        {
            if (string.IsNullOrEmpty(previewId))
            {
                return "";
            }

            Localization localization =
                Singleton<Localization>.Instance;

            if (localization == null)
            {
                return "";
            }

            string text = null;

            if (value < 0)
            {
                text = localization.TryGetString(
                    "story_icon_description_" +
                    previewId +
                    "_negative");
            }

            if (string.IsNullOrEmpty(text))
            {
                text = localization.TryGetString(
                    "story_icon_description_" +
                    previewId);
            }

            return text ?? "";
        }

        private static string GetStoryChoiceQuirkName(
            string quirkId)
        {
            if (string.IsNullOrEmpty(quirkId))
            {
                return "";
            }

            Localization localization =
                Singleton<Localization>.Instance;

            if (localization == null)
            {
                return "";
            }

            string name =
                localization.TryGetString(quirkId);

            return name ?? "";
        }

        private static string GetStoryChoiceTitle(
            StoryScreenBhv screen)
        {
            if (screen == null ||
                StoryTitleTextField == null)
            {
                return "";
            }

            object titleComponent =
                StoryTitleTextField.GetValue(screen);

            if (titleComponent == null)
            {
                return "";
            }

            PropertyInfo textProperty =
                titleComponent.GetType().GetProperty(
                    "text",
                    BindingFlags.Instance |
                    BindingFlags.Public);

            if (textProperty == null)
            {
                return "";
            }

            object text =
                textProperty.GetValue(
                    titleComponent,
                    null);

            return text == null
                ? ""
                : text.ToString();
        }

        private static StoryChoiceDefinition GetStoryChoiceForActor(
            uint actorGuid)
        {
            Type storyBhvType =
                typeof(StoryChoiceDefinition).Assembly.GetType(
                    "Assets.Code.Story.StoryBhv");

            if (storyBhvType == null)
            {
                return null;
            }

            UnityEngine.Object[] instances =
                UnityEngine.Resources.FindObjectsOfTypeAll(
                    storyBhvType);

            if (instances == null || instances.Length == 0)
            {
                return null;
            }

            MethodInfo getChoice =
                storyBhvType.GetMethod(
                    "GetStoryChoiceFromActorGuid",
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic,
                    null,
                    new Type[] { typeof(uint) },
                    null);

            if (getChoice == null)
            {
                return null;
            }

            foreach (UnityEngine.Object instance in instances)
            {
                if (instance == null)
                {
                    continue;
                }

                object value =
                    getChoice.Invoke(
                        instance,
                        new object[] { actorGuid });

                StoryChoiceDefinition definition =
                    value as StoryChoiceDefinition;

                if (definition != null)
                {
                    return definition;
                }
            }

            return null;
        }

        private static string GetStoryChoiceDefinitionId(
            StoryChoiceDefinition definition)
        {
            if (definition == null)
            {
                return "";
            }

            Type type = definition.GetType();

            while (type != null)
            {
                FieldInfo idField =
                    type.GetField(
                        "m_Id",
                        StoryChoiceInstanceFlags);

                if (idField != null)
                {
                    object value =
                        idField.GetValue(definition);

                    return value == null
                        ? ""
                        : value.ToString();
                }

                type = type.BaseType;
            }

            return "";
        }

        private static string FormatStoryChoiceValue(
            object value)
        {
            if (value == null)
            {
                return "";
            }

            MethodInfo getName =
                value.GetType().GetMethod(
                    "GetName",
                    BindingFlags.Instance |
                    BindingFlags.Public);

            if (getName != null)
            {
                object name =
                    getName.Invoke(
                        value,
                        null);

                if (name != null)
                {
                    return name.ToString();
                }
            }

            return value.ToString();
        }

        private void HandleStoryChoice(
            BridgeRequest request,
            string actorGuidText)
        {
            uint actorGuid;

            if (!uint.TryParse(
                    actorGuidText,
                    out actorGuid) ||
                actorGuid == 0u)
            {
                request.Response =
                    "ERROR: choose_story requires actor_guid";
                return;
            }

            StoryScreenBhv screen;
            List<StoryChoiceButtonBhv> choices;

            if (!TryGetStableStoryChoice(
                    out screen,
                    out choices))
            {
                request.Response =
                    "ERROR: no stable story choice is active";
                return;
            }

            StoryChoiceButtonBhv selected = null;

            foreach (StoryChoiceButtonBhv button in choices)
            {
                if (button.ActorGuid == actorGuid)
                {
                    selected = button;
                    break;
                }
            }

            if (selected == null)
            {
                request.Response =
                    "ERROR: actor is not an available story choice";
                return;
            }

            StoryChoiceDefinition definition =
                GetStoryChoiceForActor(actorGuid);

            Logger.LogInfo(
                $"STORY choose: actor={actorGuid}, " +
                $"choice='{GetStoryChoiceDefinitionId(definition)}'");

            // This is the semantic seam reached by the native hold UI.
            StoryButtonSelectMethod.Invoke(
                selected,
                null);

            _storyChoiceReadySent = false;

            QueueActivityStatus("resolving story choice");

            request.Response = "OK";
        }
    }
}
