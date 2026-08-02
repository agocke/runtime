// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Behavior tests for the memory limit and cache sizing port of GCToOSInterface -- the
// translation of GCToOSInterface::GetPhysicalMemoryLimit, GetMemoryStatus and
// GetCacheSizePerLogicalCpu, and the helpers under them, of gc/unix/gcenv.unix.cpp,
// gc/unix/cgroup.cpp and gc/windows/gcenv.windows.cpp.
//
// The ported bodies are the code under test. Only the libc / Win32 declarations and the handful
// of narrow native shims underneath them are substituted, by GCToOSInterface.Imports.*.TestHost.cs.
//
// Everything a cgroup limit, a /proc/meminfo row, a sysfs cache size or a job object would tell
// the process is injected rather than measured: a test machine cannot be put into a cgroup or a
// job object, and asserting against whatever the host happens to report would test nothing. That
// makes every assertion here exact.
//
// The expected values are written out here rather than read from the constants of the port, so
// that a wrong constant fails a test instead of being confirmed by it.

using System;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

// The injected state of these substitutes is process-wide -- getrlimit in particular is shared
// with GCVirtualMemoryTests -- so this class joins the collection that serializes every test
// class that injects into an import.
[Collection(SyncImportsCollection.Name)]
public sealed unsafe class GCMemoryLimitsTests
{
    public GCMemoryLimitsTests() => GCToOSInterface.ResetMemoryLimitsRecording();

#if !TARGET_WINDOWS
#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD

    //
    // The sysconf names the port asks for, written out here rather than read from the port.
    // Only the glibc and bionic branches are exercised, because that is what the test host runs
    // on; the rest are checked by the static_asserts of nativeaot/Runtime/gcenv.managed.cpp.
    //
#if TARGET_BIONIC
    private const int SC_PAGE_SIZE = 0x27;
    private const int SC_PHYS_PAGES = 0x62;
    private const int SC_AVPHYS_PAGES = 0x63;
    private const int SC_LEVEL1_DCACHE_SIZE = 0x92;
    private const int SC_LEVEL2_CACHE_SIZE = 0x95;
    private const int SC_LEVEL3_CACHE_SIZE = 0x98;
    private const int SC_LEVEL4_CACHE_SIZE = 0x9b;
#else
    private const int SC_PAGE_SIZE = 30;
    private const int SC_PHYS_PAGES = 85;
    private const int SC_AVPHYS_PAGES = 86;
    private const int SC_LEVEL1_DCACHE_SIZE = 188;
    private const int SC_LEVEL2_CACHE_SIZE = 191;
    private const int SC_LEVEL3_CACHE_SIZE = 194;
    private const int SC_LEVEL4_CACHE_SIZE = 197;
#endif

    /// <summary>The sentinel an unrestricted cgroup v1 reports, per gc/unix/cgroup.cpp.</summary>
    private const ulong CGroupUnrestrictedSentinel = 0x7FFFFFFF00000000;

    private const string SysFsSizePath = "/sys/devices/system/cpu/cpu0/cache/index{0}/size";
    private const string SysFsLevelPath = "/sys/devices/system/cpu/cpu0/cache/index{0}/level";

    private static string SizePath(int index) => string.Format(SysFsSizePath, index);

    private static string LevelPath(int index) => string.Format(SysFsLevelPath, index);

    /// <summary>
    /// Makes the two sysconf clamps of GetRestrictedPhysicalMemoryLimit inert, so that a test
    /// can drive one input at a time. -1 is what sysconf reports for a name it does not know,
    /// which is the "no clamp" path of the C++.
    /// </summary>
    private static void DisableSysconfClamp()
    {
        GCToOSInterface.SysconfValues[SC_PHYS_PAGES] = -1;
        GCToOSInterface.SysconfValues[SC_PAGE_SIZE] = -1;
    }

    /// <summary>
    /// Makes <c>GetAvailablePageFile</c> report no swap, so that the page file output of
    /// <c>GetMemoryStatus</c> is deterministic. Only the platforms whose C++ reads
    /// <c>sysinfo</c> can be driven this way; the BSDs read a sysctl node a test process cannot
    /// set, which is why the two page file tests below are compiled only where sysinfo is.
    /// </summary>
    private static void ReportNoSwap()
    {
#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD
        GCToOSInterface.SysinfoResult = -1;
#endif
    }

    private static void InjectCGroupLimit(ulong limit)
    {
        GCToOSInterface.CGroupPhysicalMemoryLimitResult = 1;
        GCToOSInterface.CGroupPhysicalMemoryLimitValue = limit;
    }

    //
    // GetRestrictedPhysicalMemoryLimit of gc/unix/cgroup.cpp.
    //

    [Fact]
    public void RestrictedPhysicalMemoryLimit_IsZeroWhenTheCGroupReaderFails()
    {
        GCToOSInterface.CGroupPhysicalMemoryLimitResult = 0;
        GCToOSInterface.CGroupPhysicalMemoryLimitValue = 64UL * 1024 * 1024;

        Assert.Equal((nuint)0, GCToOSInterface.GetRestrictedPhysicalMemoryLimit());
        Assert.Equal(1, GCToOSInterface.CGroupPhysicalMemoryLimitCalls);
    }

    [Theory]
    [InlineData(CGroupUnrestrictedSentinel + 1)]
    [InlineData(0x7FFFFFFFFFFFF000)]
    [InlineData(ulong.MaxValue)]
    public void RestrictedPhysicalMemoryLimit_IsZeroAboveTheUnrestrictedSentinel(ulong limit)
    {
        InjectCGroupLimit(limit);
        DisableSysconfClamp();

        Assert.Equal((nuint)0, GCToOSInterface.GetRestrictedPhysicalMemoryLimit());
    }

