using System;
using System.Collections.Generic;
using System.Threading;

namespace BorealisTests;

/// <summary>
/// The game window's frame thread (<see cref="Sandbox.BorealisGpuFrameThread"/>): the work handed over runs on its own
/// thread, the join waits for it and throws what it threw, stopping never waits longer than the timeout, and a hand-over
/// and join allocate nothing on either thread once warm.
/// </summary>
[TestClass]
public class FrameThreadTests
{
	[TestMethod]
	public void HandedOverWorkRunsOnItsThreadAndTheJoinWaitsForIt()
	{
		var ran = -1;
		var thread = new Sandbox.BorealisGpuFrameThread( () =>
		{
			Thread.Sleep( 30 );
			ran = Environment.CurrentManagedThreadId;
		}, timed: false );

		try
		{
			thread.HandOver();
			Assert.IsTrue( thread.Pending );
			thread.Join();
			Assert.IsFalse( thread.Pending );
			Assert.AreNotEqual( -1, ran, "the join waited for the work" );
			Assert.AreNotEqual( Environment.CurrentManagedThreadId, ran, "on a thread of its own" );
			Assert.AreEqual( 1, thread.JoinsWaited );

			// Joining again, or with nothing handed over, does nothing
			thread.Join();
			Assert.AreEqual( 1, thread.HandOvers );
		}
		finally
		{
			thread.Dispose();
		}
	}

	[TestMethod]
	public void WhatTheWorkThrowsIsThrownAtTheJoin()
	{
		var calls = 0;
		var thread = new Sandbox.BorealisGpuFrameThread( () =>
		{
			if ( ++calls == 1 ) throw new InvalidOperationException( "the frame start failed" );
		}, timed: false );

		try
		{
			thread.HandOver();
			var e = Assert.ThrowsException<InvalidOperationException>( thread.Join );
			Assert.AreEqual( "the frame start failed", e.Message );

			// The thread goes on
			thread.HandOver();
			thread.Join();
			Assert.AreEqual( 2, calls );
		}
		finally
		{
			thread.Dispose();
		}
	}

	[TestMethod]
	public void StoppingWaitsNoLongerThanTheTimeout()
	{
		using var never = new ManualResetEventSlim();
		var thread = new Sandbox.BorealisGpuFrameThread( () => never.Wait(), timed: false );
		thread.HandOver();

		var clock = System.Diagnostics.Stopwatch.StartNew();
		thread.Dispose();
		Assert.IsTrue( clock.Elapsed < Sandbox.BorealisGpuFrameThread.StopTimeout * 2 + TimeSpan.FromSeconds( 1 ), $"stopped in {clock.Elapsed}" );
		never.Set();
	}

	[TestMethod, TestCategory( Allocations.Category )]
	public void HandOverAndJoinAllocateNothingOnEitherThread()
	{
		long last = -1, frameThreadBytes = 0;
		var thread = new Sandbox.BorealisGpuFrameThread( () =>
		{
			// What the frame thread allocated since the last hand-over: its loop's waits and timings
			var now = GC.GetAllocatedBytesForCurrentThread();
			frameThreadBytes = last < 0 ? 0 : now - last;
			last = now;
		}, timed: false );

		try
		{
			Allocations.AssertSettles( "a hand-over and join on the main thread", () =>
			{
				thread.HandOver();
				thread.Join();
			} );

			var bytes = new List<long>();
			var settled = 0;
			for ( int i = 0; i < 100 && settled < 5; i++ )
			{
				thread.HandOver();
				thread.Join();
				bytes.Add( frameThreadBytes );
				settled = frameThreadBytes == 0 ? settled + 1 : 0;
			}

			Assert.AreEqual( 5, settled, $"the frame thread kept allocating: {string.Join( ", ", bytes )}" );
		}
		finally
		{
			thread.Dispose();
		}
	}
}
