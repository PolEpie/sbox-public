using System;
using System.Linq;
using Sandbox.Rendering;

namespace SceneTests;

/// <summary>
/// Decals, sky boxes and aggregates made without a native twin (<see cref="SceneObject.MakesNativeTwin"/> off, the game under
/// Borealis.Gpu) start with the state a twin would have given them, follow the same changes, and leave their world (and its
/// skies) when deleted.
/// </summary>
[TestClass]
public class SceneDecalSkyTwinTests
{
	static T Make<T>( SceneWorld world, Func<SceneWorld, T> make, bool twin ) where T : SceneObject
	{
		SceneObject.MakesNativeTwinOverride = twin;
		try { return make( world ); }
		finally { SceneObject.MakesNativeTwinOverride = null; }
	}

	static void Both<T>( T twin, T twinless, Action<T> change )
	{
		change( twin );
		change( twinless );
	}

	static void AssertSameObject( SceneObject twin, SceneObject twinless, string what )
	{
		Assert.IsTrue( twin.HasTwin, $"{what}: twin" );
		Assert.IsFalse( twinless.HasTwin, $"{what}: twinless" );
		Assert.IsTrue( twinless.IsValid(), $"{what}: valid" );
		Assert.AreEqual( twin.CurrentFlags, twinless.CurrentFlags, $"{what}: flags" );
		Assert.AreEqual( twin.RenderingEnabled, twinless.RenderingEnabled, $"{what}: visible" );
		Assert.AreEqual( twin.ColorTint, twinless.ColorTint, $"{what}: tint" );
		Assert.AreEqual( twin.Batchable, twinless.Batchable, $"{what}: batchable" );
		Assert.AreEqual( twin.WorldBounds, twinless.WorldBounds, $"{what}: bounds" );
		Assert.IsTrue( twin.Transform.AlmostEqual( twinless.Transform ), $"{what}: transform" );
		CollectionAssert.AreEqual( twin.TagTokens.ToArray(), twinless.TagTokens.ToArray(), $"{what}: tags" );
	}

	static void AssertSameDecal( DecalSceneObject twin, DecalSceneObject twinless, string what )
	{
		AssertSameObject( twin, twinless, what );
		Assert.AreEqual( twin.Color, twinless.Color, $"{what}: colour" );
		Assert.AreEqual( twin.ColorTexture, twinless.ColorTexture, $"{what}: colour texture" );
		Assert.AreEqual( twin.NormalTexture, twinless.NormalTexture, $"{what}: normal texture" );
		Assert.AreEqual( twin.SortOrder, twinless.SortOrder, $"{what}: sort order" );
		Assert.AreEqual( twin.ExclusionBitMask, twinless.ExclusionBitMask, $"{what}: exclusion" );
		Assert.AreEqual( twin.AttenuationAngle, twinless.AttenuationAngle, $"{what}: attenuation angle" );
		Assert.AreEqual( twin.ColorMix, twinless.ColorMix, $"{what}: colour mix" );
		Assert.AreEqual( twin.EmissionEnergy, twinless.EmissionEnergy, $"{what}: emission" );
		Assert.AreEqual( twin.SequenceIndex, twinless.SequenceIndex, $"{what}: sequence" );
		Assert.AreEqual( twin.ParallaxStrength, twinless.ParallaxStrength, $"{what}: parallax" );
		Assert.AreEqual( twin.SamplerIndex, twinless.SamplerIndex, $"{what}: sampler" );
		Assert.AreEqual( twin.CoverageAmount, twinless.CoverageAmount, $"{what}: coverage" );
		Assert.AreEqual( twin.CoverageRange, twinless.CoverageRange, $"{what}: coverage range" );
		Assert.AreEqual( twin.BlendMode, twinless.BlendMode, $"{what}: blend mode" );
	}

