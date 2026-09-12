using ShipRobot.LaneFollowing;
using UnityEngine;

namespace ShipRobot.ObstacleAvoidance
{
    [DisallowMultipleComponent]
    public sealed class SafetySupervisor : MonoBehaviour
    {
        public enum SafetyState
        {
            Clear,
            EarlyWarning,
            Slowdown,
            EmergencyStop,
            WaitingForClear
        }

        [Header("Connections")]
        [SerializeField] private DualToFSensorRig tofRig;
        [SerializeField] private LaneFollowerController laneFollower;
        [SerializeField] private MonoBehaviour personBearingProvider;

        [Header("RGB person early warning")]
        [SerializeField, Range(0.1f, 1f)] private float earlyWarningSpeedScale = 0.65f;
        [SerializeField, Range(0f, 0.8f)] private float centreSectorHalfWidth = 0.20f;

        [Header("ToF safety thresholds")]
        [SerializeField, Min(0.05f)] private float emergencyStopDistance = 0.80f;
        [SerializeField, Min(0.1f)] private float emergencyStopTtc = 1.50f;
        [SerializeField, Min(0.1f)] private float slowdownDistance = 1.50f;
        [SerializeField, Range(0.05f, 1f)] private float minimumSlowdownScale = 0.25f;
        [SerializeField, Min(0.05f)] private float releaseDistance = 1.00f;
        [SerializeField, Min(0f)] private float clearHoldSeconds = 0.50f;

        [Header("Control enforcement")]
        [Tooltip("Disable during RL training so the supervisor observes hazards without overriding policy actions.")]
        [SerializeField] private bool enforceControl = true;

        [Header("Debug")]
        [SerializeField] private bool showDebugPanel = true;

        public SafetyState State { get; private set; } = SafetyState.Clear;
        public float AppliedSpeedScale { get; private set; } = 1f;
        public bool HasAssociatedPerson => currentPersonSector != PersonSector.None;
        public float AssociatedPersonScreenX { get; private set; }
        public float AssociatedPersonConfidence { get; private set; }
        public float AssociatedTofDistance => associatedTofDistance;
        public float AssociatedTofTtc => associatedTofTtc;
        public bool EnforceControl => enforceControl;

        private enum PersonSector { None, Left, Centre, Right }
        private PersonSector currentPersonSector;
        private float associatedTofDistance;
        private float associatedTofTtc = float.PositiveInfinity;
        private string associatedSensorName = "MIN";
        private IPersonBearingSource PersonBearingSource => personBearingProvider as IPersonBearingSource;

        private float safeSince = -1f;
        private GUIStyle titleStyle;
        private GUIStyle valueStyle;

        public void ResetForTrainingEpisode()
        {
            State = SafetyState.Clear;
            AppliedSpeedScale = 1f;
            safeSince = -1f;
            currentPersonSector = PersonSector.None;
            associatedTofDistance = 0f;
            associatedTofTtc = float.PositiveInfinity;
            laneFollower?.SetSafetyStop(false);
            laneFollower?.SetSafetySpeedScale(1f);
        }

        public void SetControlEnforcement(bool enabled)
        {
            enforceControl = enabled;
            if (!enforceControl && laneFollower != null)
            {
                laneFollower.SetSafetyStop(false);
                laneFollower.SetSafetySpeedScale(1f);
            }
        }

        private void Update()
        {
            if (tofRig == null || laneFollower == null || !tofRig.IsInitialized)
                return;

            float distance = tofRig.MinimumDistance;
            float ttc = tofRig.MinimumTtc;
            UpdatePersonTofAssociation();
            bool distanceEmergency = distance <= emergencyStopDistance;
            bool ttcEmergency = !float.IsPositiveInfinity(ttc) && ttc <= emergencyStopTtc;
            bool emergency = distanceEmergency || ttcEmergency;

            if (emergency)
            {
                safeSince = -1f;
                EnterEmergencyStop();
                return;
            }

            if (State == SafetyState.EmergencyStop || State == SafetyState.WaitingForClear)
            {
                bool safelySeparated = distance >= releaseDistance &&
                                       (float.IsPositiveInfinity(ttc) || ttc > emergencyStopTtc);
                if (!safelySeparated)
                {
                    safeSince = -1f;
                    EnterEmergencyStop();
                    return;
                }

                State = SafetyState.WaitingForClear;
                AppliedSpeedScale = 0f;
                ApplyControl(true, 0f);
                if (safeSince < 0f)
                    safeSince = Time.time;
                if (Time.time - safeSince < clearHoldSeconds)
                    return;

                ApplyControl(false, 1f);
                safeSince = -1f;
            }

            if (distance < slowdownDistance)
            {
                State = SafetyState.Slowdown;
                AppliedSpeedScale = Mathf.Lerp(
                    minimumSlowdownScale,
                    1f,
                    Mathf.InverseLerp(emergencyStopDistance, slowdownDistance, distance));
                ApplyControl(false, AppliedSpeedScale);
                return;
            }

            if (currentPersonSector != PersonSector.None)
            {
                State = SafetyState.EarlyWarning;
                AppliedSpeedScale = earlyWarningSpeedScale;
                ApplyControl(false, AppliedSpeedScale);
                return;
            }

            State = SafetyState.Clear;
            AppliedSpeedScale = 1f;
            ApplyControl(false, 1f);
        }

