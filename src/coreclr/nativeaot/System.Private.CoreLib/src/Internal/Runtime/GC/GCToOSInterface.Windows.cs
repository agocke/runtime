// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if !TARGET_UNIX

namespace Internal.Runtime.GC
{
    internal unsafe partial struct GCToOSInterface
    {
        public static bool Initialize() => false;
        public static void Shutdown() { }
        public static void* VirtualReserve(nuint size, nuint alignment, uint flags, ushort node = NUMA_NODE_UNDEFINED) => null;
        public static bool VirtualRelease(void* address, nuint size) => false;
        public static bool VirtualCommit(void* address, nuint size, ushort node = NUMA_NODE_UNDEFINED) => false;
        public static void* VirtualReserveAndCommitLargePages(nuint size, ushort node = NUMA_NODE_UNDEFINED) => null;
        public static bool VirtualDecommit(void* address, nuint size) => false;
        public static bool VirtualReset(void* address, nuint size, bool unlock) => false;
        public static bool SupportsWriteWatch() => false;
        public static void ResetWriteWatch(void* address, nuint size) { }
        public static bool GetWriteWatch(bool resetState, void* address, nuint size, void** pageAddresses, nuint* pageAddressesCount)
        {
            *pageAddressesCount = 0;
            return false;
        }
        public static void Sleep(uint sleepMSec) { }
        public static void YieldThread(uint switchCount) { }
        public static uint GetCurrentProcessorNumber() => 0;
        public static bool CanGetCurrentProcessorNumber() => false;
        public static bool SetCurrentThreadIdealAffinity(ushort srcProcNo, ushort dstProcNo) => false;
        public static bool GetCurrentThreadIdealProc(ushort* procNo) => false;
        public static ulong GetCurrentThreadIdForLogging() => 0;
        public static uint GetCurrentProcessId() => 0;
        public static nuint GetCacheSizePerLogicalCpu(bool trueSize = true) => 0;
        public static bool SetThreadAffinity(ushort procNo) => false;
        public static bool BoostThreadPriority() => false;
        public static AffinitySet* SetGCThreadsAffinitySet(nuint configAffinityMask, AffinitySet* configAffinitySet) => null;
        public static nuint GetVirtualMemoryLimit() => 0;
        public static nuint GetVirtualMemoryMaxAddress() => 0;
        public static ulong GetPhysicalMemoryLimit(bool* isRestricted) => 0;
        public static void GetMemoryStatus(ulong restrictedLimit, uint* memoryLoad, ulong* availablePhysical, ulong* availablePageFile)
        {
            if (memoryLoad is not null) *memoryLoad = 0;
            if (availablePhysical is not null) *availablePhysical = 0;
            if (availablePageFile is not null) *availablePageFile = 0;
        }
        public static nuint GetPageSize() => 0;
        public static void DebugBreak() { }
        public static uint GetTotalProcessorCount() => 0;
        public static uint GetMaxProcessorCount() => 0;
        public static bool CanEnableGCNumaAware() => false;
        public static bool CanEnableGCCPUGroups() => false;
    }
}

#endif
