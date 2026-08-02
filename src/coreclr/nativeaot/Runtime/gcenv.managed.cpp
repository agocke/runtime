// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Native side of the managed GC's environment layer.
//
// System.Private.GC translates gcenv.os.h, gcenv.base.h, gcenv.interlocked.h and volatile.h to
// C#. Everything in those headers that is pure computation is translated outright, as are the
// whole of virtual memory management and write watching, the GCEvent condition variable /
// Win32 event, the CLRCriticalSection mutex, and the thread, processor, affinity, NUMA and CPU
// group methods; what is left -- initialization, shutdown and the debug break -- is, for now,
// forwarded here and implemented by the existing C++ GCToOSInterface in
// gc/unix/gcenv.unix.cpp and gc/windows/gcenv.windows.cpp.
//
// This file also carries the static_asserts that check the <sys/mman.h>, <sys/resource.h>,
// <pthread.h>, <time.h>, <errno.h>, <unistd.h>, <sched.h>, <sys/sysctl.h>, <sys/sysinfo.h>,
// <minipal/time.h>, config.gc.h, <windows.h> and <psapi.h> constants, layouts and entry points
// that those managed ports hardcode against the real headers of the platform being built, in the
// same spirit as AsmOffsets.h. The C# #if structure and the one below must stay in the same shape.
//
// The managed side calls these with [RuntimeImport], which is a direct call to a linked symbol
// with no marshalling, no argument copying and no GC mode transition. That is what code running
// with the world suspended requires, and it is why these cannot be [DllImport]s.
//
// Each forwarder is deliberately a single expression with no logic of its own, so that the
// managed declaration and the C++ declaration can be diffed against each other. The three that
// remain exist because NativeAOT still initializes the C++ GCToOSInterface from PalInit, before
// any managed code runs. Deletion point: the initialization submodule of plan step 3 of
// System.Private.GC/ROADMAP.md, together with moving that call out of PalInit.
//
// The narrow leaves of the memory limit, processor count, affinity and NUMA ports are not here:
// ManagedGC_CGroup_GetPhysicalMemoryLimit and ManagedGC_Unix_GetPhysicalMemoryUsed are in
// gc/unix/cgroup.cpp; ManagedGC_Unix_ReadMemoryValueFromFile, ManagedGC_Unix_ReadMemAvailable,
// ManagedGC_Unix_GetCurrentVirtualMemorySize, ManagedGC_Unix_GetProcessAffinitySet,
// ManagedGC_Unix_GetTotalCpuCount, ManagedGC_Unix_GetConfiguredCpuCount,
// ManagedGC_Unix_GetCurrentThreadId, ManagedGC_Unix_GetNumaAvailable,
// ManagedGC_Unix_GetHighestNumaNode, ManagedGC_Unix_GetNumaNodeNumByCpu and
// ManagedGC_Unix_BindMemoryPolicy are in gc/unix/gcenv.unix.cpp; and
// ManagedGC_Windows_GetTotalCpuCount, ManagedGC_Windows_GetSystemInfoProcessorCount,
// ManagedGC_Windows_GetProcessAffinitySet, ManagedGC_Windows_GetCanEnableGCNumaAware,
// ManagedGC_Windows_GetNumaNodeCount, ManagedGC_Windows_GetCanEnableGCCPUGroups,
// ManagedGC_Windows_GetCpuGroupCount, ManagedGC_Windows_GetCpuGroupActiveProcessorCount and
// ManagedGC_Windows_GetCpuGroupBegin are in gc/windows/gcenv.windows.cpp. Each of them
// wraps something with internal linkage in its own translation unit -- the CGroup class is in an
// anonymous namespace, the file parsers are static and the CPU group, NUMA and affinity state is
// file or namespace static -- or, in the case of minipal_get_current_thread_id and of the
// C++-mangled numasupport.cpp entry points, something that is not a linkable symbol the managed
// side can name at all, so they have to sit next to what they wrap. They are guarded by
// FEATURE_MANAGED_GC there, so a default build does not carry them either.
//
// This file is only compiled into the managedgc-enabled archive, so a default (C++ GC) build
// does not carry any of it.

#include "common.h"
#define SKIP_TRACING_DEFINITIONS
#include "gcenv.h"
#undef SKIP_TRACING_DEFINITIONS

#include <string.h>

// The managed GCEvent is a struct with a single pointer field, laid out exactly like the C++
// one. Nothing about GCEvent crosses between the two any more -- GCEvent.Unix.cs and
// GCEvent.Windows.cs implement it -- but the collector's own data structures embed events, so
// the size stays pinned here as well as in GCInterfaceOffsets.h.
static_assert(sizeof(GCEvent) == sizeof(void*), "The managed GCEvent mirrors the C++ one as a single pointer.");

//
// Virtual memory management and write watching are ported: GCToOSInterface.VirtualMemory.*.cs
// and GCToOSInterface.WriteWatch.*.cs of System.Private.GC call mmap/munmap/mprotect/madvise/
// getrlimit and VirtualAlloc/VirtualFree/VirtualAllocExNuma/GetWriteWatch/ResetWriteWatch
// directly. Two pieces remain here.
//
// The first is this check of the platform constants those files hardcode. A managed source file
// cannot include a C header, so the values are written out per platform and asserted here
// against the platform being built; a platform whose values differ from the ones the C# selects
// breaks the build instead of the process.
//

#ifdef TARGET_UNIX

#include <sys/mman.h>
#include <sys/resource.h>
#include <errno.h>

#if defined(TARGET_APPLE)
#include <mach/vm_statistics.h>
#endif

