// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Behavior tests for the dependency-closed parts of gcscan.cpp. The production GCScan body is
// compiled directly into this assembly; only the IGCToCLR root-scan call beneath it is
// substituted.

using Xunit;

namespace Internal.Runtime.GarbageCollection;

[Collection(SyncImportsCollection.Name)]
public sealed unsafe class GCScanTests
{
    private static int s_handleScanCallCount;
    private static nuint s_firstHandleScanValue;
    private static uint s_firstHandleScanFlags;
    private static nuint s_secondHandleScanValue;
    private static uint s_secondHandleScanFlags;
    private static uint s_handleScanFlags;
    private static int s_dependentPromotionCount;

    public GCScanTests()
    {
        GCScan.Initialize();
        GCToEEInterface.Reset();
    }

    [Fact]
    public void RuntimeStructuresStartInvalid()
    {
        Assert.False(GCScan.GetGcRuntimeStructuresValid());
    }

    [Fact]
    public void RuntimeStructureValidityCountsNestedInvalidRegions()
    {
        GCScan.GcRuntimeStructuresValid(1);
        Assert.True(GCScan.GetGcRuntimeStructuresValid());

        GCScan.GcRuntimeStructuresValid(0);
        GCScan.GcRuntimeStructuresValid(0);
        Assert.False(GCScan.GetGcRuntimeStructuresValid());

        GCScan.GcRuntimeStructuresValid(1);
        Assert.False(GCScan.GetGcRuntimeStructuresValid());

        GCScan.GcRuntimeStructuresValid(1);
        Assert.True(GCScan.GetGcRuntimeStructuresValid());
    }

    [Fact]
    public void GcScanRootsForwardsEveryArgument()
    {
        ScanContext sc = default;
        delegate*<byte**, ScanContext*, uint, void> callback = &Promote;

        GCScan.GcScanRoots(callback, 2, 3, &sc);

        Assert.Equal(1, GCToEEInterface.GcScanRootsCallCount);
        Assert.Equal((nuint)callback, GCToEEInterface.LastGcScanRootsCallback);
        Assert.Equal(2, GCToEEInterface.LastGcScanRootsCondemned);
        Assert.Equal(3, GCToEEInterface.LastGcScanRootsMaxGeneration);
        Assert.True(GCToEEInterface.LastGcScanRootsContext == &sc);
    }

    [Fact]
    public void GcScanHandlesPromotesPinnedBeforeStrongAndSkipsOtherHandleTypes()
    {
        Assert.True(ObjectHandle.Ref_Initialize());
        HandleTableBucket* bucket = (HandleTableBucket*)System.Runtime.CompilerServices.Unsafe.AsPointer(
            ref ObjectHandle.g_GlobalHandleTableBucket);
        HandleTable* table = bucket->pTable[0];

        try
        {
            _ = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_STRONG,
                (byte*)0x2000,
                0);
            _ = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_PINNED,
                (byte*)0x1000,
                0);
            _ = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_WEAK_SHORT,
                (byte*)0x3000,
                0);
            _ = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_WEAK_LONG,
                (byte*)0x3100,
                0);
            _ = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_DEPENDENT,
                (byte*)0x4000,
                0);
            _ = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_ASYNCPINNED,
                (byte*)0x5000,
                0);
            _ = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_STRONG,
                null,
                0);
            OBJECTHANDLE destroyed = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_STRONG,
                (byte*)0x6000,
                0);
            HandleTableManager.HndDestroyHandle(table, (uint)HandleType.HNDTYPE_STRONG, destroyed);
#if DEBUG
            Assert.Equal(HandleTableConstants.DEBUG_DestroyedHandleValue, *(nuint*)destroyed.Value);
