// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Internal.Runtime.GC
{
    internal unsafe struct GCBootstrap
    {
        private const int E_NOTIMPL = unchecked((int)0x80004001);
        private static IGCHeap s_heap;
        private static IGCHeapVtable s_heapVtable;
        private static IGCHandleManager s_handleManager;
        private static IGCHandleManagerVtable s_handleManagerVtable;
        private static GCNameStorage s_nameStorage;

        [UnmanagedCallersOnly(EntryPoint = "CSharpGC_VersionInfo")]
        internal static void VersionInfo(VersionInfo* info)
        {
            if (info is null)
            {
                return;
            }

            info->MajorVersion = (uint)GCInterfaceVersionConstants.GC_INTERFACE_MAJOR_VERSION;
            info->MinorVersion = (uint)GCInterfaceVersionConstants.GC_INTERFACE_MINOR_VERSION;
            info->BuildVersion = 0;
            fixed (GCNameStorage* storage = &s_nameStorage)
            {
                byte* name = storage->Value;
                name[0] = (byte)'C';
                name[1] = (byte)'#';
                name[2] = (byte)' ';
                name[3] = (byte)'W';
                name[4] = (byte)'K';
                name[5] = (byte)'S';
                name[6] = (byte)' ';
                name[7] = (byte)'G';
                name[8] = (byte)'C';
                name[9] = 0;
                info->Name = name;
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "CSharpGC_Initialize")]
        internal static int Initialize(IGCToCLR* clrToGC, IGCHeap** gcHeap, IGCHandleManager** gcHandleManager, GcDacVars* gcDacVars)
        {
            _ = gcDacVars;

            if (gcHeap is not null)
            {
                *gcHeap = null;
            }

            if (gcHandleManager is not null)
            {
                *gcHandleManager = null;
            }

            if (clrToGC is null || gcHeap is null || gcHandleManager is null || gcDacVars is null)
            {
                return E_NOTIMPL;
            }

            GCCommon.g_theGCToCLR = clrToGC;
            int initializationResult = GCWksInitialization.Initialize();
            if (initializationResult != 0)
            {
                return initializationResult;
            }

            InitializeVtables();
            fixed (IGCHeap* heap = &s_heap)
            fixed (IGCHandleManager* handleManager = &s_handleManager)
            {
                *gcHeap = heap;
                *gcHandleManager = handleManager;
            }

            return 0;
        }

        private static void InitializeVtables()
        {
            fixed (IGCHeap* heap = &s_heap)
            fixed (IGCHeapVtable* heapVtable = &s_heapVtable)
            fixed (IGCHandleManager* handleManager = &s_handleManager)
            fixed (IGCHandleManagerVtable* handleManagerVtable = &s_handleManagerVtable)
            {
                heap->Vtable = heapVtable;
                handleManager->Vtable = handleManagerVtable;
            }

            s_heapVtable = default;
            s_heapVtable.IsValidSegmentSize = &GCWksInitialization.IsValidSegmentSize;
            s_heapVtable.IsValidGen0MaxSize = &GCWksInitialization.IsValidGen0MaxSize;
            s_heapVtable.GetValidSegmentSize = &GCWksInitialization.GetValidSegmentSize;
            s_heapVtable.SetReservedVMLimit = &Stub_SetReservedVMLimit;
            s_heapVtable.WaitUntilConcurrentGCComplete = &Stub_WaitUntilConcurrentGCComplete;
            s_heapVtable.IsConcurrentGCInProgress = &Stub_IsConcurrentGCInProgress;
            s_heapVtable.TemporaryEnableConcurrentGC = &Stub_TemporaryEnableConcurrentGC;
            s_heapVtable.TemporaryDisableConcurrentGC = &Stub_TemporaryDisableConcurrentGC;
            s_heapVtable.IsConcurrentGCEnabled = &GCWksInitialization.IsConcurrentGCEnabled;
            s_heapVtable.WaitUntilConcurrentGCCompleteAsync = &Stub_WaitUntilConcurrentGCCompleteAsync;
            s_heapVtable.GetNumberOfFinalizable = &Stub_GetNumberOfFinalizable;
            s_heapVtable.GetNextFinalizable = &Stub_GetNextFinalizable;
            s_heapVtable.GetMemoryInfo = &Stub_GetMemoryInfo;
            s_heapVtable.GetMemoryLoad = &Stub_GetMemoryLoad;
            s_heapVtable.GetGcLatencyMode = &Stub_GetGcLatencyMode;
            s_heapVtable.SetGcLatencyMode = &Stub_SetGcLatencyMode;
            s_heapVtable.GetLOHCompactionMode = &Stub_GetLOHCompactionMode;
            s_heapVtable.SetLOHCompactionMode = &Stub_SetLOHCompactionMode;
            s_heapVtable.RegisterForFullGCNotification = &Stub_RegisterForFullGCNotification;
            s_heapVtable.CancelFullGCNotification = &Stub_CancelFullGCNotification;
            s_heapVtable.WaitForFullGCApproach = &Stub_WaitForFullGCApproach;
            s_heapVtable.WaitForFullGCComplete = &Stub_WaitForFullGCComplete;
            s_heapVtable.WhichGeneration = &Stub_WhichGeneration;
            s_heapVtable.CollectionCount = &Stub_CollectionCount;
            s_heapVtable.StartNoGCRegion = &Stub_StartNoGCRegion;
            s_heapVtable.EndNoGCRegion = &Stub_EndNoGCRegion;
            s_heapVtable.GetTotalBytesInUse = &Stub_GetTotalBytesInUse;
            s_heapVtable.GetTotalAllocatedBytes = &Stub_GetTotalAllocatedBytes;
            s_heapVtable.GarbageCollect = &Stub_GarbageCollect;
            s_heapVtable.GetMaxGeneration = &GCWksInitialization.GetMaxGeneration;
            s_heapVtable.SetFinalizationRun = &GCWksInitialization.SetFinalizationRun;
            s_heapVtable.RegisterForFinalization = &GCWksInitialization.RegisterForFinalization;
            s_heapVtable.GetLastGCPercentTimeInGC = &Stub_GetLastGCPercentTimeInGC;
            s_heapVtable.GetLastGCGenerationSize = &Stub_GetLastGCGenerationSize;
            s_heapVtable.Initialize = &GCWksInitialization.InitializeHeap;
            s_heapVtable.IsPromoted = &Stub_IsPromoted;
            s_heapVtable.IsHeapPointer = &Stub_IsHeapPointer;
            s_heapVtable.GetCondemnedGeneration = &Stub_GetCondemnedGeneration;
            s_heapVtable.IsGCInProgressHelper = &Stub_IsGCInProgressHelper;
            s_heapVtable.GetGcCount = &Stub_GetGcCount;
            s_heapVtable.IsThreadUsingAllocationContextHeap = &GCWksInitialization.IsThreadUsingAllocationContextHeap;
            s_heapVtable.IsEphemeral = &Stub_IsEphemeral;
            s_heapVtable.WaitUntilGCComplete = &Stub_WaitUntilGCComplete;
            s_heapVtable.FixAllocContext = &Stub_FixAllocContext;
            s_heapVtable.GetCurrentObjSize = &Stub_GetCurrentObjSize;
            s_heapVtable.SetGCInProgress = &Stub_SetGCInProgress;
            s_heapVtable.RuntimeStructuresValid = &Stub_RuntimeStructuresValid;
            s_heapVtable.SetSuspensionPending = &Stub_SetSuspensionPending;
            s_heapVtable.SetYieldProcessorScalingFactor = &Stub_SetYieldProcessorScalingFactor;
            s_heapVtable.Shutdown = &GCWksInitialization.Shutdown;
            s_heapVtable.GetLastGCStartTime = &Stub_GetLastGCStartTime;
            s_heapVtable.GetLastGCDuration = &Stub_GetLastGCDuration;
            s_heapVtable.GetNow = &Stub_GetNow;
            s_heapVtable.Alloc = &GCWksInitialization.Alloc;
            s_heapVtable.PublishObject = &GCWksInitialization.PublishObject;
            s_heapVtable.SetWaitForGCEvent = &Stub_SetWaitForGCEvent;
            s_heapVtable.ResetWaitForGCEvent = &Stub_ResetWaitForGCEvent;
            s_heapVtable.IsLargeObject = &Stub_IsLargeObject;
            s_heapVtable.ValidateObjectMember = &Stub_ValidateObjectMember;
            s_heapVtable.NextObj = &Stub_NextObj;
            s_heapVtable.GetContainingObject = &Stub_GetContainingObject;
            s_heapVtable.DiagWalkObject = &Stub_DiagWalkObject;
            s_heapVtable.DiagWalkObject2 = &Stub_DiagWalkObject2;
            s_heapVtable.DiagWalkHeap = &Stub_DiagWalkHeap;
            s_heapVtable.DiagWalkSurvivorsWithType = &Stub_DiagWalkSurvivorsWithType;
            s_heapVtable.DiagWalkFinalizeQueue = &Stub_DiagWalkFinalizeQueue;
            s_heapVtable.DiagScanFinalizeQueue = &Stub_DiagScanFinalizeQueue;
            s_heapVtable.DiagScanHandles = &Stub_DiagScanHandles;
            s_heapVtable.DiagScanDependentHandles = &Stub_DiagScanDependentHandles;
            s_heapVtable.DiagDescrGenerations = &Stub_DiagDescrGenerations;
            s_heapVtable.DiagTraceGCSegments = &Stub_DiagTraceGCSegments;
            s_heapVtable.DiagGetGCSettings = &Stub_DiagGetGCSettings;
            s_heapVtable.StressHeap = &Stub_StressHeap;
            s_heapVtable.RegisterFrozenSegment = &GCWksInitialization.RegisterFrozenSegment;
            s_heapVtable.UnregisterFrozenSegment = &GCWksInitialization.UnregisterFrozenSegment;
            s_heapVtable.IsInFrozenSegment = &GCWksInitialization.IsInFrozenSegment;
            s_heapVtable.ControlEvents = &GCWksInitialization.ControlEvents;
            s_heapVtable.ControlPrivateEvents = &GCWksInitialization.ControlPrivateEvents;
            s_heapVtable.GetGenerationWithRange = &Stub_GetGenerationWithRange;
            s_heapVtable.GetTotalPauseDuration = &Stub_GetTotalPauseDuration;
            s_heapVtable.EnumerateConfigurationValues = &Stub_EnumerateConfigurationValues;
            s_heapVtable.UpdateFrozenSegment = &GCWksInitialization.UpdateFrozenSegment;
            s_heapVtable.RefreshMemoryLimit = &Stub_RefreshMemoryLimit;
            s_heapVtable.EnableNoGCRegionCallback = &Stub_EnableNoGCRegionCallback;
            s_heapVtable.GetExtraWorkForFinalization = &Stub_GetExtraWorkForFinalization;
            s_heapVtable.GetGenerationBudget = &Stub_GetGenerationBudget;
            s_heapVtable.GetLOHThreshold = &GCWksInitialization.GetLOHThreshold;
            s_heapVtable.DiagWalkHeapWithACHandling = &Stub_DiagWalkHeapWithACHandling;
            s_heapVtable.NullBridgeObjectsWeakRefs = &Stub_NullBridgeObjectsWeakRefs;
            s_handleManagerVtable = default;
            s_handleManagerVtable.Initialize = &GCHandleTables.ManagerInitialize;
            s_handleManagerVtable.Shutdown = &GCHandleTables.ManagerShutdown;
            s_handleManagerVtable.GetGlobalHandleStore = &GCHandleTables.ManagerGetGlobalHandleStore;
            s_handleManagerVtable.CreateHandleStore = &GCHandleTables.ManagerCreateHandleStore;
            s_handleManagerVtable.DestroyHandleStore = &GCHandleTables.ManagerDestroyHandleStore;
            s_handleManagerVtable.CreateGlobalHandleOfType = &GCHandleTables.ManagerCreateGlobalHandleOfType;
            s_handleManagerVtable.CreateDuplicateHandle = &GCHandleTables.ManagerCreateDuplicateHandle;
            s_handleManagerVtable.DestroyHandleOfType = &GCHandleTables.ManagerDestroyHandleOfType;
            s_handleManagerVtable.DestroyHandleOfUnknownType = &GCHandleTables.ManagerDestroyHandleOfUnknownType;
            s_handleManagerVtable.SetExtraInfoForHandle = &GCHandleTables.ManagerSetExtraInfoForHandle;
            s_handleManagerVtable.GetExtraInfoFromHandle = &GCHandleTables.ManagerGetExtraInfoFromHandle;
            s_handleManagerVtable.StoreObjectInHandle = &GCHandleTables.ManagerStoreObjectInHandle;
            s_handleManagerVtable.StoreObjectInHandleIfNull = &GCHandleTables.ManagerStoreObjectInHandleIfNull;
            s_handleManagerVtable.SetDependentHandleSecondary = &GCHandleTables.ManagerSetDependentHandleSecondary;
            s_handleManagerVtable.GetDependentHandleSecondary = &GCHandleTables.ManagerGetDependentHandleSecondary;
            s_handleManagerVtable.InterlockedCompareExchangeObjectInHandle = &GCHandleTables.ManagerInterlockedCompareExchangeObjectInHandle;
            s_handleManagerVtable.HandleFetchType = &GCHandleTables.ManagerHandleFetchType;
            s_handleManagerVtable.TraceRefCountedHandles = &GCHandleTables.ManagerTraceRefCountedHandles;
        }

        private static bool Stub_IsValidSegmentSize(IGCHeap* p0, nuint p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static bool Stub_IsValidGen0MaxSize(IGCHeap* p0, nuint p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static nuint Stub_GetValidSegmentSize(IGCHeap* p0, bool p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static void Stub_SetReservedVMLimit(IGCHeap* p0, nuint p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
        }

        private static void Stub_WaitUntilConcurrentGCComplete(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
        }

        private static bool Stub_IsConcurrentGCInProgress(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static void Stub_TemporaryEnableConcurrentGC(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
        }

        private static void Stub_TemporaryDisableConcurrentGC(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
        }

        private static bool Stub_IsConcurrentGCEnabled(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static int Stub_WaitUntilConcurrentGCCompleteAsync(IGCHeap* p0, int p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static nuint Stub_GetNumberOfFinalizable(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static Object* Stub_GetNextFinalizable(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static void Stub_GetMemoryInfo(IGCHeap* p0, ulong* p1, ulong* p2, ulong* p3, ulong* p4, ulong* p5, ulong* p6, ulong* p7, ulong* p8, ulong* p9, ulong* p10, uint* p11, uint* p12, bool* p13, bool* p14, ulong* p15, ulong* p16, int p17)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            _ = p3;
            _ = p4;
            _ = p5;
            _ = p6;
            _ = p7;
            _ = p8;
            _ = p9;
            _ = p10;
            _ = p11;
            _ = p12;
            _ = p13;
            _ = p14;
            _ = p15;
            _ = p16;
            _ = p17;
            FailFast();
        }

        private static uint Stub_GetMemoryLoad(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static int Stub_GetGcLatencyMode(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static int Stub_SetGcLatencyMode(IGCHeap* p0, int p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static int Stub_GetLOHCompactionMode(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static void Stub_SetLOHCompactionMode(IGCHeap* p0, int p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
        }

        private static bool Stub_RegisterForFullGCNotification(IGCHeap* p0, uint p1, uint p2)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            FailFast();
            return default;
        }

        private static bool Stub_CancelFullGCNotification(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static int Stub_WaitForFullGCApproach(IGCHeap* p0, int p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static int Stub_WaitForFullGCComplete(IGCHeap* p0, int p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static uint Stub_WhichGeneration(IGCHeap* p0, Object* p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static int Stub_CollectionCount(IGCHeap* p0, int p1, int p2)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            FailFast();
            return default;
        }

        private static int Stub_StartNoGCRegion(IGCHeap* p0, ulong p1, bool p2, ulong p3, bool p4)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            _ = p3;
            _ = p4;
            FailFast();
            return default;
        }

        private static int Stub_EndNoGCRegion(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static nuint Stub_GetTotalBytesInUse(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static ulong Stub_GetTotalAllocatedBytes(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static int Stub_GarbageCollect(IGCHeap* p0, int p1, bool p2, int p3)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            _ = p3;
            FailFast();
            return default;
        }

        private static uint Stub_GetMaxGeneration(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static void Stub_SetFinalizationRun(IGCHeap* p0, Object* p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
        }

        private static bool Stub_RegisterForFinalization(IGCHeap* p0, int p1, Object* p2)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            FailFast();
            return default;
        }

        private static int Stub_GetLastGCPercentTimeInGC(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static nuint Stub_GetLastGCGenerationSize(IGCHeap* p0, int p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static int Stub_Initialize(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static bool Stub_IsPromoted(IGCHeap* p0, Object* p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static bool Stub_IsHeapPointer(IGCHeap* p0, void* p1, bool p2)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            FailFast();
            return default;
        }

        private static uint Stub_GetCondemnedGeneration(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static bool Stub_IsGCInProgressHelper(IGCHeap* p0, bool p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static uint Stub_GetGcCount(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static bool Stub_IsThreadUsingAllocationContextHeap(IGCHeap* p0, gc_alloc_context* p1, int p2)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            FailFast();
            return default;
        }

        private static bool Stub_IsEphemeral(IGCHeap* p0, Object* p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static uint Stub_WaitUntilGCComplete(IGCHeap* p0, bool p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static void Stub_FixAllocContext(IGCHeap* p0, gc_alloc_context* p1, void* p2, void* p3)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            _ = p3;
            FailFast();
        }

        private static nuint Stub_GetCurrentObjSize(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static void Stub_SetGCInProgress(IGCHeap* p0, bool p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
        }

        private static bool Stub_RuntimeStructuresValid(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static void Stub_SetSuspensionPending(IGCHeap* p0, bool p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
        }

        private static void Stub_SetYieldProcessorScalingFactor(IGCHeap* p0, float p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
        }

        private static void Stub_Shutdown(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
        }

        private static nuint Stub_GetLastGCStartTime(IGCHeap* p0, int p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static nuint Stub_GetLastGCDuration(IGCHeap* p0, int p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static nuint Stub_GetNow(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static Object* Stub_Alloc(IGCHeap* p0, gc_alloc_context* p1, nuint p2, uint p3)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            _ = p3;
            FailFast();
            return default;
        }

        private static void Stub_PublishObject(IGCHeap* p0, byte* p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
        }

        private static void Stub_SetWaitForGCEvent(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
        }

        private static void Stub_ResetWaitForGCEvent(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
        }

        private static bool Stub_IsLargeObject(IGCHeap* p0, Object* p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static void Stub_ValidateObjectMember(IGCHeap* p0, Object* p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
        }

        private static Object* Stub_NextObj(IGCHeap* p0, Object* p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static Object* Stub_GetContainingObject(IGCHeap* p0, void* p1, bool p2)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            FailFast();
            return default;
        }

        private static void Stub_DiagWalkObject(IGCHeap* p0, Object* p1, delegate* unmanaged[SuppressGCTransition]<Object*, void*, bool> p2, void* p3)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            _ = p3;
            FailFast();
        }

        private static void Stub_DiagWalkObject2(IGCHeap* p0, Object* p1, delegate* unmanaged[SuppressGCTransition]<Object*, byte**, void*, bool> p2, void* p3)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            _ = p3;
            FailFast();
        }

        private static void Stub_DiagWalkHeap(IGCHeap* p0, delegate* unmanaged[SuppressGCTransition]<Object*, void*, bool> p1, void* p2, int p3, bool p4)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            _ = p3;
            _ = p4;
            FailFast();
        }

        private static void Stub_DiagWalkSurvivorsWithType(IGCHeap* p0, void* p1, delegate* unmanaged[SuppressGCTransition]<byte*, byte*, nint, void*, bool, bool, void> p2, void* p3, walk_surv_type p4, int p5)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            _ = p3;
            _ = p4;
            _ = p5;
            FailFast();
        }

        private static void Stub_DiagWalkFinalizeQueue(IGCHeap* p0, void* p1, delegate* unmanaged[SuppressGCTransition]<bool, void*, void> p2)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            FailFast();
        }

        private static void Stub_DiagScanFinalizeQueue(IGCHeap* p0, delegate* unmanaged[SuppressGCTransition]<Object**, ScanContext*, uint, void> p1, ScanContext* p2)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            FailFast();
        }

        private static void Stub_DiagScanHandles(IGCHeap* p0, delegate* unmanaged[SuppressGCTransition]<Object**, Object*, uint, ScanContext*, bool, void> p1, int p2, ScanContext* p3)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            _ = p3;
            FailFast();
        }

        private static void Stub_DiagScanDependentHandles(IGCHeap* p0, delegate* unmanaged[SuppressGCTransition]<Object**, Object*, uint, ScanContext*, bool, void> p1, int p2, ScanContext* p3)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            _ = p3;
            FailFast();
        }

        private static void Stub_DiagDescrGenerations(IGCHeap* p0, delegate* unmanaged[SuppressGCTransition]<void*, int, byte*, byte*, byte*, void> p1, void* p2)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            FailFast();
        }

        private static void Stub_DiagTraceGCSegments(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
        }

        private static void Stub_DiagGetGCSettings(IGCHeap* p0, EtwGCSettingsInfo* p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
        }

        private static bool Stub_StressHeap(IGCHeap* p0, gc_alloc_context* p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static gc_heap_segment_stub* Stub_RegisterFrozenSegment(IGCHeap* p0, segment_info* p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static void Stub_UnregisterFrozenSegment(IGCHeap* p0, gc_heap_segment_stub* p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
        }

        private static bool Stub_IsInFrozenSegment(IGCHeap* p0, Object* p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static void Stub_ControlEvents(IGCHeap* p0, GCEventKeyword p1, GCEventLevel p2)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            FailFast();
        }

        private static void Stub_ControlPrivateEvents(IGCHeap* p0, GCEventKeyword p1, GCEventLevel p2)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            FailFast();
        }

        private static uint Stub_GetGenerationWithRange(IGCHeap* p0, Object* p1, byte** p2, byte** p3, byte** p4)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            _ = p3;
            _ = p4;
            FailFast();
            return default;
        }

        private static long Stub_GetTotalPauseDuration(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static void Stub_EnumerateConfigurationValues(IGCHeap* p0, void* p1, delegate* unmanaged[SuppressGCTransition]<void*, byte*, byte*, GCConfigurationType, long, void> p2)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            FailFast();
        }

        private static void Stub_UpdateFrozenSegment(IGCHeap* p0, gc_heap_segment_stub* p1, byte* p2, byte* p3)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            _ = p3;
            FailFast();
        }

        private static int Stub_RefreshMemoryLimit(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static enable_no_gc_region_callback_status Stub_EnableNoGCRegionCallback(IGCHeap* p0, NoGCRegionCallbackFinalizerWorkItem* p1, ulong p2)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            FailFast();
            return default;
        }

        private static FinalizerWorkItem* Stub_GetExtraWorkForFinalization(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static ulong Stub_GetGenerationBudget(IGCHeap* p0, int p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static nuint Stub_GetLOHThreshold(IGCHeap* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static void Stub_DiagWalkHeapWithACHandling(IGCHeap* p0, delegate* unmanaged[SuppressGCTransition]<Object*, void*, bool> p1, void* p2, int p3, bool p4)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            _ = p3;
            _ = p4;
            FailFast();
        }

        private static void Stub_NullBridgeObjectsWeakRefs(IGCHeap* p0, nuint p1, void* p2)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            FailFast();
        }

        private static bool Stub_Initialize(IGCHandleManager* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static void Stub_Shutdown(IGCHandleManager* p0)
        {
            _ = p0;
            FailFast();
        }

        private static IGCHandleStore* Stub_GetGlobalHandleStore(IGCHandleManager* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static IGCHandleStore* Stub_CreateHandleStore(IGCHandleManager* p0)
        {
            _ = p0;
            FailFast();
            return default;
        }

        private static void Stub_DestroyHandleStore(IGCHandleManager* p0, IGCHandleStore* p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
        }

        private static OBJECTHANDLE__* Stub_CreateGlobalHandleOfType(IGCHandleManager* p0, Object* p1, HandleType p2)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            FailFast();
            return default;
        }

        private static OBJECTHANDLE__* Stub_CreateDuplicateHandle(IGCHandleManager* p0, OBJECTHANDLE__* p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static void Stub_DestroyHandleOfType(IGCHandleManager* p0, OBJECTHANDLE__* p1, HandleType p2)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            FailFast();
        }

        private static void Stub_DestroyHandleOfUnknownType(IGCHandleManager* p0, OBJECTHANDLE__* p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
        }

        private static void Stub_SetExtraInfoForHandle(IGCHandleManager* p0, OBJECTHANDLE__* p1, HandleType p2, void* p3)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            _ = p3;
            FailFast();
        }

        private static void* Stub_GetExtraInfoFromHandle(IGCHandleManager* p0, OBJECTHANDLE__* p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static void Stub_StoreObjectInHandle(IGCHandleManager* p0, OBJECTHANDLE__* p1, Object* p2)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            FailFast();
        }

        private static bool Stub_StoreObjectInHandleIfNull(IGCHandleManager* p0, OBJECTHANDLE__* p1, Object* p2)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            FailFast();
            return default;
        }

        private static void Stub_SetDependentHandleSecondary(IGCHandleManager* p0, OBJECTHANDLE__* p1, Object* p2)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            FailFast();
        }

        private static Object* Stub_GetDependentHandleSecondary(IGCHandleManager* p0, OBJECTHANDLE__* p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static Object* Stub_InterlockedCompareExchangeObjectInHandle(IGCHandleManager* p0, OBJECTHANDLE__* p1, Object* p2, Object* p3)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            _ = p3;
            FailFast();
            return default;
        }

        private static HandleType Stub_HandleFetchType(IGCHandleManager* p0, OBJECTHANDLE__* p1)
        {
            _ = p0;
            _ = p1;
            FailFast();
            return default;
        }

        private static void Stub_TraceRefCountedHandles(IGCHandleManager* p0, delegate* unmanaged[SuppressGCTransition]<Object**, nuint*, nuint, nuint, void> p1, nuint p2, nuint p3)
        {
            _ = p0;
            _ = p1;
            _ = p2;
            _ = p3;
            FailFast();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FailFast()
        {
            System.Runtime.InternalCalls.RhpFallbackFailFast();
        }

        private unsafe struct GCNameStorage
        {
            public fixed byte Value[16];
        }
    }
}
