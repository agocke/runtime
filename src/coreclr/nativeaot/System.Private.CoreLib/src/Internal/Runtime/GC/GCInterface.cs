// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable CA1823, CS0169

using System.Runtime.CompilerServices;

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
        public const nuint GC_MARKED = 1;

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

        public void SetMarked()
        {
            m_pMethTab = (MethodTable*)((nuint)m_pMethTab | GC_MARKED);
        }

        public bool IsMarked()
        {
            return ((nuint)m_pMethTab & GC_MARKED) != 0;
        }

        public void ClearMarked()
        {
            m_pMethTab = (MethodTable*)((nuint)m_pMethTab & ~GC_MARKED);
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

    // These implementation slots intentionally use raw managed function pointers. Linux x64 uses the platform ABI for these blittable signatures; nested callbacks remain unmanaged.
    internal unsafe struct IGCHeapVtable
    {
        public delegate*<IGCHeap*, nuint, bool> IsValidSegmentSize;
        public delegate*<IGCHeap*, nuint, bool> IsValidGen0MaxSize;
        public delegate*<IGCHeap*, bool, nuint> GetValidSegmentSize;
        public delegate*<IGCHeap*, nuint, void> SetReservedVMLimit;
        public delegate*<IGCHeap*, void> WaitUntilConcurrentGCComplete;
        public delegate*<IGCHeap*, bool> IsConcurrentGCInProgress;
        public delegate*<IGCHeap*, void> TemporaryEnableConcurrentGC;
        public delegate*<IGCHeap*, void> TemporaryDisableConcurrentGC;
        public delegate*<IGCHeap*, bool> IsConcurrentGCEnabled;
        public delegate*<IGCHeap*, int, int> WaitUntilConcurrentGCCompleteAsync;
        public delegate*<IGCHeap*, nuint> GetNumberOfFinalizable;
        public delegate*<IGCHeap*, Object*> GetNextFinalizable;
        public delegate*<
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
        public delegate*<IGCHeap*, uint> GetMemoryLoad;
        public delegate*<IGCHeap*, int> GetGcLatencyMode;
        public delegate*<IGCHeap*, int, int> SetGcLatencyMode;
        public delegate*<IGCHeap*, int> GetLOHCompactionMode;
        public delegate*<IGCHeap*, int, void> SetLOHCompactionMode;
        public delegate*<IGCHeap*, uint, uint, bool> RegisterForFullGCNotification;
        public delegate*<IGCHeap*, bool> CancelFullGCNotification;
        public delegate*<IGCHeap*, int, int> WaitForFullGCApproach;
        public delegate*<IGCHeap*, int, int> WaitForFullGCComplete;
        public delegate*<IGCHeap*, Object*, uint> WhichGeneration;
        public delegate*<IGCHeap*, int, int, int> CollectionCount;
        public delegate*<IGCHeap*, ulong, bool, ulong, bool, int> StartNoGCRegion;
        public delegate*<IGCHeap*, int> EndNoGCRegion;
        public delegate*<IGCHeap*, nuint> GetTotalBytesInUse;
        public delegate*<IGCHeap*, ulong> GetTotalAllocatedBytes;
        public delegate*<IGCHeap*, int, bool, int, int> GarbageCollect;
        public delegate*<IGCHeap*, uint> GetMaxGeneration;
        public delegate*<IGCHeap*, Object*, void> SetFinalizationRun;
        public delegate*<IGCHeap*, int, Object*, bool> RegisterForFinalization;
        public delegate*<IGCHeap*, int> GetLastGCPercentTimeInGC;
        public delegate*<IGCHeap*, int, nuint> GetLastGCGenerationSize;
        public delegate*<IGCHeap*, int> Initialize;
        public delegate*<IGCHeap*, Object*, bool> IsPromoted;
        public delegate*<IGCHeap*, void*, bool, bool> IsHeapPointer;
        public delegate*<IGCHeap*, uint> GetCondemnedGeneration;
        public delegate*<IGCHeap*, bool, bool> IsGCInProgressHelper;
        public delegate*<IGCHeap*, uint> GetGcCount;
        public delegate*<IGCHeap*, gc_alloc_context*, int, bool> IsThreadUsingAllocationContextHeap;
        public delegate*<IGCHeap*, Object*, bool> IsEphemeral;
        public delegate*<IGCHeap*, bool, uint> WaitUntilGCComplete;
        public delegate*<IGCHeap*, gc_alloc_context*, void*, void*, void> FixAllocContext;
        public delegate*<IGCHeap*, nuint> GetCurrentObjSize;
        public delegate*<IGCHeap*, bool, void> SetGCInProgress;
        public delegate*<IGCHeap*, bool> RuntimeStructuresValid;
        public delegate*<IGCHeap*, bool, void> SetSuspensionPending;
        public delegate*<IGCHeap*, float, void> SetYieldProcessorScalingFactor;
        public delegate*<IGCHeap*, void> Shutdown;
        public delegate*<IGCHeap*, int, nuint> GetLastGCStartTime;
        public delegate*<IGCHeap*, int, nuint> GetLastGCDuration;
        public delegate*<IGCHeap*, nuint> GetNow;
        public delegate*<IGCHeap*, gc_alloc_context*, nuint, uint, Object*> Alloc;
        public delegate*<IGCHeap*, byte*, void> PublishObject;
        public delegate*<IGCHeap*, void> SetWaitForGCEvent;
        public delegate*<IGCHeap*, void> ResetWaitForGCEvent;
        public delegate*<IGCHeap*, Object*, bool> IsLargeObject;
        public delegate*<IGCHeap*, Object*, void> ValidateObjectMember;
        public delegate*<IGCHeap*, Object*, Object*> NextObj;
        public delegate*<IGCHeap*, void*, bool, Object*> GetContainingObject;
        public delegate*<IGCHeap*, Object*, delegate* unmanaged[SuppressGCTransition]<Object*, void*, bool>, void*, void> DiagWalkObject;
        public delegate*<IGCHeap*, Object*, delegate* unmanaged[SuppressGCTransition]<Object*, byte**, void*, bool>, void*, void> DiagWalkObject2;
        public delegate*<IGCHeap*, delegate* unmanaged[SuppressGCTransition]<Object*, void*, bool>, void*, int, bool, void> DiagWalkHeap;
        public delegate*<IGCHeap*, void*, delegate* unmanaged[SuppressGCTransition]<byte*, byte*, nint, void*, bool, bool, void>, void*, walk_surv_type, int, void> DiagWalkSurvivorsWithType;
        public delegate*<IGCHeap*, void*, delegate* unmanaged[SuppressGCTransition]<bool, void*, void>, void> DiagWalkFinalizeQueue;
        public delegate*<IGCHeap*, delegate* unmanaged[SuppressGCTransition]<Object**, ScanContext*, uint, void>, ScanContext*, void> DiagScanFinalizeQueue;
        public delegate*<IGCHeap*, delegate* unmanaged[SuppressGCTransition]<Object**, Object*, uint, ScanContext*, bool, void>, int, ScanContext*, void> DiagScanHandles;
        public delegate*<IGCHeap*, delegate* unmanaged[SuppressGCTransition]<Object**, Object*, uint, ScanContext*, bool, void>, int, ScanContext*, void> DiagScanDependentHandles;
        public delegate*<IGCHeap*, delegate* unmanaged[SuppressGCTransition]<void*, int, byte*, byte*, byte*, void>, void*, void> DiagDescrGenerations;
        public delegate*<IGCHeap*, void> DiagTraceGCSegments;
        public delegate*<IGCHeap*, EtwGCSettingsInfo*, void> DiagGetGCSettings;
        public delegate*<IGCHeap*, gc_alloc_context*, bool> StressHeap;
        public delegate*<IGCHeap*, segment_info*, gc_heap_segment_stub*> RegisterFrozenSegment;
        public delegate*<IGCHeap*, gc_heap_segment_stub*, void> UnregisterFrozenSegment;
        public delegate*<IGCHeap*, Object*, bool> IsInFrozenSegment;
        public delegate*<IGCHeap*, GCEventKeyword, GCEventLevel, void> ControlEvents;
        public delegate*<IGCHeap*, GCEventKeyword, GCEventLevel, void> ControlPrivateEvents;
        public delegate*<IGCHeap*, Object*, byte**, byte**, byte**, uint> GetGenerationWithRange;
        public delegate*<IGCHeap*, long> GetTotalPauseDuration;
        public delegate*<IGCHeap*, void*, delegate* unmanaged[SuppressGCTransition]<void*, byte*, byte*, GCConfigurationType, long, void>, void> EnumerateConfigurationValues;
        public delegate*<IGCHeap*, gc_heap_segment_stub*, byte*, byte*, void> UpdateFrozenSegment;
        public delegate*<IGCHeap*, int> RefreshMemoryLimit;
        public delegate*<IGCHeap*, NoGCRegionCallbackFinalizerWorkItem*, ulong, enable_no_gc_region_callback_status> EnableNoGCRegionCallback;
        public delegate*<IGCHeap*, FinalizerWorkItem*> GetExtraWorkForFinalization;
        public delegate*<IGCHeap*, int, ulong> GetGenerationBudget;
        public delegate*<IGCHeap*, nuint> GetLOHThreshold;
        public delegate*<IGCHeap*, delegate* unmanaged[SuppressGCTransition]<Object*, void*, bool>, void*, int, bool, void> DiagWalkHeapWithACHandling;
        public delegate*<IGCHeap*, nuint, void*, void> NullBridgeObjectsWeakRefs;
    }

    internal unsafe struct IGCHandleManagerVtable
    {
        public delegate*<IGCHandleManager*, bool> Initialize;
        public delegate*<IGCHandleManager*, void> Shutdown;
        public delegate*<IGCHandleManager*, IGCHandleStore*> GetGlobalHandleStore;
        public delegate*<IGCHandleManager*, IGCHandleStore*> CreateHandleStore;
        public delegate*<IGCHandleManager*, IGCHandleStore*, void> DestroyHandleStore;
        public delegate*<IGCHandleManager*, Object*, HandleType, OBJECTHANDLE__*> CreateGlobalHandleOfType;
        public delegate*<IGCHandleManager*, OBJECTHANDLE__*, OBJECTHANDLE__*> CreateDuplicateHandle;
        public delegate*<IGCHandleManager*, OBJECTHANDLE__*, HandleType, void> DestroyHandleOfType;
        public delegate*<IGCHandleManager*, OBJECTHANDLE__*, void> DestroyHandleOfUnknownType;
        public delegate*<IGCHandleManager*, OBJECTHANDLE__*, HandleType, void*, void> SetExtraInfoForHandle;
        public delegate*<IGCHandleManager*, OBJECTHANDLE__*, void*> GetExtraInfoFromHandle;
        public delegate*<IGCHandleManager*, OBJECTHANDLE__*, Object*, void> StoreObjectInHandle;
        public delegate*<IGCHandleManager*, OBJECTHANDLE__*, Object*, bool> StoreObjectInHandleIfNull;
        public delegate*<IGCHandleManager*, OBJECTHANDLE__*, Object*, void> SetDependentHandleSecondary;
        public delegate*<IGCHandleManager*, OBJECTHANDLE__*, Object*> GetDependentHandleSecondary;
        public delegate*<IGCHandleManager*, OBJECTHANDLE__*, Object*, Object*, Object*> InterlockedCompareExchangeObjectInHandle;
        public delegate*<IGCHandleManager*, OBJECTHANDLE__*, HandleType> HandleFetchType;
        public delegate*<IGCHandleManager*, delegate* unmanaged[SuppressGCTransition]<Object**, nuint*, nuint, nuint, void>, nuint, nuint, void> TraceRefCountedHandles;
    }

    internal unsafe struct GCExports
    {
        public delegate* unmanaged<VersionInfo*, void> GC_VersionInfo;
        public delegate* unmanaged<IGCToCLR*, IGCHeap**, IGCHandleManager**, GcDacVars*, int> GC_Initialize;
    }
}
