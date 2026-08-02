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
//     GCToOSInterface.Timers.Unix.cs and GCToOSInterface.Timers.Windows.cs, and the processor
//     counts and identity, in GCToOSInterface.Processors.Unix.cs and
//     GCToOSInterface.Processors.Windows.cs. Their declarations
//     stay here as comments pointing at the platform file, so that this file still reads in
//     gcenv.os.h declaration order.
//   * The remaining bodies are still forwarders. Each one is a [RuntimeImport] call to a
//     one-line shim in nativeaot/Runtime/gcenv.managed.cpp, which calls the C++
//     GCToOSInterface. A runtime import is a direct call to a linked symbol with no marshalling
//     and no GC mode transition, which is what code that runs with the world suspended
//     requires; a [DllImport] would not be usable here.
//
// The only forwarders left are Initialize/Shutdown/DebugBreak. Deletion point: plan step 3 of
// ROADMAP.md; a forwarder and its shim disappear together when the managed implementation of that
// method lands.
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
        // GCToOSInterface.Thread.Unix.cs and GCToOSInterface.Thread.Windows.cs. The rest of
        // the section -- identity, ideal processor, affinity and priority -- is translated per
        // platform in GCToOSInterface.Processors.Unix.cs and
        // GCToOSInterface.Processors.Windows.cs.

        //
        // Processor topology
        //

        // GetCacheSizePerLogicalCpu is translated per platform in
        // GCToOSInterface.MemoryLimits.Unix.cs and GCToOSInterface.MemoryLimits.Windows.cs.

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

        // GetTotalProcessorCount and GetMaxProcessorCount are translated per platform in
        // GCToOSInterface.Processors.Unix.cs and GCToOSInterface.Processors.Windows.cs, beside
        // the rest of the processor counts. Processor indices returned by
        // GetCurrentProcessorNumber are guaranteed to be less than GetMaxProcessorCount.

        // CanEnableGCNumaAware, GetNumaInfo, CanEnableGCCPUGroups, GetProcessorForHeap,
        // GetCPUGroupInfo and ParseGCHeapAffinitizeRangesEntry are translated per platform in
        // GCToOSInterface.Processors.Unix.cs and GCToOSInterface.Processors.Windows.cs.

        //
        // The native forwarders that remain. One per method above.
        //

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_Initialize")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_OS_Initialize();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_Shutdown")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_OS_Shutdown();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_OS_DebugBreak")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_OS_DebugBreak();
    }
}
