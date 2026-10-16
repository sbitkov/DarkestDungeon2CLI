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
        // v17: temporary Altar of Hope root boundary. This deliberately exposes
        // only the native OnEmbark() path for now; altar progression/subscreens
        // will be modeled separately rather than guessed from presentation.
        private AltarOfHopeUiBhv _altarReadyUi;
        private bool _altarReadySent;

        private AltarOfHopeUiBhv GetStableAltarOfHopeUi()
        {
            if (GameModeMgr.CurrentMode != GameModeType.ALTAR_OF_HOPE ||
                GameModeMgr.IsLoadingGameMode)
            {
                ResetAltarOfHopeReadyBoundary();
                return null;
            }

            GameModeMgr modeMgr = GameModeMgr.Instance;
            if (modeMgr == null || modeMgr.IsChangingState())
            {
                ResetAltarOfHopeReadyBoundary();
                return null;
            }

            ScreenStackBhv screenStack =
                SingletonMonoBehaviour<ScreenStackBhv>.Instance;

            if (screenStack != null &&
                screenStack.GetTopMostCloseableWithHotkeyScreen() != null)
            {
                ResetAltarOfHopeReadyBoundary();
                return null;
            }

            if (_altarReadyUi != null &&
                _altarReadyUi.gameObject != null &&
                _altarReadyUi.gameObject.activeInHierarchy &&
                _altarReadyUi.enabled)
            {
                return _altarReadyUi;
            }

            AltarOfHopeUiBhv[] candidates =
                UnityEngine.Resources.FindObjectsOfTypeAll<AltarOfHopeUiBhv>();

            AltarOfHopeUiBhv activeUi = null;

            foreach (AltarOfHopeUiBhv candidate in candidates)
            {
                if (candidate != null &&
                    candidate.gameObject != null &&
                    candidate.gameObject.activeInHierarchy &&
                    candidate.enabled)
                {
                    activeUi = candidate;
                    break;
                }
            }

            if (activeUi == null)
            {
                ResetAltarOfHopeReadyBoundary();
                return null;
            }

            if (!ReferenceEquals(activeUi, _altarReadyUi))
            {
                _altarReadyUi = activeUi;
                _altarReadySent = false;

                Logger.LogInfo(
                    $"READY boundary detected: altar_of_hope " +
                    $"path='{GetTransformPath(activeUi.transform)}'");
            }

            return activeUi;
        }

        private void ResetAltarOfHopeReadyBoundary()
        {
            _altarReadyUi = null;
            _altarReadySent = false;
        }

        private JObject BuildAltarOfHopeReadyMessage()
        {
            return new JObject
            {
                ["type"] = "ready",
                ["frontend_state"] = "altar_of_hope",
                ["available_actions"] = new JArray
                {
                    new JObject
                    {
                        ["action"] = "leave_altar"
                    }
                }
            };
        }

        private void ProcessAltarOfHopeReady()
        {
            if (GetStableAltarOfHopeUi() == null || _altarReadySent)
            {
                return;
            }

            _altarReadySent = true;
            _hostReadyCatchupRequested = false;

            Logger.LogInfo("READY altar_of_hope");
            QueueOutgoingMessage(BuildAltarOfHopeReadyMessage());
        }

        private void HandleAltarOfHopeLeave(BridgeRequest request)
        {
            AltarOfHopeUiBhv ui = GetStableAltarOfHopeUi();

            if (ui == null)
            {
                request.Response =
                    "ERROR: altar of hope is not at its root interactive boundary";
                return;
            }

            _altarReadySent = true;
            _hostReadyCatchupRequested = false;

            Logger.LogInfo(
                "COMMAND consumes READY boundary: altar_of_hope leave_altar");

            // Preserve the game's full path: close active subscreen, honor intro
            // confirmation rules, then ExitAltar() performs mode change, audio,
            // narration and soft-save when DD2 allows the departure.
            ui.OnEmbark();
            request.Response = "OK";
        }
    }
}
