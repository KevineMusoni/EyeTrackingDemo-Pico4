Shader "Custom/SimpleUnlitTexture"
{
    // Hand-written equivalent of the "vid" Shader Graph - samples _BaseMap and
    // outputs it directly with no lighting. Written for the Built-in Render
    // Pipeline (confirmed active via ProjectSettings/GraphicsSettings.asset -
    // m_CustomRenderPipeline is unassigned), not URP.
    Properties
    {
        _BaseMap ("Texture", 2D) = "white" {}
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

            sampler2D _BaseMap;
            float4 _BaseMap_ST;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return tex2D(_BaseMap, i.uv);
            }
            ENDCG
        }
    }
}
