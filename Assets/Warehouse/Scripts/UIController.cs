using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Unity.Templates.IndustryFundamentals;

namespace Unity.Templates.IndustryFundamentals
{
    public class UIController : MonoBehaviour
    {
        [SerializeField] private VisualTreeAsset _robotDataCardTemplate;
    
        private UIDocument _UIDocument;
        private VisualElement _root;

        // Left panel
        private ScrollView _leftPanelRobotCardListView;
        private List<VisualElement> _robotDataCards = new(); // Updated every time a new robot is added
        private VisualElement _pipelineHeader; // Temp clickable header, for Robot spawn testing
    
        // Right panel
        private VisualElement _rightPanel;
        private VisualElement _autoModeButton;
        private VisualElement _manualModeButton;
        private VisualElement _switchVariantButton;
        private VisualElement _manualModeWarning;
        private Label _variantNameLabel;
        private List<Slider> _rightPanelSliders;
        private Robot _activeRobot;

        private void OnEnable()
        {
            _UIDocument = GetComponent<UIDocument>();
            _root = _UIDocument.rootVisualElement;
            CacheUIElements();
            BindButtonClickCallbacks();
        
            RobotManager.Instance.ActiveRobotChanged += OnRobotChanged;
            RobotManager.Instance.RobotListChanged += OnRobotListChanged;
        }

        private void OnRobotListChanged(List<Robot> robots)
        {
            //Clear out and build new UI cards for robots in the array
            _robotDataCards.Clear();
            _leftPanelRobotCardListView.Clear();

            for (int i = 0; i < robots.Count; i++)
            {
                VisualElement newRobotCard = _robotDataCardTemplate.Instantiate();
                newRobotCard[0].dataSource = robots[i].RobotData;
                newRobotCard.Q<Label>("CardID").text = (i + 1).ToString();
                newRobotCard.Q<Label>("RobotIDLabel").text = $"Robot {i + 1}";

                int index = i;  //closure captured on purpose for stability of index
                newRobotCard[0].RegisterCallback<ClickEvent>(_ => RobotManager.Instance.SetActiveRobot(RobotManager.Instance.GetRobotByIndex(index)));

                robots[i].RobotData.RobotStatusChanged -= UpdateCardStatus; //unsub to prevent duplicate listeners - safe if none are already subscribed
                robots[i].RobotData.RobotStatusChanged += UpdateCardStatus;
            
                _robotDataCards.Add(newRobotCard);
                _leftPanelRobotCardListView.Add(_robotDataCards[^1]);
                UpdateCardStatus(robots[i].RobotData.CurrentRobotStatus, i);
            }
        }

        private void CacheUIElements()
        {
            _rightPanel = _root.Q<VisualElement>("RightPanel");
            _leftPanelRobotCardListView = _root.Q<ScrollView>("RobotsListView");
            _autoModeButton = _rightPanel.Q<VisualElement>("ModeAutoBtn");
            _manualModeButton = _rightPanel.Q<VisualElement>("ModeManualBtn");
            _manualModeWarning = _rightPanel.Q<VisualElement>("ManualModeWarning");
            _pipelineHeader = _root.Q<VisualElement>("PipelineHeader");
            _switchVariantButton = _root.Q<VisualElement>("VariantSwitcherBtn");
            _variantNameLabel = _root.Q<Label>("VariantNameLabel");
            _rightPanelSliders = _rightPanel.Query<Slider>(className: "custom-slider").ToList();
        }

        private void OnRobotChanged(Robot robot)
        {
            _activeRobot = robot;
            UpdateRightPanel();
            UpdateModeButtons();
            UpdateSliders();
        }

        private void BindButtonClickCallbacks()
        {
            _switchVariantButton.RegisterCallback<ClickEvent>(SwitchRobotVariant);
            //_pipelineHeader.RegisterCallback<ClickEvent>(_ => RobotManager.Instance.SpawnNewRobot()); // TODO: Only for debug! Remove later.
            _autoModeButton.RegisterCallback<ClickEvent, OperationMode>(ChangeRobotMode, OperationMode.Auto);
            _manualModeButton.RegisterCallback<ClickEvent, OperationMode>(ChangeRobotMode, OperationMode.Manual);
        }

        private void SwitchRobotVariant(ClickEvent evt)
        {
            _activeRobot.VariantScript.CycleThroughVariants();
        }

