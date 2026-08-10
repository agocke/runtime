// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// GC selector for the managed (C#) GC. This is the static-linking counterpart of
// clrgc.enabled.cpp: instead of loading a shared library and looking up GC_VersionInfo /
// GC_Initialize with PalGetProcAddress, the entry points are native symbols that ILC emits
// from the [RuntimeExport] methods in System.Private.GC, so they are resolved by the linker.
//
// Exactly one of clrgc.disabled.cpp, clrgc.enabled.cpp and this file is linked into an
// application, which is what keeps ManagedGC_Initialize from being an undefined symbol in the
// builds that do not opt into the managed GC.

#include "common.h"
#define SKIP_TRACING_DEFINITIONS
#include "gcenv.h"
#undef SKIP_TRACING_DEFINITIONS
#include "gcheaputilities.h"
#include "gchandleutilities.h"
#include "thread.h"
#include "threadstore.h"
#include "threadstore.inl"
#include "thread.inl"
#include "event.h"

#include "gceventstatus.h"
#include "holder.h"

#include "gctoeeinterface.standalone.inl"

#ifdef TARGET_UNIX
#include <errno.h>
#include <pthread.h>
#include <time.h>
#endif

// Emitted by ILC from System.Private.GC's ManagedGCEntryPoints. See
// Microsoft.NETCore.Native.targets, which only passes the assembly to
// --generateunmanagedentrypoints when IlcManagedGC is set.
// LOCALGC_CALLCONV matches how gcheaputilities.cpp declares the C++ GC's GC_Initialize; these
// symbols stand in for the same contract.
extern "C" void LOCALGC_CALLCONV ManagedGC_VersionInfo(/* InOut */ VersionInfo* info);
extern "C" HRESULT LOCALGC_CALLCONV ManagedGC_Initialize(
    /* In  */ IGCToCLR* clrToGC,
    /* Out */ IGCHeap** gcHeap,
    /* Out */ IGCHandleManager** gcHandleManager,
    /* Out */ GcDacVars* gcDacVars);
#ifdef FEATURE_SVR_GC
extern "C" void LOCALGC_CALLCONV ManagedServerGC_VersionInfo(/* InOut */ VersionInfo* info);
extern "C" HRESULT LOCALGC_CALLCONV ManagedServerGC_Initialize(
    /* In  */ IGCToCLR* clrToGC,
    /* Out */ IGCHeap** gcHeap,
    /* Out */ IGCHandleManager** gcHandleManager,
    /* Out */ GcDacVars* gcDacVars);
#endif

// Managed GC methods are ordinary managed code, so the runtime would otherwise be allowed to
// suspend a thread at one of their safe points. That differs from the C++ GC, where a thread in
// an IGCHeap method remains in cooperative native code until the operation is complete. Bracket
// multi-step heap mutations with TSF_DoNotTriggerGc to preserve that contract: RhpGcPoll2 returns
// immediately while the flag is set, and HijackCallback refuses to suspend the thread.
//
// The calls are nesting-safe because some runtime paths already set TSF_DoNotTriggerGc before
// calling managed code. Only the outermost managed-GC critical region owns the flag.
int32_t g_managedGCCriticalRegionCount = 0;
int32_t g_managedGCSuspensionPending = 0;
thread_local bool t_managedGCOwnsCriticalRegion = false;
thread_local int32_t t_managedGCAllocationHelperDepth = 0;
thread_local bool t_managedGCAllocationHelperOwnsCriticalRegion = false;

