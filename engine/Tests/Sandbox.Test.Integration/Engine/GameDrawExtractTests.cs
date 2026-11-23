using NativeEngine;
using Sandbox.Rendering;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace EngineTests;

/// <summary>
/// A game's own custom objects recorded in the extract on the main thread and replayed where the frame draws them
/// (<see cref="GameDrawExtract"/>, docs/reference/borealis-render-thread.md): the render thread runs no game code, and a
/// replay draws what the drawing would have drawn.
/// </summary>
[TestClass]
[DoNotParallelize]
public class GameDrawExtractTests
{
	/// <summary>A custom object of the game's own type: it counts its drawings and what it was asked to draw in.</summary>
	sealed class DrawnByTheGame( SceneWorld world ) : SceneCustomObject( world )
	{
		public int Calls;
		public Action Draw;

		public override void RenderSceneObject()
		{
			Calls++;
			Draw?.Invoke();
		}
	}

	// Run draw as a game's drawing recorded into recording, as the extract runs it
	static void Record( DrawRecording recording, Action draw, SceneLayerType layer = SceneLayerType.Opaque )
	{
		var backend = new RecordingRenderBackend();
		using var view = new RecordingFrameView { Into = recording, Facts = new GameDrawExtract.ViewFacts { CubeFace = -1, PostProcessEnabled = true } };
		view.Frustum.InitCamera( Vector3.Zero, Angles.Zero, 1, 1000, 90, 1 );
		backend.Begin( recording, new RenderViewport( new Rect( 0, 0, 64, 64 ), 0, 1 ) );
		try
		{
			using var scope = Graphics.RecordingScope.Create( backend, view, recording, layer, ImageFormat.RGBA16161616F, MultisampleAmount.MultisampleNone );
			draw();
		}
		finally
		{
			backend.End();
		}
	}

	/// <summary>
	/// What a recorded drawing passes is kept as it was at each call: its vertices copied, and an attribute set it changes
	/// between two draws seen as each draw saw it, though the game changes both after. The replay (here into a second
	/// recording, which shows what it was given) draws with those.
	/// </summary>
	[TestMethod]
	public void RecordedDrawsKeepWhatEachCallWasGiven()
	{
		ThreadSafe.MarkMainThread();
		var material = Material.Load( "materials/dev/primary_white.vmat" );
		Assert.IsNotNull( material );

		var vertices = new Vertex[3];
		vertices[0].Position = new Vector3( 1, 2, 3 );
		var attributes = RenderAttributes.CreateManaged();

		var recording = new DrawRecording();
		Record( recording, () =>
		{
			attributes.Set( "Tint", 1f );
			Graphics.Draw( vertices.AsSpan(), 3, material, attributes );
			attributes.Set( "Tint", 2f );
			Graphics.Draw( vertices.AsSpan(), 3, material, attributes );
			Graphics.Attributes.Set( "Own", 5f );
			Graphics.Draw( vertices.AsSpan(), 3, material );
			Graphics.Viewport = new Rect( 0, 0, 32, 32 );
		} );

		// The game goes on changing what it drew with
		vertices[0].Position = new Vector3( 9, 9, 9 );
		attributes.Set( "Tint", 3f );
		Assert.AreEqual( 4, recording.Count, "three draws and a viewport" );

		var replayed = new DrawRecording();
		Record( replayed, recording.Replay );
		var entries = replayed.Entries;
		Assert.AreEqual( 4, entries.Length );
		Assert.AreEqual( DrawRecording.Op.DrawVertices, entries[0].Op );

		var first = MemoryMarshal.Read<Vertex>( replayed.Bytes( entries[0].I1, Marshal.SizeOf<Vertex>() ) );
		Assert.AreEqual( new Vector3( 1, 2, 3 ), first.Position, "the vertices as they were drawn" );
		Assert.AreEqual( 1f, entries[0].Attributes.GetFloat( "Tint" ), "the first draw's attributes" );
		Assert.AreEqual( 2f, entries[1].Attributes.GetFloat( "Tint" ), "the second draw's" );
		Assert.AreEqual( 5f, entries[2].Attributes.GetFloat( "Own" ), "the block's attributes, which the game set" );
		Assert.AreEqual( DrawRecording.Op.ViewportRect, entries[3].Op );
		Assert.AreEqual( 32f, entries[3].Rect.Width );

		recording.Clear();
		replayed.Clear();
		Assert.AreEqual( 0, recording.Count );
	}

