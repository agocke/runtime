// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The Win32 entry points that the Windows virtual memory, write watch, thread and memory limit
// ports of GCToOSInterface call, declared as <windows.h> and <psapi.h> declare them, except that
// every BOOL is spelled as int: a Win32 BOOL is four bytes wide and a managed bool is one, and
// there is no marshalling here to convert between them.
//
// They are [RuntimeImport]s rather than [DllImport]s: a runtime import is a direct call to a
// linked symbol with no marshalling, no argument copying, no lazy binding step and no GC mode
// transition, which is what code that runs with the world suspended requires. kernel32.lib and
// advapi32.lib are on the default link line of every NativeAOT application
// (Microsoft.NETCore.Native.Windows.targets), so each of these resolves at link time.
//
// This file is compiled into the shipping library only. The xUnit tests compile
// tests/GCToOSInterface.Imports.Windows.TestHost.cs in its place, which declares the same
// methods as ordinary P/Invokes so that the ported logic above them can be exercised, and
// records their arguments so that the flag translation can be asserted.

using System.Runtime;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe partial class GCToOSInterface
    {
        [RuntimeImport(RuntimeLibrary, "VirtualAlloc")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void* VirtualAlloc(void* lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

        [RuntimeImport(RuntimeLibrary, "VirtualAllocExNuma")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void* VirtualAllocExNuma(void* hProcess, void* lpAddress, nuint dwSize, uint flAllocationType, uint flProtect, uint nndPreferred);

        [RuntimeImport(RuntimeLibrary, "VirtualFree")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int VirtualFree(void* lpAddress, nuint dwSize, uint dwFreeType);

        [RuntimeImport(RuntimeLibrary, "VirtualUnlock")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int VirtualUnlock(void* lpAddress, nuint dwSize);

        [RuntimeImport(RuntimeLibrary, "GetLargePageMinimum")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern nuint GetLargePageMinimum();

        // ResetWriteWatch and GetWriteWatch return a UINT that is zero on success, not a BOOL.
        // Their managed names are prefixed because GCToOSInterface has methods of its own with
        // the Win32 names; [RuntimeImport] names the symbol, so the two need not agree.

        [RuntimeImport(RuntimeLibrary, "ResetWriteWatch")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern uint Win32ResetWriteWatch(void* lpBaseAddress, nuint dwRegionSize);

        [RuntimeImport(RuntimeLibrary, "GetWriteWatch")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern uint Win32GetWriteWatch(uint dwFlags, void* lpBaseAddress, nuint dwRegionSize, void** lpAddresses, nuint* lpdwCount, uint* lpdwGranularity);

        [RuntimeImport(RuntimeLibrary, "GetSystemInfo")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void GetSystemInfo(SYSTEM_INFO* lpSystemInfo);

        [RuntimeImport(RuntimeLibrary, "GlobalMemoryStatusEx")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int GlobalMemoryStatusEx(MEMORYSTATUSEX* lpBuffer);

        [RuntimeImport(RuntimeLibrary, "GetCurrentProcess")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void* GetCurrentProcess();

        [RuntimeImport(RuntimeLibrary, "GetLastError")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern uint GetLastError();

        [RuntimeImport(RuntimeLibrary, "CloseHandle")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int CloseHandle(void* hObject);

        [RuntimeImport(RuntimeLibrary, "OpenProcessToken")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int OpenProcessToken(void* ProcessHandle, uint DesiredAccess, void** TokenHandle);

        [RuntimeImport(RuntimeLibrary, "LookupPrivilegeValueW")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int LookupPrivilegeValueW(char* lpSystemName, char* lpName, LUID* lpLuid);

        [RuntimeImport(RuntimeLibrary, "AdjustTokenPrivileges")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int AdjustTokenPrivileges(void* TokenHandle, int DisableAllPrivileges, TOKEN_PRIVILEGES* NewState, uint BufferLength, TOKEN_PRIVILEGES* PreviousState, uint* ReturnLength);

        /// <summary>
        /// <c>SleepEx</c> of <c>&lt;windows.h&gt;</c>. Its <c>DWORD</c> return -- zero, or
        /// <c>WAIT_IO_COMPLETION</c> for an alertable sleep ended by an APC -- is discarded by
        /// the caller, as it is by the C++.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "SleepEx")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern uint SleepEx(uint dwMilliseconds, int bAlertable);

        /// <summary><c>SwitchToThread</c> of <c>&lt;windows.h&gt;</c>.</summary>
        [RuntimeImport(RuntimeLibrary, "SwitchToThread")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int SwitchToThread();

        // QueryPerformanceCounter and QueryPerformanceFrequency of <windows.h>. Their managed
        // names are prefixed because GCToOSInterface has methods of its own with the Win32
        // names; [RuntimeImport] names the symbol, so the two need not agree. The prefix is
        // spelling only: these are not overloads of the parameterless methods in
        // GCToOSInterface.Timers.Windows.cs that call them. The LARGE_INTEGER
        // out parameter is spelled as the long that its QuadPart is: a LARGE_INTEGER is a union
        // of that field with a two-DWORD struct, so it is eight bytes with QuadPart at offset
        // zero, and the C++ reads nothing but QuadPart.

        [RuntimeImport(RuntimeLibrary, "QueryPerformanceCounter")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int Win32QueryPerformanceCounter(long* lpPerformanceCount);

        [RuntimeImport(RuntimeLibrary, "QueryPerformanceFrequency")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int Win32QueryPerformanceFrequency(long* lpFrequency);

        /// <summary>
        /// <c>QueryUnbiasedInterruptTime</c> of <c>&lt;windows.h&gt;</c>, which counts 100ns
        /// intervals since boot without the time the system spent asleep.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "QueryUnbiasedInterruptTime")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int QueryUnbiasedInterruptTime(ulong* UnbiasedTime);

        [RuntimeImport(RuntimeLibrary, "IsProcessInJob")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int IsProcessInJob(void* ProcessHandle, void* JobHandle, int* Result);

        [RuntimeImport(RuntimeLibrary, "QueryInformationJobObject")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int QueryInformationJobObject(void* hJob, int JobObjectInformationClass, void* lpJobObjectInformation, uint cbJobObjectInformationLength, uint* lpReturnLength);

        [RuntimeImport(RuntimeLibrary, "GetLogicalProcessorInformation")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int GetLogicalProcessorInformation(SYSTEM_LOGICAL_PROCESSOR_INFORMATION* Buffer, uint* ReturnedLength);

        /// <summary>
        /// <c>GetProcessMemoryInfo</c> of <c>&lt;psapi.h&gt;</c>. The C++ calls it under that
        /// name, which every psapi.h since PSAPI_VERSION 2 defines to be this symbol; the
        /// forwarding entry point is the one kernel32 exports, and kernel32.lib is on the
        /// default NativeAOT link line where psapi.lib is not. gcenv.managed.cpp checks that
        /// the header still redirects the one name to the other.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "K32GetProcessMemoryInfo")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int GetProcessMemoryInfo(void* Process, PROCESS_MEMORY_COUNTERS* ppsmemCounters, uint cb);

        /// <summary>
        /// Stands in for the <c>new (nothrow) SYSTEM_LOGICAL_PROCESSOR_INFORMATION[]</c> of
        /// <c>GetLPI</c>. See <c>nativeaot/Runtime/gcenv.managed.cpp</c>.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_AllocZeroed")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void* ManagedGC_AllocZeroed(nuint size);

        /// <summary>Stands in for <c>delete[]</c>.</summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_Free")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_Free(void* memory);
    }
}
