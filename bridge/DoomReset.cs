using Assets.Code.Data;
using Assets.Code.Game;
using Assets.Code.Run;
using Assets.Code.UI.Managers;
using Assets.Code.UI.Screens;
using Assets.Code.UI.Widgets;
using Assets.Code.Utils;

using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        private DoomResetScreenWidgetBhv _doomResetReadyWidget;
        private bool _doomResetReadySent;

        private sealed class DoomResetBoundary
        {
            public UiScreenBhv Screen;
            public DoomResetScreenWidgetBhv Widget;
            public DataContextBhv DataContext;
        }

        private DoomResetBoundary GetStableDoomResetBoundary()
        {
            if (GameModeMgr.CurrentMode != GameModeType.DRIVING)
            {
                ResetDoomResetBoundary();
                return null;
            }

            GameModeMgr mgr = GameModeMgr.Instance;
            if (mgr == null || mgr.IsChangingState())
            {
                ResetDoomResetBoundary();
                return null;
            }

            DoomResetBoundary cached =
                TryBuildStableDoomResetBoundary(_doomResetReadyWidget);

            if (cached != null)
            {
                return cached;
            }

            DoomResetScreenWidgetBhv[] widgets =
                UnityEngine.Resources
                    .FindObjectsOfTypeAll<DoomResetScreenWidgetBhv>();

            foreach (DoomResetScreenWidgetBhv widget in widgets)
            {
                DoomResetBoundary boundary =
                    TryBuildStableDoomResetBoundary(widget);

                if (boundary == null)
                {
                    continue;
                }

                if (!ReferenceEquals(widget, _doomResetReadyWidget))
                {
                    _doomResetReadyWidget = widget;
                    _doomResetReadySent = false;

                    Logger.LogInfo(
                        $"READY boundary detected: doom_reset " +
                        $"path='{GetTransformPath(widget.transform)}'");
                }

                return boundary;
            }

            ResetDoomResetBoundary();
            return null;
        }

        private DoomResetBoundary TryBuildStableDoomResetBoundary(
            DoomResetScreenWidgetBhv widget)
        {
            if (widget == null ||
                widget.gameObject == null ||
                !widget.gameObject.activeInHierarchy ||
                !widget.enabled)
            {
                return null;
            }

            UiScreenBhv screen =
                widget.GetComponentInParent<UiScreenBhv>();

            if (screen == null ||
                screen.gameObject == null ||
                !screen.gameObject.activeInHierarchy ||
                screen.ScreenState != UiScreenState.Open)
            {
                return null;
            }

            DataContextBhv dataContext =
                GetMemberValue(widget, "m_dataContextBhv") as DataContextBhv;

            if (dataContext == null)
            {
                return null;
            }

            return new DoomResetBoundary
            {
                Screen = screen,
                Widget = widget,
                DataContext = dataContext
            };
        }

        private void ResetDoomResetBoundary()
        {
            _doomResetReadyWidget = null;
            _doomResetReadySent = false;
        }

        private JObject BuildDoomResetReadyMessage(
            DoomResetBoundary boundary)
        {
            string bossDescription =
                boundary.DataContext.GetStringValue("boss_desc") ?? "";

            string bossId = "";
            int resetCount = 0;

            try
            {
                RunBhv run = SingletonMonoBehaviour<RunBhv>.Instance;
                if (run != null &&
                    run.RunManager != null)
                {
                    resetCount = run.RunManager.DoomResetCount;

                    if (run.RunManager.Boss != null)
                    {
                        bossId = run.RunManager.Boss.m_Id ?? "";
                    }
                }
            }
            catch
            {
                // The UI text above is authoritative for the boundary.  Boss id
                // and reset count are supplementary diagnostics only.
            }

            return new JObject
            {
                ["type"] = "ready",
                ["frontend_state"] = "doom_reset",
                ["boss_id"] = bossId,
                ["doom_reset_count"] = resetCount,
                ["boss_description"] = bossDescription,
                ["available_actions"] = new JArray
                {
                    new JObject
                    {
                        ["action"] = "continue",
                        ["consumes_boundary"] = true
                    }
                }
            };
        }

        private bool ProcessDoomResetReady()
        {
            DoomResetBoundary boundary = GetStableDoomResetBoundary();

            if (boundary == null)
            {
                return false;
            }

            if (_doomResetReadySent)
            {
                return true;
            }

            _doomResetReadySent = true;
            _hostReadyCatchupRequested = false;

            Logger.LogInfo(
                $"READY doom_reset: count=" +
                $"{BuildDoomResetReadyMessage(boundary)["doom_reset_count"]}");

            QueueOutgoingMessage(BuildDoomResetReadyMessage(boundary));
            return true;
        }

        private bool TryProcessDoomResetCatchup()
        {
            DoomResetBoundary boundary = GetStableDoomResetBoundary();

            if (boundary == null)
            {
                return false;
            }

            _doomResetReadySent = true;

            JObject ready = BuildDoomResetReadyMessage(boundary);

            Logger.LogInfo(
                $"READY catch-up: doom_reset count={ready["doom_reset_count"]}");

            QueueOutgoingMessage(ready);
            return true;
        }

        private bool TryHandleDoomResetContinue(BridgeRequest request)
        {
            DoomResetBoundary boundary = GetStableDoomResetBoundary();

            if (boundary == null)
            {
                return false;
            }

            Logger.LogInfo("COMMAND consumes READY boundary: doom_reset continue");

            _doomResetReadySent = true;
            _hostReadyCatchupRequested = false;

            CommonUiBhv commonUi =
                SingletonMonoBehaviour<CommonUiBhv>.Instance;

            if (commonUi == null)
            {
                request.Response =
                    "ERROR: CommonUiBhv is unavailable for doom_reset continue";
                return true;
            }

            commonUi.AttemptToCloseDoomResetScreen();
            request.Response = "OK";
            return true;
        }
    }
}
