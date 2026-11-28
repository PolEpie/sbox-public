using Zio;
using Zio.FileSystems;

namespace Sandbox;

/// <summary>
/// How folders on disk are watched, per platform. .NET's <see cref="System.IO.FileSystemWatcher"/> works
/// everywhere but costs too much in two of them:
/// <list type="bullet">
/// <item>Linux (<c>PhysicalWatch.Linux.cs</c>): each one is an inotify instance, and a user only gets
/// 128 across every process they run. Watches share one recursive watcher per tree instead.</item>
/// <item>macOS (<c>PhysicalWatch.MacOS.cs</c>): each one waits 50-100 ms on a shared run loop
/// thread to start, and the editor starts about fifty. Watches are FSEvents streams instead.</item>
/// </list>
/// Windows keeps .NET's watcher. <see cref="WatchedPhysicalFileSystem"/> hands these out for the
/// mounted filesystems, <see cref="FolderWatcher"/> for code that watches a folder directly.
/// </summary>
internal static partial class PhysicalWatch
{
	/// <summary>
	/// The platform's watcher for <paramref name="path"/> on <paramref name="fileSystem"/>, or null
	/// where Zio's own is the one to use.
	/// </summary>
	internal static IFileSystemWatcher Create( PhysicalFileSystem fileSystem, UPath path )
	{
		if ( OperatingSystem.IsLinux() )
			return new InotifyWatcher( fileSystem, path );

		if ( OperatingSystem.IsMacOS() )
			return new FSEventsWatcher( fileSystem, path );

		return null;
	}
}

/// <summary>
/// A physical filesystem whose watchers are the platform's (<see cref="PhysicalWatch"/>).
/// </summary>
internal class WatchedPhysicalFileSystem : PhysicalFileSystem
{
	protected override IFileSystemWatcher WatchImpl( UPath path ) => PhysicalWatch.Create( this, path ) ?? base.WatchImpl( path );
}
