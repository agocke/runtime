// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Behavior tests for the sleep and yield port of GCToOSInterface -- the translation of
// GCToOSInterface::Sleep and GCToOSInterface::YieldThread of gc/unix/gcenv.unix.cpp and
// gc/windows/gcenv.windows.cpp.
//
// The ported bodies are the code under test. Only the libc and Win32 declarations underneath
// them are substituted, by GCToOSInterface.Imports.Unix.TestHost.cs and
// GCToOSInterface.Imports.Windows.TestHost.cs, which forward each call to the real kernel and
// record its arguments -- and, for nanosleep, can report EINTR with an interval left over, which
// is the only way to drive the retry loop without waiting for a signal.
//
// Almost every assertion here is on the arguments the port passes to the operating system and on
// how many times it calls it, which is deterministic. The two that involve real time only assert
// that a sleep did not return early, which is the guarantee nanosleep and SleepEx give; no test
// asserts an upper bound on how long anything took.
//
// The expected values are written out here rather than read from the constants of the port, so
// that a wrong constant fails a test instead of being confirmed by it.

using System;
using System.Diagnostics;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

public sealed unsafe class GCSleepYieldTests
{
    /// <summary>
    /// A sleep long enough that the clock cannot fail to notice it, and short enough that a test
    /// run does not care. Only its lower bound is ever asserted.
    /// </summary>
    private const uint ElapsingSleepMSec = 30;

#if !TARGET_WINDOWS

    /// <summary>
    /// The C++ builds the interval out of <c>tccSecondsToMilliSeconds</c> and
    /// <c>tccMilliSecondsToNanoSeconds</c> of gc/unix/globals.h; this is the second of those two
    /// values, written out here so that a wrong constant in the port fails a test.
    /// </summary>
    private const long NanosecondsPerMillisecond = 1000000;

    private static timespec TimeSpec(long seconds, long nanoseconds)
    {
        timespec value = default;
        value.tv_sec = (nint)seconds;
        value.tv_nsec = (nint)nanoseconds;
        return value;
    }

    private static void AssertTimeSpec(long expectedSeconds, long expectedNanoseconds, timespec actual)
    {
        Assert.Equal(expectedSeconds, (long)actual.tv_sec);
        Assert.Equal(expectedNanoseconds, (long)actual.tv_nsec);
    }

    /// <summary>
    /// The C++ returns before touching the clock at all when asked for zero milliseconds. A
    /// nanosleep of a zero interval is not the same thing: it is a call into the kernel, and on
    /// some systems a yield.
    /// </summary>
    [Fact]
    public void SleepOfZeroDoesNotCallNanosleep()
    {
        GCToOSInterface.ResetSleepYieldRecording();

        GCToOSInterface.Sleep(0);

        Assert.Equal(0, GCToOSInterface.NanosleepCount);
    }

    /// <summary>
    /// The requested interval is the millisecond count split into whole seconds and the
    /// nanosecond remainder, which is the arithmetic of the C++ line for line. The cases cover
    /// the sub-second remainder, the exact second, the carry over one second, and a count large
    /// enough that the nanosecond product would overflow a 32-bit computation had the split not
    /// happened first.
    /// </summary>
    [Theory]
    [InlineData(1u, 0L, 1L * NanosecondsPerMillisecond)]
    [InlineData(5u, 0L, 5L * NanosecondsPerMillisecond)]
    [InlineData(999u, 0L, 999L * NanosecondsPerMillisecond)]
    [InlineData(1000u, 1L, 0L)]
    [InlineData(1001u, 1L, 1L * NanosecondsPerMillisecond)]
    [InlineData(1500u, 1L, 500L * NanosecondsPerMillisecond)]
    [InlineData(3600123u, 3600L, 123L * NanosecondsPerMillisecond)]
    [InlineData(uint.MaxValue, 4294967L, 295L * NanosecondsPerMillisecond)]
    public void SleepSplitsTheMillisecondsIntoSecondsAndNanoseconds(uint sleepMSec, long expectedSeconds, long expectedNanoseconds)
    {
        GCToOSInterface.ResetSleepYieldRecording();

        // Fail the call rather than perform it, so that a test of the arithmetic does not
        // actually sleep for an hour. An errno other than EINTR ends the loop after one call,
        // which is the C++ behavior for any other failure.
        GCToOSInterface.NanosleepFailErrno = 22; // EINVAL

        GCToOSInterface.Sleep(sleepMSec);

        Assert.Equal(1, GCToOSInterface.NanosleepCount);
        AssertTimeSpec(expectedSeconds, expectedNanoseconds, GCToOSInterface.NanosleepCalls[0].requested);
        Assert.True(expectedNanoseconds < 1000L * NanosecondsPerMillisecond, "The remainder must be less than a second.");
    }

