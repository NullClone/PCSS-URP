#ifndef PCSS_SAMPLING_INCLUDED
#define PCSS_SAMPLING_INCLUDED

// -----------------------------------------------------------------------------
// Physical directional-light PCSS for a single cascade: blocker search ->
// angular-diameter penumbra estimate -> variable-radius PCF.
// -----------------------------------------------------------------------------

#include "PCSSCommon.hlsl"

// Returns the raw lit fraction (1 = lit, 0 = shadow). valid is false when the
// position falls outside this cascade's shadow range.
//
// The sun is a directional light (no point source), so penumbra width comes from
// the angular diameter: penumbra = 2 * deltaD * tan(alpha / 2), where deltaD is
// the world-space receiver/blocker gap reconstructed from the ortho shadow depth.
float SampleCascadePCSS(float3 positionWS, uint cascadeIndex, uint2 pixel, float angle,
                        float texel, float searchRadius, out bool valid)
{
    float worldPerDepth, worldPerUV;
    float3 shadowCoord = WorldToShadowCoordEx(positionWS, cascadeIndex, worldPerDepth, worldPerUV);

    if (shadowCoord.z <= 0.0 || shadowCoord.z >= 1.0 ||
        shadowCoord.x < 0.0 || shadowCoord.x > 1.0 ||
        shadowCoord.y < 0.0 || shadowCoord.y > 1.0)
    {
        valid = false;
        return 1.0;
    }
    valid = true;

    float2 shadowUV = shadowCoord.xy;
    float receiverZ = shadowCoord.z;

    // Snap the kernel origin to texel centers to suppress shadow swimming.
    if (_PCSS_StabilizeSampling != 0)
        shadowUV = (floor(shadowUV / texel) + 0.5) * texel;

    float biasSearch = ReceiverBias(searchRadius);

    // Blocker search (Fibonacci spiral, configurable tap count).
    uint sampleCount = (uint)max(_PCSS_SampleCount, 1);
    float blockerSum = 0.0;
    int blockerCount = 0;
    [loop]
    for (uint i = 0; i < sampleCount; i++)
    {
        float2 offset = SampleDiskFibonacci(i, sampleCount, angle) * searchRadius;
        float s = _MainLightShadowmapTexture.SampleLevel(sampler_PointClamp, shadowUV + offset, 0);
        if (IsBlocker(s, receiverZ, biasSearch))
        {
            blockerSum += s;
            blockerCount++;
        }
    }

    if (blockerCount == 0) return 1.0;

    float avgBlockerZ = blockerSum / blockerCount;

    // Physical penumbra width from the sun's angular diameter.
    float deltaD = abs(receiverZ - avgBlockerZ) * worldPerDepth;
    deltaD *= (1.0 + deltaD * _PCSS_FarSoftness);
    float penumbraWorld = 2.0 * deltaD * tan(_PCSS_SunAngularDiameter * 0.5) * _PCSS_PenumbraScale;
    float filterRadius = penumbraWorld / worldPerUV;
    filterRadius = clamp(filterRadius, texel, _PCSS_MaxFilterRadius);

    float biasFilter = ReceiverBias(filterRadius);

    // Variable-radius PCF (Fibonacci spiral, configurable tap count).
    float litSum = 0.0;
    [loop]
    for (uint j = 0; j < sampleCount; j++)
    {
        float2 offset = SampleDiskFibonacci(j, sampleCount, angle) * filterRadius;
        float s = _MainLightShadowmapTexture.SampleLevel(sampler_PointClamp, shadowUV + offset, 0);
        litSum += IsBlocker(s, receiverZ, biasFilter) ? 0.0 : 1.0;
    }

    return litSum / (float)sampleCount;
}

// Cheap, continuous real-shadow evaluation for non-penumbra (umbra) pixels —
// the analogue of RecaNoMaho's SAMPLE_TEXTURE2D_SHADOW else-branch. A small
// fixed-radius PCF using the same manual IsBlocker compare, so its value agrees
// with SampleCascadePCSS at the penumbra/umbra boundary (no hard cut to 0).
// Skips the blocker search / penumbra estimate, so it is far cheaper than full
// PCSS while staying a genuine shadow sample rather than a constant.
float SampleHardShadowPCF(float3 positionWS, uint cascadeIndex, float angle, float texel, out bool valid)
{
    float worldPerDepth, worldPerUV;
    float3 shadowCoord = WorldToShadowCoordEx(positionWS, cascadeIndex, worldPerDepth, worldPerUV);

    if (shadowCoord.z <= 0.0 || shadowCoord.z >= 1.0 ||
        shadowCoord.x < 0.0 || shadowCoord.x > 1.0 ||
        shadowCoord.y < 0.0 || shadowCoord.y > 1.0)
    {
        valid = false;
        return 1.0;
    }
    valid = true;

    float2 shadowUV = shadowCoord.xy;
    float receiverZ = shadowCoord.z;

    if (_PCSS_StabilizeSampling != 0)
        shadowUV = (floor(shadowUV / texel) + 0.5) * texel;

    // Match the lower clamp of the PCSS branch (filterRadius >= texel) so the two
    // evaluations meet continuously; a touch above 1 texel softens contact edges.
    float radius = texel * 1.5;
    float bias = ReceiverBias(radius);

    float litSum = 0.0;
    [unroll]
    for (uint i = 0; i < UMBRA_SAMPLES; i++)
    {
        float2 offset = SampleDiskFibonacci(i, UMBRA_SAMPLES, angle) * radius;
        float s = _MainLightShadowmapTexture.SampleLevel(sampler_PointClamp, shadowUV + offset, 0);
        litSum += IsBlocker(s, receiverZ, bias) ? 0.0 : 1.0;
    }

    return litSum / (float)UMBRA_SAMPLES;
}

#endif // PCSS_SAMPLING_INCLUDED
