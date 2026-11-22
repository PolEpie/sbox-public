using System;
using System.Reflection;

namespace SceneTests;

/// <summary>
/// Lights, cubemaps and probe volumes made without a native twin (<see cref="SceneObject.MakesNativeTwin"/> off, the game under
/// Borealis.Gpu) start with the description, flags and bounds a twin would have given them, follow the same changes, and pack
/// for the light binner exactly as native packs the twin (<c>RenderTools.PackSceneLight</c>, <c>PackSceneEnvMap</c>).
/// </summary>
[TestClass]
public class SceneLightTwinTests
{
	static readonly Type Packing = Type.GetType( "Sandbox.Rendering.LightPacking, Sandbox.Engine", true );
	static readonly Type Native = Type.GetType( "Sandbox.Rendering.Bridge.SceneNative, Sandbox.Rendering", true );
	static readonly Type EnvMapObject = Type.GetType( "Borealis.EnvMapObject, Borealis", true );

	static T Make<T>( SceneWorld world, Func<SceneWorld, T> make, bool twin ) where T : SceneObject
	{
		SceneObject.MakesNativeTwinOverride = twin;
		try { return make( world ); }
		finally { SceneObject.MakesNativeTwinOverride = null; }
	}

	static void AssertClose( float expected, float actual, string what )
	{
		var tolerance = MathF.Max( 1e-5f, MathF.Abs( expected ) * 1e-5f );
		Assert.IsTrue( expected == actual || MathF.Abs( expected - actual ) <= tolerance, $"{what}: twin {expected}, twinless {actual}" );
	}

	/// <summary>Every public field of two structs alike: floats and vectors within a float's rounding, the rest exactly.</summary>
	static void AssertFields( object twin, object twinless, string what, params string[] skip )
	{
		foreach ( var field in twin.GetType().GetFields( BindingFlags.Instance | BindingFlags.Public ) )
		{
			if ( Array.IndexOf( skip, field.Name ) >= 0 ) continue;
			var a = field.GetValue( twin );
			var b = field.GetValue( twinless );
			var name = $"{what}: {field.Name}";
			switch ( a )
			{
				case float fa: AssertClose( fa, (float)b, name ); break;
				case Vector2 va: AssertClose( va.x, ((Vector2)b).x, name + ".x" ); AssertClose( va.y, ((Vector2)b).y, name + ".y" ); break;
				case Vector3 v3: { var m = (Vector3)b; AssertClose( v3.x, m.x, name + ".x" ); AssertClose( v3.y, m.y, name + ".y" ); AssertClose( v3.z, m.z, name + ".z" ); break; }
				case Vector4 v4: { var m = (Vector4)b; AssertClose( v4.x, m.x, name + ".x" ); AssertClose( v4.y, m.y, name + ".y" ); AssertClose( v4.z, m.z, name + ".z" ); AssertClose( v4.w, m.w, name + ".w" ); break; }
				default: Assert.AreEqual( a, b, name ); break;
			}
		}
	}

	static void AssertSameObject( SceneObject twin, SceneObject twinless, string what )
	{
		Assert.IsTrue( twin.HasTwin, $"{what}: twin" );
		Assert.IsFalse( twinless.HasTwin, $"{what}: twinless" );
		Assert.IsTrue( twinless.IsValid(), $"{what}: valid" );
		Assert.AreEqual( twin.CurrentFlags, twinless.CurrentFlags, $"{what}: flags" );
		Assert.AreEqual( twin.RenderingEnabled, twinless.RenderingEnabled, $"{what}: visible" );
		Assert.AreEqual( twin.ColorTint, twinless.ColorTint, $"{what}: tint" );
		Assert.AreEqual( twin.WorldBounds, twinless.WorldBounds, $"{what}: bounds" );
		Assert.IsTrue( twin.Transform.AlmostEqual( twinless.Transform ), $"{what}: transform" );
		lock ( twin.World.InternalSceneObjects )
		{
			Assert.AreEqual( twin.World.InternalSceneLights.Contains( twin as SceneLight ), twinless.World.InternalSceneLights.Contains( twinless as SceneLight ), $"{what}: in its world's lights" );
		}
	}

