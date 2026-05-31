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

    // Blocker search.
    float blockerSum = 0.0;
    int blockerCount = 0;
    [unroll]
    for (int i = 0; i < N_SAMPLE; i++)
    {
        float2 offset = RotateVec2(poissonDisk[i], angle) * searchRadius;
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

    // Variable-radius PCF.
    float litSum = 0.0;
    [unroll]
    for (int j = 0; j < N_SAMPLE; j++)
    {
        float2 offset = RotateVec2(poissonDisk[j], angle) * filterRadius;
        float s = _MainLightShadowmapTexture.SampleLevel(sampler_PointClamp, shadowUV + offset, 0);
        litSum += IsBlocker(s, receiverZ, biasFilter) ? 0.0 : 1.0;
    }

    return litSum / N_SAMPLE;
}

#endif // PCSS_SAMPLING_INCLUDED
