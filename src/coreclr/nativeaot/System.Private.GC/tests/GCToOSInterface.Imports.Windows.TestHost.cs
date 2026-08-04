// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Test-host substitute for src/.../Environment/GCToOSInterface.Imports.Windows.cs.
//
// The shipping declarations are [RuntimeImport]s, which only resolve inside a NativeAOT image,
// so this file declares the same private methods as ordinary P/Invokes. That makes the ported
// bodies above them -- the flag combinations, the NUMA alternatives, the large page rounding,
// the failure paths, the sleep, the yield, the timers and the processor counts -- runnable in a
// normal test process against the real kernel, and it records the arguments of every call so
// that the flag translation can be asserted directly rather than inferred.
//
// A [DllImport] is exactly what the GC must not use; it is fine here because this file is never
// compiled into the GC. The methods it replaces are the boundary of the port: everything the
// tests exercise above them is the shipping code.
//
// One behavior cannot be reproduced here: GetLastError called through a P/Invoke reports the
// error of the marshalling stub rather than of the preceding call, so InitLargePagesPrivilege
// cannot be tested in this host. The large page path is therefore only covered on Unix, where
// it is a plain mmap flag.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

internal static unsafe partial class GCToOSInterface
{
    internal struct VirtualAllocCall
    {
        public void* lpAddress;
        public nuint dwSize;
        public uint flAllocationType;
        public uint flProtect;
        public uint nndPreferred;
        public bool numaAware;
        public void* result;
    }

    internal struct VirtualFreeCall
    {
        public void* lpAddress;
        public nuint dwSize;
        public uint dwFreeType;
        public int result;
    }

    internal static VirtualAllocCall LastVirtualAlloc;
    internal static int VirtualAllocCount;

    internal static VirtualFreeCall LastVirtualFree;
    internal static int VirtualFreeCount;

    internal static int VirtualUnlockCount;
    internal static int ForceVirtualDecommitFailureCount;

    internal struct WriteWatchCall
    {
        public uint dwFlags;
        public void* lpBaseAddress;
        public nuint dwRegionSize;
        public nuint count;
        public uint granularity;
        public uint result;
    }

    internal static WriteWatchCall LastResetWriteWatch;
    internal static int ResetWriteWatchCount;

    internal static WriteWatchCall LastGetWriteWatch;
    internal static int GetWriteWatchCount;

    //
    // Sleep and yield. These recordings are deliberately not touched by ResetRecording, so that
    // the sleep and yield tests cannot clobber -- or be clobbered by -- a virtual memory test
    // that xUnit happens to run at the same time in another class.
    //

    internal struct SleepExCall
    {
        public uint dwMilliseconds;
        public int bAlertable;
    }

    internal static SleepExCall LastSleepEx;
    internal static int SleepExCount;
    internal static int SwitchToThreadCount;

    /// <summary>Forgets the sleep and yield recording.</summary>
    internal static void ResetSleepYieldRecording()
    {
        LastSleepEx = default;
        SleepExCount = 0;
        SwitchToThreadCount = 0;
    }

    /// <summary>Forgets every recorded call. Each test starts by calling this.</summary>
    internal static void ResetRecording()
    {
        LastVirtualAlloc = default;
        VirtualAllocCount = 0;
        LastVirtualFree = default;
        VirtualFreeCount = 0;
        VirtualUnlockCount = 0;
        ForceVirtualDecommitFailureCount = 0;
        LastResetWriteWatch = default;
        ResetWriteWatchCount = 0;
        LastGetWriteWatch = default;
        GetWriteWatchCount = 0;
    }

    private static void* VirtualAlloc(void* lpAddress, nuint dwSize, uint flAllocationType, uint flProtect)
    {
        void* result = sys_VirtualAlloc(lpAddress, dwSize, flAllocationType, flProtect);
        LastVirtualAlloc = new VirtualAllocCall
        {
            lpAddress = lpAddress,
            dwSize = dwSize,
            flAllocationType = flAllocationType,
            flProtect = flProtect,
            numaAware = false,
            result = result,
        };
        VirtualAllocCount++;
        return result;
    }

    private static void* VirtualAllocExNuma(void* hProcess, void* lpAddress, nuint dwSize, uint flAllocationType, uint flProtect, uint nndPreferred)
    {
        void* result = sys_VirtualAllocExNuma(hProcess, lpAddress, dwSize, flAllocationType, flProtect, nndPreferred);
        LastVirtualAlloc = new VirtualAllocCall
        {
            lpAddress = lpAddress,
            dwSize = dwSize,
            flAllocationType = flAllocationType,
            flProtect = flProtect,
            nndPreferred = nndPreferred,
            numaAware = true,
            result = result,
        };
        VirtualAllocCount++;
        return result;
    }