    [Fact]
    public void RestrictedPhysicalMemoryLimit_KeepsTheSentinelItself()
    {
        // The C++ compares with `>`, so the sentinel value exactly is still a limit. It survives
        // both clamps here, which is the only case where the SIZE_T_MAX saturation of the C++
        // could ever be reached on a 32-bit target.
        InjectCGroupLimit(CGroupUnrestrictedSentinel);
        DisableSysconfClamp();

        nuint expected = sizeof(nuint) == 8 ? unchecked((nuint)CGroupUnrestrictedSentinel) : nuint.MaxValue;
        Assert.Equal(expected, GCToOSInterface.GetRestrictedPhysicalMemoryLimit());
    }

    [Theory]
    // limit, rlim_cur, expected -- the C++ takes the smaller of the two.
    [InlineData(1024UL * 1024 * 1024, 512UL * 1024 * 1024, 512UL * 1024 * 1024)]
    [InlineData(512UL * 1024 * 1024, 1024UL * 1024 * 1024, 512UL * 1024 * 1024)]
    [InlineData(512UL * 1024 * 1024, 512UL * 1024 * 1024, 512UL * 1024 * 1024)]
    public void RestrictedPhysicalMemoryLimit_IsClampedByTheAddressSpaceRlimit(ulong limit, ulong softLimit, ulong expected)
    {
        InjectCGroupLimit(limit);
        DisableSysconfClamp();
        GCToOSInterface.GetrlimitInject = true;
        GCToOSInterface.GetrlimitResult = 0;
        GCToOSInterface.GetrlimitValue.rlim_cur = (nuint)softLimit;
        GCToOSInterface.GetrlimitValue.rlim_max = (nuint)softLimit;

        Assert.Equal((nuint)expected, GCToOSInterface.GetRestrictedPhysicalMemoryLimit());
    }

    [Fact]
    public void RestrictedPhysicalMemoryLimit_IgnoresTheRlimitWhenGetrlimitFails()
    {
        // A failed getrlimit leaves the soft limit at RLIM_INFINITY, so the cgroup limit stands.
        InjectCGroupLimit(1024UL * 1024 * 1024);
        DisableSysconfClamp();
        GCToOSInterface.GetrlimitInject = true;
        GCToOSInterface.GetrlimitResult = -1;
        GCToOSInterface.GetrlimitValue.rlim_cur = 1;

        Assert.Equal((nuint)(1024UL * 1024 * 1024), GCToOSInterface.GetRestrictedPhysicalMemoryLimit());
    }

    [Theory]
    // pages, pageSize, limit, expected. -1 for either sysconf means the clamp is skipped.
    [InlineData(1024, 4096, 64UL * 1024 * 1024, 4UL * 1024 * 1024)]
    [InlineData(1024, 4096, 1UL * 1024 * 1024, 1UL * 1024 * 1024)]
    [InlineData(-1, 4096, 64UL * 1024 * 1024, 64UL * 1024 * 1024)]
    [InlineData(1024, -1, 64UL * 1024 * 1024, 64UL * 1024 * 1024)]
    public void RestrictedPhysicalMemoryLimit_IsClampedByTheRealMemorySize(long pages, long pageSize, ulong limit, ulong expected)
    {
        InjectCGroupLimit(limit);
        GCToOSInterface.SysconfValues[SC_PHYS_PAGES] = (nint)pages;
        GCToOSInterface.SysconfValues[SC_PAGE_SIZE] = (nint)pageSize;

        Assert.Equal((nuint)expected, GCToOSInterface.GetRestrictedPhysicalMemoryLimit());
    }

    [Fact]
    public void PhysicalMemoryUsed_ForwardsTheCGroupReader()
    {
        GCToOSInterface.PhysicalMemoryUsedResult = 1;
        GCToOSInterface.PhysicalMemoryUsedValue = 12345;

        nuint used = 0;
        Assert.True(GCToOSInterface.GetPhysicalMemoryUsed(&used));
        Assert.Equal((nuint)12345, used);

        GCToOSInterface.PhysicalMemoryUsedResult = 0;
        Assert.False(GCToOSInterface.GetPhysicalMemoryUsed(&used));
    }

    //
    // GetPhysicalMemoryLimit.
    //

    [Fact]
    public void PhysicalMemoryLimit_ReportsTheRestrictedLimit()
    {
        InjectCGroupLimit(256UL * 1024 * 1024);
        DisableSysconfClamp();

        byte is_restricted = 0xCC;
        Assert.Equal(256UL * 1024 * 1024, GCToOSInterface.GetPhysicalMemoryLimit(&is_restricted));
        Assert.Equal(1, is_restricted);
        Assert.Equal((nuint)(256UL * 1024 * 1024), GCToOSInterface.g_RestrictedPhysicalMemoryLimit);
    }

    [Fact]
    public void PhysicalMemoryLimit_ReportsTheMachineSizeWhenUnrestricted()
    {
        GCToOSInterface.CGroupPhysicalMemoryLimitResult = 0;
        GCToOSInterface.SysconfValues[SC_PHYS_PAGES] = 1024;

        byte is_restricted = 0xCC;
        ulong expected = 1024UL * (ulong)GCToOSInterface.GetPageSize();
        Assert.Equal(expected, GCToOSInterface.GetPhysicalMemoryLimit(&is_restricted));
        Assert.Equal(0, is_restricted);
        Assert.Equal((nuint)0, GCToOSInterface.g_RestrictedPhysicalMemoryLimit);
    }

    [Fact]
    public void PhysicalMemoryLimit_IsZeroWhenTheMachineSizeIsNotKnown()
    {
        // sysconf(_SC_PHYS_PAGES) failing is the case the C++ turns into a failed
        // GCToOSInterface::Initialize, which leaves g_totalPhysicalMemSize at zero.
        GCToOSInterface.CGroupPhysicalMemoryLimitResult = 0;
        GCToOSInterface.SysconfValues[SC_PHYS_PAGES] = -1;

        Assert.Equal(0UL, GCToOSInterface.GetPhysicalMemoryLimit());
    }

