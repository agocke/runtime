// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Behavior tests for the processor, affinity, NUMA and CPU-group ports of GCToOSInterface -- the
// translation of the corresponding methods of gc/unix/gcenv.unix.cpp and
// gc/windows/gcenv.windows.cpp.
//
// The ported bodies are the code under test. Only what is underneath them is substituted: on
// Unix sched_getcpu / sched_setaffinity / getpid plus narrow shims over native-owned state; on
// Windows the Win32 calls plus narrow shims over Initialize-owned CPU-group and NUMA state.
//
// The things worth pinning are the ones the C++ makes exact: forwarding and widening behavior
// of the identity calls, the compile-time HAVE_SCHED_GETCPU branch, the Windows
// (group << 6) | procIndex packing, the GetTotalProcessorCount cache/source selection, and the
// affinity/NUMA/CPU-group branches of the translated methods.
//
// The expected constants are written out here rather than read from the port, so that a wrong
// constant fails a test instead of being confirmed by it.

using System;
using System.Runtime.InteropServices;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

[Collection(SyncImportsCollection.Name)]
public sealed unsafe class GCProcessorTests
{
    public GCProcessorTests() => GCToOSInterface.ResetProcessorRecording();

    /// <summary>
    /// The capacity of the affinity set the substitutes hand back, in whole bitset entries, so
    /// that the values the theories use are legal on both a 32 and a 64 bit host.
    /// </summary>
    private static nuint BitsPerBitsetEntry => (nuint)sizeof(nuint) * 8;

#if !TARGET_WINDOWS

    [Fact]
    public void CurrentProcessIdIsOneCallToGetpid()
    {
        GCToOSInterface.GetPidInject = true;
        GCToOSInterface.GetPidValue = 4242;

        Assert.Equal(4242u, GCToOSInterface.GetCurrentProcessId());
        Assert.Equal(1, GCToOSInterface.GetPidCalls);
    }

    [Theory]
    // pid_t is a signed 32 bit integer and the C++ returns it through an implicit conversion to
    // uint32_t, so every bit pattern it can hold has to survive.
    [InlineData(0, 0u)]
    [InlineData(1, 1u)]
    [InlineData(int.MaxValue, (uint)int.MaxValue)]
    [InlineData(-1, uint.MaxValue)]
    public void CurrentProcessIdForwardsThePidUnchanged(int pid, uint expected)
    {
        GCToOSInterface.GetPidInject = true;
        GCToOSInterface.GetPidValue = pid;

        Assert.Equal(expected, GCToOSInterface.GetCurrentProcessId());
    }

    [Fact]
    public void CurrentProcessIdWithoutInjectionIsThisProcess()
    {
        Assert.Equal((uint)Environment.ProcessId, GCToOSInterface.GetCurrentProcessId());
    }

    [Theory]
    // The shim returns a size_t and the method a uint64_t, so the widening is what has to be
    // exact -- above all that it is zero extended rather than sign extended.
    [InlineData(0ul)]
    [InlineData(1ul)]
    [InlineData(0x7FFFFFFFul)]
    [InlineData(0x80000000ul)]
    [InlineData(0xFFFFFFFFul)]
    public void CurrentThreadIdForLoggingForwardsTheShimUnchanged(ulong threadId)
    {
        GCToOSInterface.CurrentThreadIdValue = (nuint)threadId;

        Assert.Equal(threadId, GCToOSInterface.GetCurrentThreadIdForLogging());
        Assert.Equal(1, GCToOSInterface.CurrentThreadIdCalls);
    }

    [Fact]
    public void CurrentThreadIdForLoggingCarriesTheWholeWord()
    {
        GCToOSInterface.CurrentThreadIdValue = nuint.MaxValue;

        Assert.Equal((ulong)nuint.MaxValue, GCToOSInterface.GetCurrentThreadIdForLogging());
    }

#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD

    [Fact]
    public void CanGetCurrentProcessorNumberIsTrueWhereSchedGetcpuExists()
    {
        Assert.True(GCToOSInterface.CanGetCurrentProcessorNumber());
    }

    [Fact]
    public void CurrentProcessorNumberIsOneCallToSchedGetcpu()
    {
        GCToOSInterface.SchedGetCpuInject = true;
        GCToOSInterface.SchedGetCpuValue = 3;

        Assert.Equal(3u, GCToOSInterface.GetCurrentProcessorNumber());
        Assert.Equal(1, GCToOSInterface.SchedGetCpuCalls);
    }

    [Theory]
    [InlineData(0, 0u)]
    [InlineData(1, 1u)]
    [InlineData(63, 63u)]
    [InlineData(1023, 1023u)]
    [InlineData(int.MaxValue, (uint)int.MaxValue)]
    public void CurrentProcessorNumberForwardsTheProcessorUnchanged(int processorNumber, uint expected)
    {
        GCToOSInterface.SchedGetCpuInject = true;
        GCToOSInterface.SchedGetCpuValue = processorNumber;

        Assert.Equal(expected, GCToOSInterface.GetCurrentProcessorNumber());
    }

