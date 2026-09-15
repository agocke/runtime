// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if TARGET_UNIX

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime;

#pragma warning disable CA1823, IDE0059, IDE0060

namespace Internal.Runtime.GC
{
    internal unsafe partial struct GCUnixImports
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Timespec
        {
            public nint tv_sec;
            public nint tv_nsec;
        }

        [LibraryImport("libc", EntryPoint = "nanosleep", SetLastError = true)]
        internal static partial int nanosleep(Timespec* requested, Timespec* remaining);

        [RuntimeImport("*", "minipal_get_cpu_max_possible_count")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int minipal_get_cpu_max_possible_count();

        [RuntimeImport("*", "minipal_initialize_memory_barrier_process_wide")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern bool minipal_initialize_memory_barrier_process_wide();

        [LibraryImport("libc", EntryPoint = "sched_yield")]
        internal static partial int sched_yield();

        [RuntimeImport("*", "madvise")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int madvise(void* address, nuint size, int advice);

        [RuntimeImport("*", "mmap")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern void* mmap(void* address, nuint size, int protection, int flags, int fd, nint offset);

        [RuntimeImport("*", "munmap")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int munmap(void* address, nuint size);

        [RuntimeImport("*", "mprotect")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int mprotect(void* address, nuint size, int protection);

        [RuntimeImport("*", "sched_getaffinity")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int sched_getaffinity(int pid, nuint size, byte* mask);

        [LibraryImport("libc", EntryPoint = "getrlimit")]
        internal static partial int getrlimit(int resource, Rlimit* limit);

        [RuntimeImport("*", "free")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern void free(void* pointer);

        [RuntimeImport("*", "malloc")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern void* malloc(nuint size);

        [RuntimeImport("*", "sysconf")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern long sysconf(int name);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rlimit
        {
            public nuint current;
            public nuint maximum;
        }
    }

    internal unsafe partial struct GCToOSInterface
    {
#if TARGET_LINUX
        private const int SC_PAGESIZE = 30;
        private const int SC_NPROCESSORS_ONLN = 84;
        private const int SC_PHYS_PAGES = 85;
        private const int SC_AVPHYS_PAGES = 86;
#elif TARGET_APPLE
        private const int SC_PAGESIZE = 29;
        private const int SC_NPROCESSORS_ONLN = 58;
        private const int SC_PHYS_PAGES = 200;
        private const int SC_AVPHYS_PAGES = -1;
#else
        private const int SC_PAGESIZE = -1;
        private const int SC_NPROCESSORS_ONLN = -1;
        private const int SC_PHYS_PAGES = -1;
        private const int SC_AVPHYS_PAGES = -1;
#endif
        private const int MADV_DONTDUMP = 16;
        private const int MADV_DODUMP = 17;
        private const int MADV_FREE = 8;
        private const int MAP_PRIVATE = 2;
        private const int MAP_ANONYMOUS = 0x20;
        private const int MAP_FIXED = 0x10;
        private const int MAP_HUGETLB = 0x40000;
        private const int PROT_NONE = 0;
        private const int PROT_READ = 1;
        private const int PROT_WRITE = 2;
        private const int RLIMIT_AS = 9;
        private const int CPU_SETSIZE = 1024;

        public static bool Initialize()
        {
            if (!IsSupportedUnixTarget())
            {
                return false;
            }

            long pageSize = GCUnixImports.sysconf(SC_PAGESIZE);
            long totalCpuCount = GCUnixImports.sysconf(SC_NPROCESSORS_ONLN);
            int configuredCpuCount = GCUnixImports.minipal_get_cpu_max_possible_count();
            if (configuredCpuCount == -1)
            {
                return false;
            }

            if (pageSize <= 0 || totalCpuCount <= 0 || configuredCpuCount <= 0)
            {
                return false;
            }

            s_pageSize = (uint)pageSize;
            s_totalCpuCount = (uint)totalCpuCount;
            s_configuredCpuCount = (uint)configuredCpuCount;
            s_processAffinitySet = (AffinitySet*)GCUnixImports.malloc((nuint)sizeof(AffinitySet));
            if (s_processAffinitySet is null || !s_processAffinitySet->Initialize(s_configuredCpuCount))
            {
                if (s_processAffinitySet is not null)
                {
                    GCUnixImports.free(s_processAffinitySet);
                    s_processAffinitySet = null;
                }

                return false;
            }

            if (!GCUnixImports.minipal_initialize_memory_barrier_process_wide())
            {
                s_processAffinitySet->Destroy();
                GCUnixImports.free(s_processAffinitySet);
                s_processAffinitySet = null;
                return false;
            }

            int cpusToAllocate = configuredCpuCount > CPU_SETSIZE ? configuredCpuCount : CPU_SETSIZE;
            nuint maskSize = (nuint)(((cpusToAllocate + 63) / 64) * 8);
            byte* mask = (byte*)GCUnixImports.malloc(maskSize);
            if (mask is null)
            {
                s_processAffinitySet->Destroy();
                GCUnixImports.free(s_processAffinitySet);
                s_processAffinitySet = null;
                return false;
            }

            for (nuint i = 0; i < maskSize; i++)
            {
                mask[i] = 0;
            }

            bool affinitySucceeded = GCUnixImports.sched_getaffinity((int)Interop.Sys.GetPid(), maskSize, mask) == 0;
            if (affinitySucceeded)
            {
                for (nuint i = 0; i < s_configuredCpuCount; i++)
                {
                    if ((mask[i / 8] & (1 << (int)(i % 8))) != 0)
                    {
                        s_processAffinitySet->Add(i);
                    }
                }
            }

            GCUnixImports.free(mask);
            if (!affinitySucceeded)
            {
                for (nuint i = 0; i < s_configuredCpuCount; i++)
                {
                    s_processAffinitySet->Add(i);
                }
            }

            long physicalPages = GCUnixImports.sysconf(SC_PHYS_PAGES);
            if (physicalPages <= 0)
            {
                s_processAffinitySet->Destroy();
                GCUnixImports.free(s_processAffinitySet);
                s_processAffinitySet = null;
                return false;
            }

            s_totalPhysicalMemSize = (ulong)physicalPages * s_pageSize;
            return true;
        }

        private static bool IsSupportedUnixTarget()
        {
            return SC_PAGESIZE >= 0;
        }

        public static void* AllocateUnmanaged(nuint size)
        {
            return GCUnixImports.malloc(size);
        }

        public static void FreeUnmanaged(void* address)
        {
            GCUnixImports.free(address);
        }

        public static void Shutdown()
        {
            s_pageSize = 0;
            s_totalCpuCount = 0;
            s_configuredCpuCount = 0;
            s_totalPhysicalMemSize = 0;
            if (s_processAffinitySet is not null)
            {
                s_processAffinitySet->Destroy();
                GCUnixImports.free(s_processAffinitySet);
                s_processAffinitySet = null;
            }
        }

        public static void* VirtualReserve(nuint size, nuint alignment, uint flags, ushort node = NUMA_NODE_UNDEFINED)
        {
            if ((flags & (uint)VirtualReserveFlags.WriteWatch) != 0)
            {
                return null;
            }

            return VirtualReserveInner(size, alignment, 0, false);
        }

        private static void* VirtualReserveInner(nuint size, nuint alignment, int hugePagesFlag, bool committing)
        {
            if (alignment < s_pageSize)
            {
                alignment = s_pageSize;
            }

            nuint alignedSize = size + (alignment - s_pageSize);
            void* result = GCUnixImports.mmap(null, alignedSize, PROT_NONE, MAP_PRIVATE | MAP_ANONYMOUS | hugePagesFlag, -1, 0);

            if ((nint)result == -1)
            {
                return null;
            }

            byte* aligned = GCEnvironment.AlignUp((byte*)result, alignment);
            nuint startPadding = (nuint)(aligned - (byte*)result);
            if (startPadding != 0)
            {
                GCUnixImports.munmap(result, startPadding);
            }

            nuint endPadding = alignedSize - (startPadding + size);
            if (endPadding != 0)
            {
                GCUnixImports.munmap(aligned + size, endPadding);
            }

            if (!committing)
            {
                GCUnixImports.madvise(aligned, size, MADV_DONTDUMP);
            }
            return aligned;
        }

        public static bool VirtualRelease(void* address, nuint size)
        {
            return GCUnixImports.munmap(address, size) == 0;
        }

        public static bool VirtualCommit(void* address, nuint size, ushort node = NUMA_NODE_UNDEFINED)
        {
            return VirtualCommitInner(address, size, node, false);
        }

        private static bool VirtualCommitInner(void* address, nuint size, ushort node, bool newMemory)
        {
            bool success = GCUnixImports.mprotect(address, size, PROT_READ | PROT_WRITE) == 0;

            if (success && !newMemory)
            {
                GCUnixImports.madvise(address, size, MADV_DODUMP);
            }

            return success;
        }

        public static void* VirtualReserveAndCommitLargePages(nuint size, ushort node = NUMA_NODE_UNDEFINED)
        {
            void* result = VirtualReserveInner(size, s_pageSize, MAP_HUGETLB, true);
            return result is not null && VirtualCommitInner(result, size, node, true) ? result : null;
        }

        public static bool VirtualDecommit(void* address, nuint size)
        {
            void* result = GCUnixImports.mmap(address, size, PROT_NONE, MAP_FIXED | MAP_ANONYMOUS | MAP_PRIVATE, -1, 0);

            if ((nint)result == -1)
            {
                return false;
            }

            GCUnixImports.madvise(address, size, MADV_DONTDUMP);
            return true;
        }

        public static bool VirtualReset(void* address, nuint size, bool unlock)
        {
            int status = GCUnixImports.madvise(address, size, MADV_DONTDUMP);
            status = GCUnixImports.madvise(address, size, MADV_FREE);
            return status == 0;
        }

        public static bool SupportsWriteWatch()
        {
            return false;
        }

        public static void ResetWriteWatch(void* address, nuint size)
        {
            DebugBreak();
        }

        public static bool GetWriteWatch(bool resetState, void* address, nuint size, void** pageAddresses, nuint* pageAddressesCount)
        {
            *pageAddressesCount = 0;
            DebugBreak();
            return false;
        }

        public static void Sleep(uint sleepMSec)
        {
            if (sleepMSec == 0)
            {
                return;
            }

            GCUnixImports.Timespec requested = default;
            requested.tv_sec = (nint)(sleepMSec / 1000);
            requested.tv_nsec = (nint)((sleepMSec % 1000) * 1000000);
            GCUnixImports.Timespec remaining;
            while (GCUnixImports.nanosleep(&requested, &remaining) == -1 && Interop.Sys.GetLastError() == Interop.Error.EINTR)
            {
                requested = remaining;
            }
        }

        public static void YieldThread(uint switchCount)
        {
            GCUnixImports.sched_yield();
        }

        public static uint GetCurrentProcessorNumber()
        {
            return unchecked((uint)Interop.Sys.SchedGetCpu());
        }

        public static bool CanGetCurrentProcessorNumber()
        {
            return true;
        }

        public static bool SetCurrentThreadIdealAffinity(ushort srcProcNo, ushort dstProcNo)
        {
            return true;
        }

        public static bool GetCurrentThreadIdealProc(ushort* procNo)
        {
            return false;
        }

        public static ulong GetCurrentThreadIdForLogging()
        {
            return Interop.Sys.TryGetUInt32OSThreadId();
        }

        public static uint GetCurrentProcessId()
        {
            return unchecked((uint)Interop.Sys.GetPid());
        }

        public static nuint GetCacheSizePerLogicalCpu(bool trueSize = true)
        {
            return 0;
        }

        public static bool SetThreadAffinity(ushort procNo)
        {
            return false;
        }

        public static bool BoostThreadPriority()
        {
            return false;
        }

        public static AffinitySet* SetGCThreadsAffinitySet(nuint configAffinityMask, AffinitySet* configAffinitySet)
        {
            if (configAffinitySet is not null && !configAffinitySet->IsEmpty())
            {
                for (nuint i = 0; i < s_totalCpuCount; i++)
                {
                    if (s_processAffinitySet->Contains(i) && !configAffinitySet->Contains(i))
                    {
                        s_processAffinitySet->Remove(i);
                    }
                }
            }

            return s_processAffinitySet;
        }

        public static nuint GetVirtualMemoryLimit()
        {
            GCUnixImports.Rlimit limit;
            if (GCUnixImports.getrlimit(RLIMIT_AS, &limit) == 0 && limit.current != unchecked((nuint)(-1)))
            {
                return limit.current;
            }

            return GetVirtualMemoryMaxAddress();
        }

        public static nuint GetVirtualMemoryMaxAddress()
        {
            return (nuint)1 << 47;
        }

        public static ulong GetPhysicalMemoryLimit(bool* isRestricted)
        {
            if (isRestricted is not null)
            {
                *isRestricted = false;
            }

            return s_totalPhysicalMemSize;
        }

        public static void GetMemoryStatus(ulong restrictedLimit, uint* memoryLoad, ulong* availablePhysical, ulong* availablePageFile)
        {
            ulong available = 0;
            uint load = 0;

#if TARGET_LINUX
            long availablePages = GCUnixImports.sysconf(SC_AVPHYS_PAGES);
            if (availablePages > 0)
            {
                available = (ulong)availablePages * s_pageSize;
            }
#endif

            ulong total = restrictedLimit != 0
                ? restrictedLimit
                : s_totalPhysicalMemSize;
            if (total > available)
            {
                load = (uint)(((total - available) * 100) / total);
            }

            if (memoryLoad is not null)
            {
                *memoryLoad = load;
            }
            if (availablePhysical is not null)
            {
                *availablePhysical = available;
            }
            if (availablePageFile is not null)
            {
                *availablePageFile = 0;
            }
        }

        public static nuint GetPageSize()
        {
            return s_pageSize != 0 ? s_pageSize : 0x1000;
        }

        public static void DebugBreak()
        {
            System.Runtime.InternalCalls.RhpFallbackFailFast();
        }

        public static uint GetTotalProcessorCount()
        {
            return s_totalCpuCount;
        }

        public static uint GetMaxProcessorCount()
        {
            return s_processAffinitySet is null ? 0 : (uint)s_processAffinitySet->MaxCpuCount();
        }

        public static bool CanEnableGCNumaAware()
        {
            return false;
        }

        public static bool CanEnableGCCPUGroups()
        {
            return false;
        }
    }
}

#endif
