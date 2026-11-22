using Sandbox.Utility;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Sandbox;

/// <summary>
/// The game window's frame thread on Borealis.Gpu (docs/reference/borealis-render-thread.md, step 1): once the main thread
/// has presented a frame it hands the next device frame's start over (the wait for the frame before last on the GPU, the
/// slot's recycle) and the acquire of the window's next image, and goes on with its frame end and the next frame's game
/// code. Its first use of the device in that frame joins (<see cref="BorealisGpuRenderBackend.FollowFrame"/>). Recording,
/// submitting and presenting stay on the main thread.
/// </summary>
internal sealed class BorealisGpuFrameThread : IDisposable
{
	/// <summary>
	/// Whether the game runs one: not with <c>-norenderthread</c> (serial, for A/B runs and bisecting), with
	/// <c>-serialrecording</c>, or with <c>BOREALIS_RENDERTHREAD=0</c>.
	/// </summary>
	internal static readonly bool Enabled = !CommandLine.HasSwitch( "-norenderthread" ) && !CommandLine.HasSwitch( "-serialrecording" )
		&& System.Environment.GetEnvironmentVariable( "BOREALIS_RENDERTHREAD" ) != "0";

	/// <summary>
	/// Whether the thread also draws the game window's frames (the render thread, docs/reference/borealis-render-thread.md):
	/// with <c>-renderthread</c> or <c>BOREALIS_RENDERTHREAD=draw</c>, and the frame thread on. Off by default: it loses the
	/// device in the game window (the report of the card that made it says what's known).
	/// </summary>
	internal static readonly bool Draws = Enabled && (CommandLine.HasSwitch( "-renderthread" ) || System.Environment.GetEnvironmentVariable( "BOREALIS_RENDERTHREAD" ) == "draw");

	/// <summary>The longest <see cref="Dispose"/> waits for the thread; one still busy is left to the process's exit.</summary>
	internal static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds( 2 );

	readonly Action work;
	readonly Func<TimeSpan> idle;
	readonly Thread thread;
	readonly bool timed, markRenderThread;

	// Kept events: waiting on them allocates nothing once each has blocked once
	readonly ManualResetEventSlim go = new( false, 64 );
	readonly ManualResetEventSlim done = new( true, 64 );
	volatile bool stopping;
	ExceptionDispatchInfo error;

	/// <summary>Whether the work handed over has finished (it may not have been joined yet).</summary>
	public bool Done => done.IsSet;

	/// <summary>Handed over and not joined yet (the main thread's view).</summary>
	public bool Pending { get; private set; }

	/// <summary>Hand-overs so far, and those whose join had to wait for the thread, for the tests.</summary>
	public long HandOvers { get; private set; }

	/// <inheritdoc cref="HandOvers"/>
	public long JoinsWaited { get; private set; }

	static readonly Sandbox.Diagnostics.PerformanceStats.Timings JoinTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Get( "Borealis.Gpu frame join" );
	static readonly Sandbox.Diagnostics.PerformanceStats.Timings AheadTiming = Sandbox.Diagnostics.PerformanceStats.Timings.Get( "Borealis.Gpu frame ahead" );

	/// <summary>
	/// A thread that runs <paramref name="work"/> each time it's handed a frame; <paramref name="timed"/> times it and the
	/// joins that wait in the engine's timings (which need the engine's native side). Once the work is done and until the
	/// next hand-over the thread calls <paramref name="idle"/>, if given, again after each time it returns, until it returns a
	/// negative time (the window presenting its last frame again while the main thread is away).
	/// </summary>
	public BorealisGpuFrameThread( Action work, string name = "Borealis.Gpu frame", bool timed = true, bool renderThread = false, Func<TimeSpan> idle = null )
	{
		this.work = work;
		this.idle = idle;
		this.timed = timed;
		markRenderThread = renderThread;
		thread = new Thread( Loop ) { IsBackground = true, Name = name, Priority = ThreadPriority.AboveNormal };
		thread.Start();
	}

	/// <summary>Run the work on the thread. The last hand-over must have been joined.</summary>
	public void HandOver()
	{
		if ( Pending ) throw new InvalidOperationException( "The frame thread's last frame wasn't joined" );
		if ( stopping ) return;
		Pending = true;
		HandOvers++;
		done.Reset();
		go.Set();
	}

	/// <summary>
	/// Wait until the work handed over has finished, and throw what it threw. Nothing when nothing was handed over.
	/// </summary>
	public void Join()
	{
		if ( !Pending ) return;
		Pending = false;
		if ( !done.IsSet )
		{
			JoinsWaited++;
			using ( timed ? JoinTiming.Scope() : default )
			{
				if ( !done.Wait( LongJoin ) )
				{
					// Never expected: said once with where each thread is, then the join waits on
					if ( !warnedLongJoin )
					{
						warnedLongJoin = true;
						Log.Warning( $"The main thread has waited {LongJoin.TotalSeconds:F0} s for the {thread.Name} thread, which is at '{Doing}'; the main thread at {new System.Diagnostics.StackTrace( 1, false )}" );
					}

					done.Wait();
				}
			}
		}

		if ( error is { } e )
		{
			error = null;
			e.Throw();
		}
	}

	static readonly TimeSpan LongJoin = TimeSpan.FromSeconds( 5 );
	bool warnedLongJoin;

	/// <summary>What the thread's work says it's doing (a step's static name), for a join that waits too long.</summary>
	internal static string Doing { get; set; } = "idle";

	void Loop()
	{
		// It draws the frames handed over with the main thread's rendering code (BorealisGpuRenderBackend.HandOverRenderFrame)
		if ( markRenderThread ) ThreadSafe.MarkRenderThread();

		while ( true )
		{
			go.Wait();
			go.Reset();
			if ( stopping ) return;

			try
			{
				using ( timed ? AheadTiming.Scope() : default ) work();
				Doing = "idle";
			}
			catch ( Exception e )
			{
				error = ExceptionDispatchInfo.Capture( e );
			}
			finally
			{
				done.Set();
			}

			// Until the next hand-over, which ends the wait at once
			if ( idle is not null )
			{
				var wait = idle();
				while ( wait >= TimeSpan.Zero && !stopping && !go.Wait( wait ) ) wait = idle();
			}
		}
	}

	/// <summary>Stop the thread, waiting at most <see cref="StopTimeout"/> for the work it's doing.</summary>
	public void Dispose()
	{
		stopping = true;
		if ( Pending )
		{
			Pending = false;
			done.Wait( StopTimeout );
		}

		go.Set();
		thread.Join( StopTimeout );
	}
}
