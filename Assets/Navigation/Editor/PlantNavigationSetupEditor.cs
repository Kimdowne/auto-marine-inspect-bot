using System;
using System.Collections.Generic;
using ShipRobot.Navigation;
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
            ApplyRelaxedVisionSettings(coordinatorObject);
            ApplyAllPerimeterManeuvers(coordinatorObject);
            coordinatorObject.ApplyModifiedPropertiesWithoutUndo();

            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(root.scene);
            Debug.Log("AprilTag navigation setup complete: six markers, route graph, and mission planner created.", root);
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