#endif

            ScanContext sc = default;
            sc.promotion = 1;
            ResetHandleScanObservations();

            GCScan.GcScanHandles(&RecordHandle, 2, 2, &sc);

            Assert.Equal(2, s_handleScanCallCount);
            Assert.Equal((nuint)0x1000, s_firstHandleScanValue);
            Assert.Equal((uint)GCCallFlags.GC_CALL_PINNED, s_firstHandleScanFlags);
            Assert.Equal((nuint)0x2000, s_secondHandleScanValue);
            Assert.Equal(0u, s_secondHandleScanFlags);
        }
        finally
        {
            ObjectHandle.Ref_DestroyHandleTableBucket(bucket);
            ObjectHandle.Ref_Shutdown();
        }
    }

    [Fact]
    public void GcScanHandlesScansEveryBlockInTheStrongTypeChain()
    {
        const int HandleCount = HandleTableConstants.HANDLE_HANDLES_PER_BLOCK + 1;

        Assert.True(ObjectHandle.Ref_Initialize());
        HandleTableBucket* bucket = (HandleTableBucket*)System.Runtime.CompilerServices.Unsafe.AsPointer(
            ref ObjectHandle.g_GlobalHandleTableBucket);
        HandleTable* table = bucket->pTable[0];

        try
        {
            OBJECTHANDLE* handles = stackalloc OBJECTHANDLE[HandleCount];
            for (int i = 0; i < HandleCount; i++)
            {
                handles[i] = HandleTableManager.HndCreateHandle(
                    table,
                    (uint)HandleType.HNDTYPE_STRONG,
                    (byte*)(nuint)(0x1000 + (i * 0x10)),
                    0);
            }

            TableSegment* segment = table->pSegmentList;
            uint tail = segment->Header.rgTail[(uint)HandleType.HNDTYPE_STRONG];
            uint head = segment->Header.rgAllocation[tail];
            Assert.NotEqual(tail, head);

            ScanContext sc = default;
            sc.promotion = 1;
            ResetHandleScanObservations();

            GCScan.GcScanHandles(&RecordHandle, 2, 2, &sc);

            Assert.Equal(HandleCount, s_handleScanCallCount);
            Assert.Equal(0u, s_handleScanFlags);
        }
        finally
        {
            ObjectHandle.Ref_DestroyHandleTableBucket(bucket);
            ObjectHandle.Ref_Shutdown();
        }
    }

    [Theory]
    [InlineData(0, 0, 2, 2)]
    [InlineData(1, 1, 2, 2)]
    [InlineData(1, 0, 1, 2)]
    public void GcScanHandlesExcludesNonPromotionConcurrentAndEphemeralScans(
        int promotion,
        int concurrent,
        int condemned,
        int maxGeneration)
    {
        Assert.True(ObjectHandle.Ref_Initialize());
        HandleTableBucket* bucket = (HandleTableBucket*)System.Runtime.CompilerServices.Unsafe.AsPointer(
            ref ObjectHandle.g_GlobalHandleTableBucket);

        try
        {
            _ = HandleTableManager.HndCreateHandle(
                bucket->pTable[0],
                (uint)HandleType.HNDTYPE_STRONG,
                (byte*)0x1000,
                0);

            ScanContext sc = default;
            sc.promotion = (byte)promotion;
            sc.concurrent = (byte)concurrent;
            ResetHandleScanObservations();

            GCScan.GcScanHandles(&RecordHandle, condemned, maxGeneration, &sc);

            Assert.Equal(0, s_handleScanCallCount);
        }
        finally
        {
            ObjectHandle.Ref_DestroyHandleTableBucket(bucket);
            ObjectHandle.Ref_Shutdown();
        }
    }

    [Fact]
    public void DependentHandleWithLivePrimaryPromotesSecondary()
    {
        Assert.True(ObjectHandle.Ref_Initialize());
        HandleTableBucket* bucket = (HandleTableBucket*)System.Runtime.CompilerServices.Unsafe.AsPointer(
            ref ObjectHandle.g_GlobalHandleTableBucket);
        HandleTable* table = bucket->pTable[0];

        try
        {
            MethodTable* methodTable = stackalloc MethodTable[1];
            CObjectHeader* objects = stackalloc CObjectHeader[2];
            objects[0].RawSetMethodTable(methodTable);
            objects[1].RawSetMethodTable(methodTable);
            objects[0].SetMarked();

            _ = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_DEPENDENT,
                (byte*)&objects[0],
                (nuint)(void*)&objects[1]);

            ScanContext sc = default;
            s_dependentPromotionCount = 0;

            GCScan.GcDhInitialScan(&MarkDependentObject, 2, 2, &sc);

            Assert.Equal(1, s_dependentPromotionCount);
            Assert.True(objects[1].IsMarked() != 0);
            Assert.False(GCScan.GcDhUnpromotedHandlesExist(&sc));
        }
        finally
        {
            ObjectHandle.Ref_DestroyHandleTableBucket(bucket);
            ObjectHandle.Ref_Shutdown();
        }
    }

    [Fact]
    public void DependentHandleInitialScanClosesReverseChains()
    {
        Assert.True(ObjectHandle.Ref_Initialize());
        HandleTableBucket* bucket = (HandleTableBucket*)System.Runtime.CompilerServices.Unsafe.AsPointer(
            ref ObjectHandle.g_GlobalHandleTableBucket);
        HandleTable* table = bucket->pTable[0];

        try
        {
            MethodTable* methodTable = stackalloc MethodTable[1];
            CObjectHeader* objects = stackalloc CObjectHeader[3];
            objects[0].RawSetMethodTable(methodTable);
            objects[1].RawSetMethodTable(methodTable);
            objects[2].RawSetMethodTable(methodTable);
            objects[2].SetMarked();

            _ = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_DEPENDENT,
                (byte*)&objects[0],
                (nuint)(void*)&objects[1]);
            _ = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_DEPENDENT,
                (byte*)&objects[2],
                (nuint)(void*)&objects[0]);

            ScanContext sc = default;
            s_dependentPromotionCount = 0;

            GCScan.GcDhInitialScan(&MarkDependentObject, 2, 2, &sc);

            Assert.Equal(2, s_dependentPromotionCount);
            Assert.True(objects[0].IsMarked() != 0);
            Assert.True(objects[1].IsMarked() != 0);
            Assert.False(GCScan.GcDhUnpromotedHandlesExist(&sc));
        }
        finally
        {
            ObjectHandle.Ref_DestroyHandleTableBucket(bucket);
            ObjectHandle.Ref_Shutdown();
        }
    }

    [Fact]
    public void DependentHandleWithDeadPrimarySkipsSecondaryAndSupportsRescan()
    {
        Assert.True(ObjectHandle.Ref_Initialize());
        HandleTableBucket* bucket = (HandleTableBucket*)System.Runtime.CompilerServices.Unsafe.AsPointer(
            ref ObjectHandle.g_GlobalHandleTableBucket);
        HandleTable* table = bucket->pTable[0];

        try
        {
            MethodTable* methodTable = stackalloc MethodTable[1];
            CObjectHeader* objects = stackalloc CObjectHeader[2];
            objects[0].RawSetMethodTable(methodTable);
            objects[1].RawSetMethodTable(methodTable);

            _ = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_DEPENDENT,
                (byte*)&objects[0],
                (nuint)(void*)&objects[1]);

            ScanContext sc = default;
            s_dependentPromotionCount = 0;

            GCScan.GcDhInitialScan(&MarkDependentObject, 2, 2, &sc);

            Assert.Equal(0, s_dependentPromotionCount);
            Assert.Equal(0, objects[1].IsMarked());
            Assert.True(GCScan.GcDhUnpromotedHandlesExist(&sc));

            objects[0].SetMarked();
            Assert.True(GCScan.GcDhReScan(&sc));

            Assert.Equal(1, s_dependentPromotionCount);
            Assert.True(objects[1].IsMarked() != 0);
            Assert.False(GCScan.GcDhUnpromotedHandlesExist(&sc));
        }
        finally
        {
            ObjectHandle.Ref_DestroyHandleTableBucket(bucket);
            ObjectHandle.Ref_Shutdown();
        }
    }

    private static void Promote(byte** objectRef, ScanContext* sc, uint flags)
    {
    }

    private static void MarkDependentObject(byte** objectRef, ScanContext* sc, uint flags)
    {
        _ = sc;
        _ = flags;
        s_dependentPromotionCount++;
        ((CObjectHeader*)*objectRef)->SetMarked();
    }

    private static void ResetHandleScanObservations()
    {
        s_handleScanCallCount = 0;
        s_firstHandleScanValue = 0;
        s_firstHandleScanFlags = 0;
        s_secondHandleScanValue = 0;
        s_secondHandleScanFlags = 0;
        s_handleScanFlags = 0;
    }

    private static void RecordHandle(byte** objectRef, ScanContext* sc, uint flags)
    {
        int callCount = s_handleScanCallCount++;
        if (callCount == 0)
        {
            s_firstHandleScanValue = (nuint)(*objectRef);
            s_firstHandleScanFlags = flags;
        }
        else if (callCount == 1)
        {
            s_secondHandleScanValue = (nuint)(*objectRef);
            s_secondHandleScanFlags = flags;
        }

        s_handleScanFlags |= flags;
    }

