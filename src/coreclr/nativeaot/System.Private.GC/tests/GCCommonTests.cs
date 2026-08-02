// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Behavior tests for GetHighPrecisionTimeStamp of gccommon.cpp. The production body is compiled
// directly into this test assembly; only the already-ported GCToOSInterface timer calls beneath
// it are substituted.

using Xunit;

namespace Internal.Runtime.GarbageCollection;

[Collection(SyncImportsCollection.Name)]
public sealed class GCCommonTests
{
    public GCCommonTests()
    {
        GCToOSInterface.ResetTimerRecording();
        GCCommon.ResetHighPrecisionTimeStamp();
    }

    [Theory]
    [InlineData(1000000L, 1234567L, 1234567UL)]
    [InlineData(10000000L, 12345678L, 1234567UL)]
    [InlineData(3L, 10L, 3333333UL)]
    [InlineData(1000000000L, 0L, 0UL)]
    public void HighPrecisionTimeStampScalesCounterToMicroseconds(long frequency, long counter, ulong expected)
    {
        SetPerformanceFrequency(frequency);
        SetPerformanceCounter(counter);

        Assert.Equal(expected, GCCommon.GetHighPrecisionTimeStamp());
        Assert.Equal(1, GetPerformanceFrequencyCalls());
        Assert.Equal(1, GetPerformanceCounterCalls());
    }

    [Fact]
    public void HighPrecisionTimeStampCachesTheFrequency()
    {
        SetPerformanceFrequency(1000000);
        SetPerformanceCounter(11);

        Assert.Equal(11UL, GCCommon.GetHighPrecisionTimeStamp());

        SetPerformanceFrequency(2000000);
        SetPerformanceCounter(12);

        Assert.Equal(12UL, GCCommon.GetHighPrecisionTimeStamp());
        Assert.Equal(1, GetPerformanceFrequencyCalls());
        Assert.Equal(2, GetPerformanceCounterCalls());
    }

    private static void SetPerformanceFrequency(long frequency)
    {
#if TARGET_WINDOWS
        GCToOSInterface.PerformanceFrequencyInject = true;
        GCToOSInterface.PerformanceFrequencyValue = frequency;
#else
        GCToOSInterface.HiresTickFrequencyInject = true;
        GCToOSInterface.HiresTickFrequencyValue = frequency;
#endif
    }

    private static void SetPerformanceCounter(long counter)
    {
#if TARGET_WINDOWS
        GCToOSInterface.PerformanceCounterInject = true;
        GCToOSInterface.PerformanceCounterValue = counter;
#else
        GCToOSInterface.HiresTicksInject = true;
        GCToOSInterface.HiresTicksValue = counter;
#endif
    }

    private static int GetPerformanceFrequencyCalls()
    {
#if TARGET_WINDOWS
        return GCToOSInterface.QueryPerformanceFrequencyCalls;
#else
        return GCToOSInterface.HiresTickFrequencyCalls;
#endif
    }

    private static int GetPerformanceCounterCalls()
    {
#if TARGET_WINDOWS
        return GCToOSInterface.QueryPerformanceCounterCalls;
#else
        return GCToOSInterface.HiresTicksCalls;
#endif
    }
}
