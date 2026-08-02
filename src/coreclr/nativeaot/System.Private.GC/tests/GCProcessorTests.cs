// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Behavior tests for the processor count and identity port of GCToOSInterface -- the translation
// of GetCurrentThreadIdForLogging, GetCurrentProcessId, GetCurrentProcessorNumber,
// CanGetCurrentProcessorNumber, GetTotalProcessorCount and GetMaxProcessorCount of
// gc/unix/gcenv.unix.cpp and gc/windows/gcenv.windows.cpp.
//
// The ported bodies are the code under test. Only what is underneath them is substituted: on
// Unix sched_getcpu and getpid, plus the two shims that report the state GCToOSInterface::Initialize
// still owns; on Windows GetCurrentThreadId, GetCurrentProcessId and GetCurrentProcessorNumberEx,
// plus the four shims and the CanEnableGCCPUGroups forwarder that stand for the CPU group state.
//
// The three things worth pinning are the ones the C++ makes exact. First, forwarding: each of
// these bodies is one call, and what a test can check is that it is that call, once, and that
// the value survives the widening or the signed-to-unsigned cast unchanged. Second, the
// HAVE_SCHED_GETCPU branch: which of the two Unix bodies is compiled is a platform decision, and
// the assert path of each can only run where asserts are compiled out. Third, the Windows
// GetTotalProcessorCount cache and its two sources, which is the only one of the six with a
// branch in it, and the (group << 6) | procIndex packing of GroupProcNo.
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
        GCToOSInterface.CpuGroupProcessorCountValue = 99;
        GCToOSInterface.SystemInfoProcessorCountValue = 98;

        Assert.Equal(cached, GCToOSInterface.GetTotalProcessorCount());

        // The C++ returns before it asks anything else.
        Assert.Equal(0, GCToOSInterface.CanEnableGCCPUGroupsCalls);
        Assert.Equal(0, GCToOSInterface.CpuGroupProcessorCountCalls);
        Assert.Equal(0, GCToOSInterface.SystemInfoProcessorCountCalls);
    }

    [Fact]
    public void TotalProcessorCountUsesTheCpuGroupCountWhenGroupsAreEnabled()
    {
        GCToOSInterface.CanEnableGCCPUGroupsValue = 1;
        GCToOSInterface.CpuGroupProcessorCountValue = 40;
        GCToOSInterface.SystemInfoProcessorCountValue = 12;

        Assert.Equal(40u, GCToOSInterface.GetTotalProcessorCount());
        Assert.Equal(1, GCToOSInterface.CpuGroupProcessorCountCalls);
        Assert.Equal(0, GCToOSInterface.SystemInfoProcessorCountCalls);

        // The value is written back into the g_totalCpuCount the C++ body shares.
        Assert.Equal(40u, GCToOSInterface.TotalCpuCountValue);
    }

    [Fact]
    public void TotalProcessorCountUsesTheSystemInfoCountWhenGroupsAreDisabled()
    {
        GCToOSInterface.CanEnableGCCPUGroupsValue = 0;
        GCToOSInterface.CpuGroupProcessorCountValue = 40;
        GCToOSInterface.SystemInfoProcessorCountValue = 12;

        Assert.Equal(12u, GCToOSInterface.GetTotalProcessorCount());
        Assert.Equal(0, GCToOSInterface.CpuGroupProcessorCountCalls);
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

        Assert.Equal(2, GCToOSInterface.SystemInfoProcessorCountCalls);
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
}
