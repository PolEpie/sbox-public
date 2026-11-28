using System.IO;

namespace Sandbox;

/// <summary>
/// Watches a folder on disk for changes - the shape of <see cref="FileSystemWatcher"/>, with the
/// same events, watched the way the platform can afford (<see cref="PhysicalWatch"/>):
/// <list type="bullet">
/// <item>Linux (<c>FolderWatcher.Linux.cs</c>): a watch on the trees the mounted filesystems already
/// watch, so it costs no inotify instance of its own when one covers it.</item>
/// <item>macOS (<c>FolderWatcher.MacOS.cs</c>): an FSEvents stream, which starts in well under a
/// millisecond where <see cref="FileSystemWatcher"/> takes 50-100 ms. FSEvents doesn't pair the two
/// ends of a rename, so there a rename arrives as <see cref="Deleted"/> for the old path and
/// <see cref="Created"/> for the new one, never <see cref="Renamed"/>.</item>
/// <item>Windows: <see cref="FileSystemWatcher"/>.</item>
/// </list>
/// </summary>
public sealed partial class FolderWatcher : IDisposable
{
	public event FileSystemEventHandler Changed;
	public event FileSystemEventHandler Created;
	public event FileSystemEventHandler Deleted;
	public event RenamedEventHandler Renamed;

	string path;
	bool includeSubdirectories;
	bool enabled;

	FileSystemWatcher stock;

	public FolderWatcher()
	{
	}

	public FolderWatcher( string path )
	{
		Path = path;
	}

	/// <summary>
	/// The folder to watch. Changing it while enabled moves the watch.
	/// </summary>
	public string Path
	{
		get => path;
		set
		{
			if ( path == value ) return;
			path = value;

			if ( enabled )
			{
				Stop();
				Start();
			}
		}
	}

	/// <summary>
	/// Whether changes anywhere under the folder count, or only its direct contents.
	/// </summary>
	public bool IncludeSubdirectories
	{
		get => includeSubdirectories;
		set
		{
			includeSubdirectories = value;
			if ( stock is not null ) stock.IncludeSubdirectories = value;
		}
	}

	public bool EnableRaisingEvents
	{
		get => enabled;
		set
		{
			if ( enabled == value ) return;

			if ( value ) Start();
			else Stop();

			enabled = value;
		}
	}

	void Start()
	{
		if ( string.IsNullOrEmpty( path ) )
			throw new InvalidOperationException( "FolderWatcher needs a Path before it can be enabled" );

		if ( OperatingSystem.IsLinux() )
		{
			StartLinux();
			return;
		}

		if ( OperatingSystem.IsMacOS() )
		{
			StartMacOS();
			return;
		}

		stock = new FileSystemWatcher( path ) { IncludeSubdirectories = includeSubdirectories };
		stock.Changed += ( s, e ) => Changed?.Invoke( this, e );
		stock.Created += ( s, e ) => Created?.Invoke( this, e );
		stock.Deleted += ( s, e ) => Deleted?.Invoke( this, e );
		stock.Renamed += ( s, e ) => Renamed?.Invoke( this, e );

		try
		{
			stock.EnableRaisingEvents = true;
		}
		catch
		{
			stock.Dispose();
			stock = null;
			throw;
		}
	}

	void Stop()
	{
		StopLinux();
		StopMacOS();

		stock?.Dispose();
		stock = null;
	}

	public void Dispose()
	{
		enabled = false;
		Stop();
	}
}