        private void EnterEmergencyStop()
        {
            State = SafetyState.EmergencyStop;
            AppliedSpeedScale = 0f;
            ApplyControl(true, 0f);
        }

        private void ApplyControl(bool stopped, float speedScale)
        {
            if (laneFollower == null)
                return;
            if (enforceControl)
            {
                laneFollower.SetSafetySpeedScale(speedScale);
                laneFollower.SetSafetyStop(stopped);
            }
            else
            {
                laneFollower.SetSafetySpeedScale(1f);
                laneFollower.SetSafetyStop(false);
            }
        }

        private void UpdatePersonTofAssociation()
        {
            currentPersonSector = PersonSector.None;
            associatedTofDistance = tofRig.MinimumDistance;
            associatedTofTtc = tofRig.MinimumTtc;
            associatedSensorName = "MIN";
            AssociatedPersonScreenX = 0f;
            AssociatedPersonConfidence = 0f;
            if (PersonBearingSource == null ||
                !PersonBearingSource.TryGetPersonBearing(out PersonBearingObservation observation))
                return;

            float screenX = observation.normalizedHorizontalPosition;
            AssociatedPersonScreenX = screenX;
            AssociatedPersonConfidence = observation.confidence;
            if (screenX < -centreSectorHalfWidth)
            {
                currentPersonSector = PersonSector.Left;
                associatedTofDistance = tofRig.LeftDistance;
                associatedTofTtc = tofRig.LeftTtc;
                associatedSensorName = "LEFT";
            }
            else if (screenX > centreSectorHalfWidth)
            {
                currentPersonSector = PersonSector.Right;
                associatedTofDistance = tofRig.RightDistance;
                associatedTofTtc = tofRig.RightTtc;
                associatedSensorName = "RIGHT";
            }
            else
            {
                currentPersonSector = PersonSector.Centre;
                associatedTofDistance = tofRig.MinimumDistance;
                associatedTofTtc = tofRig.MinimumTtc;
                associatedSensorName = tofRig.LeftDistance <= tofRig.RightDistance ? "LEFT(min)" : "RIGHT(min)";
            }
        }

        private void OnDisable()
        {
            if (laneFollower == null)
                return;
            laneFollower.SetSafetyStop(false);
            laneFollower.SetSafetySpeedScale(1f);
        }

        private void OnGUI()
        {
            if (!showDebugPanel)
                return;
            EnsureStyles();
            Rect panel = new Rect(Screen.width - 375f, Screen.height - 330f, 365f, 102f);
            GUI.Box(panel, GUIContent.none);
            Color stateColour = State == SafetyState.EmergencyStop || State == SafetyState.WaitingForClear
                ? Color.red
                : State == SafetyState.Slowdown || State == SafetyState.EarlyWarning ? Color.yellow : Color.green;
            titleStyle.normal.textColor = stateColour;
            string mode = enforceControl ? "SAFETY" : "SAFETY MONITOR";
            GUI.Label(new Rect(panel.x + 10f, panel.y + 6f, 345f, 20f), $"{mode}: {State}", titleStyle);

            if (tofRig == null || !tofRig.IsInitialized)
            {
                GUI.Label(new Rect(panel.x + 10f, panel.y + 30f, 345f, 40f), "Waiting for dual ToF data", valueStyle);
                return;
            }

            string ttc = float.IsPositiveInfinity(tofRig.MinimumTtc) ? "INF" : $"{tofRig.MinimumTtc:F2} s";
            string personTtc = float.IsPositiveInfinity(associatedTofTtc) ? "INF" : $"{associatedTofTtc:F2}s";
            GUI.Label(new Rect(panel.x + 10f, panel.y + 29f, 345f, 68f),
                $"min distance {tofRig.MinimumDistance:F2} m   min TTC {ttc}\n" +
                $"person {currentPersonSector} x {AssociatedPersonScreenX:F2} conf {AssociatedPersonConfidence:F2}\n" +
                $"selected {associatedSensorName} {associatedTofDistance:F2}m TTC {personTtc}  suggested {AppliedSpeedScale:F2}\n" +
                $"control {(enforceControl ? "ENFORCED" : "MONITOR ONLY")}",
                valueStyle);
        }

        private void EnsureStyles()
        {
            if (titleStyle != null)
                return;
            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.green }
            };
            valueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal = { textColor = Color.white }
            };
        }
    }
}