	[TestMethod]
	public void TwinlessDecalsSkiesAndAggregatesStartAndChangeAsTwinnedOnes()
	{
		var world = new SceneWorld();
		try
		{
			// A decal as the component makes and places it
			var decal = Make( world, w => new DecalSceneObject( w ), true );
			var decalless = Make( world, w => new DecalSceneObject( w ), false );
			AssertSameDecal( decal, decalless, "new decal" );
			Both( decal, decalless, d =>
			{
				d.Transform = new Transform( new Vector3( 10, -20, 30 ), Rotation.From( 30, 60, 10 ), new Vector3( 64, 32, 8 ) );
				d.Color = new Color( 1, 0.5f, 0.25f, 0.75f );
				d.SortOrder = 2;
				d.ExclusionBitMask = 4;
				d.AttenuationAngle = 0.5f;
				d.ColorMix = 0.25f;
				d.ParallaxStrength = 0.1f;
				d.CoverageAmount = 0.5f;
				d.BlendMode = DecalBlendMode.Multiply;
				d.Tags.Add( "decal" );
			} );
			AssertSameDecal( decal, decalless, "placed decal" );

			// A sky as the sky component makes it
			var material = Material.Load( "materials/skybox/skybox_day_01.vmat" );
			var sky = Make( world, w => new SceneSkyBox( w, material ), true );
			var skyless = Make( world, w => new SceneSkyBox( w, material ), false );
			AssertSameObject( sky, skyless, "new sky" );
			Assert.AreEqual( sky.Material, skyless.Material, "sky material" );
			Assert.AreEqual( sky.SkyTint, skyless.SkyTint, "sky tint" );
			Assert.AreEqual( sky.FogParams, skyless.FogParams, "sky fog" );
			lock ( world.Skies ) Assert.IsTrue( world.Skies.Contains( sky ) && world.Skies.Contains( skyless ), "in the world's skies" );
			Assert.AreSame( skyless, world.GetSky(), "the newest sky is the world's" );
			Both( sky, skyless, s => { s.SkyTint = Color.Red; s.FogParams = new SceneSkyBox.FogParamInfo { FogType = SceneSkyBox.FogType.Angular, FogMinStart = 1, FogMinEnd = 2, FogMaxStart = 3, FogMaxEnd = 4 }; } );
			AssertSameObject( sky, skyless, "changed sky" );
			Assert.AreEqual( sky.SkyTint, skyless.SkyTint, "changed sky tint" );
			Assert.AreEqual( sky.FogParams, skyless.FogParams, "changed sky fog" );

			// An aggregate as the aggregate renderer makes it
			var model = AggregateModel( 3 );
			var fragments = Enumerable.Range( 0, 3 ).Select( i => new AggregateFragment
			{
				Transform = new Transform( new Vector3( i * 100, 0, 0 ) ),
				BoundsMin = new Vector3( i * 100, 0, 0 ),
				BoundsMax = new Vector3( i * 100 + 32, 32, 0 ),
				Tint = Color.White,
				DrawDescriptorIndex = i,
			} ).ToArray();
			var aggregate = Make( world, w => new SceneAggregateObject( w, model, fragments, true ), true );
			var aggregateless = Make( world, w => new SceneAggregateObject( w, model, fragments, true ), false );
			AssertSameObject( aggregate, aggregateless, "new aggregate" );
			Assert.AreEqual( aggregate.Bounds, aggregateless.Bounds, "aggregate bounds" );
		}
		finally
		{
			world.Delete();
		}
	}

	[TestMethod]
	public void TwinlessSkiesAndAggregatesLeaveTheirWorldWhenDeleted()
	{
		var world = new SceneWorld();
		try
		{
			var material = Material.Load( "materials/skybox/skybox_day_01.vmat" );
			var sky = Make( world, w => new SceneSkyBox( w, material ), false );
			var fragments = new[] { new AggregateFragment { Transform = Transform.Zero, BoundsMax = new Vector3( 32 ), Tint = Color.White, DrawDescriptorIndex = 0 } };
			var aggregate = Make( world, w => new SceneAggregateObject( w, AggregateModel( 1 ), fragments, true ), false );
			var decal = Make( world, w => new DecalSceneObject( w ), false );

			sky.Delete();
			aggregate.Delete();
			decal.Delete();
			Assert.IsFalse( aggregate.IsValid() );
			aggregate.Delete(); // twice is fine, as before

			world.DeletePendingObjects();
			lock ( world.Skies ) Assert.IsFalse( world.Skies.Contains( sky ), "gone from the world's skies" );
			lock ( world.InternalSceneObjects )
			{
				Assert.IsFalse( world.InternalSceneObjects.Contains( sky ) );
				Assert.IsFalse( world.InternalSceneObjects.Contains( aggregate ) );
				Assert.IsFalse( world.InternalSceneObjects.Contains( decal ) );
			}
		}
		finally
		{
			world.Delete();
		}
	}

	static Model AggregateModel( int drawCalls )
	{
		var material = Material.Load( "materials/default/white.vmat" );
		var mesh = new Mesh( material );
		mesh.CreateVertexBuffer( 3, new Vertex[]
		{
			new() { Position = new Vector3( 0, 0, 0 ), Normal = Vector3.Up },
			new() { Position = new Vector3( 32, 0, 0 ), Normal = Vector3.Up },
			new() { Position = new Vector3( 0, 32, 0 ), Normal = Vector3.Up }
		} );
		mesh.CreateIndexBuffer( 3, new[] { 0, 1, 2 } );
		mesh.Bounds = new BBox( Vector3.Zero, new Vector3( 32, 32, 0 ) );
		for ( int i = 1; i < drawCalls; i++ )
			mesh.AddSubMesh( material, 0, 3, 0, 3 );

		return Model.Builder.AddMesh( mesh ).Create();
	}
}
