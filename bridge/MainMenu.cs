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
        // v14: first non-combat frontend boundary. A stable interactive main
        // menu gets one READY per appearance/connection, just like combat
        // IN_TURN_SELECT.
        private bool _mainMenuReadySent;

        private MainMenuUiScreenBhv GetStableMainMenuScreen()
        {
            if (GameModeMgr.CurrentMode != GameModeType.MAIN_MENU)
            {
                return null;
            }

            GameModeMgr mgr = GameModeMgr.Instance;
            if (mgr == null || mgr.IsChangingState())
            {
                return null;
            }

            MainMenuUiScreenBhv screen =
                UnityEngine.Object.FindObjectOfType<MainMenuUiScreenBhv>();

            if (screen == null ||
                !screen.gameObject.activeInHierarchy ||
                !screen.HasDisclaimerShown ||
                screen.IsPlayingOpenTimeline ||
                screen.IsLoadingGameOrCinematic)
            {
                return null;
            }

            return screen;
        }

        private JObject BuildMainMenuReadyMessage()
        {
            JArray actions = new JArray();

            // For the first frontend milestone expose the expedition entry
            // points that are already public native UI handlers. A continue
            // action is only advertised when DD2 itself reports a usable save.
            if (MainMenuUiScreenBhv.HasGameToContinue())
            {
                actions.Add(
                    new JObject
                    {
                        ["action"] = "continue_expedition"
                    });
            }

            actions.Add(
                new JObject
                {
                    ["action"] = "new_expedition"
                });

            return new JObject
            {
                ["type"] = "ready",
                ["frontend_state"] = "main_menu",
                ["available_actions"] = actions
            };
        }

        private void ProcessMainMenuReady()
        {
            MainMenuUiScreenBhv screen = GetStableMainMenuScreen();

            if (screen == null)
            {
                _mainMenuReadySent = false;
                return;
            }

            if (_mainMenuReadySent)
            {
                return;
            }

            _mainMenuReadySent = true;
            _hostReadyCatchupRequested = false;

            Logger.LogInfo("READY main_menu");
            QueueOutgoingMessage(BuildMainMenuReadyMessage());
        }

        private void HandleMainMenuAction(
            BridgeRequest request,
            string action)
        {
            MainMenuUiScreenBhv screen = GetStableMainMenuScreen();

            if (screen == null)
            {
                request.Response =
                    "ERROR: main menu is not at an interactive boundary";
                return;
            }

            if (action == "continue_expedition")
            {
                if (!MainMenuUiScreenBhv.HasGameToContinue())
                {
                    request.Response =
                        "ERROR: no expedition is available to continue";
                    return;
                }

                _mainMenuReadySent = true;
                _hostReadyCatchupRequested = false;
                Logger.LogInfo("COMMAND consumes READY boundary: main_menu continue_expedition");
                screen.OnContinueGameClick();
                request.Response = "OK";
                return;
            }

            if (action == "new_expedition")
            {
                _mainMenuReadySent = true;
                _hostReadyCatchupRequested = false;
                Logger.LogInfo("COMMAND consumes READY boundary: main_menu new_expedition");
                screen.OnNewGameClick();
                request.Response = "OK";
                return;
            }

            request.Response = "ERROR: unsupported main-menu action";
        }

        // [NEW: READY SNAPSHOT ON UNITY MAIN THREAD]
    }
}
