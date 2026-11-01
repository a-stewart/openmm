Shader "UI/MM2MenuDuotoneBackground"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _Color;
            float4 _ClipRect;
            float4 _MainTex_ST;

            v2f vert(appdata_t v)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.worldPosition = v.vertex;
                OUT.vertex = UnityObjectToClipPos(OUT.worldPosition);
                OUT.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);
                OUT.color = v.color * _Color;
                return OUT;
            }

            // Evaluates a 3-stop (shadow -> midtone -> highlight) tone curve for a given luminance [0..1]
            float3 EvalToneTriplet(float lum, float3 shadowCol, float3 midCol, float3 highCol)
            {
                float tLow = saturate(lum * 2.0);
                float tHigh = saturate((lum - 0.5) * 2.0);
                float3 lowToMid = lerp(shadowCol, midCol, tLow);
                return lerp(lowToMid, highCol, tHigh);
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                float2 uv = IN.texcoord;
                float3 src = tex2D(_MainTex, uv).rgb;

                // Perceptual luminance with punchy contrast curve matching MM2's photo-collage treatment
                float lum = dot(src, float3(0.299, 0.587, 0.114));
                lum = smoothstep(0.04, 0.90, lum);
                lum = pow(lum, 0.92);

                // Vertical gradient parameter: vy = 1 at top of screen, vy = 0 at bottom of screen
                float vy = saturate(uv.y);

                // 4 vertical palette bands measured directly from MM2's bg_orange_base artwork:
                // Band 3 (Top: vy = 0.72..1.00) -> Vibrant Amber / Sunlit Orange
                float3 topShadow = float3(0.68, 0.18, 0.01);
                float3 topMid    = float3(0.92, 0.44, 0.08);
                float3 topHigh   = float3(0.99, 0.62, 0.17);
                float3 colTop    = EvalToneTriplet(lum, topShadow, topMid, topHigh);

                // Band 2 (Upper-Mid: vy = 0.46) -> Warm Burnt Orange / Crimson-Plum
                float3 uMidShadow = float3(0.32, 0.08, 0.06);
                float3 uMidMid    = float3(0.56, 0.20, 0.10);
                float3 uMidHigh   = float3(0.76, 0.36, 0.16);
                float3 colUMid    = EvalToneTriplet(lum, uMidShadow, uMidMid, uMidHigh);

                // Band 1 (Lower-Mid: vy = 0.25) -> Deep Indigo-Plum
                float3 lMidShadow = float3(0.05, 0.02, 0.11);
                float3 lMidMid    = float3(0.12, 0.04, 0.14);
                float3 lMidHigh   = float3(0.22, 0.09, 0.20);
                float3 colLMid    = EvalToneTriplet(lum, lMidShadow, lMidMid, lMidHigh);

                // Band 0 (Bottom: vy = 0.00..0.10) -> Midnight Navy Blue
                float3 botShadow = float3(0.008, 0.008, 0.10);
                float3 botMid    = float3(0.025, 0.018, 0.13);
                float3 botHigh   = float3(0.065, 0.045, 0.20);
                float3 colBot    = EvalToneTriplet(lum, botShadow, botMid, botHigh);

                float3 finalRgb;
                if (vy >= 0.48)
                {
                    float t = smoothstep(0.48, 0.78, vy);
                    finalRgb = lerp(colUMid, colTop, t);
                }
                else if (vy >= 0.24)
                {
                    float t = smoothstep(0.24, 0.48, vy);
                    finalRgb = lerp(colLMid, colUMid, t);
                }
                else
                {
                    float t = smoothstep(0.04, 0.24, vy);
                    finalRgb = lerp(colBot, colLMid, t);
                }

                fixed4 outCol = fixed4(finalRgb, 1.0) * IN.color;
                outCol.a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
                return outCol;
            }
            ENDCG
        }
    }
}
