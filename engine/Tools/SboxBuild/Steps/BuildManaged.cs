using static Facepunch.Constants;

namespace Facepunch.Steps;

internal class BuildManaged( bool clean = false )
{
	internal ExitCode Run() => BuildDisplay.Run( "Build managed code", () =>
	{
		string engineDir = Path.Combine( Directory.GetCurrentDirectory(), "engine" );
		string rootDir = Directory.GetCurrentDirectory();

		try
		{
			Log.Info( "Step 1: Dotnet Clean" );
			if ( clean )
			{
				BuildDisplay.Status( "Clean managed projects" );
				if ( !Utility.RunDotnetCommand( engineDir, "clean" ) )
					return ExitCode.Failure;
			}
			else
			{
				Log.Info( "Skipping dotnet clean as cleanBuild is false." );
			}

			Log.Info( "Step 2: Dotnet Restore" );
			BuildDisplay.Status( "Restore managed dependencies" );
			if ( !Utility.RunDotnetCommand( engineDir, "restore" ) )
				return ExitCode.Failure;

			// CodeGen is built by the solution build below (Sandbox.Engine references it). Building it
			// on its own first was a second, Debug build of the whole engine.

			Log.Info( "Step 4: Clear managed folder" );
			BuildDisplay.Status( "Clear managed output" );
			string managedDir = Path.Combine( rootDir, "game", "bin", "managed" );
			if ( Directory.Exists( managedDir ) )
			{
				try
				{
					Directory.Delete( managedDir, true );
					Directory.CreateDirectory( managedDir ); // Recreate the empty directory
					Log.Info( $"Successfully cleared directory: {managedDir}" );
				}
				catch ( Exception ex )
				{
					Log.Warning( $"Warning: Failed to clear directory: {managedDir}. Error: {ex.Message}" );
					// Continue execution since this is a warning in the original script
				}
			}
			else
			{
				Log.Info( $"Directory does not exist, creating: {managedDir}" );
				Directory.CreateDirectory( managedDir );
			}

			Log.Info( "Step 5: Build Managed" );
			BuildDisplay.Status( "Build managed engine" );
			if ( !Utility.RunDotnetCommand( engineDir, "build -c Release Sandbox-Engine.slnx -p:TreatWarningsAsErrors=true" ) )
				return ExitCode.Failure;

			var launcherRid = OperatingSystem.IsWindows() ? "win-x64"
				: OperatingSystem.IsLinux() ? "linux-x64"
				: null;
			if ( launcherRid is not null )
			{
				Log.Info( $"Step 6: Publish {launcherRid} framework-dependent single-file launchers" );
				var publishRoot = Path.Combine( Path.GetTempPath(), $"sbox-launchers-{Guid.NewGuid():N}" );
				Directory.CreateDirectory( publishRoot );
				var completedLaunchers = 0;
				BuildDisplay.Status( $"Publish {launcherRid} launchers" );

				var launcherProjects = new[]
				{
					"Sbox/Sbox.csproj",
					"SboxDev/Sbox-Dev.csproj",
					"StandaloneTest/Sbox-Launcher.csproj",
					"SboxServer/Sbox-Server.csproj",
					"SboxBench/SboxBench.csproj"
				};
				BuildDisplay.Progress( completedLaunchers, launcherProjects.Length );

				foreach ( var project in launcherProjects )
				{
					var output = Path.Combine( publishRoot, Path.GetFileNameWithoutExtension( project ) );
					var launcherDir = Path.Combine( engineDir, "Launcher" );
					BuildDisplay.Detail( $"Restore {project}" );

					// Linux has no .NET redistributable the way game/_redist covers Windows, so the
					// runtime ships in game/bin/dotnet and the apphost is told to look there
					// relative to itself. Without this it falls through to /usr/share/dotnet and
					// runs against whatever the machine happens to have, or nothing at all. It
					// survives PublishSingleFile, though it has to be on the restore too: publish
					// runs --no-restore, and if only one of the pair sets it the single file
					// bundler is handed no apphost and fails with "Must specify the host binary".
					var relativeDotnet = OperatingSystem.IsLinux() ? " -p:AppHostRelativeDotNet=bin/dotnet" : "";

					if ( !Utility.RunDotnetCommand( launcherDir,
						$"restore {project} -r {launcherRid} -p:Configuration=Release -p:SelfContained=false -p:RestoreRecursive=false{relativeDotnet}" ) )
						return ExitCode.Failure;

					BuildDisplay.Detail( $"Publish {project}" );
					if ( !Utility.RunDotnetCommand( launcherDir,
						$"publish {project} -c Release -r {launcherRid} -p:SelfContained=false -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -p:BuildProjectReferences=false{relativeDotnet} --no-restore -o \"{output}\"" ) )
						return ExitCode.Failure;

					var name = project switch
					{
						"Sbox/Sbox.csproj" => "sbox",
						"SboxDev/Sbox-Dev.csproj" => "sbox-dev",
						"StandaloneTest/Sbox-Launcher.csproj" => "sbox-launcher",
						"SboxServer/Sbox-Server.csproj" => "sbox-server",
						_ => "benchmark"
					};
					var extension = OperatingSystem.IsWindows() ? ".exe" : "";
					File.Copy( Path.Combine( output, name + extension ), Path.Combine( rootDir, "game", name + extension ), true );
					BuildDisplay.Progress( ++completedLaunchers, launcherProjects.Length );
				}

				Directory.Delete( publishRoot, true );

				if ( OperatingSystem.IsLinux() && !StageLinuxRuntime( rootDir ) )
					return ExitCode.Failure;

				// delete any old .runtimeconfig.json that are hanging around
				BuildDisplay.Status( "Remove old launcher files" );
				var staleFiles = new[] { "sbox", "sbox-dev", "sbox-launcher", "sbox-server", "benchmark" }
					.SelectMany( name => new[] { $"{name}.dll", $"{name}.runtimeconfig.json" } )
					// sbox-standalone belongs in bin/managed; a copy in the root would match the depot's *.exe
					.Concat( ["sbox-standalone", "sbox-standalone.exe", "sbox-standalone.dll", "sbox-standalone.runtimeconfig.json"] );

				foreach ( var file in staleFiles )
				{
					var looseFile = Path.Combine( rootDir, "game", file );
					if ( File.Exists( looseFile ) ) File.Delete( looseFile );
				}
			}

			if ( OperatingSystem.IsMacOS() )
			{
				Log.Info( "Step 6: Wrap the game and editor in macOS app bundles" );
				BuildDisplay.Status( "Create app bundles" );
				foreach ( var (name, displayName, identifier, icon) in new[]
				{
					("sbox", "s&box", "com.facepunch.sbox", "engine/Launcher/Sbox/sbox.ico"),
					("sbox-dev", "s&box editor", "com.facepunch.sbox.dev", "engine/Launcher/SboxDev/sbox-dev.ico"),
				} )
				{
					if ( !CreateMacAppBundle( Path.Combine( rootDir, "game" ), name, displayName, identifier, Path.Combine( rootDir, icon ) ) )
						return ExitCode.Failure;
				}
			}

			Log.Info( "Build completed successfully!" );
			return ExitCode.Success;
		}
		catch ( Exception ex )
		{
			Log.Error( $"Build failed with error: {ex}" );
			return ExitCode.Failure;
		}
	} );

