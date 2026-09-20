// YOLOX object detector (COCO). Self-contained Unity Sentis inference.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unity.InferenceEngine;
using UnityEngine;

namespace SentisModels
{
public sealed class YoloxDetector : IDisposable
{
    const int DefaultInputSize = 640;
    const int NumCoords = 4;
    const float NmsIouThreshold = 0.45f;
    const string DefaultModelFile = "yolox_fp16.sentis";

    static readonly string[] s_CocoLabels =
    {
        "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat", "traffic light",
        "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat", "dog", "horse", "sheep", "cow",
        "elephant", "bear", "zebra", "giraffe", "backpack", "umbrella", "handbag", "tie", "suitcase", "frisbee",
        "skis", "snowboard", "sports ball", "kite", "baseball bat", "baseball glove", "skateboard", "surfboard", "tennis racket", "bottle",
        "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple", "sandwich", "orange",
        "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair", "couch", "potted plant", "bed",
        "dining table", "toilet", "tv", "laptop", "mouse", "remote", "keyboard", "cell phone", "microwave", "oven",
        "toaster", "sink", "refrigerator", "book", "clock", "vase", "scissors", "teddy bear", "hair drier", "toothbrush"
    };

    readonly BackendType m_BackendType;
    readonly float m_ConfidenceThreshold;
    readonly bool m_LogOutputShapeOnce;
    readonly bool m_WarmupOnLoad;
    // Swap R<->B before inference. YOLOX is typically trained on BGR while Sentis ToTensor produces RGB.
    readonly bool m_SwapRedBlue;

    string[] Labels => s_CocoLabels;

    Worker m_Worker;
    // Model input resolution (square), read from the loaded model; falls back to DefaultInputSize.
    int m_InputSize = DefaultInputSize;
    bool m_LoggedShape;
    // Worker is not re-entrant; serializes concurrent callers.
    readonly SemaphoreSlim m_InferLock = new SemaphoreSlim(1, 1);

    public struct Detection
    {
        public int ClassId;
        public string ClassName;
        public float Confidence;
        public Rect BoxXyxy;
    }

    public bool IsReady => m_Worker != null;
    public int InputSize => m_InputSize;

    public YoloxDetector(
        BackendType backendType = BackendType.GPUCompute,
        float confidenceThreshold = 0.25f,
        bool swapRedBlue = true,
        bool warmupOnLoad = true,
        bool logOutputShapeOnce = true)
    {
        m_BackendType = backendType;
        m_ConfidenceThreshold = confidenceThreshold;
        m_SwapRedBlue = swapRedBlue;
        m_WarmupOnLoad = warmupOnLoad;
        m_LogOutputShapeOnce = logOutputShapeOnce;
    }

    // modelRoot: absolute path to the folder holding yolox_fp16.sentis.
    public void Load(string modelRoot, string modelFile = DefaultModelFile)
    {
        DisposeResources();
        Model model;
        try
        {
            model = ModelLoader.Load(Path.Combine(modelRoot, modelFile));
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            return;
        }

        m_InputSize = ResolveInputSize(model, DefaultInputSize);
        m_Worker = new Worker(model, m_BackendType);

        if (m_WarmupOnLoad)
            WarmupAsync();
    }

    // YOLO inputs are square NCHW (1, 3, S, S); read S from the model so 320- and 640-input
    // exports both work. Falls back when the input dim is dynamic/unknown.
    static int ResolveInputSize(Model model, int fallback)
    {
        if (model.inputs.Count == 0)
            return fallback;
        var dynamicShape = model.inputs[0].shape;
        if (!dynamicShape.IsStatic())
            return fallback;
        var shape = dynamicShape.ToTensorShape();
        return shape.rank >= 4 ? Mathf.Max(1, shape[shape.rank - 1]) : fallback;
    }

