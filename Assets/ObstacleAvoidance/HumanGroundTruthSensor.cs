using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace ShipRobot.ObstacleAvoidance
{
    [DisallowMultipleComponent]
    public sealed class HumanGroundTruthSensor : MonoBehaviour, IPersonBearingSource
    {
        [Serializable]
        public struct Observation
        {
            public Transform target;
            public float normalizedHorizontalPosition;
            public Vector3 localPosition;
            public Vector3 localRelativeVelocity;
            public float planarDistance;
            public float closingSpeed;
            public float timeToCollision;
            public bool lineOfSight;
            public double timestamp;
        }

        [Header("References")]
        [SerializeField] private Camera perceptionCamera;

        [Header("Ground-truth perception")]
        [SerializeField, Min(0.5f)] private float maximumRange = 12f;
        [SerializeField, Range(10f, 170f)] private float horizontalFieldOfView = 90f;
        [SerializeField] private bool requireLineOfSight = true;
        [SerializeField] private LayerMask occlusionMask = ~0;
        [SerializeField, Min(0.1f)] private float candidateRefreshInterval = 1f;
        [SerializeField, Min(0f)] private float surfaceSafetyMargin = 0.10f;

        [Header("Debug")]
        [SerializeField] private bool showDebugPanel = true;
        [SerializeField] private bool drawDebugGizmos = true;

        public bool HasObservation { get; private set; }
        public Observation LatestObservation { get; private set; }

        private readonly List<Transform> candidates = new();
        private readonly Dictionary<Transform, MotionSample> motionSamples = new();
        private Vector3 previousRobotPosition;
        private Vector3 robotVelocity;
        private Rigidbody robotBody;
        private Collider robotCollisionCollider;
        private float nextCandidateRefreshTime;
        private double previousSampleTime;
        private GUIStyle titleStyle;
        private GUIStyle valueStyle;

        private struct MotionSample
        {
            public Vector3 position;
            public Vector3 velocity;
            public double timestamp;
        }

        private void Awake()
        {
            if (perceptionCamera == null)
            {
                Transform cameraTransform = transform.Find("front_camera");
                perceptionCamera = cameraTransform != null ? cameraTransform.GetComponent<Camera>() : null;
            }

            previousRobotPosition = transform.position;
            previousSampleTime = Time.timeAsDouble;
            robotBody = GetComponent<Rigidbody>();
            robotCollisionCollider = GetComponent<BoxCollider>();
            if (robotCollisionCollider == null)
                robotCollisionCollider = GetComponent<Collider>();
            RefreshCandidates();
        }

        private void Update()
        {
            double now = Time.timeAsDouble;
            float deltaTime = Mathf.Max((float)(now - previousSampleTime), 0.0001f);
            robotVelocity = robotBody != null
                ? robotBody.linearVelocity
                : (transform.position - previousRobotPosition) / deltaTime;
            previousRobotPosition = transform.position;
            previousSampleTime = now;

            if (Time.time >= nextCandidateRefreshTime)
                RefreshCandidates();

            UpdateObservation(now, deltaTime);
        }

        public bool TryGetNearestHuman(out Observation observation)
        {
            observation = LatestObservation;
            return HasObservation;
        }

        public bool TryGetPersonBearing(out PersonBearingObservation observation)
        {
            observation = default;
            if (!HasObservation)
                return false;
            observation = new PersonBearingObservation
            {
                normalizedHorizontalPosition = LatestObservation.normalizedHorizontalPosition,
                confidence = 1f,
                normalizedBoxWidth = 0f,
                normalizedBoxHeight = 0f,
                timestamp = LatestObservation.timestamp
            };
            return true;
        }

        private void RefreshCandidates()
        {
            nextCandidateRefreshTime = Time.time + candidateRefreshInterval;
            candidates.Clear();
            try
            {
                GameObject[] humans = GameObject.FindGameObjectsWithTag("Human");
                for (int i = 0; i < humans.Length; i++)
                {
                    if (humans[i] != null && humans[i].activeInHierarchy)
                        candidates.Add(humans[i].transform);
                }
            }
            catch (UnityException)
            {
                // The project may temporarily be opened before the Human tag is created.
            }
        }

        private void UpdateObservation(double now, float deltaTime)
        {
            HasObservation = false;
            float nearestDistance = float.PositiveInfinity;
            Transform reference = perceptionCamera != null ? perceptionCamera.transform : transform;

            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                Transform human = candidates[i];
                if (human == null || !human.gameObject.activeInHierarchy)
                {
                    candidates.RemoveAt(i);
                    continue;
                }

                Vector3 humanVelocity = EstimateVelocity(human, now, deltaTime);
                Vector3 relativeWorld = human.position - transform.position;
                Vector3 planarRelative = Vector3.ProjectOnPlane(relativeWorld, Vector3.up);
                float centreDistance = planarRelative.magnitude;
                Collider humanCollider = FindPrimaryHumanCollider(human);
                if (robotCollisionCollider != null && humanCollider != null)
                {
                    Vector3 robotCentre = robotCollisionCollider.bounds.center;
                    Vector3 humanCentre = humanCollider.bounds.center;
                    relativeWorld = humanCentre - robotCentre;
                    planarRelative = Vector3.ProjectOnPlane(relativeWorld, Vector3.up);
                    centreDistance = planarRelative.magnitude;
                }
                float distance = CalculateSurfaceDistance(humanCollider, centreDistance);
                if (distance > maximumRange || distance >= nearestDistance)
                    continue;

                Vector3 cameraLocal = reference.InverseTransformPoint(human.position);
                if (cameraLocal.z <= 0f)
                    continue;
                float horizontalAngle = Mathf.Abs(Mathf.Atan2(cameraLocal.x, cameraLocal.z) * Mathf.Rad2Deg);
                if (horizontalAngle > horizontalFieldOfView * 0.5f)
                    continue;

                bool lineOfSight = HasLineOfSight(reference.position, human);
                if (requireLineOfSight && !lineOfSight)
                    continue;

                Vector3 relativeVelocityWorld = humanVelocity - robotVelocity;
                Vector3 planarRelativeVelocity = Vector3.ProjectOnPlane(relativeVelocityWorld, Vector3.up);
                float closingSpeed = centreDistance > 0.001f
                    ? -Vector3.Dot(planarRelative / centreDistance, planarRelativeVelocity)
                    : 0f;
                float clearance = Mathf.Max(0f, distance - surfaceSafetyMargin);
                float ttc = closingSpeed > 0.01f ? clearance / closingSpeed : float.PositiveInfinity;

                nearestDistance = distance;
                HasObservation = true;
                LatestObservation = new Observation
                {
                    target = human,
                    normalizedHorizontalPosition = Mathf.Clamp(
                        cameraLocal.x / Mathf.Max(cameraLocal.z * Mathf.Tan(horizontalFieldOfView * 0.5f * Mathf.Deg2Rad), 0.001f),
                        -1f, 1f),
                    localPosition = transform.InverseTransformDirection(relativeWorld),
                    localRelativeVelocity = transform.InverseTransformDirection(relativeVelocityWorld),
                    planarDistance = distance,
                    closingSpeed = closingSpeed,
                    timeToCollision = ttc,
                    lineOfSight = lineOfSight,
                    timestamp = now
                };
            }
        }

        private Vector3 EstimateVelocity(Transform target, double now, float fallbackDeltaTime)
        {
            Rigidbody body = target.GetComponentInParent<Rigidbody>();
            if (body != null)
                return body.linearVelocity;
            NavMeshAgent agent = target.GetComponentInParent<NavMeshAgent>();
            if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
                return agent.velocity;

            if (!motionSamples.TryGetValue(target, out MotionSample sample))
            {
                motionSamples[target] = new MotionSample
                {
                    position = target.position,
                    velocity = Vector3.zero,
                    timestamp = now
                };
                return Vector3.zero;
            }

            float deltaTime = Mathf.Max((float)(now - sample.timestamp), fallbackDeltaTime, 0.0001f);
            Vector3 velocity = (target.position - sample.position) / deltaTime;
            motionSamples[target] = new MotionSample
            {
                position = target.position,
                velocity = velocity,
                timestamp = now
            };
            return velocity;
        }

        private Collider FindPrimaryHumanCollider(Transform human)
        {
            CharacterController controller = human.GetComponentInParent<CharacterController>();
            if (controller != null && controller.enabled)
                return controller;
            CapsuleCollider capsule = human.GetComponentInParent<CapsuleCollider>();
            if (capsule != null && capsule.enabled && !capsule.isTrigger)
                return capsule;
            Collider[] colliders = human.GetComponentsInChildren<Collider>();
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null && colliders[i].enabled && !colliders[i].isTrigger)
                    return colliders[i];
            }
            return null;
        }

        private float CalculateSurfaceDistance(Collider humanCollider, float fallbackDistance)
        {
            if (robotCollisionCollider == null || humanCollider == null)
                return fallbackDistance;

            Bounds robotBounds = robotCollisionCollider.bounds;
            Bounds humanBounds = humanCollider.bounds;
            float gapX = Mathf.Max(0f,
                Mathf.Max(robotBounds.min.x - humanBounds.max.x, humanBounds.min.x - robotBounds.max.x));
            float gapZ = Mathf.Max(0f,
                Mathf.Max(robotBounds.min.z - humanBounds.max.z, humanBounds.min.z - robotBounds.max.z));
            return Mathf.Sqrt(gapX * gapX + gapZ * gapZ);
        }

        private bool HasLineOfSight(Vector3 origin, Transform human)
        {
            Vector3 direction = human.position - origin;
            float distance = direction.magnitude;
            if (distance < 0.001f)
                return true;

            RaycastHit[] hits = Physics.RaycastAll(
                origin, direction / distance, distance, occlusionMask, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            for (int i = 0; i < hits.Length; i++)
            {
                Transform hit = hits[i].transform;
                if (hit == transform || hit.IsChildOf(transform))
                    continue;
                return hit == human || hit.IsChildOf(human) || human.IsChildOf(hit);
            }
            return true;
        }

        private void OnGUI()
        {
            if (!showDebugPanel)
                return;
            EnsureStyles();
            Rect panel = new Rect(Screen.width - 375f, Screen.height - 130f, 365f, 120f);
            GUI.Box(panel, GUIContent.none);
            GUI.Label(new Rect(panel.x + 10f, panel.y + 7f, 345f, 22f), "HUMAN GROUND-TRUTH SENSOR", titleStyle);
            if (!HasObservation)
            {
                GUI.Label(new Rect(panel.x + 10f, panel.y + 35f, 345f, 70f),
                    $"No human in view\nRange {maximumRange:F1} m / FOV {horizontalFieldOfView:F0} deg", valueStyle);
                return;
            }

            Observation o = LatestObservation;
            string ttc = float.IsPositiveInfinity(o.timeToCollision) ? "INF" : $"{o.timeToCollision:F2} s";
            GUI.Label(new Rect(panel.x + 10f, panel.y + 33f, 345f, 80f),
                $"Target: {o.target.name}   screen x={o.normalizedHorizontalPosition:F2}\n" +
                $"GT diagnostic clearance={o.planarDistance:F2} m\n" +
                $"local x/z=({o.localPosition.x:F2}, {o.localPosition.z:F2}) m\n" +
                $"relative vx/vz=({o.localRelativeVelocity.x:F2}, {o.localRelativeVelocity.z:F2}) m/s   " +
                $"closing={o.closingSpeed:F2}   TTC={ttc}", valueStyle);
        }

        private void OnDrawGizmosSelected()
        {
            if (!drawDebugGizmos)
                return;
            Gizmos.color = HasObservation ? Color.red : Color.yellow;
            if (HasObservation && LatestObservation.target != null)
                Gizmos.DrawLine(transform.position, LatestObservation.target.position);
        }

        private void EnsureStyles()
        {
            if (titleStyle != null)
                return;
            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.red }
            };
            valueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal = { textColor = Color.white }
            };
        }
    }
}
