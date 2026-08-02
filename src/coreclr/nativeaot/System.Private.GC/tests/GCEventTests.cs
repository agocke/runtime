// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Behavior tests for the GCEvent port -- the translation of GCEvent::Impl of gc/unix/events.cpp
// and of the Win32 GCEvent::Impl of gc/windows/gcenv.windows.cpp.
//
// The ported bodies are the code under test. Only the pthread / Win32 declarations underneath
// them are substituted, by SyncImports.*.TestHost.cs, which forwards each call to the real
// operating system and records it. So the tests check two things at once: that the sequence of
// calls the port makes is the one the C++ makes, and that the event that comes out behaves like
// an event -- auto versus manual reset, timeouts, wakeups, and the races between a signal and a
// reset.
//
// The expected constants are written out here rather than read from the port, so that a wrong
// constant fails a test instead of being confirmed by it. The C++ values they stand for are
// named in the comments.
//
// Each event is allocated in native memory, as the collector's events are: a GCEvent is a plain
// pointer-sized struct with no managed state, and the threads a test starts have to share one.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

/// <summary>
/// Every test class that injects into or records the substituted imports shares that state
/// process-wide, so none of them may run at the same time as another.
/// </summary>
[CollectionDefinition(SyncImportsCollection.Name, DisableParallelization = true)]
public sealed class SyncImportsCollection
{
    public const string Name = "SyncImports";
}

[Collection(SyncImportsCollection.Name)]
public sealed unsafe class GCEventTests
{
    // WAIT_OBJECT_0, WAIT_TIMEOUT, WAIT_FAILED and INFINITE of gcenv.base.h.
    private const uint WAIT_OBJECT_0 = 0;
    private const uint WAIT_TIMEOUT = 258;
    private const uint WAIT_FAILED = 0xFFFFFFFF;
    private const uint INFINITE = 0xFFFFFFFF;

    /// <summary>How long a test waits for a thread it expects to finish promptly.</summary>
    private const int JoinTimeoutMs = 30000;

    private static GCEvent* AllocEvent()
    {
        return (GCEvent*)NativeMemory.AllocZeroed((nuint)sizeof(GCEvent));
    }

    private static void FreeEvent(GCEvent* @event)
    {
        @event->CloseEvent();
        NativeMemory.Free(@event);
    }

