using System;
using System.Collections.Generic;
using System.Reflection;

using Assets.Code.Game;
using Assets.Code.Map.Generation.Row;
using Assets.Code.Map.Generation.Route;
using Assets.Code.UI;

using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        private bool _routeChoiceReadySent;

        private static readonly BindingFlags RouteChoiceInstanceFlags =
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic;

        private static readonly FieldInfo TriggerPreviewIconsField =
            typeof(TriggerIntersectionBhv).GetField(
                "m_PreviewIcons",
                RouteChoiceInstanceFlags);

        private static readonly FieldInfo IndicatorOptionIndexField =
            typeof(RoadIndicatorUIBhv).GetField(
                "m_intersectionOptionIndex",
                RouteChoiceInstanceFlags);

        private static readonly FieldInfo IndicatorDirectionField =
            typeof(RoadIndicatorUIBhv).GetField(
                "m_indicatorDirection",
                RouteChoiceInstanceFlags);

        private static readonly FieldInfo IndicatorRouteDefField =
            typeof(RoadIndicatorUIBhv).GetField(
                "m_routeDef",
                RouteChoiceInstanceFlags);

        private bool ProcessRouteChoiceReady()
        {
            TriggerIntersectionBhv trigger;
            List<RoadIndicatorUIBhv> indicators;

            if (!TryGetStableRouteChoice(out trigger, out indicators))
            {
                _routeChoiceReadySent = false;
                return false;
            }

            if (_routeChoiceReadySent)
            {
                return true;
            }

            JObject ready =
                BuildRouteChoiceReadyMessage(trigger, indicators);

            JArray actions =
                ready["available_actions"] as JArray;

            if (actions == null || actions.Count == 0)
            {
                _routeChoiceReadySent = false;
                return false;
            }

            _routeChoiceReadySent = true;

            Logger.LogInfo(
                $"READY route_choice: choices={actions.Count}");

            QueueOutgoingMessage(ready);
            return true;
        }

        private bool TryProcessRouteChoiceCatchup()
        {
            TriggerIntersectionBhv trigger;
            List<RoadIndicatorUIBhv> indicators;

            if (!TryGetStableRouteChoice(out trigger, out indicators))
            {
                return false;
            }

            JObject ready =
                BuildRouteChoiceReadyMessage(trigger, indicators);

            JArray actions =
                ready["available_actions"] as JArray;

            if (actions == null || actions.Count == 0)
            {
                return false;
            }

            _routeChoiceReadySent = true;

            Logger.LogInfo(
                $"READY catch-up: route_choice choices={actions.Count}");

            QueueOutgoingMessage(ready);
            return true;
        }

        private bool TryGetStableRouteChoice(
            out TriggerIntersectionBhv selectedTrigger,
            out List<RoadIndicatorUIBhv> selectedIndicators)
        {
            selectedTrigger = null;
            selectedIndicators = null;

            if (GameModeMgr.CurrentMode != GameModeType.DRIVING)
            {
                return false;
            }

            GameModeMgr mgr = GameModeMgr.Instance;

            if (mgr == null || mgr.IsChangingState())
            {
                return false;
            }

            if (TriggerPreviewIconsField == null ||
                IndicatorOptionIndexField == null ||
                IndicatorDirectionField == null ||
                IndicatorRouteDefField == null)
            {
                return false;
            }

            TriggerIntersectionBhv[] triggers =
                UnityEngine.Resources
                    .FindObjectsOfTypeAll<TriggerIntersectionBhv>();

            foreach (TriggerIntersectionBhv trigger in triggers)
            {
                if (trigger == null ||
                    !trigger.gameObject.activeInHierarchy ||
                    !trigger.enabled ||
                    trigger.SelectedIntersectionOptionIndex != -1)
                {
                    continue;
                }

                List<RoadIndicatorUIBhv> previewIcons =
                    TriggerPreviewIconsField.GetValue(trigger)
                        as List<RoadIndicatorUIBhv>;

                if (previewIcons == null ||
                    previewIcons.Count < 2 ||
                    previewIcons.Count > 3)
                {
                    continue;
                }

                List<RoadIndicatorUIBhv> active =
                    new List<RoadIndicatorUIBhv>();

                bool invalid = false;

                foreach (RoadIndicatorUIBhv indicator in previewIcons)
                {
                    if (indicator == null ||
                        !indicator.gameObject.activeInHierarchy ||
                        !indicator.IsTriggered())
                    {
                        invalid = true;
                        break;
                    }

                    active.Add(indicator);
                }

                if (invalid || active.Count != previewIcons.Count)
                {
                    continue;
                }

                active.Sort(
                    delegate(
                        RoadIndicatorUIBhv left,
                        RoadIndicatorUIBhv right)
                    {
                        return GetRouteChoiceOptionIndex(left)
                            .CompareTo(
                                GetRouteChoiceOptionIndex(right));
                    });

                selectedTrigger = trigger;
                selectedIndicators = active;
                return true;
            }

            return false;
        }

        private JObject BuildRouteChoiceReadyMessage(
            TriggerIntersectionBhv trigger,
            List<RoadIndicatorUIBhv> indicators)
        {
            JArray options = new JArray();
            JArray actions = new JArray();

            foreach (RoadIndicatorUIBhv indicator in indicators)
            {
                int optionIndex =
                    GetRouteChoiceOptionIndex(indicator);

                string direction =
                    GetRouteChoiceDirection(indicator);

                bool nodeRevealed =
                    indicator.IsRevealed();

                RouteDefinition routeDef =
                    IndicatorRouteDefField.GetValue(indicator)
                        as RouteDefinition;

                bool routeRevealed =
                    routeDef != null;

                JObject option = new JObject
                {
                    ["option_index"] = optionIndex,
                    ["direction"] = direction,
                    ["node_revealed"] = nodeRevealed,
                    ["node_type"] =
                        nodeRevealed
                            ? FormatNodeType(
                                indicator.GetNodeType())
                            : "unknown",
                    ["node_subtype"] =
                        nodeRevealed
                            ? (indicator.GetNodeSubType() ?? "")
                            : "",
                    ["route_revealed"] = routeRevealed,
                    ["route_type"] =
                        routeRevealed &&
                        routeDef.m_RouteType != null
                            ? routeDef.m_RouteType.GetName()
                            : "unknown"
                };

                options.Add(option);

                actions.Add(
                    new JObject
                    {
                        ["action"] = "choose_route",
                        ["option_index"] = optionIndex,
                        ["direction"] = direction
                    });
            }

            actions.Add(
                new JObject
                {
                    ["action"] = "map"
                });

            return new JObject
            {
                ["type"] = "ready",
                ["frontend_state"] = "route_choice",
                ["selected_option_index"] =
                    trigger.SelectedIntersectionOptionIndex,
                ["options"] = options,
                ["available_actions"] = actions
            };
        }

        private static int GetRouteChoiceOptionIndex(
            RoadIndicatorUIBhv indicator)
        {
            if (indicator == null ||
                IndicatorOptionIndexField == null)
            {
                return -1;
            }

            object value =
                IndicatorOptionIndexField.GetValue(indicator);

            return value is int
                ? (int)value
                : -1;
        }

        private static string GetRouteChoiceDirection(
            RoadIndicatorUIBhv indicator)
        {
            if (indicator == null ||
                IndicatorDirectionField == null)
            {
                return "unknown";
            }

            object value =
                IndicatorDirectionField.GetValue(indicator);

            if (value == null)
            {
                return "unknown";
            }

            return value.ToString().ToLowerInvariant();
        }

        private static string FormatNodeType(
            object nodeType)
        {
            if (nodeType == null)
            {
                return "unknown";
            }

            MethodInfo getName =
                nodeType.GetType().GetMethod(
                    "GetName",
                    BindingFlags.Instance |
                    BindingFlags.Public);

            if (getName != null)
            {
                object name =
                    getName.Invoke(
                        nodeType,
                        null);

                if (name != null)
                {
                    return name.ToString();
                }
            }

            return nodeType.ToString();
        }

        private void HandleRouteChoice(
            BridgeRequest request,
            string optionIndexText)
        {
            if (GameModeMgr.CurrentMode != GameModeType.DRIVING)
            {
                request.Response =
                    "ERROR: choose_route is only valid while driving";
                return;
            }

            int optionIndex;

            if (!int.TryParse(
                    optionIndexText,
                    out optionIndex) ||
                optionIndex < 0)
            {
                request.Response =
                    "ERROR: choose_route requires a non-negative option_index";
                return;
            }

            TriggerIntersectionBhv trigger;
            List<RoadIndicatorUIBhv> indicators;

            if (!TryGetStableRouteChoice(
                    out trigger,
                    out indicators))
            {
                request.Response =
                    "ERROR: no stable route choice is active";
                return;
            }

            RoadIndicatorUIBhv selected = null;

            foreach (RoadIndicatorUIBhv indicator in indicators)
            {
                if (GetRouteChoiceOptionIndex(indicator) ==
                    optionIndex)
                {
                    selected = indicator;
                    break;
                }
            }

            if (selected == null)
            {
                request.Response =
                    "ERROR: route option is not currently available";
                return;
            }

            Logger.LogInfo(
                $"ROUTE choose: option={optionIndex}, " +
                $"direction={GetRouteChoiceDirection(selected)}, " +
                $"node=" +
                $"{(selected.IsRevealed() ? FormatNodeType(selected.GetNodeType()) : "unknown")}");

            // This is the semantic seam used by the native hold UI once the
            // hold timer completes. It triggers EventRoadIntersectionOptionSelected
            // and lets TriggerIntersectionBhv advance its own state machine.
            selected.OnClick();

            _routeChoiceReadySent = false;

            // Route selection resumes the already-running driving activity.
            QueueActivityStatus("driving");

            request.Response = "OK";
        }
    }
}
