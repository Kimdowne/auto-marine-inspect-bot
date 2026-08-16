using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Unity.Templates.IndustryFundamentals;

namespace Unity.Templates.IndustryFundamentals
{
    [RequireComponent(typeof(UIDocument))]
    public class RobotMapController : MonoBehaviour
    {
        [SerializeField] private VisualTreeAsset _robotDotLabel;
        [SerializeField] private Color _healthyGreen;
        [SerializeField] private Color _warningAmber;
        [SerializeField] private Color _criticalRed;
    
        [Header("Warehouse Bounds")]
        public float minX;
        public float maxX;
        public float minZ;
        public float maxZ;

        [Header("MiniMapBounds")] 
        public float minMapFromLeft;
        public float maxMapFromLeft;
        public float minMapFromTop;
        public float maxMapFromTop;

        private List<Transform> _robotTransforms = new();

        private UIDocument _uiDocument;
        private VisualElement _dotsContainer;
        private Dictionary<Transform, VisualElement> _dots = new();

        private void Awake()
        {
            _uiDocument = GetComponent<UIDocument>();
            StartCoroutine(InitializeUI());
        }

        private void OnEnable()
        {
            RobotManager.Instance.RobotListChanged += SetLocalRobotReferences;
        }

        private void OnDisable()
        {
            RobotManager.Instance.RobotListChanged -= SetLocalRobotReferences;
        }

        private void SetLocalRobotReferences(List<Robot> newRobots)
        {
            _robotTransforms.Clear();
            for (int i = 0; i < newRobots.Count; i++)
            {
                _robotTransforms.Add(newRobots[i].transform);
            }
        }

        private IEnumerator InitializeUI()
        {
            // Wait one frame to ensure all template instances are created
            yield return null;

            VisualElement root = _uiDocument.rootVisualElement;

            _dotsContainer = root.Q<VisualElement>("RobotMiniMap");

            CreateRobotDots();
        }

        private void CreateRobotDots()
        {
            foreach (Transform robot in _robotTransforms)
            {
                string id = robot.GetComponent<Robot>().RobotData.robotName;
            
                VisualElement dotContainer = _robotDotLabel.Instantiate();
                dotContainer.name = $"Dot_{id}";
                dotContainer.Q<Label>("RobotID").text = id;

                _dotsContainer.Add(dotContainer);
                _dots.Add(robot, dotContainer);
            }
        }

        private void Update()
        {
            if (_dotsContainer == null)
                return;

            foreach (KeyValuePair<Transform, VisualElement> kvp in _dots)
            {
                Transform robot = kvp.Key;
                VisualElement dot = kvp.Value;

                Vector3 pos = robot.position;

                float nx = Mathf.InverseLerp(minX, maxX, pos.x);
                float ny = Mathf.InverseLerp(minZ, maxZ, pos.z);

                float uiX = Mathf.Lerp(minMapFromLeft, maxMapFromLeft, nx);
                float uiY = Mathf.Lerp(maxMapFromTop, minMapFromTop, ny);  

                dot.style.position = Position.Absolute;
                dot.style.left = Length.Percent(uiX);
                dot.style.top  = Length.Percent(uiY);

                // OPTIONAL: Robot state color
                Color statusColor = GetColorByStatus(robot);

                dot.Q<VisualElement>("RobotLocationDot").style.backgroundColor = statusColor;
                dot.Q<Label>("RobotID").style.color = statusColor;
            }
        }

        private Color GetColorByStatus(Transform robotTransform)
        {
            RobotStatus robotStatus = robotTransform.GetComponent<Robot>().RobotData.CurrentRobotStatus;
        
            switch (robotStatus)
            {
                case RobotStatus.STANDARD:
                    return _healthyGreen;
                case RobotStatus.WARNING:
                    return _warningAmber;
                case RobotStatus.CRITICAL:
                    return _criticalRed;
                case RobotStatus.DEAD:
                    return _criticalRed;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}