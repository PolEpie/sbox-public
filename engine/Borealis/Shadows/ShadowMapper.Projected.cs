using System.Numerics;
using System.Runtime.InteropServices;

namespace Borealis.Shadows;

internal partial class ShadowMapper
{
	internal static int ProjectedShadowsRendered { get; set; }
	internal static int ProjectedShadowsCulled { get; set; }
	internal static int ProjectedShadowsRenderedLastFrame { get; set; }
	internal static int ProjectedShadowsCulledLastFrame { get; set; }

	[StructLayout( LayoutKind.Sequential )]
	internal struct GPUProjectedShadow
	{
		public Matrix WorldToShadowMatrix;
		public int ShadowMapTextureIndex;
		public float InvShadowMapRes;
		public float ShadowHardness;
	}

	/// <summary>
	/// All projected shadows for this view
	/// </summary>
	List<GPUProjectedShadow> GPUProjectedShadows { get; set; } = new();

	/// <summary>
	/// Finds a cached shadow map or creates a new one.
	/// This is for a single shadow map like a spot light
	/// </summary>
	internal unsafe uint FindOrCreateProjectedShadowMap( in ShadowLight light, float flScreenSize )
	{
		// Cull shadows below the screen size threshold
		if ( flScreenSize < (SizeCullThreshold / 100.0f) )
		{
			ProjectedShadowsCulled++;
			return InvalidShadowIndex;
		}

		// Don't exceed GPU buffer capacity
		if ( GPUProjectedShadows.Count >= ProjectedShadowBufferSize )
		{
			ProjectedShadowsCulled++;
			return InvalidShadowIndex;
		}

		var cacheEntry = GetOrCreateCacheEntry( light, isCube: false, flScreenSize );

		// Already rendered this frame, or not this light's turn. A light filling the view never waits its turn. A frame in flight
		// drew it this frame: it's drawn again for this one, submitted first
		var inFlight = DrawnInFlight( cacheEntry );
		if ( !inFlight && (cacheEntry.RenderedFrame == FrameCount || (cacheEntry.RenderedFrame != 0 && !cacheEntry.Scheduled && cacheEntry.ScreenSize < 1f)) )
			return AddProjectedShadow( cacheEntry );

		Matrix ScaleBias = Matrix.Identity;
		ScaleBias._numerics[0, 0] = 0.5f;
		ScaleBias._numerics[1, 1] = -0.5f;
		ScaleBias._numerics[0, 3] = 0.5f;
		ScaleBias._numerics[1, 3] = 0.5f;

		float biasScale = ComputeBiasScale( light.ConeOuter, light.Radius, cacheEntry.CurrentResolution );

		var shadowView = new ShadowViewDesc
		{
			Name = cacheEntry.DebugName,
			Position = light.Position,
			Rotation = light.Rotation,
			FieldOfView = 2.0f * light.ConeOuter,
			ZNear = 1.0f,
			ZFar = light.Radius,
			Resolution = cacheEntry.CurrentResolution,
			DepthBias = (int)(ShadowDepthBias * biasScale),
			SlopeScaledDepthBias = ShadowSlopeScale * biasScale,
		};

		// Static lights, and ones that have kept still, draw their static and settled casters once into a cache that's copied in
		// each update, and only the casters that move are drawn on top
		if ( DrawsCache( light, cacheEntry, inFlight ) )
		{
			var staticView = shadowView;
			ToCache( light, cacheEntry, ref staticView );
			Renderer.RenderShadowView( staticView );
		}

		ToMap( light, cacheEntry, ref shadowView );

		Matrix viewProjection = Renderer.RenderShadowView( shadowView );

		GPUProjectedShadow shadow;
		shadow.WorldToShadowMatrix = ScaleBias * viewProjection.Transpose();
		shadow.ShadowMapTextureIndex = 0; // filled in by ResolveTextureIndices
		shadow.ShadowHardness = 1.0f + light.Hardness * 4.0f;
		shadow.InvShadowMapRes = 1.0f / (float)cacheEntry.ShadowMap.Resolution;

		cacheEntry.Projected = shadow;
		cacheEntry.RenderedFrame = FrameCount;
		cacheEntry.DrawnBy = this;
		Drawn.Add( cacheEntry );
		ProjectedShadowsRendered++;

		return AddProjectedShadow( cacheEntry );
	}

	uint AddProjectedShadow( LightEntry cacheEntry )
	{
		GPUProjectedShadows.Add( cacheEntry.Projected );
		ProjectedShadowMaps.Add( cacheEntry.ShadowMap );
		SpotEntries.Add( cacheEntry );
		ShadowsAllocated++;

		cacheEntry.LastFrame = RealTime.Now;
		cacheEntry.UsedFrame = FrameCount;

		// Return the index we just inserted
		var index = GPUProjectedShadows.Count - 1;
		cacheEntry.DebugLightIndex = index;
		return (uint)index;
	}
}
