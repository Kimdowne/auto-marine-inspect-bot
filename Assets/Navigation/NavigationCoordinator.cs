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
            StraightToNextMarker, InspectingEquipment, Completed, Fault
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

        private enum ActiveMission { None, SingleEdge, Perimeter, EquipmentA, EquipmentAAndB }

        [Header("Connections")]
        [SerializeField] private PlantRouteGraph routeGraph;
        [SerializeField] private MissionRoutePlanner missionPlanner;
        [SerializeField] private SimulatedMarkerObservationSource markerSource;
        [SerializeField] private LaneFollowerController laneFollower;
        [SerializeField] private ShipRobot.ObstacleAvoidance.HumanAvoidanceAgent avoidanceAgent;

        [Header("Equipment A inspection")]
        [SerializeField] private Transform inspectionPointA1;
        [SerializeField] private Transform inspectionPointA2;
        [SerializeField] private Transform inspectionPointB1;
        [SerializeField] private Transform inspectionPointB2;
        [SerializeField, Min(0.1f)] private float inspectionReachDistance = 1.20f;

        [Header("Marker localization")]
        [SerializeField] private PlantNodeId initialNode = PlantNodeId.UnderMid;
        [FormerlySerializedAs("markerDecisionDistance")]
        [SerializeField, Min(0.1f)] private float markerDetectionDistance = 4.00f;
        [SerializeField, Min(0.1f)] private float junctionActionDistance = 1.20f;
        [SerializeField, Range(0f, 1f)] private float minimumMarkerConfidence = 0.45f;
        [SerializeField, Min(1)] private int requiredMarkerFrames = 3;

        [Header("Approach to junction centre")]
        [SerializeField, Min(0f)] private float minimumApproachDistance = 0.10f;
        [SerializeField, Min(0f)] private float postAvoidanceMinimumApproachDistance = 0.50f;
        [SerializeField, Min(0.1f)] private float maximumApproachDistance = 1.50f;
        [SerializeField, Min(1)] private int requiredSideLossFrames = 30;
        [SerializeField, Range(0.05f, 1f)] private float approachCommand = 0.16f;

        [Header("Search for two exit boundaries")]
        [SerializeField, Range(0.05f, 1f)] private float searchTurnCommand = 0.20f;
        [SerializeField, Range(0f, 90f)] private float minimumTurnBeforePair = 10f;
        [SerializeField, Range(45f, 175f)] private float maximumSearchTurn = 150f;
        [SerializeField, Range(5f, 90f)] private float exitHeadingTolerance = 35f;
        [SerializeField, Min(0.1f)] private float maximumExitLaneProbeDistance = 1.2f;
        [SerializeField, Range(0.05f, 1f)] private float exitLaneProbeCommand = 0.10f;
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
        [SerializeField, Min(0)] private int maximumExitLaneRecoveryAttempts = 2;
        [SerializeField] private ManeuverOverride[] maneuverOverrides;

        [Header("Straight junction traversal")]
        [SerializeField, Range(0f, 45f)] private float straightDirectionTolerance = 25f;
        [SerializeField, Range(0.05f, 1f)] private float straightJunctionCommand = 0.14f;
        [SerializeField, Min(0f)] private float minimumStraightTravel = 0.20f;
        [SerializeField, Min(0.5f)] private float maximumStraightTravel = 3.0f;
        [SerializeField, Min(1)] private int requiredStraightLossFrames = 2;
        [SerializeField, Min(1)] private int requiredStraightReacquireFrames = 3;

        [Header("Equipment demo bottom crossing")]
        [SerializeField, Min(0f)] private float turnCentrePastMarkerDistance = 0.65f;
        [SerializeField, Min(2f)] private float rightBottomMaximumApproachDistance = 10f;

        [Header("No-lane marker fallback")]
        [SerializeField, Range(0.05f, 1f)] private float fallbackStraightCommand = 0.10f;
        [SerializeField, Min(1)] private int laneLostFramesBeforeFallback = 12;
        [SerializeField, Min(0.5f)] private float maximumFallbackDistance = 8f;
        [SerializeField, Min(1f)] private float maximumFallbackSeconds = 30f;

        [Header("UI")]
        [SerializeField] private bool showMissionPanel = true;
        [FormerlySerializedAs("autoStartEquipmentAMission")]
        [SerializeField] private bool autoStartEquipmentAAndBMission;

        public MissionState State { get; private set; }
        public PlantNodeId CurrentNode { get; private set; }
        public string StatusDetail => statusDetail;
        public bool IsFollowingEquipmentLeg(PlantNodeId from, PlantNodeId to) =>
            activeMission == ActiveMission.EquipmentAAndB &&
            (State == MissionState.FollowingLane || State == MissionState.StraightToNextMarker) &&
            CurrentNode == from &&
            targetRouteIndex < activeRoute.Count && activeRoute[targetRouteIndex] == to;
        public bool IsAlignedOnEquipmentLeg(PlantNodeId from, PlantNodeId to)
        {
            if (!IsFollowingEquipmentLeg(from, to) || State != MissionState.FollowingLane ||
                laneFollower == null || !laneFollower.HasUsableLane ||
                !laneFollower.TryGetBoundaryPair(minimumPairConfidence, out HsvLaneDetector.Detection detection))
                return false;

            return Mathf.Abs(detection.lateralError) <= alignedLateralTolerance &&
                   Mathf.Abs(detection.headingError) <= alignedHeadingTolerance;
        }
        public bool IsMotionRequested => State != MissionState.Idle &&
            State != MissionState.InspectingEquipment && State != MissionState.Completed &&
            State != MissionState.Fault;
        private bool avoidancePaused;
        private bool avoidanceInterruptedCurrentLeg;
        private Vector3 pausePosition;
        private float pauseStarted;

        private readonly List<PlantNodeId> activeRoute = new();
        private ActiveMission activeMission;
        private int targetRouteIndex;
        private int markerFrames;
        private int sideLossFrames;
        private int pairFrames;
        private int alignedFrames;
        private int pairLostFrames;
        private int exitLaneRecoveryAttempts;
        private int normalLaneLostFrames;
        private int straightLossFrames;
        private int straightReacquireFrames;
        private bool straightLaneGapObserved;
        private double lastSideObservationTimestamp = -1d;
        private double lastStraightObservationTimestamp = -1d;
        private float desiredExitYaw;
        private float searchStartYaw;
        private float minimumSearchAngle;
        private Vector3 exitLaneProbeStartPosition;
        private bool exitLaneProbeStarted;
        private Vector3 motionStartPosition;
        private float fallbackStartTime;
        private float activeApproachDistance;
        private float activeMinimumApproachDistance;
        private float activeApproachCommand;
        private float activeSearchTurnCommand;
        private float activeVisualMoveCommand;
        private int activeInspectionIndex;
        private float inspectionTimeRemaining;
        private Transform activeInspectionPoint;
        private string statusDetail = "Ready";
        private GUIStyle titleStyle;
        private GUIStyle statusStyle;

        private void Awake()
        {
            CurrentNode = initialNode;
            State = MissionState.Idle;
            laneFollower?.SetDriveEnabled(false);
        }

        private void Start()
        {
            if (autoStartEquipmentAAndBMission && (avoidanceAgent == null || !avoidanceAgent.IsTraining))
                StartEquipmentAAndBMission();
        }

        private void Update()
        {
            if (IsMotionRequested && avoidanceAgent != null && !avoidanceAgent.IsTraining &&
                (avoidanceAgent.HasAvoidanceControl || laneFollower.IsSafetyStopped))
            {
                if (avoidanceAgent.HasAvoidanceControl)
                    avoidanceInterruptedCurrentLeg = true;
                if (!avoidancePaused)
                {
                    avoidancePaused = true;
                    pausePosition = laneFollower.transform.position;
                    pauseStarted = Time.time;
                }
                return;
            }
            if (avoidancePaused)
            {
                // Avoidance travel/time is not junction approach/fallback progress.
                motionStartPosition += laneFollower.transform.position - pausePosition;
                fallbackStartTime += Time.time - pauseStarted;
                markerFrames = sideLossFrames = pairFrames = alignedFrames = normalLaneLostFrames = 0;
                avoidancePaused = false;
            }
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
                case MissionState.InspectingEquipment:
                    UpdateEquipmentInspection();
                    return;
            }

            if (State != MissionState.FollowingLane && State != MissionState.ConfirmingNode)
                return;
            if (!ConnectionsReady())
            {
                Fail("Navigation setup is incomplete");
                return;
            }

            if (TryBeginEquipmentInspection())
                return;

            if (TryArriveAtTargetAfterAvoidance())
                return;

            PlantNodeId target = activeRoute[targetRouteIndex];
            bool targetVisible = markerSource.TryGetLatestObservation(out MarkerObservation observation) &&
                                 observation.nodeId == target &&
                                 observation.confidence >= minimumMarkerConfidence;
            if (!targetVisible)
            {
                markerFrames = 0;
                State = MissionState.FollowingLane;
                CountUnusableLaneFrames();
                statusDetail = $"Following to ID {(int)target} ({target}), laneLost={normalLaneLostFrames}/{laneLostFramesBeforeFallback}";
                if (normalLaneLostFrames >= laneLostFramesBeforeFallback)
                    EnterStraightMarkerFallback();
                return;
            }

            float distance = observation.cameraRelativePosition.magnitude;
            statusDetail = $"ID {(int)target} visible at {distance:F2} m";
            if (distance > markerDetectionDistance)
            {
                markerFrames = 0;
                State = MissionState.FollowingLane;
                CountUnusableLaneFrames();
                statusDetail = $"Target far at {distance:F2} m, laneLost={normalLaneLostFrames}/{laneLostFramesBeforeFallback}";
                if (normalLaneLostFrames >= laneLostFramesBeforeFallback)
                    EnterStraightMarkerFallback();
                return;
            }

            if (distance > junctionActionDistance)
            {
                markerFrames = 0;
                State = MissionState.FollowingLane;
                CountUnusableLaneFrames();
                statusDetail = $"ID {(int)target} detected at {distance:F2} m; action at {junctionActionDistance:F2} m, " +
                               $"laneLost={normalLaneLostFrames}/{laneLostFramesBeforeFallback}";
                if (normalLaneLostFrames >= laneLostFramesBeforeFallback)
                    EnterStraightMarkerFallback();
                return;
            }

            normalLaneLostFrames = 0;
            State = MissionState.ConfirmingNode;
            markerFrames++;
            if (markerFrames >= requiredMarkerFrames)
                ArriveAtTargetNode();
        }

        private void CountUnusableLaneFrames()
        {
            // The mission and motor controller must agree on whether lane following
            // can actually move the robot. A visible boundary pair alone is not enough.
            bool laneUsable = laneFollower.HasUsableLane &&
                              laneFollower.TryGetBoundaryPair(minimumPairConfidence, out _);
            normalLaneLostFrames = laneUsable ? 0 : normalLaneLostFrames + 1;
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

        [ContextMenu("Start Equipment A Mission")]
        public void StartEquipmentAMission()
        {
            if (missionPlanner == null || inspectionPointA1 == null || inspectionPointA2 == null)
            {
                Fail("Equipment A inspection setup is incomplete");
                return;
            }

            StartRoute(
                missionPlanner.BuildMissionRoute(PlantMission.InspectEquipmentA, CurrentNode),
                ActiveMission.EquipmentA);
        }

        [ContextMenu("Start Equipment A And B Mission")]
        public void StartEquipmentAAndBMission()
        {
            if (missionPlanner == null || routeGraph == null ||
                inspectionPointA1 == null || inspectionPointA2 == null)
            {
                Fail("Equipment A/B inspection setup is incomplete");
                return;
            }

            if (!routeGraph.TryGetMarker(PlantNodeId.UpperLeft, out NavigationMarker leftMarker) ||
                !routeGraph.TryGetMarker(PlantNodeId.UpperRight, out NavigationMarker rightMarker))
            {
                Fail("Equipment B placement requires both upper route markers");
                return;
            }

            // The A points sit under a translated waypoint parent, so use the
            // actual lane-to-lane offset rather than negating world X.
            float aisleOffsetX = rightMarker.transform.position.x - leftMarker.transform.position.x;
            if (inspectionPointB1 == null)
                inspectionPointB1 = CreateBInspectionPoint(inspectionPointA1, "inspect_point_B1", aisleOffsetX);
            if (inspectionPointB2 == null)
                inspectionPointB2 = CreateBInspectionPoint(inspectionPointA2, "inspect_point_B2", aisleOffsetX);

            StartRoute(
                missionPlanner.BuildMissionRoute(PlantMission.InspectEquipmentAAndB, CurrentNode),
                ActiveMission.EquipmentAAndB);
        }

        private static Transform CreateBInspectionPoint(Transform source, string pointName, float aisleOffsetX)
        {
            Transform point = Instantiate(source, source.parent);
            point.name = pointName;
            Vector3 position = source.position;
            point.position = new Vector3(position.x + aisleOffsetX, position.y, position.z);
            return point;
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
            avoidanceInterruptedCurrentLeg = false;
            CurrentNode = activeRoute[0];
            targetRouteIndex = 1;
            markerFrames = 0;
            normalLaneLostFrames = 0;
            activeInspectionIndex = 0;
            activeInspectionPoint = null;
            State = MissionState.FollowingLane;
            statusDetail = $"Following to ID {(int)activeRoute[1]} ({activeRoute[1]})";
            laneFollower.ResumeLaneFollowing();
        }

        private bool TryBeginEquipmentInspection()
        {
            int inspectionCount = activeMission == ActiveMission.EquipmentAAndB ? 4 :
                activeMission == ActiveMission.EquipmentA ? 2 : 0;
            if (activeInspectionIndex >= inspectionCount)
                return false;

            Transform target = activeInspectionIndex switch
            {
                0 => inspectionPointA1,
                1 => inspectionPointA2,
                2 => inspectionPointB2,
                _ => inspectionPointB1
            };
            Collider footprint = laneFollower.GetComponent<Collider>();
            Vector3 robotCentre = footprint is BoxCollider box
                ? laneFollower.transform.TransformPoint(box.center)
                : laneFollower.transform.position;
            if (target == null || PlanarDistance(robotCentre, target.position) > inspectionReachDistance)
                return false;

            activeInspectionPoint = target;
            InspectionPoint point = target.GetComponent<InspectionPoint>();
            inspectionTimeRemaining = point != null ? Mathf.Max(0f, point.inspectionTime) : 3f;
            State = MissionState.InspectingEquipment;
            avoidanceAgent?.CancelDemoAvoidance();
            statusDetail = $"Inspecting {target.name}: {inspectionTimeRemaining:F1} s";
            laneFollower.SetDriveEnabled(false);
            return true;
        }

        private void UpdateEquipmentInspection()
        {
            inspectionTimeRemaining = Mathf.Max(0f, inspectionTimeRemaining - Time.deltaTime);
            string pointName = activeInspectionPoint != null ? activeInspectionPoint.name : "inspection point";
            statusDetail = $"Inspecting {pointName}: {inspectionTimeRemaining:F1} s";
            if (inspectionTimeRemaining > 0f)
                return;

            activeInspectionIndex++;
            activeInspectionPoint = null;
            State = MissionState.FollowingLane;
            PlantNodeId target = activeRoute[targetRouteIndex];
            statusDetail = $"Inspection complete; following ID {(int)target} ({target})";
            laneFollower.ResumeLaneFollowing();
        }

        private void ArriveAtTargetNode()
        {
            bool arrivedAfterAvoidance = avoidanceInterruptedCurrentLeg;
            avoidanceInterruptedCurrentLeg = false;
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
            exitLaneRecoveryAttempts = 0;
            activeMinimumApproachDistance = arrivedAfterAvoidance
                ? Mathf.Max(minimumApproachDistance,
                    Mathf.Min(postAvoidanceMinimumApproachDistance, activeApproachDistance - 0.1f))
                : minimumApproachDistance;
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
            bool mayAcceptSideLoss = travelled >= activeMinimumApproachDistance;
            bool newObservation = observationFresh &&
                                  detection.timestamp > lastSideObservationTimestamp;
            if (newObservation)
            {
                lastSideObservationTimestamp = detection.timestamp;
                sideLossFrames = !bothSidesVisible
                    ? sideLossFrames + 1
                    : 0;
            }

            statusDetail =
                $"Approach {travelled:F2}/{activeApproachDistance:F2} m " +
                $"(turn after {activeMinimumApproachDistance:F2}), " +
                $"L={(detection.leftBoundaryVisible ? detection.leftBoundaryConfidence.ToString("F2") : "NO")}, " +
                $"R={(detection.rightBoundaryVisible ? detection.rightBoundaryConfidence.ToString("F2") : "NO")}, " +
                $"sideLost={sideLossFrames}/{requiredSideLossFrames}";

            if (mayAcceptSideLoss && sideLossFrames >= requiredSideLossFrames)
            {
                BeginExitLaneSearch();
                return;
            }

            if (travelled >= activeApproachDistance)
            {
                // Some corners keep both painted entry lines visible all the way
                // to the turn centre. Use the same bounded-distance fallback at
                // every corner instead of faulting before the turn can begin.
                BeginExitLaneSearch();
                return;
            }
            laneFollower.SetManualCommand(activeApproachCommand, 0f);
        }

        private bool TryGetDemoTurnCentre(PlantNodeId entry, PlantNodeId junction,
            out Vector3 turnCentre, out Vector3 incomingDirection)
        {
            turnCentre = default;
            incomingDirection = default;
            if (!routeGraph.TryGetMarker(entry, out NavigationMarker entryMarker) ||
                !routeGraph.TryGetMarker(junction, out NavigationMarker cornerMarker))
                return false;

            incomingDirection = Vector3.ProjectOnPlane(
                cornerMarker.transform.position - entryMarker.transform.position, Vector3.up).normalized;
            if (incomingDirection.sqrMagnitude < 0.5f)
                return false;
            turnCentre = cornerMarker.transform.position + incomingDirection * turnCentrePastMarkerDistance;
            return true;
        }

        private void BeginExitLaneSearch()
        {
            searchStartYaw = laneFollower.transform.eulerAngles.y;
            float plannedAngle = Mathf.DeltaAngle(searchStartYaw, desiredExitYaw);
            minimumSearchAngle = Mathf.Min(minimumTurnBeforePair, Mathf.Abs(plannedAngle) * 0.45f);
            pairFrames = 0;
            exitLaneProbeStarted = false;
            State = MissionState.SearchingExitLane;
            laneFollower.SetManualCommand(0f, GetExitHeadingTurnCommand(plannedAngle));
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
            float signedHeadingError = Mathf.DeltaAngle(
                laneFollower.transform.eulerAngles.y, desiredExitYaw);
            float headingError = Mathf.Abs(signedHeadingError);
            bool headingReady = headingError <= Mathf.Min(exitHeadingTolerance, 12f);
            bool pairUsable = laneFollower.TryGetBoundaryPair(
                minimumPairConfidence, out HsvLaneDetector.Detection detection);
            bool pairVisible = angleReady && headingReady && pairUsable;
            pairFrames = pairVisible ? pairFrames + 1 : Mathf.Max(0, pairFrames - 1);
            if (headingReady && !exitLaneProbeStarted)
            {
                exitLaneProbeStarted = true;
                exitLaneProbeStartPosition = laneFollower.transform.position;
            }
            float probeDistance = exitLaneProbeStarted
                ? PlanarDistance(exitLaneProbeStartPosition, laneFollower.transform.position)
                : 0f;
            float effectivePairConfidence = Mathf.Max(
                detection.boundaryPairConfidence, detection.confidence);
            statusDetail =
                $"Search: angle {turned:F1}/{minimumSearchAngle:F1}, exit yaw error={headingError:F0} " +
                $"ready={(angleReady && headingReady ? "YES" : "NO")}, " +
                $"pair={(detection.hasBoundaryPair ? "YES" : "NO")}, conf={detection.confidence:F2}, " +
                $"pairConf={detection.boundaryPairConfidence:F2}, effective={effectivePairConfidence:F2}, " +
                $"stable={pairFrames}/{requiredPairFrames}, probe={probeDistance:F2}/{maximumExitLaneProbeDistance:F2} m";

            if (pairFrames >= requiredPairFrames)
            {
                alignedFrames = 0;
                pairLostFrames = 0;
                motionStartPosition = laneFollower.transform.position;
                State = MissionState.VisualAlign;
                return;
            }
            if (probeDistance >= maximumExitLaneProbeDistance)
            {
                Fail($"Exit lane not visible after {probeDistance:F2} m at the planned heading");
                return;
            }

            if (headingReady)
                laneFollower.SetManualCommand(exitLaneProbeCommand, 0f);
            else
                laneFollower.SetManualCommand(0f, GetExitHeadingTurnCommand(signedHeadingError));
        }

        private float GetExitHeadingTurnCommand(float headingError)
        {
            if (Mathf.Abs(headingError) <= 8f)
                return 0f;
            float magnitude = Mathf.Clamp(
                Mathf.Abs(headingError) / 45f * activeSearchTurnCommand,
                Mathf.Min(0.10f, activeSearchTurnCommand), activeSearchTurnCommand);
            return Mathf.Sign(headingError) * magnitude;
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
                    if (exitLaneRecoveryAttempts >= maximumExitLaneRecoveryAttempts)
                    {
                        Fail($"Exit lane was not recovered after {exitLaneRecoveryAttempts} searches");
                        return;
                    }
                    exitLaneRecoveryAttempts++;
                    BeginExitLaneSearch();
                    statusDetail = $"Re-searching exit lane ({exitLaneRecoveryAttempts}/{maximumExitLaneRecoveryAttempts})";
                }
                else
                {
                    laneFollower.SetManualCommand(0f, 0f);
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
            if (activeMission == ActiveMission.EquipmentAAndB &&
                CurrentNode == PlantNodeId.UnderMid &&
                activeRoute[targetRouteIndex] == PlantNodeId.UnderRight)
            {
                UpdateRightBottomMarkerApproach();
                return;
            }

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

        private void UpdateRightBottomMarkerApproach()
        {
            if (!TryGetDemoTurnCentre(PlantNodeId.UnderMid, PlantNodeId.UnderRight,
                    out Vector3 turnCentre, out Vector3 incomingDirection))
            {
                Fail("Right-bottom turn centre is missing");
                return;
            }

            Collider footprint = laneFollower.GetComponent<Collider>();
            Vector3 robotCentre = footprint is BoxCollider box
                ? laneFollower.transform.TransformPoint(box.center)
                : laneFollower.transform.position;
            Vector3 toCentre = turnCentre - robotCentre;
            toCentre.y = 0f;
            float travelled = PlanarDistance(motionStartPosition, robotCentre);
            if (travelled >= rightBottomMaximumApproachDistance)
            {
                Fail($"Right-bottom turn centre was not reached within {rightBottomMaximumApproachDistance:F1} m");
                return;
            }

            float remainingAlong = Vector3.Dot(toCentre, incomingDirection);
            float lateralDistance = (toCentre - incomingDirection * remainingAlong).magnitude;
            if (toCentre.magnitude <= junctionActionDistance ||
                (remainingAlong <= 0f && lateralDistance <= junctionActionDistance))
            {
                ArriveAtTargetNode();
                return;
            }

            float headingError = Vector3.SignedAngle(laneFollower.transform.forward, toCentre, Vector3.up);
            float turn = Mathf.Clamp(headingError / 70f * activeSearchTurnCommand,
                -activeSearchTurnCommand, activeSearchTurnCommand);
            float move = Mathf.Abs(headingError) > 40f
                ? 0f
                : straightJunctionCommand * Mathf.Lerp(1f, 0.45f, Mathf.Abs(headingError) / 40f);
            statusDetail = $"Guided to right-bottom turn centre: {toCentre.magnitude:F2} m, " +
                           $"heading {headingError:F0} deg, travelled {travelled:F1} m";
            laneFollower.SetManualCommand(move, turn);
        }

        private void EnterStraightMarkerFallback()
        {
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
            if (TryArriveAtTargetAfterAvoidance())
                return;
            float travelled = PlanarDistance(motionStartPosition, laneFollower.transform.position);
            float elapsed = Time.time - fallbackStartTime;
            if (travelled >= maximumFallbackDistance || elapsed >= maximumFallbackSeconds)
            {
                Fail($"Marker fallback limit reached: {travelled:F1} m, {elapsed:F1} s");
                return;
            }

            PlantNodeId target = activeRoute[targetRouteIndex];
            if (avoidanceInterruptedCurrentLeg && routeGraph.TryGetMarker(target, out NavigationMarker targetMarker))
            {
                // After PPO moves the robot sideways, driving straight may never put
                // the floor marker back in the camera. Use the simulator's route
                // marker position until close enough to resume the original approach.
                Collider footprint = laneFollower.GetComponent<Collider>();
                Vector3 robotCentre = footprint is BoxCollider box
                    ? laneFollower.transform.TransformPoint(box.center)
                    : laneFollower.transform.position;
                Vector3 toMarker = targetMarker.transform.position - robotCentre;
                toMarker.y = 0f;
                float headingError = Vector3.SignedAngle(laneFollower.transform.forward, toMarker, Vector3.up);
                float move = Mathf.Abs(headingError) <= 35f ? fallbackStraightCommand : 0f;
                float turn = Mathf.Clamp(headingError / 90f, -searchTurnCommand, searchTurnCommand);
                statusDetail = $"Returning to marker ID {(int)target} after avoidance: " +
                               $"{toMarker.magnitude:F1} m, heading {headingError:F0} deg";
                laneFollower.SetManualCommand(move, turn);
                return;
            }
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

        private bool TryArriveAtTargetAfterAvoidance()
        {
            if (!avoidanceInterruptedCurrentLeg || targetRouteIndex >= activeRoute.Count ||
                !routeGraph.TryGetMarker(activeRoute[targetRouteIndex], out NavigationMarker marker))
                return false;

            Collider footprint = laneFollower.GetComponent<Collider>();
            Vector3 robotCentre = footprint is BoxCollider box
                ? laneFollower.transform.TransformPoint(box.center)
                : laneFollower.transform.position;
            if (PlanarDistance(robotCentre, marker.transform.position) > junctionActionDistance)
                return false;

            markerFrames++;
            statusDetail = $"Reacquired route at ID {(int)activeRoute[targetRouteIndex]} after avoidance, " +
                           $"confirm={markerFrames}/{requiredMarkerFrames}";
            if (markerFrames >= requiredMarkerFrames)
            {
                ArriveAtTargetNode();
            }
            return true;
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
            avoidanceInterruptedCurrentLeg = false;
            normalLaneLostFrames = 0;
            activeInspectionIndex = 0;
            activeInspectionPoint = null;
            statusDetail = $"Ready at {CurrentNode}";
            laneFollower?.SetDriveEnabled(false);
        }

        private void CompleteMission()
        {
            int requiredInspections = activeMission == ActiveMission.EquipmentAAndB ? 4 :
                activeMission == ActiveMission.EquipmentA ? 2 : 0;
            if (activeInspectionIndex < requiredInspections)
            {
                Fail($"Equipment mission reached the route end with only {activeInspectionIndex}/{requiredInspections} inspections completed");
                return;
            }

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
            Rect panel = new Rect(10f, Screen.height - 90f, 500f, 80f);
            GUI.Box(panel, GUIContent.none);
            GUI.Label(new Rect(panel.x + 10f, panel.y + 7f, panel.width - 20f, 22f),
                $"MISSION: {activeMission}   State: {State}" +
                (avoidancePaused ? " [PPO / ADAS]" : ""), titleStyle);
            GUI.Label(new Rect(panel.x + 10f, panel.y + 31f, panel.width - 20f, 44f),
                $"{statusDetail}\nRoute: {RouteText()}", statusStyle);
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