    private static int VirtualFree(void* lpAddress, nuint dwSize, uint dwFreeType)
    {
        if (ForceVirtualDecommitFailureCount > 0 && dwFreeType == MEM_DECOMMIT)
        {
            ForceVirtualDecommitFailureCount--;
            LastVirtualFree = new VirtualFreeCall
            {
                lpAddress = lpAddress,
                dwSize = dwSize,
                dwFreeType = dwFreeType,
                result = 0,
            };
            VirtualFreeCount++;
            return 0;
        }

        int result = sys_VirtualFree(lpAddress, dwSize, dwFreeType);
        LastVirtualFree = new VirtualFreeCall
        {
            lpAddress = lpAddress,
            dwSize = dwSize,
            dwFreeType = dwFreeType,
            result = result,
        };
        VirtualFreeCount++;
        return result;
    }

    private static int VirtualUnlock(void* lpAddress, nuint dwSize)
    {
        VirtualUnlockCount++;
        return sys_VirtualUnlock(lpAddress, dwSize);
    }

    private static uint Win32ResetWriteWatch(void* lpBaseAddress, nuint dwRegionSize)
    {
        uint result = sys_ResetWriteWatch(lpBaseAddress, dwRegionSize);
        LastResetWriteWatch = new WriteWatchCall
        {
            lpBaseAddress = lpBaseAddress,
            dwRegionSize = dwRegionSize,
            result = result,
        };
        ResetWriteWatchCount++;
        return result;
    }

    private static uint Win32GetWriteWatch(uint dwFlags, void* lpBaseAddress, nuint dwRegionSize, void** lpAddresses, nuint* lpdwCount, uint* lpdwGranularity)
    {
        uint result = sys_GetWriteWatch(dwFlags, lpBaseAddress, dwRegionSize, lpAddresses, lpdwCount, lpdwGranularity);
        LastGetWriteWatch = new WriteWatchCall
        {
            dwFlags = dwFlags,
            lpBaseAddress = lpBaseAddress,
            dwRegionSize = dwRegionSize,
            count = result == 0 ? *lpdwCount : 0,
            granularity = result == 0 ? *lpdwGranularity : 0,
            result = result,
        };
        GetWriteWatchCount++;
        return result;
    }

    private static uint SleepEx(uint dwMilliseconds, int bAlertable)
    {
        LastSleepEx = new SleepExCall { dwMilliseconds = dwMilliseconds, bAlertable = bAlertable };
        SleepExCount++;
        return sys_SleepEx(dwMilliseconds, bAlertable);
    }

    private static int SwitchToThread()
    {
        SwitchToThreadCount++;
        return sys_SwitchToThread();
    }

    [DllImport("kernel32", EntryPoint = "VirtualAlloc", SetLastError = true)]
    private static extern void* sys_VirtualAlloc(void* lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);
    [DllImport("kernel32", EntryPoint = "VirtualAllocExNuma", SetLastError = true)]
    private static extern void* sys_VirtualAllocExNuma(void* hProcess, void* lpAddress, nuint dwSize, uint flAllocationType, uint flProtect, uint nndPreferred);

    [DllImport("kernel32", EntryPoint = "VirtualFree", SetLastError = true)]
    private static extern int sys_VirtualFree(void* lpAddress, nuint dwSize, uint dwFreeType);

    [DllImport("kernel32", EntryPoint = "VirtualUnlock", SetLastError = true)]
    private static extern int sys_VirtualUnlock(void* lpAddress, nuint dwSize);

    [DllImport("kernel32", EntryPoint = "GetLargePageMinimum")]
    private static extern nuint GetLargePageMinimum();

    [DllImport("kernel32", EntryPoint = "ResetWriteWatch")]
    private static extern uint sys_ResetWriteWatch(void* lpBaseAddress, nuint dwRegionSize);

    [DllImport("kernel32", EntryPoint = "GetWriteWatch")]
    private static extern uint sys_GetWriteWatch(uint dwFlags, void* lpBaseAddress, nuint dwRegionSize, void** lpAddresses, nuint* lpdwCount, uint* lpdwGranularity);

    [DllImport("kernel32", EntryPoint = "GetSystemInfo")]
    private static extern void GetSystemInfo(SYSTEM_INFO* lpSystemInfo);