    // One throwaway inference on a blank frame so the GPUCompute kernels compile before the first
    // real detection, which would otherwise stall while every shader is built.
    async void WarmupAsync()
    {
        if (m_Worker == null)
            return;

        await m_InferLock.WaitAsync();
        try
        {
            using var input = new Tensor<float>(new TensorShape(1, 3, m_InputSize, m_InputSize));
            m_Worker.Schedule(input);

            var out0 = m_Worker.PeekOutput(0) as Tensor<float>;
            if (out0 != null)
                (await out0.ReadbackAndCloneAsync()).Dispose();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
        finally
        {
            m_InferLock.Release();
        }
    }

    public async Task<List<Detection>> DetectAsync(Texture frame)
    {
        if (m_Worker == null || frame == null)
            return new List<Detection>();

        // No ConfigureAwait(false): must resume on Unity main thread for Schedule + readback.
        await m_InferLock.WaitAsync();
        try
        {
            using var input = new Tensor<float>(new TensorShape(1, 3, m_InputSize, m_InputSize));
            // Sentis ToTensor produces RGB; YOLOX is typically BGR. m_SwapRedBlue toggles R<->B (the
            // [0,1]->[0,255] scale YOLOX expects is baked into the model).
            var transform = m_SwapRedBlue
                ? new TextureTransform().SetChannelSwizzle(2, 1, 0, 3)
                : new TextureTransform();
            TextureConverter.ToTensor(frame, input, transform);
            m_Worker.Schedule(input);

            var out0 = m_Worker.PeekOutput(0) as Tensor<float>;
            if (out0 == null)
            {
                if (!m_LoggedShape)
                {
                    Debug.LogWarning("[Vision] YOLOX model produced no float output 0.");
                    m_LoggedShape = true;
                }
                return new List<Detection>();
            }

            using var c0 = await out0.ReadbackAndCloneAsync();

            if (m_LogOutputShapeOnce && !m_LoggedShape)
            {
                Debug.Log("[Vision] YOLOX output: out0=" + c0.shape);
                m_LoggedShape = true;
            }

            return Decode(c0);
        }
        finally
        {
            m_InferLock.Release();
        }
    }

    // YOLOX output: a single tensor [1, N, 5+C] or [1, 5+C, N], where each of the N candidates is
    // [cx, cy, w, h, objectness, class_0 .. class_{C-1}]. Scores are already sigmoid-activated and
    // box coords are in input-pixel space (export YOLOX with the grid/stride decode baked in, i.e.
    // decode_in_inference). Input is RGB NCHW in [0,1] (Sentis ToTensor default) — bake any /255 or
    // mean/std normalization into the exported model.
    List<Detection> Decode(Tensor<float> t)
    {
        var labels = Labels;
        var feat = NumCoords + 1 + labels.Length; // cx,cy,w,h + objectness + classes

        if (!TryResolveLayout(t, feat, out var featAxis, out var candAxis))
        {
            if (!m_LoggedShape)
            {
                Debug.LogWarning("[Vision] Could not resolve YOLOX output layout (expected a dim == " +
                    feat + " for " + labels.Length + " classes): " + t.shape);
                m_LoggedShape = true;
            }
            return new List<Detection>();
        }

        var candidates = t.shape[candAxis];
        var featMajor = featAxis < candAxis; // [1, F, N] flattens feature-major
        float At(int f, int i) => featMajor ? t[f * candidates + i] : t[i * feat + f];

        var dets = new List<Detection>();
        for (var i = 0; i < candidates; i++)
        {
            var obj = At(4, i);

            var bestClass = 0;
            var bestCls = float.NegativeInfinity;
            for (var c = 0; c < labels.Length; c++)
            {
                var v = At(5 + c, i);
                if (v > bestCls) { bestCls = v; bestClass = c; }
            }

            var score = obj * bestCls;
            if (score < m_ConfidenceThreshold)
                continue;

            float cx = At(0, i), cy = At(1, i), w = At(2, i), h = At(3, i);
            var x1 = Mathf.Clamp(cx - w * 0.5f, 0f, m_InputSize);
            var y1 = Mathf.Clamp(cy - h * 0.5f, 0f, m_InputSize);
            var x2 = Mathf.Clamp(cx + w * 0.5f, 0f, m_InputSize);
            var y2 = Mathf.Clamp(cy + h * 0.5f, 0f, m_InputSize);
            if (x2 - x1 < 1f || y2 - y1 < 1f)
                continue;

            dets.Add(new Detection
            {
                ClassId = bestClass,
                ClassName = labels[bestClass],
                Confidence = score,
                BoxXyxy = new Rect(x1, y1, x2 - x1, y2 - y1),
            });
        }

        return NonMaxSuppression(dets, NmsIouThreshold);
    }

    static bool TryResolveLayout(Tensor<float> t, int targetDim, out int valueAxis, out int candidateAxis)
    {
        valueAxis = -1;
        candidateAxis = -1;
        var shape = t.shape;
        if (shape.rank < 2)
            return false;

        for (var ax = 0; ax < shape.rank; ax++)
        {
            if (shape[ax] == targetDim) { valueAxis = ax; break; }
        }
        if (valueAxis < 0)
            return false;

        var maxDim = -1;
        for (var ax = 0; ax < shape.rank; ax++)
        {
            if (ax == valueAxis) continue;
            var d = shape[ax];
            if (d <= 1) continue;
            if (d > maxDim) { maxDim = d; candidateAxis = ax; }
        }
        if (candidateAxis < 0)
        {
            for (var ax = 0; ax < shape.rank; ax++)
                if (ax != valueAxis) { candidateAxis = ax; break; }
        }
        return candidateAxis >= 0;
    }

    static List<Detection> NonMaxSuppression(List<Detection> dets, float iouThreshold)
    {
        dets.Sort((x, y) => y.Confidence.CompareTo(x.Confidence));
        var kept = new List<Detection>();
        var removed = new bool[dets.Count];
        for (var i = 0; i < dets.Count; i++)
        {
            if (removed[i]) continue;
            kept.Add(dets[i]);
            for (var j = i + 1; j < dets.Count; j++)
            {
                if (removed[j]) continue;
                if (dets[i].ClassId == dets[j].ClassId && Iou(dets[i].BoxXyxy, dets[j].BoxXyxy) > iouThreshold)
                    removed[j] = true;
            }
        }
        return kept;
    }

    static float Iou(Rect a, Rect b)
    {
        var x1 = Mathf.Max(a.xMin, b.xMin);
        var y1 = Mathf.Max(a.yMin, b.yMin);
        var x2 = Mathf.Min(a.xMax, b.xMax);
        var y2 = Mathf.Min(a.yMax, b.yMax);
        var inter = Mathf.Max(0f, x2 - x1) * Mathf.Max(0f, y2 - y1);
        var union = a.width * a.height + b.width * b.height - inter;
        return union <= 0f ? 0f : inter / union;
    }

    public void Dispose()
    {
        DisposeResources();
    }

    void DisposeResources()
    {
        m_Worker?.Dispose();
        m_Worker = null;
    }
}
}