    [Fact]
    public void PhysicalMemoryLimit_AcceptsANullIsRestricted()
    {
        InjectCGroupLimit(256UL * 1024 * 1024);
        DisableSysconfClamp();

        Assert.Equal(256UL * 1024 * 1024, GCToOSInterface.GetPhysicalMemoryLimit(null));
        Assert.Equal(256UL * 1024 * 1024, GCToOSInterface.GetPhysicalMemoryLimit());
    }

    //
    // The four cache size helpers.
    //

#if !TARGET_LINUX_MUSL
    [Theory]
    // The C++ walks the four names from the highest level down and stops at the first positive
    // size, so the level it reports is the highest one the C library knows a size for.
    [InlineData(1, 2, 3, 4, 4, 4)]
    [InlineData(1, 2, 3, 0, 3, 3)]
    [InlineData(1, 2, 0, 0, 2, 2)]
    [InlineData(1, 0, 0, 0, 1, 1)]
    [InlineData(0, 0, 0, 0, 0, 0)]
    // A negative size is what sysconf reports for a name it does not know; the C++ tests `> 0`.
    [InlineData(7, -1, -1, -1, 7, 1)]
    public void CacheSizeFromSysConf_TakesTheHighestLevelWithAPositiveSize(
        long l1, long l2, long l3, long l4, ulong expectedSize, ulong expectedLevel)
    {
        GCToOSInterface.SysconfValues[SC_LEVEL1_DCACHE_SIZE] = (nint)l1;
        GCToOSInterface.SysconfValues[SC_LEVEL2_CACHE_SIZE] = (nint)l2;
        GCToOSInterface.SysconfValues[SC_LEVEL3_CACHE_SIZE] = (nint)l3;
        GCToOSInterface.SysconfValues[SC_LEVEL4_CACHE_SIZE] = (nint)l4;

        nuint cacheLevel = 0;
        nuint cacheSize = 0;
        GCToOSInterface.GetLogicalProcessorCacheSizeFromSysConf(&cacheLevel, &cacheSize);

        Assert.Equal((nuint)expectedSize, cacheSize);
        Assert.Equal((nuint)expectedLevel, cacheLevel);
    }
#endif

#if !TARGET_ARM
    [Fact]
    public void CacheSizeFromSysFs_ReadsTheFivePathsInOrder()
    {
        nuint cacheLevel = 0;
        nuint cacheSize = 0;
        GCToOSInterface.GetLogicalProcessorCacheSizeFromSysFs(&cacheLevel, &cacheSize);

        // No file is readable, so nothing is written and only the size files are visited.
        Assert.Equal((nuint)0, cacheSize);
        Assert.Equal((nuint)0, cacheLevel);
        Assert.Equal(new[] { SizePath(0), SizePath(1), SizePath(2), SizePath(3), SizePath(4) },
            GCToOSInterface.MemoryValueFileCalls);
    }

    [Fact]
    public void CacheSizeFromSysFs_TakesTheLargestSizeAndTheLastLevelItRead()
    {
        GCToOSInterface.MemoryValueFiles[SizePath(0)] = 32 * 1024;
        GCToOSInterface.MemoryValueFiles[LevelPath(0)] = 1;
        GCToOSInterface.MemoryValueFiles[SizePath(1)] = 8 * 1024 * 1024;
        GCToOSInterface.MemoryValueFiles[LevelPath(1)] = 3;
        GCToOSInterface.MemoryValueFiles[SizePath(2)] = 512 * 1024;
        GCToOSInterface.MemoryValueFiles[LevelPath(2)] = 2;

        nuint cacheLevel = 0;
        nuint cacheSize = 0;
        GCToOSInterface.GetLogicalProcessorCacheSizeFromSysFs(&cacheLevel, &cacheSize);

        // The size is the largest of the three, but the level is whichever one was read last:
        // the C++ overwrites *cacheLevel every time a size file was readable, without comparing.
        Assert.Equal((nuint)(8 * 1024 * 1024), cacheSize);
        Assert.Equal((nuint)2, cacheLevel);
    }

    [Fact]
    public void CacheSizeFromSysFs_LeavesTheLevelAloneWhenOnlyTheSizeFileIsReadable()
    {
        GCToOSInterface.MemoryValueFiles[SizePath(3)] = 4 * 1024 * 1024;

        nuint cacheLevel = 9;
        nuint cacheSize = 0;
        GCToOSInterface.GetLogicalProcessorCacheSizeFromSysFs(&cacheLevel, &cacheSize);

        Assert.Equal((nuint)(4 * 1024 * 1024), cacheSize);
        Assert.Equal((nuint)9, cacheLevel);
        Assert.Contains(LevelPath(3), GCToOSInterface.MemoryValueFileCalls);
    }
#endif

    [Theory]
    [InlineData(1, 4UL * 1024 * 1024)]
    [InlineData(4, 4UL * 1024 * 1024)]
    [InlineData(5, 8UL * 1024 * 1024)]
    [InlineData(16, 8UL * 1024 * 1024)]
    [InlineData(17, 16UL * 1024 * 1024)]
    [InlineData(64, 16UL * 1024 * 1024)]
    [InlineData(65, 32UL * 1024 * 1024)]
    [InlineData(128, 32UL * 1024 * 1024)]
    public void CacheSizeFromHeuristic_ScalesWithTheProcessAffinityCount(int cpuCount, ulong expected)
    {
        GCToOSInterface.SetProcessAffinityCpuCount((nuint)cpuCount);

        nuint cacheLevel = 0;
        nuint cacheSize = 0;
        GCToOSInterface.GetLogicalProcessorCacheSizeFromHeuristic(&cacheLevel, &cacheSize);

        Assert.Equal((nuint)expected, cacheSize);

        // The C++ heuristic never reports a level, which is what makes the arm64 and
        // loongarch64 re-entry of GetLogicalProcessorCacheSizeFromOS terminate.
        Assert.Equal((nuint)0, cacheLevel);
    }

