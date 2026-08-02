// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Test-host substitute for src/.../Environment/GCToOSInterface.Imports.Windows.cs.
//
// The shipping declarations are [RuntimeImport]s, which only resolve inside a NativeAOT image,
// so this file declares the same private methods as ordinary P/Invokes. That makes the ported
// bodies above them -- the flag combinations, the NUMA alternatives, the large page rounding,
// the failure paths, the sleep and the yield -- runnable in a normal test process against the
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

using System.Runtime.InteropServices;

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
    private static extern int GlobalMemoryStatusEx(MEMORYSTATUSEX* lpBuffer);

    [DllImport("kernel32", EntryPoint = "GetCurrentProcess")]
    private static extern void* GetCurrentProcess();

    [DllImport("kernel32", EntryPoint = "GetLastError")]
    private static extern uint GetLastError();

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
}
