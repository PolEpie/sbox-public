using NativeEngine;
using Sandbox.Engine;
using Sandbox.Engine.Utility.RayTrace;

namespace Sandbox;

/// <summary>
/// A model.
/// </summary>
public sealed partial class Model : Resource
{
	IModel _native;

	/// <summary>
	/// Set while this model is a reference read from a file that nothing has used yet. The editor leaves those
	/// unloaded, so that a model which is only referenced - by a prefab nobody placed, by a resource - stays out of memory.
	/// </summary>
	ResourceId? _reference;

	/// <summary>
	/// The native model. Reading it loads a model that was only referenced so far.
	/// </summary>
	internal IModel native => EnsureLoaded()._native;

	internal bool procedural;

	public override bool IsValid => _reference is not null || _native.IsValid;

	/// <summary>
	/// Private constructor, use <see cref="FromNative(IModel, bool, string)"/>
	/// </summary>
	private Model( IModel native, string name, bool procedural )
	{
		if ( native.IsNull ) throw new Exception( "Model pointer cannot be null!" );

		_native = native;
		this.Name = name;
		this.procedural = procedural;
		_animationGraph = new( LoadAnimationGraph );

		RegisterWeakResourceId( Name, native.GetGuid() );
	}

	/// <summary>
	/// A reference that loads on first use, see <see cref="LoadDeferred"/>.
	/// </summary>
	private Model( ResourceId reference, Guid guid )
	{
		_reference = reference;

		// The path and guid the loaded model will have: what gets serialized must not change when it loads
		Name = reference.Guid is null ? reference.Path : reference.ResolvePath();
		_animationGraph = new( LoadAnimationGraph );

		RegisterWeakResourceId( Name, guid );
	}

	/// <summary>
	/// Load now if this is still an unused reference.
	/// </summary>
	internal Model EnsureLoaded()
	{
		if ( _reference is not { } reference ) return this;

		// The same rule as Load
		ThreadSafe.AssertIsMainThread();
		_reference = null;

		var loaded = NativeGlue.Resources.GetModel( reference.Path, reference.Guid ?? Guid.Empty );
		if ( loaded.IsNull || !loaded.IsStrongHandleValid() ) return this;

		// By guid the model may have moved from the path the reference carries, as in Load
		SetNative( loaded, reference.Guid is null || loaded.IsError() ? reference.Path : NativeGlue.Resources.GetModelResourceName( loaded ) );
		return this;
	}

	/// <summary>
	/// Give an unused reference its native model.
	/// </summary>
	internal void SetNative( IModel loaded, string name )
	{
		_reference = null;
		_native = loaded;
		Name = name;

		RegisterWeakResourceId( name, loaded.GetGuid() );

		var instanceId = loaded.GetBindingPtr().ToInt64();
		NativeResourceCache.Add( instanceId, this );
		Loaded[instanceId] = new WeakReference<Model>( this );
	}

	internal override void Destroy()
	{
		// Not through the native property: destroying an unused reference must not load it
		_reference = null;

		if ( !_native.IsNull )
		{
			var path = ResourcePath;

			var n = _native;
			_native = default;

			MainThread.Queue( () =>
			{
				n.DestroyStrongHandle();
			} );
		}

		base.Destroy();
	}

	/// <summary>
	/// Called when the resource is reloaded. We should clear any cached values.
	/// </summary>
	internal override void OnReloaded()
	{
		_data?.Dispose();
		_data = null;

		_physics?.Dispose();
		_physics = default;

		_morphs?.Dispose();
		_morphs = null;

		_attachments?.Dispose();
		_attachments = default;

		_animationNames?.Clear();
		_animationNames = default;

		_boneMaskNames?.Clear();
		_boneMaskNames = default;

		_animationGraph = new( LoadAnimationGraph );

		_sequenceNames?.Clear();
		_sequenceNames = default;

		_materials = default;

		_bones = default;
		_hitboxset = default;

		_parts?.Dispose();
		_parts = default;

		DataCache?.Clear();

		BaseModel = default;

		MeshInfo = null;

		IToolsDll.Current?.RunEvent( "model.reload", this );

		foreach ( var scene in Scene.AllIncludingUnlisted )
		{
			using var scope = scene.Push();

			var components = scene.GetAllComponents<IHasModel>();
			foreach ( var c in components )
			{
				if ( c.Model != this ) continue;

				c.OnModelReloaded();
			}
		}
	}

	/// <summary>
	/// Whether this model is an error model or invalid or not.
	/// </summary>
	public override bool IsError => native.IsNull || !native.IsStrongHandleValid() || native.IsError();

	/// <summary>
	/// Name of the model, usually being its file path.
	/// </summary>
	public string Name { get; internal set; }

	/// <summary>
	/// Whether this model is procedural, i.e. it was created at runtime via <see cref="ModelBuilder.Create"/>.
	/// </summary>
	public bool IsProcedural => procedural;

	/// <summary>
	/// Total number of meshes this model is made out of.
	/// </summary>
	public int MeshCount => native.GetNumMeshes();

	/// <summary>
	/// The highest LOD index used by this model, or -1 if the model has no effective LODs (all meshes enabled at all levels).
	/// </summary>
	internal int MaxLodLevel => native.ComputeMaxLODLevelUsedByModel();

	/// <summary>
	/// Returns the LOD level for a given screen size in pixels and object scale.
	/// Uses the same calculation as the standard rendering pipeline.
	/// </summary>
	internal int GetLodLevelForScreenSize( float screenWidthInPixels, float scale = 1f )
	{
		return native.LODLevelForScreenSize( screenWidthInPixels, scale );
	}

	/// <summary>
	/// Returns the switch distances for each LOD level, as defined by the model.
	/// </summary>
	internal float[] GetLodSwitchDistances()
	{
		var count = native.GetLODSwitchDistanceCount();
		var distances = new float[count];
		for ( int i = 0; i < count; i++ )
			distances[i] = native.GetLODSwitchDistance( i );
		return distances;
	}

	/// <summary>
	/// Trace against the triangles in this mesh
	/// </summary>
	public MeshTraceRequest Trace => new() { targetModel = this };

	/// <summary>
	/// Base model of this model if one was used.
	/// </summary>
	internal Model BaseModel
	{
		get => field ??= FromNative( native.GetBaseModel() );
		set;
	}

	/// <summary>
	/// Path of the <see cref="Animation.Skeleton"/> this model's animation is authored against, if it names one.
	/// </summary>
	public string SkeletonName => native.GetSkeletonName();
}

internal interface IHasModel
{
	/// <summary>
	/// The <see cref="Model"/> associated with this object.
	/// </summary>
	Model Model { get; }

	/// <summary>
	/// Called by the engine when the associated <see cref="Model"/> is reloaded.
	/// Implementing classes should clear or update any cached data that depends on the model.
	/// </summary>
	void OnModelReloaded();
}
