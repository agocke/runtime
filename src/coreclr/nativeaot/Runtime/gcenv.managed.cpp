// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Native side of the managed GC's environment layer.
//
// System.Private.GC translates gcenv.os.h, gcenv.base.h, gcenv.interlocked.h and volatile.h to
// C#. Everything in those headers that is pure computation is translated outright; everything
// that reaches the operating system is, for now, forwarded here and implemented by the existing
// C++ GCToOSInterface in gc/unix/gcenv.unix.cpp and gc/windows/gcenv.windows.cpp.
//
// The managed side calls these with [RuntimeImport], which is a direct call to a linked symbol
// with no marshalling, no argument copying and no GC mode transition. That is what code running
// with the world suspended requires, and it is why these cannot be [DllImport]s.
//
// Each forwarder is deliberately a single expression with no logic of its own, so that the
// managed declaration and the C++ declaration can be diffed against each other. They exist
// because porting mmap/VirtualAlloc, cgroups, NUMA, CPU groups, pthread affinity and the
// condition-variable event implementation to C# is a separate, platform-by-platform piece of
// work. Deletion point: plan step 3 of System.Private.GC/ROADMAP.md, one platform module at a
// time; a forwarder disappears when the managed GCToOSInterface implements that method itself.
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

extern "C" void* ManagedGC_OS_VirtualReserve(size_t size, size_t alignment, uint32_t flags, uint16_t node)
{
    return GCToOSInterface::VirtualReserve(size, alignment, flags, node);
}

extern "C" UInt32_BOOL ManagedGC_OS_VirtualRelease(void* address, size_t size)
{
    return GCToOSInterface::VirtualRelease(address, size) ? UInt32_TRUE : UInt32_FALSE;
}

extern "C" UInt32_BOOL ManagedGC_OS_VirtualCommit(void* address, size_t size, uint16_t node)
{
    return GCToOSInterface::VirtualCommit(address, size, node) ? UInt32_TRUE : UInt32_FALSE;
}

extern "C" void* ManagedGC_OS_VirtualReserveAndCommitLargePages(size_t size, uint16_t node)
{
    return GCToOSInterface::VirtualReserveAndCommitLargePages(size, node);
}

extern "C" UInt32_BOOL ManagedGC_OS_VirtualDecommit(void* address, size_t size)
{
    return GCToOSInterface::VirtualDecommit(address, size) ? UInt32_TRUE : UInt32_FALSE;
}

extern "C" UInt32_BOOL ManagedGC_OS_VirtualReset(void* address, size_t size, UInt32_BOOL unlock)
{
    return GCToOSInterface::VirtualReset(address, size, unlock != UInt32_FALSE) ? UInt32_TRUE : UInt32_FALSE;
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

extern "C" size_t ManagedGC_OS_GetVirtualMemoryLimit()
{
    return GCToOSInterface::GetVirtualMemoryLimit();
}

extern "C" size_t ManagedGC_OS_GetVirtualMemoryMaxAddress()
{
    return GCToOSInterface::GetVirtualMemoryMaxAddress();
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

extern "C" size_t ManagedGC_OS_GetPageSize()
{
    return GCToOSInterface::GetPageSize();
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
