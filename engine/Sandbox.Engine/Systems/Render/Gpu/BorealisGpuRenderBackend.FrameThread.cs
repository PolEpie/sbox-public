namespace Sandbox;

internal sealed partial class BorealisGpuRenderBackend
{
	BorealisGpuFrameThread frameThread;
	BorealisGpuWindow aheadWindow;
	ulong aheadFrame, aheadDeviceFrame;
	Action startAhead;

	/// <summary>Whether a device frame was started ahead and the main thread hasn't joined it yet.</summary>
	internal bool FrameAhead => frameThread?.Pending ?? false;

	/// <summary>The game window's frame thread, once a frame was handed to it.</summary>
	internal BorealisGpuFrameThread FrameThread => frameThread;

	/// <summary>
	/// After the engine frame's last present (the game window's): start the next engine frame's device frame on the frame
	/// thread, and acquire <paramref name="window"/>'s next image there, while the main thread ends this frame and runs the
	/// next one's game code. What reads the game or its settings is done here first, on the main thread; the rest of the
	/// frame start runs when the main thread joins (<see cref="FollowFrame"/>). Nothing when the backend doesn't follow
	/// frames, the frame thread is off (<see cref="BorealisGpuFrameThread.Enabled"/>) or the device is lost.
	/// </summary>
	internal void HandOverFrame( BorealisGpuWindow window )
	{
		if ( !followsFrames || !BorealisGpuFrameThread.Enabled || lendNext is not null || gpu.IsLost ) return;
		JoinFrameThread();

		// What the next frame's start takes from this one: the uploads recorded since (in the closing device frame), the
		// player's settings and whether a load is showing
		var next = Application.FrameCount + 1;
		SubmitUploads();
		LimitAnisotropy();
		BiasMips( next );
		services.Loading = GameLoading( Sandbox.Engine.GlobalContext.Game?.ActiveScene );

		frameThread ??= new BorealisGpuFrameThread( startAhead ??= StartAhead );
		aheadFrame = next;
		aheadDeviceFrame = gpu.FrameNumber + 1;
		aheadWindow = window;
		frameThread.HandOver();
	}

	// On the frame thread: the device's half of the frame start, then the window's acquire. Touches only the device and the
	// window's swap chain, which the main thread leaves alone until it joins
	void StartAhead()
	{
		services.BeginDeviceFrame();
		aheadWindow?.AcquireAhead();
	}

	/// <summary>
	/// Join the frame thread if a frame was handed to it, then finish that frame's start on this thread: what frees the
	/// backend's own resources, the frame services (readbacks' callbacks, texture streaming) and the GPU timings.
	/// </summary>
	void JoinFrameThread()
	{
		if ( frameThread is not { Pending: true } thread ) return;
		using var timing = FrameStartTiming.Scope();
		var window = aheadWindow;
		aheadWindow = null;
		try
		{
			thread.Join();
		}
		catch ( Borealis.Gpu.GpuException e ) when ( e.IsDeviceLost )
		{
			// Said and handled as at the frame's own start; the window acquires again, which throws
			window?.ForgetAhead();
			BorealisGpuDeviceLost.Check( gpu );
			throw;
		}

		frame = aheadFrame;
		FreeModelGeometry();
		FreeCollectedBackings();
		services.BeginFrameServices();
		TimeFrame();
		if ( frame % PipelineCacheSaveFrames == 0 ) SavePipelineCacheInBackground();
	}

	/// <summary>
	/// Wait until the device frame started ahead has switched (<see cref="Borealis.Gpu.Device.BeginFrame"/>'s first step), not
	/// for the rest of its start: a list begun after it goes in the new frame. Joins instead if the thread is done or failed.
	/// </summary>
	void WaitForAheadSwitch()
	{
		var spin = new System.Threading.SpinWait();
		while ( gpu.FrameNumber < aheadDeviceFrame && FrameAhead && !gpu.IsLost )
		{
			if ( frameThread.Done ) break;
			spin.SpinOnce();
		}

		if ( gpu.FrameNumber < aheadDeviceFrame ) FollowFrame();
	}

	/// <summary>Join the frame thread and stop it: the window or the backend is going away.</summary>
	internal void StopFrameThread()
	{
		if ( frameThread is null ) return;
		try
		{
			JoinFrameThread();
		}
		finally
		{
			frameThread.Dispose();
			frameThread = null;
		}
	}
}
