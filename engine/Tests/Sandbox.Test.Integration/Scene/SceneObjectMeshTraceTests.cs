using System;
using System.Collections.Generic;
using Sandbox.Engine.Utility.RayTrace;

namespace SceneTests;

/// <summary>
/// Render-mesh traces of a world's scene objects run in managed code, each object's model traced in its own space. They give
/// what native's world trace through the objects' native twins gives. Render meshes have trace triangles only in tools mode, so
/// headless runs compare two empty traces; an editor probe compares them on a real map.
/// </summary>
[TestClass]
public class SceneObjectMeshTraceTests
{
	static SceneWorld MakeWorld( out List<SceneObject> objects )
	{
		var world = new SceneWorld();
		var box = Model.Load( "models/dev/box.vmdl" );
		var human = Model.Load( "models/citizen_human/human.vmdl" );

		objects =
		[
			new SceneObject( world, box, new Transform( new Vector3( 0, 0, 0 ) ) ),
			new SceneObject( world, box, new Transform( new Vector3( 80, 10, 5 ), Rotation.From( 30, 45, 10 ), 1.5f ) ),
			new SceneObject( world, box, new Transform( new Vector3( 160, -20, 0 ), Rotation.From( 0, 90, 0 ), new Vector3( 0.5f, 2.0f, 1.0f ) ) ),
			new SceneObject( world, human, new Transform( new Vector3( 240, 0, -30 ), Rotation.From( 0, 180, 0 ) ) ),
			new SceneObject( world, Model.Cube, new Transform( new Vector3( 320, 0, 0 ), Rotation.Identity, 20.0f ) ),
			new SceneObject( world, box, new Transform( new Vector3( 400, 0, 0 ) ) ),
			new SceneObject( world, box, new Transform( new Vector3( 480, 0, 0 ) ) ),
		];

		objects[0].Tags.Add( "solid" );
		objects[1].Tags.Add( "solid" );
		objects[1].Tags.Add( "prop" );
		objects[3].Tags.Add( "player" );
		objects[5].RenderingEnabled = false;
		objects[6].Flags.ViewModelLayer = true;
		return world;
	}

	static IEnumerable<(Vector3 From, Vector3 To)> Rays()
	{
		var random = new Random( 1234 );
		for ( int i = 0; i < 400; i++ )
		{
			var target = new Vector3( random.Float( -40, 520 ), random.Float( -60, 60 ), random.Float( -60, 80 ) );
			var direction = new Vector3( random.Float( -1, 1 ), random.Float( -1, 1 ), random.Float( -1, 1 ) ).Normal;
			var from = target + direction * random.Float( 100, 600 );
			var to = from + (target - from) * random.Float( 1.0f, 2.0f );
			yield return (from, to);
		}

		// Along the row, through everything
		yield return (new Vector3( -200, 0, 10 ), new Vector3( 700, 0, 10 ));
		yield return (new Vector3( 700, 1, 20 ), new Vector3( -200, 1, 20 ));
	}

	static MeshTraceRequest.Result[] Run( Func<MeshTraceRequest> request, bool native, bool all )
	{
		MeshTraceRequest.TracesNativeTwins = native;
		try
		{
			return all ? request().RunAll() : [request().Run()];
		}
		finally
		{
			MeshTraceRequest.TracesNativeTwins = false;
		}
	}

