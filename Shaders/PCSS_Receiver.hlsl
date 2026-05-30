#ifndef CUSTOM_PCSS_RECEIVER_INCLUDED
#define CUSTOM_PCSS_RECEIVER_INCLUDED

// =============================================================================
// PCSS_Receiver.hlsl
// レシーバー(背景/キャラクター)側で、AdvancedPCSSFeature が生成した
// スクリーン空間シャドウマスク _CustomScreenSpaceShadowmap を読み取り、
// アニメ調のRampグラデーション色を合成するための Shader Graph Custom Function。
//
//  使い方 (Shader Graph):
//    1) Custom Function ノードを追加し、Type=File, Source=このファイルを指定。
//    2) "SampleCustomShadow_float" : Screen Position(Default) を ScreenUV に接続し、
//       0(影)〜1(光) のマスクを取得する。
//    3) "ApplyAnimeRamp_float"     : 取得したマスクと Ramp テクスチャを接続し、
//       影のキワに色を付ける。
// =============================================================================

// AdvancedPCSSFeature が SetGlobalTextureAfterPass でグローバル設定するマスク。
TEXTURE2D(_CustomScreenSpaceShadowmap);
SAMPLER(sampler_CustomScreenSpaceShadowmap);

// -----------------------------------------------------------------------------
// マスク取得: スクリーンUVから 0(影)〜1(光) のシャドウマスクを読む。
//   ScreenUV : Screen Position ノード(Default)の xy
// -----------------------------------------------------------------------------
void SampleCustomShadow_float(float2 ScreenUV, out float ShadowMask)
{
#ifdef SHADERGRAPH_PREVIEW
    ShadowMask = 1.0;
#else
    ShadowMask = SAMPLE_TEXTURE2D_LOD(_CustomScreenSpaceShadowmap,
                                      sampler_CustomScreenSpaceShadowmap, ScreenUV, 0).r;
#endif
}

// -----------------------------------------------------------------------------
// アニメ調Ramp合成:
//   ShadowMask(0〜1) を U座標として Ramp テクスチャをサンプリングし、
//   半影エリア(0より大きく1未満)に好みのグラデーション色を割り当てる。
//   ・U=0   : 完全な影の色
//   ・U=1   : 完全な光(通常は白=影なし)
// -----------------------------------------------------------------------------
void ApplyAnimeRamp_float(float ShadowMask, UnityTexture2D RampTex, UnitySamplerState RampSS, out float3 ShadowColor)
{
#ifdef SHADERGRAPH_PREVIEW
    ShadowColor = float3(1.0, 1.0, 1.0);
#else
    ShadowColor = SAMPLE_TEXTURE2D(RampTex, RampSS, float2(saturate(ShadowMask), 0.5)).rgb;
#endif
}

// -----------------------------------------------------------------------------
// 一括版: マスク取得 + Ramp合成 + ベースカラー乗算 をまとめて行う。
//   diffuseColor : アルベド * ライトカラー など
//   戻り値       : 影色を合成した最終カラー
// -----------------------------------------------------------------------------
void PCSS_ShadeWithRamp_float(float2 ScreenUV, float3 BaseColor, UnityTexture2D RampTex, UnitySamplerState RampSS,
                              out float ShadowMask, out float3 FinalColor)
{
#ifdef SHADERGRAPH_PREVIEW
    ShadowMask = 1.0;
    FinalColor = BaseColor;
#else
    ShadowMask = SAMPLE_TEXTURE2D_LOD(_CustomScreenSpaceShadowmap,
                                      sampler_CustomScreenSpaceShadowmap, ScreenUV, 0).r;
    float3 rampColor = SAMPLE_TEXTURE2D(RampTex, RampSS, float2(saturate(ShadowMask), 0.5)).rgb;
    FinalColor = BaseColor * rampColor;
#endif
}

// -----------------------------------------------------------------------------
// アニメ調Ramp + 光のにじみ (Light Bleed):
//   明暗の境界(半影)に、光が影側へ「侵入」するような発光バンドを加える。
//   原神/チェンソーマン的な color1(光側) / color2(影側) + 境界の高輝度ふち を再現。
//
//   BaseColor      : アルベド×ライトカラー等のベース
//   RampTex/RampSS : 0(影)〜1(光) を U とする横方向Rampテクスチャ
//   BleedColor     : にじみ(発光)の色。光の透過色など
//   BleedPosition  : にじみ帯の中心 (0〜1のマスク値基準。境界=おおむね0.5)
//   BleedWidth     : にじみ帯の幅
//   BleedIntensity : にじみの強さ(加算量)
// -----------------------------------------------------------------------------
void PCSS_ShadeAnimeRamp_float(float2 ScreenUV, float3 BaseColor,
                               UnityTexture2D RampTex, UnitySamplerState RampSS,
                               float3 BleedColor, float BleedPosition, float BleedWidth, float BleedIntensity,
                               out float ShadowMask, out float3 RampColor, out float3 FinalColor)
{
#ifdef SHADERGRAPH_PREVIEW
    ShadowMask = 1.0;
    RampColor  = float3(1.0, 1.0, 1.0);
    FinalColor = BaseColor;
#else
    float mask = SAMPLE_TEXTURE2D_LOD(_CustomScreenSpaceShadowmap,
                                      sampler_CustomScreenSpaceShadowmap, ScreenUV, 0).r;
    ShadowMask = mask;

    // Ramp によるアニメ調の2色グラデーション (U=0:影色 / U=1:光)
    RampColor = SAMPLE_TEXTURE2D(RampTex, RampSS, float2(saturate(mask), 0.5)).rgb;
    float3 baseShaded = BaseColor * RampColor;

    // 光のにじみ: 境界付近にピークを持つ滑らかな帯 (正反 smoothstep の積)
    float w = max(BleedWidth, 1e-3);
    float band = smoothstep(BleedPosition - w, BleedPosition, mask) *
                 (1.0 - smoothstep(BleedPosition, BleedPosition + w, mask));

    // 加算で「光が影へにじむ」発光を表現
    FinalColor = baseShaded + BleedColor * (band * BleedIntensity);
#endif
}

#endif // CUSTOM_PCSS_RECEIVER_INCLUDED
