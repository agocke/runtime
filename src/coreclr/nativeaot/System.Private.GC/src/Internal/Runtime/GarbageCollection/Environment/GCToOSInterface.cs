// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the GCToOSInterface class of gcenv.os.h: every service the GC gets from the operating
// system, in declaration order, with the C++ names, parameter names and defaults.
//
// The class is split by what the code actually does:
//
//   * The virtual memory methods are translated, in GCToOSInterface.VirtualMemory.Unix.cs and
//     GCToOSInterface.VirtualMemory.Windows.cs, from gc/unix/gcenv.unix.cpp and
//     gc/windows/gcenv.windows.cpp. Their declarations stay here as comments pointing at the
//     platform file, so that this file still reads in gcenv.os.h declaration order.
//   * The remaining bodies are still forwarders. Each one is a [RuntimeImport] call to a
//     one-line shim in nativeaot/Runtime/gcenv.managed.cpp, which calls the C++
//     GCToOSInterface. A runtime import is a direct call to a linked symbol with no marshalling
//     and no GC mode transition, which is what code that runs with the world suspended
//     requires; a [DllImport] would not be usable here.
//
// They are forwarders because the implementations are the platform code -- cgroup and
// job-object limits, NUMA, Windows CPU groups, pthread and Win32 affinity, the high-resolution
// clock -- and porting it is a separate piece of work per platform. Deletion point: plan step 3
// of ROADMAP.md; a forwarder and its shim disappear together when the managed implementation of
// that method lands.
//
// Two more members of gcenv.os.h are ported rather than forwarded: AffinitySet, which is pure
// bit manipulation, and ParseIndexOrRange, which is pure parsing. They live in AffinitySet.cs
// and GCEnv.Base.cs.

