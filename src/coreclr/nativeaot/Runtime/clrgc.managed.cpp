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

#include "gceventstatus.h"
#include "holder.h"

#include "gctoeeinterface.standalone.inl"

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

// Managed GC methods are ordinary managed code, so the runtime would otherwise be allowed to
// suspend a thread at one of their safe points. That differs from the C++ GC, where a thread in
// an IGCHeap method remains in cooperative native code until the operation is complete. Bracket
// multi-step heap mutations with TSF_DoNotTriggerGc to preserve that contract: RhpGcPoll2 returns
// immediately while the flag is set, and HijackCallback refuses to suspend the thread.
//
// The calls are nesting-safe because some runtime paths already set TSF_DoNotTriggerGc before
// calling managed code. Only the outermost managed-GC critical region owns the flag.
extern "C" UInt32_BOOL ManagedGC_EnterCriticalRegion()
{
    Thread* thread = ThreadStore::GetCurrentThread();
    if (thread->IsDoNotTriggerGcSet())
    {
        return UInt32_FALSE;
    }

    thread->SetDoNotTriggerGc();
    return UInt32_TRUE;
}

extern "C" void ManagedGC_ExitCriticalRegion(UInt32_BOOL entered)
{
    if (entered)
    {
        ThreadStore::GetCurrentThread()->ClearDoNotTriggerGc();
    }
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
    ManagedGC_VersionInfo(&versionInfo);

    if (versionInfo.MajorVersion < GC_INTERFACE_MAJOR_VERSION)
    {
        LOG((LF_GC, LL_FATALERROR, "GC initialization failed because the managed GC reported a major version lower than what the runtime requires.\n"));
        return E_FAIL;
    }

    g_gc_dac_vars.major_version_number = GC_INTERFACE_MAJOR_VERSION;
    g_gc_dac_vars.minor_version_number = GC_INTERFACE_MINOR_VERSION;

    IGCHeap* heap = nullptr;
    IGCHandleManager* manager = nullptr;
    HRESULT initResult = ManagedGC_Initialize(gcToClr, &heap, &manager, &g_gc_dac_vars);

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