    /// <summary>
    /// Without injection the port reaches the real sched_getcpu. The value cannot be bounded by
    /// Environment.ProcessorCount, because sched_getcpu reports a kernel CPU index rather than a
    /// position in a dense range -- an affinity mask of {18, 19} makes the count 2 and both legal
    /// answers larger than it -- so the thread is pinned to one CPU of its own mask and the port
    /// is asked which one it is.
    /// </summary>
    [Fact]
    public void CurrentProcessorNumberWithoutInjectionIsTheProcessorTheThreadRunsOn()
    {
        // sizeof(cpu_set_t) of <sched.h>; sched_getaffinity fails with EINVAL on a smaller one.
        const int CpuSetSize = 128;

        nuint* original = stackalloc nuint[CpuSetSize / sizeof(nuint)];
        nuint* pinned = stackalloc nuint[CpuSetSize / sizeof(nuint)];
        if (sys_sched_getaffinity(0, CpuSetSize, original) != 0)
        {
            return;
        }

        int cpu = FirstSetCpu(original, CpuSetSize);
        Assert.True(cpu >= 0);

        NativeMemory.Clear(pinned, CpuSetSize);
        int bitsPerWord = sizeof(nuint) * 8;
        pinned[cpu / bitsPerWord] = (nuint)1 << (cpu % bitsPerWord);
        if (sys_sched_setaffinity(0, CpuSetSize, pinned) != 0)
        {
            return;
        }

        try
        {
            Assert.Equal((uint)cpu, GCToOSInterface.GetCurrentProcessorNumber());
            Assert.Equal(1, GCToOSInterface.SchedGetCpuCalls);
        }
        finally
        {
            Assert.Equal(0, sys_sched_setaffinity(0, CpuSetSize, original));
        }
    }

    private static int FirstSetCpu(nuint* mask, int size)
    {
        int bitsPerWord = sizeof(nuint) * 8;
        int wordCount = size / sizeof(nuint);
        for (int i = 0; i < wordCount; i++)
        {
            for (int j = 0; j < bitsPerWord; j++)
            {
                if ((mask[i] & ((nuint)1 << j)) != 0)
                {
                    return (i * bitsPerWord) + j;
                }
            }
        }

        return -1;
    }

    [DllImport("libc", EntryPoint = "sched_getaffinity", SetLastError = true)]
    private static extern int sys_sched_getaffinity(int pid, nuint cpusetsize, nuint* mask);

    [DllImport("libc", EntryPoint = "sched_setaffinity", SetLastError = true)]
    private static extern int sys_sched_setaffinity(int pid, nuint cpusetsize, nuint* mask);

#if !DEBUG
    [Fact]
    public void CurrentProcessorNumberReturnsTheFailureUnchangedWhereAssertsAreCompiledOut()
    {
        // The C++ asserts that sched_getcpu did not fail and returns what it said either way,
        // so a release build returns the -1 widened to uint32_t rather than a value of its own.
        GCToOSInterface.SchedGetCpuInject = true;
        GCToOSInterface.SchedGetCpuValue = -1;

        Assert.Equal(uint.MaxValue, GCToOSInterface.GetCurrentProcessorNumber());
    }
#endif

#else

    [Fact]
    public void CanGetCurrentProcessorNumberIsFalseWhereSchedGetcpuIsMissing()
    {
        Assert.False(GCToOSInterface.CanGetCurrentProcessorNumber());
    }

#if !DEBUG
    [Fact]
    public void CurrentProcessorNumberIsZeroWhereSchedGetcpuIsMissing()
    {
        // The C++ asserts that this is unreachable -- the GC is expected to ask
        // CanGetCurrentProcessorNumber first -- and returns 0 where the assert is compiled out.
        Assert.Equal(0u, GCToOSInterface.GetCurrentProcessorNumber());
    }
#endif

#endif // !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD

    [Theory]
    // g_totalCpuCount is a uint32_t that the C++ only reads, so the whole range forwards.
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(64u)]
    [InlineData(uint.MaxValue)]
    public void TotalProcessorCountIsTheInitializedCount(uint totalCpuCount)
    {
        GCToOSInterface.TotalCpuCountValue = totalCpuCount;

        Assert.Equal(totalCpuCount, GCToOSInterface.GetTotalProcessorCount());
        Assert.Equal(1, GCToOSInterface.TotalCpuCountCalls);
    }

    [Theory]
    [InlineData((ushort)0, (ushort)0)]
    [InlineData((ushort)1, (ushort)2)]
    [InlineData(ushort.MaxValue, 0)]
    public void SetCurrentThreadIdealAffinityIsANoOpThatSucceedsOnUnix(ushort srcProcNo, ushort dstProcNo)
    {
        Assert.True(GCToOSInterface.SetCurrentThreadIdealAffinity(srcProcNo, dstProcNo));
    }

    [Fact]
    public void GetCurrentThreadIdealProcIsUnsupportedOnUnix()
    {
        ushort procNo = 1234;

        Assert.False(GCToOSInterface.GetCurrentThreadIdealProc(&procNo));
        Assert.Equal((ushort)1234, procNo);
    }

