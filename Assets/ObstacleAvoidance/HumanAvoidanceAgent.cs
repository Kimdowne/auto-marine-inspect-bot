using System.Collections.Generic;
using ShipRobot.LaneFollowing;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.Serialization;

namespace ShipRobot.ObstacleAvoidance
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public sealed class HumanAvoidanceAgent : Agent
    {
        public enum HumanResponsePhase
        {
            Clear,
            EarlyWarning,
            AvoidanceActive,
            EmergencyStop
        }

        public enum AvoidanceDecision
        {
            Stop = 0,
            Left = 1,
            Right = 2,
            None = 4
        }

        public enum AvoidanceMagnitude
        {
            None = 0,
            Small = 1,
            Medium = 2,
            Large = 3
        }

        public enum TrainingCurriculumStage
        {
            AvoidanceOnly = 0,
            AvoidanceAndLaneRecovery = 1
        }

        public enum TrainingScenarioCurriculum
        {
            ActiveAvoidanceFoundation = 0,
            Mixed = 1
        }

        private enum TrainingOutcome
        {
            Success,
            Collision,
            EmergencyStop,
            LaneExit,
            SafePass,
            PassiveFailure,
            Timeout
        }

        private enum TrainingEncounterClass
        {
            ActiveAvoidanceRequired,
            YieldRequired,
            NaturalPass
        }

        public const int ObservationCount = 20;

        [Header("Connections")]
        [SerializeField] private DualToFSensorRig tofRig;
        [SerializeField] private RgbPersonDetector personDetector;
        [SerializeField] private LaneFollowerController laneFollower;
        [SerializeField] private SafetySupervisor safetySupervisor;
        [SerializeField] private Rigidbody robotBody;
        [SerializeField] private ShipRobot.Navigation.NavigationCoordinator demoMission;
        [Tooltip("Physical lane centre reference. Use front_camera, located between the two front ToF sensors.")]
        [SerializeField] private Transform scenarioReference;

        [Header("Policy gate")]
        [Tooltip("Keep disabled while validating observations. SafetySupervisor remains authoritative.")]
        [SerializeField] private bool applyPolicyActions;
        [FormerlySerializedAs("activationDistance")]
        [SerializeField, Min(0.1f)] private float policyActivationDistance = 1.80f;
        [Tooltip("Virtual lane-centre shifts are normalized camera width, not metres.")]
        [FormerlySerializedAs("maximumAvoidanceLaneOffset")]
        [SerializeField, Range(0f, 0.75f)] private float smallAvoidanceLaneOffset = 0.28f;
        [SerializeField, Range(0f, 0.85f)] private float mediumAvoidanceLaneOffset = 0.45f;
        [SerializeField, Range(0f, 1f)] private float largeAvoidanceLaneOffset = 0.60f;
        [Header("Adaptive avoidance magnitude")]
        [SerializeField, Min(0f)] private float mediumAvoidanceDistance = 1.50f;
        [SerializeField, Min(0f)] private float largeAvoidanceDistance = 1.05f;
        [SerializeField, Min(0f)] private float mediumAvoidanceTtc = 2.25f;
        [SerializeField, Min(0f)] private float largeAvoidanceTtc = 1.25f;
        [SerializeField, Range(0f, 1f)] private float mediumPersonBoxHeight = 0.35f;
        [SerializeField, Range(0f, 1f)] private float largePersonBoxHeight = 0.60f;
        [SerializeField, Min(0f)] private float minimumSelectedSideDistance = 0.55f;
        [SerializeField, Min(0f)] private float deterministicStopDistance = 0.35f;
        [SerializeField, Min(0f)] private float deterministicStopTtc = 0.75f;
        [SerializeField, Min(0f)] private float fullSpeedTtc = 3.0f;
        [SerializeField, Range(0f, 1f)] private float minimumAvoidanceSpeedScale = 0.25f;
        [SerializeField, Min(0f)] private float decisionReleaseDistance = 1.60f;
        [SerializeField, Min(0f)] private float decisionReleaseHoldSeconds = 0.60f;

        [Header("Algorithmic recovery (deployment only)")]
        [SerializeField, Min(1f)] private float recoveryTimeout = 12f;
        private bool recovering;
        private bool recoveryFailed;
        private float recoveryStartedAt;
        private float recoveryLaneSince = -1f;
        private float avoidanceEntryYaw;
        private Vector3 avoidanceEntryPosition;
        private float recoveryHandoffUntil;
        private float policyMove, policyTurn;

        [Header("Debug")]
        [SerializeField] private bool showDebugPanel = true;

        [Header("Training only")]
        [SerializeField] private bool trainingMode;
        [Tooltip("Stage 1 trains collision avoidance only. Stage 2 additionally requires lane recovery.")]
        [SerializeField] private TrainingCurriculumStage trainingStage = TrainingCurriculumStage.AvoidanceOnly;
        [SerializeField] private Transform trainingPerson;
        [SerializeField] private TrainingPedestrianMover trainingMover;
        [SerializeField] private bool randomizeTrainingPerson = true;
        [Tooltip("Distance from the robot centre to the pedestrian's initial position.")]
        [SerializeField] private Vector2 approachSpawnDistanceRange = new Vector2(4.0f, 6.0f);
        [Tooltip("Random horizontal camera angle. The actual value is also clamped inside 75% of the camera FOV.")]
        [SerializeField, Range(0f, 40f)] private float maximumApproachAngleDegrees = 12f;
        [Header("Training encounter curriculum")]
        [SerializeField] private TrainingScenarioCurriculum scenarioCurriculum =
            TrainingScenarioCurriculum.ActiveAvoidanceFoundation;
        [SerializeField] private bool automaticallyAdvanceScenarioCurriculum = true;
        [SerializeField, Min(10)] private int curriculumEvaluationEpisodes = 100;
        [SerializeField, Range(0f, 1f)] private float curriculumAdvanceSuccessRate = 0.70f;
        [Tooltip("Foundation mix: active avoidance / yield / natural = 80 / 20 / 0.")]
        [SerializeField, Range(0f, 1f)] private float foundationActiveAvoidanceProbability = 0.80f;
        [SerializeField, Range(0f, 1f)] private float foundationYieldProbability = 0.20f;
        [Tooltip("Mixed curriculum: active avoidance / yield / natural = 60 / 25 / 15.")]
        [SerializeField, Range(0f, 1f)] private float mixedActiveAvoidanceProbability = 0.60f;
        [SerializeField, Range(0f, 1f)] private float mixedYieldProbability = 0.25f;
        [SerializeField] private Vector2 activeImpactLateralRange = new Vector2(-0.15f, 0.15f);
        [SerializeField] private Vector2 yieldEncounterDistanceRange = new Vector2(2.50f, 3.50f);
        [SerializeField] private Vector2 naturalPassLateralMagnitudeRange = new Vector2(0.80f, 1.10f);
        [Tooltip("Nominal straight-driving speed used only for counterfactual encounter timing.")]
        [SerializeField, Min(0.05f)] private float trainingNominalRobotSpeed = 0.35f;
        [Tooltip("Combined robot/person footprint plus safety margin.")]
        [SerializeField, Min(0.05f)] private float counterfactualSafetyClearance = 0.55f;
        [SerializeField, Range(1, 20)] private int scenarioGenerationAttempts = 8;
        [SerializeField] private Vector2 pedestrianSpeedRange = new Vector2(0.30f, 0.60f);
        [Tooltip("Additional walking distance after passing the near-robot target.")]
        [SerializeField] private Vector2 approachExtraTravelRange = new Vector2(1.0f, 1.5f);
        [SerializeField, Min(0f)] private float completionHoldSeconds = 0.50f;
        [SerializeField, Min(1f)] private float maximumEpisodeSeconds = 28f;
        [Header("Yellow-line track (coordinates relative to Line transform)")]
        [SerializeField] private Transform trackReference;
        // Intersections of the vertical/horizontal yellow stripe centre lines
        // in jetbot_env: outer perimeter minus the two equipment islands.
        [SerializeField] private Rect trackOuterArea = new Rect(-5.07f, -0.70f, 16.34f, 15.61f);
        [SerializeField] private Rect[] trackExcludedAreas = {
            new Rect(-2.88f, 0.883f, 5.68f, 12.917f),
            new Rect(5.11f, 0.88f, 4.51f, 13.01f)
        };
        [SerializeField, Min(0.01f)] private float allowedTrackExitDistance = 0.5f;
        [SerializeField] private float trainingBoundsExitPenalty = -3f;
        private float trackOutsideDistance;

        [Header("Training lane perception override")]
        [SerializeField, Range(0f, 1f)] private float trainingMinimumLaneConfidence = 0.15f;
        [SerializeField, Min(0.05f)] private float trainingMaximumLaneObservationAge = 1.50f;
        [SerializeField, Range(0f, 1f)] private float trainingMinimumConfidenceSpeedScale = 0.55f;
        [SerializeField, Min(0f)] private float stepPenalty = 0.0005f;
        [SerializeField, Min(0f)] private float progressRewardScale = 0.002f;
        [SerializeField, Min(0f)] private float laneErrorPenaltyScale = 0.001f;
        [SerializeField, Min(0f)] private float unsafeDistancePenaltyScale = 0.004f;
        [FormerlySerializedAs("steeringPenaltyScale")]
        [SerializeField, Min(0f)] private float offsetPenaltyScale = 0.0005f;
        [SerializeField] private float successfulAvoidanceReward = 2f;
        [SerializeField] private float yieldStopReward = 1.2f;
        [SerializeField] private float yieldMovingReward = 1.0f;
        [SerializeField] private float safePassReward = 0.05f;
        [SerializeField] private float passiveFailurePenalty = -1f;
        [SerializeField] private float emergencyStopPenalty = -2f;
        [SerializeField] private float collisionPenalty = -5f;
        [SerializeField, Range(0.1f, 1f)] private float maximumLaneError = 0.75f;
        [SerializeField, Min(1)] private int laneFailureDecisionLimit = 15;

        public float LastSpeedScale { get; private set; } = 1f;
        public float TargetLaneOffset { get; private set; }
        public AvoidanceDecision SelectedDecision { get; private set; } = AvoidanceDecision.None;
        public AvoidanceMagnitude CurrentAvoidanceMagnitude { get; private set; } = AvoidanceMagnitude.None;
        public bool IsPolicyActive => decisionLatched;
        public bool HasAvoidanceControl => decisionLatched || recovering || recoveryFailed;
        public bool IsTraining => trainingMode;

        public void CancelDemoAvoidance()
        {
            if (trainingMode) return;
            decisionLatched = decisionPending = recovering = recoveryFailed = false;
            recoveryHandoffUntil = 0f;
            policyMove = policyTurn = 0f;
            clearSince = -1f;
            SelectedDecision = AvoidanceDecision.None;
            laneFollower?.SetContinuousAvoidanceCommand(false);
        }
        public HumanResponsePhase Phase { get; private set; }

        private PersonBearingObservation person;
        private bool hasPerson;
        private GUIStyle titleStyle;
        private GUIStyle valueStyle;
        private Vector3 episodeStartPosition;
        private Quaternion episodeStartRotation;
        private Vector3 robotCentreLocalOffset;
        private Vector3 episodeRobotCentrePosition;
        private Quaternion scenarioReferenceLocalRotation;
        private bool experiencedAvoidance;
        private bool interventionRequired;
        private bool yieldRequired;
        private float stationaryPathClearance = float.PositiveInfinity;
        private float nominalPathClearance = float.PositiveInfinity;
        private TrainingEncounterClass sampledEncounterClass;
        private TrainingEncounterClass encounterClass;
        private readonly Queue<float> recentActiveAvoidanceOutcomes = new Queue<float>();
        private bool episodeTerminating;
        private bool hadUsableLane;
        private int laneFailureDecisionCount;
        private bool episodeStarted;
        private bool episodeOutcomeRecorded;
        private float episodeMinimumDistance = 2f;
        private float episodeMaximumForwardSpeed;
        private string lastOutcomeLabel = "NONE";
        private float pedestrianCompletedAt = -1f;
        private float episodeStartedAt;
        private float clearSince = -1f;
        private bool decisionPending;
        [SerializeField, Min(0.02f)] private float policyDecisionInterval = 0.2f;
        [SerializeField, Min(0f)] private float actionChangePenalty = 0.002f;
        private float nextPolicyDecisionAt;
        private bool decisionLatched;
        private bool decisionForcedToStop;
        private float decisionLatchedAt;
        private AvoidanceMagnitude episodeMaximumAvoidanceMagnitude;

        public override void Initialize()
        {
            if (!trainingMode && demoMission != null) Time.timeScale = 1f;
            if (robotBody == null)
                robotBody = GetComponent<Rigidbody>();
            if (safetySupervisor == null)
                safetySupervisor = GetComponent<SafetySupervisor>();
            if (scenarioReference == null)
                scenarioReference = transform.Find("front_camera");
            episodeStartPosition = transform.position;
            episodeStartRotation = transform.rotation;
            Collider footprint = GetComponent<Collider>();
            robotCentreLocalOffset = footprint is BoxCollider boxCollider
                ? boxCollider.center
                : footprint != null
                    ? transform.InverseTransformPoint(footprint.bounds.center)
                    : Vector3.zero;
            episodeRobotCentrePosition = episodeStartPosition +
                episodeStartRotation * robotCentreLocalOffset;
            if (trackReference == null)
                trackReference = GameObject.Find("Line")?.transform;
            if (trackReference == null && trainingMode)
                throw new System.InvalidOperationException("Assign the yellow-line track reference before training.");
            scenarioReferenceLocalRotation = scenarioReference != null
                ? Quaternion.Inverse(transform.rotation) * scenarioReference.rotation
                : Quaternion.identity;
            ApplyLanePerceptionProfile();
        }

        public override void OnEpisodeBegin()
        {
            if (!trainingMode)
                return;

            if (episodeStarted && !episodeOutcomeRecorded)
                RecordEpisodeOutcome(TrainingOutcome.Timeout);

            episodeTerminating = false;
            episodeStarted = true;
            episodeOutcomeRecorded = false;
            episodeMinimumDistance = 2f;
            episodeMaximumForwardSpeed = 0f;
            experiencedAvoidance = false;
            interventionRequired = false;
            yieldRequired = false;
            stationaryPathClearance = float.PositiveInfinity;
            nominalPathClearance = float.PositiveInfinity;
            sampledEncounterClass = TrainingEncounterClass.ActiveAvoidanceRequired;
            encounterClass = TrainingEncounterClass.ActiveAvoidanceRequired;
            hadUsableLane = false;
            laneFailureDecisionCount = 0;
            pedestrianCompletedAt = -1f;
            episodeStartedAt = Time.time;
            clearSince = -1f;
            decisionPending = false;
            recovering = recoveryFailed = false;
            policyMove = policyTurn = 0f;
            laneFollower?.SetContinuousAvoidanceCommand(false);
            decisionLatched = false;
            nextPolicyDecisionAt = Time.time;
            decisionForcedToStop = false;
            decisionLatchedAt = -1f;
            SelectedDecision = AvoidanceDecision.None;
            CurrentAvoidanceMagnitude = AvoidanceMagnitude.None;
            episodeMaximumAvoidanceMagnitude = AvoidanceMagnitude.None;
            LastSpeedScale = 1f;
            TargetLaneOffset = 0f;
            tofRig?.ResetMeasurements();
            safetySupervisor?.ResetForTrainingEpisode();
            laneFollower?.ResumeLaneFollowing();
            laneFollower?.SetContinuousAvoidanceCommand(false);
            laneFollower?.SetAvoidanceIntent(false, 1f, 0f);
            ApplyLanePerceptionProfile();
            if (robotBody != null)
            {
                robotBody.position = episodeStartPosition;
                robotBody.rotation = episodeStartRotation;
                robotBody.linearVelocity = Vector3.zero;
                robotBody.angularVelocity = Vector3.zero;
            }

            if (trainingPerson != null && randomizeTrainingPerson)
            {
                Transform pathReference = scenarioReference != null ? scenarioReference : transform;
                // This asset's root pivot is far outside its physical footprint. Cache the
                // collider centre in local space so resets are deterministic at any time scale.
                Vector3 scenarioOrigin = episodeRobotCentrePosition;
                if (trainingMover == null)
                    trainingMover = trainingPerson.GetComponent<TrainingPedestrianMover>();
                if (trainingMover != null)
                {
                    float speed = Random.Range(pedestrianSpeedRange.x, pedestrianSpeedRange.y);
                    Quaternion cameraRotation = episodeStartRotation * scenarioReferenceLocalRotation;
                    Vector3 cameraForward = Vector3.ProjectOnPlane(cameraRotation * Vector3.forward, Vector3.up).normalized;
                    Vector3 cameraRight = Vector3.ProjectOnPlane(cameraRotation * Vector3.right, Vector3.up).normalized;
                    if (cameraForward.sqrMagnitude < 0.5f)
                        cameraForward = episodeStartRotation * Vector3.forward;
                    if (cameraRight.sqrMagnitude < 0.5f)
                        cameraRight = episodeStartRotation * Vector3.right;

                    float allowedHalfAngle = GetAllowedApproachHalfAngle(pathReference);
                    sampledEncounterClass = SampleEncounterClass();
                    for (int attempt = 0; attempt < scenarioGenerationAttempts; attempt++)
                    {
                        BuildTrainingScenario(
                            sampledEncounterClass,
                            scenarioOrigin,
                            cameraForward,
                            cameraRight,
                            allowedHalfAngle,
                            speed,
                            out Vector3 start,
                            out Vector3 target,
                            out float extraTravel,
                            out float signedAngle);
                        start.y = trainingPerson.position.y;
                        target.y = start.y;
                        trainingMover.ConfigureApproachScenario(
                            pathReference,
                            start,
                            target,
                            extraTravel,
                            speed,
                            signedAngle);
                        ClassifyCounterfactualEncounter(cameraForward, speed);
                        if (encounterClass == sampledEncounterClass)
                            break;
                    }
                }
                else
                {
                    float forward = Random.Range(approachSpawnDistanceRange.x, approachSpawnDistanceRange.y);
                    Vector3 fallbackPosition = scenarioOrigin + episodeStartRotation *
                        (Vector3.forward * forward);
                    fallbackPosition.y = trainingPerson.position.y;
                    trainingPerson.position = fallbackPosition;
                }
            }
        }

        private float GetAllowedApproachHalfAngle(Transform pathReference)
        {
            float allowed = maximumApproachAngleDegrees;
            Camera camera = pathReference != null ? pathReference.GetComponent<Camera>() : null;
            if (camera == null)
                return allowed;

            float verticalHalfRadians = camera.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float horizontalHalfDegrees = Mathf.Atan(Mathf.Tan(verticalHalfRadians) * camera.aspect) * Mathf.Rad2Deg;
            return Mathf.Min(allowed, horizontalHalfDegrees * 0.75f);
        }

        private TrainingEncounterClass SampleEncounterClass()
        {
            float activeProbability = scenarioCurriculum == TrainingScenarioCurriculum.ActiveAvoidanceFoundation
                ? foundationActiveAvoidanceProbability
                : mixedActiveAvoidanceProbability;
            float yieldProbability = scenarioCurriculum == TrainingScenarioCurriculum.ActiveAvoidanceFoundation
                ? foundationYieldProbability
                : mixedYieldProbability;
            float roll = Random.value;
            if (roll < activeProbability)
                return TrainingEncounterClass.ActiveAvoidanceRequired;
            if (roll < Mathf.Clamp01(activeProbability + yieldProbability))
                return TrainingEncounterClass.YieldRequired;
            return TrainingEncounterClass.NaturalPass;
        }

        private void BuildTrainingScenario(
            TrainingEncounterClass requestedClass,
            Vector3 scenarioOrigin,
            Vector3 cameraForward,
            Vector3 cameraRight,
            float allowedHalfAngle,
            float pedestrianSpeed,
            out Vector3 start,
            out Vector3 target,
            out float extraTravel,
            out float signedAngle)
        {
            signedAngle = Random.Range(-allowedHalfAngle, allowedHalfAngle);
            extraTravel = Random.Range(
                Mathf.Min(approachExtraTravelRange.x, approachExtraTravelRange.y),
                Mathf.Max(approachExtraTravelRange.x, approachExtraTravelRange.y));

            if (requestedClass == TrainingEncounterClass.ActiveAvoidanceRequired)
            {
                float spawnDistance = Random.Range(
                    Mathf.Min(approachSpawnDistanceRange.x, approachSpawnDistanceRange.y),
                    Mathf.Max(approachSpawnDistanceRange.x, approachSpawnDistanceRange.y));
                Vector3 spawnDirection = Quaternion.AngleAxis(signedAngle, Vector3.up) * cameraForward;
                start = scenarioOrigin + spawnDirection * spawnDistance;
                float impactLateral = Random.Range(
                    Mathf.Min(activeImpactLateralRange.x, activeImpactLateralRange.y),
                    Mathf.Max(activeImpactLateralRange.x, activeImpactLateralRange.y));
                target = episodeRobotCentrePosition + cameraRight * impactLateral;
                return;
            }

            float encounterDistance = Random.Range(
                Mathf.Min(yieldEncounterDistanceRange.x, yieldEncounterDistanceRange.y),
                Mathf.Max(yieldEncounterDistanceRange.x, yieldEncounterDistanceRange.y));
            float lateralOffset = requestedClass == TrainingEncounterClass.NaturalPass
                ? RandomSignedMagnitude(naturalPassLateralMagnitudeRange)
                : 0f;
            target = episodeRobotCentrePosition + cameraForward * encounterDistance + cameraRight * lateralOffset;
            float encounterTime = encounterDistance / Mathf.Max(trainingNominalRobotSpeed, 0.05f);
            Vector3 walkingDirection = Quaternion.AngleAxis(signedAngle, Vector3.up) * -cameraForward;
            start = target - walkingDirection * pedestrianSpeed * encounterTime;
        }

        private void ClassifyCounterfactualEncounter(Vector3 nominalRobotForward, float pedestrianSpeed)
        {
            Vector3 start = trainingMover.PlannedStartPosition;
            Vector3 end = trainingMover.PlannedEndPosition;
            stationaryPathClearance = DistancePointToPlanarSegment(episodeRobotCentrePosition, start, end);
            nominalPathClearance = CalculateTimedNominalClearance(
                episodeRobotCentrePosition,
                nominalRobotForward,
                trainingNominalRobotSpeed,
                start,
                end,
                pedestrianSpeed);
            if (stationaryPathClearance <= counterfactualSafetyClearance)
                encounterClass = TrainingEncounterClass.ActiveAvoidanceRequired;
            else if (nominalPathClearance <= counterfactualSafetyClearance)
                encounterClass = TrainingEncounterClass.YieldRequired;
            else
                encounterClass = TrainingEncounterClass.NaturalPass;
            interventionRequired = encounterClass == TrainingEncounterClass.ActiveAvoidanceRequired;
            yieldRequired = encounterClass == TrainingEncounterClass.YieldRequired;
        }

        private static float DistancePointToPlanarSegment(Vector3 point, Vector3 start, Vector3 end)
        {
            Vector2 p = new Vector2(point.x, point.z);
            Vector2 a = new Vector2(start.x, start.z);
            Vector2 b = new Vector2(end.x, end.z);
            Vector2 segment = b - a;
            float lengthSquared = segment.sqrMagnitude;
            if (lengthSquared < 0.000001f)
                return Vector2.Distance(p, a);
            float t = Mathf.Clamp01(Vector2.Dot(p - a, segment) / lengthSquared);
            return Vector2.Distance(p, a + segment * t);
        }

        private static float CalculateTimedNominalClearance(
            Vector3 robotStart,
            Vector3 robotForward,
            float robotSpeed,
            Vector3 pedestrianStart,
            Vector3 pedestrianEnd,
            float pedestrianSpeed)
        {
            Vector2 robotPosition = new Vector2(robotStart.x, robotStart.z);
            Vector2 robotVelocity = new Vector2(robotForward.x, robotForward.z).normalized * robotSpeed;
            Vector2 humanStart = new Vector2(pedestrianStart.x, pedestrianStart.z);
            Vector2 humanPath = new Vector2(
                pedestrianEnd.x - pedestrianStart.x,
                pedestrianEnd.z - pedestrianStart.z);
            float pathLength = humanPath.magnitude;
            if (pathLength < 0.0001f)
                return Vector2.Distance(robotPosition, humanStart);
            Vector2 relativePosition = humanStart - robotPosition;
            Vector2 relativeVelocity = humanPath / pathLength * pedestrianSpeed - robotVelocity;
            float duration = pathLength / Mathf.Max(pedestrianSpeed, 0.05f);
            float relativeSpeedSquared = relativeVelocity.sqrMagnitude;
            float closestTime = relativeSpeedSquared < 0.000001f
                ? 0f
                : Mathf.Clamp(-Vector2.Dot(relativePosition, relativeVelocity) / relativeSpeedSquared, 0f, duration);
            return (relativePosition + relativeVelocity * closestTime).magnitude;
        }

        private static float RandomSignedMagnitude(Vector2 range)
        {
            float magnitude = Random.Range(Mathf.Min(range.x, range.y), Mathf.Max(range.x, range.y));
            return Random.value < 0.5f ? -magnitude : magnitude;
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            hasPerson = personDetector != null && personDetector.TryGetPersonBearing(out person);
            float range = 2f;
            float leftDistance = tofRig != null && tofRig.IsInitialized ? tofRig.LeftDistance : range;
            float rightDistance = tofRig != null && tofRig.IsInitialized ? tofRig.RightDistance : range;

            sensor.AddObservation(Mathf.Clamp01(leftDistance / range));                         // 1
            sensor.AddObservation(Mathf.Clamp01(rightDistance / range));                        // 2
            sensor.AddObservation(Mathf.Clamp01((tofRig?.LeftClosingSpeed ?? 0f) / 2f));         // 3
            sensor.AddObservation(Mathf.Clamp01((tofRig?.RightClosingSpeed ?? 0f) / 2f));        // 4
            sensor.AddObservation(NormalizeTtc(tofRig?.LeftTtc ?? float.PositiveInfinity));      // 5
            sensor.AddObservation(NormalizeTtc(tofRig?.RightTtc ?? float.PositiveInfinity));     // 6
            sensor.AddObservation(hasPerson ? 1f : 0f);                                          // 7
            sensor.AddObservation(hasPerson ? person.normalizedHorizontalPosition : 0f);         // 8
            sensor.AddObservation(hasPerson ? person.confidence : 0f);                           // 9
            sensor.AddObservation(hasPerson ? person.normalizedBoxWidth : 0f);                   // 10
            sensor.AddObservation(hasPerson ? person.normalizedBoxHeight : 0f);                  // 11

            if (laneFollower != null && laneFollower.TryGetLaneDetection(out HsvLaneDetector.Detection lane))
            {
                sensor.AddObservation(lane.lateralError);                                        // 12
                sensor.AddObservation(lane.headingError);                                        // 13
                sensor.AddObservation(lane.confidence);                                          // 14
            }
            else
            {
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
            }

            sensor.AddObservation(laneFollower != null ? laneFollower.MoveCommand : 0f);          // 15
            sensor.AddObservation(laneFollower != null ? laneFollower.TurnCommand : 0f);          // 16
            sensor.AddObservation(robotBody != null ? Mathf.Clamp(robotBody.angularVelocity.y / 5f, -1f, 1f) : 0f); // 17
            sensor.AddObservation(robotBody != null ? Mathf.Clamp(Vector3.Dot(robotBody.linearVelocity, transform.forward) / 2f, -1f, 1f) : 0f); // 18
            Vector3 trackCorrection = transform.InverseTransformDirection(GetTrackCorrection());
            sensor.AddObservation(Mathf.Clamp(trackCorrection.x / allowedTrackExitDistance, -1f, 1f)); // 19
            sensor.AddObservation(Mathf.Clamp(trackCorrection.z / allowedTrackExitDistance, -1f, 1f)); // 20
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            if (!trainingMode && (!decisionPending ||
                (laneFollower != null && laneFollower.IsSafetyStopped)))
            {
                decisionPending = false;
                return;
            }
            decisionPending = false;
            if (!applyPolicyActions || actions.ContinuousActions.Length != 2 || episodeTerminating ||
                (!trainingMode && demoMission != null && !demoMission.IsMotionRequested))
                return;
            if (!decisionLatched)
            {
                avoidanceEntryYaw = transform.eulerAngles.y;
                avoidanceEntryPosition = transform.position;
            }
            float move = Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f);
            float turn = Mathf.Clamp(actions.ContinuousActions[1], -1f, 1f);
            if (trainingMode && decisionLatched)
                AddReward(-actionChangePenalty * (Mathf.Abs(move - policyMove) + Mathf.Abs(turn - policyTurn)));
            policyMove = move;
            policyTurn = turn;
            // Legacy direction labels are diagnostic only; they do not gate commands/rewards.
            SelectedDecision = Mathf.Abs(move) < 0.01f && Mathf.Abs(turn) < 0.01f
                ? AvoidanceDecision.Stop : turn < 0f ? AvoidanceDecision.Left : AvoidanceDecision.Right;
            decisionForcedToStop = false;
            decisionLatched = true;
            recovering = recoveryFailed = false;
            experiencedAvoidance = true;
            Academy.Instance.StatsRecorder.Add("policy/move_command", policyMove);
            Academy.Instance.StatsRecorder.Add("policy/turn_command", policyTurn);
            ApplyDeterministicAvoidance(GetMinimumDistance());
        }

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var actions = actionsOut.ContinuousActions;
            actions[0] = 0f;
            actions[1] = 0f;
        }

        private void FixedUpdate()
        {
            if (!trainingMode && demoMission != null && !demoMission.IsMotionRequested)
            {
                CancelDemoAvoidance();
                return;
            }
            hasPerson = personDetector != null && personDetector.TryGetPersonBearing(out person);
            float minimumDistance = GetMinimumDistance();
            UpdateResponsePhase(minimumDistance);

            if (!trainingMode && laneFollower != null && laneFollower.IsSafetyStopped)
            {
                // Emergency braking owns the drive. Do not consume recovery timeout
                // while stopped by ADAS, or replay commands sampled during the stop.
                if (recovering) recoveryStartedAt += Time.fixedDeltaTime;
                return;
            }

            if (decisionLatched)
                UpdateDecisionRelease(minimumDistance);

            // A briefly valid lane must not leave the robot stranded at the edge
            // immediately after control is handed back to lane following.
            if (!trainingMode && !decisionLatched && !recovering && !recoveryFailed &&
                Time.time < recoveryHandoffUntil && laneFollower != null &&
                !laneFollower.HasUsableLane)
            {
                recovering = true;
                recoveryStartedAt = Time.time;
                recoveryLaneSince = -1f;
                recoveryHandoffUntil = 0f;
            }

            if (applyPolicyActions && !decisionPending && Time.time >= nextPolicyDecisionAt &&
                (trainingMode || decisionLatched || Phase == HumanResponsePhase.AvoidanceActive))
            {
                nextPolicyDecisionAt = Time.time + Mathf.Max(Time.fixedDeltaTime, policyDecisionInterval);
                decisionPending = true;
                RequestDecision();
            }

            ApplyDeterministicAvoidance(minimumDistance);

            if (trainingMode && episodeStarted && !episodeTerminating)
                EvaluateTrainingStep(minimumDistance);
        }

        private void ApplyDeterministicAvoidance(float minimumDistance)
        {
            if (laneFollower == null)
                return;
            laneFollower.SetAvoidanceIntent(false, 1f, 0f);
            TargetLaneOffset = 0f;
            CurrentAvoidanceMagnitude = AvoidanceMagnitude.None;
            if (!applyPolicyActions)
            {
                laneFollower.SetContinuousAvoidanceCommand(false);
                return;
            }
            if (decisionLatched)
            {
                LastSpeedScale = policyMove;
                laneFollower.SetContinuousAvoidanceCommand(true, policyMove, policyTurn);
            }
            else if (recovering)
                UpdateLaneRecovery();
            else
                laneFollower.SetContinuousAvoidanceCommand(recoveryFailed);
        }

        private void UpdateLaneRecovery()
        {
            if (Time.time - recoveryStartedAt >= recoveryTimeout)
            {
                recovering = false;
                recoveryFailed = true;
                laneFollower.SetContinuousAvoidanceCommand(true);
                Debug.LogWarning("Lane recovery timed out; drive held stopped.", this);
                return;
            }
            bool usable = laneFollower.HasUsableLane &&
                laneFollower.TryGetLaneDetection(out HsvLaneDetector.Detection ignored);
            if (usable && laneFollower.TryGetLaneDetection(out HsvLaneDetector.Detection lane))
            {
                float turn = Mathf.Clamp(lane.lateralError * 0.85f + lane.headingError * 0.55f, -0.5f, 0.5f);
                laneFollower.SetContinuousAvoidanceCommand(true, 0.2f, turn);
                bool aligned = Mathf.Abs(lane.lateralError) < 0.2f && Mathf.Abs(lane.headingError) < 0.2f;
                if (!aligned) recoveryLaneSince = -1f;
                else if (recoveryLaneSince < 0f) recoveryLaneSince = Time.time;
                else if (Time.time - recoveryLaneSince >= 0.5f)
                {
                    recovering = false;
                    recoveryHandoffUntil = Time.time + 5f;
                    laneFollower.SetContinuousAvoidanceCommand(false);
                    laneFollower.ResumeLaneFollowing();
                }
            }
            else
            {
                recoveryLaneSince = -1f;
                // The camera can see only one boundary after a wide avoidance.
                // Approach the path through the pre-avoidance pose at low speed,
                // then let the camera take over once both boundaries are usable.
                Vector3 pathRight = Quaternion.Euler(0f, avoidanceEntryYaw, 0f) * Vector3.right;
                Vector3 displacement = transform.position - avoidanceEntryPosition;
                float lateralOffset = Vector3.Dot(displacement, pathRight);
                float targetYaw = avoidanceEntryYaw - Mathf.Atan2(lateralOffset, 1.2f) * Mathf.Rad2Deg;
                float error = Mathf.DeltaAngle(transform.eulerAngles.y, targetYaw);
                float move = Mathf.Abs(lateralOffset) > 0.15f && Mathf.Abs(error) < 55f ? 0.2f : 0f;
                laneFollower.SetContinuousAvoidanceCommand(true, move, Mathf.Clamp(error / 60f, -0.4f, 0.4f));
            }
        }

        private void UpdateDecisionRelease(float minimumDistance)
        {
            if (trainingMode)
                return;

            bool clear = !hasPerson && minimumDistance >= decisionReleaseDistance;
            if (!clear)
            {
                clearSince = -1f;
                return;
            }

            if (clearSince < 0f)
                clearSince = Time.time;
            if (Time.time - clearSince < decisionReleaseHoldSeconds)
                return;

            recovering = true;
            recoveryHandoffUntil = 0f;
            decisionPending = false;
            recoveryFailed = false;
            recoveryStartedAt = Time.time;
            recoveryLaneSince = -1f;
            decisionLatched = false;
            SelectedDecision = AvoidanceDecision.None;
            CurrentAvoidanceMagnitude = AvoidanceMagnitude.None;
            TargetLaneOffset = 0f;
            LastSpeedScale = 1f;
            clearSince = -1f;
        }

        private float GetMinimumDistance() =>
            tofRig != null && tofRig.IsInitialized ? tofRig.MinimumDistance : 2f;

        private void ApplyLanePerceptionProfile()
        {
            if (laneFollower == null)
                return;
            if (trainingMode)
            {
                laneFollower.SetPerceptionOverride(
                    trainingMinimumLaneConfidence,
                    trainingMaximumLaneObservationAge,
                    trainingMinimumConfidenceSpeedScale);
            }
            else
            {
                laneFollower.ClearPerceptionOverride();
            }
        }

        protected override void OnDisable()
        {
            if (laneFollower != null)
            {
                laneFollower.SetAvoidanceIntent(false, 1f, 0f);
                laneFollower.SetContinuousAvoidanceCommand(false);
                laneFollower.ClearPerceptionOverride();
            }
            base.OnDisable();
        }

        private void OnGUI()
        {
            if (!showDebugPanel)
                return;
            EnsureStyles();
            Rect panel = new Rect(Screen.width - 375f, Mathf.Max(280f, Screen.height - 416f) + 76f, 365f, 150f);
            GUI.Box(panel, GUIContent.none);
            GUI.Label(new Rect(panel.x + 10f, panel.y + 6f, 345f, 20f), "RL AVOIDANCE I/O", titleStyle);
            string pedestrianState = trainingMover == null ? "NONE" :
                trainingMover.HasCompletedRoute ? "DONE" :
                trainingMover.HasStartedWalking ? "WALK" :
                trainingMover.IsArmed ? "WAIT" : "OFF";
            float pedestrianDistance = trainingMover != null ? trainingMover.RobotDistanceToPedestrian : 0f;
            float forwardSpeed = GetForwardSpeed();
            if (!trainingMode && demoMission != null)
            {
                GUI.Label(new Rect(panel.x + 10f, panel.y + 29f, 345f, 116f),
                    $"person {hasPerson}  ToF {GetMinimumDistance():F2}m  phase {Phase}\n" +
                    $"PPO latch {decisionLatched} wait {decisionPending} rec {recovering} fault {recoveryFailed}\n" +
                    $"move {policyMove:F2} turn {policyTurn:F2}\n" +
                    $"robot {forwardSpeed:F2} m/s  lane {laneFollower?.HasUsableLane}  drive {laneFollower?.IsDriveEnabled}\n" +
                    $"mission {demoMission.State}  motion {demoMission.IsMotionRequested}  safety-stop {laneFollower?.IsSafetyStopped}\n" +
                    demoMission.StatusDetail,
                    valueStyle);
                return;
            }
            GUI.Label(new Rect(panel.x + 10f, panel.y + 29f, 345f, 116f),
                $"person {hasPerson} x {(hasPerson ? person.normalizedHorizontalPosition : 0f):F2}  phase {Phase}\n" +
                $"decision {SelectedDecision}/{CurrentAvoidanceMagnitude} latched {decisionLatched} forced-stop {decisionForcedToStop}\n" +
                $"PPO move {policyMove:F2} turn {policyTurn:F2} recovery {recovering} fault {recoveryFailed}\n" +
                $"robot speed {forwardSpeed:F2} m/s  episode max {episodeMaximumForwardSpeed:F2} m/s\n" +
                $"active {IsPolicyActive} apply {applyPolicyActions} train {trainingMode}/{trainingStage} connected {Academy.Instance.IsCommunicatorOn}\n" +
                $"reward {GetCumulativeReward():F2} last {lastOutcomeLabel}\n" +
                $"pedestrian {trainingMover?.ScenarioLabel ?? "NONE"} {pedestrianState} " +
                $"range {pedestrianDistance:F2} m\n" +
                $"scenario {encounterClass} (sampled {sampledEncounterClass})\n" +
                $"clearance stop {stationaryPathClearance:F2} m / drive {nominalPathClearance:F2} m", valueStyle);
        }

        private void UpdateResponsePhase(float minimumDistance)
        {
            bool emergencyStopped = safetySupervisor != null && safetySupervisor.EnforceControl &&
                (safetySupervisor.State == SafetySupervisor.SafetyState.EmergencyStop ||
                 safetySupervisor.State == SafetySupervisor.SafetyState.WaitingForClear);
            if (emergencyStopped)
                Phase = HumanResponsePhase.EmergencyStop;
            else if (!hasPerson)
                Phase = HumanResponsePhase.Clear;
            else if (minimumDistance < policyActivationDistance)
                Phase = HumanResponsePhase.AvoidanceActive;
            else
                Phase = HumanResponsePhase.EarlyWarning;
        }

        private void OnDrawGizmosSelected()
        {
            if (trackReference == null) return;
            Matrix4x4 previous = Gizmos.matrix;
            Gizmos.matrix = trackReference.localToWorldMatrix;
            Gizmos.color = Color.cyan;
            DrawTrackRect(trackOuterArea);
            Gizmos.color = Color.yellow;
            foreach (Rect hole in trackExcludedAreas) DrawTrackRect(hole);
            Gizmos.matrix = previous;
            if (Application.isPlaying)
            {
                Vector3 centre = transform.TransformPoint(robotCentreLocalOffset);
                Gizmos.color = Color.red;
                Gizmos.DrawLine(centre, centre + GetTrackCorrection());
            }
        }

        private static void DrawTrackRect(Rect area)
        {
            Vector3 a = new Vector3(area.xMin, 0f, area.yMin);
            Vector3 b = new Vector3(area.xMax, 0f, area.yMin);
            Vector3 c = new Vector3(area.xMax, 0f, area.yMax);
            Vector3 d = new Vector3(area.xMin, 0f, area.yMax);
            Gizmos.DrawLine(a, b); Gizmos.DrawLine(b, c);
            Gizmos.DrawLine(c, d); Gizmos.DrawLine(d, a);
        }

        private Vector3 GetTrackCorrection()
        {
            if (trackReference == null) return Vector3.zero;
            Vector3 centre = transform.TransformPoint(robotCentreLocalOffset);
            Vector3 local = trackReference.InverseTransformPoint(centre);
            Vector2 point = new Vector2(local.x, local.z);
            Vector2 nearest = new Vector2(
                Mathf.Clamp(point.x, trackOuterArea.xMin, trackOuterArea.xMax),
                Mathf.Clamp(point.y, trackOuterArea.yMin, trackOuterArea.yMax));
            foreach (Rect hole in trackExcludedAreas)
            {
                if (!hole.Contains(nearest)) continue;
                Vector2[] candidates = {
                    new Vector2(hole.xMin, nearest.y), new Vector2(hole.xMax, nearest.y),
                    new Vector2(nearest.x, hole.yMin), new Vector2(nearest.x, hole.yMax)
                };
                float best = float.PositiveInfinity;
                foreach (Vector2 candidate in candidates)
                {
                    Vector3 delta = trackReference.TransformVector(
                        new Vector3(candidate.x - point.x, 0f, candidate.y - point.y));
                    if (delta.sqrMagnitude >= best) continue;
                    best = delta.sqrMagnitude;
                    nearest = candidate;
                }
            }
            return trackReference.TransformVector(new Vector3(nearest.x - point.x, 0f, nearest.y - point.y));
        }

        private void EvaluateTrainingStep(float minimumDistance)
        {
            if (episodeTerminating)
                return;

            trackOutsideDistance = GetTrackCorrection().magnitude;
            bool outsideBounds = trackOutsideDistance > allowedTrackExitDistance;
            var boundsStats = Academy.Instance.StatsRecorder;
            boundsStats.Add("HumanAvoidance/Bounds/OutsideTrackMetres", trackOutsideDistance);
            if (outsideBounds)
            {
                boundsStats.Add("HumanAvoidance/Bounds/Exit", 1f);
                FinishTrainingEpisode(trainingBoundsExitPenalty, TrainingOutcome.LaneExit);
                return;
            }

            AddReward(-stepPenalty);
            HsvLaneDetector.Detection lane = default;
            bool laneUsable = laneFollower != null && laneFollower.TryGetLaneDetection(out lane);
            float forwardSpeed = GetForwardSpeed();
            episodeMaximumForwardSpeed = Mathf.Max(episodeMaximumForwardSpeed, forwardSpeed);
            episodeMinimumDistance = Mathf.Min(episodeMinimumDistance, minimumDistance);
            RecordTrainingStepMetrics(minimumDistance, laneUsable, lane);
            if (laneUsable)
            {
                hadUsableLane = true;
                bool outsideLane = Mathf.Abs(lane.lateralError) > maximumLaneError;
                laneFailureDecisionCount = outsideLane ? laneFailureDecisionCount + 1 : 0;
            }
            else if (decisionLatched && laneFollower != null &&
                     (laneFollower.IsUsingAvoidanceLaneLossFallback ||
                      CurrentAvoidanceMagnitude == AvoidanceMagnitude.Large))
            {
                // A person can temporarily occlude the yellow boundaries during a
                // deliberate avoidance manoeuvre. This is not a policy lane exit.
                laneFailureDecisionCount = 0;
            }
            else if (hadUsableLane)
            {
                laneFailureDecisionCount++;
            }

            if (trainingStage == TrainingCurriculumStage.AvoidanceAndLaneRecovery &&
                !decisionLatched && laneFailureDecisionCount >= laneFailureDecisionLimit)
            {
                // Lane perception/control failures are outside the high-level policy's
                // action space, so exclude them from PPO reward rather than blaming it.
                InterruptTrainingEpisode(TrainingOutcome.LaneExit);
                return;
            }

            if (IsPolicyActive)
            {
                // Do not reward driving toward a pedestrian. Progress only becomes
                // useful after the encounter has cleared from both sensing gates.
                if (!hasPerson && minimumDistance >= policyActivationDistance)
                    AddReward(Mathf.Clamp01(forwardSpeed) * progressRewardScale);

                if (trainingStage == TrainingCurriculumStage.AvoidanceAndLaneRecovery && laneUsable)
                    AddReward(-Mathf.Abs(lane.lateralError) * laneErrorPenaltyScale);

                float danger = 1f - Mathf.InverseLerp(0.8f, policyActivationDistance, minimumDistance);
                AddReward(-danger * unsafeDistancePenaltyScale);
                AddReward(-Mathf.Abs(TargetLaneOffset) * offsetPenaltyScale);
            }

            if (Phase == HumanResponsePhase.EmergencyStop)
            {
                FinishTrainingEpisode(emergencyStopPenalty, TrainingOutcome.EmergencyStop);
                return;
            }

            if (Time.time - episodeStartedAt >= maximumEpisodeSeconds)
            {
                FinishTrainingEpisode(emergencyStopPenalty, TrainingOutcome.Timeout);
                return;
            }

            bool pedestrianRouteComplete = trainingMover == null || trainingMover.HasCompletedRoute;
            if (pedestrianRouteComplete)
            {
                if (pedestrianCompletedAt < 0f)
                    pedestrianCompletedAt = Time.time;
                bool laneRequirementSatisfied =
                    trainingStage == TrainingCurriculumStage.AvoidanceOnly ||
                    (laneUsable && Mathf.Abs(lane.lateralError) <= maximumLaneError);
                if (laneRequirementSatisfied && Time.time - pedestrianCompletedAt >= completionHoldSeconds)
                {
                    FinishCompletedEncounter();
                }
            }
        }

        private void FinishCompletedEncounter()
        {
            if (encounterClass == TrainingEncounterClass.ActiveAvoidanceRequired)
            {
                FinishTrainingEpisode(successfulAvoidanceReward, TrainingOutcome.Success);
                return;
            }

            if (encounterClass == TrainingEncounterClass.YieldRequired)
            {
                if (SelectedDecision == AvoidanceDecision.None)
                {
                    FinishTrainingEpisode(passiveFailurePenalty, TrainingOutcome.PassiveFailure);
                    return;
                }
                FinishTrainingEpisode(
                    yieldMovingReward,
                    TrainingOutcome.Success);
                return;
            }

            FinishTrainingEpisode(safePassReward, TrainingOutcome.SafePass);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!trainingMode || episodeTerminating)
                return;
            Transform root = collision.transform.root;
            if (root.CompareTag("Human") || collision.gameObject.CompareTag("Obstacle") ||
                root.name.IndexOf("Handyman", System.StringComparison.OrdinalIgnoreCase) >= 0)
                FinishTrainingEpisode(collisionPenalty, TrainingOutcome.Collision);
        }

        private void FinishTrainingEpisode(float terminalReward, TrainingOutcome outcome)
        {
            episodeTerminating = true;
            AddReward(terminalReward);
            RecordEpisodeOutcome(outcome);
            laneFollower?.SetContinuousAvoidanceCommand(false);
            laneFollower?.SetAvoidanceIntent(false, 1f, 0f);
            EndEpisode();
        }

        private void InterruptTrainingEpisode(TrainingOutcome outcome)
        {
            episodeTerminating = true;
            RecordEpisodeOutcome(outcome);
            laneFollower?.SetContinuousAvoidanceCommand(false);
            laneFollower?.SetAvoidanceIntent(false, 1f, 0f);
            EpisodeInterrupted();
        }

        private void RecordTrainingStepMetrics(
            float minimumDistance,
            bool laneUsable,
            HsvLaneDetector.Detection lane)
        {
            StatsRecorder stats = Academy.Instance.StatsRecorder;
            float minimumTtc = tofRig != null ? tofRig.MinimumTtc : float.PositiveInfinity;
            float cappedTtc = float.IsPositiveInfinity(minimumTtc) ? 5f : Mathf.Min(minimumTtc, 5f);
            stats.Add("HumanAvoidance/Sensors/MinimumDistance", minimumDistance);
            stats.Add("HumanAvoidance/Sensors/MinimumTTC", cappedTtc);
            stats.Add("HumanAvoidance/Sensors/PersonDetected", hasPerson ? 1f : 0f);
            stats.Add("HumanAvoidance/Sensors/PersonConfidence", hasPerson ? person.confidence : 0f);
            stats.Add("HumanAvoidance/Lane/Usable", laneUsable ? 1f : 0f);
            stats.Add("HumanAvoidance/Lane/RawConfidence", lane.confidence);
            float laneAge = lane.timestamp > 0d
                ? Mathf.Clamp((float)(Time.timeAsDouble - lane.timestamp), 0f, 5f)
                : 5f;
            stats.Add("HumanAvoidance/Lane/ObservationAge", laneAge);
            stats.Add("HumanAvoidance/Lane/AbsoluteError", laneUsable ? Mathf.Abs(lane.lateralError) : 1f);
            stats.Add("HumanAvoidance/Policy/Active", IsPolicyActive ? 1f : 0f);
            stats.Add("HumanAvoidance/Policy/Latched", decisionLatched ? 1f : 0f);
            stats.Add("HumanAvoidance/Policy/RuleSpeedScale", LastSpeedScale);
            stats.Add("HumanAvoidance/Policy/MoveCommand", policyMove);
            stats.Add("HumanAvoidance/Policy/TurnCommand", policyTurn);
            stats.Add("HumanAvoidance/Policy/AvoidanceMagnitude", (float)CurrentAvoidanceMagnitude);
            stats.Add("HumanAvoidance/Robot/ForwardSpeed", GetForwardSpeed());
            stats.Add("HumanAvoidance/Robot/LinearSpeed",
                robotBody != null ? robotBody.linearVelocity.magnitude : 0f);
            stats.Add("HumanAvoidance/Safety/AppliedSpeedScale",
                safetySupervisor != null ? safetySupervisor.AppliedSpeedScale : 1f);
        }

        private void RecordEpisodeOutcome(TrainingOutcome outcome)
        {
            if (episodeOutcomeRecorded)
                return;
            episodeOutcomeRecorded = true;
            lastOutcomeLabel = outcome.ToString();
            StatsRecorder stats = Academy.Instance.StatsRecorder;
            bool success = outcome == TrainingOutcome.Success || outcome == TrainingOutcome.SafePass;
            stats.Add("HumanAvoidance/Episode/SuccessRate", success ? 1f : 0f);
            stats.Add("HumanAvoidance/Episode/AvoidanceSuccessRate", outcome == TrainingOutcome.Success ? 1f : 0f);
            stats.Add("HumanAvoidance/Episode/SafePassRate", outcome == TrainingOutcome.SafePass ? 1f : 0f);
            stats.Add("HumanAvoidance/Episode/CollisionRate", outcome == TrainingOutcome.Collision ? 1f : 0f);
            stats.Add("HumanAvoidance/Episode/EmergencyStopRate",
                outcome == TrainingOutcome.EmergencyStop ? 1f : 0f);
            stats.Add("HumanAvoidance/Episode/LaneExitRate", outcome == TrainingOutcome.LaneExit ? 1f : 0f);
            stats.Add("HumanAvoidance/Episode/TimeoutRate", outcome == TrainingOutcome.Timeout ? 1f : 0f);
            stats.Add("HumanAvoidance/Episode/PassiveFailureRate",
                outcome == TrainingOutcome.PassiveFailure ? 1f : 0f);
            stats.Add("HumanAvoidance/Episode/MinimumDistance", episodeMinimumDistance);
            stats.Add("HumanAvoidance/Episode/MaximumForwardSpeed", episodeMaximumForwardSpeed);
            stats.Add("HumanAvoidance/Episode/ExperiencedAvoidance", experiencedAvoidance ? 1f : 0f);
            stats.Add("HumanAvoidance/Episode/InterventionRequired", interventionRequired ? 1f : 0f);
            stats.Add("HumanAvoidance/Episode/InterventionSuccessRate",
                interventionRequired && outcome == TrainingOutcome.Success ? 1f : 0f);
            stats.Add("HumanAvoidance/Episode/ActiveAvoidanceRequired", interventionRequired ? 1f : 0f);
            stats.Add("HumanAvoidance/Episode/ActiveAvoidanceSuccessRate",
                interventionRequired && outcome == TrainingOutcome.Success ? 1f : 0f);
            stats.Add("HumanAvoidance/Episode/YieldRequired", yieldRequired ? 1f : 0f);
            stats.Add("HumanAvoidance/Episode/YieldSuccessRate",
                yieldRequired && outcome == TrainingOutcome.Success ? 1f : 0f);
            stats.Add("HumanAvoidance/Episode/NaturalSafePassRate",
                !interventionRequired && outcome == TrainingOutcome.SafePass ? 1f : 0f);
            stats.Add("HumanAvoidance/Episode/StationaryPathClearance",
                float.IsPositiveInfinity(stationaryPathClearance) ? 5f : stationaryPathClearance);
            stats.Add("HumanAvoidance/Episode/NominalPathClearance",
                float.IsPositiveInfinity(nominalPathClearance) ? 5f : nominalPathClearance);
            stats.Add("HumanAvoidance/Curriculum/AvoidanceOnly",
                trainingStage == TrainingCurriculumStage.AvoidanceOnly ? 1f : 0f);
            stats.Add("HumanAvoidance/Curriculum/LaneRecovery",
                trainingStage == TrainingCurriculumStage.AvoidanceAndLaneRecovery ? 1f : 0f);
            stats.Add("HumanAvoidance/Decision/StopRate",
                SelectedDecision == AvoidanceDecision.Stop ? 1f : 0f);
            stats.Add("HumanAvoidance/Decision/LeftRate",
                SelectedDecision == AvoidanceDecision.Left ? 1f : 0f);
            stats.Add("HumanAvoidance/Decision/RightRate",
                SelectedDecision == AvoidanceDecision.Right ? 1f : 0f);
            stats.Add("HumanAvoidance/Decision/ReverseRate",
                0f); // Retain the legacy log key for existing dashboards.
            stats.Add("HumanAvoidance/Decision/NoneRate",
                SelectedDecision == AvoidanceDecision.None ? 1f : 0f);
            stats.Add("HumanAvoidance/Decision/ForcedStopRate", decisionForcedToStop ? 1f : 0f);
            stats.Add("HumanAvoidance/Episode/MaxMagnitudeSmallRate",
                episodeMaximumAvoidanceMagnitude == AvoidanceMagnitude.Small ? 1f : 0f);
            stats.Add("HumanAvoidance/Episode/MaxMagnitudeMediumRate",
                episodeMaximumAvoidanceMagnitude == AvoidanceMagnitude.Medium ? 1f : 0f);
            stats.Add("HumanAvoidance/Episode/MaxMagnitudeLargeRate",
                episodeMaximumAvoidanceMagnitude == AvoidanceMagnitude.Large ? 1f : 0f);
            TrainingPedestrianMover.ScenarioKind scenario = trainingMover != null
                ? trainingMover.Scenario
                : TrainingPedestrianMover.ScenarioKind.ApproachCentre;
            stats.Add("HumanAvoidance/Scenario/ApproachLeftRate",
                scenario == TrainingPedestrianMover.ScenarioKind.ApproachLeft ? 1f : 0f);
            stats.Add("HumanAvoidance/Scenario/ApproachCentreRate",
                scenario == TrainingPedestrianMover.ScenarioKind.ApproachCentre ? 1f : 0f);
            stats.Add("HumanAvoidance/Scenario/ApproachRightRate",
                scenario == TrainingPedestrianMover.ScenarioKind.ApproachRight ? 1f : 0f);
            stats.Add("HumanAvoidance/Scenario/ApproachAngleDegrees",
                trainingMover != null ? trainingMover.ApproachAngleDegrees : 0f);
            stats.Add("HumanAvoidance/Scenario/InterventionMix",
                sampledEncounterClass == TrainingEncounterClass.ActiveAvoidanceRequired ? 1f : 0f);
            stats.Add("HumanAvoidance/Scenario/ModerateMix",
                sampledEncounterClass == TrainingEncounterClass.YieldRequired ? 1f : 0f);
            stats.Add("HumanAvoidance/Scenario/NaturalPassMix",
                sampledEncounterClass == TrainingEncounterClass.NaturalPass ? 1f : 0f);
            stats.Add("HumanAvoidance/Scenario/ClassificationMatch",
                sampledEncounterClass == encounterClass ? 1f : 0f);
            stats.Add("HumanAvoidance/Curriculum/ScenarioFoundation",
                scenarioCurriculum == TrainingScenarioCurriculum.ActiveAvoidanceFoundation ? 1f : 0f);
            stats.Add("HumanAvoidance/Curriculum/ScenarioMixed",
                scenarioCurriculum == TrainingScenarioCurriculum.Mixed ? 1f : 0f);
            UpdateScenarioCurriculum(outcome);
        }

        private void UpdateScenarioCurriculum(TrainingOutcome outcome)
        {
            if (encounterClass != TrainingEncounterClass.ActiveAvoidanceRequired)
                return;
            recentActiveAvoidanceOutcomes.Enqueue(outcome == TrainingOutcome.Success ? 1f : 0f);
            int window = Mathf.Max(10, curriculumEvaluationEpisodes);
            while (recentActiveAvoidanceOutcomes.Count > window)
                recentActiveAvoidanceOutcomes.Dequeue();
            if (!automaticallyAdvanceScenarioCurriculum ||
                scenarioCurriculum != TrainingScenarioCurriculum.ActiveAvoidanceFoundation ||
                recentActiveAvoidanceOutcomes.Count < window)
                return;

            float successes = 0f;
            foreach (float value in recentActiveAvoidanceOutcomes)
                successes += value;
            if (successes / recentActiveAvoidanceOutcomes.Count >= curriculumAdvanceSuccessRate)
                scenarioCurriculum = TrainingScenarioCurriculum.Mixed;
        }

        private static float NormalizeTtc(float value) =>
            float.IsPositiveInfinity(value) ? 1f : Mathf.Clamp01(value / 5f);

        private float GetForwardSpeed()
        {
            return robotBody != null
                ? Mathf.Max(0f, Vector3.Dot(robotBody.linearVelocity, transform.forward))
                : 0f;
        }

        private void EnsureStyles()
        {
            if (titleStyle != null)
                return;
            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.55f, 0f) }
            };
            valueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal = { textColor = Color.white }
            };
        }
    }
}
