#ifndef PCSS_SAMPLING_INCLUDED
#define PCSS_SAMPLING_INCLUDED

// -----------------------------------------------------------------------------
// Physical directional-light PCSS for a single cascade: blocker search ->
// angular-diameter penumbra estimate -> variable-radius PCF.
// -----------------------------------------------------------------------------

#include "PCSSCommon.hlsl"

// Cheap hardware-PCF-equivalent shadow — the analogue of RecaNoMaho's
// SAMPLE_TEXTURE2D_SHADOW else-branch for non-penumbra pixels. A 2x2 bilinear
// percentage-closer sample using the same IsBlocker compare, so it is
// reversed-Z-correct and continuous (anti-aliased) like a single hardware shadow
// tap. Sharp and correct in flat lit / umbra (incl. overlapping shadows), and
// cheap (4 point taps, no blocker search / penumbra estimate).
float SampleHardwareShadow(float3 positionWS, uint cascadeIndex, float texel)
{
    float worldPerDepth, worldPerUV;
    float3 sc = WorldToShadowCoordEx(positionWS, cascadeIndex, worldPerDepth, worldPerUV);

    if (sc.z <= 0.0 || sc.z >= 1.0 || sc.x < 0.0 || sc.x > 1.0 || sc.y < 0.0 || sc.y > 1.0)
        return 1.0;

    float lDirY = abs(_PCSS_LightDirection.y);
    float tanElev = max(lDirY / sqrt(max(1.0 - lDirY * lDirY, 1e-4)), 1e-3);
    float bias = ConeDepthBias(texel, worldPerUV, worldPerDepth, tanElev);

    // Manual 2x2 bilinear PCF (matches hardware comparison-sampler filtering).
    float2 uvT = sc.xy / texel - 0.5;
    float2 baseUV = (floor(uvT) + 0.5) * texel;
    float2 f = frac(uvT);

    float s00 = _MainLightShadowmapTexture.SampleLevel(sampler_PointClamp, baseUV, 0);
    float s10 = _MainLightShadowmapTexture.SampleLevel(sampler_PointClamp, baseUV + float2(texel, 0.0), 0);
    float s01 = _MainLightShadowmapTexture.SampleLevel(sampler_PointClamp, baseUV + float2(0.0, texel), 0);
    float s11 = _MainLightShadowmapTexture.SampleLevel(sampler_PointClamp, baseUV + float2(texel, texel), 0);

    float l00 = IsBlocker(s00, sc.z, bias) ? 0.0 : 1.0;
    float l10 = IsBlocker(s10, sc.z, bias) ? 0.0 : 1.0;
    float l01 = IsBlocker(s01, sc.z, bias) ? 0.0 : 1.0;
    float l11 = IsBlocker(s11, sc.z, bias) ? 0.0 : 1.0;

    return lerp(lerp(l00, l10, f.x), lerp(l01, l11, f.x), f.y);
}

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

    // Light grazing angle, shared by the cone-based per-sample Z-offset below.
    float lDirY = abs(_PCSS_LightDirection.y);
    float tanElev = max(lDirY / sqrt(max(1.0 - lDirY * lDirY, 1e-4)), 1e-3);

    // Blocker search (Fibonacci spiral, configurable tap count) with a cone-based
    // per-sample Z-offset: the bias grows with each sample's radius, so same-
    // surface samples are not false blockers (self-shadow / acne removal).
    uint sampleCount = (uint)max(_PCSS_SampleCount, 1);
    float blockerSum = 0.0;
    int blockerCount = 0;
    [loop]
    for (uint i = 0; i < sampleCount; i++)
    {
        float sampleDistNorm;
        float2 disk = SampleDiskFibonacci(i, sampleCount, angle, sampleDistNorm);
        float2 offset = disk * searchRadius;
        float bias = ConeDepthBias(searchRadius * sampleDistNorm, worldPerUV, worldPerDepth, tanElev);
        float s = _MainLightShadowmapTexture.SampleLevel(sampler_PointClamp, shadowUV + offset, 0);
        if (IsBlocker(s, receiverZ, bias))
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

    // Variable-radius PCF (Fibonacci spiral) with the same cone-based Z-offset.
    float litSum = 0.0;
    [loop]
    for (uint j = 0; j < sampleCount; j++)
    {
        float sampleDistNorm;
        float2 disk = SampleDiskFibonacci(j, sampleCount, angle, sampleDistNorm);
        float2 offset = disk * filterRadius;
        float bias = ConeDepthBias(filterRadius * sampleDistNorm, worldPerUV, worldPerDepth, tanElev);
        float s = _MainLightShadowmapTexture.SampleLevel(sampler_PointClamp, shadowUV + offset, 0);
        litSum += IsBlocker(s, receiverZ, bias) ? 0.0 : 1.0;
    }

    return litSum / (float)sampleCount;
}

#endif // PCSS_SAMPLING_INCLUDED
