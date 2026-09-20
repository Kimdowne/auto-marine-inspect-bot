using System;
using UnityEngine;

namespace Unity.Templates.IndustryFundamentals
{
    public enum OperationMode
    {
        Auto,
        Manual
    }

    public enum RobotStatus
    {
        STANDARD,
        WARNING,
        CRITICAL,
        DEAD
    }

    [CreateAssetMenu(fileName = nameof(RobotDataSO), menuName = "Industry Template/Robot Data")]
    public class RobotDataSO : ScriptableObject
    {
        [Header("ID")] public string robotName;

        [Header("Mode")]
        public OperationMode OperationMode = OperationMode.Auto;

        [Header("Status")] 
        public RobotStatus CurrentRobotStatus = RobotStatus.STANDARD;
        private RobotStatus _previousRobotStatus;

        [Header("Variant")]
        public int currentVariant;

        [Header("Runtime Telemetry")]
        [Range(0, 100)] public float Battery = 100f;
        [Range(0, 100)] public float Health = 100f;
        public float Temperature = 25f;
        public float Speed;
        [Range(0f, 1f)] public float ArmRange;
        public bool IsMoving;
    
        [Header("Settings – Battery Costs")]
        [Range(0f, 10f)] public float TurboMultiplier = 1f;
        public float SpeedUsageCost = 0.01f;
        public float ArmUsageCost = 0.05f;

        [Header("Settings – Robot Parameters")]
        public float BaseSpeed = 3f;

        [Header("Health Thresholds")] //values below which status changes to warning or critical
        public float HealthWarningThreshold = 60f;
        public float HealthCriticalThreshold = 30f;

        [Header("Battery Thresholds")] 
        public float BatteryWarningThreshold = 50f;
        public float BatteryCriticalThreshold = 20f;
    
        [Header("Health Penalties")]
        public float LowBatteryThreshold = 10f;
        public float LowBatteryHealthLossPerSecond = 1f;

        [Header("Collision Handling")]
        public float CollisionDamage = 10f;
        public int CollisionCount;

        public event Action<RobotStatus, int> RobotStatusChanged;
        public event Action<Vector3> RobotCollisionDetected;

        public int RobotRuntimeIndex { get; set; } // 0-based index of this robot, in the RobotManager array at runtime

        /// <summary>
        /// Resets critical SO values at startup, so that the Robot gets a fresh start.
        /// </summary>
        public void ResetToDefaultValues()
        {
            OperationMode = OperationMode.Auto;
            CurrentRobotStatus = RobotStatus.STANDARD;
        
            Battery = 100f;
            Health = 100f;
            Temperature = 25f;
            ArmRange = 0f;
            Speed = 0f;
            IsMoving = false;

            CollisionCount = 0;
        }

        public void UpdateStatus(RobotStatus newStatus)
        {
            CurrentRobotStatus = newStatus;
            _previousRobotStatus = CurrentRobotStatus;
            RobotStatusChanged?.Invoke(CurrentRobotStatus, RobotRuntimeIndex);
        }
    
        public void RegisterCollision(Vector3 locationOfCollision)
        {
            CollisionCount++;
            Health -= CollisionDamage;
            Health = Mathf.Clamp(Health, 0f, 100f);
        
            RobotCollisionDetected?.Invoke(locationOfCollision);
        }

        private void OnValidate()
        {
            if (CurrentRobotStatus != _previousRobotStatus)
            {
                RobotStatusChanged?.Invoke(CurrentRobotStatus, RobotRuntimeIndex);
                _previousRobotStatus = CurrentRobotStatus;
            }
        }

        public void SetRuntimeIndex(int index)
        {
            RobotRuntimeIndex = index;
        }
    }
}