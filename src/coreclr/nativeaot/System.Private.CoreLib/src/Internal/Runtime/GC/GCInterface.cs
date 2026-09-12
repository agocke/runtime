// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable CA1823, CS0169

namespace Internal.Runtime.GC
{
    internal enum GCInterfaceVersionConstants : uint
    {
        GC_INTERFACE_MAJOR_VERSION = 5,
        GC_INTERFACE_MINOR_VERSION = 9,
        EE_INTERFACE_MAJOR_VERSION = 5,
    }

    internal enum GCInterfaceConstants : int
    {
        GC_CALL_INTERIOR = 0x1,
        GC_CALL_PINNED = 0x2,
    }

    internal enum GC_ALLOC_FLAGS : int
    {
        GC_ALLOC_NO_FLAGS = 0,
        GC_ALLOC_FINALIZE = 1,
        GC_ALLOC_CONTAINS_REF = 2,
        GC_ALLOC_ALIGN8_BIAS = 4,
        GC_ALLOC_ALIGN8 = 8,
        GC_ALLOC_ZEROING_OPTIONAL = 16,
        GC_ALLOC_LARGE_OBJECT_HEAP = 32,
        GC_ALLOC_PINNED_OBJECT_HEAP = 64,
        GC_ALLOC_USER_OLD_HEAP = GC_ALLOC_LARGE_OBJECT_HEAP | GC_ALLOC_PINNED_OBJECT_HEAP,
    }

    internal enum collection_mode : int
    {
        collection_non_blocking = 0x00000001,
        collection_blocking = 0x00000002,
        collection_optimized = 0x00000004,
        collection_compacting = 0x00000008,
        collection_aggressive = 0x00000010,
    }

    internal enum GCConfigurationType
    {
        Int64,
        StringUtf8,
        Boolean,
    }

    internal enum EtwGCRootKind : int
    {
        kEtwGCRootKindStack = 0,
        kEtwGCRootKindFinalizer = 1,
        kEtwGCRootKindHandle = 2,
        kEtwGCRootKindOther = 3,
    }

    internal enum walk_surv_type : int
    {
        walk_for_gc = 1,
        walk_for_bgc = 2,
        walk_for_uoh = 3,
    }

    internal enum GCEventLevel : int
    {
        GCEventLevel_None = 0,
        GCEventLevel_Fatal = 1,
        GCEventLevel_Error = 2,
        GCEventLevel_Warning = 3,
        GCEventLevel_Information = 4,
        GCEventLevel_Verbose = 5,
        GCEventLevel_Max = 6,
        GCEventLevel_LogAlways = 255,
    }

    internal enum GCEventKeyword : int
    {
        GCEventKeyword_None = 0x0,
        GCEventKeyword_GC = 0x1,
        GCEventKeyword_GCPrivate = 0x1,
        GCEventKeyword_GCHandle = 0x2,
        GCEventKeyword_GCHandlePrivate = 0x4000,
        GCEventKeyword_GCHeapDump = 0x100000,
        GCEventKeyword_GCSampledObjectAllocationHigh = 0x200000,
        GCEventKeyword_GCHeapSurvivalAndMovement = 0x400000,
        GCEventKeyword_ManagedHeapCollect = 0x800000,
        GCEventKeyword_GCHeapAndTypeNames = 0x1000000,
        GCEventKeyword_GCSampledObjectAllocationLow = 0x2000000,
        GCEventKeyword_All = GCEventKeyword_GC
            | GCEventKeyword_GCPrivate
            | GCEventKeyword_GCHandle
            | GCEventKeyword_GCHandlePrivate
            | GCEventKeyword_GCHeapDump
            | GCEventKeyword_GCSampledObjectAllocationHigh
            | GCEventKeyword_GCHeapSurvivalAndMovement
            | GCEventKeyword_ManagedHeapCollect
            | GCEventKeyword_GCHeapAndTypeNames
            | GCEventKeyword_GCSampledObjectAllocationLow,
    }

