using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UIElements;

namespace Unity.Templates.IndustryFundamentals.Editor
{
    [CustomEditor(typeof(Robot))]
    public class RobotEditor : UnityEditor.Editor
    {
        private VisualElement _robotDataWarning;
        private readonly string _warningMessage = $"Reference a {nameof(RobotDataSO)} ScriptableObject in order for the Robot to work properly.";
        private RobotDataSO _robotDataOriginalValue;
        
        public override VisualElement CreateInspectorGUI()
        {
            VisualElement inspector = new();
            
            inspector.Add(new PropertyField(serializedObject.FindProperty("m_Script")){ enabledSelf = false });

            SerializedProperty serializedProperty = serializedObject.FindProperty("_data");
            _robotDataOriginalValue = (RobotDataSO)serializedProperty.objectReferenceValue;
            
            PropertyField dataPropertyField = new(serializedProperty);
            dataPropertyField.RegisterValueChangeCallback(OnSOChanged);
            inspector.Add(dataPropertyField);
            
            _robotDataWarning = new VisualElement { name = "RobotDataWarning" };
            inspector.Add(_robotDataWarning);
            
            return inspector;
        }

        private void OnSOChanged(SerializedPropertyChangeEvent evt)
        {
            RobotDataSO changedPropertyObjectReferenceValue = (RobotDataSO)evt.changedProperty.objectReferenceValue;
            if (_robotDataOriginalValue == changedPropertyObjectReferenceValue) return;
            
            _robotDataWarning.Clear();
            if (changedPropertyObjectReferenceValue == null)
            {
                _robotDataWarning.Add(new HelpBox(_warningMessage, HelpBoxMessageType.Error));
            }
            else AddMissingComponents();
        }

        private void AddMissingComponents()
        {
            Robot robotComponent = (Robot)serializedObject.targetObject;
            GameObject gameObject = robotComponent.gameObject;
            PrefabStage currentPrefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            
            AddMissingComponents(gameObject);
            
            // Track the correct GO to add the components to
            if (currentPrefabStage == null
                && PrefabUtility.GetPrefabAssetType(gameObject) is PrefabAssetType.Regular or PrefabAssetType.Variant)
            {
                // TODO: The apply is not working for now
                
                List<AddedComponent> addedComponents = PrefabUtility.GetAddedComponents(gameObject);
                foreach (AddedComponent addedComponent in addedComponents)
                {
                    Debug.Log($"Applying {addedComponent.instanceComponent.name}");
                    addedComponent.Apply(InteractionMode.AutomatedAction);
                }
            }
            // else -> We're in Prefab Mode
        }
        
        private void AddMissingComponents(GameObject targetGameObject)
        {
            if (targetGameObject.GetComponent<NavMeshAgent>() == null)
            {
                NavMeshAgent navMeshAgent = targetGameObject.AddComponent<NavMeshAgent>();
                navMeshAgent.baseOffset = 0f;
            
                navMeshAgent.speed = 1;
                navMeshAgent.angularSpeed = 70;
                navMeshAgent.acceleration = 8;
                navMeshAgent.autoBraking = true;

                navMeshAgent.radius = 0.7f;
                navMeshAgent.height = 0.6f;
                navMeshAgent.obstacleAvoidanceType = ObstacleAvoidanceType.GoodQualityObstacleAvoidance;
            }

            if (targetGameObject.GetComponent<Rigidbody>() == null)
            {
                Rigidbody rb = targetGameObject.AddComponent<Rigidbody>();
                rb.constraints = RigidbodyConstraints.FreezeAll;
            } 
            if (targetGameObject.GetComponent<BoxCollider>() == null) targetGameObject.AddComponent<BoxCollider>();

            if (targetGameObject.GetComponent<RobotDataSimulator>() == null) targetGameObject.AddComponent<RobotDataSimulator>();
        
            if (targetGameObject.GetComponent<RobotVariant>() == null) targetGameObject.AddComponent<RobotVariant>();
        }
    }
}