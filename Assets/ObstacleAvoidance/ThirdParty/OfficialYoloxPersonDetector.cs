using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.InferenceEngine;
using UnityEngine;

namespace SentisModels
{
    /// <summary>
    /// Person-only decoder for the official Megvii YOLOX-S 640 ONNX release.
    /// Pre/post-processing follows the official ONNXRuntime example.
    /// </summary>
    public sealed class OfficialYoloxPersonDetector : IPersonBoxDetector
    {
        private const int ModelSize = 640;
        private const int FeatureCount = 85;
        private const float NmsIouThreshold = 0.45f;
        private readonly float confidenceThreshold;
        private readonly SemaphoreSlim inferenceLock = new SemaphoreSlim(1, 1);
        private Worker worker;

        public bool IsReady => worker != null;
        public int InputSize => ModelSize;

        public OfficialYoloxPersonDetector(BackendType backend, float threshold)
        {
            confidenceThreshold = threshold;
            ModelAsset asset = Resources.Load<ModelAsset>("ShipRobotVision/yolox_s");
            if (asset == null)
            {
                Debug.LogError("[Vision] Official YOLOX-S model was not imported at Resources/ShipRobotVision/yolox_s.onnx.");
                return;
            }

            Model model = ModelLoader.Load(asset);
            // TextureConverter returns [0,1]. Official YOLOX ONNX expects unnormalised [0,255].
            var graph = new FunctionalGraph();
            FunctionalTensor graphInput = graph.AddInput(model, 0);
            FunctionalTensor[] outputs = Functional.Forward(model, graphInput * 255f);
            model = graph.Compile(outputs);
            worker = new Worker(model, backend);
        }

        public async Task<List<PersonBoxDetection>> DetectAsync(Texture texture)
        {
            if (!IsReady || texture == null)
                return new List<PersonBoxDetection>();

            await inferenceLock.WaitAsync();
            try
            {
                using var input = new Tensor<float>(new TensorShape(1, 3, ModelSize, ModelSize));
                var transform = new TextureTransform()
                    .SetChannelSwizzle(ChannelSwizzle.BGRA)
                    .SetCoordOrigin(CoordOrigin.TopLeft);
                TextureConverter.ToTensor(texture, input, transform);
                worker.Schedule(input);
                Tensor<float> output = worker.PeekOutput(0) as Tensor<float>;
                if (output == null)
                    return new List<PersonBoxDetection>();
                using Tensor<float> cpuOutput = await output.ReadbackAndCloneAsync();
                return DecodePersonBoxes(cpuOutput);
            }
            finally
            {
                inferenceLock.Release();
            }
        }

        private List<PersonBoxDetection> DecodePersonBoxes(Tensor<float> tensor)
        {
            int candidates = ResolveCandidateCount(tensor);
            if (candidates <= 0)
            {
                Debug.LogWarning($"[Vision] Unexpected official YOLOX output shape: {tensor.shape}");
                return new List<PersonBoxDetection>();
            }

            bool featureMajor = tensor.shape.rank >= 3 && tensor.shape[1] == FeatureCount;
            float At(int candidate, int feature) => featureMajor
                ? tensor[feature * candidates + candidate]
                : tensor[candidate * FeatureCount + feature];

            var detections = new List<PersonBoxDetection>();
            for (int i = 0; i < candidates; i++)
            {
                float confidence = At(i, 4) * At(i, 5); // objectness * COCO person score
                if (confidence < confidenceThreshold)
                    continue;

                ResolveGrid(i, out int gridX, out int gridY, out int stride);
                float centreX = (At(i, 0) + gridX) * stride;
                float centreY = (At(i, 1) + gridY) * stride;
                float width = Mathf.Exp(Mathf.Clamp(At(i, 2), -10f, 10f)) * stride;
                float height = Mathf.Exp(Mathf.Clamp(At(i, 3), -10f, 10f)) * stride;
                float x1 = Mathf.Clamp(centreX - 0.5f * width, 0f, ModelSize);
                float y1 = Mathf.Clamp(centreY - 0.5f * height, 0f, ModelSize);
                float x2 = Mathf.Clamp(centreX + 0.5f * width, 0f, ModelSize);
                float y2 = Mathf.Clamp(centreY + 0.5f * height, 0f, ModelSize);
                if (x2 - x1 < 2f || y2 - y1 < 2f)
                    continue;
                detections.Add(new PersonBoxDetection
                {
                    Confidence = confidence,
                    BoxXyxy = new Rect(x1, y1, x2 - x1, y2 - y1)
                });
            }
            return NonMaxSuppression(detections);
        }

        private static int ResolveCandidateCount(Tensor<float> tensor)
        {
            if (tensor.shape.rank < 2)
                return -1;
            for (int axis = 0; axis < tensor.shape.rank; axis++)
            {
                int size = tensor.shape[axis];
                if (size == 8400)
                    return size;
            }
            return -1;
        }

        private static void ResolveGrid(int index, out int gridX, out int gridY, out int stride)
        {
            if (index < 6400)
            {
                stride = 8;
                gridX = index % 80;
                gridY = index / 80;
                return;
            }
            index -= 6400;
            if (index < 1600)
            {
                stride = 16;
                gridX = index % 40;
                gridY = index / 40;
                return;
            }
            index -= 1600;
            stride = 32;
            gridX = index % 20;
            gridY = index / 20;
        }

        private static List<PersonBoxDetection> NonMaxSuppression(List<PersonBoxDetection> detections)
        {
            detections.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
            var kept = new List<PersonBoxDetection>();
            var removed = new bool[detections.Count];
            for (int i = 0; i < detections.Count; i++)
            {
                if (removed[i])
                    continue;
                kept.Add(detections[i]);
                for (int j = i + 1; j < detections.Count; j++)
                    if (!removed[j] && IoU(detections[i].BoxXyxy, detections[j].BoxXyxy) > NmsIouThreshold)
                        removed[j] = true;
            }
            return kept;
        }

        private static float IoU(Rect a, Rect b)
        {
            float x1 = Mathf.Max(a.xMin, b.xMin);
            float y1 = Mathf.Max(a.yMin, b.yMin);
            float x2 = Mathf.Min(a.xMax, b.xMax);
            float y2 = Mathf.Min(a.yMax, b.yMax);
            float intersection = Mathf.Max(0f, x2 - x1) * Mathf.Max(0f, y2 - y1);
            float union = a.width * a.height + b.width * b.height - intersection;
            return union > 0f ? intersection / union : 0f;
        }

        public void Dispose()
        {
            worker?.Dispose();
            worker = null;
        }
    }
}
