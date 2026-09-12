// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

#pragma warning disable CA1822, CA1823, CS0169

namespace Internal.Runtime.GC
{
    internal enum SUSPEND_REASON : int
    {
        SUSPEND_FOR_GC = 1,
        SUSPEND_FOR_GC_PREP = 6,
    }

    internal enum WriteBarrierOp : int
    {
        StompResize,
        StompEphemeral,
        Initialize,
        SwitchToWriteWatch,
        SwitchToNonWriteWatch,
    }

    internal unsafe struct WriteBarrierParameters
    {
        public WriteBarrierOp operation;
        public bool is_runtime_suspended;
        public bool requires_upper_bounds_check;
        public uint* card_table;
        public uint* card_bundle_table;
        public byte* lowest_address;
        public byte* highest_address;
        public byte* ephemeral_low;
        public byte* ephemeral_high;
        public byte* write_watch_table;
        public byte* region_to_generation_table;
        public byte region_shr;
        public bool region_use_bitwise_write_barrier;
    }

    internal unsafe struct StronglyConnectedComponent
    {
        public nuint Count;
        public nuint* Contexts;
    }

    internal unsafe struct ComponentCrossReference
    {
        public nuint SourceGroupIndex;
        public nuint DestinationGroupIndex;
    }

    internal unsafe struct MarkCrossReferencesArgs
    {
        public nuint ComponentCount;
        public StronglyConnectedComponent* Components;
        public nuint CrossReferenceCount;
        public ComponentCrossReference* CrossReferences;
    }

    internal unsafe struct MethodTable
    {
        public const uint MTFlag_RequiresAlign8 = 0x00001000;
        public const uint MTFlag_Category_ValueType = 0x00040000;
        public const uint MTFlag_Category_ValueType_Mask = 0x000C0000;
        public const uint MTFlag_ContainsGCPointers = 0x01000000;
        public const uint MTFlag_HasCriticalFinalizer = 0x00000002;
        public const uint MTFlag_HasFinalizer = 0x00100000;
        public const uint MTFlag_IsArray = 0x00080000;
        public const uint MTFlag_Collectible = 0x00200000;
        public const uint MTFlag_HasComponentSize = 0x80000000;

        [StructLayout(LayoutKind.Explicit)]
        public struct FlagsOrComponentSize
        {
            [FieldOffset(0)]
            public ushort m_componentSize;

            [FieldOffset(0)]
            public uint m_flags;
        }

        public FlagsOrComponentSize m_flagsOrComponentSize;
        public uint m_baseSize;
        public MethodTable* m_pRelatedType;

        public void InitializeFreeObject()
        {
            m_baseSize = 3 * (uint)sizeof(void*);
            m_flagsOrComponentSize.m_flags = MTFlag_HasComponentSize | MTFlag_IsArray;
            m_flagsOrComponentSize.m_componentSize = 1;
        }

        public uint GetBaseSize()
        {
            return m_baseSize;
        }

        public ushort RawGetComponentSize()
        {
            return m_flagsOrComponentSize.m_componentSize;
        }

        public bool ContainsGCPointers()
        {
            return (m_flagsOrComponentSize.m_flags & MTFlag_ContainsGCPointers) != 0;
        }

        public bool ContainsGCPointersOrCollectible()
        {
            return ContainsGCPointers() || Collectible();
        }

        public bool Collectible()
        {
            return (m_flagsOrComponentSize.m_flags & MTFlag_Collectible) != 0;
        }

        public bool RequiresAlign8()
        {
            return (m_flagsOrComponentSize.m_flags & MTFlag_RequiresAlign8) != 0;
        }

        public bool IsValueType()
        {
            return (m_flagsOrComponentSize.m_flags & MTFlag_Category_ValueType_Mask) == MTFlag_Category_ValueType;
        }

        public bool HasComponentSize()
        {
            return (m_flagsOrComponentSize.m_flags & MTFlag_HasComponentSize) != 0;
        }

        public uint GetNumComponents(Object* obj)
        {
            return ((ArrayBase*)obj)->m_dwLength;
        }

