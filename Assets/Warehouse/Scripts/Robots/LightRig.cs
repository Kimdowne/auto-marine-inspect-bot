using UnityEngine;

namespace Unity.Templates.IndustryFundamentals
{
    public class LightRig : MonoBehaviour
    {
        public Light[] lights;
        
        private Robot _robotScript;

        private void Awake()
        {
            _robotScript = GetComponentInParent<Robot>();
            SetLights(false);
        }

        private void OnEnable()
        {
            RobotManager.Instance.ActiveRobotChanged += OnRobotChanged;
        }

        private void OnDisable()
        {
            RobotManager.Instance.ActiveRobotChanged -= OnRobotChanged;
        }

        private void OnRobotChanged(Robot robot) => SetLights(robot == _robotScript);

        private void SetLights(bool on)
        {
            foreach (Light l in lights) l.enabled = on;
        }
    }
}