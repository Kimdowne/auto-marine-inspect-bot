using System;
using System.Collections.Generic;
using SentisModels;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.UI;

namespace ShipRobot.ObstacleAvoidance
{
    [DisallowMultipleComponent]
    public sealed class RgbPersonDetector : MonoBehaviour, IPersonBearingSource
    {
        [Header("Input")]
        [SerializeField] private Camera rgbCamera;
        [SerializeField, Min(0.05f)] private float inferenceInterval = 0.15f;
        [SerializeField, Range(0f, 1f)] private float minimumPersonConfidence = 0.20f;
        [SerializeField, Min(0.1f)] private float maximumObservationAge = 0.50f;
        [SerializeField] private BackendType backend = BackendType.GPUCompute;

        [Header("Person Shape Filter")]
        [Tooltip("Optional heuristic for real-camera tuning. Keep disabled for synthetic people.")]
        [SerializeField] private bool enablePersonShapeFilter = false;
        [SerializeField, Range(0.5f, 3f)] private float minimumPersonAspectRatio = 1.15f;
        [SerializeField, Range(0.1f, 1f)] private float maximumPersonBoxWidth = 0.55f;
        [SerializeField, Range(0.1f, 1f)] private float maximumPersonBoxArea = 0.50f;
        [Tooltip("Always reject a near-full-frame box; it is an invalid detector output, not a usable person location.")]
        [SerializeField, Range(0.8f, 1f)] private float invalidFullFrameThreshold = 0.95f;

        [Header("Debug")]
        [SerializeField] private bool showOverlay = true;
        [SerializeField] private RawImage cameraPreview;

        public bool IsReady => detector != null && detector.IsReady;
        public bool HasPerson { get; private set; }
        public PersonBearingObservation LatestObservation { get; private set; }

        private const int CaptureSize = 640;
        private IPersonBoxDetector detector;
        private string detectorName = "LOADING";
        private RenderTexture captureTexture;
        private Rect latestNormalizedBox;
        private float nextInferenceTime;
        private bool inferenceRunning;
        private string status = "Loading YOLOX";
        private int inferenceCount;
        private int latestDetectionCount;
        private int latestRawPersonCount;
        private int latestPersonCandidateCount;
        private float latestInferenceMilliseconds;
        private readonly bool[] recentDetectionHistory = new bool[3];
        private int detectionHistoryIndex;
        private int detectionHistoryCount;
        private int recentDetectionHits;
        private GUIStyle titleStyle;
        private GUIStyle valueStyle;

        private void Awake()
        {
            if (rgbCamera == null)
            {
                Transform cameraTransform = transform.Find("front_camera");
                rgbCamera = cameraTransform != null ? cameraTransform.GetComponent<Camera>() : null;
            }

            FindCameraPreview();

            captureTexture = new RenderTexture(CaptureSize, CaptureSize, 24, RenderTextureFormat.ARGB32)
            {
                name = "RGB Person Detector Input"
            };
            captureTexture.Create();
            var yolo = new OfficialYoloxPersonDetector(backend, minimumPersonConfidence);
            if (yolo.IsReady)
            {
                detector = yolo;
                detectorName = "YOLOX-S 640";
            }
            else
            {
                yolo.Dispose();
                detector = new UnityBlazePoseDetector(backend, minimumPersonConfidence);
                detectorName = "BLAZEPOSE FALLBACK";
            }
            status = detector.IsReady ? "Ready" : "Person model load failed";
        }

        private void Update()
        {
            if (!inferenceRunning && Time.time >= nextInferenceTime)
            {
                nextInferenceTime = Time.time + inferenceInterval;
                RunInferenceAsync();
            }

            if (HasPerson && Time.timeAsDouble - LatestObservation.timestamp > maximumObservationAge)
                HasPerson = false;
        }

