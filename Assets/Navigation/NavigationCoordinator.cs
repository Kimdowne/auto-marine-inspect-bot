using System;
using System.Collections.Generic;
using System.Text;
using ShipRobot.LaneFollowing;
using UnityEngine;
using UnityEngine.Serialization;

namespace ShipRobot.Navigation
{
    [DisallowMultipleComponent]
    public sealed class NavigationCoordinator : MonoBehaviour
    {
        public enum MissionState
        {
            Idle, FollowingLane, ConfirmingNode, ApproachingTurnCenter,
            SearchingExitLane, VisualAlign, StraightThroughJunction,
            StraightToNextMarker, Completed, Fault
        }

        [Serializable]
        public struct ManeuverOverride
        {
            public PlantNodeId entryNode;
            public PlantNodeId junctionNode;
            public PlantNodeId exitNode;
            [Min(0f)] public float approachDistance;
            [Range(0.05f, 1f)] public float approachCommand;
            [Range(0.05f, 1f)] public float searchTurnCommand;
            [Range(0.05f, 1f)] public float visualAlignMoveCommand;
        }

        private enum ActiveMission { None, SingleEdge, Perimeter }

        [Header("Connections")]
        [SerializeField] private PlantRouteGraph routeGraph;
        [SerializeField] private MissionRoutePlanner missionPlanner;
        [SerializeField] private SimulatedMarkerObservationSource markerSource;
        [SerializeField] private LaneFollowerController laneFollower;

        [Header("Marker localization")]
        [SerializeField] private PlantNodeId initialNode = PlantNodeId.UnderMid;
        [FormerlySerializedAs("markerDecisionDistance")]
        [SerializeField, Min(0.1f)] private float markerDetectionDistance = 4.00f;
        [SerializeField, Min(0.1f)] private float junctionActionDistance = 1.20f;
        [SerializeField, Range(0f, 1f)] private float minimumMarkerConfidence = 0.45f;
        [SerializeField, Min(1)] private int requiredMarkerFrames = 3;

        [Header("Approach to junction centre")]
        [SerializeField, Min(0f)] private float minimumApproachDistance = 0.10f;
        [SerializeField, Min(0.1f)] private float maximumApproachDistance = 1.50f;
        [SerializeField, Min(1)] private int requiredSideLossFrames = 30;
        [SerializeField, Range(0.05f, 1f)] private float approachCommand = 0.16f;

        [Header("Search for two exit boundaries")]
        [SerializeField, Range(0.05f, 1f)] private float searchTurnCommand = 0.20f;
        [SerializeField, Range(0f, 90f)] private float minimumTurnBeforePair = 10f;
        [SerializeField, Range(45f, 175f)] private float maximumSearchTurn = 150f;
        [SerializeField, Range(0f, 1f)] private float minimumPairConfidence = 0.10f;
        [SerializeField, Min(1)] private int requiredPairFrames = 1;

        [Header("Virtual centre-line alignment")]
        [SerializeField, Range(0.05f, 1f)] private float visualAlignMoveCommand = 0.11f;
        [SerializeField, Min(0f)] private float visualLateralGain = 0.50f;
        [SerializeField, Min(0f)] private float visualHeadingGain = 0.42f;
        [SerializeField, Range(0.05f, 1f)] private float maximumVisualTurn = 0.22f;
        [SerializeField, Range(0f, 1f)] private float alignedLateralTolerance = 0.35f;
        [SerializeField, Range(0f, 1f)] private float alignedHeadingTolerance = 0.40f;
        [SerializeField, Min(1)] private int requiredAlignedFrames = 2;
        [SerializeField, Min(0f)] private float minimumAlignTravel = 0.05f;
        [SerializeField, Min(0.1f)] private float maximumAlignTravel = 1.50f;
        [SerializeField, Min(1)] private int pairLostFrameLimit = 12;
        [SerializeField] private ManeuverOverride[] maneuverOverrides;

