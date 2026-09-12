// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace Internal.Runtime.GC
{
    internal unsafe struct IGCHandleStore
    {
        public IGCHandleStoreVtable* Vtable;
    }

    // The store implementation uses the same raw managed pointer convention as IGCHeap and IGCHandleManager.
    internal unsafe struct IGCHandleStoreVtable
    {
        public delegate*<IGCHandleStore*, void> Uproot;
        public delegate*<IGCHandleStore*, OBJECTHANDLE__*, bool> ContainsHandle;
        public delegate*<IGCHandleStore*, Object*, HandleType, OBJECTHANDLE__*> CreateHandleOfType;
        public delegate*<IGCHandleStore*, Object*, HandleType, int, OBJECTHANDLE__*> CreateHandleOfTypeWithHeapAffinity;
        public delegate*<IGCHandleStore*, Object*, HandleType, void*, OBJECTHANDLE__*> CreateHandleWithExtraInfo;
        public delegate*<IGCHandleStore*, Object*, Object*, OBJECTHANDLE__*> CreateDependentHandle;
        public delegate*<IGCHandleStore*, void> CompleteObjectDestructor;
        public delegate*<IGCHandleStore*, void> DeletingDestructor;
    }

    internal unsafe struct IGCToCLREventSink
    {
        public IGCToCLREventSinkVtable* Vtable;
    }

    internal unsafe struct IGCToCLREventSinkVtable
    {
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, byte*, void*, uint, void> FireDynamicEvent;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, uint, uint, uint, uint, void> FireGCStart_V2;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, uint, uint, void> FireGCEnd_V1;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, byte, void*, ulong, ulong, void> FireGCGenerationRange;
        public delegate* unmanaged[SuppressGCTransition]<
            IGCToCLREventSink*,
            ulong,
            ulong,
            ulong,
            ulong,
            ulong,
            ulong,
            ulong,
            ulong,
            ulong,
            ulong,
            ulong,
            ulong,
            uint,
            uint,
            uint,
            void> FireGCHeapStats_V2;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, void*, nuint, uint, void> FireGCCreateSegment_V1;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, void*, void> FireGCFreeSegment_V1;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, void> FireGCCreateConcurrentThread_V1;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, void> FireGCTerminateConcurrentThread_V1;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, uint, void> FireGCTriggered;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, uint, uint, ulong, void> FireGCMarkWithType;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, uint, uint, uint, uint, void> FireGCJoin_V2;
        public delegate* unmanaged[SuppressGCTransition]<
            IGCToCLREventSink*,
            ulong,
            int,
            uint,
            uint,
            uint,
            uint,
            uint,
            uint,
            uint,
            uint,
            uint,
            uint,
            void*,
            void> FireGCGlobalHeapHistory_V4;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, uint, uint, void> FireGCAllocationTick_V1;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, ulong, uint, uint, void*, ulong, void> FireGCAllocationTick_V4;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, void*, byte**, void> FirePinObjectAtGCTime;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, byte*, byte*, byte*, void> FirePinPlugAtGCTime;
        public delegate* unmanaged[SuppressGCTransition]<
            IGCToCLREventSink*,
            void*,
            void*,
            void*,
            void*,
            void*,
            void*,
            uint,
            uint,
            uint,
            uint,
            uint,
            uint,
            void*,
            uint,
            uint,
            void*,
            void> FireGCPerHeapHistory_V3;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, ushort, uint, void*, void> FireGCLOHCompact;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, ushort, nuint, ushort, uint, void*, void> FireGCFitBucketInfo;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, void> FireBGCBegin;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, void> FireBGC1stNonConEnd;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, void> FireBGC1stConEnd;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, uint, void> FireBGC1stSweepEnd;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, void> FireBGC2ndNonConBegin;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, void> FireBGC2ndNonConEnd;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, void> FireBGC2ndConBegin;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, void> FireBGCDrainMark;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, ulong, ulong, uint, void> FireBGCRevisit;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, ulong, ulong, ulong, uint, uint, void> FireBGCOverflow_V1;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, uint, void> FireBGCAllocWaitBegin;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, uint, void> FireBGCAllocWaitEnd;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, uint, uint, void> FireGCFullNotify_V1;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, void*, void*, uint, uint, void> FireSetGCHandle;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, void*, void*, uint, uint, void> FirePrvSetGCHandle;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, void*, void> FireDestroyGCHandle;
        public delegate* unmanaged[SuppressGCTransition]<IGCToCLREventSink*, void*, void> FirePrvDestroyGCHandle;
    }
}
