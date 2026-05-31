using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace PCSS.Runtime
{
    // Resolution divisor for the reconnaissance mask, relative to screen size.
    // The enum value IS the divisor, so it can be used directly in arithmetic.
    public enum PCSSMaskResolution
    {
        Full = 1,
        Half = 2,
        Quarter = 4,
        Eighth = 8,
    }

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
        [Tooltip("Constant depth bias (normalized shadow depth) added to every " +
                 "blocker/PCF comparison. Removes residual acne; too high causes " +
                 "Peter-Panning (shadow detaches from the contact).")]
        [Range(0.0f, 0.01f)]
        public float depthBias = 0.001f;

        [Tooltip("Scale of the cone-based per-sample Z-offset: each sample's bias " +
                 "grows with its radius (a cone toward the light), so same-surface " +
                 "samples are not false blockers. This is the main self-shadow / " +
                 "banding fix; auto-strengthens as the sun gets lower. 0 disables it.")]
        [Range(0.0f, 5.0f)]
        public float slopeBias = 1.0f;

        [Header("Cascade")]
        [Range(0.0f, 0.5f)]
        public float cascadeBlend = 0.25f;

        public bool stabilizeSampling = false;

        [Header("Sampling")]
        [Tooltip("Penumbra blocker/PCF tap count (Fibonacci spiral). Higher = " +
                 "smoother soft shadows without TAA, at more cost. 16-32 is a good " +
                 "range when temporal jitter / TAA is off.")]
        [Range(8, 64)]
        public int sampleCount = 24;

        public bool useBlueNoise = true;

        [Tooltip("Jitter the sample rotation per frame so URP TAA can integrate it. " +
                 "Leave OFF when not using TAA (static blue-noise dither instead, no flicker).")]
        public bool useTemporalJitter = false;
        public Texture2D blueNoiseTexture;

        [Header("Denoise (for use without TAA)")]
        [Tooltip("Depth-guided 3x3 spatial blur on the penumbra band, replacing TAA " +
                 "as the denoiser. Edge-aware (never leaks across geometry) and " +
                 "penumbra-only (lit/umbra and contact edges are untouched).")]
        public bool denoise = true;

        [Tooltip("Depth tolerance of the denoise blur, as a fraction of the camera " +
                 "distance. Larger = stronger smoothing but more bleed across depth " +
                 "discontinuities. Smaller = more edge-preserving.")]
        [Range(0.005f, 0.1f)]
        public float denoiseDepthSensitivity = 0.03f;

        [Header("Reconnaissance Mask")]
        [Tooltip("Resolution of the reconnaissance mask relative to the screen. " +
                 "Lower (Quarter/Eighth) is cheaper; higher (Half/Full) gives a " +
                 "tighter penumbra-gate boundary with fewer stair-step artifacts.")]
        public PCSSMaskResolution maskResolution = PCSSMaskResolution.Quarter;

        [Tooltip("Blur the reconnaissance mask (the penumbra gate, not the shadow) " +
                 "with a 3x3 Gaussian so the lit/penumbra/umbra boundary is smooth " +
                 "instead of showing the mask's blocky stair-steps.")]
        public bool blurMask = true;

        [Header("Debug")]
        public PCSSDebugMode debugMode = PCSSDebugMode.None;

        public Shader debugShader;
    }
}
