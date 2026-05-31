using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace PCSS.Runtime
{
    // Serialized, inspector-facing configuration for PCSSFeature.
    [Serializable]
    public class PCSSSettings
    {
        public ComputeShader computeShader;
        public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;

        [Header("Physical Sun Shadow")]
        [Range(0.0005f, 0.2f)]
        public float sunAngularDiameter = 0.0093f;

        [Range(0.1f, 8.0f)]
        public float penumbraScale = 1.0f;

        [Range(0.5f, 32.0f)]
        public float blockerSearchRadius = 4.0f;

        [Range(0.001f, 0.3f)]
        public float maxFilterRadius = 0.1f;

        [Range(0.0f, 100.0f)]
        public float farSoftness = 12.0f;

        [Header("Bias")]
        [Range(0.0f, 0.01f)]
        public float depthBias = 0.001f;

        [Range(0.0f, 5.0f)]
        public float slopeBias = 1.0f;

        [Header("Cascade")]
        [Range(0.0f, 0.5f)]
        public float cascadeBlend = 0.25f;

        public bool stabilizeSampling = false;

        [Header("Sampling")]
        public bool useBlueNoise = true;
        public bool useTemporalJitter = true;
        public Texture2D blueNoiseTexture;

        [Header("Debug")]
        public PCSSDebugMode debugMode = PCSSDebugMode.None;
        public Shader debugShader;
    }
}
