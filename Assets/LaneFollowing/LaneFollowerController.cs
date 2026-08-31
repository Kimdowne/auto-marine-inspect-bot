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
        [SerializeField, Min(0f)] private float lateralGain = 0.85f;
        [SerializeField, Min(0f)] private float headingGain = 0.55f;
        [SerializeField, Min(0f)] private float derivativeGain = 0.08f;
        [SerializeField, Range(0f, 1f)] private float maximumTurnCommand = 0.75f;
        [SerializeField, Min(0f)] private float maxMotorTorque = 5f;
        [SerializeField, Min(0f)] private float stoppedBrakeTorque = 2f;
        [SerializeField, Min(0f)] private float controlSlewRate = 3f;

        [Header("Debug")]
        [SerializeField] private bool debugLog;

        public bool IsLaneLocked { get; private set; }
        public bool IsDriveEnabled => driveEnabled;
        public bool IsSafetyStopped => safetyStop;
        public float SafetySpeedScale => safetySpeedScale;
        public bool HasUsableLane => laneDetector != null &&
            laneDetector.LatestDetection.IsUsable(minimumConfidence, maximumDetectionAge);
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
        private void FixedUpdate()
        {
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

            if (manualControl)
            {
                MoveCommand = manualMoveCommand * safetySpeedScale;
                TurnCommand = manualTurnCommand;
                IsLaneLocked = false;
                ApplyDrive(MoveCommand, TurnCommand, false);
                return;
            }

            if (laneDetector == null || !laneDetector.LatestDetection.IsUsable(minimumConfidence, maximumDetectionAge))
            {
                IsLaneLocked = false;
                MoveCommand = Mathf.MoveTowards(MoveCommand, 0f, controlSlewRate * Time.fixedDeltaTime);
                TurnCommand = Mathf.MoveTowards(TurnCommand, 0f, controlSlewRate * Time.fixedDeltaTime);
                ApplyDrive(MoveCommand, TurnCommand, MoveCommand <= 0.01f);
                LogStatus("lane lost - stopping");
                return;
            }

            IsLaneLocked = true;
            HsvLaneDetector.Detection detection = laneDetector.LatestDetection;
            float derivative = (detection.lateralError - previousLateralError) / Mathf.Max(Time.fixedDeltaTime, 0.001f);
            previousLateralError = detection.lateralError;

            // Positive image error means the lane centre is to the robot's right.
            float desiredTurn = detection.lateralError * lateralGain +
                                detection.headingError * headingGain +
                                derivative * derivativeGain;
            desiredTurn = Mathf.Clamp(desiredTurn, -maximumTurnCommand, maximumTurnCommand);

            float cornerRatio = Mathf.Abs(desiredTurn) / Mathf.Max(maximumTurnCommand, 0.001f);
            float desiredMove = Mathf.Lerp(cruiseCommand, minimumCornerCommand, cornerRatio);
            desiredMove *= Mathf.InverseLerp(minimumConfidence, 1f, detection.confidence);
            desiredMove *= safetySpeedScale;

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

        public bool TryGetLaneDetection(out HsvLaneDetector.Detection detection)
        {
            detection = laneDetector != null ? laneDetector.LatestDetection : default;
            return laneDetector != null && detection.IsUsable(minimumConfidence, maximumDetectionAge);
        }

        public bool TryGetBoundaryPair(float minimumPairConfidence, out HsvLaneDetector.Detection detection)
        {
            detection = laneDetector != null ? laneDetector.LatestDetection : default;
            return laneDetector != null &&
                   detection.IsBoundaryPairUsable(minimumPairConfidence, maximumDetectionAge);
        }

        public bool TryGetBoundarySides(out HsvLaneDetector.Detection detection)
        {
            detection = laneDetector != null ? laneDetector.LatestDetection : default;
            return laneDetector != null && Time.timeAsDouble - detection.timestamp <= maximumDetectionAge;
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
