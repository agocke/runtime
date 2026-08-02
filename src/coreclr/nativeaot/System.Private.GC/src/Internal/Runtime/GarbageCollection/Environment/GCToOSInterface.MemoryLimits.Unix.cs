// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the memory limit and cache sizing half of gc/unix/gcenv.unix.cpp, plus the
// GetRestrictedPhysicalMemoryLimit of gc/unix/cgroup.cpp that stands behind it. The methods
// appear in the order the C++ files declare them, and the bodies are the same statements: the
// same sysconf and sysctl calls, the same clamping and saturation rules, the same sentinel
// values, the same float arithmetic for the memory load, and the same failure values.
//
// The calls to libc are [RuntimeImport] declarations, which are direct calls to the linked
// symbol with no marshalling and no GC mode transition -- what the C++ GC gets for free by being
// native code, and what the collector needs while the world is suspended. They are in
// GCToOSInterface.Imports.Unix.cs so that the test host can substitute the same private methods
// for ones it can call and record; see tests/GCToOSInterface.Imports.Unix.TestHost.cs.
//
// The <unistd.h> _SC_* constants below are hardcoded per platform, as the AsmOffsets tables are,
// and checked against the real headers by static_asserts in nativeaot/Runtime/gcenv.managed.cpp,
// which is compiled for the target platform -- so a platform whose values differ from the ones
// selected here breaks the build rather than the process. The #if structure and the
// static_asserts must be kept in the same shape.
//
// Five pieces are not translated, because each of them parses a file with getline/sscanf/
// strtok_r, which allocates, or reaches state that only the C++ translation unit has. Each stays
// behind one narrow shim named after the C++ function it is (see gcenv.managed.cpp and the
// declarations in GCToOSInterface.Imports.Unix.cs):
//
//   * CGroup::GetPhysicalMemoryLimit and GetPhysicalMemoryUsed of gc/unix/cgroup.cpp. Deletion
//     point: the cgroup submodule of plan step 3 in ROADMAP.md.
//   * ReadMemoryValueFromFile and ReadMemAvailable of gc/unix/gcenv.unix.cpp. Deletion point:
//     the same, once a file reader that does not allocate exists.
//   * GetCurrentVirtualMemorySize of gc/unix/gcenv.unix.cpp, likewise.
//
// and one more that is not a parser at all: g_processAffinitySet, which
// GCToOSInterface::Initialize fills in and which the cache size heuristic counts. Only its
// address crosses; the counting is the ported AffinitySet.Count(). Deletion point: the affinity
// submodule of plan step 3 in ROADMAP.md, together with SetGCThreadsAffinitySet.
//
// Everything below is `internal` rather than `private` so that the tests can drive each helper
// on its own -- GetCacheSizePerLogicalCpu caches its result in the C++ as well as here, so the
// helper under it is the only thing a table of cases can call. The C++ counterparts have
// internal linkage in their translation unit; nothing outside System.Private.GC can see these.

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe partial class GCToOSInterface
    {
        //
        // The <unistd.h> sysconf names of gc/unix/gcenv.unix.cpp and gc/unix/cgroup.cpp.
        //

#if TARGET_APPLE
        private const int _SC_PAGE_SIZE = 29;
        private const int _SC_PHYS_PAGES = 200;

        // SYSCONF_PAGES has no Apple definition: the C++ #define is inside #ifndef __APPLE__,
        // and the branch of GetAvailablePhysicalMemory that uses it is not the Apple one.
#elif TARGET_FREEBSD
        private const int _SC_PAGE_SIZE = 47;
        private const int _SC_PHYS_PAGES = 121;

        // FreeBSD has no _SC_AVPHYS_PAGES, so SYSCONF_PAGES falls back to _SC_PHYS_PAGES. Like
        // Apple, FreeBSD takes a branch of GetAvailablePhysicalMemory that does not use it.
        private const int SYSCONF_PAGES = _SC_PHYS_PAGES;
#elif TARGET_OPENBSD
        private const int _SC_PAGE_SIZE = 28;
        private const int _SC_PHYS_PAGES = 500;
        private const int _SC_AVPHYS_PAGES = 501;

        private const int SYSCONF_PAGES = _SC_AVPHYS_PAGES;
#elif TARGET_BIONIC
        private const int _SC_PAGE_SIZE = 0x27;
        private const int _SC_PHYS_PAGES = 0x62;
        private const int _SC_AVPHYS_PAGES = 0x63;

        private const int SYSCONF_PAGES = _SC_AVPHYS_PAGES;

        // The four cache size names of GetLogicalProcessorCacheSizeFromSysConf. bionic numbers
        // them differently from glibc; musl does not define them at all, which is why the C++
        // body of that function is inside an #if on them and why its comment says that musl is
        // one of the two cases that fall through to the sysfs reader.
        private const int _SC_LEVEL1_DCACHE_SIZE = 0x92;
        private const int _SC_LEVEL2_CACHE_SIZE = 0x95;
        private const int _SC_LEVEL3_CACHE_SIZE = 0x98;
        private const int _SC_LEVEL4_CACHE_SIZE = 0x9b;
#elif TARGET_LINUX_MUSL
        private const int _SC_PAGE_SIZE = 30;
        private const int _SC_PHYS_PAGES = 85;
        private const int _SC_AVPHYS_PAGES = 86;

        private const int SYSCONF_PAGES = _SC_AVPHYS_PAGES;
#else
        private const int _SC_PAGE_SIZE = 30;
        private const int _SC_PHYS_PAGES = 85;
        private const int _SC_AVPHYS_PAGES = 86;

        private const int SYSCONF_PAGES = _SC_AVPHYS_PAGES;

        private const int _SC_LEVEL1_DCACHE_SIZE = 188;
        private const int _SC_LEVEL2_CACHE_SIZE = 191;
        private const int _SC_LEVEL3_CACHE_SIZE = 194;
        private const int _SC_LEVEL4_CACHE_SIZE = 197;
#endif

        //
        // The <sys/sysctl.h> and <sys/sysinfo.h> constants and types of the same two functions.
        //

#if TARGET_APPLE
        /// <summary>
        /// <c>CTL_MAXNAME</c> of <c>&lt;sys/sysctl.h&gt;</c>: the longest mib the kernel can
        /// produce, and so the size of the buffer the mib lookup writes into.
        /// </summary>
        private const int CTL_MAXNAME = 12;

        /// <summary><c>CTL_VM</c> and <c>VM_SWAPUSAGE</c> of <c>&lt;sys/sysctl.h&gt;</c>.</summary>
        private const int CTL_VM = 2;
        private const int VM_SWAPUSAGE = 5;

        /// <summary><c>struct xsw_usage</c> of <c>&lt;sys/sysctl.h&gt;</c>.</summary>
        private struct xsw_usage
        {
            public ulong xsu_total;
            public ulong xsu_avail;
            public ulong xsu_used;
            public uint xsu_pagesize;
            public int xsu_encrypted;
        }
#elif TARGET_FREEBSD
        /// <summary><c>XSWDEV_VERSION</c> of <c>&lt;vm/vm_param.h&gt;</c>.</summary>
        private const uint XSWDEV_VERSION = 2;

        /// <summary>
        /// <c>struct xswdev</c> of <c>&lt;vm/vm_param.h&gt;</c>. The type name of the C++ is not
        /// available: C# reserves all-lowercase type names. <c>xsw_usage</c> above keeps its
        /// name because an underscore takes it out of that reservation.
        /// </summary>
        private struct XswDev
        {
            public uint xsw_version;

            // dev_t is 8 bytes wide and 8-byte aligned, so the four bytes of padding the C++
            // structure has after xsw_version are inserted here as well.
            public ulong xsw_dev;
            public int xsw_flags;
            public int xsw_nblks;
            public int xsw_used;
        }
#elif !TARGET_OPENBSD
        /// <summary>
        /// <c>struct sysinfo</c> of <c>&lt;sys/sysinfo.h&gt;</c>. The C++ name is not available:
        /// the entry point that fills it in is called <c>sysinfo</c> too, and a nested type
        /// cannot share a name with a method of the same class.
        /// </summary>
        /// <remarks>
        /// Only <c>freeswap</c> and <c>mem_unit</c> are read, but the whole structure is
        /// declared so that the kernel has somewhere to write the rest. The trailing reserved
        /// bytes are musl's, which is the largest of the three C libraries; glibc and bionic
        /// pad the same structure out to 112 bytes. gcenv.managed.cpp asserts that the two
        /// offsets are the ones below and that the real structure is no larger than this one.
        ///
        /// It is internal rather than private only so that the test host can declare a
        /// substitute <c>sysinfo</c> that fills one in.
        /// </remarks>
        internal struct SysInfo
        {
            public nint uptime;
            public nuint loads0;
            public nuint loads1;
            public nuint loads2;
            public nuint totalram;
            public nuint freeram;
            public nuint sharedram;
            public nuint bufferram;
            public nuint totalswap;
            public nuint freeswap;
            public ushort procs;
            public ushort pad;
            public nuint totalhigh;
            public nuint freehigh;
            public uint mem_unit;
            public fixed byte __reserved[256];
        }
#endif

        /// <summary>
        /// <c>g_RestrictedPhysicalMemoryLimit</c>. Written by
        /// <see cref="GetPhysicalMemoryLimit"/> and read by nothing in gcenv.unix.cpp; it exists
        /// so that a debugger can see the limit the process settled on.
        /// </summary>
        internal static nuint g_RestrictedPhysicalMemoryLimit;

        /// <summary>
        /// <c>g_totalPhysicalMemSize</c>, computed where it is used rather than cached.
        /// </summary>
        /// <remarks>
        /// The C++ computes it once, in <c>GCToOSInterface::Initialize</c>, which the managed GC
        /// still runs as native code -- so a process that reaches this point has already had
        /// <c>sysconf(_SC_PHYS_PAGES)</c> succeed once, and these are the same two statements
        /// against the same unchanging values. Where the C++ fails to start the runtime this
        /// returns zero, which is what an unset <c>g_totalPhysicalMemSize</c> would have been.
        /// </remarks>
        internal static long GetTotalPhysicalMemSize()
        {
            nint pages = sysconf(_SC_PHYS_PAGES);
            if (pages == -1)
            {
                return 0;
            }

            return (long)((ulong)pages * (ulong)GetPageSize());
        }

        //
        // gc/unix/cgroup.cpp.
        //

        /// <summary>
        /// <c>GetRestrictedPhysicalMemoryLimit</c> of gc/unix/cgroup.cpp: the cgroup limit,
        /// clamped by the address space rlimit and by the real memory size.
        /// </summary>
        internal static nuint GetRestrictedPhysicalMemoryLimit()
        {
#if TARGET_APPLE || TARGET_FREEBSD || TARGET_OPENBSD
            // The `#else // !TARGET_LINUX` tail of cgroup.cpp.
            return 0;
#else
            ulong physical_memory_limit = 0;

            if (ManagedGC_CGroup_GetPhysicalMemoryLimit(&physical_memory_limit) == 0)
                return 0;

            // If there's no memory limit specified on the container this
            // actually returns 0x7FFFFFFFFFFFF000 (2^63-1 rounded down to
            // 4k which is a common page size). So we know we are not
            // running in a memory restricted environment.
            if (physical_memory_limit > 0x7FFFFFFF00000000)
            {
                return 0;
            }

            Rlimit curr_rlimit;
            nuint rlimit_soft_limit = (nuint)RLIM_INFINITY;
            if (getrlimit(RLIMIT_AS, &curr_rlimit) == 0)
            {
                rlimit_soft_limit = (nuint)curr_rlimit.rlim_cur;
            }
            physical_memory_limit = (physical_memory_limit < rlimit_soft_limit) ?
                                    physical_memory_limit : (ulong)rlimit_soft_limit;

            // Ensure that limit is not greater than real memory size
            nint pages = sysconf(_SC_PHYS_PAGES);
            if (pages != -1)
            {
                nint pageSize = sysconf(_SC_PAGE_SIZE);
                if (pageSize != -1)
                {
                    physical_memory_limit = (physical_memory_limit < (nuint)pages * (nuint)pageSize) ?
                                            physical_memory_limit : (ulong)((nuint)pages * (nuint)pageSize);
                }
            }

            // The C++ compares against std::numeric_limits<size_t>::max(). On a 64-bit target
            // the comparison is between two 64-bit values and can only be false, exactly as it
            // is in C++; the branch is kept so that the two read the same.
            if (physical_memory_limit > (ulong)GCEnv.SIZE_T_MAX)
            {
                // It is observed in practice when the memory is unrestricted, Linux control
                // group returns a physical limit that is bigger than the address space
                return GCEnv.SIZE_T_MAX;
            }
            else
            {
                return (nuint)physical_memory_limit;
            }
#endif
        }

        /// <summary><c>GetPhysicalMemoryUsed</c> of gc/unix/cgroup.cpp.</summary>
        internal static bool GetPhysicalMemoryUsed(nuint* val)
        {
#if TARGET_APPLE || TARGET_FREEBSD || TARGET_OPENBSD
            // The `#else // !TARGET_LINUX` tail of cgroup.cpp.
            return false;
#else
            return ManagedGC_Unix_GetPhysicalMemoryUsed(val) != 0;
#endif
        }

        //
        // gc/unix/gcenv.unix.cpp.
        //

        /// <summary>
        /// <c>GetLogicalProcessorCacheSizeFromSysConf</c>: the last level cache size the C
        /// library reports, largest level first.
        /// </summary>
        internal static void GetLogicalProcessorCacheSizeFromSysConf(nuint* cacheLevel, nuint* cacheSize)
        {
            Debug.Assert(cacheLevel != null);
            Debug.Assert(cacheSize != null);

#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD && !TARGET_LINUX_MUSL
            int* cacheLevelNames = stackalloc int[]
            {
                _SC_LEVEL1_DCACHE_SIZE,
                _SC_LEVEL2_CACHE_SIZE,
                _SC_LEVEL3_CACHE_SIZE,
                _SC_LEVEL4_CACHE_SIZE,
            };
            const int cacheLevelNamesLength = 4;

            for (int i = cacheLevelNamesLength - 1; i >= 0; i--)
            {
                nint size = sysconf(cacheLevelNames[i]);
                if (size > 0)
                {
                    *cacheSize = (nuint)size;
                    *cacheLevel = (nuint)(i + 1);
                    break;
                }
            }
#endif
        }

        /// <summary>
        /// <c>GetLogicalProcessorCacheSizeFromSysFs</c>: the last level cache size read out of
        /// <c>/sys/devices/system/cpu/cpu0/cache</c>.
        /// </summary>
        internal static void GetLogicalProcessorCacheSizeFromSysFs(nuint* cacheLevel, nuint* cacheSize)
        {
            Debug.Assert(cacheLevel != null);
            Debug.Assert(cacheSize != null);

#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD && !TARGET_ARM && !TARGET_X86
            //
            // Retrieve cachesize via sysfs by reading the file /sys/devices/system/cpu/cpu0/cache/index{LastLevelCache}/size
            // for the platform. Currently musl and arm64 should be only cases to use
            // this method to determine cache size.
            //
            // The C++ declares this size_t and hands its address to a uint64_t* parameter, which
            // only compiles where the two are the same width - which is why the block above
            // excludes the 32-bit targets. It is a ulong here so that the width is right by
            // construction rather than by the guard.
            ulong level;

            // The two paths, spelled out on the stack because the GC may not reference a string
            // object and because the digit at `index` is written into them. Each is the C++
            // char[] with its terminator.
            byte* path_to_size_file = stackalloc byte[]
            {
                (byte)'/', (byte)'s', (byte)'y', (byte)'s', (byte)'/', (byte)'d', (byte)'e', (byte)'v',
                (byte)'i', (byte)'c', (byte)'e', (byte)'s', (byte)'/', (byte)'s', (byte)'y', (byte)'s',
                (byte)'t', (byte)'e', (byte)'m', (byte)'/', (byte)'c', (byte)'p', (byte)'u', (byte)'/',
                (byte)'c', (byte)'p', (byte)'u', (byte)'0', (byte)'/', (byte)'c', (byte)'a', (byte)'c',
                (byte)'h', (byte)'e', (byte)'/', (byte)'i', (byte)'n', (byte)'d', (byte)'e', (byte)'x',
                (byte)'-', (byte)'/', (byte)'s', (byte)'i', (byte)'z', (byte)'e', 0
            };
            byte* path_to_level_file = stackalloc byte[]
            {
                (byte)'/', (byte)'s', (byte)'y', (byte)'s', (byte)'/', (byte)'d', (byte)'e', (byte)'v',
                (byte)'i', (byte)'c', (byte)'e', (byte)'s', (byte)'/', (byte)'s', (byte)'y', (byte)'s',
                (byte)'t', (byte)'e', (byte)'m', (byte)'/', (byte)'c', (byte)'p', (byte)'u', (byte)'/',
                (byte)'c', (byte)'p', (byte)'u', (byte)'0', (byte)'/', (byte)'c', (byte)'a', (byte)'c',
                (byte)'h', (byte)'e', (byte)'/', (byte)'i', (byte)'n', (byte)'d', (byte)'e', (byte)'x',
                (byte)'-', (byte)'/', (byte)'l', (byte)'e', (byte)'v', (byte)'e', (byte)'l', 0
            };
            const int index = 40;
            Debug.Assert(path_to_size_file[index] == (byte)'-');
            Debug.Assert(path_to_level_file[index] == (byte)'-');

            for (int i = 0; i < 5; i++)
            {
                path_to_size_file[index] = (byte)(48 + i);

                ulong cache_size_from_sys_file = 0;

                if (ManagedGC_Unix_ReadMemoryValueFromFile(path_to_size_file, &cache_size_from_sys_file) != 0)
                {
                    *cacheSize = *cacheSize > (nuint)cache_size_from_sys_file ? *cacheSize : (nuint)cache_size_from_sys_file;

                    path_to_level_file[index] = (byte)(48 + i);

                    if (ManagedGC_Unix_ReadMemoryValueFromFile(path_to_level_file, &level) != 0)
                    {
                        *cacheLevel = (nuint)level;
                    }
                }
            }
#endif
        }

        /// <summary>
        /// <c>GetLogicalProcessorCacheSizeFromHeuristic</c>: a cache size guessed from the
        /// number of processors the process may run on.
        /// </summary>
        internal static void GetLogicalProcessorCacheSizeFromHeuristic(nuint* cacheLevel, nuint* cacheSize)
        {
            Debug.Assert(cacheLevel != null);
            Debug.Assert(cacheSize != null);

#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD
            {
                // Use the following heuristics at best depending on the CPU count
                // 1 ~ 4   :  4 MB
                // 5 ~ 16  :  8 MB
                // 17 ~ 64 : 16 MB
                // 65+     : 32 MB
                uint logicalCPUs = (uint)ManagedGC_Unix_GetProcessAffinitySet()->Count();
                if (logicalCPUs < 5)
                {
                    *cacheSize = 4;
                }
                else if (logicalCPUs < 17)
                {
                    *cacheSize = 8;
                }
                else if (logicalCPUs < 65)
                {
                    *cacheSize = 16;
                }
                else
                {
                    *cacheSize = 32;
                }

                *cacheSize *= 1024 * 1024;
            }
#endif
        }

        /// <summary><c>GetLogicalProcessorCacheSizeFromOS</c>.</summary>
        internal static nuint GetLogicalProcessorCacheSizeFromOS()
        {
            nuint cacheLevel = 0;
            nuint cacheSize = 0;

            if (GCConfig.GetGCCacheSizeFromSysConf() != 0)
            {
                GetLogicalProcessorCacheSizeFromSysConf(&cacheLevel, &cacheSize);
            }

            if (cacheSize == 0)
            {
                GetLogicalProcessorCacheSizeFromSysFs(&cacheLevel, &cacheSize);
                if (cacheSize == 0)
                {
                    GetLogicalProcessorCacheSizeFromHeuristic(&cacheLevel, &cacheSize);
                }
            }

#if TARGET_APPLE || TARGET_FREEBSD
            if (cacheSize == 0)
            {
                long cacheSizeFromSysctl = 0;
                nuint sz = (nuint)sizeof(long);
                bool success;
                // macOS: Since macOS 12.0, Apple added ".perflevelX." to determinate cache sizes for efficiency
                // and performance cores separately. "perflevel0" stands for "performance"
                fixed (byte* name = "hw.perflevel0.l3cachesize\0"u8)
                {
                    success = sysctlbyname(name, &cacheSizeFromSysctl, &sz, null, 0) == 0;
                }

                if (!success)
                {
                    fixed (byte* name = "hw.perflevel0.l2cachesize\0"u8)
                    {
                        success = sysctlbyname(name, &cacheSizeFromSysctl, &sz, null, 0) == 0;
                    }
                }

                // macOS: these report cache sizes for efficiency cores only:
                if (!success)
                {
                    fixed (byte* name = "hw.l3cachesize\0"u8)
                    {
                        success = sysctlbyname(name, &cacheSizeFromSysctl, &sz, null, 0) == 0;
                    }
                }

                if (!success)
                {
                    fixed (byte* name = "hw.l2cachesize\0"u8)
                    {
                        success = sysctlbyname(name, &cacheSizeFromSysctl, &sz, null, 0) == 0;
                    }
                }

                if (!success)
                {
                    fixed (byte* name = "hw.l1dcachesize\0"u8)
                    {
                        success = sysctlbyname(name, &cacheSizeFromSysctl, &sz, null, 0) == 0;
                    }
                }

                if (success)
                {
                    Debug.Assert(cacheSizeFromSysctl > 0);
                    cacheSize = (nuint)cacheSizeFromSysctl;
                }
            }
#endif

#if (TARGET_ARM64 || TARGET_LOONGARCH64) && !TARGET_APPLE
            if (cacheLevel != 3)
            {
                GetLogicalProcessorCacheSizeFromHeuristic(&cacheLevel, &cacheSize);
            }
#endif

            return cacheSize;
        }

        /// <summary>
        /// <c>s_maxSize</c> and <c>s_maxTrueSize</c> of
        /// <see cref="GetCacheSizePerLogicalCpu"/>. C# has no function-local statics, so the
        /// two function-local <c>static volatile size_t</c> of the C++ are fields; they keep
        /// their names and their volatility, and their initial value is the C# default, which
        /// is the zero the C++ gives them.
        /// </summary>
        internal static volatile nuint s_maxSize;

        /// <inheritdoc cref="s_maxSize"/>
        internal static volatile nuint s_maxTrueSize;

        /// <summary>Get the size of the on-die cache per logical processor.</summary>
        /// <param name="trueSize">
        /// true to return the true cache size, false to return a size scaled up based on the
        /// processor architecture
        /// </param>
        public static nuint GetCacheSizePerLogicalCpu(bool trueSize = true)
        {
            nuint size = trueSize ? s_maxTrueSize : s_maxSize;
            if (size != 0)
                return size;

            nuint maxSize, maxTrueSize;
            maxSize = maxTrueSize = GetLogicalProcessorCacheSizeFromOS(); // Returns the size of the highest level processor cache

            s_maxSize = maxSize;
            s_maxTrueSize = maxTrueSize;

            return trueSize ? maxTrueSize : maxSize;
        }

        /// <summary>
        /// Get the physical memory that this process can use. If a process runs with a restricted
        /// memory limit, it returns the limit. If there's no limit specified, it returns the
        /// amount of actual physical memory.
        /// </summary>
        /// <param name="is_restricted">
        /// If not null, set to a non-zero value when running restricted. This is the C++
        /// <c>bool*</c>, which is one byte wide.
        /// </param>
        /// <returns>non zero if it has succeeded, 0 if it has failed</returns>
        public static ulong GetPhysicalMemoryLimit(byte* is_restricted = null)
        {
            nuint restricted_limit;
            if (is_restricted != null)
                *is_restricted = 0;

            restricted_limit = GetRestrictedPhysicalMemoryLimit();
            fixed (nuint* limit = &g_RestrictedPhysicalMemoryLimit)
            {
                GCEnv.VolatileStore(limit, restricted_limit);
            }

            if (restricted_limit != 0 && restricted_limit != GCEnv.SIZE_T_MAX)
            {
                if (is_restricted != null)
                    *is_restricted = 1;
                return restricted_limit;
            }

            return (ulong)GetTotalPhysicalMemSize();
        }

        /// <summary>
        /// The inverse of the <c>static volatile bool tryReadMemInfo</c> of
        /// <see cref="GetAvailablePhysicalMemory"/>, which starts out true. The sense is stored
        /// inverted so that the field's initial value is the C# default and the class needs no
        /// static constructor.
        /// </summary>
        internal static volatile bool s_tryReadMemInfoFailed;

        /// <summary>
        /// <c>GetAvailablePhysicalMemory</c>: the amount of physical memory available for use in
        /// the system.
        /// </summary>
        internal static ulong GetAvailablePhysicalMemory()
        {
            ulong available = 0;

            // Get the physical memory available.
#if TARGET_APPLE
            uint mem_free = 0;
            nuint mem_free_length = (nuint)sizeof(uint);

            // The C++ resolves the "kern.memorystatus_level" mib once, in
            // GCToOSInterface::Initialize, into a malloc'd array it keeps for the life of the
            // process. The managed GC cannot allocate here, so the same two sysctlnametomib
            // calls collapse into one against a CTL_MAXNAME-sized stack buffer, which is the
            // largest mib the kernel will ever produce.
            int* g_kern_memorystatus_level_mib = stackalloc int[CTL_MAXNAME];
            nuint g_kern_memorystatus_level_mib_length = CTL_MAXNAME;
            int rc;
            fixed (byte* mem_free_name = "kern.memorystatus_level\0"u8)
            {
                rc = sysctlnametomib(mem_free_name, g_kern_memorystatus_level_mib, &g_kern_memorystatus_level_mib_length);
            }

            Debug.Assert(rc == 0);
            if (rc == 0)
            {
                rc = sysctl(g_kern_memorystatus_level_mib, (uint)g_kern_memorystatus_level_mib_length, &mem_free, &mem_free_length, null, 0);
                Debug.Assert(rc == 0);
                if (rc == 0)
                {
                    available = (ulong)((long)mem_free * GetTotalPhysicalMemSize() / 100);
                }
            }
#elif TARGET_FREEBSD
            // The C++ passes sizeof(size_t) even though each of these nodes is a 4-byte u_int,
            // and relies on the locals being zero-initialized for the upper half.
            nuint inactive_count = 0, laundry_count = 0, free_count = 0;
            nuint sz = (nuint)sizeof(nuint);
            fixed (byte* name = "vm.stats.vm.v_inactive_count\0"u8)
            {
                sysctlbyname(name, &inactive_count, &sz, null, 0);
            }

            sz = (nuint)sizeof(nuint);
            fixed (byte* name = "vm.stats.vm.v_laundry_count\0"u8)
            {
                sysctlbyname(name, &laundry_count, &sz, null, 0);
            }

            sz = (nuint)sizeof(nuint);
            fixed (byte* name = "vm.stats.vm.v_free_count\0"u8)
            {
                sysctlbyname(name, &free_count, &sz, null, 0);
            }

            // The multiplication is size_t wide in the C++, as it is here, so that a product that
            // does not fit a pointer-sized value truncates the same way in both.
            available = (ulong)((inactive_count + laundry_count + free_count) * minipal_getpagesize());
#else // Linux
            if (!s_tryReadMemInfoFailed)
            {
                // Ensure that we don't try to read the /proc/meminfo in successive calls to the GetAvailablePhysicalMemory
                // if we have failed to access the file or the file didn't contain the MemAvailable value.
                s_tryReadMemInfoFailed = ManagedGC_Unix_ReadMemAvailable(&available) == 0;
            }

            if (s_tryReadMemInfoFailed)
            {
                // The /proc/meminfo doesn't exist or it doesn't contain the MemAvailable row or the format of the row is invalid
                // Fall back to getting the available pages using sysconf.
                available = (ulong)(sysconf(SYSCONF_PAGES) * sysconf(_SC_PAGE_SIZE));
            }
#endif

            return available;
        }

        /// <summary><c>GetAvailablePageFile</c>: the amount of available swap space.</summary>
        internal static ulong GetAvailablePageFile()
        {
            ulong available = 0;

#if TARGET_APPLE || TARGET_FREEBSD
            int* mib = stackalloc int[3];
            int rc;
#endif

            // Get swap file size
#if TARGET_APPLE
            // This is available on OSX
            xsw_usage xsu;
            mib[0] = CTL_VM;
            mib[1] = VM_SWAPUSAGE;
            nuint length = (nuint)sizeof(xsw_usage);
            rc = sysctl(mib, 2, &xsu, &length, null, 0);
            if (rc == 0)
            {
                available = xsu.xsu_avail;
            }
#elif TARGET_FREEBSD
            // E.g. FreeBSD
            XswDev xsw;

            nuint length = 2;
            fixed (byte* name = "vm.swap_info\0"u8)
            {
                rc = sysctlnametomib(name, mib, &length);
            }

            if (rc == 0)
            {
                uint pagesize = minipal_getpagesize();
                // Aggregate the information for all swap files on the system
                for (mib[2] = 0; ; mib[2]++)
                {
                    length = (nuint)sizeof(XswDev);
                    rc = sysctl(mib, 3, &xsw, &length, null, 0);
                    if ((rc < 0) || (xsw.xsw_version != XSWDEV_VERSION))
                    {
                        // All the swap files were processed or coreclr was built against
                        // a version of headers not compatible with the current XSWDEV_VERSION.
                        break;
                    }

                    ulong avail = (ulong)(xsw.xsw_nblks - xsw.xsw_used);
                    available += avail * pagesize;
                }
            }
#elif TARGET_OPENBSD
            // OpenBSD has none of HAVE_XSW_USAGE, HAVE_XSWDEV, HAVE_SWAPCTL or HAVE_SYSINFO, so
            // the C++ leaves `available` at zero, and so does this.
#else
            // Linux
            SysInfo info;
            int rc = sysinfo(&info);
            if (rc == 0)
            {
                available = info.freeswap;

                // A newer version of the sysinfo structure represents all the sizes
                // in mem_unit instead of bytes
                available *= info.mem_unit;
            }
#endif

            return available;
        }

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
        public static void GetMemoryStatus(ulong restricted_limit, uint* memory_load, ulong* available_physical, ulong* available_page_file)
        {
            ulong available = 0;
            uint load = 0;

            nuint used;
            if (restricted_limit != 0)
            {
                // Get the physical memory in use - from it, we can get the physical memory available.
                // We do this only when we have the total physical memory available.
                if (GetPhysicalMemoryUsed(&used))
                {
                    available = restricted_limit > used ? restricted_limit - used : 0;
                    load = (uint)(((float)used * 100) / (float)restricted_limit);
                }
            }
            else
            {
                available = GetAvailablePhysicalMemory();

                if (memory_load != null)
                {
                    ulong total;
                    if (restricted_limit != 0 && restricted_limit != (ulong)GCEnv.SIZE_T_MAX)
                    {
                        total = restricted_limit;
                    }
                    else
                    {
                        total = (ulong)GetTotalPhysicalMemSize();
                    }

                    if (total > available)
                    {
                        used = (nuint)(total - available);
                        load = (uint)(((float)used * 100) / (float)total);
                    }

#if !TARGET_OPENBSD
                    // The C++ compiles this block only where HAVE_PROCFS_STATM holds, which is
                    // Linux. Where it does not, the shim reports the same (size_t)-1 that the
                    // C++ GetCurrentVirtualMemorySize reports for a /proc/self/statm it cannot
                    // read, so the extra getrlimit -- which has no side effects -- is the only
                    // difference. OpenBSD is excluded because it has no RLIMIT_AS at all.
                    Rlimit addressSpaceLimit;
                    if ((getrlimit(RLIMIT_AS, &addressSpaceLimit) == 0) && (addressSpaceLimit.rlim_cur != RLIM_INFINITY))
                    {
                        // If there is virtual address space limit set, compute virtual memory load and change
                        // the load to this one in case it is higher than the physical memory load
                        nuint used_virtual = ManagedGC_Unix_GetCurrentVirtualMemorySize();
                        if (used_virtual != GCEnv.SIZE_T_MAX)
                        {
                            uint load_virtual = (uint)(((float)used_virtual * 100) / (float)addressSpaceLimit.rlim_cur);
                            if (load_virtual > load)
                            {
                                load = load_virtual;
                            }
                        }
                    }
#endif
                }
            }

            if (available_physical != null)
                *available_physical = available;

            if (memory_load != null)
                *memory_load = load;

            if (available_page_file != null)
                *available_page_file = GetAvailablePageFile();
        }
    }
}
