Shader "Custom/TextureWithHeat"
{
    Properties
    {
        _MainTex ("Base Texture", 2D) = "white" {}
        _HeatTint ("Heat Tint", Color) = (1,0,0,1)
        _HeatStrength ("Heat Strength", Range(0,10)) = 5
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200
        CGPROGRAM
        #pragma surface surf Standard vertex:vert fullforwardshadows
        sampler2D _MainTex;
        float4 _HeatTint;
        half   _HeatStrength;

        struct Input { float2 uv_MainTex; fixed4 vc : COLOR; };

        void vert (inout appdata_full v, out Input o){
            UNITY_INITIALIZE_OUTPUT(Input,o);
            o.vc = v.color;         // 頂点カラー
        }

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            fixed3 baseCol = tex2D(_MainTex, IN.uv_MainTex).rgb;

            // 頂点カラーの「赤チャンネル」を 0-1 のヒート量として利用
            fixed  heatFactor = saturate(IN.vc.r);          // 白→0, 赤→1
            fixed3 heat = heatFactor * _HeatTint.rgb * _HeatStrength;

            // 加算ブレンド
            o.Albedo = saturate(baseCol + heat);
            o.Alpha  = 1;
        }
        ENDCG
    }
}
