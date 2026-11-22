using NativeEngine;

namespace Sandbox;

/// <summary>
/// A scene object that allows custom rendering within a scene world.
/// </summary>
public class SceneCustomObject : SceneObject
{
	internal NativeEngine.CManagedSceneObject managedNative;

	internal SceneCustomObject( HandleCreationData _ ) { }


	public SceneCustomObject( SceneWorld sceneWorld )
	{
		Assert.IsValid( sceneWorld );

		using ( var h = IHandle.MakeNextHandle( this ) )
		{
			if ( MakesNativeTwin ) CManagedSceneObject.Create( sceneWorld );
			else MakeWithoutTwin( sceneWorld, null, TwinlessFlags );

			if ( this.IsValid() )
			{
				// Start off with infinite bounds so it will actually get rendered
				// if the user forgets/doesn't need bounds.
				if ( HasTwin ) native.SetBoundsInfinite();
				KeepFixedBounds( infinite: true );
			}
		}

		lock ( live ) live.Add( this );
	}

	// Every custom object made, for whether game code draws in a frame (GameDrawn); those gone are dropped as it looks
	static readonly List<SceneCustomObject> live = new();

	/// <summary>
	/// A custom object whose drawing runs game code - a game's own type, or one drawn by a <see cref="RenderOverride"/> - if
	/// any is alive, else null: a frame that has one draws on the main thread, since the render thread runs no game code.
	/// </summary>
	internal static SceneCustomObject GameDrawn()
	{
		lock ( live )
		{
			for ( int i = live.Count - 1; i >= 0; i-- )
			{
				var custom = live[i];
				if ( !custom.IsValid() )
				{
					live[i] = live[^1];
					live.RemoveAt( live.Count - 1 );
					continue;
				}

				if ( custom.RenderOverride is not null || custom.GetType().Assembly != typeof( SceneCustomObject ).Assembly ) return custom;
			}
		}

		return null;
	}

	/// <summary>What native's <c>CManagedSceneObject::Create</c> sets.</summary>
	internal const Rendering.SceneObjectFlags TwinlessFlags = Rendering.SceneObjectFlags.IsTranslucent;

	internal unsafe override void OnNativeInit( CSceneObject ptr )
	{
		base.OnNativeInit( ptr );

		managedNative = (NativeEngine.CManagedSceneObject)ptr;
	}

	internal override void OnNativeDestroy()
	{
		managedNative = default;
		base.OnNativeDestroy();
	}

	internal void RenderInternal()
	{
		if ( !this.IsValid() )
			return;

		try
		{
			RenderSceneObject();
		}
		catch ( System.Exception e )
		{
			if ( BorealisGpuErrors.ShouldLog( e ) ) Log.Error( e );
		}
	}

	/// <summary>
	/// Called by default version of <see cref="RenderSceneObject"/>.
	/// </summary>
	public Action<SceneObject> RenderOverride;

	/// <summary>
	/// Called when this scene object needs to be rendered.
	/// Invokes <see cref="RenderOverride"/> by default. See the <see cref="Graphics" /> library for a starting point.
	/// </summary>
	public virtual void RenderSceneObject()
	{
		RenderOverride?.Invoke( this );
	}

	internal override bool OwnsRenderObject => true;

	internal override Borealis.RenderObject MakeRenderObject( Rendering.ISceneWorldRender render ) => new Rendering.BridgeCustomObject( this );

	internal override void WriteRender( Borealis.RenderObject obj, Rendering.SceneObjectChange change, Rendering.ISceneWorldRender render )
	{
		base.WriteRender( obj, change, render );
		if ( obj is Borealis.CustomObject custom ) WriteCustom( custom, change );
	}
}