    internal enum HandleType : int
    {
        HNDTYPE_WEAK_SHORT = 0,
        HNDTYPE_WEAK_LONG = 1,
        HNDTYPE_WEAK_DEFAULT = 1,
        HNDTYPE_STRONG = 2,
        HNDTYPE_DEFAULT = 2,
        HNDTYPE_PINNED = 3,
        HNDTYPE_VARIABLE = 4,
        HNDTYPE_REFCOUNTED = 5,
        HNDTYPE_DEPENDENT = 6,
        HNDTYPE_ASYNCPINNED = 7,
        HNDTYPE_SIZEDREF = 8,
        HNDTYPE_WEAK_NATIVE_COM = 9,
        HNDTYPE_WEAK_INTERIOR_POINTER = 10,
        HNDTYPE_CROSSREFERENCE = 11,
    }

    internal enum enable_no_gc_region_callback_status : int
    {
        succeed,
        not_started,
        insufficient_budget,
        already_registered,
    }

    internal unsafe struct Object
    {
        public MethodTable* m_pMethTab;

        public ObjHeader* GetHeader()
        {
            fixed (Object* pThis = &this)
            {
                return ((ObjHeader*)pThis) - 1;
            }
        }

        public MethodTable* RawGetMethodTable()
        {
            return m_pMethTab;
        }

        public MethodTable* GetGCSafeMethodTable()
        {
            return (MethodTable*)((nuint)m_pMethTab & ~(nuint)7);
        }

        public void RawSetMethodTable(MethodTable* pMT)
        {
            m_pMethTab = pMT;
        }
    }

    internal unsafe struct Thread
    {
        private byte _opaque;
    }

    internal unsafe struct OBJECTHANDLE__
    {
        public void* unused;
    }

    internal unsafe struct gc_heap_segment_stub
    {
        private byte _opaque;
    }

    internal unsafe struct GcDacVars
    {
        private byte _opaque;
    }

    internal unsafe struct VersionInfo
    {
        public uint MajorVersion;
        public uint MinorVersion;
        public uint BuildVersion;
        public byte* Name;
    }

    internal unsafe struct gc_alloc_context
    {
        public byte* alloc_ptr;
        public byte* alloc_limit;
        public long alloc_bytes;
        public long alloc_bytes_uoh;
        public void* gc_reserved_1;
        public void* gc_reserved_2;
        public int alloc_count;
    }

    internal unsafe struct ScanContext
    {
        public Thread* thread_under_crawl;
        public int thread_number;
        public int thread_count;
        public nuint stack_limit;
        public bool promotion;
        public bool concurrent;
        public void* _unused1;
        public void* pMD;
        public EtwGCRootKind dwEtwRootKind;
    }

    internal unsafe struct segment_info
    {
        public void* pvMem;
        public nuint ibFirstObject;
        public nuint ibAllocated;
        public nuint ibCommit;
        public nuint ibReserved;
    }

    internal unsafe struct EtwGCSettingsInfo
    {
        public nuint heap_hard_limit;
        public nuint loh_threshold;
        public nuint physical_memory_from_config;
        public nuint gen0_min_budget_from_config;
        public nuint gen0_max_budget_from_config;
        public uint high_mem_percent_from_config;
        public bool concurrent_gc_p;
        public bool use_large_pages_p;
        public bool use_frozen_segments_p;
        public bool hard_limit_config_p;
        public bool no_affinitize_p;
    }

    internal unsafe struct FinalizerWorkItem
    {
        public FinalizerWorkItem* next;
        public delegate* unmanaged<FinalizerWorkItem*, void> callback;
    }

    internal unsafe struct NoGCRegionCallbackFinalizerWorkItem
    {
        public FinalizerWorkItem* next;
        public delegate* unmanaged<FinalizerWorkItem*, void> callback;
        public bool scheduled;
        public bool abandoned;
    }

    internal unsafe struct IGCHeap
    {
        public IGCHeapVtable* Vtable;
    }

    internal unsafe struct IGCHandleManager
    {
        public IGCHandleManagerVtable* Vtable;
    }

