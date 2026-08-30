using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShipRobot.Navigation
{
    public enum PlantNodeId
    {
        UpperLeft = 1,
        UpperRight = 2,
        UpperMid = 3,
        UnderLeft = 4,
        UnderRight = 5,
        UnderMid = 6
    }

    public enum PlantMission
    {
        PerimeterPatrol,
        InspectEquipmentA,
        InspectEquipmentB,
        ReturnToBase
    }

    [Serializable]
    public struct MarkerObservation
    {
        public PlantNodeId nodeId;
        public Vector3 cameraRelativePosition;
        public Quaternion cameraRelativeRotation;
        [Range(0f, 1f)] public float confidence;
        public double timestamp;
    }

    /// <summary>
    /// Implemented later by the Unity simulation detector and the physical AprilTag detector.
    /// Navigation code depends only on this interface, not on a particular tag library.
    /// </summary>
    public interface IMarkerObservationSource
    {
        bool TryGetLatestObservation(out MarkerObservation observation);
    }

    [DisallowMultipleComponent]
    public sealed class NavigationMarker : MonoBehaviour
    {
        [SerializeField] private PlantNodeId nodeId;
        [SerializeField, Min(0.01f)] private float physicalSizeMetres = 0.30f;

        public PlantNodeId NodeId => nodeId;
        public float PhysicalSizeMetres => physicalSizeMetres;

        private void OnDrawGizmos()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(transform.position, Vector3.one * physicalSizeMetres);
        }
    }

    [DisallowMultipleComponent]
    public sealed class PlantRouteGraph : MonoBehaviour
    {
        [Serializable]
        public struct Edge
        {
            public PlantNodeId from;
            public PlantNodeId to;
            [Min(0.01f)] public float cost;
            public bool bidirectional;

            public Edge(PlantNodeId from, PlantNodeId to, float cost, bool bidirectional = true)
            {
                this.from = from;
                this.to = to;
                this.cost = cost;
                this.bidirectional = bidirectional;
            }
        }

        [Tooltip("If enabled, edge costs are calculated from the scene marker positions.")]
        [SerializeField] private bool useMarkerDistanceAsCost = true;
        [SerializeField] private NavigationMarker[] markers;
        [SerializeField] private Edge[] edges = CreateDefaultEdges();

        private readonly Dictionary<PlantNodeId, NavigationMarker> markerById = new();

        public IReadOnlyList<Edge> Edges => edges;

        private void Awake()
        {
            RebuildMarkerIndex();
        }

        [ContextMenu("Find Markers In Scene")]
        public void FindMarkersInScene()
        {
            markers = FindObjectsByType<NavigationMarker>(FindObjectsSortMode.None);
            RebuildMarkerIndex();
        }

        public bool TryGetMarker(PlantNodeId id, out NavigationMarker marker)
        {
            if (markerById.Count == 0)
                RebuildMarkerIndex();
            return markerById.TryGetValue(id, out marker);
        }

        public List<PlantNodeId> FindShortestPath(PlantNodeId start, PlantNodeId goal)
        {
            var distance = new Dictionary<PlantNodeId, float>();
            var previous = new Dictionary<PlantNodeId, PlantNodeId>();
            var unvisited = new HashSet<PlantNodeId>();

            foreach (PlantNodeId node in Enum.GetValues(typeof(PlantNodeId)))
            {
                distance[node] = float.PositiveInfinity;
                unvisited.Add(node);
            }
            distance[start] = 0f;

            while (unvisited.Count > 0)
            {
                PlantNodeId current = default;
                float bestDistance = float.PositiveInfinity;
                foreach (PlantNodeId candidate in unvisited)
                {
                    if (distance[candidate] < bestDistance)
                    {
                        current = candidate;
                        bestDistance = distance[candidate];
                    }
                }

                if (float.IsPositiveInfinity(bestDistance))
                    break;
                if (current.Equals(goal))
                    return ReconstructPath(previous, start, goal);

                unvisited.Remove(current);
                foreach ((PlantNodeId neighbour, float cost) in GetNeighbours(current))
                {
                    if (!unvisited.Contains(neighbour))
                        continue;
                    float candidateDistance = bestDistance + cost;
                    if (candidateDistance < distance[neighbour])
                    {
                        distance[neighbour] = candidateDistance;
                        previous[neighbour] = current;
                    }
                }
            }

            return new List<PlantNodeId>();
        }

        private IEnumerable<(PlantNodeId node, float cost)> GetNeighbours(PlantNodeId source)
        {
            if (edges == null)
                yield break;

            foreach (Edge edge in edges)
            {
                if (edge.from.Equals(source))
                    yield return (edge.to, ResolveCost(edge));
                if (edge.bidirectional && edge.to.Equals(source))
                    yield return (edge.from, ResolveCost(edge));
            }
        }

        private float ResolveCost(Edge edge)
        {
            if (useMarkerDistanceAsCost &&
                TryGetMarker(edge.from, out NavigationMarker from) &&
                TryGetMarker(edge.to, out NavigationMarker to))
            {
                return Vector3.Distance(from.transform.position, to.transform.position);
            }
            return Mathf.Max(0.01f, edge.cost);
        }

        private void RebuildMarkerIndex()
        {
            markerById.Clear();
            if (markers == null)
                return;

            foreach (NavigationMarker marker in markers)
            {
                if (marker == null)
                    continue;
                if (markerById.ContainsKey(marker.NodeId))
                {
                    Debug.LogError($"Duplicate navigation marker ID: {marker.NodeId}", marker);
                    continue;
                }
                markerById.Add(marker.NodeId, marker);
            }
        }

        private static List<PlantNodeId> ReconstructPath(
            Dictionary<PlantNodeId, PlantNodeId> previous,
            PlantNodeId start,
            PlantNodeId goal)
        {
            var path = new List<PlantNodeId> { goal };
            PlantNodeId current = goal;
            while (!current.Equals(start))
            {
                if (!previous.TryGetValue(current, out current))
                    return new List<PlantNodeId>();
                path.Add(current);
            }
            path.Reverse();
            return path;
        }

        private static Edge[] CreateDefaultEdges()
        {
            return new[]
            {
                new Edge(PlantNodeId.UpperLeft, PlantNodeId.UpperMid, 1f),
                new Edge(PlantNodeId.UpperMid, PlantNodeId.UpperRight, 1f),
                new Edge(PlantNodeId.UnderLeft, PlantNodeId.UnderMid, 1f),
                new Edge(PlantNodeId.UnderMid, PlantNodeId.UnderRight, 1f),
                new Edge(PlantNodeId.UpperLeft, PlantNodeId.UnderLeft, 1f),
                new Edge(PlantNodeId.UpperMid, PlantNodeId.UnderMid, 1f),
                new Edge(PlantNodeId.UpperRight, PlantNodeId.UnderRight, 1f)
            };
        }
    }
}
