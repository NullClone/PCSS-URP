using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace PCSS.Runtime
{
    // Frame-scoped handles shared from the compute pass (early event) to the
    // debug pass (late event) so the latter can blit them after scene rendering.
    internal class PCSSResources : ContextItem
    {
        public TextureHandle mask;
        public TextureHandle shadow;

        public override void Reset()
        {
            mask = TextureHandle.nullHandle;
            shadow = TextureHandle.nullHandle;
        }
    }

    // Full-screen debug overlay: blits the selected PCSS buffer to the active
    // color target. Runs after post-processing so it is not overwritten.
    internal class PCSSDebugPass : ScriptableRenderPass
    {
        private Material m_Material;
        private PCSSDebugMode m_Mode;

        public PCSSDebugPass()
        {
            profilingSampler = new ProfilingSampler("PCSS Debug View");
        }

        public void Setup(Material material, PCSSDebugMode mode)
        {
            m_Material = material;
            m_Mode = mode;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (m_Material == null || m_Mode == PCSSDebugMode.None)
                return;
            if (!frameData.Contains<PCSSResources>())
                return;

            var res = frameData.Get<PCSSResources>();
            var resourceData = frameData.Get<UniversalResourceData>();

            TextureHandle source = (m_Mode == PCSSDebugMode.ReconMask) ? res.mask : res.shadow;
            if (!source.IsValid() || !resourceData.activeColorTexture.IsValid())
                return;

            // Blitter binds _BlitTexture / _BlitScaleBias and draws a full-screen
            // triangle; the debug shader just outputs the .r channel as grayscale.
            var blitParams = new RenderGraphUtils.BlitMaterialParameters(
                source, resourceData.activeColorTexture, m_Material, 0);
            renderGraph.AddBlitPass(blitParams);
        }
    }
}
