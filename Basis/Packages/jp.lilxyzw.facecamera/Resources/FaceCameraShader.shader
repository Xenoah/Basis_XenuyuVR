Shader "Hidden/FaceCameraShader"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        _FaceCameraOpacity ("Alpha", Float) = 1
        _FaceCameraAnchorX ("AnchorX", Float) = 1
        _FaceCameraAnchorY ("AnchorY", Float) = 1
        _FaceCameraSizeDesktop ("Size", Float) = 0.1
        _FaceCameraOffsetXDesktop ("OffsetX", Float) = 0.1
        _FaceCameraOffsetYDesktop ("OffsetY", Float) = 0.1
        _FaceCameraSizeVR ("Size", Float) = 0.1
        _FaceCameraOffsetXVR ("OffsetX", Float) = 0.1
        _FaceCameraOffsetYVR ("OffsetY", Float) = 0.1
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            ZTest Always
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha, Zero One
            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct appdata
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                half _FaceCameraOpacity;
                float _FaceCameraAnchorX;
                float _FaceCameraAnchorY;
                float _FaceCameraSizeDesktop;
                float _FaceCameraOffsetXDesktop;
                float _FaceCameraOffsetYDesktop;
                float _FaceCameraSizeVR;
                float _FaceCameraOffsetXVR;
                float _FaceCameraOffsetYVR;
            CBUFFER_END

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                #if !defined(USING_STEREO_MATRICES)
                v.positionOS.xy *= float2(-2,2);
                v.positionOS.x *= _ScreenParams.y / _ScreenParams.x;
                v.positionOS.x += (1-_ScreenParams.y / _ScreenParams.x) * _FaceCameraAnchorX;

                v.positionOS.xy -= float2(_FaceCameraAnchorX, _FaceCameraAnchorY);
                v.positionOS.xy *= _FaceCameraSizeDesktop;
                v.positionOS.xy += float2(_FaceCameraAnchorX, _FaceCameraAnchorY);
                v.positionOS.xy -= float2(_FaceCameraOffsetXDesktop * _ScreenParams.y / _ScreenParams.x, _FaceCameraOffsetYDesktop) * 2 * float2(_FaceCameraAnchorX, _FaceCameraAnchorY);

                o.positionCS.xy = v.positionOS.xy;
                o.positionCS.z = 0.5;
                o.positionCS.w = 1;
                #else
                float3 forward = normalize(unity_StereoMatrixV[0]._m20_m21_m22 + unity_StereoMatrixV[1]._m20_m21_m22);
                float3 right = normalize(unity_StereoMatrixV[0]._m00_m01_m02 + unity_StereoMatrixV[1]._m00_m01_m02);
                float3 up = normalize(unity_StereoMatrixV[0]._m10_m11_m12 + unity_StereoMatrixV[1]._m10_m11_m12);
                float3 camerapos = (unity_StereoWorldSpaceCameraPos[0] + unity_StereoWorldSpaceCameraPos[1]) * 0.5;

                v.positionOS.z = 0;
                v.positionOS.xy = v.positionOS.xy * float2(-2,2);
                v.positionOS.xy -= float2(_FaceCameraAnchorX, _FaceCameraAnchorY);
                v.positionOS.xy *= _FaceCameraSizeVR;
                v.positionOS.xy += float2(_FaceCameraAnchorX, _FaceCameraAnchorY);
                v.positionOS.xy -= float2(_FaceCameraOffsetXVR, _FaceCameraOffsetYVR) * 2 * float2(_FaceCameraAnchorX, _FaceCameraAnchorY);
                v.positionOS.xy /= UNITY_MATRIX_P._m00_m11;
                v.positionOS.xy *= 0.075;
                float3 V = normalize(TransformObjectToWorld(v.positionOS.xyz) - camerapos);

                right = normalize(right - V * dot(V, right));
                up = normalize(up - V * dot(V, up));

                float3 positionWS = v.positionOS.x * right + v.positionOS.y * up;
                positionWS += TransformObjectToWorld(0);

                o.positionCS = TransformWorldToHClip(positionWS);
                #endif

                o.uv = v.uv;
                o.uv.y = 1-o.uv.y;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                half4 color = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv) ;
                color.a *= _FaceCameraOpacity;
                return color;
            }
            ENDHLSL
        }
    }
}
