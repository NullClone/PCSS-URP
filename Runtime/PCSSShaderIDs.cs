using UnityEngine;

namespace PCSS.Runtime
{
    // Single source of truth for the C# <-> compute-shader string contract.
    // Renaming any name here must be mirrored in GenerateSSShadowmap.compute.
    internal static class PCSSShaderIDs
    {
        // Kernel names.
        public const string KernelReconnaissance = "CSReconnaissance";
        public const string KernelMainPCSS = "CSMainPCSS";

        // Textures.
        public static readonly int CameraDepth = Shader.PropertyToID("_CameraDepthTexture");
        public static readonly int ShadowmapTex = Shader.PropertyToID("_MainLightShadowmapTexture");
        public static readonly int BlueNoiseTex = Shader.PropertyToID("_PCSS_BlueNoiseTex");
        public static readonly int MaskResult = Shader.PropertyToID("_PCSS_MaskResult"); // kernel 0 output
        public static readonly int MaskTex = Shader.PropertyToID("_PCSS_MaskTex"); // kernel 1 input
        public static readonly int SSResult = Shader.PropertyToID("_SSShadowResult"); // kernel 1 output

        // Output global texture consumed by receiver materials.
        public static readonly int GlobalResult = Shader.PropertyToID("_CustomScreenSpaceShadowmap");

        // Constants.
        public static readonly int InvViewProj = Shader.PropertyToID("_PCSS_InvViewProj");
        public static readonly int CameraPosWS = Shader.PropertyToID("_PCSS_CameraPosWS");
        public static readonly int TextureSize = Shader.PropertyToID("_PCSS_TextureSize");
        public static readonly int MaskSize = Shader.PropertyToID("_PCSS_MaskSize");
        public static readonly int ShadowTexelSize = Shader.PropertyToID("_PCSS_ShadowTexelSize");
        public static readonly int CascadeCount = Shader.PropertyToID("_PCSS_CascadeCount");
        public static readonly int CascadeBorder = Shader.PropertyToID("_PCSS_CascadeBorder");
        public static readonly int BlockerSearchRadius = Shader.PropertyToID("_PCSS_BlockerSearchRadius");
        public static readonly int SunAngularDiameter = Shader.PropertyToID("_PCSS_SunAngularDiameter");
        public static readonly int PenumbraScale = Shader.PropertyToID("_PCSS_PenumbraScale");
        public static readonly int DepthBias = Shader.PropertyToID("_PCSS_DepthBias");
        public static readonly int MaxFilterRadius = Shader.PropertyToID("_PCSS_MaxFilterRadius");
        public static readonly int FarSoftness = Shader.PropertyToID("_PCSS_FarSoftness");
        public static readonly int LightDirection = Shader.PropertyToID("_PCSS_LightDirection");
        public static readonly int SlopeBiasScale = Shader.PropertyToID("_PCSS_SlopeBiasScale");
        public static readonly int BlueNoiseSize = Shader.PropertyToID("_PCSS_BlueNoiseSize");
        public static readonly int UseBlueNoise = Shader.PropertyToID("_PCSS_UseBlueNoise");
        public static readonly int JitterIndex = Shader.PropertyToID("_PCSS_JitterIndex");
        public static readonly int Stabilize = Shader.PropertyToID("_PCSS_StabilizeSampling");
        public static readonly int ReversedZ = Shader.PropertyToID("_PCSS_ReversedZ");
    }
}
