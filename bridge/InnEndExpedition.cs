using System;
using System.Collections.Generic;

using Assets.Code.Game;
using Assets.Code.Inn;
using Assets.Code.Inn.Presentation;
using Assets.Code.Locale;
using Assets.Code.Run;
using Assets.Code.Score;
using Assets.Code.UI.Managers;
using Assets.Code.UI.Widgets;
using Assets.Code.Utils;

using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        private bool _innEndExpeditionReturnPending;

        private bool CanUseInnEndExpedition()
        {
            GameModeMgr modeMgr = GameModeMgr.Instance;

            if (modeMgr == null ||
                modeMgr.IsChangingState() ||
                GameModeMgr.CurrentMode != GameModeType.INN)
            {
                return false;
            }

            GameTypeMgr gameTypeMgr = Singleton<GameTypeMgr>.Instance;
            InnBhv inn = Singleton<InnBhv>.Instance;

            return
                gameTypeMgr != null &&
                gameTypeMgr.CurrentGameType == GameType.EXPEDITION &&
                inn != null &&
                inn.GetInnState() == InnState.WAIT_ON_PLAYER;
        }

        private bool GetInnForcedRunEnd()
        {
            try
            {
                InnPresentationBhv presentation =
                    SingletonMonoBehaviour<InnPresentationBhv>.Instance;

                return presentation != null && presentation.IsForcedRunEnd;
            }
            catch
            {
                return false;
            }
        }

        private ScoreInstance GetInnRunScore()
        {
            RunBhv run = SingletonMonoBehaviour<RunBhv>.Instance;
            if (run == null || run.RunScoreManager == null)
            {
                return null;
            }

            return run.RunScoreManager.GetGameScore();
        }

        private JObject BuildInnEndExpeditionStateMessage()
        {
            ScoreInstance gameScore = GetInnRunScore();
            if (gameScore == null)
            {
                return null;
            }

            // Faithfully mirror SubScreenInnEndRunBhv.Awake(): refresh the
            // current run-end values with penalty state enabled, then render
            // the visible list and total without applying the penalty modifier.
            gameScore.UpdateScores(
                isVictory: false,
                isPenalty: true);

            JArray rows = new JArray();

            foreach (ScoreType scoreType in CustomEnum<ScoreType>.GetInstances())
            {
                if (!gameScore.IsScoreTypeValid(
                        scoreType,
                        isVictory: false))
                {
                    continue;
                }

                int scoreModifier = gameScore.GetScoreModifier(
                    scoreType,
                    isVictory: false,
                    isPenalty: false);

                int combinedScore = gameScore.GetCombinedScore(scoreType);

                if (scoreType.GetIsVisible(combinedScore))
                {
                    string locKey =
                        "results_scoring_" + scoreType.GetName();

                    rows.Add(
                        BuildInnEndExpeditionScoreRow(
                            scoreType,
                            locKey,
                            combinedScore,
                            scoreModifier,
                            individualId: null));
                }

                List<IndividualScore> individualScores =
                    gameScore.GetIndividualScores(scoreType);

                foreach (IndividualScore individualScore in individualScores)
                {
                    string locKey =
                        "results_scoring_" +
                        scoreType.GetName() + "_" +
                        individualScore.m_IndividualId;

                    rows.Add(
                        BuildInnEndExpeditionScoreRow(
                            scoreType,
                            locKey,
                            gameScore.GetIndividualScore(individualScore),
                            scoreModifier,
                            individualScore.m_IndividualId));
                }
            }

            int totalScore = gameScore.GetTotalScore(
                isVictory: false,
                isPenalty: false);

            return new JObject
            {
                ["type"] = "inn_end_expedition_state",
                ["frontend_state"] = "inn_end_expedition",
                ["current_mode"] = "inn",
                ["forced"] = GetInnForcedRunEnd(),
                ["total_hope"] = totalScore,
                ["scores"] = rows,
                ["available_actions"] = new JArray
                {
                    new JObject
                    {
                        ["action"] = "take_hope",
                        ["consumes_boundary"] = true
                    },
                    new JObject
                    {
                        ["action"] = "back",
                        ["consumes_boundary"] = true
                    }
                }
            };
        }

        private JObject BuildInnEndExpeditionScoreRow(
            ScoreType scoreType,
            string locKey,
            int score,
            int scoreModifier,
            string individualId)
        {
            string label = string.Empty;

            try
            {
                label = Singleton<Localization>.Instance.GetString(locKey);
            }
            catch
            {
                label = string.Empty;
            }

            JObject row = new JObject
            {
                ["score_type"] = scoreType.GetName(),
                ["loc_key"] = locKey,
                ["label"] = label ?? string.Empty,
                ["score"] = score,
                ["score_modifier"] = scoreModifier,
                ["display_value"] = score + scoreModifier
            };

            if (!string.IsNullOrEmpty(individualId))
            {
                row["individual_id"] = individualId;
            }

            return row;
        }

        private void HandleInnEndExpeditionState(BridgeRequest request)
        {
            if (!CanUseInnEndExpedition())
            {
                request.Response =
                    "ERROR: end expedition is unavailable outside a stable expedition inn";
                return;
            }

            JObject state = BuildInnEndExpeditionStateMessage();
            if (state == null)
            {
                request.Response =
                    "ERROR: run-end score data is unavailable";
                return;
            }

            Logger.LogInfo(
                $"COMMAND inn: end_expedition total={state["total_hope"]} " +
                $"forced={state["forced"]}");

            QueueOutgoingMessage(state);
            request.Response = "OK";
        }

        private void HandleInnEndExpeditionTakeHope(BridgeRequest request)
        {
            if (!CanUseInnEndExpedition())
            {
                request.Response =
                    "ERROR: take_hope is unavailable outside a stable expedition inn";
                return;
            }

            RunBhv run = SingletonMonoBehaviour<RunBhv>.Instance;
            if (run == null || run.RunScoreManager == null)
            {
                request.Response =
                    "ERROR: run score manager is unavailable";
                return;
            }

            SetPendingConfirmationContext(
                ConfirmationContextInnEndExpedition);

            Action confirmBtnAction = delegate
            {
                run.RunScoreManager.SetGameOver(GameOverReason.CASH_OUT);
                run.RunScoreManager.TriggerGameOver();
            };

            Action declineBtnAction = delegate
            {
            };

            Logger.LogInfo(
                "COMMAND inn_end_expedition: take_hope -> native AbandonRun confirmation");

            try
            {
                SingletonMonoBehaviour<CommonUiBhv>.Instance
                    .ShowConfirmationDialog(
                        CommonUiBhv.ConfirmationDialogType.AbandonRun,
                        Singleton<Localization>.Instance.GetString(
                            "inn_end_run_screen_confirmation_dialog_title"),
                        Singleton<Localization>.Instance.GetString(
                            "inn_end_run_screen_confirmation_dialog_desc"),
                        confirmBtnAction,
                        Singleton<Localization>.Instance.GetString(
                            "inn_end_run_screen_confirmation_dialog_yes_label"),
                        declineBtnAction,
                        Singleton<Localization>.Instance.GetString(
                            "inn_end_run_screen_confirmation_dialog_no_label"));
            }
            catch
            {
                ClearPendingConfirmationContext();
                throw;
            }

            request.Response = "OK";
        }


        private void HandleInnEndExpeditionBack(BridgeRequest request)
        {
            if (!CanUseInnEndExpedition())
            {
                request.Response =
                    "ERROR: cannot leave end expedition summary outside a stable expedition inn";
                return;
            }

            _innEndExpeditionReturnPending = false;
            _innReadySent = true;
            _hostReadyCatchupRequested = false;

            Logger.LogInfo("COMMAND inn_end_expedition: back -> inn");
            QueueOutgoingMessage(BuildInnReadyMessage());
            request.Response = "OK";
        }

        private bool ProcessInnEndExpeditionReturnReady()
        {
            if (!_innEndExpeditionReturnPending)
            {
                return false;
            }

            if (!CanUseInnEndExpedition())
            {
                if (GameModeMgr.CurrentMode != GameModeType.INN)
                {
                    _innEndExpeditionReturnPending = false;
                }
                return false;
            }

            // Do not expose the underlying tab while the native confirmation
            // widget is still visibly closing.
            if (_confirmationChoiceInFlight != null &&
                _confirmationChoiceInFlight.gameObject != null &&
                _confirmationChoiceInFlight.gameObject.activeInHierarchy)
            {
                return false;
            }

            ConfirmationDialogBhv activeDialog;
            JObject activeReady;
            if (TryGetSupportedActiveConfirmation(
                    out activeDialog,
                    out activeReady))
            {
                return false;
            }

            JObject state = BuildInnEndExpeditionStateMessage();
            if (state == null)
            {
                return false;
            }

            _innEndExpeditionReturnPending = false;
            _innReadySent = true;
            _hostReadyCatchupRequested = false;

            Logger.LogInfo(
                "READY inn_end_expedition: returned after confirmation cancel");

            // This is a real decision-boundary restoration after cancel, unlike
            // the read-only query response used by the initial end_expedition command.
            state["type"] = "ready";
            QueueOutgoingMessage(state);
            return true;
        }
    }
}