#if !TARGET_APPLE && !TARGET_OPENBSD
    [Theory]
    [InlineData((ushort)0, 0u, true)]
    [InlineData((ushort)5, 0u, true)]
    [InlineData((ushort)5, -1, false)]
    public void SetThreadAffinityUsesSchedSetaffinityWithAOneBitMask(ushort procNo, int schedResult, bool expected)
    {
        GCToOSInterface.ConfiguredCpuCountValue = 64;
        GCToOSInterface.SchedSetAffinityInject = true;
        GCToOSInterface.SchedSetAffinityResult = schedResult;

        Assert.Equal(expected, GCToOSInterface.SetThreadAffinity(procNo));
        Assert.Equal(1, GCToOSInterface.SchedSetAffinityCalls);
#if !TARGET_FREEBSD
        // sched_setaffinity is called with pid 0, which is the calling thread.
        Assert.Equal(0, GCToOSInterface.LastSchedSetAffinityPid);
#else
        // The fallback arm affinitizes pthread_self() instead.
        Assert.Equal(1, GCToOSInterface.PthreadSelfCalls);
        Assert.NotEqual((nuint)0, GCToOSInterface.LastSchedSetAffinityThread);
#endif
        Assert.Equal(0, GCToOSInterface.TotalCpuCountCalls);

        nuint bitsPerWord = (nuint)sizeof(nuint) * 8;
        nuint expectedCpuSetSize = ((nuint)64 + bitsPerWord - 1) / bitsPerWord * (nuint)sizeof(nuint);
        Assert.Equal(expectedCpuSetSize, GCToOSInterface.LastSchedSetAffinityCpuSetSize);

        nuint expectedWord = procNo / bitsPerWord;
        nuint expectedBit = (nuint)(procNo & (ushort)(bitsPerWord - 1));
        for (nuint i = 0; i < (nuint)GCToOSInterface.LastSchedSetAffinityMask.Length; i++)
        {
            nuint expectedMask = i == expectedWord ? (nuint)1 << (int)expectedBit : 0;
            Assert.Equal(expectedMask, GCToOSInterface.LastSchedSetAffinityMask[i]);
        }
    }

    [Fact]
    public void SetThreadAffinityIgnoresAProcessorPastTheEndOfTheSet()
    {
        // CPU_SET_S is bounds checked, so a processor number the configured count does not
        // cover leaves the set empty rather than writing past it.
        GCToOSInterface.ConfiguredCpuCountValue = 1;
        GCToOSInterface.SchedSetAffinityInject = true;
        GCToOSInterface.SchedSetAffinityResult = 0;

        nuint bitsPerWord = (nuint)sizeof(nuint) * 8;
        Assert.True(GCToOSInterface.SetThreadAffinity((ushort)bitsPerWord));
        Assert.Equal(1, GCToOSInterface.SchedSetAffinityCalls);
        Assert.Equal((nuint)sizeof(nuint), GCToOSInterface.LastSchedSetAffinityCpuSetSize);

        for (nuint i = 0; i < (nuint)GCToOSInterface.LastSchedSetAffinityMask.Length; i++)
        {
            Assert.Equal((nuint)0, GCToOSInterface.LastSchedSetAffinityMask[i]);
        }
    }
