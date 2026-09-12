using UnityEngine;

namespace ShipRobot.Navigation
{
    /// <summary>
    /// Scene-based visibility source used to validate marker placement and UI before a pixel
    /// AprilTag decoder is connected. It never claims to be a real image decode.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class SimulatedMarkerObservationSource : MonoBehaviour, IMarkerObservationSource
    {
        [SerializeField] private Camera markerCamera;
        [SerializeField, Min(0.1f)] private float maximumDetectionDistance = 8f;
        [SerializeField, Range(0f, 0.45f)] private float viewportMargin = 0.03f;
        [SerializeField, Range(0f, 1f)] private float minimumFacingDot = 0.10f;
        [SerializeField, Min(0.02f)] private float refreshInterval = 0.05f;
        [SerializeField] private bool showOverlay = true;
        [SerializeField] private bool drawBoxInsideCameraPreview = true;
        [SerializeField] private Rect cameraPreviewGuiRect = new Rect(10f, 10f, 320f, 180f);

        private NavigationMarker[] markers;
        private MarkerObservation latestObservation;
        private NavigationMarker latestMarker;
        private Rect latestGuiRect;
        private float nextRefreshTime;
        private bool hasObservation;
        private GUIStyle detectedStyle;
        private GUIStyle missingStyle;

        private void Awake()
        {
            if (markerCamera == null)
                markerCamera = GetComponent<Camera>();
            RefreshMarkerList();
        }

        private void Update()
        {
            if (Time.time < nextRefreshTime)
                return;
            nextRefreshTime = Time.time + refreshInterval;
            DetectVisibleMarker();
        }

        [ContextMenu("Refresh Marker List")]
        public void RefreshMarkerList()
        {
            markers = FindObjectsByType<NavigationMarker>(FindObjectsSortMode.None);
        }

        public bool TryGetLatestObservation(out MarkerObservation observation)
        {
            observation = latestObservation;
            return hasObservation;
        }

        private void DetectVisibleMarker()
        {
            hasObservation = false;
            latestMarker = null;
            if (markerCamera == null)
                return;
            if (markers == null || markers.Length == 0)
                RefreshMarkerList();

            float bestScore = float.NegativeInfinity;
            foreach (NavigationMarker marker in markers)
            {
                if (marker == null || !marker.isActiveAndEnabled)
                    continue;

                Transform visual = marker.transform.Find("AprilTagVisual");
                Vector3 worldCentre = visual != null ? visual.position : marker.transform.position;
                Vector3 viewport = markerCamera.WorldToViewportPoint(worldCentre);
                if (viewport.z <= 0f || viewport.z > maximumDetectionDistance ||
                    viewport.x < viewportMargin || viewport.x > 1f - viewportMargin ||
                    viewport.y < viewportMargin || viewport.y > 1f - viewportMargin)
                    continue;

                if (visual != null)
                {
                    Vector3 toCamera = (markerCamera.transform.position - worldCentre).normalized;
                    // Unity Quad front-face orientation can be +Z or -Z depending on material/culling.
                    if (Mathf.Abs(Vector3.Dot(visual.forward, toCamera)) < minimumFacingDot)
                        continue;
                }

                float centreScore = 1f - Vector2.Distance(new Vector2(viewport.x, viewport.y), new Vector2(0.5f, 0.5f));
                float distanceScore = 1f - viewport.z / maximumDetectionDistance;
                float score = centreScore * 0.6f + distanceScore * 0.4f;
                if (score <= bestScore)
                    continue;

                bestScore = score;
                latestMarker = marker;
                latestObservation = new MarkerObservation
                {
                    nodeId = marker.NodeId,
                    cameraRelativePosition = markerCamera.transform.InverseTransformPoint(worldCentre),
                    cameraRelativeRotation = Quaternion.Inverse(markerCamera.transform.rotation) * marker.transform.rotation,
                    confidence = Mathf.Clamp01(score),
                    timestamp = Time.timeAsDouble
                };
                latestGuiRect = CalculateGuiRect(viewport, marker.PhysicalSizeMetres, viewport.z);
                hasObservation = true;
            }
        }

        private Rect CalculateGuiRect(Vector3 viewport, float physicalSize, float distance)
        {
            if (drawBoxInsideCameraPreview)
            {
                float previewFocalPixels = cameraPreviewGuiRect.height /
                                           (2f * Mathf.Tan(markerCamera.fieldOfView * 0.5f * Mathf.Deg2Rad));
                float previewSize = Mathf.Clamp(
                    physicalSize / Mathf.Max(distance, 0.01f) * previewFocalPixels,
                    12f, cameraPreviewGuiRect.height * 0.75f);
                float previewScreenX = cameraPreviewGuiRect.x + viewport.x * cameraPreviewGuiRect.width;
                float previewScreenY = cameraPreviewGuiRect.y + (1f - viewport.y) * cameraPreviewGuiRect.height;
                Rect previewBox = new Rect(
                    previewScreenX - previewSize * 0.5f,
                    previewScreenY - previewSize * 0.5f,
                    previewSize, previewSize);
                previewBox.x = Mathf.Clamp(
                    previewBox.x, cameraPreviewGuiRect.x, cameraPreviewGuiRect.xMax - previewBox.width);
                previewBox.y = Mathf.Clamp(
                    previewBox.y, cameraPreviewGuiRect.y, cameraPreviewGuiRect.yMax - previewBox.height);
                return previewBox;
            }

            Rect pixelRect = markerCamera.pixelRect;
            float focalPixels = pixelRect.height / (2f * Mathf.Tan(markerCamera.fieldOfView * 0.5f * Mathf.Deg2Rad));
            float size = Mathf.Clamp(physicalSize / Mathf.Max(distance, 0.01f) * focalPixels, 24f, pixelRect.height * 0.75f);
            float screenX = pixelRect.x + viewport.x * pixelRect.width;
            float screenY = Screen.height - (pixelRect.y + viewport.y * pixelRect.height);
            return new Rect(screenX - size * 0.5f, screenY - size * 0.5f, size, size);
        }

        private void OnGUI()
        {
            if (!showOverlay || markerCamera == null)
                return;
            GUI.depth = -100;
            EnsureStyles();

            Rect cameraRect = drawBoxInsideCameraPreview ? cameraPreviewGuiRect : markerCamera.pixelRect;
            float guiTop = drawBoxInsideCameraPreview ? cameraRect.y : Screen.height - cameraRect.yMax;
            var statusRect = new Rect(cameraRect.x + 8f, guiTop + 8f, Mathf.Max(260f, cameraRect.width - 16f), 48f);

            if (!hasObservation || latestMarker == null)
            {
                GUI.Label(statusRect, "SIM MARKER: none visible", missingStyle);
                return;
            }

            DrawBorder(latestGuiRect, Color.green, 3f);
            float distance = latestObservation.cameraRelativePosition.magnitude;
            GUI.Label(statusRect,
                $"SIM MARKER: ID {(int)latestObservation.nodeId}  {latestObservation.nodeId}\n" +
                $"distance {distance:F2} m   confidence {latestObservation.confidence:F2}",
                detectedStyle);
        }

        private void EnsureStyles()
        {
            if (detectedStyle != null)
                return;
            detectedStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.green }
            };
            missingStyle = new GUIStyle(detectedStyle)
            {
                normal = { textColor = new Color(1f, 0.75f, 0.15f) }
            };
        }

        private static void DrawBorder(Rect rect, Color colour, float thickness)
        {
            Color previous = GUI.color;
            GUI.color = colour;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), Texture2D.whiteTexture);
            GUI.color = previous;
        }
    }
}
