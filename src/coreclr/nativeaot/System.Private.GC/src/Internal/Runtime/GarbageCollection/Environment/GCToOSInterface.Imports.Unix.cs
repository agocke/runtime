// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The libc entry points that the Unix virtual memory, thread and memory limit ports of
// GCToOSInterface call, declared exactly as <sys/mman.h>, <sys/resource.h>, <time.h>, <sched.h>,
// <errno.h>, <unistd.h>, <sys/sysctl.h> and <sys/sysinfo.h> declare them, plus the handful of
// shims that stand in for the pieces of gc/unix/gcenv.unix.cpp and gc/unix/cgroup.cpp that are
// not translated yet.
//
// They are [RuntimeImport]s rather than [DllImport]s: a runtime import is a direct call to a
// linked symbol with no marshalling, no argument copying, no lazy binding step and no GC mode
// transition, which is what code that runs with the world suspended requires. Every one of them
// is a symbol the NativeAOT application already links -- libc, or the aotminipal static library
// that Microsoft.NETCore.Native.*.targets adds to every link.
//
// This file is compiled into the shipping library only. The xUnit tests compile
// tests/GCToOSInterface.Imports.Unix.TestHost.cs in its place, which declares the same methods
// as ordinary P/Invokes so that the ported logic above them can be exercised, and records their
// arguments so that the flag translation can be asserted.