    [Fact]
    public void CacheSizeFromOS_FallsBackFromSysFsToTheHeuristic()
    {
        // GCConfig::GetGCCacheSizeFromSysConf defaults to false, so the sysconf path is skipped
        // and no _SC_LEVEL* name is ever asked for.
        GCToOSInterface.SetProcessAffinityCpuCount(8);

        Assert.Equal((nuint)(8 * 1024 * 1024), GCToOSInterface.GetLogicalProcessorCacheSizeFromOS());
        Assert.DoesNotContain(SC_LEVEL1_DCACHE_SIZE, GCToOSInterface.SysconfCalls);
#if TARGET_ARM
        Assert.Empty(GCToOSInterface.MemoryValueFileCalls);
#else
        Assert.Equal(5, GCToOSInterface.MemoryValueFileCalls.Count);
#endif
    }

#if !TARGET_ARM
    [Fact]
    public void CacheSizeFromOS_PrefersSysFsOverTheHeuristic()
    {
        GCToOSInterface.SetProcessAffinityCpuCount(8);
        GCToOSInterface.MemoryValueFiles[SizePath(2)] = 6 * 1024 * 1024;
        GCToOSInterface.MemoryValueFiles[LevelPath(2)] = 3;

        Assert.Equal((nuint)(6 * 1024 * 1024), GCToOSInterface.GetLogicalProcessorCacheSizeFromOS());
    }
#endif

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CacheSizePerLogicalCpu_ReportsTheSameSizeForBothScalings(bool trueSize)
    {
        // Unix applies no architecture scaling: the C++ assigns maxSize and maxTrueSize from the
        // same call, so both answers are the size the OS reported.
        GCToOSInterface.SetProcessAffinityCpuCount(32);

        Assert.Equal((nuint)(16 * 1024 * 1024), GCToOSInterface.GetCacheSizePerLogicalCpu(trueSize));
        Assert.Equal((nuint)(16 * 1024 * 1024), GCToOSInterface.s_maxSize);
        Assert.Equal((nuint)(16 * 1024 * 1024), GCToOSInterface.s_maxTrueSize);
    }

    [Fact]
    public void CacheSizePerLogicalCpu_AsksTheOSOnlyOnce()
    {
        GCToOSInterface.SetProcessAffinityCpuCount(2);
        Assert.Equal((nuint)(4 * 1024 * 1024), GCToOSInterface.GetCacheSizePerLogicalCpu());

        int reads = GCToOSInterface.MemoryValueFileCalls.Count;

        // A different machine would now answer differently; the cached value stands.
        GCToOSInterface.SetProcessAffinityCpuCount(128);
        Assert.Equal((nuint)(4 * 1024 * 1024), GCToOSInterface.GetCacheSizePerLogicalCpu());
        Assert.Equal((nuint)(4 * 1024 * 1024), GCToOSInterface.GetCacheSizePerLogicalCpu(false));
        Assert.Equal(reads, GCToOSInterface.MemoryValueFileCalls.Count);
    }

    //
    // GetAvailablePhysicalMemory and GetAvailablePageFile.
    //

    [Fact]
    public void AvailablePhysicalMemory_ReportsWhatProcMeminfoSaid()
    {
        GCToOSInterface.ReadMemAvailableResult = 1;
        GCToOSInterface.ReadMemAvailableValue = 777 * 1024;

        Assert.Equal(777UL * 1024, GCToOSInterface.GetAvailablePhysicalMemory());
        Assert.Equal(1, GCToOSInterface.ReadMemAvailableCalls);
        Assert.False(GCToOSInterface.s_tryReadMemInfoFailed);
    }

    [Fact]
    public void AvailablePhysicalMemory_FallsBackToSysconfAndStopsReadingProcMeminfo()
    {
        GCToOSInterface.ReadMemAvailableResult = 0;
        GCToOSInterface.SysconfValues[SC_AVPHYS_PAGES] = 100;
        GCToOSInterface.SysconfValues[SC_PAGE_SIZE] = 4096;

        Assert.Equal(100UL * 4096, GCToOSInterface.GetAvailablePhysicalMemory());
        Assert.Equal(1, GCToOSInterface.ReadMemAvailableCalls);
        Assert.True(GCToOSInterface.s_tryReadMemInfoFailed);

        // The sticky flag of the C++ keeps the second call away from /proc/meminfo entirely.
        Assert.Equal(100UL * 4096, GCToOSInterface.GetAvailablePhysicalMemory());
        Assert.Equal(1, GCToOSInterface.ReadMemAvailableCalls);
    }

#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD

    [Fact]
    public void AvailablePageFile_IsFreeSwapScaledByTheMemoryUnit()
    {
        GCToOSInterface.SysinfoResult = 0;
        GCToOSInterface.SysinfoValue.freeswap = 4096;
        GCToOSInterface.SysinfoValue.mem_unit = 1024;

        Assert.Equal(4096UL * 1024, GCToOSInterface.GetAvailablePageFile());
    }

    [Fact]
    public void AvailablePageFile_IsZeroWhenSysinfoFails()
    {
        GCToOSInterface.SysinfoResult = -1;
        GCToOSInterface.SysinfoValue.freeswap = 4096;
        GCToOSInterface.SysinfoValue.mem_unit = 1024;

        Assert.Equal(0UL, GCToOSInterface.GetAvailablePageFile());
    }

#endif // sysinfo platforms

    //
    // GetMemoryStatus.
    //

