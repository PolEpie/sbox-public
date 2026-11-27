using System.Collections.Generic;

namespace EngineTests;

/// <summary>
/// A run-time model (one built in code, read by the world renderer from its saved buffers) drawn in two worlds at once.
/// </summary>
[TestClass]
public class RuntimeModelWorldsTest
{
	/// <summary>
	/// Deleting one of the worlds leaves the model drawing in the other, frame after frame: the first world letting it go
	/// mustn't free the geometry the second still draws.
	/// </summary>
	[TestMethod]
	public void DeletingOneWorldKeepsTheOtherDrawing()
	{
		TestGraphics.Require();

		var mesh = new Mesh( Material.Load( "materials/default/white.vmat" ) );
		var vertices = new List<Vertex>
		{
			new( new Vector3( 0, -32, -32 ), Vector3.Backward, Vector3.Up, new Vector4( 0, 0, 0, 0 ) ),
			new( new Vector3( 0, 32, -32 ), Vector3.Backward, Vector3.Up, new Vector4( 1, 0, 0, 0 ) ),
			new( new Vector3( 0, 0, 32 ), Vector3.Backward, Vector3.Up, new Vector4( 0.5f, 1, 0, 0 ) ),
		};
		mesh.CreateVertexBuffer( vertices.Count, vertices );
		mesh.CreateIndexBuffer( 6, new List<int> { 0, 2, 1, 0, 1, 2 } );
		mesh.Bounds = new BBox( new Vector3( -1, -32, -32 ), new Vector3( 1, 32, 32 ) );
		var model = Model.Builder.AddMesh( mesh ).Create();
		Assert.IsTrue( model.IsProcedural );

		static int Drawn( SceneWorld world )
		{
			using var camera = new SceneCamera( "runtime model" ) { World = world, Position = new Vector3( -100, 0, 0 ), Rotation = Rotation.Identity, FieldOfView = 60, BackgroundColor = Color.Black };
			camera.DebugMode = SceneCameraDebugMode.FullBright;
			using var bitmap = new Bitmap( 64, 64 );
			for ( int frame = 0; frame < 10; frame++ )
			{
				System.Threading.Thread.Sleep( 20 );
				camera.RenderToBitmap( bitmap );
			}

			return bitmap.GetPixels().Count( p => p.r > 0.2f );
		}

		var first = new SceneWorld();
		var second = new SceneWorld();
		try
		{
			_ = new SceneModel( first, model, Transform.Zero );
			_ = new SceneModel( second, model, Transform.Zero );
			Assert.IsTrue( Drawn( first ) > 100, "the model draws in the first world" );
			Assert.IsTrue( Drawn( second ) > 100, "and in the second" );

			first.Delete();
			first = null;
			for ( int frame = 0; frame < 5; frame++ ) EngineLoop.DrainFrameEndDisposables();

			Assert.IsTrue( Drawn( second ) > 100, "the second world still draws it once the first is gone" );
		}
		finally
		{
			first?.Delete();
			second.Delete();
		}
	}

	[TestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void RuntimeMaterialIsUsedWithoutLoadingItsSavedName( bool asyncModels )
	{
		TestGraphics.Require();
		var gpu = Sandbox.Rendering.Bridge.MirrorContent.Gpu;
		var original = gpu.AsyncModels;
		var resolve = gpu.Content.Resolve;
		gpu.AsyncModels = asyncModels;
		try
		{
			var material = Material.Create( $"materials/runtime-model-test-{System.Guid.NewGuid():N}.vmat", "shaders/water.shader" );
			var mesh = new Mesh( material ) { Bounds = new BBox( Vector3.Zero, new Vector3( 0, 32, 32 ) ) };
			mesh.CreateVertexBuffer( 3, new List<Vertex>
			{
				new( Vector3.Zero, Vector3.Backward, Vector3.Up, new Vector4( 0, 0, 0, 0 ) ),
				new( Vector3.Left * 32, Vector3.Backward, Vector3.Up, new Vector4( 1, 0, 0, 0 ) ),
				new( Vector3.Up * 32, Vector3.Backward, Vector3.Up, new Vector4( 0, 1, 0, 0 ) ),
			} );
			mesh.CreateIndexBuffer( 3, new List<int> { 0, 2, 1 } );
			var model = Model.Builder.AddMesh( mesh ).Create();

			var saved = model.ReadRenderModel( null );
			var path = saved.MaterialFor( saved.Meshes.Single().DrawCalls.Single().Material, 0 );
			Assert.IsFalse( string.IsNullOrEmpty( path ), "the serialized draw names the runtime material" );
			var compiled = path.EndsWith( "_c", System.StringComparison.OrdinalIgnoreCase ) ? path : path + "_c";
			int probes = 0;
			gpu.Content.Resolve = name =>
			{
				if ( string.Equals( name, compiled, System.StringComparison.OrdinalIgnoreCase ) ) System.Threading.Interlocked.Increment( ref probes );
				return resolve?.Invoke( name );
			};

			Borealis.RenderMesh rendered = null;
			for ( int i = 0; i < 100; i++ )
			{
				rendered = Sandbox.Rendering.Bridge.MirrorContent.Mesh( model, out var pending );
				if ( !pending ) break;
				System.Threading.Thread.Sleep( 5 );
			}

			Assert.IsNotNull( rendered, "the procedural mesh is published" );
			var draw = rendered.Draws.Single();
			Assert.AreSame( Sandbox.Rendering.Bridge.MirrorContent.Material( material ), draw.Material );
			Assert.IsFalse( draw.ShadowFastPath, "water uses its own depth shader" );

			Assert.IsFalse( gpu.MaterialFallbacks.ContainsKey( path ), "the runtime name was never loaded as a compiled material" );
			Assert.AreEqual( 0, probes, "publication and prefetch never ask mounted content for this runtime material" );
		}
		finally
		{
			gpu.Content.Resolve = resolve;
			gpu.AsyncModels = original;
		}
	}
}
