using System;
using System.Collections.Generic;
using ShipRobot.Navigation;
using ShipRobot.ObstacleAvoidance;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ShipRobot.Navigation.Editor
{
    public static class PlantNavigationSetupEditor
    {
        private const string RootName = "NavigationSystem";
        private const string TagFolder = "Assets/Navigation/Tags";
        private const string MaterialFolder = "Assets/Navigation/GeneratedMaterials";

        private readonly struct MarkerSetup
        {
            public readonly PlantNodeId nodeId;
            public readonly string anchorName;

            public MarkerSetup(PlantNodeId nodeId, string anchorName)
            {
                this.nodeId = nodeId;
                this.anchorName = anchorName;
            }
        }

        private static readonly MarkerSetup[] MarkerSetups =
        {
            new(PlantNodeId.UpperLeft, "upper_left"),
            new(PlantNodeId.UpperRight, "upper_right"),
            new(PlantNodeId.UpperMid, "upper_mid"),
            new(PlantNodeId.UnderLeft, "under_left"),
            new(PlantNodeId.UnderRight, "under_right"),
            new(PlantNodeId.UnderMid, "under_mid")
        };

        [MenuItem("Tools/Ship Robot/Setup AprilTag Navigation")]
        public static void SetupNavigation()
        {
            if (EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("Ship Robot Navigation", "Stop Play Mode before running setup.", "OK");
                return;
            }

            Dictionary<string, Transform> anchors = FindNamedAnchors();
            var missing = new List<string>();
            foreach (MarkerSetup setup in MarkerSetups)
            {
                if (!anchors.ContainsKey(setup.anchorName))
                    missing.Add(setup.anchorName);
            }

            if (missing.Count > 0)
            {
                EditorUtility.DisplayDialog(
                    "Ship Robot Navigation",
                    "Named marker anchors were not found: " + string.Join(", ", missing),
                    "OK");
                return;
            }

            EnsureFolder(MaterialFolder);
            GameObject root = GameObject.Find(RootName) ?? new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Create navigation system");
            RemoveLegacyGeneratedMarkers(root.transform);

            foreach (MarkerSetup setup in MarkerSetups)
                CreateOrUpdateMarker(anchors[setup.anchorName], setup.nodeId);

            PlantRouteGraph graph = GetOrAddComponent<PlantRouteGraph>(root);
            MissionRoutePlanner planner = GetOrAddComponent<MissionRoutePlanner>(root);
            graph.FindMarkersInScene();
            SimulatedMarkerObservationSource markerSource = AttachMarkerPreview();

            var plannerObject = new SerializedObject(planner);
            plannerObject.FindProperty("routeGraph").objectReferenceValue = graph;
            plannerObject.ApplyModifiedPropertiesWithoutUndo();

            NavigationCoordinator coordinator = GetOrAddComponent<NavigationCoordinator>(root);
            var coordinatorObject = new SerializedObject(coordinator);
            coordinatorObject.FindProperty("routeGraph").objectReferenceValue = graph;
            coordinatorObject.FindProperty("missionPlanner").objectReferenceValue = planner;
            coordinatorObject.FindProperty("markerSource").objectReferenceValue = markerSource;
            GameObject robot = GameObject.Find("jetbot");
            coordinatorObject.FindProperty("laneFollower").objectReferenceValue =
                robot != null ? robot.GetComponent<ShipRobot.LaneFollowing.LaneFollowerController>() : null;
            if (robot != null)
                AttachHumanGroundTruthSensor(robot);
            GameObject inspectionA1 = GameObject.Find("inspect_point_A1");
            GameObject inspectionA2 = GameObject.Find("inspect_point_A2");
            coordinatorObject.FindProperty("inspectionPointA1").objectReferenceValue =
                inspectionA1 != null ? inspectionA1.transform : null;
            coordinatorObject.FindProperty("inspectionPointA2").objectReferenceValue =
                inspectionA2 != null ? inspectionA2.transform : null;
            coordinatorObject.FindProperty("inspectionReachDistance").floatValue = 1.20f;
            ApplyRelaxedVisionSettings(coordinatorObject);
            ApplyAllPerimeterManeuvers(coordinatorObject);
            coordinatorObject.ApplyModifiedPropertiesWithoutUndo();

            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(root.scene);
            Debug.Log("AprilTag navigation setup complete: six markers, route graph, and mission planner created.", root);
        }

        [MenuItem("Tools/Ship Robot/Setup Human Ground-Truth Sensor")]
        public static void SetupHumanGroundTruthSensor()
        {
            GameObject robot = GameObject.Find("jetbot");
            if (robot == null)
            {
                Debug.LogError("jetbot was not found in the active scene.");
                return;
            }

            HumanGroundTruthSensor sensor = AttachHumanGroundTruthSensor(robot);
            Selection.activeGameObject = robot;
            EditorSceneManager.MarkSceneDirty(robot.scene);
            Debug.Log("Human ground-truth sensor setup complete. Save the scene and enter Play Mode.", sensor);
        }

        [MenuItem("Tools/Ship Robot/Setup Dual Front ToF Sensors")]
        public static void SetupDualFrontToFSensors()
        {
            GameObject robot = GameObject.Find("jetbot");
            if (robot == null)
            {
                Debug.LogError("jetbot was not found in the active scene.");
                return;
            }

            Transform cameraTransform = robot.transform.Find("front_camera");
            Vector3 centre = cameraTransform != null
                ? cameraTransform.localPosition
                : new Vector3(-1.55f, 0.35f, 1.04f);
            centre.y = Mathf.Max(centre.y, 0.35f);

            VirtualToFSensor left = CreateOrUpdateToFSensor(
                robot.transform, "ToF_front_left", centre + Vector3.left * 0.20f, -12f);
            VirtualToFSensor right = CreateOrUpdateToFSensor(
                robot.transform, "ToF_front_right", centre + Vector3.right * 0.20f, 12f);

            string[] legacyNames = { "ToF_;eft", "ToF_front", "ToF_right" };
            foreach (string legacyName in legacyNames)
            {
                Transform legacy = robot.transform.Find(legacyName);
                if (legacy != null)
                    legacy.gameObject.SetActive(false);
            }

            DualToFSensorRig rig = GetOrAddComponent<DualToFSensorRig>(robot);
            var serializedRig = new SerializedObject(rig);
            serializedRig.FindProperty("frontLeft").objectReferenceValue = left;
            serializedRig.FindProperty("frontRight").objectReferenceValue = right;
            serializedRig.FindProperty("sampleRateHz").floatValue = 20f;
            serializedRig.FindProperty("ttcSafetyDistance").floatValue = 0.20f;
            serializedRig.FindProperty("distanceSmoothing").floatValue = 0.35f;
            serializedRig.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(rig);

            SafetySupervisor safety = GetOrAddComponent<SafetySupervisor>(robot);
            var serializedSafety = new SerializedObject(safety);
            serializedSafety.FindProperty("tofRig").objectReferenceValue = rig;
            serializedSafety.FindProperty("laneFollower").objectReferenceValue =
                robot.GetComponent<ShipRobot.LaneFollowing.LaneFollowerController>();
            RgbPersonDetector rgbDetector = robot.GetComponent<RgbPersonDetector>();
            serializedSafety.FindProperty("personBearingProvider").objectReferenceValue =
                rgbDetector != null ? rgbDetector : robot.GetComponent<HumanGroundTruthSensor>();
            serializedSafety.FindProperty("earlyWarningSpeedScale").floatValue = 0.65f;
            serializedSafety.FindProperty("centreSectorHalfWidth").floatValue = 0.20f;
            serializedSafety.FindProperty("emergencyStopDistance").floatValue = 0.80f;
            serializedSafety.FindProperty("emergencyStopTtc").floatValue = 1.50f;
            serializedSafety.FindProperty("slowdownDistance").floatValue = 1.50f;
            serializedSafety.FindProperty("minimumSlowdownScale").floatValue = 0.25f;
            serializedSafety.FindProperty("releaseDistance").floatValue = 1.00f;
            serializedSafety.FindProperty("clearHoldSeconds").floatValue = 0.50f;
            serializedSafety.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(safety);

            Selection.activeGameObject = robot;
            EditorSceneManager.MarkSceneDirty(robot.scene);
            Debug.Log("Dual front ToF setup complete. Legacy three-sensor objects were disabled, not deleted.", rig);
        }

        [MenuItem("Tools/Ship Robot/Setup RGB Person Detector")]
        public static void SetupRgbPersonDetector()
        {
            GameObject robot = GameObject.Find("jetbot");
            if (robot == null)
            {
                Debug.LogError("jetbot was not found in the active scene.");
                return;
            }

            RgbPersonDetector detector = GetOrAddComponent<RgbPersonDetector>(robot);
            var serializedDetector = new SerializedObject(detector);
            Transform cameraTransform = robot.transform.Find("front_camera");
            serializedDetector.FindProperty("rgbCamera").objectReferenceValue =
                cameraTransform != null ? cameraTransform.GetComponent<Camera>() : null;
            serializedDetector.FindProperty("inferenceInterval").floatValue = 0.15f;
            serializedDetector.FindProperty("minimumPersonConfidence").floatValue = 0.20f;
            serializedDetector.ApplyModifiedPropertiesWithoutUndo();

            SafetySupervisor safety = robot.GetComponent<SafetySupervisor>();
            if (safety != null)
            {
                var serializedSafety = new SerializedObject(safety);
                serializedSafety.FindProperty("personBearingProvider").objectReferenceValue = detector;
                serializedSafety.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(safety);
            }

            EditorUtility.SetDirty(detector);
            Selection.activeGameObject = robot;
            EditorSceneManager.MarkSceneDirty(robot.scene);
            Debug.Log("RGB YOLOX person detector setup complete. SafetySupervisor now uses RGB bearing.", detector);
        }

        private static VirtualToFSensor CreateOrUpdateToFSensor(
            Transform parent, string objectName, Vector3 localPosition, float yaw)
        {
            Transform existing = parent.Find(objectName);
            GameObject sensorObject = existing != null ? existing.gameObject : new GameObject(objectName);
            if (existing == null)
            {
                Undo.RegisterCreatedObjectUndo(sensorObject, $"Create {objectName}");
                sensorObject.transform.SetParent(parent, false);
            }
            sensorObject.SetActive(true);
            sensorObject.transform.localPosition = localPosition;
            sensorObject.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);

            VirtualToFSensor sensor = GetOrAddComponent<VirtualToFSensor>(sensorObject);
            sensor.maxDistance = 2f;
            sensor.fieldOfView = 20f;
            sensor.rayCount = 7;
            sensor.addNoise = false;
            sensor.noiseStdDev = 0.01f;
            sensor.drawDebugRay = true;
            sensor.ignoreOwnHierarchy = true;
            EditorUtility.SetDirty(sensorObject);
            return sensor;
        }

        private static HumanGroundTruthSensor AttachHumanGroundTruthSensor(GameObject robot)
        {
            HumanGroundTruthSensor sensor = GetOrAddComponent<HumanGroundTruthSensor>(robot);
            var serializedSensor = new SerializedObject(sensor);
            Transform cameraTransform = robot.transform.Find("front_camera");
            serializedSensor.FindProperty("perceptionCamera").objectReferenceValue =
                cameraTransform != null ? cameraTransform.GetComponent<Camera>() : null;
            serializedSensor.FindProperty("surfaceSafetyMargin").floatValue = 0.10f;
            serializedSensor.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(sensor);
            return sensor;
        }

        private static void ApplyRelaxedVisionSettings(SerializedObject coordinator)
        {
            coordinator.FindProperty("markerDetectionDistance").floatValue = 4.00f;
            coordinator.FindProperty("junctionActionDistance").floatValue = 1.20f;
            coordinator.FindProperty("minimumTurnBeforePair").floatValue = 10f;
            coordinator.FindProperty("maximumSearchTurn").floatValue = 150f;
            coordinator.FindProperty("minimumPairConfidence").floatValue = 0.10f;
            coordinator.FindProperty("requiredPairFrames").intValue = 1;
            coordinator.FindProperty("alignedLateralTolerance").floatValue = 0.35f;
            coordinator.FindProperty("alignedHeadingTolerance").floatValue = 0.40f;
            coordinator.FindProperty("requiredAlignedFrames").intValue = 2;
            coordinator.FindProperty("minimumAlignTravel").floatValue = 0.05f;
            coordinator.FindProperty("maximumAlignTravel").floatValue = 1.50f;
            coordinator.FindProperty("pairLostFrameLimit").intValue = 12;
            coordinator.FindProperty("fallbackStraightCommand").floatValue = 0.10f;
            coordinator.FindProperty("laneLostFramesBeforeFallback").intValue = 12;
            coordinator.FindProperty("maximumFallbackDistance").floatValue = 8f;
            coordinator.FindProperty("maximumFallbackSeconds").floatValue = 30f;
            coordinator.FindProperty("minimumApproachDistance").floatValue = 0.10f;
            coordinator.FindProperty("maximumApproachDistance").floatValue = 1.50f;
            coordinator.FindProperty("requiredSideLossFrames").intValue = 30;
            coordinator.FindProperty("straightDirectionTolerance").floatValue = 25f;
            coordinator.FindProperty("straightJunctionCommand").floatValue = 0.14f;
            coordinator.FindProperty("minimumStraightTravel").floatValue = 0.20f;
            coordinator.FindProperty("maximumStraightTravel").floatValue = 3.0f;
            coordinator.FindProperty("requiredStraightLossFrames").intValue = 2;
            coordinator.FindProperty("requiredStraightReacquireFrames").intValue = 3;
        }

        private static void ApplyAllPerimeterManeuvers(SerializedObject coordinator)
        {
            PlantNodeId[,] transitions =
            {
                { PlantNodeId.UnderMid,   PlantNodeId.UpperMid,   PlantNodeId.UpperLeft },
                { PlantNodeId.UpperMid,   PlantNodeId.UpperLeft,  PlantNodeId.UnderLeft },
                { PlantNodeId.UpperLeft,  PlantNodeId.UnderLeft,  PlantNodeId.UnderMid },
                { PlantNodeId.UnderLeft,  PlantNodeId.UnderMid,   PlantNodeId.UnderRight },
                { PlantNodeId.UnderMid,   PlantNodeId.UnderRight, PlantNodeId.UpperRight },
                { PlantNodeId.UnderRight, PlantNodeId.UpperRight, PlantNodeId.UpperMid }
            };

            SerializedProperty overrides = coordinator.FindProperty("maneuverOverrides");
            overrides.arraySize = transitions.GetLength(0);
            for (int i = 0; i < transitions.GetLength(0); i++)
            {
                SerializedProperty item = overrides.GetArrayElementAtIndex(i);
                item.FindPropertyRelative("entryNode").intValue = (int)transitions[i, 0];
                item.FindPropertyRelative("junctionNode").intValue = (int)transitions[i, 1];
                item.FindPropertyRelative("exitNode").intValue = (int)transitions[i, 2];
                item.FindPropertyRelative("approachDistance").floatValue = 1.50f;
                item.FindPropertyRelative("approachCommand").floatValue = 0.16f;
                item.FindPropertyRelative("searchTurnCommand").floatValue = 0.20f;
                item.FindPropertyRelative("visualAlignMoveCommand").floatValue = 0.11f;
            }
        }

        private static SimulatedMarkerObservationSource AttachMarkerPreview()
        {
            GameObject cameraObject = GameObject.Find("front_camera");
            if (cameraObject == null || cameraObject.GetComponent<Camera>() == null)
            {
                Debug.LogWarning("front_camera was not found; marker preview overlay was not attached.");
                return null;
            }

            SimulatedMarkerObservationSource source = GetOrAddComponent<SimulatedMarkerObservationSource>(cameraObject);
            source.RefreshMarkerList();
            EditorUtility.SetDirty(cameraObject);
            return source;
        }

        private static Dictionary<string, Transform> FindNamedAnchors()
        {
            var result = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            foreach (Transform candidate in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (!candidate.gameObject.scene.IsValid() || EditorUtility.IsPersistent(candidate))
                    continue;
                result[candidate.name] = candidate;
            }
            return result;
        }

        private static void RemoveLegacyGeneratedMarkers(Transform root)
        {
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                Transform child = root.GetChild(i);
                if (child.name.StartsWith("Marker_", StringComparison.Ordinal) &&
                    child.GetComponent<NavigationMarker>() != null)
                {
                    Undo.DestroyObjectImmediate(child.gameObject);
                }
            }
        }

        private static void CreateOrUpdateMarker(Transform anchor, PlantNodeId nodeId)
        {
            NavigationMarker marker = GetOrAddComponent<NavigationMarker>(anchor.gameObject);
            var markerSerialized = new SerializedObject(marker);
            markerSerialized.FindProperty("nodeId").enumValueIndex = Array.IndexOf(
                (PlantNodeId[])Enum.GetValues(typeof(PlantNodeId)), nodeId);
            markerSerialized.FindProperty("physicalSizeMetres").floatValue = 0.30f;
            markerSerialized.ApplyModifiedPropertiesWithoutUndo();

            const string visualName = "AprilTagVisual";
            Transform existingVisual = anchor.Find(visualName);
            GameObject markerVisual;
            if (existingVisual == null)
            {
                markerVisual = GameObject.CreatePrimitive(PrimitiveType.Quad);
                markerVisual.name = visualName;
                Undo.RegisterCreatedObjectUndo(markerVisual, "Create AprilTag visual");
                markerVisual.transform.SetParent(anchor, true);
                Collider collider = markerVisual.GetComponent<Collider>();
                if (collider != null)
                    UnityEngine.Object.DestroyImmediate(collider);
            }
            else
            {
                markerVisual = existingVisual.gameObject;
            }

            Vector3 position = anchor.position;
            position.y = 0.075f;
            markerVisual.transform.position = position;
            markerVisual.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            // The PNG contains a 0.30 m tag plus a 0.05 m white quiet zone on each side.
            markerVisual.transform.localScale = Vector3.one * 0.40f;

            Renderer renderer = markerVisual.GetComponent<Renderer>();
            renderer.sharedMaterial = LoadOrCreateTagMaterial((int)nodeId);
        }

        private static Material LoadOrCreateTagMaterial(int id)
        {
            string texturePath = $"{TagFolder}/tag36h11_{id:000}.png";
            ConfigureTextureImporter(texturePath);
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            if (texture == null)
                throw new InvalidOperationException($"AprilTag texture is missing: {texturePath}");

            string materialPath = $"{MaterialFolder}/Tag36h11_{id:000}.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture");
                material = new Material(shader) { name = $"Tag36h11_{id:000}" };
                AssetDatabase.CreateAsset(material, materialPath);
            }

            if (material.HasProperty("_BaseMap"))
                material.SetTexture("_BaseMap", texture);
            if (material.HasProperty("_MainTex"))
                material.SetTexture("_MainTex", texture);
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", Color.white);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void ConfigureTextureImporter(string assetPath)
        {
            if (AssetImporter.GetAtPath(assetPath) is not TextureImporter importer)
                return;

            bool changed = importer.textureType != TextureImporterType.Default ||
                           importer.filterMode != FilterMode.Point ||
                           importer.textureCompression != TextureImporterCompression.Uncompressed ||
                           importer.mipmapEnabled;
            if (!changed)
                return;

            importer.textureType = TextureImporterType.Default;
            importer.filterMode = FilterMode.Point;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
        }

        private static T GetOrAddComponent<T>(GameObject gameObject) where T : Component
        {
            T component = gameObject.GetComponent<T>();
            return component != null ? component : Undo.AddComponent<T>(gameObject);
        }

        private static void EnsureFolder(string fullPath)
        {
            string[] parts = fullPath.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
