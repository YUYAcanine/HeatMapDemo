Shader "Custom/TextureHeatAlpha"
{
    Properties
    {
        _MainTex      ("Base Texture", 2D) = "white" {}
        _HeatStrength ("Overlay Factor", Range(0,2)) = 1
        _C0 ("Cold (Blue)",  Color) = (0,0.2,1,1)
        _C1 ("Cool (Cyan)",  Color) = (0,1,1,1)
        _C2 ("Warm (Yellow)",Color) = (1,1,0,1)
        _C3 ("Hot (Red)",    Color) = (1,0,0,1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows vertex:vert
        sampler2D _MainTex;
        float4 _C0, _C1, _C2, _C3;
        half   _HeatStrength;

        struct Input { float2 uv_MainTex; fixed4 vc : COLOR; };

        void vert(inout appdata_full v, out Input o){
            UNITY_INITIALIZE_OUTPUT(Input,o);
            o.vc = v.color;                     // 頂点カラー RGBA
        }

        fixed3 Palette(float t)
        {
            // 0-0.33 : C0→C1, 0.33-0.66 : C1→C2, 0.66-1 : C2→C3
            if (t < 0.33) return lerp(_C0.rgb, _C1.rgb, t/0.33);
            else if (t < 0.66) return lerp(_C1.rgb, _C2.rgb, (t-0.33)/0.33);
            else return lerp(_C2.rgb, _C3.rgb, (t-0.66)/0.34);
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed3 baseCol = tex2D(_MainTex, IN.uv_MainTex).rgb;

            fixed heatT = saturate(IN.vc.a);      // α = 0-1 ヒート量
            fixed3 heatCol = Palette(heatT) * _HeatStrength;

            // テクスチャとブレンド（半透明加算 50%）
            o.Albedo   = lerp(baseCol, baseCol + heatCol, 0.5);
            o.Emission = heatCol;                 // ほのかに発光
            o.Alpha    = 1;
        }
        ENDCG
    }
}


