#ifndef PCSS_COMMON_INCLUDED
#define PCSS_COMMON_INCLUDED

// -----------------------------------------------------------------------------
// Shared helpers: sampling rotation, world-position reconstruction, cascade
// selection, shadow-coord transform, blocker test, bias and distance fade.
// -----------------------------------------------------------------------------

#include "PCSSInput.hlsl"

// Per-2x2-block noise used as the blue-noise fallback.
float InterleavedGradientNoise(float2 pixCoord)
{
    float2 sharedCoord = floor(pixCoord / 2.0);
    return frac(sin(dot(sharedCoord, float2(12.9898, 78.233))) * 43758.5453);
}

// Poisson rotation angle. Spatial seed is locked per 2x2 block; the temporal
// term shuffles by the golden ratio so TAA integrates it into a clean gradient.
float GetRotationAngle(uint2 pixel)
{
    uint2 seed = pixel >> 1;
    float spatial;
    if (_PCSS_UseBlueNoise != 0)
    {
        uint2 c = seed % (uint2)max(_PCSS_BlueNoiseSize, float2(1.0, 1.0));
        spatial = _PCSS_BlueNoiseTex.Load(int3((int2)c, 0)).r;
    }
    else
    {
        spatial = InterleavedGradientNoise(pixel);
    }
    float temporal = frac(_PCSS_JitterIndex * GOLDEN_RATIO);
    return frac(spatial + temporal) * TWO_PI;
}

float2 RotateVec2(float2 v, float angle)
{
    float s = sin(angle);
    float c = cos(angle);
    return float2(v.x * c + v.y * s, -v.x * s + v.y * c);
}

// Reconstruct world position from screen UV and raw device depth. Reuses
// _PCSS_ReversedZ for the clip-space Y flip (matches the C# renderIntoTexture proj).
float3 ComputeWorldPosition(float2 screenUV, float rawDepth)
{
    float4 positionCS = float4(screenUV * 2.0 - 1.0, rawDepth, 1.0);
    if (_PCSS_ReversedZ != 0)
        positionCS.y = -positionCS.y;
    float4 positionWS = mul(_PCSS_InvViewProj, positionCS);
    return positionWS.xyz / positionWS.w;
}

uint ComputeCascadeIndex(float3 positionWS)
{
    if (_PCSS_CascadeCount <= 1)
        return 0;

    float3 fromCenter0 = positionWS - _CascadeShadowSplitSpheres0.xyz;
    float3 fromCenter1 = positionWS - _CascadeShadowSplitSpheres1.xyz;
    float3 fromCenter2 = positionWS - _CascadeShadowSplitSpheres2.xyz;
    float3 fromCenter3 = positionWS - _CascadeShadowSplitSpheres3.xyz;

    float4 distSq = float4(dot(fromCenter0, fromCenter0), dot(fromCenter1, fromCenter1),
                           dot(fromCenter2, fromCenter2), dot(fromCenter3, fromCenter3));
    float4 radii = float4(_CascadeShadowSplitSpheres0.w, _CascadeShadowSplitSpheres1.w,
                          _CascadeShadowSplitSpheres2.w, _CascadeShadowSplitSpheres3.w);
    float4 radiiSq = radii * radii;

    float4 weights = float4(distSq < radiiSq);
    weights.yzw = saturate(weights.yzw - weights.xyz);
    return (uint)(4 - dot(weights, float4(4, 3, 2, 1)));
}

float4 GetCascadeSphere(uint i)
{
    if (i == 0) return _CascadeShadowSplitSpheres0;
    if (i == 1) return _CascadeShadowSplitSpheres1;
    if (i == 2) return _CascadeShadowSplitSpheres2;
    return _CascadeShadowSplitSpheres3;
}

// True if storedZ is closer to the light than the receiver (i.e. a blocker).
bool IsBlocker(float storedZ, float receiverZ, float bias)
{
    if (_PCSS_ReversedZ != 0)
        return storedZ > receiverZ + bias;
    else
        return storedZ < receiverZ - bias;
}

// Receiver-plane bias derived from filter radius and the light's elevation.
float ReceiverBias(float radiusUV)
{
    float ly = abs(_PCSS_LightDirection.y);
    float tanElev = max(ly / sqrt(max(1.0 - ly * ly, 1e-4)), 1e-3);
    float b = _PCSS_DepthBias + _PCSS_SlopeBiasScale * radiusUV / tanElev;
    return min(b, 0.05);
}

// URP-compatible distance fade (fades out far shadows).
float MainLightShadowFade(float3 positionWS)
{
    float3 camToPixel = positionWS - _PCSS_CameraPosWS.xyz;
    float distSq = dot(camToPixel, camToPixel);
    float fade = distSq * _MainLightShadowParams.z + _MainLightShadowParams.w;
    return saturate(fade);
}

// Shadow-atlas coord plus world-space scales derived from the WorldToShadow row
// lengths (non-invasive: encodes cascade sphere size and atlas tiling).
//   worldPerDepth : normalized depth delta -> world distance (ortho, linear)
//   worldPerUV    : atlas UV delta         -> world distance
float3 WorldToShadowCoordEx(float3 positionWS, uint cascadeIndex, out float worldPerDepth, out float worldPerUV)
{
    float4x4 M = _MainLightWorldToShadow[cascadeIndex];
    float4 coord = mul(M, float4(positionWS, 1.0));
    float3 sc = coord.xyz / coord.w;
    worldPerDepth = 1.0 / max(length(M[2].xyz), 1e-8);
    worldPerUV = 1.0 / max(length(M[0].xyz), 1e-8);
    return sc;
}

#endif // PCSS_COMMON_INCLUDED
