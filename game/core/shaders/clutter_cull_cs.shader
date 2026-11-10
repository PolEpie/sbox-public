HEADER
{
	DevShader = true;
	Description = "Clutter GPU frustum culling";
}

MODES
{
	Default();
}

FEATURES
{
}

COMMON
{
	#include "system.fxc"
	#include "common.fxc"
	#include "transform_buffer.fxc" // TransformBufferData_t
}

CS
{
	// Every batch in the scene is culled in one go per view:
	//   0 = count: frustum + LOD test per instance, counts survivors per batch LOD
	//   1 = args:  prefix sums the counts into each batch LOD's range of the visible buffer and its draw arguments
	//   2 = scatter: copies each survivor into its batch LOD's range
	//   3 = history: copies one mip of the view's depth chain for the next frame's prepass to cull against
	DynamicCombo( D_PASS, 0..3, Sys( ALL ) );

	struct ClutterBatch_t
	{
		float4 LodSwitchDistances;
		uint4 ArgsBase;  // First indirect draw entry per LOD
		uint4 DrawCount; // Indirect draw entries (one per material) per LOD
		float ModelRadius;
		uint LodCount;   // 0 for an unused batch slot
		uint CastShadows;
		uint Padding;
	};

	struct DrawIndexedArguments_t
	{
		uint IndexCount;
		uint InstanceCount;
		uint FirstIndex;
		int BaseVertex;
		uint FirstInstance;
	};

	StructuredBuffer<TransformBufferData_t> AllInstances < Attribute( "AllInstances" ); >;
	StructuredBuffer<float4> AllInstanceSpheres < Attribute( "AllInstanceSpheres" ); >;
	StructuredBuffer<uint> AllInstanceBatches < Attribute( "AllInstanceBatches" ); >;
	StructuredBuffer<ClutterBatch_t> ClutterBatches < Attribute( "ClutterBatches" ); >;
	int InstanceCount < Attribute( "InstanceCount" ); >;
	int BatchCount < Attribute( "BatchCount" ); >;

	// Survivors per batch LOD (count pass), then each range's write cursor (scatter pass).
	RWStructuredBuffer<uint> ClutterCounts < Attribute( "ClutterCounts" ); >;
	// Each instance's LOD from the count pass, or ~0 if culled.
	RWStructuredBuffer<uint> ClutterInstanceLods < Attribute( "ClutterInstanceLods" ); >;
	RWStructuredBuffer<DrawIndexedArguments_t> ClutterArgs < Attribute( "ClutterArgs" ); >;
	RWStructuredBuffer<TransformBufferData_t> ClutterVisible < Attribute( "ClutterVisible" ); >;

	float3 ClutterLodCameraPos     < Attribute( "ClutterLodCameraPos" ); >;
	float  ClutterLodTanHalfFov    < Attribute( "ClutterLodTanHalfFov" ); >;
	float  ClutterLodViewportWidth < Attribute( "ClutterLodViewportWidth" ); >;
	float  ClutterLodOrthoWidth    < Attribute( "ClutterLodOrthoWidth" ); >;

	// Debug: >1 narrows the cull frustum so culling is visible on-screen.
	float ClutterFrustumScale < Attribute( "ClutterFrustumScale" ); >;

	// Instances whose bounds are entirely farther than this from the LOD camera are dropped. 0 disables the limit.
	float ClutterMaxDistance < Attribute( "ClutterMaxDistance" ); >;

	// Shadow views skip batches that don't cast shadows.
	int CullShadowCasters < Attribute( "CullShadowCasters" ); >;

	// Plain float4x4: attribute matrices are stored raw and read column-major, so the CPU uploads
	// the transpose and we consume it as mul( M, pos ). A row_major qualifier here breaks the planes.
	float4x4 ClutterWorldToProjection < Attribute( "ClutterWorldToProjection" ); >;

	// Shadow views: the camera's visible range closed up at the light (ShadowMapper.BuildCasterVolume) - a cascade's
	// sweep along the sun, a point or spot light's cone. xyz = inward unit normal, w = -distance. Count is 0 elsewhere.
	int ClutterCasterPlaneCount < Attribute( "ClutterCasterPlaneCount" ); >;
	float4 ClutterCasterPlane0 < Attribute( "ClutterCasterPlane0" ); >;
	float4 ClutterCasterPlane1 < Attribute( "ClutterCasterPlane1" ); >;
	float4 ClutterCasterPlane2 < Attribute( "ClutterCasterPlane2" ); >;
	float4 ClutterCasterPlane3 < Attribute( "ClutterCasterPlane3" ); >;
	float4 ClutterCasterPlane4 < Attribute( "ClutterCasterPlane4" ); >;
	float4 ClutterCasterPlane5 < Attribute( "ClutterCasterPlane5" ); >;
	float4 ClutterCasterPlane6 < Attribute( "ClutterCasterPlane6" ); >;
	float4 ClutterCasterPlane7 < Attribute( "ClutterCasterPlane7" ); >;
	float4 ClutterCasterPlane8 < Attribute( "ClutterCasterPlane8" ); >;
	float4 ClutterCasterPlane9 < Attribute( "ClutterCasterPlane9" ); >;
	float4 ClutterCasterPlane10 < Attribute( "ClutterCasterPlane10" ); >;
	float4 ClutterCasterPlane11 < Attribute( "ClutterCasterPlane11" ); >;

	// Forward passes after the prepass: the view's Hi-Z chain (reverse-Z, min depth in r), mip 0 the viewport's size.
	// An instance whose bounds are behind the farthest depth everywhere they cover is culled. 0 elsewhere.
	int ClutterOcclusion < Attribute( "ClutterOcclusion" ); >;
	Texture2D<float2> ClutterDepthChain < Attribute( "ClutterDepthChain" ); SrgbRead( false ); >;
	float2 ClutterDepthRange < Attribute( "ClutterDepthRange" ); >; // the viewport's min and max Z

	// The prepass: last frame's chain from mip ClutterHistoryMip down (its mip 0 is that mip), the view it was drawn from
	// and that view's viewport size and depth range. An instance hidden there is left to the forward pass, which culls
	// against this frame's chain and draws what's visible now. 0 elsewhere.
	int ClutterHistoryValid < Attribute( "ClutterHistoryValid" ); >;
	Texture2D<float2> ClutterHistory < Attribute( "ClutterHistory" ); SrgbRead( false ); >;
	float4x4 ClutterHistoryWorldToProjection < Attribute( "ClutterHistoryWorldToProjection" ); >;
	float2 ClutterHistoryViewport < Attribute( "ClutterHistoryViewport" ); >;
	float2 ClutterHistoryDepthRange < Attribute( "ClutterHistoryDepthRange" ); >;
	int ClutterHistoryMip < Attribute( "ClutterHistoryMip" ); >;

	// The history pass: the mip of the history it writes, from the chain's mip ClutterHistoryMip
	RWTexture2D<float2> ClutterHistoryOut < Attribute( "ClutterHistoryOut" ); >;

	static const uint Culled = 0xFFFFFFFF;

	// Bounding-sphere frustum test - cheaper and slightly looser than an 8-corner OBB test.
	bool SphereInFrustum( float3 center, float radius )
	{
		float4x4 vp = ClutterWorldToProjection;

		bool ortho = abs( vp[3].x ) < 1e-5 && abs( vp[3].y ) < 1e-5 && abs( vp[3].z ) < 1e-5;
		float4 keepAll = float4( 0, 0, 0, 1 );

		// Scaling the X/Y rows narrows the cull frustum.
		float frustumScale = max( ClutterFrustumScale, 1e-3 );
		float4 rowX = vp[0] * frustumScale;
		float4 rowY = vp[1] * frustumScale;

		float4 planes[6] =
		{
			vp[3] + rowX, // left
			vp[3] - rowX, // right
			vp[3] + rowY, // bottom
			vp[3] - rowY, // top
			ortho ? keepAll : vp[2],             // near
			ortho ? keepAll : ( vp[3] - vp[2] ), // far
		};

		[unroll]
		for ( int i = 0; i < 6; i++ )
		{
			// Normalize for a true signed distance; the keepAll plane has a zero normal, so skip it.
			float len = length( planes[i].xyz );
			if ( len < 1e-6 )
				continue;

			float dist = ( dot( planes[i].xyz, center ) + planes[i].w ) / len;
			if ( dist < -radius )
				return false;
		}
		return true;
	}

	bool SphereInCasterVolume( float3 center, float radius )
	{
		float4 planes[12] =
		{
			ClutterCasterPlane0, ClutterCasterPlane1, ClutterCasterPlane2, ClutterCasterPlane3,
			ClutterCasterPlane4, ClutterCasterPlane5, ClutterCasterPlane6, ClutterCasterPlane7,
			ClutterCasterPlane8, ClutterCasterPlane9, ClutterCasterPlane10, ClutterCasterPlane11,
		};

		[loop]
		for ( int i = 0; i < ClutterCasterPlaneCount; i++ )
		{
			if ( dot( planes[i].xyz, center ) + planes[i].w < -radius )
				return false;
		}
		return true;
	}

	// The pixels a sphere's bounding box covers in a view of size viewport, padded a pixel for TAA jitter, and its nearest
	// depth mapped into the depth range. False when it can't be tested: it crosses the near plane or is off screen.
	bool ProjectSphere( float3 center, float radius, float4x4 worldToProjection, float2 viewport, float2 depthRange, out int2 lo, out int2 hi, out float depth )
	{
		float2 pixelMin = float2( 1e30, 1e30 );
		float2 pixelMax = float2( -1e30, -1e30 );
		float nearest = -1e30;
		lo = 0;
		hi = 0;
		depth = 0;

		[unroll]
		for ( int i = 0; i < 8; i++ )
		{
			float3 corner = center + radius * float3( ( i & 1 ) ? 1 : -1, ( i & 2 ) ? 1 : -1, ( i & 4 ) ? 1 : -1 );
			float4 clip = mul( worldToProjection, float4( corner, 1 ) );
			if ( clip.w <= 0 )
				return false;

			float3 ndc = clip.xyz / clip.w;
			float2 pixel = float2( ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5 ) * viewport;
			pixelMin = min( pixelMin, pixel );
			pixelMax = max( pixelMax, pixel );
			nearest = max( nearest, ndc.z );
		}

		if ( any( pixelMax < 0 ) || any( pixelMin >= viewport ) )
			return false;

		float2 last = viewport - 1;
		lo = (int2)clamp( floor( pixelMin ) - 1, 0, last );
		hi = (int2)clamp( floor( pixelMax ) + 1, 0, last );
		depth = depthRange.x + nearest * ( depthRange.y - depthRange.x );
		return true;
	}

	// Texels a side the test reads at most: it picks the finest mip where the bounds span this many
	static const int OcclusionGrid = 4;

	// The sphere's bounding box against a depth chain, as occlusion_cull_cs's TestBox. The chain's mip 0 is the view's
	// mip firstLevel. False when it can't tell.
	bool SphereOccludedIn( Texture2D<float2> chain, int firstLevel, float4x4 worldToProjection, float2 viewport, float2 depthRange, float3 center, float radius )
	{
		int2 lo, hi;
		float depth;
		if ( !ProjectSphere( center, radius, worldToProjection, viewport, depthRange, lo, hi, depth ) )
			return false;

		uint width, height, mips;
		chain.GetDimensions( 0, width, height, mips );

		float span = max( 1.0, max( hi.x - lo.x + 1, hi.y - lo.y + 1 ) );
		int level = clamp( (int)ceil( log2( span / ( OcclusionGrid - 1 ) ) ), firstLevel, firstLevel + (int)mips - 1 );
		int mip = level - firstLevel;

		uint mipWidth, mipHeight, mipCount;
		chain.GetDimensions( mip, mipWidth, mipHeight, mipCount );
		int2 a = lo >> level;
		int2 b = hi >> level;
		if ( b.x >= (int)mipWidth || b.y >= (int)mipHeight || any( b - a >= OcclusionGrid ) )
			return false;

		// Reverse-Z: hidden where the box's nearest depth is behind the farthest surface; the margin keeps coplanar ones
		[loop]
		for ( int y = a.y; y <= b.y; y++ )
		{
			[loop]
			for ( int x = a.x; x <= b.x; x++ )
			{
				if ( depth * 1.001 >= chain.Load( int3( x, y, mip ) ).r )
					return false;
			}
		}
		return true;
	}

	// This frame's chain, whose mip 0 is the viewport's size
	bool SphereOccluded( float3 center, float radius )
	{
		uint width, height, mips;
		ClutterDepthChain.GetDimensions( 0, width, height, mips );
		return SphereOccludedIn( ClutterDepthChain, 0, ClutterWorldToProjection, float2( width, height ), ClutterDepthRange, center, radius );
	}

	// Last frame's, where last frame's view saw the sphere
	bool SphereOccludedInHistory( float3 center, float radius )
	{
		return SphereOccludedIn( ClutterHistory, ClutterHistoryMip, ClutterHistoryWorldToProjection, ClutterHistoryViewport, ClutterHistoryDepthRange, center, radius );
	}

	// Matches the native LOD metric: screen coverage of a 0.5-radius sphere, then walk switch distances.
	uint ComputeLod( float3 worldPos, float scale, ClutterBatch_t batch )
	{
		float dist = length( worldPos - ClutterLodCameraPos );
		float tanHalf = max( ClutterLodTanHalfFov, 1e-5 );
		// Orthographic coverage depends on the view width, not distance from the camera.
		float screen = ClutterLodOrthoWidth > 0.0
			? saturate( 1.0 / ClutterLodOrthoWidth )
			: saturate( 0.5 / max( dist * tanHalf, 1e-5 ) );
		float pixels = screen * ClutterLodViewportWidth;
		float metric = ( pixels > 0.0 ) ? ( 50.0 / pixels ) : 0.0;

		int lod = max( (int)batch.LodCount - 1, 0 );
		[loop]
		while ( lod > 0 )
		{
			float d = batch.LodSwitchDistances[lod] * scale;
			if ( d > 0.0 && d < metric )
				break;
			lod--;
		}
		return (uint)lod;
	}

	uint CullInstance( uint id )
	{
		float4 sphere = AllInstanceSpheres[id]; // xyz = world center, w = world radius
		if ( sphere.w < 0.0 ) // Free slot.
			return Culled;

		uint batchIndex = AllInstanceBatches[id];
		ClutterBatch_t batch = ClutterBatches[batchIndex];

		if ( CullShadowCasters != 0 && batch.CastShadows == 0 )
			return Culled;

		if ( !SphereInFrustum( sphere.xyz, sphere.w ) )
			return Culled;
		if ( !SphereInCasterVolume( sphere.xyz, sphere.w ) )
			return Culled;

		if ( ClutterMaxDistance > 0.0 && length( sphere.xyz - ClutterLodCameraPos ) - sphere.w > ClutterMaxDistance )
			return Culled;

		// Last: they read depth, which most culled instances never need to
		if ( ClutterOcclusion != 0 && SphereOccluded( sphere.xyz, sphere.w ) )
			return Culled;
		if ( ClutterHistoryValid != 0 && SphereOccludedInHistory( sphere.xyz, sphere.w ) )
			return Culled;

		float scale = ( batch.ModelRadius > 1e-6 ) ? ( sphere.w / batch.ModelRadius ) : 1.0;
		return ComputeLod( sphere.xyz, scale, batch );
	}

	[numthreads( 64, 1, 1 )]
	void MainCs( uint3 vThreadId : SV_DispatchThreadID )
	{
		uint id = vThreadId.x;

	#if ( D_PASS == 0 )
		if ( id >= (uint)InstanceCount )
			return;

		uint lod = CullInstance( id );
		ClutterInstanceLods[id] = lod;

		if ( lod != Culled )
			InterlockedAdd( ClutterCounts[AllInstanceBatches[id] * 4 + lod], 1u );

	#elif ( D_PASS == 1 )
		// A few dozen batch LODs: one thread lays them out back to back.
		if ( id != 0 )
			return;

		uint first = 0;
		for ( uint b = 0; b < (uint)BatchCount; b++ )
		{
			ClutterBatch_t batch = ClutterBatches[b];
			for ( uint lod = 0; lod < batch.LodCount; lod++ )
			{
				uint counter = b * 4 + lod;
				uint count = ClutterCounts[counter];

				for ( uint d = 0; d < batch.DrawCount[lod]; d++ )
				{
					uint entry = batch.ArgsBase[lod] + d;
					ClutterArgs[entry].InstanceCount = count;
					ClutterArgs[entry].FirstInstance = first;
				}

				// The scatter pass appends from here.
				ClutterCounts[counter] = first;
				first += count;
			}
		}

	#elif ( D_PASS == 2 )
		if ( id >= (uint)InstanceCount )
			return;

		uint lod = ClutterInstanceLods[id];
		if ( lod == Culled )
			return;

		uint slot;
		InterlockedAdd( ClutterCounts[AllInstanceBatches[id] * 4 + lod], 1u, slot );
		ClutterVisible[slot] = AllInstances[id];

	#else
		uint width, height;
		ClutterHistoryOut.GetDimensions( width, height );
		if ( vThreadId.x >= width || vThreadId.y >= height )
			return;

		ClutterHistoryOut[vThreadId.xy] = ClutterDepthChain.Load( int3( vThreadId.xy, ClutterHistoryMip ) );
	#endif
	}
}