        public bool TryGetPersonBearing(out PersonBearingObservation observation)
        {
            observation = LatestObservation;
            return HasPerson && Time.timeAsDouble - observation.timestamp <= maximumObservationAge;
        }

        private async void RunInferenceAsync()
        {
            if (rgbCamera == null || detector == null || !detector.IsReady)
                return;
            inferenceRunning = true;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // Use the exact RenderTexture currently shown by JetbotCameraView. Temporarily
                // replacing targetTexture and calling Camera.Render() can leave a stale image on
                // URP cameras, even though the normal camera preview continues to update.
                RenderTexture displayedFrame = rgbCamera.targetTexture;
                if (displayedFrame != null)
                {
                    Graphics.Blit(displayedFrame, captureTexture);
                }
                else
                {
                    RenderTexture previousTarget = rgbCamera.targetTexture;
                    rgbCamera.targetTexture = captureTexture;
                    rgbCamera.Render();
                    rgbCamera.targetTexture = previousTarget;
                }

                List<PersonBoxDetection> detections = await detector.DetectAsync(captureTexture);
                inferenceCount++;
                latestDetectionCount = detections != null ? detections.Count : 0;
                SelectBestPerson(detections);
            }
            catch (Exception exception)
            {
                status = exception.GetType().Name;
                Debug.LogException(exception, this);
                HasPerson = false;
            }
            finally
            {
                stopwatch.Stop();
                latestInferenceMilliseconds = (float)stopwatch.Elapsed.TotalMilliseconds;
                inferenceRunning = false;
            }
        }

        private void SelectBestPerson(List<PersonBoxDetection> detections)
        {
            HasPerson = false;
            latestRawPersonCount = 0;
            latestPersonCandidateCount = 0;
            float bestConfidence = minimumPersonConfidence;
            float modelInputSize = Mathf.Max(1f, detector.InputSize);
            PersonBoxDetection best = default;
            for (int i = 0; i < detections.Count; i++)
            {
                PersonBoxDetection detection = detections[i];
                if (detection.Confidence < minimumPersonConfidence)
                    continue;
                latestRawPersonCount++;

                Rect candidateBox = detection.BoxXyxy;
                float normalizedWidth = candidateBox.width / modelInputSize;
                float normalizedHeight = candidateBox.height / modelInputSize;
                float aspectRatio = candidateBox.height / Mathf.Max(1f, candidateBox.width);
                float normalizedArea = normalizedWidth * normalizedHeight;
                if (normalizedWidth >= invalidFullFrameThreshold &&
                    normalizedHeight >= invalidFullFrameThreshold)
                    continue;
                if (enablePersonShapeFilter &&
                    (aspectRatio < minimumPersonAspectRatio ||
                     normalizedWidth > maximumPersonBoxWidth ||
                     normalizedArea > maximumPersonBoxArea))
                    continue;

                latestPersonCandidateCount++;
                if (detection.Confidence < bestConfidence)
                    continue;
                bestConfidence = detection.Confidence;
                best = detection;
                HasPerson = true;
            }

            if (!HasPerson)
            {
                RegisterDetection(false);
                status = recentDetectionHits > 0 ? $"Confirming person {recentDetectionHits}/2" : "No person";
                return;
            }

            Rect box = best.BoxXyxy;
            latestNormalizedBox = new Rect(
                box.x / modelInputSize,
                box.y / modelInputSize,
                box.width / modelInputSize,
                box.height / modelInputSize);
            LatestObservation = new PersonBearingObservation
            {
                normalizedHorizontalPosition = Mathf.Clamp(latestNormalizedBox.center.x * 2f - 1f, -1f, 1f),
                confidence = best.Confidence,
                normalizedBoxWidth = latestNormalizedBox.width,
                normalizedBoxHeight = latestNormalizedBox.height,
                timestamp = Time.timeAsDouble
            };
            RegisterDetection(true);
            status = HasPerson ? "Person detected" : $"Confirming person {recentDetectionHits}/2";
        }