	/// <summary>
	/// Puts a copy of game/<paramref name="name"/> in game/<paramref name="name"/>.app, which launching the plain one re-runs
	/// (LauncherEnvironment.RelaunchInAppBundle). macOS gives Game Mode only to a full-screen process whose executable is the
	/// CFBundleExecutable of a bundle in the games category: the plain apphost is in no bundle, and an embedded
	/// __info_plist doesn't count. The copy's apphost is pointed back up to the managed dll beside the plain one, so the
	/// app's base directory stays game/.
	/// </summary>
	private static bool CreateMacAppBundle( string gameDir, string name, string displayName, string identifier, string iconPath )
	{
		var host = File.ReadAllBytes( Path.Combine( gameDir, name ) );

		// The SDK writes the app's dll into the apphost's path placeholder, zero padded to 1024 bytes
		var dll = System.Text.Encoding.UTF8.GetBytes( $"{name}.dll\0" );
		var relative = System.Text.Encoding.UTF8.GetBytes( $"../../../{name}.dll\0" );
		var at = host.AsSpan().IndexOf( dll );
		if ( at < 0 || host.AsSpan( at + 1 ).IndexOf( dll ) >= 0 || host.AsSpan( at + dll.Length, relative.Length - dll.Length ).ContainsAnyExcept( (byte)0 ) )
		{
			Log.Error( $"Couldn't find where game/{name}'s apphost names {name}.dll" );
			return false;
		}
		relative.CopyTo( host, at );

		var bundle = Path.Combine( gameDir, $"{name}.app" );
		var executable = Path.Combine( bundle, "Contents", "MacOS", name );
		if ( Directory.Exists( bundle ) ) Directory.Delete( bundle, true );
		Directory.CreateDirectory( Path.GetDirectoryName( executable ) );
		File.WriteAllBytes( executable, host );
		File.SetUnixFileMode( executable, File.GetUnixFileMode( Path.Combine( gameDir, name ) ) );

		var icns = IcnsFromIco( iconPath );
		if ( icns is null )
		{
			Log.Error( $"{iconPath} has no 256x256 PNG image to make {name}.app's icon from" );
			return false;
		}
		Directory.CreateDirectory( Path.Combine( bundle, "Contents", "Resources" ) );
		File.WriteAllBytes( Path.Combine( bundle, "Contents", "Resources", $"{name}.icns" ), icns );

		File.WriteAllText( Path.Combine( bundle, "Contents", "Info.plist" ), $"""
			<?xml version="1.0" encoding="UTF-8"?>
			<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
			<plist version="1.0">
			<dict>
				<key>CFBundleExecutable</key>
				<string>{name}</string>
				<key>CFBundleIconFile</key>
				<string>{name}</string>
				<key>CFBundleIdentifier</key>
				<string>{identifier}</string>
				<key>CFBundleName</key>
				<string>{System.Security.SecurityElement.Escape( displayName )}</string>
				<key>CFBundlePackageType</key>
				<string>APPL</string>
				<key>LSApplicationCategoryType</key>
				<string>public.app-category.games</string>
				<key>LSSupportsGameMode</key>
				<true/>
				<key>NSHighResolutionCapable</key>
				<true/>
			</dict>
			</plist>

			""" );

		// Patching the apphost broke the SDK's ad-hoc signature, and arm64 runs nothing unsigned
		return Utility.RunProcess( "codesign", $"--force --sign - \"{bundle}\"" );
	}