    /// <summary>
    /// An interrupted sleep is retried with what nanosleep says is left of the interval, not
    /// with the interval it was asked for -- so the total sleep is the requested one however
    /// many signals arrive.
    /// </summary>
    [Fact]
    public void SleepRetriesWithTheRemainingIntervalWhenInterrupted()
    {
        GCToOSInterface.ResetSleepYieldRecording();
        GCToOSInterface.NanosleepInterrupts = 1;
        GCToOSInterface.NanosleepInterruptRemaining = TimeSpec(0, 250 * NanosecondsPerMillisecond);

        // The second call reports success without entering the kernel, so a real signal cannot
        // add an iteration to the loop and the call count below is exact.
        GCToOSInterface.NanosleepSucceedsWithoutSleeping = true;

        GCToOSInterface.Sleep(1500);

        Assert.Equal(2, GCToOSInterface.NanosleepCount);
        AssertTimeSpec(1, 500 * NanosecondsPerMillisecond, GCToOSInterface.NanosleepCalls[0].requested);
        Assert.Equal(-1, GCToOSInterface.NanosleepCalls[0].result);
        Assert.Equal(4, GCToOSInterface.NanosleepCalls[0].errno); // EINTR
        AssertTimeSpec(0, 250 * NanosecondsPerMillisecond, GCToOSInterface.NanosleepCalls[1].requested);
        Assert.Equal(0, GCToOSInterface.NanosleepCalls[1].result);
    }

    /// <summary>
    /// The retry is a loop and not a single second attempt: every interruption is followed by
    /// another sleep of what is left.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void SleepRetriesOncePerInterruption(int interruptions)
    {
        GCToOSInterface.ResetSleepYieldRecording();
        GCToOSInterface.NanosleepInterrupts = interruptions;
        GCToOSInterface.NanosleepInterruptRemaining = TimeSpec(0, NanosecondsPerMillisecond);
        GCToOSInterface.NanosleepSucceedsWithoutSleeping = true;

        GCToOSInterface.Sleep(5);

        Assert.Equal(interruptions + 1, GCToOSInterface.NanosleepCount);
        AssertTimeSpec(0, 5 * NanosecondsPerMillisecond, GCToOSInterface.NanosleepCalls[0].requested);

        for (int i = 1; i <= interruptions; i++)
        {
            AssertTimeSpec(0, NanosecondsPerMillisecond, GCToOSInterface.NanosleepCalls[i].requested);
        }
    }

    /// <summary>
    /// The loop condition is <c>errno == EINTR</c>, so any other failure ends the sleep rather
    /// than spinning on a call that will keep failing.
    /// </summary>
    [Theory]
    [InlineData(22)] // EINVAL
    [InlineData(14)] // EFAULT
    public void SleepDoesNotRetryWhenNanosleepFailsForAnotherReason(int errno)
    {
        GCToOSInterface.ResetSleepYieldRecording();
        GCToOSInterface.NanosleepFailErrno = errno;

        GCToOSInterface.Sleep(10);

        Assert.Equal(1, GCToOSInterface.NanosleepCount);
        Assert.Equal(-1, GCToOSInterface.NanosleepCalls[0].result);
        Assert.Equal(errno, GCToOSInterface.NanosleepCalls[0].errno);
    }

    /// <summary>
    /// The whole point of the method: a finite sleep reaches the kernel and does not come back
    /// early. Only the lower bound is asserted, which is what nanosleep guarantees.
    /// </summary>
    [Fact]
    public void SleepOfANonZeroIntervalElapses()
    {
        GCToOSInterface.ResetSleepYieldRecording();

        long start = Stopwatch.GetTimestamp();
        GCToOSInterface.Sleep(ElapsingSleepMSec);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

        Assert.True(GCToOSInterface.NanosleepCount >= 1, "The sleep must reach nanosleep.");
        AssertTimeSpec(0, ElapsingSleepMSec * NanosecondsPerMillisecond, GCToOSInterface.NanosleepCalls[0].requested);

        // The port sleeps for the interval it was given, so the call cannot return sooner than
        // that. A tolerance is allowed for the granularity of the two different clocks involved,
        // and no upper bound is asserted at all: a loaded machine may return arbitrarily late.
        Assert.True(
            elapsed >= TimeSpan.FromMilliseconds(ElapsingSleepMSec - 2),
            $"Sleep({ElapsingSleepMSec}) returned after {elapsed.TotalMilliseconds} ms.");
    }

