using System.Collections.Generic;
using UnityEngine;

namespace ShipRobot.Navigation
{
    [DisallowMultipleComponent]
    public sealed class MissionRoutePlanner : MonoBehaviour
    {
        private static readonly PlantNodeId[] PerimeterCycle =
        {
            PlantNodeId.UpperMid,
            PlantNodeId.UpperLeft,
            PlantNodeId.UnderLeft,
            PlantNodeId.UnderMid,
            PlantNodeId.UnderRight,
            PlantNodeId.UpperRight
        };

        [SerializeField] private PlantRouteGraph routeGraph;
        [SerializeField] private PlantNodeId baseNode = PlantNodeId.UnderMid;

        public IReadOnlyList<PlantNodeId> BuildMissionRoute(PlantMission mission, PlantNodeId currentNode)
        {
            switch (mission)
            {
                case PlantMission.PerimeterPatrol:
                    return ConnectCurrentNode(currentNode, new[]
                    {
                        PlantNodeId.UnderMid,
                        PlantNodeId.UnderLeft,
                        PlantNodeId.UpperLeft,
                        PlantNodeId.UpperMid,
                        PlantNodeId.UpperRight,
                        PlantNodeId.UnderRight,
                        PlantNodeId.UnderMid
                    });

                case PlantMission.InspectEquipmentA:
                    return ConnectCurrentNode(currentNode, new[]
                    {
                        PlantNodeId.UnderMid,
                        PlantNodeId.UpperMid,
                        PlantNodeId.UpperLeft,
                        PlantNodeId.UnderLeft,
                        PlantNodeId.UnderMid
                    });

                case PlantMission.InspectEquipmentB:
                    return ConnectCurrentNode(currentNode, new[]
                    {
                        PlantNodeId.UnderMid,
                        PlantNodeId.UnderRight,
                        PlantNodeId.UpperRight,
                        PlantNodeId.UpperMid,
                        PlantNodeId.UnderMid
                    });

                case PlantMission.InspectEquipmentAAndB:
                    return ConnectCurrentNode(currentNode, new[]
                    {
                        PlantNodeId.UnderMid,
                        PlantNodeId.UpperMid,
                        PlantNodeId.UpperLeft,
                        PlantNodeId.UnderLeft,
                        PlantNodeId.UnderMid,
                        PlantNodeId.UnderRight,
                        PlantNodeId.UpperRight,
                        PlantNodeId.UpperMid,
                        PlantNodeId.UnderMid
                    });

                case PlantMission.ReturnToBase:
                    return routeGraph != null
                        ? routeGraph.FindShortestPath(currentNode, baseNode)
                        : new List<PlantNodeId>();

                default:
                    return new List<PlantNodeId>();
            }
        }

        public IReadOnlyList<PlantNodeId> BuildPerimeterRoute(PlantNodeId currentNode)
        {
            if (currentNode == PlantNodeId.UnderMid)
            {
                return new[]
                {
                    PlantNodeId.UnderMid,
                    PlantNodeId.UpperMid,
                    PlantNodeId.UpperLeft,
                    PlantNodeId.UnderLeft,
                    PlantNodeId.UnderMid,
                    PlantNodeId.UnderRight,
                    PlantNodeId.UpperRight,
                    PlantNodeId.UpperMid
                };
            }

            int startIndex = System.Array.IndexOf(PerimeterCycle, currentNode);
            if (startIndex < 0)
                return new List<PlantNodeId>();

            var route = new List<PlantNodeId>(PerimeterCycle.Length + 1);
            for (int offset = 0; offset < PerimeterCycle.Length; offset++)
                route.Add(PerimeterCycle[(startIndex + offset) % PerimeterCycle.Length]);
            route.Add(currentNode);
            return route;
        }

        private IReadOnlyList<PlantNodeId> ConnectCurrentNode(
            PlantNodeId currentNode,
            IReadOnlyList<PlantNodeId> missionCheckpoints)
        {
            var route = new List<PlantNodeId>();
            if (routeGraph == null || missionCheckpoints.Count == 0)
                return route;

            AppendWithoutDuplicate(route, routeGraph.FindShortestPath(currentNode, missionCheckpoints[0]));
            for (int i = 1; i < missionCheckpoints.Count; i++)
            {
                List<PlantNodeId> segment = routeGraph.FindShortestPath(missionCheckpoints[i - 1], missionCheckpoints[i]);
                if (segment.Count == 0)
                {
                    Debug.LogError($"No graph route from {missionCheckpoints[i - 1]} to {missionCheckpoints[i]}", this);
                    return new List<PlantNodeId>();
                }
                AppendWithoutDuplicate(route, segment);
            }
            return route;
        }

        private static void AppendWithoutDuplicate(List<PlantNodeId> destination, IReadOnlyList<PlantNodeId> source)
        {
            for (int i = 0; i < source.Count; i++)
            {
                if (destination.Count == 0 || !destination[destination.Count - 1].Equals(source[i]))
                    destination.Add(source[i]);
            }
        }
    }
}
