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
        // v18: confession/boss selection boundary. DD2 calls this node
        // BOSS_SELECT and exposes each legal choice as a SelectBossOptionBhv.
        // One semantic select_boss command performs the native option selection
        // and the native confirmation immediately afterward; there is no second
        // agent decision between those two UI submits.
        private BossSelectWidgetBhv _bossSelectReadyWidget;
        private bool _bossSelectReadySent;
        private float _bossSelectNextDiscoveryAt;

        private BossSelectWidgetBhv GetStableBossSelectWidget()
        {
            GameModeMgr modeMgr = GameModeMgr.Instance;
            if (modeMgr == null || modeMgr.IsChangingState())
            {
                ResetBossSelectReadyBoundary();
                return null;
            }

            if (_bossSelectReadyWidget != null &&
                _bossSelectReadyWidget.gameObject != null &&
                _bossSelectReadyWidget.gameObject.activeInHierarchy &&
                _bossSelectReadyWidget.enabled)
            {
                SelectBossOptionBhv[] cachedOptions =
                    _bossSelectReadyWidget.GetComponentsInChildren<SelectBossOptionBhv>();

                foreach (SelectBossOptionBhv option in cachedOptions)
                {
                    if (IsSelectableBossOption(option))
                    {
                        return _bossSelectReadyWidget;
                    }
                }
            }

            if (UnityEngine.Time.unscaledTime < _bossSelectNextDiscoveryAt)
            {
                return null;
            }

            _bossSelectNextDiscoveryAt = UnityEngine.Time.unscaledTime + 0.50f;

            BossSelectWidgetBhv[] widgets =
                UnityEngine.Resources.FindObjectsOfTypeAll<BossSelectWidgetBhv>();

            BossSelectWidgetBhv activeWidget = null;

            foreach (BossSelectWidgetBhv candidate in widgets)
            {
                if (candidate == null ||
                    candidate.gameObject == null ||
                    !candidate.gameObject.activeInHierarchy ||
                    !candidate.enabled)
                {
                    continue;
                }

                SelectBossOptionBhv[] options =
                    candidate.GetComponentsInChildren<SelectBossOptionBhv>();

                bool hasSelectableOption = false;
                foreach (SelectBossOptionBhv option in options)
                {
                    if (IsSelectableBossOption(option))
                    {
                        hasSelectableOption = true;
                        break;
                    }
                }

                if (hasSelectableOption)
                {
                    activeWidget = candidate;
                    break;
                }
            }

            if (activeWidget == null)
            {
                ResetBossSelectReadyBoundary();
                return null;
            }

            if (!ReferenceEquals(activeWidget, _bossSelectReadyWidget))
            {
                _bossSelectReadyWidget = activeWidget;
                _bossSelectReadySent = false;

                Logger.LogInfo(
                    $"READY boundary detected: boss_select " +
                    $"path='{GetTransformPath(activeWidget.transform)}'");
            }

            return activeWidget;
        }

        private static bool IsSelectableBossOption(SelectBossOptionBhv option)
        {
            if (option == null ||
                option.gameObject == null ||
                !option.gameObject.activeInHierarchy ||
                !option.enabled ||
                option.GetBossDefinition() == null)
            {
                return false;
            }

            // SelectBossOptionBhv keeps the authoritative selectable state on its
            // private serialized Button. UnityEngine.UI is not referenced by this
            // bridge project, so read that one bool reflectively instead of adding
            // another Unity module dependency or guessing the object hierarchy.
            FieldInfo buttonField =
                typeof(SelectBossOptionBhv).GetField(
                    "m_buttonComponent",
                    BindingFlags.Instance | BindingFlags.NonPublic);

            object button =
                buttonField == null
                    ? null
                    : buttonField.GetValue(option);

            if (button == null)
            {
                return false;
            }

            PropertyInfo interactableProperty =
                button.GetType().GetProperty(
                    "interactable",
                    BindingFlags.Instance | BindingFlags.Public);

            if (interactableProperty == null ||
                interactableProperty.PropertyType != typeof(bool))
            {
                return false;
            }

            return (bool)interactableProperty.GetValue(button, null);
        }

        private void ResetBossSelectReadyBoundary()
        {
            _bossSelectReadyWidget = null;
            _bossSelectReadySent = false;
        }

        private JObject BuildBossSelectReadyMessage(
            BossSelectWidgetBhv widget)
        {
            JArray actions = new JArray();

            SelectBossOptionBhv[] options =
                widget.GetComponentsInChildren<SelectBossOptionBhv>();

            foreach (SelectBossOptionBhv option in options)
            {
                if (!IsSelectableBossOption(option))
                {
                    continue;
                }

                var bossDefinition = option.GetBossDefinition();
                if (bossDefinition == null ||
                    string.IsNullOrEmpty(bossDefinition.m_Id))
                {
                    continue;
                }

                actions.Add(
                    new JObject
                    {
                        ["action"] = "select_boss",
                        ["boss_id"] = bossDefinition.m_Id
                    });
            }

            return new JObject
            {
                ["type"] = "ready",
                ["frontend_state"] = "boss_select",
                ["available_actions"] = actions
            };
        }

        private void ProcessBossSelectReady()
        {
            BossSelectWidgetBhv widget = GetStableBossSelectWidget();

            if (widget == null || _bossSelectReadySent)
            {
                return;
            }

            JObject ready = BuildBossSelectReadyMessage(widget);
            JArray actions = ready["available_actions"] as JArray;

            if (actions == null || actions.Count == 0)
            {
                return;
            }

            _bossSelectReadySent = true;
            _hostReadyCatchupRequested = false;

            Logger.LogInfo(
                $"READY boss_select: choices={actions.Count}");
            QueueOutgoingMessage(ready);
        }

        private void HandleBossSelect(
            BridgeRequest request,
            string bossId)
        {
            BossSelectWidgetBhv widget = GetStableBossSelectWidget();

            if (widget == null)
            {
                request.Response =
                    "ERROR: boss select is not at an interactive boundary";
                return;
            }

            if (string.IsNullOrEmpty(bossId))
            {
                request.Response = "ERROR: boss_id is required";
                return;
            }

            SelectBossOptionBhv selectedOption = null;
            SelectBossOptionBhv[] options =
                widget.GetComponentsInChildren<SelectBossOptionBhv>();

            foreach (SelectBossOptionBhv option in options)
            {
                if (!IsSelectableBossOption(option))
                {
                    continue;
                }

                var bossDefinition = option.GetBossDefinition();
                if (bossDefinition != null &&
                    string.Equals(
                        bossDefinition.m_Id,
                        bossId,
                        StringComparison.Ordinal))
                {
                    selectedOption = option;
                    break;
                }
            }

            if (selectedOption == null)
            {
                request.Response =
                    $"ERROR: boss '{bossId}' is not currently selectable";
                return;
            }

            _bossSelectReadySent = true;
            _hostReadyCatchupRequested = false;

            Logger.LogInfo(
                $"COMMAND consumes READY boundary: boss_select " +
                $"select_boss boss_id='{bossId}'");

            // Preserve both native UI submits. OnSelected() updates the option
            // and parent widget exactly like the first Submit; OnConfirm() then
            // fires EventSelectBoss, narration, signature animation and closes
            // the screen when the native timeline finishes.
            selectedOption.OnSelected();
            widget.OnConfirm();
            request.Response = "OK";
        }

        // v19: HERO_SELECT / Crossroads. This first pass exposes the roster and a
        // read-only inspect_hero operation. inspect_hero deliberately does not open
        // Character Sheet UI and therefore does not trigger viewed/notified side effects.
    }
}
