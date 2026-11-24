// borealis_shadow_cache_copy: a standalone module, the whole shader for Borealis.Gpu (docs/reference/borealis-standalone-modules.md).
//-------------------------------------------------------------------------------------------------------------------------------------------------------------
// HEADER: DevShader = true;
// HEADER: Description = "Borealis shadows: a cached shadow map's depth into a map, unwrapped by the cache's offset, as an exact copy.";

//-------------------------------------------------------------------------------------------------------------------------------------------------------------

#include "sbox/pass.slangh"

Mode( Default );

//-------------------------------------------------------------------------------------------------------------------------------------------------------------

//-------------------------------------------------------------------------------------------------------------------------------------------------------------
#include "sbox/system.slangh" // This should always be the first include in COMMON

//-------------------------------------------------------------------------------------------------------------------------------------------------------------
#if ( PROGRAM == VFX_PROGRAM_VS )
#include "sbox/view.slangh"

struct VS_INPUT
{
	float3 vPositionOs : POSITION < Semantic( PosXyz ); >;
	float2 vTexCoord : TEXCOORD0 < Semantic( LowPrecisionUv ); >;
};

struct VS_OUTPUT
{
	float4 vPositionPs : SV_Position;
	float2 vTexCoord : TEXCOORD0;
};

[shader( "vertex" )] VS_OUTPUT MainVs( VS_INPUT i )
{
	VS_OUTPUT o;
	o.vPositionPs = float4( i.vPositionOs.xyz, 1.0 );
	o.vTexCoord = i.vTexCoord;
	return o;
}

#endif // VS

//-------------------------------------------------------------------------------------------------------------------------------------------------------------
#if ( PROGRAM == VFX_PROGRAM_PS )
	#include "sbox/view.slangh"

	Texture2D<float> g_tShadowCache < Attribute( "ShadowCacheDepth" ); >;

	// The texel of the cache each texel of the map starts at (xy, in [0, size)) and the cache's size (zw)
	float4 g_vShadowCacheWrap < Attribute( "ShadowCacheWrap" ); >;

	// Every texel written with the cache's depth as it is: no test, no colour
	RenderState( ColorWriteEnable0, false );
	RenderState( DepthEnable, true );
	RenderState( DepthWriteEnable, true );
	RenderState( DepthFunc, ALWAYS );
	RenderState( BlendEnable, false );
	RenderState( CullMode, NONE );

	[shader( "pixel" )] float MainPs( float4 vPositionPs : SV_Position, float2 vTexCoord : TEXCOORD0 ) : SV_Depth
	{
		uint2 vSize = uint2( g_vShadowCacheWrap.zw );
		uint2 vTexel = ( uint2( vPositionPs.xy ) + uint2( g_vShadowCacheWrap.xy ) ) % vSize;
		return g_tShadowCache.Load( int3( vTexel, 0 ) );
	}

#endif // PS
