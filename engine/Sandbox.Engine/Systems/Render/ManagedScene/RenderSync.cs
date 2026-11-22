using System.Diagnostics;
using System.Threading;

namespace Sandbox.Rendering;

/// <summary>
/// The sync point between the game and the frame it hands over (docs/reference/borealis-render-thread.md): where scene
/// objects' changes are written into their render objects.
/// </summary>
internal static class RenderSync
{
	static RenderSync()
	{
		// For test runs: BOREALIS_DEFERWRITES=1 defers everywhere, as the game window does
		if ( System.Environment.GetEnvironmentVariable( "BOREALIS_DEFERWRITES" ) == "1" ) DeferWrites = true;
	}

	/// <summary>
	/// Whether scene objects' changes, deletes included, wait for their world's next frame preparation (the sync point) instead
	/// of being written into their render objects as they're made: each changed object is written there once, with every change
	/// since the last frame, and render objects are written nowhere else. On in the game window on Borealis.Gpu unless it runs
	/// serially (<see cref="BorealisGpuFrameThread.Enabled"/>).
	/// </summary>
	internal static bool DeferWrites { get; set; }

	// Synchronous readbacks: calls, the time they waited, and the call sites seen
	static long readbacks, readbackTicks;
	static readonly HashSet<string> readbackSites = new();

	/// <summary>Synchronous readbacks so far (a thread waiting for the GPU to read something back), for the tests.</summary>
	internal static long Readbacks => Interlocked.Read( ref readbacks );

	/// <summary>
	/// Time a synchronous readback (<c>GpuBuffer.GetData</c>, <c>Texture.GetPixels</c>, a bitmap render): the calling thread
	/// waits for the GPU there, and with a render thread for it too. Each is counted, and each call site is logged once with
	/// how long it waited, found from the stack on the first call and at each power of two of calls (a stack each would
	/// allocate every frame for a game reading back every frame).
	/// </summary>
	internal static ReadbackScope Readback( string what ) => new( what, Stopwatch.GetTimestamp() );

	internal readonly ref struct ReadbackScope( string what, long start )
	{
		public void Dispose()
		{
			if ( what is null ) return;
			var ticks = Stopwatch.GetTimestamp() - start;
			var n = Interlocked.Increment( ref readbacks );
			Interlocked.Add( ref readbackTicks, ticks );
			if ( (n & (n - 1)) != 0 ) return;

			var site = CallSite();
			lock ( readbackSites )
			{
				if ( !readbackSites.Add( $"{what} from {site}" ) ) return;
			}

			Log.Info( $"Synchronous readback ({what}) from {site}: waited {ticks * 1000.0 / Stopwatch.Frequency:F2} ms for the GPU" );
		}
	}

	// The first frame outside the engine and the renderer (game or tool code), else the outermost engine frame named
	static string CallSite()
	{
		var frames = new StackTrace( 2, false ).GetFrames();
		string engine = null;
		foreach ( var frame in frames )
		{
			if ( frame.GetMethod() is not { DeclaringType: { } type } method ) continue;
			var assembly = type.Assembly.GetName().Name ?? "";
			var name = $"{type.FullName}.{method.Name}";
			if ( !assembly.StartsWith( "Sandbox.Engine" ) && !assembly.StartsWith( "Sandbox.Rendering" ) && !assembly.StartsWith( "Borealis" ) && !assembly.StartsWith( "System." ) ) return name;
			if ( engine is null && !type.FullName.StartsWith( "Sandbox.Rendering.RenderSync" ) && !type.Name.StartsWith( "BorealisGpu" ) && !type.Name.StartsWith( "SceneBridge" ) ) engine = name;
		}

		return engine ?? "unknown";
	}

	/// <summary>Say how many synchronous readbacks there were, how long they waited, and from where.</summary>
	[ConCmd( "r_sync_readbacks", Help = "How many synchronous GPU readbacks ran (each a stall for the GPU), how long they waited, and their call sites" )]
	internal static void PrintReadbacks()
	{
		Log.Info( $"{Readbacks} synchronous readbacks, {Interlocked.Read( ref readbackTicks ) * 1000.0 / Stopwatch.Frequency:F1} ms waiting" );
		lock ( readbackSites )
		{
			foreach ( var site in readbackSites ) Log.Info( $"  {site}" );
		}
	}
}
