#ifndef PCSS_INPUT_INCLUDED
#define PCSS_INPUT_INCLUDED

// -----------------------------------------------------------------------------
// Data contract for the PCSS compute kernels: constants, resources, URP global
// shadow variables (read-only, non-invasive) and the C#-set _PCSS_* params.
// -----------------------------------------------------------------------------

#define UMBRA_SAMPLES 8          // fixed cheap tap count for the umbra fast path
#define GOLDEN_ANGLE 2.39996323  // radians; Fibonacci spiral angular step
#define PI 3.14159265359
#define TWO_PI 6.28318530718
#define GOLDEN_RATIO 0.61803398875

// --- Resources ---------------------------------------------------------------
Texture2D<float> _CameraDepthTexture;          // raw device depth
Texture2D<float> _MainLightShadowmapTexture;   // URP main-light shadow atlas
Texture2D<float4> _PCSS_BlueNoiseTex;          // rotation-angle dither source

SamplerState sampler_PointClamp;
SamplerState sampler_LinearClamp;

RWTexture2D<float> _PCSS_MaskResult;  // kernel 0 / blur output (mask-res)
Texture2D<float> _PCSS_MaskTex;       // mask input (recon or blurred, bilinear)
RWTexture2D<float> _SSShadowResult;   // PCSS / denoise output (-> _ScreenSpaceShadowmapTexture)
Texture2D<float> _PCSS_ShadowRawTex;  // denoise input (the raw PCSS result)

// --- URP global shadow vars (set per-frame by MainLightShadowCasterPass) ------
float4x4 _MainLightWorldToShadow[5];
float4 _CascadeShadowSplitSpheres0;   // xyz=center, w=radius
float4 _CascadeShadowSplitSpheres1;
float4 _CascadeShadowSplitSpheres2;
float4 _CascadeShadowSplitSpheres3;
float4 _MainLightShadowParams;        // x=strength, y=soft, z=fadeScale, w=fadeBias

// --- C#-set parameters -------------------------------------------------------
float4x4 _PCSS_InvViewProj;       // inverse ViewProj (GPU clip space)
float4 _PCSS_CameraPosWS;         // camera world position (xyz)
float4 _PCSS_TextureSize;         // x=w, y=h, z=1/w, w=1/h (full-res RT)
float4 _PCSS_MaskSize;            // x=qw, y=qh, z=1/qw, w=1/qh (1/4-res mask RT)
float _PCSS_ShadowTexelSize;      // 1.0 / shadow atlas width
int _PCSS_CascadeCount;
float _PCSS_CascadeBorder;        // cascade blend width (0..1), 0 = hard
float _PCSS_BlockerSearchRadius;  // blocker search radius (texels)
float _PCSS_SunAngularDiameter;   // sun angular diameter alpha (radians)
float _PCSS_PenumbraScale;        // artistic multiplier (1 = physical)
float _PCSS_DepthBias;            // base shadow depth bias
float _PCSS_MaxFilterRadius;      // max filter radius (atlas UV)
float _PCSS_FarSoftness;          // extra softening for far/elongated shadow tips
float3 _PCSS_LightDirection;      // normalized direction towards the light
float _PCSS_SlopeBiasScale;       // slope (receiver-plane) bias factor
float2 _PCSS_BlueNoiseSize;       // blue-noise texture size (w, h)
int _PCSS_UseBlueNoise;           // 1 = blue noise, 0 = IGN fallback
int _PCSS_JitterIndex;            // per-frame temporal index (frameCount % 16)
int _PCSS_StabilizeSampling;      // 1 = snap sample origin to texel centers
int _PCSS_ReversedZ;              // 1 = reversed-Z (D3D/Vulkan/Metal), 0 = GL
int _PCSS_SampleCount;            // penumbra blocker/PCF tap count (Fibonacci spiral)
float _PCSS_DenoiseDepthSigma;    // depth-guard sigma (fraction of camera distance)

// Disk samples are generated on the fly as a rotated Fibonacci spiral
// (SampleDiskFibonacci in PCSSCommon.hlsl), so any tap count works without a
// large static table and coverage stays uniform at low counts.

#endif // PCSS_INPUT_INCLUDED
