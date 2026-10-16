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
        // Shared native confirmation-dialog subsystem.
        //
        // IMPORTANT:
        // A semantic confirmation boundary is NOT identified by one particular
        // ConfirmationDialogBhv instance. DD2 may swap/pool widget instances while
        // visually presenting one uninterrupted modal. Therefore READY de-duplication
        // is latched by semantic key (frontend_state + reason) until that semantic
        // confirmation is genuinely absent from the active UI.
        private ConfirmationDialogBhv _activeConfirmationDialog;
        private bool _confirmationReadySent;
        private string _activeConfirmationSemanticKey = string.Empty;

        // After choosing confirm/cancel, keep the exact native widget suppressed
        // while TryCloseScreen() finishes. This is an additional close-race guard;
        // the primary READY identity is still the semantic key above.
        private ConfirmationDialogBhv _confirmationChoiceInFlight;

        private string _pendingConfirmationContext = string.Empty;

        private const string ConfirmationContextHeroSelectIncompleteSkills =
            "hero_select_incomplete_skill_loadout";

        private const string ConfirmationContextInnEndExpedition =
            "inn_end_expedition_take_hope";

        private void SetPendingConfirmationContext(string context)
        {
            _pendingConfirmationContext = context ?? string.Empty;
        }

        private void ClearPendingConfirmationContext()
        {
            _pendingConfirmationContext = string.Empty;
        }

        private bool TryClassifyConfirmation(
            ConfirmationDialogBhv dialog,
            out string frontendState,
            out string reason)
        {
            frontendState = null;
            reason = null;

            if (dialog == null)
            {
                return false;
            }

            switch (dialog.DialogType)
            {
                case CommonUiBhv.ConfirmationDialogType.AbandonRunMainMenu:
                    frontendState = "new_expedition_confirmation";
                    reason = "abandon_existing_expedition";
                    return true;

                case CommonUiBhv.ConfirmationDialogType.AbandonRun:
                    if (string.Equals(
                        _pendingConfirmationContext,
                        ConfirmationContextInnEndExpedition,
                        StringComparison.Ordinal))
                    {
                        frontendState = "end_expedition_confirmation";
                        reason = "take_hope_and_end_expedition";
                        return true;
                    }

                    return false;

                case CommonUiBhv.ConfirmationDialogType.StagecoachUnequippable:
                    if (string.Equals(
                        _pendingConfirmationContext,
                        ConfirmationContextInnWainwrightPermanentEquip,
                        StringComparison.Ordinal))
                    {
                        frontendState = "stagecoach_equip_confirmation";
                        reason = "permanent_stagecoach_item";
                        return true;
                    }

                    return false;

                case CommonUiBhv.ConfirmationDialogType.HotkeyCloseable:
                    if (string.Equals(
                        _pendingConfirmationContext,
                        ConfirmationContextHeroSelectIncompleteSkills,
                        StringComparison.Ordinal))
                    {
                        frontendState = "hero_select_confirmation";
                        reason = "incomplete_skill_loadout";
                        return true;
                    }

                    if (_postCombatLootLeaveConfirmationPending)
                    {
                        frontendState = "post_combat_loot_confirmation";
                        reason = "leave_items";
                        return true;
                    }

                    return false;

                default:
                    return false;
            }
        }

        private static string BuildConfirmationSemanticKey(
            string frontendState,
            string reason)
        {
            return (frontendState ?? string.Empty) +
                   "|" +
                   (reason ?? string.Empty);
        }

        private ConfirmationDialogBhv FindActiveConfirmationDialog()
        {
            // A consumed native dialog can remain alive for several Unity frames.
            // Do not let that exact object become a new semantic boundary while
            // its close lifecycle is still running.
            if (_confirmationChoiceInFlight != null)
            {
                if (_confirmationChoiceInFlight.gameObject != null &&
                    _confirmationChoiceInFlight.gameObject.activeInHierarchy)
                {
                    return null;
                }

                Logger.LogInfo(
                    "CONFIRMATION native close completed");

                _confirmationChoiceInFlight = null;
                _activeConfirmationDialog = null;
                // Do NOT clear _activeConfirmationSemanticKey here. It is cleared
                // only after the scan proves that the semantic modal is absent.
            }

            if (_activeConfirmationDialog != null &&
                _activeConfirmationDialog.gameObject != null &&
                _activeConfirmationDialog.gameObject.activeInHierarchy)
            {
                string cachedFrontendState;
                string cachedReason;

                if (TryClassifyConfirmation(
                        _activeConfirmationDialog,
                        out cachedFrontendState,
                        out cachedReason))
                {
                    string cachedKey =
                        BuildConfirmationSemanticKey(
                            cachedFrontendState,
                            cachedReason);

                    if (string.Equals(
                            cachedKey,
                            _activeConfirmationSemanticKey,
                            StringComparison.Ordinal))
                    {
                        return _activeConfirmationDialog;
                    }
                }
            }

            ConfirmationDialogBhv[] dialogs =
                UnityEngine.Resources.FindObjectsOfTypeAll<ConfirmationDialogBhv>();

            ConfirmationDialogBhv firstSupported = null;
            string firstFrontendState = null;
            string firstReason = null;
            string firstKey = null;

            // Prefer an active widget representing the already-latched semantic
            // boundary. This survives DD2 swapping/pooling ConfirmationDialogBhv
            // instances during one visual modal.
            foreach (ConfirmationDialogBhv dialog in dialogs)
            {
                if (dialog == null ||
                    dialog.gameObject == null ||
                    !dialog.gameObject.activeInHierarchy)
                {
                    continue;
                }

                string frontendState;
                string reason;

                if (!TryClassifyConfirmation(
                        dialog,
                        out frontendState,
                        out reason))
                {
                    continue;
                }

                string key =
                    BuildConfirmationSemanticKey(
                        frontendState,
                        reason);

                if (firstSupported == null)
                {
                    firstSupported = dialog;
                    firstFrontendState = frontendState;
                    firstReason = reason;
                    firstKey = key;
                }

                if (!string.IsNullOrEmpty(_activeConfirmationSemanticKey) &&
                    string.Equals(
                        key,
                        _activeConfirmationSemanticKey,
                        StringComparison.Ordinal))
                {
                    _activeConfirmationDialog = dialog;
                    return dialog;
                }
            }

            if (firstSupported == null)
            {
                // The semantic confirmation is genuinely gone. Only now may the
                // same semantic key be announced again in the future.
                if (!string.IsNullOrEmpty(_activeConfirmationSemanticKey))
                {
                    Logger.LogInfo(
                        $"CONFIRMATION boundary ended: " +
                        $"key='{_activeConfirmationSemanticKey}'");
                }

                _activeConfirmationDialog = null;
                _activeConfirmationSemanticKey = string.Empty;
                _confirmationReadySent = false;
                return null;
            }

            _activeConfirmationDialog = firstSupported;

            // A different semantic key means a genuinely different decision
            // boundary. Instance identity alone never resets READY.
            if (!string.Equals(
                    firstKey,
                    _activeConfirmationSemanticKey,
                    StringComparison.Ordinal))
            {
                _activeConfirmationSemanticKey = firstKey;
                _confirmationReadySent = false;

                Logger.LogInfo(
                    $"CONFIRMATION boundary detected: " +
                    $"type={firstSupported.DialogType}, " +
                    $"state='{firstFrontendState}', " +
                    $"reason='{firstReason}', " +
                    $"key='{firstKey}', " +
                    $"path='{GetTransformPath(firstSupported.transform)}'");
            }

            return firstSupported;
        }

        private bool TryBuildConfirmationReadyMessage(
            ConfirmationDialogBhv dialog,
            out JObject ready)
        {
            ready = null;

            string frontendState;
            string reason;

            if (!TryClassifyConfirmation(
                    dialog,
                    out frontendState,
                    out reason))
            {
                return false;
            }

            ready = new JObject
            {
                ["type"] = "ready",
                ["frontend_state"] = frontendState,
                ["reason"] = reason,
                ["dialog_type"] = dialog.DialogType.ToString(),
                ["available_actions"] = new JArray
                {
                    new JObject
                    {
                        ["action"] = "confirm",
                        ["consumes_boundary"] = true
                    },
                    new JObject
                    {
                        ["action"] = "cancel",
                        ["consumes_boundary"] = true
                    }
                }
            };

            return true;
        }

        private bool TryGetSupportedActiveConfirmation(
            out ConfirmationDialogBhv dialog,
            out JObject ready)
        {
            dialog = FindActiveConfirmationDialog();

            if (dialog == null)
            {
                ready = null;
                return false;
            }

            return TryBuildConfirmationReadyMessage(
                dialog,
                out ready);
        }

        private void ResetConfirmationBoundary()
        {
            _activeConfirmationDialog = null;
            _confirmationChoiceInFlight = null;
            _activeConfirmationSemanticKey = string.Empty;
            _confirmationReadySent = false;
            ClearPendingConfirmationContext();
            _postCombatLootLeaveConfirmationPending = false;
        }

        private void ProcessConfirmationReady()
        {
            ConfirmationDialogBhv dialog;
            JObject ready;

            if (!TryGetSupportedActiveConfirmation(
                    out dialog,
                    out ready))
            {
                return;
            }

            if (_confirmationReadySent)
            {
                return;
            }

            _confirmationReadySent = true;
            _hostReadyCatchupRequested = false;

            string frontendState =
                ready["frontend_state"]?.Value<string>() ?? "";

            if (frontendState == "hero_select_confirmation")
            {
                _heroSelectReadySent = true;
            }
            else if (frontendState == "new_expedition_confirmation")
            {
                _mainMenuReadySent = true;
            }
            else if (frontendState == "post_combat_loot_confirmation")
            {
                _postCombatLootReadySent = true;
            }
            else if (frontendState == "end_expedition_confirmation")
            {
                _innReadySent = true;
            }
            else if (frontendState == "stagecoach_equip_confirmation")
            {
                _innReadySent = true;
            }

            Logger.LogInfo(
                $"READY {frontendState}: {ready["reason"]} " +
                $"key='{_activeConfirmationSemanticKey}'");

            QueueOutgoingMessage(ready);
        }

        private void HandleConfirmationChoice(
            BridgeRequest request,
            bool confirm)
        {
            ConfirmationDialogBhv dialog;
            JObject ready;

            if (!TryGetSupportedActiveConfirmation(
                    out dialog,
                    out ready))
            {
                request.Response =
                    "ERROR: no supported confirmation dialog is active";
                return;
            }

            string frontendState =
                ready["frontend_state"]?.Value<string>() ?? "";
            string dialogType = dialog.DialogType.ToString();

            Logger.LogInfo(
                $"COMMAND {frontendState}: " +
                $"{(confirm ? "confirm" : "cancel")} " +
                $"dialog_type={dialogType} " +
                $"key='{_activeConfirmationSemanticKey}'");

            if (confirm)
            {
                dialog.OnConfirmPressed();
            }
            else
            {
                dialog.OnDeclinePressed();
            }

            // Keep the semantic READY latch intact until the UI scan confirms
            // that this semantic modal is really gone. Clearing it here would
            // re-arm the same visual decision during native screen teardown.
            _confirmationChoiceInFlight = dialog;
            _activeConfirmationDialog = dialog;
            _confirmationReadySent = true;
            ClearPendingConfirmationContext();

            if (frontendState == "post_combat_loot_confirmation")
            {
                _postCombatLootLeaveConfirmationPending = false;
            }

            if (frontendState == "stagecoach_equip_confirmation" &&
                GameModeMgr.CurrentMode == GameModeType.INN)
            {
                // Both confirm and cancel return to the same Wainwright decision
                // surface once the native permanent-item warning has closed.
                _innWainwrightReturnPending = true;
            }

            if (!confirm)
            {
                if (frontendState == "hero_select_confirmation" &&
                    GameModeMgr.CurrentMode == GameModeType.HERO_SELECT)
                {
                    _heroSelectReadySent = false;
                }
                else if (frontendState == "new_expedition_confirmation" &&
                         GameModeMgr.CurrentMode == GameModeType.MAIN_MENU)
                {
                    _mainMenuReadySent = false;
                }
                else if (frontendState == "post_combat_loot_confirmation")
                {
                    // Declining the native "leave items" dialog returns to the
                    // same loot decision boundary. Re-announce it after the
                    // confirmation widget finishes its close lifecycle.
                    _postCombatLootReadySent = false;
                }
                else if (frontendState == "end_expedition_confirmation" &&
                         GameModeMgr.CurrentMode == GameModeType.INN)
                {
                    // Return to the run-end summary after the native modal has
                    // actually finished closing.
                    _innEndExpeditionReturnPending = true;
                }
            }

            request.Response = "OK";
        }
    }
}