#endif

    [Fact]
    public void BoostThreadPriorityReturnsFalseOnUnix()
    {
        Assert.False(GCToOSInterface.BoostThreadPriority());
    }

    [Fact]
    public void SetGCThreadsAffinitySetUsesTheConfiguredSetOnUnix()
    {
        GCToOSInterface.TotalCpuCountValue = 8;
        GCToOSInterface.SetProcessAffinityCpuCount(8);

        nuint* configBits = stackalloc nuint[1];
        AffinitySet configured = default;
        InitializeAffinitySet(&configured, configBits, 1, 1, 3, 5);

        AffinitySet* result = GCToOSInterface.SetGCThreadsAffinitySet(0b11111111, &configured);

        for (nuint i = 0; i < 8; i++)
        {
            bool shouldContain = (i == 1) || (i == 3) || (i == 5);
            Assert.Equal(shouldContain, result->Contains(i));
        }
    }

    [Fact]
    public void SetGCThreadsAffinitySetIgnoresMaskWhenConfiguredSetIsEmptyOnUnix()
    {
        GCToOSInterface.TotalCpuCountValue = 8;
        GCToOSInterface.SetProcessAffinityCpuCount(8);

        nuint* configBits = stackalloc nuint[1];
        AffinitySet configured = default;
        InitializeAffinitySet(&configured, configBits, 1);

        AffinitySet* result = GCToOSInterface.SetGCThreadsAffinitySet(0b00101101, &configured);
        for (nuint i = 0; i < 8; i++)
        {
            Assert.True(result->Contains(i));
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(-1, true)]
    public void CanEnableGCNumaAwareReflectsTheNativeStateOnUnix(int value, bool expected)
    {
        GCToOSInterface.NumaAvailableValue = value;

        Assert.Equal(expected, GCToOSInterface.CanEnableGCNumaAware());
        Assert.Equal(1, GCToOSInterface.NumaAvailableCalls);
    }

    [Fact]
    public void GetNumaInfoReturnsFalseOnUnix()
    {
        ushort totalNodes = 123;
        uint maxProcsPerNode = 456;

        Assert.False(GCToOSInterface.GetNumaInfo(&totalNodes, &maxProcsPerNode));
        Assert.Equal((ushort)123, totalNodes);
        Assert.Equal(456u, maxProcsPerNode);
    }

    [Fact]
    public void CanEnableGCCPUGroupsIsAlwaysFalseOnUnix()
    {
        Assert.False(GCToOSInterface.CanEnableGCCPUGroups());
    }

    [Fact]
    public void GetCPUGroupInfoReturnsFalseOnUnix()
    {
        ushort totalGroups = 12;
        uint maxProcsPerGroup = 34;

        Assert.False(GCToOSInterface.GetCPUGroupInfo(&totalGroups, &maxProcsPerGroup));
        Assert.Equal((ushort)12, totalGroups);
        Assert.Equal(34u, maxProcsPerGroup);
    }

    [Fact]
    public void GetProcessorForHeapReturnsFalseWhenHeapNumberIsOutOfRangeOnUnix()
    {
        GCToOSInterface.SetProcessAffinityCpuCount(3);

        ushort procNo = 0;
        ushort nodeNo = 0;

        Assert.False(GCToOSInterface.GetProcessorForHeap(3, &procNo, &nodeNo));
    }

#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD && !TARGET_ANDROID
    [Fact]
    public void GetProcessorForHeapUsesNumaLookupWhenNumaIsAvailableOnUnix()
    {
        GCToOSInterface.TotalCpuCountValue = 5;
        GCToOSInterface.SetProcessAffinityCpuCount(5);

        nuint* configBits = stackalloc nuint[1];
        AffinitySet configured = default;
        InitializeAffinitySet(&configured, configBits, 1, 1, 3, 4);
        GCToOSInterface.SetGCThreadsAffinitySet(0, &configured);

        GCToOSInterface.NumaAvailableValue = 1;
        GCToOSInterface.NumaNodeByCpuInject = true;
        GCToOSInterface.NumaNodeByCpuValues[3] = 2;

        ushort procNo = 0;
        ushort nodeNo = 0;
        Assert.True(GCToOSInterface.GetProcessorForHeap(1, &procNo, &nodeNo));
        Assert.Equal((ushort)3, procNo);
        Assert.Equal((ushort)2, nodeNo);
        Assert.Equal(1, GCToOSInterface.NumaNodeByCpuCalls);
    }

    [Fact]
    public void GetProcessorForHeapReturnsUndefinedNodeWhenNumaLookupFailsOnUnix()
    {
        GCToOSInterface.TotalCpuCountValue = 4;
        GCToOSInterface.SetProcessAffinityCpuCount(4);
        GCToOSInterface.NumaAvailableValue = 1;
        GCToOSInterface.NumaNodeByCpuInject = true;
        GCToOSInterface.NumaNodeByCpuDefaultValue = -1;

        ushort procNo = 0;
        ushort nodeNo = 0;
        Assert.True(GCToOSInterface.GetProcessorForHeap(2, &procNo, &nodeNo));
        Assert.Equal((ushort)2, procNo);
        Assert.Equal(GCToOSInterface.NUMA_NODE_UNDEFINED, nodeNo);
    }
#endif

    [Fact]
    public void GetProcessorForHeapReturnsUndefinedNodeWhenNumaIsDisabledOnUnix()
    {
        GCToOSInterface.TotalCpuCountValue = 4;
        GCToOSInterface.SetProcessAffinityCpuCount(4);
        GCToOSInterface.NumaAvailableValue = 0;

        ushort procNo = 0;
        ushort nodeNo = 0;
        Assert.True(GCToOSInterface.GetProcessorForHeap(2, &procNo, &nodeNo));
        Assert.Equal((ushort)2, procNo);
        Assert.Equal(GCToOSInterface.NUMA_NODE_UNDEFINED, nodeNo);
    }

    [Theory]
    [InlineData("5", true, 5ul, 5ul, 1)]
    [InlineData("5-7", true, 5ul, 7ul, 3)]
    [InlineData("x", false, 0ul, 0ul, 0)]
    public void ParseGCHeapAffinitizeRangesEntryMatchesParseIndexOrRangeOnUnix(string text, bool expectedParsed, ulong expectedStart, ulong expectedEnd, int expectedConsumed)
    {
        bool parsed = ParseRangeEntry(text, out nuint start, out nuint end, out int consumed);

        Assert.Equal(expectedParsed, parsed);
        if (parsed)
        {
            Assert.Equal((nuint)expectedStart, start);
            Assert.Equal((nuint)expectedEnd, end);
            Assert.Equal(expectedConsumed, consumed);
        }
    }

#else // TARGET_WINDOWS

    [Fact]
    public void CanGetCurrentProcessorNumberIsAlwaysTrue()
    {
        Assert.True(GCToOSInterface.CanGetCurrentProcessorNumber());
    }

    [Fact]
    public void CurrentProcessIdIsOneCallToGetCurrentProcessId()
    {
        GCToOSInterface.CurrentProcessIdInject = true;
        GCToOSInterface.CurrentProcessIdValue = 4242;

        Assert.Equal(4242u, GCToOSInterface.GetCurrentProcessId());
        Assert.Equal(1, GCToOSInterface.CurrentProcessIdCalls);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(uint.MaxValue)]
    public void CurrentProcessIdForwardsTheIdUnchanged(uint processId)
    {
        GCToOSInterface.CurrentProcessIdInject = true;
        GCToOSInterface.CurrentProcessIdValue = processId;

        Assert.Equal(processId, GCToOSInterface.GetCurrentProcessId());
    }

    [Fact]
    public void CurrentProcessIdWithoutInjectionIsThisProcess()
    {
        Assert.Equal((uint)Environment.ProcessId, GCToOSInterface.GetCurrentProcessId());
    }

    [Theory]
    // GetCurrentThreadId returns a DWORD and the method a uint64_t, so the widening has to be
    // zero extending.
    [InlineData(0u, 0ul)]
    [InlineData(1u, 1ul)]
    [InlineData(0x80000000u, 0x80000000ul)]
    [InlineData(uint.MaxValue, 0xFFFFFFFFul)]
    public void CurrentThreadIdForLoggingForwardsTheIdUnchanged(uint threadId, ulong expected)
    {
        GCToOSInterface.CurrentThreadIdInject = true;
        GCToOSInterface.CurrentThreadIdValue = threadId;

        Assert.Equal(expected, GCToOSInterface.GetCurrentThreadIdForLogging());
        Assert.Equal(1, GCToOSInterface.CurrentThreadIdCalls);
    }

    [Fact]
    public void CurrentThreadIdForLoggingWithoutInjectionIsThisThread()
    {
        Assert.NotEqual(0ul, GCToOSInterface.GetCurrentThreadIdForLogging());
    }

    [Theory]
    // GroupProcNo packs the group into the bits above the six the processor index occupies, so
    // the corners of both fields have to come back combined and nothing may overlap.
    [InlineData((ushort)0, (byte)0, 0u)]
    [InlineData((ushort)0, (byte)1, 1u)]
    [InlineData((ushort)0, (byte)63, 63u)]
    [InlineData((ushort)1, (byte)0, 64u)]
    [InlineData((ushort)1, (byte)1, 65u)]
    [InlineData((ushort)1, (byte)63, 127u)]
    [InlineData((ushort)2, (byte)0, 128u)]
    [InlineData((ushort)0x3FF, (byte)0x3F, 0xFFFFu)]
    public void CurrentProcessorNumberCombinesTheGroupAndTheIndex(ushort group, byte number, uint expected)
    {
        GCToOSInterface.CurrentProcessorNumberInject = true;
        GCToOSInterface.CurrentProcessorNumberGroup = group;
        GCToOSInterface.CurrentProcessorNumberNumber = number;

        Assert.Equal(expected, GCToOSInterface.GetCurrentProcessorNumber());
        Assert.Equal(1, GCToOSInterface.CurrentProcessorNumberCalls);
    }

    [Fact]
    public void CurrentProcessorNumberWithoutInjectionIsWithinTheMachine()
    {
        uint processorNumber = GCToOSInterface.GetCurrentProcessorNumber();

        Assert.Equal(1, GCToOSInterface.CurrentProcessorNumberCalls);
        Assert.True(processorNumber <= 0xFFFF, $"GetCurrentProcessorNumberEx returned {processorNumber}");
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(7u)]
    [InlineData(uint.MaxValue)]
    public void TotalProcessorCountReturnsTheCacheWhenItIsSet(uint cached)
    {
        GCToOSInterface.TotalCpuCountValue = cached;
        GCToOSInterface.CpuGroupCountValue = 3;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[0] = 10;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[1] = 11;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[2] = 12;
        GCToOSInterface.SystemInfoProcessorCountValue = 98;

        Assert.Equal(cached, GCToOSInterface.GetTotalProcessorCount());

        // The C++ returns before it asks anything else.
        Assert.Equal(0, GCToOSInterface.CanEnableGCCPUGroupsCalls);
        Assert.Equal(0, GCToOSInterface.CpuGroupCountCalls);
        Assert.Equal(0, GCToOSInterface.CpuGroupActiveProcessorCountCalls);
        Assert.Equal(0, GCToOSInterface.SystemInfoProcessorCountCalls);
    }

    [Fact]
    public void TotalProcessorCountUsesTheCpuGroupCountsWhenGroupsAreEnabled()
    {
        GCToOSInterface.CanEnableGCCPUGroupsValue = 1;
        GCToOSInterface.CpuGroupCountValue = 3;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[0] = 16;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[1] = 12;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[2] = 12;
        GCToOSInterface.SystemInfoProcessorCountValue = 12;

        Assert.Equal(40u, GCToOSInterface.GetTotalProcessorCount());
        Assert.Equal(1, GCToOSInterface.CpuGroupCountCalls);
        Assert.Equal(3, GCToOSInterface.CpuGroupActiveProcessorCountCalls);
        Assert.Equal(0, GCToOSInterface.SystemInfoProcessorCountCalls);

        // The value is written back into the g_totalCpuCount the C++ body shares.
        Assert.Equal(40u, GCToOSInterface.TotalCpuCountValue);
    }

    [Fact]
    public void TotalProcessorCountUsesTheSystemInfoCountWhenGroupsAreDisabled()
    {
        GCToOSInterface.CanEnableGCCPUGroupsValue = 0;
        GCToOSInterface.CpuGroupCountValue = 3;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[0] = 16;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[1] = 12;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[2] = 12;
        GCToOSInterface.SystemInfoProcessorCountValue = 12;

        Assert.Equal(12u, GCToOSInterface.GetTotalProcessorCount());
        Assert.Equal(0, GCToOSInterface.CpuGroupCountCalls);
        Assert.Equal(0, GCToOSInterface.CpuGroupActiveProcessorCountCalls);
        Assert.Equal(1, GCToOSInterface.SystemInfoProcessorCountCalls);
        Assert.Equal(12u, GCToOSInterface.TotalCpuCountValue);
    }

    [Fact]
    public void TotalProcessorCountAsksTheSourceOnlyOnce()
    {
        GCToOSInterface.CanEnableGCCPUGroupsValue = 0;
        GCToOSInterface.SystemInfoProcessorCountValue = 12;

        Assert.Equal(12u, GCToOSInterface.GetTotalProcessorCount());
        Assert.Equal(12u, GCToOSInterface.GetTotalProcessorCount());

        Assert.Equal(1, GCToOSInterface.CanEnableGCCPUGroupsCalls);
        Assert.Equal(1, GCToOSInterface.SystemInfoProcessorCountCalls);
    }

    [Fact]
    public void TotalProcessorCountKeepsAskingWhileTheSourceIsZero()
    {
        // Zero is the "not filled in yet" value of the cache, so a source that reports zero is
        // asked again on the next call. The C++ behaves the same way; nothing here papers over
        // it.
        GCToOSInterface.CanEnableGCCPUGroupsValue = 0;
        GCToOSInterface.SystemInfoProcessorCountValue = 0;

        Assert.Equal(0u, GCToOSInterface.GetTotalProcessorCount());
        Assert.Equal(0u, GCToOSInterface.GetTotalProcessorCount());

        Assert.Equal(2, GCToOSInterface.CanEnableGCCPUGroupsCalls);
        Assert.Equal(2, GCToOSInterface.SystemInfoProcessorCountCalls);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(-1, true)]
    public void CanEnableGCNumaAwareReflectsTheNativeStateOnWindows(int value, bool expected)
    {
        GCToOSInterface.CanEnableGCNumaAwareValue = value;

        Assert.Equal(expected, GCToOSInterface.CanEnableGCNumaAware());
        Assert.Equal(1, GCToOSInterface.CanEnableGCNumaAwareCalls);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(-1, true)]
    public void CanEnableGCCPUGroupsReflectsTheNativeStateOnWindows(int value, bool expected)
    {
        GCToOSInterface.CanEnableGCCPUGroupsValue = value;

        Assert.Equal(expected, GCToOSInterface.CanEnableGCCPUGroups());
        Assert.Equal(1, GCToOSInterface.CanEnableGCCPUGroupsCalls);
    }

    [Fact]
    public void GetNumaInfoReturnsFalseWhenNumaIsDisabledOnWindows()
    {
        GCToOSInterface.CanEnableGCNumaAwareValue = 0;

        ushort totalNodes = 33;
        uint maxProcsPerNode = 44;
        Assert.False(GCToOSInterface.GetNumaInfo(&totalNodes, &maxProcsPerNode));
        Assert.Equal((ushort)33, totalNodes);
        Assert.Equal(44u, maxProcsPerNode);
    }

    [Fact]
    public void GetNumaInfoReturnsTheLargestNodeMaskPopulationOnWindows()
    {
        GCToOSInterface.CanEnableGCNumaAwareValue = 1;
        GCToOSInterface.NumaNodeCountValue = 3;
        GCToOSInterface.GetNumaNodeProcessorMaskExInject = true;
        GCToOSInterface.GetNumaNodeProcessorMaskExResult = 1;
        GCToOSInterface.NumaNodeMasks[0] = 0b11;
        GCToOSInterface.NumaNodeMasks[1] = 0b100000;
        GCToOSInterface.NumaNodeMasks[2] = 0b1111;

        ushort totalNodes = 0;
        uint maxProcsPerNode = 0;
        Assert.True(GCToOSInterface.GetNumaInfo(&totalNodes, &maxProcsPerNode));
        Assert.Equal((ushort)3, totalNodes);
        Assert.Equal(4u, maxProcsPerNode);
        Assert.Equal(3, GCToOSInterface.GetNumaNodeProcessorMaskExCalls);
    }

    [Fact]
    public void GetCPUGroupInfoReturnsFalseWhenCpuGroupsAreDisabledOnWindows()
    {
        GCToOSInterface.CanEnableGCCPUGroupsValue = 0;

        ushort totalGroups = 33;
        uint maxProcsPerGroup = 44;
        Assert.False(GCToOSInterface.GetCPUGroupInfo(&totalGroups, &maxProcsPerGroup));
        Assert.Equal((ushort)33, totalGroups);
        Assert.Equal(44u, maxProcsPerGroup);
    }

    [Fact]
    public void GetCPUGroupInfoReturnsTheLargestActiveGroupCountOnWindows()
    {
        GCToOSInterface.CanEnableGCCPUGroupsValue = 1;
        GCToOSInterface.CpuGroupCountValue = 3;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[0] = 8;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[1] = 16;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[2] = 4;

        ushort totalGroups = 0;
        uint maxProcsPerGroup = 0;
        Assert.True(GCToOSInterface.GetCPUGroupInfo(&totalGroups, &maxProcsPerGroup));
        Assert.Equal((ushort)3, totalGroups);
        Assert.Equal(16u, maxProcsPerGroup);
        Assert.Equal(1, GCToOSInterface.CpuGroupCountCalls);
        Assert.Equal(3, GCToOSInterface.CpuGroupActiveProcessorCountCalls);
    }

    [Fact]
    public void SetCurrentThreadIdealAffinityOnWindowsSkipsCrossGroupMoves()
    {
        GCToOSInterface.CanEnableGCCPUGroupsValue = 1;
        GCToOSInterface.SetThreadIdealProcessorExInject = true;
        GCToOSInterface.SetThreadIdealProcessorExResult = 1;

        Assert.True(GCToOSInterface.SetCurrentThreadIdealAffinity((ushort)((1 << 6) | 2), (ushort)((2 << 6) | 3)));
        Assert.Equal(0, GCToOSInterface.SetThreadIdealProcessorExCalls);
    }

    [Fact]
    public void SetCurrentThreadIdealAffinityOnWindowsSetsTheDestinationWithinAGroup()
    {
        GCToOSInterface.CanEnableGCCPUGroupsValue = 1;
        GCToOSInterface.SetThreadIdealProcessorExInject = true;
        GCToOSInterface.SetThreadIdealProcessorExResult = 1;

        Assert.True(GCToOSInterface.SetCurrentThreadIdealAffinity((ushort)((2 << 6) | 1), (ushort)((2 << 6) | 5)));
        Assert.Equal(1, GCToOSInterface.SetThreadIdealProcessorExCalls);
        Assert.Equal((ushort)2, GCToOSInterface.LastSetThreadIdealProcessorEx.Group);
        Assert.Equal((byte)5, GCToOSInterface.LastSetThreadIdealProcessorEx.Number);
    }

    [Fact]
    public void SetCurrentThreadIdealAffinityOnWindowsUsesCurrentGroupWhenGroupsAreDisabled()
    {
        GCToOSInterface.CanEnableGCCPUGroupsValue = 0;
        GCToOSInterface.GetThreadIdealProcessorExInject = true;
        GCToOSInterface.GetThreadIdealProcessorExResult = 1;
        GCToOSInterface.GetThreadIdealProcessorExGroup = 3;
        GCToOSInterface.GetThreadIdealProcessorExNumber = 7;
        GCToOSInterface.SetThreadIdealProcessorExInject = true;
        GCToOSInterface.SetThreadIdealProcessorExResult = 1;

        Assert.True(GCToOSInterface.SetCurrentThreadIdealAffinity(0, 5));
        Assert.Equal(1, GCToOSInterface.GetThreadIdealProcessorExCalls);
        Assert.Equal(1, GCToOSInterface.SetThreadIdealProcessorExCalls);
        Assert.Equal((ushort)3, GCToOSInterface.LastSetThreadIdealProcessorEx.Group);
        Assert.Equal((byte)5, GCToOSInterface.LastSetThreadIdealProcessorEx.Number);
    }

    [Fact]
    public void SetCurrentThreadIdealAffinityOnWindowsReturnsTrueIfCurrentIdealProcessorCannotBeRead()
    {
        GCToOSInterface.CanEnableGCCPUGroupsValue = 0;
        GCToOSInterface.GetThreadIdealProcessorExInject = true;
        GCToOSInterface.GetThreadIdealProcessorExResult = 0;

        Assert.True(GCToOSInterface.SetCurrentThreadIdealAffinity(0, 5));
        Assert.Equal(1, GCToOSInterface.GetThreadIdealProcessorExCalls);
        Assert.Equal(0, GCToOSInterface.SetThreadIdealProcessorExCalls);
    }

    [Fact]
    public void GetCurrentThreadIdealProcOnWindowsReturnsThePackedGroupAndProcessor()
    {
        GCToOSInterface.GetThreadIdealProcessorExInject = true;
        GCToOSInterface.GetThreadIdealProcessorExResult = 1;
        GCToOSInterface.GetThreadIdealProcessorExGroup = 4;
        GCToOSInterface.GetThreadIdealProcessorExNumber = 9;

        ushort procNo = 0;
        Assert.True(GCToOSInterface.GetCurrentThreadIdealProc(&procNo));
        Assert.Equal((ushort)((4 << 6) | 9), procNo);
    }

    [Fact]
    public void GetCurrentThreadIdealProcOnWindowsReturnsFalseOnFailure()
    {
        GCToOSInterface.GetThreadIdealProcessorExInject = true;
        GCToOSInterface.GetThreadIdealProcessorExResult = 0;

        ushort procNo = 123;
        Assert.False(GCToOSInterface.GetCurrentThreadIdealProc(&procNo));
        Assert.Equal((ushort)123, procNo);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void SetThreadAffinityOnWindowsUsesSetThreadGroupAffinityWhenCpuGroupsAreEnabled(int nativeResult, bool expected)
    {
        GCToOSInterface.CanEnableGCCPUGroupsValue = 1;
        GCToOSInterface.SetThreadGroupAffinityInject = true;
        GCToOSInterface.SetThreadGroupAffinityResult = nativeResult;

        Assert.Equal(expected, GCToOSInterface.SetThreadAffinity((ushort)((2 << 6) | 9)));
        Assert.Equal(1, GCToOSInterface.SetThreadGroupAffinityCalls);
        Assert.Equal(0, GCToOSInterface.SetThreadAffinityMaskCalls);
        Assert.Equal((ushort)2, GCToOSInterface.LastSetThreadGroupAffinity.Group);
        Assert.Equal((nuint)1 << 9, GCToOSInterface.LastSetThreadGroupAffinity.Mask);
    }

    [Theory]
    [InlineData(1u, true)]
    [InlineData(0u, false)]
    public void SetThreadAffinityOnWindowsUsesSetThreadAffinityMaskWhenCpuGroupsAreDisabled(nuint nativeResult, bool expected)
    {
        GCToOSInterface.CanEnableGCCPUGroupsValue = 0;
        GCToOSInterface.SetThreadAffinityMaskInject = true;
        GCToOSInterface.SetThreadAffinityMaskResult = nativeResult;

        Assert.Equal(expected, GCToOSInterface.SetThreadAffinity(11));
        Assert.Equal(0, GCToOSInterface.SetThreadGroupAffinityCalls);
        Assert.Equal(1, GCToOSInterface.SetThreadAffinityMaskCalls);
        Assert.Equal((nuint)1 << 11, GCToOSInterface.LastSetThreadAffinityMask);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void BoostThreadPriorityOnWindowsUsesThreadPriorityHighest(int nativeResult, bool expected)
    {
        GCToOSInterface.SetThreadPriorityInject = true;
        GCToOSInterface.SetThreadPriorityResult = nativeResult;

        Assert.Equal(expected, GCToOSInterface.BoostThreadPriority());
        Assert.Equal(1, GCToOSInterface.SetThreadPriorityCalls);
        Assert.Equal(2, GCToOSInterface.LastSetThreadPriority);
    }

    [Fact]
    public void SetGCThreadsAffinitySetOnWindowsUsesConfiguredSetWhenCpuGroupsAreEnabled()
    {
        GCToOSInterface.TotalCpuCountValue = 8;
        GCToOSInterface.CanEnableGCCPUGroupsValue = 1;
        GCToOSInterface.SetProcessAffinityCpuCount(8);

        nuint* configBits = stackalloc nuint[1];
        AffinitySet configured = default;
        InitializeAffinitySet(&configured, configBits, 1, 1, 3, 5);

        AffinitySet* result = GCToOSInterface.SetGCThreadsAffinitySet(0b11111111, &configured);
        for (nuint i = 0; i < 8; i++)
        {
            bool shouldContain = (i == 1) || (i == 3) || (i == 5);
            Assert.Equal(shouldContain, result->Contains(i));
        }
    }

    [Fact]
    public void SetGCThreadsAffinitySetOnWindowsUsesMaskWhenCpuGroupsAreDisabled()
    {
        GCToOSInterface.TotalCpuCountValue = 8;
        GCToOSInterface.CanEnableGCCPUGroupsValue = 0;
        GCToOSInterface.SetProcessAffinityCpuCount(8);

        nuint* configBits = stackalloc nuint[1];
        AffinitySet configured = default;
        InitializeAffinitySet(&configured, configBits, 1);

        const nuint Mask = 0b0010_1101;
        AffinitySet* result = GCToOSInterface.SetGCThreadsAffinitySet(Mask, &configured);
        for (nuint i = 0; i < 8; i++)
        {
            bool shouldContain = (Mask & ((nuint)1 << (int)i)) != 0;
            Assert.Equal(shouldContain, result->Contains(i));
        }
    }

    [Fact]
    public void GetProcessorForHeapOnWindowsReturnsPackedProcessorAndNumaNode()
    {
        GCToOSInterface.TotalCpuCountValue = 6;
        GCToOSInterface.SetProcessAffinityCpuCount(6);
        GCToOSInterface.CanEnableGCCPUGroupsValue = 1;
        GCToOSInterface.CpuGroupCountValue = 3;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[0] = 2;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[1] = 2;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[2] = 2;
        GCToOSInterface.CpuGroupBeginValues[0] = 0;
        GCToOSInterface.CpuGroupBeginValues[1] = 2;
        GCToOSInterface.CpuGroupBeginValues[2] = 4;
        GCToOSInterface.CanEnableGCNumaAwareValue = 1;
        GCToOSInterface.GetNumaProcessorNodeExInject = true;
        GCToOSInterface.GetNumaProcessorNodeExResult = 1;
        GCToOSInterface.GetNumaProcessorNodeExNode = 9;

        ushort procNo = 0;
        ushort nodeNo = 0;
        Assert.True(GCToOSInterface.GetProcessorForHeap(3, &procNo, &nodeNo));
        Assert.Equal((ushort)((1 << 6) | 1), procNo);
        Assert.Equal((ushort)9, nodeNo);
        Assert.Equal(1, GCToOSInterface.GetNumaProcessorNodeExCalls);
    }

    [Fact]
    public void GetProcessorForHeapOnWindowsUsesCpuGroupAsNodeWhenNumaIsDisabled()
    {
        GCToOSInterface.TotalCpuCountValue = 6;
        GCToOSInterface.SetProcessAffinityCpuCount(6);
        GCToOSInterface.CanEnableGCCPUGroupsValue = 1;
        GCToOSInterface.CpuGroupCountValue = 3;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[0] = 2;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[1] = 2;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[2] = 2;
        GCToOSInterface.CpuGroupBeginValues[0] = 0;
        GCToOSInterface.CpuGroupBeginValues[1] = 2;
        GCToOSInterface.CpuGroupBeginValues[2] = 4;
        GCToOSInterface.CanEnableGCNumaAwareValue = 0;

        ushort procNo = 0;
        ushort nodeNo = 0;
        Assert.True(GCToOSInterface.GetProcessorForHeap(4, &procNo, &nodeNo));
        Assert.Equal((ushort)((2 << 6) | 0), procNo);
        Assert.Equal((ushort)2, nodeNo);
    }

    [Fact]
    public void GetProcessorForHeapOnWindowsReturnsFalseWhenHeapIsOutOfRange()
    {
        GCToOSInterface.TotalCpuCountValue = 2;
        GCToOSInterface.SetProcessAffinityCpuCount(2);

        ushort procNo = 0;
        ushort nodeNo = 0;
        Assert.False(GCToOSInterface.GetProcessorForHeap(2, &procNo, &nodeNo));
    }

    [Fact]
    public void ParseGCHeapAffinitizeRangesEntryOnWindowsParsesGroupRelativeRanges()
    {
        GCToOSInterface.CanEnableGCCPUGroupsValue = 1;
        GCToOSInterface.CpuGroupCountValue = 2;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[0] = 4;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[1] = 3;
        GCToOSInterface.CpuGroupBeginValues[0] = 0;
        GCToOSInterface.CpuGroupBeginValues[1] = 4;

        Assert.True(ParseRangeEntry("0:1-3", out nuint start0, out nuint end0, out int consumed0));
        Assert.Equal((nuint)1, start0);
        Assert.Equal((nuint)3, end0);
        Assert.Equal(5, consumed0);

        Assert.True(ParseRangeEntry("1:2", out nuint start1, out nuint end1, out int consumed1));
        Assert.Equal((nuint)6, start1);
        Assert.Equal((nuint)6, end1);
        Assert.Equal(3, consumed1);
    }

    [Theory]
    [InlineData("2:0")]
    [InlineData("1:3")]
    [InlineData("0-0:2")]
    [InlineData("0-1:2")]
    [InlineData("x")]
    public void ParseGCHeapAffinitizeRangesEntryOnWindowsRejectsInvalidEntries(string text)
    {
        GCToOSInterface.CanEnableGCCPUGroupsValue = 1;
        GCToOSInterface.CpuGroupCountValue = 2;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[0] = 4;
        GCToOSInterface.CpuGroupActiveProcessorCountValues[1] = 3;
        GCToOSInterface.CpuGroupBeginValues[0] = 0;
        GCToOSInterface.CpuGroupBeginValues[1] = 4;

        Assert.False(ParseRangeEntry(text, out _, out _, out _));
    }

#endif // TARGET_WINDOWS

    [Theory]
    // GetMaxProcessorCount is the capacity of the process affinity set, which is a whole number
    // of bitset entries.
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(16u)]
    public void MaxProcessorCountIsTheCapacityOfTheProcessAffinitySet(uint entries)
    {
        nuint maxCpuCount = entries * BitsPerBitsetEntry;
        GCToOSInterface.SetProcessAffinityMaxCpuCount(maxCpuCount);

        Assert.Equal((uint)maxCpuCount, GCToOSInterface.GetMaxProcessorCount());
    }

    private static void InitializeAffinitySet(AffinitySet* set, nuint* storage, nuint storageEntries, params nuint[] cpus)
    {
        NativeMemory.Clear(storage, storageEntries * (nuint)sizeof(nuint));
        set->InitializeWithStorage(storage, storageEntries);
        for (int i = 0; i < cpus.Length; i++)
        {
            set->Add(cpus[i]);
        }
    }

    private static bool ParseRangeEntry(string text, out nuint start, out nuint end, out int consumed)
    {
        Span<byte> buffer = stackalloc byte[text.Length + 1];
        for (int i = 0; i < text.Length; i++)
        {
            buffer[i] = (byte)text[i];
        }

        buffer[text.Length] = 0;

        fixed (byte* first = buffer)
        {
            byte* cursor = first;
            nuint parsedStart = 0;
            nuint parsedEnd = 0;
            bool parsed = GCToOSInterface.ParseGCHeapAffinitizeRangesEntry(&cursor, &parsedStart, &parsedEnd);
            consumed = (int)(cursor - first);
            start = parsedStart;
            end = parsedEnd;
            return parsed;
        }
    }
}
