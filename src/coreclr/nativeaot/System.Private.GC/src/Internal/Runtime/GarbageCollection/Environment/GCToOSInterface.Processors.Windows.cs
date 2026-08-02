// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the affinity, NUMA and processor methods of gc/windows/gcenv.windows.cpp:
//   GCToOSInterface::GetCurrentThreadIdForLogging
//   GCToOSInterface::GetCurrentProcessId
//   GCToOSInterface::SetCurrentThreadIdealAffinity
//   GCToOSInterface::GetCurrentThreadIdealProc
//   GCToOSInterface::GetCurrentProcessorNumber
//   GCToOSInterface::CanGetCurrentProcessorNumber
//   GCToOSInterface::SetThreadAffinity
//   GCToOSInterface::BoostThreadPriority
//   GCToOSInterface::SetGCThreadsAffinitySet
//   GCToOSInterface::GetTotalProcessorCount
//   GCToOSInterface::GetMaxProcessorCount
//   GCToOSInterface::CanEnableGCNumaAware
//   GCToOSInterface::GetNumaInfo
//   GCToOSInterface::CanEnableGCCPUGroups
//   GCToOSInterface::GetCPUGroupInfo
//   GCToOSInterface::GetProcessorForHeap
//   GCToOSInterface::ParseGCHeapAffinitizeRangesEntry
//
// The methods are in the order gcenv.windows.cpp defines them and keep the same statements.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe partial class GCToOSInterface
    {
        private const int THREAD_PRIORITY_HIGHEST = 2;

        /// <summary><c>PROCESSOR_NUMBER</c> of <c>&lt;windows.h&gt;</c>.</summary>
        internal struct PROCESSOR_NUMBER
        {
            public ushort Group;
            public byte Number;
            public byte Reserved;
        }

        /// <summary><c>GROUP_AFFINITY</c> of <c>&lt;windows.h&gt;</c>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct GROUP_AFFINITY
        {
            public nuint Mask;
            public ushort Group;
            public fixed ushort Reserved[3];
        }

        /// <summary>
        /// <c>GroupProcNo</c> of <c>gc/windows/gcenv.windows.cpp</c>: a processor number and the
        /// CPU group it belongs to, packed into the single 16 bit value the GC passes around.
        /// </summary>
        private readonly struct GroupProcNo
        {
            public const ushort NoGroup = 0;

            private readonly ushort m_groupProc;

            public GroupProcNo(ushort groupProc)
            {
                m_groupProc = groupProc;
            }

            public GroupProcNo(ushort group, ushort procIndex)
            {
                m_groupProc = (ushort)((group << 6) | procIndex);
                Debug.Assert(group <= 0x3ff);
                Debug.Assert(procIndex <= 0x3f);
            }

            public ushort GetGroup() => (ushort)(m_groupProc >> 6);
            public ushort GetProcIndex() => (ushort)(m_groupProc & 0x3f);
            public ushort GetCombinedValue() => m_groupProc;
        }

        private static void GetGroupForProcessor(ushort processor_number, ushort* group_number, ushort* group_processor_number)
        {
            Debug.Assert(CanEnableGCCPUGroups());

#if TARGET_AMD64 || TARGET_ARM64
            ushort bTemp = 0;
            ushort bDiff = (ushort)(processor_number - bTemp);

            ushort nGroups = ManagedGC_Windows_GetCpuGroupCount();
            for (ushort i = 0; i < nGroups; i++)
            {
                bTemp = (ushort)(bTemp + ManagedGC_Windows_GetCpuGroupActiveProcessorCount(i));
                if (bTemp > processor_number)
                {
                    *group_number = i;
                    *group_processor_number = bDiff;
                    break;
                }

                bDiff = (ushort)(processor_number - bTemp);
            }
#else
            *group_number = 0;
            *group_processor_number = 0;
#endif
        }

        //
        // Thread and process
        //

        /// <summary>
        /// Get numeric id of the current thread if possible on the current platform. It is
        /// intended for logging purposes only.
        /// </summary>
        public static ulong GetCurrentThreadIdForLogging()
        {
            return GetCurrentThreadId();
        }

        /// <summary>Get id of the process.</summary>
        public static uint GetCurrentProcessId()
        {
            return Win32GetCurrentProcessId();
        }

        /// <summary>
        /// Set ideal processor for the current thread.
        /// </summary>
        /// <param name="srcProcNo">processor number the thread currently runs on</param>
        /// <param name="dstProcNo">processor number the thread should be migrated to</param>
        /// <returns>true if it has succeeded, false if it has failed</returns>
        public static bool SetCurrentThreadIdealAffinity(ushort srcProcNo, ushort dstProcNo)
        {
            bool success = true;

            GroupProcNo srcGroupProcNo = new GroupProcNo(srcProcNo);
            GroupProcNo dstGroupProcNo = new GroupProcNo(dstProcNo);

            PROCESSOR_NUMBER proc;

            if (CanEnableGCCPUGroups())
            {
                if (srcGroupProcNo.GetGroup() != dstGroupProcNo.GetGroup())
                {
                    // only set ideal processor when srcProcNo and dstProcNo are in the same cpu
                    // group. DO NOT MOVE THREADS ACROSS CPU GROUPS
                    return true;
                }

                proc.Group = dstGroupProcNo.GetGroup();
                proc.Number = (byte)dstGroupProcNo.GetProcIndex();
                proc.Reserved = 0;

                success = SetThreadIdealProcessorEx(GetCurrentThread(), &proc, null) != 0;
            }
            else
            {
                if (GetThreadIdealProcessorEx(GetCurrentThread(), &proc) != 0)
                {
                    proc.Number = (byte)dstGroupProcNo.GetProcIndex();
                    success = SetThreadIdealProcessorEx(GetCurrentThread(), &proc, &proc) != 0;
                }
            }

            return success;
        }

        /// <summary>Get the ideal processor of the current thread.</summary>
        public static bool GetCurrentThreadIdealProc(ushort* procNo)
        {
            PROCESSOR_NUMBER proc;

            bool success = GetThreadIdealProcessorEx(GetCurrentThread(), &proc) != 0;

            if (success)
            {
                GroupProcNo groupProcNo = new GroupProcNo(proc.Group, proc.Number);
                *procNo = groupProcNo.GetCombinedValue();
            }

            return success;
        }

        /// <summary>Get the number of the current processor.</summary>
        public static uint GetCurrentProcessorNumber()
        {
            Debug.Assert(CanGetCurrentProcessorNumber());

            PROCESSOR_NUMBER proc_no_cpu_group;
            GetCurrentProcessorNumberEx(&proc_no_cpu_group);

            GroupProcNo groupProcNo = new GroupProcNo(proc_no_cpu_group.Group, proc_no_cpu_group.Number);
            return groupProcNo.GetCombinedValue();
        }

        /// <summary>Check if the OS supports getting current processor number.</summary>
        public static bool CanGetCurrentProcessorNumber()
        {
            // on all Windows platforms we support this API exists
            return true;
        }

        //
        // Processor topology
        //

        /// <summary>
        /// Sets the calling thread's affinity to only run on the processor specified.
        /// </summary>
        public static bool SetThreadAffinity(ushort procNo)
        {
            GroupProcNo groupProcNo = new GroupProcNo(procNo);

            if (CanEnableGCCPUGroups())
            {
                GROUP_AFFINITY ga;
                ga.Group = groupProcNo.GetGroup();
                ga.Reserved[0] = 0; // reserve must be filled with zero
                ga.Reserved[1] = 0; // otherwise call may fail
                ga.Reserved[2] = 0;
                ga.Mask = (nuint)1 << groupProcNo.GetProcIndex();
                return SetThreadGroupAffinity(GetCurrentThread(), &ga, null) != 0;
            }

            return SetThreadAffinityMask(GetCurrentThread(), (nuint)1 << groupProcNo.GetProcIndex()) != 0;
        }

        /// <summary>
        /// Boosts the calling thread's thread priority to a level higher than the default for
        /// new threads.
        /// </summary>
        public static bool BoostThreadPriority()
        {
            return SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST) != 0;
        }

        /// <summary>
        /// Set the set of processors enabled for GC threads for the current process based on the
        /// config specified affinity mask and set, and return the set of enabled processors.
        /// </summary>
        public static AffinitySet* SetGCThreadsAffinitySet(nuint configAffinityMask, AffinitySet* configAffinitySet)
        {
            AffinitySet* processAffinitySet = ManagedGC_Windows_GetProcessAffinitySet();

            // When the configAffinitySet is not empty, enforce the cpu groups
            if (CanEnableGCCPUGroups())
            {
                if (!configAffinitySet->IsEmpty())
                {
                    // Update the process affinity set using the configured set
                    uint totalCpuCount = *ManagedGC_Windows_GetTotalCpuCount();
                    for (nuint i = 0; i < totalCpuCount; i++)
                    {
                        if (processAffinitySet->Contains(i) && !configAffinitySet->Contains(i))
                        {
                            processAffinitySet->Remove(i);
                        }
                    }
                }
            }
            else if (configAffinityMask != 0)
            {
                // Update the process affinity set using the configured mask
                for (nuint i = 0; i < 8 * (nuint)sizeof(nuint); i++)
                {
                    if (processAffinitySet->Contains(i) && ((configAffinityMask & ((nuint)1 << (int)i)) == 0))
                    {
                        processAffinitySet->Remove(i);
                    }
                }
            }

            return processAffinitySet;
        }

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
            uint* totalCpuCount = ManagedGC_Windows_GetTotalCpuCount();

            if (*totalCpuCount != 0)
                return *totalCpuCount;

            if (CanEnableGCCPUGroups())
            {
                uint nProcessors = 0;
                ushort nGroups = ManagedGC_Windows_GetCpuGroupCount();
                for (ushort i = 0; i < nGroups; i++)
                {
                    nProcessors += ManagedGC_Windows_GetCpuGroupActiveProcessorCount(i);
                }

                *totalCpuCount = nProcessors;
            }
            else
            {
                *totalCpuCount = ManagedGC_Windows_GetSystemInfoProcessorCount();
            }

            return *totalCpuCount;
        }

        /// <summary>
        /// Gets the maximum number of processors that could potentially exist on the machine
        /// (including offlined ones).
        /// </summary>
        public static uint GetMaxProcessorCount()
        {
            return (uint)ManagedGC_Windows_GetProcessAffinitySet()->MaxCpuCount();
        }

        /// <summary>Is NUMA support available.</summary>
        public static bool CanEnableGCNumaAware()
        {
            return ManagedGC_Windows_GetCanEnableGCNumaAware() != 0;
        }

        /// <summary>For no NUMA this returns false.</summary>
        public static bool GetNumaInfo(ushort* total_nodes, uint* max_procs_per_node)
        {
            if (CanEnableGCNumaAware())
            {
                uint nNodes = ManagedGC_Windows_GetNumaNodeCount();
                uint currentProcsOnNode = 0;
                for (uint i = 0; i < nNodes; i++)
                {
                    GROUP_AFFINITY processorMask;
                    if (GetNumaNodeProcessorMaskEx((ushort)i, &processorMask) != 0)
                    {
                        uint procsOnNode = 0;
                        nuint mask = processorMask.Mask;
                        while (mask != 0)
                        {
                            procsOnNode++;
                            mask &= mask - 1;
                        }

                        currentProcsOnNode = currentProcsOnNode > procsOnNode ? currentProcsOnNode : procsOnNode;
                    }

                    *max_procs_per_node = currentProcsOnNode;
                    *total_nodes = (ushort)nNodes;
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Is CPU Group enabled. This only applies on Windows and is only used by
        /// instrumentation, but is on the interface due to LocalGC.
        /// </summary>
        public static bool CanEnableGCCPUGroups()
        {
            return ManagedGC_Windows_GetCanEnableGCCPUGroups() != 0;
        }

        /// <summary>For no CPU groups this returns false.</summary>
        public static bool GetCPUGroupInfo(ushort* total_groups, uint* max_procs_per_group)
        {
            if (CanEnableGCCPUGroups())
            {
                ushort nGroups = ManagedGC_Windows_GetCpuGroupCount();
                *total_groups = nGroups;
                uint currentProcsInGroup = 0;
                for (ushort i = 0; i < nGroups; i++)
                {
                    uint procsInGroup = ManagedGC_Windows_GetCpuGroupActiveProcessorCount(i);
                    currentProcsInGroup = currentProcsInGroup > procsInGroup ? currentProcsInGroup : procsInGroup;
                }

                *max_procs_per_group = currentProcsInGroup;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Get the processor number and optionally its NUMA node number for the specified heap
        /// number.
        /// </summary>
        public static bool GetProcessorForHeap(ushort heap_number, ushort* proc_no, ushort* node_no)
        {
            bool success = false;

            // Locate heap_number-th available processor
            ushort procIndex = 0;
            nuint cnt = heap_number;
            AffinitySet* processAffinitySet = ManagedGC_Windows_GetProcessAffinitySet();
            uint totalCpuCount = *ManagedGC_Windows_GetTotalCpuCount();
            for (uint i = 0; i < totalCpuCount; i++)
            {
                if (processAffinitySet->Contains(i))
                {
                    if (cnt == 0)
                    {
                        procIndex = (ushort)i;
                        success = true;
                        break;
                    }

                    cnt--;
                }
            }

            if (success)
            {
                ushort gn;
                ushort gpn;

                if (CanEnableGCCPUGroups())
                {
                    GetGroupForProcessor(procIndex, &gn, &gpn);
                }
                else
                {
                    gn = GroupProcNo.NoGroup;
                    gpn = procIndex;
                }

                GroupProcNo groupProcNo = new GroupProcNo(gn, gpn);
                *proc_no = groupProcNo.GetCombinedValue();

                PROCESSOR_NUMBER procNumber;

                if (CanEnableGCCPUGroups())
                {
                    procNumber.Group = gn;
                }
                else
                {
                    // Get the current processor group
                    GetCurrentProcessorNumberEx(&procNumber);
                }

                if (CanEnableGCNumaAware())
                {
                    procNumber.Number = (byte)gpn;
                    procNumber.Reserved = 0;

                    if (GetNumaProcessorNodeEx(&procNumber, node_no) == 0)
                    {
                        *node_no = NUMA_NODE_UNDEFINED;
                    }
                }
                else
                {   // no numa setting, each cpu group is treated as a node
                    *node_no = procNumber.Group;
                }
            }

            return success;
        }

        /// <summary>
        /// Parse the config string describing affinitization ranges and update the passed in
        /// indices accordingly. Returns true if the config string was successfully parsed.
        /// </summary>
        public static bool ParseGCHeapAffinitizeRangesEntry(byte** config_string, nuint* start_index, nuint* end_index)
        {
            Debug.Assert(CanEnableGCCPUGroups());

            byte* number_end;
            nuint group_number = StrToUInt(*config_string, &number_end);

            if ((number_end == *config_string) || (*number_end != (byte)':'))
            {
                // No number or no colon after the number found, invalid format
                return false;
            }

            ushort totalGroups = ManagedGC_Windows_GetCpuGroupCount();
            if (group_number >= totalGroups)
            {
                // Group number out of range
                return false;
            }

            *config_string = number_end + 1;

            nuint start;
            nuint end;
            if (!GCEnv.ParseIndexOrRange(config_string, &start, &end))
            {
                return false;
            }

            ushort group_processor_count = ManagedGC_Windows_GetCpuGroupActiveProcessorCount((ushort)group_number);
            if ((start >= group_processor_count) || (end >= group_processor_count))
            {
                // Invalid CPU index values or range
                return false;
            }

            ushort group_begin = ManagedGC_Windows_GetCpuGroupBegin((ushort)group_number);

            *start_index = group_begin + start;
            *end_index = group_begin + end;

            return true;
        }

        private static nuint StrToUInt(byte* s, byte** end)
        {
            byte* current = s;

            while (*current == (byte)' ' || (*current >= 0x09 && *current <= 0x0D))
            {
                current++;
            }

            bool negative = false;
            if (*current == (byte)'+' || *current == (byte)'-')
            {
                negative = *current == (byte)'-';
                current++;
            }

            byte* digits = current;
            nuint value = 0;
            bool saturated = false;

            while (*current >= (byte)'0' && *current <= (byte)'9')
            {
                nuint digit = (nuint)(*current - (byte)'0');
                if (value > (nuint.MaxValue - digit) / 10)
                {
                    saturated = true;
                }
                else
                {
                    value = (value * 10) + digit;
                }

                current++;
            }

            if (current == digits)
            {
                *end = s;
                return 0;
            }

            *end = current;

            if (saturated)
            {
                return nuint.MaxValue;
            }

            return negative ? unchecked(0 - value) : value;
        }
    }
}