using System;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>Flags for the <see cref="GCToOSInterface.VirtualReserve"/> method.</summary>
    internal enum VirtualReserveFlags
    {
        None = 0,
        WriteWatch = 1,
    }

    /// <summary>
    /// Interface that the GC uses to invoke OS specific functionality.
    /// </summary>
    internal static unsafe partial class GCToOSInterface
    {
        private const string RuntimeLibrary = "*";

        /// <summary><c>NUMA_NODE_UNDEFINED</c>.</summary>
        public const ushort NUMA_NODE_UNDEFINED = ushort.MaxValue;

        /// <summary>
        /// Right now we support maximum 1024 heaps - meaning that we will create at most
        /// that many GC threads and GC heaps.
        /// </summary>
#if TARGET_64BIT
        public const int MAX_SUPPORTED_HEAPS = 1024;

        /// <summary>The maximum number of NUMA nodes the GC supports.</summary>
        public const int MAX_SUPPORTED_NODES = 64;
#else
        public const int MAX_SUPPORTED_HEAPS = 64;

        /// <summary>The maximum number of NUMA nodes the GC supports.</summary>
        public const int MAX_SUPPORTED_NODES = 16;
#endif

        //
        // Initialization and shutdown of the interface
        //

        /// <summary>
        /// Initialize the interface implementation. Returns true if it has succeeded.
        /// </summary>
        /// <remarks>
        /// NativeAOT initializes the C++ <c>GCToOSInterface</c> from <c>PalInit</c>, before any
        /// managed code runs, so the managed GC never calls this. It is declared for source
        /// correspondence and so that a future managed implementation has the slot.
        /// </remarks>
        public static bool Initialize() => ManagedGC_OS_Initialize() != 0;

        /// <summary>Shutdown the interface implementation.</summary>
        public static void Shutdown() => ManagedGC_OS_Shutdown();

        //
        // Virtual memory management -- VirtualReserve, VirtualRelease, VirtualCommit,
        // VirtualReserveAndCommitLargePages, VirtualDecommit and VirtualReset are translated per
        // platform in GCToOSInterface.VirtualMemory.Unix.cs and
        // GCToOSInterface.VirtualMemory.Windows.cs.
        //

        //
        // Write watching
        //

        /// <summary>Check if the OS supports write watching.</summary>
        public static bool SupportsWriteWatch() => ManagedGC_OS_SupportsWriteWatch() != 0;

        /// <summary>Reset the write tracking state for the specified virtual memory range.</summary>
        public static void ResetWriteWatch(void* address, nuint size) => ManagedGC_OS_ResetWriteWatch(address, size);

        /// <summary>
        /// Retrieve addresses of the pages that are written to in a region of virtual memory.
        /// </summary>
        public static bool GetWriteWatch(bool resetState, void* address, nuint size, void** pageAddresses, nuint* pageAddressesCount) =>
            ManagedGC_OS_GetWriteWatch(resetState ? 1 : 0, address, size, pageAddresses, pageAddressesCount) != 0;

        //
        // Thread and process
        //

        /// <summary>
        /// Causes the calling thread to sleep for the specified number of milliseconds.
        /// </summary>
        public static void Sleep(uint sleepMSec) => ManagedGC_OS_Sleep(sleepMSec);

        /// <summary>
        /// Causes the calling thread to yield execution to another thread that is ready to run
        /// on the current processor.
        /// </summary>
        /// <param name="switchCount">number of times the YieldThread was called in a loop</param>
        public static void YieldThread(uint switchCount) => ManagedGC_OS_YieldThread(switchCount);

        /// <summary>Get the number of the current processor.</summary>
        public static uint GetCurrentProcessorNumber() => ManagedGC_OS_GetCurrentProcessorNumber();

        /// <summary>Check if the OS supports getting the current processor number.</summary>
        public static bool CanGetCurrentProcessorNumber() => ManagedGC_OS_CanGetCurrentProcessorNumber() != 0;

        /// <summary>Set the ideal processor for the current thread.</summary>
        public static bool SetCurrentThreadIdealAffinity(ushort srcProcNo, ushort dstProcNo) =>
            ManagedGC_OS_SetCurrentThreadIdealAffinity(srcProcNo, dstProcNo) != 0;

        /// <summary>Get the ideal processor of the current thread.</summary>
        public static bool GetCurrentThreadIdealProc(ushort* procNo) =>
            ManagedGC_OS_GetCurrentThreadIdealProc(procNo) != 0;

        /// <summary>
        /// Get the numeric id of the current thread. It is intended for logging purposes only.
        /// </summary>
        public static ulong GetCurrentThreadIdForLogging() => ManagedGC_OS_GetCurrentThreadIdForLogging();

        /// <summary>Get the id of the current process.</summary>
        public static uint GetCurrentProcessId() => ManagedGC_OS_GetCurrentProcessId();

        //
        // Processor topology
        //

        /// <summary>Get the size of the on-die cache per logical processor.</summary>
        /// <param name="trueSize">
        /// true to return the true cache size, false to return a size scaled up based on the
        /// processor architecture
        /// </param>
        public static nuint GetCacheSizePerLogicalCpu(bool trueSize = true) =>
            ManagedGC_OS_GetCacheSizePerLogicalCpu(trueSize ? 1 : 0);

        /// <summary>
        /// Sets the calling thread's affinity to only run on the processor specified.
        /// </summary>
        public static bool SetThreadAffinity(ushort procNo) => ManagedGC_OS_SetThreadAffinity(procNo) != 0;

        /// <summary>
        /// Boosts the calling thread's thread priority to a level higher than the default for
        /// new threads.
        /// </summary>
        public static bool BoostThreadPriority() => ManagedGC_OS_BoostThreadPriority() != 0;

        /// <summary>
        /// Set the set of processors enabled for GC threads for the current process based on the
        /// config specified affinity mask and set, and return the set of enabled processors.
        /// </summary>
        /// <remarks>
        /// The returned set is owned by the platform layer and lives for the lifetime of the
        /// process, exactly as the C++ <c>g_processAffinitySet</c> does. The managed
        /// <see cref="AffinitySet"/> has the same layout as the C++ one, so the pointer that
        /// comes back can be read directly.
        /// </remarks>
        public static AffinitySet* SetGCThreadsAffinitySet(nuint configAffinityMask, AffinitySet* configAffinitySet) =>
            (AffinitySet*)ManagedGC_OS_SetGCThreadsAffinitySet(configAffinityMask, configAffinitySet);

        //
        // Global memory info
        //

        // GetVirtualMemoryLimit and GetVirtualMemoryMaxAddress are translated per platform in
        // GCToOSInterface.VirtualMemory.Unix.cs and GCToOSInterface.VirtualMemory.Windows.cs.

        /// <summary>
        /// Get the physical memory that this process can use. If a process runs with a restricted
        /// memory limit, it returns the limit. If there's no limit specified, it returns the
        /// amount of actual physical memory.
        /// </summary>
        /// <param name="is_restricted">
        /// If not null, set to a non-zero value when running restricted. This is the C++
        /// <c>bool*</c>, which is one byte wide.
        /// </param>
        public static ulong GetPhysicalMemoryLimit(byte* is_restricted = null) =>
            ManagedGC_OS_GetPhysicalMemoryLimit(is_restricted);

        /// <summary>Get memory status. Any parameter can be null.</summary>
        /// <param name="restricted_limit">
        /// The amount of physical memory in bytes that the current process is being restricted
        /// to. If non-zero, it is used to calculate <paramref name="memory_load"/> and
        /// <paramref name="available_physical"/>. If zero, they are calculated based on all
        /// available memory.
        /// </param>
        /// <param name="memory_load">
        /// A number between 0 and 100 that specifies the approximate percentage of physical
        /// memory that is in use.
        /// </param>
        /// <param name="available_physical">The amount of physical memory currently available, in bytes.</param>
        /// <param name="available_page_file">The maximum amount of memory the current process can commit, in bytes.</param>
        public static void GetMemoryStatus(ulong restricted_limit, uint* memory_load, ulong* available_physical, ulong* available_page_file) =>
            ManagedGC_OS_GetMemoryStatus(restricted_limit, memory_load, available_physical, available_page_file);

        // GetPageSize is translated per platform in GCToOSInterface.VirtualMemory.Unix.cs and
        // GCToOSInterface.VirtualMemory.Windows.cs, next to the OS_PAGE_SIZE macro of
        // env/gcenv.unix.inl and env/gcenv.windows.inl that it backs.

        //
        // Misc
        //

        /// <summary>Break into a debugger.</summary>
        public static void DebugBreak() => ManagedGC_OS_DebugBreak();

        //
        // Time
        //

        /// <summary>Get a high precision performance counter.</summary>
        public static long QueryPerformanceCounter() => ManagedGC_OS_QueryPerformanceCounter();

        /// <summary>Get the frequency of the high precision performance counter.</summary>
        public static long QueryPerformanceFrequency() => ManagedGC_OS_QueryPerformanceFrequency();

        /// <summary>Get a time stamp with a low precision, in milliseconds.</summary>
        public static ulong GetLowPrecisionTimeStamp() => ManagedGC_OS_GetLowPrecisionTimeStamp();

        /// <summary>
        /// Gets the total number of processors on the machine, not taking into account the
        /// current process affinity.
        /// </summary>
        public static uint GetTotalProcessorCount() => ManagedGC_OS_GetTotalProcessorCount();

        /// <summary>
        /// Gets the maximum number of processors that could potentially exist on the machine
        /// (including offlined ones). Processor indices returned by
        /// <see cref="GetCurrentProcessorNumber"/> are guaranteed to be less than this value.
        /// </summary>
        public static uint GetMaxProcessorCount() => ManagedGC_OS_GetMaxProcessorCount();

        /// <summary>Is NUMA support available.</summary>
        public static bool CanEnableGCNumaAware() => ManagedGC_OS_CanEnableGCNumaAware() != 0;

        /// <summary>For no NUMA this returns false.</summary>
        public static bool GetNumaInfo(ushort* total_nodes, uint* max_procs_per_node) =>
            ManagedGC_OS_GetNumaInfo(total_nodes, max_procs_per_node) != 0;

        /// <summary>
        /// Is CPU Group enabled. This only applies on Windows and is only used by
        /// instrumentation, but is on the interface due to LocalGC.
        /// </summary>
        public static bool CanEnableGCCPUGroups() => ManagedGC_OS_CanEnableGCCPUGroups() != 0;

        /// <summary>
        /// Get the processor number and optionally its NUMA node number for the specified heap
        /// number.
        /// </summary>
        public static bool GetProcessorForHeap(ushort heap_number, ushort* proc_no, ushort* node_no) =>
            ManagedGC_OS_GetProcessorForHeap(heap_number, proc_no, node_no) != 0;

        /// <summary>For no CPU groups this returns false.</summary>
        public static bool GetCPUGroupInfo(ushort* total_groups, uint* max_procs_per_group) =>
            ManagedGC_OS_GetCPUGroupInfo(total_groups, max_procs_per_group) != 0;

        /// <summary>
        /// Parse the config string describing affinitization ranges and update the passed in
        /// indices accordingly. Returns true if the config string was successfully parsed.
        /// </summary>
        /// <remarks>
        /// On Unix this is exactly <see cref="GCEnv.ParseIndexOrRange"/>, which is ported. It is
        /// still forwarded because the Windows implementation prefixes every entry with a CPU
        /// group and validates it against the group table that only <c>gcenv.windows.cpp</c>
        /// has.
        /// </remarks>
        public static bool ParseGCHeapAffinitizeRangesEntry(byte** config_string, nuint* start_index, nuint* end_index) =>
            ManagedGC_OS_ParseGCHeapAffinitizeRangesEntry(config_string, start_index, end_index) != 0;

        //
        // The native forwarders. One per method above, in the same order.
        //

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_Initialize")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_OS_Initialize();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_Shutdown")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_OS_Shutdown();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_SupportsWriteWatch")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_OS_SupportsWriteWatch();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_ResetWriteWatch")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_OS_ResetWriteWatch(void* address, nuint size);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_GetWriteWatch")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_OS_GetWriteWatch(int resetState, void* address, nuint size, void** pageAddresses, nuint* pageAddressesCount);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_Sleep")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_OS_Sleep(uint sleepMSec);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_YieldThread")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_OS_YieldThread(uint switchCount);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_GetCurrentProcessorNumber")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern uint ManagedGC_OS_GetCurrentProcessorNumber();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_CanGetCurrentProcessorNumber")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_OS_CanGetCurrentProcessorNumber();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_SetCurrentThreadIdealAffinity")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_OS_SetCurrentThreadIdealAffinity(ushort srcProcNo, ushort dstProcNo);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_GetCurrentThreadIdealProc")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_OS_GetCurrentThreadIdealProc(ushort* procNo);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_GetCurrentThreadIdForLogging")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern ulong ManagedGC_OS_GetCurrentThreadIdForLogging();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_GetCurrentProcessId")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern uint ManagedGC_OS_GetCurrentProcessId();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_GetCacheSizePerLogicalCpu")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern nuint ManagedGC_OS_GetCacheSizePerLogicalCpu(int trueSize);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_SetThreadAffinity")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_OS_SetThreadAffinity(ushort procNo);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_BoostThreadPriority")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_OS_BoostThreadPriority();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_SetGCThreadsAffinitySet")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void* ManagedGC_OS_SetGCThreadsAffinitySet(nuint configAffinityMask, void* configAffinitySet);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_GetPhysicalMemoryLimit")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern ulong ManagedGC_OS_GetPhysicalMemoryLimit(byte* is_restricted);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_GetMemoryStatus")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_OS_GetMemoryStatus(ulong restricted_limit, uint* memory_load, ulong* available_physical, ulong* available_page_file);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_DebugBreak")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_OS_DebugBreak();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_QueryPerformanceCounter")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern long ManagedGC_OS_QueryPerformanceCounter();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_QueryPerformanceFrequency")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern long ManagedGC_OS_QueryPerformanceFrequency();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_GetLowPrecisionTimeStamp")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern ulong ManagedGC_OS_GetLowPrecisionTimeStamp();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_GetTotalProcessorCount")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern uint ManagedGC_OS_GetTotalProcessorCount();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_GetMaxProcessorCount")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern uint ManagedGC_OS_GetMaxProcessorCount();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_CanEnableGCNumaAware")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_OS_CanEnableGCNumaAware();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_GetNumaInfo")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_OS_GetNumaInfo(ushort* total_nodes, uint* max_procs_per_node);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_CanEnableGCCPUGroups")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_OS_CanEnableGCCPUGroups();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_GetProcessorForHeap")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_OS_GetProcessorForHeap(ushort heap_number, ushort* proc_no, ushort* node_no);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_GetCPUGroupInfo")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_OS_GetCPUGroupInfo(ushort* total_groups, uint* max_procs_per_group);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_ParseGCHeapAffinitizeRangesEntry")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_OS_ParseGCHeapAffinitizeRangesEntry(byte** config_string, nuint* start_index, nuint* end_index);
    }
}