	static void AssertSameLight( SceneLight twin, SceneLight twinless, string what )
	{
		AssertSameObject( twin, twinless, what );
		AssertFields( twin.Description, twinless.Description, $"{what} description" );

		// The twinless light packed here, against native packing the twin
		object[] native = [twin, null, 0u, 0];
		Assert.IsTrue( (bool)Native.GetMethod( "PackSceneLight", BindingFlags.Static | BindingFlags.Public ).Invoke( null, native ), $"{what}: native packs it" );
		object[] managed = [twinless, null, 0u, 0];
		Packing.GetMethods( BindingFlags.Static | BindingFlags.Public ).Single( m => m.Name == "Pack" && m.GetParameters()[0].ParameterType == typeof( SceneLight ) ).Invoke( null, managed );
		Assert.AreEqual( (uint)native[2], (uint)managed[2], $"{what}: light type flags" );
		Assert.AreEqual( (int)native[3], (int)managed[3], $"{what}: bake index" );
		AssertFields( native[1], managed[1], $"{what} packed", "LightCookieTextureIndex" );
	}

	static void AssertSameCubemap( SceneCubemap twin, SceneCubemap twinless, string what, bool priority = true )
	{
		AssertSameObject( twin, twinless, what );
		Assert.AreEqual( twin.Projection, twinless.Projection, $"{what}: projection" );
		Assert.AreEqual( twin.ProjectionBounds, twinless.ProjectionBounds, $"{what}: box" );
		Assert.AreEqual( twin.TintColor, twinless.TintColor, $"{what}: tint" );
		Assert.AreEqual( twin.Feathering, twinless.Feathering, $"{what}: feathering" );
		Assert.AreEqual( twin.Texture, twinless.Texture, $"{what}: texture" );
		AssertFields( twin.Description, twinless.Description, $"{what} description" );
		if ( !priority ) return; // native leaves a new cubemap's priority unset
		Assert.AreEqual( twin.Priority, twinless.Priority, $"{what}: priority" );

		// The twinless cubemap packed here (as its render object has it), against native packing the twin; the twin's own
		// managed packing first, so a packing difference isn't put down to the missing twin
		var packsAsNative = Native.GetMethod( "PacksAsNative", BindingFlags.Static | BindingFlags.Public );
		Assert.IsTrue( (bool)packsAsNative.Invoke( null, [twin, RenderObjectOf( twin )] ), $"{what}: the twin packs as native packs it" );
		Assert.IsTrue( (bool)packsAsNative.Invoke( null, [twin, RenderObjectOf( twinless )] ), $"{what}: packs as native packs the twin" );
	}

	static object RenderObjectOf( SceneCubemap cubemap )
	{
		var obj = Activator.CreateInstance( EnvMapObject, [null, cubemap.ProjectionBounds, cubemap.Transform] );
		EnvMapObject.GetProperty( "Tint" ).SetValue( obj, cubemap.TintColor );
		EnvMapObject.GetProperty( "Feathering" ).SetValue( obj, cubemap.Feathering );
		EnvMapObject.GetProperty( "Priority" ).SetValue( obj, cubemap.Priority );
		return obj;
	}

	static void AssertSameProbe( SceneLightProbe twin, SceneLightProbe twinless, string what )
	{
		AssertSameObject( twin, twinless, what );
		Assert.AreEqual( twin.BoxMins, twinless.BoxMins, $"{what}: box mins" );
		Assert.AreEqual( twin.BoxMaxs, twinless.BoxMaxs, $"{what}: box maxs" );
		Assert.AreEqual( twin.Handshake, twinless.Handshake, $"{what}: handshake" );
		Assert.AreEqual( twin.RenderPriority, twinless.RenderPriority, $"{what}: priority" );
		CollectionAssert.AreEqual( twin.LightGroups, twinless.LightGroups, $"{what}: light groups" );
	}

	static void Both<T>( T twin, T twinless, Action<T> change )
	{
		change( twin );
		change( twinless );
	}