	/// <summary>
	/// A pooled target a recorded drawing takes and gives back stays the drawing's until the recording is let go (the
	/// replay draws into it later): the pool doesn't lend it to anyone else meanwhile.
	/// </summary>
	[TestMethod]
	public void TargetsGivenBackWhileRecordedStayUntilTheReplay()
	{
		ThreadSafe.MarkMainThread();
		var recording = new DrawRecording();
		RenderTarget taken = null;
		Record( recording, () =>
		{
			taken = RenderTarget.GetTemporary( 8, 8, ImageFormat.RGBA8888, ImageFormat.None, targetName: "recorded" );
			taken.Dispose();
		} );

		Assert.AreEqual( 1, recording.Returned, "kept for the replay" );
		using ( var meanwhile = RenderTarget.GetTemporary( 8, 8, ImageFormat.RGBA8888, ImageFormat.None, targetName: "recorded" ) )
			Assert.AreNotSame( taken, meanwhile, "not lent to anyone else before the replay" );

		recording.Clear();
		using var after = RenderTarget.GetTemporary( 8, 8, ImageFormat.RGBA8888, ImageFormat.None, targetName: "recorded" );
		Assert.AreSame( taken, after, "back in the pool once the recording is let go" );
	}

	/// <summary>
	/// A game's custom object drawn on the render thread with nothing recorded for it draws nothing and runs no game code
	/// (a miss, recorded by the next extract); on the main thread it runs its drawing as before. The engine's own custom
	/// objects aren't the game's.
	/// </summary>
	[TestMethod]
	public void GameCodeNeverRunsOnTheRenderThread()
	{
		ThreadSafe.MarkMainThread();
		var world = new SceneWorld();
		try
		{
			var custom = new DrawnByTheGame( world );
			Assert.IsTrue( custom.IsGameDrawn, "a game's own type" );
			Assert.IsFalse( new SceneCustomObject( world ).IsGameDrawn, "the engine's, without an override" );
			Assert.IsTrue( new SceneCustomObject( world ) { RenderOverride = _ => { } }.IsGameDrawn, "an override is game code" );

			var misses = GameDrawExtract.Misses;
			RenderSync.MarkRenderThread( true );
			try
			{
				custom.RenderInternal();
			}
			finally
			{
				RenderSync.MarkRenderThread( false );
			}

			Assert.AreEqual( 0, custom.Calls, "no game code on the render thread" );
			Assert.AreEqual( misses + 1, GameDrawExtract.Misses, "counted as a miss" );

			custom.RenderInternal();
			Assert.AreEqual( 1, custom.Calls, "the main thread runs it" );
		}
		finally
		{
			world.Delete();
		}
	}

	/// <summary>
	/// A synchronous readback in a recorded drawing reads on the main thread and is counted as the drawing's sync point.
	/// </summary>
	[TestMethod]
	public void ReadbacksInARecordedDrawingAreSyncPoints()
	{
		ThreadSafe.MarkMainThread();
		var before = GameDrawExtract.SyncPoints;
		Record( new DrawRecording(), () =>
		{
			using var readback = RenderSync.Readback( "a test's readback" );
		} );

		Assert.AreEqual( before + 1, GameDrawExtract.SyncPoints );
	}

