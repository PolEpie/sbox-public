using Facepunch.ActionGraphs;
using System.Text.Json.Nodes;

namespace Sandbox;

/// <summary>
/// A prefab which is loaded and accessible via GameObject.GetPrefab( x )
/// </summary>
internal partial class PrefabCacheScene : PrefabScene
{
	internal PrefabCacheScene() : base( false )
	{
	}

	/// <summary>
	/// Contains the the JSON for the prefab after loading it's cached scene and expanding all prefab instances.
	/// We cache this since we use this quite often to resolve nested prefab instance overrides.
	/// </summary>
	internal JsonObject FullPrefabInstanceJson { get; private set; }

	private Func<JsonObject, Json.Patch> calculateDifferences;

	internal Json.Patch CalculateDifferences( JsonObject instance ) => calculateDifferences( instance );

	Dictionary<Guid, JsonObject> _fullPrefabGameObjects;

	/// <summary>
	/// The json one of this prefab's GameObjects has in <see cref="FullPrefabInstanceJson"/>, by its prefab id -
	/// what an instance's copy of it is diffed against. Null if the prefab has no such object.
	/// </summary>
	internal JsonObject FindFullPrefabGameObject( Guid prefabId )
	{
		if ( _fullPrefabGameObjects is null )
		{
			_fullPrefabGameObjects = new();
			if ( FullPrefabInstanceJson is not null )
				IndexGameObjects( FullPrefabInstanceJson );
		}

		return _fullPrefabGameObjects.GetValueOrDefault( prefabId );
	}

	void IndexGameObjects( JsonObject node )
	{
		if ( node[GameObject.JsonKeys.Id] is JsonValue id && id.TryGetValue<Guid>( out var guid ) )
			_fullPrefabGameObjects.TryAdd( guid, node );

		if ( node[GameObject.JsonKeys.Children] is not JsonArray children )
			return;

		foreach ( var child in children )
		{
			if ( child is JsonObject childObject )
				IndexGameObjects( childObject );
		}
	}

	/// <summary>
	/// Contains all the prefab files that are referenced by this prefab scene.
	/// </summary>
	private HashSet<PrefabFile> referencedPrefabs = new();

	public override bool Load( GameResource resource )
	{
		if ( !base.Load( resource ) )
			return false;

		// The cached snapshot outlives both the resource's loading scope and any ambient capture.
		using var sourceScope = ActionGraph.PushSerializationOptions( resource.SerializationOptions with { ForceUpdateCached = IsEditor } );
		using var suppressBlobs = BlobDataSerializer.Suppress();
		FullPrefabInstanceJson = Serialize( new SerializeOptions { SerializePrefabForDiff = true } );
		_fullPrefabGameObjects = null;
		calculateDifferences = Json.CreateDifferenceCalculator( FullPrefabInstanceJson, DiffObjectDefinitions );

		// Iterate all gameobjects in scene and find prefab instances, add them to reference set
		referencedPrefabs = GetAllObjects( false ).Where( o => o.IsPrefabInstanceRoot ).Select( p => ResourceLibrary.Get<PrefabFile>( p.PrefabInstanceSource ) ).ToHashSet();

		return true;
	}

	/// <summary>
	/// Don't try and do this. You can't destroy a PrefabCacheScene.
	/// </summary>
	public override void Destroy()
	{
		throw new InvalidOperationException( $"Destroying a {nameof( PrefabCacheScene )} is not allowed. Did you mean to destroy the GameObject instance?" );
	}

	internal void Refresh( PrefabFile file )
	{
		Load( file );
		UpdateDependencies( file );
	}

	private void UpdateDependencies( PrefabFile file )
	{
		var dependantSet = new HashSet<PrefabFile>();
		BuildDependantSet( file, dependantSet, ResourceLibrary.GetAll<PrefabFile>().ToArray() );

		// Expanded hierarchies reference transitive dependencies too. Discovery order is not
		// load order: only rebuild a consumer once all of its affected dependencies are current.
		var ordered = new List<PrefabFile>( dependantSet.Count );
		while ( dependantSet.Count > 0 )
		{
			var ready = dependantSet.Where( x => !x.CachedScene.referencedPrefabs.Overlaps( dependantSet ) ).ToArray();
			if ( ready.Length == 0 )
			{
				Log.Warning( $"Cyclic prefab dependencies while refreshing {file.ResourceName}" );
				return;
			}

			foreach ( var dependant in ready )
			{
				dependantSet.Remove( dependant );
				ordered.Add( dependant );
			}
		}

		foreach ( var dependant in ordered )
		{
			dependant.CachedScene?.Load( dependant );
			dependant.ReleaseRootObject();
		}
	}

	private void BuildDependantSet( PrefabFile file, HashSet<PrefabFile> prefabScenesRequiringUpdate, PrefabFile[] prefabFiles )
	{
		foreach ( var pf in prefabFiles )
		{
			if ( prefabScenesRequiringUpdate.Contains( pf ) )
			{
				continue;
			}

			// Only check prefabs that already have a cached scene loaded.
			// Prefabs without a cached scene will load fresh data when first accessed,
			// so we don't need to force-load them just to check dependencies.
			if ( pf.CachedScene is not PrefabCacheScene prefabScene )
			{
				continue;
			}

			if ( !prefabScene.IsValid() )
			{
				Log.Warning( $"Failed to update prefab dependencies, prefab {pf.ResourceName} is not valid" );
				continue;
			}

			if ( prefabScene == this )
			{
				continue;
			}

			if ( !prefabScene.referencedPrefabs.Contains( file ) )
			{
				continue;
			}

			prefabScenesRequiringUpdate.Add( pf );

			BuildDependantSet( pf, prefabScenesRequiringUpdate, prefabFiles );
		}
	}
}


