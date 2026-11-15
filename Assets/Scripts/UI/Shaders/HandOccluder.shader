// Soft hand occluder for an additive (optical see-through) display.
// The hand mesh is drawn AFTER the overlay keys as black with alpha: black adds no light, so where alpha = 1 the overlay
// behind the hand disappears and the real hand shows through; toward the silhouette alpha fades to 0, so the cut-out has a
// soft edge instead of a hard one. The fade is driven by how edge-on the surface is (|N·V|): 0 exactly at the outline,
// 1 once the surface faces the eye, so it starts at the outline and ends just inside it.
// Cost: one extra draw per tracked hand (a few thousand vertices), no extra render targets or full-screen passes.
Shader "PianoTeacher/HandOccluder"
{
    Properties
    {
        _Inflate ("Grow (+) / shrink (-) along normals (m)", Float) = -0.0015
        _EdgeWidth ("Edge fade width (0 = hard, 1 = very soft)", Range(0.01, 1)) = 0.45
        _Strength ("Max occlusion", Range(0, 1)) = 1
    }
    SubShader
    {
        // After the opaque overlay keys (2000), still in the opaque pass so it is not re-sorted with UI text.
        Tags { "RenderType"="Transparent" "Queue"="Geometry+450" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" }

        Pass
        {
            Name "HandOccluder"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ColorMask RGB      // leave the alpha channel (passthrough blend) untouched
            ZWrite Off
            ZTest LEqual       // keys in front of the hand (none in practice) stay visible
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Inflate;
                float _EdgeWidth;
                float _Strength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 nWS = normalize(TransformObjectToWorldNormal(v.normalOS));
                float3 posWS = TransformObjectToWorld(v.positionOS.xyz) + nWS * _Inflate;
                o.positionWS = posWS;
                o.normalWS = nWS;
                o.positionCS = TransformWorldToHClip(posWS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 viewDir = normalize(GetCameraPositionWS() - i.positionWS);
                float facing = abs(dot(normalize(i.normalWS), viewDir)); // 0 at the outline, 1 facing the eye
                float a = smoothstep(0.0, _EdgeWidth, facing) * _Strength;
                return half4(0, 0, 0, a);
            }
            ENDHLSL
        }
    }
}