    [Theory]
    // restricted_limit, used, expected load, expected available.
    [InlineData(1000UL, 0UL, 0U, 1000UL)]
    [InlineData(1000UL, 250UL, 25U, 750UL)]
    [InlineData(1000UL, 1000UL, 100U, 0UL)]
    // Saturation: more in use than the limit leaves nothing available, and the load runs past 100.
    [InlineData(1000UL, 1500UL, 150U, 0UL)]
    public void MemoryStatus_RestrictedUsesTheCGroupUsage(ulong limit, ulong used, uint expectedLoad, ulong expectedAvailable)
    {
        GCToOSInterface.PhysicalMemoryUsedResult = 1;
        GCToOSInterface.PhysicalMemoryUsedValue = (nuint)used;
        ReportNoSwap();

        uint load = 0xCCCC;
        ulong available = 0xCCCC;
        ulong pageFile = 0xCCCC;
        GCToOSInterface.GetMemoryStatus(limit, &load, &available, &pageFile);

        Assert.Equal(expectedLoad, load);
        Assert.Equal(expectedAvailable, available);

#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD
        // The restricted branch of the Unix C++ still reports the swap space, unlike Windows.
        Assert.Equal(0UL, pageFile);
#endif
    }

    [Fact]
    public void MemoryStatus_RestrictedReportsNothingWhenTheUsageIsUnknown()
    {
        GCToOSInterface.PhysicalMemoryUsedResult = 0;
        GCToOSInterface.PhysicalMemoryUsedValue = 500;
        ReportNoSwap();

        uint load = 0xCCCC;
        ulong available = 0xCCCC;
        GCToOSInterface.GetMemoryStatus(1000, &load, &available, null);

        Assert.Equal(0U, load);
        Assert.Equal(0UL, available);
    }

    [Fact]
    public void MemoryStatus_UnrestrictedUsesTheMachineSize()
    {
        // 1000 pages of the machine's page size, of which 250 pages' worth is available.
        nuint pageSize = GCToOSInterface.GetPageSize();
        GCToOSInterface.SysconfValues[SC_PHYS_PAGES] = 1000;
        GCToOSInterface.ReadMemAvailableResult = 1;
        GCToOSInterface.ReadMemAvailableValue = 250 * (ulong)pageSize;
        ReportNoSwap();

        uint load = 0xCCCC;
        ulong available = 0xCCCC;
        ulong pageFile = 0xCCCC;
        GCToOSInterface.GetMemoryStatus(0, &load, &available, &pageFile);

        Assert.Equal(75U, load);
        Assert.Equal(250 * (ulong)pageSize, available);
#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD
        Assert.Equal(0UL, pageFile);
#endif
    }

    [Fact]
    public void MemoryStatus_UnrestrictedLoadIsZeroWhenNothingIsInUse()
    {
        nuint pageSize = GCToOSInterface.GetPageSize();
        GCToOSInterface.SysconfValues[SC_PHYS_PAGES] = 1000;
        GCToOSInterface.ReadMemAvailableResult = 1;

        // More available than the machine has: the C++ tests `total > available` and leaves the
        // load at zero rather than computing a negative one.
        GCToOSInterface.ReadMemAvailableValue = 4000 * (ulong)pageSize;
        ReportNoSwap();

        uint load = 0xCCCC;
        GCToOSInterface.GetMemoryStatus(0, &load, null, null);

        Assert.Equal(0U, load);
    }

    [Fact]
    public void MemoryStatus_UnrestrictedSkipsTheLoadWhenMemoryLoadIsNull()
    {
        GCToOSInterface.SysconfValues[SC_PHYS_PAGES] = 1000;
        GCToOSInterface.ReadMemAvailableResult = 1;
        GCToOSInterface.ReadMemAvailableValue = 4096;
        ReportNoSwap();
        GCToOSInterface.GetrlimitInject = true;
        GCToOSInterface.GetrlimitResult = 0;
        GCToOSInterface.GetrlimitValue.rlim_cur = 4096;

        ulong available = 0xCCCC;
        GCToOSInterface.GetMemoryStatus(0, null, &available, null);

        Assert.Equal(4096UL, available);

        // The whole `if (memory_load != NULL)` block is skipped, so the address space limit is
        // never looked at.
        Assert.Equal(0, GCToOSInterface.GetrlimitCalls);
    }

    [Fact]
    public void MemoryStatus_AcceptsEveryOutputBeingNull()
    {
        GCToOSInterface.SysconfValues[SC_PHYS_PAGES] = 1000;
        GCToOSInterface.ReadMemAvailableResult = 1;
        GCToOSInterface.ReadMemAvailableValue = 4096;
        ReportNoSwap();

        GCToOSInterface.GetMemoryStatus(0, null, null, null);
        GCToOSInterface.GetMemoryStatus(1000, null, null, null);
    }

    [Theory]
    // rlim_cur, used virtual, physical load, expected load. The higher of the two wins.
    [InlineData(1000UL, 900UL, 25U, 90U)]
    [InlineData(1000UL, 100UL, 25U, 25U)]
    public void MemoryStatus_TakesTheVirtualLoadWhenItIsHigher(ulong addressSpaceLimit, ulong usedVirtual, uint physicalLoad, uint expectedLoad)
    {
        nuint pageSize = GCToOSInterface.GetPageSize();
        GCToOSInterface.SysconfValues[SC_PHYS_PAGES] = 1000;
        GCToOSInterface.ReadMemAvailableResult = 1;
        GCToOSInterface.ReadMemAvailableValue = (ulong)(100 - physicalLoad) * 10 * (ulong)pageSize;
        ReportNoSwap();
        GCToOSInterface.GetrlimitInject = true;
        GCToOSInterface.GetrlimitResult = 0;
        GCToOSInterface.GetrlimitValue.rlim_cur = (nuint)addressSpaceLimit;
        GCToOSInterface.CurrentVirtualMemorySize = (nuint)usedVirtual;

        uint load = 0xCCCC;
        GCToOSInterface.GetMemoryStatus(0, &load, null, null);

        Assert.Equal(expectedLoad, load);
    }

