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
    [BepInPlugin("dd2.agent.bridge", "DD2 Agent Bridge", BridgeVersion)]
    public sealed partial class DD2BridgePlugin : BaseUnityPlugin
    {
        private const string BridgeVersion = "0.0.55";
        private string _lastState;
        private Thread _pipeThread;
        private volatile bool _pipeRunning;
        private volatile NamedPipeServerStream _activePipe;

        private volatile string _currentModeText = "UNDETERMINED";
        private volatile bool _hostReadyCatchupRequested;
        
        private float _nextFrontendProbeAt;
        private const float FrontendProbeInterval = 0.10f;
        
        // v13.2 diagnostic-only transition watchdog. This never sends data to
        // the agent and never changes READY/command gating. It runs from Unity
        // Update(), so a monotonically increasing update counter proves that the
        // main thread is still ticking while a GameMode transition is in flight.
        private long _transitionWatchdogUpdateCount;
        private DateTime _transitionWatchdogNextLogAt = DateTime.MinValue;
        private bool _transitionWatchdogWasActive;
        
        private GameModeType _lastLoggedMode;
        private GameModeType _lastLoggedPreviousMode;
        private GameModeType _lastLoggedNextMode;
        private bool _lastLoggedTransitioning;
        private bool _hasLoggedModeState;

        private void Awake()
        {
            Logger.LogInfo($"DD2Bridge {BridgeVersion} loaded");

            _instance = this;

            try
            {
                _harmony = new Harmony(
                    "dd2.agent.bridge.effect-observer");

                _harmony.PatchAll(
                    typeof(DD2BridgePlugin).Assembly);

                Logger.LogInfo(
                    "EFFECT observer: Harmony patches installed");
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    $"EFFECT observer patch install failed: {ex}");
            }
            
            // [NEW: REAL DECISION BOUNDARY]
            EventManager.AddListener<EventBattleStateChanged>(
                HandleEventBattleStateChanged);

            EventManager.AddListener<EventSkillFinalizeResults>(
                HandleEventSkillFinalizeResults);

            // v8 diagnostic: observe the authoritative health-damage event
            // without replacing the existing consequence stream yet.
            EventManager.AddListener<EventActorHealthDamage>(
                HandleNativeHealthDamageEvent);
            EventManager.AddListener<EventActorHealthHeal>(
                HandleNativeHealthHealEvent);

            // Native stress events provide the actual post-resist/post-clamp
            // amount. The handler exports them only while a StressManager
            // trigger context is active, avoiding duplicates with skill results.
            EventManager.AddListener<EventStressDamage>(
                HandleNativeStressDamageEvent);
            EventManager.AddListener<EventStressHeal>(
                HandleNativeStressHealEvent);

            // Authoritative combat movement. ActorInstance.SetTeamPosition emits
            // this only when the actual team position changes, carrying the
            // source type and source actor captured by Move/SetTargetTeamPosition.
            EventManager.AddListener<EventTeamPositionChange>(
                HandleTeamPositionChange);

            // Native affinity leaning changes are authoritative consequences.
            // During ApplyPendingOverstress the same handler folds them into the
            // aggregate overstress event instead of emitting a duplicate event.
            EventManager.AddListener<EventAffinityConnectionLeaningChange>(
                HandleOverstressAffinityChange);
            
            _pipeRunning = true;

            _pipeThread = new Thread(PipeServerLoop);
            _pipeThread.IsBackground = true;
            _pipeThread.Start();
        }

        private void OnDestroy()
        {
            try
            {
                if (_harmony != null)
                {
                    _harmony.UnpatchSelf();
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    $"EFFECT observer unpatch failed: {ex.Message}");
            }

            if (_instance == this)
            {
                _instance = null;
            }

            // [NEW: CLEANUP]
            EventManager.RemoveListener<EventBattleStateChanged>(
                HandleEventBattleStateChanged);

            EventManager.RemoveListener<EventSkillFinalizeResults>(
                HandleEventSkillFinalizeResults);

            EventManager.RemoveListener<EventActorHealthDamage>(
                HandleNativeHealthDamageEvent);
            EventManager.RemoveListener<EventActorHealthHeal>(
                HandleNativeHealthHealEvent);
            EventManager.RemoveListener<EventStressDamage>(
                HandleNativeStressDamageEvent);
            EventManager.RemoveListener<EventStressHeal>(
                HandleNativeStressHealEvent);
            EventManager.RemoveListener<EventTeamPositionChange>(
                HandleTeamPositionChange);
            EventManager.RemoveListener<EventAffinityConnectionLeaningChange>(
                HandleOverstressAffinityChange);

            _pipeRunning = false;
        }

        private void Update()
        {
            try
            {
                UpdateModeState();

                ProcessNextRequest();
                ProcessPendingCombatReady();
                ProcessHostReadyCatchup();

                // Passive frontend discovery is mode-aware.  The old 10 Hz
                // sweep queried every frontend type in one frame, which turned
                // several Resources.FindObjectsOfTypeAll calls into a visible
                // 100 ms hitch pattern.
                if (UnityEngine.Time.unscaledTime >= _nextFrontendProbeAt)
                {
                    _nextFrontendProbeAt =
                        UnityEngine.Time.unscaledTime + FrontendProbeInterval;

                    ProcessFrontendReadyForCurrentMode();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Update failed: {ex}");
            }
        }
        
        private void UpdateModeState()
        {
            GameModeType currentMode = GameModeMgr.CurrentMode;
            _currentModeText = FormatMode(currentMode);

            if (currentMode == GameModeType.UNDETERMINED)
            {
                if (!_hasLoggedModeState ||
                    _lastLoggedMode != GameModeType.UNDETERMINED)
                {
                    _hasLoggedModeState = true;
                    _lastLoggedMode = GameModeType.UNDETERMINED;
                    _lastLoggedPreviousMode = null;
                    _lastLoggedNextMode = null;
                    _lastLoggedTransitioning = false;

                    Logger.LogInfo(
                        $"STATE ts:{DateTime.Now:HH:mm:ss}: " +
                        "mode=UNDETERMINED, transitioning=false");
                }

                return;
            }

            GameModeMgr mgr = GameModeMgr.Instance;

            if (mgr == null)
            {
                return;
            }

            bool transitioning = mgr.IsChangingState();
            GameModeType previousMode = mgr.GetPreviousMode();
            GameModeType nextMode = mgr.GetNextMode();

            if (_hasLoggedModeState &&
                _lastLoggedMode == currentMode &&
                _lastLoggedPreviousMode == previousMode &&
                _lastLoggedNextMode == nextMode &&
                _lastLoggedTransitioning == transitioning)
            {
                return;
            }

            _hasLoggedModeState = true;
            _lastLoggedMode = currentMode;
            _lastLoggedPreviousMode = previousMode;
            _lastLoggedNextMode = nextMode;
            _lastLoggedTransitioning = transitioning;

            Logger.LogInfo(
                $"STATE ts:{DateTime.Now:HH:mm:ss}: " +
                $"mode={FormatMode(currentMode)}, " +
                $"transitioning={transitioning}, " +
                $"previous={FormatMode(previousMode)}, " +
                $"next={FormatMode(nextMode)}");
        }

        private void ProcessFrontendReadyForCurrentMode()
        {
            GameModeMgr mgr = GameModeMgr.Instance;

            if (mgr == null || mgr.IsChangingState())
            {
                return;
            }

            GameModeType mode = GameModeMgr.CurrentMode;

            // COMBAT has absolute frontend priority. Road combats can keep
            // DRIVING objects alive underneath them, so no passive driving
            // frontend may overwrite a combat READY while Battle is running.
            if (IsCombatForegroundActive())
            {
                ResetStagecoachReadyBoundary();
                return;
            }

            // Player inventory is a real foreground decision surface. In
            // particular, overfill blocks driving and DD2 forces this screen.
            if (ProcessInventoryReady())
            {
                return;
            }

            // The mandatory arrival travelogue owns the foreground while the
            // Inn state machine is waiting for the player to close results.
            if (mode == GameModeType.INN &&
                ProcessInnTravelogueReady())
            {
                return;
            }

            if (mode == GameModeType.RESULTS)
            {
                ProcessConfirmationReady();
                ProcessPostCombatLootReady();
                ProcessExpeditionResultsReady();
                return;
            }

            if (mode == GameModeType.DRIVING)
            {
                // Max-Loathing boss empowerment is a modal DRIVING boundary.
                // Give it priority over every underlying road/node frontend.
                if (ProcessDoomResetReady())
                {
                    return;
                }

                // Generic loot is also used by road/node story rewards (for
                // example Oasis).  After the loot detector was generalized,
                // passive discovery still only polled it in RESULTS, so a
                // DRIVING-mode loot screen could remain invisible until a
                // reconnect/catch-up.  Mirror the RESULTS ordering here and
                // also poll confirmations needed by "leave items".
                ProcessConfirmationReady();
                ProcessPostCombatLootReady();

                if (ProcessHospitalReady())
                {
                    return;
                }

                if (ProcessStoreReady())
                {
                    return;
                }

                if (ProcessStoryChoiceReady())
                {
                    return;
                }

                if (ProcessRouteChoiceReady())
                {
                    return;
                }

                ProcessNodeEntryReady();
                ProcessBossSelectReady();
                ProcessStagecoachReady();
                return;
            }

            if (mode == GameModeType.ALTAR_OF_HOPE)
            {
                ProcessAltarOfHopeReady();
                return;
            }

            if (mode == GameModeType.HERO_SELECT)
            {
                ProcessConfirmationReady();
                ProcessHeroSelectReady();
                return;
            }

            if (mode == GameModeType.MAIN_MENU)
            {
                ProcessConfirmationReady();
                ProcessMainMenuReady();
                return;
            }
            
            if (mode == GameModeType.INN)
            {
                ProcessConfirmationReady();

                if (ProcessInnEndExpeditionReturnReady())
                {
                    return;
                }

                if (ProcessInnWainwrightReturnReady())
                {
                    return;
                }

                ProcessInnReady();
                return;
            }
        }

        // v13.2: passive main-thread / GameMode transition watchdog.
        // Log once per second only while GameModeMgr reports a transition. This
        // is intentionally BepInEx-log-only: it does not create a new agent
        // decision boundary and cannot be queried through the pipe.
        private void ProcessTransitionWatchdog()
        {
            DateTime now = DateTime.Now;

            try
            {
                GameModeType currentMode = GameModeMgr.CurrentMode;

                if (currentMode == GameModeType.UNDETERMINED)
                {
                    _transitionWatchdogWasActive = false;
                    _transitionWatchdogNextLogAt = DateTime.MinValue;
                    return;
                }

                GameModeMgr mgr = GameModeMgr.Instance;
                if (mgr == null)
                {
                    // This should be transient at most. Throttle it exactly like
                    // the normal watchdog so diagnostics cannot spam the log.
                    if (now >= _transitionWatchdogNextLogAt)
                    {
                        _transitionWatchdogNextLogAt = now.AddSeconds(1);
                        Logger.LogWarning(
                            $"WATCHDOG ts:{now:HH:mm:ss.fff}: " +
                            $"update={_transitionWatchdogUpdateCount}, " +
                            $"mode={FormatMode(currentMode)}, mgr=<null>");
                    }
                    return;
                }

                bool transitioning = mgr.IsChangingState();

                if (!transitioning)
                {
                    if (_transitionWatchdogWasActive)
                    {
                        Logger.LogInfo(
                            $"WATCHDOG transition ended ts:{now:HH:mm:ss.fff}: " +
                            $"update={_transitionWatchdogUpdateCount}, " +
                            $"mode={FormatMode(currentMode)}, " +
                            $"previous={FormatMode(mgr.GetPreviousMode())}, " +
                            $"next={FormatMode(mgr.GetNextMode())}");
                    }

                    _transitionWatchdogWasActive = false;
                    _transitionWatchdogNextLogAt = DateTime.MinValue;
                    return;
                }

                if (!_transitionWatchdogWasActive)
                {
                    _transitionWatchdogWasActive = true;
                    _transitionWatchdogNextLogAt = DateTime.MinValue;
                }

                if (now < _transitionWatchdogNextLogAt)
                {
                    return;
                }

                _transitionWatchdogNextLogAt = now.AddSeconds(1);

                string battleState = "<none>";
                try
                {
                    CombatBhv combat = CombatBhv.Instance;
                    if (combat != null)
                    {
                        battleState = combat.CurrentBattleState.ToString();
                    }
                }
                catch (Exception ex)
                {
                    // Battle state is supplementary. A teardown-time failure
                    // must not hide the authoritative GameMode watchdog line.
                    battleState = $"<unavailable:{ex.GetType().Name}>";
                }

                Logger.LogInfo(
                    $"WATCHDOG ts:{now:HH:mm:ss.fff}: " +
                    $"update={_transitionWatchdogUpdateCount}, " +
                    $"mode={FormatMode(currentMode)}, " +
                    $"transitioning=true, " +
                    $"previous={FormatMode(mgr.GetPreviousMode())}, " +
                    $"next={FormatMode(mgr.GetNextMode())}, " +
                    $"battle_state={battleState}");
            }
            catch (Exception ex)
            {
                // Diagnostics must never interfere with the game or bridge.
                if (now >= _transitionWatchdogNextLogAt)
                {
                    _transitionWatchdogNextLogAt = now.AddSeconds(1);
                    Logger.LogWarning(
                        $"WATCHDOG diagnostic failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private void LogStateIfChanged(string state)
        {
            if (state == _lastState)
            {
                return;
            }

            _lastState = state;
            Logger.LogInfo(
                $"STATE ts:{DateTime.Now:HH:mm:ss}: {state}");
        }

        private static string FormatMode(GameModeType mode)
        {
            return mode == null ? "<null>" : mode.GetName();
        }
    }
}