using System.Runtime;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe partial class GCToOSInterface
    {
        [RuntimeImport(RuntimeLibrary, "mmap")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void* mmap(void* addr, nuint length, int prot, int flags, int fd, nint offset);

        [RuntimeImport(RuntimeLibrary, "munmap")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int munmap(void* addr, nuint length);

        [RuntimeImport(RuntimeLibrary, "mprotect")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int mprotect(void* addr, nuint len, int prot);

        [RuntimeImport(RuntimeLibrary, "madvise")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int madvise(void* addr, nuint length, int advice);

        [RuntimeImport(RuntimeLibrary, "getrlimit")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int getrlimit(int resource, Rlimit* rlim);

        /// <summary>
        /// <c>nanosleep</c> of <c>&lt;time.h&gt;</c>, which the sleep port retries with the
        /// interval it reports back in <paramref name="rem"/>.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "nanosleep")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int nanosleep(timespec* req, timespec* rem);

        /// <summary><c>sched_yield</c> of <c>&lt;sched.h&gt;</c>.</summary>
        [RuntimeImport(RuntimeLibrary, "sched_yield")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int sched_yield();

        /// <summary>
        /// The accessor that returns the address of the calling thread's <c>errno</c>. The
        /// <c>&lt;errno.h&gt;</c> <c>errno</c> macro is a dereference of exactly this call on
        /// every platform, because <c>errno</c> is a thread-local that only the C library can
        /// locate.
        /// </summary>
        /// <remarks>
        /// The symbol differs per C library, and [RuntimeImport] names the symbol, so the
        /// managed name and the entry point need not agree -- the glibc name is kept for all of
        /// them, as the port keeps the C++ names elsewhere. glibc and musl export
        /// <c>__errno_location</c>, Apple's libSystem and FreeBSD's libc export <c>__error</c>,
        /// and bionic and OpenBSD export <c>__errno</c>. There is nothing constant to assert
        /// about a function name; a platform whose C library has none of these fails the managed
        /// link, which is the same outcome as the C++ failing to compile.
        /// </remarks>
#if TARGET_APPLE || TARGET_FREEBSD
        [RuntimeImport(RuntimeLibrary, "__error")]
#elif TARGET_BIONIC || TARGET_OPENBSD
        [RuntimeImport(RuntimeLibrary, "__errno")]
#else
        [RuntimeImport(RuntimeLibrary, "__errno_location")]
#endif
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int* __errno_location();

        /// <summary>
        /// The shared page size helper of <c>src/native/minipal/ospagesize.h</c>, which caches
        /// one <c>sysconf(_SC_PAGESIZE)</c> per process. The C++ <c>GetPageSize</c> uses it
        /// directly on Windows and WASM, and on Unix it reads the copy of the same value that
        /// <c>GCToOSInterface::Initialize</c> caches.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "minipal_getpagesize")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern uint minipal_getpagesize();

        /// <summary>
        /// <c>sysconf</c> of <c>&lt;unistd.h&gt;</c>. Its <c>long</c> return is a native word on
        /// every supported platform, and -1 means "no limit" or "not known".
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "sysconf")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern nint sysconf(int name);

#if TARGET_APPLE || TARGET_FREEBSD
        /// <summary><c>sysctl</c> of <c>&lt;sys/sysctl.h&gt;</c>.</summary>
        [RuntimeImport(RuntimeLibrary, "sysctl")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int sysctl(int* name, uint namelen, void* oldp, nuint* oldlenp, void* newp, nuint newlen);

        /// <summary><c>sysctlbyname</c> of <c>&lt;sys/sysctl.h&gt;</c>. HAVE_SYSCTLBYNAME.</summary>
        [RuntimeImport(RuntimeLibrary, "sysctlbyname")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int sysctlbyname(byte* name, void* oldp, nuint* oldlenp, void* newp, nuint newlen);

        /// <summary><c>sysctlnametomib</c> of <c>&lt;sys/sysctl.h&gt;</c>.</summary>
        [RuntimeImport(RuntimeLibrary, "sysctlnametomib")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int sysctlnametomib(byte* name, int* mibp, nuint* sizep);
#elif !TARGET_OPENBSD
        /// <summary><c>sysinfo</c> of <c>&lt;sys/sysinfo.h&gt;</c>. HAVE_SYSINFO.</summary>
        [RuntimeImport(RuntimeLibrary, "sysinfo")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int sysinfo(SysInfo* info);
#endif

        /// <summary>
        /// The NUMA half of <c>VirtualCommitInner</c>: places the range on the requested node
        /// with <c>mbind</c> when the process has NUMA support.
        /// </summary>
        /// <remarks>
        /// The only part of the virtual memory submodule that is still native. It depends on
        /// <c>g_numaAvailable</c>, <c>g_highestNumaNode</c> and <c>BindMemoryPolicy</c> of
        /// <c>gc/unix/numasupport.cpp</c>, and is deleted together with the NUMA submodule of
        /// plan step 3 in ROADMAP.md.
        /// </remarks>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_NUMA_BindMemoryPolicy")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_NUMA_BindMemoryPolicy(void* address, nuint size, ushort node);

        //
        // The six pieces of the memory limit port that are still native, each one shim named
        // after the C++ function it is. They are the file parsers -- getline, sscanf and
        // strtok_r all allocate, and the managed GC has no allocator -- plus the address of the
        // process affinity set that GCToOSInterface::Initialize fills in. See
        // GCToOSInterface.MemoryLimits.Unix.cs for what each of them stands for and where it is
        // deleted.
        //

#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD
        /// <summary><c>CGroup::GetPhysicalMemoryLimit</c> of <c>gc/unix/cgroup.cpp</c>.</summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_CGroup_GetPhysicalMemoryLimit")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_CGroup_GetPhysicalMemoryLimit(ulong* val);

        /// <summary><c>GetPhysicalMemoryUsed</c> of <c>gc/unix/cgroup.cpp</c>.</summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_Unix_GetPhysicalMemoryUsed")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_Unix_GetPhysicalMemoryUsed(nuint* val);
#endif

#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD && !TARGET_ARM && !TARGET_X86
        /// <summary><c>ReadMemoryValueFromFile</c> of <c>gc/unix/gcenv.unix.cpp</c>.</summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_Unix_ReadMemoryValueFromFile")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_Unix_ReadMemoryValueFromFile(byte* filename, ulong* val);
#endif

#if !TARGET_APPLE && !TARGET_FREEBSD
        /// <summary><c>ReadMemAvailable</c> of <c>gc/unix/gcenv.unix.cpp</c>.</summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_Unix_ReadMemAvailable")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_Unix_ReadMemAvailable(ulong* memAvailable);
#endif

#if !TARGET_OPENBSD
        /// <summary>
        /// <c>GetCurrentVirtualMemorySize</c> of <c>gc/unix/gcenv.unix.cpp</c>. Where
        /// HAVE_PROCFS_STATM does not hold the C++ has no such function and skips the whole
        /// block; the shim reports the <c>(size_t)-1</c> that stands for "not known" there, so
        /// the managed caller takes the same path.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_Unix_GetCurrentVirtualMemorySize")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern nuint ManagedGC_Unix_GetCurrentVirtualMemorySize();
#endif

#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD
        /// <summary>
        /// The address of <c>g_processAffinitySet</c> of <c>gc/unix/gcenv.unix.cpp</c>. Only
        /// the address crosses: the counting the cache size heuristic does is the ported
        /// <see cref="AffinitySet.Count"/>.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_Unix_GetProcessAffinitySet")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern AffinitySet* ManagedGC_Unix_GetProcessAffinitySet();
#endif
    }
}