	[TestMethod]
	public void TwinlessLightsStartChangeAndPackAsTwinnedOnes()
	{
		var world = new SceneWorld();
		try
		{
			// Point lights, as a component and a map make them, then every setting the components and maps change
			var point = Make( world, w => new ScenePointLight( w, new Vector3( 10, 20, 30 ), 200, Color.Red * 5 ), true );
			var pointless = Make( world, w => new ScenePointLight( w, new Vector3( 10, 20, 30 ), 200, Color.Red * 5 ), false );
			AssertSameLight( point, pointless, "new point light" );
			Both( point, pointless, l =>
			{
				l.Transform = new Transform( new Vector3( -50, 5, 70 ), Rotation.From( 10, 40, 5 ) );
				l.LinearAttenuation = 0.5f;
				l.ConstantAttenuation = 0.25f;
				l.QuadraticAttenuation = 2;
				l.FogLighting = SceneLight.FogLightingMode.Dynamic;
				l.FogStrength = 0.5f;
				l.Shape = SceneLight.LightShape.Capsule;
				l.ShapeSize = new Vector2( 10, 20 );
				l.RenderSpecular = false;
				l.RenderTransmissive = false;
				l.ShadowsEnabled = false;
				l.ShadowTextureResolution = 512;
				l.Radius = 350;
				l.LightColor = new Color( 1, 2, 3 );
			} );
			AssertSameLight( point, pointless, "changed point light" );
			Both( point, pointless, l => l.ApplyLegacy( new Light.LegacyLightData
			{
				DirectLight = 3,
				BakeLightIndex = 7,
				BakeLightIndexScale = 2,
				BakedLightIndexing = true,
				FogContributionStrength = 0.25f,
				RenderDiffuse = true,
				RenderSpecular = true,
				FogLighting = 2,
				ConstantAttenuation = 1,
				LinearAttenuation = 0.1f,
				FallOff = 3,
			} ) );
			AssertSameLight( point, pointless, "a map's stationary point light" );
			Both( point, pointless, l => l.SetModulatorRange( 4, 2 ) );
			AssertSameLight( point, pointless, "modulated point light" );

#pragma warning disable CS0618 // the obsolete SceneLight constructor still makes a point light
			var obsolete = Make( world, w => new SceneLight( w ), true );
			var obsoleteless = Make( world, w => new SceneLight( w ), false );
#pragma warning restore CS0618
			AssertSameLight( obsolete, obsoleteless, "new obsolete scene light" );

			var spot = Make( world, w => new SceneSpotLight( w, new Vector3( 1, 2, 3 ), Color.White * 10 ), true );
			var spotless = Make( world, w => new SceneSpotLight( w, new Vector3( 1, 2, 3 ), Color.White * 10 ), false );
			AssertSameLight( spot, spotless, "new spot light" );
			Both( spot, spotless, l =>
			{
				l.Transform = new Transform( new Vector3( 100, 0, 50 ), Rotation.From( 45, 90, 0 ) );
				l.ConeInner = 20;
				l.ConeOuter = 40;
				l.FallOff = 2;
				l.Radius = 500;
				l.SetBakedLightIndex( 3, true );
				l.FogLighting = SceneLight.FogLightingMode.DynamicNoShadows;
				l.Shape = SceneLight.LightShape.Rectangle;
				l.ShapeSize = new Vector2( 4, 8 );
			} );
			AssertSameLight( spot, spotless, "changed spot light" );
			Both( spot, spotless, l => { l.Transform = new Transform( new Vector3( 0, 0, 200 ), Rotation.From( 90, 0, 0 ) ); l.Shape = SceneLight.LightShape.Sphere; l.ShapeSize = new Vector2( 3, 0 ); } );
			AssertSameLight( spot, spotless, "spot light pointing straight down" );

			var sun = Make( world, w => new SceneDirectionalLight( w, Rotation.From( 60, 30, 0 ), Color.White * 3 ), true );
			var sunless = Make( world, w => new SceneDirectionalLight( w, Rotation.From( 60, 30, 0 ), Color.White * 3 ), false );
			AssertSameLight( sun, sunless, "new directional light" );
			Both( sun, sunless, l =>
			{
				l.Rotation = Rotation.From( 50, 120, 0 );
				l.SetBakedLightIndex( 1 );
				l.ShadowCascadeCount = 2;
				l.ShadowCascadeSplitRatio = 0.8f;
				l.SetCascadeDistanceScale( 2 );
				l.SetWorldDirection( Rotation.From( 40, 10, 0 ) );
			} );
			AssertSameLight( sun, sunless, "changed directional light" );

			Both( point, pointless, l => l.RenderingEnabled = false );
			AssertSameLight( point, pointless, "hidden point light" );
		}
		finally
		{
			world.Delete();
		}
	}

