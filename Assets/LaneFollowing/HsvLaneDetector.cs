using System;
using UnityEngine;

namespace ShipRobot.LaneFollowing
{
    /// <summary>
    /// Reads a forward-facing RGB camera and estimates the lane centre using HSV colour thresholding.
    /// The implementation intentionally runs at a small resolution so it can later be ported to JetBot.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HsvLaneDetector : MonoBehaviour
    {
        public enum MarkingLayout
        {
            TwoBoundaries,
            SingleCentreLine
        }

        [Serializable]
        public struct Detection
        {
            [Range(-1f, 1f)] public float lateralError;
            [Range(-1f, 1f)] public float headingError;
            [Range(0f, 1f)] public float confidence;
            public bool hasBoundaryPair;
            [Range(0f, 1f)] public float boundaryPairConfidence;
            public bool leftBoundaryVisible;
            public bool rightBoundaryVisible;
            [Range(0f, 1f)] public float leftBoundaryConfidence;
            [Range(0f, 1f)] public float rightBoundaryConfidence;
            public double timestamp;

            public bool IsUsable(float minimumConfidence, float maximumAgeSeconds)
            {
                return confidence >= minimumConfidence &&
                       Time.timeAsDouble - timestamp <= maximumAgeSeconds;
            }

            public bool IsBoundaryPairUsable(float minimumConfidence, float maximumAgeSeconds)
            {
                float effectiveConfidence = Mathf.Max(boundaryPairConfidence, confidence);
                return hasBoundaryPair && effectiveConfidence >= minimumConfidence &&
                       Time.timeAsDouble - timestamp <= maximumAgeSeconds;
            }
        }

        [Header("Input")]
        [SerializeField] private Camera rgbCamera;
        [SerializeField, Min(32)] private int processingWidth = 160;
        [SerializeField, Min(24)] private int processingHeight = 90;
        [SerializeField, Min(0.01f)] private float detectionInterval = 0.05f;

        [Header("Lane marking")]
        [SerializeField] private MarkingLayout markingLayout = MarkingLayout.TwoBoundaries;
        [Tooltip("OpenCV hue 0..179 is converted to Unity's normalized hue 0..1.")]
        [SerializeField, Range(0f, 1f)] private float minimumHue = 0.10f;
        [SerializeField, Range(0f, 1f)] private float maximumHue = 0.20f;
        [SerializeField, Range(0f, 1f)] private float minimumSaturation = 0.45f;
        [SerializeField, Range(0f, 1f)] private float minimumValue = 0.35f;

        [Header("Region of interest")]
        [Tooltip("Normalized image height. 0 is the bottom (near the robot), 1 is the top.")]
        [SerializeField, Range(0f, 1f)] private float roiBottom = 0.05f;
        [SerializeField, Range(0f, 1f)] private float roiTop = 0.62f;
        [SerializeField, Range(1, 20)] private int rowsPerBand = 7;
        [SerializeField, Range(1, 20)] private int minimumRunWidth = 2;
        [SerializeField, Range(0f, 1f)] private float minimumBoundarySeparation = 0.20f;

        [Header("Filtering")]
        [SerializeField, Range(0f, 1f)] private float smoothing = 0.35f;
        [SerializeField, Range(0f, 1f)] private float minimumPublishedConfidence = 0.05f;
        [SerializeField] private bool drawDebugOverlay = true;

        public Detection LatestDetection { get; private set; }
        public Texture DebugTexture => debugTexture;

        private RenderTexture renderTexture;
        private Texture2D readbackTexture;
        private Texture2D debugTexture;
        private Color32[] pixels;
        private Color32[] overlayPixels;
        private bool[] mask;
        private float nextDetectionTime;
        private bool hasFilteredDetection;

        private void Awake()
        {
            if (rgbCamera == null)
                rgbCamera = GetComponent<Camera>();

            AllocateBuffers();
        }

        private void OnValidate()
        {
            processingWidth = Mathf.Max(32, processingWidth);
            processingHeight = Mathf.Max(24, processingHeight);
            roiTop = Mathf.Max(roiBottom + 0.05f, roiTop);
        }

        private void Update()
        {
            if (Time.time < nextDetectionTime)
                return;

            nextDetectionTime = Time.time + detectionInterval;
            ProcessFrame();
        }

        public bool ProcessFrame()
        {
            if (rgbCamera == null)
                return false;

            if (renderTexture == null || renderTexture.width != processingWidth || renderTexture.height != processingHeight)
                AllocateBuffers();

            RenderTexture previousActive = RenderTexture.active;
            RenderTexture previousTarget = rgbCamera.targetTexture;
            try
            {
                // Preserve the original ADAS input: render at the detector's own
                // resolution instead of resizing the camera preview.
                rgbCamera.targetTexture = renderTexture;
                rgbCamera.Render();
                RenderTexture.active = renderTexture;
                readbackTexture.ReadPixels(new Rect(0, 0, processingWidth, processingHeight), 0, 0, false);
                readbackTexture.Apply(false, false);
            }
            finally
            {
                rgbCamera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
            }

            pixels = readbackTexture.GetPixels32();
            BuildMask();
            MeasureBoundarySideVisibility(
                out bool leftVisible, out bool rightVisible,
                out float leftSideConfidence, out float rightSideConfidence);

            int nearY = Mathf.RoundToInt(Mathf.Lerp(roiBottom, roiTop, 0.25f) * (processingHeight - 1));
            int farY = Mathf.RoundToInt(Mathf.Lerp(roiBottom, roiTop, 0.78f) * (processingHeight - 1));

            bool nearFound = TryFindBandCentre(nearY, out float nearCentre, out float nearConfidence);
            bool farFound = TryFindBandCentre(farY, out float farCentre, out float farConfidence);
            // Relaxed reacquisition: either observation band may establish a left/right pair.
            // When both are present, heading is still estimated from both band centres.
            bool boundaryPair = nearFound || farFound;
            float pairConfidence = boundaryPair ? Mathf.Max(nearConfidence, farConfidence) : 0f;

            if (!nearFound && !farFound)
            {
                PublishInvalidDetection(leftVisible, rightVisible, leftSideConfidence, rightSideConfidence);
                UpdateDebugTexture(-1f, -1f, nearY, farY);
                return false;
            }

            if (!nearFound)
            {
                nearCentre = farCentre;
                nearConfidence = farConfidence * 0.5f;
            }

            if (!farFound)
            {
                farCentre = nearCentre;
                farConfidence = nearConfidence * 0.5f;
            }

            float lateral = PixelToNormalizedError(nearCentre);
            float heading = Mathf.Clamp((farCentre - nearCentre) / (processingWidth * 0.35f), -1f, 1f);
            float confidence = Mathf.Clamp01((nearConfidence + farConfidence) * 0.5f);

            if (hasFilteredDetection)
            {
                lateral = Mathf.Lerp(LatestDetection.lateralError, lateral, smoothing);
                heading = Mathf.Lerp(LatestDetection.headingError, heading, smoothing);
                confidence = Mathf.Lerp(LatestDetection.confidence, confidence, smoothing);
            }

            LatestDetection = new Detection
            {
                lateralError = lateral,
                headingError = heading,
                confidence = confidence,
                hasBoundaryPair = boundaryPair,
                boundaryPairConfidence = pairConfidence,
                leftBoundaryVisible = leftVisible,
                rightBoundaryVisible = rightVisible,
                leftBoundaryConfidence = leftSideConfidence,
                rightBoundaryConfidence = rightSideConfidence,
                timestamp = Time.timeAsDouble
            };
            hasFilteredDetection = confidence >= minimumPublishedConfidence;

            UpdateDebugTexture(nearCentre, farCentre, nearY, farY);
            return hasFilteredDetection;
        }

        private void BuildMask()
        {
            int bottom = Mathf.RoundToInt(roiBottom * (processingHeight - 1));
            int top = Mathf.RoundToInt(roiTop * (processingHeight - 1));

            for (int i = 0; i < mask.Length; i++)
            {
                int y = i / processingWidth;
                if (y < bottom || y > top)
                {
                    mask[i] = false;
                    continue;
                }

                Color.RGBToHSV(pixels[i], out float h, out float s, out float v);
                bool hueMatches = minimumHue <= maximumHue
                    ? h >= minimumHue && h <= maximumHue
                    : h >= minimumHue || h <= maximumHue;
                mask[i] = hueMatches && s >= minimumSaturation && v >= minimumValue;
            }
        }

        private void MeasureBoundarySideVisibility(
            out bool leftVisible, out bool rightVisible,
            out float leftConfidence, out float rightConfidence)
        {
            int bottom = Mathf.RoundToInt(roiBottom * (processingHeight - 1));
            int top = Mathf.RoundToInt(Mathf.Lerp(roiBottom, roiTop, 0.62f) * (processingHeight - 1));
            float imageCentre = (processingWidth - 1) * 0.5f;
            int sampledRows = 0;
            int leftRows = 0;
            int rightRows = 0;

            for (int y = bottom; y <= top; y += 2)
            {
                sampledRows++;
                bool rowLeft = false;
                bool rowRight = false;
                int x = 0;
                while (x < processingWidth)
                {
                    if (!mask[y * processingWidth + x])
                    {
                        x++;
                        continue;
                    }

                    int start = x;
                    while (x < processingWidth && mask[y * processingWidth + x]) x++;
                    int length = x - start;
                    // Wide horizontal/intersection paint is not a longitudinal boundary.
                    if (length < minimumRunWidth || length > processingWidth * 0.18f)
                        continue;

                    float runCentre = start + (length - 1) * 0.5f;
                    if (runCentre < imageCentre) rowLeft = true;
                    else rowRight = true;
                }
                if (rowLeft) leftRows++;
                if (rowRight) rightRows++;
            }

            leftConfidence = sampledRows > 0 ? leftRows / (float)sampledRows : 0f;
            rightConfidence = sampledRows > 0 ? rightRows / (float)sampledRows : 0f;
            leftVisible = leftConfidence >= 0.25f;
            rightVisible = rightConfidence >= 0.25f;
        }

        private bool TryFindBandCentre(int centreY, out float centreX, out float confidence)
        {
            float centreSum = 0f;
            float confidenceSum = 0f;
            int validRows = 0;
            int halfBand = rowsPerBand / 2;

            for (int y = centreY - halfBand; y <= centreY + halfBand; y++)
            {
                if (y < 0 || y >= processingHeight)
                    continue;

                if (TryFindRowCentre(y, out float rowCentre, out float rowConfidence))
                {
                    centreSum += rowCentre;
                    confidenceSum += rowConfidence;
                    validRows++;
                }
            }

            centreX = validRows > 0 ? centreSum / validRows : 0f;
            confidence = validRows > 0
                ? (confidenceSum / validRows) * (validRows / (float)Mathf.Max(rowsPerBand, 1))
                : 0f;
            return validRows > 0;
        }

        private bool TryFindRowCentre(int y, out float centreX, out float confidence)
        {
            float imageCentre = (processingWidth - 1) * 0.5f;
            float leftCentre = -1f;
            float rightCentre = -1f;
            float closestCentre = -1f;
            int selectedPixels = 0;

            int x = 0;
            while (x < processingWidth)
            {
                if (!mask[y * processingWidth + x])
                {
                    x++;
                    continue;
                }

                int start = x;
                while (x < processingWidth && mask[y * processingWidth + x])
                    x++;
                int length = x - start;
                if (length < minimumRunWidth)
                    continue;

                float runCentre = start + (length - 1) * 0.5f;
                if (markingLayout == MarkingLayout.SingleCentreLine)
                {
                    if (closestCentre < 0f || Mathf.Abs(runCentre - imageCentre) < Mathf.Abs(closestCentre - imageCentre))
                    {
                        closestCentre = runCentre;
                        selectedPixels = length;
                    }
                }
                else if (runCentre < imageCentre && (leftCentre < 0f || runCentre > leftCentre))
                {
                    leftCentre = runCentre;
                    selectedPixels += length;
                }
                else if (runCentre >= imageCentre && (rightCentre < 0f || runCentre < rightCentre))
                {
                    rightCentre = runCentre;
                    selectedPixels += length;
                }
            }

            if (markingLayout == MarkingLayout.SingleCentreLine)
            {
                centreX = closestCentre;
                confidence = closestCentre >= 0f ? Mathf.Clamp01(selectedPixels / 8f) : 0f;
                return closestCentre >= 0f;
            }

            float minimumSeparationPixels = minimumBoundarySeparation * processingWidth;
            if (leftCentre >= 0f && rightCentre >= 0f && rightCentre - leftCentre >= minimumSeparationPixels)
            {
                centreX = (leftCentre + rightCentre) * 0.5f;
                confidence = Mathf.Clamp01((rightCentre - leftCentre) / processingWidth + selectedPixels / 20f);
                return true;
            }

            centreX = 0f;
            confidence = 0f;
            return false;
        }

        private float PixelToNormalizedError(float x)
        {
            return Mathf.Clamp((x - (processingWidth - 1) * 0.5f) / (processingWidth * 0.5f), -1f, 1f);
        }

        private void PublishInvalidDetection(
            bool leftVisible, bool rightVisible,
            float leftConfidence, float rightConfidence)
        {
            LatestDetection = new Detection
            {
                lateralError = LatestDetection.lateralError,
                headingError = LatestDetection.headingError,
                confidence = 0f,
                hasBoundaryPair = false,
                boundaryPairConfidence = 0f,
                leftBoundaryVisible = leftVisible,
                rightBoundaryVisible = rightVisible,
                leftBoundaryConfidence = leftConfidence,
                rightBoundaryConfidence = rightConfidence,
                timestamp = Time.timeAsDouble
            };
            hasFilteredDetection = false;
        }

        private void AllocateBuffers()
        {
            ReleaseBuffers();
            renderTexture = new RenderTexture(processingWidth, processingHeight, 16, RenderTextureFormat.ARGB32)
            {
                name = "Lane detector RGB"
            };
            renderTexture.Create();
            readbackTexture = new Texture2D(processingWidth, processingHeight, TextureFormat.RGB24, false);
            debugTexture = new Texture2D(processingWidth, processingHeight, TextureFormat.RGBA32, false);
            pixels = new Color32[processingWidth * processingHeight];
            overlayPixels = new Color32[pixels.Length];
            mask = new bool[pixels.Length];
        }

        private void UpdateDebugTexture(float nearX, float farX, int nearY, int farY)
        {
            if (!drawDebugOverlay || debugTexture == null)
                return;

            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 source = pixels[i];
                overlayPixels[i] = mask[i]
                    ? new Color32(0, 255, 80, 255)
                    : new Color32((byte)(source.r / 3), (byte)(source.g / 3), (byte)(source.b / 3), 255);
            }

            DrawCross(nearX, nearY, new Color32(255, 60, 60, 255));
            DrawCross(farX, farY, new Color32(60, 160, 255, 255));
            DrawVirtualCentreLine(new Color32(0, 220, 255, 255));
            debugTexture.SetPixels32(overlayPixels);
            debugTexture.Apply(false, false);
        }

