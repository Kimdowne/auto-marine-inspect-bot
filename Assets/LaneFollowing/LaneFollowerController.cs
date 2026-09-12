using UnityEngine;

namespace ShipRobot.LaneFollowing
{
    /// <summary>
    /// Fail-safe PD lane follower for a skid-steer robot using four WheelColliders.
    /// Only this component (or another drive component) should command the wheels at a time.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public sealed class LaneFollowerController : MonoBehaviour
    {
        [Header("Perception")]
        [SerializeField] private HsvLaneDetector laneDetector;
        [SerializeField, Range(0f, 1f)] private float minimumConfidence = 0.30f;
        [SerializeField, Min(0.05f)] private float maximumDetectionAge = 0.20f;

        [Header("Wheel Colliders")]
        [SerializeField] private WheelCollider frontLeftWheel;
        [SerializeField] private WheelCollider rearLeftWheel;
        [SerializeField] private WheelCollider frontRightWheel;
        [SerializeField] private WheelCollider rearRightWheel;

        [Header("Control")]
        [SerializeField, Range(0f, 1f)] private float cruiseCommand = 0.55f;
        [SerializeField, Range(0f, 1f)] private float minimumCornerCommand = 0.20f;
        [Tooltip("Lowest retained speed fraction once lane confidence is usable. Prevents a barely-valid lane from reducing motion almost to zero.")]
        [SerializeField, Range(0f, 1f)] private float minimumConfidenceSpeedScale = 0.25f;
        [SerializeField, Min(0f)] private float lateralGain = 0.85f;
        [SerializeField, Min(0f)] private float headingGain = 0.55f;
        [SerializeField, Min(0f)] private float derivativeGain = 0.08f;
        [SerializeField, Range(0f, 1f)] private float maximumTurnCommand = 0.75f;
        [SerializeField, Min(0f)] private float maxMotorTorque = 5f;
        [SerializeField, Min(0f)] private float stoppedBrakeTorque = 2f;
        [SerializeField, Min(0f)] private float controlSlewRate = 3f;
        [Tooltip("How quickly the virtual lane-centre offset changes. The offset is normalized to camera width, not metres.")]
        [SerializeField, Min(0f)] private float avoidanceOffsetSlewRate = 0.8f;
        [Tooltip("During avoidance only, continue briefly on the last heading while a person occludes the lane.")]
        [SerializeField, Min(0f)] private float avoidanceLaneLossGraceSeconds = 1.5f;
        [SerializeField, Range(0f, 1f)] private float avoidanceLaneLossMoveCommand = 0.30f;
        [Tooltip("A large avoidance offset may intentionally move both boundaries out of view.")]
        [SerializeField, Range(0f, 1f)] private float largeAvoidanceOffsetThreshold = 0.40f;
        [SerializeField, Min(0f)] private float largeAvoidanceLaneLossGraceSeconds = 5.0f;
        [SerializeField, Range(0f, 1f)] private float largeAvoidanceLaneLossTurnCommand = 0.22f;
        [SerializeField, Min(0f)] private float largeAvoidanceLaneLossTurnSeconds = 0.80f;

        [Header("Debug")]
        [SerializeField] private bool debugLog;

        public bool IsLaneLocked { get; private set; }
        public bool IsDriveEnabled => driveEnabled;
        public bool IsSafetyStopped => safetyStop;
        public float SafetySpeedScale => safetySpeedScale;
        public bool IsAvoidanceActive => avoidanceActive;
        public float AvoidanceSpeedScale => avoidanceSpeedScale;
        public float AvoidanceLateralOffset => currentAvoidanceLateralOffset;
        public float TargetAvoidanceLateralOffset => targetAvoidanceLateralOffset;
        public float EffectiveMinimumConfidence => perceptionOverrideActive
            ? overrideMinimumConfidence : minimumConfidence;
        public float EffectiveMaximumDetectionAge => perceptionOverrideActive
            ? overrideMaximumDetectionAge : maximumDetectionAge;
        public bool IsUsingAvoidanceLaneLossFallback { get; private set; }
        public bool HasUsableLane => laneDetector != null &&
            laneDetector.LatestDetection.IsUsable(EffectiveMinimumConfidence, EffectiveMaximumDetectionAge);
        public float MoveCommand { get; private set; }
        public float TurnCommand { get; private set; }

        private float previousLateralError;
        private float lastLogTime;
        private bool driveEnabled = true;
        private bool manualControl;
        private float manualMoveCommand;
        private float manualTurnCommand;
        private bool safetyStop;
        private float safetySpeedScale = 1f;
        private bool avoidanceActive;
        private float avoidanceSpeedScale = 1f;
        private float targetAvoidanceLateralOffset;
        private float currentAvoidanceLateralOffset;
        private bool perceptionOverrideActive;
        private float overrideMinimumConfidence;
        private float overrideMaximumDetectionAge;
        private float overrideMinimumConfidenceSpeedScale;
        private float lastUsableLaneTime = float.NegativeInfinity;
        private void FixedUpdate()
        {
            currentAvoidanceLateralOffset = Mathf.MoveTowards(
                currentAvoidanceLateralOffset,
                avoidanceActive ? targetAvoidanceLateralOffset : 0f,
                avoidanceOffsetSlewRate * Time.fixedDeltaTime);

            if (safetyStop)
            {
                MoveCommand = 0f;
                TurnCommand = 0f;
                IsLaneLocked = false;
                ApplyDrive(0f, 0f, true);
                return;
            }

            if (!driveEnabled)
            {
                MoveCommand = 0f;
                TurnCommand = 0f;
                IsLaneLocked = false;
                ApplyDrive(0f, 0f, true);
                return;
            }

            if (avoidanceActive && Mathf.Abs(avoidanceSpeedScale) <= 0.001f)
            {
                MoveCommand = 0f;
                TurnCommand = 0f;
                IsLaneLocked = laneDetector != null &&
                    laneDetector.LatestDetection.IsUsable(
                        EffectiveMinimumConfidence,
                        EffectiveMaximumDetectionAge);
                ApplyDrive(0f, 0f, true);
                return;
            }

            if (avoidanceActive && avoidanceSpeedScale < 0f)
            {
                // The forward camera cannot provide useful lane geometry while reversing.
                // Reverse straight at a bounded command; rear clearance is enforced by
                // the high-level avoidance coordinator.
                IsUsingAvoidanceLaneLossFallback = false;
                IsLaneLocked = false;
                float reverseMove = cruiseCommand * avoidanceSpeedScale * safetySpeedScale;
                MoveCommand = Mathf.MoveTowards(
                    MoveCommand,
                    reverseMove,
                    controlSlewRate * Time.fixedDeltaTime);
                TurnCommand = Mathf.MoveTowards(
                    TurnCommand,
                    0f,
                    controlSlewRate * Time.fixedDeltaTime);
                ApplyDrive(MoveCommand, TurnCommand, false);
                return;
            }

            if (manualControl)
            {
                MoveCommand = manualMoveCommand * safetySpeedScale;
                TurnCommand = manualTurnCommand;
                IsLaneLocked = false;
                ApplyDrive(MoveCommand, TurnCommand, false);
                return;
            }

            if (laneDetector == null ||
                !laneDetector.LatestDetection.IsUsable(EffectiveMinimumConfidence, EffectiveMaximumDetectionAge))
            {
                float laneLossAge = Time.time - lastUsableLaneTime;
                bool isLargeAvoidance = avoidanceActive &&
                    Mathf.Abs(targetAvoidanceLateralOffset) >= largeAvoidanceOffsetThreshold;
                float allowedLaneLossSeconds = isLargeAvoidance
                    ? largeAvoidanceLaneLossGraceSeconds
                    : avoidanceLaneLossGraceSeconds;
                bool mayContinueAvoidance = avoidanceActive &&
                    laneLossAge <= allowedLaneLossSeconds;
                if (mayContinueAvoidance)
                {
                    IsUsingAvoidanceLaneLossFallback = true;
                    IsLaneLocked = false;
                    float fallbackMove = avoidanceLaneLossMoveCommand *
                        avoidanceSpeedScale * safetySpeedScale;
                    MoveCommand = Mathf.MoveTowards(
                        MoveCommand,
                        fallbackMove,
                        controlSlewRate * Time.fixedDeltaTime);
                    float fallbackTurn = isLargeAvoidance &&
                        laneLossAge <= largeAvoidanceLaneLossTurnSeconds
                        ? Mathf.Sign(targetAvoidanceLateralOffset) * largeAvoidanceLaneLossTurnCommand
                        : 0f;
                    // Large manoeuvres keep a short outward arc, then continue straight.
                    TurnCommand = Mathf.MoveTowards(
                        TurnCommand,
                        fallbackTurn,
                        controlSlewRate * 0.5f * Time.fixedDeltaTime);
                    ApplyDrive(MoveCommand, TurnCommand, false);
                    LogStatus(isLargeAvoidance
                        ? "large avoidance lane loss - executing escape arc"
                        : "avoidance lane occlusion - holding course");
                    return;
                }

                IsUsingAvoidanceLaneLossFallback = false;
                IsLaneLocked = false;
                MoveCommand = Mathf.MoveTowards(MoveCommand, 0f, controlSlewRate * Time.fixedDeltaTime);
                TurnCommand = Mathf.MoveTowards(TurnCommand, 0f, controlSlewRate * Time.fixedDeltaTime);
                ApplyDrive(MoveCommand, TurnCommand, MoveCommand <= 0.01f);
                LogStatus("lane lost - stopping");
                return;
            }

            IsUsingAvoidanceLaneLossFallback = false;
            lastUsableLaneTime = Time.time;
            IsLaneLocked = true;
            HsvLaneDetector.Detection detection = laneDetector.LatestDetection;
            // A positive target offset moves the virtual lane centre to the image/right side.
            // The existing PD controller then tracks that shifted centre without a second wheel controller.
            float effectiveLateralError = detection.lateralError + currentAvoidanceLateralOffset;
            float derivative = (effectiveLateralError - previousLateralError) /
                Mathf.Max(Time.fixedDeltaTime, 0.001f);
            previousLateralError = effectiveLateralError;

            // Positive image error means the lane centre is to the robot's right.
            float desiredTurn = effectiveLateralError * lateralGain +
                                detection.headingError * headingGain +
                                derivative * derivativeGain;
            desiredTurn = Mathf.Clamp(desiredTurn, -maximumTurnCommand, maximumTurnCommand);

            float cornerRatio = Mathf.Abs(desiredTurn) / Mathf.Max(maximumTurnCommand, 0.001f);
            float desiredMove = Mathf.Lerp(cruiseCommand, minimumCornerCommand, cornerRatio);
            float confidenceRatio = Mathf.InverseLerp(EffectiveMinimumConfidence, 1f, detection.confidence);
            float confidenceSpeedFloor = perceptionOverrideActive
                ? overrideMinimumConfidenceSpeedScale : minimumConfidenceSpeedScale;
            desiredMove *= Mathf.Lerp(confidenceSpeedFloor, 1f, confidenceRatio);
            desiredMove *= safetySpeedScale;
            if (avoidanceActive)
                desiredMove *= avoidanceSpeedScale;

            MoveCommand = Mathf.MoveTowards(MoveCommand, desiredMove, controlSlewRate * Time.fixedDeltaTime);
            TurnCommand = Mathf.MoveTowards(TurnCommand, desiredTurn, controlSlewRate * Time.fixedDeltaTime);
            ApplyDrive(MoveCommand, TurnCommand, false);
            LogStatus($"locked move={MoveCommand:F2}, turn={TurnCommand:F2}");
        }

        public void SetDriveEnabled(bool enabled)
        {
            driveEnabled = enabled;
            manualControl = false;
            if (enabled)
                return;

            MoveCommand = 0f;
            TurnCommand = 0f;
            IsLaneLocked = false;
            IsUsingAvoidanceLaneLossFallback = false;
            ApplyDrive(0f, 0f, true);
        }

        public void SetManualCommand(float move, float turn)
        {
            driveEnabled = true;
            manualControl = true;
            manualMoveCommand = Mathf.Clamp(move, -1f, 1f);
            manualTurnCommand = Mathf.Clamp(turn, -1f, 1f);
        }

        public void ResumeLaneFollowing()
        {
            manualControl = false;
            driveEnabled = true;
        }

        public void SetSafetyStop(bool stopped)
        {
            safetyStop = stopped;
            if (!stopped)
                return;
            MoveCommand = 0f;
            TurnCommand = 0f;
            IsLaneLocked = false;
            ApplyDrive(0f, 0f, true);
        }

        public void SetSafetySpeedScale(float scale)
        {
            safetySpeedScale = Mathf.Clamp01(scale);
        }

        public void SetAvoidanceIntent(bool active, float speedScale, float normalizedLateralOffset)
        {
            avoidanceActive = active;
            avoidanceSpeedScale = Mathf.Clamp(speedScale, -1f, 1f);
            targetAvoidanceLateralOffset = Mathf.Clamp(normalizedLateralOffset, -1f, 1f);
        }

        public void SetPerceptionOverride(
            float trainingMinimumConfidence,
            float trainingMaximumDetectionAge,
            float trainingMinimumConfidenceSpeedScale)
        {
            perceptionOverrideActive = true;
            overrideMinimumConfidence = Mathf.Clamp01(trainingMinimumConfidence);
            overrideMaximumDetectionAge = Mathf.Max(0.05f, trainingMaximumDetectionAge);
            overrideMinimumConfidenceSpeedScale = Mathf.Clamp01(trainingMinimumConfidenceSpeedScale);
        }

        public void ClearPerceptionOverride()
        {
            perceptionOverrideActive = false;
        }

        public bool TryGetLaneDetection(out HsvLaneDetector.Detection detection)
        {
            detection = laneDetector != null ? laneDetector.LatestDetection : default;
            return laneDetector != null &&
                   detection.IsUsable(EffectiveMinimumConfidence, EffectiveMaximumDetectionAge);
        }

        public bool TryGetBoundaryPair(float minimumPairConfidence, out HsvLaneDetector.Detection detection)
        {
            detection = laneDetector != null ? laneDetector.LatestDetection : default;
            return laneDetector != null &&
                   detection.IsBoundaryPairUsable(minimumPairConfidence, EffectiveMaximumDetectionAge);
        }

        public bool TryGetBoundarySides(out HsvLaneDetector.Detection detection)
        {
            detection = laneDetector != null ? laneDetector.LatestDetection : default;
            return laneDetector != null &&
                   Time.timeAsDouble - detection.timestamp <= EffectiveMaximumDetectionAge;
        }

        private void ApplyDrive(float move, float turn, bool brake)
        {
            float leftInput = Mathf.Clamp(move + turn, -1f, 1f);
            float rightInput = Mathf.Clamp(move - turn, -1f, 1f);
            ApplyWheel(frontLeftWheel, leftInput * maxMotorTorque, brake ? stoppedBrakeTorque : 0f);
            ApplyWheel(rearLeftWheel, leftInput * maxMotorTorque, brake ? stoppedBrakeTorque : 0f);
            ApplyWheel(frontRightWheel, rightInput * maxMotorTorque, brake ? stoppedBrakeTorque : 0f);
            ApplyWheel(rearRightWheel, rightInput * maxMotorTorque, brake ? stoppedBrakeTorque : 0f);
        }

        private static void ApplyWheel(WheelCollider wheel, float torque, float brakeTorque)
        {
            if (wheel == null)
                return;

            wheel.motorTorque = torque;
            wheel.brakeTorque = brakeTorque;
        }

        private void OnDisable()
        {
            ApplyDrive(0f, 0f, true);
            MoveCommand = 0f;
            TurnCommand = 0f;
            IsLaneLocked = false;
            IsUsingAvoidanceLaneLossFallback = false;
            avoidanceActive = false;
            avoidanceSpeedScale = 1f;
            targetAvoidanceLateralOffset = 0f;
            currentAvoidanceLateralOffset = 0f;
            perceptionOverrideActive = false;
            lastUsableLaneTime = float.NegativeInfinity;
        }

        private void LogStatus(string message)
        {
            if (!debugLog || Time.time - lastLogTime < 0.5f)
                return;

            lastLogTime = Time.time;
            Debug.Log($"[{nameof(LaneFollowerController)}] {message}", this);
        }
    }
}
