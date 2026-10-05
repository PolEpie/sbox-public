using Sandbox.Rendering;
using System;

namespace SceneRendererTests;

[TestClass]
public class ShadowReceiverRegionTests
{
	/// <summary>
	/// A caster may only be skipped when no point it shadows lies in the region. Samples points inside each caster's
	/// shadow - its sphere swept along the light - and any that land in the region must keep the caster. Also checks
	/// the test isn't vacuous: casters behind the camera and over the earlier cascade get skipped.
	/// </summary>
	[TestMethod]
	public void NeverSkipsACasterThatShadowsTheRegion()
	{
		var random = new Random( 7 );
		float Range( float min, float max ) => min + (float)random.NextDouble() * (max - min);
		Vector3 InBall( float radius )
		{
			while ( true )
			{
				var v = new Vector3( Range( -1, 1 ), Range( -1, 1 ), Range( -1, 1 ) );
				if ( v.LengthSquared <= 1 ) return v * radius;
			}
		}

		int skipped = 0, total = 0;

		for ( int r = 0; r < 40; r++ )
		{
			var region = RandomRegion( random, Range );

			for ( int c = 0; c < 200; c++ )
			{
				var center = new Vector3( Range( -6000, 6000 ), Range( -6000, 6000 ), Range( -500, 2000 ) );
				float radius = Range( 10, 600 );
				bool kept = region.MayShadow( center, radius );

				total++;
				if ( !kept ) skipped++;
				if ( kept ) continue;

				for ( int s = 0; s < 400; s++ )
				{
					var shadowed = center + region.LightDirection * Range( 0, 20000 ) + InBall( radius );
					Assert.IsFalse( Contains( region, shadowed ), $"Skipped a caster at {center} r={radius} that shadows {shadowed}" );
				}
			}
		}

		Assert.IsTrue( skipped > total / 10, $"Only {skipped} of {total} casters were skipped" );
	}

	static ShadowReceiverRegion RandomRegion( Random random, Func<float, float, float> range )
	{
		// A camera at the origin looking down a random horizontal direction, 90 degrees wide
		var rotation = Rotation.From( range( -30, 10 ), range( 0, 360 ), 0 );
		var forward = rotation.Forward;
		var left = rotation.Left;
		var up = rotation.Up;

		var region = new ShadowReceiverRegion
		{
			LightDirection = new Vector3( range( -0.6f, 0.6f ), range( -0.6f, 0.6f ), -1 ).Normal,
			Left = FacingIn( new Plane( Vector3.Zero, forward + left, forward + left + up ), forward ),
			Right = FacingIn( new Plane( Vector3.Zero, forward - left, forward - left + up ), forward ),
			Top = FacingIn( new Plane( Vector3.Zero, forward + up, forward + up + left ), forward ),
			Bottom = FacingIn( new Plane( Vector3.Zero, forward - up, forward - up + left ), forward ),
			Sphere = new Vector4( forward * range( 1000, 4000 ), range( 2000, 5000 ) ),
		};

		if ( random.Next( 4 ) != 0 )
			region.Excluded = new Vector4( forward * range( 200, 1000 ), range( 500, 1500 ) );

		return region;
	}

	static Plane FacingIn( Plane plane, Vector3 inside ) => plane.GetDistance( inside ) >= 0
		? plane
		: new Plane { Normal = -plane.Normal, Distance = -plane.Distance };

	static bool Contains( in ShadowReceiverRegion region, Vector3 point )
	{
		for ( int i = 0; i < 4; i++ )
		{
			if ( region.GetPlane( i ).GetDistance( point ) < 0 )
				return false;
		}

		var sphere = new Vector3( region.Sphere.x, region.Sphere.y, region.Sphere.z );
		if ( point.Distance( sphere ) > region.Sphere.w )
			return false;

		var excluded = new Vector3( region.Excluded.x, region.Excluded.y, region.Excluded.z );
		return region.Excluded.w <= 0 || point.Distance( excluded ) > region.Excluded.w;
	}
}
