// Copies this frame's depth prepass into mip 0 of the Hi-Z pyramid as linear eye depth.
Shader "Hidden/Sari/HiZCopyDepth"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        Pass
        {
            Name "HiZCopyDepth"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            float4 Vert(uint vertexID : SV_VertexID) : SV_POSITION
            {
                return GetFullScreenTriangleVertexPosition(vertexID);
            }

            // Texel-for-texel copy keeps the pyramid in the depth texture's orientation.
            float Frag(float4 positionCS : SV_POSITION) : SV_Target
            {
                return LinearEyeDepth(LoadSceneDepth(uint2(positionCS.xy)), _ZBufferParams);
            }
            ENDHLSL
        }
    }
}