    /// <summary>
    /// Yielding is one sched_yield, whatever the caller's spin count is -- the C++ does not name
    /// the parameter on Unix, and the collector passes both a constant zero and an incrementing
    /// counter from its spin loops.
    /// </summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(1024u)]
    [InlineData(uint.MaxValue)]
    public void YieldThreadCallsSchedYieldOnceAndIgnoresTheSwitchCount(uint switchCount)
    {
        GCToOSInterface.ResetSleepYieldRecording();

        GCToOSInterface.YieldThread(switchCount);

        Assert.Equal(1, GCToOSInterface.SchedYieldCount);
        Assert.Equal(0, GCToOSInterface.NanosleepCount);
    }

    /// <summary>
    /// A yield does not accumulate state, so a spin loop's repeated calls are one call each.
    /// </summary>
    [Fact]
    public void YieldThreadInALoopCallsSchedYieldOncePerIteration()
    {
        GCToOSInterface.ResetSleepYieldRecording();

        for (uint i = 0; i < 16; i++)
        {
            GCToOSInterface.YieldThread(i);
        }

        Assert.Equal(16, GCToOSInterface.SchedYieldCount);
    }

#else

    /// <summary>
    /// The C++ guards the call with <c>if (sleepMSec &gt; 0)</c>, so a zero sleep is not a
    /// <c>SleepEx(0, FALSE)</c> -- which is a yield to a thread of equal priority -- but no call
    /// at all.
    /// </summary>
    [Fact]
    public void SleepOfZeroDoesNotCallSleepEx()
    {
        GCToOSInterface.ResetSleepYieldRecording();

        GCToOSInterface.Sleep(0);

        Assert.Equal(0, GCToOSInterface.SleepExCount);
    }

    /// <summary>
    /// Every other interval is forwarded unchanged, and the sleep is never alertable: bAlertable
    /// is the C++ FALSE, so a queued APC does not cut the sleep short.
    /// </summary>
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(15u)]
    public void SleepForwardsTheIntervalAndIsNotAlertable(uint sleepMSec)
    {
        GCToOSInterface.ResetSleepYieldRecording();

        GCToOSInterface.Sleep(sleepMSec);

        Assert.Equal(1, GCToOSInterface.SleepExCount);
        Assert.Equal(sleepMSec, GCToOSInterface.LastSleepEx.dwMilliseconds);
        Assert.Equal(0, GCToOSInterface.LastSleepEx.bAlertable); // FALSE
    }

    /// <summary>
    /// The whole point of the method: a finite sleep reaches the kernel and does not come back
    /// early. Only the lower bound is asserted, and it allows for the timer resolution Windows
    /// rounds a sleep down to.
    /// </summary>
    [Fact]
    public void SleepOfANonZeroIntervalElapses()
    {
        GCToOSInterface.ResetSleepYieldRecording();

        long start = Stopwatch.GetTimestamp();
        GCToOSInterface.Sleep(ElapsingSleepMSec);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

        Assert.Equal(1, GCToOSInterface.SleepExCount);
        Assert.Equal(ElapsingSleepMSec, GCToOSInterface.LastSleepEx.dwMilliseconds);

        // SleepEx rounds to the current timer resolution, which is up to 15.6 ms on a machine
        // where nothing has raised it, so a sleep may legitimately return that much early. No
        // upper bound is asserted at all.
        Assert.True(
            elapsed >= TimeSpan.FromMilliseconds(ElapsingSleepMSec - 16),
            $"Sleep({ElapsingSleepMSec}) returned after {elapsed.TotalMilliseconds} ms.");
    }

    /// <summary>
    /// Yielding is one SwitchToThread, whatever the caller's spin count is -- the C++ discards
    /// the parameter with UNREFERENCED_PARAMETER, and the collector passes both a constant zero
    /// and an incrementing counter from its spin loops.
    /// </summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(1024u)]
    [InlineData(uint.MaxValue)]
    public void YieldThreadCallsSwitchToThreadOnceAndIgnoresTheSwitchCount(uint switchCount)
    {
        GCToOSInterface.ResetSleepYieldRecording();

        GCToOSInterface.YieldThread(switchCount);

        Assert.Equal(1, GCToOSInterface.SwitchToThreadCount);
        Assert.Equal(0, GCToOSInterface.SleepExCount);
    }

    /// <summary>
    /// A yield does not accumulate state, so a spin loop's repeated calls are one call each. The
    /// BOOL SwitchToThread returns says whether there was another thread to run; the port
    /// discards it, so a false does not turn into a second call or an assert.
    /// </summary>
    [Fact]
    public void YieldThreadInALoopCallsSwitchToThreadOncePerIteration()
    {
        GCToOSInterface.ResetSleepYieldRecording();

        for (uint i = 0; i < 16; i++)
        {
            GCToOSInterface.YieldThread(i);
        }

        Assert.Equal(16, GCToOSInterface.SwitchToThreadCount);
    }

#endif
}
