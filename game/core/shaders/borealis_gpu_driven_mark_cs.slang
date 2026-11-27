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

DynamicCombo( D_MSAA, 0..1, Sys( ALL ) );
DynamicCombo( D_MASK_CLEAR, 0..1, Sys( ALL ) );

#if !D_MASK_CLEAR
	// The vertex bit + 1, then (with normals from the ids) the instance record; a target of one word reads 0 for the second.
	// Unknown format: Slang would type it Rg32ui, which an R32_UINT view (the default ids target) leaves undefined
	#if D_MSAA
		[format( "unknown" )] Texture2DMS<uint2> g_tIds < Attribute( "BorealisIds" ); >;
	#else
		[format( "unknown" )] Texture2D<uint2> g_tIds < Attribute( "BorealisIds" ); >;
	#endif
#endif

RWByteAddressBuffer g_Mask < Attribute( "BorealisCullMask" ); >;

// The mask words the frame's marks set from 0, which the next frame's clear zeroes instead of the whole mask: the clear's
// dispatch arguments (a group per 64 words), then how many words (past the capacity: too many, every word is cleared),
// then the words
RWByteAddressBuffer g_Dirty < Attribute( "BorealisCullMaskDirty" ); >;
static const uint DirtyCount = 12;
static const uint DirtyWords = 16;

cbuffer BorealisGpuDrivenMark_t
{
	uint g_nMarkWidth;
	uint g_nMarkHeight;
	uint g_nMarkSamples;
	uint g_nMarkBits;	// vertex bits in use
	uint g_nMarkCallSlot;		// with normals from the ids, whose target keeps earlier calls' ids: the call's frame chunk
	uint g_nMarkCallFirst;		// its first instance record
	uint g_nMarkCallInstances;	// and how many; only its own ids are marked (earlier calls marked theirs). 0: every id
	uint g_nMarkDirtyCapacity;	// words the dirty list holds; 0: none kept (the next clear clears the whole mask)
};

#if D_MASK_CLEAR

// The last frame's dirty words zeroed, a thread each; where they overflowed the list, every word below the bits in use
[numthreads( 64, 1, 1 )]
[shader( "compute" )] void MainCs( uint3 vThreadId : SV_DispatchThreadID )
{
	uint nCount = g_Dirty.Load( DirtyCount );
	if ( nCount <= g_nMarkDirtyCapacity )
	{
		if ( vThreadId.x < nCount )
			g_Mask.Store( g_Dirty.Load( DirtyWords + vThreadId.x * 4 ) * 4, 0 );
		return;
	}

	uint nThreads = g_Dirty.Load( 0 ) * 64;
	uint nWords = ( g_nMarkBits + 31 ) / 32;
	for ( uint w = vThreadId.x; w < nWords; w += nThreads )
		g_Mask.Store( w * 4, 0 );
}

#else

// A word a lane's atomics set from 0 goes in the dirty list
void Dirtied( uint nWord, uint nOld, inout uint dirty[14], inout uint nDirty )
{
	if ( nOld == 0 )
		dirty[nDirty++] = nWord;
}

