using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace PCSS.Runtime
{
    [DisallowMultipleRendererFeature("PCSS (Screen Space)")]
    [Tooltip("PCSS (Screen Space)")]
    internal class PCSSRenderPass : ScriptableRenderPass
    {
        private readonly PCSSSettings m_Settings;

        // Imported RTHandle for the optional external blue-noise texture.
        private RTHandle m_BlueNoiseRT;
        private Texture m_BlueNoiseSource;

        public PCSSRenderPass(PCSSSettings settings)
        {
            m_Settings = settings;
            profilingSampler = new ProfilingSampler("PCSS (Screen Space)");
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
            public int kernelRecon;
            public int kernelBlur;
            public int kernelPCSS;
            public int kernelDenoise;
            public bool blurMask;
            public bool denoise;

            public TextureHandle cameraDepth;
            public TextureHandle shadowmap;
            public TextureHandle mask;
            public TextureHandle maskBlur;
            public TextureHandle finalResult;
            public TextureHandle shadowDenoised;
            public TextureHandle blueNoise;

            public int width;
            public int height;
            public int maskWidth;
            public int maskHeight;

            public Matrix4x4 invViewProj;
            public Vector4 cameraPosWS;
            public Vector4 textureSize;
            public Vector4 maskSize;
            public float shadowTexelSize;
            public int cascadeCount;
            public float cascadeBorder;
            public int reversedZ;

            public float blockerSearchRadius;
            public float sunAngularDiameter;
            public float penumbraScale;
            public float depthBias;
            public float maxFilterRadius;
            public float farSoftness;
            public Vector4 lightDirection;
            public float slopeBiasScale;
            public int stabilizeSampling;
            public int sampleCount;
            public float denoiseDepthSigma;

            public Vector4 blueNoiseSize;
            public int useBlueNoise;
            public int jitterIndex;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();
            var shadowData = frameData.Get<UniversalShadowData>();
            var lightData = frameData.Get<UniversalLightData>();

            // Nothing to do without main-light shadows and a camera depth texture.
            if (!shadowData.supportsMainLightShadows ||
                !resourceData.mainShadowsTexture.IsValid() ||
                !resourceData.cameraDepthTexture.IsValid())
            {
                return;
            }

            // Direction towards the light, used by the slope (receiver-plane) bias.
            Vector3 lightDir = Vector3.up;
            if (lightData.mainLightIndex >= 0 && lightData.mainLightIndex < lightData.visibleLights.Length)
            {
                Vector4 fwd = lightData.visibleLights[lightData.mainLightIndex].localToWorldMatrix.GetColumn(2);
                Vector3 d = -((Vector3)fwd);
                if (d.sqrMagnitude > 1e-6f)
                    lightDir = d.normalized;
            }

            int width = cameraData.cameraTargetDescriptor.width;
            int height = cameraData.cameraTargetDescriptor.height;
            if (width <= 0 || height <= 0)
                return;

            int maskDiv = Mathf.Max(1, (int)m_Settings.maskResolution);
            int maskWidth = Mathf.Max(1, Mathf.CeilToInt(width / (float)maskDiv));
            int maskHeight = Mathf.Max(1, Mathf.CeilToInt(height / (float)maskDiv));
            bool blurMask = m_Settings.blurMask;
            bool denoise = m_Settings.denoise;

            var finalDesc = new TextureDesc(width, height)
            {
                format = GraphicsFormat.R8_UNorm,
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                clearBuffer = false,
                name = "_PCSS_ScreenSpaceShadow"
            };
            TextureHandle finalResult = renderGraph.CreateTexture(finalDesc);

            // Optional denoised copy. When on, the raw PCSS goes to finalResult and
            // CSDenoise writes the cleaned result that URP actually samples.
            TextureHandle shadowDenoised = default;
            if (denoise)
            {
                finalDesc.name = "_PCSS_ScreenSpaceShadowDenoised";
                shadowDenoised = renderGraph.CreateTexture(finalDesc);
            }
            TextureHandle ssShadowOutput = denoise ? shadowDenoised : finalResult;

            // Reconnaissance mask (resolution from settings). Bilinear so the
            // full-res pass's interpolated read fattens penumbra boundaries for free.
            var maskDesc = new TextureDesc(maskWidth, maskHeight)
            {
                format = GraphicsFormat.R8_UNorm,
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                clearBuffer = false,
                name = "_PCSS_ReconMask"
            };
            TextureHandle mask = renderGraph.CreateTexture(maskDesc);

            // Optional blurred copy of the recon mask (smooths the penumbra gate).
            TextureHandle maskBlur = default;
            if (blurMask)
            {
                maskDesc.name = "_PCSS_ReconMaskBlur";
                maskBlur = renderGraph.CreateTexture(maskDesc);
            }

            // Publish handles so the optional debug pass (recorded at a later event)
            // can read them within this frame's graph.
            var pcssResources = frameData.GetOrCreate<PCSSResources>();
            pcssResources.mask = mask;
            pcssResources.shadow = ssShadowOutput;

            // RenderGraph path cannot use the public GetGPUProjectionMatrix(), so
            // build the GPU projection here. renderIntoTexture:true pairs with the
            // shader's clip-space Y flip.
            Matrix4x4 view = cameraData.GetViewMatrix();
            Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true);
            Matrix4x4 invVP = (gpuProj * view).inverse;

            // The shader texture slot must always be bound; fall back to black.
            bool wantBlueNoise = m_Settings.useBlueNoise && m_Settings.blueNoiseTexture != null;
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
                passData.cs = m_Settings.computeShader;
                passData.kernelRecon = m_Settings.computeShader.FindKernel(PCSSShaderIDs.KernelReconnaissance);
                passData.kernelBlur = m_Settings.computeShader.FindKernel(PCSSShaderIDs.KernelBlurMask);
                passData.kernelPCSS = m_Settings.computeShader.FindKernel(PCSSShaderIDs.KernelMainPCSS);
                passData.kernelDenoise = m_Settings.computeShader.FindKernel(PCSSShaderIDs.KernelDenoise);
                passData.blurMask = blurMask;
                passData.denoise = denoise;

                passData.cameraDepth = resourceData.cameraDepthTexture;
                passData.shadowmap = resourceData.mainShadowsTexture;
                passData.mask = mask;
                passData.maskBlur = maskBlur;
                passData.finalResult = finalResult;
                passData.shadowDenoised = shadowDenoised;
                passData.blueNoise = blueNoiseHandle;

                passData.width = width;
                passData.height = height;
                passData.maskWidth = maskWidth;
                passData.maskHeight = maskHeight;

                passData.invViewProj = invVP;
                passData.cameraPosWS = cameraData.camera.transform.position;
                passData.textureSize = new Vector4(width, height, 1.0f / width, 1.0f / height);
                passData.maskSize = new Vector4(maskWidth, maskHeight, 1.0f / maskWidth, 1.0f / maskHeight);
                passData.shadowTexelSize = 1.0f / Mathf.Max(1, shadowData.mainLightShadowmapWidth);
                passData.cascadeCount = shadowData.mainLightShadowCascadesCount;
                passData.cascadeBorder = m_Settings.cascadeBlend;
                passData.reversedZ = SystemInfo.usesReversedZBuffer ? 1 : 0;

                passData.blockerSearchRadius = m_Settings.blockerSearchRadius;
                passData.sunAngularDiameter = m_Settings.sunAngularDiameter;
                passData.penumbraScale = m_Settings.penumbraScale;
                passData.depthBias = m_Settings.depthBias;
                passData.maxFilterRadius = m_Settings.maxFilterRadius;
                passData.farSoftness = m_Settings.farSoftness;
                passData.lightDirection = lightDir;
                passData.slopeBiasScale = m_Settings.slopeBias;
                passData.stabilizeSampling = m_Settings.stabilizeSampling ? 1 : 0;
                passData.sampleCount = Mathf.Max(1, m_Settings.sampleCount);
                passData.denoiseDepthSigma = m_Settings.denoiseDepthSensitivity;

                passData.blueNoiseSize = new Vector4(noiseSource.width, noiseSource.height, 0, 0);
                passData.useBlueNoise = wantBlueNoise ? 1 : 0;
                passData.jitterIndex = m_Settings.useTemporalJitter ? (Time.frameCount & 15) : 0;

                builder.UseTexture(passData.cameraDepth, AccessFlags.Read);
                builder.UseTexture(passData.shadowmap, AccessFlags.Read);
                builder.UseTexture(passData.blueNoise, AccessFlags.Read);
                builder.UseTexture(passData.mask, AccessFlags.ReadWrite); // kernel 0 writes -> blur/kernel 1 reads
                if (blurMask)
                    builder.UseTexture(passData.maskBlur, AccessFlags.ReadWrite); // blur writes -> kernel 1 reads
                // Raw PCSS: written by kernel 1, and (when denoising) read by CSDenoise.
                builder.UseTexture(passData.finalResult, denoise ? AccessFlags.ReadWrite : AccessFlags.Write);
                if (denoise)
                    builder.UseTexture(passData.shadowDenoised, AccessFlags.Write); // denoise output

                // The compute shader reads URP's per-frame global shadow vars.
                builder.AllowGlobalStateModification(true);
                builder.UseAllGlobalTextures(true);

                // The consumer is a material outside the graph; never cull this pass.
                builder.AllowPassCulling(false);

                // Bind the (denoised) PCSS result to URP's screen-space shadow slot
                // so all standard materials read it through _MAIN_LIGHT_SHADOWS_SCREEN.
                builder.SetGlobalTextureAfterPass(ssShadowOutput, PCSSShaderIDs.UrpScreenSpaceShadowmap);

                builder.SetRenderFunc((PassData data, ComputeGraphContext ctx) => ExecutePass(data, ctx));
            }

            // Set the keyword trio via a command-buffer pass (not Shader.EnableKeyword
            // which is CPU-immediate and bypasses render graph ordering). The
            // UseTexture(finalResult) dependency forces this pass after the compute
            // pass. PCSSScreenSpaceShadowPostPass restores the atlas keywords before
            // transparents render.
            using (var kwBuilder = renderGraph.AddUnsafePass<KeywordPassData>(
                       "PCSS Enable SS Shadow Keywords", out _, profilingSampler))
            {
                kwBuilder.UseTexture(ssShadowOutput, AccessFlags.Read);
                kwBuilder.AllowGlobalStateModification(true);
                kwBuilder.AllowPassCulling(false);
                kwBuilder.SetRenderFunc((KeywordPassData _, UnsafeGraphContext ctx) =>
                {
                    ctx.cmd.SetKeyword(PCSSShaderIDs.KwScreenSpaceShadows, true);
                    ctx.cmd.SetKeyword(PCSSShaderIDs.KwMainLightShadows, false);
                    ctx.cmd.SetKeyword(PCSSShaderIDs.KwMainLightCascades, false);
                });
            }
        }

        private class KeywordPassData { }

        private static void ExecutePass(PassData data, ComputeGraphContext ctx)
        {
            var cmd = ctx.cmd;
            var cs = data.cs;

            cmd.SetComputeMatrixParam(cs, PCSSShaderIDs.InvViewProj, data.invViewProj);
            cmd.SetComputeVectorParam(cs, PCSSShaderIDs.CameraPosWS, data.cameraPosWS);
            cmd.SetComputeVectorParam(cs, PCSSShaderIDs.TextureSize, data.textureSize);
            cmd.SetComputeVectorParam(cs, PCSSShaderIDs.MaskSize, data.maskSize);
            cmd.SetComputeFloatParam(cs, PCSSShaderIDs.ShadowTexelSize, data.shadowTexelSize);
            cmd.SetComputeIntParam(cs, PCSSShaderIDs.CascadeCount, data.cascadeCount);
            cmd.SetComputeFloatParam(cs, PCSSShaderIDs.CascadeBorder, data.cascadeBorder);
            cmd.SetComputeIntParam(cs, PCSSShaderIDs.ReversedZ, data.reversedZ);
            cmd.SetComputeFloatParam(cs, PCSSShaderIDs.BlockerSearchRadius, data.blockerSearchRadius);
            cmd.SetComputeFloatParam(cs, PCSSShaderIDs.SunAngularDiameter, data.sunAngularDiameter);
            cmd.SetComputeFloatParam(cs, PCSSShaderIDs.PenumbraScale, data.penumbraScale);
            cmd.SetComputeFloatParam(cs, PCSSShaderIDs.DepthBias, data.depthBias);
            cmd.SetComputeFloatParam(cs, PCSSShaderIDs.MaxFilterRadius, data.maxFilterRadius);
            cmd.SetComputeFloatParam(cs, PCSSShaderIDs.FarSoftness, data.farSoftness);
            cmd.SetComputeVectorParam(cs, PCSSShaderIDs.LightDirection, data.lightDirection);
            cmd.SetComputeFloatParam(cs, PCSSShaderIDs.SlopeBiasScale, data.slopeBiasScale);
            cmd.SetComputeIntParam(cs, PCSSShaderIDs.Stabilize, data.stabilizeSampling);
            cmd.SetComputeVectorParam(cs, PCSSShaderIDs.BlueNoiseSize, data.blueNoiseSize);
            cmd.SetComputeIntParam(cs, PCSSShaderIDs.UseBlueNoise, data.useBlueNoise);
            cmd.SetComputeIntParam(cs, PCSSShaderIDs.JitterIndex, data.jitterIndex);
            cmd.SetComputeIntParam(cs, PCSSShaderIDs.SampleCount, data.sampleCount);
            cmd.SetComputeFloatParam(cs, PCSSShaderIDs.DenoiseDepthSigma, data.denoiseDepthSigma);

            int maskGroupsX = Mathf.CeilToInt(data.maskWidth / 8.0f);
            int maskGroupsY = Mathf.CeilToInt(data.maskHeight / 8.0f);

            // Kernel 0: reconnaissance (mask res) -> recon mask.
            int k0 = data.kernelRecon;
            cmd.SetComputeTextureParam(cs, k0, PCSSShaderIDs.CameraDepth, data.cameraDepth);
            cmd.SetComputeTextureParam(cs, k0, PCSSShaderIDs.ShadowmapTex, data.shadowmap);
            cmd.SetComputeTextureParam(cs, k0, PCSSShaderIDs.MaskResult, data.mask);
            cmd.DispatchCompute(cs, k0, maskGroupsX, maskGroupsY, 1);

            // Optional blur kernel (mask res): recon mask -> blurred mask.
            // Reuses the MaskTex (SRV) / MaskResult (UAV) slots, rebound per dispatch.
            TextureHandle k1MaskInput = data.mask;
            if (data.blurMask)
            {
                int kb = data.kernelBlur;
                cmd.SetComputeTextureParam(cs, kb, PCSSShaderIDs.MaskTex, data.mask);
                cmd.SetComputeTextureParam(cs, kb, PCSSShaderIDs.MaskResult, data.maskBlur);
                cmd.DispatchCompute(cs, kb, maskGroupsX, maskGroupsY, 1);
                k1MaskInput = data.maskBlur;
            }

            // Kernel 1: physical PCSS (full res). Reads the (blurred) mask.
            int fullGroupsX = Mathf.CeilToInt(data.width / 8.0f);
            int fullGroupsY = Mathf.CeilToInt(data.height / 8.0f);
            int k1 = data.kernelPCSS;
            cmd.SetComputeTextureParam(cs, k1, PCSSShaderIDs.CameraDepth, data.cameraDepth);
            cmd.SetComputeTextureParam(cs, k1, PCSSShaderIDs.ShadowmapTex, data.shadowmap);
            cmd.SetComputeTextureParam(cs, k1, PCSSShaderIDs.BlueNoiseTex, data.blueNoise);
            cmd.SetComputeTextureParam(cs, k1, PCSSShaderIDs.MaskTex, k1MaskInput);
            cmd.SetComputeTextureParam(cs, k1, PCSSShaderIDs.SSResult, data.finalResult);
            cmd.DispatchCompute(cs, k1, fullGroupsX, fullGroupsY, 1);

            // Optional denoise (full res): raw PCSS -> depth-guided, penumbra-only blur.
            // Reuses the SSResult (UAV) slot, rebound to the denoised target.
            if (data.denoise)
            {
                int kd = data.kernelDenoise;
                cmd.SetComputeTextureParam(cs, kd, PCSSShaderIDs.CameraDepth, data.cameraDepth);
                cmd.SetComputeTextureParam(cs, kd, PCSSShaderIDs.MaskTex, k1MaskInput);
                cmd.SetComputeTextureParam(cs, kd, PCSSShaderIDs.ShadowRawTex, data.finalResult);
                cmd.SetComputeTextureParam(cs, kd, PCSSShaderIDs.SSResult, data.shadowDenoised);
                cmd.DispatchCompute(cs, kd, fullGroupsX, fullGroupsY, 1);
            }
        }
    }
}
