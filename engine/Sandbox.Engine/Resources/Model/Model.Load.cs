using Sandbox.Mounting;

namespace Sandbox;

public partial class Model
{
	/// <summary>
	/// Load a model by file path.
	/// </summary>
	/// <param name="filename">The file path to load as a model.</param>
	/// <returns>The loaded model, or null</returns>
	public static Model Load( string filename ) => Load( (ResourceId)filename );

	internal static Model Load( ResourceId id )
	{
		ThreadSafe.AssertIsMainThread();

		if ( id.Guid is Guid guid )
		{
			if ( Game.Resources.TryGet<Model>( guid, out var resource ) )
				return resource.EnsureLoaded();

			var native = NativeGlue.Resources.GetModel( id.Path, guid );
			return FromNative( native,
				name: native.IsError() ? id.Path : null ); // only use the (possibly stale) path if it failed to load, as feedback
		}

		if ( !string.IsNullOrWhiteSpace( id.Path ) )
		{
			id.Path = id.Path?.Replace( ".vmdl_c", ".vmdl" );

			if ( Sandbox.Mounting.Directory.TryLoad( id.Path, out object model ) && model is Model m )
				return m;

			if ( Game.Resources.TryGet<Model>( id.Path, out var resource ) )
				return resource.EnsureLoaded();

			return FromNative( NativeGlue.Resources.GetModel( id.Path, Guid.Empty ), name: id.Path );
		}

		return Error;
	}

	/// <summary>
	/// A model that a file references. In the editor it loads when something first uses it, so that a model which is
	/// only referenced - by a prefab nobody placed, by a resource - stays out of memory. Elsewhere this is <see cref="Load(ResourceId)"/>.
	/// </summary>
	internal static Model LoadDeferred( ResourceId id )
	{
		if ( !Application.IsEditor || string.IsNullOrWhiteSpace( id.Path ) || MountUtility.IsMountPath( id.Path ) )
			return Load( id );

		ThreadSafe.AssertIsMainThread();

		id.Path = id.Path.Replace( ".vmdl_c", ".vmdl" );

		// Lookups as in Load: by guid when the reference has one, by path otherwise
		if ( id.Guid is Guid referenced )
		{
			if ( Game.Resources.TryGet<Model>( referenced, out var existing ) ) return existing;
		}
		else if ( Game.Resources.TryGet<Model>( id.Path, out var existing ) )
		{
			return existing;
		}

		// Without its guid the reference would serialize differently once loaded, so load it now
		if ( (id.Guid ?? Sandbox.ResourceId.FromPath( id.Path ).Guid) is not Guid guid || guid == default )
			return Load( id );

		return new Model( id, guid );
	}

	/// <summary>
	/// Load a model by file path.
	/// </summary>
	/// <param name="filename">The file path to load as a model.</param>
	/// <returns>The loaded model, or null</returns>
	public static async Task<Model> LoadAsync( string filename )
	{
		ThreadSafe.AssertIsMainThread();

		if ( string.IsNullOrWhiteSpace( filename ) )
			return Error;

		filename = filename?.Replace( ".vmdl_c", ".vmdl" );

		if ( await Sandbox.Mounting.Directory.TryLoadAsync( filename ) is Model m )
			return m;

		if ( Game.Resources.TryGet<Model>( filename, out var resource ) )
			return resource;

		using var manifest = AsyncResourceLoader.Load( filename );
		if ( manifest is not null )
		{
			await manifest.WaitForLoad();
		}

		// TODO - make async
		return Load( filename );
	}
}