        [Header("Straight junction traversal")]
        [SerializeField, Range(0f, 45f)] private float straightDirectionTolerance = 25f;
        [SerializeField, Range(0.05f, 1f)] private float straightJunctionCommand = 0.14f;
        [SerializeField, Min(0f)] private float minimumStraightTravel = 0.20f;
        [SerializeField, Min(0.5f)] private float maximumStraightTravel = 3.0f;
        [SerializeField, Min(1)] private int requiredStraightLossFrames = 2;
        [SerializeField, Min(1)] private int requiredStraightReacquireFrames = 3;

        [Header("No-lane marker fallback")]
        [SerializeField, Range(0.05f, 1f)] private float fallbackStraightCommand = 0.10f;
        [SerializeField, Min(1)] private int laneLostFramesBeforeFallback = 12;
        [SerializeField, Min(0.5f)] private float maximumFallbackDistance = 8f;
        [SerializeField, Min(1f)] private float maximumFallbackSeconds = 30f;

        [Header("UI")]
        [SerializeField] private bool showMissionPanel = true;

        public MissionState State { get; private set; }
        public PlantNodeId CurrentNode { get; private set; }

        private readonly List<PlantNodeId> activeRoute = new();
        private ActiveMission activeMission;
        private int targetRouteIndex;
        private int markerFrames;
        private int sideLossFrames;
        private int pairFrames;
        private int alignedFrames;
        private int pairLostFrames;
        private int normalLaneLostFrames;
        private int straightLossFrames;
        private int straightReacquireFrames;
        private bool straightLaneGapObserved;
        private double lastSideObservationTimestamp = -1d;
        private double lastStraightObservationTimestamp = -1d;
        private float desiredExitYaw;
        private float searchStartYaw;
        private float plannedTurnSign;
        private float minimumSearchAngle;
        private Vector3 motionStartPosition;
        private float fallbackStartTime;
        private float activeApproachDistance;
        private float activeApproachCommand;
        private float activeSearchTurnCommand;
        private float activeVisualMoveCommand;
        private string statusDetail = "Ready";
        private GUIStyle titleStyle;
        private GUIStyle statusStyle;

        private void Awake()
        {
            CurrentNode = initialNode;
            State = MissionState.Idle;
            laneFollower?.SetDriveEnabled(false);
        }

        private void Update()
        {
            switch (State)
            {
                case MissionState.ApproachingTurnCenter:
                    UpdateApproach();
                    return;
                case MissionState.SearchingExitLane:
                    UpdateExitLaneSearch();
                    return;
                case MissionState.VisualAlign:
                    UpdateVisualAlignment();
                    return;
                case MissionState.StraightThroughJunction:
                    UpdateStraightThroughJunction();
                    return;
                case MissionState.StraightToNextMarker:
                    UpdateStraightToNextMarker();
                    return;
            }

            if (State != MissionState.FollowingLane && State != MissionState.ConfirmingNode)
                return;
            if (!ConnectionsReady())
            {
                Fail("Navigation setup is incomplete");
                return;
            }

            PlantNodeId target = activeRoute[targetRouteIndex];
            bool targetVisible = markerSource.TryGetLatestObservation(out MarkerObservation observation) &&
                                 observation.nodeId == target &&
                                 observation.confidence >= minimumMarkerConfidence;
            if (!targetVisible)
            {
                markerFrames = 0;
                State = MissionState.FollowingLane;
                bool pairPresent = laneFollower.TryGetBoundaryPair(minimumPairConfidence, out _);
                normalLaneLostFrames = pairPresent ? 0 : normalLaneLostFrames + 1;
                statusDetail = $"Following to ID {(int)target} ({target}), pairLost={normalLaneLostFrames}/{laneLostFramesBeforeFallback}";
                if (normalLaneLostFrames >= laneLostFramesBeforeFallback)
                    EnterStraightMarkerFallback(false);
                return;
            }

            normalLaneLostFrames = 0;

            float distance = observation.cameraRelativePosition.magnitude;
            statusDetail = $"ID {(int)target} visible at {distance:F2} m";
            if (distance > markerDetectionDistance)
            {
                markerFrames = 0;
                State = MissionState.FollowingLane;
                bool pairPresent = laneFollower.TryGetBoundaryPair(minimumPairConfidence, out _);
                normalLaneLostFrames = pairPresent ? 0 : normalLaneLostFrames + 1;
                statusDetail = $"Target far at {distance:F2} m, pairLost={normalLaneLostFrames}/{laneLostFramesBeforeFallback}";
                if (normalLaneLostFrames >= laneLostFramesBeforeFallback)
                    EnterStraightMarkerFallback(false);
                return;
            }

            if (distance > junctionActionDistance)
            {
                markerFrames = 0;
                State = MissionState.FollowingLane;
                bool pairPresent = laneFollower.TryGetBoundaryPair(minimumPairConfidence, out _);
                normalLaneLostFrames = pairPresent ? 0 : normalLaneLostFrames + 1;
                statusDetail = $"ID {(int)target} detected at {distance:F2} m; action at {junctionActionDistance:F2} m, " +
                               $"pairLost={normalLaneLostFrames}/{laneLostFramesBeforeFallback}";
                if (normalLaneLostFrames >= laneLostFramesBeforeFallback)
                    EnterStraightMarkerFallback(false);
                return;
            }

            State = MissionState.ConfirmingNode;
            markerFrames++;
            if (markerFrames >= requiredMarkerFrames)
                ArriveAtTargetNode();
        }

