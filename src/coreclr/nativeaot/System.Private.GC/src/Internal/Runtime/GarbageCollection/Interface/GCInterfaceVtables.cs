// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The C++ GC/EE interface is a set of abstract classes, which the C# port sees as C++ vtables:
// a pointer to an array of function pointers, in declaration order, each taking the object as
// its first argument. Each struct below mirrors one such vtable, one field per virtual slot, in
// exactly the order the methods are declared in the C++ header. Adding, removing or reordering a
// method in the C++ header requires the same change here.
//
// C++ `bool` is mapped to `byte` because `bool` is not blittable in a `delegate* unmanaged`
// signature, and `Object*` is mapped to `byte*` because the GC never holds a managed reference.
//
// Slot types differ by direction, and the difference is load-bearing:
//
// * IGCToCLR/IGCToCLREventSink are implemented by the native EE, so their slots are
//   `delegate* unmanaged[SuppressGCTransition]<...>` -- genuinely foreign function pointers
//   that preserve the GC thread's cooperative mode, matching calls from the C++ GC.
// * IGCHeap/IGCHandleManager/IGCHandleStore are implemented here, so their slots are
//   *managed* `delegate*<...>` holding the address of a static C# method. ILC compiles a
//   static method with a blittable signature to the platform C ABI, so the native EE can call
//   it directly; that is the same property `[RuntimeExport]` relies on, and taking the
//   method's address yields the entry point the export alias would name.
//
//   Do not "fix" these to `delegate* unmanaged<...>` by marking the implementations
//   `[UnmanagedCallersOnly]`. ILC sets CORJIT_FLAG_REVERSE_PINVOKE unconditionally for such
//   methods, and the resulting thread attach fail-fasts when the EE calls the GC from
//   cooperative mode -- which is every allocation.

