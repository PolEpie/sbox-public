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
}
