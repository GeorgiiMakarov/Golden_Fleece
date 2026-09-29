Shader "FluidBaseline/SliceCopy"
{
    // Copies one Z slice of the scalar volume to a 2D target for flipbook capture.
    // UNTESTED IN EDITOR (2026-09-27).
    Properties
    {
        _VolumeTex ("Volume", 3D) = "" {}
        _SliceZ ("Slice Z (0..1)", Float) = 0.5
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler3D _VolumeTex;
            float _SliceZ;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float s = tex3D(_VolumeTex, float3(i.uv, _SliceZ)).r;
                return fixed4(s, s, s, 1.0);
            }
            ENDCG
        }
    }
}