        public bool HasFinalizer()
        {
            return (m_flagsOrComponentSize.m_flags & MTFlag_HasFinalizer) != 0;
        }

        public bool HasCriticalFinalizer()
        {
            return !HasComponentSize() && (m_flagsOrComponentSize.m_flags & MTFlag_HasCriticalFinalizer) != 0;
        }

        public bool SanityCheck()
        {
            return true;
        }
    }

    internal unsafe struct StressLogMsg
    {
        private byte _opaque;
    }

    internal unsafe struct IGCToCLR
    {
        public IGCToCLRVtable* Vtable;
    }

    internal unsafe struct IGCToCLRVtable
    {
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, SUSPEND_REASON, void> SuspendEE;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, bool, void> RestartEE;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, delegate* unmanaged[SuppressGCTransition]<Object**, ScanContext*, uint, void>, int, int, ScanContext*, void> GcScanRoots;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, int, int, void> GcStartWork;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, int, bool, bool, void> BeforeGcScanRoots;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, int, int, ScanContext*, void> AfterGcScanRoots;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, int, void> GcDone;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, Object*, bool> RefCountedHandleCallbacks;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, delegate* unmanaged[SuppressGCTransition]<Object**, nuint*, nuint, nuint, void>, nuint, nuint, void> SyncBlockCacheWeakPtrScan;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, int, void> SyncBlockCacheDemote;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, int, void> SyncBlockCachePromotionsGranted;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, uint> GetActiveSyncBlockCount;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, bool> IsPreemptiveGCDisabled;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, bool> EnablePreemptiveGC;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, void> DisablePreemptiveGC;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, Thread*> GetThread;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, gc_alloc_context*> GetAllocContext;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, delegate* unmanaged[SuppressGCTransition]<gc_alloc_context*, void*, void>, void*, void> GcEnumAllocContexts;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, Object*, byte*> GetLoaderAllocatorObjectForGC;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, delegate* unmanaged[SuppressGCTransition]<void*, void>, void*, bool, byte*, bool> CreateThread;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, int, bool, void> DiagGCStart;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, void> DiagUpdateGenerationBounds;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, nuint, int, int, bool, void> DiagGCEnd;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, void*, void> DiagWalkFReachableObjects;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, void*, bool, void> DiagWalkSurvivors;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, void*, int, void> DiagWalkUOHSurvivors;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, void*, void> DiagWalkBGCSurvivors;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, WriteBarrierParameters*, void> StompWriteBarrier;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, bool, void> EnableFinalization;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, uint, void> HandleFatalError;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, Object*, bool> EagerFinalized;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, MethodTable*> GetFreeObjectMethodTable;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, byte*, byte*, bool*, bool> GetBooleanConfigValue;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, byte*, byte*, long*, bool> GetIntConfigValue;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, byte*, byte*, byte**, bool> GetStringConfigValue;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, byte*, void> FreeStringConfigValue;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, bool> IsGCThread;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, bool> WasCurrentThreadCreatedByGC;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, Object*, ScanContext*, delegate* unmanaged[SuppressGCTransition]<Object**, ScanContext*, uint, void>, void> WalkAsyncPinnedForPromotion;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, Object*, void*, delegate* unmanaged[SuppressGCTransition]<Object*, Object*, void*, void>, void> WalkAsyncPinned;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, IGCToCLREventSink*> EventSink;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, uint> GetTotalNumSizedRefHandles;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, int, bool> AnalyzeSurvivorsRequested;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, nuint, int, ulong, delegate* unmanaged[SuppressGCTransition]<void>, void> AnalyzeSurvivorsFinished;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, void> VerifySyncTableEntry;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, int, int, int, int, void> UpdateGCEventStatus;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, uint, uint, StressLogMsg*, void> LogStressMsg;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, uint> GetCurrentProcessCpuCount;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, int, byte*, byte*, byte*, void> DiagAddNewRegion;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, byte*, void> LogErrorToHost;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, Thread*, ulong> GetThreadOSThreadId;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, MarkCrossReferencesArgs*, void> TriggerClientBridgeProcessing;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLR*, bool> IsClientBridgeProcessingActive;
    }
}
