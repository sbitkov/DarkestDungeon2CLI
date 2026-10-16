using BepInEx;
using HarmonyLib;

using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;

using Assets.Code.Game;
using Assets.Code.Game.StageCoach;
using Assets.Code.Map;
using Assets.Code.Combat;
using Assets.Code.Combat.Events;
using Assets.Code.Actor;
using Assets.Code.Actor.Queries;
using Assets.Code.Actor.ActorController;
using Assets.Code.Actor.Events;
using Assets.Code.Affinity.Events;
using Assets.Code.Library;
using Assets.Code.Skill;
using Assets.Code.Skill.Events;
using Assets.Code.Skill.Queries;
using Assets.Code.Utils;
using Assets.Code.UI;
using Assets.Code.UI.Events;
using Assets.Code.Events;
using Assets.Code.Effect;
using Assets.Code.Token;
using Assets.Code.UI.Tooltips;
using Assets.Code.UI.Screens;
using Assets.Code.UI.Widgets;
using Assets.Code.UI.Managers;
using Assets.Code.Campaign;

using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        // v15.1: post-run results frontend. The original v15 Harmony hook on
        // GameOverScreenWidgetBhv.OnScreenOpenCompleted did not fire in the live
        // build, so detect the active native widget passively from Unity Update.
        // The widget itself remains the authoritative frontend identity.
        private GameOverScreenWidgetBhv _expeditionResultsScreen;
        private bool _expeditionResultsReadySent;
        private float _resultsNextDiscoveryAt;

        private GameOverScreenWidgetBhv GetStableExpeditionResultsScreen()
        {
            GameOverScreenWidgetBhv activeScreen = null;

            if (_expeditionResultsScreen != null &&
                _expeditionResultsScreen.gameObject != null &&
                _expeditionResultsScreen.gameObject.activeInHierarchy &&
                _expeditionResultsScreen.enabled)
            {
                activeScreen = _expeditionResultsScreen;
            }
            else
            {
                if (UnityEngine.Time.unscaledTime < _resultsNextDiscoveryAt)
                {
                    return null;
                }

                // Expedition results are rare.  When no cached screen exists,
                // discover at 2 Hz instead of doing a global Resources scan at
                // every frontend tick.
                _resultsNextDiscoveryAt = UnityEngine.Time.unscaledTime + 0.50f;

                GameOverScreenWidgetBhv[] screens =
                    UnityEngine.Resources.FindObjectsOfTypeAll<GameOverScreenWidgetBhv>();

                foreach (GameOverScreenWidgetBhv candidate in screens)
                {
                    if (candidate != null &&
                        candidate.gameObject != null &&
                        candidate.gameObject.activeInHierarchy &&
                        candidate.enabled)
                    {
                        activeScreen = candidate;
                        break;
                    }
                }
            }

            if (activeScreen == null)
            {
                if (_expeditionResultsScreen != null)
                {
                    _expeditionResultsScreen = null;
                    _expeditionResultsReadySent = false;
                }

                return null;
            }

            if (!ReferenceEquals(activeScreen, _expeditionResultsScreen))
            {
                _expeditionResultsScreen = activeScreen;
                _expeditionResultsReadySent = false;

                Logger.LogInfo(
                    $"READY boundary detected: expedition_results " +
                    $"id={activeScreen.GetInstanceID()} " +
                    $"path='{GetTransformPath(activeScreen.transform)}'");
            }

            ScreenStackBhv screenStack =
                SingletonMonoBehaviour<ScreenStackBhv>.Instance;

            if (screenStack == null)
            {
                return null;
            }

            if (screenStack.GetTopMostCloseableWithHotkeyScreen() != null)
            {
                return null;
            }

            return activeScreen;
        }

        private JObject BuildExpeditionResultsReadyMessage()
        {
            string outcome = "unknown";
            bool hasScore = false;

            try
            {
                ScoreInstance lastRunScore =
                    SingletonMonoBehaviour<CampaignBhv>.Instance.LastRunScore;

                if (lastRunScore != null)
                {
                    string nativeReason =
                        lastRunScore.GetGameOverReason().ToString();

                    if (!string.IsNullOrEmpty(nativeReason))
                    {
                        outcome = nativeReason.ToLowerInvariant();
                    }

                    hasScore = lastRunScore.HasAnyScore();
                }
            }
            catch (Exception ex)
            {
                // Outcome is useful presentation data, but failure to read it
                // must not destroy the decision boundary or Continue action.
                Logger.LogWarning(
                    $"RESULTS outcome read failed: {ex.Message}");
            }

            return new JObject
            {
                ["type"] = "ready",
                ["frontend_state"] = "expedition_results",
                ["outcome"] = outcome,
                ["has_score"] = hasScore,
                ["available_actions"] = new JArray
                {
                    new JObject
                    {
                        ["action"] = "continue"
                    }
                }
            };
        }

        private void ProcessExpeditionResultsReady()
        {
            if (GetStableExpeditionResultsScreen() == null)
            {
                return;
            }

            if (_expeditionResultsReadySent)
            {
                return;
            }

            _expeditionResultsReadySent = true;
            _hostReadyCatchupRequested = false;

            Logger.LogInfo("READY expedition_results");
            QueueOutgoingMessage(BuildExpeditionResultsReadyMessage());
        }

        private void HandleExpeditionResultsContinue(
            BridgeRequest request)
        {
            GameOverScreenWidgetBhv screen =
                GetStableExpeditionResultsScreen();

            if (screen == null)
            {
                request.Response =
                    "ERROR: expedition results are not at an interactive boundary";
                return;
            }

            FieldInfo uiScreenField =
                typeof(GameOverScreenWidgetBhv).GetField(
                    "m_uiScreenBhv",
                    BindingFlags.Instance | BindingFlags.NonPublic);

            UiScreenBhv uiScreen =
                uiScreenField == null
                    ? null
                    : uiScreenField.GetValue(screen) as UiScreenBhv;

            if (uiScreen == null)
            {
                request.Response =
                    "ERROR: expedition results screen controller is unavailable";
                return;
            }

            _expeditionResultsReadySent = true;
            _hostReadyCatchupRequested = false;

            Logger.LogInfo(
                "COMMAND consumes READY boundary: expedition_results continue");

            // Native GameOverScreenWidgetBhv confirmation path ultimately calls
            // this exact public screen operation. Avoid input/button emulation.
            uiScreen.TryCloseScreen();
            request.Response = "OK";
        }

        // v17: active native EnterNode screen. This is intentionally read from
        // UiScreenBhv.PushParams rather than inferred from the executing map
        // trigger. The localization key is stable machine-readable interaction
        // identity; no localized presentation text is parsed.
    }
}