	/// <summary>
	/// A scene with a model and a game custom object drawing a model of its own, lit from its world, through
	/// <see cref="Graphics"/>: drawn as before (the drawing run in the frame), then with the extract recording it and the frame
	/// replaying it as the render thread does. The pictures are the same, the replayed frames run no game code, and each
	/// replays what its extract recorded.
	/// </summary>
	[TestMethod]
	public void ReplayedCustomObjectsDrawAsTheirDrawingDoes()
	{
		TestGraphics.Require();
		ThreadSafe.MarkMainThread();

		const int size = 64;
		var model = Model.Load( "models/dev/box.vmdl" );
		var offset = model.Bounds.Size.y;
		var world = new SceneWorld();
		try
		{
			_ = new SceneDirectionalLight( world, Rotation.From( 45, 30, 0 ), Color.White );
			_ = new SceneModel( world, model, new Transform( new Vector3( 0, offset, 0 ) ) );
			var at = new Transform( new Vector3( 0, -offset, 0 ), Rotation.From( 0, 30, 0 ) );
			var tint = RenderAttributes.CreateManaged();
			var custom = new DrawnByTheGame( world ) { Transform = at, Bounds = model.Bounds.Transform( at ) };
			custom.Flags.IsOpaque = true;
			custom.Flags.IsTranslucent = false;
			custom.Draw = () =>
			{
				Graphics.SetupLighting( custom );
				tint.Set( "g_vColorTint", new Color( 1, 0.5f, 0.25f ) );
				Graphics.DrawModel( model, at, tint );
			};

			using var camera = new SceneCamera( "extract" ) { World = world, Position = new Vector3( -offset * 4, 0, offset ), Rotation = Rotation.From( 10, 0, 0 ), FieldOfView = 60, BackgroundColor = Color.Black };
			using var bitmap = new Bitmap( size, size );
			var cameras = new List<SceneCamera> { camera };

			// As before: the drawing runs in the frame
			for ( int i = 0; i < 10; i++ )
			{
				System.Threading.Thread.Sleep( 20 );
				camera.RenderToBitmap( bitmap );
			}

			var direct = bitmap.GetPixels32();
			Assert.IsTrue( custom.Calls > 0, "drawn" );

			// The extract first sees it new: that frame draws on the main thread and learns what it draws in
			GameDrawExtract.Extract( cameras, record: true );
			Assert.AreEqual( "a new custom object", GameDrawExtract.NotCovered );
			GameDrawExtract.Learning( true );
			try
			{
				camera.RenderToBitmap( bitmap );
			}
			finally
			{
				GameDrawExtract.Learning( false );
			}

			var replays = GameDrawExtract.Replays;
			var misses = GameDrawExtract.Misses;
			for ( int i = 0; i < 5; i++ )
			{
				GameDrawExtract.Extract( cameras, record: true );
				Assert.IsNull( GameDrawExtract.NotCovered, "recorded: the frame needs no main thread" );

				var calls = custom.Calls;
				RenderSync.MarkRenderThread( true );
				try
				{
					camera.RenderToBitmap( bitmap );
				}
				finally
				{
					RenderSync.MarkRenderThread( false );
				}

				Assert.AreEqual( calls, custom.Calls, "no game code in a replayed frame" );
			}

			var replayed = bitmap.GetPixels32();
			Assert.IsTrue( GameDrawExtract.Replays > replays, "replayed" );
			Assert.AreEqual( misses, GameDrawExtract.Misses, "nothing asked for that wasn't recorded" );

			var differing = 0;
			for ( int i = 0; i < direct.Length; i++ )
			{
				if ( !direct[i].Equals( replayed[i] ) ) differing++;
			}

			Assert.AreEqual( 0, differing, "the same picture replayed as drawn" );

			// Not lost: the right half (the custom object) has its tinted model in it
			var right = 0;
			for ( int y = 0; y < size; y++ )
			{
				for ( int x = size / 2; x < size; x++ )
				{
					if ( replayed[y * size + x].r > 8 ) right++;
				}
			}

			Assert.IsTrue( right > 20, $"the custom object drew ({right} pixels)" );
		}
		finally
		{
			GameDrawExtract.Extract( new List<SceneCamera>(), record: false );
			world.Delete();
		}
	}
}