        public void StartSingleEdgeMission() =>
            StartRoute(new[] { CurrentNode, PlantNodeId.UpperMid }, ActiveMission.SingleEdge);

        [ContextMenu("Start Perimeter Mission")]
        public void StartPerimeterMission()
        {
            if (missionPlanner == null)
            {
                Fail("Mission planner is missing");
                return;
            }
            if (markerSource != null && markerSource.TryGetLatestObservation(out MarkerObservation observation) &&
                observation.confidence >= minimumMarkerConfidence &&
                observation.cameraRelativePosition.magnitude <= junctionActionDistance * 1.5f)
                CurrentNode = observation.nodeId;
            StartRoute(missionPlanner.BuildPerimeterRoute(CurrentNode), ActiveMission.Perimeter);
        }

        private void StartRoute(IReadOnlyList<PlantNodeId> route, ActiveMission mission)
        {
            if (!ConnectionsReady() || route == null || route.Count < 2)
            {
                Fail("Route is empty or setup is incomplete");
                return;
            }
            activeRoute.Clear();
            for (int i = 0; i < route.Count; i++) activeRoute.Add(route[i]);
            activeMission = mission;
            CurrentNode = activeRoute[0];
            targetRouteIndex = 1;
            markerFrames = 0;
            normalLaneLostFrames = 0;
            State = MissionState.FollowingLane;
            statusDetail = $"Following to ID {(int)activeRoute[1]} ({activeRoute[1]})";
            laneFollower.ResumeLaneFollowing();
        }

        private void ArriveAtTargetNode()
        {
            CurrentNode = activeRoute[targetRouteIndex];
            markerFrames = 0;
            if (targetRouteIndex >= activeRoute.Count - 1)
            {
                CompleteMission();
                return;
            }

            PlantNodeId entry = activeRoute[targetRouteIndex - 1];
            PlantNodeId exit = activeRoute[targetRouteIndex + 1];
            if (!TryCalculateEdgeYaw(CurrentNode, exit, out desiredExitYaw))
            {
                Fail($"Missing graph direction for {CurrentNode} -> {exit}");
                return;
            }

            if (!TryCalculatePathDeflection(entry, CurrentNode, exit, out float pathDeflection))
            {
                Fail($"Missing graph direction for {entry} -> {CurrentNode} -> {exit}");
                return;
            }

            if (pathDeflection <= straightDirectionTolerance)
            {
                BeginStraightThroughJunction(pathDeflection);
                return;
            }

            ResolveManeuver(entry, CurrentNode, exit);
            sideLossFrames = 0;
            lastSideObservationTimestamp = -1d;
            motionStartPosition = laneFollower.transform.position;
            State = MissionState.ApproachingTurnCenter;
            laneFollower.SetManualCommand(activeApproachCommand, 0f);
        }

