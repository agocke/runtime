// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Native side of the managed GC's environment layer.
//
// System.Private.GC translates gcenv.os.h, gcenv.base.h, gcenv.interlocked.h and volatile.h to
// C#. Everything in those headers that is pure computation is translated outright, as are the
// whole of virtual memory management and write watching, the GCEvent condition variable /
// Win32 event, and the CLRCriticalSection mutex; everything else that reaches the operating
// system is, for now, forwarded here and implemented by the existing C++ GCToOSInterface in
// gc/unix/gcenv.unix.cpp and gc/windows/gcenv.windows.cpp.
//
// This file also carries the static_asserts that check the <sys/mman.h>, <sys/resource.h>,
// <pthread.h>, <time.h>, <errno.h> and <windows.h> constants and layouts that those managed
// ports hardcode against the real headers of the platform being built, in the same spirit as
// AsmOffsets.h. The C# #if structure and the one below must stay in the same shape.
//
// The managed side calls these with [RuntimeImport], which is a direct call to a linked symbol
// with no marshalling, no argument copying and no GC mode transition. That is what code running
// with the world suspended requires, and it is why these cannot be [DllImport]s.
//
// Each forwarder is deliberately a single expression with no logic of its own, so that the
// managed declaration and the C++ declaration can be diffed against each other. They exist
// because porting cgroups, NUMA, CPU groups and pthread affinity to C# is a separate,
// platform-by-platform piece of work. Deletion point: plan step 3 of
// System.Private.GC/ROADMAP.md, one platform module at a time; a forwarder disappears when the
// managed GCToOSInterface implements that method itself.
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
#elif defined(TARGET_LINUX) && !defined(TARGET_ANDROID)
#include <alloca.h>
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
// The second remaining piece: the NUMA half of VirtualCommitInner. It is the body of the
// `#if defined(TARGET_LINUX) && !defined(TARGET_ANDROID)` block of that function, verbatim,
// because it reads the NUMA state that only gc/unix/numasupport.cpp has -- which belongs to the
// NUMA submodule of plan step 3 in System.Private.GC/ROADMAP.md, and takes this shim with it.
// The managed caller has already checked that the commit succeeded and that a node was
// requested.
//

#if defined(TARGET_LINUX) && !defined(TARGET_ANDROID)
extern "C" int g_highestNumaNode;
extern "C" bool g_numaAvailable;
long BindMemoryPolicy(void* start, unsigned long len, const unsigned long* nodemask, unsigned long maxnode);
#endif

extern "C" void ManagedGC_NUMA_BindMemoryPolicy(void* address, size_t size, uint16_t node)
{
#if defined(TARGET_LINUX) && !defined(TARGET_ANDROID)
    if (g_numaAvailable)
    {
        if ((int)node <= g_highestNumaNode)
        {
            int usedNodeMaskBits = g_highestNumaNode + 1;
            int nodeMaskLength = usedNodeMaskBits + sizeof(unsigned long) - 1;
            unsigned long* nodeMask = (unsigned long*)alloca(nodeMaskLength);
            memset(nodeMask, 0, nodeMaskLength);

            int index = node / sizeof(unsigned long);
            nodeMask[index] = ((unsigned long)1) << (node & (sizeof(unsigned long) - 1));

            int st = BindMemoryPolicy(address, size, nodeMask, usedNodeMaskBits);
            assert(st == 0);
            // If the mbind fails, we still return the allocated memory since the node is just a hint
        }
    }
#else
    UNREFERENCED_PARAMETER(address);
    UNREFERENCED_PARAMETER(size);
    UNREFERENCED_PARAMETER(node);
#endif // TARGET_LINUX && !TARGET_ANDROID
}

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

// The event and lock ports of GCEvent.Windows.cs, GCEnvSync.Windows.cs and SyncTypes.Windows.cs
// hardcode one <windows.h> value and one type. The CRITICAL_SECTION is an opaque blob there, so
// only its size and alignment matter; the managed one is deliberately larger than any platform
// needs. INVALID_HANDLE_VALUE is a pointer cast, which is not a constant expression, so it
// cannot be asserted here the way an integer constant is.
static_assert(sizeof(CRITICAL_SECTION) <= 8 * sizeof(uint64_t), "CRITICAL_SECTION does not fit the blob of SyncTypes.Windows.cs.");
static_assert(alignof(CRITICAL_SECTION) <= alignof(uint64_t), "CRITICAL_SECTION is more strictly aligned than the blob of SyncTypes.Windows.cs.");

#endif // TARGET_UNIX

//
// GCToOSInterface. One forwarder per method of gcenv.os.h, in declaration order.
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

extern "C" uint32_t ManagedGC_OS_GetCurrentProcessorNumber()
{
    return GCToOSInterface::GetCurrentProcessorNumber();
}

extern "C" UInt32_BOOL ManagedGC_OS_CanGetCurrentProcessorNumber()
{
    return GCToOSInterface::CanGetCurrentProcessorNumber() ? UInt32_TRUE : UInt32_FALSE;
}

extern "C" UInt32_BOOL ManagedGC_OS_SetCurrentThreadIdealAffinity(uint16_t srcProcNo, uint16_t dstProcNo)
{
    return GCToOSInterface::SetCurrentThreadIdealAffinity(srcProcNo, dstProcNo) ? UInt32_TRUE : UInt32_FALSE;
}