// Sets the bits of each lane's words (a lane's ids merged by mask word, at most 8). Neighbouring pixels mostly name the
// same triangles, and an atomic each serialises them on its mask word, so the wave's lowest words go first with one atomic
// each, a lane's next word in the round after its last went; words left after those rounds take an atomic of their own.
// The words set from 0 go in the dirty list, the wave's with one atomic. Called by the whole wave: the wave operations
// stay in uniform control flow.
void MarkWords( uint nWords, uint words[8], uint bits[8] )
{
	uint dirty[14];
	uint nDirty = 0;
	uint k = 0;
	[loop] for ( uint nRound = 0; nRound < 6; nRound++ )
	{
		uint nWord = k < nWords ? words[k] : 0xffffffffu;
		uint nLowest = WaveActiveMin( nWord );
		if ( nLowest == 0xffffffffu )
			break;   // uniform: the wave has nothing left
		bool bLowest = nWord == nLowest;
		uint nBits = WaveActiveBitOr( bLowest ? bits[k] : 0u );
		if ( WaveIsFirstLane() )
		{
			uint nOld;
			g_Mask.InterlockedOr( nLowest * 4, nBits, nOld );
			Dirtied( nLowest, nOld, dirty, nDirty );
		}
		if ( bLowest )
			k++;
	}
	for ( ; k < nWords; k++ )
	{
		uint nOld;
		g_Mask.InterlockedOr( words[k] * 4, bits[k], nOld );
		Dirtied( words[k], nOld, dirty, nDirty );
	}

	if ( g_nMarkDirtyCapacity == 0 )
		return;
	uint nTotal = WaveActiveSum( nDirty );
	if ( nTotal == 0 )
		return;
	uint nPrefix = WavePrefixSum( nDirty );
	uint nBase = 0;
	if ( WaveIsFirstLane() )
	{
		g_Dirty.InterlockedAdd( DirtyCount, nTotal, nBase );
		// The clear's groups: one more for each 64 words the list's count passed
		uint nGroups = ( nBase + nTotal + 63 ) / 64 - ( nBase + 63 ) / 64;
		if ( nGroups != 0 )
			g_Dirty.InterlockedAdd( 0, nGroups );
	}
	nBase = WaveReadLaneFirst( nBase ) + nPrefix;
	for ( uint d = 0; d < nDirty; d++ )
	{
		if ( nBase + d < g_nMarkDirtyCapacity )
			g_Dirty.Store( DirtyWords + ( nBase + d ) * 4, dirty[d] );
	}
}

// Every distinct id of a pixel's samples sets its bit: the first vertex's bit + 1, 0 for nothing
[numthreads( 8, 8, 1 )]
[shader( "compute" )] void MainCs( uint3 vThreadId : SV_DispatchThreadID )
{
	bool bInside = vThreadId.x < g_nMarkWidth && vThreadId.y < g_nMarkHeight;
	int2 vPixel = int2( min( vThreadId.xy, uint2( g_nMarkWidth, g_nMarkHeight ) - 1 ) );
	uint words[8], bits[8];
	uint nWords = 0;
	uint nSamples = min( g_nMarkSamples, 8u );
	for ( uint s = 0; s < nSamples; s++ )
	{
		#if D_MSAA
			uint2 vId = g_tIds.Load( vPixel, s );
		#else
			uint2 vId = g_tIds.Load( int3( vPixel, 0 ) );
		#endif

		// Another call's id, which its own mark marked. An id with no record (all ones), or none written (0: a variant from
		// before the second word, which leaves it as cleared or undefined), is marked whoever drew it
		uint v = vId.x;
		bool bRecord = vId.y != 0 && vId.y != 0xffffffffu;
		if ( g_nMarkCallInstances != 0 && bRecord && ( ( vId.y >> 22 ) != g_nMarkCallSlot || ( vId.y & 0x3fffff ) - g_nMarkCallFirst >= g_nMarkCallInstances ) )
			v = 0;
		uint nBit = v - 1;
		if ( !bInside || v == 0 || nBit >= g_nMarkBits )
			continue;

		// Into the lane's word for it: a sample's id is mostly its pixel's first sample's
		uint nWord = nBit >> 5;
		uint nMine = 1u << ( nBit & 31 );
		bool bMerged = false;
		for ( uint j = 0; j < nWords; j++ )
		{
			if ( words[j] == nWord )
			{
				bits[j] |= nMine;
				bMerged = true;
			}
		}
		if ( !bMerged )
		{
			words[nWords] = nWord;
			bits[nWords] = nMine;
			nWords++;
		}
	}
	MarkWords( nWords, words, bits );
}

#endif // D_MASK_CLEAR

#endif // CS

