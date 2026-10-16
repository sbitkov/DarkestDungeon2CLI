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
        // Event callbacks request READY; the snapshot is built on the next Unity Update.
        private bool _combatReadyPending;

        // READY may be produced by either the real IN_TURN_SELECT event or
        // host catch-up. Only one source may announce the current boundary
        // to the current pipe connection.
        private volatile bool _readySentForCurrentTurnSelect;

        private void HandleBattleJson(BridgeRequest request)
        {
            request.Response = CombatBhv.Instance
                .SaveToJson()
                .ToString(Newtonsoft.Json.Formatting.None);
        }

        private void HandleCurrentActorJson(BridgeRequest request)
        {
            ActorInstance actor = CombatBhv.Instance.GetCurrentActor();

            if (actor == null)
            {
                request.Response = "null";
            }
            else
            {
                request.Response = actor
                    .SaveToJson()
                    .ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        private void HandleTeamGuids(BridgeRequest request)
        {
            QueryTeamActors friendly =
                QueryTeamActors.Trigger(0, true);

            QueryTeamActors enemy =
                QueryTeamActors.Trigger(0, false);

            request.Response =
                "friendly=[" +
                string.Join(",", friendly.m_TeamActorGuids) +
                "] enemy=[" +
                string.Join(",", enemy.m_TeamActorGuids) +
                "]";
        }

        private void HandleCombatActorsJson(BridgeRequest request)
        {
            QueryTeamActors friendly =
                QueryTeamActors.Trigger(0, true);

            QueryTeamActors enemy =
                QueryTeamActors.Trigger(0, false);

            JArray friendlyActors = new JArray();
            JArray enemyActors = new JArray();

            foreach (uint guid in friendly.m_TeamActorGuids)
            {
                ActorInstance actor =
                    SingletonMonoBehaviour<Library<uint, ActorInstance>>
                        .Instance
                        .GetLibraryElement(guid);

                if (actor != null)
                {
                    friendlyActors.Add(BuildActorJson(actor));
                }
            }

            foreach (uint guid in enemy.m_TeamActorGuids)
            {
                ActorInstance actor =
                    SingletonMonoBehaviour<Library<uint, ActorInstance>>
                        .Instance
                        .GetLibraryElement(guid);

                if (actor != null)
                {
                    enemyActors.Add(BuildActorJson(actor));
                }
            }

            JObject result = new JObject
            {
                ["friendly"] = friendlyActors,
                ["enemy"] = enemyActors
            };

            request.Response =
                result.ToString(Newtonsoft.Json.Formatting.None);
        }

        private void HandleCurrentActorStats(BridgeRequest request)
        {
            ActorInstance actor = CombatBhv.Instance.GetCurrentActor();

            if (actor == null)
            {
                request.Response = "null";
            }
            else
            {
                JObject result = new JObject
                {
                    ["guid"] = actor.ActorGuid,
                    ["hp"] = actor.HpRounded,
                    ["hp_max"] = actor.CurrentHpMax,
                    ["speed"] =
                        actor.GetClampedStatValue(
                            ActorStatType.SPEED),
                    ["stress"] = actor.Stress,
                    ["stress_max"] =
                        actor.GetClampedStatValue(
                            ActorStatType.STRESS_MAX),
                    ["crit_chance"] =
                        actor.GetClampedStatValue(
                            ActorStatType.CRIT_CHANCE),
                    ["deaths_door_chance"] =
                        actor.GetClampedStatValue(
                            ActorStatType.DEATHS_DOOR_CHANCE)
                };

                request.Response =
                    result.ToString(
                        Newtonsoft.Json.Formatting.None);
            }
        }

        private void HandleCurrentActorResists(BridgeRequest request)
        {
            ActorInstance actor = CombatBhv.Instance.GetCurrentActor();

            if (actor == null)
            {
                request.Response = "null";
            }
            else
            {
                JObject result = new JObject();

                string[] keys =
                    actor.GetSubstatKeys(
                        ActorStatType.RESISTANCE);

                foreach (string key in keys)
                {
                    result[key] =
                        actor.GetClampedStatValue(
                            ActorStatType.RESISTANCE,
                            key);
                }

                request.Response =
                    result.ToString(
                        Newtonsoft.Json.Formatting.None);
            }
        }

        private void HandleCurrentAvailableActions(BridgeRequest request)
        {
            ActorInstance actor = CombatBhv.Instance.GetCurrentActor();

            if (actor == null)
            {
                request.Response = "null";
                return;
            }

            request.Response =
                BuildAvailableActions(actor)
                    .ToString(Newtonsoft.Json.Formatting.None);
        }

        private void HandleTestExecuteSkill(BridgeRequest request)
        {
            string[] parts = request.Command.Split(' ');

            if (parts.Length != 4)
            {
                request.Response =
                    "ERROR: usage: test-execute-skill " +
                    "<performer_guid> <skill_id> <target_guid>";

                return;
            }

            uint performerGuid;
            uint targetGuid;

            if (!uint.TryParse(parts[1], out performerGuid) ||
                !uint.TryParse(parts[3], out targetGuid))
            {
                request.Response = "ERROR: invalid guid";
                return;
            }

            string skillId = parts[2];

            ActorInstance actor =
                CombatBhv.Instance.GetCurrentActor();

            if (actor == null)
            {
                request.Response =
                    "ERROR: no current actor";
                return;
            }

            if (actor.ActorGuid != performerGuid)
            {
                request.Response =
                    "ERROR: performer is not current actor";
                return;
            }

            if (actor.Controller == null)
            {
                request.Response =
                    "ERROR: actor has no controller";
                return;
            }

            if (!actor.Controller.GetIsValidSkill(skillId))
            {
                request.Response =
                    "ERROR: skill is not currently valid";
                return;
            }

            if (!actor.Controller.GetIsValidSkillTarget(
                skillId,
                targetGuid))
            {
                request.Response =
                    "ERROR: target is not valid for skill";
                return;
            }

            // The command has passed all native validation. From this point
            // the current player decision boundary is consumed. Do this
            // before DD2 receives the selection events so no stale/pending
            // READY can race the action.
            _combatReadyPending = false;
            _readySentForCurrentTurnSelect = true;
            _hostReadyCatchupRequested = false;

            Logger.LogInfo(
                $"COMMAND consumes READY boundary: actor={performerGuid}, " +
                $"skill={skillId}, target={targetGuid}");

            EventSkillSelectionChanged.Trigger(
                isSelected: true,
                performerGuid,
                skillId,
                isUserInput: false,
                autohighlightTarget: false);

            EventSelectActor.Trigger(
                targetGuid,
                isUserInput: false);

            request.Response = "OK";
        }

        private void HandleEventBattleStateChanged(
            EventBattleStateChanged battleStateChangedEvent)
        {
            Logger.LogInfo(
                $"BATTLE STATE: {battleStateChangedEvent.m_FromBattleState} -> " +
                $"{battleStateChangedEvent.m_ToBattleState}, " +
                $"actor={battleStateChangedEvent.m_CurrentActorActorGuid}, " +
                $"team={battleStateChangedEvent.m_CurrentActorTeamIndex}");

            if (battleStateChangedEvent.m_ToBattleState ==
                    BattleState.IN_TURN_SELECT &&
                battleStateChangedEvent.m_FromBattleState !=
                    BattleState.IN_TURN_SELECT)
            {
                Logger.LogInfo(
                    $"READY boundary opened: actor=" +
                    $"{battleStateChangedEvent.m_CurrentActorActorGuid}");

                _readySentForCurrentTurnSelect = false;
                _combatReadyPending = true;
            }
            else if (battleStateChangedEvent.m_ToBattleState ==
                         BattleState.IN_TURN_SELECT &&
                     battleStateChangedEvent.m_FromBattleState ==
                         BattleState.IN_TURN_SELECT)
            {
                Logger.LogInfo(
                    "READY boundary ignored: IN_TURN_SELECT -> IN_TURN_SELECT");
            }
        }

        private bool IsFriendlyCombatActor(uint actorGuid)
        {
            QueryTeamActors friendly =
                QueryTeamActors.Trigger(0, true);

            foreach (uint guid in friendly.m_TeamActorGuids)
            {
                if (guid == actorGuid)
                {
                    return true;
                }
            }

            return false;
        }

        // v15.2 diagnostic probe: FindObjectOfType<GameOverScreenWidgetBhv>()
        // returned null on a visibly open results screen. Resources.FindObjectsOfTypeAll
        // includes inactive objects, so enumerate every native instance and log its
        // Unity topology. This is intentionally diagnostic-first: once the live
        // topology is known, the stable detector can be narrowed again.

        private void ProcessPendingCombatReady()
        {
            if (!_combatReadyPending)
            {
                return;
            }

            _combatReadyPending = false;


            if (_readySentForCurrentTurnSelect)
            {
                Logger.LogInfo(
                    "READY suppressed: current turn-select boundary was already announced.");
                return;
            }

            ActorInstance currentActor =
                CombatBhv.Instance?.GetCurrentActor();

            if (currentActor == null)
            {
                Logger.LogWarning(
                    "READY: IN_TURN_SELECT but current actor is unavailable.");
                return;
            }

            if (!IsFriendlyCombatActor(currentActor.ActorGuid))
            {
                Logger.LogInfo(
                    $"READY suppressed for enemy actor={currentActor.ActorGuid}");
                return;
            }

            JObject ready = BuildCombatReadyMessage();

            if (ready == null)
            {
                Logger.LogWarning(
                    "READY: IN_TURN_SELECT but current actor is unavailable.");
                return;
            }

            _readySentForCurrentTurnSelect = true;

            // If this READY also satisfied a newly connected host, do not
            // emit a second catch-up READY in the same Update.
            _hostReadyCatchupRequested = false;

            Logger.LogInfo(
                $"READY: actor={ready["current_actor_guid"]}");

            QueueOutgoingMessage(ready);
        }

        private JObject BuildActorJson(ActorInstance actor)
        {
            JObject actorJson = (JObject)actor.SaveToJson();

            actorJson["equipped_actions"] =
                BuildEquippedActions(actorJson);

            actorJson["derived_stats"] =
                BuildDerivedStats(actor);

            return actorJson;
        }
        
        private JObject BuildCombatRosterSummary()
        {
            QueryTeamActors friendly =
                QueryTeamActors.Trigger(0, true);

            QueryTeamActors enemy =
                QueryTeamActors.Trigger(0, false);

            JArray friendlyActors = new JArray();
            JArray enemyActors = new JArray();

            foreach (uint guid in friendly.m_TeamActorGuids)
            {
                ActorInstance actor =
                    SingletonMonoBehaviour<
                        Library<uint, ActorInstance>>
                        .Instance
                        .GetLibraryElement(guid);

                if (actor != null)
                {
                    friendlyActors.Add(
                        BuildCombatActorSummary(actor));
                }
            }

            foreach (uint guid in enemy.m_TeamActorGuids)
            {
                ActorInstance actor =
                    SingletonMonoBehaviour<
                        Library<uint, ActorInstance>>
                        .Instance
                        .GetLibraryElement(guid);

                if (actor != null)
                {
                    enemyActors.Add(
                        BuildCombatActorSummary(actor));
                }
            }

            return new JObject
            {
                ["friendly"] = friendlyActors,
                ["enemy"] = enemyActors
            };
        }
        
        // v12: expose the same live token instances that the combat HUD
        // considers UI-visible. ActorInfoUiBhv filters on TokenInstance.IsHidden
        // and groups ordinary token icons by Definition.Id.
        private JArray BuildUiVisibleTokens(ActorInstance actor)
        {
            JArray result = new JArray();

            if (actor == null || actor.TokenContainer == null)
            {
                return result;
            }

            IReadOnlyList<TokenInstance> visibleInstances =
                actor.TokenContainer.GetInstances(
                    (TokenInstance tokenInstance) =>
                        tokenInstance != null &&
                        !tokenInstance.IsHidden);

            // Preserve the container's first-seen order rather than relying on
            // Dictionary enumeration order. This keeps snapshots stable and
            // mirrors the UI's Definition.Id grouping semantics.
            Dictionary<string, List<TokenInstance>> stacks =
                new Dictionary<string, List<TokenInstance>>();

            List<string> tokenOrder =
                new List<string>();

            foreach (TokenInstance tokenInstance in visibleInstances)
            {
                if (tokenInstance.Definition == null)
                {
                    continue;
                }

                string tokenId =
                    tokenInstance.Definition.Id;

                if (string.IsNullOrEmpty(tokenId))
                {
                    continue;
                }

                List<TokenInstance> stack;

                if (!stacks.TryGetValue(tokenId, out stack))
                {
                    stack = new List<TokenInstance>();
                    stacks[tokenId] = stack;
                    tokenOrder.Add(tokenId);
                }

                stack.Add(tokenInstance);
            }

            foreach (string tokenId in tokenOrder)
            {
                List<TokenInstance> stack =
                    stacks[tokenId];

                string tooltip = "";

                try
                {
                    // Native DD2 formatter used by token hover tooltips.
                    tooltip =
                        TokenTooltipBhv.MakeTooltip(stack) ?? "";
                }
                catch (Exception ex)
                {
                    // A broken/localization-specific tooltip must never make
                    // the READY snapshot fail. id/count remain authoritative.
                    Logger.LogWarning(
                        $"Token tooltip failed for {tokenId}: {ex.Message}");
                }

                result.Add(
                    new JObject
                    {
                        ["id"] = tokenId,
                        ["count"] = stack.Count,
                        ["tooltip"] = tooltip
                    });
            }

            return result;
        }

        private JObject BuildCombatActorSummary(ActorInstance actor)
        {
            JObject raw = (JObject)actor.SaveToJson();

            string actorName =
                raw["m_ActorName"]?.Value<string>() ?? "";

            string actorId = "";

            JArray actorDataIds =
                raw["m_ActorDataIds"] as JArray;

            if (actorDataIds != null &&
                actorDataIds.Count > 0)
            {
                actorId =
                    actorDataIds[actorDataIds.Count - 1]
                        ?.Value<string>() ?? "";
            }

            return new JObject
            {
                ["guid"] = actor.ActorGuid,
                ["actor_id"] = actorId,
                ["name"] = actorName,

                // DD2 internally stores these positions zero-based.
                // READY exposes the human/UI-facing rank.
                ["rank"] = actor.GetFrontRank() + 1,

                ["hp"] = actor.HpRounded,
                ["hp_max"] = actor.CurrentHpMax,

                ["stress"] = actor.Stress,
                ["stress_max"] =
                    actor.GetClampedStatValue(
                        ActorStatType.STRESS_MAX),

                // Current DoTs are observable combat state. Keep the game
                // serialization intact for now; we will normalize its schema
                // after the first live sample.
                ["dots"] =
                    CloneOrEmptyArray(raw["DotContainer"]),

                ["tokens"] =
                    BuildUiVisibleTokens(actor)
            };
        }

        private JArray BuildEquippedActions(JObject actorJson)
        {
            JArray equippedActions = new JArray();

            JArray combatSkills =
                actorJson["m_CombatSkills"] as JArray;

            if (combatSkills == null)
            {
                return equippedActions;
            }

            foreach (JToken skillToken in combatSkills)
            {
                JObject skill = skillToken as JObject;

                if (skill == null)
                {
                    continue;
                }

                bool isEquipped =
                    skill["m_IsEquipped"] != null &&
                    skill["m_IsEquipped"].Value<bool>();

                bool isAlwaysEquipped =
                    skill["m_IsAlwaysEquipped"] != null &&
                    skill["m_IsAlwaysEquipped"].Value<bool>();

                if (isEquipped && !isAlwaysEquipped)
                {
                    equippedActions.Add(
                        skill["m_SkillId"]?.Value<string>());
                }
            }

            return equippedActions;
        }

        private JObject BuildDerivedStats(ActorInstance actor)
        {
            JObject resistances = new JObject();

            string[] resistanceKeys =
                actor.GetSubstatKeys(ActorStatType.RESISTANCE);

            foreach (string key in resistanceKeys)
            {
                resistances[key] =
                    actor.GetClampedStatValue(
                        ActorStatType.RESISTANCE,
                        key);
            }

            return new JObject
            {
                ["hp_max"] = actor.CurrentHpMax,
                ["speed"] =
                    actor.GetClampedStatValue(
                        ActorStatType.SPEED),
                ["stress_max"] =
                    actor.GetClampedStatValue(
                        ActorStatType.STRESS_MAX),
                ["resistances"] = resistances
            };
        }
        
        // v13: expose the same skill-level information shown by the native
        // SkillTooltipBhv / SkillTooltipCanvasElementBhv pipeline.
        private JObject BuildSkillInfo(ActorInstance actor, string skillId)
        {
            if (actor == null || string.IsNullOrEmpty(skillId))
            {
                return null;
            }

            ActorDataSkill skill =
                SingletonMonoBehaviour<Library<string, ActorDataSkill>>
                    .Instance
                    .GetLibraryElement(skillId);

            if (skill == null)
            {
                return null;
            }

            JObject result = new JObject();

            try
            {
                result["name"] =
                    SkillDescription.GetNameText(skill) ?? "";
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    $"Skill name preview failed for {skillId}: {ex.Message}");
            }

            try
            {
                // Matches SkillTooltipBhv.AddTextFromSkillId(): the UI calls
                // GetTopBarString(skill) without passing the ActorInstance.
                result["top_bar"] =
                    SkillDescription.GetTopBarString(skill) ?? "";
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    $"Skill top-bar preview failed for {skillId}: {ex.Message}");
            }

            try
            {
                JArray resultStrings = new JArray();

                foreach (string value in
                    SkillDescription.GetResultStringsByTargetType(
                        skill,
                        false,
                        actor.ActorGuid))
                {
                    resultStrings.Add(value ?? "");
                }

                result["results"] = resultStrings;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    $"Skill result-text preview failed for {skillId}: {ex.Message}");
            }

            return result;
        }

        // Copy only UI-facing numeric preview values immediately. SkillPreview
        // instances are pooled by DD2 and are recycled when SkillCalculation
        // clears its potential-result cache, so the bridge must never retain a
        // SkillPreview reference across snapshots.
        private JObject CopyUiTargetPreview(
            SkillCalculation.ActorResult.SkillPreview preview)
        {
            JObject result = new JObject();

            if (preview == null)
            {
                return result;
            }

            if (preview.m_IsToHitValid)
            {
                result["hit_chance"] = preview.m_ToHitChance;
            }

            if (preview.m_IsDamageValid)
            {
                result["damage"] =
                    new JObject
                    {
                        ["low"] = preview.m_DamageLow,
                        ["high"] = preview.m_DamageHigh,
                        ["crit"] = preview.m_CritDamage
                    };
            }

            if (preview.m_IsCritValid)
            {
                result["crit_chance"] = preview.m_CritChance;
            }

            // The native UI suppresses heal preview when m_HideHealPreview is
            // set, so do the same here rather than leaking hidden information.
            if (preview.m_IsHealValid &&
                !preview.m_HideHealPreview &&
                (preview.m_TargetHealthHealBase > 0f ||
                 preview.m_TargetHealthHealRange > 0f))
            {
                result["heal"] =
                    new JObject
                    {
                        ["base"] = preview.m_TargetHealthHealBase,
                        ["range"] = preview.m_TargetHealthHealRange
                    };
            }

            if (preview.m_GuardingActorGuid != 0u)
            {
                result["guarding_actor_guid"] =
                    preview.m_GuardingActorGuid;
            }

            return result;
        }

        // v13: read the same already-precalculated potential result used by
        // CombatInterfaceBarUiBhv.SetStatPreviews(). QuerySkillPreview is a
        // read-side query; it does not calculate or finalize an action.
        private JObject BuildTargetActionPreview(
            uint performerGuid,
            string skillId,
            uint targetGuid)
        {
            JObject result =
                new JObject
                {
                    ["actor_guid"] = targetGuid,
                    ["preview_query_valid"] = false
                };

            try
            {
                QuerySkillPreview query =
                    QuerySkillPreview.Trigger(
                        performerGuid,
                        skillId,
                        targetGuid);

                if (query == null)
                {
                    return result;
                }

                // QuerySkillPreview exposes the same telegraphed affinity
                // change that AffinityChangeUIBhv shows before committing the
                // action. This field is independent of QuerySkillPreview.IsValid:
                // IsValid only means that ordinary stat previews exist.
                if (query.m_AffinityTickTriggerInstance != null)
                {
                    var affinity =
                        query.m_AffinityTickTriggerInstance;

                    JArray connectionActorGuids = new JArray();

                    if (affinity.m_Connection != null &&
                        affinity.m_Connection.ActorGuids != null)
                    {
                        foreach (uint actorGuid
                            in affinity.m_Connection.ActorGuids)
                        {
                            connectionActorGuids.Add(actorGuid);
                        }
                    }

                    JObject affinityWarning =
                        new JObject
                        {
                            ["connection_actor_guids"] =
                                connectionActorGuids,
                            ["leaning_change"] =
                                affinity.m_LeaningChange,
                            ["is_positive"] =
                                affinity.m_LeaningChange > 0
                        };

                    if (affinity.m_Definition != null)
                    {
                        affinityWarning["trigger_id"] =
                            affinity.m_Definition.m_Id;
                    }

                    // Convenience field for the overwhelmingly common case
                    // where the telegraphed connection contains the acting
                    // hero. Keep the full connection above as authoritative,
                    // because observing-role triggers can involve another pair.
                    if (connectionActorGuids.Count == 2)
                    {
                        uint first =
                            connectionActorGuids[0].Value<uint>();
                        uint second =
                            connectionActorGuids[1].Value<uint>();

                        if (first == performerGuid)
                        {
                            affinityWarning["with_actor_guid"] = second;
                        }
                        else if (second == performerGuid)
                        {
                            affinityWarning["with_actor_guid"] = first;
                        }
                    }

                    result["affinity_warning"] =
                        affinityWarning;
                }

                if (!query.IsValid)
                {
                    return result;
                }

                result["preview_query_valid"] = true;

                JObject uiPreview = null;

                foreach (
                    SkillCalculation.ActorResult.SkillPreview preview
                    in query.m_SkillPreviews)
                {
                    if (preview == null)
                    {
                        continue;
                    }

                    // Reproduce the same target-result selection used by
                    // CombatInterfaceBarUiBhv.SetStatPreviews(). If multiple
                    // matching results exist, the last one wins just as it
                    // does in the UI loop.
                    if (!string.Equals(
                            preview.m_ResultType.ToString(),
                            "TARGET",
                            StringComparison.OrdinalIgnoreCase) ||
                        preview.m_PerformerActorGuid != performerGuid ||
                        (preview.m_TargetActorGuid != targetGuid &&
                         preview.m_GuardingActorGuid != targetGuid))
                    {
                        continue;
                    }

                    uiPreview =
                        CopyUiTargetPreview(preview);
                }

                if (uiPreview != null && uiPreview.HasValues)
                {
                    result["ui_preview"] = uiPreview;
                }
            }
            catch (Exception ex)
            {
                // READY must remain available even if an individual preview
                // is missing during an unusual timing edge. Never trigger the
                // calculation pipeline from here; fail soft and expose that
                // the native cached preview was unavailable.
                result["preview_error"] = ex.Message;
                Logger.LogWarning(
                    $"Skill preview query failed for " +
                    $"{performerGuid}/{skillId}/{targetGuid}: {ex.Message}");
            }

            return result;
        }

        // [CANONICAL AVAILABLE ACTIONS + UI PREVIEW]
        // Legal actions still come exclusively from the actor controller.
        // v13 augments them with the native skill tooltip and the already
        // cached target-specific preview that DD2 exposes before committing.
        private JArray BuildAvailableActions(ActorInstance actor)
        {
            JArray actions = new JArray();

            if (actor == null || actor.Controller == null)
            {
                return actions;
            }

            IReadOnlyList<SkillTargetEntry> entries =
                actor.Controller.GetValidSkillTargetEntries();

            if (entries == null)
            {
                return actions;
            }

            foreach (SkillTargetEntry entry in entries)
            {
                JArray targets = new JArray();
                JArray targetPreviews = new JArray();

                foreach (uint targetGuid
                    in entry.m_ValidTargetActorGuids)
                {
                    // Keep the old GUID-only field for protocol compatibility.
                    targets.Add(targetGuid);

                    targetPreviews.Add(
                        BuildTargetActionPreview(
                            actor.ActorGuid,
                            entry.m_SkillId,
                            targetGuid));
                }

                JArray stealthedTargets = new JArray();

                foreach (uint targetGuid
                    in entry.m_StealthedTargetActorGuids)
                {
                    stealthedTargets.Add(targetGuid);
                }

                JObject action =
                    new JObject
                    {
                        ["skill_id"] = entry.m_SkillId,
                        ["forced"] = entry.m_IsForcedSkill,
                        ["targets"] = targets,
                        ["target_previews"] = targetPreviews,
                        ["stealthed_targets"] = stealthedTargets
                    };

                JObject skillInfo =
                    BuildSkillInfo(actor, entry.m_SkillId);

                if (skillInfo != null && skillInfo.HasValues)
                {
                    action["skill_info"] = skillInfo;
                }

                actions.Add(action);
            }

            return actions;
        }
        
        // v17: read-only snapshot for whichever stable decision boundary is
        // currently authoritative. This preserves the old combat-state behavior
        // while making the host's existing `state` command useful on frontends.
        private void HandleCurrentState(BridgeRequest request)
        {
            GameOverScreenWidgetBhv results = GetStableExpeditionResultsScreen();
            if (results != null)
            {
                JObject state = BuildExpeditionResultsReadyMessage();
                state["type"] = "state";
                QueueOutgoingMessage(state);
                request.Response = "OK";
                return;
            }

            UiScreenBhv nodeScreen;
            EnterNodeScreenWidgetBhv nodeWidget;
            EnterNodeScreenPushParams nodeParams;
            if (TryGetStableNodeEntry(
                out nodeScreen,
                out nodeWidget,
                out nodeParams))
            {
                JObject state = BuildNodeEntryReadyMessage(nodeParams);
                state["type"] = "state";
                QueueOutgoingMessage(state);
                request.Response = "OK";
                return;
            }

            if (GetStableAltarOfHopeUi() != null)
            {
                JObject state = BuildAltarOfHopeReadyMessage();
                state["type"] = "state";
                QueueOutgoingMessage(state);
                request.Response = "OK";
                return;
            }

            BossSelectWidgetBhv bossSelect = GetStableBossSelectWidget();
            if (bossSelect != null)
            {
                JObject state = BuildBossSelectReadyMessage(bossSelect);
                state["type"] = "state";
                QueueOutgoingMessage(state);
                request.Response = "OK";
                return;
            }

            UnityEngine.MonoBehaviour heroSelect = GetStableHeroSelectBhv();
            if (heroSelect != null)
            {
                JObject state = BuildHeroSelectReadyMessage(heroSelect);
                state["type"] = "state";
                QueueOutgoingMessage(state);
                request.Response = "OK";
                return;
            }

            if (GetStableStagecoachReadyVehicle() != null)
            {
                JObject state = BuildStagecoachReadyMessage();
                state["type"] = "state";
                QueueOutgoingMessage(state);
                request.Response = "OK";
                return;
            }

            if (GetStableMainMenuScreen() != null)
            {
                JObject state = BuildMainMenuReadyMessage();
                state["type"] = "state";
                QueueOutgoingMessage(state);
                request.Response = "OK";
                return;
            }

            if (GameModeMgr.CurrentMode == GameModeType.COMBAT)
            {
                HandleCombatState(request);
                return;
            }

            request.Response = "ERROR: no stable decision boundary is available";
        }

        // [NEW: MANUAL READ-ONLY COMBAT STATE]
        // Rebuilds the same snapshot used by READY, but does not create
        // a new decision boundary.
        private void HandleCombatState(BridgeRequest request)
        {
            if (GameModeMgr.CurrentMode != GameModeType.COMBAT)
            {
                request.Response = "ERROR: not in combat";
                return;
            }

            if (GameModeMgr.Instance.IsChangingState())
            {
                request.Response = "ERROR: game mode is transitioning";
                return;
            }

            if (CombatBhv.Instance == null)
            {
                request.Response = "ERROR: combat is unavailable";
                return;
            }

            if (CombatBhv.Instance.CurrentBattleState !=
                BattleState.IN_TURN_SELECT)
            {
                request.Response =
                    "ERROR: combat is not at a turn-select boundary";
                return;
            }

            JObject state = BuildCombatReadyMessage();

            if (state == null)
            {
                request.Response =
                    "ERROR: current combat state is unavailable";
                return;
            }

            state["type"] = "state";

            QueueOutgoingMessage(state);
            request.Response = "OK";
        }

        // v11: expose the same public turn-order query used by
        // BattleInfoUiBhv. The default Trigger() keeps
        // showStartRoundTurns=true, matching the combat UI.
        private JArray BuildUiVisibleTurnOrder()
        {
            Assets.Code.Combat.Queries.QueryTurnOrder query =
                Assets.Code.Combat.Queries.QueryTurnOrder.Trigger();

            JArray turnOrder = new JArray();

            foreach (uint actorGuid in query.m_RemainingTurnOrder)
            {
                turnOrder.Add(actorGuid);
            }

            return turnOrder;
        }

        // [NEW: CANONICAL COMBAT READY]
        private JObject BuildCombatReadyMessage()
        {
            ActorInstance actor =
                CombatBhv.Instance.GetCurrentActor();

            if (actor == null || actor.Controller == null)
            {
                return null;
            }

            JObject roster =
                BuildCombatRosterSummary();

            return new JObject
            {
                ["type"] = "ready",

                ["round"] =
                    CombatBhv.Instance.CurrentRound,

                // Native DD2 turn index; intentionally kept zero-based.
                ["turn_index"] =
                    CombatBhv.Instance.CurrentTurn,

                ["turn_order"] =
                    BuildUiVisibleTurnOrder(),

                ["current_actor_guid"] =
                    actor.ActorGuid,

                ["current_actor"] =
                    BuildCombatActorSummary(actor),

                ["friendly"] =
                    roster["friendly"],

                ["enemy"] =
                    roster["enemy"],

                ["available_actions"] =
                    BuildAvailableActions(actor)
            };
        }
    }
}
