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

        [RuntimeImport(RuntimeLibrary, "GetCurrentThreadId")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern uint GetCurrentThreadId();

        [RuntimeImport(RuntimeLibrary, "GetCurrentProcessId")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern uint Win32GetCurrentProcessId();

        [RuntimeImport(RuntimeLibrary, "GetCurrentProcessorNumberEx")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void GetCurrentProcessorNumberEx(PROCESSOR_NUMBER* ProcNumber);

        //
        // The affinity, ideal-processor, priority and NUMA entry points of <windows.h> that
        // GCToOSInterface.Processors.Windows.cs calls, declared exactly as <windows.h> declares
        // them. A HANDLE is a void*, a BOOL is an int, and a DWORD_PTR is a nuint;
        // gcenv.managed.cpp checks each declaration against the real header.
        //

        /// <summary><c>GetCurrentThread</c> of <c>&lt;windows.h&gt;</c>, which returns the
        /// current thread's pseudo handle.</summary>
        [RuntimeImport(RuntimeLibrary, "GetCurrentThread")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void* GetCurrentThread();

        /// <summary><c>SetThreadIdealProcessorEx</c> of <c>&lt;windows.h&gt;</c>.</summary>
        [RuntimeImport(RuntimeLibrary, "SetThreadIdealProcessorEx")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int SetThreadIdealProcessorEx(void* hThread, PROCESSOR_NUMBER* lpIdealProcessor, PROCESSOR_NUMBER* lpPreviousIdealProcessor);

        /// <summary><c>GetThreadIdealProcessorEx</c> of <c>&lt;windows.h&gt;</c>.</summary>
        [RuntimeImport(RuntimeLibrary, "GetThreadIdealProcessorEx")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int GetThreadIdealProcessorEx(void* hThread, PROCESSOR_NUMBER* lpIdealProcessor);

        /// <summary><c>SetThreadGroupAffinity</c> of <c>&lt;windows.h&gt;</c>.</summary>
        [RuntimeImport(RuntimeLibrary, "SetThreadGroupAffinity")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int SetThreadGroupAffinity(void* hThread, GROUP_AFFINITY* GroupAffinity, GROUP_AFFINITY* PreviousGroupAffinity);

        /// <summary><c>SetThreadAffinityMask</c> of <c>&lt;windows.h&gt;</c>, whose
        /// <c>DWORD_PTR</c> return is the previous mask and zero on failure.</summary>
        [RuntimeImport(RuntimeLibrary, "SetThreadAffinityMask")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern nuint SetThreadAffinityMask(void* hThread, nuint dwThreadAffinityMask);

        /// <summary><c>SetThreadPriority</c> of <c>&lt;windows.h&gt;</c>.</summary>
        [RuntimeImport(RuntimeLibrary, "SetThreadPriority")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int SetThreadPriority(void* hThread, int nPriority);

        /// <summary><c>GetNumaNodeProcessorMaskEx</c> of <c>&lt;windows.h&gt;</c>.</summary>
        [RuntimeImport(RuntimeLibrary, "GetNumaNodeProcessorMaskEx")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int GetNumaNodeProcessorMaskEx(ushort Node, GROUP_AFFINITY* ProcessorMask);

        /// <summary><c>GetNumaProcessorNodeEx</c> of <c>&lt;windows.h&gt;</c>, which writes the
        /// node number straight into the <c>node_no</c> the GC passed in.</summary>
        [RuntimeImport(RuntimeLibrary, "GetNumaProcessorNodeEx")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int GetNumaProcessorNodeEx(PROCESSOR_NUMBER* Processor, ushort* NodeNumber);

        /// <summary>
        /// The address of <c>g_totalCpuCount</c> of <c>gc/windows/gcenv.windows.cpp</c>. The
        /// address rather than the value, because the C++ body of
        /// <c>GCToOSInterface::GetTotalProcessorCount</c> stays compiled -- gc/gcconfig.cpp and
        /// the NativeAOT PAL still call it -- so the managed body has to fill the same cache
        /// rather than one of its own.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_Windows_GetTotalCpuCount")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern uint* ManagedGC_Windows_GetTotalCpuCount();

        /// <summary>
        /// The value of <c>g_SystemInfo.dwNumberOfProcessors</c> of
        /// <c>gc/windows/gcenv.windows.cpp</c>, from the <c>SYSTEM_INFO</c> that the same
        /// Initialize caches.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_Windows_GetSystemInfoProcessorCount")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern uint ManagedGC_Windows_GetSystemInfoProcessorCount();

        /// <summary>
        /// The address of <c>g_processAffinitySet</c> of <c>gc/windows/gcenv.windows.cpp</c>.
        /// Only the address crosses: the counting is the ported
        /// <see cref="AffinitySet.MaxCpuCount"/>.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_Windows_GetProcessAffinitySet")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern AffinitySet* ManagedGC_Windows_GetProcessAffinitySet();

        /// <summary>
        /// The value of <c>g_fEnableGCNumaAware</c> of <c>gc/windows/gcenv.windows.cpp</c>,
        /// which <c>InitNumaNodeInfo</c> sets.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_Windows_GetCanEnableGCNumaAware")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_Windows_GetCanEnableGCNumaAware();

        /// <summary>
        /// The value of <c>g_nNodes</c> of <c>gc/windows/gcenv.windows.cpp</c>, the NUMA node
        /// count the same <c>InitNumaNodeInfo</c> computes.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_Windows_GetNumaNodeCount")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern uint ManagedGC_Windows_GetNumaNodeCount();

        /// <summary>
        /// The value of <c>g_fEnableGCCPUGroups</c> of <c>gc/windows/gcenv.windows.cpp</c>,
        /// which <c>InitCPUGroupInfo</c> sets.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_Windows_GetCanEnableGCCPUGroups")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_Windows_GetCanEnableGCCPUGroups();

        /// <summary>
        /// The value of <c>g_nGroups</c> of <c>gc/windows/gcenv.windows.cpp</c>. It is a
        /// <c>DWORD</c> there and a <c>ushort</c> here, which is the narrowing the C++
        /// <c>GetCPUGroupInfo</c> hands to its own caller and the width every loop over the
        /// group table uses.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_Windows_GetCpuGroupCount")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern ushort ManagedGC_Windows_GetCpuGroupCount();

        /// <summary>
        /// <c>g_CPUGroupInfoArray[groupNumber].nr_active</c> of
        /// <c>gc/windows/gcenv.windows.cpp</c>, the number of active processors in one CPU
        /// group.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_Windows_GetCpuGroupActiveProcessorCount")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern ushort ManagedGC_Windows_GetCpuGroupActiveProcessorCount(ushort groupNumber);

        /// <summary>
        /// <c>g_CPUGroupInfoArray[groupNumber].begin</c> of <c>gc/windows/gcenv.windows.cpp</c>,
        /// the first global processor index of one CPU group.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_Windows_GetCpuGroupBegin")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern ushort ManagedGC_Windows_GetCpuGroupBegin(ushort groupNumber);
    }
}
