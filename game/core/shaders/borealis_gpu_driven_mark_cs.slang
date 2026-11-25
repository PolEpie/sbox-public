// borealis_gpu_driven_mark_cs: a standalone module, the whole shader for Borealis.Gpu (docs/reference/borealis-standalone-modules.md).
//-------------------------------------------------------------------------------------------------------------------------------------------------------------
// HEADER: DevShader = true;
// HEADER: Description = "Borealis GPU-driven visibility: marks the first vertex of every triangle that owns a sample of the prepass's ids.";

//-------------------------------------------------------------------------------------------------------------------------------------------------------------

#include "sbox/pass.slangh"

Mode( Default );

//-------------------------------------------------------------------------------------------------------------------------------------------------------------

//-------------------------------------------------------------------------------------------------------------------------------------------------------------
#include "sbox/system.slangh" // This should always be the first include in COMMON

//-------------------------------------------------------------------------------------------------------------------------------------------------------------
#if ( PROGRAM == VFX_PROGRAM_CS )
#include "sbox/view.slangh"

// 0: single-sample; 1: generic MSAA; 2: four-sample fast path.
DynamicCombo( D_MSAA, 0..2, Sys( ALL ) );

// The vertex bit + 1, then (with normals from the ids) the instance record; a target of one word reads 0 for the second.
// Unknown format: Slang would type it Rg32ui, which an R32_UINT view (the default ids target) leaves undefined
#if D_MSAA
	[format( "unknown" )] Texture2DMS<uint2> g_tIds < Attribute( "BorealisIds" ); >;
#else
	[format( "unknown" )] Texture2D<uint2> g_tIds < Attribute( "BorealisIds" ); >;
#endif

RWByteAddressBuffer g_Mask < Attribute( "BorealisCullMask" ); >;

cbuffer BorealisGpuDrivenMark_t
{
	uint g_nMarkWidth;
	uint g_nMarkHeight;
	uint g_nMarkSamples;
	uint g_nMarkBits;	// vertex bits in use
	uint g_nMarkCallSlot;		// with normals from the ids, whose target keeps earlier calls' ids: the call's frame chunk
	uint g_nMarkCallFirst;		// its first instance record
	uint g_nMarkCallInstances;	// and how many; only its own ids are marked (earlier calls marked theirs). 0: every id
	uint g_nMarkPad3;
};

// Sets the lanes' bits. Neighbouring pixels mostly name the same triangle, and an atomic each serialises them on its
// mask word, so the wave's lowest words go first with one atomic each; lanes with a word beyond those take their own.
// Called by the whole wave: the wave operations stay in uniform control flow.
void MarkWord( uint nWord, uint nMine )
{
	[unroll] for ( uint nRound = 0; nRound < 4; nRound++ )
	{
		uint nLowest = WaveActiveMin( nWord );
		if ( nLowest == 0xffffffffu )
			break;   // uniform: the wave has nothing left

		bool bLowest = nWord == nLowest;
		uint nBits = WaveActiveBitOr( bLowest ? nMine : 0u );
		if ( WaveIsFirstLane() )
			g_Mask.InterlockedOr( nLowest * 4, nBits );
		if ( bLowest )
			nWord = 0xffffffffu;
	}

	if ( nWord != 0xffffffffu )
		g_Mask.InterlockedOr( nWord * 4, nMine );
}

uint LoadBit( int2 vPixel, uint nSample )
{
	#if D_MSAA
		uint2 vId = g_tIds.Load( vPixel, nSample );
	#else
		uint2 vId = g_tIds.Load( int3( vPixel, 0 ) );
	#endif

	// Another call's id, which its own mark marked. An id with no record (all ones), or none written (0: a variant from
	// before the second word, which leaves it as cleared or undefined), is marked whoever drew it.
	bool bRecord = vId.y != 0 && vId.y != 0xffffffffu;
	if ( g_nMarkCallInstances != 0 && bRecord && ( ( vId.y >> 22 ) != g_nMarkCallSlot || ( vId.y & 0x3fffff ) - g_nMarkCallFirst >= g_nMarkCallInstances ) )
		return 0xffffffffu;

	uint nBit = vId.x - 1;
	return nBit < g_nMarkBits ? nBit : 0xffffffffu;
}

void MarkBit( bool bMark, uint nBit )
{
	MarkWord( bMark && nBit != 0xffffffffu ? nBit >> 5 : 0xffffffffu, 1u << ( nBit & 31 ) );
}

// Every distinct id of a pixel's samples sets its bit: the first vertex's bit + 1, 0 for nothing
[numthreads( 8, 8, 1 )]
[shader( "compute" )] void MainCs( uint3 vThreadId : SV_DispatchThreadID )
{
	bool bInside = vThreadId.x < g_nMarkWidth && vThreadId.y < g_nMarkHeight;
	int2 vPixel = int2( min( vThreadId.xy, uint2( g_nMarkWidth, g_nMarkHeight ) - 1 ) );

	#if D_MSAA == 2
		// Most four-sample pixels name the same triangle in each sample. Load the samples together and combine bits
		// for the same mask word in registers, so those pixels participate in just one wave reduction.
		uint4 vBits = uint4( LoadBit( vPixel, 0 ), LoadBit( vPixel, 1 ), LoadBit( vPixel, 2 ), LoadBit( vPixel, 3 ) );
		uint4 vWords = vBits >> 5;
		uint4 vMasks = 1u << ( vBits & 31 );
		uint nMask = vMasks.x | ( vWords.y == vWords.x ? vMasks.y : 0 ) | ( vWords.z == vWords.x ? vMasks.z : 0 ) | ( vWords.w == vWords.x ? vMasks.w : 0 );
		MarkWord( bInside && vBits.x != 0xffffffffu ? vWords.x : 0xffffffffu, nMask );
		bool bSecond = bInside && vWords.y != vWords.x && vBits.y != 0xffffffffu;
		bool bThird = bInside && vWords.z != vWords.x && vBits.z != vBits.y && vBits.z != 0xffffffffu;
		bool bFourth = bInside && vWords.w != vWords.x && vBits.w != vBits.y && vBits.w != vBits.z && vBits.w != 0xffffffffu;
		if ( WaveActiveAnyTrue( bSecond || bThird || bFourth ) )
		{
			MarkWord( bSecond ? vWords.y : 0xffffffffu, vMasks.y );
			MarkWord( bThird ? vWords.z : 0xffffffffu, vMasks.z );
			MarkWord( bFourth ? vWords.w : 0xffffffffu, vMasks.w );
		}
	#elif D_MSAA
		uint ids[8];
		uint nSamples = min( g_nMarkSamples, 8u );
		for ( uint s = 0; s < nSamples; s++ )
		{
			uint nBit = LoadBit( vPixel, s );
			ids[s] = nBit;
			bool bSeen = false;
			for ( uint j = 0; j < s; j++ )
				bSeen = bSeen || ids[j] == nBit;
			MarkBit( bInside && !bSeen, nBit );
		}
	#else
		MarkBit( bInside, LoadBit( vPixel, 0 ) );
	#endif
}

#endif // CS

