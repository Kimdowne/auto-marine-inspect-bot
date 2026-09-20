using UnityEngine;
using UnityEngine.AI;

namespace Unity.Templates.IndustryFundamentals
{
    /// <summary>
    /// Simulates and updates the robot's data, like health, temperature.
    /// Also processes collisions with other robots.
    /// </summary>
    public class RobotDataSimulator : MonoBehaviour
    {
        private Rigidbody _rigidbody;
        private RobotDataSO _robotData;
        private NavMeshAgent _navMeshAgent;
        private float _lastArmRange;
    
        private float _previousHealthValue;
        private float _previousBatteryValue;
        private Vector3 _lastPosition;

        private void Awake()
        {
            _rigidbody = GetComponent<Rigidbody>();
            _navMeshAgent = GetComponent<NavMeshAgent>();
            _robotData = GetComponent<Robot>().RobotData;
        }

        private void Start()
        {
            InitializeRobotData();
            _lastPosition = transform.position;
        }

        private void InitializeRobotData()
        {
            _robotData.ResetToDefaultValues();
        
            _lastArmRange = _robotData.ArmRange;
            _previousHealthValue = _robotData.Health;
            _previousBatteryValue = _robotData.Battery;
        }

        private void Update()
        {
            UpdateSpeed();
            UpdateTemperature();
            UpdateBattery();
            UpdateHealth();
        }

        private void UpdateSpeed()
        {
            _robotData.Speed = _robotData.OperationMode switch
            {
                OperationMode.Auto => _navMeshAgent.enabled ? _navMeshAgent.velocity.magnitude : 0f,
                OperationMode.Manual => PositionChange()/Time.deltaTime/_robotData.BaseSpeed,
                _ => _robotData.Speed
            };

            _robotData.IsMoving = _robotData.Speed > 0.01f;
            _lastPosition = transform.position;
        }

        private float PositionChange()
        {
            return Vector3.Magnitude(_lastPosition - transform.position);
        }

        private void UpdateTemperature()
        {
            // Temperature rises when moving fast
            if (_robotData.Speed > 0.5f)
                _robotData.Temperature += _robotData.Speed * 0.2f * Time.deltaTime;

            // Cool down when idle
            if (!_robotData.IsMoving)
                _robotData.Temperature -= 5f * Time.deltaTime;

            _robotData.Temperature = Mathf.Clamp(_robotData.Temperature, 0f, 100f);
        }

        private void UpdateBattery()
        {
            float drain = 0;

            // Movement drain
            drain += _robotData.Speed * _robotData.SpeedUsageCost * 60f;

            // Arm drain
            float armDelta = Mathf.Abs(_robotData.ArmRange - _lastArmRange);
            if (armDelta > 0f)
                drain += armDelta * _robotData.ArmUsageCost * 60f;

            _robotData.Battery -= drain * Time.deltaTime;
            _robotData.Battery = Mathf.Clamp(_robotData.Battery, 0f, 100f);
        
            _lastArmRange = _robotData.ArmRange;
        
            UpdateRobotStatusByBatteryThresholds();

            _previousBatteryValue = _robotData.Battery;
        }
    
        private void UpdateHealth()
        {
            if (_robotData.Battery <= _robotData.LowBatteryThreshold)
                _robotData.Health -= _robotData.LowBatteryHealthLossPerSecond * Time.deltaTime;

            _robotData.Health = Mathf.Clamp(_robotData.Health, 0f, 100f);
        
            UpdateRobotStatusByHealthThresholds();

            _previousHealthValue = _robotData.Health;
        }
    
        private void UpdateRobotStatusByBatteryThresholds()
        {
            //Cascade down potential thresholds being crossed to trigger status changed event only when thresholds are crossed this frame
            if (_previousBatteryValue >= _robotData.BatteryWarningThreshold && _robotData.Battery < _robotData.BatteryWarningThreshold)
                _robotData.UpdateStatus(RobotStatus.WARNING);
            else if (_previousBatteryValue >= _robotData.BatteryCriticalThreshold && _robotData.Battery < _robotData.BatteryCriticalThreshold)
                _robotData.UpdateStatus(RobotStatus.CRITICAL);
            else if (_previousBatteryValue > 0 && _robotData.Battery <= 0)
                _robotData.UpdateStatus(RobotStatus.DEAD);
        
            //Cascade up potential thresholds being crossed to trigger status changed event only when thresholds are crossed this frame
            if (_previousBatteryValue <= 0 && _robotData.Battery > 0)
                _robotData.UpdateStatus(RobotStatus.CRITICAL);
            else if (_previousBatteryValue < _robotData.BatteryCriticalThreshold && _robotData.Battery >= _robotData.BatteryCriticalThreshold)
                _robotData.UpdateStatus(RobotStatus.WARNING);
            else if (_previousBatteryValue < _robotData.BatteryWarningThreshold && _robotData.Battery >= _robotData.BatteryWarningThreshold)
                _robotData.UpdateStatus(RobotStatus.STANDARD);
        }
    
        private void UpdateRobotStatusByHealthThresholds()
        {
            //Cascade down potential thresholds being crossed to trigger status changed event only when thresholds are crossed this frame
            if (_previousHealthValue >= _robotData.HealthWarningThreshold && _robotData.Health < _robotData.HealthWarningThreshold)
                _robotData.UpdateStatus(RobotStatus.WARNING);
            else if (_previousHealthValue >= _robotData.HealthCriticalThreshold && _robotData.Health < _robotData.HealthCriticalThreshold)
                _robotData.UpdateStatus(RobotStatus.CRITICAL);
            else if (_previousHealthValue > 0 && _robotData.Health <= 0)
                _robotData.UpdateStatus(RobotStatus.DEAD);
        
            //Cascade up potential thresholds being crossed to trigger status changed event only when thresholds are crossed this frame
            if (_previousHealthValue <= 0 && _robotData.Health > 0)
                _robotData.UpdateStatus(RobotStatus.CRITICAL);
            else if (_previousHealthValue < _robotData.HealthCriticalThreshold && _robotData.Health >= _robotData.HealthCriticalThreshold)
                _robotData.UpdateStatus(RobotStatus.WARNING);
            else if (_previousHealthValue < _robotData.HealthWarningThreshold && _robotData.Health >= _robotData.HealthWarningThreshold)
                _robotData.UpdateStatus(RobotStatus.STANDARD);
        }

        public void OnCollisionEnter(Collision other)
        {
            _robotData.RegisterCollision(transform.position);
        }
    }
}