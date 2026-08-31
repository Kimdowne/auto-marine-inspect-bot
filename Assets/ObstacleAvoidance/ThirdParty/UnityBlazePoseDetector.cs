using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Unity.InferenceEngine;
using Unity.Mathematics;
using UnityEngine;

namespace SentisModels
{
    // Adapted from Unity-Technologies/sentis-samples BlazePose detector (Apache-2.0).
    public sealed class UnityBlazePoseDetector : IPersonBoxDetector
    {
        public const int DetectorInputSize = 224;
        private const int AnchorCount = 2254;
        private readonly float confidenceThreshold;
        private readonly SemaphoreSlim inferenceLock = new SemaphoreSlim(1, 1);
        private Worker worker;
        private Tensor<float> input;
        private float[,] anchors;
        private ComputeShader imageTransform;
        private int imageSampleKernel;

        public bool IsReady => worker != null && input != null && anchors != null && imageTransform != null;
        public int InputSize => DetectorInputSize;

        public UnityBlazePoseDetector(BackendType backend, float threshold)
        {
            confidenceThreshold = threshold;
            ModelAsset modelAsset = Resources.Load<ModelAsset>("ShipRobotVision/pose_detection");
            TextAsset anchorsAsset = Resources.Load<TextAsset>("ShipRobotVision/pose_anchors");
            imageTransform = Resources.Load<ComputeShader>("ComputeShaders/ShipRobotImageTransform");
            if (modelAsset == null || anchorsAsset == null || imageTransform == null)
            {
                Debug.LogError("[Vision] BlazePose resources are missing.");
                return;
            }

            anchors = LoadAnchors(anchorsAsset.text);
            Model model = ModelLoader.Load(modelAsset);
            var graph = new FunctionalGraph();
            FunctionalTensor graphInput = graph.AddInput(model, 0);
            FunctionalTensor[] outputs = Functional.Forward(model, graphInput);
            FunctionalTensor rawBoxes = outputs[0];
            FunctionalTensor rawScores = outputs[1];
            FunctionalTensor scores = Functional.Sigmoid(Functional.Clamp(rawScores, -100f, 100f));
            FunctionalTensor bestIndex = Functional.ArgMax(rawScores, 1).Squeeze();
            FunctionalTensor selectedBoxes = Functional.IndexSelect(rawBoxes, 1, bestIndex).Unsqueeze(0);
            FunctionalTensor selectedScores = Functional.IndexSelect(scores, 1, bestIndex).Unsqueeze(0);
            model = graph.Compile(bestIndex, selectedScores, selectedBoxes);
            worker = new Worker(model, backend);
            input = new Tensor<float>(new TensorShape(1, DetectorInputSize, DetectorInputSize, 3));
            imageSampleKernel = imageTransform.FindKernel("ImageSample");
        }

        public async Task<List<PersonBoxDetection>> DetectAsync(Texture texture)
        {
            var result = new List<PersonBoxDetection>(1);
            if (!IsReady || texture == null)
                return result;

            await inferenceLock.WaitAsync();
            try
            {
                float size = Mathf.Max(texture.width, texture.height);
                float scale = size / DetectorInputSize;
                float2x3 transform = Mul(
                    TranslationMatrix(0.5f * (new float2(texture.width, texture.height) + new float2(-size, size))),
                    ScaleMatrix(new float2(scale, -scale)));
                SampleImageAffine(texture, input, transform);
                worker.Schedule(input);

                var indexTask = (worker.PeekOutput(0) as Tensor<int>).ReadbackAndCloneAsync();
                var scoreTask = (worker.PeekOutput(1) as Tensor<float>).ReadbackAndCloneAsync();
                var boxTask = (worker.PeekOutput(2) as Tensor<float>).ReadbackAndCloneAsync();
                using Tensor<int> index = await indexTask;
                using Tensor<float> score = await scoreTask;
                using Tensor<float> box = await boxTask;
                float confidence = score[0];
                if (confidence < confidenceThreshold)
                    return result;

                int anchorIndex = index[0];
                float2 anchor = DetectorInputSize * new float2(anchors[anchorIndex, 0], anchors[anchorIndex, 1]);
                float2 bodyCenter = Mul(transform, anchor + new float2(box[0, 0, 4], box[0, 0, 5]));
                float2 bodyScalePoint = Mul(transform, anchor + new float2(box[0, 0, 6], box[0, 0, 7]));
                float radius = 1.25f * math.length(bodyScalePoint - bodyCenter);

                float xMin = Mathf.Clamp(bodyCenter.x - radius, 0f, texture.width);
                float xMax = Mathf.Clamp(bodyCenter.x + radius, 0f, texture.width);
                float yBottom = Mathf.Clamp(bodyCenter.y - radius, 0f, texture.height);
                float yTop = Mathf.Clamp(bodyCenter.y + radius, 0f, texture.height);
                // Blaze image coordinates are bottom-left; GUI detection boxes use top-left.
                float normalizedX = xMin / texture.width;
                float normalizedY = 1f - yTop / texture.height;
                float normalizedWidth = (xMax - xMin) / texture.width;
                float normalizedHeight = (yTop - yBottom) / texture.height;
                if (normalizedWidth <= 0.01f || normalizedHeight <= 0.01f)
                    return result;

                result.Add(new PersonBoxDetection
                {
                    Confidence = confidence,
                    BoxXyxy = new Rect(
                        normalizedX * DetectorInputSize,
                        normalizedY * DetectorInputSize,
                        normalizedWidth * DetectorInputSize,
                        normalizedHeight * DetectorInputSize)
                });
                return result;
            }
            finally
            {
                inferenceLock.Release();
            }
        }

