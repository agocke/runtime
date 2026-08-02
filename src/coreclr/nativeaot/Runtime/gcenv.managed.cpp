// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Native side of the managed GC's environment layer.
//
// System.Private.GC translates gcenv.os.h, gcenv.base.h, gcenv.interlocked.h and volatile.h to
// C#. Everything in those headers that is pure computation is translated outright, as is the
// whole of virtual memory management; everything else that reaches the operating system is, for
// now, forwarded here and implemented by the existing C++ GCToOSInterface in
// gc/unix/gcenv.unix.cpp and gc/windows/gcenv.windows.cpp.
//
// This file also carries the static_asserts that check the <sys/mman.h>, <sys/resource.h> and
// <windows.h> constants that the managed virtual memory port hardcodes against the real headers
// of the platform being built, in the same spirit as AsmOffsets.h. The C# #if structure and the
// one below must stay in the same shape.
//
// The managed side calls these with [RuntimeImport], which is a direct call to a linked symbol
// with no marshalling, no argument copying and no GC mode transition. That is what code running
// with the world suspended requires, and it is why these cannot be [DllImport]s.
//
// Each forwarder is deliberately a single expression with no logic of its own, so that the
// managed declaration and the C++ declaration can be diffed against each other. They exist
// because porting cgroups, NUMA, CPU groups, pthread affinity and the condition-variable event
// implementation to C# is a separate, platform-by-platform piece of work. Deletion point: plan
// step 3 of System.Private.GC/ROADMAP.md, one platform module at a time; a forwarder disappears
// when the managed GCToOSInterface implements that method itself.
//
// This file is only compiled into the managedgc-enabled archive, so a default (C++ GC) build
// does not carry any of it.

#include "common.h"
#define SKIP_TRACING_DEFINITIONS
#include "gcenv.h"
#undef SKIP_TRACING_DEFINITIONS

#include <string.h>

// The managed GCEvent is a struct with a single pointer field, laid out exactly like the C++
// one, so a managed GCEvent* is a native GCEvent*. Nothing else about GCEvent crosses over.
static_assert(sizeof(GCEvent) == sizeof(void*), "The managed GCEvent mirrors the C++ one as a single pointer.");

//
// Virtual memory management is ported: GCToOSInterface.VirtualMemory.Unix.cs and
// GCToOSInterface.VirtualMemory.Windows.cs of System.Private.GC call mmap/munmap/mprotect/
// madvise/getrlimit and VirtualAlloc/VirtualFree/VirtualAllocExNuma directly. Two pieces remain
// here.
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
#elif !defined(TARGET_FREEBSD)
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

#else // Linux, Android and any other Unix that shares the asm-generic values.

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

// The managed GetPageSize returns 4096, which is what minipal_getpagesize is on Windows: an
// inline function with no symbol to call and no way to static_assert its result.

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

extern "C" UInt32_BOOL ManagedGC_OS_SupportsWriteWatch()
{
    return GCToOSInterface::SupportsWriteWatch() ? UInt32_TRUE : UInt32_FALSE;
}

extern "C" void ManagedGC_OS_ResetWriteWatch(void* address, size_t size)
{
    GCToOSInterface::ResetWriteWatch(address, size);
}

extern "C" UInt32_BOOL ManagedGC_OS_GetWriteWatch(UInt32_BOOL resetState, void* address, size_t size, void** pageAddresses, uintptr_t* pageAddressesCount)
{
    return GCToOSInterface::GetWriteWatch(resetState != UInt32_FALSE, address, size, pageAddresses, pageAddressesCount) ? UInt32_TRUE : UInt32_FALSE;
}

extern "C" void ManagedGC_OS_Sleep(uint32_t sleepMSec)
{
    GCToOSInterface::Sleep(sleepMSec);
}

extern "C" void ManagedGC_OS_YieldThread(uint32_t switchCount)
{
    GCToOSInterface::YieldThread(switchCount);
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
// GCEvent. The managed struct has the same layout as the C++ class -- a single Impl pointer,
// zero-initialized, which is what the C++ default constructor produces -- so `event` below is
// the address of the managed instance and the member functions operate on it in place.
//

extern "C" void ManagedGC_GCEvent_CloseEvent(GCEvent* event)
{
    event->CloseEvent();
}

extern "C" void ManagedGC_GCEvent_Set(GCEvent* event)
{
    event->Set();
}

extern "C" void ManagedGC_GCEvent_Reset(GCEvent* event)
{
    event->Reset();
}

extern "C" uint32_t ManagedGC_GCEvent_Wait(GCEvent* event, uint32_t timeout, UInt32_BOOL alertable)
{
    return event->Wait(timeout, alertable != UInt32_FALSE);
}

extern "C" UInt32_BOOL ManagedGC_GCEvent_CreateManualEventNoThrow(GCEvent* event, UInt32_BOOL initialState)
{
    return event->CreateManualEventNoThrow(initialState != UInt32_FALSE) ? UInt32_TRUE : UInt32_FALSE;
}

extern "C" UInt32_BOOL ManagedGC_GCEvent_CreateAutoEventNoThrow(GCEvent* event, UInt32_BOOL initialState)
{
    return event->CreateAutoEventNoThrow(initialState != UInt32_FALSE) ? UInt32_TRUE : UInt32_FALSE;
}

extern "C" UInt32_BOOL ManagedGC_GCEvent_CreateOSManualEventNoThrow(GCEvent* event, UInt32_BOOL initialState)
{
    return event->CreateOSManualEventNoThrow(initialState != UInt32_FALSE) ? UInt32_TRUE : UInt32_FALSE;
}

extern "C" UInt32_BOOL ManagedGC_GCEvent_CreateOSAutoEventNoThrow(GCEvent* event, UInt32_BOOL initialState)
{
    return event->CreateOSAutoEventNoThrow(initialState != UInt32_FALSE) ? UInt32_TRUE : UInt32_FALSE;
}

//
// CLRCriticalSection.
//
// Unlike GCEvent this one cannot be mirrored field for field: it embeds a minipal_mutex, which
// is a pthread_mutex_t or a CRITICAL_SECTION, whose size differs per operating system and is
// therefore not expressible as the per-pointer-size constant that GCInterfaceOffsets.h can
// produce. The managed struct holds a pointer to a natively allocated one instead. It goes away
// with the rest of these forwarders, when the lock itself is ported.
//

extern "C" void* ManagedGC_CriticalSection_Create()
{
    CLRCriticalSection* cs = new (nothrow) CLRCriticalSection();
    if (cs == nullptr)
    {
        return nullptr;
    }

    if (!cs->Initialize())
    {
        delete cs;
        return nullptr;
    }

    return cs;
}

extern "C" void ManagedGC_CriticalSection_Destroy(void* cs)
{
    CLRCriticalSection* section = (CLRCriticalSection*)cs;
    section->Destroy();
    delete section;
}

extern "C" void ManagedGC_CriticalSection_Enter(void* cs)
{
    ((CLRCriticalSection*)cs)->Enter();
}

extern "C" void ManagedGC_CriticalSection_Leave(void* cs)
{
    ((CLRCriticalSection*)cs)->Leave();
}

//
// Stands in for the `new (nothrow) uintptr_t[]` / `delete[]` pair that AffinitySet::Initialize
// and ~AffinitySet use. The managed GC must not allocate managed memory, and it has no C
// runtime of its own; this is the whole of its heap allocation surface, used only by
// AffinitySet during GC initialization.
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
