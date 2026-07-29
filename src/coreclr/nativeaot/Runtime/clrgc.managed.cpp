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

#include "gceventstatus.h"
#include "holder.h"

#include "gctoeeinterface.standalone.inl"

// Emitted by ILC from System.Private.GC's ManagedGCEntryPoints. See
// Microsoft.NETCore.Native.targets, which only passes the assembly to
// --generateunmanagedentrypoints when IlcManagedGC is set.
extern "C" void ManagedGC_VersionInfo(/* InOut */ VersionInfo* info);
extern "C" HRESULT ManagedGC_Initialize(
    /* In  */ IGCToCLR* clrToGC,
    /* Out */ IGCHeap** gcHeap,
    /* Out */ IGCHandleManager** gcHandleManager,
    /* Out */ GcDacVars* gcDacVars);

// S_FALSE. The NativeAOT PAL headers declare S_OK but not S_FALSE. See ManagedGC_Initialize
// for what the managed side means by it.
static const HRESULT ManagedGC_S_FALSE = (HRESULT)1;

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

    // S_FALSE means the managed GC completed its bring-up self-checks but does not provide a
    // heap yet. The port is incremental, so fall back to the statically linked C++ GC and let
    // the application run. Once the managed heap is complete this becomes a hard failure.
    if (initResult == ManagedGC_S_FALSE)
    {
        LOG((LF_GC, LL_INFO100, "Managed GC declined to provide a heap; falling back to the C++ GC.\n"));

        HRESULT fallbackResult = GCHeapUtilities::InitializeDefaultGC();
        if (SUCCEEDED(fallbackResult))
        {
            FlushStashedEventState();
        }

        return fallbackResult;
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
