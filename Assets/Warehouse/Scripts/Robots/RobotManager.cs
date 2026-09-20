using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Unity.Templates.IndustryFundamentals
{
    public class RobotManager : MonoBehaviour
    {
        public GameObject robotPrefab;
        public static RobotManager Instance { get; private set; }
        public event Action<Robot> ActiveRobotChanged;
        public event Action<List<Robot>> RobotListChanged;
    
        public Robot GetActiveRobot() => _activeRobot;

        private List<Robot> _robots;
        private Robot _activeRobot;

        private void Awake()
        {
            InitSingleton();
        }

        private void InitSingleton()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            FindAndSetupRobots();
        }

        private void FindAndSetupRobots()
        {
            _robots = FindObjectsByType<Robot>().ToList();
            _robots.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

            for (int i = 0; i < _robots.Count; i++)
            {
                _robots[i].RobotData.RobotRuntimeIndex = i;
                _robots[i].RobotData.ResetToDefaultValues();
            }

            RobotListChanged?.Invoke(_robots);

            if (_robots.Count > 0)
                SetActiveRobot(_robots[0]);
        }

        public void SetActiveRobot(Robot robot)
        {
            _activeRobot?.SetOperationMode(OperationMode.Auto); // Reset old active to Auto
        
            _activeRobot = robot;
            ActiveRobotChanged?.Invoke(robot);
        }

        public Robot GetRobotByIndex(int index) => _robots[index];

        public void SpawnNewRobot()
        {
            GameObject newRobotObject = Instantiate(robotPrefab, Vector3.zero, Quaternion.identity);
            RobotDataSO newRobotSO = ScriptableObject.CreateInstance<RobotDataSO>();
            newRobotSO.robotName = $"RuntimeRobot{_robots.Count+1}";

            Robot newRobot = newRobotObject.GetComponent<Robot>();
            newRobot.AssignNewDataSO(newRobotSO);
            newRobot.RobotData.RobotRuntimeIndex = _robots.Count;

            _robots.Add(newRobot);
        
            RobotListChanged?.Invoke(_robots);
        }
    }
}