static void ManagedGC_EnterOwnedCriticalRegion(Thread* thread)
{
    while (true)
    {
        while (VolatileLoad(&g_managedGCSuspensionPending) != 0)
        {
            bool restoreCooperativeMode =
                thread->IsCurrentThreadInCooperativeMode();
            if (restoreCooperativeMode)
            {
                thread->EnablePreemptiveMode();
            }

            while (VolatileLoad(&g_managedGCSuspensionPending) != 0)
            {
                PalSwitchToThread();
            }

            if (restoreCooperativeMode)
            {
                thread->DisablePreemptiveMode();
            }
        }

        thread->SetDoNotTriggerGc();
        PalInterlockedIncrement(&g_managedGCCriticalRegionCount);
        MemoryBarrier();
        if (VolatileLoad(&g_managedGCSuspensionPending) == 0)
        {
            return;
        }

        thread->ClearDoNotTriggerGc();
        PalInterlockedDecrement(&g_managedGCCriticalRegionCount);
    }
}

// Non-spinning attempt to take ownership of the managed-GC critical region. Unlike
// ManagedGC_EnterOwnedCriticalRegion it NEVER transitions the thread's GC mode, so it is safe to
// call from a caller whose deferred transition frame may be invalid (e.g. the GC's own managed code
// reached through GCHeapCriticalRegion.Enter). Returns true and leaves DoNotTriggerGc set (with the
// critical-region count incremented) on success; returns false and changes nothing when a suspension
// is pending, so the managed caller can retry from a GC safe point where SuspendEE can hijack it.
static bool ManagedGC_TryEnterOwnedCriticalRegion(Thread* thread)
{
    if (VolatileLoad(&g_managedGCSuspensionPending) != 0)
    {
        return false;
    }

    thread->SetDoNotTriggerGc();
    PalInterlockedIncrement(&g_managedGCCriticalRegionCount);
    MemoryBarrier();

    // Re-check: PrepareForSuspension may have set the flag between our first read and our commit.
    // If so, roll the ownership back so PrepareForSuspension's count wait can complete, and report
    // retry.
    if (VolatileLoad(&g_managedGCSuspensionPending) != 0)
    {
        thread->ClearDoNotTriggerGc();
        PalInterlockedDecrement(&g_managedGCCriticalRegionCount);
        return false;
    }

    return true;
}

// Non-spinning try-enter used by GCHeapCriticalRegion.Enter's managed retry loop. Returns:
//    1 : this call took ownership of the managed-GC critical region
//    0 : nested -- the thread already holds DoNotTriggerGc, so this is a no-op enter
//   -1 : suspension pending -- the managed loop must yield at a GC safe point and retry
// It never sets DoNotTriggerGc / increments the count until the suspension flag is observed clear,
// and never transitions the thread to preemptive mode, so no invalid deferred transition frame is
// ever published here.
extern "C" int32_t ManagedGC_TryEnterCriticalRegion()
{
    Thread* thread = ThreadStore::GetCurrentThread();
    if (thread->IsDoNotTriggerGcSet())
    {
        return 0;
    }

    if (!ManagedGC_TryEnterOwnedCriticalRegion(thread))
    {
        return -1;
    }

    // Match ManagedGC_EnterCriticalRegion: record that this thread now owns the (non-allocation-helper)
    // critical region so ManagedGC_SuspendCriticalRegion releases it and ManagedGC_PrepareForSuspension
    // accounts for it in ownedCount. Omitting this leaves the count held while ownedCount reads 0, which
    // deadlocks PrepareForSuspension against the owner's own suspension.
    t_managedGCOwnsCriticalRegion = true;
    return 1;
}

extern "C" UInt32_BOOL ManagedGC_EnterCriticalRegion()
{
    Thread* thread = ThreadStore::GetCurrentThread();
    if (thread->IsDoNotTriggerGcSet())
    {
        return UInt32_FALSE;
    }

    ManagedGC_EnterOwnedCriticalRegion(thread);
    t_managedGCOwnsCriticalRegion = true;
    return UInt32_TRUE;
}

extern "C" void ManagedGC_ExitCriticalRegion(UInt32_BOOL entered)
{
    if (entered)
    {
        t_managedGCOwnsCriticalRegion = false;
        ThreadStore::GetCurrentThread()->ClearDoNotTriggerGc();
        PalInterlockedDecrement(&g_managedGCCriticalRegionCount);
    }
}