    [Fact]
    public void MemoryStatus_IgnoresTheVirtualLoadWithoutAnAddressSpaceLimit()
    {
        nuint pageSize = GCToOSInterface.GetPageSize();
        GCToOSInterface.SysconfValues[SC_PHYS_PAGES] = 1000;
        GCToOSInterface.ReadMemAvailableResult = 1;
        GCToOSInterface.ReadMemAvailableValue = 750 * (ulong)pageSize;
        ReportNoSwap();
        GCToOSInterface.GetrlimitInject = true;
        GCToOSInterface.GetrlimitResult = 0;

        // RLIM_INFINITY, which is ~0ul for the non-large-file getrlimit of the C libraries the
        // managed GC targets.
        GCToOSInterface.GetrlimitValue.rlim_cur = nuint.MaxValue;
        GCToOSInterface.CurrentVirtualMemorySize = 1;

        uint load = 0xCCCC;
        GCToOSInterface.GetMemoryStatus(0, &load, null, null);

        Assert.Equal(25U, load);
        Assert.Equal(0, GCToOSInterface.CurrentVirtualMemorySizeCalls);
    }

    [Fact]
    public void MemoryStatus_IgnoresAnUnknownVirtualSize()
    {
        nuint pageSize = GCToOSInterface.GetPageSize();
        GCToOSInterface.SysconfValues[SC_PHYS_PAGES] = 1000;
        GCToOSInterface.ReadMemAvailableResult = 1;
        GCToOSInterface.ReadMemAvailableValue = 750 * (ulong)pageSize;
        ReportNoSwap();
        GCToOSInterface.GetrlimitInject = true;
        GCToOSInterface.GetrlimitResult = 0;
        GCToOSInterface.GetrlimitValue.rlim_cur = 1;

        // (size_t)-1 is what the C++ GetCurrentVirtualMemorySize reports for a /proc/self/statm
        // it could not read, and what the shim reports where the C++ has no such function.
        GCToOSInterface.CurrentVirtualMemorySize = nuint.MaxValue;

        uint load = 0xCCCC;
        GCToOSInterface.GetMemoryStatus(0, &load, null, null);

        Assert.Equal(25U, load);
        Assert.Equal(1, GCToOSInterface.CurrentVirtualMemorySizeCalls);
    }

#endif // !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD
#else // TARGET_WINDOWS

    /// <summary>The three job object limit flags, per &lt;windows.h&gt;.</summary>
    private const uint JOB_OBJECT_LIMIT_WORKINGSET = 0x00000001;
    private const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
    private const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;

    /// <summary>A machine with plenty of both, so that nothing clamps unless a test asks for it.</summary>
    private static void InjectUnconstrainedMachine()
    {
        GCToOSInterface.MemoryStatusValue.ullTotalPhys = 16UL * 1024 * 1024 * 1024;
        GCToOSInterface.MemoryStatusValue.ullAvailPhys = 8UL * 1024 * 1024 * 1024;
        GCToOSInterface.MemoryStatusValue.ullTotalVirtual = 128UL * 1024 * 1024 * 1024 * 1024;
        GCToOSInterface.MemoryStatusValue.ullAvailVirtual = 100UL * 1024 * 1024 * 1024 * 1024;
        GCToOSInterface.MemoryStatusValue.ullTotalPageFile = 32UL * 1024 * 1024 * 1024;
        GCToOSInterface.MemoryStatusValue.ullAvailPageFile = 24UL * 1024 * 1024 * 1024;
        GCToOSInterface.MemoryStatusValue.dwMemoryLoad = 50;
        GCToOSInterface.MemoryStatusInject = true;
    }

    private static void InjectJob(uint limitFlags, nuint jobMemoryLimit, nuint processMemoryLimit, nuint maximumWorkingSetSize)
    {
        GCToOSInterface.IsProcessInJobResult = 1;
        GCToOSInterface.IsProcessInJobValue = 1;
        GCToOSInterface.QueryInformationJobObjectResult = 1;
        GCToOSInterface.JobLimitInformation = default;
        GCToOSInterface.JobLimitInformation.BasicLimitInformation.LimitFlags = limitFlags;
        GCToOSInterface.JobLimitInformation.BasicLimitInformation.MaximumWorkingSetSize = maximumWorkingSetSize;
        GCToOSInterface.JobLimitInformation.JobMemoryLimit = jobMemoryLimit;
        GCToOSInterface.JobLimitInformation.ProcessMemoryLimit = processMemoryLimit;
    }

    [Fact]
    public void RestrictedPhysicalMemoryLimit_IsZeroOutsideAJob()
    {
        InjectUnconstrainedMachine();
        GCToOSInterface.IsProcessInJobResult = 1;
        GCToOSInterface.IsProcessInJobValue = 0;

        Assert.Equal((nuint)0, GCToOSInterface.GetRestrictedPhysicalMemoryLimit());
    }

    [Fact]
    public void RestrictedPhysicalMemoryLimit_IsZeroWhenIsProcessInJobFails()
    {
        InjectUnconstrainedMachine();
        GCToOSInterface.IsProcessInJobResult = 0;

        Assert.Equal((nuint)0, GCToOSInterface.GetRestrictedPhysicalMemoryLimit());

        // The `goto exit` of the C++ skips the job interrogation entirely.
        Assert.Equal(0, GCToOSInterface.QueryInformationJobObjectCalls);
    }

    [Fact]
    public void RestrictedPhysicalMemoryLimit_IsZeroWhenTheJobHasNoMemoryLimit()
    {
        InjectUnconstrainedMachine();
        InjectJob(0, 1024, 1024, 1024);

        Assert.Equal((nuint)0, GCToOSInterface.GetRestrictedPhysicalMemoryLimit());
    }