        private void SampleImageAffine(Texture texture, Tensor<float> destination, float2x3 matrix)
        {
            ComputeTensorData data = ComputeTensorData.Pin(destination, false);
            imageTransform.SetTexture(imageSampleKernel, "X_tex2D", texture);
            imageTransform.SetBuffer(imageSampleKernel, "Optr", data.buffer);
            imageTransform.SetInt("O_height", destination.shape[1]);
            imageTransform.SetInt("O_width", destination.shape[2]);
            imageTransform.SetInt("O_channels", destination.shape[3]);
            imageTransform.SetInt("X_height", texture.height);
            imageTransform.SetInt("X_width", texture.width);
            imageTransform.SetMatrix("affineMatrix", new Matrix4x4(
                new Vector4(matrix[0][0], matrix[0][1]),
                new Vector4(matrix[1][0], matrix[1][1]),
                new Vector4(matrix[2][0], matrix[2][1]),
                Vector4.zero));
            imageTransform.Dispatch(imageSampleKernel, DivCeil(destination.shape[1], 8), DivCeil(destination.shape[2], 8), 1);
        }

        private static int DivCeil(int value, int divisor) => (value + divisor - 1) / divisor;

        private static float[,] LoadAnchors(string csv)
        {
            var result = new float[AnchorCount, 4];
            string[] lines = csv.Split('\n');
            for (int i = 0; i < AnchorCount; i++)
            {
                string[] values = lines[i].Trim().Split(',');
                for (int j = 0; j < 4; j++)
                    result[i, j] = float.Parse(values[j], CultureInfo.InvariantCulture);
            }
            return result;
        }

        private static float2x3 Mul(float2x3 a, float2x3 b) => new float2x3(
            a[0][0] * b[0][0] + a[1][0] * b[0][1],
            a[0][0] * b[1][0] + a[1][0] * b[1][1],
            a[0][0] * b[2][0] + a[1][0] * b[2][1] + a[2][0],
            a[0][1] * b[0][0] + a[1][1] * b[0][1],
            a[0][1] * b[1][0] + a[1][1] * b[1][1],
            a[0][1] * b[2][0] + a[1][1] * b[2][1] + a[2][1]);

        private static float2 Mul(float2x3 a, float2 b) => new float2(
            a[0][0] * b.x + a[1][0] * b.y + a[2][0],
            a[0][1] * b.x + a[1][1] * b.y + a[2][1]);

        private static float2x3 TranslationMatrix(float2 delta) => new float2x3(1, 0, delta.x, 0, 1, delta.y);
        private static float2x3 ScaleMatrix(float2 scale) => new float2x3(scale.x, 0, 0, 0, scale.y, 0);

        public void Dispose()
        {
            worker?.Dispose();
            worker = null;
            input?.Dispose();
            input = null;
        }
    }
}
