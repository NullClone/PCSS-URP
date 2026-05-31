#ifndef PCSS_INPUT_INCLUDED
#define PCSS_INPUT_INCLUDED

// -----------------------------------------------------------------------------
// Data contract for the PCSS compute kernels: constants, resources, URP global
// shadow variables (read-only, non-invasive) and the C#-set _PCSS_* params.
// -----------------------------------------------------------------------------

#define N_SAMPLE 8
#define PI 3.14159265359
#define TWO_PI 6.28318530718
#define GOLDEN_RATIO 0.61803398875

// --- Resources ---------------------------------------------------------------
Texture2D<float> _CameraDepthTexture;          // raw device depth
Texture2D<float> _MainLightShadowmapTexture;   // URP main-light shadow atlas
Texture2D<float4> _PCSS_BlueNoiseTex;          // rotation-angle dither source

SamplerState sampler_PointClamp;
SamplerState sampler_LinearClamp;

RWTexture2D<float> _PCSS_MaskResult;  // kernel 0 output (1/4-res, 3-value mask)
Texture2D<float> _PCSS_MaskTex;       // kernel 1 input (same content, bilinear)
RWTexture2D<float> _SSShadowResult;   // kernel 1 output (-> _ScreenSpaceShadowmapTexture)

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

// --- Poisson disk (8 taps) ---------------------------------------------------
static const float2 poissonDisk[N_SAMPLE] =
{
    float2(-0.94201624, -0.39906216), float2(0.94558609, -0.76890725),
    float2(-0.09418410, -0.92938870), float2(0.34495938, 0.29387760),
    float2(-0.91588581, 0.45771432), float2(-0.81544232, -0.87912464),
    float2(0.97484398, 0.75648379), float2(0.44323325, -0.97511554)
};

#endif // PCSS_INPUT_INCLUDED