        private void UpdateApproach()
        {
            float travelled = PlanarDistance(motionStartPosition, laneFollower.transform.position);
            bool observationFresh = laneFollower.TryGetBoundarySides(out HsvLaneDetector.Detection detection);
            bool bothSidesVisible = observationFresh &&
                                    detection.leftBoundaryVisible &&
                                    detection.rightBoundaryVisible;
            bool mayAcceptSideLoss = travelled >= minimumApproachDistance;
            bool newObservation = observationFresh &&
                                  detection.timestamp > lastSideObservationTimestamp;
            if (newObservation)
            {
                lastSideObservationTimestamp = detection.timestamp;
                sideLossFrames = mayAcceptSideLoss && !bothSidesVisible
                    ? sideLossFrames + 1
                    : 0;
            }

            statusDetail =
                $"Approach {travelled:F2}/{activeApproachDistance:F2} m, " +
                $"L={(detection.leftBoundaryVisible ? detection.leftBoundaryConfidence.ToString("F2") : "NO")}, " +
                $"R={(detection.rightBoundaryVisible ? detection.rightBoundaryConfidence.ToString("F2") : "NO")}, " +
                $"sideLost={sideLossFrames}/{requiredSideLossFrames}";

            if (sideLossFrames >= requiredSideLossFrames)
            {
                BeginExitLaneSearch();
                return;
            }

            if (travelled >= activeApproachDistance)
            {
                Fail($"Both entry boundaries remained visible for {activeApproachDistance:F2} m");
                return;
            }
            laneFollower.SetManualCommand(activeApproachCommand, 0f);
        }

        private void BeginExitLaneSearch()
        {
            searchStartYaw = laneFollower.transform.eulerAngles.y;
            float plannedAngle = Mathf.DeltaAngle(searchStartYaw, desiredExitYaw);
            plannedTurnSign = Mathf.Abs(plannedAngle) < 1f ? 1f : Mathf.Sign(plannedAngle);
            minimumSearchAngle = Mathf.Min(minimumTurnBeforePair, Mathf.Abs(plannedAngle) * 0.45f);
            pairFrames = 0;
            State = MissionState.SearchingExitLane;
            laneFollower.SetManualCommand(0f, plannedTurnSign * activeSearchTurnCommand);
        }

        private void UpdateExitLaneSearch()
        {
            float turned = Mathf.Abs(Mathf.DeltaAngle(searchStartYaw, laneFollower.transform.eulerAngles.y));
            if (turned > maximumSearchTurn)
            {
                Fail($"No exit boundary pair within {maximumSearchTurn:F0} deg");
                return;
            }

            bool angleReady = turned >= minimumSearchAngle;
            bool pairUsable = laneFollower.TryGetBoundaryPair(
                minimumPairConfidence, out HsvLaneDetector.Detection detection);
            bool pairVisible = angleReady && pairUsable;
            pairFrames = pairVisible ? pairFrames + 1 : Mathf.Max(0, pairFrames - 1);
            float effectivePairConfidence = Mathf.Max(
                detection.boundaryPairConfidence, detection.confidence);
            statusDetail =
                $"Search: angle {turned:F1}/{minimumSearchAngle:F1} ready={(angleReady ? "YES" : "NO")}, " +
                $"pair={(detection.hasBoundaryPair ? "YES" : "NO")}, conf={detection.confidence:F2}, " +
                $"pairConf={detection.boundaryPairConfidence:F2}, effective={effectivePairConfidence:F2}, " +
                $"stable={pairFrames}/{requiredPairFrames}";

            if (pairFrames >= requiredPairFrames)
            {
                alignedFrames = 0;
                pairLostFrames = 0;
                motionStartPosition = laneFollower.transform.position;
                State = MissionState.VisualAlign;
                return;
            }
            laneFollower.SetManualCommand(0f, plannedTurnSign * activeSearchTurnCommand);
        }

