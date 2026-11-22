using System;
using System.Collections.Generic;
using System.Linq;

namespace SceneTests;

/// <summary>
/// Scene objects made without a native twin (<see cref="SceneObject.MakesNativeTwin"/> off, the game under Borealis.Gpu) start
/// with the state a twin would have given them, follow the same changes, and leave their world when deleted.
/// </summary>
[TestClass]
public class SceneObjectTwinTests
{
	static readonly (string Name, Func<SceneWorld, SceneObject> Make)[] Kinds =
	[
		("box", w => new SceneObject( w, Model.Load( "models/dev/box.vmdl" ), new Transform( new Vector3( 10, 20, 30 ) ) )),
		("human object", w => new SceneObject( w, Model.Load( "models/citizen_human/human.vmdl" ) )),
		("human model", w => new SceneModel( w, Model.Load( "models/citizen_human/human.vmdl" ), new Transform( new Vector3( 5, 0, 0 ) ) )),
		("custom", w => new SceneCustomObject( w )),
		("dynamic", w => new SceneDynamicObject( w )),
		("line", w => new SceneLineObject( w )),
	];

	static SceneObject Make( SceneWorld world, Func<SceneWorld, SceneObject> make, bool twin )
	{
		SceneObject.MakesNativeTwinOverride = twin;
		try { return make( world ); }
		finally { SceneObject.MakesNativeTwinOverride = null; }
	}

	static void AssertSame( SceneObject twin, SceneObject twinless, string what )
	{
		Assert.IsTrue( twin.HasTwin, $"{what}: twin" );
		Assert.IsFalse( twinless.HasTwin, $"{what}: twinless" );
		Assert.IsTrue( twinless.IsValid(), $"{what}: valid" );
		Assert.AreEqual( twin.CurrentFlags, twinless.CurrentFlags, $"{what}: flags" );
		Assert.AreEqual( twin.RenderingEnabled, twinless.RenderingEnabled, $"{what}: visible" );
		Assert.AreEqual( twin.ColorTint, twinless.ColorTint, $"{what}: tint" );
		Assert.AreEqual( twin.LayerMatchToken, twinless.LayerMatchToken, $"{what}: layer match" );
		Assert.AreEqual( twin.LodOverride, twinless.LodOverride, $"{what}: lod" );
		Assert.AreEqual( twin.MeshGroupMask, twinless.MeshGroupMask, $"{what}: mesh groups" );
		Assert.AreEqual( twin.Batchable, twinless.Batchable, $"{what}: batchable" );
		Assert.AreEqual( twin.Model, twinless.Model, $"{what}: model" );
		Assert.AreEqual( twin.WorldBounds, twinless.WorldBounds, $"{what}: bounds" );
		Assert.IsTrue( twin.Transform.AlmostEqual( twinless.Transform ), $"{what}: transform" );
		CollectionAssert.AreEqual( twin.TagTokens.ToArray(), twinless.TagTokens.ToArray(), $"{what}: tags" );

		var a = new List<SceneObjectDraw>();
		var b = new List<SceneObjectDraw>();
		twin.GetDraws( a );
		twinless.GetDraws( b );
		CollectionAssert.AreEqual( a, b, $"{what}: draws" );
	}

	[TestMethod]
	public void TwinlessObjectsStartAndChangeAsTwinnedOnes()
	{
		var world = new SceneWorld();
		try
		{
			foreach ( var (name, make) in Kinds )
			{
				var twin = Make( world, make, true );
				var twinless = Make( world, make, false );
				AssertSame( twin, twinless, $"{name} made" );

				foreach ( var so in new[] { twin, twinless } )
				{
					so.Transform = new Transform( new Vector3( 100, -50, 25 ), Rotation.From( 10, 20, 30 ), 2.0f );
					so.ColorTint = new Color( 0.5f, 0.25f, 1.0f, 0.75f );
					so.Tags.Add( "solid" );
					so.Tags.Add( "prop" );
					so.Tags.Remove( "solid" );
					so.RenderLayer = SceneRenderLayer.OverlayWithDepth;
					so.LodOverride = 2;
					so.Batchable = false;
					so.Flags.CastShadows = false;
					if ( so.Model is null ) continue;
					so.MeshGroupMask = 1;
					so.SetMaterialOverride( Material.Load( "materials/default/white.vmat" ) );
				}
				AssertSame( twin, twinless, $"{name} changed" );

				if ( twin.Model is null ) continue;
				foreach ( var so in new[] { twin, twinless } )
				{
					so.ClearMaterialOverride();
					so.RenderingEnabled = false;
					so.Model = Model.Cube;
				}
				AssertSame( twin, twinless, $"{name} model changed" );
			}
		}
		finally
		{
			world.Delete();
		}
	}

	[TestMethod]
	public void TwinlessObjectsLeaveTheirWorldWhenDeleted()
	{
		var world = new SceneWorld();
		try
		{
			var kept = Make( world, Kinds[0].Make, false );
			var deleted = Make( world, Kinds[2].Make, false );
			lock ( world.InternalSceneObjects ) Assert.IsTrue( world.InternalSceneObjects.Contains( deleted ) );

			deleted.Delete();
			Assert.IsFalse( deleted.IsValid(), "invalid at once" );
			Assert.ThrowsException<NullReferenceException>( () => deleted.Position = Vector3.One, "a deleted object throws as before" );
			lock ( world.InternalSceneObjects ) Assert.IsTrue( world.InternalSceneObjects.Contains( deleted ), "in its world until its deletes run" );

			world.DeletePendingObjects();
			lock ( world.InternalSceneObjects )
			{
				Assert.IsFalse( world.InternalSceneObjects.Contains( deleted ), "gone after its world's deletes" );
				Assert.IsTrue( world.InternalSceneObjects.Contains( kept ) );
			}

			// Every world's deletes run at the frame's end, flushed or not
			var other = Make( world, Kinds[3].Make, false );
			other.Delete();
			SceneWorld.RunEndOfFrameDeletes();
			lock ( world.InternalSceneObjects ) Assert.IsFalse( world.InternalSceneObjects.Contains( other ) );
		}
		finally
		{
			world.Delete();
		}

		lock ( world.InternalSceneObjects ) Assert.AreEqual( 0, world.InternalSceneObjects.Count( x => !x.HasTwin ), "twinless objects go with their world" );
	}
}
