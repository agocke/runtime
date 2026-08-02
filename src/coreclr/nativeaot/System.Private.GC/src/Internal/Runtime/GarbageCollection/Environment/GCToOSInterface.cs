// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the GCToOSInterface class of gcenv.os.h: every service the GC gets from the operating
// system, in declaration order, with the C++ names, parameter names and defaults.
//
// The class is split by what the code actually does:
//
//   * The virtual memory and write watch methods are translated, in
//     GCToOSInterface.VirtualMemory.Unix.cs, GCToOSInterface.VirtualMemory.Windows.cs,
//     GCToOSInterface.WriteWatch.Unix.cs and GCToOSInterface.WriteWatch.Windows.cs, from
//     gc/unix/gcenv.unix.cpp and gc/windows/gcenv.windows.cpp, and so are Sleep and
//     YieldThread, in GCToOSInterface.Thread.Unix.cs and GCToOSInterface.Thread.Windows.cs,
//     and the memory limit and cache sizing methods, in GCToOSInterface.MemoryLimits.Unix.cs
//     and GCToOSInterface.MemoryLimits.Windows.cs, and the timers, in
//     GCToOSInterface.Timers.Unix.cs and GCToOSInterface.Timers.Windows.cs. Their declarations
//     stay here as comments pointing at the platform file, so that this file still reads in
//     gcenv.os.h declaration order.
//   * The remaining bodies are still forwarders. Each one is a [RuntimeImport] call to a
//     one-line shim in nativeaot/Runtime/gcenv.managed.cpp, which calls the C++
//     GCToOSInterface. A runtime import is a direct call to a linked symbol with no marshalling
//     and no GC mode transition, which is what code that runs with the world suspended
//     requires; a [DllImport] would not be usable here.
//
// They are forwarders because the implementations are the platform code -- NUMA, Windows CPU
// groups, pthread and Win32 affinity, processor counts -- and porting it is a separate
// piece of work per platform. Deletion point: plan step 3
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
        // Write watching -- SupportsWriteWatch, ResetWriteWatch and GetWriteWatch are translated
        // per platform in GCToOSInterface.WriteWatch.Unix.cs and
        // GCToOSInterface.WriteWatch.Windows.cs.
        //

        //
        // Thread and process. Sleep and YieldThread are translated per platform in
        // GCToOSInterface.Thread.Unix.cs and GCToOSInterface.Thread.Windows.cs; the rest of the
        // section is below.
        //

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

        // GetCacheSizePerLogicalCpu is translated per platform in
        // GCToOSInterface.MemoryLimits.Unix.cs and GCToOSInterface.MemoryLimits.Windows.cs.

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

        // GetPhysicalMemoryLimit and GetMemoryStatus are translated per platform in
        // GCToOSInterface.MemoryLimits.Unix.cs and GCToOSInterface.MemoryLimits.Windows.cs.

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

        // QueryPerformanceCounter, QueryPerformanceFrequency and GetLowPrecisionTimeStamp are
        // translated per platform in GCToOSInterface.Timers.Unix.cs and
        // GCToOSInterface.Timers.Windows.cs.

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

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_SetThreadAffinity")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_OS_SetThreadAffinity(ushort procNo);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_BoostThreadPriority")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_OS_BoostThreadPriority();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_SetGCThreadsAffinitySet")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void* ManagedGC_OS_SetGCThreadsAffinitySet(nuint configAffinityMask, void* configAffinitySet);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_DebugBreak")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_OS_DebugBreak();

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