        private void UpdateVisualAlignment()
        {
            float travelled = PlanarDistance(motionStartPosition, laneFollower.transform.position);
            if (travelled > maximumAlignTravel)
            {
                Fail($"Virtual-line alignment failed within {maximumAlignTravel:F2} m");
                return;
            }

            if (!laneFollower.TryGetBoundaryPair(minimumPairConfidence, out HsvLaneDetector.Detection detection))
            {
                pairLostFrames++;
                alignedFrames = 0;
                statusDetail = $"Boundary pair lost {pairLostFrames}/{pairLostFrameLimit}";
                if (pairLostFrames > pairLostFrameLimit)
                {
                    EnterStraightMarkerFallback(true);
                }
                else
                {
                    laneFollower.SetManualCommand(activeVisualMoveCommand * 0.35f, 0f);
                }
                return;
            }

            pairLostFrames = 0;
            float correction = detection.lateralError * visualLateralGain +
                               detection.headingError * visualHeadingGain;
            correction = Mathf.Clamp(correction, -maximumVisualTurn, maximumVisualTurn);
            float errorAmount = Mathf.Clamp01(Mathf.Abs(detection.lateralError) + Mathf.Abs(detection.headingError));
            float move = activeVisualMoveCommand * Mathf.Lerp(1f, 0.35f, errorAmount);

            bool aligned = travelled >= minimumAlignTravel &&
                           Mathf.Abs(detection.lateralError) <= alignedLateralTolerance &&
                           Mathf.Abs(detection.headingError) <= alignedHeadingTolerance;
            alignedFrames = aligned ? alignedFrames + 1 : 0;
            statusDetail = $"Virtual line: lateral={detection.lateralError:F2}, heading={detection.headingError:F2}, stable={alignedFrames}/{requiredAlignedFrames}";

            if (alignedFrames >= requiredAlignedFrames)
            {
                targetRouteIndex++;
                State = MissionState.FollowingLane;
                PlantNodeId target = activeRoute[targetRouteIndex];
                statusDetail = $"Aligned; following ID {(int)target} ({target})";
                laneFollower.ResumeLaneFollowing();
                return;
            }
            laneFollower.SetManualCommand(move, correction);
        }

        private void BeginStraightThroughJunction(float pathDeflection)
        {
            targetRouteIndex++;
            markerFrames = 0;
            straightLossFrames = 0;
            straightReacquireFrames = 0;
            straightLaneGapObserved = false;
            lastStraightObservationTimestamp = -1d;
            motionStartPosition = laneFollower.transform.position;
            State = MissionState.StraightThroughJunction;
            PlantNodeId target = activeRoute[targetRouteIndex];
            statusDetail = $"Straight junction ({pathDeflection:F1} deg); driving to ID {(int)target} ({target})";
            laneFollower.SetManualCommand(straightJunctionCommand, 0f);
        }

        private void UpdateStraightThroughJunction()
        {
            float travelled = PlanarDistance(motionStartPosition, laneFollower.transform.position);
            if (travelled >= maximumStraightTravel)
            {
                Fail($"Straight junction lane was not reacquired within {maximumStraightTravel:F2} m");
                return;
            }

            PlantNodeId target = activeRoute[targetRouteIndex];
            bool targetVisible = markerSource.TryGetLatestObservation(out MarkerObservation observation) &&
                                 observation.nodeId == target &&
                                 observation.confidence >= minimumMarkerConfidence;
            if (targetVisible && observation.cameraRelativePosition.magnitude <= junctionActionDistance)
            {
                markerFrames++;
                statusDetail = $"Straight marker ID {(int)target} at {observation.cameraRelativePosition.magnitude:F2} m, " +
                               $"confirm={markerFrames}/{requiredMarkerFrames}";
                laneFollower.SetManualCommand(straightJunctionCommand * 0.5f, 0f);
                if (markerFrames >= requiredMarkerFrames)
                    ArriveAtTargetNode();
                return;
            }
            markerFrames = 0;

            bool pairVisible = laneFollower.TryGetBoundaryPair(
                minimumPairConfidence, out HsvLaneDetector.Detection detection);
            bool newObservation = detection.timestamp > lastStraightObservationTimestamp;
            if (newObservation)
            {
                lastStraightObservationTimestamp = detection.timestamp;
                if (!straightLaneGapObserved)
                {
                    straightLossFrames = pairVisible ? 0 : straightLossFrames + 1;
                    straightLaneGapObserved = straightLossFrames >= requiredStraightLossFrames;
                }
                else
                {
                    straightReacquireFrames = pairVisible ? straightReacquireFrames + 1 : 0;
                }
            }

            statusDetail =
                $"Straight {travelled:F2}/{maximumStraightTravel:F2} m, " +
                $"gap={(straightLaneGapObserved ? "YES" : $"{straightLossFrames}/{requiredStraightLossFrames}")}, " +
                $"pair={(pairVisible ? "YES" : "NO")}, reacquire={straightReacquireFrames}/{requiredStraightReacquireFrames}";

            if (straightLaneGapObserved && travelled >= minimumStraightTravel &&
                straightReacquireFrames >= requiredStraightReacquireFrames)
            {
                State = MissionState.FollowingLane;
                normalLaneLostFrames = 0;
                statusDetail = $"Straight junction cleared; following ID {(int)target} ({target})";
                laneFollower.ResumeLaneFollowing();
                return;
            }

            laneFollower.SetManualCommand(straightJunctionCommand, 0f);
        }