    [Fact]
    public void DefaultEventIsNotValid()
    {
        GCEvent @event = default;
        Assert.False(@event.IsValid());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateManualEventStartsInTheRequestedState(bool initialState)
    {
        SyncImports.ResetRecording();
        GCEvent* @event = AllocEvent();
        try
        {
            Assert.True(@event->CreateManualEventNoThrow(initialState));
            Assert.True(@event->IsValid());

            Assert.Equal(initialState ? WAIT_OBJECT_0 : WAIT_TIMEOUT, @event->Wait(0, false));
        }
        finally
        {
            FreeEvent(@event);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateAutoEventStartsInTheRequestedState(bool initialState)
    {
        SyncImports.ResetRecording();
        GCEvent* @event = AllocEvent();
        try
        {
            Assert.True(@event->CreateAutoEventNoThrow(initialState));
            Assert.True(@event->IsValid());

            Assert.Equal(initialState ? WAIT_OBJECT_0 : WAIT_TIMEOUT, @event->Wait(0, false));

            // Whether or not it started signalled, the state is clear now: an auto-reset event
            // releases one waiter and takes the signal back.
            Assert.Equal(WAIT_TIMEOUT, @event->Wait(0, false));
        }
        finally
        {
            FreeEvent(@event);
        }
    }

    /// <summary>
    /// A manual-reset event stays signalled until it is reset, and satisfies any number of
    /// waits in between.
    /// </summary>
    [Fact]
    public void ManualEventStaysSignalledUntilReset()
    {
        SyncImports.ResetRecording();
        GCEvent* @event = AllocEvent();
        try
        {
            Assert.True(@event->CreateOSManualEventNoThrow(false));

            Assert.Equal(WAIT_TIMEOUT, @event->Wait(0, false));

            @event->Set();
            Assert.Equal(WAIT_OBJECT_0, @event->Wait(0, false));
            Assert.Equal(WAIT_OBJECT_0, @event->Wait(0, false));
            Assert.Equal(WAIT_OBJECT_0, @event->Wait(INFINITE, false));

            @event->Reset();
            Assert.Equal(WAIT_TIMEOUT, @event->Wait(0, false));

            // Setting an already-set event is idempotent, and so is resetting a clear one.
            @event->Set();
            @event->Set();
            Assert.Equal(WAIT_OBJECT_0, @event->Wait(0, false));
            @event->Reset();
            @event->Reset();
            Assert.Equal(WAIT_TIMEOUT, @event->Wait(0, false));
        }
        finally
        {
            FreeEvent(@event);
        }
    }

    /// <summary>
    /// An auto-reset event satisfies exactly one wait per <c>Set</c>, and a <c>Reset</c> takes
    /// an unconsumed signal away.
    /// </summary>
    [Fact]
    public void AutoEventSatisfiesOneWaitPerSet()
    {
        SyncImports.ResetRecording();
        GCEvent* @event = AllocEvent();
        try
        {
            Assert.True(@event->CreateOSAutoEventNoThrow(false));

            @event->Set();
            Assert.Equal(WAIT_OBJECT_0, @event->Wait(0, false));
            Assert.Equal(WAIT_TIMEOUT, @event->Wait(0, false));

            // Two sets with no wait in between are one signal, not two: the state is a flag.
            @event->Set();
            @event->Set();
            Assert.Equal(WAIT_OBJECT_0, @event->Wait(0, false));
            Assert.Equal(WAIT_TIMEOUT, @event->Wait(0, false));

            // Reset discards a signal nobody has taken yet.
            @event->Set();
            @event->Reset();
            Assert.Equal(WAIT_TIMEOUT, @event->Wait(0, false));
        }
        finally
        {
            FreeEvent(@event);
        }
    }

    /// <summary>
    /// A timed wait on an event that is never signalled reports a timeout, and takes about as
    /// long as it was asked to.
    /// </summary>
    [Fact]
    public void TimedWaitTimesOutAfterTheRequestedInterval()
    {
        SyncImports.ResetRecording();
        GCEvent* @event = AllocEvent();
        try
        {
            Assert.True(@event->CreateOSManualEventNoThrow(false));

            long start = Stopwatch.GetTimestamp();
            Assert.Equal(WAIT_TIMEOUT, @event->Wait(250, false));
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

            // A deadline computed from the wrong clock, or one that forgot to add the timeout,
            // would come back immediately; one that lost the nanosecond carry would wait far
            // too long. The upper bound is generous because a loaded machine may schedule the
            // wakeup late.
            Assert.InRange(elapsed.TotalMilliseconds, 200, 30000);
        }
        finally
        {
            FreeEvent(@event);
        }
    }

    /// <summary>
    /// A blocking wait is released by a <c>Set</c> from another thread, both when the wait is
    /// unbounded and when it has a deadline it does not reach.
    /// </summary>
    [Theory]
    [InlineData(INFINITE)]
    [InlineData(30000u)]
    public void BlockingWaitIsReleasedBySetFromAnotherThread(uint timeout)
    {
        SyncImports.ResetRecording();
        GCEvent* @event = AllocEvent();
        try
        {
            Assert.True(@event->CreateOSAutoEventNoThrow(false));

            nint eventAddress = (nint)@event;
            uint result = WAIT_FAILED;
            var waiterReady = new ManualResetEventSlim(false);
            var waiter = new Thread(() =>
            {
                waiterReady.Set();
                result = ((GCEvent*)eventAddress)->Wait(timeout, false);
            })
            {
                IsBackground = true,
            };

            waiter.Start();
            waiterReady.Wait();
            Thread.Sleep(50);
            @event->Set();

            Assert.True(waiter.Join(JoinTimeoutMs));
            Assert.Equal(WAIT_OBJECT_0, result);
        }
        finally
        {
            FreeEvent(@event);
        }
    }

    /// <summary>
    /// A manual-reset event releases every waiter with a single <c>Set</c>; an auto-reset event
    /// releases one waiter per <c>Set</c>. This is the property the collector's rendezvous
    /// depends on, and the one the <c>m_manualReset</c> branch at the end of
    /// <c>GCEvent::Impl::Wait</c> exists for.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetReleasesAllWaitersOnlyForManualEvents(bool manualReset)
    {
        const int WaiterCount = 4;

        SyncImports.ResetRecording();
        GCEvent* @event = AllocEvent();
        try
        {
            Assert.True(manualReset
                ? @event->CreateOSManualEventNoThrow(false)
                : @event->CreateOSAutoEventNoThrow(false));

            nint eventAddress = (nint)@event;
            int released = 0;
            var started = new CountdownEvent(WaiterCount);
            var waiters = new Thread[WaiterCount];
            for (int i = 0; i < WaiterCount; i++)
            {
                waiters[i] = new Thread(() =>
                {
                    started.Signal();
                    if (((GCEvent*)eventAddress)->Wait(INFINITE, false) == WAIT_OBJECT_0)
                    {
                        Interlocked.Increment(ref released);
                    }
                })
                {
                    IsBackground = true,
                };
                waiters[i].Start();
            }

            Assert.True(started.Wait(JoinTimeoutMs));
            Thread.Sleep(100);

            @event->Set();

            if (manualReset)
            {
                foreach (Thread waiter in waiters)
                {
                    Assert.True(waiter.Join(JoinTimeoutMs));
                }

                Assert.Equal(WaiterCount, Volatile.Read(ref released));
            }
            else
            {
                // One set, one waiter. The others are still blocked, and stay blocked: an
                // auto-reset event that woke a second thread here would be losing the "only one
                // waiter gets released" invariant.
                SpinWaitUntil(() => Volatile.Read(ref released) >= 1);
                Thread.Sleep(100);
                Assert.Equal(1, Volatile.Read(ref released));

                for (int i = 1; i < WaiterCount; i++)
                {
                    @event->Set();
                    SpinWaitUntil(() => Volatile.Read(ref released) >= i + 1);
                }

                foreach (Thread waiter in waiters)
                {
                    Assert.True(waiter.Join(JoinTimeoutMs));
                }

                Assert.Equal(WaiterCount, Volatile.Read(ref released));
            }
        }
        finally
        {
            FreeEvent(@event);
        }
    }

    /// <summary>
    /// A ping-pong over two auto-reset events. Every iteration is a signal that must not be
    /// lost and must not be seen twice, so a missing broadcast, a state written outside the
    /// mutex, or a wait that rechecks the predicate incorrectly deadlocks or over-counts here.
    /// </summary>
    [Fact]
    public void AutoEventPingPongLosesNoSignals()
    {
        const int Iterations = 2000;

        SyncImports.ResetRecording();
        GCEvent* request = AllocEvent();
        GCEvent* response = AllocEvent();
        try
        {
            Assert.True(request->CreateOSAutoEventNoThrow(false));
            Assert.True(response->CreateOSAutoEventNoThrow(false));

            nint requestAddress = (nint)request;
            nint responseAddress = (nint)response;
            int served = 0;
            uint workerStatus = WAIT_OBJECT_0;
            var worker = new Thread(() =>
            {
                for (int i = 0; i < Iterations; i++)
                {
                    uint status = ((GCEvent*)requestAddress)->Wait(INFINITE, false);
                    if (status != WAIT_OBJECT_0)
                    {
                        workerStatus = status;
                        return;
                    }

                    served++;
                    ((GCEvent*)responseAddress)->Set();
                }
            })
            {
                IsBackground = true,
            };

            worker.Start();

            for (int i = 0; i < Iterations; i++)
            {
                request->Set();
                Assert.Equal(WAIT_OBJECT_0, response->Wait(INFINITE, false));
            }

            Assert.True(worker.Join(JoinTimeoutMs));
            Assert.Equal(WAIT_OBJECT_0, workerStatus);
            Assert.Equal(Iterations, served);

            // Both events end clear: every signal was consumed by exactly one wait.
            Assert.Equal(WAIT_TIMEOUT, request->Wait(0, false));
            Assert.Equal(WAIT_TIMEOUT, response->Wait(0, false));
        }
        finally
        {
            FreeEvent(request);
            FreeEvent(response);
        }
    }

    /// <summary>
    /// Sets and resets racing against polls from other threads. Nothing may report a failed
    /// wait, and the event must be usable afterwards -- the point being that every access to
    /// the state goes through the mutex.
    /// </summary>
    [Fact]
    public void SetAndResetRaceWithWaiters()
    {
        const int Iterations = 20000;
        const int PollerCount = 3;

        SyncImports.ResetRecording();
        GCEvent* @event = AllocEvent();
        try
        {
            Assert.True(@event->CreateOSManualEventNoThrow(false));

            nint eventAddress = (nint)@event;
            bool stop = false;
            int failures = 0;
            int polls = 0;
            var pollers = new Thread[PollerCount];
            for (int i = 0; i < PollerCount; i++)
            {
                pollers[i] = new Thread(() =>
                {
                    while (!Volatile.Read(ref stop))
                    {
                        uint status = ((GCEvent*)eventAddress)->Wait(0, false);
                        Interlocked.Increment(ref polls);
                        if (status is not WAIT_OBJECT_0 and not WAIT_TIMEOUT)
                        {
                            Interlocked.Increment(ref failures);
                        }
                    }
                })
                {
                    IsBackground = true,
                };
                pollers[i].Start();
            }

            for (int i = 0; i < Iterations; i++)
            {
                @event->Set();
                @event->Reset();
            }

            Volatile.Write(ref stop, true);
            foreach (Thread poller in pollers)
            {
                Assert.True(poller.Join(JoinTimeoutMs));
            }

            Assert.Equal(0, Volatile.Read(ref failures));
            Assert.True(Volatile.Read(ref polls) > 0, "the pollers did not run");

            @event->Set();
            Assert.Equal(WAIT_OBJECT_0, @event->Wait(0, false));
        }
        finally
        {
            FreeEvent(@event);
        }
    }

    /// <summary>
    /// <c>CloseEvent</c> releases the operating system object but, exactly as in the C++, does
    /// not free the Impl and does not clear the pimpl pointer, so <c>IsValid</c> keeps
    /// reporting true. The GC never reuses a closed event; this pins the behavior rather than
    /// endorsing it.
    /// </summary>
    [Fact]
    public void CloseEventReleasesTheOSObjectAndLeavesThePimplBehind()
    {
        GCEvent* @event = AllocEvent();
        try
        {
            Assert.True(@event->CreateOSManualEventNoThrow(false));

            SyncImports.ResetRecording();
            @event->CloseEvent();

            Assert.True(@event->IsValid());
            Assert.Equal(0, SyncImports.FreeCount);
#if TARGET_WINDOWS
            Assert.Equal(1, SyncImports.CloseHandleCount);
#else
            Assert.Equal(1, SyncImports.MutexDestroyCount);
            Assert.Equal(1, SyncImports.CondDestroyCount);
#endif
        }
        finally
        {
            NativeMemory.Free(@event);
        }
    }

    /// <summary>
    /// Creation fails, without leaking the storage, when the nothrow allocation of the Impl
    /// fails -- the <c>if (!event) return false</c> of the C++.
    /// </summary>
    [Fact]
    public void CreateFailsWhenTheImplCannotBeAllocated()
    {
        SyncImports.ResetRecording();
        GCEvent* @event = AllocEvent();
        try
        {
            SyncImports.FailNextAlloc = true;
            Assert.False(@event->CreateOSManualEventNoThrow(false));
            Assert.False(@event->IsValid());
            Assert.Equal(1, SyncImports.AllocCount);
            Assert.Equal(0, SyncImports.FreeCount);
        }
        finally
        {
            SyncImports.FailNextAlloc = false;
            NativeMemory.Free(@event);
        }
    }

#if !TARGET_WINDOWS

#if !TARGET_APPLE
    // The clock the condition variable is created with and the deadline is read from.
    // CLOCK_MONOTONIC of <time.h>.
#if TARGET_FREEBSD
    private const int CLOCK_MONOTONIC = 4;
#elif TARGET_OPENBSD
    private const int CLOCK_MONOTONIC = 3;
#else
    private const int CLOCK_MONOTONIC = 1;
#endif

    /// <summary>
    /// The condition variable is created against the monotonic clock and the deadline of a
    /// timed wait is read from the same one. A deadline taken from the wall clock would move
    /// when the system time is adjusted.
    /// </summary>
    [Fact]
    public void TimedWaitUsesAMonotonicDeadline()
    {
        SyncImports.ResetRecording();
        GCEvent* @event = AllocEvent();
        try
        {
            Assert.True(@event->CreateOSManualEventNoThrow(false));
            Assert.Equal(CLOCK_MONOTONIC, SyncImports.LastCondAttrClock);

            Assert.Equal(WAIT_TIMEOUT, @event->Wait(100, false));

            Assert.Equal(CLOCK_MONOTONIC, SyncImports.LastClockGetTimeClock);
            Assert.Equal(1, SyncImports.CondTimedWaitCount);

            timespec shortDeadline = SyncImports.LastTimedWaitDeadline;
            Assert.InRange((long)shortDeadline.tv_nsec, 0L, 999999999L);
            Assert.True(shortDeadline.tv_sec > 0);

            Assert.Equal(WAIT_TIMEOUT, @event->Wait(1600, false));
            timespec longDeadline = SyncImports.LastTimedWaitDeadline;
            Assert.InRange((long)longDeadline.tv_nsec, 0L, 999999999L);

            // The second deadline is 1.6 seconds out from a moment 100ms later than the first,
            // so the two are about 1.5 seconds apart. A carry that was dropped or applied twice
            // would land outside this range. The difference is computed in nanoseconds so that
            // a normalization error in either field shows up.
            double differenceMs =
                ((longDeadline.tv_sec - shortDeadline.tv_sec) * 1000.0)
                + ((longDeadline.tv_nsec - shortDeadline.tv_nsec) / 1000000.0);
            Assert.InRange(differenceMs, 1400, 5000);
        }
        finally
        {
            FreeEvent(@event);
        }
    }
#endif // !TARGET_APPLE

    /// <summary>
    /// Creating an event initializes the mutex and the condition variable, and closing it
    /// destroys exactly those two.
    /// </summary>
    [Fact]
    public void CreateInitializesTheMutexAndConditionVariable()
    {
        SyncImports.ResetRecording();
        GCEvent* @event = AllocEvent();
        try
        {
            Assert.True(@event->CreateOSAutoEventNoThrow(false));

            Assert.Equal(1, SyncImports.MutexInitCount);
            Assert.Equal(1, SyncImports.CondInitCount);
            Assert.Equal(0, SyncImports.MutexDestroyCount);
            Assert.Equal(0, SyncImports.CondDestroyCount);

            // The whole Impl comes from a single nothrow allocation.
            Assert.Equal(1, SyncImports.AllocCount);
        }
        finally
        {
            FreeEvent(@event);
        }
    }

    /// <summary>
    /// Every state transition happens under the mutex: <c>Set</c> takes it, publishes the
    /// state, broadcasts and releases it; <c>Reset</c> takes it and does not broadcast.
    /// </summary>
    [Fact]
    public void SetBroadcastsUnderTheMutexAndResetDoesNot()
    {
        GCEvent* @event = AllocEvent();
        try
        {
            Assert.True(@event->CreateOSManualEventNoThrow(false));

            SyncImports.ResetRecording();
            @event->Set();
            Assert.Equal(1, SyncImports.MutexLockCount);
            Assert.Equal(1, SyncImports.MutexUnlockCount);
            Assert.Equal(1, SyncImports.CondBroadcastCount);

            SyncImports.ResetRecording();
            @event->Reset();
            Assert.Equal(1, SyncImports.MutexLockCount);
            Assert.Equal(1, SyncImports.MutexUnlockCount);
            Assert.Equal(0, SyncImports.CondBroadcastCount);

            // A wait that is satisfied immediately takes the mutex, finds the predicate false,
            // and never touches the condition variable.
            @event->Set();
            SyncImports.ResetRecording();
            Assert.Equal(WAIT_OBJECT_0, @event->Wait(0, false));
            Assert.Equal(1, SyncImports.MutexLockCount);
            Assert.Equal(1, SyncImports.MutexUnlockCount);
            Assert.Equal(0, SyncImports.CondWaitCount);
            Assert.Equal(0, SyncImports.CondTimedWaitCount);
        }
        finally
        {
            FreeEvent(@event);
        }
    }

#if !DEBUG
    // The C++ asserts on each of these paths, and so does the port. They can only be driven in
    // a build where the assert is compiled out, which is what the C++ release build does too.

    /// <summary>
    /// A failed <c>pthread_mutex_init</c> fails the creation and frees the Impl.
    /// </summary>
    [Fact]
    public void CreateFailsWhenTheMutexCannotBeInitialized()
    {
        SyncImports.ResetRecording();
        GCEvent* @event = AllocEvent();
        try
        {
            SyncImports.FailNextMutexInit = 22; // EINVAL
            Assert.False(@event->CreateOSManualEventNoThrow(false));
            Assert.False(@event->IsValid());

            Assert.Equal(0, SyncImports.CondInitCount);
            Assert.Equal(1, SyncImports.AllocCount);
            Assert.Equal(1, SyncImports.FreeCount);
        }
        finally
        {
            SyncImports.FailNextMutexInit = 0;
            NativeMemory.Free(@event);
        }
    }

    /// <summary>
    /// A failed <c>pthread_cond_init</c> fails the creation, destroys the mutex that was
    /// already initialized, and frees the Impl.
    /// </summary>
    [Fact]
    public void CreateFailsAndUnwindsTheMutexWhenTheConditionCannotBeInitialized()
    {
        SyncImports.ResetRecording();
        GCEvent* @event = AllocEvent();
        try
        {
            SyncImports.FailNextCondInit = 22; // EINVAL
            Assert.False(@event->CreateOSManualEventNoThrow(false));
            Assert.False(@event->IsValid());

            Assert.Equal(1, SyncImports.MutexInitCount);
            Assert.Equal(1, SyncImports.MutexDestroyCount);
            Assert.Equal(0, SyncImports.CondDestroyCount);
            Assert.Equal(1, SyncImports.FreeCount);
        }
        finally
        {
            SyncImports.FailNextCondInit = 0;
            NativeMemory.Free(@event);
        }
    }
#endif // !DEBUG

#else // TARGET_WINDOWS

    /// <summary>
    /// The manual and auto flavors differ only in the <c>bManualReset</c> argument of
    /// <c>CreateEvent</c>, and the initial state is passed straight through.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void CreateEventPassesTheResetModeAndInitialState(bool manualReset, bool initialState)
    {
        SyncImports.ResetRecording();
        GCEvent* @event = AllocEvent();
        try
        {
            Assert.True(manualReset
                ? @event->CreateOSManualEventNoThrow(initialState)
                : @event->CreateOSAutoEventNoThrow(initialState));

            Assert.Equal(1, SyncImports.CreateEventCount);
            Assert.Equal(manualReset ? 1 : 0, SyncImports.LastCreateEvent.bManualReset);
            Assert.Equal(initialState ? 1 : 0, SyncImports.LastCreateEvent.bInitialState);
            Assert.Equal(1, SyncImports.AllocCount);
        }
        finally
        {
            FreeEvent(@event);
        }
    }

    /// <summary>
    /// The timeout reaches <c>WaitForSingleObject</c> unchanged, including
    /// <c>INFINITE</c> -- the C++ passes the GC's value through, and the two agree because
    /// gcenv.base.h defines INFINITE as the Win32 one.
    /// </summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(INFINITE)]
    public void WaitPassesTheTimeoutThrough(uint timeout)
    {
        SyncImports.ResetRecording();
        GCEvent* @event = AllocEvent();
        try
        {
            Assert.True(@event->CreateOSManualEventNoThrow(true));
            Assert.Equal(WAIT_OBJECT_0, @event->Wait(timeout, false));
            Assert.Equal(timeout, SyncImports.LastWait.dwMilliseconds);
        }
        finally
        {
            FreeEvent(@event);
        }
    }

    /// <summary>
    /// Set and Reset are one Win32 call each.
    /// </summary>
    [Fact]
    public void SetAndResetForwardToWin32()
    {
        GCEvent* @event = AllocEvent();
        try
        {
            Assert.True(@event->CreateOSManualEventNoThrow(false));

            SyncImports.ResetRecording();
            @event->Set();
            Assert.Equal(1, SyncImports.SetEventCount);
            Assert.Equal(0, SyncImports.ResetEventCount);

            @event->Reset();
            Assert.Equal(1, SyncImports.SetEventCount);
            Assert.Equal(1, SyncImports.ResetEventCount);
        }
        finally
        {
            FreeEvent(@event);
        }
    }

#endif // TARGET_WINDOWS

    private static void SpinWaitUntil(Func<bool> condition)
    {
        long start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            Assert.True(Stopwatch.GetElapsedTime(start).TotalMilliseconds < JoinTimeoutMs, "condition was never reached");
            Thread.Sleep(1);
        }
    }
}