        private void DrawCross(float centreX, int centreY, Color32 colour)
        {
            if (centreX < 0f)
                return;

            int cx = Mathf.RoundToInt(centreX);
            for (int d = -4; d <= 4; d++)
            {
                SetOverlayPixel(cx + d, centreY, colour);
                SetOverlayPixel(cx, centreY + d, colour);
            }
        }

        private void DrawVirtualCentreLine(Color32 colour)
        {
            int x = processingWidth / 2;
            int bottom = Mathf.RoundToInt(roiBottom * (processingHeight - 1));
            int top = Mathf.RoundToInt(roiTop * (processingHeight - 1));
            for (int y = bottom; y <= top; y++)
                SetOverlayPixel(x, y, colour);
        }

        private void SetOverlayPixel(int x, int y, Color32 colour)
        {
            if (x >= 0 && x < processingWidth && y >= 0 && y < processingHeight)
                overlayPixels[y * processingWidth + x] = colour;
        }

        private void OnDestroy()
        {
            ReleaseBuffers();
        }

        private void ReleaseBuffers()
        {
            if (renderTexture != null)
            {
                renderTexture.Release();
                Destroy(renderTexture);
            }

            if (readbackTexture != null)
                Destroy(readbackTexture);
            if (debugTexture != null)
                Destroy(debugTexture);
        }

        private void OnGUI()
        {
            if (!drawDebugOverlay || debugTexture == null)
                return;

            const float scale = 2f;
            GUI.DrawTexture(new Rect(10, 10, processingWidth * scale, processingHeight * scale), debugTexture, ScaleMode.ScaleToFit, false);
            Detection d = LatestDetection;
            GUI.Label(new Rect(10, 15 + processingHeight * scale, 420, 24),
                $"Lane: lateral={d.lateralError:F2}, heading={d.headingError:F2}, confidence={d.confidence:F2}, " +
                $"L={(d.leftBoundaryVisible ? d.leftBoundaryConfidence.ToString("F2") : "NO")}, " +
                $"R={(d.rightBoundaryVisible ? d.rightBoundaryConfidence.ToString("F2") : "NO")}, " +
                $"pair={(d.hasBoundaryPair ? d.boundaryPairConfidence.ToString("F2") : "NO")}");
        }
    }
}