        private void EnterStraightMarkerFallback(bool advanceToNextRouteNode)
        {
            if (advanceToNextRouteNode)
            {
                if (targetRouteIndex >= activeRoute.Count - 1)
                {
                    CompleteMission();
                    return;
                }
                targetRouteIndex++;
            }

            markerFrames = 0;
            normalLaneLostFrames = 0;
            motionStartPosition = laneFollower.transform.position;
            fallbackStartTime = Time.time;
            State = MissionState.StraightToNextMarker;
            PlantNodeId target = activeRoute[targetRouteIndex];
            statusDetail = $"No lane; driving straight to marker ID {(int)target}";
            laneFollower.SetManualCommand(fallbackStraightCommand, 0f);
        }

        private void UpdateStraightToNextMarker()
        {
            float travelled = PlanarDistance(motionStartPosition, laneFollower.transform.position);
            float elapsed = Time.time - fallbackStartTime;
            if (travelled >= maximumFallbackDistance || elapsed >= maximumFallbackSeconds)
            {
                Fail($"Marker fallback limit reached: {travelled:F1} m, {elapsed:F1} s");
                return;
            }

            PlantNodeId target = activeRoute[targetRouteIndex];
            bool visible = markerSource.TryGetLatestObservation(out MarkerObservation observation) &&
                           observation.nodeId == target &&
                           observation.confidence >= minimumMarkerConfidence;
            if (!visible)
            {
                markerFrames = 0;
                statusDetail = $"Straight to marker ID {(int)target}: {travelled:F1}/{maximumFallbackDistance:F1} m";
                laneFollower.SetManualCommand(fallbackStraightCommand, 0f);
                return;
            }

            float distance = observation.cameraRelativePosition.magnitude;
            statusDetail = $"Fallback marker ID {(int)target} visible at {distance:F2} m";
            if (distance > junctionActionDistance)
            {
                markerFrames = 0;
                laneFollower.SetManualCommand(fallbackStraightCommand, 0f);
                return;
            }

            markerFrames++;
            laneFollower.SetManualCommand(fallbackStraightCommand * 0.5f, 0f);
            if (markerFrames >= requiredMarkerFrames)
                ArriveAtTargetNode();
        }

        private void ResolveManeuver(PlantNodeId entry, PlantNodeId junction, PlantNodeId exit)
        {
            activeApproachDistance = maximumApproachDistance;
            activeApproachCommand = approachCommand;
            activeSearchTurnCommand = searchTurnCommand;
            activeVisualMoveCommand = visualAlignMoveCommand;
            if (maneuverOverrides == null) return;
            foreach (ManeuverOverride item in maneuverOverrides)
            {
                if (item.entryNode != entry || item.junctionNode != junction || item.exitNode != exit) continue;
                activeApproachDistance = item.approachDistance;
                activeApproachCommand = item.approachCommand;
                activeSearchTurnCommand = item.searchTurnCommand;
                activeVisualMoveCommand = item.visualAlignMoveCommand;
                return;
            }
        }