	static void AssertSame( MeshTraceRequest.Result expected, MeshTraceRequest.Result actual, string what )
	{
		Assert.AreEqual( expected.Hit, actual.Hit, $"{what}: hit" );
		if ( !expected.Hit ) return;

		Assert.AreSame( expected.SceneObject, actual.SceneObject, $"{what}: object" );
		Assert.AreEqual( expected.Distance, actual.Distance, 0.02f, $"{what}: distance" );
		Assert.AreEqual( expected.Fraction, actual.Fraction, 0.0001f, $"{what}: fraction" );
		Assert.IsTrue( expected.HitPosition.Distance( actual.HitPosition ) < 0.02f, $"{what}: position {expected.HitPosition} {actual.HitPosition}" );
		Assert.IsTrue( expected.Normal.Distance( actual.Normal ) < 0.001f, $"{what}: normal {expected.Normal} {actual.Normal}" );
		Assert.AreEqual( expected.HitTriangle, actual.HitTriangle, $"{what}: triangle" );
		Assert.AreEqual( expected.Material, actual.Material, $"{what}: material" );
		Assert.IsTrue( expected.Transform.AlmostEqual( actual.Transform, 0.0001f ), $"{what}: transform" );
		Assert.IsTrue( expected.Vertex0.Position.Distance( actual.Vertex0.Position ) < 0.001f, $"{what}: vertex" );
		Assert.IsTrue( expected.HitTriangleUv.Distance( actual.HitTriangleUv ) < 0.001f, $"{what}: uv" );
	}

	static void Compare( Func<Vector3, Vector3, MeshTraceRequest> make, string name, out int hits )
	{
		hits = 0;
		foreach ( var (from, to) in Rays() )
		{
			foreach ( var all in new[] { false, true } )
			{
				var native = Run( () => make( from, to ), true, all );
				var managed = Run( () => make( from, to ), false, all );
				var what = $"{name} {(all ? "all" : "first")} {from} -> {to}";

				Assert.AreEqual( native.Length, managed.Length, $"{what}: hit count" );
				for ( int i = 0; i < native.Length; i++ ) AssertSame( native[i], managed[i], $"{what} [{i}]" );
				if ( !all && native[0].Hit ) hits++;
			}
		}
	}

	[TestMethod]
	public void ManagedTraceMatchesNativeTwins()
	{
		var world = MakeWorld( out _ );
		try
		{
			Compare( ( a, b ) => world.Trace.Ray( a, b ), "plain", out var hits );
			if ( hits == 0 ) Assert.Inconclusive( "Render meshes carry trace triangles only in tools mode (CRenderMeshManager): nothing to hit here" );
			Assert.IsTrue( hits > 50, $"only {hits} rays hit" );
		}
		finally
		{
			world.Delete();
		}
	}

	[TestMethod]
	public void ManagedTraceFiltersAsNative()
	{
		var world = MakeWorld( out var objects );
		try
		{
			Compare( ( a, b ) => world.Trace.Ray( a, b ).WithTag( "solid" ), "with solid", out _ );
			Compare( ( a, b ) => world.Trace.Ray( a, b ).WithAllTags( "solid", "prop" ), "with solid and prop", out _ );
			Compare( ( a, b ) => world.Trace.Ray( a, b ).WithAnyTags( "prop", "player" ), "with prop or player", out _ );
			Compare( ( a, b ) => world.Trace.Ray( a, b ).WithoutTags( "solid" ), "without solid", out _ );
			Compare( ( a, b ) => { var t = world.Trace.Ray( a, b ); t.filterCallback = o => o != objects[4]; return t; }, "filter", out _ );
		}
		finally
		{
			world.Delete();
		}
	}

	[TestMethod]
	public void ManagedTraceFollowsChanges()
	{
		var world = MakeWorld( out var objects );
		try
		{
			objects[0].Position = new Vector3( 0, 30, 20 );
			objects[1].Rotation = Rotation.From( 0, 10, 0 );
			objects[2].Tags.Add( "solid" );
			objects[0].Tags.Remove( "solid" );
			objects[5].RenderingEnabled = true;
			objects[3].RenderingEnabled = false;
			Compare( ( a, b ) => world.Trace.Ray( a, b ), "moved", out _ );
			Compare( ( a, b ) => world.Trace.Ray( a, b ).WithTag( "solid" ), "moved with solid", out _ );

			objects[4].Delete();
			Compare( ( a, b ) => world.Trace.Ray( a, b ), "deleted", out _ );
		}
		finally
		{
			world.Delete();
		}
	}
}
