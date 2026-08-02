// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Test-host substitute for src/.../Environment/GCToOSInterface.Imports.Windows.cs.
//
// The shipping declarations are [RuntimeImport]s, which only resolve inside a NativeAOT image,
// so this file declares the same private methods as ordinary P/Invokes. That makes the ported
// bodies above them -- the flag combinations, the NUMA alternatives, the large page rounding,
// the failure paths, the sleep, the yield and the timers -- runnable in a normal test process
// against the
// real kernel, and it
// records the arguments of every call so that the flag translation can be asserted directly
// rather than inferred.
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
}
