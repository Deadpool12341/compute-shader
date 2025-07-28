Shader "Ocean/Ocean"
{
    Properties
    {
        _DeepColor("Deep Water Color", Color) = (0.05, 0.2, 0.5, 1.0)
        _ShallowColor("Shallow Water Color", Color) = (0.2, 0.6, 0.8, 0.7)
        _Transparency("Transparency", Range(0,1)) = 0.6
        _FresnelPower("Fresnel Power", Range(0.1,5)) = 2.0
        _FoamColor("Foam Color", Color) = (1,1,1,1)
        _FoamTexture("Foam Texture", 2D) = "white" {}
        _FoamScale("Foam Scale", Range(0.1,5)) = 1
        _FoamIntensity("Foam Intensity", Range(0,2)) = 1
    }
    
    SubShader
    {
        Tags {"Queue" = "Transparent" "RenderType" = "Transparent" }
        LOD 200

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float3 viewDir : TEXCOORD2;
                float2 uv : TEXCOORD3;
                float depth : TEXCOORD4;
            };

            sampler2D _FoamTexture;
            float4 _FoamTexture_ST;
            
            fixed4 _DeepColor, _ShallowColor, _FoamColor;
            float _Transparency, _FresnelPower;
            float _FoamScale, _FoamIntensity;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNormal = normalize(mul((float3x3)unity_ObjectToWorld, v.normal));
                o.viewDir = normalize(_WorldSpaceCameraPos - o.worldPos);
                o.uv = v.uv;
                o.depth = length(_WorldSpaceCameraPos - o.worldPos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Normalize vectors
                float3 normal = normalize(i.worldNormal);
                float3 viewDir = normalize(i.viewDir);
                
                // Ensure we're using the correct face normal
                if (dot(normal, viewDir) < 0) {
                    normal = -normal; // Flip normal if facing away
                }
                
                // Fresnel effect - INVERTED so front faces are more opaque
                float fresnel = pow(saturate(dot(normal, viewDir)), _FresnelPower);
                
                // Depth-based color transition
                float depthFactor = saturate(i.depth * 0.02);
                fixed4 waterColor = lerp(_ShallowColor, _DeepColor, depthFactor);
                
                // Apply fresnel - front faces more opaque, edges more transparent
                float alpha = lerp(1.0, _Transparency, fresnel * 0.6);
                waterColor.a = alpha;
                
                // Basic lighting
                float3 lightDir = normalize(_WorldSpaceLightPos0.xyz);
                float lightDot = saturate(dot(normal, lightDir));
                waterColor.rgb += lightDot * 0.2;
                
                // Rim lighting for translucency
                float rim = 1.0 - saturate(dot(viewDir, normal));
                rim = pow(rim, 2.0);
                waterColor.rgb += rim * _ShallowColor.rgb * 0.3;
                
                // Sample foam texture
                float2 foamUV = i.uv * _FoamScale;
                fixed4 foamTex = tex2D(_FoamTexture, foamUV);
                
                // Apply foam based on rim and texture
                float foamMask = rim * foamTex.r * _FoamIntensity;
                waterColor.rgb = lerp(waterColor.rgb, _FoamColor.rgb, foamMask);
                waterColor.a = lerp(waterColor.a, 1.0, foamMask); // Foam is more opaque
                
                return waterColor;
            }
            ENDCG
        }
    }
    
    FallBack "Transparent/Diffuse"
}
