using System;
using ShipRobot.ObstacleAvoidance;
using ShipRobot.LaneFollowing;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ShipRobot.Navigation.Editor
{
    public static class EquipmentDemoSetup
    {
        public const string DemoScene = "Assets/jetbot_env.unity";

        [MenuItem("Tools/Ship Robot/Demo/Apply RL Model To Current Jetbot Scene")]
        public static void CreateDemo()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play Mode first.");
            AssetDatabase.Refresh();
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (scene.path != DemoScene)
                throw new InvalidOperationException("Open Assets/jetbot_env.unity first. This command only updates the original scene.");
            var robot = GameObject.Find("jetbot");
            var mission = UnityEngine.Object.FindAnyObjectByType<NavigationCoordinator>();
            var agent = robot != null ? robot.GetComponent<HumanAvoidanceAgent>() : null;
            var model = AssetDatabase.LoadAssetAtPath<ModelAsset>(
                "Assets/Resources/ShipRobotVision/HumanAvoidanceDemo.onnx");
            if (agent == null || mission == null || model == null)
                throw new InvalidOperationException("Missing jetbot, mission or exported HumanAvoidanceDemo.onnx.");
            ValidateInference(model);

            var agentConfig = new SerializedObject(agent);
            agentConfig.FindProperty("trainingMode").boolValue = false;
            agentConfig.FindProperty("applyPolicyActions").boolValue = true;
            agentConfig.FindProperty("demoMission").objectReferenceValue = mission;
            agentConfig.ApplyModifiedPropertiesWithoutUndo();
            agent.MaxStep = 0;
            agent.enabled = true;

            var behavior = robot.GetComponent<BehaviorParameters>();
            behavior.BehaviorName = "HumanAvoidance";
            behavior.BrainParameters.VectorObservationSize = HumanAvoidanceAgent.ObservationCount;
            behavior.BrainParameters.NumStackedVectorObservations = 4;
            behavior.BrainParameters.ActionSpec = ActionSpec.MakeContinuous(2);
            behavior.Model = model;
            behavior.BehaviorType = BehaviorType.InferenceOnly;
            var behaviorConfig = new SerializedObject(behavior);
            behaviorConfig.FindProperty("m_DeterministicInference").boolValue = true;
            behaviorConfig.ApplyModifiedPropertiesWithoutUndo();
            behavior.enabled = true;
            var requester = robot.GetComponent<DecisionRequester>();
            if (requester != null) requester.enabled = false;
            var legacy = robot.GetComponent<JetBotAgent>();
            if (legacy != null) legacy.enabled = false;

            var missionConfig = new SerializedObject(mission);
            missionConfig.FindProperty("avoidanceAgent").objectReferenceValue = agent;
            missionConfig.FindProperty("autoStartEquipmentAAndBMission").boolValue = true;
            var routeGraph = UnityEngine.Object.FindAnyObjectByType<PlantRouteGraph>();
            if (routeGraph == null ||
                !routeGraph.TryGetMarker(PlantNodeId.UpperLeft, out NavigationMarker leftMarker) ||
                !routeGraph.TryGetMarker(PlantNodeId.UpperRight, out NavigationMarker rightMarker))
                throw new InvalidOperationException("Missing upper left/right route markers for Equipment B placement.");
            float aisleOffsetX = rightMarker.transform.position.x - leftMarker.transform.position.x;
            for (int i = 1; i <= 2; i++)
            {
                var point = GameObject.Find("inspect_point_A" + i);
                if (point == null) throw new InvalidOperationException("Missing inspect_point_A" + i);
                var inspection = point.GetComponent<InspectionPoint>() ?? point.AddComponent<InspectionPoint>();
                inspection.inspectionTime = 3f;
                missionConfig.FindProperty("inspectionPointA" + i).objectReferenceValue = point.transform;
                EditorUtility.SetDirty(inspection);

                var bPoint = GameObject.Find("inspect_point_B" + i);
                if (bPoint == null)
                {
                    bPoint = UnityEngine.Object.Instantiate(point, point.transform.parent);
                    bPoint.name = "inspect_point_B" + i;
                    Vector3 position = point.transform.position;
                    bPoint.transform.position = new Vector3(position.x + aisleOffsetX, position.y, position.z);
                }
                var bInspection = bPoint.GetComponent<InspectionPoint>() ?? bPoint.AddComponent<InspectionPoint>();
                bInspection.inspectionTime = 3f;
                missionConfig.FindProperty("inspectionPointB" + i).objectReferenceValue = bPoint.transform;
                EditorUtility.SetDirty(bInspection);
            }
            missionConfig.ApplyModifiedPropertiesWithoutUndo();
            mission.enabled = true;
            robot.GetComponent<LaneFollowerController>().enabled = true;

            var firstPedestrian = GameObject.Find("Handyman_ver_1");
            if (firstPedestrian == null)
                throw new InvalidOperationException("Handyman_ver_1 is required for the demo.");
            var extraPedestrian = GameObject.Find("Handyman_ver_2");
            var sequence = mission.GetComponent<DemoPedestrianSequence>() ??
                           mission.gameObject.AddComponent<DemoPedestrianSequence>();
            var sequenceConfig = new SerializedObject(sequence);
            sequenceConfig.FindProperty("mission").objectReferenceValue = mission;
            sequenceConfig.FindProperty("routeGraph").objectReferenceValue =
                UnityEngine.Object.FindAnyObjectByType<PlantRouteGraph>();
            sequenceConfig.FindProperty("avoidanceAgent").objectReferenceValue = agent;
            sequenceConfig.FindProperty("robot").objectReferenceValue = robot.transform;
            sequenceConfig.FindProperty("person").objectReferenceValue =
                firstPedestrian.GetComponent<TrainingPedestrianMover>() ??
                firstPedestrian.AddComponent<TrainingPedestrianMover>();
            sequenceConfig.FindProperty("extraScenePerson").objectReferenceValue = extraPedestrian;
            sequenceConfig.ApplyModifiedPropertiesWithoutUndo();
            sequence.enabled = true;

            var safety = robot.GetComponent<SafetySupervisor>();
            var safetyConfig = new SerializedObject(safety);
            safetyConfig.FindProperty("enforceControl").boolValue = true;
            safetyConfig.FindProperty("emergencyStopDistance").floatValue = 0.35f;
            safetyConfig.FindProperty("emergencyStopTtc").floatValue = 0.75f;
            safetyConfig.ApplyModifiedPropertiesWithoutUndo();
            safety.enabled = true;
            foreach (var mover in UnityEngine.Object.FindObjectsByType<TrainingPedestrianMover>(FindObjectsSortMode.None))
            {
                mover.enabled = false;
                var patrol = mover.GetComponent<RandomPatrol>();
                if (patrol != null)
                {
                    patrol.deactivateAfterOneCircuit = true;
                    patrol.enabled = true;
                    EditorUtility.SetDirty(patrol);
                }
            }
            EditorUtility.SetDirty(agent);
            EditorUtility.SetDirty(behavior);
            EditorUtility.SetDirty(mission);
            EditorUtility.SetDirty(sequence);
            EditorUtility.SetDirty(safety);
            EditorSceneManager.SaveScene(scene, DemoScene);
            AssetDatabase.SaveAssets();
            Debug.Log("Equipment demo ready: PPO + ADAS, A1/A2/B2/B1 inspection 3 seconds, " +
                "one pedestrian reused on randomly selected straight legs. The A+B mission starts automatically.");
        }

        private static void ValidateInference(ModelAsset asset)
        {
            var model = ModelLoader.Load(asset);
            using var worker = new Worker(model, BackendType.CPU);
            using var input = new Tensor<float>(new TensorShape(1, 80), new float[80]);
            worker.Schedule(input);
            using var result = (worker.PeekOutput("deterministic_continuous_actions") as Tensor<float>).ReadbackAndClone();
            if (result.shape.length != 2 || float.IsNaN(result[0]) || float.IsNaN(result[1]) ||
                float.IsInfinity(result[0]) || float.IsInfinity(result[1]))
                throw new InvalidOperationException("Invalid demo policy outputs.");
            Debug.Log($"Demo inference validated: 80 inputs -> move {result[0]:F4}, turn {result[1]:F4}.");
        }
    }
}
