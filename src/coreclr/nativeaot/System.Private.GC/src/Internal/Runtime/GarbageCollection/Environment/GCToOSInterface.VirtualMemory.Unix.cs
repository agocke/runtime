// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the virtual memory half of gc/unix/gcenv.unix.cpp, plus GetPageSize and the
// OS_PAGE_SIZE macro of env/gcenv.unix.inl. The methods appear in the order the C++ file
// declares them, and the bodies are the same statements: the mmap/munmap/mprotect/madvise
// sequences, the alignment arithmetic, and the failure values.
//
// The calls to libc are [RuntimeImport] declarations, which are direct calls to the linked
// symbol with no marshalling and no GC mode transition -- what the C++ GC gets for free by being
// native code, and what the collector needs while the world is suspended. They are in
// GCToOSInterface.Imports.Unix.cs so that the test host can substitute the same private methods
// for ones it can call and record; see tests/GCToOSInterface.Imports.Unix.TestHost.cs.
//
// The constants below are the values of the <sys/mman.h> and <sys/resource.h> macros the C++
// code uses. They are hardcoded per platform, as the AsmOffsets tables are, and checked against
// the real headers by static_asserts in nativeaot/Runtime/gcenv.managed.cpp, which is compiled
// for the target platform -- so a platform whose values differ from the ones selected here
// breaks the build rather than the process. The #if structure and the static_asserts must be
// kept in the same shape.
//
// One piece of VirtualCommitInner is not translated: the mbind() of the requested NUMA node.
// It needs g_numaAvailable, g_highestNumaNode and BindMemoryPolicy from gc/unix/numasupport.cpp,
// which is the NUMA submodule of plan step 3 in ROADMAP.md. Until that lands it stays behind the
// single ManagedGC_NUMA_BindMemoryPolicy shim, which is exactly the body of that #if block.

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe partial class GCToOSInterface
    {
        //
        // The <sys/mman.h> and <sys/resource.h> constants of gc/unix/gcenv.unix.cpp.
        //

        private const int PROT_NONE = 0x0;
        private const int PROT_READ = 0x1;
        private const int PROT_WRITE = 0x2;

        /// <summary><c>MAP_FAILED</c>, the value mmap returns on failure.</summary>
        private static void* MAP_FAILED => (void*)(nint)(-1);

        /// <summary><c>EINVAL</c>, the errno VirtualReset starts from.</summary>
        private const int EINVAL = 22;

#if TARGET_APPLE
        private const int MAP_ANON = 0x1000;
        private const int MAP_PRIVATE = 0x0002;
        private const int MAP_FIXED = 0x0010;

        // VM_FLAGS_SUPERPAGE_SIZE_ANY of <mach/vm_statistics.h>, which mmap accepts in its flags
        // argument on Apple platforms. HAVE_VM_FLAGS_SUPERPAGE_SIZE_ANY of config.gc.h.
        private const uint LargePagesFlag = 0x10000;

        // MADV_FREE of <sys/mman.h>. MADV_DONTDUMP and MADV_DODUMP are Linux-only, so the
        // coredump exclusion the Linux build performs has no counterpart here, exactly as in the
        // C++ #if defined(MADV_DONTDUMP) blocks.
        private const int MADV_FREE = 5;

        private const int RLIMIT_AS = 5;
#elif TARGET_FREEBSD
        private const int MAP_ANON = 0x1000;
        private const int MAP_PRIVATE = 0x0002;
        private const int MAP_FIXED = 0x0010;

        // Neither HAVE_MAP_HUGETLB nor HAVE_VM_FLAGS_SUPERPAGE_SIZE_ANY holds here, so
        // VirtualReserveAndCommitLargePages passes no huge page flag at all.
        private const uint LargePagesFlag = 0;

        private const int MADV_FREE = 5;

        private const int RLIMIT_AS = 10;
#elif TARGET_OPENBSD
        private const int MAP_ANON = 0x1000;
        private const int MAP_PRIVATE = 0x0002;
        private const int MAP_FIXED = 0x0010;

        // Neither HAVE_MAP_HUGETLB nor HAVE_VM_FLAGS_SUPERPAGE_SIZE_ANY holds here.
        private const uint LargePagesFlag = 0;

        private const int MADV_FREE = 6;
#else
        // Linux and Android share the asm-generic values.
        private const int MAP_ANON = 0x20;
        private const int MAP_PRIVATE = 0x02;
        private const int MAP_FIXED = 0x10;

        // MAP_HUGETLB of <sys/mman.h>. HAVE_MAP_HUGETLB of config.gc.h.
        private const uint LargePagesFlag = 0x40000;

        private const int MADV_DONTDUMP = 16;
        private const int MADV_DODUMP = 17;
        private const int MADV_FREE = 8;

        private const int RLIMIT_AS = 9;
#endif

        /// <summary>
        /// <c>RLIM_INFINITY</c> of <c>&lt;sys/resource.h&gt;</c>. It cannot be a constant
        /// because C# has no <c>nuint</c> constants.
        /// </summary>
#if TARGET_APPLE || TARGET_FREEBSD || TARGET_OPENBSD
        private static ulong RLIM_INFINITY => 0x7FFFFFFFFFFFFFFF;
#else
        private static nuint RLIM_INFINITY => nuint.MaxValue;
#endif

        /// <summary>
        /// <c>struct rlimit</c> of <c>&lt;sys/resource.h&gt;</c>, as the non-large-file
        /// <c>getrlimit</c> entry point sees it. The type name of the C++ is not available:
        /// C# reserves all-lowercase type names.
        /// </summary>
        /// <remarks>
        /// Internal rather than private only so that the test host can declare a substitute
        /// <c>getrlimit</c> that hands one back.
        /// </remarks>
        internal struct Rlimit
        {
#if TARGET_APPLE || TARGET_FREEBSD || TARGET_OPENBSD
            // rlim_t is 64 bits wide on the BSDs regardless of pointer size, and RLIM_INFINITY
            // is the largest positive value rather than the all-ones one.
            public ulong rlim_cur;
            public ulong rlim_max;
#else
            // rlim_t is `unsigned long` for the non-large-file getrlimit, so it follows the
            // pointer size, and RLIM_INFINITY is ~0ul.
            public nuint rlim_cur;
            public nuint rlim_max;
#endif
        }

        /// <summary>
        /// <c>OS_PAGE_SIZE</c> of <c>env/gcenv.unix.inl</c>.
        /// </summary>
        private static nuint OS_PAGE_SIZE => GetPageSize();

        /// <summary>Get the size of an OS memory page.</summary>
        /// <remarks>
        /// The C++ version reads <c>g_pageSizeUnixInl</c>, which <c>GCToOSInterface::Initialize</c>
        /// fills in from <c>sysconf(_SC_PAGE_SIZE)</c>. The managed GC does not run that
        /// initialization -- NativeAOT does, from <c>PalInit</c> -- so it reads the same value
        /// from the shared <c>minipal_getpagesize</c>, which caches one <c>sysconf</c> call per
        /// process. That is also what the C++ <c>GetPageSize</c> is on Windows and WASM.
        /// </remarks>
        public static nuint GetPageSize() => minipal_getpagesize();

        //
        // Virtual memory management
        //

        /// <summary>
        /// Reserve virtual memory range.
        /// </summary>
        /// <param name="size">size of the virtual memory range</param>
        /// <param name="alignment">requested memory alignment, 0 means no specific alignment requested</param>
        /// <param name="flags">flags to control special settings like write watching</param>
        /// <param name="hugePagesFlag">the mmap flag that requests huge pages, if any</param>
        /// <param name="committing">memory will be comitted</param>
        /// <returns>Starting virtual address of the reserved range</returns>
        // committing is only read where MADV_DONTDUMP exists, which is the same platforms as in
        // the C++.
#pragma warning disable IDE0060
        private static void* VirtualReserveInner(nuint size, nuint alignment, uint flags, uint hugePagesFlag, bool committing)
        {
            // "WriteWatch not supported on Unix"
            Debug.Assert((flags & (uint)VirtualReserveFlags.WriteWatch) == 0);
            if (alignment < OS_PAGE_SIZE)
            {
                alignment = OS_PAGE_SIZE;
            }

            nuint alignedSize = size + (alignment - OS_PAGE_SIZE);
            int mmapFlags = MAP_ANON | MAP_PRIVATE | (int)hugePagesFlag;
            void* pRetVal = mmap(null, alignedSize, PROT_NONE, mmapFlags, -1, 0);

            if (pRetVal != MAP_FAILED)
            {
                void* pAlignedRetVal = (void*)(((nuint)pRetVal + (alignment - 1)) & ~(alignment - 1));
                nuint startPadding = (nuint)pAlignedRetVal - (nuint)pRetVal;
                if (startPadding != 0)
                {
                    int ret = munmap(pRetVal, startPadding);
                    Debug.Assert(ret == 0);
                }

                nuint endPadding = alignedSize - (startPadding + size);
                if (endPadding != 0)
                {
                    int ret = munmap((void*)((nuint)pAlignedRetVal + size), endPadding);
                    Debug.Assert(ret == 0);
                }

                pRetVal = pAlignedRetVal;
#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD
                // Do not include reserved uncommitted memory in coredump.
                if (!committing)
                {
                    madvise(pRetVal, size, MADV_DONTDUMP);
                }
#endif
                return pRetVal;
            }

            return null; // return NULL if mmap failed
        }
#pragma warning restore IDE0060

        /// <summary>
        /// Reserve virtual memory range. Returns the starting virtual address of the reserved
        /// range, or null on failure.
        /// </summary>
        /// <param name="size">size of the virtual memory range</param>
        /// <param name="alignment">requested memory alignment</param>
        /// <param name="flags">flags to control special settings like write watching</param>
        /// <param name="node">the NUMA node to reserve memory on</param>
        /// <remarks>
        /// Previous uses of this API aligned the <paramref name="size"/> parameter to the
        /// platform allocation granularity. This is not required by POSIX or Windows. Windows
        /// will round the size up to the nearest page boundary. POSIX does not specify what is
        /// done, but Linux probably also rounds up.
        /// <para>
        /// Windows guarantees that the returned mapping will be aligned to the allocation
        /// granularity.
        /// </para>
        /// </remarks>
        // A Unix reservation is not backed by anything, so the C++ ignores the node here too.
#pragma warning disable IDE0060
        public static byte* VirtualReserve(nuint size, nuint alignment, uint flags, ushort node = NUMA_NODE_UNDEFINED)
        {
            return (byte*)VirtualReserveInner(size, alignment, flags, 0, /* committing */ false);
        }
#pragma warning restore IDE0060

        /// <summary>
        /// Release virtual memory range previously reserved using <see cref="VirtualReserve"/>.
        /// </summary>
        /// <param name="address">starting virtual address</param>
        /// <param name="size">size of the virtual memory range</param>
        /// <returns>true if it has succeeded, false if it has failed</returns>
        public static bool VirtualRelease(void* address, nuint size)
        {
            int ret = munmap(address, size);

            return ret == 0;
        }

        /// <summary>
        /// Commit virtual memory range. It must be part of a range reserved using
        /// <see cref="VirtualReserve"/>.
        /// </summary>
        /// <param name="address">starting virtual address</param>
        /// <param name="size">size of the virtual memory range</param>
        /// <param name="node">the NUMA node to commit memory on</param>
        /// <param name="newMemory">memory has been newly allocated</param>
        /// <returns>true if it has succeeded, false if it has failed</returns>
        // newMemory is only read where MADV_DODUMP exists, which is the same platforms as in the
        // C++.
#pragma warning disable IDE0060
        private static bool VirtualCommitInner(void* address, nuint size, ushort node, bool newMemory)
        {
            bool success = mprotect(address, size, PROT_WRITE | PROT_READ) == 0;

#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD
            if (success && !newMemory)
            {
                // Include committed memory in coredump. New memory is included by default.
                madvise(address, size, MADV_DODUMP);
            }
#endif

            // The rest of the C++ body -- the node mask and the mbind() that places the range on
            // the requested NUMA node -- is the one part of this file that is still native. It
            // reads the NUMA state that only gc/unix/numasupport.cpp has, and it is deleted with
            // the NUMA submodule of plan step 3 in ROADMAP.md. If the mbind fails, we still
            // return the allocated memory since the node is just a hint.
            if (success && node != NUMA_NODE_UNDEFINED)
            {
                ManagedGC_NUMA_BindMemoryPolicy(address, size, node);
            }

            return success;
        }
#pragma warning restore IDE0060

        /// <summary>
        /// Commit virtual memory range. It must be part of a range reserved using
        /// <see cref="VirtualReserve"/>.
        /// </summary>
        /// <param name="address">starting virtual address</param>
        /// <param name="size">size of the virtual memory range</param>
        /// <param name="node">the NUMA node to commit memory on</param>
        /// <returns>true if it has succeeded, false if it has failed</returns>
        public static bool VirtualCommit(void* address, nuint size, ushort node = NUMA_NODE_UNDEFINED)
        {
            return VirtualCommitInner(address, size, node, /* newMemory */ false);
        }

        /// <summary>Reserve and commit a virtual memory range for large pages.</summary>
        /// <param name="size">size of the virtual memory range</param>
        /// <param name="node">the NUMA node to commit memory on</param>
        /// <returns>Starting virtual address of the committed range</returns>
        /// <remarks>
        /// As in the C++, the commit runs even when the reservation failed -- <c>mprotect</c>
        /// then fails on the null pointer -- and a reservation whose commit fails is not
        /// released. Both are properties of the C++ implementation, not of this translation.
        /// </remarks>
        public static byte* VirtualReserveAndCommitLargePages(nuint size, ushort node = NUMA_NODE_UNDEFINED)
        {
            uint largePagesFlag = LargePagesFlag;

            void* pRetVal = VirtualReserveInner(size, OS_PAGE_SIZE, 0, largePagesFlag, true);
            if (VirtualCommitInner(pRetVal, size, node, /* newMemory */ true))
            {
                return (byte*)pRetVal;
            }

            return null;
        }

        /// <summary>Decommit virtual memory range.</summary>
        /// <param name="address">starting virtual address</param>
        /// <param name="size">size of the virtual memory range</param>
        /// <returns>true if it has succeeded, false if it has failed</returns>
        public static bool VirtualDecommit(void* address, nuint size)
        {
            // TODO: This can fail, however the GC does not handle the failure gracefully
            // Explicitly calling mmap instead of mprotect here makes it
            // that much more clear to the operating system that we no
            // longer need these pages. Also, GC depends on re-committed pages to
            // be zeroed-out.
            int mmapFlags = MAP_FIXED | MAP_ANON | MAP_PRIVATE;
            bool bRetVal = mmap(address, size, PROT_NONE, mmapFlags, -1, 0) != MAP_FAILED;

#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD
            if (bRetVal)
            {
                // Do not include freed memory in coredump.
                madvise(address, size, MADV_DONTDUMP);
            }
#endif

            return bRetVal;
        }

        /// <summary>
        /// Reset virtual memory range. Indicates that data in the memory range specified by
        /// <paramref name="address"/> and <paramref name="size"/> is no longer of interest, but
        /// it should not be decommitted.
        /// </summary>
        /// <param name="address">starting virtual address</param>
        /// <param name="size">size of the virtual memory range</param>
        /// <param name="unlock">true if the memory range should also be unlocked</param>
        /// <returns>true if it has succeeded, false if it has failed</returns>
        public static bool VirtualReset(void* address, nuint size, bool unlock)
        {
            int st = EINVAL;

#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD
            // Do not include reset memory in coredump.
            st = madvise(address, size, MADV_DONTDUMP);
#endif

            // Tell the kernel that the application doesn't need the pages in the range.
            // Freeing the pages can be delayed until a memory pressure occurs.
            //
            // The C++ falls back to posix_madvise(POSIX_MADV_DONTNEED) where MADV_FREE is not
            // available, which is Solaris and illumos. NativeAOT does not support either, so
            // that branch has no managed counterpart.
            st = madvise(address, size, MADV_FREE);

            return st == 0;
        }

        //
        // Global memory info
        //

        /// <summary>
        /// Return the size of the available user-mode portion of the virtual address space of
        /// this process.
        /// </summary>
        /// <returns>
        /// non zero if it has succeeded, <see cref="GetVirtualMemoryMaxAddress"/> if not available
        /// </returns>
        public static nuint GetVirtualMemoryLimit()
        {
#if !TARGET_OPENBSD
            Rlimit addressSpaceLimit;
            if ((getrlimit(RLIMIT_AS, &addressSpaceLimit) == 0) && (addressSpaceLimit.rlim_cur != RLIM_INFINITY))
            {
                return (nuint)addressSpaceLimit.rlim_cur;
            }
#endif

            // No virtual memory limit
            return GetVirtualMemoryMaxAddress();
        }

        /// <summary>
        /// Return the maximum address of the of the virtual address space of this process.
        /// </summary>
        /// <returns>non zero if it has succeeded, 0 if it has failed</returns>
        public static nuint GetVirtualMemoryMaxAddress()
        {
#if TARGET_64BIT
#if !TARGET_RISCV64
            // There is no API to get the total virtual address space size on
            // Unix, so we use a constant value representing 128TB, which is
            // the approximate size of total user virtual address space on
            // the currently supported Unix systems.
            const ulong _128TB = 1ul << 47;
            return unchecked((nuint)_128TB);
#else // TARGET_RISCV64
            // For RISC-V Linux Kernel SV39 virtual memory limit is 256gb.
            const ulong _256GB = 1ul << 38;
            return unchecked((nuint)_256GB);
#endif // TARGET_RISCV64
#else
            return unchecked((nuint)(-1));
#endif
        }
    }
}