    [DllImport("kernel32", EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
    private static extern int sys_GlobalMemoryStatusEx(MEMORYSTATUSEX* lpBuffer);

    [DllImport("kernel32", EntryPoint = "GetCurrentProcess")]
    private static extern void* GetCurrentProcess();

    [DllImport("kernel32", EntryPoint = "GetLastError")]
    private static extern uint sys_GetLastError();

    [DllImport("kernel32", EntryPoint = "CloseHandle", SetLastError = true)]
    private static extern int CloseHandle(void* hObject);

    [DllImport("advapi32", EntryPoint = "OpenProcessToken", SetLastError = true)]
    private static extern int OpenProcessToken(void* ProcessHandle, uint DesiredAccess, void** TokenHandle);

    [DllImport("advapi32", EntryPoint = "LookupPrivilegeValueW", SetLastError = true)]
    private static extern int LookupPrivilegeValueW(char* lpSystemName, char* lpName, LUID* lpLuid);

    [DllImport("advapi32", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)]
    private static extern int AdjustTokenPrivileges(void* TokenHandle, int DisableAllPrivileges, TOKEN_PRIVILEGES* NewState, uint BufferLength, TOKEN_PRIVILEGES* PreviousState, uint* ReturnLength);

    [DllImport("kernel32", EntryPoint = "SleepEx")]
    private static extern uint sys_SleepEx(uint dwMilliseconds, int bAlertable);

    [DllImport("kernel32", EntryPoint = "SwitchToThread")]
    private static extern int sys_SwitchToThread();

    //
    // The memory limit and cache sizing substitutes.
    //
    // Nothing here can be measured on a test machine: a test process cannot put itself into a
    // job object with a memory limit, and the cache topology of the host is whatever it is. So
    // unlike the virtual memory substitutes above, which forward to the real kernel and record,
    // these hand back injected answers, and the ported bodies above them are what is exercised.
    //

    /// <summary>Injected <c>GlobalMemoryStatusEx</c> answer; the real one is used when false.</summary>
    internal static bool MemoryStatusInject;

    /// <summary>What an injecting <c>GlobalMemoryStatusEx</c> reports.</summary>
    internal static MEMORYSTATUSEX MemoryStatusValue;

    internal static int MemoryStatusCalls;

    /// <summary>What <c>GetLastError</c> reports; the real one is used when null.</summary>
    internal static uint? LastErrorValue;

    /// <summary>The BOOL <c>IsProcessInJob</c> returns, and the <c>Result</c> it writes.</summary>
    internal static int IsProcessInJobResult = 1;

    /// <inheritdoc cref="IsProcessInJobResult"/>
    internal static int IsProcessInJobValue;

    internal static int IsProcessInJobCalls;

    /// <summary>The BOOL <c>QueryInformationJobObject</c> returns.</summary>
    internal static int QueryInformationJobObjectResult;

    /// <summary>What <c>QueryInformationJobObject</c> writes into the caller's buffer.</summary>
    internal static JOBOBJECT_EXTENDED_LIMIT_INFORMATION JobLimitInformation;

    internal static int QueryInformationJobObjectCalls;

    /// <summary>
    /// One entry of the array <c>GetLogicalProcessorInformation</c> reports. Only the two
    /// members GetLogicalProcessorCacheSizeFromOS reads are settable.
    /// </summary>
    internal struct LogicalProcessorEntry
    {
        public int Relationship;
        public byte Level;
        public uint Size;
    }

    /// <summary>A RelationCache entry of the given level and size.</summary>
    internal static LogicalProcessorEntry CacheEntry(byte level, uint size) =>
        new LogicalProcessorEntry { Relationship = 2, Level = level, Size = size };

    /// <summary>
    /// A RelationProcessorCore entry, whose union member the crack loop must not look at.
    /// </summary>
    internal static LogicalProcessorEntry NonCacheEntry() =>
        new LogicalProcessorEntry { Relationship = 0, Level = 9, Size = 0xFFFFFFFF };

    /// <summary>What <c>GetLogicalProcessorInformation</c> reports.</summary>
    internal static LogicalProcessorEntry[] LogicalProcessorInformation = Array.Empty<LogicalProcessorEntry>();

    /// <summary>
    /// Makes the second <c>GetLogicalProcessorInformation</c> call fail, which is the path
    /// where GetLPI frees the buffer it just allocated and reports failure.
    /// </summary>
    internal static bool GetLogicalProcessorInformationFails;

    internal static int GetLogicalProcessorInformationCalls;

    /// <summary>The BOOL <c>GetProcessMemoryInfo</c> returns, and the working set it writes.</summary>
    internal static int ProcessMemoryInfoResult;

    /// <inheritdoc cref="ProcessMemoryInfoResult"/>
    internal static nuint ProcessMemoryInfoWorkingSetSize;

    internal static int ProcessMemoryInfoCalls;

    /// <summary>Live <c>ManagedGC_AllocZeroed</c> blocks, so that a leak fails a test.</summary>
    internal static int LiveAllocations;

    /// <summary>Forgets every memory limit recording and clears every injection.</summary>
    internal static void ResetMemoryLimitsRecording()
    {
        MemoryStatusInject = false;
        MemoryStatusValue = default;
        MemoryStatusCalls = 0;
        LastErrorValue = null;
        IsProcessInJobResult = 1;
        IsProcessInJobValue = 0;
        IsProcessInJobCalls = 0;
        QueryInformationJobObjectResult = 0;
        JobLimitInformation = default;
        QueryInformationJobObjectCalls = 0;
        LogicalProcessorInformation = Array.Empty<LogicalProcessorEntry>();
        GetLogicalProcessorInformationFails = false;
        GetLogicalProcessorInformationCalls = 0;
        ProcessMemoryInfoResult = 0;
        ProcessMemoryInfoWorkingSetSize = 0;
        ProcessMemoryInfoCalls = 0;

        // The two caches of the shipping code are function-local statics in C++ and fields
        // here, so each test starts from the value they have in a fresh process.
        s_maxSize = 0;
        s_maxTrueSize = 0;
    }

    private static int GlobalMemoryStatusEx(MEMORYSTATUSEX* lpBuffer)
    {
        MemoryStatusCalls++;
        if (!MemoryStatusInject)
        {
            return sys_GlobalMemoryStatusEx(lpBuffer);
        }

        uint dwLength = lpBuffer->dwLength;
        *lpBuffer = MemoryStatusValue;
        lpBuffer->dwLength = dwLength;
        return 1;
    }

    private static uint GetLastError() => LastErrorValue ?? sys_GetLastError();

    private static int IsProcessInJob(void* ProcessHandle, void* JobHandle, int* Result)
    {
        IsProcessInJobCalls++;
        if (IsProcessInJobResult != 0)
        {
            *Result = IsProcessInJobValue;
        }

        return IsProcessInJobResult;
    }

    private static int QueryInformationJobObject(void* hJob, int JobObjectInformationClass, void* lpJobObjectInformation, uint cbJobObjectInformationLength, uint* lpReturnLength)
    {
        QueryInformationJobObjectCalls++;
        if (QueryInformationJobObjectResult == 0)
        {
            return 0;
        }

        Assert.Equal(9, JobObjectInformationClass);
        Assert.Equal((uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION), cbJobObjectInformationLength);
        *(JOBOBJECT_EXTENDED_LIMIT_INFORMATION*)lpJobObjectInformation = JobLimitInformation;
        if (lpReturnLength != null)
        {
            *lpReturnLength = cbJobObjectInformationLength;
        }

        return 1;
    }

    private static int GetLogicalProcessorInformation(SYSTEM_LOGICAL_PROCESSOR_INFORMATION* Buffer, uint* ReturnedLength)
    {
        GetLogicalProcessorInformationCalls++;

        uint needed = (uint)(sizeof(SYSTEM_LOGICAL_PROCESSOR_INFORMATION) * LogicalProcessorInformation.Length);
        if (Buffer == null || *ReturnedLength < needed)
        {
            *ReturnedLength = needed;
            LastErrorValue = 122; // ERROR_INSUFFICIENT_BUFFER
            return 0;
        }

        if (GetLogicalProcessorInformationFails)
        {
            LastErrorValue = 1; // ERROR_INVALID_FUNCTION, anything but ERROR_INSUFFICIENT_BUFFER
            return 0;
        }

        for (int i = 0; i < LogicalProcessorInformation.Length; i++)
        {
            Buffer[i].Relationship = LogicalProcessorInformation[i].Relationship;
            Buffer[i].DUMMYUNIONNAME.Cache.Level = LogicalProcessorInformation[i].Level;
            Buffer[i].DUMMYUNIONNAME.Cache.Size = LogicalProcessorInformation[i].Size;
        }

        *ReturnedLength = needed;
        return 1;
    }

    private static int GetProcessMemoryInfo(void* Process, PROCESS_MEMORY_COUNTERS* ppsmemCounters, uint cb)
    {
        ProcessMemoryInfoCalls++;
        Assert.Equal((uint)sizeof(PROCESS_MEMORY_COUNTERS), cb);
        *ppsmemCounters = default;
        ppsmemCounters->cb = cb;
        ppsmemCounters->WorkingSetSize = ProcessMemoryInfoWorkingSetSize;
        return ProcessMemoryInfoResult;
    }

    private static void* ManagedGC_AllocZeroed(nuint size)
    {
        LiveAllocations++;
        return NativeMemory.AllocZeroed(size);
    }

    private static void ManagedGC_Free(void* ptr)
    {
        LiveAllocations--;
        NativeMemory.Free(ptr);
    }

    //
    // The three <windows.h> timer substitutes. Each one defaults to the real kernel call, as
    // the virtual memory substitutes do, and can be made to fail so that the failure path of
    // the port -- the assert, and the value the failed call left behind -- can be driven.
    //

    internal static bool QueryPerformanceCounterFails;
    internal static bool QueryPerformanceFrequencyFails;
    internal static bool QueryUnbiasedInterruptTimeFails;

    internal static bool PerformanceCounterInject;
    internal static long PerformanceCounterValue;

    internal static bool PerformanceFrequencyInject;
    internal static long PerformanceFrequencyValue;

    internal static bool UnbiasedInterruptTimeInject;
    internal static ulong UnbiasedInterruptTimeValue;

    internal static int QueryPerformanceCounterCalls;
    internal static int QueryPerformanceFrequencyCalls;
    internal static int QueryUnbiasedInterruptTimeCalls;

    /// <summary>Forgets every timer recording and clears every injection.</summary>
    internal static void ResetTimerRecording()
    {
        QueryPerformanceCounterFails = false;
        QueryPerformanceFrequencyFails = false;
        QueryUnbiasedInterruptTimeFails = false;
        PerformanceCounterInject = false;
        PerformanceCounterValue = 0;
        PerformanceFrequencyInject = false;
        PerformanceFrequencyValue = 0;
        UnbiasedInterruptTimeInject = false;
        UnbiasedInterruptTimeValue = 0;
        QueryPerformanceCounterCalls = 0;
        QueryPerformanceFrequencyCalls = 0;
        QueryUnbiasedInterruptTimeCalls = 0;
    }

    private static int Win32QueryPerformanceCounter(long* lpPerformanceCount)
    {
        QueryPerformanceCounterCalls++;
        if (QueryPerformanceCounterFails)
        {
            // A failed call writes nothing, which is what leaves the C++ LARGE_INTEGER and the
            // managed long holding whatever the stack had.
            return 0;
        }

        if (PerformanceCounterInject)
        {
            *lpPerformanceCount = PerformanceCounterValue;
            return 1;
        }

        return sys_QueryPerformanceCounter(lpPerformanceCount);
    }

    private static int Win32QueryPerformanceFrequency(long* lpFrequency)
    {
        QueryPerformanceFrequencyCalls++;
        if (QueryPerformanceFrequencyFails)
        {
            return 0;
        }

        if (PerformanceFrequencyInject)
        {
            *lpFrequency = PerformanceFrequencyValue;
            return 1;
        }

        return sys_QueryPerformanceFrequency(lpFrequency);
    }

    private static int QueryUnbiasedInterruptTime(ulong* UnbiasedTime)
    {
        QueryUnbiasedInterruptTimeCalls++;
        if (QueryUnbiasedInterruptTimeFails)
        {
            return 0;
        }

        if (UnbiasedInterruptTimeInject)
        {
            *UnbiasedTime = UnbiasedInterruptTimeValue;
            return 1;
        }

        return sys_QueryUnbiasedInterruptTime(UnbiasedTime);
    }

    [DllImport("kernel32", EntryPoint = "QueryPerformanceCounter")]
    private static extern int sys_QueryPerformanceCounter(long* lpPerformanceCount);

    [DllImport("kernel32", EntryPoint = "QueryPerformanceFrequency")]
    private static extern int sys_QueryPerformanceFrequency(long* lpFrequency);

    [DllImport("kernel32", EntryPoint = "QueryUnbiasedInterruptTime")]
    private static extern int sys_QueryUnbiasedInterruptTime(ulong* UnbiasedTime);

    //
    // The processor, affinity, NUMA and CPU-group ports. The Win32 entry points are real calls
    // that a test can also take over; the ManagedGC_Windows_ shims stand in for state that only
    // gc/windows/gcenv.windows.cpp has, so each of them is injection only and starts from the
    // value a fresh process would see.
    //

    internal static bool CurrentThreadIdInject;
    internal static uint CurrentThreadIdValue;
    internal static int CurrentThreadIdCalls;

    internal static bool CurrentProcessIdInject;
    internal static uint CurrentProcessIdValue;
    internal static int CurrentProcessIdCalls;

    internal static bool CurrentProcessorNumberInject;
    internal static ushort CurrentProcessorNumberGroup;
    internal static byte CurrentProcessorNumberNumber;
    internal static int CurrentProcessorNumberCalls;

    /// <summary>
    /// The storage behind <c>g_totalCpuCount</c>. It is native memory so that the pointer the
    /// shipping code writes through stays put, exactly as the C++ global does.
    /// </summary>
    private static readonly uint* s_totalCpuCount = (uint*)NativeMemory.AllocZeroed((nuint)sizeof(uint));

    /// <summary>The value of <c>g_totalCpuCount</c> that a test sets or reads back.</summary>
    internal static uint TotalCpuCountValue
    {
        get => *s_totalCpuCount;
        set => *s_totalCpuCount = value;
    }

    internal static int TotalCpuCountCalls;

    internal static uint SystemInfoProcessorCountValue;
    internal static int SystemInfoProcessorCountCalls;

    internal static int CanEnableGCNumaAwareValue;
    internal static int CanEnableGCNumaAwareCalls;

    internal static uint NumaNodeCountValue;
    internal static int NumaNodeCountCalls;

    internal static int CanEnableGCCPUGroupsValue;
    internal static int CanEnableGCCPUGroupsCalls;

    internal static ushort CpuGroupCountValue;
    internal static int CpuGroupCountCalls;
    internal static readonly ushort[] CpuGroupActiveProcessorCountValues = new ushort[64];
    internal static readonly ushort[] CpuGroupBeginValues = new ushort[64];
    internal static int CpuGroupActiveProcessorCountCalls;
    internal static int CpuGroupBeginCalls;

    internal static bool GetThreadIdealProcessorExInject;
    internal static int GetThreadIdealProcessorExResult;
    internal static ushort GetThreadIdealProcessorExGroup;
    internal static byte GetThreadIdealProcessorExNumber;
    internal static int GetThreadIdealProcessorExCalls;

    internal static bool SetThreadIdealProcessorExInject;
    internal static int SetThreadIdealProcessorExResult;
    internal static int SetThreadIdealProcessorExCalls;
    internal static PROCESSOR_NUMBER LastSetThreadIdealProcessorEx;

    internal static bool SetThreadGroupAffinityInject;
    internal static int SetThreadGroupAffinityResult;
    internal static int SetThreadGroupAffinityCalls;
    internal static GROUP_AFFINITY LastSetThreadGroupAffinity;

    internal static bool SetThreadAffinityMaskInject;
    internal static nuint SetThreadAffinityMaskResult;
    internal static int SetThreadAffinityMaskCalls;
    internal static nuint LastSetThreadAffinityMask;

    internal static bool SetThreadPriorityInject;
    internal static int SetThreadPriorityResult;
    internal static int SetThreadPriorityCalls;
    internal static int LastSetThreadPriority;

    internal static readonly Dictionary<ushort, nuint> NumaNodeMasks = new Dictionary<ushort, nuint>();
    internal static bool GetNumaNodeProcessorMaskExInject;
    internal static int GetNumaNodeProcessorMaskExResult;
    internal static int GetNumaNodeProcessorMaskExCalls;

    internal static bool GetNumaProcessorNodeExInject;
    internal static int GetNumaProcessorNodeExResult;
    internal static ushort GetNumaProcessorNodeExNode;
    internal static int GetNumaProcessorNodeExCalls;

    /// <summary>
    /// The bitset behind the affinity set <c>ManagedGC_Windows_GetProcessAffinitySet</c> hands
    /// back, and the set itself. Both are native memory so that the pointer the shipping code
    /// reads through stays put.
    /// </summary>
    private const nuint ProcessAffinitySetEntries = 16;

    private static readonly nuint* s_processAffinityBitset =
        (nuint*)NativeMemory.AllocZeroed(ProcessAffinitySetEntries, (nuint)sizeof(nuint));

    private static readonly AffinitySet* s_processAffinitySet = CreateProcessAffinitySet();

    private static AffinitySet* CreateProcessAffinitySet()
    {
        AffinitySet* set = (AffinitySet*)NativeMemory.AllocZeroed((nuint)sizeof(AffinitySet));
        set->InitializeWithStorage(s_processAffinityBitset, ProcessAffinitySetEntries);
        return set;
    }

    /// <summary>
    /// Makes the process affinity set contain exactly processors
    /// <c>[0, cpuCount)</c>.
    /// </summary>
    internal static void SetProcessAffinityCpuCount(nuint cpuCount)
    {
        NativeMemory.Clear(s_processAffinityBitset, ProcessAffinitySetEntries * (nuint)sizeof(nuint));
        s_processAffinitySet->InitializeWithStorage(s_processAffinityBitset, ProcessAffinitySetEntries);
        for (nuint i = 0; i < cpuCount; i++)
        {
            s_processAffinitySet->Add(i);
        }
    }

    /// <summary>
    /// Makes the process affinity set report exactly <paramref name="maxCpuCount"/> as its
    /// capacity, which is what <c>GetMaxProcessorCount</c> returns. It has to be a multiple of
    /// the bitset entry width, because the capacity of an AffinitySet is a whole number of
    /// entries; the tests pass such values.
    /// </summary>
    internal static void SetProcessAffinityMaxCpuCount(nuint maxCpuCount)
    {
        nuint bitsPerEntry = (nuint)sizeof(nuint) * 8;
        nuint entries = maxCpuCount / bitsPerEntry;
        Debug.Assert(maxCpuCount % bitsPerEntry == 0);
        Debug.Assert(entries <= ProcessAffinitySetEntries);

        NativeMemory.Clear(s_processAffinityBitset, ProcessAffinitySetEntries * (nuint)sizeof(nuint));
        s_processAffinitySet->InitializeWithStorage(s_processAffinityBitset, entries);
    }

    /// <summary>Forgets every processor recording and clears every injection.</summary>
    internal static void ResetProcessorRecording()
    {
        CurrentThreadIdInject = false;
        CurrentThreadIdValue = 0;
        CurrentThreadIdCalls = 0;
        CurrentProcessIdInject = false;
        CurrentProcessIdValue = 0;
        CurrentProcessIdCalls = 0;
        CurrentProcessorNumberInject = false;
        CurrentProcessorNumberGroup = 0;
        CurrentProcessorNumberNumber = 0;
        CurrentProcessorNumberCalls = 0;
        TotalCpuCountValue = 0;
        TotalCpuCountCalls = 0;
        SystemInfoProcessorCountValue = 0;
        SystemInfoProcessorCountCalls = 0;
        CanEnableGCNumaAwareValue = 0;
        CanEnableGCNumaAwareCalls = 0;
        NumaNodeCountValue = 0;
        NumaNodeCountCalls = 0;
        CanEnableGCCPUGroupsValue = 0;
        CanEnableGCCPUGroupsCalls = 0;
        CpuGroupCountValue = 0;
        CpuGroupCountCalls = 0;
        Array.Clear(CpuGroupActiveProcessorCountValues);
        Array.Clear(CpuGroupBeginValues);
        CpuGroupActiveProcessorCountCalls = 0;
        CpuGroupBeginCalls = 0;
        GetThreadIdealProcessorExInject = false;
        GetThreadIdealProcessorExResult = 0;
        GetThreadIdealProcessorExGroup = 0;
        GetThreadIdealProcessorExNumber = 0;
        GetThreadIdealProcessorExCalls = 0;
        SetThreadIdealProcessorExInject = false;
        SetThreadIdealProcessorExResult = 0;
        SetThreadIdealProcessorExCalls = 0;
        LastSetThreadIdealProcessorEx = default;
        SetThreadGroupAffinityInject = false;
        SetThreadGroupAffinityResult = 0;
        SetThreadGroupAffinityCalls = 0;
        LastSetThreadGroupAffinity = default;
        SetThreadAffinityMaskInject = false;
        SetThreadAffinityMaskResult = 0;
        SetThreadAffinityMaskCalls = 0;
        LastSetThreadAffinityMask = 0;
        SetThreadPriorityInject = false;
        SetThreadPriorityResult = 0;
        SetThreadPriorityCalls = 0;
        LastSetThreadPriority = 0;
        NumaNodeMasks.Clear();
        GetNumaNodeProcessorMaskExInject = false;
        GetNumaNodeProcessorMaskExResult = 0;
        GetNumaNodeProcessorMaskExCalls = 0;
        GetNumaProcessorNodeExInject = false;
        GetNumaProcessorNodeExResult = 0;
        GetNumaProcessorNodeExNode = 0;
        GetNumaProcessorNodeExCalls = 0;
        SetProcessAffinityMaxCpuCount(ProcessAffinitySetEntries * (nuint)sizeof(nuint) * 8);
    }

    private static uint GetCurrentThreadId()
    {
        CurrentThreadIdCalls++;
        return CurrentThreadIdInject ? CurrentThreadIdValue : sys_GetCurrentThreadId();
    }

    private static uint Win32GetCurrentProcessId()
    {
        CurrentProcessIdCalls++;
        return CurrentProcessIdInject ? CurrentProcessIdValue : sys_GetCurrentProcessId();
    }

    private static void GetCurrentProcessorNumberEx(PROCESSOR_NUMBER* ProcNumber)
    {
        CurrentProcessorNumberCalls++;
        if (!CurrentProcessorNumberInject)
        {
            sys_GetCurrentProcessorNumberEx(ProcNumber);
            return;
        }

        ProcNumber->Group = CurrentProcessorNumberGroup;
        ProcNumber->Number = CurrentProcessorNumberNumber;
        ProcNumber->Reserved = 0;
    }

    private static uint* ManagedGC_Windows_GetTotalCpuCount()
    {
        TotalCpuCountCalls++;
        return s_totalCpuCount;
    }

    private static uint ManagedGC_Windows_GetSystemInfoProcessorCount()
    {
        SystemInfoProcessorCountCalls++;
        return SystemInfoProcessorCountValue;
    }

    private static AffinitySet* ManagedGC_Windows_GetProcessAffinitySet() => s_processAffinitySet;

    private static int ManagedGC_Windows_GetCanEnableGCNumaAware()
    {
        CanEnableGCNumaAwareCalls++;
        return CanEnableGCNumaAwareValue;
    }

    private static uint ManagedGC_Windows_GetNumaNodeCount()
    {
        NumaNodeCountCalls++;
        return NumaNodeCountValue;
    }

    private static int ManagedGC_Windows_GetCanEnableGCCPUGroups()
    {
        CanEnableGCCPUGroupsCalls++;
        return CanEnableGCCPUGroupsValue;
    }

    private static ushort ManagedGC_Windows_GetCpuGroupCount()
    {
        CpuGroupCountCalls++;
        return CpuGroupCountValue;
    }

    private static ushort ManagedGC_Windows_GetCpuGroupActiveProcessorCount(ushort groupNumber)
    {
        CpuGroupActiveProcessorCountCalls++;
        return CpuGroupActiveProcessorCountValues[groupNumber];
    }

    private static ushort ManagedGC_Windows_GetCpuGroupBegin(ushort groupNumber)
    {
        CpuGroupBeginCalls++;
        return CpuGroupBeginValues[groupNumber];
    }

    private static void* GetCurrentThread()
    {
        return sys_GetCurrentThread();
    }

    private static int SetThreadIdealProcessorEx(void* hThread, PROCESSOR_NUMBER* lpIdealProcessor, PROCESSOR_NUMBER* lpPreviousIdealProcessor)
    {
        SetThreadIdealProcessorExCalls++;
        LastSetThreadIdealProcessorEx = *lpIdealProcessor;
        if (!SetThreadIdealProcessorExInject)
        {
            return sys_SetThreadIdealProcessorEx(hThread, lpIdealProcessor, lpPreviousIdealProcessor);
        }

        if ((SetThreadIdealProcessorExResult != 0) && (lpPreviousIdealProcessor != null))
        {
            lpPreviousIdealProcessor->Group = 0;
            lpPreviousIdealProcessor->Number = 0;
            lpPreviousIdealProcessor->Reserved = 0;
        }

        return SetThreadIdealProcessorExResult;
    }

    private static int GetThreadIdealProcessorEx(void* hThread, PROCESSOR_NUMBER* lpIdealProcessor)
    {
        GetThreadIdealProcessorExCalls++;
        if (!GetThreadIdealProcessorExInject)
        {
            return sys_GetThreadIdealProcessorEx(hThread, lpIdealProcessor);
        }

        if (GetThreadIdealProcessorExResult != 0)
        {
            lpIdealProcessor->Group = GetThreadIdealProcessorExGroup;
            lpIdealProcessor->Number = GetThreadIdealProcessorExNumber;
            lpIdealProcessor->Reserved = 0;
        }

        return GetThreadIdealProcessorExResult;
    }

    private static int SetThreadGroupAffinity(void* hThread, GROUP_AFFINITY* GroupAffinity, GROUP_AFFINITY* PreviousGroupAffinity)
    {
        SetThreadGroupAffinityCalls++;
        LastSetThreadGroupAffinity = *GroupAffinity;
        if (!SetThreadGroupAffinityInject)
        {
            return sys_SetThreadGroupAffinity(hThread, GroupAffinity, PreviousGroupAffinity);
        }

        return SetThreadGroupAffinityResult;
    }

    private static nuint SetThreadAffinityMask(void* hThread, nuint dwThreadAffinityMask)
    {
        SetThreadAffinityMaskCalls++;
        LastSetThreadAffinityMask = dwThreadAffinityMask;
        if (!SetThreadAffinityMaskInject)
        {
            return sys_SetThreadAffinityMask(hThread, dwThreadAffinityMask);
        }

        return SetThreadAffinityMaskResult;
    }

    private static int SetThreadPriority(void* hThread, int nPriority)
    {
        SetThreadPriorityCalls++;
        LastSetThreadPriority = nPriority;
        if (!SetThreadPriorityInject)
        {
            return sys_SetThreadPriority(hThread, nPriority);
        }

        return SetThreadPriorityResult;
    }

    private static int GetNumaNodeProcessorMaskEx(ushort Node, GROUP_AFFINITY* ProcessorMask)
    {
        GetNumaNodeProcessorMaskExCalls++;
        if (!GetNumaNodeProcessorMaskExInject)
        {
            return sys_GetNumaNodeProcessorMaskEx(Node, ProcessorMask);
        }

        if (GetNumaNodeProcessorMaskExResult != 0)
        {
            ProcessorMask->Mask = NumaNodeMasks.TryGetValue(Node, out nuint mask) ? mask : 0;
            ProcessorMask->Group = 0;
            ProcessorMask->Reserved[0] = 0;
            ProcessorMask->Reserved[1] = 0;
            ProcessorMask->Reserved[2] = 0;
        }

        return GetNumaNodeProcessorMaskExResult;
    }

    private static int GetNumaProcessorNodeEx(PROCESSOR_NUMBER* Processor, ushort* NodeNumber)
    {
        GetNumaProcessorNodeExCalls++;
        if (!GetNumaProcessorNodeExInject)
        {
            return sys_GetNumaProcessorNodeEx(Processor, NodeNumber);
        }

        if (GetNumaProcessorNodeExResult != 0)
        {
            *NodeNumber = GetNumaProcessorNodeExNode;
        }

        return GetNumaProcessorNodeExResult;
    }

    [DllImport("kernel32", EntryPoint = "GetCurrentThreadId")]
    private static extern uint sys_GetCurrentThreadId();

    [DllImport("kernel32", EntryPoint = "GetCurrentProcessId")]
    private static extern uint sys_GetCurrentProcessId();

    [DllImport("kernel32", EntryPoint = "GetCurrentProcessorNumberEx")]
    private static extern void sys_GetCurrentProcessorNumberEx(PROCESSOR_NUMBER* ProcNumber);

    [DllImport("kernel32", EntryPoint = "GetCurrentThread")]
    private static extern void* sys_GetCurrentThread();

    [DllImport("kernel32", EntryPoint = "SetThreadIdealProcessorEx")]
    private static extern int sys_SetThreadIdealProcessorEx(void* hThread, PROCESSOR_NUMBER* lpIdealProcessor, PROCESSOR_NUMBER* lpPreviousIdealProcessor);

    [DllImport("kernel32", EntryPoint = "GetThreadIdealProcessorEx")]
    private static extern int sys_GetThreadIdealProcessorEx(void* hThread, PROCESSOR_NUMBER* lpIdealProcessor);

    [DllImport("kernel32", EntryPoint = "SetThreadGroupAffinity")]
    private static extern int sys_SetThreadGroupAffinity(void* hThread, GROUP_AFFINITY* GroupAffinity, GROUP_AFFINITY* PreviousGroupAffinity);

    [DllImport("kernel32", EntryPoint = "SetThreadAffinityMask")]
    private static extern nuint sys_SetThreadAffinityMask(void* hThread, nuint dwThreadAffinityMask);

    [DllImport("kernel32", EntryPoint = "SetThreadPriority")]
    private static extern int sys_SetThreadPriority(void* hThread, int nPriority);

    [DllImport("kernel32", EntryPoint = "GetNumaNodeProcessorMaskEx")]
    private static extern int sys_GetNumaNodeProcessorMaskEx(ushort Node, GROUP_AFFINITY* ProcessorMask);

    [DllImport("kernel32", EntryPoint = "GetNumaProcessorNodeEx")]
    private static extern int sys_GetNumaProcessorNodeEx(PROCESSOR_NUMBER* Processor, ushort* NodeNumber);
}