using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>
    /// Virtual method table of gcinterface.h `IGCHandleStore`, in declaration order (6 slots).
    /// </summary>
    internal unsafe struct IGCHandleStoreVtable
    {
        /// <summary>Number of virtual slots this vtable describes.</summary>
        public const int SlotCount = 6;

        public delegate*<void*, void> Uproot;
        public delegate*<void*, OBJECTHANDLE, byte> ContainsHandle;
        public delegate*<void*, byte*, HandleType, OBJECTHANDLE> CreateHandleOfType;
        public delegate*<void*, byte*, HandleType, int, OBJECTHANDLE> CreateHandleOfType_2;
        public delegate*<void*, byte*, HandleType, void*, OBJECTHANDLE> CreateHandleWithExtraInfo;
        public delegate*<void*, byte*, byte*, OBJECTHANDLE> CreateDependentHandle;

        // IGCHandleStore also declares a virtual destructor, which the Itanium C++ ABI
        // places in two additional slots after the ones above. It is declared last, so the
        // slots that matter here are unaffected; any new method must be added before it.
    }

    /// <summary>
    /// Virtual method table of gcinterface.h `IGCHandleManager`, in declaration order (18 slots).
    /// </summary>
    internal unsafe struct IGCHandleManagerVtable
    {
        /// <summary>Number of virtual slots this vtable describes.</summary>
        public const int SlotCount = 18;

        public delegate*<void*, byte> Initialize;
        public delegate*<void*, void> Shutdown;
        public delegate*<void*, void*> GetGlobalHandleStore;
        public delegate*<void*, void*> CreateHandleStore;
        public delegate*<void*, void*, void> DestroyHandleStore;
        public delegate*<void*, byte*, HandleType, OBJECTHANDLE> CreateGlobalHandleOfType;
        public delegate*<void*, OBJECTHANDLE, OBJECTHANDLE> CreateDuplicateHandle;
        public delegate*<void*, OBJECTHANDLE, HandleType, void> DestroyHandleOfType;
        public delegate*<void*, OBJECTHANDLE, void> DestroyHandleOfUnknownType;
        public delegate*<void*, OBJECTHANDLE, HandleType, void*, void> SetExtraInfoForHandle;
        public delegate*<void*, OBJECTHANDLE, void*> GetExtraInfoFromHandle;
        public delegate*<void*, OBJECTHANDLE, byte*, void> StoreObjectInHandle;
        public delegate*<void*, OBJECTHANDLE, byte*, byte> StoreObjectInHandleIfNull;
        public delegate*<void*, OBJECTHANDLE, byte*, void> SetDependentHandleSecondary;
        public delegate*<void*, OBJECTHANDLE, byte*> GetDependentHandleSecondary;
        public delegate*<void*, OBJECTHANDLE, byte*, byte*, byte*> InterlockedCompareExchangeObjectInHandle;
        public delegate*<void*, OBJECTHANDLE, HandleType> HandleFetchType;
        public delegate*<void*, delegate* unmanaged<byte**, nuint*, nuint, nuint, void>, nuint, nuint, void> TraceRefCountedHandles;
    }

    /// <summary>
    /// Virtual method table of gcinterface.h `IGCHeap`, in declaration order (89 slots).
    /// </summary>
    internal unsafe struct IGCHeapVtable
    {
        /// <summary>Number of virtual slots this vtable describes.</summary>
        public const int SlotCount = 89;

        public delegate*<void*, nuint, byte> IsValidSegmentSize;
        public delegate*<void*, nuint, byte> IsValidGen0MaxSize;
        public delegate*<void*, byte, nuint> GetValidSegmentSize;
        public delegate*<void*, nuint, void> SetReservedVMLimit;
        public delegate*<void*, void> WaitUntilConcurrentGCComplete;
        public delegate*<void*, byte> IsConcurrentGCInProgress;
        public delegate*<void*, void> TemporaryEnableConcurrentGC;
        public delegate*<void*, void> TemporaryDisableConcurrentGC;
        public delegate*<void*, byte> IsConcurrentGCEnabled;
        public delegate*<void*, int, int> WaitUntilConcurrentGCCompleteAsync;
        public delegate*<void*, nuint> GetNumberOfFinalizable;
        public delegate*<void*, byte*> GetNextFinalizable;
        public delegate*<void*, ulong*, ulong*, ulong*, ulong*, ulong*, ulong*, ulong*, ulong*, ulong*, ulong*, uint*, uint*, byte*, byte*, ulong*, ulong*, int, void> GetMemoryInfo;
        public delegate*<void*, uint> GetMemoryLoad;
        public delegate*<void*, int> GetGcLatencyMode;
        public delegate*<void*, int, int> SetGcLatencyMode;
        public delegate*<void*, int> GetLOHCompactionMode;
        public delegate*<void*, int, void> SetLOHCompactionMode;
        public delegate*<void*, uint, uint, byte> RegisterForFullGCNotification;
        public delegate*<void*, byte> CancelFullGCNotification;
        public delegate*<void*, int, int> WaitForFullGCApproach;
        public delegate*<void*, int, int> WaitForFullGCComplete;
        public delegate*<void*, byte*, uint> WhichGeneration;
        public delegate*<void*, int, int, int> CollectionCount;
        public delegate*<void*, ulong, byte, ulong, byte, int> StartNoGCRegion;
        public delegate*<void*, int> EndNoGCRegion;
        public delegate*<void*, nuint> GetTotalBytesInUse;
        public delegate*<void*, ulong> GetTotalAllocatedBytes;
        public delegate*<void*, int, byte, int, int> GarbageCollect;
        public delegate*<void*, uint> GetMaxGeneration;
        public delegate*<void*, byte*, void> SetFinalizationRun;
        public delegate*<void*, int, byte*, byte> RegisterForFinalization;
        public delegate*<void*, int> GetLastGCPercentTimeInGC;
        public delegate*<void*, int, nuint> GetLastGCGenerationSize;
        public delegate*<void*, int> Initialize;
        public delegate*<void*, byte*, byte> IsPromoted;
        public delegate*<void*, void*, byte, byte> IsHeapPointer;
        public delegate*<void*, uint> GetCondemnedGeneration;
        public delegate*<void*, byte, byte> IsGCInProgressHelper;
        public delegate*<void*, uint> GetGcCount;
        public delegate*<void*, gc_alloc_context*, int, byte> IsThreadUsingAllocationContextHeap;
        public delegate*<void*, byte*, byte> IsEphemeral;
        public delegate*<void*, byte, uint> WaitUntilGCComplete;
        public delegate*<void*, gc_alloc_context*, void*, void*, void> FixAllocContext;
        public delegate*<void*, nuint> GetCurrentObjSize;
        public delegate*<void*, byte, void> SetGCInProgress;
        public delegate*<void*, byte> RuntimeStructuresValid;
        public delegate*<void*, byte, void> SetSuspensionPending;
        public delegate*<void*, float, void> SetYieldProcessorScalingFactor;
        public delegate*<void*, void> Shutdown;
        public delegate*<void*, int, nuint> GetLastGCStartTime;
        public delegate*<void*, int, nuint> GetLastGCDuration;
        public delegate*<void*, nuint> GetNow;
        public delegate*<void*, gc_alloc_context*, nuint, uint, byte*> Alloc;
        public delegate*<void*, byte*, void> PublishObject;
        public delegate*<void*, void> SetWaitForGCEvent;
        public delegate*<void*, void> ResetWaitForGCEvent;
        public delegate*<void*, byte*, byte> IsLargeObject;
        public delegate*<void*, byte*, void> ValidateObjectMember;
        public delegate*<void*, byte*, byte*> NextObj;
        public delegate*<void*, void*, byte, byte*> GetContainingObject;
        public delegate*<void*, byte*, delegate* unmanaged<byte*, void*, byte>, void*, void> DiagWalkObject;
        public delegate*<void*, byte*, delegate* unmanaged<byte*, byte**, void*, byte>, void*, void> DiagWalkObject2;
        public delegate*<void*, delegate* unmanaged<byte*, void*, byte>, void*, int, byte, void> DiagWalkHeap;
        public delegate*<void*, void*, delegate* unmanaged<byte*, byte*, nint, void*, byte, byte, void>, void*, walk_surv_type, int, void> DiagWalkSurvivorsWithType;
        public delegate*<void*, void*, delegate* unmanaged<byte, void*, void>, void> DiagWalkFinalizeQueue;
        public delegate*<void*, delegate* unmanaged<byte**, ScanContext*, uint, void>, ScanContext*, void> DiagScanFinalizeQueue;
        public delegate*<void*, delegate* unmanaged<byte**, byte*, uint, ScanContext*, byte, void>, int, ScanContext*, void> DiagScanHandles;
        public delegate*<void*, delegate* unmanaged<byte**, byte*, uint, ScanContext*, byte, void>, int, ScanContext*, void> DiagScanDependentHandles;
        public delegate*<void*, delegate* unmanaged<void*, int, byte*, byte*, byte*, void>, void*, void> DiagDescrGenerations;
        public delegate*<void*, void> DiagTraceGCSegments;
        public delegate*<void*, EtwGCSettingsInfo*, void> DiagGetGCSettings;
        public delegate*<void*, gc_alloc_context*, byte> StressHeap;
        public delegate*<void*, segment_info*, segment_handle> RegisterFrozenSegment;
        public delegate*<void*, segment_handle, void> UnregisterFrozenSegment;
        public delegate*<void*, byte*, byte> IsInFrozenSegment;
        public delegate*<void*, GCEventKeyword, GCEventLevel, void> ControlEvents;
        public delegate*<void*, GCEventKeyword, GCEventLevel, void> ControlPrivateEvents;
        public delegate*<void*, byte*, byte**, byte**, byte**, uint> GetGenerationWithRange;
        public delegate*<void*, long> GetTotalPauseDuration;
        public delegate*<void*, void*, delegate* unmanaged<void*, byte*, byte*, GCConfigurationType, long, void>, void> EnumerateConfigurationValues;
        public delegate*<void*, segment_handle, byte*, byte*, void> UpdateFrozenSegment;
        public delegate*<void*, int> RefreshMemoryLimit;
        public delegate*<void*, NoGCRegionCallbackFinalizerWorkItem*, ulong, enable_no_gc_region_callback_status> EnableNoGCRegionCallback;
        public delegate*<void*, FinalizerWorkItem*> GetExtraWorkForFinalization;
        public delegate*<void*, int, ulong> GetGenerationBudget;
        public delegate*<void*, nuint> GetLOHThreshold;
        public delegate*<void*, delegate* unmanaged<byte*, void*, byte>, void*, int, byte, void> DiagWalkHeapWithACHandling;
        public delegate*<void*, nuint, void*, void> NullBridgeObjectsWeakRefs;
    }

    /// <summary>
    /// Virtual method table of gc.h `IGCHeapInternal`, which derives from `IGCHeap` and adds the
    /// slots the GC uses internally. The object the GC hands to the EE is one of these; the EE
    /// only ever looks at the `IGCHeap` prefix.
    /// </summary>
    internal unsafe struct IGCHeapInternalVtable
    {
        /// <summary>Number of virtual slots this vtable describes.</summary>
        public const int SlotCount = IGCHeapVtable.SlotCount + 4;

        /// <summary>
        /// The inherited `IGCHeap` slots. Single inheritance from a class with no data members
        /// puts them first, before anything the derived class declares.
        /// </summary>
        public IGCHeapVtable IGCHeap;

        public delegate*<void*, int> GetNumberOfHeaps;
        public delegate*<void*, int> GetHomeHeapNumber;
        public delegate*<void*, int, nuint> GetPromotedBytes;
        public delegate*<void*, byte*, byte, byte> IsPromoted2;
    }

    /// <summary>
    /// Virtual method table of gcinterface.ee.h `IGCToCLR`, in declaration order (52 slots).
    /// </summary>
    internal unsafe struct IGCToCLRVtable
    {
        /// <summary>Number of virtual slots this vtable describes.</summary>
        public const int SlotCount = 52;

        public delegate* unmanaged[SuppressGCTransition]<void*, SUSPEND_REASON, void> SuspendEE;
        public delegate* unmanaged[SuppressGCTransition]<void*, byte, void> RestartEE;
        public delegate* unmanaged[SuppressGCTransition]<void*, delegate* unmanaged<byte**, ScanContext*, uint, void>, int, int, ScanContext*, void> GcScanRoots;
        public delegate* unmanaged[SuppressGCTransition]<void*, int, int, void> GcStartWork;
        public delegate* unmanaged[SuppressGCTransition]<void*, int, byte, byte, void> BeforeGcScanRoots;
        public delegate* unmanaged[SuppressGCTransition]<void*, int, int, ScanContext*, void> AfterGcScanRoots;
        public delegate* unmanaged[SuppressGCTransition]<void*, int, void> GcDone;
        public delegate* unmanaged[SuppressGCTransition]<void*, byte*, byte> RefCountedHandleCallbacks;
        public delegate* unmanaged[SuppressGCTransition]<void*, delegate* unmanaged<byte**, nuint*, nuint, nuint, void>, nuint, nuint, void> SyncBlockCacheWeakPtrScan;
        public delegate* unmanaged[SuppressGCTransition]<void*, int, void> SyncBlockCacheDemote;
        public delegate* unmanaged[SuppressGCTransition]<void*, int, void> SyncBlockCachePromotionsGranted;
        public delegate* unmanaged[SuppressGCTransition]<void*, uint> GetActiveSyncBlockCount;
        public delegate* unmanaged[SuppressGCTransition]<void*, byte> IsPreemptiveGCDisabled;
        public delegate* unmanaged[SuppressGCTransition]<void*, byte> EnablePreemptiveGC;
        public delegate* unmanaged[SuppressGCTransition]<void*, void> DisablePreemptiveGC;
        public delegate* unmanaged[SuppressGCTransition]<void*, void*> GetThread;
        public delegate* unmanaged[SuppressGCTransition]<void*, gc_alloc_context*> GetAllocContext;
        public delegate* unmanaged[SuppressGCTransition]<void*, delegate* unmanaged<gc_alloc_context*, void*, void>, void*, void> GcEnumAllocContexts;
        public delegate* unmanaged[SuppressGCTransition]<void*, byte*, byte*> GetLoaderAllocatorObjectForGC;
        public delegate* unmanaged[SuppressGCTransition]<void*, delegate* unmanaged<void*, void>, void*, byte, byte*, byte> CreateThread;
        public delegate* unmanaged[SuppressGCTransition]<void*, int, byte, void> DiagGCStart;
        public delegate* unmanaged[SuppressGCTransition]<void*, void> DiagUpdateGenerationBounds;
        public delegate* unmanaged[SuppressGCTransition]<void*, nuint, int, int, byte, void> DiagGCEnd;
        public delegate* unmanaged[SuppressGCTransition]<void*, void*, void> DiagWalkFReachableObjects;
        public delegate* unmanaged[SuppressGCTransition]<void*, void*, byte, void> DiagWalkSurvivors;
        public delegate* unmanaged[SuppressGCTransition]<void*, void*, int, void> DiagWalkUOHSurvivors;
        public delegate* unmanaged[SuppressGCTransition]<void*, void*, void> DiagWalkBGCSurvivors;
        public delegate* unmanaged[SuppressGCTransition]<void*, WriteBarrierParameters*, void> StompWriteBarrier;
        public delegate* unmanaged[SuppressGCTransition]<void*, byte, void> EnableFinalization;
        public delegate* unmanaged[SuppressGCTransition]<void*, uint, void> HandleFatalError;
        public delegate* unmanaged[SuppressGCTransition]<void*, byte*, byte> EagerFinalized;
        public delegate* unmanaged[SuppressGCTransition]<void*, void*> GetFreeObjectMethodTable;
        public delegate* unmanaged[SuppressGCTransition]<void*, byte*, byte*, byte*, byte> GetBooleanConfigValue;
        public delegate* unmanaged[SuppressGCTransition]<void*, byte*, byte*, long*, byte> GetIntConfigValue;
        public delegate* unmanaged[SuppressGCTransition]<void*, byte*, byte*, byte**, byte> GetStringConfigValue;
        public delegate* unmanaged[SuppressGCTransition]<void*, byte*, void> FreeStringConfigValue;
        public delegate* unmanaged[SuppressGCTransition]<void*, byte> IsGCThread;
        public delegate* unmanaged[SuppressGCTransition]<void*, byte> WasCurrentThreadCreatedByGC;
        public delegate* unmanaged[SuppressGCTransition]<void*, byte*, ScanContext*, delegate* unmanaged<byte**, ScanContext*, uint, void>, void> WalkAsyncPinnedForPromotion;
        public delegate* unmanaged[SuppressGCTransition]<void*, byte*, void*, delegate* unmanaged<byte*, byte*, void*, void>, void> WalkAsyncPinned;
        public delegate* unmanaged[SuppressGCTransition]<void*, void*> EventSink;
        public delegate* unmanaged[SuppressGCTransition]<void*, uint> GetTotalNumSizedRefHandles;
        public delegate* unmanaged[SuppressGCTransition]<void*, int, byte> AnalyzeSurvivorsRequested;
        public delegate* unmanaged[SuppressGCTransition]<void*, nuint, int, ulong, delegate* unmanaged<void>, void> AnalyzeSurvivorsFinished;
        public delegate* unmanaged[SuppressGCTransition]<void*, void> VerifySyncTableEntry;
        public delegate* unmanaged[SuppressGCTransition]<void*, int, int, int, int, void> UpdateGCEventStatus;
        public delegate* unmanaged[SuppressGCTransition]<void*, uint, uint, void*, void> LogStressMsg;
        public delegate* unmanaged[SuppressGCTransition]<void*, uint> GetCurrentProcessCpuCount;
        public delegate* unmanaged[SuppressGCTransition]<void*, int, byte*, byte*, byte*, void> DiagAddNewRegion;
        public delegate* unmanaged[SuppressGCTransition]<void*, byte*, void> LogErrorToHost;
        public delegate* unmanaged[SuppressGCTransition]<void*, void*, ulong> GetThreadOSThreadId;
        public delegate* unmanaged[SuppressGCTransition]<void*, MarkCrossReferencesArgs*, void> TriggerClientBridgeProcessing;
    }

    /// <summary>
    /// Virtual method table of gcinterface.ee.h `IGCToCLREventSink`, in declaration order (38 slots).
    /// </summary>
    internal unsafe struct IGCToCLREventSinkVtable
    {
        /// <summary>Number of virtual slots this vtable describes.</summary>
        public const int SlotCount = 38;

        public delegate* unmanaged[SuppressGCTransition]<void*, byte*, void*, uint, void> FireDynamicEvent;
        public delegate* unmanaged[SuppressGCTransition]<void*, uint, uint, uint, uint, void> FireGCStart_V2;
        public delegate* unmanaged[SuppressGCTransition]<void*, uint, uint, void> FireGCEnd_V1;
        public delegate* unmanaged[SuppressGCTransition]<void*, byte, void*, ulong, ulong, void> FireGCGenerationRange;
        public delegate* unmanaged[SuppressGCTransition]<void*, ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong, uint, uint, uint, void> FireGCHeapStats_V2;
        public delegate* unmanaged[SuppressGCTransition]<void*, void*, nuint, uint, void> FireGCCreateSegment_V1;
        public delegate* unmanaged[SuppressGCTransition]<void*, void*, void> FireGCFreeSegment_V1;
        public delegate* unmanaged[SuppressGCTransition]<void*, void> FireGCCreateConcurrentThread_V1;
        public delegate* unmanaged[SuppressGCTransition]<void*, void> FireGCTerminateConcurrentThread_V1;
        public delegate* unmanaged[SuppressGCTransition]<void*, uint, void> FireGCTriggered;
        public delegate* unmanaged[SuppressGCTransition]<void*, uint, uint, ulong, void> FireGCMarkWithType;
        public delegate* unmanaged[SuppressGCTransition]<void*, uint, uint, uint, uint, void> FireGCJoin_V2;
        public delegate* unmanaged[SuppressGCTransition]<void*, ulong, int, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, void*, void> FireGCGlobalHeapHistory_V4;
        public delegate* unmanaged[SuppressGCTransition]<void*, uint, uint, void> FireGCAllocationTick_V1;
        public delegate* unmanaged[SuppressGCTransition]<void*, ulong, uint, uint, void*, ulong, void> FireGCAllocationTick_V4;
        public delegate* unmanaged[SuppressGCTransition]<void*, void*, byte**, void> FirePinObjectAtGCTime;
        public delegate* unmanaged[SuppressGCTransition]<void*, byte*, byte*, byte*, void> FirePinPlugAtGCTime;
        public delegate* unmanaged[SuppressGCTransition]<void*, void*, void*, void*, void*, void*, void*, uint, uint, uint, uint, uint, uint, void*, uint, uint, void*, void> FireGCPerHeapHistory_V3;
        public delegate* unmanaged[SuppressGCTransition]<void*, ushort, uint, void*, void> FireGCLOHCompact;
        public delegate* unmanaged[SuppressGCTransition]<void*, ushort, nuint, ushort, uint, void*, void> FireGCFitBucketInfo;
        public delegate* unmanaged[SuppressGCTransition]<void*, void> FireBGCBegin;
        public delegate* unmanaged[SuppressGCTransition]<void*, void> FireBGC1stNonConEnd;
        public delegate* unmanaged[SuppressGCTransition]<void*, void> FireBGC1stConEnd;
        public delegate* unmanaged[SuppressGCTransition]<void*, uint, void> FireBGC1stSweepEnd;
        public delegate* unmanaged[SuppressGCTransition]<void*, void> FireBGC2ndNonConBegin;
        public delegate* unmanaged[SuppressGCTransition]<void*, void> FireBGC2ndNonConEnd;
        public delegate* unmanaged[SuppressGCTransition]<void*, void> FireBGC2ndConBegin;
        public delegate* unmanaged[SuppressGCTransition]<void*, void> FireBGC2ndConEnd;
        public delegate* unmanaged[SuppressGCTransition]<void*, ulong, void> FireBGCDrainMark;
        public delegate* unmanaged[SuppressGCTransition]<void*, ulong, ulong, uint, void> FireBGCRevisit;
        public delegate* unmanaged[SuppressGCTransition]<void*, ulong, ulong, ulong, uint, uint, void> FireBGCOverflow_V1;
        public delegate* unmanaged[SuppressGCTransition]<void*, uint, void> FireBGCAllocWaitBegin;
        public delegate* unmanaged[SuppressGCTransition]<void*, uint, void> FireBGCAllocWaitEnd;
        public delegate* unmanaged[SuppressGCTransition]<void*, uint, uint, void> FireGCFullNotify_V1;
        public delegate* unmanaged[SuppressGCTransition]<void*, void*, void*, uint, uint, void> FireSetGCHandle;
        public delegate* unmanaged[SuppressGCTransition]<void*, void*, void*, uint, uint, void> FirePrvSetGCHandle;
        public delegate* unmanaged[SuppressGCTransition]<void*, void*, void> FireDestroyGCHandle;
        public delegate* unmanaged[SuppressGCTransition]<void*, void*, void> FirePrvDestroyGCHandle;
    }
}
