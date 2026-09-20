using UnityEngine;
using UnityEngine.AI;

namespace ShipRobot.ObstacleAvoidance
{
    /// <summary>
    /// Scripted pedestrian motion for training episodes and the inspection demo.
    /// The pedestrian approaches the robot along a configured straight path.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TrainingPedestrianMover : MonoBehaviour
    {
        public enum ScenarioKind
        {
            ApproachLeft,
            ApproachCentre,
            ApproachRight
        }

        public bool IsArmed { get; private set; }
        public bool HasStartedWalking { get; private set; }
        public bool HasCompletedRoute { get; private set; }
        public float RobotDistanceToPedestrian { get; private set; } = float.PositiveInfinity;
        public string ScenarioLabel { get; private set; } = "OFF";
        public ScenarioKind Scenario { get; private set; }
        public float ApproachAngleDegrees { get; private set; }
        public Vector3 PlannedStartPosition { get; private set; }
        public Vector3 PlannedEndPosition { get; private set; }

        private Transform robot;
        private Vector3 crossingCentre;
        private Vector3 crossingDirection;
        private float crossingHalfWidth;
        private float movementSpeed;
        private float progress;
        private float fixedHeight;
        private NavMeshAgent navigationAgent;
        private RandomPatrol randomPatrol;
        private Animator animator;
        private bool navigationAgentWasEnabled;
        private bool randomPatrolWasEnabled;
        private bool animatorRootMotionWasEnabled;
        private bool suspendedExistingMotion;
        private static readonly int SpeedParameter = Animator.StringToHash("Speed");

        public void ConfigureApproachScenario(
            Transform robotTransform,
            Vector3 startPosition,
            Vector3 targetPosition,
            float extraTravelDistance,
            float speed,
            float signedCameraAngleDegrees)
        {
            SuspendExistingMotion();
            robot = robotTransform;
            crossingDirection = Vector3.ProjectOnPlane(targetPosition - startPosition, Vector3.up).normalized;
            if (crossingDirection.sqrMagnitude < 0.5f)
                crossingDirection = Vector3.back;

            float distanceToTarget = Vector3.Distance(
                Vector3.ProjectOnPlane(startPosition, Vector3.up),
                Vector3.ProjectOnPlane(targetPosition, Vector3.up));
            float distance = Mathf.Max(1f, distanceToTarget + Mathf.Max(0f, extraTravelDistance));
            crossingHalfWidth = distance * 0.5f;
            crossingCentre = startPosition + crossingDirection * crossingHalfWidth;
            PlannedStartPosition = startPosition;
            PlannedEndPosition = startPosition + crossingDirection * distance;
            movementSpeed = Mathf.Max(0.05f, speed);
            progress = -crossingHalfWidth;
            fixedHeight = transform.position.y;
            ApproachAngleDegrees = signedCameraAngleDegrees;
            Scenario = signedCameraAngleDegrees < -3f
                ? ScenarioKind.ApproachLeft
                : signedCameraAngleDegrees > 3f
                    ? ScenarioKind.ApproachRight
                    : ScenarioKind.ApproachCentre;
            ScenarioLabel = $"APPROACH {ScenarioShortName(Scenario)} {signedCameraAngleDegrees:+0;-0;0} deg";
            IsArmed = true;
            HasStartedWalking = true;
            HasCompletedRoute = false;
            RobotDistanceToPedestrian = Vector3.Distance(robotTransform.position, startPosition);
            SetWalkingAnimation(movementSpeed);
            ApplyPosition();
        }

        public void StopMotion()
        {
            IsArmed = false;
            HasStartedWalking = false;
            HasCompletedRoute = false;
            RobotDistanceToPedestrian = float.PositiveInfinity;
            ScenarioLabel = "OFF";
            SetWalkingAnimation(0f);
            RestoreExistingMotion();
        }

        public void ParkOffCamera(Vector3 parkingPosition)
        {
            // Keep the GameObject and this mover enabled for the next encounter.
            // Only the old patrol/NavMesh motion stays suspended.
            SuspendExistingMotion();
            IsArmed = false;
            HasStartedWalking = false;
            HasCompletedRoute = false;
            RobotDistanceToPedestrian = float.PositiveInfinity;
            ScenarioLabel = "PARKED";
            SetWalkingAnimation(0f);
            parkingPosition.y = transform.position.y;
            transform.position = parkingPosition;
        }

        private void LateUpdate()
        {
            if (!IsArmed || HasCompletedRoute)
                return;

            if (robot != null)
                RobotDistanceToPedestrian = Vector3.Distance(robot.position, transform.position);

            progress = Mathf.MoveTowards(progress, crossingHalfWidth, movementSpeed * Time.deltaTime);
            SetWalkingAnimation(movementSpeed);
            ApplyPosition();
            if (progress >= crossingHalfWidth - 0.001f)
            {
                HasCompletedRoute = true;
                SetWalkingAnimation(0f);
            }
        }

        private static string ScenarioShortName(ScenarioKind scenario)
        {
            switch (scenario)
            {
                case ScenarioKind.ApproachLeft: return "LEFT";
                case ScenarioKind.ApproachRight: return "RIGHT";
                default: return "CENTRE";
            }
        }

        private void ApplyPosition()
        {
            Vector3 position = crossingCentre + crossingDirection * progress;
            position.y = fixedHeight;
            transform.position = position;
            transform.rotation = Quaternion.LookRotation(crossingDirection, Vector3.up);
        }

        private void SuspendExistingMotion()
        {
            if (suspendedExistingMotion)
                return;
            navigationAgent = GetComponent<NavMeshAgent>();
            randomPatrol = GetComponent<RandomPatrol>();
            animator = GetComponentInChildren<Animator>();
            navigationAgentWasEnabled = navigationAgent != null && navigationAgent.enabled;
            randomPatrolWasEnabled = randomPatrol != null && randomPatrol.enabled;
            animatorRootMotionWasEnabled = animator != null && animator.applyRootMotion;
            if (randomPatrol != null)
                randomPatrol.enabled = false;
            if (navigationAgent != null)
                navigationAgent.enabled = false;
            if (animator != null)
                animator.applyRootMotion = false;
            suspendedExistingMotion = true;
        }

        private void RestoreExistingMotion()
        {
            if (!suspendedExistingMotion)
                return;
            if (navigationAgent != null)
                navigationAgent.enabled = navigationAgentWasEnabled;
            if (randomPatrol != null)
                randomPatrol.enabled = randomPatrolWasEnabled;
            if (animator != null)
                animator.applyRootMotion = animatorRootMotionWasEnabled;
            suspendedExistingMotion = false;
        }

        private void SetWalkingAnimation(float speed)
        {
            if (animator == null)
                animator = GetComponentInChildren<Animator>();
            if (animator != null)
                animator.SetFloat(SpeedParameter, Mathf.Max(0f, speed));
        }

        private void OnDisable()
        {
            SetWalkingAnimation(0f);
            RestoreExistingMotion();
        }
    }
}