        private void ChangeRobotMode(ClickEvent evt, OperationMode newMode)
        {
            _activeRobot.SetOperationMode(newMode);
            UpdateModeButtons();
            UpdateSliders();
        }

        private void UpdateRightPanel()
        {
            _rightPanel.dataSource = _activeRobot.RobotData;
        }

        private void UpdateModeButtons()
        {
            bool autoMode = _activeRobot.RobotData.OperationMode == OperationMode.Auto;
            _autoModeButton.EnableInClassList("active", autoMode);
            _manualModeButton.EnableInClassList("active", !autoMode);
            _switchVariantButton.SetEnabled(!autoMode && _activeRobot.VariantScript.GetVariantNumber() > 1);
            _manualModeWarning.style.display = autoMode ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void UpdateSliders()
        {
            bool autoMode = _activeRobot.RobotData.OperationMode == OperationMode.Auto;
            foreach (Slider slider in _rightPanelSliders)
            {
                slider.SetEnabled(!autoMode);
                slider.EnableInClassList("slider-locked", autoMode);
            }
        }
    
        private void UpdateCardStatus(RobotStatus robotStatus, int index)
        {
            switch (robotStatus)
            {
                case RobotStatus.STANDARD:
                    _robotDataCards[index].Q<VisualElement>("Step1").RemoveFromClassList("step-warning");
                    _robotDataCards[index].Q<VisualElement>("StatusDot").RemoveFromClassList("step-status-dot-warning");
                    _robotDataCards[index].Q<VisualElement>("HealthDataLabel").RemoveFromClassList("metric-warning");
                    _robotDataCards[index].Q<VisualElement>("BatteryDataLabel").RemoveFromClassList("metric-warning");
                
                    _robotDataCards[index].Q<VisualElement>("Step1").RemoveFromClassList("step-critical");
                    _robotDataCards[index].Q<VisualElement>("StatusDot").RemoveFromClassList("step-status-dot-critical");
                    _robotDataCards[index].Q<VisualElement>("HealthDataLabel").RemoveFromClassList("metric-critical");
                    _robotDataCards[index].Q<VisualElement>("BatteryDataLabel").RemoveFromClassList("metric-critical");
                    break;
                case RobotStatus.WARNING:
                    _robotDataCards[index].Q<VisualElement>("Step1").AddToClassList("step-warning");
                    _robotDataCards[index].Q<VisualElement>("StatusDot").AddToClassList("step-status-dot-warning");
                    _robotDataCards[index].Q<VisualElement>("HealthDataLabel").AddToClassList("metric-warning");
                    _robotDataCards[index].Q<VisualElement>("BatteryDataLabel").AddToClassList("metric-warning");
                
                    _robotDataCards[index].Q<VisualElement>("Step1").RemoveFromClassList("step-critical");
                    _robotDataCards[index].Q<VisualElement>("StatusDot").RemoveFromClassList("step-status-dot-critical");
                    _robotDataCards[index].Q<VisualElement>("HealthDataLabel").RemoveFromClassList("metric-critical");
                    _robotDataCards[index].Q<VisualElement>("BatteryDataLabel").RemoveFromClassList("metric-critical");
                    break;
                case RobotStatus.CRITICAL:
                    _robotDataCards[index].Q<VisualElement>("Step1").AddToClassList("step-critical");
                    _robotDataCards[index].Q<VisualElement>("StatusDot").AddToClassList("step-status-dot-critical");
                    _robotDataCards[index].Q<VisualElement>("HealthDataLabel").AddToClassList("metric-critical");
                    _robotDataCards[index].Q<VisualElement>("BatteryDataLabel").AddToClassList("metric-critical");
                    break;
                case RobotStatus.DEAD:
                    Debug.Log($"BOT {index} is dead");
                    _robotDataCards[index].Q<VisualElement>("Step1").AddToClassList("step-critical");
                    _robotDataCards[index].Q<VisualElement>("StatusDot").AddToClassList("step-status-dot-critical");
                    _robotDataCards[index].Q<VisualElement>("HealthDataLabel").AddToClassList("metric-critical");
                    _robotDataCards[index].Q<VisualElement>("BatteryDataLabel").AddToClassList("metric-critical");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(robotStatus), robotStatus, null);
            }
        }

        private void OnDisable()
        {
            RobotManager.Instance.ActiveRobotChanged -= OnRobotChanged;
            RobotManager.Instance.RobotListChanged -= OnRobotListChanged;
        }
    }
}