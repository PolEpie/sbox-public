using System.Numerics;
using System.Runtime.InteropServices;

namespace Borealis.Shadows;

internal partial class ShadowMapper
{
	static readonly TextureFormat LocalShadowDepthFormat = TextureFormat.D16;

	/// <summary>
	/// The cube faces, in the order the shader picks them (GetCubemapFace in ProjectedShadowCube.hlsl).
	/// </summary>
	internal static readonly Rotation[] CubeRotations =
	{
		Rotation.LookAt( Vector3.Backward, Vector3.Right ),
		Rotation.LookAt( Vector3.Forward, Vector3.Right ),
		Rotation.LookAt( Vector3.Right, Vector3.Up ),
		Rotation.LookAt( Vector3.Left, Vector3.Down ),
		Rotation.LookAt( Vector3.Down, Vector3.Right ),
		Rotation.LookAt( Vector3.Up, Vector3.Right )
	};

	[StructLayout( LayoutKind.Sequential )]
	internal struct GPUProjectedCubeShadow
	{
		public Matrix ShadowViewProjectionMatrix0;
		public Matrix ShadowViewProjectionMatrix1;
		public Matrix ShadowViewProjectionMatrix2;
		public Matrix ShadowViewProjectionMatrix3;
		public Matrix ShadowViewProjectionMatrix4;
		public Matrix ShadowViewProjectionMatrix5;
		public Vector3 LightPosition;
		public uint ShadowMapTextureCubeIndex;
		public float InvShadowMapRes;
		public float ShadowHardness;
	}

	/// <summary>
	/// All cube projected shadows (special case)
	/// </summary>
	List<GPUProjectedCubeShadow> GPUProjectedCubeShadows { get; set; } = new();

	static readonly string[] CubeFaceNames = ["Cube face 0", "Cube face 1", "Cube face 2", "Cube face 3", "Cube face 4", "Cube face 5"];

	internal unsafe uint FindOrCreateProjectedCubeShadowMap( in ShadowLight light, float flScreenSize )
	{
		// Don't exceed GPU buffer capacity
		if ( GPUProjectedCubeShadows.Count >= ProjectedCubeShadowBufferSize )
		{
			ProjectedShadowsCulled++;
			return InvalidShadowIndex;
		}

		var cacheEntry = GetOrCreateCacheEntry( light, isCube: true, flScreenSize );

		// Already rendered this frame, or not this light's turn. A light filling the view never waits its turn. A frame in flight
		// drew it this frame: it's drawn again for this one, submitted first
		var inFlight = DrawnInFlight( cacheEntry );
		if ( !inFlight && (cacheEntry.RenderedFrame == FrameCount || (cacheEntry.RenderedFrame != 0 && !cacheEntry.Scheduled && cacheEntry.ScreenSize < 1f)) )
			return AddProjectedCubeShadow( cacheEntry );

		// The entry may keep a bigger map than this view asked for
		int resolution = cacheEntry.CurrentResolution;

		GPUProjectedCubeShadow shadow = new();

		float biasScale = ComputeBiasScale( 45f, light.Radius, resolution );

		var shadowView = new ShadowViewDesc
		{
			Name = cacheEntry.DebugName,
			Position = light.Position,
			FieldOfView = 90.0f,
			ZNear = 1.0f,
			ZFar = light.Radius,
			Resolution = resolution,
			DepthBias = (int)(ShadowDepthBias * biasScale),
			SlopeScaledDepthBias = ShadowSlopeScale * biasScale,
		};

		// Static lights, and ones that have kept still, draw their static and settled casters once into a cache that's copied in
		// each update, and only the casters that move are drawn on top
		if ( DrawsCache( light, cacheEntry, inFlight ) )
		{
			var staticView = shadowView;
			ToCache( light, cacheEntry, ref staticView );
			for ( int i = 0; i < 6; i++ )
			{
				staticView.Rotation = CubeRotations[i];
				staticView.Slice = i;
				Renderer.RenderShadowView( staticView );
			}
		}

		// The cache is copied into the map once, on the first face
		ToMap( light, cacheEntry, ref shadowView );

		for ( int i = 0; i < 6; i++ )
		{
			shadowView.Rotation = CubeRotations[i];
			shadowView.Slice = i;
			Matrix viewProjection = Renderer.RenderShadowView( shadowView );

			// Set our matrix in the GPU struct
			((Matrix*)&shadow)[i] = viewProjection.Transpose();
		}

		shadow.ShadowMapTextureCubeIndex = 0; // filled in by ResolveTextureIndices
		shadow.LightPosition = light.Position;
		shadow.InvShadowMapRes = 1.0f / resolution;
		shadow.ShadowHardness = 1.0f + light.Hardness * 4.0f;

		cacheEntry.Cube = shadow;
		cacheEntry.RenderedFrame = FrameCount;
		cacheEntry.DrawnBy = this;
		Drawn.Add( cacheEntry );
		ProjectedShadowsRendered++;

		return AddProjectedCubeShadow( cacheEntry );
	}

	uint AddProjectedCubeShadow( LightEntry cacheEntry )
	{
		GPUProjectedCubeShadows.Add( cacheEntry.Cube );
		ProjectedCubeShadowMaps.Add( cacheEntry.ShadowMap );
		PointEntries.Add( cacheEntry );
		ShadowsAllocated++;

		cacheEntry.LastFrame = RealTime.Now;
		cacheEntry.UsedFrame = FrameCount;

		var index = GPUProjectedCubeShadows.Count - 1;
		cacheEntry.DebugLightIndex = index;
		return (uint)index;
	}
}