#if USE_REGIONS
    [Fact]
    public void MarkPhaseStackRootsScansCondemnedInteriorAndPinnedRootsAndRescansDependentHandlesAfterOverflow()
    {
        using MarkPhaseStateScope scope = new();
        bool handlesInitialized = false;
        mark* markStack = null;
        CFinalize* previousFinalizeQueue = gc_heap.finalize_queue;
        CFinalize* finalizeQueue = null;
        GCToOSInterface.ResetRecording();
        SyncImports.ResetRecording();
        GCConfig.Initialize();
        GCCommon.initialize();
        Assert.True(gc_heap.check_commit_cs.Initialize());
        Assert.Equal(0, ManagedGCRegionBootstrap.Prepare());

        try
        {
            Assert.True(ManagedGCRegionBootstrap.Initialize());

            gc_heap* heap = ManagedGCRegionBootstrap.Heap;
            generation* generations = ManagedGCRegionBootstrap.GenerationTable;
            Assert.True(heap is not null);
            Assert.True(generations is not null);

            nuint objectSize = (nuint)GCInterfaceOffsets.min_obj_size;
            byte* rootDescriptor = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
            byte* leafDescriptor = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
            MethodTable* rootMethodTable = InitializeMethodTable(
                rootDescriptor,
                objectSize,
                pointerCount: 1,
                hasPointers: 1);
            MethodTable* leafMethodTable = InitializeMethodTable(
                leafDescriptor,
                objectSize,
                pointerCount: 0,
                hasPointers: 0);

            heap_segment* gen0Segment = generation.generation_start_segment(
                gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0));
            heap_segment* gen1Segment = generation.generation_start_segment(
                gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen1));
            Assert.True(gen0Segment is not null);
            Assert.True(gen1Segment is not null);

            byte* root = heap_segment.heap_segment_mem(gen0Segment);
            byte* child = root + (nint)objectSize;
            byte* nonCondemned = heap_segment.heap_segment_mem(gen1Segment);
            ((CObjectHeader*)root)->RawSetMethodTable(rootMethodTable);
            ((CObjectHeader*)child)->RawSetMethodTable(leafMethodTable);
            ((CObjectHeader*)nonCondemned)->RawSetMethodTable(leafMethodTable);
            *((byte**)(root + sizeof(nuint))) = child;
            heap_segment.heap_segment_allocated(gen0Segment) = child + (nint)objectSize;

            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen0;
            gc_heap.g_mark_list = gc_heap.make_mark_list(2);
            gc_heap.g_mark_list_copy = null;
            gc_heap.mark_list_size = 2;
            gc_heap.g_mark_list_total_size = 2;
            gc_heap.g_mark_list_piece = null;
            gc_heap.g_mark_list_piece_size = 0;
            gc_heap.g_mark_list_piece_total_size = 0;
            Assert.True(gc_heap.g_mark_list is not null);

            byte* rootSlot = root;
            int afterGcScanRootsObserverCallCount = 0;
            GCToEEInterface.AfterGcScanRootsObserver = () =>
            {
                afterGcScanRootsObserverCallCount++;
                Assert.True(((CObjectHeader*)root)->IsMarked() != 0);
                Assert.True(((CObjectHeader*)child)->IsMarked() != 0);
                gc_heap.mark_queue.verify_empty();
            };
            ScanStackRoot(&rootSlot, 0);
            Assert.Equal(1, afterGcScanRootsObserverCallCount);
            GCToEEInterface.AfterGcScanRootsObserver = null;

            Assert.True(((CObjectHeader*)root)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)child)->IsMarked() != 0);
            Assert.Equal((nuint)root, (nuint)gc_heap.g_mark_list[0]);
            Assert.Equal((nuint)child, (nuint)gc_heap.g_mark_list[1]);
            Assert.Equal((nuint)(delegate*<byte**, ScanContext*, uint, void>)&gc_heap.promote,
                GCToEEInterface.LastGcScanRootsCallback);
            Assert.True(GCToEEInterface.LastGcScanRootsCallbackContext == GCToEEInterface.LastGcScanRootsContext);
            Assert.Equal(0u, GCToEEInterface.LastGcScanRootsCallbackFlags);
            AssertInitializedStackScanContext(GCToEEInterface.LastGcScanRootsContextValue);
            gc_heap.mark_queue.verify_empty();

            ((CObjectHeader*)root)->ClearMarked();
            ((CObjectHeader*)child)->ClearMarked();

            rootSlot = null;
            ScanStackRoot(&rootSlot, 0);
            Assert.Equal(0, ((CObjectHeader*)root)->IsMarked());

            byte* nonHeap = stackalloc byte[1];
            rootSlot = nonHeap;
            ScanStackRoot(&rootSlot, 0);
            Assert.Equal(0, ((CObjectHeader*)root)->IsMarked());

            rootSlot = nonCondemned;
            ScanStackRoot(&rootSlot, 0);
            Assert.Equal(0, ((CObjectHeader*)nonCondemned)->IsMarked());

            byte* interiorRootSlot = root + sizeof(nuint);
            byte* pinnedRootSlot = root;
            byte* duplicatePinnedRootSlot = root;
            byte* interiorPinnedRootSlot = root + sizeof(nuint);
            GCToEEInterface.GcScanRootsSlots.Add(((nuint)(void*)&interiorRootSlot, (uint)GCCallFlags.GC_CALL_INTERIOR));
            GCToEEInterface.GcScanRootsSlots.Add(((nuint)(void*)&pinnedRootSlot, (uint)GCCallFlags.GC_CALL_PINNED));
            GCToEEInterface.GcScanRootsSlots.Add(((nuint)(void*)&duplicatePinnedRootSlot, (uint)GCCallFlags.GC_CALL_PINNED));
            GCToEEInterface.GcScanRootsSlots.Add(((nuint)(void*)&interiorPinnedRootSlot,
                (uint)GCCallFlags.GC_CALL_INTERIOR | (uint)GCCallFlags.GC_CALL_PINNED));
            ScanStackRoots();
            Assert.True(((CObjectHeader*)root)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)child)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)root)->IsPinned() != 0);
            Assert.Equal((nuint)3, gc_heap.num_pinned_objects);
            Assert.Equal((nuint)root, (nuint)gc_heap.g_mark_list[0]);
            Assert.Equal((nuint)child, (nuint)gc_heap.g_mark_list[1]);
            Assert.Equal(
                (uint)GCCallFlags.GC_CALL_INTERIOR | (uint)GCCallFlags.GC_CALL_PINNED,
                GCToEEInterface.LastGcScanRootsCallbackFlags);

            GCToEEInterface.GcScanRootsSlots.Clear();
            rootSlot = null;
            ScanStackRoot(&rootSlot, 0);
            Assert.Equal((nuint)0, gc_heap.num_pinned_objects);
            Assert.Equal(6, GCToEEInterface.GcScanRootsCallCount);
            Assert.Equal(6, GCToEEInterface.BeforeGcScanRootsCallCount);
            Assert.Equal(6, GCToEEInterface.AfterGcScanRootsCallCount);
            Assert.Equal(18, GCToEEInterface.RootScanCallOrder.Count);
            for (int index = 0; index < GCToEEInterface.RootScanCallOrder.Count; index += 3)
            {
                Assert.Equal(RootScanCall.Before, GCToEEInterface.RootScanCallOrder[index]);
                Assert.Equal(RootScanCall.Scan, GCToEEInterface.RootScanCallOrder[index + 1]);
                Assert.Equal(RootScanCall.After, GCToEEInterface.RootScanCallOrder[index + 2]);
            }

