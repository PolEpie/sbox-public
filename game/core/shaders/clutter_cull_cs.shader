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
	DynamicCombo( D_PASS, 0..2, Sys( ALL ) );

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
	int ClutterShadowPass < Attribute( "ClutterShadowPass" ); >;

	// Sun cascades: skip casters whose shadow can't reach a pixel that samples this cascade. The pixels are inside the
	// camera's side planes and the receiver sphere, and outside the excluded sphere (an earlier cascade's).
	int    ClutterCullReceivers    < Attribute( "ClutterCullReceivers" ); >;
	float3 ClutterReceiverLight    < Attribute( "ClutterReceiverLight" ); >; // the way the light travels
	float4 ClutterReceiverPlane0   < Attribute( "ClutterReceiverPlane0" ); >; // xyz = normal facing in, w = distance
	float4 ClutterReceiverPlane1   < Attribute( "ClutterReceiverPlane1" ); >;
	float4 ClutterReceiverPlane2   < Attribute( "ClutterReceiverPlane2" ); >;
	float4 ClutterReceiverPlane3   < Attribute( "ClutterReceiverPlane3" ); >;
	float4 ClutterReceiverSphere   < Attribute( "ClutterReceiverSphere" ); >; // xyz = center, w = radius
	float4 ClutterReceiverExcluded < Attribute( "ClutterReceiverExcluded" ); >; // w = 0 for none

	// Plain float4x4: attribute matrices are stored raw and read column-major, so the CPU uploads
	// the transpose and we consume it as mul( M, pos ). A row_major qualifier here breaks the planes.
	float4x4 ClutterWorldToProjection < Attribute( "ClutterWorldToProjection" ); >;

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

	// Clips the sphere's path along the light to the receiver region and checks something is left.
	// Matches ShadowReceiverRegion.MayShadow.
	bool MayShadowReceivers( float3 center, float radius )
	{
		float3 light = ClutterReceiverLight;

		// Where the path is within the receiver sphere grown by the caster's radius
		float3 toCenter = center - ClutterReceiverSphere.xyz;
		float reach = ClutterReceiverSphere.w + radius;
		float b = dot( toCenter, light );
		float discriminant = b * b - ( dot( toCenter, toCenter ) - reach * reach );
		if ( discriminant < 0.0 )
			return false;

		float root = sqrt( discriminant );
		float t0 = max( -b - root, 0.0 );
		float t1 = -b + root;

		// Then in front of every side plane, grown by the radius
		float4 planes[4] = { ClutterReceiverPlane0, ClutterReceiverPlane1, ClutterReceiverPlane2, ClutterReceiverPlane3 };

		[unroll]
		for ( int i = 0; i < 4; i++ )
		{
			float distance = dot( planes[i].xyz, center ) - planes[i].w + radius;
			float rate = dot( planes[i].xyz, light );

			if ( abs( rate ) < 1e-6 )
			{
				if ( distance < 0.0 )
					return false;
				continue;
			}

			float t = -distance / rate;
			if ( rate > 0.0 ) t0 = max( t0, t );
			else t1 = min( t1, t );
		}

		if ( t0 > t1 )
			return false;

		// Pixels in the earlier cascade's sphere never sample this one. The sphere is convex, so the path is inside
		// it if both ends are.
		float inner = ClutterReceiverExcluded.w - radius;
		if ( inner > 0.0 )
		{
			float3 a = center + light * t0 - ClutterReceiverExcluded.xyz;
			float3 e = center + light * t1 - ClutterReceiverExcluded.xyz;
			if ( dot( a, a ) <= inner * inner && dot( e, e ) <= inner * inner )
				return false;
		}

		return true;
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

		if ( ClutterShadowPass != 0 && batch.CastShadows == 0 )
			return Culled;

		if ( !SphereInFrustum( sphere.xyz, sphere.w ) )
			return Culled;

		if ( ClutterMaxDistance > 0.0 && length( sphere.xyz - ClutterLodCameraPos ) - sphere.w > ClutterMaxDistance )
			return Culled;

		if ( ClutterCullReceivers != 0 && !MayShadowReceivers( sphere.xyz, sphere.w ) )
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

	#else
		if ( id >= (uint)InstanceCount )
			return;

		uint lod = ClutterInstanceLods[id];
		if ( lod == Culled )
			return;

		uint slot;
		InterlockedAdd( ClutterCounts[AllInstanceBatches[id] * 4 + lod], 1u, slot );
		ClutterVisible[slot] = AllInstances[id];
	#endif
	}
}
