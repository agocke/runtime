// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Internal.Runtime.GC
{
    internal unsafe struct IGCHandleStore
    {
        public IGCHandleStoreVtable* Vtable;
    }

    internal unsafe struct IGCHandleStoreVtable
    {
        public delegate* unmanaged<IGCHandleStore*, void> Uproot;
        public delegate* unmanaged<IGCHandleStore*, OBJECTHANDLE__*, bool> ContainsHandle;
        public delegate* unmanaged<IGCHandleStore*, Object*, HandleType, OBJECTHANDLE__*> CreateHandleOfType;
        public delegate* unmanaged<IGCHandleStore*, Object*, HandleType, int, OBJECTHANDLE__*> CreateHandleOfTypeWithHeapAffinity;
        public delegate* unmanaged<IGCHandleStore*, Object*, HandleType, void*, OBJECTHANDLE__*> CreateHandleWithExtraInfo;
        public delegate* unmanaged<IGCHandleStore*, Object*, Object*, OBJECTHANDLE__*> CreateDependentHandle;
        public delegate* unmanaged<IGCHandleStore*, void> CompleteObjectDestructor;
        public delegate* unmanaged<IGCHandleStore*, void> DeletingDestructor;
    }

    internal unsafe struct IGCToCLREventSink
    {
        public IGCToCLREventSinkVtable* Vtable;
    }

    internal unsafe struct IGCToCLREventSinkVtable
    {
        public delegate* unmanaged<IGCToCLREventSink*, byte*, void*, uint, void> FireDynamicEvent;
        public delegate* unmanaged<IGCToCLREventSink*, uint, uint, uint, uint, void> FireGCStart_V2;
        public delegate* unmanaged<IGCToCLREventSink*, uint, uint, void> FireGCEnd_V1;
        public delegate* unmanaged<IGCToCLREventSink*, byte, void*, ulong, ulong, void> FireGCGenerationRange;
        public delegate* unmanaged<
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
        public delegate* unmanaged<IGCToCLREventSink*, void*, nuint, uint, void> FireGCCreateSegment_V1;
        public delegate* unmanaged<IGCToCLREventSink*, void*, void> FireGCFreeSegment_V1;
        public delegate* unmanaged<IGCToCLREventSink*, void> FireGCCreateConcurrentThread_V1;
        public delegate* unmanaged<IGCToCLREventSink*, void> FireGCTerminateConcurrentThread_V1;
        public delegate* unmanaged<IGCToCLREventSink*, uint, void> FireGCTriggered;
        public delegate* unmanaged<IGCToCLREventSink*, uint, uint, ulong, void> FireGCMarkWithType;
        public delegate* unmanaged<IGCToCLREventSink*, uint, uint, uint, uint, void> FireGCJoin_V2;
        public delegate* unmanaged<
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
        public delegate* unmanaged<IGCToCLREventSink*, uint, uint, void> FireGCAllocationTick_V1;
        public delegate* unmanaged<IGCToCLREventSink*, ulong, uint, uint, void*, ulong, void> FireGCAllocationTick_V4;
        public delegate* unmanaged<IGCToCLREventSink*, void*, byte**, void> FirePinObjectAtGCTime;
        public delegate* unmanaged<IGCToCLREventSink*, byte*, byte*, byte*, void> FirePinPlugAtGCTime;
        public delegate* unmanaged<
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
        public delegate* unmanaged<IGCToCLREventSink*, ushort, uint, void*, void> FireGCLOHCompact;
        public delegate* unmanaged<IGCToCLREventSink*, ushort, nuint, ushort, uint, void*, void> FireGCFitBucketInfo;
        public delegate* unmanaged<IGCToCLREventSink*, void> FireBGCBegin;
        public delegate* unmanaged<IGCToCLREventSink*, void> FireBGC1stNonConEnd;
        public delegate* unmanaged<IGCToCLREventSink*, void> FireBGC1stConEnd;
        public delegate* unmanaged<IGCToCLREventSink*, uint, void> FireBGC1stSweepEnd;
        public delegate* unmanaged<IGCToCLREventSink*, void> FireBGC2ndNonConBegin;
        public delegate* unmanaged<IGCToCLREventSink*, void> FireBGC2ndNonConEnd;
        public delegate* unmanaged<IGCToCLREventSink*, void> FireBGC2ndConBegin;
        public delegate* unmanaged<IGCToCLREventSink*, void> FireBGC2ndConEnd;
        public delegate* unmanaged<IGCToCLREventSink*, ulong, void> FireBGCDrainMark;
        public delegate* unmanaged<IGCToCLREventSink*, ulong, ulong, uint, void> FireBGCRevisit;
        public delegate* unmanaged<IGCToCLREventSink*, ulong, ulong, ulong, uint, uint, void> FireBGCOverflow_V1;
        public delegate* unmanaged<IGCToCLREventSink*, uint, void> FireBGCAllocWaitBegin;
        public delegate* unmanaged<IGCToCLREventSink*, uint, void> FireBGCAllocWaitEnd;
        public delegate* unmanaged<IGCToCLREventSink*, uint, uint, void> FireGCFullNotify_V1;
        public delegate* unmanaged<IGCToCLREventSink*, void*, void*, uint, uint, void> FireSetGCHandle;
        public delegate* unmanaged<IGCToCLREventSink*, void*, void*, uint, uint, void> FirePrvSetGCHandle;
        public delegate* unmanaged<IGCToCLREventSink*, void*, void> FireDestroyGCHandle;
        public delegate* unmanaged<IGCToCLREventSink*, void*, void> FirePrvDestroyGCHandle;
    }
}
