using UnityEngine;
using UnityEngine.Rendering;
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
    //
    // An optional debug pass blits a chosen intermediate buffer to the screen
    // (Scene/Game view) for inspection; see PCSSSettings.debugMode.
    public class PCSSFeature : ScriptableRendererFeature
    {
        public PCSSSettings settings = new PCSSSettings();

        private PCSSRenderPass m_Pass;
        private PCSSDebugPass m_DebugPass;
        private Material m_DebugMaterial;
        private bool m_DebugShaderWarned;

        public override void Create()
        {
            m_Pass = new PCSSRenderPass(settings);
            m_Pass.renderPassEvent = settings.renderPassEvent;
            m_DebugPass = new PCSSDebugPass();
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

            if (settings.debugMode == PCSSDebugMode.None)
                return;

            if (settings.debugShader == null)
            {
                if (!m_DebugShaderWarned)
                {
                    Debug.LogWarning("[PCSS] Debug Mode is set but Debug Shader is unassigned " +
                                     "(assign Shaders/PCSSDebug.shader). Skipping debug view.");
                    m_DebugShaderWarned = true;
                }

                return;
            }

            m_DebugShaderWarned = false;

            if (m_DebugMaterial == null || m_DebugMaterial.shader != settings.debugShader)
            {
                CoreUtils.Destroy(m_DebugMaterial);
                m_DebugMaterial = CoreUtils.CreateEngineMaterial(settings.debugShader);
            }

            m_DebugPass.Setup(m_DebugMaterial, settings.debugMode);
            m_DebugPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
            renderer.EnqueuePass(m_DebugPass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass?.Dispose();
            CoreUtils.Destroy(m_DebugMaterial);
            m_DebugMaterial = null;
            base.Dispose(disposing);
        }
    }
}
