using Assets.Code.Game;
using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        private bool _innReadySent;

        private JObject BuildInnReadyMessage()
        {
            return new JObject
            {
                ["type"] = "ready",
                ["frontend_state"] = "inn",
                ["implemented"] = false,
                ["available_actions"] =
                    new JArray
                    {
                        new JObject
                        {
                            ["action"] = "travelogue"
                        },
                        new JObject
                        {
                            ["action"] = "end_expedition"
                        },
                        new JObject
                        {
                            ["action"] = "store"
                        },
                        new JObject
                        {
                            ["action"] = "mastery"
                        },
                        new JObject
                        {
                            ["action"] = "wainwright"
                        },
                        new JObject
                        {
                            ["action"] = "route"
                        },
                        new JObject
                        {
                            ["action"] = "inn_items"
                        }
                    }
            };
        }

        private bool IsStableInn()
        {
            GameModeMgr mgr = GameModeMgr.Instance;

            return
                mgr != null &&
                !mgr.IsChangingState() &&
                GameModeMgr.CurrentMode == GameModeType.INN;
        }

        private void ProcessInnReady()
        {
            if (!IsStableInn())
            {
                _innReadySent = false;
                return;
            }

            if (_innReadySent)
            {
                return;
            }

            _innReadySent = true;
            _hostReadyCatchupRequested = false;

            Logger.LogInfo("READY inn");

            QueueOutgoingMessage(
                BuildInnReadyMessage());
        }

        private bool TryProcessInnCatchup()
        {
            if (!IsStableInn())
            {
                return false;
            }

            _innReadySent = true;

            Logger.LogInfo("READY catch-up: inn");

            QueueOutgoingMessage(
                BuildInnReadyMessage());

            return true;
        }
    }
}