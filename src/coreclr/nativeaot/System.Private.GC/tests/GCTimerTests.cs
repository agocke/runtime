// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Behavior tests for the timer port of GCToOSInterface -- the translation of
// QueryPerformanceCounter, QueryPerformanceFrequency and GetLowPrecisionTimeStamp of
// gc/unix/gcenv.unix.cpp and gc/windows/gcenv.windows.cpp.
//
// The ported bodies are the code under test. Only the entry points underneath them are
// substituted: the three src/native/minipal/time.h functions on Unix, and QueryPerformanceCounter,
// QueryPerformanceFrequency and QueryUnbiasedInterruptTime on Windows.
//
// What there is to check is small and exact, because the C++ is small and exact. On Unix each
// method is a single call, so what a test can pin is that it is that call, once, and that the
// value comes back unchanged -- including the values a signed-to-unsigned cast has to survive.
// On Windows there are three things: that the value the OS wrote is what comes back, that the
// low precision stamp is the unbiased 100ns count divided by 10000, and that a failed call is
// not turned into a return value of its own. The last of those can only run where the assert is
// compiled out, exactly as with the event and lock ports.
//
// The expected constants are written out here rather than read from the port, so that a wrong
// constant fails a test instead of being confirmed by it.

using Xunit;

namespace Internal.Runtime.GarbageCollection;

[Collection(SyncImportsCollection.Name)]
public sealed unsafe class GCTimerTests
{
    public GCTimerTests() => GCToOSInterface.ResetTimerRecording();

#if !TARGET_WINDOWS

    /// <summary>
    /// tccSecondsToNanoSeconds of src/native/minipal/time.c, which is what
    /// minipal_hires_tick_frequency returns on every Unix platform.
    /// </summary>
    private const long SecondsToNanoSeconds = 1000000000;

    [Fact]
    public void PerformanceCounterIsOneCallToTheHiresTicks()
    {
        GCToOSInterface.HiresTicksInject = true;
        GCToOSInterface.HiresTicksValue = 1234567890123;

        Assert.Equal(1234567890123, GCToOSInterface.QueryPerformanceCounter());
        Assert.Equal(1, GCToOSInterface.HiresTicksCalls);

        // Nothing else is consulted: the C++ body is that one call.
        Assert.Equal(0, GCToOSInterface.HiresTickFrequencyCalls);
        Assert.Equal(0, GCToOSInterface.LowresTicksCalls);
    }

