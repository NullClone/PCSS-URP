// =============================================================================
// AdvancedPCSSFeature.cs
// URP向け 非破壊・高品質PCSS スクリーンスペースシャドウ RenderFeature (RenderGraph)
//
//  ・URPコアは一切改変しない (ノンインバシブ)。
//  ・URPがグローバル設定するシャドウ変数を Compute Shader 側で読み取り、
//    スクリーン空間シャドウマスク _CustomScreenSpaceShadowmap を生成する。
//  ・blockerSearchRadius (遮蔽検索:小) と pcfFilterRadius (ぼかし:大) を
//    インスペクタから独立して設定可能。
//
//  対象: Unity 6.x / URP 17.x (RenderGraph). Compatibility Mode は非対応。
// =============================================================================

using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class AdvancedPCSSFeature : ScriptableRendererFeature
{
    [Serializable]
    public class PCSSSettings
    {
        [Tooltip("PCSS計算を行うComputeShader (GenerateSSShadowmap.compute)")]
        public ComputeShader computeShader;

        [Tooltip("パスの実行タイミング。深度テクスチャが必要なため PrePass 後を推奨。")]
        public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;

        [Header("PCSS Parameters")]
        [Tooltip("遮蔽物検索の半径(テクセル単位)。【ソフト境界の幅を決める最重要値】。" +
                 "大きいほど影が外へ広く滲み、端のグラデーションが滑らかになる。小さいと接地部はカッチリするが端が急になる。")]
        [Range(0.5f, 64.0f)] public float blockerSearchRadius = 4.0f;

        [Tooltip("ぼかし(PCF)半径の乗数。大きいほど検出済みの帯の内側が柔らかくなる。")]
        [Range(1.0f, 128.0f)] public float pcfFilterRadius = 24.0f;

        [Tooltip("ライトの大きさ。半影の広がりの強さ。")]
        [Range(0.1f, 20.0f)] public float lightSize = 2.0f;

        [Tooltip("自遮蔽(縞模様)対策の基本深度バイアス。")]
        [Range(0.0f, 0.01f)] public float depthBias = 0.001f;

        [Tooltip("傾斜(receiver-plane)バイアス係数。探索半径×1/tan(光の仰角)に比例して深度バイアスを増やし、" +
                 "大きなフィルタ時の縞模様・突変エッジを防ぐ。光が低い(夕方)ほど自動で強くなる。0で無効。")]
        [Range(0.0f, 5.0f)] public float slopeBias = 1.0f;

        [Tooltip("フィルタ半径の最大値(アトラスUV単位)。遠方の影のボケ上限。大きいほど遠い影が更にボケる。")]
        [Range(0.001f, 0.3f)] public float maxFilterRadius = 0.1f;

        [Tooltip("遠くに伸びた影(先端)ほど追加でボケを強調する。0=物理線形のまま、大きいほど先端が強くボケる。接地部のシャープさは維持される。")]
        [Range(0.0f, 100.0f)] public float farSoftness = 12.0f;

        [Tooltip("カスケードの変わり目を滑らかにブレンドする幅(各カスケード球の外側何割で隣へ移行するか)。" +
                 "0=ハード切替(継ぎ目が出る)、0.2〜0.3 推奨。大きいほど境界が滑らかだが境界付近で2回計算しコスト増。")]
        [Range(0.0f, 0.5f)] public float cascadeBlend = 0.25f;

        [Header("Bilateral Filter (ノイズ除去)")]
        [Tooltip("バイラテラルフィルタを有効化する。")]
        public bool enableBilateral = true;

        [Tooltip("距離重み係数。大きいほど段差でシャープ(根元の硬さ維持)。")]
        [Range(1.0f, 200.0f)] public float bilateralSharpness = 40.0f;

        [Header("Blue Noise (サンプリング品質)")]
        [Tooltip("ブルーノイズで回転角をディザし、少サンプルでもバンディング/ノイズを低減する。" +
                 "OFFまたはテクスチャ未設定時は IGN にフォールバック。")]
        public bool useBlueNoise = true;

        [Tooltip("タイリング可能なブルーノイズ(できればSTBN)。グレースケール推奨。R成分を回転角に使用。" +
                 "未設定の場合は自動的にIGNを使う。")]
        public Texture2D blueNoiseTexture;
    }

    public PCSSSettings settings = new PCSSSettings();

    private PCSSRenderPass m_Pass;

    public override void Create()
    {
        m_Pass = new PCSSRenderPass(settings);
        m_Pass.renderPassEvent = settings.renderPassEvent;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings.computeShader == null)
            return;

        // ゲーム/シーンビューのみ。プレビュー等はスキップ。
        var cameraType = renderingData.cameraData.cameraType;
        if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection)
            return;

        m_Pass.renderPassEvent = settings.renderPassEvent;
        // スクリーン空間で深度を参照するため、深度テクスチャを要求する。
        m_Pass.ConfigureInput(ScriptableRenderPassInput.Depth);
        renderer.EnqueuePass(m_Pass);
    }

    protected override void Dispose(bool disposing)
    {
        m_Pass?.Dispose();
        base.Dispose(disposing);
    }

    // -------------------------------------------------------------------------
    // RenderPass
    // -------------------------------------------------------------------------
    private class PCSSRenderPass : ScriptableRenderPass
    {
        private readonly PCSSSettings m_Settings;

        // Shader property IDs
        static readonly int s_CameraDepth        = Shader.PropertyToID("_CameraDepthTexture");
        static readonly int s_ShadowmapTex       = Shader.PropertyToID("_MainLightShadowmapTexture");
        static readonly int s_RawResult          = Shader.PropertyToID("_RawShadowResult");
        static readonly int s_RawResultTex        = Shader.PropertyToID("_RawShadowResultTex");
        static readonly int s_SSResult           = Shader.PropertyToID("_SSShadowResult");

        static readonly int s_InvViewProj        = Shader.PropertyToID("_PCSS_InvViewProj");
        static readonly int s_CameraPosWS        = Shader.PropertyToID("_PCSS_CameraPosWS");
        static readonly int s_TextureSize        = Shader.PropertyToID("_PCSS_TextureSize");
        static readonly int s_ShadowTexelSize    = Shader.PropertyToID("_PCSS_ShadowTexelSize");
        static readonly int s_CascadeCount       = Shader.PropertyToID("_PCSS_CascadeCount");
        static readonly int s_CascadeBorder      = Shader.PropertyToID("_PCSS_CascadeBorder");
        static readonly int s_BlockerSearchRadius= Shader.PropertyToID("_PCSS_BlockerSearchRadius");
        static readonly int s_PCFFilterRadius    = Shader.PropertyToID("_PCSS_PCFFilterRadius");
        static readonly int s_LightSize          = Shader.PropertyToID("_PCSS_LightSize");
        static readonly int s_DepthBias          = Shader.PropertyToID("_PCSS_DepthBias");
        static readonly int s_MaxFilterRadius    = Shader.PropertyToID("_PCSS_MaxFilterRadius");
        static readonly int s_BilateralSharpness = Shader.PropertyToID("_PCSS_BilateralSharpness");
        static readonly int s_FarSoftness        = Shader.PropertyToID("_PCSS_FarSoftness");
        static readonly int s_LightDirection     = Shader.PropertyToID("_PCSS_LightDirection");
        static readonly int s_SlopeBiasScale     = Shader.PropertyToID("_PCSS_SlopeBiasScale");
        static readonly int s_BlueNoiseTex       = Shader.PropertyToID("_PCSS_BlueNoiseTex");
        static readonly int s_BlueNoiseSize      = Shader.PropertyToID("_PCSS_BlueNoiseSize");
        static readonly int s_UseBlueNoise       = Shader.PropertyToID("_PCSS_UseBlueNoise");
        static readonly int s_ReversedZ          = Shader.PropertyToID("_PCSS_ReversedZ");

        // ブルーノイズ(外部Texture)をRenderGraphにインポートするためのRTHandleキャッシュ
        private RTHandle m_BlueNoiseRT;
        private Texture m_BlueNoiseSource;

        // 出力グローバルテクスチャ名 (レシーバーが読む)
        static readonly int s_GlobalResult       = Shader.PropertyToID("_CustomScreenSpaceShadowmap");

        public PCSSRenderPass(PCSSSettings settings)
        {
            m_Settings = settings;
            profilingSampler = new ProfilingSampler("Advanced PCSS (Screen Space)");
        }

        public void Dispose()
        {
            m_BlueNoiseRT?.Release();
            m_BlueNoiseRT = null;
            m_BlueNoiseSource = null;
        }

        private class PassData
        {
            public ComputeShader cs;
            public int kernelPCSS;
            public int kernelBilateral;
            public bool useBilateral;

            public TextureHandle cameraDepth;
            public TextureHandle shadowmap;
            public TextureHandle rawResult;
            public TextureHandle finalResult;

            public int width;
            public int height;

            public Matrix4x4 invViewProj;
            public Vector4 cameraPosWS;
            public Vector4 textureSize;
            public float shadowTexelSize;
            public int cascadeCount;
            public float cascadeBorder;
            public int reversedZ;

            public float blockerSearchRadius;
            public float pcfFilterRadius;
            public float lightSize;
            public float depthBias;
            public float maxFilterRadius;
            public float bilateralSharpness;
            public float farSoftness;
            public Vector4 lightDirection;
            public float slopeBiasScale;
            public TextureHandle blueNoise;
            public Vector4 blueNoiseSize;
            public int useBlueNoise;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData   = frameData.Get<UniversalCameraData>();
            var shadowData   = frameData.Get<UniversalShadowData>();
            var lightData    = frameData.Get<UniversalLightData>();

            // メインライトシャドウ・深度が無ければ何もしない。
            if (!shadowData.supportsMainLightShadows ||
                !resourceData.mainShadowsTexture.IsValid() ||
                !resourceData.cameraDepthTexture.IsValid())
            {
                return;
            }

            // メインライト方向 (光源へ向かう向き)。傾斜バイアスの仰角に使用。
            Vector3 lightDir = Vector3.up;
            if (lightData.mainLightIndex >= 0 && lightData.mainLightIndex < lightData.visibleLights.Length)
            {
                Vector4 fwd = lightData.visibleLights[lightData.mainLightIndex].localToWorldMatrix.GetColumn(2);
                Vector3 d = -((Vector3)fwd);
                if (d.sqrMagnitude > 1e-6f)
                    lightDir = d.normalized;
            }

            int width  = cameraData.cameraTargetDescriptor.width;
            int height = cameraData.cameraTargetDescriptor.height;
            if (width <= 0 || height <= 0)
                return;

            // --- 出力テクスチャ (R8, RandomWrite) -----------------------------
            var finalDesc = new TextureDesc(width, height)
            {
                format = GraphicsFormat.R8_UNorm,
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                clearBuffer = false,
                name = "_CustomScreenSpaceShadowmap"
            };
            TextureHandle finalResult = renderGraph.CreateTexture(finalDesc);

            bool useBilateral = m_Settings.enableBilateral;
            TextureHandle rawResult = finalResult;
            if (useBilateral)
            {
                var rawDesc = finalDesc;
                rawDesc.name = "_PCSS_RawResult";
                rawResult = renderGraph.CreateTexture(rawDesc);
            }

            // --- 行列・パラメータ ---------------------------------------------
            // 非Compatibility(RenderGraph)モードでは public な GetGPUProjectionMatrix()
            // が使えないため、GL.GetGPUProjectionMatrix で自前にGPU射影を構築する。
            // URPはRenderTextureへ描画するため renderIntoTexture=true。
            // (シェーダ側の y 反転と対になる。)
            Matrix4x4 view = cameraData.GetViewMatrix();
            Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true);
            Matrix4x4 invVP = (gpuProj * view).inverse;

            // --- ブルーノイズ (外部Texture) を RenderGraph にインポート --------
            bool wantBlueNoise = m_Settings.useBlueNoise && m_Settings.blueNoiseTexture != null;
            // シェーダのテクスチャスロットは常にバインドが必要。未設定時は黒で埋める。
            Texture noiseSource = wantBlueNoise ? (Texture)m_Settings.blueNoiseTexture : Texture2D.blackTexture;
            if (m_BlueNoiseSource != noiseSource)
            {
                m_BlueNoiseRT?.Release();
                m_BlueNoiseRT = RTHandles.Alloc(noiseSource);
                m_BlueNoiseSource = noiseSource;
            }
            TextureHandle blueNoiseHandle = renderGraph.ImportTexture(m_BlueNoiseRT);

            using (var builder = renderGraph.AddComputePass<PassData>(passName, out var passData, profilingSampler))
            {
                passData.cs              = m_Settings.computeShader;
                passData.kernelPCSS      = m_Settings.computeShader.FindKernel("CSMainPCSS");
                passData.kernelBilateral = m_Settings.computeShader.FindKernel("CSBilateral");
                passData.useBilateral    = useBilateral;

                passData.cameraDepth = resourceData.cameraDepthTexture;
                passData.shadowmap   = resourceData.mainShadowsTexture;
                passData.rawResult   = rawResult;
                passData.finalResult = finalResult;

                passData.width  = width;
                passData.height = height;

                passData.invViewProj     = invVP;
                passData.cameraPosWS     = cameraData.camera.transform.position;
                passData.textureSize     = new Vector4(width, height, 1.0f / width, 1.0f / height);
                passData.shadowTexelSize = 1.0f / Mathf.Max(1, shadowData.mainLightShadowmapWidth);
                passData.cascadeCount    = shadowData.mainLightShadowCascadesCount;
                passData.cascadeBorder   = m_Settings.cascadeBlend;
                passData.reversedZ       = SystemInfo.usesReversedZBuffer ? 1 : 0;

                passData.blockerSearchRadius = m_Settings.blockerSearchRadius;
                passData.pcfFilterRadius     = m_Settings.pcfFilterRadius;
                passData.lightSize           = m_Settings.lightSize;
                passData.depthBias           = m_Settings.depthBias;
                passData.maxFilterRadius     = m_Settings.maxFilterRadius;
                passData.bilateralSharpness  = m_Settings.bilateralSharpness;
                passData.farSoftness         = m_Settings.farSoftness;
                passData.lightDirection      = lightDir;
                passData.slopeBiasScale      = m_Settings.slopeBias;
                passData.blueNoise           = blueNoiseHandle;
                passData.blueNoiseSize       = new Vector4(noiseSource.width, noiseSource.height, 0, 0);
                passData.useBlueNoise        = wantBlueNoise ? 1 : 0;

                // リソース宣言
                builder.UseTexture(passData.cameraDepth, AccessFlags.Read);
                builder.UseTexture(passData.shadowmap, AccessFlags.Read);
                builder.UseTexture(passData.blueNoise, AccessFlags.Read);
                if (useBilateral)
                {
                    builder.UseTexture(passData.rawResult, AccessFlags.ReadWrite);
                    builder.UseTexture(passData.finalResult, AccessFlags.Write);
                }
                else
                {
                    builder.UseTexture(passData.finalResult, AccessFlags.Write);
                }

                // URPがグローバル設定するシャドウ変数(_MainLightWorldToShadow等)を
                // ComputeShaderが読むため、グローバル状態の変更を許可する。
                builder.AllowGlobalStateModification(true);
                builder.UseAllGlobalTextures(true);

                // 結果はRenderGraph管理外のマテリアル(レシーバー)が読むため、
                // パスがカリングされないようにする。
                builder.AllowPassCulling(false);

                // 生成結果をグローバルテクスチャとして公開 (レシーバーが読む)。
                builder.SetGlobalTextureAfterPass(finalResult, s_GlobalResult);

                builder.SetRenderFunc((PassData data, ComputeGraphContext ctx) => ExecutePass(data, ctx));
            }
        }

        private static void ExecutePass(PassData data, ComputeGraphContext ctx)
        {
            var cmd = ctx.cmd;
            var cs  = data.cs;

            int groupsX = Mathf.CeilToInt(data.width  / 8.0f);
            int groupsY = Mathf.CeilToInt(data.height / 8.0f);

            // --- 共通定数 -----------------------------------------------------
            cmd.SetComputeMatrixParam(cs, s_InvViewProj, data.invViewProj);
            cmd.SetComputeVectorParam(cs, s_CameraPosWS, data.cameraPosWS);
            cmd.SetComputeVectorParam(cs, s_TextureSize, data.textureSize);
            cmd.SetComputeFloatParam (cs, s_ShadowTexelSize, data.shadowTexelSize);
            cmd.SetComputeIntParam   (cs, s_CascadeCount, data.cascadeCount);
            cmd.SetComputeFloatParam (cs, s_CascadeBorder, data.cascadeBorder);
            cmd.SetComputeIntParam   (cs, s_ReversedZ, data.reversedZ);
            cmd.SetComputeFloatParam (cs, s_BlockerSearchRadius, data.blockerSearchRadius);
            cmd.SetComputeFloatParam (cs, s_PCFFilterRadius, data.pcfFilterRadius);
            cmd.SetComputeFloatParam (cs, s_LightSize, data.lightSize);
            cmd.SetComputeFloatParam (cs, s_DepthBias, data.depthBias);
            cmd.SetComputeFloatParam (cs, s_MaxFilterRadius, data.maxFilterRadius);
            cmd.SetComputeFloatParam (cs, s_BilateralSharpness, data.bilateralSharpness);
            cmd.SetComputeFloatParam (cs, s_FarSoftness, data.farSoftness);
            cmd.SetComputeVectorParam(cs, s_LightDirection, data.lightDirection);
            cmd.SetComputeFloatParam (cs, s_SlopeBiasScale, data.slopeBiasScale);
            cmd.SetComputeVectorParam(cs, s_BlueNoiseSize, data.blueNoiseSize);
            cmd.SetComputeIntParam   (cs, s_UseBlueNoise, data.useBlueNoise);

            // --- Kernel 0 : PCSS ---------------------------------------------
            int k0 = data.kernelPCSS;
            cmd.SetComputeTextureParam(cs, k0, s_CameraDepth, data.cameraDepth);
            cmd.SetComputeTextureParam(cs, k0, s_ShadowmapTex, data.shadowmap);
            cmd.SetComputeTextureParam(cs, k0, s_BlueNoiseTex, data.blueNoise);
            cmd.SetComputeTextureParam(cs, k0, s_RawResult, data.rawResult); // bilateral無効時は finalResult
            cmd.DispatchCompute(cs, k0, groupsX, groupsY, 1);

            // --- Kernel 1 : Bilateral ----------------------------------------
            if (data.useBilateral)
            {
                int k1 = data.kernelBilateral;
                cmd.SetComputeTextureParam(cs, k1, s_CameraDepth, data.cameraDepth);
                cmd.SetComputeTextureParam(cs, k1, s_RawResultTex, data.rawResult);
                cmd.SetComputeTextureParam(cs, k1, s_SSResult, data.finalResult);
                cmd.DispatchCompute(cs, k1, groupsX, groupsY, 1);
            }
        }
    }
}
