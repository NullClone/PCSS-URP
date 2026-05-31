using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace PCSS.Runtime
{
    // Mirrors URP's ScreenSpaceShadowsPostPass exactly.
    //
    // Runs at BeforeRenderingTransparents and restores the atlas keyword state
    // (_MAIN_LIGHT_SHADOWS / _CASCADE) so transparent objects fall back to URP's
    // standard shadow atlas, matching the behaviour of URP's built-in feature.
    internal class PCSSScreenSpaceShadowPostPass : ScriptableRenderPass
    {
        public PCSSScreenSpaceShadowPostPass()
        {
            profilingSampler = new ProfilingSampler("PCSS Restore Shadow Keywords");
        }

        private class PassData
        {
            internal UniversalShadowData shadowData;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();

            // AddRasterRenderPass matches URP's ScreenSpaceShadowsPostPass.
            using (var builder = renderGraph.AddRasterRenderPass<PassData>(
                       "PCSS Restore Shadow Keywords", out var passData, profilingSampler))
            {
                // Attach to the active colour target (same as URP's PostPass).
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);

                passData.shadowData = frameData.Get<UniversalShadowData>();

                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                    ExecutePass(ctx.cmd, data.shadowData));
            }
        }

        // Mirrors ScreenSpaceShadowsPostPass.ExecutePass() line-for-line.
        private static void ExecutePass(RasterCommandBuffer cmd, UniversalShadowData shadowData)
        {
            int cascadesCount = shadowData.mainLightShadowCascadesCount;
            bool mainLightShadows = shadowData.supportsMainLightShadows;
            bool receiveShadowsNoCascade = mainLightShadows && cascadesCount == 1;
            bool receiveShadowsCascades = mainLightShadows && cascadesCount > 1;

            // Disable screen-space path before transparents.
            cmd.SetKeyword(PCSSShaderIDs.KwScreenSpaceShadows, false);

            // Re-enable whichever atlas keyword URP originally set.
            cmd.SetKeyword(PCSSShaderIDs.KwMainLightShadows, receiveShadowsNoCascade);
            cmd.SetKeyword(PCSSShaderIDs.KwMainLightCascades, receiveShadowsCascades);
        }
    }
}
