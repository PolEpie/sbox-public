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

		frameThread ??= NewFrameThread( BorealisGpuFrameThread.Draws );
		aheadFrame = next;
		aheadDeviceFrame = gpu.FrameNumber + 1;
		aheadWindow = window;
		ArmKeepPresenting( window );
		frameThread.HandOver();
	}

	/// <summary>
	/// At the end of the main thread's half of a frame (the game window's extract, <c>GameWindow.Render</c>): hand the frame's
	/// drawing, <paramref name="draw"/> (the cameras, the overlay, the present), to the render thread (this backend's frame
	/// thread, docs/reference/borealis-render-thread.md), which then starts the next device frame and acquires the window's next
	/// image as <see cref="HandOverFrame"/> does, while the main thread ends this frame and runs the next one's game code.
	/// The main thread's first use of the device meanwhile waits for the drawing (<see cref="FromMain"/>), its first block or
	/// scene render for all of it (<see cref="FollowFrame"/>). False, with nothing handed over, when there's no render thread:
	/// the caller draws the frame itself.
	/// </summary>
	internal bool HandOverRenderFrame( BorealisGpuWindow window, Action draw )
	{
		if ( !followsFrames || !BorealisGpuFrameThread.Draws || lendNext is not null || gpu.IsLost || list is not null ) return false;
		JoinFrameThread();

		// What the next frame's start takes from this one, as at HandOverFrame; the uploads recorded since the last block go
		// before the frame's blocks
		var next = Application.FrameCount + 1;
		if ( staged.Count > 0 ) WriteStaged();
		SubmitUploads();
		LimitAnisotropy();
		BiasMips( next );
		services.Loading = GameLoading( Sandbox.Engine.GlobalContext.Game?.ActiveScene );

		frameThread ??= NewFrameThread( renderThread: true );
		renderDraw = draw;
		drawFrame = Application.FrameCount;
		aheadFrame = next;
		aheadDeviceFrame = gpu.FrameNumber + 1;
		aheadWindow = window;
		ArmKeepPresenting( window );
		drawn.Reset();
		drawing = true;
		Sandbox.Rendering.RenderSync.HandOver();
		frameThread.HandOver();
		return true;
	}

	// The frame the render thread draws (HandOverRenderFrame) and its number, and whether it's drawing it
	Action renderDraw;
	ulong drawFrame;
	volatile bool drawing;
	readonly System.Threading.ManualResetEventSlim drawn = new( true, 64 );

	/// <summary>Whether the render thread is drawing a frame handed over (<see cref="HandOverRenderFrame"/>).</summary>
	internal bool Drawing => drawing;

	/// <summary>Frames the render thread drew, and main-thread uses of the device that waited for one, for the tests and stats.</summary>
	internal long FramesDrawnOnThread { get; private set; }

	/// <inheritdoc cref="FramesDrawnOnThread"/>
	internal long MainWaits { get; private set; }

	static readonly Sandbox.Diagnostics.PerformanceStats.Timings RenderThreadDrawTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Get( "Borealis render thread draw" );
	static readonly Sandbox.Diagnostics.PerformanceStats.Timings MainWaitTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Get( "Borealis main waits for the render thread" );

	/// <summary>
	/// Called where the main thread uses the device's recording state outside a block of its own (a buffer write, the list of
	/// uploads, a readback): while the render thread draws a frame that state is its, so the main thread waits for the drawing
	/// to finish first. Nothing on the render thread, or with nothing drawing.
	/// </summary>
	[System.Runtime.CompilerServices.MethodImpl( System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining )]
	void FromMain()
	{
		if ( ThreadSafe.IsRenderThread ) return;
		if ( drawing ) WaitForDrawing();
		if ( staged.Count > 0 && ThreadSafe.IsMainThread ) WriteStaged();
	}

	// Buffer writes the main thread made while the render thread drew (SetBufferData), their bytes, and how many there were
	readonly List<(GpuBuffer Buffer, int Offset, int Size, int At)> staged = new();
	byte[] stagedBytes = [];
	int stagedUsed;

	/// <summary>Buffer writes made on the main thread while the render thread drew, kept for after it, for the tests and stats.</summary>
	internal long BufferWritesStaged { get; private set; }

	void StageBufferData( GpuBuffer buffer, ReadOnlySpan<byte> bytes, int offset )
	{
		if ( stagedUsed + bytes.Length > stagedBytes.Length ) Array.Resize( ref stagedBytes, Math.Max( stagedBytes.Length * 2, Math.Max( 64 << 10, stagedUsed + bytes.Length ) ) );
		bytes.CopyTo( stagedBytes.AsSpan( stagedUsed ) );
		staged.Add( (buffer, offset, bytes.Length, stagedUsed) );
		stagedUsed += bytes.Length;
		BufferWritesStaged++;
	}

	// The writes kept while the render thread drew, in the order they were made, now that it's done (the main thread's next
	// use of the device, its join)
	void WriteStaged()
	{
		for ( int i = 0; i < staged.Count; i++ )
		{
			var (buffer, offset, size, at) = staged[i];
			if ( buffer.IsValid ) WriteBufferData( buffer, stagedBytes.AsSpan( at, size ), offset );
		}

		staged.Clear();
		stagedUsed = 0;
	}

	void WaitForDrawing()
	{
		MainWaits++;
		using ( Sandbox.Rendering.RenderSync.RenderWait() )
		using ( MainWaitTiming.Scope() )
		{
			if ( drawn.Wait( LongWait ) ) return;

			// Never expected: said once with where the main thread waits, then it waits on
			if ( !warnedLongWait )
			{
				warnedLongWait = true;
				Log.Warning( $"The main thread has waited {LongWait.TotalSeconds:F0} s for the render thread's frame, which is at '{BorealisGpuFrameThread.Doing}'; the main thread at {new System.Diagnostics.StackTrace( 1, false )}" );
			}

			drawn.Wait();
		}
	}

	static readonly TimeSpan LongWait = TimeSpan.FromSeconds( 5 );
	bool warnedLongWait;

	// On the frame thread: the frame handed over drawn (HandOverRenderFrame), then the device's half of the next frame's start
	// and the window's acquire. The frame start touches only the device and the window's swap chain, which the main thread
	// leaves alone until it joins
	void StartAhead()
	{
		if ( renderDraw is { } draw )
		{
			renderDraw = null;
			BorealisGpuFrameThread.Doing = "drawing the frame";
			Application.DrawingFrame( drawFrame );
			try
			{
				using ( RenderThreadDrawTiming.Scope() )
				using ( Borealis.Zones.RenderThreadFrame.Start() )
					draw();
				FramesDrawnOnThread++;
			}
			finally
			{
				Application.DrawingFrame( 0 );
				drawing = false;
				drawn.Set();
			}
		}

		BorealisGpuFrameThread.Doing = "starting the device frame";
		services.BeginDeviceFrame();
		BorealisGpuFrameThread.Doing = "acquiring the window's image";
		aheadWindow?.AcquireAhead();
		StartKeepPresenting();
	}

	BorealisGpuFrameThread NewFrameThread( bool renderThread ) => new( startAhead ??= StartAhead, renderThread: renderThread, idle: KeepPresenting ? KeepPresentingIdle : null );

	/// <summary>
	/// Join the frame thread if a frame was handed to it, then finish that frame's start on this thread: what frees the
	/// backend's own resources, the frame services (readbacks' callbacks, texture streaming) and the GPU timings.
	/// </summary>
	void JoinFrameThread()
	{
		// The render thread doesn't join itself: it's drawing the frame the main thread joins
		if ( frameThread is not { Pending: true } thread || ThreadSafe.IsRenderThread ) return;
		using var timing = FrameStartTiming.Scope();
		StopKeepPresenting();
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

		// The buffer writes made meanwhile, what the frame it drew left to dispose of, and its render targets go now
		// (EngineLoop.FrameEnd waited for it), and what was released meanwhile
		if ( staged.Count > 0 ) WriteStaged();
		EngineLoop.RenderThreadFrameDone();
		ReleaseDeferred();
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
			drawing = false;
			drawn.Set();
			EngineLoop.RenderThreadFrameDone();
			ReleaseDeferred();
		}
	}
}