#if !MULTIPLE_HEAPS
            gc_heap.gen0_must_clear_bricks = gc_heap.FFIND_DECAY;
            for (int remaining = gc_heap.FFIND_DECAY - 1; remaining >= 0; remaining--)
            {
                rootSlot = null;
                ScanStackRoot(&rootSlot, 0);
                Assert.Equal(remaining, gc_heap.gen0_must_clear_bricks);
            }

            ScanStackRoot(&rootSlot, 0);
            Assert.Equal(0, gc_heap.gen0_must_clear_bricks);
#endif

            Assert.Equal((int)gc_generation_num.soh_gen0, GCToEEInterface.LastBeforeGcScanRootsCondemned);
            Assert.Equal((byte)0, GCToEEInterface.LastBeforeGcScanRootsIsBackground);
            Assert.Equal((byte)0, GCToEEInterface.LastBeforeGcScanRootsIsConcurrent);
            Assert.Equal((int)gc_generation_num.soh_gen0, GCToEEInterface.LastAfterGcScanRootsCondemned);
            Assert.Equal(GCInterfaceOffsets.max_generation, GCToEEInterface.LastAfterGcScanRootsMaxGeneration);
            Assert.True(GCToEEInterface.LastAfterGcScanRootsContext == GCToEEInterface.LastGcScanRootsContext);
            AssertInitializedStackScanContext(GCToEEInterface.LastAfterGcScanRootsContextValue);
            gc_heap.mark_queue.verify_empty();

            SyncImports.ManagedGC_Free(gc_heap.g_mark_list);
            gc_heap.g_mark_list = gc_heap.make_mark_list(8);
            gc_heap.mark_list_size = 8;
            gc_heap.g_mark_list_total_size = 8;
            Assert.True(gc_heap.g_mark_list is not null);

            rootMethodTable->m_uFlags |= MethodTable.HasFinalizerFlag;
            byte* finalizableRoot = child + (nint)objectSize;
            byte* finalizableChild = finalizableRoot + (nint)objectSize;
            ((CObjectHeader*)finalizableRoot)->RawSetMethodTable(rootMethodTable);
            ((CObjectHeader*)finalizableChild)->RawSetMethodTable(leafMethodTable);
            *((byte**)(finalizableRoot + sizeof(nuint))) = finalizableChild;

            finalizeQueue = CFinalize.Allocate();
            Assert.True(finalizeQueue is not null);
            gc_heap.finalize_queue = finalizeQueue;
            Assert.True(finalizeQueue->RegisterForFinalization((int)gc_generation_num.soh_gen0, finalizableRoot));
            Assert.True(finalizeQueue->ScanForFinalization(
                &IgnoreFinalizableRoot,
                (int)gc_generation_num.soh_gen2,
                heap));

            byte* pinnedHandleRoot = finalizableChild + (nint)objectSize;
            byte* pinnedHandleChild = pinnedHandleRoot + (nint)objectSize;
            byte* strongHandleRoot = pinnedHandleChild + (nint)objectSize;
            byte* strongHandleChild = strongHandleRoot + (nint)objectSize;
            byte* weakHandleRoot = strongHandleChild + (nint)objectSize;
            byte* dependentHandleRoot = weakHandleRoot + (nint)objectSize;
            byte* dependentHandleChild = dependentHandleRoot + (nint)objectSize;
            byte* finalizableWeakRoot = dependentHandleChild + (nint)objectSize;
            byte* finalizableWeakChild = finalizableWeakRoot + (nint)objectSize;
            byte* finalizableDependentChild = finalizableWeakChild + (nint)objectSize;
            byte* deadLongWeakRoot = finalizableDependentChild + (nint)objectSize;
            byte* deadDependentPrimary = deadLongWeakRoot + (nint)objectSize;
            byte* deadDependentSecondary = deadDependentPrimary + (nint)objectSize;
            byte* syncBlockWeakRoot = deadDependentSecondary + (nint)objectSize;
            ((CObjectHeader*)pinnedHandleRoot)->RawSetMethodTable(rootMethodTable);
            ((CObjectHeader*)pinnedHandleChild)->RawSetMethodTable(leafMethodTable);
            ((CObjectHeader*)strongHandleRoot)->RawSetMethodTable(rootMethodTable);
            ((CObjectHeader*)strongHandleChild)->RawSetMethodTable(leafMethodTable);
            ((CObjectHeader*)weakHandleRoot)->RawSetMethodTable(leafMethodTable);
            ((CObjectHeader*)dependentHandleRoot)->RawSetMethodTable(rootMethodTable);
            ((CObjectHeader*)dependentHandleChild)->RawSetMethodTable(leafMethodTable);
            ((CObjectHeader*)finalizableWeakRoot)->RawSetMethodTable(rootMethodTable);
            ((CObjectHeader*)finalizableWeakChild)->RawSetMethodTable(leafMethodTable);
            ((CObjectHeader*)finalizableDependentChild)->RawSetMethodTable(leafMethodTable);
            ((CObjectHeader*)deadLongWeakRoot)->RawSetMethodTable(leafMethodTable);
            ((CObjectHeader*)deadDependentPrimary)->RawSetMethodTable(leafMethodTable);
            ((CObjectHeader*)deadDependentSecondary)->RawSetMethodTable(leafMethodTable);
            ((CObjectHeader*)syncBlockWeakRoot)->RawSetMethodTable(leafMethodTable);
            *((byte**)(pinnedHandleRoot + sizeof(nuint))) = pinnedHandleChild;
            *((byte**)(strongHandleRoot + sizeof(nuint))) = strongHandleChild;
            *((byte**)(dependentHandleRoot + sizeof(nuint))) = dependentHandleChild;
            *((byte**)(finalizableWeakRoot + sizeof(nuint))) = finalizableWeakChild;
            heap_segment.heap_segment_allocated(gen0Segment) = syncBlockWeakRoot + (nint)objectSize;
            Assert.True(finalizeQueue->RegisterForFinalization(
                (int)gc_generation_num.soh_gen0,
                finalizableWeakRoot));

            markStack = (mark*)SyncImports.ManagedGC_AllocZeroed(
                (nuint)gc_rand.MARK_STACK_INITIAL_LENGTH * (nuint)sizeof(mark));
            Assert.True(markStack is not null);
            gc_heap.make_mark_stack(heap, markStack);

            Assert.True(ObjectHandle.Ref_Initialize());
            handlesInitialized = true;
            HandleTableBucket* bucket = (HandleTableBucket*)System.Runtime.CompilerServices.Unsafe.AsPointer(
                ref ObjectHandle.g_GlobalHandleTableBucket);
            HandleTable* table = bucket->pTable[0];
            _ = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_STRONG,
                strongHandleRoot,
                0);
            _ = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_PINNED,
                pinnedHandleRoot,
                0);
            OBJECTHANDLE shortWeak = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_WEAK_SHORT,
                weakHandleRoot,
                0);
            OBJECTHANDLE finalizableShortWeak = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_WEAK_SHORT,
                finalizableWeakRoot,
                0);
            OBJECTHANDLE survivingLongWeak = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_WEAK_LONG,
                strongHandleRoot,
                0);
            OBJECTHANDLE finalizableLongWeak = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_WEAK_LONG,
                finalizableWeakRoot,
                0);
            OBJECTHANDLE deadLongWeak = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_WEAK_LONG,
                deadLongWeakRoot,
                0);
            OBJECTHANDLE finalizableDependent = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_DEPENDENT,
                finalizableWeakRoot,
                (nuint)(void*)finalizableDependentChild);
            OBJECTHANDLE deadDependent = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_DEPENDENT,
                deadDependentPrimary,
                (nuint)(void*)deadDependentSecondary);
            _ = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_DEPENDENT,
                strongHandleRoot,
                (nuint)(void*)dependentHandleRoot);

            GCToEEInterface.Reset();
            gc_heap.settings.condemned_generation = GCInterfaceOffsets.max_generation;
            GCToEEInterface.GcScanRootsSlots.Clear();
            GCToEEInterface.GcScanRootsSlot = null;
            GCToEEInterface.GcScanRootsFlags = 0;
            byte* syncBlockWeakSlot = syncBlockWeakRoot;
            GCToEEInterface.SyncBlockCacheWeakPtrScanSlot = &syncBlockWeakSlot;
            int afterHandleScanObserverCallCount = 0;
            GCToEEInterface.AfterGcScanRootsObserver = () =>
            {
                afterHandleScanObserverCallCount++;
                Assert.True(((CObjectHeader*)finalizableRoot)->IsMarked() != 0);
                Assert.True(((CObjectHeader*)finalizableChild)->IsMarked() != 0);
                Assert.True(((CObjectHeader*)pinnedHandleRoot)->IsMarked() != 0);
                Assert.True(((CObjectHeader*)pinnedHandleChild)->IsMarked() != 0);
                Assert.True(((CObjectHeader*)strongHandleRoot)->IsMarked() != 0);
                Assert.True(((CObjectHeader*)strongHandleChild)->IsMarked() != 0);
                Assert.True(((CObjectHeader*)dependentHandleRoot)->IsMarked() != 0);
                Assert.True(((CObjectHeader*)dependentHandleChild)->IsMarked() != 0);
                Assert.Equal(0, ((CObjectHeader*)finalizableWeakRoot)->IsMarked());
                Assert.Equal((nuint)finalizableWeakRoot, *(nuint*)finalizableShortWeak.Value);
                Assert.Equal(0, ((CObjectHeader*)finalizableDependentChild)->IsMarked());
                gc_heap.mark_queue.verify_empty();
            };
            Assert.True(gc_heap.mark_phase_stack_roots());
            Assert.Equal(1, afterHandleScanObserverCallCount);
            GCToEEInterface.AfterGcScanRootsObserver = null;

            Assert.Equal((nuint)2, finalizeQueue->GetNumberFinalizableObjects());
            Assert.True(((CObjectHeader*)finalizableRoot)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)finalizableChild)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)pinnedHandleRoot)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)pinnedHandleChild)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)strongHandleRoot)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)strongHandleChild)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)dependentHandleRoot)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)dependentHandleChild)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)pinnedHandleRoot)->IsPinned() != 0);
            Assert.Equal((nuint)1, gc_heap.num_pinned_objects);
            Assert.Equal(0, ((CObjectHeader*)weakHandleRoot)->IsMarked());
            Assert.Equal((nuint)0, *(nuint*)shortWeak.Value);
            Assert.True(((CObjectHeader*)finalizableWeakRoot)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)finalizableWeakChild)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)finalizableDependentChild)->IsMarked() != 0);
            Assert.Equal((nuint)0, *(nuint*)finalizableShortWeak.Value);
            Assert.Equal((nuint)strongHandleRoot, *(nuint*)survivingLongWeak.Value);
            Assert.Equal((nuint)finalizableWeakRoot, *(nuint*)finalizableLongWeak.Value);
            Assert.Equal((nuint)0, *(nuint*)deadLongWeak.Value);
            Assert.Equal((nuint)0, *(nuint*)deadDependent.Value);
            Assert.True(HandleTableManager.GetDependentHandleSecondary(deadDependent) is null);
            Assert.Equal(1, GCToEEInterface.DiagWalkFReachableObjectsCallCount);
            Assert.True(GCToEEInterface.LastDiagWalkFReachableObjectsContext == heap);
            Assert.Equal(1, GCToEEInterface.SyncBlockCacheWeakPtrScanCallCount);
            Assert.NotEqual((nuint)0, GCToEEInterface.LastSyncBlockCacheWeakPtrScanCallback);
            Assert.Equal((nuint)0, (nuint)syncBlockWeakSlot);
            gc_heap.mark_queue.verify_empty();

            byte* overflowRoot = dependentHandleChild + (nint)objectSize;
            byte* overflowPrimary = overflowRoot + (nint)objectSize;
            byte* overflowSecondary = overflowPrimary + (nint)objectSize;
            ((CObjectHeader*)overflowRoot)->RawSetMethodTable(rootMethodTable);
            ((CObjectHeader*)overflowPrimary)->RawSetMethodTable(leafMethodTable);
            ((CObjectHeader*)overflowSecondary)->RawSetMethodTable(leafMethodTable);
            *((byte**)(overflowRoot + sizeof(nuint))) = overflowPrimary;
            heap_segment.heap_segment_allocated(gen0Segment) = overflowSecondary + (nint)objectSize;
            ((CObjectHeader*)overflowRoot)->SetMarked();

            _ = HandleTableManager.HndCreateHandle(
                table,
                (uint)HandleType.HNDTYPE_DEPENDENT,
                overflowPrimary,
                (nuint)(void*)overflowSecondary);

            ScanContext overflowScanContext = default;
            overflowScanContext.init();
            overflowScanContext.promotion = 1;
            GCScan.GcDhInitialScan(
                &gc_heap.promote,
                GCInterfaceOffsets.max_generation,
                GCInterfaceOffsets.max_generation,
                &overflowScanContext);
            Assert.True(GCScan.GcDhUnpromotedHandlesExist(&overflowScanContext));

