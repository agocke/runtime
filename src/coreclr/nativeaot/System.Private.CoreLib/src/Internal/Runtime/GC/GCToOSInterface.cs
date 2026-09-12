// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Internal.Runtime.GC
{
    internal enum VirtualReserveFlags : uint
    {
        None = 0,
        WriteWatch = 1,
    }

    internal unsafe struct GCSystemInfo
    {
        public uint dwNumberOfProcessors;
        public uint dwPageSize;
        public uint dwAllocationGranularity;
    }

    internal unsafe struct AffinitySet
    {
        private uint m_bitsetDataSize;
        private nuint* m_bitset;

        public bool Initialize(uint cpuCount)
        {
            uint bitsPerEntry = (uint)(sizeof(nuint) * 8);
            m_bitsetDataSize = (cpuCount + bitsPerEntry - 1) / bitsPerEntry;
            m_bitset = (nuint*)GCUnixImports.malloc((nuint)m_bitsetDataSize * (nuint)sizeof(nuint));
            if (m_bitset is null)
            {
                m_bitsetDataSize = 0;
                return false;
            }

            for (uint i = 0; i < m_bitsetDataSize; i++)
            {
                m_bitset[i] = 0;
            }

            return true;
        }

        public bool Contains(nuint cpuIndex)
        {
            nuint maxCpuCount = (nuint)m_bitsetDataSize * (nuint)(sizeof(nuint) * 8);
            if (cpuIndex >= maxCpuCount)
            {
                return false;
            }

            nuint bitsPerEntry = (nuint)(sizeof(nuint) * 8);
            return (m_bitset[cpuIndex / bitsPerEntry] & ((nuint)1 << (int)(cpuIndex % bitsPerEntry))) != 0;
        }

        public void Add(nuint cpuIndex)
        {
            nuint bitsPerEntry = (nuint)(sizeof(nuint) * 8);
            if (cpuIndex < (nuint)m_bitsetDataSize * bitsPerEntry)
            {
                m_bitset[cpuIndex / bitsPerEntry] |= (nuint)1 << (int)(cpuIndex % bitsPerEntry);
            }
        }

        public void Remove(nuint cpuIndex)
        {
            nuint bitsPerEntry = (nuint)(sizeof(nuint) * 8);
            if (cpuIndex < (nuint)m_bitsetDataSize * bitsPerEntry)
            {
                m_bitset[cpuIndex / bitsPerEntry] &= ~((nuint)1 << (int)(cpuIndex % bitsPerEntry));
            }
        }

        public bool IsEmpty()
        {
            nuint maxCpuCount = (nuint)m_bitsetDataSize * (nuint)(sizeof(nuint) * 8);
            for (nuint i = 0; i < maxCpuCount; i++)
            {
                if (Contains(i))
                {
                    return false;
                }
            }

            return true;
        }

        public nuint MaxCpuCount()
        {
            return (nuint)m_bitsetDataSize * (nuint)(sizeof(nuint) * 8);
        }

        public nuint Count()
        {
            nuint count = 0;
            nuint maxCpuCount = (nuint)m_bitsetDataSize * (nuint)(sizeof(nuint) * 8);
            for (nuint i = 0; i < maxCpuCount; i++)
            {
                if (Contains(i))
                {
                    count++;
                }
            }

            return count;
        }

        public void Destroy()
        {
            if (m_bitset is not null)
            {
                GCUnixImports.free(m_bitset);
                m_bitset = null;
            }

            m_bitsetDataSize = 0;
        }
    }

    internal unsafe partial struct GCToOSInterface
    {
        internal const ushort NUMA_NODE_UNDEFINED = ushort.MaxValue;

        private static uint s_pageSize;
        private static uint s_totalCpuCount;
        private static uint s_configuredCpuCount;
        private static ulong s_totalPhysicalMemSize;
        private static AffinitySet* s_processAffinitySet;

        public static uint PageSize => s_pageSize;

        public static uint TotalCpuCount => s_totalCpuCount;

        public static bool ParseGCHeapAffinitizeRangesEntry(byte** configString, nuint* startIndex, nuint* endIndex)
        {
            if (configString is null || *configString is null)
            {
                return false;
            }

            byte* current = *configString;
            nuint start = 0;
            bool found = false;
            while (*current >= (byte)'0' && *current <= (byte)'9')
            {
                start = (start * 10) + (nuint)(*current - (byte)'0');
                current++;
                found = true;
            }

            if (!found)
            {
                return false;
            }

            nuint end = start;
            if (*current == (byte)'-')
            {
                current++;
                end = 0;
                found = false;
                while (*current >= (byte)'0' && *current <= (byte)'9')
                {
                    end = (end * 10) + (nuint)(*current - (byte)'0');
                    current++;
                    found = true;
                }

                if (!found)
                {
                    return false;
                }
            }

            *startIndex = start;
            *endIndex = end;
            *configString = current;
            return true;
        }

        public static bool GetProcessorForHeap(ushort heapNumber, ushort* procNo, ushort* nodeNo)
        {
            ushort availableProcNumber = 0;
            for (nuint processorNumber = 0; processorNumber < s_processAffinitySet->MaxCpuCount(); processorNumber++)
            {
                if (s_processAffinitySet->Contains(processorNumber))
                {
                    if (availableProcNumber == heapNumber)
                    {
                        *procNo = (ushort)processorNumber;
                        *nodeNo = NUMA_NODE_UNDEFINED;
                        return true;
                    }

                    availableProcNumber++;
                }
            }

            return false;
        }

        public static bool GetNumaInfo(ushort* totalNodes, uint* maxProcsPerNode)
        {
            *totalNodes = 1;
            *maxProcsPerNode = s_totalCpuCount;
            return false;
        }

        public static bool GetCPUGroupInfo(ushort* totalGroups, uint* maxProcsPerGroup)
        {
            *totalGroups = 1;
            *maxProcsPerGroup = s_totalCpuCount;
            return false;
        }
    }
}