static_assert(PROT_NONE == 0x0, "PROT_NONE does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(PROT_READ == 0x1, "PROT_READ does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(PROT_WRITE == 0x2, "PROT_WRITE does not match GCToOSInterface.VirtualMemory.Unix.cs.");
// MAP_FAILED is ((void*)-1) on every POSIX system, but a pointer cast is not a constant
// expression, so it cannot be asserted here the way the integer constants are.
static_assert(EINVAL == 22, "EINVAL does not match GCToOSInterface.VirtualMemory.Unix.cs.");

#if defined(TARGET_APPLE)

static_assert(MAP_ANON == 0x1000, "MAP_ANON does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(MAP_PRIVATE == 0x0002, "MAP_PRIVATE does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(MAP_FIXED == 0x0010, "MAP_FIXED does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(VM_FLAGS_SUPERPAGE_SIZE_ANY == 0x10000, "The large page flag does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(MADV_FREE == 5, "MADV_FREE does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(RLIMIT_AS == 5, "RLIMIT_AS does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(RLIM_INFINITY == (rlim_t)0x7FFFFFFFFFFFFFFF, "RLIM_INFINITY does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(sizeof(rlim_t) == sizeof(uint64_t), "struct rlimit does not match GCToOSInterface.VirtualMemory.Unix.cs.");

#elif defined(TARGET_FREEBSD)

static_assert(MAP_ANON == 0x1000, "MAP_ANON does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(MAP_PRIVATE == 0x0002, "MAP_PRIVATE does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(MAP_FIXED == 0x0010, "MAP_FIXED does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(MADV_FREE == 5, "MADV_FREE does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(RLIMIT_AS == 10, "RLIMIT_AS does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(RLIM_INFINITY == (rlim_t)0x7FFFFFFFFFFFFFFF, "RLIM_INFINITY does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(sizeof(rlim_t) == sizeof(uint64_t), "struct rlimit does not match GCToOSInterface.VirtualMemory.Unix.cs.");

#elif defined(TARGET_OPENBSD)

static_assert(MAP_ANON == 0x1000, "MAP_ANON does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(MAP_PRIVATE == 0x0002, "MAP_PRIVATE does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(MAP_FIXED == 0x0010, "MAP_FIXED does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(MADV_FREE == 6, "MADV_FREE does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(RLIM_INFINITY == (rlim_t)0x7FFFFFFFFFFFFFFF, "RLIM_INFINITY does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(sizeof(rlim_t) == sizeof(uint64_t), "struct rlimit does not match GCToOSInterface.VirtualMemory.Unix.cs.");

#else // Linux and Android.

static_assert(MAP_ANON == 0x20, "MAP_ANON does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(MAP_PRIVATE == 0x02, "MAP_PRIVATE does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(MAP_FIXED == 0x10, "MAP_FIXED does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(MAP_HUGETLB == 0x40000, "The large page flag does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(MADV_DONTDUMP == 16, "MADV_DONTDUMP does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(MADV_DODUMP == 17, "MADV_DODUMP does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(MADV_FREE == 8, "MADV_FREE does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(RLIMIT_AS == 9, "RLIMIT_AS does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(RLIM_INFINITY == (rlim_t)~(uintptr_t)0, "RLIM_INFINITY does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(sizeof(rlim_t) == sizeof(uintptr_t), "struct rlimit does not match GCToOSInterface.VirtualMemory.Unix.cs.");

#endif

static_assert(sizeof(struct rlimit) == 2 * sizeof(rlim_t), "struct rlimit does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(offsetof(struct rlimit, rlim_cur) == 0, "struct rlimit does not match GCToOSInterface.VirtualMemory.Unix.cs.");
static_assert(offsetof(struct rlimit, rlim_max) == sizeof(rlim_t), "struct rlimit does not match GCToOSInterface.VirtualMemory.Unix.cs.");

//
// The <pthread.h>, <time.h> and <errno.h> values that the event and lock ports of
// GCEvent.Unix.cs, GCEnvSync.Unix.cs and SyncTypes.Unix.cs hardcode.
//
// The pthread types are opaque blobs there, so only their size and alignment matter; the
// managed ones are deliberately larger than any platform needs. struct timespec is not opaque --
// the deadline arithmetic of GCEvent::Impl::Wait writes its fields -- so its layout is asserted
// exactly, in the two variants the C# selects between.
//

#include <pthread.h>
#include <time.h>

static_assert(sizeof(pthread_mutex_t) <= 16 * sizeof(uint64_t), "pthread_mutex_t does not fit the blob of SyncTypes.Unix.cs.");
static_assert(alignof(pthread_mutex_t) <= alignof(uint64_t), "pthread_mutex_t is more strictly aligned than the blob of SyncTypes.Unix.cs.");
static_assert(sizeof(pthread_cond_t) <= 16 * sizeof(uint64_t), "pthread_cond_t does not fit the blob of SyncTypes.Unix.cs.");
static_assert(alignof(pthread_cond_t) <= alignof(uint64_t), "pthread_cond_t is more strictly aligned than the blob of SyncTypes.Unix.cs.");
static_assert(sizeof(pthread_mutexattr_t) <= 8 * sizeof(uint64_t), "pthread_mutexattr_t does not fit the blob of SyncTypes.Unix.cs.");
static_assert(alignof(pthread_mutexattr_t) <= alignof(uint64_t), "pthread_mutexattr_t is more strictly aligned than the blob of SyncTypes.Unix.cs.");
static_assert(sizeof(pthread_condattr_t) <= 8 * sizeof(uint64_t), "pthread_condattr_t does not fit the blob of SyncTypes.Unix.cs.");
static_assert(alignof(pthread_condattr_t) <= alignof(uint64_t), "pthread_condattr_t is more strictly aligned than the blob of SyncTypes.Unix.cs.");

#ifdef TARGET_LINUX_MUSL

// musl widens time_t to 64 bits on every architecture, and pads the following long tv_nsec out
// to the same width. The managed struct is a 64-bit tv_sec, a native-word tv_nsec and, on a
// 32-bit architecture, an explicit padding field -- which places tv_nsec at offset 8 only on a
// little-endian architecture, the only kind this branch is built for.
static_assert(sizeof(time_t) == 8, "time_t does not match SyncTypes.Unix.cs.");
static_assert(sizeof(struct timespec) == 16, "struct timespec does not match SyncTypes.Unix.cs.");
static_assert(sizeof(((struct timespec*)nullptr)->tv_nsec) == sizeof(long), "struct timespec does not match SyncTypes.Unix.cs.");
static_assert(offsetof(struct timespec, tv_sec) == 0, "struct timespec does not match SyncTypes.Unix.cs.");
static_assert(offsetof(struct timespec, tv_nsec) == 8, "struct timespec does not match SyncTypes.Unix.cs.");
static_assert(__BYTE_ORDER__ == __ORDER_LITTLE_ENDIAN__, "struct timespec does not match SyncTypes.Unix.cs.");

#else

static_assert(sizeof(struct timespec) == 2 * sizeof(intptr_t), "struct timespec does not match SyncTypes.Unix.cs.");
static_assert(sizeof(time_t) == sizeof(intptr_t), "struct timespec does not match SyncTypes.Unix.cs.");
static_assert(offsetof(struct timespec, tv_sec) == 0, "struct timespec does not match SyncTypes.Unix.cs.");
static_assert(offsetof(struct timespec, tv_nsec) == sizeof(intptr_t), "struct timespec does not match SyncTypes.Unix.cs.");

#endif // TARGET_LINUX_MUSL

#if defined(TARGET_APPLE)

static_assert(PTHREAD_MUTEX_RECURSIVE == 2, "PTHREAD_MUTEX_RECURSIVE does not match GCEnvSync.Unix.cs.");
static_assert(CLOCK_UPTIME_RAW == 8, "CLOCK_UPTIME_RAW does not match GCEvent.Unix.cs.");
static_assert(ETIMEDOUT == 60, "ETIMEDOUT does not match GCEvent.Unix.cs.");

// HAVE_CLOCK_GETTIME_NSEC_NP of config.gc.h is what picks the relative timed wait of
// GCEvent::Impl::Wait, and TARGET_APPLE stands for it in the C#. There is nothing to assert
// about a function's existence, but a platform that had neither that function nor
// pthread_condattr_setclock would fail to link the managed GC, which is the same outcome as the
// #error the C++ carries for that case.

#elif defined(TARGET_FREEBSD)

static_assert(PTHREAD_MUTEX_RECURSIVE == 2, "PTHREAD_MUTEX_RECURSIVE does not match GCEnvSync.Unix.cs.");
static_assert(CLOCK_MONOTONIC == 4, "CLOCK_MONOTONIC does not match GCEvent.Unix.cs.");
static_assert(ETIMEDOUT == 60, "ETIMEDOUT does not match GCEvent.Unix.cs.");

#elif defined(TARGET_OPENBSD)

static_assert(PTHREAD_MUTEX_RECURSIVE == 2, "PTHREAD_MUTEX_RECURSIVE does not match GCEnvSync.Unix.cs.");
static_assert(CLOCK_MONOTONIC == 3, "CLOCK_MONOTONIC does not match GCEvent.Unix.cs.");
static_assert(ETIMEDOUT == 60, "ETIMEDOUT does not match GCEvent.Unix.cs.");

#else // Linux and Android.

static_assert(PTHREAD_MUTEX_RECURSIVE == 1, "PTHREAD_MUTEX_RECURSIVE does not match GCEnvSync.Unix.cs.");
static_assert(CLOCK_MONOTONIC == 1, "CLOCK_MONOTONIC does not match GCEvent.Unix.cs.");
static_assert(ETIMEDOUT == 110, "ETIMEDOUT does not match GCEvent.Unix.cs.");

#endif

//
// The <errno.h>, <time.h> and <sched.h> pieces that the sleep and yield port of
// GCToOSInterface.Thread.Unix.cs names.
//
// EINTR is a constant, so it is compared. The three entry points are not: a function name has
// no value to assert against. Naming each of them in an unevaluated `sizeof` expression instead
// checks what actually matters -- that the platform being built declares it, that it accepts
// the arguments the managed declaration passes, and that it returns what the managed
// declaration expects -- without depending on the attributes (`__THROW`, and so `noexcept` in
// C++17) that the C libraries differ in. A platform that has none of the three errno accessors
// breaks this build rather than the managed link.
//

#include <sched.h>

static_assert(EINTR == 4, "EINTR does not match GCToOSInterface.Thread.Unix.cs.");
static_assert(sizeof(nanosleep((const struct timespec*)nullptr, (struct timespec*)nullptr)) == sizeof(int),
    "nanosleep does not match GCToOSInterface.Imports.Unix.cs.");
static_assert(sizeof(sched_yield()) == sizeof(int), "sched_yield does not match GCToOSInterface.Imports.Unix.cs.");

// The `errno` macro of <errno.h> is a dereference of exactly this accessor, which is the only
// way to reach the thread's own copy from outside the C library. Its name is the one thing
// about errno that is per C library rather than per operating system, so the condition is
// __BIONIC__ rather than TARGET_ANDROID: the linux-bionic RID uses bionic without being Android
// as the native build labels it, and TARGET_BIONIC of System.Private.GC.csproj is defined for
// both. OpenBSD uses the same accessor name and has its own target define on both sides.
#if defined(TARGET_APPLE) || defined(TARGET_FREEBSD)
static_assert(sizeof(*__error()) == sizeof(int), "The errno accessor does not match GCToOSInterface.Imports.Unix.cs.");
#elif defined(__BIONIC__) || defined(TARGET_OPENBSD)
static_assert(sizeof(*__errno()) == sizeof(int), "The errno accessor does not match GCToOSInterface.Imports.Unix.cs.");
#else // glibc, musl and any other C library that exports the glibc name.
static_assert(sizeof(*__errno_location()) == sizeof(int), "The errno accessor does not match GCToOSInterface.Imports.Unix.cs.");
#endif

//
// The <unistd.h>, <sys/sysctl.h> and <sys/sysinfo.h> values that the memory limit and cache
// sizing port of GCToOSInterface.MemoryLimits.Unix.cs hardcodes.
//
// The sysconf names are per C library rather than per operating system, so the branches below
// are the ones the C# selects between. Where the C++ compiles a block only if the platform
// defines a name -- the sysconf cache sizes, _SC_AVPHYS_PAGES behind the SYSCONF_PAGES macro --
// the C# has already decided which platforms those are, so the check here is that the name is
// there where the C# expects it and absent where the C# leaves the block out. That is the
// fail-closed half: a platform that grows one of these breaks this build instead of silently
// taking a path the C++ would not have taken.
//

#include <unistd.h>

#if defined(TARGET_APPLE) || defined(TARGET_FREEBSD)
#include <sys/types.h>
#include <sys/sysctl.h>
#endif

#if defined(TARGET_FREEBSD)
#include <vm/vm_param.h>
#endif

#if !defined(TARGET_APPLE) && !defined(TARGET_FREEBSD) && !defined(TARGET_OPENBSD)
#include <sys/sysinfo.h>
#endif

static_assert(sizeof(sysconf(0)) == sizeof(intptr_t), "sysconf does not match GCToOSInterface.Imports.Unix.cs.");

#if defined(TARGET_APPLE)

static_assert(_SC_PAGE_SIZE == 29, "_SC_PAGE_SIZE does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(_SC_PHYS_PAGES == 200, "_SC_PHYS_PAGES does not match GCToOSInterface.MemoryLimits.Unix.cs.");

// The C++ leaves SYSCONF_PAGES undefined on Apple and takes the kern.memorystatus_level path
// instead, which is what the C# does; there is no _SC_AVPHYS_PAGES here to fall back to.
#ifdef _SC_AVPHYS_PAGES
#error "Apple now defines _SC_AVPHYS_PAGES; GCToOSInterface.MemoryLimits.Unix.cs has no SYSCONF_PAGES for it."
#endif

static_assert(CTL_MAXNAME == 12, "CTL_MAXNAME does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(CTL_VM == 2, "CTL_VM does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(VM_SWAPUSAGE == 5, "VM_SWAPUSAGE does not match GCToOSInterface.MemoryLimits.Unix.cs.");

static_assert(sizeof(struct xsw_usage) == 32, "struct xsw_usage does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(offsetof(struct xsw_usage, xsu_total) == 0, "struct xsw_usage does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(offsetof(struct xsw_usage, xsu_avail) == 8, "struct xsw_usage does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(offsetof(struct xsw_usage, xsu_used) == 16, "struct xsw_usage does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(offsetof(struct xsw_usage, xsu_pagesize) == 24, "struct xsw_usage does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(offsetof(struct xsw_usage, xsu_encrypted) == 28, "struct xsw_usage does not match GCToOSInterface.MemoryLimits.Unix.cs.");

#elif defined(TARGET_FREEBSD)

static_assert(_SC_PAGE_SIZE == 47, "_SC_PAGE_SIZE does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(_SC_PHYS_PAGES == 121, "_SC_PHYS_PAGES does not match GCToOSInterface.MemoryLimits.Unix.cs.");

// FreeBSD has no _SC_AVPHYS_PAGES, so the SYSCONF_PAGES of the C++ is _SC_PHYS_PAGES, which is
// what the C# selects.
#ifdef _SC_AVPHYS_PAGES
#error "FreeBSD now defines _SC_AVPHYS_PAGES; GCToOSInterface.MemoryLimits.Unix.cs points SYSCONF_PAGES at _SC_PHYS_PAGES."
#endif

static_assert(XSWDEV_VERSION == 2, "XSWDEV_VERSION does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(sizeof(struct xswdev) == 32, "struct xswdev does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(offsetof(struct xswdev, xsw_version) == 0, "struct xswdev does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(offsetof(struct xswdev, xsw_dev) == 8, "struct xswdev does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(offsetof(struct xswdev, xsw_flags) == 16, "struct xswdev does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(offsetof(struct xswdev, xsw_nblks) == 20, "struct xswdev does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(offsetof(struct xswdev, xsw_used) == 24, "struct xswdev does not match GCToOSInterface.MemoryLimits.Unix.cs.");

#elif defined(TARGET_OPENBSD)

static_assert(_SC_PAGE_SIZE == 28, "_SC_PAGE_SIZE does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(_SC_PHYS_PAGES == 500, "_SC_PHYS_PAGES does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(_SC_AVPHYS_PAGES == 501, "_SC_AVPHYS_PAGES does not match GCToOSInterface.MemoryLimits.Unix.cs.");

#elif defined(__BIONIC__)

static_assert(_SC_PAGE_SIZE == 0x27, "_SC_PAGE_SIZE does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(_SC_PHYS_PAGES == 0x62, "_SC_PHYS_PAGES does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(_SC_AVPHYS_PAGES == 0x63, "_SC_AVPHYS_PAGES does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(_SC_LEVEL1_DCACHE_SIZE == 0x92, "_SC_LEVEL1_DCACHE_SIZE does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(_SC_LEVEL2_CACHE_SIZE == 0x95, "_SC_LEVEL2_CACHE_SIZE does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(_SC_LEVEL3_CACHE_SIZE == 0x98, "_SC_LEVEL3_CACHE_SIZE does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(_SC_LEVEL4_CACHE_SIZE == 0x9b, "_SC_LEVEL4_CACHE_SIZE does not match GCToOSInterface.MemoryLimits.Unix.cs.");

#elif defined(TARGET_LINUX_MUSL)

static_assert(_SC_PAGE_SIZE == 30, "_SC_PAGE_SIZE does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(_SC_PHYS_PAGES == 85, "_SC_PHYS_PAGES does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(_SC_AVPHYS_PAGES == 86, "_SC_AVPHYS_PAGES does not match GCToOSInterface.MemoryLimits.Unix.cs.");

// musl defines none of the four sysconf cache names, so the C++ compiles the body of
// GetLogicalProcessorCacheSizeFromSysConf away there and the C# leaves it out.
#if defined(_SC_LEVEL1_DCACHE_SIZE) || defined(_SC_LEVEL2_CACHE_SIZE) || defined(_SC_LEVEL3_CACHE_SIZE) || defined(_SC_LEVEL4_CACHE_SIZE)
#error "musl now defines a _SC_LEVEL*_CACHE_SIZE; GetLogicalProcessorCacheSizeFromSysConf of GCToOSInterface.MemoryLimits.Unix.cs is empty there."
#endif

#else // glibc and any other C library that exports the glibc names.

static_assert(_SC_PAGE_SIZE == 30, "_SC_PAGE_SIZE does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(_SC_PHYS_PAGES == 85, "_SC_PHYS_PAGES does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(_SC_AVPHYS_PAGES == 86, "_SC_AVPHYS_PAGES does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(_SC_LEVEL1_DCACHE_SIZE == 188, "_SC_LEVEL1_DCACHE_SIZE does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(_SC_LEVEL2_CACHE_SIZE == 191, "_SC_LEVEL2_CACHE_SIZE does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(_SC_LEVEL3_CACHE_SIZE == 194, "_SC_LEVEL3_CACHE_SIZE does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(_SC_LEVEL4_CACHE_SIZE == 197, "_SC_LEVEL4_CACHE_SIZE does not match GCToOSInterface.MemoryLimits.Unix.cs.");

#endif

#if defined(TARGET_APPLE) || defined(TARGET_FREEBSD)

// The three <sys/sysctl.h> entry points of GetAvailablePhysicalMemory, GetAvailablePageFile and
// GetLogicalProcessorCacheSizeFromOS. As with nanosleep above, a function name has no value to
// compare, so each is named in an unevaluated sizeof that checks the platform declares it, takes
// the arguments the managed declaration passes and returns what it expects.
static_assert(sizeof(sysctl((int*)nullptr, 0, nullptr, (size_t*)nullptr, nullptr, 0)) == sizeof(int),
    "sysctl does not match GCToOSInterface.Imports.Unix.cs.");
static_assert(sizeof(sysctlbyname((const char*)nullptr, nullptr, (size_t*)nullptr, nullptr, 0)) == sizeof(int),
    "sysctlbyname does not match GCToOSInterface.Imports.Unix.cs.");
static_assert(sizeof(sysctlnametomib((const char*)nullptr, (int*)nullptr, (size_t*)nullptr)) == sizeof(int),
    "sysctlnametomib does not match GCToOSInterface.Imports.Unix.cs.");

// The four sysconf cache names are a glibc extension; neither BSD has them, so the C++ compiles
// the body of GetLogicalProcessorCacheSizeFromSysConf away and the C# leaves it out.
#if defined(_SC_LEVEL1_DCACHE_SIZE) || defined(_SC_LEVEL2_CACHE_SIZE) || defined(_SC_LEVEL3_CACHE_SIZE) || defined(_SC_LEVEL4_CACHE_SIZE)
#error "This platform now defines a _SC_LEVEL*_CACHE_SIZE; GetLogicalProcessorCacheSizeFromSysConf of GCToOSInterface.MemoryLimits.Unix.cs is empty there."
#endif

#elif defined(TARGET_OPENBSD)

#if defined(_SC_LEVEL1_DCACHE_SIZE) || defined(_SC_LEVEL2_CACHE_SIZE) || defined(_SC_LEVEL3_CACHE_SIZE) || defined(_SC_LEVEL4_CACHE_SIZE)
#error "This platform now defines a _SC_LEVEL*_CACHE_SIZE; GetLogicalProcessorCacheSizeFromSysConf of GCToOSInterface.MemoryLimits.Unix.cs is empty there."
#endif

#else // Linux and Android.

// struct sysinfo of <sys/sysinfo.h>, which GetAvailablePageFile fills in. Only freeswap and
// mem_unit are read, so those two offsets are pinned exactly; the rest of the managed structure
// exists so that the kernel has somewhere to write, and only has to be at least as large as the
// platform's. The C++ reads mem_unit only where HAVE_SYSINFO_WITH_MEM_UNIT holds, which is a
// configure check the C# cannot see; naming the field here is the same test.
static_assert(sizeof(sysinfo((struct sysinfo*)nullptr)) == sizeof(int), "sysinfo does not match GCToOSInterface.Imports.Unix.cs.");
static_assert(sizeof(((struct sysinfo*)nullptr)->mem_unit) == sizeof(uint32_t), "struct sysinfo does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(offsetof(struct sysinfo, freeswap) == (sizeof(void*) == 8 ? 72 : 36), "struct sysinfo does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(offsetof(struct sysinfo, mem_unit) == (sizeof(void*) == 8 ? 104 : 52), "struct sysinfo does not match GCToOSInterface.MemoryLimits.Unix.cs.");
static_assert(sizeof(struct sysinfo) <= offsetof(struct sysinfo, mem_unit) + sizeof(uint32_t) + 256, "struct sysinfo does not fit the one of GCToOSInterface.MemoryLimits.Unix.cs.");

#endif

//
// The three src/native/minipal/time.h entry points that the timer port of
// GCToOSInterface.Timers.Unix.cs calls. The C++ QueryPerformanceCounter,
// QueryPerformanceFrequency and GetLowPrecisionTimeStamp are one call each to exactly these,
// so the managed port calls them rather than re-deriving the per-platform clock selection of
// time.c, whose configure checks (HAVE_CLOCK_GETTIME_NSEC_NP, HAVE_CLOCK_MONOTONIC_COARSE) the
// C# cannot see. As with nanosleep above, a function name has no value to compare, so each is
// named in an unevaluated sizeof that checks it is declared, takes no arguments and returns the
// int64_t the managed declaration expects.
//

#include <minipal/time.h>

static_assert(sizeof(minipal_hires_ticks()) == sizeof(int64_t), "minipal_hires_ticks does not match GCToOSInterface.Imports.Unix.cs.");
static_assert(sizeof(minipal_hires_tick_frequency()) == sizeof(int64_t), "minipal_hires_tick_frequency does not match GCToOSInterface.Imports.Unix.cs.");
static_assert(sizeof(minipal_lowres_ticks()) == sizeof(int64_t), "minipal_lowres_ticks does not match GCToOSInterface.Imports.Unix.cs.");

//
// The processor count and identity port of GCToOSInterface.Processors.Unix.cs. HAVE_SCHED_GETCPU
// is a configure check -- gc/unix/configure.cmake compiles and runs a program that calls
// sched_getcpu() -- and a managed source file cannot see it, so the C# names the platforms it
// holds on directly: absent on Apple, FreeBSD and OpenBSD, present on glibc, musl and bionic.
// This is where that list is checked against the real check, so that a platform whose configure
// result differs breaks the build instead of silently taking the wrong branch. The #if shape
// here and the one in the C# must stay the same.
//

#include "config.gc.h"
#include <sched.h>
#include <unistd.h>

#if defined(TARGET_APPLE) || defined(TARGET_FREEBSD) || defined(TARGET_OPENBSD)

static_assert(HAVE_SCHED_GETCPU == 0, "HAVE_SCHED_GETCPU does not match GCToOSInterface.Processors.Unix.cs.");

#else

static_assert(HAVE_SCHED_GETCPU == 1, "HAVE_SCHED_GETCPU does not match GCToOSInterface.Processors.Unix.cs.");
static_assert(sizeof(sched_getcpu()) == sizeof(int32_t), "sched_getcpu does not match GCToOSInterface.Imports.Unix.cs.");

#endif

// pid_t is a 32 bit signed integer on every supported platform, which is what the managed
// declaration names and what the C++ body's implicit conversion to uint32_t assumes.
static_assert(sizeof(getpid()) == sizeof(int32_t), "getpid does not match GCToOSInterface.Imports.Unix.cs.");

//
// The affinity port of GCToOSInterface.Processors.Unix.cs. The C++ SetThreadAffinity has a two
// level #if that neither arm of the C# can see: the outer one compiles the body at all where
// HAVE_SCHED_SETAFFINITY or HAVE_PTHREAD_SETAFFINITY_NP holds, and the inner one picks
// sched_setaffinity(0, ...) where the first holds and pthread_setaffinity_np(pthread_self(), ...)
// where only the second does. Both are configure checks of gc/unix/configure.cmake, so the C#
// spells them as platform lists -- the outer as "not Apple and not OpenBSD", the inner as "not
// FreeBSD" -- and both arms are checked here so that a target whose configure result differs
// breaks the build instead of silently losing thread affinitization. The #if shape here and the
// one in the C# must stay the same.
//
// The C++ sizes its set with CPU_ALLOC_SIZE and fills it with CPU_ZERO_S / CPU_SET_S, all of
// which are macros. The managed body writes the same bytes out of the same arithmetic -- one
// uintptr_t-sized word per 8 * sizeof(uintptr_t) processors, rounded up, with the single bit of
// the requested processor set in a buffer that ManagedGC_AllocZeroed has already zeroed -- so
// what has to hold is that the C library's macro produces that same size. Three counts pin the
// whole formula: one processor takes exactly one word (so the mask element is pointer-sized), a
// full word of processors still takes one, and one more takes two.
//
// cpu_set_t and pthread_setaffinity_np come from the same two headers gcenv.unix.cpp takes them
// from, selected by the same two configure checks.
//

#if HAVE_PTHREAD_NP_H
#include <pthread_np.h>
#endif

#if HAVE_CPUSET_T
typedef cpuset_t cpu_set_t;
#endif

#if defined(TARGET_APPLE) || defined(TARGET_OPENBSD)

static_assert(HAVE_SCHED_SETAFFINITY == 0, "HAVE_SCHED_SETAFFINITY does not match GCToOSInterface.Processors.Unix.cs.");
static_assert(HAVE_PTHREAD_SETAFFINITY_NP == 0, "HAVE_PTHREAD_SETAFFINITY_NP does not match GCToOSInterface.Processors.Unix.cs.");

#else

static_assert(CPU_ALLOC_SIZE(1) == sizeof(uintptr_t), "CPU_ALLOC_SIZE does not match GCToOSInterface.Processors.Unix.cs.");
static_assert(CPU_ALLOC_SIZE(8 * sizeof(uintptr_t)) == sizeof(uintptr_t), "CPU_ALLOC_SIZE does not match GCToOSInterface.Processors.Unix.cs.");
static_assert(CPU_ALLOC_SIZE(8 * sizeof(uintptr_t) + 1) == 2 * sizeof(uintptr_t), "CPU_ALLOC_SIZE does not match GCToOSInterface.Processors.Unix.cs.");

#if defined(TARGET_FREEBSD)

static_assert(HAVE_SCHED_SETAFFINITY == 0, "HAVE_SCHED_SETAFFINITY does not match GCToOSInterface.Processors.Unix.cs.");
static_assert(HAVE_PTHREAD_SETAFFINITY_NP == 1, "HAVE_PTHREAD_SETAFFINITY_NP does not match GCToOSInterface.Processors.Unix.cs.");
// pthread_t is opaque and only handed straight back to pthread_setaffinity_np, so the managed
// declaration spells it as the pointer-sized value it is here.
static_assert(sizeof(pthread_t) == sizeof(uintptr_t), "pthread_t does not match GCToOSInterface.Imports.Unix.cs.");
static_assert(sizeof(pthread_self()) == sizeof(uintptr_t), "pthread_self does not match GCToOSInterface.Imports.Unix.cs.");
static_assert(sizeof(pthread_setaffinity_np(pthread_self(), (size_t)0, (const cpu_set_t*)nullptr)) == sizeof(int32_t),
    "pthread_setaffinity_np does not match GCToOSInterface.Imports.Unix.cs.");

#else

static_assert(HAVE_SCHED_SETAFFINITY == 1, "HAVE_SCHED_SETAFFINITY does not match GCToOSInterface.Processors.Unix.cs.");
static_assert(sizeof(sched_setaffinity(0, (size_t)0, (const cpu_set_t*)nullptr)) == sizeof(int32_t),
    "sched_setaffinity does not match GCToOSInterface.Imports.Unix.cs.");

#endif

#endif

//
// The NUMA port of GCToOSInterface.Processors.Unix.cs and of the mbind half of
// VirtualCommitInner in GCToOSInterface.VirtualMemory.Unix.cs. The C++ compiles both of those
// blocks under `#if defined(TARGET_LINUX) && !defined(TARGET_ANDROID)`. TARGET_LINUX is not a
// define System.Private.GC.csproj has, so the C# spells the same set the way it spells
// HAVE_SCHED_GETCPU -- every Unix that is not Apple, FreeBSD or OpenBSD -- and excludes Android
// explicitly. The two selections must name the same platforms, so each arm below checks that the
// other spelling agrees; a target that gains or loses one breaks this build rather than silently
// taking a branch the C++ would not have taken. This is TARGET_ANDROID rather than the
// __BIONIC__ used for the errno accessor above, because the C++ block is keyed on the operating
// system and the linux-bionic RID is Linux there while still using bionic.
//

#if !defined(TARGET_APPLE) && !defined(TARGET_FREEBSD) && !defined(TARGET_OPENBSD) && !defined(TARGET_ANDROID)
#if !defined(TARGET_LINUX) || defined(TARGET_ANDROID)
#error "The NUMA platform selection of GCToOSInterface.Processors.Unix.cs does not match gc/unix/gcenv.unix.cpp."
#endif
#else
#if defined(TARGET_LINUX) && !defined(TARGET_ANDROID)
#error "The NUMA platform selection of GCToOSInterface.Processors.Unix.cs does not match gc/unix/gcenv.unix.cpp."
#endif
#endif

// The two entry points of gc/unix/numasupport.cpp behind the ManagedGC_Unix_GetNumaNodeNumByCpu
// and ManagedGC_Unix_BindMemoryPolicy shims of gc/unix/gcenv.unix.cpp. numasupport.h declares
// them with C++ linkage, so the managed side cannot name their mangled symbols and reaches them
// through those two shims instead; the shims repeat these signatures, so this is where the
// widths the managed declarations name are pinned. `long` and `unsigned long` are what
// numasupport.h uses, and on Unix -- LP64 everywhere except a 32 bit ILP32 target, never the
// LLP64 that Windows uses -- both are exactly the width of a pointer, which is what the managed
// `nint` and `nuint` are. The nodemask element type follows from the same equality, which is why
// the managed body indexes it with nuint.
int GetNumaNodeNumByCpu(int cpu);
long BindMemoryPolicy(void* start, unsigned long len, const unsigned long* nodemask, unsigned long maxnode);

static_assert(sizeof(long) == sizeof(intptr_t), "BindMemoryPolicy does not return the nint of GCToOSInterface.Imports.Unix.cs.");
static_assert(sizeof(unsigned long) == sizeof(uintptr_t), "BindMemoryPolicy does not take the nuint of GCToOSInterface.Imports.Unix.cs.");
static_assert(sizeof(GetNumaNodeNumByCpu(0)) == sizeof(int32_t), "GetNumaNodeNumByCpu does not match GCToOSInterface.Imports.Unix.cs.");
static_assert(sizeof(BindMemoryPolicy(nullptr, 0, (const unsigned long*)nullptr, 0)) == sizeof(intptr_t),
    "BindMemoryPolicy does not match GCToOSInterface.Imports.Unix.cs.");

#else // TARGET_UNIX

static_assert(MEM_COMMIT == 0x00001000, "MEM_COMMIT does not match GCToOSInterface.VirtualMemory.Windows.cs.");
static_assert(MEM_RESERVE == 0x00002000, "MEM_RESERVE does not match GCToOSInterface.VirtualMemory.Windows.cs.");
static_assert(MEM_DECOMMIT == 0x00004000, "MEM_DECOMMIT does not match GCToOSInterface.VirtualMemory.Windows.cs.");
static_assert(MEM_RELEASE == 0x00008000, "MEM_RELEASE does not match GCToOSInterface.VirtualMemory.Windows.cs.");
static_assert(MEM_RESET == 0x00080000, "MEM_RESET does not match GCToOSInterface.VirtualMemory.Windows.cs.");
static_assert(MEM_LARGE_PAGES == 0x20000000, "MEM_LARGE_PAGES does not match GCToOSInterface.VirtualMemory.Windows.cs.");
static_assert(MEM_WRITE_WATCH == 0x00200000, "MEM_WRITE_WATCH does not match GCToOSInterface.VirtualMemory.Windows.cs.");
static_assert(PAGE_READWRITE == 0x04, "PAGE_READWRITE does not match GCToOSInterface.VirtualMemory.Windows.cs.");
static_assert(TOKEN_ADJUST_PRIVILEGES == 0x0020, "TOKEN_ADJUST_PRIVILEGES does not match GCToOSInterface.VirtualMemory.Windows.cs.");
static_assert(SE_PRIVILEGE_ENABLED == 0x00000002, "SE_PRIVILEGE_ENABLED does not match GCToOSInterface.VirtualMemory.Windows.cs.");

static_assert(sizeof(LUID) == 8, "LUID does not match GCToOSInterface.VirtualMemory.Windows.cs.");
static_assert(sizeof(TOKEN_PRIVILEGES) == 16, "TOKEN_PRIVILEGES does not match GCToOSInterface.VirtualMemory.Windows.cs.");
static_assert(offsetof(TOKEN_PRIVILEGES, Privileges) == 4, "TOKEN_PRIVILEGES does not match GCToOSInterface.VirtualMemory.Windows.cs.");
static_assert(sizeof(MEMORYSTATUSEX) == 64, "MEMORYSTATUSEX does not match GCToOSInterface.VirtualMemory.Windows.cs.");
static_assert(offsetof(MEMORYSTATUSEX, ullTotalPhys) == 8, "MEMORYSTATUSEX does not match GCToOSInterface.VirtualMemory.Windows.cs.");
static_assert(offsetof(MEMORYSTATUSEX, ullAvailVirtual) == 48, "MEMORYSTATUSEX does not match GCToOSInterface.VirtualMemory.Windows.cs.");

// The write watch port hardcodes the reset flag and reads dwAllocationGranularity out of a
// SYSTEM_INFO it declares itself.
static_assert(WRITE_WATCH_FLAG_RESET == 1, "WRITE_WATCH_FLAG_RESET does not match GCToOSInterface.WriteWatch.Windows.cs.");
static_assert(sizeof(SYSTEM_INFO) == (sizeof(void*) == 8 ? 48 : 36), "SYSTEM_INFO does not match GCToOSInterface.WriteWatch.Windows.cs.");
static_assert(offsetof(SYSTEM_INFO, dwPageSize) == 4, "SYSTEM_INFO does not match GCToOSInterface.WriteWatch.Windows.cs.");
static_assert(offsetof(SYSTEM_INFO, dwAllocationGranularity) == (sizeof(void*) == 8 ? 40 : 28), "SYSTEM_INFO does not match GCToOSInterface.WriteWatch.Windows.cs.");

// The managed GetPageSize returns 4096, which is what minipal_getpagesize is on Windows: an
// inline function with no symbol to call and no way to static_assert its result.

// The sleep and yield port of GCToOSInterface.Thread.Windows.cs passes FALSE for bAlertable and
// discards the return of both calls. There are no constants to check, so what is checked is the
// same thing the Unix side checks about its libc entry points: that <windows.h> declares each
// one, that it accepts the arguments the managed declaration passes, and that it returns what
// the managed declaration expects.
static_assert(FALSE == 0, "The bAlertable argument of GCToOSInterface.Thread.Windows.cs is not FALSE.");
static_assert(sizeof(SleepEx(0, FALSE)) == sizeof(uint32_t), "SleepEx does not match GCToOSInterface.Imports.Windows.cs.");
static_assert(sizeof(SwitchToThread()) == sizeof(int32_t), "SwitchToThread does not match GCToOSInterface.Imports.Windows.cs.");

//
// The <windows.h> and <psapi.h> values that the memory limit and cache sizing port of
// GCToOSInterface.MemoryLimits.Windows.cs hardcodes.
//

#include <psapi.h>

static_assert(JobObjectExtendedLimitInformation == 9, "JobObjectExtendedLimitInformation does not match GCToOSInterface.MemoryLimits.Windows.cs.");
static_assert(JOB_OBJECT_LIMIT_WORKINGSET == 0x00000001, "JOB_OBJECT_LIMIT_WORKINGSET does not match GCToOSInterface.MemoryLimits.Windows.cs.");
static_assert(JOB_OBJECT_LIMIT_PROCESS_MEMORY == 0x00000100, "JOB_OBJECT_LIMIT_PROCESS_MEMORY does not match GCToOSInterface.MemoryLimits.Windows.cs.");
static_assert(JOB_OBJECT_LIMIT_JOB_MEMORY == 0x00000200, "JOB_OBJECT_LIMIT_JOB_MEMORY does not match GCToOSInterface.MemoryLimits.Windows.cs.");
static_assert(RelationCache == 2, "RelationCache does not match GCToOSInterface.MemoryLimits.Windows.cs.");
static_assert(ERROR_INSUFFICIENT_BUFFER == 122, "ERROR_INSUFFICIENT_BUFFER does not match GCToOSInterface.MemoryLimits.Windows.cs.");

static_assert(sizeof(JOBOBJECT_BASIC_LIMIT_INFORMATION) == (sizeof(void*) == 8 ? 64 : 48), "JOBOBJECT_BASIC_LIMIT_INFORMATION does not match GCToOSInterface.MemoryLimits.Windows.cs.");
static_assert(offsetof(JOBOBJECT_BASIC_LIMIT_INFORMATION, LimitFlags) == 16, "JOBOBJECT_BASIC_LIMIT_INFORMATION does not match GCToOSInterface.MemoryLimits.Windows.cs.");
static_assert(sizeof(IO_COUNTERS) == 48, "IO_COUNTERS does not match GCToOSInterface.MemoryLimits.Windows.cs.");
static_assert(sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION) == (sizeof(void*) == 8 ? 144 : 112), "JOBOBJECT_EXTENDED_LIMIT_INFORMATION does not match GCToOSInterface.MemoryLimits.Windows.cs.");
static_assert(offsetof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION, IoInfo) == (sizeof(void*) == 8 ? 64 : 48), "JOBOBJECT_EXTENDED_LIMIT_INFORMATION does not match GCToOSInterface.MemoryLimits.Windows.cs.");
static_assert(offsetof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION, ProcessMemoryLimit) == (sizeof(void*) == 8 ? 112 : 96), "JOBOBJECT_EXTENDED_LIMIT_INFORMATION does not match GCToOSInterface.MemoryLimits.Windows.cs.");
static_assert(offsetof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JobMemoryLimit) == (sizeof(void*) == 8 ? 120 : 100), "JOBOBJECT_EXTENDED_LIMIT_INFORMATION does not match GCToOSInterface.MemoryLimits.Windows.cs.");

static_assert(sizeof(PROCESS_MEMORY_COUNTERS) == (sizeof(void*) == 8 ? 72 : 40), "PROCESS_MEMORY_COUNTERS does not match GCToOSInterface.MemoryLimits.Windows.cs.");
static_assert(offsetof(PROCESS_MEMORY_COUNTERS, WorkingSetSize) == (sizeof(void*) == 8 ? 16 : 12), "PROCESS_MEMORY_COUNTERS does not match GCToOSInterface.MemoryLimits.Windows.cs.");

static_assert(sizeof(CACHE_DESCRIPTOR) == 12, "CACHE_DESCRIPTOR does not match GCToOSInterface.MemoryLimits.Windows.cs.");
static_assert(offsetof(CACHE_DESCRIPTOR, Size) == 4, "CACHE_DESCRIPTOR does not match GCToOSInterface.MemoryLimits.Windows.cs.");
static_assert(sizeof(SYSTEM_LOGICAL_PROCESSOR_INFORMATION) == (sizeof(void*) == 8 ? 32 : 24), "SYSTEM_LOGICAL_PROCESSOR_INFORMATION does not match GCToOSInterface.MemoryLimits.Windows.cs.");
static_assert(offsetof(SYSTEM_LOGICAL_PROCESSOR_INFORMATION, Relationship) == sizeof(void*), "SYSTEM_LOGICAL_PROCESSOR_INFORMATION does not match GCToOSInterface.MemoryLimits.Windows.cs.");
static_assert(offsetof(SYSTEM_LOGICAL_PROCESSOR_INFORMATION, Cache) == (sizeof(void*) == 8 ? 16 : 8), "SYSTEM_LOGICAL_PROCESSOR_INFORMATION does not match GCToOSInterface.MemoryLimits.Windows.cs.");

// The four entry points, checked the same way the Unix libc ones are: named in an unevaluated
// sizeof, which fails to compile unless <windows.h> or <psapi.h> declares it with the signature
// the managed declaration passes and returns what the managed declaration expects.
static_assert(sizeof(::IsProcessInJob(nullptr, nullptr, (PBOOL)nullptr)) == sizeof(int32_t), "IsProcessInJob does not match GCToOSInterface.Imports.Windows.cs.");
static_assert(sizeof(::QueryInformationJobObject(nullptr, JobObjectExtendedLimitInformation, nullptr, 0, (LPDWORD)nullptr)) == sizeof(int32_t), "QueryInformationJobObject does not match GCToOSInterface.Imports.Windows.cs.");
static_assert(sizeof(::GetLogicalProcessorInformation((PSYSTEM_LOGICAL_PROCESSOR_INFORMATION)nullptr, (PDWORD)nullptr)) == sizeof(int32_t), "GetLogicalProcessorInformation does not match GCToOSInterface.Imports.Windows.cs.");

// The managed declaration names K32GetProcessMemoryInfo rather than GetProcessMemoryInfo: that
// is the entry point kernel32 exports and the one every psapi.h since PSAPI_VERSION 2 redirects
// the documented name to, and kernel32.lib is on the default NativeAOT link line where
// psapi.lib is not. A psapi.h that stopped redirecting would leave the managed GC naming a
// symbol that is only in psapi.dll, so check the redirect rather than assume it.
#ifndef GetProcessMemoryInfo
#error "psapi.h no longer redirects GetProcessMemoryInfo to K32GetProcessMemoryInfo, which is the symbol GCToOSInterface.Imports.Windows.cs names."
#endif
static_assert(sizeof(::K32GetProcessMemoryInfo(nullptr, (PPROCESS_MEMORY_COUNTERS)nullptr, 0)) == sizeof(int32_t), "K32GetProcessMemoryInfo does not match GCToOSInterface.Imports.Windows.cs.");

//
// The <windows.h> pieces that the timer port of GCToOSInterface.Timers.Windows.cs names. The
// LARGE_INTEGER that the first two fill is spelled as its QuadPart there, which is exact
// because a union member always begins at the start of the union; what has to hold is that the
// union is no wider than that field, and that the field is the int64_t the C# declares. The
// three entry points are checked the way the Unix libc ones are, in an unevaluated sizeof.
//
static_assert(sizeof(LARGE_INTEGER) == sizeof(int64_t), "LARGE_INTEGER does not match GCToOSInterface.Imports.Windows.cs.");
static_assert(sizeof(((LARGE_INTEGER*)nullptr)->QuadPart) == sizeof(int64_t), "LARGE_INTEGER::QuadPart does not match GCToOSInterface.Imports.Windows.cs.");
static_assert(sizeof(ULONGLONG) == sizeof(uint64_t), "ULONGLONG does not match GCToOSInterface.Imports.Windows.cs.");
static_assert(sizeof(::QueryPerformanceCounter((LARGE_INTEGER*)nullptr)) == sizeof(int32_t), "QueryPerformanceCounter does not match GCToOSInterface.Imports.Windows.cs.");
static_assert(sizeof(::QueryPerformanceFrequency((LARGE_INTEGER*)nullptr)) == sizeof(int32_t), "QueryPerformanceFrequency does not match GCToOSInterface.Imports.Windows.cs.");
static_assert(sizeof(::QueryUnbiasedInterruptTime((PULONGLONG)nullptr)) == sizeof(int32_t), "QueryUnbiasedInterruptTime does not match GCToOSInterface.Imports.Windows.cs.");

// The event and lock ports of GCEvent.Windows.cs, GCEnvSync.Windows.cs and SyncTypes.Windows.cs
// hardcode one <windows.h> value and one type. The CRITICAL_SECTION is an opaque blob there, so
// only its size and alignment matter; the managed one is deliberately larger than any platform
// needs. INVALID_HANDLE_VALUE is a pointer cast, which is not a constant expression, so it
// cannot be asserted here the way an integer constant is.
static_assert(sizeof(CRITICAL_SECTION) <= 8 * sizeof(uint64_t), "CRITICAL_SECTION does not fit the blob of SyncTypes.Windows.cs.");
static_assert(alignof(CRITICAL_SECTION) <= alignof(uint64_t), "CRITICAL_SECTION is more strictly aligned than the blob of SyncTypes.Windows.cs.");

//
// The <windows.h> pieces that the processor count and identity port of
// GCToOSInterface.Processors.Windows.cs names. GetCurrentProcessorNumberEx returns VOID, which
// cannot be the operand of sizeof, so the call is wrapped in a comma expression whose type is
// int; the call itself is still unevaluated, so what is checked is the same thing the other
// entry points check -- that <windows.h> declares it and accepts the argument the managed
// declaration passes.
//
static_assert(sizeof(PROCESSOR_NUMBER) == 4, "PROCESSOR_NUMBER does not match GCToOSInterface.Processors.Windows.cs.");
static_assert(offsetof(PROCESSOR_NUMBER, Group) == 0, "PROCESSOR_NUMBER does not match GCToOSInterface.Processors.Windows.cs.");
static_assert(offsetof(PROCESSOR_NUMBER, Number) == 2, "PROCESSOR_NUMBER does not match GCToOSInterface.Processors.Windows.cs.");
static_assert(offsetof(PROCESSOR_NUMBER, Reserved) == 3, "PROCESSOR_NUMBER does not match GCToOSInterface.Processors.Windows.cs.");
static_assert(sizeof(((PROCESSOR_NUMBER*)nullptr)->Group) == sizeof(uint16_t), "PROCESSOR_NUMBER does not match GCToOSInterface.Processors.Windows.cs.");
static_assert(sizeof(((PROCESSOR_NUMBER*)nullptr)->Number) == sizeof(uint8_t), "PROCESSOR_NUMBER does not match GCToOSInterface.Processors.Windows.cs.");
static_assert(sizeof(::GetCurrentThreadId()) == sizeof(uint32_t), "GetCurrentThreadId does not match GCToOSInterface.Imports.Windows.cs.");
static_assert(sizeof(::GetCurrentProcessId()) == sizeof(uint32_t), "GetCurrentProcessId does not match GCToOSInterface.Imports.Windows.cs.");
static_assert(sizeof((::GetCurrentProcessorNumberEx((PPROCESSOR_NUMBER)nullptr), 0)) == sizeof(int32_t), "GetCurrentProcessorNumberEx does not match GCToOSInterface.Imports.Windows.cs.");

//
// The <windows.h> pieces that the affinity, NUMA and CPU group port of
// GCToOSInterface.Processors.Windows.cs names on top of those. GROUP_AFFINITY is the one
// structure of this slice that the managed code writes into rather than passes through, so its
// layout is pinned exactly, including the three reserved words that SetThreadGroupAffinity
// requires to be zero. THREAD_PRIORITY_HIGHEST is the only constant. The entry points are
// checked the way the others are, in an unevaluated sizeof: <windows.h> has to declare each one,
// accept the arguments the managed declaration passes and return what it expects. GetCurrentThread
// returns a pseudo handle, which is a HANDLE and therefore the managed void*.
//
static_assert(THREAD_PRIORITY_HIGHEST == 2, "THREAD_PRIORITY_HIGHEST does not match GCToOSInterface.Processors.Windows.cs.");

static_assert(sizeof(GROUP_AFFINITY) == (sizeof(void*) == 8 ? 16 : 12), "GROUP_AFFINITY does not match GCToOSInterface.Processors.Windows.cs.");
static_assert(alignof(GROUP_AFFINITY) == sizeof(void*), "GROUP_AFFINITY does not match GCToOSInterface.Processors.Windows.cs.");
static_assert(offsetof(GROUP_AFFINITY, Mask) == 0, "GROUP_AFFINITY does not match GCToOSInterface.Processors.Windows.cs.");
static_assert(offsetof(GROUP_AFFINITY, Group) == sizeof(void*), "GROUP_AFFINITY does not match GCToOSInterface.Processors.Windows.cs.");
static_assert(offsetof(GROUP_AFFINITY, Reserved) == sizeof(void*) + 2, "GROUP_AFFINITY does not match GCToOSInterface.Processors.Windows.cs.");
static_assert(sizeof(((GROUP_AFFINITY*)nullptr)->Mask) == sizeof(uintptr_t), "GROUP_AFFINITY does not match GCToOSInterface.Processors.Windows.cs.");
static_assert(sizeof(((GROUP_AFFINITY*)nullptr)->Group) == sizeof(uint16_t), "GROUP_AFFINITY does not match GCToOSInterface.Processors.Windows.cs.");
static_assert(sizeof(((GROUP_AFFINITY*)nullptr)->Reserved) == 3 * sizeof(uint16_t), "GROUP_AFFINITY does not match GCToOSInterface.Processors.Windows.cs.");

static_assert(sizeof(::GetCurrentThread()) == sizeof(void*), "GetCurrentThread does not match GCToOSInterface.Imports.Windows.cs.");
static_assert(sizeof(::SetThreadIdealProcessorEx(nullptr, (PPROCESSOR_NUMBER)nullptr, (PPROCESSOR_NUMBER)nullptr)) == sizeof(int32_t), "SetThreadIdealProcessorEx does not match GCToOSInterface.Imports.Windows.cs.");
static_assert(sizeof(::GetThreadIdealProcessorEx(nullptr, (PPROCESSOR_NUMBER)nullptr)) == sizeof(int32_t), "GetThreadIdealProcessorEx does not match GCToOSInterface.Imports.Windows.cs.");
static_assert(sizeof(::SetThreadGroupAffinity(nullptr, (const GROUP_AFFINITY*)nullptr, (PGROUP_AFFINITY)nullptr)) == sizeof(int32_t), "SetThreadGroupAffinity does not match GCToOSInterface.Imports.Windows.cs.");
static_assert(sizeof(::SetThreadAffinityMask(nullptr, (DWORD_PTR)0)) == sizeof(uintptr_t), "SetThreadAffinityMask does not match GCToOSInterface.Imports.Windows.cs.");
static_assert(sizeof(::SetThreadPriority(nullptr, 0)) == sizeof(int32_t), "SetThreadPriority does not match GCToOSInterface.Imports.Windows.cs.");
static_assert(sizeof(::GetNumaNodeProcessorMaskEx((USHORT)0, (PGROUP_AFFINITY)nullptr)) == sizeof(int32_t), "GetNumaNodeProcessorMaskEx does not match GCToOSInterface.Imports.Windows.cs.");
static_assert(sizeof(::GetNumaProcessorNodeEx((PPROCESSOR_NUMBER)nullptr, (PUSHORT)nullptr)) == sizeof(int32_t), "GetNumaProcessorNodeEx does not match GCToOSInterface.Imports.Windows.cs.");

#endif // TARGET_UNIX

//
// GCToOSInterface. One forwarder per method of gcenv.os.h that is not translated yet, in
// declaration order. Only three are left: Initialize and Shutdown, which NativeAOT calls from
// PalInit before any managed code runs and the managed GC therefore never calls, and DebugBreak.
//
// bool is returned as UInt32_BOOL because the width of the register a C++ bool return occupies
// is unspecified, while the managed declaration has to name a concrete type.
//

extern "C" UInt32_BOOL ManagedGC_OS_Initialize()
{
    return GCToOSInterface::Initialize() ? UInt32_TRUE : UInt32_FALSE;
}

extern "C" void ManagedGC_OS_Shutdown()
{
    GCToOSInterface::Shutdown();
}

extern "C" void ManagedGC_OS_DebugBreak()
{
    GCToOSInterface::DebugBreak();
}

//
// Stands in for the `new (nothrow)` allocations of the environment layer: the `uintptr_t[]` of
// AffinitySet::Initialize, the GCEvent::Impl of the event ports, and the minipal_mutex that the
// C++ CLRCriticalSection embeds by value and the managed one has to allocate. The managed GC
// must not allocate managed memory, and it has no C runtime of its own; this is the whole of its
// heap allocation surface.
//

extern "C" void* ManagedGC_AllocZeroed(size_t size)
{
    void* memory = new (nothrow) uint8_t[size];
    if (memory != nullptr)
    {
        memset(memory, 0, size);
    }

    return memory;
}

extern "C" void ManagedGC_Free(void* memory)
{
    delete[] (uint8_t*)memory;
}
