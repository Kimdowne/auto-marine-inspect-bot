using System.Collections.Generic;
using Unity.Cinemachine;
using UnityEngine;
using Unity.Templates.IndustryFundamentals;

namespace Unity.Templates.IndustryFundamentals
{
    public class CinemachineSwitcher : MonoBehaviour
    {
        public GameObject followCamPrefab;

        private CinemachineCamera[] _cameras;
        private int _currentIndex;

        public void OnEnable()
        {
            RobotManager.Instance.ActiveRobotChanged += ChangeCameraTargetToActiveRobot;
            RobotManager.Instance.RobotListChanged += UpdateFollowCamList;
        }

        private void ChangeCameraTargetToActiveRobot(Robot activeRobot)
        {
            SwitchToCamera(activeRobot.RobotData.RobotRuntimeIndex);
        }

        private void UpdateFollowCamList(List<Robot> robots)
        {
            if (_cameras is { Length: > 0 })
            {
                foreach (CinemachineCamera cinemachineCamera in _cameras)
                {
                    Destroy(cinemachineCamera.gameObject);
                }
            }

            _cameras = new CinemachineCamera[robots.Count];
            for (int i = 0; i < robots.Count; i++)
            {
                _cameras[i] = Instantiate(followCamPrefab, robots[i].transform.position, robots[i].transform.rotation).GetComponent<CinemachineCamera>();
                _cameras[i].Follow = robots[i].transform;
                _cameras[i].transform.parent = transform;
                _cameras[i].CancelDamping(true);
            }
        }

        private void SwitchToCamera(int index)
        {
            if (index < 0 || index >= _cameras.Length)
            {
                Debug.LogWarning("Camera index out of range.");
                return;
            }

            foreach (CinemachineCamera cinemachineCamera in _cameras)
                cinemachineCamera.Priority = 0;

            _cameras[index].Priority = 100;
        }
    
        private void OnDisable()
        {
            RobotManager.Instance.ActiveRobotChanged -= ChangeCameraTargetToActiveRobot;
            RobotManager.Instance.RobotListChanged -= UpdateFollowCamList;
        }
    }
}