extern "C" UInt32_BOOL ManagedGC_OS_GetCurrentThreadIdealProc(uint16_t* procNo)
{
    return GCToOSInterface::GetCurrentThreadIdealProc(procNo) ? UInt32_TRUE : UInt32_FALSE;
}

extern "C" uint64_t ManagedGC_OS_GetCurrentThreadIdForLogging()
{
    return GCToOSInterface::GetCurrentThreadIdForLogging();
}

extern "C" uint32_t ManagedGC_OS_GetCurrentProcessId()
{
    return GCToOSInterface::GetCurrentProcessId();
}

extern "C" size_t ManagedGC_OS_GetCacheSizePerLogicalCpu(UInt32_BOOL trueSize)
{
    return GCToOSInterface::GetCacheSizePerLogicalCpu(trueSize != UInt32_FALSE);
}

extern "C" UInt32_BOOL ManagedGC_OS_SetThreadAffinity(uint16_t procNo)
{
    return GCToOSInterface::SetThreadAffinity(procNo) ? UInt32_TRUE : UInt32_FALSE;
}

extern "C" UInt32_BOOL ManagedGC_OS_BoostThreadPriority()
{
    return GCToOSInterface::BoostThreadPriority() ? UInt32_TRUE : UInt32_FALSE;
}

extern "C" const void* ManagedGC_OS_SetGCThreadsAffinitySet(uintptr_t configAffinityMask, const void* configAffinitySet)
{
    return GCToOSInterface::SetGCThreadsAffinitySet(configAffinityMask, (const AffinitySet*)configAffinitySet);
}

// is_restricted is a `bool*` in C++ and a `byte*` on the managed side; both are one byte, and
// the null the C++ default argument supplies is passed explicitly by the managed declaration.
extern "C" uint64_t ManagedGC_OS_GetPhysicalMemoryLimit(bool* is_restricted)
{
    return GCToOSInterface::GetPhysicalMemoryLimit(is_restricted);
}

extern "C" void ManagedGC_OS_GetMemoryStatus(uint64_t restricted_limit, uint32_t* memory_load, uint64_t* available_physical, uint64_t* available_page_file)
{
    GCToOSInterface::GetMemoryStatus(restricted_limit, memory_load, available_physical, available_page_file);
}

extern "C" void ManagedGC_OS_DebugBreak()
{
    GCToOSInterface::DebugBreak();
}

extern "C" int64_t ManagedGC_OS_QueryPerformanceCounter()
{
    return GCToOSInterface::QueryPerformanceCounter();
}

extern "C" int64_t ManagedGC_OS_QueryPerformanceFrequency()
{
    return GCToOSInterface::QueryPerformanceFrequency();
}

extern "C" uint64_t ManagedGC_OS_GetLowPrecisionTimeStamp()
{
    return GCToOSInterface::GetLowPrecisionTimeStamp();
}

extern "C" uint32_t ManagedGC_OS_GetTotalProcessorCount()
{
    return GCToOSInterface::GetTotalProcessorCount();
}

extern "C" uint32_t ManagedGC_OS_GetMaxProcessorCount()
{
    return GCToOSInterface::GetMaxProcessorCount();
}

extern "C" UInt32_BOOL ManagedGC_OS_CanEnableGCNumaAware()
{
    return GCToOSInterface::CanEnableGCNumaAware() ? UInt32_TRUE : UInt32_FALSE;
}

extern "C" UInt32_BOOL ManagedGC_OS_GetNumaInfo(uint16_t* total_nodes, uint32_t* max_procs_per_node)
{
    return GCToOSInterface::GetNumaInfo(total_nodes, max_procs_per_node) ? UInt32_TRUE : UInt32_FALSE;
}

extern "C" UInt32_BOOL ManagedGC_OS_CanEnableGCCPUGroups()
{
    return GCToOSInterface::CanEnableGCCPUGroups() ? UInt32_TRUE : UInt32_FALSE;
}

extern "C" UInt32_BOOL ManagedGC_OS_GetProcessorForHeap(uint16_t heap_number, uint16_t* proc_no, uint16_t* node_no)
{
    return GCToOSInterface::GetProcessorForHeap(heap_number, proc_no, node_no) ? UInt32_TRUE : UInt32_FALSE;
}

extern "C" UInt32_BOOL ManagedGC_OS_GetCPUGroupInfo(uint16_t* total_groups, uint32_t* max_procs_per_group)
{
    return GCToOSInterface::GetCPUGroupInfo(total_groups, max_procs_per_group) ? UInt32_TRUE : UInt32_FALSE;
}

// The Unix implementation is ParseIndexOrRange, which System.Private.GC translates directly.
// The Windows one prefixes each entry with a CPU group and validates it against the group
// table that only gcenv.windows.cpp has, so both go through here until that table is ported.
extern "C" UInt32_BOOL ManagedGC_OS_ParseGCHeapAffinitizeRangesEntry(const char** config_string, size_t* start_index, size_t* end_index)
{
    return GCToOSInterface::ParseGCHeapAffinitizeRangesEntry(config_string, start_index, end_index) ? UInt32_TRUE : UInt32_FALSE;
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
