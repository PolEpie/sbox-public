using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Sandbox;

public static class LauncherEnvironment
{
	/// <summary>
	/// The folder containing sbox.exe
	/// </summary>
	public static string GamePath { get; set; }

	/// <summary>
	/// The folder containing Sandbox.Engine.dll
	/// </summary>
	public static string ManagedDllPath { get; set; }

	public static string PlatformName
	{
		get
		{
			var platform = OperatingSystem.IsWindows() ? "win"
				: OperatingSystem.IsLinux() ? "linuxsteamrt"
				: OperatingSystem.IsMacOS() ? "osx"
				: throw new Exception( "Unsupported platform" );

			var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "64";
			return $"{platform}{architecture}";
		}
	}

	/// <summary>
	/// On macOS, become the copy of this executable in its app bundle (game/sbox-dev.app, which the build makes beside
	/// game/sbox-dev) if there is one and this isn't it: Game Mode only engages for the executable of a bundle in the games
	/// category. Same process, same arguments. Not under a debugger, which would lose the process.
	/// </summary>
	public static void RelaunchInAppBundle()
	{
		if ( !OperatingSystem.IsMacOS() || System.Diagnostics.Debugger.IsAttached ) return;

		var executable = Environment.ProcessPath;
		if ( executable is null || executable.Contains( ".app/Contents/MacOS/", StringComparison.Ordinal ) ) return;

		var name = System.IO.Path.GetFileName( executable );
		var bundled = System.IO.Path.Combine( System.IO.Path.GetDirectoryName( executable ), $"{name}.app", "Contents", "MacOS", name );
		if ( !System.IO.File.Exists( bundled ) ) return;

		// execv wants argv null terminated: the last element stays null
		var args = Environment.GetCommandLineArgs();
		var argv = new string[args.Length + 1];
		argv[0] = bundled;
		Array.Copy( args, 1, argv, 1, args.Length - 1 );
		execv( bundled, argv );

		// Only returns if it failed: carry on as this one
	}

	[DllImport( "libc", SetLastError = true )]
	static extern int execv( string path, string[] argv );

	public static void Init()
	{
		AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;

		GamePath = System.IO.Path.TrimEndingDirectorySeparator( AppContext.BaseDirectory );

		// this exe is in a folder inside bin - bin/win64, bin/managed
		if ( System.IO.Path.GetFileName( System.IO.Path.GetDirectoryName( GamePath ) ) == "bin" )
		{
			// go up two folders
			GamePath = System.IO.Path.GetDirectoryName( GamePath );
			GamePath = System.IO.Path.GetDirectoryName( GamePath );
		}

		// this exe is in the game folder
		ManagedDllPath = $"{GamePath}/bin/managed/";
		var nativeDllPath = $"{GamePath}/bin/{PlatformName}/";

		// make the game dir our current dir
		Environment.CurrentDirectory = GamePath;

		System.Environment.SetEnvironmentVariable( "FACEPUNCH_ENGINE", GamePath );

		UpdateNativeDllPath( nativeDllPath );

		// Don't update the user-level FACEPUNCH_ENGINE env var if we're running an exported standalone game!
#if !SBOX_STANDALONE
		_ = UpdateEnginePathAsync( GamePath );
#endif
	}

	/// <summary>
	/// Allows unit tests and csproj to find the engine path.
	/// </summary>
	private static async Task UpdateEnginePathAsync( string gamePath )
	{
		try
		{
			await Task.Run( () =>
			{
				if ( System.Environment.GetEnvironmentVariable( "FACEPUNCH_ENGINE", EnvironmentVariableTarget.User ) != gamePath )
				{
					System.Environment.SetEnvironmentVariable( "FACEPUNCH_ENGINE", gamePath, EnvironmentVariableTarget.User );
				}
			} ).ConfigureAwait( false );
		}
		catch ( Exception e )
		{
			Console.Error.WriteLine( $"Failed to update FACEPUNCH_ENGINE: {e}" );
		}
	}

	private static void UpdateNativeDllPath( string nativeDllPath )
	{
		// WARNING: this calls into Sandbox.Engine.dll - so we need to put it in
		// this method, which is executed AFTER CurrentDomain_AssemblyResolve is set
		// so that managed can find the correct dll
		NetCore.NativeDllPath = nativeDllPath;

		//
		// Put our native dll path first so that when looking up native dlls we'll
		// always use the ones from our folder first
		//
		if ( OperatingSystem.IsWindows() )
		{
			var path = System.Environment.GetEnvironmentVariable( "PATH" );
			path = $"{nativeDllPath};{path}";
			System.Environment.SetEnvironmentVariable( "PATH", path );
		}
	}

	private static Assembly CurrentDomain_AssemblyResolve( object sender, ResolveEventArgs args )
	{
		var trim = args.Name.Split( ',' )[0];

		var name = $"{ManagedDllPath}/{trim}.dll";

		// dlls with resources inside appear as a different name
		name = name.Replace( ".resources.dll", ".dll" );

		// Assembly names can come from untrusted code archives, never load from outside the managed folder
		if ( string.IsNullOrEmpty( ManagedDllPath ) )
			return null;

		name = System.IO.Path.GetFullPath( name );
		if ( !name.StartsWith( System.IO.Path.TrimEndingDirectorySeparator( System.IO.Path.GetFullPath( ManagedDllPath ) ) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ) )
			return null;

		if ( System.IO.File.Exists( name ) )
		{
			return Assembly.LoadFrom( name );
		}

		return null;
	}
}