    [Theory]
    // flags, job, process, workingset, expected -- the C++ takes the smallest of the flagged ones.
    [InlineData(JOB_OBJECT_LIMIT_JOB_MEMORY, 512UL, 128UL, 64UL, 512UL)]
    [InlineData(JOB_OBJECT_LIMIT_PROCESS_MEMORY, 512UL, 128UL, 64UL, 128UL)]
    [InlineData(JOB_OBJECT_LIMIT_WORKINGSET, 512UL, 128UL, 64UL, 64UL)]
    [InlineData(JOB_OBJECT_LIMIT_JOB_MEMORY | JOB_OBJECT_LIMIT_PROCESS_MEMORY, 512UL, 128UL, 64UL, 128UL)]
    [InlineData(JOB_OBJECT_LIMIT_JOB_MEMORY | JOB_OBJECT_LIMIT_PROCESS_MEMORY | JOB_OBJECT_LIMIT_WORKINGSET, 512UL, 128UL, 64UL, 64UL)]
    public void RestrictedPhysicalMemoryLimit_TakesTheSmallestFlaggedJobLimit(
        uint limitFlags, ulong jobMemoryLimit, ulong processMemoryLimit, ulong maximumWorkingSetSize, ulong expected)
    {
        InjectUnconstrainedMachine();
        InjectJob(limitFlags, (nuint)jobMemoryLimit, (nuint)processMemoryLimit, (nuint)maximumWorkingSetSize);

        Assert.Equal((nuint)expected, GCToOSInterface.GetRestrictedPhysicalMemoryLimit());
    }

    [Fact]
    public void RestrictedPhysicalMemoryLimit_IsClampedByThePhysicalMemory()
    {
        InjectUnconstrainedMachine();
        GCToOSInterface.MemoryStatusValue.ullTotalPhys = 256;
        InjectJob(JOB_OBJECT_LIMIT_JOB_MEMORY, 1024, 0, 0);

        Assert.Equal((nuint)256, GCToOSInterface.GetRestrictedPhysicalMemoryLimit());
    }

    [Fact]
    public void RestrictedPhysicalMemoryLimit_IsZeroWhenLimitedByTheAddressSpace()
    {
        InjectUnconstrainedMachine();

        // The C++ compares the total virtual address space it read while interrogating the job
        // against the limit it settled on, and gives up on the limit when the process is bounded
        // by address space instead.
        GCToOSInterface.MemoryStatusValue.ullTotalVirtual = 128;
        InjectJob(JOB_OBJECT_LIMIT_JOB_MEMORY, 1024, 0, 0);

        Assert.Equal((nuint)0, GCToOSInterface.GetRestrictedPhysicalMemoryLimit());
    }

    [Fact]
    public void PhysicalMemoryLimit_ReportsTheJobLimit()
    {
        InjectUnconstrainedMachine();
        InjectJob(JOB_OBJECT_LIMIT_JOB_MEMORY, (nuint)(512UL * 1024 * 1024), 0, 0);

        byte is_restricted = 0xCC;
        Assert.Equal(512UL * 1024 * 1024, GCToOSInterface.GetPhysicalMemoryLimit(&is_restricted));
        Assert.Equal(1, is_restricted);
    }

    [Fact]
    public void PhysicalMemoryLimit_ReportsThePhysicalMemoryWhenUnrestricted()
    {
        InjectUnconstrainedMachine();
        GCToOSInterface.IsProcessInJobResult = 1;
        GCToOSInterface.IsProcessInJobValue = 0;

        byte is_restricted = 0xCC;
        Assert.Equal(16UL * 1024 * 1024 * 1024, GCToOSInterface.GetPhysicalMemoryLimit(&is_restricted));
        Assert.Equal(0, is_restricted);

        Assert.Equal(16UL * 1024 * 1024 * 1024, GCToOSInterface.GetPhysicalMemoryLimit(null));
    }

    [Fact]
    public void PhysicalMemoryLimit_IsCappedByTheVirtualAddressSpace()
    {
        InjectUnconstrainedMachine();
        GCToOSInterface.MemoryStatusValue.ullTotalVirtual = 2UL * 1024 * 1024 * 1024;
        GCToOSInterface.IsProcessInJobResult = 1;
        GCToOSInterface.IsProcessInJobValue = 0;

        Assert.Equal(2UL * 1024 * 1024 * 1024, GCToOSInterface.GetPhysicalMemoryLimit());
    }

    //
    // Cache sizing.
    //

#if !TARGET_ARM64
    [Fact]
    public void CacheSizeFromOS_IsZeroWhenGetLogicalProcessorInformationFails()
    {
        GCToOSInterface.LogicalProcessorInformation = new[]
        {
            GCToOSInterface.CacheEntry(level: 3, size: 8 * 1024 * 1024),
        };
        GCToOSInterface.GetLogicalProcessorInformationFails = true;

        Assert.Equal((nuint)0, GCToOSInterface.GetLogicalProcessorCacheSizeFromOS());

        // GetLPI sizes the buffer with one call and fills it with a second, and frees what it
        // allocated when the second one fails.
        Assert.Equal(2, GCToOSInterface.GetLogicalProcessorInformationCalls);
        Assert.Equal(0, GCToOSInterface.LiveAllocations);
    }
#endif