	[TestMethod]
	public void TwinlessCubemapsAndProbeVolumesStartAndChangeAsTwinnedOnes()
	{
		var world = new SceneWorld();
		try
		{
			// As the envmap component and a sky make them
			var box = new BBox( new Vector3( -100, -200, -50 ), new Vector3( 300, 100, 150 ) );
			var placed = new Transform( new Vector3( 40, 50, 60 ), Rotation.From( 0, 30, 0 ) );
			var cube = Make( world, w => new SceneCubemap( w, null, box, placed, Color.White, 0.25f, 1 ), true );
			var cubeless = Make( world, w => new SceneCubemap( w, null, box, placed, Color.White, 0.25f, 1 ), false );
			AssertSameCubemap( cube, cubeless, "new cubemap", priority: false );
			Both( cube, cubeless, c =>
			{
				c.Priority = 3;
				c.Projection = SceneCubemap.ProjectionMode.Sphere;
				c.TintColor = new Color( 0.5f, 1, 2 );
				c.Feathering = 0.1f;
				c.ProjectionBounds = new BBox( new Vector3( -10 ), new Vector3( 20 ) );
				c.Transform = new Transform( new Vector3( -5, 5, 10 ), Rotation.From( 0, -45, 0 ) );
			} );
			AssertSameCubemap( cube, cubeless, "changed cubemap" );

			// Scaled up, and mirrored as map probes can be: native's inverse divides by the squared scale unless it's 1
			Both( cube, cubeless, c => c.Transform = new Transform( new Vector3( -5, 5, 10 ), Rotation.From( 0, -45, 0 ), 2 ) );
			AssertSameCubemap( cube, cubeless, "scaled cubemap" );
			// (not on a right angle: a bound landing exactly on a whole unit floors either way with the basis's float noise)
			Both( cube, cubeless, c => c.Transform = new Transform( new Vector3( 7.3f, 0.4f, -3.2f ), Rotation.From( 0, 70, 0 ), -1 ) );
			AssertSameCubemap( cube, cubeless, "mirrored cubemap" );

			var sky = Make( world, w => new SceneCubemap( w ), true );
			var skyless = Make( world, w => new SceneCubemap( w ), false );
			Both( sky, skyless, c => c.Priority = 0 );
			AssertSameCubemap( sky, skyless, "default cubemap" );

			var probeBox = new BBox( new Vector3( -64 ), new Vector3( 128, 64, 32 ) );
			var probe = Make( world, w => new SceneLightProbe( w, null, null, null, probeBox, placed, 5, 2 ), true );
			var probeless = Make( world, w => new SceneLightProbe( w, null, null, null, probeBox, placed, 5, 2 ), false );
			AssertSameProbe( probe, probeless, "new probe volume" );
			Both( probe, probeless, p => { p.Transform = new Transform( new Vector3( 1, 2, 3 ) ); p.RenderingEnabled = false; } );
			AssertSameProbe( probe, probeless, "moved probe volume" );
		}
		finally
		{
			world.Delete();
		}
	}

	[TestMethod]
	public void TwinlessLightsLeaveTheirWorldWhenDeleted()
	{
		var world = new SceneWorld();
		try
		{
			var light = Make( world, w => new ScenePointLight( w ), false );
			var cube = Make( world, w => new SceneCubemap( w ), false );
			lock ( world.InternalSceneObjects ) Assert.IsTrue( world.InternalSceneLights.Contains( light ) && world.InternalSceneLights.Contains( cube ) );

			light.Delete();
			cube.Delete();
			Assert.IsFalse( light.IsValid() );
			world.DeletePendingObjects();
			lock ( world.InternalSceneObjects )
			{
				Assert.IsFalse( world.InternalSceneLights.Contains( light ), "light gone from the world's lights" );
				Assert.IsFalse( world.InternalSceneLights.Contains( cube ), "cubemap gone from the world's lights" );
				Assert.IsFalse( world.InternalSceneObjects.Contains( light ) );
			}
		}
		finally
		{
			world.Delete();
		}
	}
}
