using System.Collections.Generic;
using Unity.Templates.IndustryFundamentals;
using UnityEngine;

namespace Unity.Templates.IndustryFundamentals
{
    /// <summary>
    /// Updates the <see cref="OverallDataSO"/> ScriptableObject every frame,
    /// or at a specific time interval.
    /// </summary>
    public class OverallDataUpdater : MonoBehaviour
    {
        [SerializeField] private OverallDataSO _dataSO;

        [Header("Update Settings")]
        [Tooltip("Update every frame if true, otherwise use updateInterval")]
        [SerializeField] private bool _updateEveryFrame = true;
    
        [Tooltip("Time in seconds between updates (only used if updateEveryFrame is false)")]
        [SerializeField] private float _updateInterval = 1f;
    
        private float _timeSinceLastUpdate;

        private void OnEnable()
        {
            RobotManager.Instance.RobotListChanged += UpdateOverallDataRobotList;
        }

        private void UpdateOverallDataRobotList(List<Robot> newRobots)
        {
            _dataSO.robotDataList.Clear();
            foreach (Robot robot in newRobots)
            {
                _dataSO.robotDataList.Add(robot.RobotData);
            }
        }

        private void Update()
        {
            if (_updateEveryFrame)
            {
                _dataSO.CollectRobotData();
            }
            else
            {
                _timeSinceLastUpdate += Time.deltaTime;
                if (_timeSinceLastUpdate >= _updateInterval)
                {
                    _dataSO.CollectRobotData();
                    _timeSinceLastUpdate = 0f;
                }
            }
        }
    
        private void OnDisable()
        {
            RobotManager.Instance.RobotListChanged -= UpdateOverallDataRobotList;
        }
    }
}