    internal unsafe struct IGCHeapVtable
    {
        public delegate* unmanaged<IGCHeap*, nuint, bool> IsValidSegmentSize;
        public delegate* unmanaged<IGCHeap*, nuint, bool> IsValidGen0MaxSize;
        public delegate* unmanaged<IGCHeap*, bool, nuint> GetValidSegmentSize;
        public delegate* unmanaged<IGCHeap*, nuint, void> SetReservedVMLimit;
        public delegate* unmanaged<IGCHeap*, void> WaitUntilConcurrentGCComplete;
        public delegate* unmanaged<IGCHeap*, bool> IsConcurrentGCInProgress;
        public delegate* unmanaged<IGCHeap*, void> TemporaryEnableConcurrentGC;
        public delegate* unmanaged<IGCHeap*, void> TemporaryDisableConcurrentGC;
        public delegate* unmanaged<IGCHeap*, bool> IsConcurrentGCEnabled;
        public delegate* unmanaged<IGCHeap*, int, int> WaitUntilConcurrentGCCompleteAsync;
        public delegate* unmanaged<IGCHeap*, nuint> GetNumberOfFinalizable;
        public delegate* unmanaged<IGCHeap*, Object*> GetNextFinalizable;
        public delegate* unmanaged<
            IGCHeap*,
            ulong*,
            ulong*,
            ulong*,
            ulong*,
            ulong*,
            ulong*,
            ulong*,
            ulong*,
            ulong*,
            ulong*,
            uint*,
            uint*,
            bool*,
            bool*,
            ulong*,
            ulong*,
            int,
            void> GetMemoryInfo;
        public delegate* unmanaged<IGCHeap*, uint> GetMemoryLoad;
        public delegate* unmanaged<IGCHeap*, int> GetGcLatencyMode;
        public delegate* unmanaged<IGCHeap*, int, int> SetGcLatencyMode;
        public delegate* unmanaged<IGCHeap*, int> GetLOHCompactionMode;
        public delegate* unmanaged<IGCHeap*, int, void> SetLOHCompactionMode;
        public delegate* unmanaged<IGCHeap*, uint, uint, bool> RegisterForFullGCNotification;
        public delegate* unmanaged<IGCHeap*, bool> CancelFullGCNotification;
        public delegate* unmanaged<IGCHeap*, int, int> WaitForFullGCApproach;
        public delegate* unmanaged<IGCHeap*, int, int> WaitForFullGCComplete;
        public delegate* unmanaged<IGCHeap*, Object*, uint> WhichGeneration;
        public delegate* unmanaged<IGCHeap*, int, int, int> CollectionCount;
        public delegate* unmanaged<IGCHeap*, ulong, bool, ulong, bool, int> StartNoGCRegion;
        public delegate* unmanaged<IGCHeap*, int> EndNoGCRegion;
        public delegate* unmanaged<IGCHeap*, nuint> GetTotalBytesInUse;
        public delegate* unmanaged<IGCHeap*, ulong> GetTotalAllocatedBytes;
        public delegate* unmanaged<IGCHeap*, int, bool, int, int> GarbageCollect;
        public delegate* unmanaged<IGCHeap*, uint> GetMaxGeneration;
        public delegate* unmanaged<IGCHeap*, Object*, void> SetFinalizationRun;
        public delegate* unmanaged<IGCHeap*, int, Object*, bool> RegisterForFinalization;
        public delegate* unmanaged<IGCHeap*, int> GetLastGCPercentTimeInGC;
        public delegate* unmanaged<IGCHeap*, int, nuint> GetLastGCGenerationSize;
        public delegate* unmanaged<IGCHeap*, int> Initialize;
        public delegate* unmanaged<IGCHeap*, Object*, bool> IsPromoted;
        public delegate* unmanaged<IGCHeap*, void*, bool, bool> IsHeapPointer;
        public delegate* unmanaged<IGCHeap*, uint> GetCondemnedGeneration;
        public delegate* unmanaged<IGCHeap*, bool, bool> IsGCInProgressHelper;
        public delegate* unmanaged<IGCHeap*, uint> GetGcCount;
        public delegate* unmanaged<IGCHeap*, gc_alloc_context*, int, bool> IsThreadUsingAllocationContextHeap;
        public delegate* unmanaged<IGCHeap*, Object*, bool> IsEphemeral;
        public delegate* unmanaged<IGCHeap*, bool, uint> WaitUntilGCComplete;
        public delegate* unmanaged<IGCHeap*, gc_alloc_context*, void*, void*, void> FixAllocContext;
        public delegate* unmanaged<IGCHeap*, nuint> GetCurrentObjSize;
        public delegate* unmanaged<IGCHeap*, bool, void> SetGCInProgress;
        public delegate* unmanaged<IGCHeap*, bool> RuntimeStructuresValid;
        public delegate* unmanaged<IGCHeap*, bool, void> SetSuspensionPending;
        public delegate* unmanaged<IGCHeap*, float, void> SetYieldProcessorScalingFactor;
        public delegate* unmanaged<IGCHeap*, void> Shutdown;
        public delegate* unmanaged<IGCHeap*, int, nuint> GetLastGCStartTime;
        public delegate* unmanaged<IGCHeap*, int, nuint> GetLastGCDuration;
        public delegate* unmanaged<IGCHeap*, nuint> GetNow;
        public delegate* unmanaged<IGCHeap*, gc_alloc_context*, nuint, uint, Object*> Alloc;
        public delegate* unmanaged<IGCHeap*, byte*, void> PublishObject;
        public delegate* unmanaged<IGCHeap*, void> SetWaitForGCEvent;
        public delegate* unmanaged<IGCHeap*, void> ResetWaitForGCEvent;
        public delegate* unmanaged<IGCHeap*, Object*, bool> IsLargeObject;
        public delegate* unmanaged<IGCHeap*, Object*, void> ValidateObjectMember;
        public delegate* unmanaged<IGCHeap*, Object*, Object*> NextObj;
        public delegate* unmanaged<IGCHeap*, void*, bool, Object*> GetContainingObject;
        public delegate* unmanaged<IGCHeap*, Object*, delegate* unmanaged<Object*, void*, bool>, void*, void> DiagWalkObject;
        public delegate* unmanaged<IGCHeap*, Object*, delegate* unmanaged<Object*, byte**, void*, bool>, void*, void> DiagWalkObject2;
        public delegate* unmanaged<IGCHeap*, delegate* unmanaged<Object*, void*, bool>, void*, int, bool, void> DiagWalkHeap;
        public delegate* unmanaged<IGCHeap*, void*, delegate* unmanaged<byte*, byte*, nint, void*, bool, bool, void>, void*, walk_surv_type, int, void> DiagWalkSurvivorsWithType;
        public delegate* unmanaged<IGCHeap*, void*, delegate* unmanaged<bool, void*, void>, void> DiagWalkFinalizeQueue;
        public delegate* unmanaged<IGCHeap*, delegate* unmanaged<Object**, ScanContext*, uint, void>, ScanContext*, void> DiagScanFinalizeQueue;
        public delegate* unmanaged<IGCHeap*, delegate* unmanaged<Object**, Object*, uint, ScanContext*, bool, void>, int, ScanContext*, void> DiagScanHandles;
        public delegate* unmanaged<IGCHeap*, delegate* unmanaged<Object**, Object*, uint, ScanContext*, bool, void>, int, ScanContext*, void> DiagScanDependentHandles;
        public delegate* unmanaged<IGCHeap*, delegate* unmanaged<void*, int, byte*, byte*, byte*, void>, void*, void> DiagDescrGenerations;
        public delegate* unmanaged<IGCHeap*, void> DiagTraceGCSegments;
        public delegate* unmanaged<IGCHeap*, EtwGCSettingsInfo*, void> DiagGetGCSettings;
        public delegate* unmanaged<IGCHeap*, gc_alloc_context*, bool> StressHeap;
        public delegate* unmanaged<IGCHeap*, segment_info*, gc_heap_segment_stub*> RegisterFrozenSegment;
        public delegate* unmanaged<IGCHeap*, gc_heap_segment_stub*, void> UnregisterFrozenSegment;
        public delegate* unmanaged<IGCHeap*, Object*, bool> IsInFrozenSegment;
        public delegate* unmanaged<IGCHeap*, GCEventKeyword, GCEventLevel, void> ControlEvents;
        public delegate* unmanaged<IGCHeap*, GCEventKeyword, GCEventLevel, void> ControlPrivateEvents;
        public delegate* unmanaged<IGCHeap*, Object*, byte**, byte**, byte**, uint> GetGenerationWithRange;
        public delegate* unmanaged<IGCHeap*, long> GetTotalPauseDuration;
        public delegate* unmanaged<IGCHeap*, void*, delegate* unmanaged<void*, byte*, byte*, GCConfigurationType, long, void>, void> EnumerateConfigurationValues;
        public delegate* unmanaged<IGCHeap*, gc_heap_segment_stub*, byte*, byte*, void> UpdateFrozenSegment;
        public delegate* unmanaged<IGCHeap*, int> RefreshMemoryLimit;
        public delegate* unmanaged<IGCHeap*, NoGCRegionCallbackFinalizerWorkItem*, ulong, enable_no_gc_region_callback_status> EnableNoGCRegionCallback;
        public delegate* unmanaged<IGCHeap*, FinalizerWorkItem*> GetExtraWorkForFinalization;
        public delegate* unmanaged<IGCHeap*, int, ulong> GetGenerationBudget;
        public delegate* unmanaged<IGCHeap*, nuint> GetLOHThreshold;
        public delegate* unmanaged<IGCHeap*, delegate* unmanaged<Object*, void*, bool>, void*, int, bool, void> DiagWalkHeapWithACHandling;
        public delegate* unmanaged<IGCHeap*, nuint, void*, void> NullBridgeObjectsWeakRefs;
    }

