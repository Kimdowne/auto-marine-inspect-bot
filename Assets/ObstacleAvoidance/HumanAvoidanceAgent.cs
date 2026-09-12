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
            Reverse = 3,
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

        public const int ObservationCount = 18;

        [Header("Connections")]
        [SerializeField] private DualToFSensorRig tofRig;
        [SerializeField] private RgbPersonDetector personDetector;
        [SerializeField] private LaneFollowerController laneFollower;
        [SerializeField] private SafetySupervisor safetySupervisor;
        [SerializeField] private Rigidbody robotBody;
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

        [Header("Reverse safety")]
        [Tooltip("Magnitude of the signed lane-follower command while reversing.")]
        [SerializeField, Range(0.05f, 1f)] private float reverseSpeedScale = 0.30f;
        [SerializeField, Min(0.1f)] private float maximumReverseSeconds = 1.00f;
        [SerializeField, Min(0.1f)] private float rearProbeRange = 2.00f;
        [SerializeField, Min(0.05f)] private float minimumRearClearance = 0.60f;
        [SerializeField, Min(0f)] private float rearProbeHeight = 0.15f;
        [SerializeField] private LayerMask rearObstacleMask = ~0;

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
        private bool decisionLatched;
        private bool decisionForcedToStop;
        private float decisionLatchedAt;
        private AvoidanceMagnitude episodeMaximumAvoidanceMagnitude;

        public override void Initialize()
        {
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
            decisionLatched = false;
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
            sensor.AddObservation(Mathf.Clamp01(GetRearClearance() / Mathf.Max(rearProbeRange, 0.01f))); // 18
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            decisionPending = false;
            if (decisionLatched || actions.DiscreteActions.Length == 0)
                return;

            AvoidanceDecision requested = (AvoidanceDecision)Mathf.Clamp(actions.DiscreteActions[0], 0, 3);
            SelectedDecision = ValidateDecision(requested);
            decisionForcedToStop = requested != AvoidanceDecision.Stop &&
                                   SelectedDecision == AvoidanceDecision.Stop;
            decisionLatched = true;
            decisionLatchedAt = Time.time;
            experiencedAvoidance = true;
            ApplyDeterministicAvoidance(GetMinimumDistance());
        }

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            // A missing model must fail safely instead of inventing a passing direction.
            ActionSegment<int> actions = actionsOut.DiscreteActions;
            actions[0] = (int)AvoidanceDecision.Stop;
        }

        private void FixedUpdate()
        {
            hasPerson = personDetector != null && personDetector.TryGetPersonBearing(out person);
            float minimumDistance = GetMinimumDistance();
            UpdateResponsePhase(minimumDistance);

            if (applyPolicyActions && !decisionLatched && !decisionPending &&
                Phase == HumanResponsePhase.AvoidanceActive)
            {
                decisionPending = true;
                RequestDecision();
            }

            if (decisionLatched)
                UpdateDecisionRelease(minimumDistance);
            ApplyDeterministicAvoidance(minimumDistance);

            if (trainingMode && episodeStarted && !episodeTerminating)
                EvaluateTrainingStep(minimumDistance);
        }

        private AvoidanceDecision ValidateDecision(AvoidanceDecision requested)
        {
            if (requested == AvoidanceDecision.Reverse)
                return GetRearClearance() >= minimumRearClearance
                    ? requested
                    : AvoidanceDecision.Stop;
            if (requested == AvoidanceDecision.Stop || tofRig == null || !tofRig.IsInitialized)
                return requested;

            float selectedDistance = requested == AvoidanceDecision.Left
                ? tofRig.LeftDistance
                : tofRig.RightDistance;
            return selectedDistance >= minimumSelectedSideDistance
                ? requested
                : AvoidanceDecision.Stop;
        }

        private void ApplyDeterministicAvoidance(float minimumDistance)
        {
            if (laneFollower == null)
                return;
            if (!applyPolicyActions || !decisionLatched)
            {
                LastSpeedScale = 1f;
                TargetLaneOffset = 0f;
                CurrentAvoidanceMagnitude = AvoidanceMagnitude.None;
                laneFollower.SetAvoidanceIntent(false, 1f, 0f);
                return;
            }

            if (SelectedDecision == AvoidanceDecision.Stop)
            {
                LastSpeedScale = 0f;
                TargetLaneOffset = 0f;
                CurrentAvoidanceMagnitude = AvoidanceMagnitude.None;
                laneFollower.SetAvoidanceIntent(true, 0f, 0f);
                return;
            }

            if (SelectedDecision == AvoidanceDecision.Reverse)
            {
                bool reverseTimeAvailable = Time.time - decisionLatchedAt < maximumReverseSeconds;
                bool rearIsClear = GetRearClearance() >= minimumRearClearance;
                LastSpeedScale = reverseTimeAvailable && rearIsClear ? -reverseSpeedScale : 0f;
                TargetLaneOffset = 0f;
                CurrentAvoidanceMagnitude = AvoidanceMagnitude.None;
                laneFollower.SetAvoidanceIntent(true, LastSpeedScale, 0f);
                return;
            }

            float selectedDistance = tofRig == null || !tofRig.IsInitialized
                ? 2f
                : SelectedDecision == AvoidanceDecision.Left
                    ? tofRig.LeftDistance
                    : tofRig.RightDistance;
            float minimumTtc = tofRig != null ? tofRig.MinimumTtc : float.PositiveInfinity;
            bool mustStop = selectedDistance < minimumSelectedSideDistance ||
                            minimumDistance <= deterministicStopDistance ||
                            (!float.IsPositiveInfinity(minimumTtc) && minimumTtc <= deterministicStopTtc);

            float distanceScale = Mathf.InverseLerp(
                deterministicStopDistance,
                policyActivationDistance,
                minimumDistance);
            float ttcScale = float.IsPositiveInfinity(minimumTtc)
                ? 1f
                : Mathf.InverseLerp(deterministicStopTtc, fullSpeedTtc, minimumTtc);
            LastSpeedScale = mustStop
                ? 0f
                : Mathf.Lerp(minimumAvoidanceSpeedScale, 1f, Mathf.Min(distanceScale, ttcScale));
            CurrentAvoidanceMagnitude = SelectAvoidanceMagnitude(minimumDistance, minimumTtc);
            if ((int)CurrentAvoidanceMagnitude > (int)episodeMaximumAvoidanceMagnitude)
                episodeMaximumAvoidanceMagnitude = CurrentAvoidanceMagnitude;
            TargetLaneOffset = (SelectedDecision == AvoidanceDecision.Left ? -1f : 1f) *
                GetAvoidanceOffset(CurrentAvoidanceMagnitude);
            laneFollower.SetAvoidanceIntent(true, LastSpeedScale, TargetLaneOffset);
        }

        private AvoidanceMagnitude SelectAvoidanceMagnitude(float minimumDistance, float minimumTtc)
        {
            float boxHeight = hasPerson ? person.normalizedBoxHeight : 0f;
            if (minimumDistance <= largeAvoidanceDistance ||
                (!float.IsPositiveInfinity(minimumTtc) && minimumTtc <= largeAvoidanceTtc) ||
                boxHeight >= largePersonBoxHeight)
                return AvoidanceMagnitude.Large;
            if (minimumDistance <= mediumAvoidanceDistance ||
                (!float.IsPositiveInfinity(minimumTtc) && minimumTtc <= mediumAvoidanceTtc) ||
                boxHeight >= mediumPersonBoxHeight)
                return AvoidanceMagnitude.Medium;
            return AvoidanceMagnitude.Small;
        }

        private float GetAvoidanceOffset(AvoidanceMagnitude magnitude)
        {
            switch (magnitude)
            {
                case AvoidanceMagnitude.Large: return largeAvoidanceLaneOffset;
                case AvoidanceMagnitude.Medium: return mediumAvoidanceLaneOffset;
                case AvoidanceMagnitude.Small: return smallAvoidanceLaneOffset;
                default: return 0f;
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

            decisionLatched = false;
            SelectedDecision = AvoidanceDecision.None;
            CurrentAvoidanceMagnitude = AvoidanceMagnitude.None;
            TargetLaneOffset = 0f;
            LastSpeedScale = 1f;
            clearSince = -1f;
        }

        private float GetMinimumDistance() =>
            tofRig != null && tofRig.IsInitialized ? tofRig.MinimumDistance : 2f;

        private float GetRearClearance()
        {
            Vector3 origin = transform.position + Vector3.up * rearProbeHeight;
            RaycastHit[] hits = Physics.RaycastAll(
                origin,
                -transform.forward,
                rearProbeRange,
                rearObstacleMask,
                QueryTriggerInteraction.Ignore);
            float nearest = rearProbeRange;
            foreach (RaycastHit hit in hits)
            {
                if (hit.transform == null || hit.transform.root == transform.root)
                    continue;
                nearest = Mathf.Min(nearest, hit.distance);
            }
            return nearest;
        }

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
                laneFollower.ClearPerceptionOverride();
            }
            base.OnDisable();
        }

        private void OnGUI()
        {
            if (!showDebugPanel)
                return;
            EnsureStyles();
            Rect panel = new Rect(Screen.width - 375f, Screen.height - 500f, 365f, 158f);
            GUI.Box(panel, GUIContent.none);
            GUI.Label(new Rect(panel.x + 10f, panel.y + 6f, 345f, 20f), "RL AVOIDANCE I/O", titleStyle);
            string pedestrianState = trainingMover == null ? "NONE" :
                trainingMover.HasCompletedRoute ? "DONE" :
                trainingMover.HasStartedWalking ? "WALK" :
                trainingMover.IsArmed ? "WAIT" : "OFF";
            float pedestrianDistance = trainingMover != null ? trainingMover.RobotDistanceToPedestrian : 0f;
            float forwardSpeed = GetForwardSpeed();
            GUI.Label(new Rect(panel.x + 10f, panel.y + 29f, 345f, 124f),
                $"person {hasPerson} x {(hasPerson ? person.normalizedHorizontalPosition : 0f):F2}  phase {Phase}\n" +
                $"decision {SelectedDecision}/{CurrentAvoidanceMagnitude} latched {decisionLatched} forced-stop {decisionForcedToStop}\n" +
                $"rule speed {LastSpeedScale:F2} target offset {TargetLaneOffset:F2}\n" +
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
            bool emergencyStopped = safetySupervisor != null &&
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

        private void EvaluateTrainingStep(float minimumDistance)
        {
            if (episodeTerminating)
                return;

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

            if (!decisionLatched && laneFailureDecisionCount >= laneFailureDecisionLimit)
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
                bool activeManeuver = SelectedDecision == AvoidanceDecision.Left ||
                                      SelectedDecision == AvoidanceDecision.Right ||
                                      SelectedDecision == AvoidanceDecision.Reverse;
                FinishTrainingEpisode(
                    activeManeuver ? successfulAvoidanceReward : passiveFailurePenalty,
                    activeManeuver ? TrainingOutcome.Success : TrainingOutcome.PassiveFailure);
                return;
            }

            if (encounterClass == TrainingEncounterClass.YieldRequired)
            {
                if (SelectedDecision == AvoidanceDecision.None)
                {
                    FinishTrainingEpisode(passiveFailurePenalty, TrainingOutcome.PassiveFailure);
                    return;
                }
                bool stopped = SelectedDecision == AvoidanceDecision.Stop;
                FinishTrainingEpisode(
                    stopped ? yieldStopReward : yieldMovingReward,
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
            laneFollower?.SetAvoidanceIntent(false, 1f, 0f);
            EndEpisode();
        }

        private void InterruptTrainingEpisode(TrainingOutcome outcome)
        {
            episodeTerminating = true;
            RecordEpisodeOutcome(outcome);
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
            stats.Add("HumanAvoidance/Policy/TargetLaneOffset", TargetLaneOffset);
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
                SelectedDecision == AvoidanceDecision.Reverse ? 1f : 0f);
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
