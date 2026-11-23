using Sandbox.Rendering;

namespace SceneTests;

[TestClass]
public class DecalTests
{
	[TestMethod]
	public void DefinitionsDefaultToAlphaBlending()
	{
		var definition = new DecalDefinition();
		Assert.AreEqual( DecalBlendMode.Alpha, definition.BlendMode );
		Assert.IsTrue( typeof( DecalBlendMode ).GetCustomAttributesData().Any( attribute => attribute.AttributeType.Name == "ExposeAttribute" ) );
	}

	[TestMethod]
	public void SceneObjectPublishesBlendModeWithoutChangingNativeLayout()
	{
		var scene = new Scene();
		using var scope = scene.Push();
		var decal = new DecalSceneObject( scene.SceneWorld );
		Assert.AreEqual( DecalBlendMode.Alpha, decal.BlendMode );
		decal.BlendMode = DecalBlendMode.Multiply;
		Assert.AreEqual( DecalBlendMode.Multiply, decal.BlendMode );
		decal.BlendMode = DecalBlendMode.Alpha;
		Assert.AreEqual( DecalBlendMode.Alpha, decal.BlendMode );
		decal.Delete();
		scene.Destroy();
	}

	/// <summary>
	/// A decal is managed only: no native object, native's old defaults for its settings, a unit box around its transform for
	/// bounds, and the renderer reads its settings.
	/// </summary>
	[TestMethod]
	public void SettingsAreManagedWithNativesDefaults()
	{
		var scene = new Scene();
		using var scope = scene.Push();
		try
		{
			var source = new DecalSceneObject( scene.SceneWorld );
			Assert.IsTrue( source.native.IsNull, "no native object" );
			Assert.IsTrue( source.IsValid() );
			Assert.AreEqual( Color.White, source.Color );
			Assert.AreEqual( 1.0f, source.AttenuationAngle );
			Assert.AreEqual( 1.0f, source.EmissionEnergy );
			Assert.AreEqual( 1.0f, source.ColorMix );
			Assert.AreEqual( 0.25f, source.ParallaxStrength );
			Assert.AreEqual( 1.0f, source.CoverageAmount );
			Assert.AreEqual( 0.07f, source.CoverageRange );
			Assert.IsNull( source.ColorTexture );
			Assert.IsFalse( source.Batchable );
			Assert.IsTrue( source.RenderingEnabled );

			source.Transform = new Transform( new Vector3( 10, 20, 30 ), Rotation.Identity, new Vector3( 64, 32, 8 ) );
			Assert.AreEqual( new BBox( new Vector3( -22, 4, 26 ), new Vector3( 42, 36, 34 ) ), source.WorldBounds, "a unit box around its transform" );

			source.ColorTexture = Texture.White;
			source.Color = Color.Blue;
			source.SortOrder = 9;
			source.CoverageRange = 0.5f;

			var decal = new Borealis.DecalObject( Transform.Zero );
			using var render = NewRender( scene.SceneWorld );
			source.WriteRender( decal, SceneObjectChange.Settings, (ISceneWorldRender)render );
			Assert.AreEqual( Color.Blue, decal.Tint, "the renderer reads the managed settings" );
			Assert.AreEqual( 9u, decal.SortOrder );
			Assert.AreEqual( 0.5f, decal.CoverageRange );
			source.Delete();
			Assert.IsFalse( source.IsValid() );
		}
		finally
		{
			scene.Destroy();
		}
	}

	/// <summary>A render of <paramref name="world"/> (it holds the textures its decals draw with), disposed by the caller.</summary>
	static System.IDisposable NewRender( SceneWorld world )
		=> (System.IDisposable)System.Activator.CreateInstance( System.Type.GetType( "Sandbox.Rendering.Bridge.WorldRender, Sandbox.Rendering", true ), [world] );

	[TestMethod]
	public void ManagedRendererFollowsBlendModeChanges()
	{
		var scene = new Scene();
		using var scope = scene.Push();
		try
		{
			var source = new DecalSceneObject( scene.SceneWorld );
			var decal = new Borealis.DecalObject( Transform.Zero );
			using var render = NewRender( scene.SceneWorld );
			foreach ( var mode in new[] { DecalBlendMode.Multiply, DecalBlendMode.Additive, DecalBlendMode.Alpha } )
			{
				source.BlendMode = mode;
				source.WriteRender( decal, SceneObjectChange.Settings, (ISceneWorldRender)render );
				Assert.AreEqual( (Borealis.DecalBlendMode)mode, decal.BlendMode );
			}
			source.Delete();
		}
		finally
		{
			scene.Destroy();
		}
	}
}
