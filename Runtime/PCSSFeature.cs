using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace PCSS.Runtime
{
    // Non-invasive, physically based PCSS screen-space shadows for the main
    // directional light. RenderGraph only (Unity 6.x / URP 17.x).
    //
    // Pipeline:
    //   1) Reconnaissance (1/4 res): classify lit / shadow / penumbra into a mask.
    //   2) Main PCSS (full res): run physical PCSS only on penumbra pixels.
    // No spatial blur; temporal jitter + the game's TAA resolve the noise.
    //
    // The output stays named _CustomScreenSpaceShadowmap so existing receivers
    // (PCSS_Receiver.hlsl / Shader Graph) keep working unchanged.
    public class PCSSFeature : ScriptableRendererFeature
    {
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

            var cameraType = renderingData.cameraData.cameraType;
            if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection)
                return;

            m_Pass.renderPassEvent = settings.renderPassEvent;
            m_Pass.ConfigureInput(ScriptableRenderPassInput.Depth);
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass?.Dispose();
            base.Dispose(disposing);
        }
    }
}
