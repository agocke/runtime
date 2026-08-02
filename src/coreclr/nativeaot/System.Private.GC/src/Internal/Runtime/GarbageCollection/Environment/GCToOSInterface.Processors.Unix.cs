// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the affinity, NUMA and processor methods of gc/unix/gcenv.unix.cpp:
//   GCToOSInterface::GetCurrentThreadIdForLogging
//   GCToOSInterface::GetCurrentProcessId
//   GCToOSInterface::SetCurrentThreadIdealAffinity
//   GCToOSInterface::GetCurrentProcessorNumber
//   GCToOSInterface::CanGetCurrentProcessorNumber
//   GCToOSInterface::SetThreadAffinity
//   GCToOSInterface::BoostThreadPriority
//   GCToOSInterface::SetGCThreadsAffinitySet
//   GCToOSInterface::GetTotalProcessorCount
//   GCToOSInterface::GetMaxProcessorCount
//   GCToOSInterface::CanEnableGCNumaAware
//   GCToOSInterface::CanEnableGCCPUGroups
//   GCToOSInterface::GetProcessorForHeap
//   GCToOSInterface::ParseGCHeapAffinitizeRangesEntry
//
// plus the no-op / unsupported Unix arms for GetCurrentThreadIdealProc, GetNumaInfo and
// GetCPUGroupInfo, which are only implemented on Windows in the native code.
//
// The methods are in the order gcenv.unix.cpp defines them and keep the same statements.
//
// Two configure-time selections of the C++ have no managed spelling and are written out as
// platform lists instead, both checked against the native build by gcenv.managed.cpp:
// HAVE_SCHED_GETCPU and HAVE_SCHED_SETAFFINITY, which hold everywhere except Apple, FreeBSD and
// OpenBSD -- FreeBSD has HAVE_PTHREAD_SETAFFINITY_NP instead, which is the fallback arm the C++
// SetThreadAffinity keeps; and the `TARGET_LINUX && !TARGET_ANDROID` of the NUMA blocks, which
// is the HAVE_SCHED_GETCPU list minus Android. That one is TARGET_ANDROID rather than
// TARGET_BIONIC because it is keyed on the operating system: the linux-bionic RID is Linux to
// the native build, so the C++ compiles those blocks for it.

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe partial class GCToOSInterface
    {
        /// <summary>
        /// <c>CPU_ALLOC_SIZE</c> of <c>&lt;sched.h&gt;</c>: the number of bytes a cpu set of
        /// <paramref name="cpuCount"/> processors occupies, which is one pointer-sized word per
        /// <c>8 * sizeof(nuint)</c> processors, rounded up.
        /// </summary>
        private static nuint CpuSetSize(uint cpuCount)
        {
            nuint bitsPerBitsetEntry = (nuint)sizeof(nuint) * 8;
            return (((nuint)cpuCount + bitsPerBitsetEntry - 1) / bitsPerBitsetEntry) * (nuint)sizeof(nuint);
        }

        //
        // Thread and process
        //

        /// <summary>
        /// Get numeric id of the current thread if possible on the current platform. It is
        /// intended for logging purposes only.
        /// </summary>
        /// <returns>Numeric id of the current thread, as best we can retrieve it.</returns>
        public static ulong GetCurrentThreadIdForLogging()
        {
            return (ulong)ManagedGC_Unix_GetCurrentThreadId();
        }

        /// <summary>Get the process ID of the process.</summary>
        public static uint GetCurrentProcessId()
        {
            return (uint)getpid();
        }

        /// <summary>
        /// Set ideal processor for the current thread.
        /// </summary>
        /// <remarks>
        /// There is no way to set a thread ideal processor on Unix, so this is a no-op that
        /// succeeds.
        /// </remarks>
#pragma warning disable IDE0060
        public static bool SetCurrentThreadIdealAffinity(ushort srcProcNo, ushort dstProcNo)
        {
            return true;
        }
#pragma warning restore IDE0060

        /// <summary>Get the number of the current processor.</summary>
        public static uint GetCurrentProcessorNumber()
        {
#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD // HAVE_SCHED_GETCPU
            int processorNumber = sched_getcpu();
            Debug.Assert(processorNumber != -1);
            return (uint)processorNumber;
#else
            // This method is expected to be called only if CanGetCurrentProcessorNumber is true
            Debug.Assert(false);
            return 0;
#endif
        }

        /// <summary>Check if the OS supports getting current processor number.</summary>
        public static bool CanGetCurrentProcessorNumber()
        {
            // return HAVE_SCHED_GETCPU;
#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD
            return true;
#else
            return false;
#endif
        }

        /// <summary>
        /// Get the ideal processor of the current thread.
        /// </summary>
        /// <remarks>
        /// Unix has no corresponding API.
        /// </remarks>
#pragma warning disable IDE0060
        public static bool GetCurrentThreadIdealProc(ushort* procNo)
        {
            return false;
        }
#pragma warning restore IDE0060

        //
        // Processor topology
        //

        /// <summary>
        /// Sets the calling thread's affinity to only run on the processor specified.
        /// </summary>
        public static bool SetThreadAffinity(ushort procNo)
        {
#if !TARGET_APPLE && !TARGET_OPENBSD // HAVE_SCHED_SETAFFINITY || HAVE_PTHREAD_SETAFFINITY_NP
            uint configuredCpuCount = ManagedGC_Unix_GetConfiguredCpuCount();
            nuint cpuSetSize = CpuSetSize(configuredCpuCount);
            nuint* pCpuSet = (nuint*)ManagedGC_AllocZeroed(cpuSetSize);
            if (pCpuSet == null)
            {
                return false;
            }

            nuint bitsPerBitsetEntry = (nuint)sizeof(nuint) * 8;
            nuint bitsetEntryIndex = procNo / bitsPerBitsetEntry;
            // CPU_SET_S is bounds checked: a processor number past the end of the set is
            // ignored rather than written, on every C library that has it.
            if (bitsetEntryIndex < cpuSetSize / (nuint)sizeof(nuint))
            {
                nuint bitsetEntryBit = procNo & (bitsPerBitsetEntry - 1);
                pCpuSet[bitsetEntryIndex] = (nuint)1 << (int)bitsetEntryBit;
            }

            // Snap's default strict confinement does not allow sched_setaffinity(<nonzeroPid>, ...) without manually
            // connecting the process-control plug. sched_setaffinity(<currentThreadPid>, ...) is also currently not
            // allowed, only sched_setaffinity(0, ...). pthread_setaffinity_np(pthread_self(), ...) seems to call
            // sched_setaffinity(<currentThreadPid>, ...) in at least one implementation, and does not work. To work
            // around those issues, use sched_setaffinity(0, ...) if available and only otherwise fall back to
            // pthread_setaffinity_np(). See the following for more information:
            // - https://github.com/dotnet/runtime/pull/38795
            // - https://github.com/dotnet/runtime/issues/1634
            // - https://forum.snapcraft.io/t/requesting-autoconnect-for-interfaces-in-pigmeat-process-control-home/17987/13
#if !TARGET_FREEBSD // HAVE_SCHED_SETAFFINITY
            int st = sched_setaffinity(0, cpuSetSize, pCpuSet);
#else
            int st = pthread_setaffinity_np(pthread_self(), cpuSetSize, pCpuSet);
#endif

            ManagedGC_Free(pCpuSet);

            return st == 0;
#else
            // There is no API to manage thread affinity, so let's ignore the request
            return false;
#endif
        }

        /// <summary>
        /// Boosts the calling thread's thread priority to a level higher than the default for
        /// new threads.
        /// </summary>
        public static bool BoostThreadPriority()
        {
            // [LOCALGC TODO] Thread priority for unix
            return false;
        }

        /// <summary>
        /// Set the set of processors enabled for GC threads for the current process based on
        /// config specified affinity mask and set.
        /// </summary>
#pragma warning disable IDE0060
        public static AffinitySet* SetGCThreadsAffinitySet(nuint configAffinityMask, AffinitySet* configAffinitySet)
        {
            AffinitySet* processAffinitySet = ManagedGC_Unix_GetProcessAffinitySet();
            if (!configAffinitySet->IsEmpty())
            {
                // Update the process affinity set using the configured set
                uint totalCpuCount = ManagedGC_Unix_GetTotalCpuCount();
                for (nuint i = 0; i < totalCpuCount; i++)
                {
                    if (processAffinitySet->Contains(i) && !configAffinitySet->Contains(i))
                    {
                        processAffinitySet->Remove(i);
                    }
                }
            }

            return processAffinitySet;
        }
#pragma warning restore IDE0060

        //
        // Processor counts
        //

        /// <summary>
        /// Gets the total number of processors on the machine, not taking into account current
        /// process affinity.
        /// </summary>
        /// <returns>Number of processors on the machine.</returns>
        public static uint GetTotalProcessorCount()
        {
            // Calculated in GCToOSInterface::Initialize using
            // sysconf(_SC_NPROCESSORS_ONLN)
            return ManagedGC_Unix_GetTotalCpuCount();
        }

        /// <summary>
        /// Gets the maximum number of processors that could potentially exist on the machine
        /// (including offlined ones).
        /// </summary>
        public static uint GetMaxProcessorCount()
        {
            return (uint)ManagedGC_Unix_GetProcessAffinitySet()->MaxCpuCount();
        }

        /// <summary>Is NUMA support available.</summary>
        public static bool CanEnableGCNumaAware()
        {
            return ManagedGC_Unix_GetNumaAvailable() != 0;
        }

        /// <summary>For no NUMA this returns false.</summary>
#pragma warning disable IDE0060
        public static bool GetNumaInfo(ushort* total_nodes, uint* max_procs_per_node)
        {
            return false;
        }
#pragma warning restore IDE0060

        /// <summary>
        /// Is CPU Group enabled. This only applies on Windows and is only used by
        /// instrumentation, but is on the interface due to LocalGC.
        /// </summary>
        public static bool CanEnableGCCPUGroups()
        {
            return false;
        }

        /// <summary>For no CPU groups this returns false.</summary>
#pragma warning disable IDE0060
        public static bool GetCPUGroupInfo(ushort* total_groups, uint* max_procs_per_group)
        {
            return false;
        }
#pragma warning restore IDE0060

        /// <summary>
        /// Get the processor number and optionally its NUMA node number for the specified heap
        /// number.
        /// </summary>
        public static bool GetProcessorForHeap(ushort heap_number, ushort* proc_no, ushort* node_no)
        {
            bool success = false;

            ushort availableProcNumber = 0;
            AffinitySet* processAffinitySet = ManagedGC_Unix_GetProcessAffinitySet();
            nuint maxCpuCount = processAffinitySet->MaxCpuCount();
            for (nuint procNumber = 0; procNumber < maxCpuCount; procNumber++)
            {
                if (processAffinitySet->Contains(procNumber))
                {
                    if (availableProcNumber == heap_number)
                    {
                        *proc_no = (ushort)procNumber;
#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD && !TARGET_ANDROID
                        if (CanEnableGCNumaAware())
                        {
                            int result = GetNumaNodeNumByCpu((int)procNumber);
                            *node_no = result >= 0 ? (ushort)result : NUMA_NODE_UNDEFINED;
                        }
                        else
#endif
                        {
                            *node_no = NUMA_NODE_UNDEFINED;
                        }

                        success = true;
                        break;
                    }

                    availableProcNumber++;
                }
            }

            return success;
        }

        private static void BindMemoryPolicyForNuma(void* address, nuint size, ushort node)
        {
#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD && !TARGET_ANDROID
            if (CanEnableGCNumaAware())
            {
                int highestNumaNode = ManagedGC_Unix_GetHighestNumaNode();
                if ((int)node <= highestNumaNode)
                {
                    int usedNodeMaskBits = highestNumaNode + 1;
                    int nodeMaskLength = usedNodeMaskBits + sizeof(nuint) - 1;
                    byte* nodeMaskBytes = stackalloc byte[nodeMaskLength];
                    for (int i = 0; i < nodeMaskLength; i++)
                    {
                        nodeMaskBytes[i] = 0;
                    }

                    nuint* nodeMask = (nuint*)nodeMaskBytes;

                    int index = node / sizeof(nuint);
                    nodeMask[index] = (nuint)1 << (int)(node & (sizeof(nuint) - 1));

                    int st = (int)BindMemoryPolicy(address, size, nodeMask, (nuint)usedNodeMaskBits);
                    Debug.Assert(st == 0);
                    // If the mbind fails, we still return the allocated memory since the node is
                    // just a hint.
                }
            }
#endif
        }

        /// <summary>
        /// Parse the config string describing affinitization ranges and update the passed in
        /// indices accordingly. Returns true if the config string was successfully parsed.
        /// </summary>
        public static bool ParseGCHeapAffinitizeRangesEntry(byte** config_string, nuint* start_index, nuint* end_index)
        {
            return GCEnv.ParseIndexOrRange(config_string, start_index, end_index);
        }
    }
}