        private void RegisterDetection(bool detected)
        {
            if (detectionHistoryCount == recentDetectionHistory.Length)
            {
                if (recentDetectionHistory[detectionHistoryIndex])
                    recentDetectionHits--;
            }
            else
            {
                detectionHistoryCount++;
            }

            recentDetectionHistory[detectionHistoryIndex] = detected;
            if (detected)
                recentDetectionHits++;
            detectionHistoryIndex = (detectionHistoryIndex + 1) % recentDetectionHistory.Length;
            HasPerson = recentDetectionHits >= 2;
        }

        private void OnGUI()
        {
            if (!showOverlay)
                return;
            GUI.depth = -110;
            EnsureStyles();
            Rect panel = new Rect(Screen.width - 375f, 10f, 365f, 68f);
            GUI.Box(panel, GUIContent.none);
            GUI.Label(new Rect(panel.x + 10f, panel.y + 5f, 345f, 20f), $"RGB {detectorName} PERSON", titleStyle);
            string detail = HasPerson
                ? $"conf {LatestObservation.confidence:F3}  x {LatestObservation.normalizedHorizontalPosition:F3}  " +
                  $"box {latestNormalizedBox.width:F2}x{latestNormalizedBox.height:F2}"
                : status;
            GUI.Label(new Rect(panel.x + 10f, panel.y + 27f, 345f, 18f), detail, valueStyle);
            GUI.Label(new Rect(panel.x + 10f, panel.y + 44f, 345f, 18f),
                $"#{inferenceCount} all {latestDetectionCount}  raw-person {latestRawPersonCount}  valid {latestPersonCandidateCount}  {latestInferenceMilliseconds:F0}ms",
                valueStyle);

            if (!HasPerson)
                return;
            if (!TryGetCameraPreviewGuiRect(out Rect cameraPreviewGuiRect))
                return;
            Rect box = new Rect(
                cameraPreviewGuiRect.x + latestNormalizedBox.x * cameraPreviewGuiRect.width,
                cameraPreviewGuiRect.y + latestNormalizedBox.y * cameraPreviewGuiRect.height,
                latestNormalizedBox.width * cameraPreviewGuiRect.width,
                latestNormalizedBox.height * cameraPreviewGuiRect.height);
            DrawBorder(box, Color.magenta, 3f);
        }

        private void FindCameraPreview()
        {
            if (cameraPreview != null || rgbCamera == null || rgbCamera.targetTexture == null)
                return;

            RawImage[] images = FindObjectsByType<RawImage>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < images.Length; i++)
            {
                if (images[i].texture == rgbCamera.targetTexture)
                {
                    cameraPreview = images[i];
                    return;
                }
            }
        }

        private bool TryGetCameraPreviewGuiRect(out Rect guiRect)
        {
            guiRect = default;
            if (cameraPreview == null)
            {
                FindCameraPreview();
                if (cameraPreview == null)
                    return false;
            }

            RectTransform rectTransform = cameraPreview.rectTransform;
            Canvas canvas = cameraPreview.canvas;
            Camera canvasCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera
                : null;
            Vector3[] corners = new Vector3[4];
            rectTransform.GetWorldCorners(corners);
            Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(canvasCamera, corners[0]);
            Vector2 topRight = RectTransformUtility.WorldToScreenPoint(canvasCamera, corners[2]);
            guiRect = new Rect(
                bottomLeft.x,
                Screen.height - topRight.y,
                topRight.x - bottomLeft.x,
                topRight.y - bottomLeft.y);
            return guiRect.width > 1f && guiRect.height > 1f;
        }

        private void OnDestroy()
        {
            detector?.Dispose();
            if (captureTexture != null)
            {
                captureTexture.Release();
                Destroy(captureTexture);
            }
        }

        private void EnsureStyles()
        {
            if (titleStyle != null)
                return;
            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.magenta }
            };
            valueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal = { textColor = Color.white }
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
