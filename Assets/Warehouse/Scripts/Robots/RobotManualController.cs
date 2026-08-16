using UnityEngine;
using UnityEngine.AI;

namespace Unity.Templates.IndustryFundamentals
{
    public class RobotManualController : MonoBehaviour
    {
        [Header("Movement Settings")]
        [SerializeField] private float _rotationSpeed = 70f;
        [SerializeField] private float _navMeshCheckRadius = 0.5f;
    
        [Header("Collision Settings")]
        [SerializeField] private LayerMask _botLayer; // Set this to "Bot" in the Inspector
        [SerializeField] private float _raycastDistance = 1.0f;
        [SerializeField] private float _raycastRadius = 0.4f;

        [Header("Acceleration Settings")]
        [SerializeField] private float _acceleration = 20f;
        [SerializeField] private float _deceleration = 20f;

        [Header("Thermal / Battery Simulation")]
        [SerializeField] private float _turboBatteryDrain = 0.06f;   // battery per second
        [SerializeField] private float _normalBatteryDrain = 0.01f;  // battery per second

        [SerializeField] private InputSystem_Actions _inputSystemActions;

        private float _currentSpeed;
        private float _targetSpeed;
        private Robot _activeRobot;
        private RobotDataSO _robotData;

        private void Awake()
        {
            _inputSystemActions = new InputSystem_Actions();
        }

        private void OnEnable()
        {
            _inputSystemActions.Player.Enable();
            RobotManager.Instance.ActiveRobotChanged += ActiveRobotChanged;
        }

        private void ActiveRobotChanged(Robot activeRobot)
        {
            _activeRobot = activeRobot;
            _robotData = _activeRobot.RobotData;
        }

        private void Update()
        {
            if (_robotData == null || _robotData.OperationMode == OperationMode.Auto) return;

            HandleMovement(_activeRobot, _robotData);
            SimulateBatteryAndTemperature(_robotData);
        }

        private void HandleMovement(Robot robot, RobotDataSO data)
        {
            Vector2 moveXY = _inputSystemActions.Player.Move.ReadValue<Vector2>();
            Transform robotTransform = robot.transform;

            float maxSpeed = data.BaseSpeed * Mathf.Max(0.01f, data.TurboMultiplier);
        
            if (Mathf.Abs(moveXY.x) > 0.01f)
            {
                float turn = moveXY.x * _rotationSpeed * Time.deltaTime * Mathf.Sign(moveXY.y);
                robotTransform.Rotate(0f, turn, 0f);
            }
        
            _targetSpeed = Mathf.Abs(moveXY.y) > 0.01f ? maxSpeed * Mathf.Sign(moveXY.y) : 0f;
            _currentSpeed = Mathf.MoveTowards(_currentSpeed, _targetSpeed, 
                (Mathf.Abs(_targetSpeed) > 0.01f ? _acceleration : _deceleration) * Time.deltaTime);
        
            if (IsPathBlocked(robotTransform, _targetSpeed))
            {
                _targetSpeed = 0f;
                _currentSpeed = 0f;
            }
        
            if (Mathf.Abs(_currentSpeed) > 0.01f)
            {
                Vector3 movementStep = robotTransform.forward * (_currentSpeed * Time.deltaTime);
                Vector3 targetPosition = robotTransform.position + movementStep;

                //We use a slightly larger radius for SamplePosition to find the closest valid NavMesh edge outside our own carving hole.
                if (NavMesh.SamplePosition(targetPosition, out NavMeshHit hit, _navMeshCheckRadius + 0.3f, NavMesh.AllAreas)) 
                {
                    robotTransform.position = targetPosition;
                    data.IsMoving = true;
                }
                else
                {
                    _currentSpeed = 0f;
                    data.IsMoving = false;
                }
            }
        
            data.Speed = Mathf.Abs(_currentSpeed);
        }
    
        private bool IsPathBlocked(Transform t, float targetSpeed)
        {
            // Only check if we are actually trying to move forward or backward
            if (Mathf.Abs(targetSpeed) < 0.01f) return false;

            // Determine direction (forward or backward)
            Vector3 direction = targetSpeed > 0 ? t.forward : -t.forward;
        
            // Origin slightly offset upward so it doesn't hit the floor
            Vector3 origin = t.position + Vector3.up * 0.5f;

            // Perform SphereCast to detect other bots
            if (Physics.SphereCast(origin, _raycastRadius, direction, out RaycastHit hit, _raycastDistance, _botLayer))
            {
                // Ensure we aren't hitting ourselves (if the bot has a collider on the Bot layer)
                if (hit.transform != t) return true;
            }

            return false;
        }

        private void SimulateBatteryAndTemperature(RobotDataSO data)
        {
            bool turbo = (_inputSystemActions.Player.Turbo.ReadValue<float>() > 0);
            bool moving = Mathf.Abs(_currentSpeed) > 0.1f;

            float dt = Time.deltaTime;

            if (moving)
            {
                data.Battery -= (turbo ? _turboBatteryDrain : _normalBatteryDrain) * dt;
                data.Temperature += turbo ? 0.7f * dt : 0.25f * dt;
            }
            else
            {
                data.Temperature -= 0.3f * dt;
            }

            data.Battery = Mathf.Clamp(data.Battery, 0f, 100f);
            data.Temperature = Mathf.Clamp(data.Temperature, 0f, 100f);
        }

        private void OnDisable()
        {
            _inputSystemActions.Player.Disable();
            RobotManager.Instance.ActiveRobotChanged -= ActiveRobotChanged;
        }
    }
}