#if DEBUG
            nuint promoted = 0;
            for (nuint regionIndex = 0; regionIndex < gc_heap.region_count; regionIndex++)
            {
                promoted += gc_heap.survived_per_region[(nint)regionIndex];
            }

            gc_heap.g_promoted = promoted;
#endif
            gc_heap.record_mark_stack_overflow(heap, overflowRoot);
            gc_heap.scan_dependent_handles(
                GCInterfaceOffsets.max_generation,
                &overflowScanContext,
                initial_scan_p: true);

            Assert.True(((CObjectHeader*)overflowPrimary)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)overflowSecondary)->IsMarked() != 0);
            Assert.False(GCScan.GcDhUnpromotedHandlesExist(&overflowScanContext));
            gc_heap.mark_queue.verify_empty();
        }
        finally
        {
            CFinalize.Free(finalizeQueue);
            gc_heap.finalize_queue = previousFinalizeQueue;

            if (handlesInitialized)
            {
                HandleTableBucket* bucket = (HandleTableBucket*)System.Runtime.CompilerServices.Unsafe.AsPointer(
                    ref ObjectHandle.g_GlobalHandleTableBucket);
                ObjectHandle.Ref_DestroyHandleTableBucket(bucket);
                ObjectHandle.Ref_Shutdown();
            }

            if (gc_heap.mark_stack_array is not null)
            {
                SyncImports.ManagedGC_Free(gc_heap.mark_stack_array);
                gc_heap.mark_stack_array = null;
                gc_heap.mark_stack_array_length = 0;
            }

            if (gc_heap.g_mark_list_piece is not null)
            {
                SyncImports.ManagedGC_Free(gc_heap.g_mark_list_piece);
                gc_heap.g_mark_list_piece = null;
            }

            if (gc_heap.g_mark_list is not null)
            {
                SyncImports.ManagedGC_Free(gc_heap.g_mark_list);
                gc_heap.g_mark_list = null;
            }

            ManagedGCRegionBootstrap.Shutdown();
            gc_heap.check_commit_cs.Destroy();
            GCToEEInterface.Reset();
            GCToOSInterface.ResetRecording();
            SyncImports.ResetRecording();
        }
    }

    private static void ScanStackRoot(byte** root, uint flags)
    {
        GCToEEInterface.GcScanRootsSlots.Clear();
        GCToEEInterface.GcScanRootsSlot = root;
        GCToEEInterface.GcScanRootsFlags = flags;

        Assert.True(gc_heap.mark_phase_stack_roots());
    }

    private static void ScanStackRoots()
    {
        GCToEEInterface.GcScanRootsSlot = null;
        GCToEEInterface.GcScanRootsFlags = 0;

        Assert.True(gc_heap.mark_phase_stack_roots());
    }

    private static void IgnoreFinalizableRoot(byte** obj, ScanContext* sc, uint flags)
    {
        _ = obj;
        _ = sc;
        _ = flags;
    }

    private static void AssertInitializedStackScanContext(ScanContext sc)
    {
        Assert.True(sc.thread_under_crawl is null);
        Assert.Equal(0, sc.thread_number);
        Assert.Equal(1, sc.thread_count);
        Assert.Equal((nuint)0, sc.stack_limit);
        Assert.Equal((byte)1, sc.promotion);
        Assert.Equal((byte)0, sc.concurrent);
        Assert.True(sc.pMD is null);
        Assert.Equal(EtwGCRootKind.kEtwGCRootKindOther, sc.dwEtwRootKind);
    }

    private static MethodTable* InitializeMethodTable(
        byte* descriptor,
        nuint objectSize,
        nuint pointerCount,
        int hasPointers)
    {
        MethodTable* methodTable = (MethodTable*)(descriptor + sizeof(nuint) + sizeof(CGCDescSeries));
        CGCDescSeries* series = (CGCDescSeries*)descriptor;

        *methodTable = default;
        methodTable->m_uFlags = hasPointers != 0 ? MethodTable.HasPointersFlag : 0;
        methodTable->m_uBaseSize = (uint)objectSize;
        *((nuint*)methodTable - 1) = 1;
        series->startoffset = (nuint)sizeof(nuint);
        series->seriessize = unchecked(
            (nuint)((nint)(pointerCount * (nuint)sizeof(byte*)) - (nint)objectSize));
        return methodTable;
    }

    private sealed unsafe class MarkPhaseStateScope : System.IDisposable
    {
        private readonly gc_mechanisms _settings = gc_heap.settings;
        private readonly mark_queue_t _markQueue = gc_heap.mark_queue;
        private readonly nuint _markStackTos = gc_heap.mark_stack_tos;
        private readonly nuint _markStackBos = gc_heap.mark_stack_bos;
        private readonly byte* _oldestPinnedPlug = gc_heap.oldest_pinned_plug;
        private readonly nuint _numPinnedObjects = gc_heap.num_pinned_objects;
        private readonly mark* _markStackArray = gc_heap.mark_stack_array;
        private readonly nuint _markStackArrayLength = gc_heap.mark_stack_array_length;
        private readonly byte* _minOverflowAddress = gc_heap.min_overflow_address;
        private readonly byte* _maxOverflowAddress = gc_heap.max_overflow_address;
        private readonly byte** _markList = gc_heap.mark_list;
        private readonly byte** _markListEnd = gc_heap.mark_list_end;
        private readonly byte** _markListIndex = gc_heap.mark_list_index;
        private readonly byte* _gcLow = gc_heap.gc_low;
        private readonly byte* _gcHigh = gc_heap.gc_high;
        private readonly byte* _slow = gc_heap.slow;
        private readonly byte* _shigh = gc_heap.shigh;
        private readonly nuint _regionCount = gc_heap.region_count;
        private readonly nuint* _survivedPerRegion = gc_heap.survived_per_region;
        private readonly nuint* _oldCardSurvivedPerRegion = gc_heap.old_card_survived_per_region;
        private readonly byte** _gMarkList = gc_heap.g_mark_list;
        private readonly byte** _gMarkListCopy = gc_heap.g_mark_list_copy;
        private readonly nuint _markListSize = gc_heap.mark_list_size;
        private readonly nuint _gMarkListTotalSize = gc_heap.g_mark_list_total_size;
        private readonly bool _markListOverflow = gc_heap.mark_list_overflow;
        private readonly byte*** _gMarkListPiece = gc_heap.g_mark_list_piece;
        private readonly nuint _gMarkListPieceSize = gc_heap.g_mark_list_piece_size;
        private readonly nuint _gMarkListPieceTotalSize = gc_heap.g_mark_list_piece_total_size;
        private readonly byte* _ephemeralLow = gc_heap.ephemeral_low;
        private readonly byte* _ephemeralHigh = gc_heap.ephemeral_high;
#if !MULTIPLE_HEAPS
        private readonly int _gen0BricksCleared = gc_heap.gen0_bricks_cleared;
        private readonly int _gen0MustClearBricks = gc_heap.gen0_must_clear_bricks;
#endif
        private readonly nuint _minSegmentSizeShr = gc_heap.min_segment_size_shr;
        private readonly region_allocator _globalRegionAllocator = gc_heap.global_region_allocator;
        private readonly byte** _initialRegions = gc_heap.initial_regions;
        private readonly GCSpinLock _gcLock = gc_heap.gc_lock;
        private readonly GCSpinLock _writeBarrierSpinLock = GCWriteBarrier.write_barrier_spin_lock;
        private readonly CLRCriticalSection _checkCommitCs = gc_heap.check_commit_cs;
#if DEBUG
        private readonly nuint _promoted = gc_heap.g_promoted;
#endif

        public MarkPhaseStateScope()
        {
            gc_heap.settings = default;
            gc_heap.check_commit_cs = default;
            gc_heap.initialize_mark_phase_state();
        }

        public void Dispose()
        {
            gc_heap.settings = _settings;
            gc_heap.mark_queue = _markQueue;
            gc_heap.mark_stack_tos = _markStackTos;
            gc_heap.mark_stack_bos = _markStackBos;
            gc_heap.oldest_pinned_plug = _oldestPinnedPlug;
            gc_heap.num_pinned_objects = _numPinnedObjects;
            gc_heap.mark_stack_array = _markStackArray;
            gc_heap.mark_stack_array_length = _markStackArrayLength;
            gc_heap.min_overflow_address = _minOverflowAddress;
            gc_heap.max_overflow_address = _maxOverflowAddress;
            gc_heap.mark_list = _markList;
            gc_heap.mark_list_end = _markListEnd;
            gc_heap.mark_list_index = _markListIndex;
            gc_heap.gc_low = _gcLow;
            gc_heap.gc_high = _gcHigh;
            gc_heap.slow = _slow;
            gc_heap.shigh = _shigh;
            gc_heap.region_count = _regionCount;
            gc_heap.survived_per_region = _survivedPerRegion;
            gc_heap.old_card_survived_per_region = _oldCardSurvivedPerRegion;
            gc_heap.g_mark_list = _gMarkList;
            gc_heap.g_mark_list_copy = _gMarkListCopy;
            gc_heap.mark_list_size = _markListSize;
            gc_heap.g_mark_list_total_size = _gMarkListTotalSize;
            gc_heap.mark_list_overflow = _markListOverflow;
            gc_heap.g_mark_list_piece = _gMarkListPiece;
            gc_heap.g_mark_list_piece_size = _gMarkListPieceSize;
            gc_heap.g_mark_list_piece_total_size = _gMarkListPieceTotalSize;
            gc_heap.ephemeral_low = _ephemeralLow;
            gc_heap.ephemeral_high = _ephemeralHigh;
#if !MULTIPLE_HEAPS
            gc_heap.gen0_bricks_cleared = _gen0BricksCleared;
            gc_heap.gen0_must_clear_bricks = _gen0MustClearBricks;
#endif
            gc_heap.min_segment_size_shr = _minSegmentSizeShr;
            gc_heap.global_region_allocator = _globalRegionAllocator;
            gc_heap.initial_regions = _initialRegions;
            gc_heap.gc_lock = _gcLock;
            GCWriteBarrier.write_barrier_spin_lock = _writeBarrierSpinLock;
            gc_heap.check_commit_cs = _checkCommitCs;
#if DEBUG
            gc_heap.g_promoted = _promoted;
#endif
        }
    }
#endif
}
