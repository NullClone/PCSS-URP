Shader "Hidden/PCSS/DebugView"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        Pass
        {
            Name "PCSS Debug View"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            // Vert / Varyings / _BlitTexture / sampler_LinearClamp come from Blit.hlsl
            // (which includes Common.hlsl), matching RenderGraph's AddBlitPass + Blitter.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float v = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord).r;
                return half4(v, v, v, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