    [Theory]
    // The counter is an int64_t on both sides, so every value it can hold survives unchanged,
    // including the ones a narrowing or an unsigned reinterpretation would damage.
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(0x0000000100000000L)]
    public void PerformanceCounterForwardsTheValueUnchanged(long ticks)
    {
        GCToOSInterface.HiresTicksInject = true;
        GCToOSInterface.HiresTicksValue = ticks;

        Assert.Equal(ticks, GCToOSInterface.QueryPerformanceCounter());
    }

    [Fact]
    public void PerformanceCounterIsMonotonicWithoutInjection()
    {
        long first = GCToOSInterface.QueryPerformanceCounter();
        long second = GCToOSInterface.QueryPerformanceCounter();

        Assert.True(second >= first);
        Assert.Equal(2, GCToOSInterface.HiresTicksCalls);
    }

    [Fact]
    public void PerformanceFrequencyIsTheNanosecondFrequency()
    {
        // Not injected: this is the value src/native/minipal/time.c returns on Unix, because
        // both clocks it can read count nanoseconds.
        Assert.Equal(SecondsToNanoSeconds, GCToOSInterface.QueryPerformanceFrequency());
        Assert.Equal(1, GCToOSInterface.HiresTickFrequencyCalls);
        Assert.Equal(0, GCToOSInterface.HiresTicksCalls);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(SecondsToNanoSeconds)]
    [InlineData(long.MaxValue)]
    public void PerformanceFrequencyForwardsTheValueUnchanged(long frequency)
    {
        GCToOSInterface.HiresTickFrequencyInject = true;
        GCToOSInterface.HiresTickFrequencyValue = frequency;

        Assert.Equal(frequency, GCToOSInterface.QueryPerformanceFrequency());
    }

    [Fact]
    public void LowPrecisionTimeStampIsOneCallToTheLowresTicks()
    {
        GCToOSInterface.LowresTicksInject = true;
        GCToOSInterface.LowresTicksValue = 987654321;

        Assert.Equal(987654321UL, GCToOSInterface.GetLowPrecisionTimeStamp());
        Assert.Equal(1, GCToOSInterface.LowresTicksCalls);

        // The C++ scales nothing here: minipal_lowres_ticks already counts milliseconds.
        Assert.Equal(0, GCToOSInterface.HiresTicksCalls);
        Assert.Equal(0, GCToOSInterface.HiresTickFrequencyCalls);
    }

    [Theory]
    // The C++ casts an int64_t to a uint64_t, which is a reinterpretation rather than a
    // conversion, so a negative count comes back as the value with the same bits -- and a count
    // this clock cannot produce still has to round-trip the same way in both languages.
    [InlineData(0L, 0UL)]
    [InlineData(1L, 1UL)]
    [InlineData(long.MaxValue, (ulong)long.MaxValue)]
    [InlineData(-1L, ulong.MaxValue)]
    [InlineData(long.MinValue, 0x8000000000000000UL)]
    public void LowPrecisionTimeStampReinterpretsTheSignedCount(long ticks, ulong expected)
    {
        GCToOSInterface.LowresTicksInject = true;
        GCToOSInterface.LowresTicksValue = ticks;

        Assert.Equal(expected, GCToOSInterface.GetLowPrecisionTimeStamp());
    }

    [Fact]
    public void LowPrecisionTimeStampIsMonotonicWithoutInjection()
    {
        ulong first = GCToOSInterface.GetLowPrecisionTimeStamp();
        ulong second = GCToOSInterface.GetLowPrecisionTimeStamp();

        Assert.True(second >= first);
        Assert.Equal(2, GCToOSInterface.LowresTicksCalls);
    }

    [Fact]
    public void TheTwoClocksAgreeOnTheMillisecond()
    {
        // Both come from the same monotonic clock, so the millisecond stamp and the nanosecond
        // counter scaled by the reported frequency have to land in the same second. This is the
        // one place the three methods are checked against each other rather than in isolation.
        ulong milliseconds = GCToOSInterface.GetLowPrecisionTimeStamp();
        long ticks = GCToOSInterface.QueryPerformanceCounter();
        long frequency = GCToOSInterface.QueryPerformanceFrequency();

        ulong secondsFromCounter = (ulong)(ticks / frequency);
        ulong secondsFromStamp = milliseconds / 1000;

        Assert.InRange(secondsFromCounter, secondsFromStamp == 0 ? 0 : secondsFromStamp - 1, secondsFromStamp + 1);
    }

