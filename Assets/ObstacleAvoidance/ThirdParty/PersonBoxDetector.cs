using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace SentisModels
{
    public struct PersonBoxDetection
    {
        public float Confidence;
        public Rect BoxXyxy;
    }

    public interface IPersonBoxDetector : IDisposable
    {
        bool IsReady { get; }
        int InputSize { get; }
        Task<List<PersonBoxDetection>> DetectAsync(Texture texture);
    }
}
