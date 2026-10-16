using System;
using System.Collections.Generic;
using System.Reflection;

using Assets.Code.Game;
using Assets.Code.Map;
using Assets.Code.Map.Minimap;
using Assets.Code.Utils;

using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        private static readonly BindingFlags MapStateInstanceFlags =
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic;

        private static readonly FieldInfo MinimapIconRowsField =
            typeof(MinimapMgrBhv).GetField(
                "m_iconRows",
                MapStateInstanceFlags);

        private static string FormatMinimapState(object state)
        {
            return state == null
                ? "unknown"
                : state.ToString().ToLowerInvariant();
        }

        private bool TryBuildMapState(out JObject state, out string error)
        {
            state = null;
            error = null;

            if (GameModeMgr.CurrentMode != GameModeType.DRIVING)
            {
                error = "map is only available in driving mode";
                return false;
            }

            GameModeMgr modeMgr = GameModeMgr.Instance;
            if (modeMgr == null || modeMgr.IsChangingState())
            {
                error = "driving mode is transitioning";
                return false;
            }

            MapMgrBhv mapMgr = SingletonMonoBehaviour<MapMgrBhv>.Instance;
            if (mapMgr == null || !mapMgr.IsPlayable())
            {
                error = "map is not currently playable";
                return false;
            }

            Map map = mapMgr.GetMap();
            MinimapMgrBhv minimap = mapMgr.GetMinimapMgr();

            if (map == null || minimap == null || MinimapIconRowsField == null)
            {
                error = "native minimap state is unavailable";
                return false;
            }

            List<List<MinimapRow>> allBiomeRows =
                MinimapIconRowsField.GetValue(minimap)
                    as List<List<MinimapRow>>;

            int biomeIndex = map.GetCurrentBiomeIndex();
            if (allBiomeRows == null ||
                biomeIndex < 0 ||
                biomeIndex >= allBiomeRows.Count)
            {
                error = "current biome minimap rows are unavailable";
                return false;
            }

            List<MinimapRow> rows = allBiomeRows[biomeIndex];
            if (rows == null || rows.Count == 0)
            {
                error = "current biome minimap is empty";
                return false;
            }

            MinimapRow currentRow = null;
            try
            {
                currentRow = minimap.GetCurrentMinimapRow();
            }
            catch
            {
                currentRow = null;
            }

            Dictionary<MinimapIcon, string> nodeIds =
                new Dictionary<MinimapIcon, string>();

            JArray rowPayloads = new JArray();

            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                MinimapRow row = rows[rowIndex];
                IReadOnlyList<MinimapIcon> icons = row.GetMinimapIcons();
                JArray nodes = new JArray();

                for (int nodeIndex = 0; nodeIndex < icons.Count; nodeIndex++)
                {
                    MinimapIcon icon = icons[nodeIndex];
                    string nodeId = $"r{rowIndex}n{nodeIndex}";
                    nodeIds[icon] = nodeId;

                    bool revealed = icon.IsRevealed();
                    string nodeType =
                        revealed && icon.GetNodeType() != null
                            ? icon.GetNodeType().GetName()
                            : "unknown";

                    string nodeSubtype =
                        revealed
                            ? (icon.GetNodeSubType() ?? string.Empty)
                            : string.Empty;

                    JObject node = new JObject
                    {
                        ["id"] = nodeId,
                        ["row"] = rowIndex,
                        ["index"] = nodeIndex,
                        ["revealed"] = revealed,
                        ["node_type"] = nodeType,
                        ["node_subtype"] = nodeSubtype,
                        ["chosen_state"] =
                            FormatMinimapState(icon.GetChosenState()),
                        ["kill_contract"] =
                            revealed && icon.GetHasKillContract()
                    };

                    nodes.Add(node);
                }

                rowPayloads.Add(
                    new JObject
                    {
                        ["row"] = rowIndex,
                        ["current"] = ReferenceEquals(row, currentRow),
                        ["nodes"] = nodes
                    });
            }

            JArray links = new JArray();

            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                MinimapRow row = rows[rowIndex];
                List<MinimapLink> rowLinks = row.GetLinks();

                foreach (MinimapLink link in rowLinks)
                {
                    string fromId;
                    string toId;

                    if (!nodeIds.TryGetValue(link.FromIcon, out fromId) ||
                        !nodeIds.TryGetValue(link.ApproachingIcon, out toId))
                    {
                        continue;
                    }

                    bool revealed = link.IsRevealed();
                    string routeType = "unknown";
                    string routeId = string.Empty;

                    if (revealed && link.RouteDef != null)
                    {
                        routeId = link.RouteDef.m_Id ?? string.Empty;
                        if (link.RouteDef.m_RouteType != null)
                        {
                            routeType = link.RouteDef.m_RouteType.GetName();
                        }
                    }

                    links.Add(
                        new JObject
                        {
                            ["from"] = fromId,
                            ["to"] = toId,
                            ["revealed"] = revealed,
                            ["route_type"] = routeType,
                            ["route_id"] = routeId,
                            ["chosen_state"] =
                                FormatMinimapState(link.GetChosenState())
                        });
                }
            }

            state = new JObject
            {
                ["type"] = "map_state",
                ["current_mode"] = "driving",
                ["biome_index"] = biomeIndex,
                ["biome_type"] =
                    map.GetCurrentBiomeType() != null
                        ? map.GetCurrentBiomeType().GetName()
                        : "unknown",
                ["rows"] = rowPayloads,
                ["links"] = links
            };

            return true;
        }

        private void HandleMapState(BridgeRequest request)
        {
            JObject state;
            string error;

            if (!TryBuildMapState(out state, out error))
            {
                request.Response = "ERROR: " + error;
                return;
            }

            Logger.LogInfo(
                $"MAP state: biome={state["biome_type"]} " +
                $"rows={((JArray)state["rows"]).Count} " +
                $"links={((JArray)state["links"]).Count}");

            QueueOutgoingMessage(state);
            request.Response = "OK";
        }
    }
}