    [Fact]
    public void CacheSizeFromOS_TakesTheLargestCacheOfAnyRelationCacheEntry()
    {
        GCToOSInterface.LogicalProcessorInformation = new[]
        {
            GCToOSInterface.CacheEntry(level: 1, size: 32 * 1024),
            GCToOSInterface.NonCacheEntry(),
            GCToOSInterface.CacheEntry(level: 3, size: 8 * 1024 * 1024),
            GCToOSInterface.CacheEntry(level: 2, size: 512 * 1024),
        };

        // The largest is an L3, so the arm64 heuristic below the crack loop leaves it alone.
        Assert.Equal((nuint)(8 * 1024 * 1024), GCToOSInterface.GetLogicalProcessorCacheSizeFromOS());
        Assert.Equal(0, GCToOSInterface.LiveAllocations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CacheSizePerLogicalCpu_ReportsTheSameSizeForBothScalings(bool trueSize)
    {
        // Windows applies no architecture scaling either: the C++ assigns maxSize and
        // maxTrueSize from the same call.
        GCToOSInterface.LogicalProcessorInformation = new[]
        {
            GCToOSInterface.CacheEntry(level: 3, size: 4 * 1024 * 1024),
        };

        Assert.Equal((nuint)(4 * 1024 * 1024), GCToOSInterface.GetCacheSizePerLogicalCpu(trueSize));
        Assert.Equal((nuint)(4 * 1024 * 1024), GCToOSInterface.s_maxSize);
        Assert.Equal((nuint)(4 * 1024 * 1024), GCToOSInterface.s_maxTrueSize);
    }

    [Fact]
    public void CacheSizePerLogicalCpu_AsksTheOSOnlyOnce()
    {
        GCToOSInterface.LogicalProcessorInformation = new[]
        {
            GCToOSInterface.CacheEntry(level: 3, size: 4 * 1024 * 1024),
        };

        Assert.Equal((nuint)(4 * 1024 * 1024), GCToOSInterface.GetCacheSizePerLogicalCpu());
        int calls = GCToOSInterface.GetLogicalProcessorInformationCalls;

        GCToOSInterface.LogicalProcessorInformation = new[]
        {
            GCToOSInterface.CacheEntry(level: 3, size: 64 * 1024 * 1024),
        };
        Assert.Equal((nuint)(4 * 1024 * 1024), GCToOSInterface.GetCacheSizePerLogicalCpu());
        Assert.Equal((nuint)(4 * 1024 * 1024), GCToOSInterface.GetCacheSizePerLogicalCpu(false));
        Assert.Equal(calls, GCToOSInterface.GetLogicalProcessorInformationCalls);
    }

    //
    // GetMemoryStatus.
    //

    [Theory]
    // restricted_limit, working set, expected load, expected available.
    [InlineData(1000UL, 0UL, 0U, 1000UL)]
    [InlineData(1000UL, 250UL, 25U, 750UL)]
    [InlineData(1000UL, 1000UL, 100U, 0UL)]
    [InlineData(1000UL, 1500UL, 150U, 0UL)]
    public void MemoryStatus_RestrictedUsesTheWorkingSet(ulong limit, ulong workingSetSize, uint expectedLoad, ulong expectedAvailable)
    {
        InjectUnconstrainedMachine();
        GCToOSInterface.ProcessMemoryInfoResult = 1;
        GCToOSInterface.ProcessMemoryInfoWorkingSetSize = (nuint)workingSetSize;

        uint load = 0xCCCC;
        ulong available = 0xCCCC;
        ulong pageFile = 0xCCCC;
        GCToOSInterface.GetMemoryStatus(limit, &load, &available, &pageFile);

        Assert.Equal(expectedLoad, load);
        Assert.Equal(expectedAvailable, available);

        // Windows deliberately does not make another OS call for the page file when restricted.
        Assert.Equal(0UL, pageFile);
    }

    [Fact]
    public void MemoryStatus_FallsBackToTheMachineWhenGetProcessMemoryInfoFails()
    {
        InjectUnconstrainedMachine();
        GCToOSInterface.ProcessMemoryInfoResult = 0;
        GCToOSInterface.ProcessMemoryInfoWorkingSetSize = 250;

        uint load = 0xCCCC;
        ulong available = 0xCCCC;
        ulong pageFile = 0xCCCC;
        GCToOSInterface.GetMemoryStatus(1000, &load, &available, &pageFile);

        Assert.Equal(50U, load);
        Assert.Equal(8UL * 1024 * 1024 * 1024, available);
        Assert.Equal(24UL * 1024 * 1024 * 1024, pageFile);
    }

    [Fact]
    public void MemoryStatus_UnrestrictedReportsTheMemoryStatus()
    {
        InjectUnconstrainedMachine();

        uint load = 0xCCCC;
        ulong available = 0xCCCC;
        ulong pageFile = 0xCCCC;
        GCToOSInterface.GetMemoryStatus(0, &load, &available, &pageFile);

        Assert.Equal(50U, load);
        Assert.Equal(8UL * 1024 * 1024 * 1024, available);
        Assert.Equal(24UL * 1024 * 1024 * 1024, pageFile);
    }

    [Fact]
    public void MemoryStatus_UnrestrictedUsesTheVirtualSpaceWhenItIsTheSmaller()
    {
        InjectUnconstrainedMachine();
        GCToOSInterface.MemoryStatusValue.ullTotalVirtual = 1000;
        GCToOSInterface.MemoryStatusValue.ullAvailVirtual = 250;

        uint load = 0xCCCC;
        ulong available = 0xCCCC;
        ulong pageFile = 0xCCCC;
        GCToOSInterface.GetMemoryStatus(0, &load, &available, &pageFile);

        Assert.Equal(75U, load);
        Assert.Equal(1000UL, available);
        Assert.Equal(0UL, pageFile);
    }

    [Fact]
    public void MemoryStatus_AcceptsEveryOutputBeingNull()
    {
        InjectUnconstrainedMachine();
        GCToOSInterface.ProcessMemoryInfoResult = 1;
        GCToOSInterface.ProcessMemoryInfoWorkingSetSize = 250;

        GCToOSInterface.GetMemoryStatus(0, null, null, null);
        GCToOSInterface.GetMemoryStatus(1000, null, null, null);

        GCToOSInterface.MemoryStatusValue.ullTotalVirtual = 1000;
        GCToOSInterface.MemoryStatusValue.ullAvailVirtual = 250;
        GCToOSInterface.GetMemoryStatus(0, null, null, null);
    }

#endif // TARGET_WINDOWS
}
