using System.IO;
using Zio;
using Zio.FileSystems;
using WatcherChangeTypes = System.IO.WatcherChangeTypes;
using NotifyFilters = System.IO.NotifyFilters;

namespace Sandbox;

/// <summary>
/// Linux: every <see cref="System.IO.FileSystemWatcher"/> is an inotify instance, and a user only gets 128
/// of them across every process they run. Each mounted filesystem asks for its own watch and plenty of
/// them mount the same folders (the editor had each addon watched four times over), so instead one
/// recursive watcher per tree (a <see cref="Root"/>) is shared by every watch inside it.
/// </summary>
internal static partial class PhysicalWatch
{
	/// <summary>
	/// What a root listens for. Roots are shared, so they can't take each watch's filter - this is
	/// everything <see cref="BaseFileSystem"/> asks for. Anyone asking for less sees a few more changes.
	/// </summary>
	const NotifyFilters RootFilters = NotifyFilters.Attributes | NotifyFilters.Size | NotifyFilters.CreationTime
		| NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Security;

	static readonly System.Threading.Lock RootsLock = new();
	static readonly List<Root> Roots = new();

	/// <summary>
	/// One watch on a shared <see cref="Root"/>.
	/// </summary>
	internal interface IListener
	{
		/// <summary>The watched directory as the OS spells it, which is how roots report paths. No trailing slash.</summary>
		string Directory { get; }

		bool IncludeSubdirectories { get; }

		/// <summary>The root it's on. Guarded by <see cref="RootsLock"/>.</summary>
		Root Root { get; set; }

		/// <summary>
		/// A change this watch would have seen watching on its own. <paramref name="oldFullPath"/> is only
		/// set for <see cref="WatcherChangeTypes.Renamed"/>. On the root's event thread.
		/// </summary>
		void OnEvent( WatcherChangeTypes type, string fullPath, string oldFullPath );

		void OnError( Exception exception );
	}

	/// <summary>
	/// A recursive System.IO watcher over one tree, feeding every <see cref="IListener"/> inside it.
	/// </summary>
	internal sealed class Root
	{
		public readonly string Path;
		readonly System.IO.FileSystemWatcher watcher;

		/// <summary>
		/// Copied on write, so the event thread reads it without taking <see cref="RootsLock"/> - roots get
		/// disposed while that's held, and we don't want to find out whether disposing waits on events.
		/// </summary>
		volatile IListener[] listeners = [];

		public bool IsEmpty => listeners.Length == 0;

		public IListener[] Listeners => listeners;

		public Root( string path )
		{
			Path = path;
			watcher = new System.IO.FileSystemWatcher( path ) { IncludeSubdirectories = true, NotifyFilter = RootFilters };

			watcher.Changed += ( _, e ) => Dispatch( WatcherChangeTypes.Changed, e.FullPath );
			watcher.Created += ( _, e ) => Dispatch( WatcherChangeTypes.Created, e.FullPath );
			watcher.Deleted += ( _, e ) => Dispatch( WatcherChangeTypes.Deleted, e.FullPath );
			watcher.Renamed += ( _, e ) => DispatchRename( e.FullPath, e.OldFullPath );
			watcher.Error += ( _, e ) => { foreach ( var l in listeners ) l.OnError( e.GetException() ); };

			try
			{
				// Where the inotify instance gets made, so where running out of them throws
				watcher.EnableRaisingEvents = true;
			}
			catch
			{
				watcher.Dispose();
				throw;
			}
		}

		public void Add( IListener l ) => listeners = [.. listeners, l];

		public void Remove( IListener l ) => listeners = Array.FindAll( listeners, x => x != l );

		public void Dispose() => watcher.Dispose();

		void Dispatch( WatcherChangeTypes type, string fullPath )
		{
			foreach ( var l in listeners )
			{
				if ( Sees( l, fullPath ) )
					l.OnEvent( type, fullPath, null );
			}
		}

		void DispatchRename( string fullPath, string oldFullPath )
		{
			foreach ( var l in listeners )
			{
				var to = Sees( l, fullPath );
				var from = Sees( l, oldFullPath );

				if ( to && from )
					l.OnEvent( WatcherChangeTypes.Renamed, fullPath, oldFullPath );
				// A root wider than the watch sees moves across its edge as renames. Watching just its own
				// folder it would have seen something arrive or leave, so it's told that.
				else if ( to )
					l.OnEvent( WatcherChangeTypes.Created, fullPath, null );
				else if ( from )
					l.OnEvent( WatcherChangeTypes.Deleted, oldFullPath, null );
			}
		}
	}

