using System;
using System.Collections.Generic;
using static Sandbox.TerrainClipmap;

namespace SceneTests;

[TestClass]
public class TerrainClipmapCullerTest
{
	const int Levels = 7;
	const int ExtentCells = 256;
	const int BlockSize = 4;
	const float UnitsPerTexel = 48.828125f;
	const float HeightScale = 20000.0f;

	/// <summary>
	/// Culling by ring and group must keep exactly the blocks the per-block world-space test keeps when the terrain
	/// isn't rotated. A wrong group bound would drop an on-screen block (a hole) or keep an off-screen one.
	/// </summary>
	[TestMethod]
	public void MatchesPerBlockCullingForUnrotatedTerrain()
	{
		Span<Plane> planes = stackalloc Plane[6];

		foreach ( var transform in new[] { Transform.Zero, new Transform( new Vector3( 1000, -2000, 300 ), Rotation.Identity, 1.5f ) } )
		{
			var culler = new TerrainClipmapCuller( BuildLayout( Levels, ExtentCells, BlockSize ), BlockSize );
			var visible = new Meshlet[culler.Meshlets.Length];

			foreach ( var (frustum, camera) in Views( transform, seed: 1 ) )
			{
				TerrainClipmapCuller.ToLocalPlanes( frustum, transform, planes );
				int count = culler.Cull( planes, camera, UnitsPerTexel, HeightScale, visible );

				var expected = culler.Meshlets.Where( m => frustum.IsInside( WorldBounds( m, camera, transform ), partially: true ) ).Select( Key ).ToHashSet();
				var actual = visible.Take( count ).Select( Key ).ToHashSet();

				Assert.AreEqual( count, actual.Count, "No meshlet is emitted twice" );
				Assert.IsTrue( expected.SetEquals( actual ), $"Culled set differs from per-block culling: {expected.Except( actual ).Count()} missing, {actual.Except( expected ).Count()} extra" );
			}
		}
	}

	/// <summary>
	/// On rotated terrain the local-space test is tighter than testing each block's world-space AABB: it may drop
	/// blocks that AABB test kept, but only ones whose box really is entirely behind a frustum plane.
	/// </summary>
	[TestMethod]
	public void RotatedTerrainOnlyDropsBlocksBehindAPlane()
	{
		var transform = new Transform( new Vector3( 500, 700, -100 ), Rotation.FromYaw( 30 ) );
		var culler = new TerrainClipmapCuller( BuildLayout( Levels, ExtentCells, BlockSize ), BlockSize );
		var visible = new Meshlet[culler.Meshlets.Length];
		Span<Plane> planes = stackalloc Plane[6];

		foreach ( var (frustum, camera) in Views( transform, seed: 2 ) )
		{
			TerrainClipmapCuller.ToLocalPlanes( frustum, transform, planes );
			int count = culler.Cull( planes, camera, UnitsPerTexel, HeightScale, visible );
			var actual = visible.Take( count ).Select( Key ).ToHashSet();

			foreach ( var m in culler.Meshlets )
			{
				bool kept = actual.Contains( Key( m ) );
				bool aabbKept = frustum.IsInside( WorldBounds( m, camera, transform ), partially: true );

				if ( kept )
					Assert.IsTrue( aabbKept, "Kept a block the conservative AABB test culls" );
				else if ( aabbKept )
					Assert.IsTrue( IsBehindAPlane( frustum, LocalBounds( m, camera ), transform ), "Dropped a block that is in front of every plane" );
			}
		}
	}

	static (int Level, Vector2 Offset) Key( Meshlet m ) => (m.Level, m.BlockOffset);

	static IEnumerable<(Frustum Frustum, Vector2 Camera)> Views( Transform transform, int seed )
	{
		var random = new Random( seed );
		float Range( float min, float max ) => min + (float)random.NextDouble() * (max - min);

		for ( int i = 0; i < 64; i++ )
		{
			var local = new Vector3( Range( -200000, 200000 ), Range( -200000, 200000 ), Range( 0, 25000 ) );
			var position = transform.PointToWorld( local );
			var rotation = Rotation.From( Range( -70, 20 ), Range( 0, 360 ), 0 );

			// 90 degree horizontal fov at 16:9, matching the vertex shader's camera-relative placement
			var forward = rotation.Forward;
			var left = rotation.Left;
			var up = rotation.Up * (9.0f / 16.0f);

			var frustum = Frustum.FromCorners(
				new Ray( position, (forward + left + up).Normal ),
				new Ray( position, (forward - left + up).Normal ),
				new Ray( position, (forward - left - up).Normal ),
				new Ray( position, (forward + left - up).Normal ),
				1.0f, 500000.0f );

			yield return (frustum, new Vector2( local.x, local.y ));
		}
	}

	// The bounds the scene object tested before this culler: each block's local box, grown by the ring's snap increment
	static BBox LocalBounds( Meshlet m, Vector2 camera )
	{
		float vertexStep = UnitsPerTexel * (1 << m.Level);
		float increment = vertexStep * 2.0f;
		var center = camera.SnapToGrid( increment );

		float ox = center.x + m.BlockOffset.x * vertexStep;
		float oy = center.y + m.BlockOffset.y * vertexStep;
		float ext = BlockSize * vertexStep;

		return new BBox(
			new Vector3( ox - increment, oy - increment, -increment ),
			new Vector3( ox + ext + increment, oy + ext + increment, HeightScale + increment ) );
	}

	static BBox WorldBounds( Meshlet m, Vector2 camera, Transform transform ) => LocalBounds( m, camera ).Transform( transform );

	static bool IsBehindAPlane( Frustum frustum, BBox local, Transform transform )
	{
		var corners = Enumerable.Range( 0, 8 ).Select( i => transform.PointToWorld( new Vector3(
			(i & 1) == 0 ? local.Mins.x : local.Maxs.x,
			(i & 2) == 0 ? local.Mins.y : local.Maxs.y,
			(i & 4) == 0 ? local.Mins.z : local.Maxs.z ) ) ).ToArray();

		var planes = new[] { frustum.LeftPlane, frustum.RightPlane, frustum.TopPlane, frustum.BottomPlane, frustum.NearPlane, frustum.FarPlane };

		// A little slack: the local and world tests round differently right at a plane
		return planes.Any( p => corners.All( c => p.GetDistance( c ) <= 1.0f ) );
	}
}
