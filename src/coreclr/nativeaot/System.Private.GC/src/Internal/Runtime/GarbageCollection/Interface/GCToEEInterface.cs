// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of gcenv.ee.standalone.inl: every call the GC makes into the EE is forwarded to the
// singular IGCToCLR instance the EE handed to the GC at load time. The C# port sees that
// instance as a pointer whose first field is the vtable, so a call is a load of the slot from
// GCInterfaceVtables.IGCToCLRVtable followed by an indirect call with the instance as the
// first argument.

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe class GCToEEInterface
    {
        // The singular interface instance. All calls here are forwarded to it. Set once by
        // GCHeapUtilities/gcload before any other GC code runs; never null afterwards.
        private static void* g_theGCToCLR;

        private static IGCToCLRVtable* Vtable => *(IGCToCLRVtable**)g_theGCToCLR;

        /// <summary>
        /// Records the IGCToCLR instance the EE passed to the GC. Must be called before any other
        /// method on this class.
        /// </summary>
        public static void Initialize(void* theGCToCLR) => g_theGCToCLR = theGCToCLR;

        public static void SuspendEE(SUSPEND_REASON reason) => Vtable->SuspendEE(g_theGCToCLR, reason);

        public static void RestartEE(byte bFinishedGC) => Vtable->RestartEE(g_theGCToCLR, bFinishedGC);

        public static void GcScanRoots(delegate*<byte**, ScanContext*, uint, void> fn, int condemned, int max_gen, ScanContext* sc) =>
            Vtable->GcScanRoots(g_theGCToCLR, (delegate* unmanaged<byte**, ScanContext*, uint, void>)fn, condemned, max_gen, sc);

        public static void GcStartWork(int condemned, int max_gen) => Vtable->GcStartWork(g_theGCToCLR, condemned, max_gen);

        public static void BeforeGcScanRoots(int condemned, byte is_bgc, byte is_concurrent) => Vtable->BeforeGcScanRoots(g_theGCToCLR, condemned, is_bgc, is_concurrent);

        public static void AfterGcScanRoots(int condemned, int max_gen, ScanContext* sc) => Vtable->AfterGcScanRoots(g_theGCToCLR, condemned, max_gen, sc);

        public static void GcDone(int condemned) => Vtable->GcDone(g_theGCToCLR, condemned);

        public static byte RefCountedHandleCallbacks(byte* pObject) => Vtable->RefCountedHandleCallbacks(g_theGCToCLR, pObject);

        public static void SyncBlockCacheWeakPtrScan(delegate* unmanaged<byte**, nuint*, nuint, nuint, void> scanProc, nuint lp1, nuint lp2) => Vtable->SyncBlockCacheWeakPtrScan(g_theGCToCLR, scanProc, lp1, lp2);

        public static void SyncBlockCacheDemote(int max_gen) => Vtable->SyncBlockCacheDemote(g_theGCToCLR, max_gen);

        public static void SyncBlockCachePromotionsGranted(int max_gen) => Vtable->SyncBlockCachePromotionsGranted(g_theGCToCLR, max_gen);

        public static uint GetActiveSyncBlockCount() => Vtable->GetActiveSyncBlockCount(g_theGCToCLR);

        public static byte IsPreemptiveGCDisabled() => Vtable->IsPreemptiveGCDisabled(g_theGCToCLR);

        public static byte EnablePreemptiveGC() => Vtable->EnablePreemptiveGC(g_theGCToCLR);

        public static void DisablePreemptiveGC() => Vtable->DisablePreemptiveGC(g_theGCToCLR);

        public static void* GetThread() => Vtable->GetThread(g_theGCToCLR);

        public static gc_alloc_context* GetAllocContext() => Vtable->GetAllocContext(g_theGCToCLR);

        // The callback is a direct managed entry point. Using an UnmanagedCallersOnly thunk
        // would introduce a nested reverse-P/Invoke when the persistent BGC worker enumerates
        // contexts from its managed cycle callback.
        public static void GcEnumAllocContexts(delegate*<gc_alloc_context*, void*, void> fn, void* param) =>
            Vtable->GcEnumAllocContexts(
                g_theGCToCLR,
                (delegate* unmanaged<gc_alloc_context*, void*, void>)fn,
                param);

        public static byte* GetLoaderAllocatorObjectForGC(byte* pObject) => Vtable->GetLoaderAllocatorObjectForGC(g_theGCToCLR, pObject);

        public static byte CreateThread(delegate* unmanaged<void*, void> threadStart, void* arg, byte is_suspendable, byte* @name) => Vtable->CreateThread(g_theGCToCLR, threadStart, arg, is_suspendable, @name);

        public static void DiagGCStart(int gen, byte isInduced) => Vtable->DiagGCStart(g_theGCToCLR, gen, isInduced);

        public static void DiagUpdateGenerationBounds() => Vtable->DiagUpdateGenerationBounds(g_theGCToCLR);

        public static void DiagGCEnd(nuint index, int gen, int reason, byte fConcurrent) => Vtable->DiagGCEnd(g_theGCToCLR, index, gen, reason, fConcurrent);

        public static void DiagWalkFReachableObjects(void* gcContext) => Vtable->DiagWalkFReachableObjects(g_theGCToCLR, gcContext);

        public static void DiagWalkSurvivors(void* gcContext, byte fCompacting) => Vtable->DiagWalkSurvivors(g_theGCToCLR, gcContext, fCompacting);

        public static void DiagWalkUOHSurvivors(void* gcContext, int gen) => Vtable->DiagWalkUOHSurvivors(g_theGCToCLR, gcContext, gen);

        public static void DiagWalkBGCSurvivors(void* gcContext) => Vtable->DiagWalkBGCSurvivors(g_theGCToCLR, gcContext);

        public static void StompWriteBarrier(WriteBarrierParameters* args) => Vtable->StompWriteBarrier(g_theGCToCLR, args);

        public static void EnableFinalization(byte gcHasWorkForFinalizerThread) => Vtable->EnableFinalization(g_theGCToCLR, gcHasWorkForFinalizerThread);

        public static void HandleFatalError(uint exitCode) => Vtable->HandleFatalError(g_theGCToCLR, exitCode);

        public static byte EagerFinalized(byte* obj) => Vtable->EagerFinalized(g_theGCToCLR, obj);

        public static void* GetFreeObjectMethodTable() => Vtable->GetFreeObjectMethodTable(g_theGCToCLR);

        public static byte GetBooleanConfigValue(byte* privateKey, byte* publicKey, byte* value) => Vtable->GetBooleanConfigValue(g_theGCToCLR, privateKey, publicKey, value);

        public static byte GetIntConfigValue(byte* privateKey, byte* publicKey, long* value) => Vtable->GetIntConfigValue(g_theGCToCLR, privateKey, publicKey, value);

        public static byte GetStringConfigValue(byte* privateKey, byte* publicKey, byte** value) => Vtable->GetStringConfigValue(g_theGCToCLR, privateKey, publicKey, value);

        public static void FreeStringConfigValue(byte* value) => Vtable->FreeStringConfigValue(g_theGCToCLR, value);

        public static byte IsGCThread() => Vtable->IsGCThread(g_theGCToCLR);

        public static byte WasCurrentThreadCreatedByGC() => Vtable->WasCurrentThreadCreatedByGC(g_theGCToCLR);

        public static void WalkAsyncPinnedForPromotion(byte* @object, ScanContext* sc, delegate* unmanaged<byte**, ScanContext*, uint, void> callback) => Vtable->WalkAsyncPinnedForPromotion(g_theGCToCLR, @object, sc, callback);

        public static void WalkAsyncPinned(byte* @object, void* context, delegate* unmanaged<byte*, byte*, void*, void> callback) => Vtable->WalkAsyncPinned(g_theGCToCLR, @object, context, callback);

        public static void* EventSink() => Vtable->EventSink(g_theGCToCLR);

        private static IGCToCLREventSinkVtable* EventSinkVtable(void* eventSink) => *(IGCToCLREventSinkVtable**)eventSink;

        public static void FireDynamicEvent(byte* name, void* payload, uint payloadSize)
        {
            void* eventSink = EventSink();
            EventSinkVtable(eventSink)->FireDynamicEvent(eventSink, name, payload, payloadSize);
        }

        public static void FireGCStart_V2(uint count, uint depth, uint reason, uint type) { void* sink = EventSink(); EventSinkVtable(sink)->FireGCStart_V2(sink, count, depth, reason, type); }
        public static void FireGCEnd_V1(uint count, uint depth) { void* sink = EventSink(); EventSinkVtable(sink)->FireGCEnd_V1(sink, count, depth); }
        public static void FireGCGenerationRange(byte generation, void* rangeStart, ulong rangeUsedLength, ulong rangeReservedLength) { void* sink = EventSink(); EventSinkVtable(sink)->FireGCGenerationRange(sink, generation, rangeStart, rangeUsedLength, rangeReservedLength); }
        public static void FireGCHeapStats_V2(ulong generationSize0, ulong totalPromotedSize0, ulong generationSize1, ulong totalPromotedSize1, ulong generationSize2, ulong totalPromotedSize2, ulong generationSize3, ulong totalPromotedSize3, ulong generationSize4, ulong totalPromotedSize4, ulong finalizationPromotedSize, ulong finalizationPromotedCount, uint pinnedObjectCount, uint sinkBlockCount, uint gcHandleCount) { void* sink = EventSink(); EventSinkVtable(sink)->FireGCHeapStats_V2(sink, generationSize0, totalPromotedSize0, generationSize1, totalPromotedSize1, generationSize2, totalPromotedSize2, generationSize3, totalPromotedSize3, generationSize4, totalPromotedSize4, finalizationPromotedSize, finalizationPromotedCount, pinnedObjectCount, sinkBlockCount, gcHandleCount); }
        public static void FireGCCreateSegment_V1(void* address, nuint size, uint type) { void* sink = EventSink(); EventSinkVtable(sink)->FireGCCreateSegment_V1(sink, address, size, type); }
        public static void FireGCFreeSegment_V1(void* address) { void* sink = EventSink(); EventSinkVtable(sink)->FireGCFreeSegment_V1(sink, address); }
        public static void FireGCCreateConcurrentThread_V1() { void* sink = EventSink(); EventSinkVtable(sink)->FireGCCreateConcurrentThread_V1(sink); }
        public static void FireGCTerminateConcurrentThread_V1() { void* sink = EventSink(); EventSinkVtable(sink)->FireGCTerminateConcurrentThread_V1(sink); }
        public static void FireGCTriggered(uint reason) { void* sink = EventSink(); EventSinkVtable(sink)->FireGCTriggered(sink, reason); }
        public static void FireGCMarkWithType(uint heapNum, uint type, ulong bytes) { void* sink = EventSink(); EventSinkVtable(sink)->FireGCMarkWithType(sink, heapNum, type, bytes); }
        public static void FireGCJoin_V2(uint heap, uint joinTime, uint joinType, uint joinId) { void* sink = EventSink(); EventSinkVtable(sink)->FireGCJoin_V2(sink, heap, joinTime, joinType, joinId); }
        public static void FireGCGlobalHeapHistory_V4(ulong finalYoungestDesired, int numHeaps, uint condemnedGeneration, uint gen0ReductionCount, uint reason, uint globalMechanisms, uint pauseMode, uint memoryPressure, uint condemnReasons0, uint condemnReasons1, uint count, uint valuesLen, void* values) { void* sink = EventSink(); EventSinkVtable(sink)->FireGCGlobalHeapHistory_V4(sink, finalYoungestDesired, numHeaps, condemnedGeneration, gen0ReductionCount, reason, globalMechanisms, pauseMode, memoryPressure, condemnReasons0, condemnReasons1, count, valuesLen, values); }
        public static void FireGCAllocationTick_V1(uint allocationAmount, uint allocationKind) { void* sink = EventSink(); EventSinkVtable(sink)->FireGCAllocationTick_V1(sink, allocationAmount, allocationKind); }
        public static void FireGCAllocationTick_V4(ulong allocationAmount, uint allocationKind, uint heapIndex, void* objectAddress, ulong objectSize) { void* sink = EventSink(); EventSinkVtable(sink)->FireGCAllocationTick_V4(sink, allocationAmount, allocationKind, heapIndex, objectAddress, objectSize); }
        public static void FirePinObjectAtGCTime(void* objectAddress, byte** objectHandle) { void* sink = EventSink(); EventSinkVtable(sink)->FirePinObjectAtGCTime(sink, objectAddress, objectHandle); }
        public static void FirePinPlugAtGCTime(byte* plugStart, byte* plugEnd, byte* gapBeforeSize) { void* sink = EventSink(); EventSinkVtable(sink)->FirePinPlugAtGCTime(sink, plugStart, plugEnd, gapBeforeSize); }
        public static void FireGCPerHeapHistory_V3(void* freeListAllocated, void* freeListRejected, void* endOfSegAllocated, void* condemnedAllocated, void* pinnedAllocated, void* pinnedAllocatedAdvance, uint runningFreeListEfficiency, uint condemnReasons0, uint condemnReasons1, uint compactMechanisms, uint expandMechanisms, uint heapIndex, void* extraGen0Commit, uint count, uint valuesLen, void* values) { void* sink = EventSink(); EventSinkVtable(sink)->FireGCPerHeapHistory_V3(sink, freeListAllocated, freeListRejected, endOfSegAllocated, condemnedAllocated, pinnedAllocated, pinnedAllocatedAdvance, runningFreeListEfficiency, condemnReasons0, condemnReasons1, compactMechanisms, expandMechanisms, heapIndex, extraGen0Commit, count, valuesLen, values); }
        public static void FireGCLOHCompact(ushort count, uint valuesLen, void* values) { void* sink = EventSink(); EventSinkVtable(sink)->FireGCLOHCompact(sink, count, valuesLen, values); }
        public static void FireGCFitBucketInfo(ushort kind, nuint totalSize, ushort count, uint valuesLen, void* values) { void* sink = EventSink(); EventSinkVtable(sink)->FireGCFitBucketInfo(sink, kind, totalSize, count, valuesLen, values); }
        public static void FireBGCBegin() { void* sink = EventSink(); EventSinkVtable(sink)->FireBGCBegin(sink); }
        public static void FireBGC1stNonConEnd() { void* sink = EventSink(); EventSinkVtable(sink)->FireBGC1stNonConEnd(sink); }
        public static void FireBGC1stConEnd() { void* sink = EventSink(); EventSinkVtable(sink)->FireBGC1stConEnd(sink); }
        public static void FireBGC1stSweepEnd(uint genNumber) { void* sink = EventSink(); EventSinkVtable(sink)->FireBGC1stSweepEnd(sink, genNumber); }
        public static void FireBGC2ndNonConBegin() { void* sink = EventSink(); EventSinkVtable(sink)->FireBGC2ndNonConBegin(sink); }
        public static void FireBGC2ndNonConEnd() { void* sink = EventSink(); EventSinkVtable(sink)->FireBGC2ndNonConEnd(sink); }
        public static void FireBGC2ndConBegin() { void* sink = EventSink(); EventSinkVtable(sink)->FireBGC2ndConBegin(sink); }
        public static void FireBGC2ndConEnd() { void* sink = EventSink(); EventSinkVtable(sink)->FireBGC2ndConEnd(sink); }
        public static void FireBGCDrainMark(ulong objects) { void* sink = EventSink(); EventSinkVtable(sink)->FireBGCDrainMark(sink, objects); }
        public static void FireBGCRevisit(ulong pages, ulong objects, uint isLarge) { void* sink = EventSink(); EventSinkVtable(sink)->FireBGCRevisit(sink, pages, objects, isLarge); }
        public static void FireBGCOverflow_V1(ulong min, ulong max, ulong objects, uint isLarge, uint genNumber) { void* sink = EventSink(); EventSinkVtable(sink)->FireBGCOverflow_V1(sink, min, max, objects, isLarge, genNumber); }
        public static void FireBGCAllocWaitBegin(uint reason) { void* sink = EventSink(); EventSinkVtable(sink)->FireBGCAllocWaitBegin(sink, reason); }
        public static void FireBGCAllocWaitEnd(uint reason) { void* sink = EventSink(); EventSinkVtable(sink)->FireBGCAllocWaitEnd(sink, reason); }
        public static void FireGCFullNotify_V1(uint genNumber, uint isAlloc) { void* sink = EventSink(); EventSinkVtable(sink)->FireGCFullNotify_V1(sink, genNumber, isAlloc); }
        public static void FireSetGCHandle(void* handleId, void* objectId, uint kind, uint generation) { void* sink = EventSink(); EventSinkVtable(sink)->FireSetGCHandle(sink, handleId, objectId, kind, generation); }
        public static void FirePrvSetGCHandle(void* handleId, void* objectId, uint kind, uint generation) { void* sink = EventSink(); EventSinkVtable(sink)->FirePrvSetGCHandle(sink, handleId, objectId, kind, generation); }
        public static void FireDestroyGCHandle(void* handleId) { void* sink = EventSink(); EventSinkVtable(sink)->FireDestroyGCHandle(sink, handleId); }
        public static void FirePrvDestroyGCHandle(void* handleId) { void* sink = EventSink(); EventSinkVtable(sink)->FirePrvDestroyGCHandle(sink, handleId); }

        public static uint GetTotalNumSizedRefHandles() => Vtable->GetTotalNumSizedRefHandles(g_theGCToCLR);

        public static byte AnalyzeSurvivorsRequested(int condemnedGeneration) => Vtable->AnalyzeSurvivorsRequested(g_theGCToCLR, condemnedGeneration);

        public static void AnalyzeSurvivorsFinished(nuint gcIndex, int condemnedGeneration, ulong promoted_bytes, delegate* unmanaged<void> reportGenerationBounds) => Vtable->AnalyzeSurvivorsFinished(g_theGCToCLR, gcIndex, condemnedGeneration, promoted_bytes, reportGenerationBounds);

        public static void VerifySyncTableEntry() => Vtable->VerifySyncTableEntry(g_theGCToCLR);

        public static void UpdateGCEventStatus(int publicLevel, int publicKeywords, int privateLevel, int privateKeywords) => Vtable->UpdateGCEventStatus(g_theGCToCLR, publicLevel, publicKeywords, privateLevel, privateKeywords);

        public static void LogStressMsg(uint level, uint facility, void* msg) => Vtable->LogStressMsg(g_theGCToCLR, level, facility, msg);

        public static uint GetCurrentProcessCpuCount() => Vtable->GetCurrentProcessCpuCount(g_theGCToCLR);

        public static void DiagAddNewRegion(int generation, byte* rangeStart, byte* rangeEnd, byte* rangeEndReserved) => Vtable->DiagAddNewRegion(g_theGCToCLR, generation, rangeStart, rangeEnd, rangeEndReserved);

        public static void LogErrorToHost(byte* message) => Vtable->LogErrorToHost(g_theGCToCLR, message);

        public static ulong GetThreadOSThreadId(void* thread) => Vtable->GetThreadOSThreadId(g_theGCToCLR, thread);

        public static void TriggerClientBridgeProcessing(MarkCrossReferencesArgs* args) => Vtable->TriggerClientBridgeProcessing(g_theGCToCLR, args);

    }
}
