using UnityEngine;

namespace ShipRobot.ObstacleAvoidance
{
    [DisallowMultipleComponent]
    public sealed class DualToFSensorRig : MonoBehaviour
    {
        [Header("Sensors")]
        [SerializeField] private VirtualToFSensor frontLeft;
        [SerializeField] private VirtualToFSensor frontRight;

        [Header("Sampling")]
        [SerializeField, Range(5f, 60f)] private float sampleRateHz = 20f;
        [SerializeField, Min(0f)] private float ttcSafetyDistance = 0.20f;
        [SerializeField, Range(0f, 1f)] private float distanceSmoothing = 0.35f;

        [Header("Debug")]
        [SerializeField] private bool showDebugPanel = true;

        public float LeftDistance { get; private set; }
        public float RightDistance { get; private set; }
        public float LeftClosingSpeed { get; private set; }
        public float RightClosingSpeed { get; private set; }
        public float LeftTtc { get; private set; } = float.PositiveInfinity;
        public float RightTtc { get; private set; } = float.PositiveInfinity;
        public bool IsInitialized => initialized;
        public float MinimumDistance => Mathf.Min(LeftDistance, RightDistance);
        public float MinimumTtc => Mathf.Min(LeftTtc, RightTtc);

        private float nextSampleTime;
        private float lastSampleTime;
        private bool initialized;
        private GUIStyle titleStyle;
        private GUIStyle valueStyle;

        private void Update()
        {
            if (Time.time < nextSampleTime)
                return;
            float interval = 1f / Mathf.Max(sampleRateHz, 1f);
            nextSampleTime = Time.time + interval;
            float deltaTime = initialized ? Mathf.Max(Time.time - lastSampleTime, 0.001f) : interval;
            lastSampleTime = Time.time;

            float rawLeft = frontLeft != null ? frontLeft.ReadDistance() : 0f;
            float rawRight = frontRight != null ? frontRight.ReadDistance() : 0f;
            if (!initialized)
            {
                LeftDistance = rawLeft;
                RightDistance = rawRight;
                initialized = true;
            }

            float previousLeft = LeftDistance;
            float previousRight = RightDistance;
            LeftDistance = Mathf.Lerp(previousLeft, rawLeft, distanceSmoothing);
            RightDistance = Mathf.Lerp(previousRight, rawRight, distanceSmoothing);
            LeftClosingSpeed = Mathf.Max(0f, (previousLeft - LeftDistance) / deltaTime);
            RightClosingSpeed = Mathf.Max(0f, (previousRight - RightDistance) / deltaTime);
            LeftTtc = CalculateTtc(LeftDistance, LeftClosingSpeed);
            RightTtc = CalculateTtc(RightDistance, RightClosingSpeed);
        }

        private float CalculateTtc(float distance, float closingSpeed)
        {
            if (closingSpeed <= 0.01f)
                return float.PositiveInfinity;
            return Mathf.Max(0f, distance - ttcSafetyDistance) / closingSpeed;
        }

        private void OnGUI()
        {
            if (!showDebugPanel)
                return;
            EnsureStyles();
            Rect panel = new Rect(Screen.width - 375f, Screen.height - 220f, 365f, 82f);
            GUI.Box(panel, GUIContent.none);
            GUI.Label(new Rect(panel.x + 10f, panel.y + 6f, 345f, 20f), "DUAL FRONT ToF", titleStyle);
            GUI.Label(new Rect(panel.x + 10f, panel.y + 29f, 345f, 48f),
                $"LEFT  {LeftDistance:F2} m   closing {LeftClosingSpeed:F2} m/s   TTC {FormatTtc(LeftTtc)}\n" +
                $"RIGHT {RightDistance:F2} m   closing {RightClosingSpeed:F2} m/s   TTC {FormatTtc(RightTtc)}",
                valueStyle);
        }

        private static string FormatTtc(float value) =>
            float.IsPositiveInfinity(value) ? "INF" : $"{value:F2} s";

        private void EnsureStyles()
        {
            if (titleStyle != null)
                return;
            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.cyan }
            };
            valueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal = { textColor = Color.white }
            };
        }
    }
}
