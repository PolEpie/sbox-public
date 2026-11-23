namespace Sandbox;

public partial class SceneObject
{
	/// <summary>
	/// Whether a scene object's state is written through to its native twin: in a process that doesn't draw on Borealis.Gpu.
	/// A type whose state is kept in managed fields (decals, sky boxes) writes it through only then; under <c>-borealisgpu</c>
	/// Borealis reads the managed fields.
	/// </summary>
	internal static bool WritesNative => WritesNativeOverride ?? !Rendering.ManagedSceneRendering.Enabled;

	/// <summary>Tests set <see cref="WritesNative"/> either way; null follows the process.</summary>
	internal static bool? WritesNativeOverride { get; set; }

	/// <summary>
	/// Whether scene objects managed code makes get a native twin: only in a process without a Borealis.Gpu device (headless
	/// tests, servers, content workers), where tests compare managed state against native's. Nothing native reads a managed
	/// object's twin in the game or the editor: render-mesh traces walk the managed objects, and Hammer's views, gizmos and
	/// picking are managed or trace Hammer's own map data. Objects native code makes (Hammer's lights and probes, tool previews)
	/// always have their native object; decals never have one.
	/// </summary>
	internal static bool MakesNativeTwin => MakesNativeTwinOverride ?? !Rendering.ManagedSceneRendering.Enabled;

	/// <summary>Tests set <see cref="MakesNativeTwin"/> either way; null follows the process.</summary>
	internal static bool? MakesNativeTwinOverride { get; set; }

	/// <summary>From made (with or without a twin) until deleted or destroyed.</summary>
	bool _alive;

	/// <summary>Whether it has a native object: a twin, or the object native code made.</summary>
	internal bool HasTwin => !native.IsNull;

	private protected Model _keptModel;
	bool _batchable = true;

	/// <summary>
	/// Whether a change also goes to the native twin: false without one. A deleted object throws, as a call on its gone twin did.
	/// </summary>
	private protected bool WritesTwin( string method )
	{
		if ( !_alive ) Sandbox.Interop.ThrowNullSelf( "CSceneObject", method );
		return !native.IsNull;
	}

	/// <summary>
	/// Hands this object to the native twin made next, for its handle: only when it gets one (<see cref="MakesNativeTwin"/>).
	/// One made without a twin isn't in the handle index, so nothing waits for a native object that's never made.
	/// </summary>
	private protected IHandle.NextHandleScope NextHandleForTwin() => IHandle.MakeNextHandle( MakesNativeTwin ? this : null );

	/// <summary>
	/// Made without a native twin (<see cref="MakesNativeTwin"/> off): in its world and its renderer as <see cref="OnNativeInit"/>
	/// puts one, with native's defaults for what a new native object would have had (visible, white, no layer match, no LOD
	/// override, batchable, the model's default mesh groups or all of them without a model).
	/// </summary>
	private protected void MakeWithoutTwin( SceneWorld world, Model model, Rendering.SceneObjectFlags flags = default, bool batchable = true )
	{
		_alive = true;
		World = world;
		_flags ??= flags;
		_batchable = batchable;
		_transform = Transform.Zero; // where native's new object is until it's placed
									 // Native's mesh instances start on the model's default groups; an object without a model reports every group
		_meshGroupMask = model is { native.IsNull: false } ? model.native.GetDefaultMeshGroupMask() : ulong.MaxValue;

		lock ( World.InternalSceneObjects )
		{
			World.InternalSceneObjects.Add( this );
		}

		World.Render?.Made( this );
	}
}