	internal static void Subscribe( IListener listener )
	{
		lock ( RootsLock )
		{
			foreach ( var root in Roots )
			{
				if ( !Covers( root.Path, listener.Directory ) )
					continue;

				root.Add( listener );
				listener.Root = root;
				return;
			}

			var created = new Root( listener.Directory );

			// Roots under this one are covered twice over now. Move their watches across before
			// letting them go, so nothing goes unwatched in between - an event seen twice is fine.
			for ( var i = Roots.Count - 1; i >= 0; i-- )
			{
				var covered = Roots[i];
				if ( !Covers( created.Path, covered.Path ) )
					continue;

				foreach ( var moved in covered.Listeners )
				{
					created.Add( moved );
					moved.Root = created;
				}

				Roots.RemoveAt( i );
				covered.Dispose();
			}

			created.Add( listener );
			listener.Root = created;
			Roots.Add( created );
		}
	}

	internal static void Unsubscribe( IListener listener )
	{
		lock ( RootsLock )
		{
			var root = listener.Root;
			if ( root is null )
				return;

			listener.Root = null;
			root.Remove( listener );

			// A root that grew to cover a parent outlives the watch that asked for the parent, for
			// whoever is left under it. That costs inotify watches, not instances.
			if ( !root.IsEmpty )
				return;

			Roots.Remove( root );
			root.Dispose();
		}
	}

	/// <summary>
	/// Whether <paramref name="path"/> is <paramref name="root"/> or somewhere below it.
	/// </summary>
	static bool Covers( string root, string path )
	{
		if ( !path.StartsWith( root, StringComparison.Ordinal ) )
			return false;

		return path.Length == root.Length || root.EndsWith( '/' ) || path[root.Length] == '/';
	}

	/// <summary>
	/// Whether <paramref name="listener"/> would have seen <paramref name="fullPath"/> watching on its own.
	/// Checked before anything is made for an event, because most of what a root reports is for someone else.
	/// </summary>
	static bool Sees( IListener listener, string fullPath )
	{
		var directory = listener.Directory;
		if ( fullPath.Length <= directory.Length || !Covers( directory, fullPath ) )
			return false;

		if ( listener.IncludeSubdirectories )
			return true;

		var nameStart = directory.EndsWith( '/' ) ? directory.Length : directory.Length + 1;
		return fullPath.IndexOf( '/', nameStart ) < 0;
	}

	/// <summary>
	/// Zio's watcher over one folder of a physical filesystem on Linux: a listener on a shared root.
	/// </summary>
	sealed class InotifyWatcher : Zio.FileSystems.FileSystemWatcher, IListener
	{
		readonly PhysicalFileSystem physical;

		public string Directory { get; }

		public Root Root { get; set; }

		public InotifyWatcher( PhysicalFileSystem fileSystem, UPath path ) : base( fileSystem, fileSystem.ConvertPathFromInternal( fileSystem.ConvertPathToInternal( path ) ) )
		{
			physical = fileSystem;
			Directory = fileSystem.ConvertPathToInternal( Path );

			// What Zio's own physical watcher uses - the base default of "*.*" isn't the same thing everywhere
			Filter = "*";
		}

		public override bool EnableRaisingEvents
		{
			get => base.EnableRaisingEvents;
			set
			{
				if ( value == base.EnableRaisingEvents )
					return;

				if ( value )
					Subscribe( this );
				else
					Unsubscribe( this );

				base.EnableRaisingEvents = value;
			}
		}

		protected override void Dispose( bool disposing )
		{
			if ( disposing )
				EnableRaisingEvents = false;

			base.Dispose( disposing );
		}

		public void OnEvent( WatcherChangeTypes type, string fullPath, string oldFullPath )
		{
			var path = physical.ConvertPathFromInternal( fullPath );

			switch ( type )
			{
				case WatcherChangeTypes.Created: RaiseCreated( new FileChangedEventArgs( FileSystem, Zio.WatcherChangeTypes.Created, path ) ); break;
				case WatcherChangeTypes.Deleted: RaiseDeleted( new FileChangedEventArgs( FileSystem, Zio.WatcherChangeTypes.Deleted, path ) ); break;
				case WatcherChangeTypes.Renamed: RaiseRenamed( new FileRenamedEventArgs( FileSystem, Zio.WatcherChangeTypes.Renamed, path, physical.ConvertPathFromInternal( oldFullPath ) ) ); break;
				default: RaiseChanged( new FileChangedEventArgs( FileSystem, Zio.WatcherChangeTypes.Changed, path ) ); break;
			}
		}

		public void OnError( Exception exception ) => RaiseError( new FileSystemErrorEventArgs( exception ) );
	}
}