extern "C" UInt32_BOOL ManagedGC_SuspendCriticalRegion()
{
    Thread* thread = ThreadStore::GetCurrentThread();
    uint32_t suspended = 0;
    if (thread->IsDoNotTriggerGcSet())
    {
        thread->ClearDoNotTriggerGc();
        if (t_managedGCAllocationHelperOwnsCriticalRegion)
        {
            t_managedGCAllocationHelperOwnsCriticalRegion = false;
            PalInterlockedDecrement(&g_managedGCCriticalRegionCount);
            suspended |= 2;
        }
        else if (t_managedGCOwnsCriticalRegion)
        {
            t_managedGCOwnsCriticalRegion = false;
            PalInterlockedDecrement(&g_managedGCCriticalRegionCount);
            suspended |= 1;
        }
        else
        {
            suspended |= 4;
        }
    }

    return suspended;
}

extern "C" void ManagedGC_ResumeCriticalRegion(UInt32_BOOL suspended)
{
    if ((suspended & 2) != 0)
    {
        ManagedGC_EnterOwnedCriticalRegion(ThreadStore::GetCurrentThread());
        t_managedGCAllocationHelperOwnsCriticalRegion = true;
    }

    if ((suspended & 1) != 0)
    {
        ManagedGC_EnterOwnedCriticalRegion(ThreadStore::GetCurrentThread());
        t_managedGCOwnsCriticalRegion = true;
    }

    if ((suspended & 4) != 0)
    {
        ThreadStore::GetCurrentThread()->SetDoNotTriggerGc();
    }
}

extern "C" void ManagedGC_PrepareForSuspension()
{
    VolatileStore(&g_managedGCSuspensionPending, 1);
    MemoryBarrier();
    int32_t ownedCount =
        (t_managedGCOwnsCriticalRegion ||
         t_managedGCAllocationHelperOwnsCriticalRegion)
            ? 1
            : 0;
    while (VolatileLoad(&g_managedGCCriticalRegionCount) != ownedCount)
    {
        PalSwitchToThread();
    }
}

extern "C" void ManagedGC_CompleteSuspension()
{
    VolatileStore(&g_managedGCSuspensionPending, 0);
}

extern "C" void ManagedGC_EnterAllocationHelper()
{
    if (t_managedGCAllocationHelperDepth++ == 0)
    {
        Thread* thread = ThreadStore::GetCurrentThread();
        if (!thread->IsDoNotTriggerGcSet())
        {
            // Unlike GCHeapCriticalRegion.Enter (which reaches ManagedGC_TryEnterCriticalRegion from
            // the GC's own managed code, where the deferred transition frame may be invalid), this
            // helper is only ever reached through the allocation entry points -- RhpGcAlloc's
            // SetDeferredTransitionFrame and RhAllocateNewArray/RhAllocateNewObject's
            // DeferTransitionFrame -- which always publish a valid deferred transition frame before
            // GcAllocInternal runs. The suspension-pending wait inside ManagedGC_EnterOwnedCriticalRegion
            // therefore transitions to preemptive on a valid frame, so it never caches an invalid
            // frame and cannot deadlock PrepareForSuspension (it holds no critical-region count while
            // it waits). Keeping the direct native wait here avoids restructuring the native
            // GcAllocInternal entry into a managed retry boundary.
            ManagedGC_EnterOwnedCriticalRegion(thread);
            t_managedGCAllocationHelperOwnsCriticalRegion = true;
        }
    }
}

extern "C" void ManagedGC_ExitAllocationHelper()
{
    if (--t_managedGCAllocationHelperDepth == 0)
    {
        if (t_managedGCAllocationHelperOwnsCriticalRegion)
        {
            t_managedGCAllocationHelperOwnsCriticalRegion = false;
            ThreadStore::GetCurrentThread()->ClearDoNotTriggerGc();
            PalInterlockedDecrement(&g_managedGCCriticalRegionCount);
        }
    }
}

