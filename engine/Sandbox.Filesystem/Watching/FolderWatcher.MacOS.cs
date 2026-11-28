using System.IO;

namespace Sandbox;

/// <summary>
/// macOS: an FSEvents stream over the folder (<see cref="PhysicalWatch.FSEventStream"/>).
/// </summary>
public sealed partial class FolderWatcher
{
	PhysicalWatch.FSEventStream stream;

	void StartMacOS()
	{
		stream = new PhysicalWatch.FSEventStream( path, OnMacOSEvent );
	}

	void StopMacOS()
	{
		stream?.Dispose();
		stream = null;
	}

	void OnMacOSEvent( string fullPath, uint flags )
	{
		var watched = stream;
		if ( watched is null ) return;

		// The stream is always recursive - drop what's deeper than asked for
		if ( !includeSubdirectories && !string.Equals( System.IO.Path.GetDirectoryName( fullPath ), watched.Path, StringComparison.Ordinal ) )
			return;

		var changeType = PhysicalWatch.FSEventStream.ChangeTypeOf( fullPath, flags );
		var args = new FileSystemEventArgs( changeType, System.IO.Path.GetDirectoryName( fullPath ) ?? watched.Path, System.IO.Path.GetFileName( fullPath ) );

		switch ( changeType )
		{
			case WatcherChangeTypes.Created: Created?.Invoke( this, args ); break;
			case WatcherChangeTypes.Deleted: Deleted?.Invoke( this, args ); break;
			default: Changed?.Invoke( this, args ); break;
		}
	}
}