    internal unsafe struct IGCHandleManagerVtable
    {
        public delegate* unmanaged<IGCHandleManager*, bool> Initialize;
        public delegate* unmanaged<IGCHandleManager*, void> Shutdown;
        public delegate* unmanaged<IGCHandleManager*, IGCHandleStore*> GetGlobalHandleStore;
        public delegate* unmanaged<IGCHandleManager*, IGCHandleStore*> CreateHandleStore;
        public delegate* unmanaged<IGCHandleManager*, IGCHandleStore*, void> DestroyHandleStore;
        public delegate* unmanaged<IGCHandleManager*, Object*, HandleType, OBJECTHANDLE__*> CreateGlobalHandleOfType;
        public delegate* unmanaged<IGCHandleManager*, OBJECTHANDLE__*, OBJECTHANDLE__*> CreateDuplicateHandle;
        public delegate* unmanaged<IGCHandleManager*, OBJECTHANDLE__*, HandleType, void> DestroyHandleOfType;
        public delegate* unmanaged<IGCHandleManager*, OBJECTHANDLE__*, void> DestroyHandleOfUnknownType;
        public delegate* unmanaged<IGCHandleManager*, OBJECTHANDLE__*, HandleType, void*, void> SetExtraInfoForHandle;
        public delegate* unmanaged<IGCHandleManager*, OBJECTHANDLE__*, void*> GetExtraInfoFromHandle;
        public delegate* unmanaged<IGCHandleManager*, OBJECTHANDLE__*, Object*, void> StoreObjectInHandle;
        public delegate* unmanaged<IGCHandleManager*, OBJECTHANDLE__*, Object*, bool> StoreObjectInHandleIfNull;
        public delegate* unmanaged<IGCHandleManager*, OBJECTHANDLE__*, Object*, void> SetDependentHandleSecondary;
        public delegate* unmanaged<IGCHandleManager*, OBJECTHANDLE__*, Object*> GetDependentHandleSecondary;
        public delegate* unmanaged<IGCHandleManager*, OBJECTHANDLE__*, Object*, Object*, Object*> InterlockedCompareExchangeObjectInHandle;
        public delegate* unmanaged<IGCHandleManager*, OBJECTHANDLE__*, HandleType> HandleFetchType;
        public delegate* unmanaged<IGCHandleManager*, delegate* unmanaged<Object**, nuint*, nuint, nuint, void>, nuint, nuint, void> TraceRefCountedHandles;
    }

    internal unsafe struct GCExports
    {
        public delegate* unmanaged<VersionInfo*, void> GC_VersionInfo;
        public delegate* unmanaged<IGCToCLR*, IGCHeap**, IGCHandleManager**, GcDacVars*, int> GC_Initialize;
    }
}