	/// <summary>
	/// An .icns holding the 256x256 PNG of an .ico, which the exe icons already carry, as its one image ('ic08').
	/// </summary>
	private static byte[] IcnsFromIco( string icoPath )
	{
		var ico = File.ReadAllBytes( icoPath );
		var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian( ico.AsSpan( 4 ) );
		for ( var i = 0; i < count; i++ )
		{
			var entry = ico.AsSpan( 6 + 16 * i, 16 );
			if ( entry[0] != 0 ) continue; // a width of 0 is 256

			var size = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( entry[8..] );
			var png = ico.AsSpan( (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( entry[12..] ), size );
			if ( !png.StartsWith( new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' } ) ) continue;

			var icns = new byte[16 + size];
			"icns"u8.CopyTo( icns );
			System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian( icns.AsSpan( 4 ), (uint)icns.Length );
			"ic08"u8.CopyTo( icns.AsSpan( 8 ) );
			System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian( icns.AsSpan( 12 ), (uint)(8 + size) );
			png.CopyTo( icns.AsSpan( 16 ) );
			return icns;
		}

		return null;
	}

	/// <summary>
	/// Lays the shared framework the Linux launchers resolve against into game/bin/dotnet.
	///
	/// Taken from the SDK running this build rather than downloaded, so what ships is the runtime
	/// the assemblies were compiled against, and the build needs no network for it. Only
	/// Microsoft.NETCore.App: every launcher's runtimeconfig.json asks for that alone, and
	/// Microsoft.AspNetCore.App would add 27MB nothing loads.
	/// </summary>
	private static bool StageLinuxRuntime( string rootDir )
	{
		// DOTNET_ROOT when something set it, otherwise walk up from the dotnet on PATH, which is
		// where dotnet-install.sh puts it and how CI reaches it.
		var dotnetRoot = Environment.GetEnvironmentVariable( "DOTNET_ROOT" );
		if ( string.IsNullOrEmpty( dotnetRoot ) )
			dotnetRoot = Path.GetDirectoryName( FindDotnetOnPath() );

		if ( string.IsNullOrEmpty( dotnetRoot ) || !Directory.Exists( Path.Combine( dotnetRoot, "shared" ) ) )
		{
			Log.Error( "Could not locate a .NET root to take the shared framework from. Set DOTNET_ROOT." );
			return false;
		}

		var framework = Path.Combine( dotnetRoot, "shared", "Microsoft.NETCore.App" );
		var source = Directory.Exists( framework )
			? Directory.GetDirectories( framework )
				// TryParse, not the constructor: prerelease folder names (10.0.0-rc.1.25451.107) are not a
				// Version and would throw out of the whole enumeration rather than fall through to the
				// error below.
				.Select( d => Version.TryParse( Path.GetFileName( d ), out var v ) ? new { Dir = d, Version = v } : null )
				.Where( x => x is not null && x.Version.Major == 10 )
				.OrderBy( x => x.Version )
				.LastOrDefault()
				?.Dir
			: null;

		if ( source is null )
		{
			Log.Error( $"No Microsoft.NETCore.App found under {framework}." );
			return false;
		}

		var version = Path.GetFileName( source );
		var target = Path.Combine( rootDir, "game", "bin", "dotnet" );
		var icuSource = Path.Combine( AppContext.BaseDirectory, "icu", "linux-x64" );
		// ICU is a separate native dependency, not part of Microsoft.NETCore.App. The SDK
		// container may have it even when the player's Steam runtime does not.
		foreach ( var library in new[] { "libicudata", "libicuuc", "libicui18n" } )
		{
			if ( !Directory.Exists( icuSource ) || Directory.GetFiles( icuSource, $"{library}.so.*" ).Length == 0 )
			{
				Log.Error( $"Missing bundled ICU library {library} in {icuSource}. Restore and rebuild SboxBuild." );
				return false;
			}
		}
		Log.Info( $"Step 7: Stage .NET {version} runtime into game/bin/dotnet" );
		BuildDisplay.Status( $"Stage .NET {version} runtime" );

		RecreateDirectory( target );
		CopyDirectory( source, Path.Combine( target, "shared", "Microsoft.NETCore.App", version ) );
		// App-local ICU is loaded through CoreLib's native search directories, including the
		// shared framework. This also survives our launcher publish filter (no app deps.json).
		CopyDirectory( icuSource, Path.Combine( target, "shared", "Microsoft.NETCore.App", version ) );
		// libhostfxr.so, which the apphost loads first and which then finds the framework above.
		CopyDirectory( Path.Combine( dotnetRoot, "host" ), Path.Combine( target, "host" ) );
		return true;
	}

	/// <summary>The dotnet on PATH, with symlinks followed: distributions commonly put a link in
	/// /usr/bin pointing at the real root, and it is the root we want.</summary>
	private static string FindDotnetOnPath()
	{
		foreach ( var dir in (Environment.GetEnvironmentVariable( "PATH" ) ?? "")
			.Split( Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries ) )
		{
			var candidate = Path.Combine( dir, "dotnet" );
			if ( !File.Exists( candidate ) ) continue;

			return File.ResolveLinkTarget( candidate, returnFinalTarget: true )?.FullName ?? candidate;
		}

		return null;
	}

	private static void CopyDirectory( string source, string destination )
	{
		Directory.CreateDirectory( destination );

		foreach ( var file in Directory.GetFiles( source ) )
			File.Copy( file, Path.Combine( destination, Path.GetFileName( file ) ), true );

		foreach ( var directory in Directory.GetDirectories( source ) )
			CopyDirectory( directory, Path.Combine( destination, Path.GetFileName( directory ) ) );
	}

	private static void RecreateDirectory( string path )
	{
		if ( Directory.Exists( path ) ) Directory.Delete( path, true );
		Directory.CreateDirectory( path );
	}
}
