// Copies the camera depth attachment into an RFloat color RT for the Hi-Z pyramid.
// Driven by HiZDepthCopyFeature, which binds _HiZDepthSource to renderer.cameraDepthTargetHandle
// and sets the MSAA keyword to match the attachment's sample count.
//
// The depth source is a D32_SFloat_S8_UInt combined depth-stencil. URP's CopyDepthPass samples
// non-MSAA depth as a depth texture, and only uses texel LOAD for MSAA depth. Loading a non-MSAA
// D32+S8 texture on Metal returns 0, which makes the copy/pyramid/debug dumps pure black.
//
// _HiZForceMark (debugPattern) remaps output to 0.5+0.5*depth: mid-gray = "copy ran, depth read
// 0"; black = "copy never ran"; varying gray = depth read works.
Shader "Hidden/HiZCopyDepth"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "HiZCopyDepth"
            ZWrite Off ZTest Always Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _HIZ_MSAA2 _HIZ_MSAA4 _HIZ_MSAA8
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #if defined(_HIZ_MSAA8)
                #define MSAA_SAMPLES 8
            #elif defined(_HIZ_MSAA4)
                #define MSAA_SAMPLES 4
            #elif defined(_HIZ_MSAA2)
                #define MSAA_SAMPLES 2
            #else
                #define MSAA_SAMPLES 1
            #endif

            #if MSAA_SAMPLES == 1
                TEXTURE2D_FLOAT(_HiZDepthSource);
                SAMPLER(sampler_HiZDepthSource);
            #else
                Texture2DMS<float, MSAA_SAMPLES> _HiZDepthSource;
            #endif
            float4 _HiZDepthSource_TexelSize;

            float _HiZForceMark;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 texcoord : TEXCOORD0;
            };

            // Self-contained fullscreen triangle — no _BlitScaleBias dependency, safe under
            // CoreUtils.DrawFullScreen.
            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                o.texcoord = GetFullScreenTriangleTexCoord(vertexID);
                return o;
            }

            float Frag(Varyings i) : SV_Target
            {
            #if MSAA_SAMPLES == 1
                float d = SAMPLE_DEPTH_TEXTURE(_HiZDepthSource, sampler_HiZDepthSource, i.texcoord);
            #else
                int2 coord = int2(i.texcoord * _HiZDepthSource_TexelSize.zw);
                // Reversed-Z: min over samples = farthest surface = conservative occluder
                float d = _HiZDepthSource.Load(coord, 0);
                [unroll] for (int s = 1; s < MSAA_SAMPLES; s++)
                    d = min(d, _HiZDepthSource.Load(coord, s));
            #endif
                if (_HiZForceMark > 0.5) return 0.5 + 0.5 * d;
                return d;
            }
            ENDHLSL
        }
    }
}
