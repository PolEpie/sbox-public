using System.IO;

namespace Sandbox;

/// <summary>
/// Linux: a listener on the shared inotify roots (<see cref="PhysicalWatch.Subscribe"/>), so the
/// asset browser's folders ride on the watches the mounted filesystems already have.
/// </summary>
public sealed partial class FolderWatcher
{
	Listener listener;

	void StartLinux()
	{
		var full = System.IO.Path.GetFullPath( path );
		var directory = full.Length > 1 ? full.TrimEnd( '/' ) : full;

		var created = new Listener( this, directory );
		PhysicalWatch.Subscribe( created );
		listener = created;
	}

	void StopLinux()
	{
		if ( listener is null ) return;

		PhysicalWatch.Unsubscribe( listener );
		listener = null;
	}

	/// <summary>
	/// Raise what a root saw, as <see cref="FileSystemWatcher"/> would: relative to the watched folder.
	/// </summary>
	void OnLinuxEvent( Listener from, WatcherChangeTypes type, string fullPath, string oldFullPath )
	{
		// Stopped or moved since the root picked it up
		if ( from != listener ) return;

		var name = System.IO.Path.GetRelativePath( from.Directory, fullPath );

		switch ( type )
		{
			case WatcherChangeTypes.Created: Created?.Invoke( this, new FileSystemEventArgs( type, from.Directory, name ) ); break;
			case WatcherChangeTypes.Deleted: Deleted?.Invoke( this, new FileSystemEventArgs( type, from.Directory, name ) ); break;
			case WatcherChangeTypes.Renamed: Renamed?.Invoke( this, new RenamedEventArgs( type, from.Directory, name, System.IO.Path.GetRelativePath( from.Directory, oldFullPath ) ) ); break;
			default: Changed?.Invoke( this, new FileSystemEventArgs( type, from.Directory, name ) ); break;
		}
	}

	sealed class Listener( FolderWatcher owner, string directory ) : PhysicalWatch.IListener
	{
		public string Directory { get; } = directory;

		public bool IncludeSubdirectories => owner.includeSubdirectories;

		public PhysicalWatch.Root Root { get; set; }

		public void OnEvent( WatcherChangeTypes type, string fullPath, string oldFullPath ) => owner.OnLinuxEvent( this, type, fullPath, oldFullPath );

		// FileSystemWatcher's errors never reached anyone through here before either
		public void OnError( Exception exception ) { }
	}
}
