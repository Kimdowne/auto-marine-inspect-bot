using System.Collections.Generic;
using UnityEngine;
using Unity.Templates.IndustryFundamentals;


//[CreateAssetMenu(fileName = nameof(OverallDataSO), menuName = "Industry Template/Overall Data")]
namespace Unity.Templates.IndustryFundamentals
{
    public class OverallDataSO : ScriptableObject
    {
        public List<RobotDataSO> robotDataList = new();
    
        [Header("Calculated Statistics")]
        public float AverageBattery;
        public float MaxHealth;
        public float MinHealth;
        public int MovingRobotsCount;
        public float AverageSpeed;
        public int TotalRobots;
        public int standardStatusBotCount;
        public int warningStatusBotCount;
        public int criticalStatusBotCount;
        public float AverageTemperature; // Placeholder for future use

        public void CollectRobotData()
        {
            MaxHealth = 0f;
            MinHealth = float.MaxValue;
            MovingRobotsCount = 0;
            TotalRobots = 0;
            standardStatusBotCount = 0;
            warningStatusBotCount = 0;
            criticalStatusBotCount = 0;
        
            float totalBattery = 0f;
            float totalSpeed = 0f;
            float totalTemperature = 0f;

            foreach (RobotDataSO robot in robotDataList)
            {
                totalBattery += robot.Battery;
                totalSpeed += robot.Speed;
                totalTemperature += robot.Temperature;
            
                if (robot.Health > MaxHealth)
                    MaxHealth = robot.Health;
            
                if (robot.Health < MinHealth)
                    MinHealth = robot.Health;
            
                if (robot.IsMoving)
                    MovingRobotsCount++;
           
                TotalRobots++;
                if (robot.CurrentRobotStatus == RobotStatus.STANDARD) standardStatusBotCount++;
                if (robot.CurrentRobotStatus == RobotStatus.WARNING) warningStatusBotCount++;
                if (robot.CurrentRobotStatus == RobotStatus.CRITICAL) criticalStatusBotCount++;
                if (robot.CurrentRobotStatus == RobotStatus.DEAD) criticalStatusBotCount++;
            }

            AverageBattery = TotalRobots > 0 ? totalBattery / TotalRobots : 0f;
            AverageSpeed = TotalRobots > 0 ? totalSpeed / TotalRobots : 0f;
            AverageTemperature = TotalRobots > 0 ? totalTemperature / TotalRobots : 0f;
        }
    }
}
