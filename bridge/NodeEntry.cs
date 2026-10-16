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
        // v17: generic enter-node boundary. The active UiScreenBhv owns the
        // authoritative EnterNodeScreenPushParams, while the native widget owns
        // the exact player-equivalent OnInteractButton() action.
        private UiScreenBhv _nodeEntryReadyScreen;
        private EnterNodeScreenWidgetBhv _nodeEntryReadyWidget;
        private bool _nodeEntryReadySent;

        private bool TryGetStableNodeEntry(
            out UiScreenBhv screen,
            out EnterNodeScreenWidgetBhv widget,
            out EnterNodeScreenPushParams pushParams)
        {
            screen = null;
            widget = null;
            pushParams = null;

            CommonUiBhv commonUi = SingletonMonoBehaviour<CommonUiBhv>.Instance;
            GameModeMgr modeMgr = GameModeMgr.Instance;

            if (commonUi == null ||
                !commonUi.IsEnterNodeScreenActive ||
                modeMgr == null ||
                modeMgr.IsChangingState())
            {
                ResetNodeEntryReadyBoundary();
                return false;
            }

            if (_nodeEntryReadyScreen != null &&
                _nodeEntryReadyWidget != null &&
                _nodeEntryReadyScreen.gameObject != null &&
                _nodeEntryReadyScreen.gameObject.activeInHierarchy &&
                _nodeEntryReadyScreen.enabled &&
                _nodeEntryReadyWidget.gameObject != null &&
                _nodeEntryReadyWidget.gameObject.activeInHierarchy &&
                _nodeEntryReadyWidget.enabled &&
                IsNativeEnterNodeButtonInteractable(_nodeEntryReadyWidget))
            {
                EnterNodeScreenPushParams cachedParams =
                    _nodeEntryReadyScreen.PushParams as EnterNodeScreenPushParams;

                if (cachedParams != null)
                {
                    screen = _nodeEntryReadyScreen;
                    widget = _nodeEntryReadyWidget;
                    pushParams = cachedParams;
                    return true;
                }
            }

            UiScreenBhv[] screens =
                UnityEngine.Resources.FindObjectsOfTypeAll<UiScreenBhv>();

            foreach (UiScreenBhv candidate in screens)
            {
                if (candidate == null ||
                    candidate.gameObject == null ||
                    !candidate.gameObject.activeInHierarchy ||
                    !candidate.enabled)
                {
                    continue;
                }

                EnterNodeScreenPushParams candidateParams =
                    candidate.PushParams as EnterNodeScreenPushParams;

                if (candidateParams != null)
                {
                    screen = candidate;
                    pushParams = candidateParams;
                    break;
                }
            }

            if (screen == null || pushParams == null)
            {
                ResetNodeEntryReadyBoundary();
                return false;
            }

            EnterNodeScreenWidgetBhv[] widgets =
                UnityEngine.Resources.FindObjectsOfTypeAll<EnterNodeScreenWidgetBhv>();

            foreach (EnterNodeScreenWidgetBhv candidate in widgets)
            {
                if (candidate == null ||
                    candidate.gameObject == null ||
                    !candidate.gameObject.activeInHierarchy ||
                    !candidate.enabled)
                {
                    continue;
                }

                if (IsNativeEnterNodeButtonInteractable(candidate))
                {
                    widget = candidate;
                    break;
                }
            }

            if (widget == null)
            {
                // OnScreenOpenStart explicitly enables the native button. Waiting
                // for that bit prevents a READY one frame before the UI is ready.
                return false;
            }

            if (!ReferenceEquals(screen, _nodeEntryReadyScreen))
            {
                _nodeEntryReadyScreen = screen;
                _nodeEntryReadyWidget = widget;
                _nodeEntryReadySent = false;

                Logger.LogInfo(
                    $"READY boundary detected: node_entry " +
                    $"key='{pushParams.m_buttonString}' " +
                    $"path='{GetTransformPath(screen.transform)}'");
            }
            else
            {
                _nodeEntryReadyWidget = widget;
            }

            return true;
        }

        private static bool IsNativeEnterNodeButtonInteractable(
            EnterNodeScreenWidgetBhv widget)
        {
            if (widget == null)
            {
                return false;
            }

            FieldInfo field = typeof(EnterNodeScreenWidgetBhv).GetField(
                "m_interactBtn",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (field == null)
            {
                return false;
            }

            object button = field.GetValue(widget);
            if (button == null)
            {
                return false;
            }

            PropertyInfo property = button.GetType().GetProperty(
                "interactable",
                BindingFlags.Instance | BindingFlags.Public);

            if (property == null || property.PropertyType != typeof(bool))
            {
                return false;
            }

            return (bool)property.GetValue(button, null);
        }

        private void ResetNodeEntryReadyBoundary()
        {
            _nodeEntryReadyScreen = null;
            _nodeEntryReadyWidget = null;
            _nodeEntryReadySent = false;
        }

        private static string NodeTypeFromButtonKey(string buttonKey)
        {
            const string prefix = "node_interaction_";
            const string suffix = "_label";

            if (!string.IsNullOrEmpty(buttonKey) &&
                buttonKey.StartsWith(prefix, StringComparison.Ordinal) &&
                buttonKey.EndsWith(suffix, StringComparison.Ordinal) &&
                buttonKey.Length > prefix.Length + suffix.Length)
            {
                return buttonKey.Substring(
                    prefix.Length,
                    buttonKey.Length - prefix.Length - suffix.Length);
            }

            return string.IsNullOrEmpty(buttonKey)
                ? "unknown"
                : buttonKey;
        }

        private JObject BuildNodeEntryReadyMessage(
            EnterNodeScreenPushParams pushParams)
        {
            JObject management = BuildCurrentPartyManagementPayload();

            JArray actions =
                management["available_actions"] as JArray ?? new JArray();

            actions.Add(
                new JObject
                {
                    ["action"] = "enter_node",
                    ["consumes_boundary"] = true
                });

            actions.Add(
                new JObject
                {
                    ["action"] = "map"
                });

            return new JObject
            {
                ["type"] = "ready",
                ["frontend_state"] = "node_entry",
                ["node_type"] = NodeTypeFromButtonKey(pushParams.m_buttonString),
                ["button_key"] = pushParams.m_buttonString ?? "",
                ["has_candle_reward"] = pushParams.m_hasLootCandle,
                ["party"] = management["party"],
                ["available_heroes"] = management["available_heroes"],
                ["available_actions"] = actions
            };
        }

        private void ProcessNodeEntryReady()
        {
            UiScreenBhv screen;
            EnterNodeScreenWidgetBhv widget;
            EnterNodeScreenPushParams pushParams;

            if (!TryGetStableNodeEntry(out screen, out widget, out pushParams) ||
                _nodeEntryReadySent)
            {
                return;
            }

            _nodeEntryReadySent = true;
            _hostReadyCatchupRequested = false;

            Logger.LogInfo(
                $"READY node_entry: {NodeTypeFromButtonKey(pushParams.m_buttonString)}");
            QueueOutgoingMessage(BuildNodeEntryReadyMessage(pushParams));
        }

        private void HandleNodeEntryEnter(BridgeRequest request)
        {
            UiScreenBhv screen;
            EnterNodeScreenWidgetBhv widget;
            EnterNodeScreenPushParams pushParams;

            if (!TryGetStableNodeEntry(out screen, out widget, out pushParams))
            {
                request.Response =
                    "ERROR: enter-node screen is not at an interactive boundary";
                return;
            }

            _nodeEntryReadySent = true;
            _hostReadyCatchupRequested = false;

            Logger.LogInfo(
                $"COMMAND consumes READY boundary: node_entry " +
                $"{NodeTypeFromButtonKey(pushParams.m_buttonString)} enter_node");

            // Exact native player path: disables the button, closes the screen,
            // invokes its original trigger callback, then plays NodeClickToEnter.
            widget.OnInteractButton();
            request.Response = "OK";
        }

        // v17: stable Altar root. A top-most closeable screen means a modal or
        // altar subscreen currently owns input; do not leak the root leave action.
    }
}
