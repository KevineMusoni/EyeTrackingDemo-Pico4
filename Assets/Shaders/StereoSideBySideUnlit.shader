Shader "Custom/StereoSideBySideUnlit"
{
    // Splits a single side-by-side stereo video texture (left half = left eye frame, right half
    // = right eye frame) so each eye samples only its own half - relies on unity_StereoEyeIndex,
    // which is only populated under Single Pass Instanced/Multi-view stereo rendering.
    //
    // Written for the Built-in Render Pipeline (confirmed active via
    // ProjectSettings/GraphicsSettings.asset - m_CustomRenderPipeline is unassigned), not URP.
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                uv.x = uv.x * 0.5 + (unity_StereoEyeIndex * 0.5);
                return tex2D(_MainTex, uv);
            }
            ENDCG
        }
    }
}