extern "C" void ManagedGC_WaitUntilGCComplete(
    int32_t* gcInProgress,
    int32_t* gcStarted,
    int32_t* waitForGCEvent,
    UInt32_BOOL considerGcStart)
{
    Thread* thread = ThreadStore::GetCurrentThread();
    uint32_t suspendedCriticalRegion = 0;
    bool restoreCooperativeMode = thread->IsCurrentThreadInCooperativeMode();
    if (restoreCooperativeMode)
    {
        thread->EnablePreemptiveMode();
    }

    while (VolatileLoad(waitForGCEvent) == 0 ||
        VolatileLoad(gcInProgress) != 0 ||
        (considerGcStart && VolatileLoad(gcStarted) != 0))
    {
        if (suspendedCriticalRegion == 0 &&
            VolatileLoad(&g_managedGCSuspensionPending) != 0)
        {
            suspendedCriticalRegion = ManagedGC_SuspendCriticalRegion();
        }
        PalSwitchToThread();
    }

    if (restoreCooperativeMode)
    {
        thread->DisablePreemptiveMode();
    }
    ManagedGC_ResumeCriticalRegion(suspendedCriticalRegion);
}

#ifdef TARGET_UNIX
extern "C" uint32_t QCALLTYPE ManagedGC_PthreadEventWait(
    pthread_cond_t* condition,
    pthread_mutex_t* mutex,
    uint8_t* state,
    uint8_t manualReset,
    uint32_t milliseconds)
{
    constexpr uint64_t NanosecondsPerSecond = 1000000000;
    constexpr uint64_t NanosecondsPerMillisecond = 1000000;

    timespec endTime{};
#ifdef TARGET_APPLE
    uint64_t endMachTime = 0;
    if (milliseconds != INFINITE)
    {
        uint64_t nanoseconds =
            static_cast<uint64_t>(milliseconds) * NanosecondsPerMillisecond;
        endTime.tv_sec = static_cast<time_t>(
            nanoseconds / NanosecondsPerSecond);
        endTime.tv_nsec = static_cast<long>(
            nanoseconds % NanosecondsPerSecond);
        endMachTime = clock_gettime_nsec_np(CLOCK_UPTIME_RAW) + nanoseconds;
    }
#else
    if (milliseconds != INFINITE)
    {
        clock_gettime(CLOCK_MONOTONIC, &endTime);
        uint64_t nanoseconds =
            static_cast<uint64_t>(endTime.tv_nsec) +
            static_cast<uint64_t>(milliseconds) *
                NanosecondsPerMillisecond;
        endTime.tv_sec += static_cast<time_t>(
            nanoseconds / NanosecondsPerSecond);
        endTime.tv_nsec = static_cast<long>(
            nanoseconds % NanosecondsPerSecond);
    }
#endif

    int status = 0;
    pthread_mutex_lock(mutex);
    while (*state == 0)
    {
        if (milliseconds == INFINITE)
        {
            status = pthread_cond_wait(condition, mutex);
        }
        else
        {
#ifdef TARGET_APPLE
            status = pthread_cond_timedwait_relative_np(
                condition,
                mutex,
                &endTime);
            if ((status == 0) && (*state == 0))
            {
                uint64_t machTime = clock_gettime_nsec_np(CLOCK_UPTIME_RAW);
                if (machTime < endMachTime)
                {
                    uint64_t remainingNanoseconds = endMachTime - machTime;
                    endTime.tv_sec = static_cast<time_t>(
                        remainingNanoseconds / NanosecondsPerSecond);
                    endTime.tv_nsec = static_cast<long>(
                        remainingNanoseconds % NanosecondsPerSecond);
                }
                else
                {
                    status = ETIMEDOUT;
                }
            }
#else
            status = pthread_cond_timedwait(condition, mutex, &endTime);
#endif
        }

        if (status != 0)
        {
            break;
        }
    }

    if ((status == 0) && (manualReset == 0))
    {
        *state = 0;
    }

    pthread_mutex_unlock(mutex);

    if (status == 0)
    {
        return WAIT_OBJECT_0;
    }

    return status == ETIMEDOUT ? WAIT_TIMEOUT : WAIT_FAILED;
}
#endif