#else // TARGET_WINDOWS

    /// <summary>The TicksPerMillisecond of the C++ GetLowPrecisionTimeStamp.</summary>
    private const ulong TicksPerMillisecond = 10000;

    [Fact]
    public void PerformanceCounterIsOneCallToQueryPerformanceCounter()
    {
        long ticks = GCToOSInterface.QueryPerformanceCounter();

        Assert.Equal(1, GCToOSInterface.QueryPerformanceCounterCalls);
        Assert.Equal(0, GCToOSInterface.QueryPerformanceFrequencyCalls);
        Assert.Equal(0, GCToOSInterface.QueryUnbiasedInterruptTimeCalls);

        // The QuadPart of the LARGE_INTEGER the kernel filled in, which is a count since boot
        // and so is positive on any machine that has finished booting.
        Assert.True(ticks > 0);
    }

    [Fact]
    public void PerformanceCounterIsMonotonic()
    {
        long first = GCToOSInterface.QueryPerformanceCounter();
        long second = GCToOSInterface.QueryPerformanceCounter();

        Assert.True(second >= first);
        Assert.Equal(2, GCToOSInterface.QueryPerformanceCounterCalls);
    }

    [Theory]
    // The QuadPart of the LARGE_INTEGER is an int64_t on both sides, so every value it can hold
    // comes back unchanged -- including the ones a narrowing or an unsigned reinterpretation
    // would damage.
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(0x0000000100000000L)]
    public void PerformanceCounterForwardsTheQuadPartUnchanged(long ticks)
    {
        GCToOSInterface.PerformanceCounterInject = true;
        GCToOSInterface.PerformanceCounterValue = ticks;

        Assert.Equal(ticks, GCToOSInterface.QueryPerformanceCounter());
        Assert.Equal(1, GCToOSInterface.QueryPerformanceCounterCalls);
    }

    [Fact]
    public void PerformanceFrequencyIsOneCallToQueryPerformanceFrequency()
    {
        long frequency = GCToOSInterface.QueryPerformanceFrequency();

        Assert.Equal(1, GCToOSInterface.QueryPerformanceFrequencyCalls);
        Assert.Equal(0, GCToOSInterface.QueryPerformanceCounterCalls);
        Assert.True(frequency > 0);

        // The frequency is a machine constant, so the second call reports the same thing.
        Assert.Equal(frequency, GCToOSInterface.QueryPerformanceFrequency());
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(10000000L)]
    [InlineData(long.MaxValue)]
    public void PerformanceFrequencyForwardsTheQuadPartUnchanged(long frequency)
    {
        GCToOSInterface.PerformanceFrequencyInject = true;
        GCToOSInterface.PerformanceFrequencyValue = frequency;

        Assert.Equal(frequency, GCToOSInterface.QueryPerformanceFrequency());
        Assert.Equal(1, GCToOSInterface.QueryPerformanceFrequencyCalls);
    }

    [Theory]
    // The C++ divides the unbiased 100ns count by TicksPerMillisecond, which is an integer
    // division and therefore truncates: the boundaries below are the ones that would move if it
    // were rounded or scaled in floating point instead.
    [InlineData(0UL, 0UL)]
    [InlineData(1UL, 0UL)]
    [InlineData(TicksPerMillisecond - 1, 0UL)]
    [InlineData(TicksPerMillisecond, 1UL)]
    [InlineData(TicksPerMillisecond + 1, 1UL)]
    [InlineData(2 * TicksPerMillisecond - 1, 1UL)]
    [InlineData(10 * TicksPerMillisecond, 10UL)]
    [InlineData(ulong.MaxValue, ulong.MaxValue / TicksPerMillisecond)]
    public void LowPrecisionTimeStampDividesTheUnbiasedTime(ulong unbiasedTime, ulong expected)
    {
        GCToOSInterface.UnbiasedInterruptTimeInject = true;
        GCToOSInterface.UnbiasedInterruptTimeValue = unbiasedTime;

        Assert.Equal(expected, GCToOSInterface.GetLowPrecisionTimeStamp());
        Assert.Equal(1, GCToOSInterface.QueryUnbiasedInterruptTimeCalls);
        Assert.Equal(0, GCToOSInterface.QueryPerformanceCounterCalls);
    }

    [Fact]
    public void LowPrecisionTimeStampIsMonotonicWithoutInjection()
    {
        ulong first = GCToOSInterface.GetLowPrecisionTimeStamp();
        ulong second = GCToOSInterface.GetLowPrecisionTimeStamp();

        Assert.True(second >= first);
        Assert.Equal(2, GCToOSInterface.QueryUnbiasedInterruptTimeCalls);
    }

#if !DEBUG
    // The C++ asserts on each of these paths and then reads the value the failed call left
    // behind rather than returning early. They can only be driven in a build where the assert
    // is compiled out, which is what the C++ release build does too.
    //
    // None of them asserts on the returned value on purpose. The C++ leaves it indeterminate,
    // and the port deliberately declares the output local without an initializer to match; that
    // it comes back as zero here is an artifact of the `.locals init` flag the assembly is
    // compiled with, not a contract, so pinning it would pin the artifact.

    [Fact]
    public void PerformanceCounterStillReturnsWhenTheCallFails()
    {
        GCToOSInterface.QueryPerformanceCounterFails = true;

        // The failure is not turned into a sentinel of its own: the C++ falls through to
        // `return ts.QuadPart` with ts never written, so all this can pin is that the call was
        // made and that nothing threw.
        GCToOSInterface.QueryPerformanceCounter();

        Assert.Equal(1, GCToOSInterface.QueryPerformanceCounterCalls);
    }

    [Fact]
    public void PerformanceFrequencyStillReturnsWhenTheCallFails()
    {
        GCToOSInterface.QueryPerformanceFrequencyFails = true;

        GCToOSInterface.QueryPerformanceFrequency();

        Assert.Equal(1, GCToOSInterface.QueryPerformanceFrequencyCalls);
    }

    [Fact]
    public void LowPrecisionTimeStampStillDividesWhenTheCallFails()
    {
        GCToOSInterface.QueryUnbiasedInterruptTimeFails = true;

        GCToOSInterface.GetLowPrecisionTimeStamp();

        Assert.Equal(1, GCToOSInterface.QueryUnbiasedInterruptTimeCalls);
    }
#endif // !DEBUG

#endif // TARGET_WINDOWS
}
