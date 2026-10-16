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
using Assets.Code.Buff;
using Assets.Code.Quirk;
using Assets.Code.Stress;
using Assets.Code.Source;
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
        // v7: generic EffectInstance.Apply observer. The first semantic
        // adapter is DoT; the snapshot deliberately includes the other
        // structural containers so token/buff/quirk/movement adapters can
        // reuse the same hook later without another interception layer.
        private Harmony _harmony;
        private static DD2BridgePlugin _instance;
        private OverstressObservation _activeOverstressObservation;
        private StressTriggerQueueElement _activeStressTrigger;
        private HealthDamageContext _activeHealthDamageContext;
        private EffectHealObservation _activeEffectHealObservation;
        private readonly List<PendingNativeSkillHeal> _pendingNativeSkillHeals =
            new List<PendingNativeSkillHeal>();

        private sealed class HealthDamageContext
        {
            public readonly JArray SourceActorGuids = new JArray();
            public readonly JArray SourceIds = new JArray();
            public string SourceType = "";
            public string DeathType = "";
            public HealthDamageContext Previous;
        }

        private sealed class EffectHealProvenance
        {
            public readonly JArray SourceActorGuids = new JArray();
            public readonly JArray SourceIds = new JArray();
            public string SourceType = "";
            public float RequestedAmount;
            public bool IsCrit;
        }

        private sealed class EffectHealObservation
        {
            public readonly List<EffectHealProvenance> Heals =
                new List<EffectHealProvenance>();
            public int NextIndex;
            public EffectHealObservation Previous;
        }

        private sealed class PendingNativeSkillHeal
        {
            public uint TargetGuid;
            public int TeamIndex;
            public float RequestedAmount;
            public float ActualAmount;
            public bool IsCrit;
            public string SourceType = "";
            public bool HasDisplayed;
        }

        private sealed class EffectObservation
        {
            public JObject PerformerBefore;
            public JObject TargetBefore;
        }

        // v10: one aggregate observation around ActorInstance.ApplyPendingOverstress.
        // DD2 chooses the OverstressDefinition before this method, applies all
        // of its effects synchronously, then fires EventActorOverstress. Keeping
        // the transaction open across the whole method lets the bridge report
        // the selected result plus the resulting HP/stress and related visible
        // consequences without guessing from individual damage events.
        private sealed class OverstressObservation
        {
            public uint ActorGuid;
            public string ResultId;
            public float HpBefore;
            public float StressBefore;
            public JArray QuirksBefore;
            public readonly JArray RelationshipConsequences =
                new JArray();
        }

        [HarmonyPatch(typeof(ActorInstance), "ApplyPendingOverstress")]
        private static class ActorInstanceApplyPendingOverstressPatch
        {
            private static void Prefix(
                ActorInstance __instance,
                out OverstressObservation __state)
            {
                __state = null;

                DD2BridgePlugin bridge = _instance;

                if (bridge == null)
                {
                    return;
                }

                try
                {
                    __state =
                        bridge.BeginOverstressObservation(__instance);
                }
                catch (Exception ex)
                {
                    bridge.LogOverstressObserverException(
                        "prefix",
                        ex);
                }
            }

            private static void Postfix(
                ActorInstance __instance,
                OverstressObservation __state)
            {
                DD2BridgePlugin bridge = _instance;

                if (bridge == null || __state == null)
                {
                    return;
                }

                try
                {
                    bridge.EndOverstressObservation(
                        __instance,
                        __state);
                }
                catch (Exception ex)
                {
                    bridge.LogOverstressObserverException(
                        "postfix",
                        ex);
                }
            }
        }
        // Native StressManager provenance observer. The queue element still
        // contains source_id/source_type, while EventStressDamage/Heal carries
        // the actual post-resist/post-clamp amount. Keep the current element
        // alive only for the synchronous duration of AttemptProcessTrigger().
        [HarmonyPatch(typeof(StressManager), "AttemptProcessTrigger")]
        private static class StressManagerAttemptProcessTriggerPatch
        {
            private static void Prefix(
                List<StressTriggerQueueElement> ___m_StressTriggerQueue,
                out StressTriggerQueueElement __state)
            {
                __state = null;

                DD2BridgePlugin bridge = _instance;

                if (bridge == null ||
                    ___m_StressTriggerQueue == null ||
                    ___m_StressTriggerQueue.Count == 0)
                {
                    return;
                }

                try
                {
                    __state = ___m_StressTriggerQueue[0];
                    bridge.BeginStressTriggerObservation(__state);
                }
                catch (Exception ex)
                {
                    bridge.LogStressTriggerObserverException(
                        "prefix",
                        ex);
                }
            }

            private static void Postfix(
                StressTriggerQueueElement __state)
            {
                DD2BridgePlugin bridge = _instance;

                if (bridge == null || __state == null)
                {
                    return;
                }

                try
                {
                    bridge.EndStressTriggerObservation(__state);
                }
                catch (Exception ex)
                {
                    bridge.LogStressTriggerObserverException(
                        "postfix",
                        ex);
                }
            }
        }


        // Capture the provenance that EventActorHealthDamage itself drops.
        // The final ApplyHealthDamage overload is the authoritative application
        // seam: it already has source actor GUIDs/source IDs and synchronously
        // emits EventActorHealthDamage with the accepted damage result.
        [HarmonyPatch(
            typeof(ActorInstance),
            "ApplyHealthDamage",
            new Type[]
            {
                typeof(float),
                typeof(bool),
                typeof(bool),
                typeof(IReadOnlyList<uint>),
                typeof(DeathType),
                typeof(SourceType),
                typeof(IReadOnlyList<string>),
                typeof(bool),
                typeof(ActorHealthDamageCalculation)
            })]
        private static class ActorInstanceApplyHealthDamagePatch
        {
            private static void Prefix(
                IReadOnlyList<uint> __3,
                DeathType __4,
                SourceType __5,
                IReadOnlyList<string> __6,
                out HealthDamageContext __state)
            {
                __state = null;

                DD2BridgePlugin bridge = _instance;

                if (bridge == null)
                {
                    return;
                }

                try
                {
                    __state = bridge.BeginHealthDamageContext(
                        __3,
                        __4,
                        __5,
                        __6);
                }
                catch (Exception ex)
                {
                    bridge.Logger.LogError(
                        $"HEALTH DAMAGE provenance prefix failed: {ex}");
                }
            }

            private static void Postfix(
                HealthDamageContext __state)
            {
                DD2BridgePlugin bridge = _instance;

                if (bridge == null || __state == null)
                {
                    return;
                }

                try
                {
                    bridge.EndHealthDamageContext(__state);
                }
                catch (Exception ex)
                {
                    bridge.Logger.LogError(
                        $"HEALTH DAMAGE provenance postfix failed: {ex}");
                }
            }
        }

        // EffectApplyCombinedResult still owns performer/source provenance for
        // healing, but ApplyHealthHeal intentionally receives only SourceType.
        // Snapshot its positive-health changes in the exact native enumeration
        // order; EventActorHealthHeal fires synchronously for those entries.
        [HarmonyPatch(typeof(EffectApplyCombinedResult), "Apply")]
        private static class EffectApplyCombinedResultApplyPatch
        {
            private static void Prefix(
                EffectApplyCombinedResult __instance,
                out EffectHealObservation __state)
            {
                __state = null;

                DD2BridgePlugin bridge = _instance;

                if (bridge == null)
                {
                    return;
                }

                try
                {
                    __state =
                        bridge.BeginEffectHealObservation(__instance);
                }
                catch (Exception ex)
                {
                    bridge.Logger.LogError(
                        $"HEALTH HEAL effect provenance prefix failed: {ex}");
                }
            }

            private static void Postfix(
                EffectHealObservation __state)
            {
                DD2BridgePlugin bridge = _instance;

                if (bridge == null || __state == null)
                {
                    return;
                }

                try
                {
                    bridge.EndEffectHealObservation(__state);
                }
                catch (Exception ex)
                {
                    bridge.Logger.LogError(
                        $"HEALTH HEAL effect provenance postfix failed: {ex}");
                }
            }
        }

        [HarmonyPatch(
            typeof(EffectInstance),
            "Apply",
            new Type[]
            {
                typeof(ActorInstance),
                typeof(ActorInstance),
                typeof(EffectApplyCombinedResult)
            })]
        private static class EffectInstanceApplyPatch
        {
            private static void Prefix(
                EffectInstance __instance,
                ActorInstance __0,
                ActorInstance __1,
                out EffectObservation __state)
            {
                __state = null;

                DD2BridgePlugin bridge = _instance;

                if (bridge == null)
                {
                    return;
                }

                try
                {
                    __state = bridge.BeginEffectObservation(
                        __0,
                        __1);
                }
                catch (Exception ex)
                {
                    bridge.LogEffectObserverException(
                        "prefix",
                        ex);
                }
            }

            private static void Postfix(
                EffectInstance __instance,
                ActorInstance __0,
                ActorInstance __1,
                EffectObservation __state)
            {
                DD2BridgePlugin bridge = _instance;

                if (bridge == null || __state == null)
                {
                    return;
                }

                try
                {
                    bridge.EndEffectObservation(
                        __instance,
                        __0,
                        __1,
                        __state);
                }
                catch (Exception ex)
                {
                    // Observer failures must never alter DD2 gameplay.
                    bridge.LogEffectObserverException(
                        "postfix",
                        ex);
                }
            }
        }

        private OverstressObservation BeginOverstressObservation(
            ActorInstance actor)
        {
            if (actor == null ||
                GameModeMgr.CurrentMode != GameModeType.COMBAT)
            {
                return null;
            }

            var pendingOverstress = actor.GetPendingOverstress();

            if (pendingOverstress == null)
            {
                return null;
            }

            if (_activeOverstressObservation != null)
            {
                Logger.LogWarning(
                    "OVERSTRESS observer: nested transaction ignored.");
                return null;
            }

            OverstressObservation observation =
                new OverstressObservation
                {
                    ActorGuid = actor.ActorGuid,
                    ResultId = pendingOverstress.m_Id ?? "",
                    HpBefore = actor.HpRounded,
                    StressBefore = actor.Stress,
                    QuirksBefore = CaptureNormalizedQuirks(actor)
                };

            _activeOverstressObservation = observation;

            Logger.LogInfo(
                $"OVERSTRESS begin: actor={observation.ActorGuid}, " +
                $"result={observation.ResultId}, " +
                $"hp={observation.HpBefore}, " +
                $"stress={observation.StressBefore}");

            return observation;
        }

        private void EndOverstressObservation(
            ActorInstance actor,
            OverstressObservation observation)
        {
            try
            {
                if (actor == null || observation == null)
                {
                    return;
                }

                float hpAfter = actor.HpRounded;
                float stressAfter = actor.Stress;

                JArray consequences =
                    (JArray)observation.RelationshipConsequences.DeepClone();

                AppendQuirkConsequences(
                    consequences,
                    observation.QuirksBefore,
                    CaptureNormalizedQuirks(actor));

                JObject message = new JObject
                {
                    ["type"] = "event",
                    ["event"] = "overstress",
                    ["actor_guid"] = observation.ActorGuid,
                    ["result"] = observation.ResultId,
                    ["hp"] = BuildBeforeAfterDelta(
                        observation.HpBefore,
                        hpAfter),
                    ["stress"] = BuildBeforeAfterDelta(
                        observation.StressBefore,
                        stressAfter),
                    ["consequences"] = consequences
                };

                QueueOutgoingMessage(message);

                Logger.LogInfo(
                    "OVERSTRESS result: " +
                    message.ToString(
                        Newtonsoft.Json.Formatting.None));
            }
            finally
            {
                if (ReferenceEquals(
                    _activeOverstressObservation,
                    observation))
                {
                    _activeOverstressObservation = null;
                }
            }
        }

        private void HandleOverstressAffinityChange(
            EventAffinityConnectionLeaningChange affinityEvent)
        {
            bool innItemObservation =
                GameModeMgr.CurrentMode == GameModeType.INN &&
                _activeInnItemRelationshipConsequences != null;

            if (affinityEvent == null ||
                affinityEvent.m_Connection == null ||
                (GameModeMgr.CurrentMode != GameModeType.COMBAT && !innItemObservation))
            {
                return;
            }

            JArray actorGuids = new JArray();
            int actorCount = 0;

            foreach (uint actorGuid
                in affinityEvent.m_Connection.ActorGuids)
            {
                actorGuids.Add(actorGuid);
                actorCount++;
            }

            int previousLeaning = affinityEvent.m_PreviousLeaning;
            int leaningChange = affinityEvent.m_LeaningChange;
            int currentLeaning = previousLeaning + leaningChange;

            string sourceType =
                affinityEvent.m_SourceType == null
                    ? ""
                    : affinityEvent.m_SourceType.GetName();

            if (innItemObservation)
            {
                JObject consequence = new JObject
                {
                    ["type"] = "relationship_changed",
                    ["actor_guids"] = actorGuids.DeepClone(),
                    ["delta"] = leaningChange,
                    ["previous"] = previousLeaning,
                    ["current"] = currentLeaning,
                    ["source_type"] = sourceType
                };
                if (affinityEvent.m_SouceActorGuid.HasValue)
                    consequence["source_actor_guid"] = affinityEvent.m_SouceActorGuid.Value;
                if (!string.IsNullOrEmpty(affinityEvent.m_ResetRelationshipId))
                    consequence["reset_relationship_id"] = affinityEvent.m_ResetRelationshipId;
                _activeInnItemRelationshipConsequences.Add(consequence);
                return;
            }

            OverstressObservation observation =
                _activeOverstressObservation;

            if (observation != null)
            {
                bool involvesObservedActor = false;
                uint otherActorGuid = 0u;

                foreach (uint actorGuid
                    in affinityEvent.m_Connection.ActorGuids)
                {
                    if (actorGuid == observation.ActorGuid)
                    {
                        involvesObservedActor = true;
                    }
                    else
                    {
                        otherActorGuid = actorGuid;
                    }
                }

                if (involvesObservedActor)
                {
                    JObject consequence = new JObject
                    {
                        ["type"] = "relationship_changed",
                        ["actor_guids"] = actorGuids.DeepClone(),
                        ["delta"] = leaningChange,
                        ["previous"] = previousLeaning,
                        ["current"] = currentLeaning,
                        ["source_type"] = sourceType
                    };

                    if (affinityEvent.m_SouceActorGuid.HasValue)
                    {
                        consequence["source_actor_guid"] =
                            affinityEvent.m_SouceActorGuid.Value;
                    }

                    if (!string.IsNullOrEmpty(
                        affinityEvent.m_ResetRelationshipId))
                    {
                        consequence["reset_relationship_id"] =
                            affinityEvent.m_ResetRelationshipId;
                    }

                    if (actorCount == 2 && otherActorGuid != 0u)
                    {
                        consequence["other_actor_guid"] = otherActorGuid;
                    }

                    observation.RelationshipConsequences.Add(consequence);

                    // The aggregate overstress event owns relationship changes
                    // that occur inside ApplyPendingOverstress. Do not also emit
                    // a standalone affinity_change for the same native event.
                    return;
                }
            }

            JObject message = new JObject
            {
                ["type"] = "event",
                ["event"] = "affinity_change",
                ["actor_guids"] = actorGuids,
                ["amount"] = leaningChange,
                ["previous"] = previousLeaning,
                ["current"] = currentLeaning,
                ["source_type"] = sourceType
            };

            if (affinityEvent.m_SouceActorGuid.HasValue)
            {
                message["source_actor_guid"] =
                    affinityEvent.m_SouceActorGuid.Value;
            }

            if (!string.IsNullOrEmpty(
                affinityEvent.m_ResetRelationshipId))
            {
                message["reset_relationship_id"] =
                    affinityEvent.m_ResetRelationshipId;
            }

            QueueOutgoingMessage(message);

            Logger.LogInfo(
                $"AFFINITY change: actors={actorGuids}, " +
                $"delta={leaningChange}, " +
                $"previous={previousLeaning}, current={currentLeaning}, " +
                $"source={sourceType}");
        }

        private static void AppendQuirkConsequences(
            JArray consequences,
            JArray before,
            JArray after)
        {
            before = before ?? new JArray();
            after = after ?? new JArray();

            Dictionary<string, List<JObject>> beforeGroups =
                GroupQuirkInstances(before);

            Dictionary<string, List<JObject>> afterGroups =
                GroupQuirkInstances(after);

            HashSet<string> keys =
                new HashSet<string>(beforeGroups.Keys);

            foreach (string key in afterGroups.Keys)
            {
                keys.Add(key);
            }

            foreach (string key in keys)
            {
                List<JObject> beforeItems;
                List<JObject> afterItems;

                if (!beforeGroups.TryGetValue(key, out beforeItems))
                {
                    beforeItems = new List<JObject>();
                }

                if (!afterGroups.TryGetValue(key, out afterItems))
                {
                    afterItems = new List<JObject>();
                }

                int paired =
                    System.Math.Min(
                        beforeItems.Count,
                        afterItems.Count);

                for (int i = 0; i < paired; i++)
                {
                    if (!JToken.DeepEquals(
                        beforeItems[i],
                        afterItems[i]))
                    {
                        consequences.Add(
                            BuildQuirkConsequence(
                                "quirk_changed",
                                afterItems[i],
                                beforeItems[i]));
                    }
                }

                for (int i = paired;
                    i < beforeItems.Count;
                    i++)
                {
                    consequences.Add(
                        BuildQuirkConsequence(
                            "quirk_removed",
                            beforeItems[i],
                            null));
                }

                for (int i = paired;
                    i < afterItems.Count;
                    i++)
                {
                    consequences.Add(
                        BuildQuirkConsequence(
                            "quirk_added",
                            afterItems[i],
                            null));
                }
            }
        }

        private static JObject BuildBeforeAfterDelta(
            float before,
            float after)
        {
            return new JObject
            {
                ["before"] = before,
                ["after"] = after,
                ["delta"] = after - before
            };
        }

        private void LogOverstressObserverException(
            string phase,
            Exception ex)
        {
            Logger.LogError(
                $"OVERSTRESS observer {phase} failed: {ex}");
        }

        private void BeginStressTriggerObservation(
            StressTriggerQueueElement trigger)
        {
            if (trigger == null ||
                GameModeMgr.CurrentMode != GameModeType.COMBAT)
            {
                return;
            }

            if (_activeStressTrigger != null)
            {
                Logger.LogWarning(
                    "STRESS TRIGGER observer: nested trigger context replaced.");
            }

            _activeStressTrigger = trigger;

            Logger.LogInfo(
                $"STRESS TRIGGER begin: actor={trigger.m_ActorGuid}, " +
                $"stress={trigger.m_Stress}, " +
                $"source={trigger.m_SourceType?.GetName()}/{trigger.m_SourceId}");
        }

        private void EndStressTriggerObservation(
            StressTriggerQueueElement trigger)
        {
            if (ReferenceEquals(_activeStressTrigger, trigger))
            {
                _activeStressTrigger = null;
            }
        }

        private void HandleNativeStressDamageEvent(
            EventStressDamage stressEvent)
        {
            if (stressEvent == null ||
                stressEvent.m_StressDamageAmount <= 0f)
            {
                return;
            }

            EmitNativeStressTriggerEvent(
                "stress_damage",
                stressEvent.m_ActorGuid,
                stressEvent.m_TeamIndex,
                stressEvent.m_StressDamageAmount,
                stressEvent.m_SourceType);
        }

        private void HandleNativeStressHealEvent(
            EventStressHeal stressEvent)
        {
            if (stressEvent == null ||
                stressEvent.m_StressHealAmount <= 0f)
            {
                return;
            }

            EmitNativeStressTriggerEvent(
                "stress_heal",
                stressEvent.m_ActorGuid,
                stressEvent.m_TeamIndex,
                stressEvent.m_StressHealAmount,
                stressEvent.m_SourceType);
        }

        private void EmitNativeStressTriggerEvent(
            string eventName,
            uint actorGuid,
            int teamIndex,
            float actualAmount,
            Assets.Code.Source.SourceType eventSourceType)
        {
            StressTriggerQueueElement trigger = _activeStressTrigger;

            // Skill/effect stress changes are already exported by the existing
            // consequence path. This adapter intentionally exports only changes
            // occurring synchronously inside StressManager.AttemptProcessTrigger.
            if (trigger == null ||
                GameModeMgr.CurrentMode != GameModeType.COMBAT ||
                trigger.m_ActorGuid != actorGuid)
            {
                return;
            }

            JObject message = new JObject
            {
                ["type"] = "event",
                ["event"] = eventName,
                ["target_guid"] = actorGuid,
                ["team_index"] = teamIndex,
                ["amount"] = actualAmount,
                ["source_type"] =
                    eventSourceType == null
                        ? ""
                        : eventSourceType.GetName(),
                ["source_id"] = trigger.m_SourceId ?? "",
                ["stress_trigger"] = true,
                ["trigger_stress"] = trigger.m_Stress,
                ["bark_valid"] = trigger.m_IsBarkValid
            };

            QueueOutgoingMessage(message);

            Logger.LogInfo(
                $"STRESS TRIGGER {eventName}: actor={actorGuid}, " +
                $"amount={actualAmount}, requested={trigger.m_Stress}, " +
                $"source={message["source_type"]}/{message["source_id"]}");
        }

        private void LogStressTriggerObserverException(
            string phase,
            Exception ex)
        {
            Logger.LogError(
                $"STRESS TRIGGER observer {phase} failed: {ex}");
        }

        private EffectObservation BeginEffectObservation(
            ActorInstance performerActor,
            ActorInstance targetActor)
        {
            if (GameModeMgr.CurrentMode != GameModeType.COMBAT)
            {
                return null;
            }

            EffectObservation observation =
                new EffectObservation();

            if (targetActor != null)
            {
                observation.TargetBefore =
                    CaptureStructuralEffectSnapshot(targetActor);
            }

            if (performerActor != null &&
                (targetActor == null ||
                 performerActor.ActorGuid != targetActor.ActorGuid))
            {
                observation.PerformerBefore =
                    CaptureStructuralEffectSnapshot(performerActor);
            }

            return observation;
        }

        private void EndEffectObservation(
            EffectInstance effect,
            ActorInstance performerActor,
            ActorInstance targetActor,
            EffectObservation observation)
        {
            JObject targetAfter =
                targetActor == null
                    ? null
                    : CaptureStructuralEffectSnapshot(targetActor);

            JObject performerAfter = null;

            if (performerActor != null &&
                (targetActor == null ||
                 performerActor.ActorGuid != targetActor.ActorGuid))
            {
                performerAfter =
                    CaptureStructuralEffectSnapshot(performerActor);
            }

            uint performerGuid =
                performerActor == null
                    ? 0u
                    : performerActor.ActorGuid;

            uint effectTargetGuid =
                targetActor == null
                    ? 0u
                    : targetActor.ActorGuid;

            // The hook is intentionally generic; v7 only exports the
            // user-visible DoT adapter. Other structural differences are
            // logged so we can add vetted consequence schemas one by one.
            EmitDotDiff(
                effect,
                performerGuid,
                effectTargetGuid,
                observation.TargetBefore,
                targetAfter);

            EmitDotDiff(
                effect,
                performerGuid,
                effectTargetGuid,
                observation.PerformerBefore,
                performerAfter);

            EmitTokenDiff(
                effect,
                performerGuid,
                effectTargetGuid,
                observation.TargetBefore,
                targetAfter);

            EmitTokenDiff(
                effect,
                performerGuid,
                effectTargetGuid,
                observation.PerformerBefore,
                performerAfter);

            EmitBuffDiff(
                effect,
                performerGuid,
                effectTargetGuid,
                observation.TargetBefore,
                targetAfter);

            EmitBuffDiff(
                effect,
                performerGuid,
                effectTargetGuid,
                observation.PerformerBefore,
                performerAfter);

            EmitQuirkDiff(
                effect,
                performerGuid,
                effectTargetGuid,
                observation.TargetBefore,
                targetAfter);

            EmitQuirkDiff(
                effect,
                performerGuid,
                effectTargetGuid,
                observation.PerformerBefore,
                performerAfter);

            LogOtherStructuralEffectChanges(
                effect,
                observation.TargetBefore,
                targetAfter);

            LogOtherStructuralEffectChanges(
                effect,
                observation.PerformerBefore,
                performerAfter);
        }

        private static JObject CaptureStructuralEffectSnapshot(
            ActorInstance actor)
        {
            if (actor == null)
            {
                return null;
            }

            // Serializing the whole ActorInstance for every EffectInstance
            // would be needlessly expensive. The containers already expose
            // the same SaveToJson contract used by ActorInstance.SaveToJson,
            // so capture only the structural state this observer needs.
            JObject containers = new JObject();

            foreach (var container in actor.Containers)
            {
                container.SaveToJson(containers);
            }

            return new JObject
            {
                ["guid"] = actor.ActorGuid,
                ["DotContainer"] =
                    CloneOrEmptyArray(containers["DotContainer"]),
                ["TokenContainer"] =
                    CloneOrEmptyArray(containers["TokenContainer"]),
                ["NormalizedTokens"] =
                    CaptureNormalizedTokens(actor),
                ["BuffContainer"] =
                    CloneOrEmptyArray(containers["BuffContainer"]),
                ["NormalizedBuffs"] =
                    CaptureNormalizedBuffs(actor),
                ["QuirkContainer"] =
                    CloneOrEmptyArray(containers["QuirkContainer"]),
                ["NormalizedQuirks"] =
                    CaptureNormalizedQuirks(actor),
                ["m_TeamPosition"] =
                    actor.GetIsTeamPositionSet()
                        ? (JToken)new JValue(actor.TeamPosition)
                        : JValue.CreateNull(),
                ["m_FrontRank"] =
                    new JValue(actor.GetFrontRank()),
                ["actor_id"] =
                    actor.ActorDataId ?? "",
                ["actor_mode"] =
                    actor.ActorDataMode == null
                        ? JValue.CreateNull()
                        : (JToken)new JValue(actor.ActorDataMode.Id)
            };
        }

        private static JArray CaptureNormalizedTokens(
            ActorInstance actor)
        {
            JArray result = new JArray();

            if (actor == null)
            {
                return result;
            }

            foreach (var container in actor.Containers)
            {
                TokenContainer tokenContainer = container as TokenContainer;

                if (tokenContainer == null)
                {
                    continue;
                }

                foreach (TokenInstance token in tokenContainer.GetInstances())
                {
                    if (token == null || token.Definition == null)
                    {
                        continue;
                    }

                    result.Add(
                        new JObject
                        {
                            ["token_id"] = token.Definition.Id ?? "",
                            ["source_type"] =
                                token.SourceType == null
                                    ? ""
                                    : token.SourceType.GetName(),
                            ["source_id"] = token.SourceId ?? "",
                            ["source_actor_guid"] = token.SourceActorGuid,
                            ["duration"] = token.GetDurationAmount(),
                            ["locked_team_position"] =
                                token.LockedTeamPosition,
                            ["is_hidden"] = token.IsHidden
                        });
                }

                // An ActorInstance has one TokenContainer.
                break;
            }

            return result;
        }

        private void EmitTokenDiff(
            EffectInstance effect,
            uint performerGuid,
            uint effectTargetGuid,
            JObject before,
            JObject after)
        {
            if (before == null || after == null)
            {
                return;
            }

            uint actorGuid =
                after["guid"] == null
                    ? 0u
                    : after["guid"].Value<uint>();

            JArray beforeTokens =
                before["NormalizedTokens"] as JArray ?? new JArray();

            JArray afterTokens =
                after["NormalizedTokens"] as JArray ?? new JArray();

            Dictionary<string, List<JObject>> beforeGroups =
                GroupTokenInstances(beforeTokens);

            Dictionary<string, List<JObject>> afterGroups =
                GroupTokenInstances(afterTokens);

            HashSet<string> keys =
                new HashSet<string>(beforeGroups.Keys);

            foreach (string key in afterGroups.Keys)
            {
                keys.Add(key);
            }

            foreach (string key in keys)
            {
                List<JObject> beforeItems;
                List<JObject> afterItems;

                if (!beforeGroups.TryGetValue(key, out beforeItems))
                {
                    beforeItems = new List<JObject>();
                }

                if (!afterGroups.TryGetValue(key, out afterItems))
                {
                    afterItems = new List<JObject>();
                }

                int paired =
                    System.Math.Min(
                        beforeItems.Count,
                        afterItems.Count);

                for (int i = 0; i < paired; i++)
                {
                    if (!JToken.DeepEquals(
                        beforeItems[i],
                        afterItems[i]))
                    {
                        QueueOutgoingMessage(
                            BuildTokenEffectEvent(
                                "token_changed",
                                effect,
                                performerGuid,
                                effectTargetGuid,
                                actorGuid,
                                afterItems[i],
                                beforeItems[i]));
                    }
                }

                for (int i = paired;
                    i < beforeItems.Count;
                    i++)
                {
                    QueueOutgoingMessage(
                        BuildTokenEffectEvent(
                            "token_removed",
                            effect,
                            performerGuid,
                            effectTargetGuid,
                            actorGuid,
                            beforeItems[i],
                            null));
                }

                for (int i = paired;
                    i < afterItems.Count;
                    i++)
                {
                    QueueOutgoingMessage(
                        BuildTokenEffectEvent(
                            "token_added",
                            effect,
                            performerGuid,
                            effectTargetGuid,
                            actorGuid,
                            afterItems[i],
                            null));
                }
            }
        }

        private static Dictionary<string, List<JObject>>
            GroupTokenInstances(JArray tokens)
        {
            Dictionary<string, List<JObject>> groups =
                new Dictionary<string, List<JObject>>();

            foreach (JToken tokenValue in tokens)
            {
                JObject token = tokenValue as JObject;

                if (token == null)
                {
                    continue;
                }

                string key = BuildTokenIdentity(token);

                List<JObject> group;

                if (!groups.TryGetValue(key, out group))
                {
                    group = new List<JObject>();
                    groups[key] = group;
                }

                group.Add((JObject)token.DeepClone());
            }

            foreach (List<JObject> group in groups.Values)
            {
                // Duration is deliberately outside the identity. Sorting the
                // complete normalized snapshot gives deterministic pairing when
                // multiple instances share the same provenance.
                group.Sort(
                    (left, right) =>
                        string.CompareOrdinal(
                            left.ToString(
                                Newtonsoft.Json.Formatting.None),
                            right.ToString(
                                Newtonsoft.Json.Formatting.None)));
            }

            return groups;
        }

        private static string BuildTokenIdentity(JObject token)
        {
            return string.Join(
                "\u001f",
                new string[]
                {
                    CompactJson(token["token_id"]),
                    CompactJson(token["source_type"]),
                    CompactJson(token["source_id"]),
                    CompactJson(token["source_actor_guid"]),
                    CompactJson(token["locked_team_position"]),
                    CompactJson(token["is_hidden"])
                });
        }

        private JObject BuildTokenEffectEvent(
            string eventName,
            EffectInstance effect,
            uint performerGuid,
            uint effectTargetGuid,
            uint actorGuid,
            JObject token,
            JObject previousToken)
        {
            JObject message = new JObject
            {
                ["type"] = "event",
                ["event"] = eventName,
                ["performer_guid"] = performerGuid,
                ["effect_target_guid"] = effectTargetGuid,
                ["target_guid"] = actorGuid,
                ["effect_id"] =
                    effect?.EffectDefinition?.m_Id ?? "",
                ["token_id"] =
                    CloneOrNull(token["token_id"]),
                ["source_type"] =
                    CloneOrNull(token["source_type"]),
                ["source_id"] =
                    CloneOrNull(token["source_id"]),
                ["source_actor_guid"] =
                    CloneOrNull(token["source_actor_guid"]),
                ["duration"] =
                    CloneOrNull(token["duration"]),
                ["locked_team_position"] =
                    CloneOrNull(token["locked_team_position"]),
                ["is_hidden"] =
                    CloneOrNull(token["is_hidden"])
            };

            if (previousToken != null)
            {
                message["previous_token"] =
                    previousToken.DeepClone();

                message["previous_duration"] =
                    CloneOrNull(previousToken["duration"]);
            }

            if (eventName == "token_removed")
            {
                // source_* describes the provenance of the removed token
                // instance. The cause of removal is not available from a pure
                // before/after EffectInstance.Apply observation.
                message["removal_source_known"] = false;
            }

            Logger.LogInfo(
                $"EFFECT {eventName}: effect={message["effect_id"]}, " +
                $"performer={performerGuid}, actor={actorGuid}, " +
                $"token={message["token_id"]}, " +
                $"source={message["source_type"]}/{message["source_id"]}, " +
                $"sourceActor={message["source_actor_guid"]}");

            return message;
        }

        private static JArray CaptureNormalizedBuffs(
            ActorInstance actor)
        {
            JArray result = new JArray();

            if (actor == null)
            {
                return result;
            }

            foreach (var container in actor.Containers)
            {
                BuffContainer buffContainer = container as BuffContainer;

                if (buffContainer == null)
                {
                    continue;
                }

                foreach (BuffInstance buff in buffContainer.GetInstances())
                {
                    if (buff == null ||
                        buff.Definition == null ||
                        !buff.Definition.m_IsVisible)
                    {
                        continue;
                    }

                    string description = "";

                    try
                    {
                        description =
                            BuffDescription.GetDescription(
                                new List<BuffInstance> { buff },
                                separateSources: true,
                                buffActorGuid: actor.ActorGuid);

                        if (string.IsNullOrEmpty(description))
                        {
                            description =
                                buff.Definition.GetContentDescription() ?? "";
                        }
                    }
                    catch
                    {
                        // Description is convenience text. A localization or
                        // formatter failure must never suppress the structural
                        // buff consequence itself.
                        description = "";
                    }

                    result.Add(
                        new JObject
                        {
                            ["buff_id"] = buff.Definition.Id ?? "",
                            ["description"] = description,
                            ["source_type"] =
                                buff.SourceType == null
                                    ? ""
                                    : buff.SourceType.GetName(),
                            ["source_id"] = buff.SourceId ?? "",
                            ["source_actor_guid"] = buff.SourceActorGuid,
                            ["duration"] = buff.GetDurationAmount(),
                            ["locked_team_position"] =
                                buff.LockedTeamPosition,
                            ["is_buff"] =
                                buff.Definition.IsEligibleToShowAsBuffCombatUi,
                            ["is_debuff"] =
                                buff.Definition.IsEligibleToShowAsDebuffCombatUi,
                            ["is_character_sheet_condition"] =
                                buff.IsCharacterSheetCondition
                        });
                }

                // An ActorInstance has one BuffContainer.
                break;
            }

            return result;
        }

        private void EmitBuffDiff(
            EffectInstance effect,
            uint performerGuid,
            uint effectTargetGuid,
            JObject before,
            JObject after)
        {
            if (before == null || after == null)
            {
                return;
            }

            uint actorGuid =
                after["guid"] == null
                    ? 0u
                    : after["guid"].Value<uint>();

            JArray beforeBuffs =
                before["NormalizedBuffs"] as JArray ?? new JArray();

            JArray afterBuffs =
                after["NormalizedBuffs"] as JArray ?? new JArray();

            Dictionary<string, List<JObject>> beforeGroups =
                GroupBuffInstances(beforeBuffs);

            Dictionary<string, List<JObject>> afterGroups =
                GroupBuffInstances(afterBuffs);

            HashSet<string> keys =
                new HashSet<string>(beforeGroups.Keys);

            foreach (string key in afterGroups.Keys)
            {
                keys.Add(key);
            }

            foreach (string key in keys)
            {
                List<JObject> beforeItems;
                List<JObject> afterItems;

                if (!beforeGroups.TryGetValue(key, out beforeItems))
                {
                    beforeItems = new List<JObject>();
                }

                if (!afterGroups.TryGetValue(key, out afterItems))
                {
                    afterItems = new List<JObject>();
                }

                int paired =
                    System.Math.Min(
                        beforeItems.Count,
                        afterItems.Count);

                for (int i = 0; i < paired; i++)
                {
                    if (!JToken.DeepEquals(
                        beforeItems[i],
                        afterItems[i]))
                    {
                        QueueOutgoingMessage(
                            BuildBuffEffectEvent(
                                "buff_changed",
                                effect,
                                performerGuid,
                                effectTargetGuid,
                                actorGuid,
                                afterItems[i],
                                beforeItems[i]));
                    }
                }

                for (int i = paired;
                    i < beforeItems.Count;
                    i++)
                {
                    QueueOutgoingMessage(
                        BuildBuffEffectEvent(
                            "buff_removed",
                            effect,
                            performerGuid,
                            effectTargetGuid,
                            actorGuid,
                            beforeItems[i],
                            null));
                }

                for (int i = paired;
                    i < afterItems.Count;
                    i++)
                {
                    QueueOutgoingMessage(
                        BuildBuffEffectEvent(
                            "buff_added",
                            effect,
                            performerGuid,
                            effectTargetGuid,
                            actorGuid,
                            afterItems[i],
                            null));
                }
            }
        }

        private static Dictionary<string, List<JObject>>
            GroupBuffInstances(JArray buffs)
        {
            Dictionary<string, List<JObject>> groups =
                new Dictionary<string, List<JObject>>();

            foreach (JToken buffValue in buffs)
            {
                JObject buff = buffValue as JObject;

                if (buff == null)
                {
                    continue;
                }

                string key = BuildBuffIdentity(buff);

                List<JObject> group;

                if (!groups.TryGetValue(key, out group))
                {
                    group = new List<JObject>();
                    groups[key] = group;
                }

                group.Add((JObject)buff.DeepClone());
            }

            foreach (List<JObject> group in groups.Values)
            {
                // Duration and description are deliberately outside identity.
                // This keeps an extension/refresh paired with the same native
                // buff instance provenance and reports it as buff_changed.
                group.Sort(
                    (left, right) =>
                        string.CompareOrdinal(
                            left.ToString(
                                Newtonsoft.Json.Formatting.None),
                            right.ToString(
                                Newtonsoft.Json.Formatting.None)));
            }

            return groups;
        }

        private static string BuildBuffIdentity(JObject buff)
        {
            return string.Join(
                "\u001f",
                new string[]
                {
                    CompactJson(buff["buff_id"]),
                    CompactJson(buff["source_type"]),
                    CompactJson(buff["source_id"]),
                    CompactJson(buff["source_actor_guid"]),
                    CompactJson(buff["locked_team_position"]),
                    CompactJson(buff["is_buff"]),
                    CompactJson(buff["is_debuff"]),
                    CompactJson(buff["is_character_sheet_condition"])
                });
        }

        private JObject BuildBuffEffectEvent(
            string eventName,
            EffectInstance effect,
            uint performerGuid,
            uint effectTargetGuid,
            uint actorGuid,
            JObject buff,
            JObject previousBuff)
        {
            JObject message = new JObject
            {
                ["type"] = "event",
                ["event"] = eventName,
                ["performer_guid"] = performerGuid,
                ["effect_target_guid"] = effectTargetGuid,
                ["target_guid"] = actorGuid,
                ["effect_id"] =
                    effect?.EffectDefinition?.m_Id ?? "",
                ["buff_id"] =
                    CloneOrNull(buff["buff_id"]),
                ["description"] =
                    CloneOrNull(buff["description"]),
                ["source_type"] =
                    CloneOrNull(buff["source_type"]),
                ["source_id"] =
                    CloneOrNull(buff["source_id"]),
                ["source_actor_guid"] =
                    CloneOrNull(buff["source_actor_guid"]),
                ["duration"] =
                    CloneOrNull(buff["duration"]),
                ["locked_team_position"] =
                    CloneOrNull(buff["locked_team_position"]),
                ["is_buff"] =
                    CloneOrNull(buff["is_buff"]),
                ["is_debuff"] =
                    CloneOrNull(buff["is_debuff"]),
                ["is_character_sheet_condition"] =
                    CloneOrNull(buff["is_character_sheet_condition"])
            };

            if (previousBuff != null)
            {
                message["previous_buff"] =
                    previousBuff.DeepClone();

                message["previous_duration"] =
                    CloneOrNull(previousBuff["duration"]);

                message["previous_description"] =
                    CloneOrNull(previousBuff["description"]);
            }

            if (eventName == "buff_removed")
            {
                // source_* is provenance of the removed buff instance. The
                // cause of removal is not available from this before/after
                // EffectInstance.Apply observation.
                message["removal_source_known"] = false;
            }

            Logger.LogInfo(
                $"EFFECT {eventName}: effect={message["effect_id"]}, " +
                $"performer={performerGuid}, actor={actorGuid}, " +
                $"buff={message["buff_id"]}, " +
                $"source={message["source_type"]}/{message["source_id"]}, " +
                $"sourceActor={message["source_actor_guid"]}");

            return message;
        }

        private static JArray CaptureNormalizedQuirks(
            ActorInstance actor)
        {
            JArray result = new JArray();

            if (actor == null || actor.QuirkContainer == null)
            {
                return result;
            }

            foreach (QuirkInstance quirk
                in actor.QuirkContainer.GetInstances())
            {
                if (quirk == null || quirk.Definition == null)
                {
                    continue;
                }

                string name = "";
                string description = "";

                try
                {
                    name =
                        QuirkDescription.GetNameString(
                            quirk.Definition,
                            actor,
                            appendRareIcon: false) ?? "";
                }
                catch
                {
                    name = "";
                }

                try
                {
                    description =
                        QuirkDescription.GetDescriptionString(
                            quirk.Definition,
                            actor) ?? "";
                }
                catch
                {
                    description = "";
                }

                result.Add(
                    new JObject
                    {
                        ["quirk_id"] = quirk.Definition.Id ?? "",
                        ["name"] = name,
                        ["description"] = description,
                        ["source_type"] =
                            quirk.SourceType == null
                                ? ""
                                : quirk.SourceType.GetName(),
                        ["source_id"] = quirk.SourceId ?? "",
                        ["source_actor_guid"] =
                            quirk.SourceActorGuid,
                        ["add_group"] = quirk.GetAddGroup(),
                        ["is_locked"] = quirk.IsLocked(),
                        ["is_new"] = quirk.IsNew(),
                        ["is_disease"] = quirk.Definition.IsDisease,
                        ["is_negative"] = quirk.Definition.IsNegative,
                        ["is_curse"] = quirk.Definition.IsCurse,
                        ["kind"] =
                            quirk.Definition.IsDisease
                                ? "disease"
                                : (
                                    quirk.Definition.IsCurse
                                        ? "curse"
                                        : (
                                            quirk.Definition.IsNegative
                                                ? "negative"
                                                : "positive"
                                        )
                                )
                    });
            }

            return result;
        }

        private void EmitQuirkDiff(
            EffectInstance effect,
            uint performerGuid,
            uint effectTargetGuid,
            JObject before,
            JObject after)
        {
            if (before == null || after == null)
            {
                return;
            }

            uint actorGuid =
                after["guid"] == null
                    ? 0u
                    : after["guid"].Value<uint>();

            // ApplyPendingOverstress owns quirk changes for the affected actor
            // and emits them inside the aggregate overstress consequence list.
            if (_activeOverstressObservation != null &&
                _activeOverstressObservation.ActorGuid == actorGuid)
            {
                return;
            }

            JArray beforeQuirks =
                before["NormalizedQuirks"] as JArray ?? new JArray();

            JArray afterQuirks =
                after["NormalizedQuirks"] as JArray ?? new JArray();

            Dictionary<string, List<JObject>> beforeGroups =
                GroupQuirkInstances(beforeQuirks);

            Dictionary<string, List<JObject>> afterGroups =
                GroupQuirkInstances(afterQuirks);

            HashSet<string> keys =
                new HashSet<string>(beforeGroups.Keys);

            foreach (string key in afterGroups.Keys)
            {
                keys.Add(key);
            }

            foreach (string key in keys)
            {
                List<JObject> beforeItems;
                List<JObject> afterItems;

                if (!beforeGroups.TryGetValue(key, out beforeItems))
                {
                    beforeItems = new List<JObject>();
                }

                if (!afterGroups.TryGetValue(key, out afterItems))
                {
                    afterItems = new List<JObject>();
                }

                int paired =
                    System.Math.Min(
                        beforeItems.Count,
                        afterItems.Count);

                for (int i = 0; i < paired; i++)
                {
                    if (!JToken.DeepEquals(
                        beforeItems[i],
                        afterItems[i]))
                    {
                        QueueOutgoingMessage(
                            BuildQuirkEffectEvent(
                                "quirk_changed",
                                effect,
                                performerGuid,
                                effectTargetGuid,
                                actorGuid,
                                afterItems[i],
                                beforeItems[i]));
                    }
                }

                for (int i = paired;
                    i < beforeItems.Count;
                    i++)
                {
                    QueueOutgoingMessage(
                        BuildQuirkEffectEvent(
                            "quirk_removed",
                            effect,
                            performerGuid,
                            effectTargetGuid,
                            actorGuid,
                            beforeItems[i],
                            null));
                }

                for (int i = paired;
                    i < afterItems.Count;
                    i++)
                {
                    QueueOutgoingMessage(
                        BuildQuirkEffectEvent(
                            "quirk_added",
                            effect,
                            performerGuid,
                            effectTargetGuid,
                            actorGuid,
                            afterItems[i],
                            null));
                }
            }
        }

        private static Dictionary<string, List<JObject>>
            GroupQuirkInstances(JArray quirks)
        {
            Dictionary<string, List<JObject>> groups =
                new Dictionary<string, List<JObject>>();

            foreach (JToken quirkValue in quirks)
            {
                JObject quirk = quirkValue as JObject;

                if (quirk == null)
                {
                    continue;
                }

                string key = BuildQuirkIdentity(quirk);

                List<JObject> group;

                if (!groups.TryGetValue(key, out group))
                {
                    group = new List<JObject>();
                    groups[key] = group;
                }

                group.Add((JObject)quirk.DeepClone());
            }

            foreach (List<JObject> group in groups.Values)
            {
                group.Sort(
                    (left, right) =>
                        string.CompareOrdinal(
                            left.ToString(
                                Newtonsoft.Json.Formatting.None),
                            right.ToString(
                                Newtonsoft.Json.Formatting.None)));
            }

            return groups;
        }

        private static string BuildQuirkIdentity(JObject quirk)
        {
            return string.Join(
                "\u001f",
                new string[]
                {
                    CompactJson(quirk["quirk_id"]),
                    CompactJson(quirk["source_type"]),
                    CompactJson(quirk["source_id"]),
                    CompactJson(quirk["source_actor_guid"]),
                    CompactJson(quirk["add_group"])
                });
        }

        private static JObject BuildQuirkConsequence(
            string eventName,
            JObject quirk,
            JObject previousQuirk)
        {
            JObject consequence = new JObject
            {
                ["type"] = eventName,
                ["quirk_id"] =
                    CloneOrNull(quirk["quirk_id"]),
                ["name"] =
                    CloneOrNull(quirk["name"]),
                ["description"] =
                    CloneOrNull(quirk["description"]),
                ["source_type"] =
                    CloneOrNull(quirk["source_type"]),
                ["source_id"] =
                    CloneOrNull(quirk["source_id"]),
                ["source_actor_guid"] =
                    CloneOrNull(quirk["source_actor_guid"]),
                ["add_group"] =
                    CloneOrNull(quirk["add_group"]),
                ["is_locked"] =
                    CloneOrNull(quirk["is_locked"]),
                ["is_new"] =
                    CloneOrNull(quirk["is_new"]),
                ["is_disease"] =
                    CloneOrNull(quirk["is_disease"]),
                ["is_negative"] =
                    CloneOrNull(quirk["is_negative"]),
                ["is_curse"] =
                    CloneOrNull(quirk["is_curse"]),
                ["kind"] =
                    CloneOrNull(quirk["kind"])
            };

            if (previousQuirk != null)
            {
                consequence["previous_quirk"] =
                    previousQuirk.DeepClone();
            }

            if (eventName == "quirk_removed")
            {
                consequence["removal_source_known"] = false;
            }

            return consequence;
        }

        private JObject BuildQuirkEffectEvent(
            string eventName,
            EffectInstance effect,
            uint performerGuid,
            uint effectTargetGuid,
            uint actorGuid,
            JObject quirk,
            JObject previousQuirk)
        {
            JObject message =
                BuildQuirkConsequence(
                    eventName,
                    quirk,
                    previousQuirk);

            message["type"] = "event";
            message["event"] = eventName;
            message["performer_guid"] = performerGuid;
            message["effect_target_guid"] = effectTargetGuid;
            message["target_guid"] = actorGuid;
            message["effect_id"] =
                effect?.EffectDefinition?.m_Id ?? "";

            Logger.LogInfo(
                $"EFFECT {eventName}: effect={message["effect_id"]}, " +
                $"performer={performerGuid}, actor={actorGuid}, " +
                $"quirk={message["quirk_id"]}, " +
                $"source={message["source_type"]}/{message["source_id"]}, " +
                $"sourceActor={message["source_actor_guid"]}");

            return message;
        }

        private static JToken CloneOrNull(JToken token)
        {
            return token == null
                ? JValue.CreateNull()
                : token.DeepClone();
        }

        private static JArray CloneOrEmptyArray(JToken token)
        {
            JArray array = token as JArray;

            return array == null
                ? new JArray()
                : (JArray)array.DeepClone();
        }

        private void EmitDotDiff(
            EffectInstance effect,
            uint performerGuid,
            uint effectTargetGuid,
            JObject before,
            JObject after)
        {
            if (before == null || after == null)
            {
                return;
            }

            uint actorGuid =
                after["guid"] == null
                    ? 0u
                    : after["guid"].Value<uint>();

            JArray beforeDots =
                before["DotContainer"] as JArray ?? new JArray();

            JArray afterDots =
                after["DotContainer"] as JArray ?? new JArray();

            Dictionary<string, List<JObject>> beforeGroups =
                GroupDotInstances(beforeDots);

            Dictionary<string, List<JObject>> afterGroups =
                GroupDotInstances(afterDots);

            HashSet<string> keys =
                new HashSet<string>(beforeGroups.Keys);

            foreach (string key in afterGroups.Keys)
            {
                keys.Add(key);
            }

            foreach (string key in keys)
            {
                List<JObject> beforeItems;
                List<JObject> afterItems;

                if (!beforeGroups.TryGetValue(key, out beforeItems))
                {
                    beforeItems = new List<JObject>();
                }

                if (!afterGroups.TryGetValue(key, out afterItems))
                {
                    afterItems = new List<JObject>();
                }

                int paired =
                    System.Math.Min(
                        beforeItems.Count,
                        afterItems.Count);

                for (int i = 0; i < paired; i++)
                {
                    if (!JToken.DeepEquals(
                        beforeItems[i],
                        afterItems[i]))
                    {
                        QueueOutgoingMessage(
                            BuildDotEffectEvent(
                                "dot_changed",
                                effect,
                                performerGuid,
                                effectTargetGuid,
                                actorGuid,
                                afterItems[i],
                                beforeItems[i]));
                    }
                }

                for (int i = paired;
                    i < beforeItems.Count;
                    i++)
                {
                    QueueOutgoingMessage(
                        BuildDotEffectEvent(
                            "dot_removed",
                            effect,
                            performerGuid,
                            effectTargetGuid,
                            actorGuid,
                            beforeItems[i],
                            null));
                }

                for (int i = paired;
                    i < afterItems.Count;
                    i++)
                {
                    QueueOutgoingMessage(
                        BuildDotEffectEvent(
                            "dot_added",
                            effect,
                            performerGuid,
                            effectTargetGuid,
                            actorGuid,
                            afterItems[i],
                            null));
                }
            }
        }

        private static Dictionary<string, List<JObject>>
            GroupDotInstances(JArray dots)
        {
            Dictionary<string, List<JObject>> groups =
                new Dictionary<string, List<JObject>>();

            foreach (JToken token in dots)
            {
                JObject dot = token as JObject;

                if (dot == null)
                {
                    continue;
                }

                string key = BuildDotIdentity(dot);

                List<JObject> group;

                if (!groups.TryGetValue(key, out group))
                {
                    group = new List<JObject>();
                    groups[key] = group;
                }

                group.Add((JObject)dot.DeepClone());
            }

            foreach (List<JObject> group in groups.Values)
            {
                group.Sort(
                    (left, right) =>
                        string.CompareOrdinal(
                            left.ToString(
                                Newtonsoft.Json.Formatting.None),
                            right.ToString(
                                Newtonsoft.Json.Formatting.None)));
            }

            return groups;
        }

        private static string BuildDotIdentity(JObject dot)
        {
            return string.Join(
                "\u001f",
                new string[]
                {
                    CompactJson(dot["m_Definition"]),
                    CompactJson(dot["m_SourceType"]),
                    CompactJson(dot["m_SourceId"]),
                    CompactJson(dot["m_SourceActorGuid"]),
                    CompactJson(dot["m_LockedTeamPosition"]),
                    CompactJson(dot["m_EffectValueChange"]),
                    CompactJson(dot["m_EffectValueMultiplier"])
                });
        }

        private static string CompactJson(JToken token)
        {
            return token == null
                ? ""
                : token.ToString(
                    Newtonsoft.Json.Formatting.None);
        }

        private JObject BuildDotEffectEvent(
            string eventName,
            EffectInstance effect,
            uint performerGuid,
            uint effectTargetGuid,
            uint actorGuid,
            JObject dot,
            JObject previousDot)
        {
            JObject message = new JObject
            {
                ["type"] = "event",
                ["event"] = eventName,
                ["performer_guid"] = performerGuid,
                ["effect_target_guid"] = effectTargetGuid,
                ["target_guid"] = actorGuid,
                ["effect_id"] =
                    effect?.EffectDefinition?.m_Id ?? "",
                ["source_type"] =
                    effect?.SourceType == null
                        ? ""
                        : effect.SourceType.GetName(),
                ["source_id"] =
                    effect?.SourceId ?? "",
                ["dot"] = dot.DeepClone()
            };

            JToken dotId = dot["m_Definition"];
            JToken duration = dot["m_DurationAmountRemaining"];

            if (dotId != null)
            {
                message["dot_id"] = dotId.DeepClone();
            }

            if (duration != null)
            {
                message["duration"] = duration.DeepClone();
            }

            if (previousDot != null)
            {
                message["previous_dot"] =
                    previousDot.DeepClone();

                JToken previousDuration =
                    previousDot["m_DurationAmountRemaining"];

                if (previousDuration != null)
                {
                    message["previous_duration"] =
                        previousDuration.DeepClone();
                }
            }

            Logger.LogInfo(
                $"EFFECT {eventName}: effect={message["effect_id"]}, " +
                $"source={message["source_type"]}/{message["source_id"]}, " +
                $"performer={performerGuid}, actor={actorGuid}, " +
                $"dot={message["dot_id"]}, duration={message["duration"]}");

            return message;
        }

        private void HandleTeamPositionChange(
            EventTeamPositionChange positionEvent)
        {
            if (positionEvent == null ||
                GameModeMgr.CurrentMode != GameModeType.COMBAT)
            {
                return;
            }

            int delta =
                positionEvent.m_NewTeamPosition -
                positionEvent.m_PreviousTeamPosition;

            JObject message = new JObject
            {
                ["type"] = "event",
                ["event"] = "position_changed",
                ["actor_guid"] = positionEvent.m_ActorGuid,
                ["target_guid"] = positionEvent.m_ActorGuid,
                ["team_index"] = positionEvent.m_TeamIndex,
                ["previous_position"] =
                    positionEvent.m_PreviousTeamPosition,
                ["new_position"] =
                    positionEvent.m_NewTeamPosition,
                ["delta"] = delta,
                ["is_move_effects_valid"] =
                    positionEvent.m_IsMoveEffectsValid,
                ["source_type"] =
                    positionEvent.m_SourceType == null
                        ? ""
                        : positionEvent.m_SourceType.GetName(),
                ["source_actor_guid"] =
                    positionEvent.m_SourceActorGuid
            };

            QueueOutgoingMessage(message);

            Logger.LogInfo(
                $"POSITION change: actor={positionEvent.m_ActorGuid}, " +
                $"team={positionEvent.m_TeamIndex}, " +
                $"{positionEvent.m_PreviousTeamPosition}" +
                $"->{positionEvent.m_NewTeamPosition}, " +
                $"delta={delta}, " +
                $"moveEffects={positionEvent.m_IsMoveEffectsValid}, " +
                $"source={message["source_type"]}, " +
                $"sourceActor={positionEvent.m_SourceActorGuid}");
        }

        private void LogOtherStructuralEffectChanges(
            EffectInstance effect,
            JObject before,
            JObject after)
        {
            if (before == null || after == null)
            {
                return;
            }

            string[] fields = new string[]
            {
                "actor_id",
                "actor_mode"
            };

            List<string> changed = new List<string>();

            foreach (string field in fields)
            {
                if (!JToken.DeepEquals(
                    before[field],
                    after[field]))
                {
                    changed.Add(field);
                }
            }

            if (changed.Count == 0)
            {
                return;
            }

            uint actorGuid =
                after["guid"] == null
                    ? 0u
                    : after["guid"].Value<uint>();

            Logger.LogInfo(
                $"EFFECT structural change: " +
                $"effect={effect?.EffectDefinition?.m_Id ?? ""}, " +
                $"source={effect?.SourceId ?? ""}, " +
                $"actor={actorGuid}, " +
                $"changed={string.Join(",", changed)}");
        }

        private void LogEffectObserverException(
            string phase,
            Exception ex)
        {
            Logger.LogError(
                $"EFFECT observer {phase} failed: {ex}");
        }

        // v9 semantic adapter. EventActorHealthDamage is emitted by
        // ActorInstance.ApplyHealthDamage after DD2 has accepted actual damage.
        // Live v8 samples proved that DoT ticks are identified natively by
        // source_type=dot and m_SourceIds=["blight"/"bleed"/...]. Export those
        // as semantic dot_tick consequences. Keep non-DoT health events as the
        // v8 diagnostic payload until ordinary health_damage moves off the
        // SkillFinalizeResults path in a later, separate change.
        private HealthDamageContext BeginHealthDamageContext(
            IReadOnlyList<uint> sourceActorGuids,
            DeathType deathType,
            SourceType sourceType,
            IReadOnlyList<string> sourceIds)
        {
            if (GameModeMgr.CurrentMode != GameModeType.COMBAT)
            {
                return null;
            }

            HealthDamageContext context =
                new HealthDamageContext
                {
                    Previous = _activeHealthDamageContext,
                    SourceType =
                        sourceType == null
                            ? ""
                            : sourceType.GetName(),
                    DeathType =
                        ((object)deathType) == null
                            ? ""
                            : deathType.ToString()
                };

            if (sourceActorGuids != null)
            {
                foreach (uint actorGuid in sourceActorGuids)
                {
                    context.SourceActorGuids.Add(actorGuid);
                }
            }

            if (sourceIds != null)
            {
                foreach (string sourceId in sourceIds)
                {
                    context.SourceIds.Add(sourceId ?? "");
                }
            }

            _activeHealthDamageContext = context;
            return context;
        }

        private void EndHealthDamageContext(
            HealthDamageContext context)
        {
            if (ReferenceEquals(_activeHealthDamageContext, context))
            {
                _activeHealthDamageContext = context.Previous;
            }
        }

        private EffectHealObservation BeginEffectHealObservation(
            EffectApplyCombinedResult combinedResult)
        {
            if (combinedResult == null ||
                GameModeMgr.CurrentMode != GameModeType.COMBAT)
            {
                return null;
            }

            EffectHealObservation observation =
                new EffectHealObservation
                {
                    Previous = _activeEffectHealObservation
                };

            try
            {
                FieldInfo changeAmountsField =
                    typeof(EffectApplyCombinedResult).GetField(
                        "m_ChangeAmounts",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic);

                IEnumerable changeAmounts =
                    changeAmountsField?.GetValue(combinedResult)
                        as IEnumerable;

                if (changeAmounts != null)
                {
                    foreach (object entry in changeAmounts)
                    {
                        if (entry == null)
                        {
                            continue;
                        }

                        Type entryType = entry.GetType();

                        object key =
                            entryType.GetProperty("Key")?.GetValue(
                                entry,
                                null);

                        object value =
                            entryType.GetProperty("Value")?.GetValue(
                                entry,
                                null);

                        if (key == null || value == null)
                        {
                            continue;
                        }

                        Type keyType = key.GetType();
                        Type valueType = value.GetType();

                        FieldInfo healthField =
                            valueType.GetField(
                                "m_Health",
                                BindingFlags.Instance |
                                BindingFlags.Public |
                                BindingFlags.NonPublic);

                        if (healthField == null)
                        {
                            continue;
                        }

                        float health =
                            Convert.ToSingle(
                                healthField.GetValue(value));

                        if (health <= 0.000001f)
                        {
                            continue;
                        }

                        EffectHealProvenance heal =
                            new EffectHealProvenance
                            {
                                RequestedAmount = health
                            };

                        FieldInfo sourceTypeField =
                            keyType.GetField(
                                "m_SourceType",
                                BindingFlags.Instance |
                                BindingFlags.Public |
                                BindingFlags.NonPublic);

                        SourceType sourceType =
                            sourceTypeField?.GetValue(key)
                                as SourceType;

                        heal.SourceType =
                            sourceType == null
                                ? ""
                                : sourceType.GetName();

                        FieldInfo critField =
                            valueType.GetField(
                                "m_HealthIsPositiveCrit",
                                BindingFlags.Instance |
                                BindingFlags.Public |
                                BindingFlags.NonPublic);

                        if (critField != null)
                        {
                            heal.IsCrit =
                                Convert.ToBoolean(
                                    critField.GetValue(value));
                        }

                        AppendEnumerableFieldToJArray(
                            value,
                            valueType,
                            "m_PerformerActorGuids",
                            heal.SourceActorGuids);

                        AppendEnumerableFieldToJArray(
                            value,
                            valueType,
                            "m_SourceIds",
                            heal.SourceIds);

                        observation.Heals.Add(heal);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    $"HEALTH HEAL effect provenance snapshot incomplete: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }

            _activeEffectHealObservation = observation;
            return observation;
        }

        private void EndEffectHealObservation(
            EffectHealObservation observation)
        {
            if (ReferenceEquals(
                _activeEffectHealObservation,
                observation))
            {
                _activeEffectHealObservation = observation.Previous;
            }
        }

        private static void AppendEnumerableFieldToJArray(
            object instance,
            Type instanceType,
            string fieldName,
            JArray destination)
        {
            FieldInfo field =
                instanceType.GetField(
                    fieldName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic);

            IEnumerable values =
                field?.GetValue(instance) as IEnumerable;

            if (values == null)
            {
                return;
            }

            foreach (object value in values)
            {
                if (value == null)
                {
                    destination.Add(JValue.CreateNull());
                }
                else
                {
                    destination.Add(JToken.FromObject(value));
                }
            }
        }

        private static bool FloatNear(
            float left,
            float right)
        {
            return System.Math.Abs(left - right) <= 0.001f;
        }

        private void HandleNativeHealthHealEvent(
            EventActorHealthHeal healEvent)
        {
            if (healEvent == null ||
                GameModeMgr.CurrentMode != GameModeType.COMBAT)
            {
                return;
            }

            string sourceType =
                healEvent.m_SourceType == null
                    ? ""
                    : healEvent.m_SourceType.GetName();

            EffectHealObservation effectObservation =
                _activeEffectHealObservation;

            if (effectObservation != null &&
                effectObservation.NextIndex <
                    effectObservation.Heals.Count)
            {
                EffectHealProvenance provenance =
                    effectObservation.Heals[
                        effectObservation.NextIndex++];

                JObject effectHeal = new JObject
                {
                    ["type"] = "event",
                    ["event"] = "health_heal",
                    ["target_guid"] = healEvent.m_ActorGuid,
                    ["team_index"] = healEvent.m_TeamIndex,
                    ["amount"] = healEvent.m_HealthChange,
                    ["requested_amount"] = healEvent.m_HealthHeal,
                    ["is_crit"] = healEvent.m_IsCrit,
                    ["source_type"] = sourceType,
                    ["source_ids"] =
                        provenance.SourceIds.DeepClone(),
                    ["source_actor_guids"] =
                        provenance.SourceActorGuids.DeepClone(),
                    ["has_displayed"] = healEvent.m_HasDisplayed,
                    ["provenance"] = "effect_apply_combined_result"
                };

                QueueOutgoingMessage(effectHeal);
                return;
            }

            if (string.Equals(
                sourceType,
                "skill",
                StringComparison.OrdinalIgnoreCase))
            {
                _pendingNativeSkillHeals.Add(
                    new PendingNativeSkillHeal
                    {
                        TargetGuid = healEvent.m_ActorGuid,
                        TeamIndex = healEvent.m_TeamIndex,
                        RequestedAmount = healEvent.m_HealthHeal,
                        ActualAmount = healEvent.m_HealthChange,
                        IsCrit = healEvent.m_IsCrit,
                        SourceType = sourceType,
                        HasDisplayed = healEvent.m_HasDisplayed
                    });

                return;
            }

            QueueOutgoingMessage(
                new JObject
                {
                    ["type"] = "event",
                    ["event"] = "health_heal",
                    ["target_guid"] = healEvent.m_ActorGuid,
                    ["team_index"] = healEvent.m_TeamIndex,
                    ["amount"] = healEvent.m_HealthChange,
                    ["requested_amount"] = healEvent.m_HealthHeal,
                    ["is_crit"] = healEvent.m_IsCrit,
                    ["source_type"] = sourceType,
                    ["has_displayed"] = healEvent.m_HasDisplayed,
                    ["provenance_known"] = false
                });
        }

        private void FlushPendingNativeSkillHeals(
            EventSkillFinalizeResults skillFinalizeResult)
        {
            if (_pendingNativeSkillHeals.Count == 0)
            {
                return;
            }

            bool[] used =
                new bool[_pendingNativeSkillHeals.Count];

            foreach (SkillCalculation.ActorResult actorResult
                in skillFinalizeResult.ActorResults)
            {
                if (!actorResult.IsHealthHeal ||
                    actorResult.HealthHeal <= 0f)
                {
                    continue;
                }

                int matchIndex = -1;

                for (int i = 0;
                    i < _pendingNativeSkillHeals.Count;
                    i++)
                {
                    if (used[i])
                    {
                        continue;
                    }

                    PendingNativeSkillHeal pending =
                        _pendingNativeSkillHeals[i];

                    if (pending.TargetGuid ==
                            actorResult.m_TargetActorGuid &&
                        pending.IsCrit ==
                            actorResult.IsHealthHealCrit &&
                        FloatNear(
                            pending.RequestedAmount,
                            actorResult.HealthHeal))
                    {
                        matchIndex = i;
                        break;
                    }
                }

                if (matchIndex < 0)
                {
                    continue;
                }

                used[matchIndex] = true;

                PendingNativeSkillHeal heal =
                    _pendingNativeSkillHeals[matchIndex];

                QueueOutgoingMessage(
                    new JObject
                    {
                        ["type"] = "event",
                        ["event"] = "health_heal",
                        ["performer_guid"] =
                            actorResult.m_PerformerActorGuid,
                        ["target_guid"] =
                            actorResult.m_TargetActorGuid,
                        ["team_index"] = heal.TeamIndex,
                        ["skill_id"] =
                            actorResult.m_SkillId ?? "",
                        ["amount"] = heal.ActualAmount,
                        ["requested_amount"] =
                            heal.RequestedAmount,
                        ["is_crit"] = heal.IsCrit,
                        ["source_type"] = heal.SourceType,
                        ["source_ids"] =
                            new JArray(
                                actorResult.m_SkillId ?? ""),
                        ["source_actor_guids"] =
                            new JArray(
                                actorResult.m_PerformerActorGuid),
                        ["has_displayed"] =
                            heal.HasDisplayed,
                        ["provenance"] =
                            "skill_actor_result"
                    });
            }

            for (int i = 0;
                i < _pendingNativeSkillHeals.Count;
                i++)
            {
                if (used[i])
                {
                    continue;
                }

                PendingNativeSkillHeal heal =
                    _pendingNativeSkillHeals[i];

                QueueOutgoingMessage(
                    new JObject
                    {
                        ["type"] = "event",
                        ["event"] = "health_heal",
                        ["target_guid"] = heal.TargetGuid,
                        ["team_index"] = heal.TeamIndex,
                        ["amount"] = heal.ActualAmount,
                        ["requested_amount"] =
                            heal.RequestedAmount,
                        ["is_crit"] = heal.IsCrit,
                        ["source_type"] = heal.SourceType,
                        ["has_displayed"] =
                            heal.HasDisplayed,
                        ["provenance_known"] = false
                    });
            }

            _pendingNativeSkillHeals.Clear();
        }

        private void HandleNativeHealthDamageEvent(
            EventActorHealthDamage damageEvent)
        {
            if (damageEvent == null ||
                GameModeMgr.CurrentMode != GameModeType.COMBAT)
            {
                return;
            }

            try
            {
                string sourceType =
                    damageEvent.m_SourceType == null
                        ? ""
                        : damageEvent.m_SourceType.GetName();

                HealthDamageContext context =
                    _activeHealthDamageContext;

                JArray sourceIds = new JArray();

                if (damageEvent.m_SourceIds != null)
                {
                    foreach (string sourceId
                        in damageEvent.m_SourceIds)
                    {
                        sourceIds.Add(sourceId ?? "");
                    }
                }

                JArray sourceActorGuids =
                    context == null
                        ? new JArray()
                        : (JArray)context.SourceActorGuids.DeepClone();

                if (string.Equals(
                    sourceType,
                    "dot",
                    StringComparison.OrdinalIgnoreCase))
                {
                    string dotType =
                        sourceIds.Count > 0
                            ? sourceIds[0].Value<string>() ?? ""
                            : "";

                    JObject dotTick = new JObject
                    {
                        ["type"] = "event",
                        ["event"] = "dot_tick",
                        ["target_guid"] =
                            damageEvent.m_ActorGuid,
                        ["team_index"] =
                            damageEvent.m_TeamIndex,
                        ["dot_type"] = dotType,
                        ["amount"] =
                            damageEvent.m_HealthDamage,
                        ["is_crit"] =
                            damageEvent.m_IsCrit,
                        ["killed"] =
                            damageEvent.IsKill,
                        ["source_type"] = sourceType,
                        ["source_ids"] = sourceIds,
                        ["source_actor_guids"] =
                            sourceActorGuids,
                        ["has_displayed"] =
                            damageEvent.m_HasDisplayed
                    };

                    QueueOutgoingMessage(dotTick);
                    return;
                }

                if (string.Equals(
                        sourceType,
                        "overstress",
                        StringComparison.OrdinalIgnoreCase) &&
                    _activeOverstressObservation != null)
                {
                    return;
                }

                JObject message = new JObject
                {
                    ["type"] = "event",
                    ["event"] = "health_damage",
                    ["target_guid"] =
                        damageEvent.m_ActorGuid,
                    ["team_index"] =
                        damageEvent.m_TeamIndex,
                    ["amount"] =
                        damageEvent.m_HealthDamage,
                    ["is_crit"] =
                        damageEvent.m_IsCrit,
                    ["is_riposte"] =
                        damageEvent.m_IsRiposte,
                    ["entering_deaths_door"] =
                        damageEvent.IsEnteringDeathsDoor,
                    ["killed"] =
                        damageEvent.IsKill,
                    ["source_type"] = sourceType,
                    ["source_ids"] = sourceIds,
                    ["source_actor_guids"] =
                        sourceActorGuids,
                    ["has_displayed"] =
                        damageEvent.m_HasDisplayed
                };

                if (context != null)
                {
                    message["death_type"] =
                        context.DeathType;
                    message["provenance"] =
                        "apply_health_damage";
                }
                else
                {
                    message["provenance_known"] = false;
                }

                QueueOutgoingMessage(message);
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    $"NATIVE health_damage observer failed: {ex}");
            }
        }

        private static JObject CaptureNativeEventDiagnostic(
            object nativeEvent)
        {
            JObject result = new JObject();

            if (nativeEvent == null)
            {
                return result;
            }

            Type eventType = nativeEvent.GetType();
            BindingFlags flags =
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic;

            foreach (FieldInfo field in eventType.GetFields(flags))
            {
                try
                {
                    result[field.Name] =
                        ToDiagnosticJToken(
                            field.GetValue(nativeEvent));
                }
                catch (Exception ex)
                {
                    result[field.Name] =
                        $"<field read failed: {ex.GetType().Name}>";
                }
            }

            foreach (PropertyInfo property
                in eventType.GetProperties(flags))
            {
                if (!property.CanRead ||
                    property.GetIndexParameters().Length != 0 ||
                    result[property.Name] != null)
                {
                    continue;
                }

                try
                {
                    result[property.Name] =
                        ToDiagnosticJToken(
                            property.GetValue(
                                nativeEvent,
                                null));
                }
                catch (Exception ex)
                {
                    result[property.Name] =
                        $"<property read failed: {ex.GetType().Name}>";
                }
            }

            return result;
        }

        private static JToken ToDiagnosticJToken(
            object value,
            int depth = 0)
        {
            if (value == null)
            {
                return JValue.CreateNull();
            }

            if (depth >= 2)
            {
                return new JValue(value.ToString());
            }

            if (value is string || value is char)
            {
                return new JValue(value.ToString());
            }

            if (value is bool ||
                value is byte || value is sbyte ||
                value is short || value is ushort ||
                value is int || value is uint ||
                value is long || value is ulong ||
                value is float || value is double ||
                value is decimal)
            {
                return JToken.FromObject(value);
            }

            Type valueType = value.GetType();

            if (valueType.IsEnum)
            {
                return new JValue(value.ToString());
            }

            IEnumerable enumerable = value as IEnumerable;

            if (enumerable != null)
            {
                JArray array = new JArray();
                int count = 0;

                foreach (object item in enumerable)
                {
                    if (count >= 32)
                    {
                        array.Add("<truncated>");
                        break;
                    }

                    array.Add(
                        ToDiagnosticJToken(
                            item,
                            depth + 1));
                    count++;
                }

                return array;
            }

            try
            {
                MethodInfo getName =
                    valueType.GetMethod(
                        "GetName",
                        BindingFlags.Instance |
                        BindingFlags.Public,
                        null,
                        Type.EmptyTypes,
                        null);

                if (getName != null &&
                    getName.ReturnType == typeof(string))
                {
                    string name =
                        getName.Invoke(value, null) as string;

                    if (name != null)
                    {
                        return new JValue(name);
                    }
                }
            }
            catch
            {
                // Fall through to ToString for diagnostics only.
            }

            return new JValue(value.ToString());
        }
        // [NEW: REAL DECISION BOUNDARY]
        private void HandleEventSkillFinalizeResults(
            EventSkillFinalizeResults skillFinalizeResult)
        {
            JArray targetGuids = new JArray();

            foreach (uint targetGuid in skillFinalizeResult.TargetGuids)
            {
                targetGuids.Add(targetGuid);
            }

            QueueOutgoingMessage(
                new JObject
                {
                    ["type"] = "event",
                    ["event"] = "skill_used",
                    ["performer_guid"] = skillFinalizeResult.PerformerGuid,
                    ["skill_id"] = skillFinalizeResult.SkillId,
                    ["target_guids"] = targetGuids
                });

            FlushPendingNativeSkillHeals(skillFinalizeResult);

            foreach (SkillCalculation.ActorResult actorResult
                in skillFinalizeResult.ActorResults)
            {
                EmitActorResultConsequences(actorResult);
            }
        }

        private void EmitActorResultConsequences(
            SkillCalculation.ActorResult actorResult)
        {
            uint performerGuid = actorResult.m_PerformerActorGuid;
            uint targetGuid = actorResult.m_TargetActorGuid;

            if (actorResult.IsMiss)
            {
                QueueOutgoingMessage(
                    BuildActorResultEvent(
                        "miss",
                        performerGuid,
                        targetGuid,
                        actorResult.m_SkillId));
            }

            if (actorResult.IsDodge)
            {
                QueueOutgoingMessage(
                    BuildActorResultEvent(
                        "dodge",
                        performerGuid,
                        targetGuid,
                        actorResult.m_SkillId));
            }

            if (actorResult.IsCrit)
            {
                QueueOutgoingMessage(
                    BuildActorResultEvent(
                        "crit",
                        performerGuid,
                        targetGuid,
                        actorResult.m_SkillId));
            }

            if (actorResult.IsBlocked)
            {
                QueueOutgoingMessage(
                    BuildActorResultEvent(
                        "blocked",
                        performerGuid,
                        targetGuid,
                        actorResult.m_SkillId));
            }

            float stressDamage = actorResult.StressDamage;

            if (stressDamage > 0f)
            {
                JObject stressDamageEvent =
                    BuildActorResultEvent(
                        "stress_damage",
                        performerGuid,
                        targetGuid,
                        actorResult.m_SkillId);

                stressDamageEvent["amount"] = stressDamage;

                QueueOutgoingMessage(stressDamageEvent);
            }

            float stressHeal = actorResult.StressHeal;

            if (stressHeal > 0f)
            {
                JObject stressHealEvent =
                    BuildActorResultEvent(
                        "stress_heal",
                        performerGuid,
                        targetGuid,
                        actorResult.m_SkillId);

                stressHealEvent["amount"] = stressHeal;

                QueueOutgoingMessage(stressHealEvent);
            }

            if (actorResult.IsEnteringDeathsDoor)
            {
                QueueOutgoingMessage(
                    BuildActorResultEvent(
                        "entered_deaths_door",
                        performerGuid,
                        targetGuid,
                        actorResult.m_SkillId));
            }

            if (actorResult.IsDeathsDoorSurvival)
            {
                QueueOutgoingMessage(
                    BuildActorResultEvent(
                        "deathblow_survived",
                        performerGuid,
                        targetGuid,
                        actorResult.m_SkillId));
            }

            if (actorResult.IsKill)
            {
                JObject killEvent =
                    BuildActorResultEvent(
                        "actor_killed",
                        performerGuid,
                        targetGuid,
                        actorResult.m_SkillId);

                killEvent["damage_kill"] =
                    actorResult.IsDamageKill;

                killEvent["effect_kill"] =
                    actorResult.IsEffectKill;

                QueueOutgoingMessage(killEvent);
            }
        }

        private JObject BuildActorResultEvent(
            string eventName,
            uint performerGuid,
            uint targetGuid,
            string skillId)
        {
            return new JObject
            {
                ["type"] = "event",
                ["event"] = eventName,
                ["performer_guid"] = performerGuid,
                ["target_guid"] = targetGuid,
                ["skill_id"] = skillId
            };
        }
    }
}