extern "C" void ManagedGC_AllowForegroundGC()
{
    Thread* thread = ThreadStore::GetCurrentThread();
    uint32_t suspendedCriticalRegion = ManagedGC_SuspendCriticalRegion();
    if (thread->IsCurrentThreadInCooperativeMode())
    {
        thread->EnablePreemptiveMode();
        thread->DisablePreemptiveMode();
    }
    ManagedGC_ResumeCriticalRegion(suspendedCriticalRegion);
}

#ifdef FEATURE_SVR_GC
extern "C" int ManagedGC_CreateServerThread(
    void (*threadStart)(void*),
    void* context,
    const char* name)
{
    return GCToEEInterface::CreateThread(
        threadStart,
        context,
        false,
        name);
}
#endif

struct ManagedGCBackgroundThreadArgs
{
    void (*threadStart)(void*);
    void* context;
    CLREventStatic startEvent;
    int32_t* shutdown;
    int32_t* exited;
};

static void ManagedGCBackgroundThreadStub(void* argument)
{
    ManagedGCBackgroundThreadArgs* args =
        static_cast<ManagedGCBackgroundThreadArgs*>(argument);
    void (*threadStart)(void*) = args->threadStart;
    void* context = args->context;
    int32_t* shutdown = args->shutdown;
    int32_t* exited = args->exited;

    while (true)
    {
        uint32_t result = args->startEvent.Wait(INFINITE, false);
        if (result != WAIT_OBJECT_0)
        {
            break;
        }

        args->startEvent.Reset();
        if (VolatileLoad(shutdown) != 0)
        {
            break;
        }

        threadStart(context);
    }

    args->startEvent.CloseEvent();
    VolatileStore(exited, 1);
    delete args;
}

extern "C" BOOL ManagedGC_CreateBackgroundThread(
    void (*threadStart)(void*),
    void* context,
    int32_t* shutdown,
    int32_t* exited,
    void** worker,
    const char* name)
{
    ManagedGCBackgroundThreadArgs* args =
        new (nothrow) ManagedGCBackgroundThreadArgs{
            threadStart,
            context,
            {},
            shutdown,
            exited};
    if (args == nullptr)
    {
        return FALSE;
    }

    if (!args->startEvent.CreateManualEventNoThrow(false))
    {
        delete args;
        return FALSE;
    }

    if (!::GCToEEInterface::CreateThread(
        ManagedGCBackgroundThreadStub,
        args,
        true,
        name))
    {
        args->startEvent.CloseEvent();
        delete args;
        return FALSE;
    }

    *worker = args;
    return TRUE;
}

extern "C" void ManagedGC_SignalBackgroundThread(void* worker)
{
    static_cast<ManagedGCBackgroundThreadArgs*>(worker)->startEvent.Set();
}

// Stands in for the C++ GC's PURE_VIRTUAL: the managed heap points every IGCHeap slot it has
// not implemented yet at this, so reaching one stops the process at the call rather than
// crashing later on a null slot or a bogus return value.
extern "C" void ManagedGC_Unsupported()
{
    ASSERT_UNCONDITIONALLY("The managed GC does not implement this IGCHeap/IGCHandleManager method yet.");
    RhFailFast();
}

CrstStatic g_eventStashLock;

GCEventLevel g_stashedLevel = GCEventLevel_None;
GCEventKeyword g_stashedKeyword = GCEventKeyword_None;
GCEventLevel g_stashedPrivateLevel = GCEventLevel_None;
GCEventKeyword g_stashedPrivateKeyword = GCEventKeyword_None;

