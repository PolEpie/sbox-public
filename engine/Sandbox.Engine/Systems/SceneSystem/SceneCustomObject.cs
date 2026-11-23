using NativeEngine;

namespace Sandbox;

/// <summary>
/// A scene object that allows custom rendering within a scene world.
/// </summary>
public class SceneCustomObject : SceneObject
{
	internal NativeEngine.CManagedSceneObject managedNative;

	internal SceneCustomObject( HandleCreationData _ )
	{
		gameType = GetType().Assembly != typeof( SceneCustomObject ).Assembly;
	}


	public SceneCustomObject( SceneWorld sceneWorld )
	{
		Assert.IsValid( sceneWorld );

		using ( var h = NextHandleForTwin() )
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

		gameType = GetType().Assembly != typeof( SceneCustomObject ).Assembly;
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

				if ( custom.IsGameDrawn ) return custom;
			}
		}

		return null;
	}

	/// <summary>Every live custom object whose drawing runs game code (<see cref="GameDrawn"/>), into <paramref name="into"/>.</summary>
	internal static void CopyGameDrawn( List<SceneCustomObject> into )
	{
		into.Clear();
		lock ( live )
		{
			foreach ( var custom in live )
			{
				if ( custom.IsValid() && custom.IsGameDrawn ) into.Add( custom );
			}
		}
	}

	/// <summary>Whether drawing this object runs game code: a game's own type, or a <see cref="RenderOverride"/>.</summary>
	internal bool IsGameDrawn => RenderOverride is not null || gameType;

	readonly bool gameType;

	/// <summary>Its drawing's recordings in the extract, by camera and layer kind (<see cref="Rendering.GameDrawExtract"/>).</summary>
	internal Rendering.GameDrawExtract.Slots DrawSlots;

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

		// A game's drawing replays what the extract recorded where it isn't run here (the render thread)
		if ( IsGameDrawn && Rendering.GameDrawExtract.Draws( this ) )
			return;

		RecordInternal();
	}

	/// <summary>Run the object's drawing: into the frame, or into a recording in the extract.</summary>
	internal void RecordInternal()
	{
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