        private bool TryCalculateEdgeYaw(PlantNodeId from, PlantNodeId to, out float yaw)
        {
            yaw = 0f;
            if (routeGraph == null || !routeGraph.TryGetMarker(from, out NavigationMarker fromMarker) ||
                !routeGraph.TryGetMarker(to, out NavigationMarker toMarker)) return false;
            Vector3 direction = toMarker.transform.position - fromMarker.transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.001f) return false;
            yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            return true;
        }

        private bool TryCalculatePathDeflection(
            PlantNodeId entry, PlantNodeId junction, PlantNodeId exit, out float angle)
        {
            angle = 0f;
            if (routeGraph == null ||
                !routeGraph.TryGetMarker(entry, out NavigationMarker entryMarker) ||
                !routeGraph.TryGetMarker(junction, out NavigationMarker junctionMarker) ||
                !routeGraph.TryGetMarker(exit, out NavigationMarker exitMarker))
                return false;

            Vector3 incoming = junctionMarker.transform.position - entryMarker.transform.position;
            Vector3 outgoing = exitMarker.transform.position - junctionMarker.transform.position;
            incoming.y = 0f;
            outgoing.y = 0f;
            if (incoming.sqrMagnitude < 0.001f || outgoing.sqrMagnitude < 0.001f)
                return false;

            angle = Vector3.Angle(incoming, outgoing);
            return true;
        }

        [ContextMenu("Reset Mission")]
        public void ResetMission()
        {
            activeRoute.Clear();
            activeMission = ActiveMission.None;
            State = MissionState.Idle;
            normalLaneLostFrames = 0;
            statusDetail = $"Ready at {CurrentNode}";
            laneFollower?.SetDriveEnabled(false);
        }

        private void CompleteMission()
        {
            State = MissionState.Completed;
            statusDetail = $"Completed at ID {(int)CurrentNode} ({CurrentNode})";
            laneFollower.SetDriveEnabled(false);
        }

        private bool ConnectionsReady() =>
            routeGraph != null && missionPlanner != null && markerSource != null && laneFollower != null;

        private void Fail(string reason)
        {
            State = MissionState.Fault;
            statusDetail = reason;
            laneFollower?.SetDriveEnabled(false);
            Debug.LogError($"Navigation mission fault: {reason}", this);
        }

        private static float PlanarDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f; b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private string RouteText()
        {
            if (activeRoute.Count == 0) return "No active route";
            var text = new StringBuilder();
            for (int i = 0; i < activeRoute.Count; i++)
            {
                if (i > 0) text.Append(" > ");
                text.Append((int)activeRoute[i]);
            }
            return text.ToString();
        }

        private void OnGUI()
        {
            if (!showMissionPanel) return;
            EnsureStyles();
            Rect panel = new Rect(10f, Screen.height - 165f, 500f, 155f);
            GUI.Box(panel, GUIContent.none);
            GUI.Label(new Rect(panel.x + 10f, panel.y + 7f, panel.width - 20f, 22f),
                $"MISSION: {activeMission}   State: {State}", titleStyle);
            GUI.Label(new Rect(panel.x + 10f, panel.y + 31f, panel.width - 20f, 55f),
                $"{statusDetail}\nRoute: {RouteText()}", statusStyle);
            bool canStart = State == MissionState.Idle || State == MissionState.Completed || State == MissionState.Fault;
            if (canStart && GUI.Button(new Rect(panel.x + 10f, panel.y + 92f, 230f, 27f), "START SINGLE 6 -> 3"))
                StartSingleEdgeMission();
            if (canStart && GUI.Button(new Rect(panel.x + 255f, panel.y + 92f, 230f, 27f), "START PERIMETER"))
                StartPerimeterMission();
            if (State != MissionState.Idle && GUI.Button(new Rect(panel.x + 10f, panel.y + 123f, 475f, 25f), "RESET / STOP"))
                ResetMission();
        }

        private void EnsureStyles()
        {
            if (titleStyle != null) return;
            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14, fontStyle = FontStyle.Bold,
                normal = { textColor = Color.cyan }
            };
            statusStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12, normal = { textColor = Color.white }, wordWrap = true
            };
        }
    }
}