BOOL g_gcEventTracingInitialized = FALSE;

void InitializeGCEventLock()
{
    g_eventStashLock.InitNoThrow(CrstGcEvent);
}

// Replays the event state the EE recorded before a heap existed onto the heap that was just
// published, and switches RecordEventStateChange over to forwarding directly.
static void FlushStashedEventState()
{
    CrstHolder lh(&g_eventStashLock);
    g_pGCHeap->ControlEvents(g_stashedKeyword, g_stashedLevel);
    g_pGCHeap->ControlPrivateEvents(g_stashedPrivateKeyword, g_stashedPrivateLevel);
    g_gcEventTracingInitialized = TRUE;
}

HRESULT InitializeGCSelector()
{
    // Deliberately never freed: ManagedGC_Initialize hands this to the managed
    // GCToEEInterface, which keeps it in a static for the lifetime of the process. Matches
    // clrgc.enabled.cpp.
    IGCToCLR* gcToClr = new (nothrow) standalone::GCToEEInterface();
    if (!gcToClr)
    {
        return E_OUTOFMEMORY;
    }

    VersionInfo versionInfo;
    versionInfo.MajorVersion = EE_INTERFACE_MAJOR_VERSION;
    versionInfo.MinorVersion = 0;
    versionInfo.BuildVersion = 0;
    versionInfo.Name = nullptr;
#ifdef FEATURE_SVR_GC
    if (GCHeapUtilities::IsServerHeap())
    {
        ManagedServerGC_VersionInfo(&versionInfo);
    }
    else
#endif
    {
        ManagedGC_VersionInfo(&versionInfo);
    }

    if (versionInfo.MajorVersion < GC_INTERFACE_MAJOR_VERSION)
    {
        LOG((LF_GC, LL_FATALERROR, "GC initialization failed because the managed GC reported a major version lower than what the runtime requires.\n"));
        return E_FAIL;
    }

    g_gc_dac_vars.major_version_number = GC_INTERFACE_MAJOR_VERSION;
    g_gc_dac_vars.minor_version_number = GC_INTERFACE_MINOR_VERSION;

    IGCHeap* heap = nullptr;
    IGCHandleManager* manager = nullptr;
    HRESULT initResult;
#ifdef FEATURE_SVR_GC
    if (GCHeapUtilities::IsServerHeap())
    {
        initResult = ManagedServerGC_Initialize(gcToClr, &heap, &manager, &g_gc_dac_vars);
    }
    else
#endif
    {
        initResult = ManagedGC_Initialize(gcToClr, &heap, &manager, &g_gc_dac_vars);
    }

    if (FAILED(initResult))
    {
        LOG((LF_GC, LL_FATALERROR, "Managed GC initialization failed with HR = 0x%X\n", initResult));
        return initResult;
    }

    g_pGCHeap = heap;
    g_pGCHandleManager = manager;
    g_gcDacGlobals = &g_gc_dac_vars;
    FlushStashedEventState();
    LOG((LF_GC, LL_INFO100, "Managed GC load successful\n"));

    return initResult;
}

void GCHeapUtilities::RecordEventStateChange(bool isPublicProvider, GCEventKeyword keywords, GCEventLevel level)
{
    CrstHolder lh(&g_eventStashLock);
    if (g_gcEventTracingInitialized)
    {
        if (isPublicProvider)
        {
            g_pGCHeap->ControlEvents(keywords, level);
        }
        else
        {
            g_pGCHeap->ControlPrivateEvents(keywords, level);
        }
    }
    else
    {
        if (isPublicProvider)
        {
            g_stashedKeyword = keywords;
            g_stashedLevel = level;
        }
        else
        {
            g_stashedPrivateKeyword = keywords;
            g_stashedPrivateLevel = level;
        }
    }
}
