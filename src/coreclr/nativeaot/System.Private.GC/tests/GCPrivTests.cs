// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
#if USE_REGIONS
using allocation_callback_result = Internal.Runtime.GarbageCollection.gc_heap.allocation_callback_result;
using allocation_callback_result_kind = Internal.Runtime.GarbageCollection.gc_heap.allocation_callback_result_kind;
using allocation_deferred_operation = Internal.Runtime.GarbageCollection.gc_heap.allocation_deferred_operation;
using try_allocate_more_space_context = Internal.Runtime.GarbageCollection.gc_heap.try_allocate_more_space_context;
#endif
using SysInterlocked = System.Threading.Interlocked;
using SysVolatile = System.Threading.Volatile;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

[Collection(SyncImportsCollection.Name)]
public sealed unsafe class GCPrivTests
{
#if USE_REGIONS
    private static int s_regionAllocatorCallbackCount;
    private static nuint s_regionAllocatorCallbackLastLeftUsed;
    private static int s_allocationCallbackCount;
    private static allocation_deferred_operation s_lastAllocationDeferredOperation;
    private static int s_backgroundQueryCallbackCount;
    private static int s_budgetCheckCallbackCount;
    private static int s_highMemoryCallbackCount;
    private static int s_budgetTriggerCallbackCount;
    private static int s_fullGcCheckCallbackCount;
    private static int s_allocateMoreSpaceEnterCount;
    private static int s_allocateMoreSpaceLeaveCount;
    private static allocation_state s_allocateMoreSpaceRetryState;
    private static oom_reason s_allocateMoreSpaceRetryOomReason;
    private static int s_allocateMoreSpaceGeneration;
    private static int s_allocateMoreSpaceAlignment;
    private static heap_segment* s_adjustLimitSegment;
    private static byte* s_adjustLimitExpectedUsed;
    private static int s_adjustLimitUsedPublishedAtRelease;
#endif
    private static int s_finalizationScanCount;
    private static byte s_finalizationScanPromotion;
    private static int s_finalizationScanThreadCount;

    [Fact]
    public void GcRandPreservesNativeSequence()
    {
        gc_rand.x = 0;

        Assert.Equal(278281UL, gc_rand.get_rand());
        Assert.Equal(496504790UL, gc_rand.get_rand());
        Assert.Equal(462394359UL, gc_rand.get_rand());
        Assert.Equal(1153920316UL, gc_rand.get_rand());
        Assert.Equal(402843317UL, gc_rand.get_rand());
    }

    [Fact]
    public void GcRandBoundedScalingPreservesNativeSequence()
    {
        gc_rand.x = 0;

        Assert.Equal(0UL, gc_rand.get_rand(10));
        Assert.Equal(2UL, gc_rand.get_rand(10));
        Assert.Equal(2UL, gc_rand.get_rand(10));
        Assert.Equal(5UL, gc_rand.get_rand(10));
        Assert.Equal(1UL, gc_rand.get_rand(10));
    }

    [Fact]
    public void GcRandConstantsMatchNativeValues()
    {
        Assert.Equal(32768u, gc_rand.MAX_YP_SPIN_COUNT_UNIT);
        Assert.Equal(400u, gc_rand.MIN_SOH_CROSS_GEN_REFS);
        Assert.Equal(800u, gc_rand.MIN_LOH_CROSS_GEN_REFS);
        Assert.Equal(100 * GCToOSInterface.GetPageSize(), gc_rand.MIN_DECOMMIT_SIZE);
#if TARGET_64BIT
        Assert.Equal(1024u, gc_rand.MARK_STACK_INITIAL_LENGTH);
#else
        Assert.Equal(128u, gc_rand.MARK_STACK_INITIAL_LENGTH);
#endif
    }

    [Fact]
    public void CFinalizeLifecycleGrowsAndDequeuesInReverseRegistrationOrder()
    {
        const int FinalizerCount = 101;
        SyncImports.ResetRecording();
        GCToEEInterface.Reset();

        CFinalize* queue = CFinalize.Allocate();
        byte* objects = null;
        try
        {
            Assert.True(queue is not null);

            MethodTable methodTable = default;
            methodTable.m_uFlags = MethodTable.HasFinalizerFlag;
            objects = AllocateFinalizableObjects(&methodTable, FinalizerCount);
            Assert.True(objects is not null);

            for (int i = 0; i < FinalizerCount; i++)
            {
                Assert.True(queue->RegisterForFinalization((int)gc_generation_num.soh_gen0, FinalizableObjectAt(objects, i)));
            }

            Assert.Equal(0u, queue->GetNumberFinalizableObjects());
            Assert.Equal((nuint)(120 * sizeof(byte*)), SyncImports.LastAllocSize);

            ResetFinalizationScanObservation();
            Assert.True(queue->ScanForFinalization(&MarkFinalizable, (int)gc_generation_num.soh_gen2, null));
            Assert.Equal(FinalizerCount, s_finalizationScanCount);
            Assert.Equal((byte)0, s_finalizationScanPromotion);
            Assert.Equal(0, s_finalizationScanThreadCount);
            Assert.Equal((nuint)FinalizerCount, queue->GetPromotedCount());
            Assert.Equal((nuint)FinalizerCount, queue->GetNumberFinalizableObjects());

            for (int i = 0; i < FinalizerCount; i++)
            {
                Assert.Equal(
                    (nuint)FinalizableObjectAt(objects, FinalizerCount - i - 1),
                    (nuint)queue->GetNextFinalizableObject());
            }

            Assert.True(queue->GetNextFinalizableObject() is null);
            Assert.Equal(0u, queue->GetNumberFinalizableObjects());
        }
        finally
        {
            if (objects is not null)
            {
                SyncImports.ManagedGC_Free(objects);
            }

            CFinalize.Free(queue);
            GCToEEInterface.Reset();
        }

        Assert.Equal(SyncImports.AllocCount, SyncImports.FreeCount);
    }

    [Fact]
    public void CFinalizeDropsEagerAndPreviouslyFinalizedObjectsBeforeReregistration()
    {
        SyncImports.ResetRecording();
        GCToEEInterface.Reset();

        CFinalize* queue = CFinalize.Allocate();
        byte* objects = null;
        try
        {
            Assert.True(queue is not null);

            MethodTable methodTable = default;
            methodTable.m_uFlags = MethodTable.HasFinalizerFlag;
            objects = AllocateFinalizableObjects(&methodTable, 1);
            byte* obj = FinalizableObjectAt(objects, 0);

            GCToEEInterface.EagerFinalizedObject = obj;
            Assert.True(queue->RegisterForFinalization((int)gc_generation_num.soh_gen0, obj));
            Assert.False(queue->ScanForFinalization(&MarkFinalizable, (int)gc_generation_num.soh_gen0, null));
            Assert.Equal(0u, queue->GetNumberFinalizableObjects());
            Assert.Equal(0u, queue->GetPromotedCount());

            GCToEEInterface.EagerFinalizedObject = null;
            Assert.True(queue->RegisterForFinalization((int)gc_generation_num.soh_gen0, obj));
            ((CObjectHeader*)obj)->GetHeader()->SetFinalizerRun();
            Assert.False(queue->ScanForFinalization(&MarkFinalizable, (int)gc_generation_num.soh_gen0, null));
            Assert.Equal(0u, ((CObjectHeader*)obj)->GetHeader()->GetBits() & ObjHeader.BIT_SBLK_FINALIZER_RUN);

            Assert.True(queue->RegisterForFinalization((int)gc_generation_num.soh_gen0, obj));
            ResetFinalizationScanObservation();
            Assert.True(queue->ScanForFinalization(&MarkFinalizable, (int)gc_generation_num.soh_gen0, null));
            Assert.Equal(1, s_finalizationScanCount);
            Assert.Equal((nuint)1, queue->GetPromotedCount());
            Assert.Equal((nuint)obj, (nuint)queue->GetNextFinalizableObject());
            Assert.True(queue->GetNextFinalizableObject() is null);
        }
        finally
        {
            GCToEEInterface.EagerFinalizedObject = null;
            if (objects is not null)
            {
                SyncImports.ManagedGC_Free(objects);
            }

            CFinalize.Free(queue);
            GCToEEInterface.Reset();
        }
    }

    [Fact]
    public void CFinalizePromotesOnlyDeadObjectsAndSeparatesCriticalFinalizers()
    {
        SyncImports.ResetRecording();
        GCToEEInterface.Reset();

        CFinalize* queue = CFinalize.Allocate();
        byte* markedObjects = null;
        byte* normalObjects = null;
        byte* criticalObjects = null;
        try
        {
            Assert.True(queue is not null);

            MethodTable markedMethodTable = default;
            markedMethodTable.m_uFlags = MethodTable.HasFinalizerFlag;
            MethodTable normalMethodTable = default;
            normalMethodTable.m_uFlags = MethodTable.HasFinalizerFlag;
            MethodTable criticalMethodTable = default;
            criticalMethodTable.m_uFlags = MethodTable.HasFinalizerFlag | MethodTable.HasCriticalFinalizerFlag;

            markedObjects = AllocateFinalizableObjects(&markedMethodTable, 1);
            normalObjects = AllocateFinalizableObjects(&normalMethodTable, 1);
            criticalObjects = AllocateFinalizableObjects(&criticalMethodTable, 1);
            byte* marked = FinalizableObjectAt(markedObjects, 0);
            byte* normal = FinalizableObjectAt(normalObjects, 0);
            byte* critical = FinalizableObjectAt(criticalObjects, 0);
            ((CObjectHeader*)marked)->SetMarked();

            Assert.True(queue->RegisterForFinalization((int)gc_generation_num.soh_gen0, marked));
            Assert.True(queue->RegisterForFinalization((int)gc_generation_num.soh_gen0, normal));
            Assert.True(queue->RegisterForFinalization((int)gc_generation_num.soh_gen1, critical));

            ResetFinalizationScanObservation();
            Assert.True(queue->ScanForFinalization(&MarkFinalizable, (int)gc_generation_num.soh_gen1, null));
            Assert.Equal(2, s_finalizationScanCount);
            Assert.Equal((nuint)2, queue->GetPromotedCount());
            Assert.Equal((nuint)2, queue->GetNumberFinalizableObjects());

            ScanContext scanContext = default;
            ResetFinalizationScanObservation();
            queue->GcScanRoots(&MarkFinalizable, heapNumber: 7, scanContext: &scanContext);
            Assert.Equal(2, s_finalizationScanCount);
            Assert.Equal(7, scanContext.thread_number);

            Assert.Equal((nuint)normal, (nuint)queue->GetNextFinalizableObject(only_non_critical: true));
            Assert.True(queue->GetNextFinalizableObject(only_non_critical: true) is null);
            Assert.Equal((nuint)critical, (nuint)queue->GetNextFinalizableObject());

            ((CObjectHeader*)marked)->ClearMarked();
            ResetFinalizationScanObservation();
            Assert.True(queue->ScanForFinalization(&MarkFinalizable, (int)gc_generation_num.soh_gen0, null));
            Assert.Equal(1, s_finalizationScanCount);
            Assert.Equal((nuint)1, queue->GetPromotedCount());
            Assert.Equal((nuint)marked, (nuint)queue->GetNextFinalizableObject());
        }
        finally
        {
            if (criticalObjects is not null)
            {
                SyncImports.ManagedGC_Free(criticalObjects);
            }

            if (normalObjects is not null)
            {
                SyncImports.ManagedGC_Free(normalObjects);
            }

            if (markedObjects is not null)
            {
                SyncImports.ManagedGC_Free(markedObjects);
            }

            CFinalize.Free(queue);
            GCToEEInterface.Reset();
        }
    }

    private static byte* AllocateFinalizableObjects(MethodTable* methodTable, int count)
    {
        nuint stride = (nuint)sizeof(ObjHeader) + (nuint)sizeof(CObjectHeader);
        byte* storage = (byte*)SyncImports.ManagedGC_AllocZeroed((nuint)count * stride);
        if (storage is not null)
        {
            for (int i = 0; i < count; i++)
            {
                ((CObjectHeader*)FinalizableObjectAt(storage, i))->RawSetMethodTable(methodTable);
            }
        }

        return storage;
    }

    private static byte* FinalizableObjectAt(byte* storage, int index)
    {
        nuint stride = (nuint)sizeof(ObjHeader) + (nuint)sizeof(CObjectHeader);
        return storage + (nint)((nuint)index * stride) + sizeof(ObjHeader);
    }

    private static void ResetFinalizationScanObservation()
    {
        s_finalizationScanCount = 0;
        s_finalizationScanPromotion = 0;
        s_finalizationScanThreadCount = 0;
    }

    private static void MarkFinalizable(byte** obj, ScanContext* scanContext, uint flags)
    {
        _ = flags;
        s_finalizationScanCount++;
        s_finalizationScanPromotion = scanContext->promotion;
        s_finalizationScanThreadCount = scanContext->thread_count;
        ((CObjectHeader*)*obj)->SetMarked();
    }

#if USE_REGIONS && !MULTIPLE_HEAPS
    [Fact]
    public void InitRecordsClearsHistoryAndSnapshotsGenerationData()
    {
        using InitRecordsStateScope _ = new();
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        heap_segment[] regions = new heap_segment[(int)gc_generation_num.total_generation_count];
        generation* generationTable = gc_heap.generation_table_of(pHeap);
        nuint* expectedSizes = stackalloc nuint[(int)gc_generation_num.total_generation_count];
        nuint* expectedFreeListSpaces = stackalloc nuint[(int)gc_generation_num.total_generation_count];
        nuint* expectedFreeObjSpaces = stackalloc nuint[(int)gc_generation_num.total_generation_count];

        fixed (heap_segment* pRegions = regions)
        {
            pHeap->heap_number = 0;
            for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
            {
                heap_segment* region = &pRegions[i];
                nuint start = unchecked((nuint)0x100000 + ((nuint)i * 0x1000));
                expectedSizes[i] = unchecked((nuint)(i + 1) * 0x10);
                expectedFreeListSpaces[i] = unchecked((nuint)0x100 + (nuint)i);
                expectedFreeObjSpaces[i] = unchecked((nuint)0x200 + (nuint)i);

                heap_segment.heap_segment_mem(region) = (byte*)start;
                heap_segment.heap_segment_allocated(region) = (byte*)(start + expectedSizes[i]);
                heap_segment.heap_segment_next(region) = null;

                generation* gen = generationTable + i;
                generation.generation_start_segment(gen) = region;
                generation.generation_free_list_space(gen) = expectedFreeListSpaces[i];
                generation.generation_free_obj_space(gen) = expectedFreeObjSpaces[i];
            }

            gc_heap.gc_data_per_heap.gen_data0.size_after = 1;
            gc_heap.gc_data_per_heap.maxgen_size_info.free_list_allocated = 2;
            gc_heap.gc_data_per_heap.extra_gen0_committed = 3;
            gc_heap.gc_data_global.final_youngest_desired = 4;
            gc_heap.gc_data_global.condemned_generation = 5;
            gc_heap.fgm_result.set_fgm(failure_get_memory.fgm_commit_table, 6, 1);
            gc_heap.fgm_result.available_pagefile_mb = 7;
            gc_heap.end_gen0_region_space = 8;
            gc_heap.end_gen0_region_committed_space = 9;
            gc_heap.gen0_pinned_free_space = 10;
            gc_heap.gen0_large_chunk_found = true;
            gc_heap.num_regions_freed_in_sweep = 11;
            gc_heap.sufficient_gen0_space_p = 1;

            gc_heap.init_records(pHeap);
        }

        Assert.Equal(0u, gc_heap.gc_data_per_heap.heap_index);
        Assert.Equal((nuint)0, gc_heap.gc_data_per_heap.maxgen_size_info.free_list_allocated);
        Assert.Equal((nuint)0, gc_heap.gc_data_per_heap.extra_gen0_committed);
        Assert.Equal((nuint)0, gc_heap.gc_data_global.final_youngest_desired);
        Assert.Equal(0, gc_heap.gc_data_global.condemned_generation);
        Assert.Equal(failure_get_memory.fgm_no_failure, gc_heap.fgm_result.fgm);
        Assert.Equal((nuint)0, gc_heap.fgm_result.size);
        Assert.Equal((nuint)0, gc_heap.fgm_result.available_pagefile_mb);
        Assert.Equal(0, gc_heap.fgm_result.loh_p);

        gc_history_per_heap history = gc_heap.gc_data_per_heap;
        gc_generation_data* genData = &history.gen_data0;
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            Assert.Equal(expectedSizes[i], genData[i].size_before);
            Assert.Equal(expectedFreeListSpaces[i], genData[i].free_list_space_before);
            Assert.Equal(expectedFreeObjSpaces[i], genData[i].free_obj_space_before);
            Assert.Equal((nuint)0, genData[i].size_after);
        }

        Assert.Equal(nuint.MaxValue, gc_heap.end_gen0_region_space);
        Assert.Equal((nuint)0, gc_heap.end_gen0_region_committed_space);
        Assert.Equal((nuint)0, gc_heap.gen0_pinned_free_space);
        Assert.False(gc_heap.gen0_large_chunk_found);
        Assert.Equal(0, gc_heap.num_regions_freed_in_sweep);
        Assert.Equal(0, gc_heap.sufficient_gen0_space_p);
    }
#endif

    [Fact]
    public void DynamicTuningSurvivalGrowthUsesNativeThresholdAndCap()
    {
        const float Limit = 2.0f;
        const float MaxLimit = 4.0f;
        float threshold = (MaxLimit - Limit) / (Limit * (MaxLimit - 1.0f));

        Assert.Equal(3.0f, gc_heap.surv_to_growth(0.25f, Limit, MaxLimit));
        Assert.Equal(MaxLimit, gc_heap.surv_to_growth(threshold, Limit, MaxLimit));
        Assert.Equal(MaxLimit, gc_heap.surv_to_growth(0.5f, Limit, MaxLimit));
    }

    [Fact]
    public void DynamicTuningLinearAllocationModelPreservesNativeBoundariesAndTruncation()
    {
        Assert.Equal((nuint)1000, LinearAllocationModel(0.0f, 1000, 2000, 0.0f));
        Assert.Equal((nuint)1050, LinearAllocationModel(0.95f, 1000, 2000, 0.0f));
        Assert.Equal((nuint)1250, LinearAllocationModel(0.5f, 1000, 2000, 150.0f));
        Assert.Equal((nuint)1000, LinearAllocationModel(0.5f, 1000, 2000, 300.0f));
        Assert.Equal((nuint)100, LinearAllocationModel(0.5f, 100, 101, 0.0f));
    }

#if !MULTIPLE_HEAPS
    [Fact]
    public void DynamicTuningUpdateCollectionCountsPreservesWksAccounting()
    {
        gc_mechanisms savedSettings = gc_heap.settings;

        try
        {
            GCToOSInterface.ResetTimerRecording();
            GCCommon.ResetHighPrecisionTimeStamp();
#if TARGET_WINDOWS
            GCToOSInterface.PerformanceFrequencyInject = true;
            GCToOSInterface.PerformanceFrequencyValue = 1_000_000;
            GCToOSInterface.PerformanceCounterInject = true;
            GCToOSInterface.PerformanceCounterValue = 777;
#else
            GCToOSInterface.HiresTickFrequencyInject = true;
            GCToOSInterface.HiresTickFrequencyValue = 1_000_000;
            GCToOSInterface.HiresTicksInject = true;
            GCToOSInterface.HiresTicksValue = 777;
#endif

            gc_heap heap = default;
            gc_heap* pHeap = &heap;
            dynamic_data* gen0 = gc_heap.dynamic_data_of(pHeap, (int)gc_generation_num.soh_gen0);
            dynamic_data* gen1 = gc_heap.dynamic_data_of(pHeap, (int)gc_generation_num.soh_gen1);
            dynamic_data* gen2 = gc_heap.dynamic_data_of(pHeap, (int)gc_generation_num.soh_gen2);
            dynamic_data* loh = gc_heap.dynamic_data_of(pHeap, (int)gc_generation_num.loh_generation);
            dynamic_data* poh = gc_heap.dynamic_data_of(pHeap, (int)gc_generation_num.poh_generation);

            dynamic_data.dd_collection_count(gen0) = 10;
            dynamic_data.dd_collection_count(gen1) = 20;
            dynamic_data.dd_collection_count(gen2) = 30;
            dynamic_data.dd_collection_count(loh) = 40;
            dynamic_data.dd_collection_count(poh) = 50;
            dynamic_data.dd_gc_clock(gen0) = 40;
            dynamic_data.dd_gc_clock(gen1) = 50;
            dynamic_data.dd_gc_clock(gen2) = 60;
            dynamic_data.dd_gc_clock(loh) = 70;
            dynamic_data.dd_gc_clock(poh) = 80;
            dynamic_data.dd_time_clock(gen0) = 100;
            dynamic_data.dd_time_clock(gen1) = 101;
            dynamic_data.dd_time_clock(gen2) = 102;
            dynamic_data.dd_time_clock(loh) = 103;
            dynamic_data.dd_time_clock(poh) = 104;
            dynamic_data.dd_previous_time_clock(gen0) = 110;
            dynamic_data.dd_previous_time_clock(gen1) = 111;
            dynamic_data.dd_previous_time_clock(gen2) = 112;
            dynamic_data.dd_previous_time_clock(loh) = 113;
            dynamic_data.dd_previous_time_clock(poh) = 114;

            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen1;
            gc_heap.update_collection_counts(pHeap);

            Assert.Equal((nuint)11, dynamic_data.dd_collection_count(gen0));
            Assert.Equal((nuint)21, dynamic_data.dd_collection_count(gen1));
            Assert.Equal((nuint)30, dynamic_data.dd_collection_count(gen2));
            Assert.Equal((nuint)40, dynamic_data.dd_collection_count(loh));
            Assert.Equal((nuint)50, dynamic_data.dd_collection_count(poh));
            Assert.Equal((nuint)41, dynamic_data.dd_gc_clock(gen0));
            Assert.Equal((nuint)41, dynamic_data.dd_gc_clock(gen1));
            Assert.Equal((nuint)60, dynamic_data.dd_gc_clock(gen2));
            Assert.Equal((nuint)70, dynamic_data.dd_gc_clock(loh));
            Assert.Equal((nuint)80, dynamic_data.dd_gc_clock(poh));
            Assert.Equal(777UL, dynamic_data.dd_time_clock(gen0));
            Assert.Equal(777UL, dynamic_data.dd_time_clock(gen1));
            Assert.Equal(102UL, dynamic_data.dd_time_clock(gen2));
            Assert.Equal(103UL, dynamic_data.dd_time_clock(loh));
            Assert.Equal(104UL, dynamic_data.dd_time_clock(poh));
            Assert.Equal(100UL, dynamic_data.dd_previous_time_clock(gen0));
            Assert.Equal(101UL, dynamic_data.dd_previous_time_clock(gen1));
            Assert.Equal(112UL, dynamic_data.dd_previous_time_clock(gen2));
            Assert.Equal(113UL, dynamic_data.dd_previous_time_clock(loh));
            Assert.Equal(114UL, dynamic_data.dd_previous_time_clock(poh));

#if TARGET_WINDOWS
            GCToOSInterface.PerformanceCounterValue = 888;
#else
            GCToOSInterface.HiresTicksValue = 888;
#endif
            gc_heap.settings.condemned_generation = (int)gc_generation_num.max_generation;
            gc_heap.update_collection_counts(pHeap);

            Assert.Equal((nuint)12, dynamic_data.dd_collection_count(gen0));
            Assert.Equal((nuint)22, dynamic_data.dd_collection_count(gen1));
            Assert.Equal((nuint)31, dynamic_data.dd_collection_count(gen2));
            Assert.Equal((nuint)41, dynamic_data.dd_collection_count(loh));
            Assert.Equal((nuint)51, dynamic_data.dd_collection_count(poh));
            Assert.Equal((nuint)42, dynamic_data.dd_gc_clock(gen0));
            Assert.Equal((nuint)42, dynamic_data.dd_gc_clock(gen1));
            Assert.Equal((nuint)42, dynamic_data.dd_gc_clock(gen2));
            Assert.Equal((nuint)70, dynamic_data.dd_gc_clock(loh));
            Assert.Equal((nuint)80, dynamic_data.dd_gc_clock(poh));
            Assert.Equal(888UL, dynamic_data.dd_time_clock(gen0));
            Assert.Equal(888UL, dynamic_data.dd_time_clock(gen1));
            Assert.Equal(888UL, dynamic_data.dd_time_clock(gen2));
            Assert.Equal(103UL, dynamic_data.dd_time_clock(loh));
            Assert.Equal(104UL, dynamic_data.dd_time_clock(poh));
            Assert.Equal(777UL, dynamic_data.dd_previous_time_clock(gen0));
            Assert.Equal(777UL, dynamic_data.dd_previous_time_clock(gen1));
            Assert.Equal(102UL, dynamic_data.dd_previous_time_clock(gen2));
            Assert.Equal(113UL, dynamic_data.dd_previous_time_clock(loh));
            Assert.Equal(114UL, dynamic_data.dd_previous_time_clock(poh));
        }
        finally
        {
            gc_heap.settings = savedSettings;
            GCToOSInterface.ResetTimerRecording();
            GCCommon.ResetHighPrecisionTimeStamp();
        }
    }
#endif

#if !MULTIPLE_HEAPS
    [Fact]
    public void DynamicTuningUpdateEndTimePreservesWksAccounting()
    {
        gc_mechanisms savedSettings = gc_heap.settings;
        ulong savedEndGcTime = gc_heap.end_gc_time;
        ulong savedLastAllocResetSuspendedEndTime = gc_heap.last_alloc_reset_suspended_end_time;

        try
        {
            GCToOSInterface.ResetTimerRecording();
            GCCommon.ResetHighPrecisionTimeStamp();
#if TARGET_WINDOWS
            GCToOSInterface.PerformanceFrequencyInject = true;
            GCToOSInterface.PerformanceFrequencyValue = 1_000_000;
            GCToOSInterface.PerformanceCounterInject = true;
            GCToOSInterface.PerformanceCounterValue = 1_000;
#else
            GCToOSInterface.HiresTickFrequencyInject = true;
            GCToOSInterface.HiresTickFrequencyValue = 1_000_000;
            GCToOSInterface.HiresTicksInject = true;
            GCToOSInterface.HiresTicksValue = 1_000;
#endif

            gc_heap heap = default;
            gc_heap* pHeap = &heap;
            dynamic_data* gen0 = gc_heap.dynamic_data_of(pHeap, (int)gc_generation_num.soh_gen0);
            dynamic_data* gen1 = gc_heap.dynamic_data_of(pHeap, (int)gc_generation_num.soh_gen1);
            dynamic_data* gen2 = gc_heap.dynamic_data_of(pHeap, (int)gc_generation_num.soh_gen2);
            dynamic_data.dd_time_clock(gen0) = 1_050;
            dynamic_data.dd_time_clock(gen1) = 800;
            dynamic_data.dd_gc_elapsed_time(gen0) = 10;
            dynamic_data.dd_gc_elapsed_time(gen1) = 20;
            dynamic_data.dd_gc_elapsed_time(gen2) = 30;

            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen1;
            gc_heap.update_end_ngc_time();
            gc_heap.update_end_gc_time_per_heap(pHeap);

            Assert.Equal(1_000UL, gc_heap.end_gc_time);
            Assert.Equal(1_000UL, gc_heap.last_alloc_reset_suspended_end_time);
            Assert.Equal(unchecked((nuint)(1_000UL - 1_050UL)), dynamic_data.dd_gc_elapsed_time(gen0));
            Assert.Equal((nuint)200, dynamic_data.dd_gc_elapsed_time(gen1));
            Assert.Equal((nuint)30, dynamic_data.dd_gc_elapsed_time(gen2));
        }
        finally
        {
            gc_heap.settings = savedSettings;
            gc_heap.end_gc_time = savedEndGcTime;
            gc_heap.last_alloc_reset_suspended_end_time = savedLastAllocResetSuspendedEndTime;
            GCToOSInterface.ResetTimerRecording();
            GCCommon.ResetHighPrecisionTimeStamp();
        }
    }
#endif

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    [InlineData(9, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(12, false)]
    [InlineData(13, true)]
    [InlineData(14, false)]
    [InlineData(15, false)]
    [InlineData(16, false)]
    [InlineData(17, true)]
    [InlineData(18, false)]
    public void PlanPhaseIsInducedBlockingPreservesNativeReasons(int reason, bool expected)
    {
        Assert.Equal(expected, gc_heap.is_induced_blocking((gc_reason)reason));
    }

    [Fact]
    public void PlanPhaseRelativePowerOfTwoIndicesPreserveNativeBoundaries()
    {
#if TARGET_64BIT
        const int ExpectedMaxIndexPower2 = 28;
#else
        const int ExpectedMaxIndexPower2 = 24;
#endif
        nuint belowMinimum = ((nuint)1 << gc_heap.MIN_INDEX_POWER2) - 1;
        nuint minimum = (nuint)1 << gc_heap.MIN_INDEX_POWER2;
        nuint maximum = (nuint)1 << gc_heap.MAX_INDEX_POWER2;
        nuint belowMaximum = maximum - 1;

        Assert.Equal(6, gc_heap.MIN_INDEX_POWER2);
        Assert.Equal(ExpectedMaxIndexPower2, gc_heap.MAX_INDEX_POWER2);
        Assert.Equal(ExpectedMaxIndexPower2 - 6 + 1, gc_heap.MAX_NUM_BUCKETS);

        Assert.Equal(0, gc_heap.relative_index_power2_plug(0));
        Assert.Equal(-1, gc_heap.relative_index_power2_free_space(0));
        Assert.Equal(0, gc_heap.relative_index_power2_plug(belowMinimum));
        Assert.Equal(-1, gc_heap.relative_index_power2_free_space(belowMinimum));
        Assert.Equal(0, gc_heap.relative_index_power2_plug(minimum));
        Assert.Equal(0, gc_heap.relative_index_power2_free_space(minimum));
        Assert.Equal(gc_heap.MAX_NUM_BUCKETS - 2, gc_heap.relative_index_power2_plug(belowMaximum));
        Assert.Equal(gc_heap.MAX_NUM_BUCKETS - 2, gc_heap.relative_index_power2_free_space(belowMaximum));
        Assert.Equal(gc_heap.MAX_NUM_BUCKETS - 1, gc_heap.relative_index_power2_plug(maximum));
        Assert.Equal(gc_heap.MAX_NUM_BUCKETS - 1, gc_heap.relative_index_power2_free_space(maximum));
    }

    [Theory]
    [InlineData(0UL, false)]
    [InlineData(1UL, true)]
    [InlineData(2UL, false)]
    [InlineData(3UL, true)]
    [InlineData(65534UL, false)]
    [InlineData(65535UL, true)]
    public void PlanPhaseOddpPreservesNativeParity(ulong value, bool expected)
    {
        Assert.Equal(expected, gc_heap.oddp((nuint)value));
    }

    [Fact]
    public void PlanPhaseLogcountMatchesReferenceForEveryWord()
    {
        for (nuint word = 0; word < 0x10000; word++)
        {
            Assert.Equal(LogcountReference(word), gc_heap.logcount(word));
        }
    }

#if !MULTIPLE_HEAPS
    [Fact]
    public void PlanPhaseCurrentGenerationSizeUsesWksDynamicData()
    {
        gc_heap heap = default;
        dynamic_data* data = gc_heap.dynamic_data_of(&heap, (int)gc_generation_num.soh_gen1);

        dynamic_data.dd_current_size(data) = 1000;
        dynamic_data.dd_desired_allocation(data) = 200;
        dynamic_data.dd_new_allocation(data) = 50;

        Assert.Equal((nuint)1150, gc_heap.current_generation_size(&heap, (int)gc_generation_num.soh_gen1));
        Assert.Equal((nuint)1000, dynamic_data.dd_current_size(data));
        Assert.Equal((nuint)200, dynamic_data.dd_desired_allocation(data));
        Assert.Equal((nint)50, dynamic_data.dd_new_allocation(data));

        dynamic_data.dd_current_size(data) = nuint.MaxValue;
        dynamic_data.dd_desired_allocation(data) = 2;
        dynamic_data.dd_new_allocation(data) = 1;

        Assert.Equal((nuint)0, gc_heap.current_generation_size(&heap, (int)gc_generation_num.soh_gen1));
    }
#endif

#if USE_REGIONS && !MULTIPLE_HEAPS
    [Fact]
    public void PlanPhaseGenerationSizesSumLinkedRegionsAndReturnZeroForNullStart()
    {
        using PlanPhaseStateScope _ = new();
        gc_heap heap = default;
        heap_segment* regions = stackalloc heap_segment[3];
        generation* generationTable = gc_heap.generation_table_of(&heap);

        SetGenerationSizeRegion(&regions[0], mem: 0x1000, planAllocated: 0x1200, allocated: 0x1300, next: &regions[1]);
        SetGenerationSizeRegion(&regions[1], mem: 0x2000, planAllocated: 0x2400, allocated: 0x2800, next: &regions[2]);
        SetGenerationSizeRegion(&regions[2], mem: 0x3000, planAllocated: 0x3400, allocated: 0x3500, next: null);
        generation.generation_start_segment(
            generationTable + (int)gc_generation_num.soh_gen1) = &regions[0];

        Assert.Equal((nuint)0xA00, gc_heap.generation_plan_size(&heap, (int)gc_generation_num.soh_gen1));
        Assert.Equal((nuint)0x1000, gc_heap.generation_size(&heap, (int)gc_generation_num.soh_gen1));

        generation.generation_start_segment(
            generationTable + (int)gc_generation_num.soh_gen1) = null;

        Assert.Equal((nuint)0, gc_heap.generation_plan_size(&heap, (int)gc_generation_num.soh_gen1));
        Assert.Equal((nuint)0, gc_heap.generation_size(&heap, (int)gc_generation_num.soh_gen1));
    }

    [Fact]
    public void PlanPhaseAllocationAndPromotionTotalsUseWksDynamicData()
    {
        gc_mechanisms expectedSettings = gc_heap.settings;
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        dynamic_data* gen0 = gc_heap.dynamic_data_of(pHeap, (int)gc_generation_num.soh_gen0);
        dynamic_data* gen1 = gc_heap.dynamic_data_of(pHeap, (int)gc_generation_num.soh_gen1);
        dynamic_data* gen2 = gc_heap.dynamic_data_of(pHeap, (int)gc_generation_num.soh_gen2);
        dynamic_data* loh = gc_heap.dynamic_data_of(pHeap, (int)gc_generation_num.loh_generation);
        dynamic_data* poh = gc_heap.dynamic_data_of(pHeap, (int)gc_generation_num.poh_generation);

        using (new PlanPhaseStateScope())
        {
            dynamic_data.dd_desired_allocation(gen0) = 1000;
            dynamic_data.dd_new_allocation(gen0) = 100;
            dynamic_data.dd_promoted_size(gen0) = 11;
            dynamic_data.dd_desired_allocation(gen1) = 2000;
            dynamic_data.dd_new_allocation(gen1) = 200;
            dynamic_data.dd_promoted_size(gen1) = 22;
            dynamic_data.dd_desired_allocation(gen2) = 3000;
            dynamic_data.dd_new_allocation(gen2) = 300;
            dynamic_data.dd_promoted_size(gen2) = 33;
            dynamic_data.dd_desired_allocation(loh) = 4000;
            dynamic_data.dd_new_allocation(loh) = 400;
            dynamic_data.dd_promoted_size(loh) = 44;
            dynamic_data.dd_desired_allocation(poh) = 5000;
            dynamic_data.dd_new_allocation(poh) = 500;
            dynamic_data.dd_promoted_size(poh) = 55;

            Assert.Equal((nuint)9000, gc_heap.get_current_allocated(pHeap));
            Assert.Equal((nuint)9000, gc_heap.get_total_allocated(pHeap));

            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen0;
            Assert.Equal((nuint)11, gc_heap.get_total_promoted(pHeap));

            gc_heap.settings.condemned_generation = (int)gc_generation_num.max_generation;
            Assert.Equal((nuint)165, gc_heap.get_total_promoted(pHeap));

            Assert.Equal((nuint)1000, dynamic_data.dd_desired_allocation(gen0));
            Assert.Equal((nint)100, dynamic_data.dd_new_allocation(gen0));
            Assert.Equal((nuint)11, dynamic_data.dd_promoted_size(gen0));
            Assert.Equal((nuint)4000, dynamic_data.dd_desired_allocation(loh));
            Assert.Equal((nint)400, dynamic_data.dd_new_allocation(loh));
            Assert.Equal((nuint)44, dynamic_data.dd_promoted_size(loh));
            Assert.Equal((nuint)5000, dynamic_data.dd_desired_allocation(poh));
            Assert.Equal((nint)500, dynamic_data.dd_new_allocation(poh));
            Assert.Equal((nuint)55, dynamic_data.dd_promoted_size(poh));
        }

        Assert.Equal(expectedSettings.condemned_generation, gc_heap.settings.condemned_generation);
    }
#endif

#if USE_REGIONS
    [Fact]
    public void PlanPhaseGetGen0EndPlanSpaceFiltersRegionsAndResetsTotal()
    {
        using PlanPhaseStateScope _ = new();
        gc_heap heap = default;
        heap_segment* regions = stackalloc heap_segment[4];
        generation* generationTable = gc_heap.generation_table_of(&heap);

        SetPlanRegion(&regions[0], reserved: 0x1100, planAllocated: 0x1000, planGenNumber: 0, next: null);
        SetPlanRegion(&regions[1], reserved: 0x2200, planAllocated: 0x2000, planGenNumber: 1, next: &regions[2]);
        SetPlanRegion(&regions[2], reserved: 0x3400, planAllocated: 0x3000, planGenNumber: 0, next: null);
        SetPlanRegion(&regions[3], reserved: 0x4800, planAllocated: 0x4000, planGenNumber: 0, next: null);
        generation.generation_start_segment(generationTable + (int)gc_generation_num.soh_gen0) = &regions[0];
        generation.generation_start_segment(generationTable + (int)gc_generation_num.soh_gen1) = &regions[1];
        generation.generation_start_segment(generationTable + (int)gc_generation_num.soh_gen2) = &regions[3];
        gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen1;
        gc_heap.end_gen0_region_space = 1;

        gc_heap.get_gen0_end_plan_space(&heap);

        Assert.Equal((nuint)0x500, gc_heap.end_gen0_region_space);
        Assert.False(gc_heap.gen0_large_chunk_found);
    }

    [Fact]
    public void PlanPhaseGetGen0EndPlanSpaceSetsLargeChunkAtThreshold()
    {
        using PlanPhaseStateScope _ = new();
        gc_heap heap = default;
        heap_segment* region = stackalloc heap_segment[1];
        generation* generationTable = gc_heap.generation_table_of(&heap);
        nuint threshold = gc_heap.END_SPACE_AFTER_GC_FL;

        SetPlanRegion(
            region,
            reserved: (nuint)0x100000 + threshold - 1,
            planAllocated: 0x100000,
            planGenNumber: 0,
            next: null);
        generation.generation_start_segment(generationTable + (int)gc_generation_num.soh_gen0) = region;
        gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen0;

        gc_heap.get_gen0_end_plan_space(&heap);

        Assert.Equal(threshold - 1, gc_heap.end_gen0_region_space);
        Assert.False(gc_heap.gen0_large_chunk_found);

        heap_segment.heap_segment_reserved(region) = (byte*)((nuint)heap_segment.heap_segment_plan_allocated(region) + threshold);
        gc_heap.get_gen0_end_plan_space(&heap);

        Assert.Equal(threshold, gc_heap.end_gen0_region_space);
        Assert.True(gc_heap.gen0_large_chunk_found);
    }

    [Fact]
    public void PlanPhaseUpdatePlannedGen0FreeSpaceAccumulatesAndFindsLargeChunkAtThreshold()
    {
        using PlanPhaseStateScope _ = new();
        nuint threshold = gc_heap.END_SPACE_AFTER_GC_FL;

        gc_heap.update_planned_gen0_free_space(threshold - 1, null);

        Assert.Equal(threshold - 1, gc_heap.gen0_pinned_free_space);
        Assert.False(gc_heap.gen0_large_chunk_found);

        gc_heap.update_planned_gen0_free_space(threshold, null);

        Assert.Equal((2 * threshold) - 1, gc_heap.gen0_pinned_free_space);
        Assert.True(gc_heap.gen0_large_chunk_found);

        gc_heap.update_planned_gen0_free_space(1, null);

        Assert.Equal(2 * threshold, gc_heap.gen0_pinned_free_space);
        Assert.True(gc_heap.gen0_large_chunk_found);
    }

    [Fact]
    public void PlanPhaseStateScopeRestoresEndPlanSpaceState()
    {
        gc_mechanisms expectedSettings = gc_heap.settings;
        nuint expectedEndGen0RegionSpace = gc_heap.end_gen0_region_space;
        nuint expectedGen0PinnedFreeSpace = gc_heap.gen0_pinned_free_space;
        bool expectedGen0LargeChunkFound = gc_heap.gen0_large_chunk_found;

        using (new PlanPhaseStateScope())
        {
            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen2;
            gc_heap.end_gen0_region_space = 1;
            gc_heap.gen0_pinned_free_space = 2;
            gc_heap.gen0_large_chunk_found = true;
        }

        Assert.Equal(expectedSettings.condemned_generation, gc_heap.settings.condemned_generation);
        Assert.Equal(expectedEndGen0RegionSpace, gc_heap.end_gen0_region_space);
        Assert.Equal(expectedGen0PinnedFreeSpace, gc_heap.gen0_pinned_free_space);
        Assert.Equal(expectedGen0LargeChunkFound, gc_heap.gen0_large_chunk_found);
    }

    [Theory]
    [InlineData((int)memory_type.memory_type_reserved, 0x1300UL)]
    [InlineData((int)memory_type.memory_type_committed, 0xC00UL)]
    public void PlanPhaseGetGen0EndSpaceSumsLinkedSegments(int memoryType, ulong expected)
    {
        gc_heap heap = default;
        heap_segment* segments = stackalloc heap_segment[2];
        generation* generationTable = gc_heap.generation_table_of(&heap);

        heap_segment.heap_segment_allocated(&segments[0]) = (byte*)0x1100;
        heap_segment.heap_segment_committed(&segments[0]) = (byte*)0x1800;
        heap_segment.heap_segment_reserved(&segments[0]) = (byte*)0x1A00;
        heap_segment.heap_segment_next(&segments[0]) = &segments[1];
        heap_segment.heap_segment_allocated(&segments[1]) = (byte*)0x2200;
        heap_segment.heap_segment_committed(&segments[1]) = (byte*)0x2700;
        heap_segment.heap_segment_reserved(&segments[1]) = (byte*)0x2C00;
        heap_segment.heap_segment_next(&segments[1]) = null;
        generation.generation_start_segment(
            generationTable + (int)gc_generation_num.soh_gen0) = &segments[0];

        Assert.Equal((nuint)expected, gc_heap.get_gen0_end_space(&heap, (memory_type)memoryType));
    }

    [Fact]
    public void PlanPhaseGetGen0EndSpaceReturnsZeroForNullStartSegment()
    {
        gc_heap heap = default;

        Assert.Equal((nuint)0, gc_heap.get_gen0_end_space(&heap, memory_type.memory_type_reserved));
        Assert.Equal((nuint)0, gc_heap.get_gen0_end_space(&heap, memory_type.memory_type_committed));
    }
#endif

#if USE_REGIONS && !MULTIPLE_HEAPS
    [Fact]
    public void PlanPhaseSaveCurrentSurvivedSnapshotsEveryRegion()
    {
        using MarkPhaseStateScope _ = new();
        nuint* survived = stackalloc nuint[3];
        nuint* oldCardSurvived = stackalloc nuint[3];

        survived[0] = 11;
        survived[1] = 0;
        survived[2] = 99;
        oldCardSurvived[0] = 1;
        oldCardSurvived[1] = 2;
        oldCardSurvived[2] = 3;
        gc_heap.region_count = 3;
        gc_heap.survived_per_region = survived;
        gc_heap.old_card_survived_per_region = oldCardSurvived;

        gc_heap.save_current_survived();

        Assert.Equal((nuint)11, oldCardSurvived[0]);
        Assert.Equal((nuint)0, oldCardSurvived[1]);
        Assert.Equal((nuint)99, oldCardSurvived[2]);
        Assert.Equal((nuint)11, survived[0]);
        Assert.Equal((nuint)0, survived[1]);
        Assert.Equal((nuint)99, survived[2]);
    }

    [Fact]
    public void PlanPhaseUpdateOldCardSurvivedStoresDeltas()
    {
        using MarkPhaseStateScope _ = new();
        nuint* survived = stackalloc nuint[3];
        nuint* oldCardSurvived = stackalloc nuint[3];

        survived[0] = 11;
        survived[1] = 25;
        survived[2] = 99;
        oldCardSurvived[0] = 1;
        oldCardSurvived[1] = 25;
        oldCardSurvived[2] = 3;
        gc_heap.region_count = 3;
        gc_heap.survived_per_region = survived;
        gc_heap.old_card_survived_per_region = oldCardSurvived;

        gc_heap.update_old_card_survived();

        Assert.Equal((nuint)10, oldCardSurvived[0]);
        Assert.Equal((nuint)0, oldCardSurvived[1]);
        Assert.Equal((nuint)96, oldCardSurvived[2]);
        Assert.Equal((nuint)11, survived[0]);
        Assert.Equal((nuint)25, survived[1]);
        Assert.Equal((nuint)99, survived[2]);
    }

    [Fact]
    public void PlanPhaseSurvivedHelpersLeaveOldCardSnapshotWhenCurrentIsNull()
    {
        using MarkPhaseStateScope _ = new();
        nuint* oldCardSurvived = stackalloc nuint[2];

        oldCardSurvived[0] = 7;
        oldCardSurvived[1] = 9;
        gc_heap.region_count = 2;
        gc_heap.survived_per_region = null;
        gc_heap.old_card_survived_per_region = oldCardSurvived;

        gc_heap.save_current_survived();
        gc_heap.update_old_card_survived();

        Assert.Equal((nuint)7, oldCardSurvived[0]);
        Assert.Equal((nuint)9, oldCardSurvived[1]);
    }

    [Fact]
    public void PlanPhaseUpdateOldCardSurvivedPreservesUnsignedUnderflow()
    {
        using MarkPhaseStateScope _ = new();
        nuint* survived = stackalloc nuint[1];
        nuint* oldCardSurvived = stackalloc nuint[1];

        survived[0] = 0;
        oldCardSurvived[0] = 1;
        gc_heap.region_count = 1;
        gc_heap.survived_per_region = survived;
        gc_heap.old_card_survived_per_region = oldCardSurvived;

        gc_heap.update_old_card_survived();

        Assert.Equal(nuint.MaxValue, oldCardSurvived[0]);
    }
#endif

    private static nuint LogcountReference(nuint word)
    {
        nuint count = 0;
        while (word != 0)
        {
            count += word & 1;
            word >>= 1;
        }

        return count;
    }

    private static nuint LinearAllocationModel(
        float allocationFraction,
        nuint newAllocation,
        nuint previousDesiredAllocation,
        float timeSincePreviousCollectionSecs)
    {
        MethodInfo method = Assert.IsAssignableFrom<MethodInfo>(typeof(gc_heap).GetMethod(
            "linear_allocation_model",
            BindingFlags.NonPublic | BindingFlags.Static));
        return Assert.IsType<nuint>(method.Invoke(
            null,
            new object[] {
                allocationFraction,
                newAllocation,
                previousDesiredAllocation,
                timeSincePreviousCollectionSecs,
            }));
    }

#if USE_REGIONS
    private static void SetPlanRegion(heap_segment* region, nuint reserved, nuint planAllocated, int planGenNumber, heap_segment* next)
    {
        heap_segment.heap_segment_reserved(region) = (byte*)reserved;
        heap_segment.heap_segment_plan_allocated(region) = (byte*)planAllocated;
        heap_segment.heap_segment_plan_gen_num(region) = planGenNumber;
        heap_segment.heap_segment_next(region) = next;
    }
#endif

#if USE_REGIONS && !MULTIPLE_HEAPS
    private static void SetGenerationSizeRegion(
        heap_segment* region,
        nuint mem,
        nuint planAllocated,
        nuint allocated,
        heap_segment* next)
    {
        heap_segment.heap_segment_mem(region) = (byte*)mem;
        heap_segment.heap_segment_plan_allocated(region) = (byte*)planAllocated;
        heap_segment.heap_segment_allocated(region) = (byte*)allocated;
        heap_segment.heap_segment_next(region) = next;
    }
#endif

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void RelocateCompactMemcopyCopiesOnlyRequestedPointerWords(int pointerWordCount)
    {
        const nuint SourceLeadingGuard = 0x1357;
        const nuint SourceTrailingGuard = 0x2468;
        const nuint DestinationLeadingGuard = 0x369A;
        const nuint DestinationTrailingGuard = 0x48BC;
        nuint* source = stackalloc nuint[11];
        nuint* destination = stackalloc nuint[11];

        source[0] = SourceLeadingGuard;
        source[pointerWordCount + 1] = SourceTrailingGuard;
        destination[0] = DestinationLeadingGuard;
        destination[pointerWordCount + 1] = DestinationTrailingGuard;

        for (int i = 0; i < pointerWordCount; i++)
        {
            source[i + 1] = (nuint)(0x100 + i);
            destination[i + 1] = (nuint)(0x200 + i);
        }

        gc_heap.memcopy(
            (byte*)(destination + 1),
            (byte*)(source + 1),
            (nuint)(pointerWordCount * sizeof(nuint)));

        Assert.Equal(SourceLeadingGuard, source[0]);
        Assert.Equal(SourceTrailingGuard, source[pointerWordCount + 1]);
        Assert.Equal(DestinationLeadingGuard, destination[0]);
        Assert.Equal(DestinationTrailingGuard, destination[pointerWordCount + 1]);

        for (int i = 0; i < pointerWordCount; i++)
        {
            Assert.Equal((nuint)(0x100 + i), source[i + 1]);
            Assert.Equal(source[i + 1], destination[i + 1]);
        }
    }

    [Fact]
    public void SortedTableStoragePreservesNativeSentinelLayout()
    {
        sorted_table table = default;
        bk* slots = stackalloc bk[4];

        sorted_table.initialize(&table, 3, slots);

        Assert.Equal((nuint)(slots + 1), (nuint)sorted_table.buckets(&table));
        Assert.Equal((nuint)0, (nuint)sorted_table.last_slot(slots));
        Assert.Equal(nuint.MaxValue, (nuint)sorted_table.buckets(&table)[0].add);
    }

    [Fact]
    public void SortedTableSchemaMatchesNativeLayout()
    {
        bk bucket = default;

        Assert.Equal((nuint)0, OffsetOf(&bucket.add, &bucket));
        Assert.Equal((nuint)sizeof(nuint), OffsetOf(&bucket.val, &bucket));
        Assert.Equal(2 * sizeof(nuint), sizeof(bk));
        Assert.Equal(4 * sizeof(nuint), sizeof(sorted_table));
    }

    [Fact]
    public void SortedTableInsertAndLookupPreservePredecessorIntervals()
    {
        sorted_table table = default;
        bk* slots = stackalloc bk[8];
        sorted_table.initialize(&table, 7, slots);

        Assert.Equal(1, sorted_table.insert(&table, (byte*)0x3000, 30));
        Assert.Equal(1, sorted_table.insert(&table, (byte*)0x1000, 10));
        Assert.Equal(1, sorted_table.insert(&table, (byte*)0x2000, 20));

        AssertSortedTableLookup(&table, 0x1000, 0x1000, 10);
        AssertSortedTableLookup(&table, 0x1FFF, 0x1000, 10);
        AssertSortedTableLookup(&table, 0x2000, 0x2000, 20);
        AssertSortedTableLookup(&table, 0x2FFF, 0x2000, 20);
        AssertSortedTableLookup(&table, 0x3000, 0x3000, 30);
        AssertSortedTableLookup(&table, 0xFFFF, 0x3000, 30);

        byte* belowFirst = (byte*)0xFFF;
        Assert.Equal((nuint)0, sorted_table.lookup(&table, ref belowFirst));
        Assert.Equal((nuint)0, (nuint)belowFirst);
    }

    [Fact]
    public void SortedTableRemoveUsesNativeContainingInterval()
    {
        sorted_table table = default;
        bk* slots = stackalloc bk[8];
        sorted_table.initialize(&table, 7, slots);
        sorted_table.insert(&table, (byte*)0x1000, 10);
        sorted_table.insert(&table, (byte*)0x2000, 20);
        sorted_table.insert(&table, (byte*)0x3000, 30);

        sorted_table.remove(&table, (byte*)0x2800);

        AssertSortedTableLookup(&table, 0x2000, 0x1000, 10);
        AssertSortedTableLookup(&table, 0x2FFF, 0x1000, 10);
        AssertSortedTableLookup(&table, 0x3000, 0x3000, 30);
    }

    [Fact]
    public void SortedTableDuplicateBoundaryUsesLastInsertedValue()
    {
        sorted_table table = default;
        bk* slots = stackalloc bk[6];
        sorted_table.initialize(&table, 5, slots);
        sorted_table.insert(&table, (byte*)0x1000, 10);
        sorted_table.insert(&table, (byte*)0x1000, 11);

        AssertSortedTableLookup(&table, 0x1000, 0x1000, 11);

        sorted_table.remove(&table, (byte*)0x1000);

        AssertSortedTableLookup(&table, 0x1000, 0x1000, 10);
    }

    [Fact]
    public void SortedTableClearRestoresOnlySentinel()
    {
        sorted_table table = default;
        bk* slots = stackalloc bk[5];
        sorted_table.initialize(&table, 4, slots);
        sorted_table.insert(&table, (byte*)0x1000, 10);

        sorted_table.clear(&table);

        byte* address = (byte*)0x1000;
        Assert.Equal((nuint)0, sorted_table.lookup(&table, ref address));
        Assert.Equal((nuint)0, (nuint)address);
        Assert.Equal(nuint.MaxValue, (nuint)sorted_table.buckets(&table)[0].add);
    }

    [Fact]
    public void SortedTableAllocationGrowthAndReclamationPreserveNativeOwnership()
    {
        SyncImports.ResetRecording();
        sorted_table* table = sorted_table.make_sorted_table();
        Assert.NotEqual((nuint)0, (nuint)table);
        Assert.Equal(1, SyncImports.AllocCount);
        int freeCountAfterDelete = 0;

        try
        {
            for (nuint index = 0; index < 399; index++)
            {
                sorted_table.insert(table, (byte*)((index + 1) * 0x1000), index + 1);
            }

            Assert.Equal(1, sorted_table.ensure_space_for_insert(table));
            Assert.Equal(2, SyncImports.AllocCount);
            AssertSortedTableLookup(table, 399 * 0x1000, 399 * 0x1000, 399);

            for (nuint index = 399; index < 599; index++)
            {
                sorted_table.insert(table, (byte*)((index + 1) * 0x1000), index + 1);
            }

            Assert.Equal(1, sorted_table.ensure_space_for_insert(table));
            Assert.Equal(3, SyncImports.AllocCount);
            AssertSortedTableLookup(table, 599 * 0x1000, 599 * 0x1000, 599);
        }
        finally
        {
            sorted_table.delete_sorted_table(table);
            freeCountAfterDelete = SyncImports.FreeCount;
            SyncImports.ManagedGC_Free(table);
        }

        Assert.Equal(2, freeCountAfterDelete);
        Assert.Equal(3, SyncImports.FreeCount);
    }

    [Fact]
    public void SortedTableAllocationFailuresReturnNullOrFalse()
    {
        SyncImports.ResetRecording();
        SyncImports.FailNextAlloc = true;
        Assert.Equal((nuint)0, (nuint)sorted_table.make_sorted_table());

#if !DEBUG
        // This path asserts in the port, as it does in the C++, so it can only be driven in a
        // build where the assert is compiled out.
        sorted_table* table = sorted_table.make_sorted_table();
        Assert.NotEqual((nuint)0, (nuint)table);
        try
        {
            for (nuint index = 0; index < 399; index++)
            {
                sorted_table.insert(table, (byte*)((index + 1) * 0x1000), index + 1);
            }

            SyncImports.FailNextAlloc = true;
            Assert.Equal(0, sorted_table.ensure_space_for_insert(table));
        }
        finally
        {
            sorted_table.delete_sorted_table(table);
            SyncImports.ManagedGC_Free(table);
        }
#endif
    }

    private static void AssertSortedTableLookup(
        sorted_table* table,
        nuint requested,
        nuint expectedAddress,
        nuint expectedValue)
    {
        byte* address = (byte*)requested;
        Assert.Equal(expectedValue, sorted_table.lookup(table, ref address));
        Assert.Equal(expectedAddress, (nuint)address);
    }

    private static nuint OffsetOf(void* field, bk* bucket) => (nuint)((byte*)field - (byte*)bucket);

#if !TARGET_WASM
    [Fact]
    public void EventBucketSetReplacesAllFields()
    {
        etw_bucket_info info = new()
        {
            index = ushort.MaxValue,
            count = uint.MaxValue,
            size = nuint.MaxValue,
        };

        info.set(12, 34, 56);

        Assert.Equal((ushort)12, info.index);
        Assert.Equal((uint)34, info.count);
        Assert.Equal((nuint)56, info.size);
    }
#endif

    [Fact]
    public void AllocListStartsEmptyAndAccessorsReferToItsFields()
    {
        alloc_list list = default;

        Assert.Equal((nuint)0, (nuint)alloc_list.alloc_list_head(&list));
        Assert.Equal((nuint)0, (nuint)alloc_list.alloc_list_tail(&list));
        Assert.Equal((nuint)0, alloc_list.alloc_list_damage_count(&list));
#if TARGET_64BIT && !TARGET_WASM
        Assert.Equal((nuint)0, (nuint)alloc_list.added_alloc_list_head(&list));
        Assert.Equal((nuint)0, (nuint)alloc_list.added_alloc_list_tail(&list));
#endif

        nuint offset = 0;
#if TARGET_64BIT && !TARGET_WASM
        fixed (byte** field = &alloc_list.added_alloc_list_head(&list))
        {
            Assert.Equal(offset, OffsetOf(field, &list));
        }
        offset += (nuint)sizeof(void*);
        fixed (byte** field = &alloc_list.added_alloc_list_tail(&list))
        {
            Assert.Equal(offset, OffsetOf(field, &list));
        }
        offset += (nuint)sizeof(void*);
#endif
        fixed (byte** field = &alloc_list.alloc_list_head(&list))
        {
            Assert.Equal(offset, OffsetOf(field, &list));
        }
        offset += (nuint)sizeof(void*);
        fixed (byte** field = &alloc_list.alloc_list_tail(&list))
        {
            Assert.Equal(offset, OffsetOf(field, &list));
        }
        offset += (nuint)sizeof(void*);
        fixed (nuint* field = &alloc_list.alloc_list_damage_count(&list))
        {
            Assert.Equal(offset, OffsetOf(field, &list));
        }

        alloc_list.alloc_list_head(&list) = (byte*)1;
        alloc_list.alloc_list_tail(&list) = (byte*)2;
        alloc_list.alloc_list_damage_count(&list) = 3;
#if TARGET_64BIT && !TARGET_WASM
        alloc_list.added_alloc_list_head(&list) = (byte*)4;
        alloc_list.added_alloc_list_tail(&list) = (byte*)5;
#endif

        Assert.Equal((nuint)1, (nuint)alloc_list.alloc_list_head(&list));
        Assert.Equal((nuint)2, (nuint)alloc_list.alloc_list_tail(&list));
        Assert.Equal((nuint)3, alloc_list.alloc_list_damage_count(&list));
#if TARGET_64BIT && !TARGET_WASM
        Assert.Equal((nuint)4, (nuint)alloc_list.added_alloc_list_head(&list));
        Assert.Equal((nuint)5, (nuint)alloc_list.added_alloc_list_tail(&list));
#endif
    }

    private static nuint OffsetOf(void* field, alloc_list* list) => (nuint)((byte*)field - (byte*)list);

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void MarkShortBitsPreserveEveryNativeMask(bool post, int bit)
    {
        mark value = default;
        mark* p = &value;
        int expected = 1 << (28 + bit);

        Assert.Equal((nuint)3, mark.get_max_short_bits());
        Assert.Equal((nuint)28, mark.get_pre_short_start_bit());
        Assert.Equal((nuint)28, mark.get_post_short_start_bit());

        if (post)
        {
            mark.set_post_short_bit(p, (nuint)bit);
            Assert.Equal(expected, value.saved_post_p);
            Assert.Equal(expected, mark.post_short_bit_p(p, (nuint)bit));
            Assert.Equal(0, mark.post_short_p(p));
        }
        else
        {
            mark.set_pre_short_bit(p, (nuint)bit);
            Assert.Equal(expected, value.saved_pre_p);
            Assert.Equal(expected, mark.pre_short_bit_p(p, (nuint)bit));
            Assert.Equal(0, mark.pre_short_p(p));
        }
    }

    [Fact]
    public void MarkShortAndCollectibleBitsPreserveNativeBoolValues()
    {
        mark value = default;
        mark* p = &value;

        mark.set_pre_short(p);
        mark.set_post_short(p);
        Assert.Equal(unchecked((int)0x80000000), value.saved_pre_p);
        Assert.Equal(unchecked((int)0x80000000), value.saved_post_p);
        Assert.Equal(unchecked((int)0x80000000), mark.pre_short_p(p));
        Assert.Equal(unchecked((int)0x80000000), mark.post_short_p(p));

#if COLLECTIBLE_CLASS
        mark.set_pre_short_collectible(p);
        mark.set_post_short_collectible(p);
        Assert.Equal(2, mark.pre_short_collectible_p(p));
        Assert.Equal(2, mark.post_short_collectible_p(p));
#else
        // NativeAOT defines FEATURE_NATIVEAOT, so gcpriv.h does not define COLLECTIBLE_CLASS.
        // The reserved collectible bit remains part of the packed BOOL and must not be normalized.
        value.saved_pre_p |= 2;
        value.saved_post_p |= 2;
#endif

        Assert.Equal(unchecked((int)0x80000002), value.saved_pre_p);
        Assert.Equal(unchecked((int)0x80000002), value.saved_post_p);

        value.saved_pre_p = 0x40000002;
        value.saved_post_p = 0x20000002;
        Assert.Equal(0x40000002, mark.has_pre_plug_info(p));
        Assert.Equal(0x20000002, mark.has_post_plug_info(p));
    }

    [Fact]
    public void MarkPointerAccessorsReferToStoredAddresses()
    {
        mark value = default;
        mark* p = &value;

        value.first = (byte*)0x100;
        value.saved_post_plug_info_start = (byte*)0x200;
        mark.set_pre_plug_info_reloc_start(p, (byte*)0x300);

        Assert.Equal((nuint)0x100, (nuint)mark.get_plug_address(p));
        Assert.Equal((nuint)0x200, (nuint)mark.get_post_plug_info_start(p));
        Assert.Equal((nuint)0x300, (nuint)value.saved_pre_plug_info_reloc_start);
        Assert.True(mark.get_pre_plug_reloc_info(p) == &value.saved_pre_plug_reloc);
        Assert.True(mark.get_post_plug_reloc_info(p) == &value.saved_post_plug_reloc);

        mark.get_pre_plug_reloc_info(p)->gap = 0x11;
        mark.get_post_plug_reloc_info(p)->reloc = 0x22;
        Assert.Equal((nuint)0x11, value.saved_pre_plug_reloc.gap);
        Assert.Equal((nuint)0x22, value.saved_post_plug_reloc.reloc);
    }

    [Fact]
    public void MarkSwapMethodsExchangeExactGapRelocPairs()
    {
        byte* storage = stackalloc byte[2 * sizeof(plug_and_gap)];
        mark value = default;
        mark* p = &value;
        p->first = storage + sizeof(plug_and_gap);
        p->saved_post_plug_info_start = storage + sizeof(plug_and_gap);
        gap_reloc_pair* pre = (gap_reloc_pair*)(p->first - sizeof(plug_and_gap));
        gap_reloc_pair* post = (gap_reloc_pair*)p->saved_post_plug_info_start;

        *pre = Pair(1, 2, 3, 4);
        value.saved_pre_plug_reloc = Pair(5, 6, 7, 8);
        mark.swap_pre_plug_and_saved(p);
        AssertPair(*pre, 5, 6, 7, 8);
        AssertPair(value.saved_pre_plug_reloc, 1, 2, 3, 4);

        *post = Pair(9, 10, 11, 12);
        value.saved_post_plug_reloc = Pair(13, 14, 15, 16);
        mark.swap_post_plug_and_saved(p);
        AssertPair(*post, 13, 14, 15, 16);
        AssertPair(value.saved_post_plug_reloc, 9, 10, 11, 12);

        *pre = Pair(17, 18, 19, 20);
        value.saved_pre_plug = Pair(21, 22, 23, 24);
        mark.swap_pre_plug_and_saved_for_profiler(p);
        AssertPair(*pre, 21, 22, 23, 24);
        AssertPair(value.saved_pre_plug, 17, 18, 19, 20);

        *post = Pair(25, 26, 27, 28);
        value.saved_post_plug = Pair(29, 30, 31, 32);
        mark.swap_post_plug_and_saved_for_profiler(p);
        AssertPair(*post, 29, 30, 31, 32);
        AssertPair(value.saved_post_plug, 25, 26, 27, 28);
    }

    [Fact]
    public void MarkRecoverPlugInfoRestoresPairsForCompactAndSweep()
    {
        using MarkPhaseStateScope _ = new();
        byte* storage = stackalloc byte[6 * sizeof(plug_and_gap)];
        mark value = default;
        mark* entry = &value;
        gap_reloc_pair* pre = (gap_reloc_pair*)storage;
        gap_reloc_pair* post = (gap_reloc_pair*)(storage + (2 * sizeof(plug_and_gap)));
        gap_reloc_pair* preReloc = (gap_reloc_pair*)(storage + (3 * sizeof(plug_and_gap)));
        gap_reloc_pair* postReloc = (gap_reloc_pair*)(storage + (4 * sizeof(plug_and_gap)));

        entry->first = storage + sizeof(plug_and_gap);
        entry->saved_pre_plug_info_reloc_start = (byte*)preReloc;
        entry->saved_post_plug_info_start = (byte*)postReloc;
        entry->saved_pre_p = 1;
        entry->saved_post_p = 1;
        entry->saved_pre_plug = Pair(1, 2, 3, 4);
        entry->saved_post_plug = Pair(5, 6, 7, 8);
        entry->saved_pre_plug_reloc = Pair(9, 10, 11, 12);
        entry->saved_post_plug_reloc = Pair(13, 14, 15, 16);
        *pre = Pair(17, 18, 19, 20);
        *post = Pair(21, 22, 23, 24);

        gc_heap.settings.compaction = 1;
        Assert.Equal((nuint)0, mark.recover_plug_info(entry));
        AssertPair(*preReloc, 9, 10, 11, 12);
        AssertPair(*postReloc, 13, 14, 15, 16);

        entry->saved_post_plug_info_start = (byte*)post;
        gc_heap.settings.compaction = 0;
        Assert.Equal((nuint)(2 * sizeof(gap_reloc_pair)), mark.recover_plug_info(entry));
        AssertPair(*pre, 1, 2, 3, 4);
        AssertPair(*post, 5, 6, 7, 8);
    }

    [Fact]
    public void RelocateCompactRecoveryAggregatesOnlyGen2AndDrainsQueue()
    {
        using MarkPhaseStateScope _ = new();
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowest = GCCommon.g_gc_lowest_address;
        byte* oldHighest = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
        region_info* oldGenerationMap = gc_heap.map_region_to_generation;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
        mark* entries = stackalloc mark[2];
        gap_reloc_pair* records = stackalloc gap_reloc_pair[2];
        seg_mapping* segmentMap = stackalloc seg_mapping[3];
        region_info* generationMap = stackalloc region_info[3];

        try
        {
            gc_heap.min_segment_size_shr = 12;
            GCCommon.g_gc_lowest_address = (byte*)0x5000;
            GCCommon.g_gc_highest_address = (byte*)0x8000;
            GCCommon.seg_mapping_table = segmentMap - 5;
            gc_heap.map_region_to_generation = generationMap;
            gc_heap.map_region_to_generation_skewed = generationMap - 5;
            InitializeRegionGenerationMaps(generationMap, segmentMap, 3, 1);
            SetRegionGenerationForAddress((byte*)0x7000, 5, generationMap, segmentMap, 2);

            entries[0] = default;
            entries[0].first = (byte*)0x6000;
            entries[0].saved_post_p = 1;
            entries[0].saved_post_plug_info_start = (byte*)&records[0];
            entries[0].saved_post_plug = Pair(1, 2, 3, 4);
            entries[1] = default;
            entries[1].first = (byte*)0x7000;
            entries[1].saved_post_p = 1;
            entries[1].saved_post_plug_info_start = (byte*)&records[1];
            entries[1].saved_post_plug = Pair(5, 6, 7, 8);

            gc_heap.mark_stack_array = entries;
            gc_heap.mark_stack_array_length = 2;
            gc_heap.mark_stack_tos = 2;
            gc_heap.mark_stack_bos = 0;
            gc_heap.settings.compaction = 0;

            Assert.Equal((nuint)sizeof(gap_reloc_pair), gc_heap.recover_saved_pinned_info());
            AssertPair(records[0], 1, 2, 3, 4);
            AssertPair(records[1], 5, 6, 7, 8);
            Assert.Equal((nuint)2, gc_heap.mark_stack_bos);
            Assert.True(gc_heap.pinned_plug_que_empty_p(null) != 0);
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowest;
            GCCommon.g_gc_highest_address = oldHighest;
            GCCommon.seg_mapping_table = oldSegMappingTable;
            gc_heap.map_region_to_generation = oldGenerationMap;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
        }
    }

    [Fact]
    public void MarkPhasePinnedQueuePreservesNativeStateTransitions()
    {
        using MarkPhaseStateScope _ = new();
        byte* storage = stackalloc byte[128];
        mark* entries = stackalloc mark[2];
        generation generation = default;
        heap_segment segment = default;
        gc_heap heap = default;
        gc_heap* pHeap = &heap;

        entries[0] = default;
        entries[1] = default;
        segment.mem = storage;
        generation.generation_allocation_segment(&generation) = &segment;
        generation.generation_allocation_pointer(&generation) = storage;
        generation.generation_allocation_limit(&generation) = storage + 96;

        gc_heap.make_mark_stack(pHeap, entries);

        Assert.Equal((nuint)0, gc_heap.mark_stack_tos);
        Assert.Equal((nuint)0, gc_heap.mark_stack_bos);
        Assert.Equal(gc_rand.MARK_STACK_INITIAL_LENGTH, gc_heap.mark_stack_array_length);
        Assert.True(gc_heap.pinned_plug_que_empty_p(pHeap) != 0);
        Assert.True(gc_heap.before_oldest_pin(pHeap) is null);

        entries[0].first = storage + 64;
        gc_heap.set_pinned_info(pHeap, entries[0].first, 16, &generation);

        Assert.Equal((nuint)1, gc_heap.mark_stack_tos);
        Assert.Equal((nuint)16, gc_heap.pinned_len(&entries[0]));
        Assert.Equal((nuint)(storage + 64), (nuint)generation.generation_allocation_limit(&generation));
        Assert.True(gc_heap.oldest_pin(pHeap) == &entries[0]);
        Assert.Equal((nuint)(storage + 64), (nuint)gc_heap.pinned_plug(gc_heap.oldest_pin(pHeap)));

        gc_heap.set_new_pin_info(&entries[0], storage + 16);
        Assert.Equal((nuint)48, gc_heap.pinned_len(&entries[0]));
        Assert.Equal((nuint)(storage + 16), (nuint)entries[0].allocation_context_start_region);

        gc_heap.update_oldest_pinned_plug(pHeap);
        Assert.Equal((nuint)(storage + 64), (nuint)gc_heap.oldest_pinned_plug);
        Assert.Equal((nuint)0, gc_heap.deque_pinned_plug(pHeap));
        Assert.True(gc_heap.pinned_plug_que_empty_p(pHeap) != 0);
        Assert.True(gc_heap.before_oldest_pin(pHeap) == &entries[0]);

        gc_heap.reset_pinned_queue_bos(pHeap);
        Assert.Equal((nuint)0, gc_heap.mark_stack_bos);
        gc_heap.reset_pinned_queue(pHeap);
        Assert.Equal((nuint)0, gc_heap.mark_stack_tos);
        Assert.Equal((nuint)0, gc_heap.mark_stack_bos);

        gc_heap.min_overflow_address = storage;
        gc_heap.max_overflow_address = storage + 96;
        gc_heap.reset_mark_stack(pHeap);
        Assert.Equal((nuint)0, gc_heap.mark_stack_tos);
        Assert.Equal((nuint)0, gc_heap.mark_stack_bos);
        Assert.Equal(nuint.MaxValue, (nuint)gc_heap.min_overflow_address);
        Assert.Equal((nuint)0, (nuint)gc_heap.max_overflow_address);
    }

    [Fact]
    public void RelocateCompactGetOldestPinnedEntryDequeuesAndUpdatesOldestPlug()
    {
        using MarkPhaseStateScope _ = new();
        byte* storage = stackalloc byte[128];
        mark* entries = stackalloc mark[2];
        generation generation = default;
        heap_segment segment = default;
        gc_heap heap = default;
        gc_heap* pHeap = &heap;

        entries[0] = default;
        entries[1] = default;
        segment.mem = storage;
        generation.generation_allocation_segment(&generation) = &segment;
        generation.generation_allocation_pointer(&generation) = storage;
        generation.generation_allocation_limit(&generation) = storage + 96;
        gc_heap.make_mark_stack(pHeap, entries);

        entries[0].first = storage + 32;
        entries[0].saved_pre_p = 1;
        entries[0].saved_post_p = 0;
        gc_heap.set_pinned_info(pHeap, entries[0].first, 16, &generation);

        entries[1].first = storage + 64;
        entries[1].saved_pre_p = 0;
        entries[1].saved_post_p = 1;
        gc_heap.set_pinned_info(pHeap, entries[1].first, 16, &generation);

        gc_heap.update_oldest_pinned_plug(pHeap);

        int firstHasPrePlugInfo;
        int firstHasPostPlugInfo;
        mark* firstEntry = gc_heap.get_oldest_pinned_entry(
            pHeap,
            &firstHasPrePlugInfo,
            &firstHasPostPlugInfo);

        Assert.True(firstEntry == &entries[0]);
        Assert.Equal(1, firstHasPrePlugInfo);
        Assert.Equal(0, firstHasPostPlugInfo);
        Assert.Equal((nuint)1, gc_heap.mark_stack_bos);
        Assert.Equal((nuint)entries[1].first, (nuint)gc_heap.oldest_pinned_plug);

        int secondHasPrePlugInfo;
        int secondHasPostPlugInfo;
        mark* secondEntry = gc_heap.get_oldest_pinned_entry(
            pHeap,
            &secondHasPrePlugInfo,
            &secondHasPostPlugInfo);

        Assert.True(secondEntry == &entries[1]);
        Assert.Equal(0, secondHasPrePlugInfo);
        Assert.Equal(1, secondHasPostPlugInfo);
        Assert.Equal((nuint)2, gc_heap.mark_stack_bos);
        Assert.Equal((nuint)0, (nuint)gc_heap.oldest_pinned_plug);
    }

    [Fact]
    public void RelocateCompactGetNextPinnedEntryEmptyOrMismatchedLeavesOutputsAndQueue()
    {
        using MarkPhaseStateScope _ = new();
        byte* storage = stackalloc byte[128];
        mark* entries = stackalloc mark[1];
        gc_heap heap = default;
        gc_heap* pHeap = &heap;

        gc_heap.mark_stack_array = entries;
        gc_heap.mark_stack_array_length = 1;
        gc_heap.mark_stack_tos = 0;
        gc_heap.mark_stack_bos = 0;
        gc_heap.oldest_pinned_plug = storage + 96;

        int hasPrePlugInfo = 37;
        int hasPostPlugInfo = 41;
        mark* entry = gc_heap.get_next_pinned_entry(
            pHeap,
            storage,
            &hasPrePlugInfo,
            &hasPostPlugInfo,
            deque_p: 1);

        Assert.True(entry is null);
        Assert.Equal(37, hasPrePlugInfo);
        Assert.Equal(41, hasPostPlugInfo);
        Assert.Equal((nuint)0, gc_heap.mark_stack_bos);
        Assert.Equal((nuint)0, gc_heap.mark_stack_tos);
        Assert.Equal((nuint)(storage + 96), (nuint)gc_heap.oldest_pinned_plug);

        entries[0].first = storage + 32;
        entries[0].saved_pre_p = 1;
        entries[0].saved_post_p = 1;
        gc_heap.mark_stack_tos = 1;
        hasPrePlugInfo = 43;
        hasPostPlugInfo = 47;

        entry = gc_heap.get_next_pinned_entry(
            pHeap,
            storage + 64,
            &hasPrePlugInfo,
            &hasPostPlugInfo,
            deque_p: 1);

        Assert.True(entry is null);
        Assert.Equal(43, hasPrePlugInfo);
        Assert.Equal(47, hasPostPlugInfo);
        Assert.Equal((nuint)0, gc_heap.mark_stack_bos);
        Assert.Equal((nuint)1, gc_heap.mark_stack_tos);
        Assert.Equal((nuint)(storage + 96), (nuint)gc_heap.oldest_pinned_plug);
    }

    [Fact]
    public void RelocateCompactGetNextPinnedEntryMatchesWithoutDequeueing()
    {
        using MarkPhaseStateScope _ = new();
        byte* storage = stackalloc byte[128];
        mark* entries = stackalloc mark[1];
        gc_heap heap = default;
        gc_heap* pHeap = &heap;

        entries[0].first = storage + 32;
        entries[0].saved_pre_p = 1;
        entries[0].saved_post_p = 0;
        gc_heap.mark_stack_array = entries;
        gc_heap.mark_stack_array_length = 1;
        gc_heap.mark_stack_tos = 1;
        gc_heap.mark_stack_bos = 0;
        gc_heap.oldest_pinned_plug = storage + 96;

        int hasPrePlugInfo = 0;
        int hasPostPlugInfo = 0;
        mark* entry = gc_heap.get_next_pinned_entry(
            pHeap,
            entries[0].first,
            &hasPrePlugInfo,
            &hasPostPlugInfo,
            deque_p: 0);

        Assert.True(entry == &entries[0]);
        Assert.Equal(1, hasPrePlugInfo);
        Assert.Equal(0, hasPostPlugInfo);
        Assert.Equal((nuint)0, gc_heap.mark_stack_bos);
        Assert.Equal((nuint)1, gc_heap.mark_stack_tos);
        Assert.Equal((nuint)(storage + 96), (nuint)gc_heap.oldest_pinned_plug);
    }

    [Fact]
    public void RelocateCompactGetNextPinnedEntryMatchesAndDequeuesWithoutUpdatingOldestPlug()
    {
        using MarkPhaseStateScope _ = new();
        byte* storage = stackalloc byte[128];
        mark* entries = stackalloc mark[2];
        gc_heap heap = default;
        gc_heap* pHeap = &heap;

        entries[0].first = storage + 32;
        entries[0].saved_pre_p = 0;
        entries[0].saved_post_p = 1;
        entries[1].first = storage + 64;
        gc_heap.mark_stack_array = entries;
        gc_heap.mark_stack_array_length = 2;
        gc_heap.mark_stack_tos = 2;
        gc_heap.mark_stack_bos = 0;
        gc_heap.oldest_pinned_plug = entries[0].first;

        int hasPrePlugInfo = 1;
        int hasPostPlugInfo = 0;
        mark* entry = gc_heap.get_next_pinned_entry(
            pHeap,
            entries[0].first,
            &hasPrePlugInfo,
            &hasPostPlugInfo,
            deque_p: 1);

        Assert.True(entry == &entries[0]);
        Assert.Equal(0, hasPrePlugInfo);
        Assert.Equal(1, hasPostPlugInfo);
        Assert.Equal((nuint)1, gc_heap.mark_stack_bos);
        Assert.Equal((nuint)2, gc_heap.mark_stack_tos);
        Assert.True(gc_heap.oldest_pin(pHeap) == &entries[1]);
        Assert.Equal((nuint)entries[0].first, (nuint)gc_heap.oldest_pinned_plug);
    }

    [Fact]
    public void MarkPhasePaddedPlugUsesMarkedHeaderBit()
    {
        MethodTable methodTable = default;
        CObjectHeader header = default;
        MethodTable* pMethodTable = &methodTable;
        CObjectHeader* pHeader = &header;

        pHeader->RawSetMethodTable(pMethodTable);
        gc_heap.set_plug_padded((byte*)pHeader);

        Assert.True(gc_heap.is_plug_padded((byte*)pHeader) != 0);
        Assert.Equal((nuint)pMethodTable | CObjectHeader.GC_MARKED, (nuint)pHeader->RawGetMethodTable());

        gc_heap.clear_plug_padded((byte*)pHeader);

        Assert.Equal(0, gc_heap.is_plug_padded((byte*)pHeader));
        Assert.Equal((nuint)pMethodTable, (nuint)pHeader->RawGetMethodTable());
    }

    [Fact]
    public void GcMechanismsUsesNativeLohCompactionRequestState()
    {
        using MarkPhaseStateScope _ = new();
        FieldInfo configField = GetGCConfigField("s_LOHCompactionMode");
        long savedConfig = (long)configField.GetValue(null);
        int savedAlways = gc_heap.loh_compaction_always_p;
        gc_loh_compaction_mode savedMode = gc_heap.loh_compaction_mode;

        try
        {
            configField.SetValue(null, 1L);
            gc_heap.initialize_loh_compaction_state();
            Assert.Equal(1, gc_heap.loh_compaction_always_p);
            Assert.Equal(gc_loh_compaction_mode.loh_compaction_default, gc_heap.loh_compaction_mode);

            gc_heap.settings.init_mechanisms();
            Assert.Equal(1, gc_heap.settings.loh_compaction);

            configField.SetValue(null, 0L);
            gc_heap.initialize_loh_compaction_state();
            gc_heap.loh_compaction_mode = gc_loh_compaction_mode.loh_compaction_once;
            gc_heap.settings.init_mechanisms();
            Assert.Equal(1, gc_heap.settings.loh_compaction);

            gc_heap.loh_compaction_mode = gc_loh_compaction_mode.loh_compaction_default;
            gc_heap.settings.init_mechanisms();
            Assert.Equal(0, gc_heap.settings.loh_compaction);
        }
        finally
        {
            configField.SetValue(null, savedConfig);
            gc_heap.loh_compaction_always_p = savedAlways;
            gc_heap.loh_compaction_mode = savedMode;
        }
    }

    [Fact]
    public void GcStaticStateInitializationPreservesNativeLohOrdering()
    {
        using MarkPhaseStateScope _ = new();
        FieldInfo configField = GetGCConfigField("s_LOHCompactionMode");
        long savedConfig = (long)configField.GetValue(null);
        int savedAlways = gc_heap.loh_compaction_always_p;
        gc_loh_compaction_mode savedMode = gc_heap.loh_compaction_mode;
#if USE_REGIONS && !MULTIPLE_HEAPS
        int savedGen0BricksCleared = gc_heap.gen0_bricks_cleared;
        int savedGen0MustClearBricks = gc_heap.gen0_must_clear_bricks;
#endif

        try
        {
            configField.SetValue(null, 1L);
#if USE_REGIONS && !MULTIPLE_HEAPS
            gc_heap.gen0_bricks_cleared = 1;
            gc_heap.gen0_must_clear_bricks = gc_heap.FFIND_DECAY;
#endif
            gc_heap.initialize_gc_static_state();

            Assert.Equal(0, gc_heap.settings.loh_compaction);
            Assert.Equal(1, gc_heap.loh_compaction_always_p);
            Assert.Equal(gc_loh_compaction_mode.loh_compaction_default, gc_heap.loh_compaction_mode);
#if USE_REGIONS && !MULTIPLE_HEAPS
            Assert.Equal(0, gc_heap.gen0_bricks_cleared);
            Assert.Equal(0, gc_heap.gen0_must_clear_bricks);
#endif
            gc_heap.mark_queue.verify_empty();

            gc_heap.settings.init_mechanisms();
            Assert.Equal(1, gc_heap.settings.loh_compaction);

            gc_heap.initialize_gc_static_state();
            Assert.Equal(0, gc_heap.settings.loh_compaction);
            Assert.Equal(1, gc_heap.loh_compaction_always_p);
            gc_heap.mark_queue.verify_empty();
        }
        finally
        {
            configField.SetValue(null, savedConfig);
            gc_heap.loh_compaction_always_p = savedAlways;
            gc_heap.loh_compaction_mode = savedMode;
#if USE_REGIONS && !MULTIPLE_HEAPS
            gc_heap.gen0_bricks_cleared = savedGen0BricksCleared;
            gc_heap.gen0_must_clear_bricks = savedGen0MustClearBricks;
#endif
        }
    }

#if DEBUG && BACKGROUND_GC
    [Fact]
    public void GcMechanismsFirstInitHonorsDebugLatencyMode()
    {
        using MarkPhaseStateScope _ = new();
        FieldInfo configField = GetGCConfigField("s_LatencyMode");
        long savedConfig = (long)configField.GetValue(null);

        try
        {
            configField.SetValue(null, (long)gc_pause_mode.pause_sustained_low_latency);
            gc_heap.settings.first_init();

            Assert.Equal(gc_pause_mode.pause_sustained_low_latency, gc_heap.settings.pause_mode);
        }
        finally
        {
            configField.SetValue(null, savedConfig);
        }
    }
#endif

#if USE_REGIONS && !MULTIPLE_HEAPS
    [Theory]
    [InlineData(4 * 1024 * 1024, 8192)]
    [InlineData(64 * 1024 * 1024, 32768)]
    [InlineData(256 * 1024 * 1024, 100 * 1024)]
    public void MarkPhaseOwnedMarkListUsesWksCapacityFormula(long segmentSize, long expectedCapacity)
    {
        using MarkPhaseStateScope _ = new();
        ResetOwnedMarkListState();
        FieldInfo segmentSizeField = GetGCConfigField("s_SegmentSize");
        long savedSegmentSize = (long)segmentSizeField.GetValue(null);

        try
        {
            SyncImports.ResetRecording();
            segmentSizeField.SetValue(null, segmentSize);

            Assert.True(gc_heap.initialize_mark_list());
            Assert.NotEqual((nuint)0, (nuint)gc_heap.g_mark_list);
            Assert.Equal((nuint)expectedCapacity, gc_heap.mark_list_size);
            Assert.Equal((nuint)expectedCapacity, gc_heap.g_mark_list_total_size);
            Assert.Equal(1, SyncImports.AllocCount);
            Assert.Equal((nuint)expectedCapacity * (nuint)sizeof(byte*), SyncImports.LastAllocSize);
        }
        finally
        {
            gc_heap.destroy_semi_shared();
            segmentSizeField.SetValue(null, savedSegmentSize);
        }
    }

    [Fact]
    public void MarkPhaseOwnedMarkListDoesNotPublishOnOom()
    {
        using MarkPhaseStateScope _ = new();
        ResetOwnedMarkListState();
        SyncImports.ResetRecording();
        SyncImports.FailNextAlloc = true;

        Assert.False(gc_heap.initialize_mark_list());

        Assert.False(SyncImports.FailNextAlloc);
        Assert.Equal(1, SyncImports.AllocCount);
        Assert.Equal(0, SyncImports.FreeCount);
        Assert.Equal((nuint)0, (nuint)gc_heap.g_mark_list);
        Assert.Equal((nuint)0, gc_heap.mark_list_size);
        Assert.Equal((nuint)0, gc_heap.g_mark_list_total_size);
        gc_heap.region_count = 2;
        Assert.False(gc_heap.setup_mark_state_for_collection());
        Assert.Equal(1, SyncImports.AllocCount);
        Assert.Equal((nuint)0, (nuint)gc_heap.g_mark_list_piece);
        Assert.Equal((nuint)0, (nuint)gc_heap.mark_list);
        Assert.Equal((nuint)0, (nuint)gc_heap.mark_list_index);
        Assert.Equal((nuint)0, (nuint)gc_heap.mark_list_end);
    }

    [Fact]
    public void MarkPhaseOwnedMarkListSetupUsesPartialAndFullGcEndpoints()
    {
        using MarkPhaseStateScope _ = new();
        ResetOwnedMarkListState();
        SyncImports.ResetRecording();

        try
        {
            Assert.True(gc_heap.initialize_mark_list());

            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen1;
            Assert.True(gc_heap.setup_mark_state_for_collection());
            Assert.Equal((nuint)gc_heap.g_mark_list, (nuint)gc_heap.mark_list);
            Assert.Equal((nuint)gc_heap.g_mark_list, (nuint)gc_heap.mark_list_index);
            Assert.Equal(
                (nuint)(gc_heap.g_mark_list + (nint)(gc_heap.mark_list_size - 1)),
                (nuint)gc_heap.mark_list_end);

            gc_heap.settings.condemned_generation = GCInterfaceOffsets.max_generation;
            Assert.True(gc_heap.setup_mark_state_for_collection());
            Assert.Equal((nuint)gc_heap.g_mark_list, (nuint)gc_heap.mark_list);
            Assert.Equal((nuint)gc_heap.g_mark_list, (nuint)gc_heap.mark_list_index);
            Assert.Equal((nuint)gc_heap.g_mark_list, (nuint)gc_heap.mark_list_end);
        }
        finally
        {
            gc_heap.destroy_semi_shared();
        }
    }

    [Fact]
    public void MarkPhaseOwnedRegionBackingGrowsReallocatesAndZeroes()
    {
        using MarkPhaseStateScope _ = new();
        ResetOwnedMarkListState();
        SyncImports.ResetRecording();

        try
        {
            Assert.True(gc_heap.initialize_mark_list());
            gc_heap.region_count = 2;
            gc_heap.grow_mark_list_piece();

            byte*** firstBacking = gc_heap.g_mark_list_piece;
            Assert.NotEqual((nuint)0, (nuint)firstBacking);
            Assert.Equal((nuint)2, gc_heap.g_mark_list_piece_size);
            Assert.Equal((nuint)4, gc_heap.g_mark_list_piece_total_size);
            Assert.Equal(2, SyncImports.AllocCount);
            Assert.Equal(0, SyncImports.FreeCount);
            Assert.Equal(4 * (nuint)sizeof(byte**), SyncImports.LastAllocSize);

            nuint* firstCounters = (nuint*)firstBacking;
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal((nuint)0, firstCounters[i]);
                firstCounters[i] = (nuint)(i + 1);
            }

            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen1;
            Assert.True(gc_heap.setup_mark_state_for_collection());
            Assert.Equal((nuint)firstCounters, (nuint)gc_heap.survived_per_region);
            Assert.Equal((nuint)(firstCounters + 2), (nuint)gc_heap.old_card_survived_per_region);
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal((nuint)0, firstCounters[i]);
            }

            gc_heap.region_count = 3;
            gc_heap.grow_mark_list_piece();

            byte*** secondBacking = gc_heap.g_mark_list_piece;
            Assert.NotEqual((nuint)0, (nuint)secondBacking);
            Assert.Equal((nuint)4, gc_heap.g_mark_list_piece_size);
            Assert.Equal((nuint)8, gc_heap.g_mark_list_piece_total_size);
            Assert.Equal(3, SyncImports.AllocCount);
            Assert.Equal(1, SyncImports.FreeCount);
            Assert.Equal(8 * (nuint)sizeof(byte**), SyncImports.LastAllocSize);

            nuint* secondCounters = (nuint*)secondBacking;
            for (int i = 0; i < 8; i++)
            {
                Assert.Equal((nuint)0, secondCounters[i]);
            }

            Assert.True(gc_heap.setup_mark_state_for_collection());
            Assert.Equal((nuint)secondCounters, (nuint)gc_heap.survived_per_region);
            Assert.Equal((nuint)(secondCounters + 4), (nuint)gc_heap.old_card_survived_per_region);
        }
        finally
        {
            if (gc_heap.g_mark_list_piece is not null)
            {
                SyncImports.ManagedGC_Free(gc_heap.g_mark_list_piece);
                gc_heap.g_mark_list_piece = null;
                gc_heap.g_mark_list_piece_size = 0;
                gc_heap.g_mark_list_piece_total_size = 0;
            }

            gc_heap.destroy_semi_shared();
        }

        Assert.Equal(3, SyncImports.FreeCount);
    }

    [Fact]
    public void MarkPhaseOwnedMarkListShutdownReleasesUnmanagedStorage()
    {
        using MarkPhaseStateScope _ = new();
        ResetOwnedMarkListState();
        SyncImports.ResetRecording();
        Assert.True(gc_heap.initialize_mark_list());

        gc_heap.destroy_semi_shared();

        Assert.Equal(1, SyncImports.AllocCount);
        Assert.Equal(1, SyncImports.FreeCount);
        Assert.Equal((nuint)0, (nuint)gc_heap.g_mark_list);
        gc_heap.destroy_semi_shared();
        Assert.Equal(1, SyncImports.FreeCount);
        Assert.Equal(1, SyncImports.FreeCount);
    }

    [Fact]
    public void MarkPhaseBoundaryWritesThroughInclusiveEndAndStopsAtExhaustion()
    {
        using MarkPhaseStateScope _ = new();
        byte** markList = stackalloc byte*[2];
        gc_heap heap = default;
        gc_heap* pHeap = &heap;

        markList[0] = (byte*)0xDEAD;
        markList[1] = (byte*)0xBEEF;
        gc_heap.mark_list = markList;
        gc_heap.mark_list_index = markList;
        gc_heap.mark_list_end = markList + 1;
        gc_heap.slow = (byte*)nuint.MaxValue;

        gc_heap.m_boundary(pHeap, (byte*)0x2000);
        gc_heap.m_boundary(pHeap, (byte*)0x1000);
        gc_heap.m_boundary(pHeap, (byte*)0x3000);

        Assert.Equal((nuint)0x2000, (nuint)markList[0]);
        Assert.Equal((nuint)0x1000, (nuint)markList[1]);
        Assert.Equal((nuint)(markList + 2), (nuint)gc_heap.mark_list_index);
        Assert.Equal((nuint)0x1000, (nuint)gc_heap.slow);
        Assert.Equal((nuint)0x3000, (nuint)gc_heap.shigh);
    }

    [Fact]
    public void MarkPhaseFullGcBoundarySuppressesListWritesAndTracksExtrema()
    {
        using MarkPhaseStateScope _ = new();
        byte** markList = stackalloc byte*[1];
        gc_heap heap = default;
        gc_heap* pHeap = &heap;

        markList[0] = (byte*)0xDEAD;
        gc_heap.mark_list = markList;
        gc_heap.mark_list_index = markList;
        gc_heap.mark_list_end = markList;
        gc_heap.slow = (byte*)nuint.MaxValue;

        gc_heap.m_boundary_fullgc(pHeap, (byte*)0x3000);
        gc_heap.m_boundary_fullgc(pHeap, (byte*)0x1000);

        Assert.Equal((nuint)0xDEAD, (nuint)markList[0]);
        Assert.Equal((nuint)markList, (nuint)gc_heap.mark_list_index);
        Assert.Equal((nuint)0x1000, (nuint)gc_heap.slow);
        Assert.Equal((nuint)0x3000, (nuint)gc_heap.shigh);
    }

    [Fact]
    public void MarkPhaseCollectionSetupPreservesWksSettingsQueueAndCounterLifecycle()
    {
        using MarkPhaseStateScope _ = new();
        byte** markList = stackalloc byte*[3];
        nuint* survived = stackalloc nuint[3];
        nuint* oldCardSurvived = stackalloc nuint[3];
        MethodTable methodTable = default;
        CObjectHeader header = default;

        gc_heap.settings.gc_index = 17;
        gc_heap.settings.card_bundles = 1;
        gc_heap.settings.first_init();

        Assert.Equal((nuint)0, gc_heap.settings.gc_index);
        Assert.Equal(0, gc_heap.settings.condemned_generation);
        Assert.Equal(0, gc_heap.settings.promotion);
        Assert.Equal(1, gc_heap.settings.compaction);
        Assert.Equal(0u, gc_heap.settings.concurrent);
        Assert.Equal(1, gc_heap.settings.card_bundles);
        Assert.Equal(gc_reason.reason_empty, gc_heap.settings.reason);

        gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen1;
        survived[0] = 1;
        survived[1] = 2;
        survived[2] = 3;
        oldCardSurvived[0] = 4;
        oldCardSurvived[1] = 5;
        oldCardSurvived[2] = 6;
        Assert.True(gc_heap.setup_mark_state_for_collection(markList, 3, survived, oldCardSurvived, 3));

        Assert.Equal((nuint)markList, (nuint)gc_heap.mark_list);
        Assert.Equal((nuint)markList, (nuint)gc_heap.mark_list_index);
        Assert.Equal((nuint)(markList + 2), (nuint)gc_heap.mark_list_end);
        Assert.Equal((nuint)3, gc_heap.region_count);
        Assert.Equal((nuint)survived, (nuint)gc_heap.survived_per_region);
        Assert.Equal((nuint)oldCardSurvived, (nuint)gc_heap.old_card_survived_per_region);
        Assert.Equal((nuint)0, survived[0]);
        Assert.Equal((nuint)0, survived[1]);
        Assert.Equal((nuint)0, survived[2]);
        Assert.Equal((nuint)0, oldCardSurvived[0]);
        Assert.Equal((nuint)0, oldCardSurvived[1]);
        Assert.Equal((nuint)0, oldCardSurvived[2]);
        Assert.Equal(nuint.MaxValue, (nuint)gc_heap.slow);
        Assert.Equal((nuint)0, (nuint)gc_heap.shigh);

        header.RawSetMethodTable(&methodTable);
        Assert.Equal((nuint)0, (nuint)gc_heap.mark_queue.queue_mark((byte*)&header));
        gc_heap.initialize_mark_phase_state();
        Assert.Equal((nuint)0, (nuint)gc_heap.mark_queue.get_next_marked());
        Assert.Equal((nuint)0, (nuint)gc_heap.mark_list);
        Assert.Equal((nuint)0, (nuint)gc_heap.survived_per_region);
        Assert.Equal(nuint.MaxValue, (nuint)gc_heap.min_overflow_address);
        Assert.Equal((nuint)0, (nuint)gc_heap.max_overflow_address);

        gc_heap.settings.init_mechanisms();
        gc_heap.settings.condemned_generation = GCInterfaceOffsets.max_generation;
        survived[0] = 7;
        oldCardSurvived[0] = 8;
        Assert.True(gc_heap.setup_mark_state_for_collection(markList, 3, survived, oldCardSurvived, 3));

        Assert.Equal((nuint)markList, (nuint)gc_heap.mark_list_index);
        Assert.Equal((nuint)markList, (nuint)gc_heap.mark_list_end);
        Assert.Equal((nuint)0, survived[0]);
        Assert.Equal((nuint)0, oldCardSurvived[0]);
        gc_heap.m_boundary_fullgc(null, (byte*)0x2000);
        Assert.Equal((nuint)markList, (nuint)gc_heap.mark_list_index);
    }

    [Fact]
    public void MarkPhaseCollectionSetupRejectsUnavailableMarkListStorage()
    {
        using MarkPhaseStateScope _ = new();
        nuint* survived = stackalloc nuint[1];
        nuint* oldCardSurvived = stackalloc nuint[1];
        byte** markList = stackalloc byte*[1];

        Assert.False(gc_heap.setup_mark_state_for_collection(null, 1, survived, oldCardSurvived, 1));
        Assert.Equal((nuint)0, (nuint)gc_heap.mark_list);
        Assert.Equal((nuint)0, (nuint)gc_heap.mark_list_index);
        Assert.Equal((nuint)0, (nuint)gc_heap.mark_list_end);
        Assert.Equal((nuint)0, (nuint)gc_heap.survived_per_region);
        Assert.Equal((nuint)0, (nuint)gc_heap.old_card_survived_per_region);
        Assert.Equal(nuint.MaxValue, (nuint)gc_heap.slow);
        Assert.Equal((nuint)0, (nuint)gc_heap.shigh);

        Assert.False(gc_heap.setup_mark_state_for_collection(markList, 0, survived, oldCardSurvived, 1));
        Assert.Equal((nuint)0, (nuint)gc_heap.mark_list);
        Assert.Equal((nuint)0, (nuint)gc_heap.mark_list_index);
        Assert.Equal((nuint)0, (nuint)gc_heap.mark_list_end);
        Assert.Equal((nuint)0, (nuint)gc_heap.survived_per_region);
        Assert.Equal((nuint)0, (nuint)gc_heap.old_card_survived_per_region);
    }

    [Fact]
    public void MarkPhaseCollectionSetupUsesSingleEntryForFullGc()
    {
        using MarkPhaseStateScope _ = new();
        byte** markList = stackalloc byte*[1];

        markList[0] = (byte*)0xDEAD;
        gc_heap.settings.condemned_generation = GCInterfaceOffsets.max_generation;
        Assert.True(gc_heap.setup_mark_state_for_collection(markList, 1, null, null, 0));

        Assert.Equal((nuint)markList, (nuint)gc_heap.mark_list);
        Assert.Equal((nuint)markList, (nuint)gc_heap.mark_list_index);
        Assert.Equal((nuint)markList, (nuint)gc_heap.mark_list_end);
        gc_heap.m_boundary_fullgc(null, (byte*)0x1000);
        Assert.Equal((nuint)0xDEAD, (nuint)markList[0]);
        Assert.Equal((nuint)markList, (nuint)gc_heap.mark_list_index);
    }

    [Fact]
    public void MarkPhasePromotedBytesUseRegionIndexAndObjectSizeOverloads()
    {
        using MarkPhaseStateScope _ = new();
        byte* storage = stackalloc byte[128];
        nuint* survived = stackalloc nuint[16];
        nuint* oldCardSurvived = stackalloc nuint[16];
        MethodTable methodTable = default;
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        byte* object1 = storage + 16;
        byte* object2 = storage + 48;
        byte* savedLowestAddress = GCCommon.g_gc_lowest_address;
        byte* savedHighestAddress = GCCommon.g_gc_highest_address;
        nuint savedMinSegmentSizeShr = gc_heap.min_segment_size_shr;
#if DEBUG
        nuint savedPromoted = gc_heap.g_promoted;
#endif

        try
        {
            methodTable.m_uBaseSize = 16;
            ((CObjectHeader*)object1)->RawSetMethodTable(&methodTable);
            ((CObjectHeader*)object2)->RawSetMethodTable(&methodTable);
            GCCommon.g_gc_lowest_address = storage;
            GCCommon.g_gc_highest_address = storage + 128;
            gc_heap.min_segment_size_shr = 4;
            pHeap->heap_number = 12;
            gc_heap.survived_per_region = survived;
            gc_heap.old_card_survived_per_region = oldCardSurvived;

            nuint object1RegionIndex = gc_heap.get_basic_region_index_for_address(object1);
            nuint object2RegionIndex = gc_heap.get_basic_region_index_for_address(object2);
            Assert.Equal(object1RegionIndex + 2, object2RegionIndex);

#if DEBUG
            gc_heap.init_promoted_bytes();
            Assert.Equal((nuint)0, gc_heap.promoted_bytes(pHeap->heap_number));
#endif

            gc_heap.add_to_promoted_bytes(pHeap, object1, pHeap->heap_number);
            gc_heap.add_to_promoted_bytes(pHeap, object1, 8, pHeap->heap_number);
            gc_heap.add_to_promoted_bytes(pHeap, object2, 32, pHeap->heap_number);

            Assert.Equal((nuint)24, survived[(nint)object1RegionIndex]);
            Assert.Equal((nuint)32, survived[(nint)object2RegionIndex]);
            Assert.Equal((nuint)0, oldCardSurvived[(nint)object1RegionIndex]);
            Assert.Equal((nuint)0, oldCardSurvived[(nint)object2RegionIndex]);

#if DEBUG
            Assert.Equal((nuint)56, gc_heap.promoted_bytes(pHeap->heap_number));
            gc_heap.init_promoted_bytes();
            Assert.Equal((nuint)0, gc_heap.promoted_bytes(pHeap->heap_number));
            Assert.Equal((nuint)24, survived[(nint)object1RegionIndex]);
            Assert.Equal((nuint)32, survived[(nint)object2RegionIndex]);
#endif
        }
        finally
        {
            GCCommon.g_gc_lowest_address = savedLowestAddress;
            GCCommon.g_gc_highest_address = savedHighestAddress;
            gc_heap.min_segment_size_shr = savedMinSegmentSizeShr;
#if DEBUG
            gc_heap.g_promoted = savedPromoted;
#endif
        }
    }

#if !MULTIPLE_HEAPS
    [Fact]
    public void MarkPhaseSyncPromotedBytesTransfersAndResetsCondemnedRegionChains()
    {
        using MarkPhaseStateScope _ = new();
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        heap_segment* regions = stackalloc heap_segment[4];
        generation* generationTable = gc_heap.generation_table_of(pHeap);
        nuint* survived = stackalloc nuint[4];
        nuint* oldCardSurvived = stackalloc nuint[4];
        nuint savedMinSegmentSizeShr = gc_heap.min_segment_size_shr;
        byte* savedLowestAddress = GCCommon.g_gc_lowest_address;
        byte* savedHighestAddress = GCCommon.g_gc_highest_address;

        try
        {
            gc_heap.min_segment_size_shr = 12;
            GCCommon.g_gc_lowest_address = (byte*)0x1000;
            GCCommon.g_gc_highest_address = (byte*)0x5000;
            heap_segment.heap_segment_mem(&regions[0]) = (byte*)0x1000;
            heap_segment.heap_segment_mem(&regions[1]) = (byte*)0x2000;
            heap_segment.heap_segment_mem(&regions[2]) = (byte*)0x3000;
            heap_segment.heap_segment_mem(&regions[3]) = (byte*)0x4000;
            heap_segment.heap_segment_next(&regions[0]) = &regions[1];

            generation.generation_start_segment(
                generationTable + (int)gc_generation_num.soh_gen0) = &regions[0];
            generation.generation_start_segment(
                generationTable + (int)gc_generation_num.soh_gen1) = &regions[2];
            generation.generation_start_segment(
                generationTable + (int)gc_generation_num.soh_gen2) = &regions[3];

            survived[0] = 10;
            survived[1] = 20;
            survived[2] = 30;
            survived[3] = 40;
            oldCardSurvived[0] = 1;
            oldCardSurvived[1] = 2;
            oldCardSurvived[2] = 3;
            oldCardSurvived[3] = 4;
            gc_heap.survived_per_region = survived;
            gc_heap.old_card_survived_per_region = oldCardSurvived;
            heap_segment.heap_segment_survived(&regions[3]) = 99;
            heap_segment.heap_segment_old_card_survived(&regions[3]) = 9;
            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen1;

            gc_heap.sync_promoted_bytes(pHeap);

            Assert.Equal((nuint)10, heap_segment.heap_segment_survived(&regions[0]));
            Assert.Equal((nuint)20, heap_segment.heap_segment_survived(&regions[1]));
            Assert.Equal((nuint)30, heap_segment.heap_segment_survived(&regions[2]));
            Assert.Equal(1, heap_segment.heap_segment_old_card_survived(&regions[0]));
            Assert.Equal(2, heap_segment.heap_segment_old_card_survived(&regions[1]));
            Assert.Equal(3, heap_segment.heap_segment_old_card_survived(&regions[2]));
            Assert.Equal((nuint)99, heap_segment.heap_segment_survived(&regions[3]));
            Assert.Equal(9, heap_segment.heap_segment_old_card_survived(&regions[3]));

            survived[0] = 0;
            survived[1] = 0;
            survived[2] = 0;
            oldCardSurvived[0] = 0;
            oldCardSurvived[1] = 0;
            oldCardSurvived[2] = 0;

            gc_heap.sync_promoted_bytes(pHeap);

            Assert.Equal((nuint)0, heap_segment.heap_segment_survived(&regions[0]));
            Assert.Equal((nuint)0, heap_segment.heap_segment_survived(&regions[1]));
            Assert.Equal((nuint)0, heap_segment.heap_segment_survived(&regions[2]));
            Assert.Equal(0, heap_segment.heap_segment_old_card_survived(&regions[0]));
            Assert.Equal(0, heap_segment.heap_segment_old_card_survived(&regions[1]));
            Assert.Equal(0, heap_segment.heap_segment_old_card_survived(&regions[2]));
            Assert.Equal((nuint)99, heap_segment.heap_segment_survived(&regions[3]));
            Assert.Equal(9, heap_segment.heap_segment_old_card_survived(&regions[3]));

            gc_heap.survived_per_region = null;
            gc_heap.old_card_survived_per_region = null;
            gc_heap.sync_promoted_bytes(pHeap);

            Assert.Equal((nuint)0, heap_segment.heap_segment_survived(&regions[0]));
            Assert.Equal((nuint)0, heap_segment.heap_segment_survived(&regions[1]));
            Assert.Equal((nuint)0, heap_segment.heap_segment_survived(&regions[2]));
            Assert.Equal(0, heap_segment.heap_segment_old_card_survived(&regions[0]));
            Assert.Equal(0, heap_segment.heap_segment_old_card_survived(&regions[1]));
            Assert.Equal(0, heap_segment.heap_segment_old_card_survived(&regions[2]));
            Assert.Equal((nuint)99, heap_segment.heap_segment_survived(&regions[3]));
            Assert.Equal(9, heap_segment.heap_segment_old_card_survived(&regions[3]));
        }
        finally
        {
            gc_heap.min_segment_size_shr = savedMinSegmentSizeShr;
            GCCommon.g_gc_lowest_address = savedLowestAddress;
            GCCommon.g_gc_highest_address = savedHighestAddress;
        }
    }
#endif

    [Fact]
    public void MarkPhasePromotedBytesPreserveNativeUnsignedOverflow()
    {
        using MarkPhaseStateScope _ = new();
        byte* storage = stackalloc byte[32];
        nuint* survived = stackalloc nuint[1];
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        byte* savedLowestAddress = GCCommon.g_gc_lowest_address;
        byte* savedHighestAddress = GCCommon.g_gc_highest_address;
        nuint savedMinSegmentSizeShr = gc_heap.min_segment_size_shr;
#if DEBUG
        nuint savedPromoted = gc_heap.g_promoted;
#endif

        try
        {
            GCCommon.g_gc_lowest_address = storage;
            GCCommon.g_gc_highest_address = storage + 32;
            gc_heap.min_segment_size_shr = 4;
            gc_heap.survived_per_region = survived;
            survived[0] = nuint.MaxValue - 1;

#if DEBUG
            gc_heap.g_promoted = nuint.MaxValue - 1;
#endif

            gc_heap.add_to_promoted_bytes(pHeap, storage, 2, thread: 0);

            Assert.Equal((nuint)0, survived[0]);
#if DEBUG
            Assert.Equal((nuint)0, gc_heap.g_promoted);
#endif
        }
        finally
        {
            GCCommon.g_gc_lowest_address = savedLowestAddress;
            GCCommon.g_gc_highest_address = savedHighestAddress;
            gc_heap.min_segment_size_shr = savedMinSegmentSizeShr;
#if DEBUG
            gc_heap.g_promoted = savedPromoted;
#endif
        }
    }
#endif

    [Fact]
    public void MarkPhaseEnqueueAndSavePreserveShortPlugState()
    {
        using MarkPhaseStateScope _ = new();
        byte* storage = stackalloc byte[7 * sizeof(plug_and_gap)];
        mark* entries = stackalloc mark[1];
        MethodTable methodTable = default;
        MethodTable* pMethodTable = &methodTable;
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        byte* plug = storage + (2 * sizeof(plug_and_gap));
        byte* lastObjectBeforePlug = plug - (2 * sizeof(nuint));
        byte* postPlug = storage + (6 * sizeof(plug_and_gap));
        byte* lastObjectBeforePostPlug = postPlug - (2 * sizeof(nuint));
        gap_reloc_pair* preInfo = (gap_reloc_pair*)(((plug_and_gap*)plug) - 1);
        gap_reloc_pair* postInfo = (gap_reloc_pair*)(((plug_and_gap*)postPlug) - 1);

        entries[0] = default;
        methodTable.m_uFlags = 0;
        ((CObjectHeader*)lastObjectBeforePlug)->RawSetMethodTable(pMethodTable);
        ((CObjectHeader*)lastObjectBeforePostPlug)->RawSetMethodTable(pMethodTable);
        gc_heap.set_plug_padded(lastObjectBeforePlug);
        gc_heap.set_plug_padded(lastObjectBeforePostPlug);
        preInfo->gap = 11;
        preInfo->reloc = 12;
        postInfo->gap = 21;
        postInfo->reloc = 22;
        gc_heap.make_mark_stack(pHeap, entries);
        gc_heap.mark_stack_array_length = 1;

        gc_heap.enque_pinned_plug(pHeap, plug, 1, lastObjectBeforePlug);

        Assert.True(mark.pre_short_p(&entries[0]) != 0);
        Assert.Equal((nuint)11, entries[0].saved_pre_plug.gap);
        Assert.Equal((nuint)12, entries[0].saved_pre_plug.reloc);
        Assert.Equal((nuint)11, entries[0].saved_pre_plug_reloc.gap);
        Assert.Equal((nuint)12, entries[0].saved_pre_plug_reloc.reloc);
        Assert.Equal((nuint)pMethodTable, *(nuint*)&entries[0].saved_pre_plug.m_pair);
        Assert.Equal(
            (nuint)pMethodTable | CObjectHeader.GC_MARKED,
            *(nuint*)&entries[0].saved_pre_plug_reloc.m_pair);
        Assert.True(gc_heap.is_plug_padded(lastObjectBeforePlug) != 0);
        Assert.Equal(
            (nuint)pMethodTable | CObjectHeader.GC_MARKED,
            (nuint)((CObjectHeader*)lastObjectBeforePlug)->RawGetMethodTable());

        gc_heap.mark_stack_tos = 1;
        gc_heap.save_post_plug_info(pHeap, plug, lastObjectBeforePostPlug, postPlug);

        Assert.True(mark.post_short_p(&entries[0]) != 0);
        Assert.Equal((nuint)(postPlug - sizeof(plug_and_gap)), (nuint)entries[0].saved_post_plug_info_start);
        Assert.Equal((nuint)21, entries[0].saved_post_plug.gap);
        Assert.Equal((nuint)22, entries[0].saved_post_plug.reloc);
        Assert.Equal((nuint)21, entries[0].saved_post_plug_reloc.gap);
        Assert.Equal((nuint)22, entries[0].saved_post_plug_reloc.reloc);
        Assert.Equal((nuint)pMethodTable, *(nuint*)&entries[0].saved_post_plug.m_pair);
        Assert.Equal(
            (nuint)pMethodTable | CObjectHeader.GC_MARKED,
            *(nuint*)&entries[0].saved_post_plug_reloc.m_pair);
        Assert.True(gc_heap.is_plug_padded(lastObjectBeforePostPlug) != 0);
        Assert.Equal(
            (nuint)pMethodTable | CObjectHeader.GC_MARKED,
            (nuint)((CObjectHeader*)lastObjectBeforePostPlug)->RawGetMethodTable());
#if DEBUG
        Assert.Equal((nuint)1, entries[0].saved_post_plug_debug.gap);
#endif
    }

    [Fact]
    public void ExpandPaddingUsesDirectAndSavedPlugLocations()
    {
        byte* storage = stackalloc byte[3 * sizeof(plug_and_gap)];
        MethodTable methodTable = default;
        MethodTable* pMethodTable = &methodTable;
        mark entry = default;
        mark* pEntry = &entry;
        entry.first = storage + (2 * sizeof(plug_and_gap));
        byte* oldLocation = entry.first - (2 * sizeof(nuint));
        CObjectHeader* directHeader = (CObjectHeader*)oldLocation;
        CObjectHeader* savedHeader = (CObjectHeader*)&entry.saved_pre_plug_reloc.m_pair;
        directHeader->RawSetMethodTable(pMethodTable);
        savedHeader->RawSetMethodTable(pMethodTable);

        try
        {
            Assert.Equal((nuint)savedHeader, (nuint)gc_heap.get_plug_start_in_saved(oldLocation, pEntry));

            gc_heap.set_padding_in_expand(oldLocation, 0, pEntry);
            Assert.True(gc_heap.is_plug_padded(oldLocation) != 0);
            Assert.Equal((nuint)pMethodTable, (nuint)savedHeader->RawGetMethodTable());
            gc_heap.clear_padding_in_expand(oldLocation, 0, pEntry);
            Assert.Equal(0, gc_heap.is_plug_padded(oldLocation));

            gc_heap.set_padding_in_expand(oldLocation, 1, pEntry);
            Assert.Equal((nuint)pMethodTable, (nuint)directHeader->RawGetMethodTable());
            Assert.True(gc_heap.is_plug_padded((byte*)savedHeader) != 0);
            gc_heap.clear_padding_in_expand(oldLocation, 1, pEntry);
            Assert.Equal(0, gc_heap.is_plug_padded((byte*)savedHeader));
        }
        finally
        {
            directHeader->RawSetMethodTable(pMethodTable);
            savedHeader->RawSetMethodTable(pMethodTable);
        }
    }

    [Fact]
    public void MarkPhaseEnqueueAndSaveRecordShortObjectReferenceBits()
    {
        using MarkPhaseStateScope _ = new();
        const int GapOffset = 2;
        byte* storage = stackalloc byte[7 * sizeof(plug_and_gap)];
        byte* descriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        mark* entries = stackalloc mark[1];
        int descriptorSize = sizeof(nuint) + sizeof(CGCDescSeries);
        MethodTable* methodTable = (MethodTable*)(descriptorStorage + descriptorSize);
        CGCDescSeries* series = (CGCDescSeries*)descriptorStorage;
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        byte* plug = storage + (2 * sizeof(plug_and_gap));
        byte* lastObjectBeforePlug = plug - (3 * sizeof(nuint));
        byte* postPlug = storage + (6 * sizeof(plug_and_gap));
        byte* lastObjectBeforePostPlug = postPlug - (3 * sizeof(nuint));
        byte** preReference = (byte**)(lastObjectBeforePlug + sizeof(nuint));
        byte** postReference = (byte**)(lastObjectBeforePostPlug + sizeof(nuint));
        nuint shortObjectSize = 3 * (nuint)sizeof(nuint);

        entries[0] = default;
        methodTable->m_uFlags = MethodTable.HasPointersFlag;
        *((nuint*)methodTable - 1) = 1;
        series->seriessize = unchecked((nuint)(-(nint)(shortObjectSize - (nuint)sizeof(nuint))));
        series->startoffset = (nuint)sizeof(nuint);
        ((CObjectHeader*)lastObjectBeforePlug)->RawSetMethodTable(methodTable);
        ((CObjectHeader*)lastObjectBeforePostPlug)->RawSetMethodTable(methodTable);
        gc_heap.set_plug_padded(lastObjectBeforePlug);
        gc_heap.set_plug_padded(lastObjectBeforePostPlug);
        gc_heap.make_mark_stack(pHeap, entries);
        gc_heap.mark_stack_array_length = 1;

        Assert.Equal(
            (nuint)GapOffset,
            ((nuint)preReference - ((nuint)plug - (nuint)sizeof(gap_reloc_pair) - (nuint)sizeof(nuint)))
            / (nuint)sizeof(byte*));
        Assert.Equal(
            (nuint)GapOffset,
            ((nuint)postReference - ((nuint)postPlug - (nuint)sizeof(gap_reloc_pair) - (nuint)sizeof(nuint)))
            / (nuint)sizeof(byte*));

        gc_heap.enque_pinned_plug(pHeap, plug, 1, lastObjectBeforePlug);

        Assert.True(mark.pre_short_p(&entries[0]) != 0);
        Assert.True(mark.pre_short_bit_p(&entries[0], (nuint)GapOffset) != 0);
        Assert.True(gc_heap.is_plug_padded(lastObjectBeforePlug) != 0);

        gc_heap.mark_stack_tos = 1;
        gc_heap.save_post_plug_info(pHeap, plug, lastObjectBeforePostPlug, postPlug);

        Assert.True(mark.post_short_p(&entries[0]) != 0);
        Assert.True(mark.post_short_bit_p(&entries[0], (nuint)GapOffset) != 0);
        Assert.True(gc_heap.is_plug_padded(lastObjectBeforePostPlug) != 0);
    }

    [Fact]
    public void MarkPhaseMergeRecoversSavedPostPlugBeforeExtending()
    {
        using MarkPhaseStateScope _ = new();
        byte* storage = stackalloc byte[2 * sizeof(plug_and_gap)];
        mark* entries = stackalloc mark[1];
        gc_heap heap = default;
        gc_heap* pHeap = &heap;

        entries[0] = default;
        gc_heap.make_mark_stack(pHeap, entries);
        entries[0].first = storage + sizeof(plug_and_gap);
        entries[0].len = (nuint)sizeof(plug_and_gap);
        entries[0].saved_post_p = 1;
        entries[0].saved_post_plug = Pair(1, 2, 3, 4);
        gc_heap.mark_stack_tos = 1;

        gc_heap.merge_with_last_pinned_plug(pHeap, entries[0].first, 16);

        AssertPair(*(gap_reloc_pair*)(storage + sizeof(plug_and_gap)), 1, 2, 3, 4);
        Assert.Equal(0, entries[0].saved_post_p);
        Assert.Equal((nuint)(sizeof(plug_and_gap) + 16), entries[0].len);
    }

    [Fact]
    public void MarkPhaseMarkStackGrowthCopiesEntriesAndReleasesOldStorage()
    {
        SyncImports.ResetRecording();
        nuint length = 2;
        mark* stack = (mark*)SyncImports.ManagedGC_AllocZeroed(length * (nuint)sizeof(mark));
        Assert.NotEqual((nuint)0, (nuint)stack);
        stack[0].first = (byte*)0x1000;
        stack[0].len = 11;
        stack[0].saved_pre_p = 1;
        stack[1].first = (byte*)0x2000;
        stack[1].len = 22;
        stack[1].saved_post_p = 1;

        try
        {
            Assert.Equal(1, gc_heap.grow_mark_stack(ref stack, ref length, 1));

            Assert.Equal((nuint)4, length);
            Assert.Equal(2, SyncImports.AllocCount);
            Assert.Equal(1, SyncImports.FreeCount);
            Assert.Equal(4 * (nuint)sizeof(mark), SyncImports.LastAllocSize);
            Assert.Equal((nuint)0x1000, (nuint)stack[0].first);
            Assert.Equal((nuint)11, stack[0].len);
            Assert.Equal(1, stack[0].saved_pre_p);
            Assert.Equal((nuint)0x2000, (nuint)stack[1].first);
            Assert.Equal((nuint)22, stack[1].len);
            Assert.Equal(1, stack[1].saved_post_p);
        }
        finally
        {
            SyncImports.ManagedGC_Free(stack);
        }

        Assert.Equal(2, SyncImports.FreeCount);
    }

    [Fact]
    public void MarkPhaseMarkStackGrowthFailurePreservesOwnershipAndState()
    {
        SyncImports.ResetRecording();
        mark* stack = (mark*)0x1000;
        nuint length = 2;
        SyncImports.FailNextAlloc = true;

        Assert.Equal(0, gc_heap.grow_mark_stack(ref stack, ref length, 1));

        Assert.Equal((nuint)0x1000, (nuint)stack);
        Assert.Equal((nuint)2, length);
        Assert.Equal(1, SyncImports.AllocCount);
        Assert.Equal(0, SyncImports.FreeCount);
    }

    [Fact]
    public void MarkPhaseEnqueueGrowthFailureReportsFatalErrorAndPreservesMarkStack()
    {
        using MarkPhaseStateScope _ = new();
        SyncImports.ResetRecording();
        mark* stack = (mark*)SyncImports.ManagedGC_AllocZeroed((nuint)sizeof(mark));
        gc_heap heap = default;
        gc_heap* pHeap = &heap;

        Assert.NotEqual((nuint)0, (nuint)stack);
        stack[0].first = (byte*)0x1000;
        stack[0].len = 11;
        gc_heap.mark_stack_array = stack;
        gc_heap.mark_stack_array_length = 1;
        gc_heap.mark_stack_tos = 1;

        try
        {
            SyncImports.FailNextAlloc = true;

            Assert.Throws<InvalidOperationException>(
                () => gc_heap.enque_pinned_plug(pHeap, (byte*)0x2000, 0, null));

            Assert.False(SyncImports.FailNextAlloc);
            Assert.Equal(2, SyncImports.AllocCount);
            Assert.Equal(
                gc_rand.MARK_STACK_INITIAL_LENGTH * (nuint)sizeof(mark),
                SyncImports.LastAllocSize);
            Assert.Equal(0, SyncImports.FreeCount);
            Assert.Equal((nuint)stack, (nuint)gc_heap.mark_stack_array);
            Assert.Equal((nuint)1, gc_heap.mark_stack_array_length);
            Assert.Equal((nuint)1, gc_heap.mark_stack_tos);
            Assert.Equal((nuint)0x1000, (nuint)stack[0].first);
            Assert.Equal((nuint)11, stack[0].len);
        }
        finally
        {
            SyncImports.FailNextAlloc = false;
            SyncImports.ManagedGC_Free(stack);
        }

        Assert.Equal(1, SyncImports.FreeCount);
    }

    [Fact]
    public void MarkPhaseObjectHeaderSpecialBitsPreserveMethodTable()
    {
        MethodTable methodTable = default;
        CObjectHeader header = default;
        MethodTable* pMethodTable = &methodTable;
        CObjectHeader* pHeader = &header;

        Assert.Equal((nuint)0, (nuint)pMethodTable & ((nuint)sizeof(nuint) - 1));

        pHeader->RawSetMethodTable((MethodTable*)((nuint)pMethodTable | CObjectHeader.GC_MARKED));
        Assert.True(pHeader->IsMarked() != 0);
        Assert.Equal((nuint)pMethodTable, (nuint)pHeader->GetMethodTable());

        nuint specialBits = gc_heap.clear_special_bits((byte*)pHeader);
        Assert.Equal(CObjectHeader.GC_MARKED, specialBits);
        Assert.Equal((nuint)pMethodTable, (nuint)pHeader->RawGetMethodTable());

        gc_heap.set_special_bits((byte*)pHeader, specialBits);
        Assert.Equal((nuint)pMethodTable | CObjectHeader.GC_MARKED, (nuint)pHeader->RawGetMethodTable());

#if TARGET_64BIT && !TARGET_WASM
        pHeader->SetBGCMarkBit();
        pHeader->SetFreeObjInCompactBit();
        Assert.True(pHeader->IsBGCMarkBitSet() != 0);
        Assert.True(pHeader->IsFreeObjInCompactBitSet() != 0);

        pHeader->ClearMarked();
        Assert.Equal(
            (nuint)pMethodTable | CObjectHeader.BGC_MARKED_BY_FGC | CObjectHeader.MAKE_FREE_OBJ_IN_COMPACT,
            (nuint)pHeader->RawGetMethodTable());
#else
        pHeader->ClearMarked();
        Assert.Equal((nuint)pMethodTable, (nuint)pHeader->RawGetMethodTable());
#endif
    }

    [Fact]
    public void MarkPhaseObjectHeaderReadsNativeMethodTableFlags()
    {
        MethodTable methodTable = default;
        CObjectHeader header = default;
        MethodTable* pMethodTable = &methodTable;
        CObjectHeader* pHeader = &header;

        Assert.Equal((nuint)sizeof(nuint), (nuint)sizeof(CObjectHeader));
#if TARGET_64BIT
        Assert.Equal((nuint)24, (nuint)sizeof(MethodTable));
#else
        Assert.Equal((nuint)20, (nuint)sizeof(MethodTable));
#endif

        pMethodTable->m_uFlags = MethodTable.HasPointersFlag;
        pMethodTable->m_uBaseSize = 0x1234;
        pHeader->RawSetMethodTable(pMethodTable);

        Assert.Equal((uint)0x1234, pHeader->GetMethodTable()->GetBaseSize());
        Assert.True(pHeader->ContainsGCPointers() != 0);
        Assert.True(pHeader->ContainsGCPointersOrCollectible() != 0);
        Assert.True(gc_heap.contain_pointers((byte*)pHeader) != 0);
        Assert.True(gc_heap.contain_pointers_or_collectible((byte*)pHeader) != 0);
        Assert.Equal((nuint)pMethodTable, (nuint)gc_heap.method_table((byte*)pHeader));

        pMethodTable->m_uFlags = MethodTable.CollectibleFlag;
        Assert.Equal(0, pHeader->ContainsGCPointers());
        Assert.Equal(0, pHeader->ContainsGCPointersOrCollectible());
        Assert.Equal(0, gc_heap.contain_pointers_or_collectible((byte*)pHeader));
    }

#if USE_REGIONS
    [Fact]
    public void SweepPhaseUohObjectMarkedPreservesNativeMarkPinAndRangeTransitions()
    {
        byte* storage = stackalloc byte[2 * sizeof(nuint)];
        ObjHeader* objHeader = (ObjHeader*)storage;
        CObjectHeader* @object = (CObjectHeader*)(storage + sizeof(ObjHeader));
        MethodTable methodTable = default;
        byte* oldLowestAddress = gc_heap.lowest_address;
        byte* oldHighestAddress = gc_heap.highest_address;

        try
        {
            Assert.Equal((nuint)sizeof(nuint), (nuint)sizeof(ObjHeader));
            Assert.Equal((nuint)objHeader, (nuint)@object->GetHeader());

            @object->RawSetMethodTable(&methodTable);
            gc_heap.lowest_address = (byte*)@object;
            gc_heap.highest_address = (byte*)@object + sizeof(CObjectHeader);

            @object->SetMarked();
            @object->SetPinned();
            Assert.Equal(1, gc_heap.uoh_object_marked((byte*)@object, clearp: 0));
            Assert.True(@object->IsMarked() != 0);
            Assert.True(@object->IsPinned() != 0);

            Assert.Equal(1, gc_heap.uoh_object_marked((byte*)@object, clearp: 1));
            Assert.Equal(0, @object->IsMarked());
            Assert.Equal(0, @object->IsPinned());

            @object->SetPinned();
            Assert.Equal(0, gc_heap.uoh_object_marked((byte*)@object, clearp: 1));
            Assert.True(@object->IsPinned() != 0);

            @object->SetMarked();
            gc_heap.highest_address = (byte*)@object;
            Assert.Equal(1, gc_heap.uoh_object_marked((byte*)@object, clearp: 1));
            Assert.True(@object->IsMarked() != 0);
            Assert.True(@object->IsPinned() != 0);
        }
        finally
        {
            gc_heap.lowest_address = oldLowestAddress;
            gc_heap.highest_address = oldHighestAddress;
        }
    }

    [Fact]
    public void UpdateStartTailRegionsUpdatesWritableStartAndRetainsReadOnlyPrefix()
    {
        heap_segment deleted = default;
        heap_segment next = default;
        generation writableGeneration = default;
        generation.generation_start_segment(&writableGeneration) = &deleted;
        generation.generation_tail_region(&writableGeneration) = &next;

        gc_heap.update_start_tail_regions(&writableGeneration, &deleted, null, &next);

        Assert.Equal((nuint)(void*)&next, (nuint)generation.generation_start_segment(&writableGeneration));
        Assert.Equal((nuint)(void*)&next, (nuint)generation.generation_tail_region(&writableGeneration));

        heap_segment readOnly = default;
        readOnly.flags = heap_segment.heap_segment_flags_readonly | heap_segment.heap_segment_flags_loh;
        heap_segment.heap_segment_next(&readOnly) = &deleted;
        generation readOnlyGeneration = default;
        generation.generation_start_segment(&readOnlyGeneration) = &readOnly;
        generation.generation_tail_ro_region(&readOnlyGeneration) = &readOnly;
        generation.generation_tail_region(&readOnlyGeneration) = &next;

        gc_heap.update_start_tail_regions(&readOnlyGeneration, &deleted, null, &next);

        Assert.Equal((nuint)(void*)&readOnly, (nuint)generation.generation_start_segment(&readOnlyGeneration));
        Assert.Equal((nuint)(void*)&next, (nuint)heap_segment.heap_segment_next(&readOnly));
        Assert.Equal((nuint)(void*)&next, (nuint)generation.generation_tail_region(&readOnlyGeneration));
    }

    [Theory]
    [InlineData((int)gc_generation_num.loh_generation)]
    [InlineData((int)gc_generation_num.poh_generation)]
    public void SweepUohObjectsReclaimsLinkedWritableTailAfterPreservingReadOnlyPrefix(int genNumber)
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: true);
        void* savedFreeObjectMethodTable = GCCommon.g_gc_pFreeObjectMethodTable;
        nuint pageSize = GCToOSInterface.GetPageSize();
        nuint basicRegionSize = 16 * pageSize;
        nuint largeRegionSize = basicRegionSize * region_allocator.LARGE_REGION_FACTOR;
        nuint reservationSize = 3 * largeRegionSize;
        byte* reservationBacking = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(
            reservationSize + basicRegionSize);
        byte* reservation = (byte*)(((nuint)reservationBacking + basicRegionSize - 1) & ~(basicRegionSize - 1));
        Assert.True(reservation is not null);

        try
        {
            nuint firstRegionCommit = 2 * pageSize;
            Assert.True(firstRegionCommit < largeRegionSize);

            int basicRegionCount = checked((int)(reservationSize / basicRegionSize));
            seg_mapping* table = stackalloc seg_mapping[basicRegionCount];
            for (int i = 0; i < basicRegionCount; i++)
            {
                table[i] = default;
            }

            uint* cards = stackalloc uint[128];
            for (int i = 0; i < 128; i++)
            {
                cards[i] = uint.MaxValue;
            }

            int regionShift = gc_heap.index_of_highest_set_bit(basicRegionSize);
            nuint firstRegionIndex = (nuint)reservation >> regionShift;
            gc_heap.min_segment_size_shr = (nuint)regionShift;
            gc_heap.global_region_allocator.initialize_alignment(basicRegionSize);
            GCCommon.seg_mapping_table = table - (nint)firstRegionIndex;
            GCCommon.g_gc_lowest_address = reservation;
            GCCommon.g_gc_highest_address = reservation + (nint)reservationSize;
            gc_heap.bookkeeping_covered_committed = GCCommon.g_gc_highest_address;
            gc_heap.card_table = cards - (nint)card_table_info.card_word(card_table_info.gcard_of(reservation));
            gc_heap.lowest_address = reservation;
            gc_heap.highest_address = GCCommon.g_gc_highest_address;

            heap_segment* readOnly = &table[0].region_info;
            heap_segment* writable = &table[region_allocator.LARGE_REGION_FACTOR].region_info;
            heap_segment* emptyTail = &table[2 * region_allocator.LARGE_REGION_FACTOR].region_info;
            byte* writableStart = reservation + (nint)largeRegionSize;
            byte* emptyTailStart = writableStart + (nint)largeRegionSize;
            nuint emptyTailCommit = pageSize;
            InitializeRegion(readOnly, (nuint)reservation, (nuint)(reservation + (nint)pageSize), (nuint)(reservation + (nint)largeRegionSize), age: 0);
            InitializeRegion(writable, (nuint)writableStart, (nuint)(writableStart + (nint)firstRegionCommit), (nuint)(writableStart + (nint)largeRegionSize), age: 0);
            InitializeRegion(emptyTail, (nuint)emptyTailStart, (nuint)(emptyTailStart + (nint)emptyTailCommit), (nuint)(emptyTailStart + (nint)largeRegionSize), age: 0);

            nuint uohFlag = genNumber == (int)gc_generation_num.loh_generation
                ? heap_segment.heap_segment_flags_loh
                : heap_segment.heap_segment_flags_poh;
            readOnly->flags = uohFlag | heap_segment.heap_segment_flags_readonly;
            writable->flags = uohFlag;
            emptyTail->flags = uohFlag;
            heap_segment.heap_segment_allocated(readOnly) = heap_segment.heap_segment_mem(readOnly);
            heap_segment.heap_segment_allocated(emptyTail) = heap_segment.heap_segment_mem(emptyTail);
            heap_segment.heap_segment_used(writable) = heap_segment.heap_segment_committed(writable);
            heap_segment.heap_segment_next(readOnly) = writable;
            heap_segment.heap_segment_next(writable) = emptyTail;

            MethodTable liveMethodTable = default;
            MethodTable freeObjectMethodTable = default;
            liveMethodTable.m_uBaseSize = (uint)gc_heap.Align(
                (nuint)GCInterfaceOffsets.min_obj_size,
                gc_heap.get_alignment_constant(small_object_p: false));
            GCCommon.g_gc_pFreeObjectMethodTable = &freeObjectMethodTable;

            nuint objectSize = liveMethodTable.m_uBaseSize;
            byte* liveFirst = heap_segment.heap_segment_mem(writable);
            byte* deadFirst = liveFirst + (nint)objectSize;
            byte* deadSecond = deadFirst + (nint)objectSize;
            byte* liveSecond = deadSecond + (nint)objectSize;
            byte* trailingDead = liveSecond + (nint)objectSize;
            ((CObjectHeader*)liveFirst)->RawSetMethodTable(&liveMethodTable);
            ((CObjectHeader*)deadFirst)->RawSetMethodTable(&liveMethodTable);
            ((CObjectHeader*)deadSecond)->RawSetMethodTable(&liveMethodTable);
            ((CObjectHeader*)liveSecond)->RawSetMethodTable(&liveMethodTable);
            ((CObjectHeader*)trailingDead)->RawSetMethodTable(&liveMethodTable);
            ((CObjectHeader*)liveFirst)->SetMarked();
            ((CObjectHeader*)liveFirst)->SetPinned();
            ((CObjectHeader*)liveSecond)->SetMarked();
            heap_segment.heap_segment_allocated(writable) = trailingDead + (nint)objectSize;

            gc_heap heap = default;
            generation* gen = gc_heap.generation_of(gc_heap.generation_table_of(&heap), genNumber);
            generation.initialize(gen);
            gen->gen_num = genNumber;
            generation.generation_start_segment(gen) = readOnly;
            generation.generation_tail_ro_region(gen) = readOnly;
            generation.generation_tail_region(gen) = emptyTail;
            generation.generation_allocation_segment(gen) = writable;

            gc_oh_num oh = genNumber == (int)gc_generation_num.loh_generation ? gc_oh_num.loh : gc_oh_num.poh;
            gc_heap.committed_by_oh[(int)oh] = firstRegionCommit + emptyTailCommit;
            gc_heap.current_total_committed = firstRegionCommit + emptyTailCommit;

            gc_heap.sweep_uoh_objects(&heap, genNumber);

            byte* expectedCommitted = writableStart + (nint)firstRegionCommit;
            Assert.Equal((nuint)expectedCommitted, (nuint)heap_segment.heap_segment_committed(writable));
            Assert.Equal((nuint)expectedCommitted, (nuint)heap_segment.heap_segment_used(writable));
            Assert.Equal((nuint)(liveSecond + (nint)objectSize), (nuint)heap_segment.heap_segment_allocated(writable));
            Assert.Equal(0, ((CObjectHeader*)liveFirst)->IsMarked());
            Assert.Equal(0, ((CObjectHeader*)liveFirst)->IsPinned());
            Assert.Equal(0, ((CObjectHeader*)liveSecond)->IsMarked());
            Assert.Equal((nuint)GCCommon.g_gc_pFreeObjectMethodTable, (nuint)((CObjectHeader*)deadFirst)->RawGetMethodTable());
            Assert.Equal(2 * objectSize, gc_heap.unused_array_size(deadFirst));
            Assert.Equal(2 * objectSize, generation.generation_free_list_space(gen));
            Assert.Equal((nuint)0, generation.generation_free_obj_space(gen));
            uint freeListBucket = generation.generation_allocator(gen)->first_suitable_bucket(2 * objectSize);
            Assert.Equal((nuint)deadFirst, (nuint)allocator.alloc_list_head_of(generation.generation_allocator(gen), freeListBucket));
            Assert.Equal((nuint)deadFirst, (nuint)allocator.alloc_list_tail_of(generation.generation_allocator(gen), freeListBucket));

            Assert.Equal((nuint)readOnly, (nuint)generation.generation_start_segment(gen));
            Assert.Equal((nuint)readOnly, (nuint)generation.generation_tail_ro_region(gen));
            Assert.Equal((nuint)writable, (nuint)heap_segment.heap_segment_next(readOnly));
            Assert.Equal((nuint)heap_segment.heap_segment_mem(readOnly), (nuint)heap_segment.heap_segment_allocated(readOnly));
            Assert.Equal((nuint)writable, (nuint)generation.generation_tail_region(gen));
            Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_next(writable));
            Assert.Equal((nuint)writable, (nuint)generation.generation_allocation_segment(gen));
            Assert.Equal((nuint)emptyTail, (nuint)gc_heap.freeable_uoh_segment);

            gc_heap.rearrange_uoh_segments();

            Assert.True(gc_heap.freeable_uoh_segment is null);
            region_free_list* largeFreeRegions = gc_heap.free_regions_of((int)free_region_kind.large_free_region);
            Assert.Equal((nuint)1, region_free_list.get_num_free_regions(largeFreeRegions));
            Assert.Equal((nuint)emptyTail, (nuint)largeFreeRegions->get_first_free_region());
            Assert.Equal((nuint)expectedCommitted - (nuint)writableStart, gc_heap.committed_by_oh[(int)oh]);
            Assert.Equal(emptyTailCommit, gc_heap.committed_by_oh[gc_heap.recorded_committed_free_bucket]);
            for (int i = 0; i < region_allocator.LARGE_REGION_FACTOR; i++)
            {
                Assert.True(heap_segment.heap_segment_allocated(&table[(2 * region_allocator.LARGE_REGION_FACTOR) + i].region_info) is null);
            }
        }
        finally
        {
            GCCommon.g_gc_pFreeObjectMethodTable = savedFreeObjectMethodTable;
            System.Runtime.InteropServices.NativeMemory.Free(reservationBacking);
        }
    }

    [Fact]
    public void RelocateSurvivorsInPlugRelocatesReferencesInEveryObject()
    {
        int storageSize = checked((int)(5 * card_table_info.brick_size));
        byte* storage = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)storageSize);
        short* bricks = stackalloc short[4];
        region_info* generationMap = stackalloc region_info[4];
        seg_mapping* segmentMap = stackalloc seg_mapping[4];

        try
        {
            byte* firstBrick = card_table_info.align_on_brick(storage);
            using RelocateAddressStateScope _ = new(
                firstBrick,
                firstBrick + (nint)(4 * card_table_info.brick_size),
                bricks,
                generationMap,
                segmentMap);

            byte* node = firstBrick + 512;
            ((plug_and_reloc*)node)[-1].reloc = -64;
            ((plug_and_pair*)node)[-1].m_pair = default;
            gc_heap.set_brick(0, (nint)(node - firstBrick));

            byte* firstOldAddress = firstBrick + 768;
            byte* secondOldAddress = firstBrick + 896;
            nuint objectSize = (nuint)(3 * sizeof(byte*));
            byte* descriptorStorage =
                stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
            MethodTable* methodTable =
                InitializePointerMethodTable(descriptorStorage, objectSize, pointerCount: 1);

            byte* firstObject = firstBrick + 1024;
            byte* secondObject = firstObject + (nint)gc_heap.Align(objectSize);
            ((CObjectHeader*)firstObject)->RawSetMethodTable(methodTable);
            ((CObjectHeader*)secondObject)->RawSetMethodTable(methodTable);
            *(byte**)(firstObject + sizeof(byte*)) = firstOldAddress;
            *(byte**)(secondObject + sizeof(byte*)) = secondOldAddress;

            gc_heap heap = default;
            gc_heap.relocate_survivors_in_plug(
                &heap,
                firstObject,
                secondObject + (nint)gc_heap.Align(objectSize),
                check_last_object_p: 0,
                pinned_plug_entry: null);

            Assert.Equal(
                (nuint)(firstOldAddress - 64),
                (nuint)(*(byte**)(firstObject + sizeof(byte*))));
            Assert.Equal(
                (nuint)(secondOldAddress - 64),
                (nuint)(*(byte**)(secondObject + sizeof(byte*))));
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(storage);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RelocateShortenedPlugRelocatesPreAndPostSavedReferences(bool isPinned)
    {
        int storageSize = checked((int)(5 * card_table_info.brick_size));
        byte* storage = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)storageSize);
        short* bricks = stackalloc short[4];
        region_info* generationMap = stackalloc region_info[4];
        seg_mapping* segmentMap = stackalloc seg_mapping[4];

        try
        {
            byte* firstBrick = card_table_info.align_on_brick(storage);
            using RelocateAddressStateScope _ = new(
                firstBrick,
                firstBrick + (nint)(4 * card_table_info.brick_size),
                bricks,
                generationMap,
                segmentMap);

            byte* node = firstBrick + 512;
            ((plug_and_reloc*)node)[-1].reloc = -64;
            ((plug_and_pair*)node)[-1].m_pair = default;
            gc_heap.set_brick(0, (nint)(node - firstBrick));

            nuint objectSize = (nuint)(6 * sizeof(byte*));
            byte* descriptorStorage =
                stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
            MethodTable* methodTable =
                InitializePointerMethodTable(descriptorStorage, objectSize, pointerCount: 4);
            byte* obj = firstBrick + 1024;
            ((CObjectHeader*)obj)->RawSetMethodTable(methodTable);

            byte* directOldAddress = firstBrick + 768;
            *(byte**)(obj + sizeof(byte*)) = directOldAddress;

            mark entry = default;
            entry.first = isPinned ? obj : obj + (nint)objectSize;
            byte** savedReferences = isPinned
                ? (byte**)&entry.saved_post_plug_reloc
                : (byte**)&entry.saved_pre_plug_reloc;
            for (nint i = 0; i < (nint)mark.get_max_short_bits(); i++)
            {
                savedReferences[i] = firstBrick + 800 + (i * 16);
            }

            if (isPinned)
            {
                entry.saved_post_p = 1;
                entry.saved_post_plug_info_start = obj + (2 * sizeof(byte*));
            }
            else
            {
                entry.saved_pre_p = 1;
            }

            byte* fourthSavedReference = savedReferences[3];
            gc_heap heap = default;
            gc_heap.relocate_survivors_in_plug(
                &heap,
                obj,
                obj + (2 * sizeof(byte*)),
                check_last_object_p: 1,
                &entry);

            Assert.Equal(
                (nuint)(directOldAddress - 64),
                (nuint)(*(byte**)(obj + sizeof(byte*))));
            Assert.Equal((nuint)(firstBrick + 800 - 64), (nuint)savedReferences[0]);
            Assert.Equal((nuint)(firstBrick + 816 - 64), (nuint)savedReferences[1]);
            Assert.Equal((nuint)(firstBrick + 832 - 64), (nuint)savedReferences[2]);
            Assert.Equal((nuint)fourthSavedReference, (nuint)savedReferences[3]);
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(storage);
        }
    }

    [Fact]
    public void RelocateShortenedPlugReplaysOnlyTruncatedLastObjectShortBits()
    {
        int storageSize = checked((int)(5 * card_table_info.brick_size));
        byte* storage = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)storageSize);
        short* bricks = stackalloc short[4];
        region_info* generationMap = stackalloc region_info[4];
        seg_mapping* segmentMap = stackalloc seg_mapping[4];

        try
        {
            byte* firstBrick = card_table_info.align_on_brick(storage);
            using RelocateAddressStateScope _ = new(
                firstBrick,
                firstBrick + (nint)(4 * card_table_info.brick_size),
                bricks,
                generationMap,
                segmentMap);

            byte* node = firstBrick + 512;
            ((plug_and_reloc*)node)[-1].reloc = -64;
            ((plug_and_pair*)node)[-1].m_pair = default;
            gc_heap.set_brick(0, (nint)(node - firstBrick));

            byte* plug = firstBrick + 1024;
            mark entry = default;
            entry.first = plug;
            entry.saved_post_plug_info_start = plug + (2 * sizeof(byte*));
            mark.set_post_short(&entry);
            mark.set_post_short_bit(&entry, 0);
            mark.set_post_short_bit(&entry, 2);

            byte** savedReferences = (byte**)&entry.saved_post_plug_reloc;
            for (nint i = 0; i < (nint)mark.get_max_short_bits(); i++)
            {
                savedReferences[i] = firstBrick + 800 + (i * 16);
            }

            byte* untouchedFirst = savedReferences[1];
            byte* untouchedSecond = savedReferences[3];
            gc_heap heap = default;
            gc_heap.relocate_survivors_in_plug(
                &heap,
                plug,
                plug + (2 * sizeof(byte*)),
                check_last_object_p: 1,
                &entry);

            Assert.Equal((nuint)(firstBrick + 800 - 64), (nuint)savedReferences[0]);
            Assert.Equal((nuint)untouchedFirst, (nuint)savedReferences[1]);
            Assert.Equal((nuint)(firstBrick + 832 - 64), (nuint)savedReferences[2]);
            Assert.Equal((nuint)untouchedSecond, (nuint)savedReferences[3]);
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(storage);
        }
    }

    [Fact]
    public void RelocatePrePlugInfoAdjustsRelocationLookupByOnePointer()
    {
        int storageSize = checked((int)(5 * card_table_info.brick_size));
        byte* storage = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)storageSize);
        short* bricks = stackalloc short[4];
        region_info* generationMap = stackalloc region_info[4];

        try
        {
            byte* firstBrick = card_table_info.align_on_brick(storage);
            using RelocateAddressStateScope _ = new(
                firstBrick,
                firstBrick + (nint)(4 * card_table_info.brick_size),
                bricks,
                generationMap);

            byte* node = firstBrick + 512;
            ((plug_and_reloc*)node)[-1].reloc = -64;
            ((plug_and_pair*)node)[-1].m_pair = default;
            gc_heap.set_brick(0, (nint)(node - firstBrick));

            mark entry = default;
            entry.first = node + sizeof(plug_and_gap) - sizeof(byte*);

            gc_heap.relocate_pre_plug_info(&entry);

            Assert.Equal(
                (nuint)(node - 64 - sizeof(byte*)),
                (nuint)entry.saved_pre_plug_info_reloc_start);
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(storage);
        }
    }

    [Theory]
    [InlineData((int)gc_generation_num.loh_generation)]
    [InlineData((int)gc_generation_num.poh_generation)]
    public void RelocateInUohObjectsRelocatesWritableReferencesAndSkipsReadOnlyAndPointerFreeObjects(int genNumber)
    {
        int storageSize = checked((int)(8 * card_table_info.brick_size));
        byte* storage = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)storageSize);
        short* bricks = stackalloc short[4];
        region_info* generationMap = stackalloc region_info[4];
        seg_mapping* segmentMap = stackalloc seg_mapping[4];
        seg_mapping* oldSegmentMap = GCCommon.seg_mapping_table;

        try
        {
            byte* firstBrick = card_table_info.align_on_brick(storage);
            using RelocateAddressStateScope _ = new(
                firstBrick,
                firstBrick + (nint)(6 * card_table_info.brick_size),
                bricks,
                generationMap);

            nuint firstRegionIndex = (nuint)firstBrick >> (int)gc_heap.min_segment_size_shr;
            GCCommon.seg_mapping_table = segmentMap - (nint)firstRegionIndex;
            for (int i = 0; i < 4; i++)
            {
                segmentMap[i] = default;
                heap_segment.heap_segment_gen_num(&segmentMap[i].region_info) =
                    (byte)gc_generation_num.soh_gen0;
                heap_segment.heap_segment_plan_gen_num(&segmentMap[i].region_info) =
                    (int)gc_generation_num.soh_gen0;
            }

            byte* node = firstBrick + 512;
            ((plug_and_reloc*)node)[-1].reloc = -64;
            ((plug_and_pair*)node)[-1].m_pair = default;
            gc_heap.set_brick(0, (nint)(node - firstBrick));
            byte* oldAddress = firstBrick + 768;
            byte* newAddress = oldAddress - 64;

            const int NumSeries = 1;
            int pointerSize = sizeof(nuint);
            nuint objectSize = (nuint)(3 * pointerSize);
            int descriptorSize = sizeof(nuint) + (NumSeries * sizeof(CGCDescSeries));
            byte* descriptorStorage = stackalloc byte[descriptorSize + sizeof(MethodTable)];
            MethodTable* pointerMethodTable = (MethodTable*)(descriptorStorage + descriptorSize);
            pointerMethodTable->m_uFlags = MethodTable.HasPointersFlag;
            pointerMethodTable->m_uBaseSize = (uint)objectSize;
            *((nuint*)pointerMethodTable - 1) = NumSeries;
            CGCDescSeries* series = (CGCDescSeries*)descriptorStorage;
            series->seriessize = unchecked((nuint)(-(nint)(objectSize - (nuint)pointerSize)));
            series->startoffset = (nuint)pointerSize;

            MethodTable pointerFreeMethodTable = default;
            pointerFreeMethodTable.m_uBaseSize = (uint)objectSize;

            byte* readOnlyPrefixObject = firstBrick + (nint)(2 * card_table_info.brick_size) + 128;
            byte* writableFirstObject = firstBrick + (nint)(3 * card_table_info.brick_size) + 128;
            byte* readOnlyMiddleObject = firstBrick + (nint)(4 * card_table_info.brick_size) + 128;
            byte* writableSecondObject = firstBrick + (nint)(5 * card_table_info.brick_size) + 128;
            byte* writablePointerObject = writableSecondObject + (nint)objectSize;

            ((CObjectHeader*)readOnlyPrefixObject)->RawSetMethodTable(pointerMethodTable);
            ((CObjectHeader*)writableFirstObject)->RawSetMethodTable(pointerMethodTable);
            ((CObjectHeader*)readOnlyMiddleObject)->RawSetMethodTable(pointerMethodTable);
            ((CObjectHeader*)writableSecondObject)->RawSetMethodTable(&pointerFreeMethodTable);
            ((CObjectHeader*)writablePointerObject)->RawSetMethodTable(pointerMethodTable);
            *(byte**)(readOnlyPrefixObject + pointerSize) = oldAddress;
            *(byte**)(writableFirstObject + pointerSize) = oldAddress;
            *(byte**)(readOnlyMiddleObject + pointerSize) = oldAddress;
            *(byte**)(writableSecondObject + pointerSize) = oldAddress;
            *(byte**)(writablePointerObject + pointerSize) = oldAddress;

            nuint uohFlag = genNumber == (int)gc_generation_num.loh_generation
                ? heap_segment.heap_segment_flags_loh
                : heap_segment.heap_segment_flags_poh;
            heap_segment readOnlyPrefix = default;
            heap_segment writableFirst = default;
            heap_segment readOnlyMiddle = default;
            heap_segment writableSecond = default;
            readOnlyPrefix.flags = uohFlag | heap_segment.heap_segment_flags_readonly;
            writableFirst.flags = uohFlag;
            readOnlyMiddle.flags = uohFlag | heap_segment.heap_segment_flags_readonly;
            writableSecond.flags = uohFlag;
            heap_segment.heap_segment_mem(&readOnlyPrefix) = readOnlyPrefixObject;
            heap_segment.heap_segment_allocated(&readOnlyPrefix) = readOnlyPrefixObject + (nint)objectSize;
            heap_segment.heap_segment_mem(&writableFirst) = writableFirstObject;
            heap_segment.heap_segment_allocated(&writableFirst) = writableFirstObject + (nint)objectSize;
            heap_segment.heap_segment_mem(&readOnlyMiddle) = readOnlyMiddleObject;
            heap_segment.heap_segment_allocated(&readOnlyMiddle) = readOnlyMiddleObject + (nint)objectSize;
            heap_segment.heap_segment_mem(&writableSecond) = writableSecondObject;
            heap_segment.heap_segment_allocated(&writableSecond) = writablePointerObject + (nint)objectSize;
            heap_segment.heap_segment_next(&readOnlyPrefix) = &writableFirst;
            heap_segment.heap_segment_next(&writableFirst) = &readOnlyMiddle;
            heap_segment.heap_segment_next(&readOnlyMiddle) = &writableSecond;

            gc_heap heap = default;
            generation* gen = gc_heap.generation_of(gc_heap.generation_table_of(&heap), genNumber);
            generation.generation_start_segment(gen) = &readOnlyPrefix;

            gc_heap.relocate_in_uoh_objects(&heap, genNumber);

            Assert.Equal((nuint)oldAddress, (nuint)(*(byte**)(readOnlyPrefixObject + pointerSize)));
            Assert.Equal((nuint)newAddress, (nuint)(*(byte**)(writableFirstObject + pointerSize)));
            Assert.Equal((nuint)oldAddress, (nuint)(*(byte**)(readOnlyMiddleObject + pointerSize)));
            Assert.Equal((nuint)oldAddress, (nuint)(*(byte**)(writableSecondObject + pointerSize)));
            Assert.Equal((nuint)newAddress, (nuint)(*(byte**)(writablePointerObject + pointerSize)));
        }
        finally
        {
            GCCommon.seg_mapping_table = oldSegmentMap;
            System.Runtime.InteropServices.NativeMemory.Free(storage);
        }
    }
#endif

    [Fact]
    public void MarkPhaseObjectSizeUsesNativeArrayLengthAndPointerSizedArithmetic()
    {
        byte* storage = stackalloc byte[2 * sizeof(nuint)];
        MethodTable methodTable = default;
        CObjectHeader* header = (CObjectHeader*)storage;

        methodTable.m_uBaseSize = 3 * (uint)sizeof(nuint);
        methodTable.m_uFlags = MethodTable.HasComponentSizeFlag;
        methodTable.m_usComponentSize = 3;
        header->RawSetMethodTable(&methodTable);
        *(uint*)(storage + sizeof(nuint)) = 7;

        Assert.Equal(7u, CObjectHeader.GetNumComponents(header));
        Assert.Equal(
            (nuint)(3 * sizeof(nuint)) + ((nuint)7 * 3),
            gc_heap.size(storage));
    }

    [Theory]
    [InlineData(0x100UL, 0, 0, 1, 0, 1)]
    [InlineData(0x101UL, 0, 1, 0, 0, 0)]
    [InlineData(0x102UL, 1, 0, 0, 0, 0)]
    [InlineData(0x103UL, 0, 0, 1, 1, 1)]
    public void MarkPhaseReferenceTagsPreserveNativePredicates(
        ulong value,
        int stolen,
        int partial,
        int straightReference,
        int partialObject,
        int reference)
    {
        byte* tagged = (byte*)(nuint)value;

        Assert.Equal(stolen, gc_heap.stolen_p(tagged));
        Assert.Equal(partial, gc_heap.partial_p(tagged));
        Assert.Equal(straightReference, gc_heap.straight_ref_p(tagged));
        Assert.Equal(partialObject, gc_heap.partial_object_p(tagged));
        Assert.Equal(reference, gc_heap.ref_p(tagged));
        Assert.Equal((nuint)0x100, (nuint)gc_heap.ref_from_slot((byte*)0x103));
    }

#if USE_REGIONS
    [Fact]
    public void MarkPhaseQueuePrefetchesAndDrainsInNativeRotationOrder()
    {
        MethodTable methodTable = default;
        CObjectHeader* headers = stackalloc CObjectHeader[18];
        mark_queue_t queue = default;

        Assert.Equal((nuint)(17 * sizeof(nuint)), (nuint)sizeof(mark_queue_t));
        mark_queue_t.initialize(&queue);
        for (int index = 0; index < 18; index++)
        {
            headers[index].RawSetMethodTable(&methodTable);
        }

        for (int index = 0; index < 16; index++)
        {
            Assert.Equal((nuint)0, (nuint)queue.queue_mark((byte*)&headers[index]));
            Assert.Equal(0, headers[index].IsMarked());
        }

        Assert.Equal((nuint)(byte*)&headers[0], (nuint)queue.queue_mark((byte*)&headers[16]));
        Assert.True(headers[0].IsMarked() != 0);
        Assert.Equal(0, headers[16].IsMarked());

        Assert.Equal((nuint)(byte*)&headers[1], (nuint)queue.get_next_marked());
        Assert.True(headers[1].IsMarked() != 0);

        headers[2].SetMarked();
        Assert.Equal((nuint)(byte*)&headers[3], (nuint)queue.get_next_marked());
        for (int index = 4; index < 16; index++)
        {
            Assert.Equal((nuint)(byte*)&headers[index], (nuint)queue.get_next_marked());
        }

        Assert.Equal((nuint)(byte*)&headers[16], (nuint)queue.get_next_marked());
        Assert.Equal((nuint)0, (nuint)queue.get_next_marked());

        Assert.Equal((nuint)0, (nuint)queue.queue_mark((byte*)&headers[17]));
        Assert.Equal((nuint)(byte*)&headers[17], (nuint)queue.get_next_marked());
        queue.verify_empty();
    }
#endif

#if USE_REGIONS
    [Fact]
    public void MarkPhaseQueueHonorsHeapAndRegionGenerationBoundaries()
    {
        MethodTable methodTable = default;
        CObjectHeader header = default;
        CObjectHeader* objectHeader = &header;
        byte* objectAddress = (byte*)objectHeader;
        mark_queue_t queue = default;
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowestAddress = GCCommon.g_gc_lowest_address;
        byte* oldHighestAddress = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
        region_info* oldGenerationMap = gc_heap.map_region_to_generation;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
        region_info* generationMap = stackalloc region_info[1];
        seg_mapping* segmentMap = stackalloc seg_mapping[1];

        try
        {
            const nuint RegionSizeShift = 4;
            nuint regionIndex = (nuint)objectAddress >> (int)RegionSizeShift;
            objectHeader->RawSetMethodTable(&methodTable);
            mark_queue_t.initialize(&queue);
            gc_heap.min_segment_size_shr = RegionSizeShift;
            GCCommon.g_gc_lowest_address = objectAddress;
            GCCommon.g_gc_highest_address = objectAddress + sizeof(CObjectHeader);
            gc_heap.map_region_to_generation = generationMap;
            gc_heap.map_region_to_generation_skewed = generationMap - (nint)regionIndex;
            GCCommon.seg_mapping_table = segmentMap - (nint)regionIndex;
            segmentMap[0].region_info.gen_num = (byte)gc_generation_num.soh_gen2;
            generationMap[0] = region_info.RI_GEN_2;

            Assert.True(gc_heap.is_in_heap_range(objectAddress));
            Assert.False(gc_heap.is_in_heap_range(objectAddress + sizeof(CObjectHeader)));
            Assert.Equal(
                (nuint)0,
                (nuint)queue.queue_mark(objectAddress, (int)gc_generation_num.soh_gen1));
            Assert.Equal(0, objectHeader->IsMarked());

            generationMap[0] = region_info.RI_GEN_1;
            segmentMap[0].region_info.gen_num = (byte)gc_generation_num.soh_gen1;
            Assert.Equal(
                (nuint)0,
                (nuint)queue.queue_mark(objectAddress, (int)gc_generation_num.soh_gen1));
            Assert.Equal(0, objectHeader->IsMarked());
            Assert.Equal(
                (nuint)objectAddress,
                (nuint)queue.get_next_marked());
            Assert.Equal(
                (nuint)0,
                (nuint)queue.queue_mark(objectAddress, (int)gc_generation_num.soh_gen1));
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowestAddress;
            GCCommon.g_gc_highest_address = oldHighestAddress;
            GCCommon.seg_mapping_table = oldSegMappingTable;
            gc_heap.map_region_to_generation = oldGenerationMap;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RelocateCompactCheckDemotionHelperSetsParentCardOnlyForDemotedInHeapChild(
        bool childInHeap,
        bool childDemoted)
    {
        const nuint RegionSizeShift = 12;
        byte* parent = (byte*)0x1080;
        byte* child = childInHeap ? (byte*)0x1000 : (byte*)0x2000;
        byte** pval = &child;
        nuint regionIndex = (nuint)parent >> (int)RegionSizeShift;
        nuint parentCard = gc_heap.card_of(parent);
        nuint parentCardWord = card_table_info.card_word(parentCard);
        uint parentCardMask = 1u << (int)card_table_info.card_bit(parentCard);
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowestAddress = GCCommon.g_gc_lowest_address;
        byte* oldHighestAddress = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
        region_info* oldGenerationMap = gc_heap.map_region_to_generation;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
        uint* oldCardTable = gc_heap.card_table;
        region_info* generationMap = stackalloc region_info[1];
        seg_mapping* segmentMap = stackalloc seg_mapping[1];
        uint* cardTable = stackalloc uint[1];

        try
        {
            gc_heap.min_segment_size_shr = RegionSizeShift;
            GCCommon.g_gc_lowest_address = (byte*)0x1000;
            GCCommon.g_gc_highest_address = (byte*)0x2000;
            gc_heap.map_region_to_generation = generationMap;
            gc_heap.map_region_to_generation_skewed = generationMap - (nint)regionIndex;
            GCCommon.seg_mapping_table = segmentMap - (nint)regionIndex;
            gc_heap.card_table = cardTable - (nint)parentCardWord;
            generationMap[0] = childDemoted ? region_info.RI_DEMOTED : default;
            heap_segment.heap_segment_plan_gen_num(&segmentMap[0].region_info) =
                (int)gc_generation_num.soh_gen0;
            segmentMap[0].region_info.flags = childDemoted
                ? heap_segment.heap_segment_flags_demoted
                : 0;

            gc_heap.check_demotion_helper(pval, parent);

            Assert.Equal(childInHeap && childDemoted ? parentCardMask : 0u, cardTable[0]);
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowestAddress;
            GCCommon.g_gc_highest_address = oldHighestAddress;
            GCCommon.seg_mapping_table = oldSegMappingTable;
            gc_heap.map_region_to_generation = oldGenerationMap;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
            gc_heap.card_table = oldCardTable;
        }
    }

    [Fact]
    public void RelocateSurvivorHelperRelocatesAndMarksTheCardForADemotedChild()
    {
        int storageSize = checked((int)(5 * card_table_info.brick_size));
        byte* storage = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)storageSize);
        short* bricks = stackalloc short[4];
        region_info* generationMap = stackalloc region_info[4];
        seg_mapping* segmentMap = stackalloc seg_mapping[4];
        uint* cardTable = stackalloc uint[1];
        cardTable[0] = 0;
        seg_mapping* oldSegmentMap = GCCommon.seg_mapping_table;
        uint* oldCardTable = gc_heap.card_table;

        try
        {
            byte* firstBrick = card_table_info.align_on_brick(storage);
            using RelocateAddressStateScope _ = new(
                firstBrick,
                firstBrick + (nint)(4 * card_table_info.brick_size),
                bricks,
                generationMap);

            nuint firstRegionIndex = (nuint)firstBrick >> (int)gc_heap.min_segment_size_shr;
            GCCommon.seg_mapping_table = segmentMap - (nint)firstRegionIndex;
            for (int i = 0; i < 4; i++)
            {
                segmentMap[i] = default;
                heap_segment.heap_segment_gen_num(&segmentMap[i].region_info) =
                    (byte)gc_generation_num.soh_gen0;
                heap_segment.heap_segment_plan_gen_num(&segmentMap[i].region_info) =
                    (int)gc_generation_num.soh_gen0;
            }

            generationMap[0] = region_info.RI_GEN_0 | region_info.RI_DEMOTED;
            segmentMap[0].region_info.flags = heap_segment.heap_segment_flags_demoted;

            byte* node = firstBrick + 512;
            ((plug_and_reloc*)node)[-1].reloc = -64;
            ((plug_and_pair*)node)[-1].m_pair = default;
            gc_heap.set_brick(0, (nint)(node - firstBrick));

            byte* oldAddress = firstBrick + 768;
            byte* child = oldAddress;
            byte** pval = &child;
            nuint parentCard = gc_heap.card_of((byte*)pval);
            nuint parentCardWord = card_table_info.card_word(parentCard);
            uint parentCardMask = 1u << (int)card_table_info.card_bit(parentCard);
            gc_heap.card_table = cardTable - (nint)parentCardWord;

            gc_heap heap = default;
            gc_heap.reloc_survivor_helper(&heap, pval);

            Assert.Equal((nuint)(oldAddress - 64), (nuint)child);
            Assert.Equal(parentCardMask, cardTable[0]);
        }
        finally
        {
            GCCommon.seg_mapping_table = oldSegmentMap;
            gc_heap.card_table = oldCardTable;
            System.Runtime.InteropServices.NativeMemory.Free(storage);
        }
    }

    [Fact]
    public void CardAddressAndCopyCardsCopyAlignedCardsAcrossWordsAndClearZeroBits()
    {
        const nuint FirstCardWord = 4;
        const nuint CardCount = 35;
        const uint LeadingGuard = 0x13579BDF;
        const uint TrailingGuard = 0x2468ACE0;
        uint* storage = stackalloc uint[6];
        uint* cards = storage + 1;
        nuint sourceCard = (FirstCardWord * card_table_info.card_word_width) + 1;
        nuint destinationCard = sourceCard + 64;

        using CardTableStateScope _ = new();
        storage[0] = LeadingGuard;
        storage[5] = TrailingGuard;
        for (int i = 0; i < 4; i++)
        {
            cards[i] = 0;
        }

        gc_heap.card_table = cards - (nint)FirstCardWord;
        Assert.Equal(
            sourceCard * card_table_info.card_size,
            (nuint)gc_heap.card_address(sourceCard));
        Assert.False(gc_heap.card_set_p(sourceCard));

        for (nuint i = 0; i < CardCount; i++)
        {
            if ((i & 1) == 0)
            {
                gc_heap.set_card(sourceCard + i);
            }
        }

        cards[2] = uint.MaxValue;
        cards[3] = uint.MaxValue;

        gc_heap.copy_cards(destinationCard, sourceCard, destinationCard + CardCount, nextp: false);

        for (nuint i = 0; i < CardCount; i++)
        {
            Assert.Equal((i & 1) == 0, gc_heap.card_set_p(destinationCard + i));
            Assert.Equal((i & 1) == 0, gc_heap.card_set_p(sourceCard + i));
        }

        Assert.Equal(LeadingGuard, storage[0]);
        Assert.Equal(TrailingGuard, storage[5]);
    }

    [Fact]
    public void CopyCardsForAddressesCopiesAlignedCardsAcrossWords()
    {
        const nuint FirstCardWord = 4;
        const nuint CardCount = 35;
        const uint LeadingGuard = 0x13579BDF;
        const uint TrailingGuard = 0x2468ACE0;
        uint* storage = stackalloc uint[6];
        uint* cards = storage + 1;
        nuint sourceCard = (FirstCardWord * card_table_info.card_word_width) + 1;
        nuint destinationCard = sourceCard + 64;

        using CardTableStateScope _ = new();
        storage[0] = LeadingGuard;
        storage[5] = TrailingGuard;
        for (int i = 0; i < 4; i++)
        {
            cards[i] = 0;
        }

        gc_heap.card_table = cards - (nint)FirstCardWord;
        for (nuint i = 0; i < CardCount; i++)
        {
            if ((i & 1) == 0)
            {
                gc_heap.set_card(sourceCard + i);
            }
        }

        cards[2] = uint.MaxValue;
        cards[3] = uint.MaxValue;

        gc_heap.copy_cards_for_addresses(
            gc_heap.card_address(destinationCard),
            gc_heap.card_address(sourceCard),
            CardCount * card_table_info.card_size);

        for (nuint i = 0; i < CardCount; i++)
        {
            Assert.Equal((i & 1) == 0, gc_heap.card_set_p(destinationCard + i));
            Assert.Equal((i & 1) == 0, gc_heap.card_set_p(sourceCard + i));
        }

        Assert.Equal(LeadingGuard, storage[0]);
        Assert.Equal(TrailingGuard, storage[5]);
    }

    [Fact]
    public void CopyCardsForAddressesPreservesPartialStartAndEndCards()
    {
        const nuint FirstCardWord = 4;
        const uint LeadingGuard = 0x13579BDF;
        const uint TrailingGuard = 0x2468ACE0;
        uint* storage = stackalloc uint[6];
        uint* cards = storage + 1;
        nuint sourceCard = (FirstCardWord * card_table_info.card_word_width) + 1;
        nuint destinationCard = sourceCard + 64;

        using CardTableStateScope _ = new();
        storage[0] = LeadingGuard;
        storage[5] = TrailingGuard;
        for (int i = 0; i < 4; i++)
        {
            cards[i] = 0;
        }

        gc_heap.card_table = cards - (nint)FirstCardWord;
        gc_heap.set_card(sourceCard + 1);
        gc_heap.set_card(destinationCard + 2);
        gc_heap.set_card(destinationCard + 3);

        gc_heap.copy_cards_for_addresses(
            gc_heap.card_address(destinationCard) + 17,
            gc_heap.card_address(sourceCard) + 31,
            (3 * card_table_info.card_size) + 50);

        Assert.True(gc_heap.card_set_p(destinationCard));
        Assert.True(gc_heap.card_set_p(destinationCard + 1));
        Assert.False(gc_heap.card_set_p(destinationCard + 2));
        Assert.True(gc_heap.card_set_p(destinationCard + 3));
        Assert.True(gc_heap.card_set_p(sourceCard + 1));
        Assert.Equal(LeadingGuard, storage[0]);
        Assert.Equal(TrailingGuard, storage[5]);
    }

    [Fact]
    public void CopyCardsRangeWithoutCopyClearsOnlyTheDestinationCards()
    {
        const nuint FirstCardWord = 4;
        const uint LeadingGuard = 0x13579BDF;
        const uint TrailingGuard = 0x2468ACE0;
        const uint SourcePattern = 0xA5A5A5A5;
        uint* storage = stackalloc uint[6];
        uint* cards = storage + 1;
        nuint sourceCard = (FirstCardWord * card_table_info.card_word_width) + 1;
        nuint destinationCard = sourceCard + 64;

        using CardTableStateScope _ = new();
        storage[0] = LeadingGuard;
        storage[5] = TrailingGuard;
        for (int i = 0; i < 4; i++)
        {
            cards[i] = 0;
        }

        gc_heap.card_table = cards - (nint)FirstCardWord;
        cards[0] = SourcePattern;
        cards[2] = uint.MaxValue;

        gc_heap.copy_cards_range(
            gc_heap.card_address(destinationCard) + 17,
            gc_heap.card_address(sourceCard) + 31,
            3 * card_table_info.card_size,
            copy_cards_p: false);

        Assert.Equal(SourcePattern, cards[0]);
        Assert.True(gc_heap.card_set_p(destinationCard));
        Assert.False(gc_heap.card_set_p(destinationCard + 1));
        Assert.False(gc_heap.card_set_p(destinationCard + 2));
        Assert.True(gc_heap.card_set_p(destinationCard + 3));
        Assert.Equal(LeadingGuard, storage[0]);
        Assert.Equal(TrailingGuard, storage[5]);
    }
#endif

#if USE_REGIONS && !MULTIPLE_HEAPS && !MH_SC_MARK
    [Fact]
    public void MarkPhaseOverflowRecoveryDrainsTransitiveQueueAndReturnsFalseWhenStable()
    {
        using MarkPhaseStateScope _ = new();
        SyncImports.ResetRecording();
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        nuint pointerSize = (nuint)sizeof(byte*);
        byte* objectStorage = stackalloc byte[(int)(11 * pointerSize)];
        byte* root = AlignUp(objectStorage, pointerSize);
        byte* child = root + (nint)(2 * pointerSize);
        byte* grandchild = child + (nint)(2 * pointerSize);
        byte* drainRoot = grandchild + (nint)(2 * pointerSize);
        byte* drainChild = drainRoot + (nint)(2 * pointerSize);
        byte* descriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        MethodTable* methodTable = InitializeSingleSeriesMethodTable(
            descriptorStorage,
            2 * pointerSize,
            pointerSize,
            pointerCount: 1,
            hasPointers: 1);
        heap_segment region = default;
        heap_segment largeRegion = default;
        generation* generationTable = gc_heap.generation_table_of(pHeap);
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowestAddress = GCCommon.g_gc_lowest_address;
        byte* oldHighestAddress = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
        region_info* oldGenerationMap = gc_heap.map_region_to_generation;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
        mark* initialMarkStack = null;
#if DEBUG
        nuint oldPromoted = gc_heap.g_promoted;
#endif

        try
        {
            ((CObjectHeader*)root)->RawSetMethodTable(methodTable);
            ((CObjectHeader*)child)->RawSetMethodTable(methodTable);
            ((CObjectHeader*)grandchild)->RawSetMethodTable(methodTable);
            ((CObjectHeader*)drainRoot)->RawSetMethodTable(methodTable);
            ((CObjectHeader*)drainChild)->RawSetMethodTable(methodTable);
            *(byte**)(root + (nint)pointerSize) = child;
            *(byte**)(child + (nint)pointerSize) = grandchild;
            *(byte**)(grandchild + (nint)pointerSize) = null;
            *(byte**)(drainRoot + (nint)pointerSize) = drainChild;
            *(byte**)(drainChild + (nint)pointerSize) = null;

            const nuint RegionShift = 4;
            gc_heap.min_segment_size_shr = RegionShift;
            GCCommon.g_gc_lowest_address = root;
            GCCommon.g_gc_highest_address = drainChild + (nint)(2 * pointerSize);
            gc_heap.gc_low = root;
            gc_heap.gc_high = GCCommon.g_gc_highest_address;
            nuint minimumRegionIndex = (nuint)GCCommon.g_gc_lowest_address >> (int)RegionShift;
            nuint maximumRegionIndex = ((nuint)GCCommon.g_gc_highest_address - 1) >> (int)RegionShift;
            nuint regionCount = maximumRegionIndex - minimumRegionIndex + 1;
            Assert.InRange(regionCount, (nuint)1, (nuint)8);
            region_info* generationMap = stackalloc region_info[8];
            seg_mapping* segmentMap = stackalloc seg_mapping[8];
            nuint* survived = stackalloc nuint[8];
            nuint* oldCardSurvived = stackalloc nuint[8];
            GCCommon.seg_mapping_table = segmentMap - (nint)minimumRegionIndex;
            gc_heap.map_region_to_generation = generationMap;
            gc_heap.map_region_to_generation_skewed = generationMap - (nint)minimumRegionIndex;
            InitializeRegionGenerationMaps(
                generationMap,
                segmentMap,
                regionCount,
                (int)gc_generation_num.soh_gen0);

            for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
            {
                generationTable[i].gen_num = i;
            }

            heap_segment.heap_segment_mem(&region) = root;
            heap_segment.heap_segment_allocated(&region) = drainChild + (nint)(2 * pointerSize);
            generation.generation_start_segment(
                generationTable + (int)gc_generation_num.soh_gen0) = &region;

            nuint largeRegionSize =
                20 * (nuint)gc_rand.MARK_STACK_INITIAL_LENGTH * (nuint)sizeof(mark);
            byte* largeRegionStart = (byte*)(nuint)0x100000;
            heap_segment.heap_segment_mem(&largeRegion) = largeRegionStart;
            heap_segment.heap_segment_allocated(&largeRegion) = largeRegionStart + (nint)largeRegionSize;
            generation.generation_start_segment(
                generationTable + (int)gc_generation_num.soh_gen2) = &largeRegion;

            pHeap->heap_number = 0;
            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen0;
            byte** markList = stackalloc byte*[16];
            Assert.True(gc_heap.setup_mark_state_for_collection(
                markList,
                16,
                survived,
                oldCardSurvived,
                regionCount));
#if DEBUG
            gc_heap.init_promoted_bytes();
#endif

            initialMarkStack = (mark*)SyncImports.ManagedGC_AllocZeroed((nuint)sizeof(mark));
            Assert.NotEqual((nuint)0, (nuint)initialMarkStack);
            gc_heap.mark_stack_array = initialMarkStack;
            gc_heap.mark_stack_array_length = 1;
            ((CObjectHeader*)root)->SetMarked();
            gc_heap.record_mark_stack_overflow(pHeap, root);

            Assert.Equal((nuint)root, (nuint)gc_heap.min_overflow_address);
            Assert.Equal((nuint)root, (nuint)gc_heap.max_overflow_address);
            Assert.True(gc_heap.process_mark_overflow(pHeap, (int)gc_generation_num.soh_gen0));
            Assert.True(((CObjectHeader*)child)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)grandchild)->IsMarked() != 0);
            Assert.Equal((nuint)gc_rand.MARK_STACK_INITIAL_LENGTH, gc_heap.mark_stack_array_length);
            Assert.Equal(nuint.MaxValue, (nuint)gc_heap.min_overflow_address);
            Assert.Equal((nuint)0, (nuint)gc_heap.max_overflow_address);
            Assert.Equal(4 * pointerSize, SumRegionCounters(survived, regionCount));
            Assert.Equal((nuint)0, (nuint)gc_heap.mark_queue.get_next_marked());
            gc_heap.mark_queue.verify_empty();

            Assert.Equal((nuint)0, (nuint)gc_heap.mark_queue.queue_mark(drainRoot));
            Assert.False(gc_heap.process_mark_overflow(pHeap, (int)gc_generation_num.soh_gen0));
            Assert.True(((CObjectHeader*)drainRoot)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)drainChild)->IsMarked() != 0);
            Assert.Equal(8 * pointerSize, SumRegionCounters(survived, regionCount));
            Assert.Equal(nuint.MaxValue, (nuint)gc_heap.min_overflow_address);
            Assert.Equal((nuint)0, (nuint)gc_heap.max_overflow_address);
            Assert.Equal((nuint)0, (nuint)gc_heap.mark_queue.get_next_marked());
            gc_heap.mark_queue.verify_empty();
        }
        finally
        {
            if (initialMarkStack is not null && gc_heap.mark_stack_array is not null)
            {
                SyncImports.ManagedGC_Free(gc_heap.mark_stack_array);
            }

            GCCommon.g_gc_lowest_address = oldLowestAddress;
            GCCommon.g_gc_highest_address = oldHighestAddress;
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.seg_mapping_table = oldSegMappingTable;
            gc_heap.map_region_to_generation = oldGenerationMap;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
#if DEBUG
            gc_heap.g_promoted = oldPromoted;
#endif
        }
    }

    [Fact]
    public void MarkPhaseOverflowRecoveryRechecksOverflowRecordedWhileRescanning()
    {
        using MarkPhaseStateScope _ = new();
        SyncImports.ResetRecording();
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        const int ReferenceCount = 17;
        nuint pointerSize = (nuint)sizeof(byte*);
        int markStackSlots = (int)((nuint)sizeof(mark) / pointerSize);
        int overflowReferenceCount = markStackSlots + 1;
        nuint rootSize = (nuint)(ReferenceCount + 1) * pointerSize;
        nuint overflowSize = (nuint)(overflowReferenceCount + 1) * pointerSize;
        byte* objectStorage = stackalloc byte[
            (int)(rootSize + rootSize + overflowSize + (3 * pointerSize) + pointerSize)];
        byte* root = AlignUp(objectStorage, pointerSize);
        byte* candidate = root + (nint)rootSize;
        byte* overflow = candidate + (nint)rootSize;
        byte* recoveryChild = overflow + (nint)overflowSize;
        byte* rootDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        byte* candidateDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        byte* overflowDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        MethodTable* rootMethodTable = InitializeSingleSeriesMethodTable(
            rootDescriptorStorage,
            rootSize,
            pointerSize,
            (nuint)ReferenceCount,
            hasPointers: 1);
        MethodTable* candidateMethodTable = InitializeSingleSeriesMethodTable(
            candidateDescriptorStorage,
            rootSize,
            pointerSize,
            (nuint)ReferenceCount,
            hasPointers: 1);
        MethodTable* overflowMethodTable = InitializeSingleSeriesMethodTable(
            overflowDescriptorStorage,
            overflowSize,
            pointerSize,
            (nuint)overflowReferenceCount,
            hasPointers: 1);
        MethodTable leafMethodTable = default;
        heap_segment region = default;
        heap_segment largeRegion = default;
        generation* generationTable = gc_heap.generation_table_of(pHeap);
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowestAddress = GCCommon.g_gc_lowest_address;
        byte* oldHighestAddress = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
        region_info* oldGenerationMap = gc_heap.map_region_to_generation;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
        mark* initialMarkStack = null;
#if DEBUG
        nuint oldPromoted = gc_heap.g_promoted;
#endif

        try
        {
            leafMethodTable.m_uBaseSize = (uint)(2 * pointerSize);
            ((CObjectHeader*)root)->RawSetMethodTable(rootMethodTable);
            ((CObjectHeader*)candidate)->RawSetMethodTable(candidateMethodTable);
            ((CObjectHeader*)overflow)->RawSetMethodTable(overflowMethodTable);
            ((CObjectHeader*)recoveryChild)->RawSetMethodTable(&leafMethodTable);

            byte** rootReferences = (byte**)(root + (nint)pointerSize);
            byte** candidateReferences = (byte**)(candidate + (nint)pointerSize);
            byte** overflowReferences = (byte**)(overflow + (nint)pointerSize);
            for (int i = 0; i < ReferenceCount; i++)
            {
                rootReferences[i] = candidate;
                candidateReferences[i] = overflow;
            }

            overflowReferences[0] = recoveryChild;
            for (int i = 1; i < overflowReferenceCount; i++)
            {
                overflowReferences[i] = null;
            }

            const nuint RegionShift = 4;
            gc_heap.min_segment_size_shr = RegionShift;
            GCCommon.g_gc_lowest_address = root;
            GCCommon.g_gc_highest_address = recoveryChild + (nint)(2 * pointerSize);
            gc_heap.gc_low = root;
            gc_heap.gc_high = GCCommon.g_gc_highest_address;
            nuint minimumRegionIndex = (nuint)GCCommon.g_gc_lowest_address >> (int)RegionShift;
            nuint maximumRegionIndex = ((nuint)GCCommon.g_gc_highest_address - 1) >> (int)RegionShift;
            nuint regionCount = maximumRegionIndex - minimumRegionIndex + 1;
            Assert.InRange(regionCount, (nuint)1, (nuint)64);
            region_info* generationMap = stackalloc region_info[64];
            seg_mapping* segmentMap = stackalloc seg_mapping[64];
            nuint* survived = stackalloc nuint[64];
            nuint* oldCardSurvived = stackalloc nuint[64];
            GCCommon.seg_mapping_table = segmentMap - (nint)minimumRegionIndex;
            gc_heap.map_region_to_generation = generationMap;
            gc_heap.map_region_to_generation_skewed = generationMap - (nint)minimumRegionIndex;
            InitializeRegionGenerationMaps(
                generationMap,
                segmentMap,
                regionCount,
                (int)gc_generation_num.soh_gen0);

            for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
            {
                generationTable[i].gen_num = i;
            }

            heap_segment.heap_segment_mem(&region) = root;
            heap_segment.heap_segment_allocated(&region) = recoveryChild + (nint)(2 * pointerSize);
            generation.generation_start_segment(
                generationTable + (int)gc_generation_num.soh_gen0) = &region;

            nuint largeRegionSize =
                20 * (nuint)gc_rand.MARK_STACK_INITIAL_LENGTH * (nuint)sizeof(mark);
            byte* largeRegionStart = (byte*)(nuint)0x100000;
            heap_segment.heap_segment_mem(&largeRegion) = largeRegionStart;
            heap_segment.heap_segment_allocated(&largeRegion) = largeRegionStart + (nint)largeRegionSize;
            generation.generation_start_segment(
                generationTable + (int)gc_generation_num.soh_gen2) = &largeRegion;

            pHeap->heap_number = 0;
            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen0;
            byte** markList = stackalloc byte*[16];
            Assert.True(gc_heap.setup_mark_state_for_collection(
                markList,
                16,
                survived,
                oldCardSurvived,
                regionCount));
#if DEBUG
            gc_heap.init_promoted_bytes();
#endif

            initialMarkStack = (mark*)SyncImports.ManagedGC_AllocZeroed((nuint)sizeof(mark));
            Assert.NotEqual((nuint)0, (nuint)initialMarkStack);
            gc_heap.mark_stack_array = initialMarkStack;
            gc_heap.mark_stack_array_length = 1;
            ((CObjectHeader*)root)->SetMarked();
            gc_heap.record_mark_stack_overflow(pHeap, root);
            SyncImports.FailNextAlloc = true;

            Assert.True(gc_heap.process_mark_overflow(pHeap, (int)gc_generation_num.soh_gen0));
            Assert.False(SyncImports.FailNextAlloc);
            Assert.True(((CObjectHeader*)overflow)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)recoveryChild)->IsMarked() != 0);
            Assert.Equal((nuint)gc_rand.MARK_STACK_INITIAL_LENGTH, gc_heap.mark_stack_array_length);
            Assert.Equal(nuint.MaxValue, (nuint)gc_heap.min_overflow_address);
            Assert.Equal((nuint)0, (nuint)gc_heap.max_overflow_address);
            Assert.Equal((nuint)0, (nuint)gc_heap.mark_queue.get_next_marked());
            gc_heap.mark_queue.verify_empty();
        }
        finally
        {
            SyncImports.FailNextAlloc = false;
            if (initialMarkStack is not null && gc_heap.mark_stack_array is not null)
            {
                SyncImports.ManagedGC_Free(gc_heap.mark_stack_array);
            }

            GCCommon.g_gc_lowest_address = oldLowestAddress;
            GCCommon.g_gc_highest_address = oldHighestAddress;
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.seg_mapping_table = oldSegMappingTable;
            gc_heap.map_region_to_generation = oldGenerationMap;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
#if DEBUG
            gc_heap.g_promoted = oldPromoted;
#endif
        }
    }

    [Fact]
    public void MarkPhaseOverflowRecoveryCapsMarkStackGrowthByHeapSize()
    {
        using MarkPhaseStateScope _ = new();
        SyncImports.ResetRecording();
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        CObjectHeader root = default;
        MethodTable methodTable = default;
        heap_segment region = default;
        heap_segment largeRegion = default;
        generation* generationTable = gc_heap.generation_table_of(pHeap);
        nuint oldLength = (100 * 1024) / (2 * (nuint)sizeof(mark)) + 1;
        nuint cappedLength = oldLength + oldLength / 2 + 1;
        mark* initialMarkStack = null;

        try
        {
            methodTable.m_uBaseSize = (uint)sizeof(nuint);
            root.RawSetMethodTable(&methodTable);
            root.SetMarked();
            for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
            {
                generationTable[i].gen_num = i;
            }

            heap_segment.heap_segment_mem(&region) = (byte*)&root;
            heap_segment.heap_segment_allocated(&region) = (byte*)&root + sizeof(nuint);
            generation.generation_start_segment(
                generationTable + (int)gc_generation_num.soh_gen0) = &region;

            nuint totalHeapSize = cappedLength * 10 * (nuint)sizeof(mark);
            byte* largeRegionStart = (byte*)(nuint)0x100000;
            heap_segment.heap_segment_mem(&largeRegion) = largeRegionStart;
            heap_segment.heap_segment_allocated(&largeRegion) =
                largeRegionStart + (nint)(totalHeapSize - (nuint)sizeof(nuint));
            generation.generation_start_segment(
                generationTable + (int)gc_generation_num.soh_gen2) = &largeRegion;

            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen0;
            initialMarkStack = (mark*)SyncImports.ManagedGC_AllocZeroed(
                oldLength * (nuint)sizeof(mark));
            Assert.NotEqual((nuint)0, (nuint)initialMarkStack);
            gc_heap.mark_stack_array = initialMarkStack;
            gc_heap.mark_stack_array_length = oldLength;
            gc_heap.record_mark_stack_overflow(pHeap, (byte*)&root);

            Assert.True(2 * oldLength * (nuint)sizeof(mark) > 100 * 1024);
            Assert.True(gc_heap.process_mark_overflow(pHeap, (int)gc_generation_num.soh_gen0));
            Assert.Equal(cappedLength, gc_heap.mark_stack_array_length);
        }
        finally
        {
            if (initialMarkStack is not null && gc_heap.mark_stack_array is not null)
            {
                SyncImports.ManagedGC_Free(gc_heap.mark_stack_array);
            }
        }
    }

    [Fact]
    public void PlanPhaseGenerationSizesAndTotalHeapSizeIncludeSohLohAndPoh()
    {
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        generation* generationTable = gc_heap.generation_table_of(pHeap);
        heap_segment gen0 = default;
        heap_segment gen1 = default;
        heap_segment gen2 = default;
        heap_segment loh = default;
        heap_segment poh = default;

        InitializeGenerationSegment(generationTable, (int)gc_generation_num.soh_gen0, &gen0, (byte*)0x1000, 10, 11);
        InitializeGenerationSegment(generationTable, (int)gc_generation_num.soh_gen1, &gen1, (byte*)0x2000, 20, 22);
        InitializeGenerationSegment(generationTable, (int)gc_generation_num.soh_gen2, &gen2, (byte*)0x3000, 30, 33);
        InitializeGenerationSegment(generationTable, (int)gc_generation_num.loh_generation, &loh, (byte*)0x4000, 40, 44);
        InitializeGenerationSegment(generationTable, (int)gc_generation_num.poh_generation, &poh, (byte*)0x5000, 50, 55);

        Assert.Equal(
            (nuint)60,
            gc_heap.generation_sizes(
                pHeap,
                generationTable + (int)gc_generation_num.soh_gen2));
        Assert.Equal(
            (nuint)66,
            gc_heap.generation_sizes(
                pHeap,
                generationTable + (int)gc_generation_num.soh_gen2,
                use_saved_p: true));
        Assert.Equal(
            (nuint)40,
            gc_heap.generation_sizes(
                pHeap,
                generationTable + (int)gc_generation_num.loh_generation));
        Assert.Equal(
            (nuint)50,
            gc_heap.generation_sizes(
                pHeap,
                generationTable + (int)gc_generation_num.poh_generation));
        Assert.Equal((nuint)150, gc_heap.get_total_heap_size(pHeap));
    }

    [Fact]
    public void GCPrivPromotedBytesAggregatesRegionCountersAndHandlesUnavailableStorage()
    {
        using MarkPhaseStateScope _ = new();
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        heap_segment* regions = stackalloc heap_segment[3];
        nuint* survived = stackalloc nuint[3];
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowestAddress = GCCommon.g_gc_lowest_address;
        byte* oldHighestAddress = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
#if DEBUG
        nuint oldPromoted = gc_heap.g_promoted;
#endif

        try
        {
            gc_heap.min_segment_size_shr = 12;
            GCCommon.g_gc_lowest_address = (byte*)0x1000;
            GCCommon.g_gc_highest_address = (byte*)0x4000;
            GCCommon.seg_mapping_table = (seg_mapping*)regions - 1;
            gc_heap.region_count = 3;
            survived[0] = 11;
            survived[1] = 22;
            survived[2] = 33;
            gc_heap.survived_per_region = survived;
            pHeap->heap_number = 0;
#if DEBUG
            gc_heap.g_promoted = 66;
#endif

            Assert.Equal((nuint)66, gc_heap.get_promoted_bytes(pHeap));

            gc_heap.survived_per_region = null;
            Assert.Equal((nuint)0, gc_heap.get_promoted_bytes(pHeap));
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowestAddress;
            GCCommon.g_gc_highest_address = oldHighestAddress;
            GCCommon.seg_mapping_table = oldSegMappingTable;
#if DEBUG
            gc_heap.g_promoted = oldPromoted;
#endif
        }
    }

    private static void InitializeGenerationSegment(
        generation* generationTable,
        int generationNumber,
        heap_segment* segment,
        byte* start,
        nuint allocatedSize,
        nuint savedAllocatedSize)
    {
        generationTable[generationNumber].gen_num = generationNumber;
        heap_segment.heap_segment_mem(segment) = start;
        heap_segment.heap_segment_allocated(segment) = start + (nint)allocatedSize;
        heap_segment.heap_segment_saved_allocated(segment) = start + (nint)savedAllocatedSize;
        generation.generation_start_segment(generationTable + generationNumber) = segment;
    }
#endif

    [Fact]
    public void MarkPhaseOverflowRangeTracksInclusiveExtrema()
    {
        using MarkPhaseStateScope _ = new();
        gc_heap heap = default;
        gc_heap* pHeap = &heap;

        gc_heap.reset_mark_stack(pHeap);
        gc_heap.record_mark_stack_overflow(pHeap, (byte*)0x300);
        gc_heap.record_mark_stack_overflow(pHeap, (byte*)0x100);
        gc_heap.record_mark_stack_overflow(pHeap, (byte*)0x500);
        gc_heap.record_mark_stack_overflow(pHeap, (byte*)0x300);

        Assert.Equal((nuint)0x100, (nuint)gc_heap.min_overflow_address);
        Assert.Equal((nuint)0x500, (nuint)gc_heap.max_overflow_address);
    }

    [Fact]
    public void MarkPhaseShortPlugSizeMatchesNativeFormula()
    {
        Assert.Equal(sizeof(nuint) == 8 ? (nuint)48 : (nuint)24, gc_heap.min_pre_pin_obj_size);
    }

#if BACKGROUND_GC
    [Fact]
    public void MarkPhaseMarkBitmapQueriesAndClearsNativePartialWordRange()
    {
        uint* markStorage = stackalloc uint[18];
        uint* previousMarkArray = gc_heap.mark_array;
        byte* previousLowestAddress = gc_heap.background_saved_lowest_address;
        byte* previousHighestAddress = gc_heap.background_saved_highest_address;
        bool previousCanUseConcurrent = gc_heap.gc_can_use_concurrent;
        nuint wordSize = card_table_info.mark_word_size;
        nuint wordsPerPage = card_table_info.GC_PAGE_SIZE / wordSize;
        byte* start = (byte*)(64 * wordSize);
        byte* end = start + (nint)(wordsPerPage * wordSize);
        nuint firstMarkWord = ExpectedMarkWord(start);

        try
        {
            gc_heap.mark_array = markStorage - (nint)firstMarkWord;
            gc_heap.background_saved_lowest_address = start;
            gc_heap.background_saved_highest_address = end;
            gc_heap.gc_can_use_concurrent = true;

            markStorage[0] =
                ExpectedMarkMask(start)
                | ExpectedMarkMask(start + (nint)card_table_info.mark_bit_pitch)
                | ExpectedMarkMask(start + (nint)(31 * card_table_info.mark_bit_pitch));
            markStorage[1] = uint.MaxValue;
            markStorage[(int)wordsPerPage - 1] = uint.MaxValue;
            markStorage[(int)wordsPerPage] = ExpectedMarkMask(end);

            Assert.Equal(unchecked((int)ExpectedMarkMask(start)), gc_heap.is_mark_bit_set(start));
            Assert.Equal(
                unchecked((int)ExpectedMarkMask(start + (nint)card_table_info.mark_bit_pitch)),
                gc_heap.is_mark_bit_set(start + (nint)card_table_info.mark_bit_pitch));
            Assert.Equal(
                unchecked((int)ExpectedMarkMask(start + (nint)(31 * card_table_info.mark_bit_pitch))),
                gc_heap.is_mark_bit_set(start + (nint)(31 * card_table_info.mark_bit_pitch)));
            Assert.Equal(unchecked((int)ExpectedMarkMask(end)), gc_heap.is_mark_bit_set(end));

            gc_heap.clear_mark_array(
                start + (nint)card_table_info.mark_bit_pitch,
                end);

            Assert.Equal(ExpectedMarkMask(start), markStorage[0]);
            Assert.Equal(0u, markStorage[1]);
            Assert.Equal(0u, markStorage[(int)wordsPerPage - 1]);
            Assert.Equal(ExpectedMarkMask(end), markStorage[(int)wordsPerPage]);

            gc_heap.clear_mark_array(
                end,
                end + (nint)card_table_info.GC_PAGE_SIZE);

            Assert.Equal(ExpectedMarkMask(end), markStorage[(int)wordsPerPage]);
        }
        finally
        {
            gc_heap.mark_array = previousMarkArray;
            gc_heap.background_saved_lowest_address = previousLowestAddress;
            gc_heap.background_saved_highest_address = previousHighestAddress;
            gc_heap.gc_can_use_concurrent = previousCanUseConcurrent;
        }
    }

    private static nuint ExpectedMarkWord(byte* address)
    {
        return (nuint)address / ((nuint)sizeof(uint) * 8 * (sizeof(nuint) == 8 ? 16u : 8u));
    }

    private static uint ExpectedMarkMask(byte* address)
    {
        nuint pitch = sizeof(nuint) == 8 ? 16u : 8u;
        return 1u << (int)(((nuint)address / pitch) % 32);
    }
#endif

#if USE_REGIONS
    [Fact]
    public void MarkPhaseGcMarkIsIdempotentAndHonorsRangeAndRegionGeneration()
    {
        MethodTable methodTable = default;
        CObjectHeader header = default;
        MethodTable* methodTableAddress = &methodTable;
        CObjectHeader* headerAddress = &header;
        byte* objectAddress = (byte*)headerAddress;
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowestAddress = GCCommon.g_gc_lowest_address;
        byte* oldHighestAddress = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
        region_info* oldGenerationMap = gc_heap.map_region_to_generation;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
        region_info* generationMap = stackalloc region_info[1];
        seg_mapping* segmentMap = stackalloc seg_mapping[1];

        try
        {
            headerAddress->RawSetMethodTable(methodTableAddress);

            const nuint RegionSizeShift = 4;
            nuint regionIndex = (nuint)objectAddress >> (int)RegionSizeShift;
            gc_heap.min_segment_size_shr = RegionSizeShift;
            GCCommon.g_gc_lowest_address = objectAddress;
            GCCommon.g_gc_highest_address = objectAddress + sizeof(CObjectHeader);
            gc_heap.map_region_to_generation = generationMap;
            gc_heap.map_region_to_generation_skewed = generationMap - (nint)regionIndex;
            GCCommon.seg_mapping_table = segmentMap - (nint)regionIndex;
            generationMap[0] = region_info.RI_GEN_2;
            segmentMap[0].region_info.gen_num = (byte)gc_generation_num.soh_gen2;

            Assert.Equal(0, gc_heap.gc_mark(
                objectAddress,
                objectAddress,
                objectAddress + sizeof(CObjectHeader),
                (int)gc_generation_num.soh_gen1));
            Assert.Equal(0, headerAddress->IsMarked());

            Assert.Equal(1, gc_heap.gc_mark(
                objectAddress,
                objectAddress,
                objectAddress + sizeof(CObjectHeader),
                GCInterfaceOffsets.max_generation));
            Assert.True(headerAddress->IsMarked() != 0);
            Assert.Equal(0, gc_heap.gc_mark(
                objectAddress,
                objectAddress,
                objectAddress + sizeof(CObjectHeader),
                GCInterfaceOffsets.max_generation));
            Assert.Equal(0, gc_heap.gc_mark(
                objectAddress + sizeof(CObjectHeader),
                objectAddress,
                objectAddress + sizeof(CObjectHeader),
                GCInterfaceOffsets.max_generation));

            headerAddress->ClearMarked();
            Assert.Equal(1, gc_heap.gc_mark1(objectAddress));
            Assert.Equal(0, gc_heap.gc_mark1(objectAddress));
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowestAddress;
            GCCommon.g_gc_highest_address = oldHighestAddress;
            GCCommon.seg_mapping_table = oldSegMappingTable;
            gc_heap.map_region_to_generation = oldGenerationMap;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
        }
    }

#if !MULTIPLE_HEAPS && !MH_SC_MARK
    [Fact]
    public void MarkPhaseMarkObjectSimple1ResumesLargeObjectTraversalAndLeavesPrefetchTail()
    {
        using MarkPhaseStateScope _ = new();
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        const int FilteredChildren = 8;
        const int ExpectedTailCount = 16;
        const nuint RegionShift = 4;
        nuint pointerSize = (nuint)sizeof(byte*);
        nuint childObjectSize = 2 * pointerSize;
        int slotsPerMark = MarkStackSlotsPerEntry();
        int markStackEntryCount = (gc_heap.num_partial_refs + 3 + slotsPerMark - 1) / slotsPerMark;
        int markStackSlotCapacity = MarkStackSlotCapacity((nuint)markStackEntryCount);
        int validChildren = Math.Max(markStackSlotCapacity + 2, gc_heap.num_partial_refs + ExpectedTailCount + 1);
        int totalChildren = FilteredChildren + validChildren;
        int rootWords = Math.Max(gc_heap.partial_size_th, totalChildren + 1);
        nuint rootObjectSize = (nuint)rootWords * pointerSize;
        int expectedMarkedChildren = validChildren - ExpectedTailCount;

        Assert.True(markStackSlotCapacity > gc_heap.num_partial_refs + 2);
        Assert.True(validChildren > markStackSlotCapacity - 2);
        Assert.True(expectedMarkedChildren > gc_heap.num_partial_refs);

        byte* objectStorage = stackalloc byte[
            checked((int)(rootObjectSize + ((nuint)totalChildren * childObjectSize) + pointerSize))];
        byte* root = AlignUp(objectStorage, pointerSize);
        byte* childStart = root + (nint)rootObjectSize;
        byte** children = stackalloc byte*[totalChildren];
        byte** rootReferences = (byte**)(root + sizeof(nuint));
        byte* rootDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        byte* childDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        MethodTable* rootMethodTable = InitializeSingleSeriesMethodTable(
            rootDescriptorStorage,
            rootObjectSize,
            (nuint)sizeof(nuint),
            (nuint)totalChildren,
            hasPointers: 1);
        MethodTable* childMethodTable = InitializeSingleSeriesMethodTable(
            childDescriptorStorage,
            childObjectSize,
            (nuint)sizeof(nuint),
            pointerCount: (nuint)1,
            hasPointers: 1);
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowestAddress = GCCommon.g_gc_lowest_address;
        byte* oldHighestAddress = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
        region_info* oldGenerationMap = gc_heap.map_region_to_generation;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
#if DEBUG
        nuint savedPromoted = gc_heap.g_promoted;
#endif

        try
        {
            ((CObjectHeader*)root)->RawSetMethodTable(rootMethodTable);

            for (int index = 0; index < totalChildren; index++)
            {
                byte* child = childStart + (nint)((nuint)index * childObjectSize);
                children[index] = child;
                ((CObjectHeader*)child)->RawSetMethodTable(childMethodTable);
                *(byte**)(child + sizeof(nuint)) = null;
                rootReferences[index] = child;
            }

            pHeap->heap_number = 0;
            gc_heap.min_segment_size_shr = RegionShift;
            GCCommon.g_gc_lowest_address = root;
            GCCommon.g_gc_highest_address = childStart + (nint)((nuint)totalChildren * childObjectSize);

            nuint minRegionIndex = (nuint)GCCommon.g_gc_lowest_address >> (int)RegionShift;
            nuint maxRegionIndex = ((nuint)GCCommon.g_gc_highest_address - 1) >> (int)RegionShift;
            nuint regionCount = maxRegionIndex - minRegionIndex + 1;
            region_info* generationMap = stackalloc region_info[(int)regionCount];
            seg_mapping* segmentMap = stackalloc seg_mapping[(int)regionCount];
            gc_heap.map_region_to_generation = generationMap;
            gc_heap.map_region_to_generation_skewed = generationMap - (nint)minRegionIndex;
            GCCommon.seg_mapping_table = segmentMap - (nint)minRegionIndex;

            for (nuint offset = 0; offset < regionCount; offset++)
            {
                generationMap[(nint)offset] = region_info.RI_GEN_1;
                segmentMap[(nint)offset] = default;
                segmentMap[(nint)offset].region_info.gen_num = (byte)gc_generation_num.soh_gen1;
            }

            for (int index = 0; index < FilteredChildren; index++)
            {
                SetRegionGenerationForAddress(
                    children[index],
                    minRegionIndex,
                    generationMap,
                    segmentMap,
                    (int)gc_generation_num.soh_gen2);
            }

            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen1;
            byte** markList = stackalloc byte*[totalChildren];
            nuint* survived = stackalloc nuint[(int)regionCount];
            nuint* oldCardSurvived = stackalloc nuint[(int)regionCount];
            Assert.True(
                gc_heap.setup_mark_state_for_collection(
                    markList,
                    (nuint)totalChildren,
                    survived,
                    oldCardSurvived,
                    regionCount));

            mark* markStack = stackalloc mark[markStackEntryCount];
            gc_heap.mark_stack_array = markStack;
            gc_heap.mark_stack_array_length = (nuint)markStackEntryCount;

#if DEBUG
            gc_heap.init_promoted_bytes();
#endif

            gc_heap.mark_object_simple1(pHeap, root, root);

            Assert.Equal(expectedMarkedChildren, (int)(gc_heap.mark_list_index - gc_heap.mark_list));
            Assert.True(((CObjectHeader*)children[FilteredChildren + gc_heap.num_partial_refs])->IsMarked() != 0);

            for (int index = 0; index < FilteredChildren; index++)
            {
                Assert.Equal(0, ((CObjectHeader*)children[index])->IsMarked());
            }

            for (int index = FilteredChildren; index < FilteredChildren + expectedMarkedChildren; index++)
            {
                Assert.True(((CObjectHeader*)children[index])->IsMarked() != 0);
            }

            for (int index = FilteredChildren + expectedMarkedChildren; index < totalChildren; index++)
            {
                Assert.Equal(0, ((CObjectHeader*)children[index])->IsMarked());
            }

            Assert.Equal((nuint)expectedMarkedChildren * childObjectSize, SumRegionCounters(survived, regionCount));
            for (nuint offset = 0; offset < regionCount; offset++)
            {
                Assert.Equal((nuint)0, oldCardSurvived[(nint)offset]);
            }

#if DEBUG
            Assert.Equal(
                (nuint)expectedMarkedChildren * childObjectSize,
                gc_heap.promoted_bytes(pHeap->heap_number));
#endif

            for (int index = FilteredChildren + expectedMarkedChildren; index < totalChildren; index++)
            {
                Assert.Equal((nuint)children[index], (nuint)gc_heap.mark_queue.get_next_marked());
            }

            Assert.Equal((nuint)0, (nuint)gc_heap.mark_queue.get_next_marked());
            gc_heap.mark_queue.verify_empty();
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowestAddress;
            GCCommon.g_gc_highest_address = oldHighestAddress;
            GCCommon.seg_mapping_table = oldSegMappingTable;
            gc_heap.map_region_to_generation = oldGenerationMap;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
#if DEBUG
            gc_heap.g_promoted = savedPromoted;
#endif
        }
    }

    [Fact]
    public void MarkPhaseMarkObjectSimple1ResumesRepeatingDescriptorTraversalAndLeavesPrefetchTail()
    {
        using MarkPhaseStateScope _ = new();
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        const int ComponentWords = 2;
        const int BaseObjectWords = 2;
        const int ExpectedTailCount = 16;
        const nuint RegionShift = 4;
        nuint pointerSize = (nuint)sizeof(byte*);
        nuint childObjectSize = 2 * pointerSize;
        nuint componentSize = (nuint)ComponentWords * pointerSize;
        int slotsPerMark = MarkStackSlotsPerEntry();
        int markStackEntryCount = (gc_heap.num_partial_refs + 3 + slotsPerMark - 1) / slotsPerMark;
        int markStackSlotCapacity = MarkStackSlotCapacity((nuint)markStackEntryCount);
        int minComponentsForLargeObject =
            (gc_heap.partial_size_th - BaseObjectWords + (ComponentWords - 1)) / ComponentWords;
        int totalChildren = Math.Max(
            Math.Max(markStackSlotCapacity + 2, gc_heap.num_partial_refs + ExpectedTailCount + 1),
            minComponentsForLargeObject);
        nuint rootObjectSize = (nuint)(BaseObjectWords + (totalChildren * ComponentWords)) * pointerSize;
        int expectedMarkedChildren = totalChildren - ExpectedTailCount;

        Assert.True(markStackSlotCapacity > gc_heap.num_partial_refs + 2);
        Assert.True(totalChildren > markStackSlotCapacity - 2);
        Assert.True(expectedMarkedChildren > gc_heap.num_partial_refs);

        byte* objectStorage = stackalloc byte[
            checked((int)(rootObjectSize + ((nuint)totalChildren * childObjectSize) + pointerSize))];
        byte* root = AlignUp(objectStorage, pointerSize);
        byte* childStart = root + (nint)rootObjectSize;
        byte** children = stackalloc byte*[totalChildren];
        byte* rootDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        byte* childDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        MethodTable* rootMethodTable = InitializeRepeatingSeriesMethodTable(
            rootDescriptorStorage,
            (nuint)BaseObjectWords * pointerSize,
            componentSize,
            (nuint)BaseObjectWords * pointerSize,
            pointersPerComponent: 1,
            skipBytes: pointerSize);
        MethodTable* childMethodTable = InitializeSingleSeriesMethodTable(
            childDescriptorStorage,
            childObjectSize,
            (nuint)sizeof(nuint),
            pointerCount: (nuint)1,
            hasPointers: 1);
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowestAddress = GCCommon.g_gc_lowest_address;
        byte* oldHighestAddress = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
        region_info* oldGenerationMap = gc_heap.map_region_to_generation;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
#if DEBUG
        nuint savedPromoted = gc_heap.g_promoted;
#endif

        try
        {
            ((CObjectHeader*)root)->RawSetMethodTable(rootMethodTable);
            *(uint*)(root + sizeof(nuint)) = (uint)totalChildren;
            byte* componentData = root + (nint)((nuint)BaseObjectWords * pointerSize);

            for (int index = 0; index < totalChildren; index++)
            {
                byte* child = childStart + (nint)((nuint)index * childObjectSize);
                children[index] = child;
                ((CObjectHeader*)child)->RawSetMethodTable(childMethodTable);
                *(byte**)(child + sizeof(nuint)) = null;

                byte** componentSlots = (byte**)(componentData + (nint)((nuint)index * componentSize));
                componentSlots[0] = child;
                componentSlots[1] = null;
            }

            pHeap->heap_number = 0;
            gc_heap.min_segment_size_shr = RegionShift;
            GCCommon.g_gc_lowest_address = root;
            GCCommon.g_gc_highest_address = childStart + (nint)((nuint)totalChildren * childObjectSize);

            nuint minRegionIndex = (nuint)GCCommon.g_gc_lowest_address >> (int)RegionShift;
            nuint maxRegionIndex = ((nuint)GCCommon.g_gc_highest_address - 1) >> (int)RegionShift;
            nuint regionCount = maxRegionIndex - minRegionIndex + 1;
            region_info* generationMap = stackalloc region_info[(int)regionCount];
            seg_mapping* segmentMap = stackalloc seg_mapping[(int)regionCount];
            gc_heap.map_region_to_generation = generationMap;
            gc_heap.map_region_to_generation_skewed = generationMap - (nint)minRegionIndex;
            GCCommon.seg_mapping_table = segmentMap - (nint)minRegionIndex;

            for (nuint offset = 0; offset < regionCount; offset++)
            {
                generationMap[(nint)offset] = region_info.RI_GEN_1;
                segmentMap[(nint)offset] = default;
                segmentMap[(nint)offset].region_info.gen_num = (byte)gc_generation_num.soh_gen1;
            }

            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen1;
            byte** markList = stackalloc byte*[totalChildren];
            nuint* survived = stackalloc nuint[(int)regionCount];
            nuint* oldCardSurvived = stackalloc nuint[(int)regionCount];
            Assert.True(
                gc_heap.setup_mark_state_for_collection(
                    markList,
                    (nuint)totalChildren,
                    survived,
                    oldCardSurvived,
                    regionCount));

            mark* markStack = stackalloc mark[markStackEntryCount];
            gc_heap.mark_stack_array = markStack;
            gc_heap.mark_stack_array_length = (nuint)markStackEntryCount;

#if DEBUG
            gc_heap.init_promoted_bytes();
#endif

            gc_heap.mark_object_simple1(pHeap, root, root);

            Assert.Equal(expectedMarkedChildren, (int)(gc_heap.mark_list_index - gc_heap.mark_list));
            Assert.True(((CObjectHeader*)children[gc_heap.num_partial_refs])->IsMarked() != 0);

            for (int index = 0; index < expectedMarkedChildren; index++)
            {
                Assert.True(((CObjectHeader*)children[index])->IsMarked() != 0);
            }

            for (int index = expectedMarkedChildren; index < totalChildren; index++)
            {
                Assert.Equal(0, ((CObjectHeader*)children[index])->IsMarked());
            }

            Assert.Equal((nuint)expectedMarkedChildren * childObjectSize, SumRegionCounters(survived, regionCount));
            for (nuint offset = 0; offset < regionCount; offset++)
            {
                Assert.Equal((nuint)0, oldCardSurvived[(nint)offset]);
            }

#if DEBUG
            Assert.Equal(
                (nuint)expectedMarkedChildren * childObjectSize,
                gc_heap.promoted_bytes(pHeap->heap_number));
#endif

            for (int index = expectedMarkedChildren; index < totalChildren; index++)
            {
                Assert.Equal((nuint)children[index], (nuint)gc_heap.mark_queue.get_next_marked());
            }

            Assert.Equal((nuint)0, (nuint)gc_heap.mark_queue.get_next_marked());
            gc_heap.mark_queue.verify_empty();
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowestAddress;
            GCCommon.g_gc_highest_address = oldHighestAddress;
            GCCommon.seg_mapping_table = oldSegMappingTable;
            gc_heap.map_region_to_generation = oldGenerationMap;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
#if DEBUG
            gc_heap.g_promoted = savedPromoted;
#endif
        }
    }

    [Fact]
    public void MarkPhaseMarkObjectSimple1FullGcUsesBoundaryWithoutMarkListWrites()
    {
        using MarkPhaseStateScope _ = new();
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        const int TotalChildren = 20;
        const int ExpectedTailCount = 16;
        const nuint RegionShift = 4;
        nuint pointerSize = (nuint)sizeof(byte*);
        nuint rootObjectSize = 128 * pointerSize;
        nuint childObjectSize = 2 * pointerSize;
        byte* objectStorage = stackalloc byte[(int)(rootObjectSize + ((nuint)TotalChildren * childObjectSize) + pointerSize)];
        byte* root = AlignUp(objectStorage, pointerSize);
        byte* childStart = root + (nint)rootObjectSize;
        byte** children = stackalloc byte*[TotalChildren];
        byte** rootReferences = (byte**)(root + sizeof(nuint));
        byte* rootDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        byte* childDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        MethodTable* rootMethodTable = InitializeSingleSeriesMethodTable(
            rootDescriptorStorage,
            rootObjectSize,
            (nuint)sizeof(nuint),
            (nuint)TotalChildren,
            hasPointers: 1);
        MethodTable* childMethodTable = InitializeSingleSeriesMethodTable(
            childDescriptorStorage,
            childObjectSize,
            (nuint)sizeof(nuint),
            pointerCount: (nuint)1,
            hasPointers: 1);
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowestAddress = GCCommon.g_gc_lowest_address;
        byte* oldHighestAddress = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
        region_info* oldGenerationMap = gc_heap.map_region_to_generation;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
#if DEBUG
        nuint savedPromoted = gc_heap.g_promoted;
#endif

        try
        {
            ((CObjectHeader*)root)->RawSetMethodTable(rootMethodTable);

            for (int index = 0; index < TotalChildren; index++)
            {
                byte* child = childStart + (nint)((nuint)index * childObjectSize);
                children[index] = child;
                ((CObjectHeader*)child)->RawSetMethodTable(childMethodTable);
                *(byte**)(child + sizeof(nuint)) = null;
                rootReferences[index] = child;
            }

            pHeap->heap_number = 0;
            gc_heap.min_segment_size_shr = RegionShift;
            GCCommon.g_gc_lowest_address = root;
            GCCommon.g_gc_highest_address = childStart + (nint)((nuint)TotalChildren * childObjectSize);

            nuint minRegionIndex = (nuint)GCCommon.g_gc_lowest_address >> (int)RegionShift;
            nuint maxRegionIndex = ((nuint)GCCommon.g_gc_highest_address - 1) >> (int)RegionShift;
            nuint regionCount = maxRegionIndex - minRegionIndex + 1;
            region_info* generationMap = stackalloc region_info[(int)regionCount];
            seg_mapping* segmentMap = stackalloc seg_mapping[(int)regionCount];
            gc_heap.map_region_to_generation = generationMap;
            gc_heap.map_region_to_generation_skewed = generationMap - (nint)minRegionIndex;
            GCCommon.seg_mapping_table = segmentMap - (nint)minRegionIndex;

            for (nuint offset = 0; offset < regionCount; offset++)
            {
                generationMap[(nint)offset] = region_info.RI_GEN_2;
                segmentMap[(nint)offset] = default;
                segmentMap[(nint)offset].region_info.gen_num = (byte)gc_generation_num.soh_gen2;
            }

            gc_heap.settings.condemned_generation = GCInterfaceOffsets.max_generation;
            byte** markList = stackalloc byte*[2];
            markList[0] = (byte*)0xDEAD;
            markList[1] = (byte*)0xBEEF;
            nuint* survived = stackalloc nuint[(int)regionCount];
            nuint* oldCardSurvived = stackalloc nuint[(int)regionCount];
            Assert.True(gc_heap.setup_mark_state_for_collection(markList, 2, survived, oldCardSurvived, regionCount));

            mark* markStack = stackalloc mark[8];
            gc_heap.mark_stack_array = markStack;
            gc_heap.mark_stack_array_length = 8;

#if DEBUG
            gc_heap.init_promoted_bytes();
#endif

            gc_heap.mark_object_simple1(pHeap, root, root);

            int expectedMarkedChildren = TotalChildren - ExpectedTailCount;
            Assert.Equal((nuint)markList, (nuint)gc_heap.mark_list_index);
            Assert.Equal((nuint)0xDEAD, (nuint)markList[0]);
            Assert.Equal((nuint)0xBEEF, (nuint)markList[1]);
            Assert.Equal((nuint)children[0], (nuint)gc_heap.slow);
            Assert.Equal((nuint)children[expectedMarkedChildren - 1], (nuint)gc_heap.shigh);

            for (int index = 0; index < expectedMarkedChildren; index++)
            {
                Assert.True(((CObjectHeader*)children[index])->IsMarked() != 0);
            }

            for (int index = expectedMarkedChildren; index < TotalChildren; index++)
            {
                Assert.Equal(0, ((CObjectHeader*)children[index])->IsMarked());
            }

            Assert.Equal((nuint)expectedMarkedChildren * childObjectSize, SumRegionCounters(survived, regionCount));
            for (nuint offset = 0; offset < regionCount; offset++)
            {
                Assert.Equal((nuint)0, oldCardSurvived[(nint)offset]);
            }

#if DEBUG
            Assert.Equal(
                (nuint)expectedMarkedChildren * childObjectSize,
                gc_heap.promoted_bytes(pHeap->heap_number));
#endif
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowestAddress;
            GCCommon.g_gc_highest_address = oldHighestAddress;
            GCCommon.seg_mapping_table = oldSegMappingTable;
            gc_heap.map_region_to_generation = oldGenerationMap;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
#if DEBUG
            gc_heap.g_promoted = savedPromoted;
#endif
        }
    }

    [Fact]
    public void MarkPhaseMarkObjectSimple1SmallObjectOverflowTracksExtrema()
    {
        using MarkPhaseStateScope _ = new();
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        int pointerCount = (int)((nuint)sizeof(mark) / (nuint)sizeof(byte*)) + 4;
        nuint objectSize = ((nuint)pointerCount + 1) * (nuint)sizeof(byte*);
        byte* objectStorage = stackalloc byte[(int)(objectSize + (nuint)sizeof(byte*))];
        byte* descriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        MethodTable* methodTable = InitializeSingleSeriesMethodTable(
            descriptorStorage,
            objectSize,
            (nuint)sizeof(nuint),
            (nuint)pointerCount,
            hasPointers: 1);
        byte* root = AlignUp(objectStorage, (nuint)sizeof(byte*));
        byte** references = (byte**)(root + sizeof(nuint));

        ((CObjectHeader*)root)->RawSetMethodTable(methodTable);
        for (int index = 0; index < pointerCount; index++)
        {
            references[index] = null;
        }

        gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen1;
        byte** markList = stackalloc byte*[4];
        nuint* survived = stackalloc nuint[1];
        nuint* oldCardSurvived = stackalloc nuint[1];
        Assert.True(gc_heap.setup_mark_state_for_collection(markList, 4, survived, oldCardSurvived, 1));

        mark* markStack = stackalloc mark[1];
        gc_heap.mark_stack_array = markStack;
        gc_heap.mark_stack_array_length = 1;
        pHeap->heap_number = 0;

        gc_heap.mark_object_simple1(pHeap, root, root);

        Assert.Equal((nuint)root, (nuint)gc_heap.min_overflow_address);
        Assert.Equal((nuint)root, (nuint)gc_heap.max_overflow_address);
        Assert.Equal(0, (int)(gc_heap.mark_list_index - gc_heap.mark_list));
        Assert.Equal((nuint)0, survived[0]);
    }

    [Fact]
    public void MarkPhaseMarkObjectSimple1LargeObjectOverflowTracksExtremaAndSkipsTraversal()
    {
        using MarkPhaseStateScope _ = new();
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        const int RootPointerCount = 2;
        nuint pointerSize = (nuint)sizeof(byte*);
        nuint rootObjectSize = (nuint)gc_heap.partial_size_th * pointerSize;
        nuint childObjectSize = 2 * pointerSize;
        byte* objectStorage = stackalloc byte[
            checked((int)((2 * rootObjectSize) + (2 * childObjectSize) + pointerSize))];
        byte* rootLow = AlignUp(objectStorage, pointerSize);
        byte* rootHigh = rootLow + (nint)rootObjectSize;
        byte* childLow = rootHigh + (nint)rootObjectSize;
        byte* childHigh = childLow + (nint)childObjectSize;
        byte** lowReferences = (byte**)(rootLow + sizeof(nuint));
        byte** highReferences = (byte**)(rootHigh + sizeof(nuint));
        byte* rootDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        byte* childDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        MethodTable* rootMethodTable = InitializeSingleSeriesMethodTable(
            rootDescriptorStorage,
            rootObjectSize,
            (nuint)sizeof(nuint),
            pointerCount: RootPointerCount,
            hasPointers: 1);
        MethodTable* childMethodTable = InitializeSingleSeriesMethodTable(
            childDescriptorStorage,
            childObjectSize,
            (nuint)sizeof(nuint),
            pointerCount: (nuint)1,
            hasPointers: 1);
        byte* oldLowestAddress = GCCommon.g_gc_lowest_address;
        byte* oldHighestAddress = GCCommon.g_gc_highest_address;

        try
        {
            ((CObjectHeader*)rootLow)->RawSetMethodTable(rootMethodTable);
            ((CObjectHeader*)rootHigh)->RawSetMethodTable(rootMethodTable);
            ((CObjectHeader*)childLow)->RawSetMethodTable(childMethodTable);
            ((CObjectHeader*)childHigh)->RawSetMethodTable(childMethodTable);
            *(byte**)(childLow + sizeof(nuint)) = null;
            *(byte**)(childHigh + sizeof(nuint)) = null;
            lowReferences[0] = childLow;
            lowReferences[1] = childHigh;
            highReferences[0] = childLow;
            highReferences[1] = childHigh;

            GCCommon.g_gc_lowest_address = rootLow;
            GCCommon.g_gc_highest_address = childHigh + (nint)childObjectSize;

            gc_heap.settings.condemned_generation = GCInterfaceOffsets.max_generation;
            byte** markList = stackalloc byte*[4];
            nuint* survived = stackalloc nuint[1];
            nuint* oldCardSurvived = stackalloc nuint[1];
            Assert.True(gc_heap.setup_mark_state_for_collection(markList, 4, survived, oldCardSurvived, 1));

            mark* markStack = stackalloc mark[1];
            int slotsPerMark = MarkStackSlotsPerEntry();
            nuint markStackLength = (nuint)((gc_heap.num_partial_refs + 2) / slotsPerMark);
            Assert.True(MarkStackSlotCapacity(markStackLength) <= gc_heap.num_partial_refs + 2);

            gc_heap.mark_stack_array = markStack;
            gc_heap.mark_stack_array_length = markStackLength;
            pHeap->heap_number = 0;

            gc_heap.mark_object_simple1(pHeap, rootHigh, rootHigh);
            gc_heap.mark_object_simple1(pHeap, rootLow, rootLow);

            Assert.Equal((nuint)rootLow, (nuint)gc_heap.min_overflow_address);
            Assert.Equal((nuint)rootHigh, (nuint)gc_heap.max_overflow_address);
            Assert.Equal(0, (int)(gc_heap.mark_list_index - gc_heap.mark_list));
            Assert.Equal((nuint)0, survived[0]);
            Assert.Equal((nuint)0, oldCardSurvived[0]);
            Assert.Equal(0, ((CObjectHeader*)childLow)->IsMarked());
            Assert.Equal(0, ((CObjectHeader*)childHigh)->IsMarked());
            Assert.Equal((nuint)0, (nuint)gc_heap.mark_queue.get_next_marked());
            gc_heap.mark_queue.verify_empty();
        }
        finally
        {
            GCCommon.g_gc_lowest_address = oldLowestAddress;
            GCCommon.g_gc_highest_address = oldHighestAddress;
        }
    }

    [Fact]
    public void MarkPhaseMarkObjectSimple1SmallObjectUsesGetNumPointersSecondChance()
    {
        using MarkPhaseStateScope _ = new();
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        nuint pointerSize = (nuint)sizeof(byte*);
        nuint childObjectSize = 2 * pointerSize;
        nuint markStackLength = 1;
        int markStackSlotCapacity = MarkStackSlotCapacity(markStackLength);
        Assert.True(markStackSlotCapacity < gc_heap.partial_size_th);

        nuint objectSize = (nuint)markStackSlotCapacity * pointerSize;
        byte* objectStorage = stackalloc byte[
            checked((int)(objectSize + childObjectSize + pointerSize))];
        byte* root = AlignUp(objectStorage, pointerSize);
        byte* child = root + (nint)objectSize;
        byte** references = (byte**)(root + sizeof(nuint));
        byte* rootDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        MethodTable* rootMethodTable = InitializeSingleSeriesMethodTable(
            rootDescriptorStorage,
            objectSize,
            (nuint)sizeof(nuint),
            pointerCount: (nuint)1,
            hasPointers: 1);
        MethodTable childMethodTable = default;
        childMethodTable.m_uBaseSize = (uint)childObjectSize;
        byte* oldLowestAddress = GCCommon.g_gc_lowest_address;
        byte* oldHighestAddress = GCCommon.g_gc_highest_address;

        try
        {
            ((CObjectHeader*)root)->RawSetMethodTable(rootMethodTable);
            ((CObjectHeader*)child)->RawSetMethodTable(&childMethodTable);
            references[0] = child;
            GCCommon.g_gc_lowest_address = root;
            GCCommon.g_gc_highest_address = child + (nint)childObjectSize;

            gc_heap.settings.condemned_generation = GCInterfaceOffsets.max_generation;
            byte** markList = stackalloc byte*[2];
            nuint* survived = stackalloc nuint[1];
            nuint* oldCardSurvived = stackalloc nuint[1];
            Assert.True(gc_heap.setup_mark_state_for_collection(markList, 2, survived, oldCardSurvived, 1));

            mark* markStack = stackalloc mark[1];
            gc_heap.mark_stack_array = markStack;
            gc_heap.mark_stack_array_length = markStackLength;
            pHeap->heap_number = 0;

            int conservativePointerSlots = (int)(objectSize / pointerSize);
            int exactPointerSlots = (int)CGCDesc.GetNumPointers(rootMethodTable, objectSize, numComponents: 0);
            Assert.True(conservativePointerSlots >= markStackSlotCapacity - 1);
            Assert.True(exactPointerSlots < markStackSlotCapacity - 1);

            gc_heap.mark_object_simple1(pHeap, root, root);

            Assert.Equal(nuint.MaxValue, (nuint)gc_heap.min_overflow_address);
            Assert.Equal((nuint)0, (nuint)gc_heap.max_overflow_address);
            Assert.Equal((nuint)markList, (nuint)gc_heap.mark_list_index);
            Assert.Equal((nuint)0, survived[0]);
            Assert.Equal((nuint)0, oldCardSurvived[0]);
            Assert.Equal(0, ((CObjectHeader*)child)->IsMarked());
            Assert.Equal((nuint)child, (nuint)gc_heap.mark_queue.get_next_marked());
            Assert.True(((CObjectHeader*)child)->IsMarked() != 0);
            Assert.Equal((nuint)0, (nuint)gc_heap.mark_queue.get_next_marked());
            gc_heap.mark_queue.verify_empty();
        }
        finally
        {
            GCCommon.g_gc_lowest_address = oldLowestAddress;
            GCCommon.g_gc_highest_address = oldHighestAddress;
        }
    }

    [Fact]
    public void MarkPhaseMarkObjectSimple1SkipsAlreadyMarkedCycleReferences()
    {
        using MarkPhaseStateScope _ = new();
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        const int RootReferenceCount = 17;
        const nuint RegionShift = 4;
        nuint pointerSize = (nuint)sizeof(byte*);
        nuint rootObjectSize = 32 * pointerSize;
        nuint childObjectSize = 2 * pointerSize;
        byte* objectStorage = stackalloc byte[(int)(rootObjectSize + childObjectSize + pointerSize)];
        byte* root = AlignUp(objectStorage, pointerSize);
        byte* child = root + (nint)rootObjectSize;
        byte** rootReferences = (byte**)(root + sizeof(nuint));
        byte* rootDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        byte* childDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        MethodTable* rootMethodTable = InitializeSingleSeriesMethodTable(
            rootDescriptorStorage,
            rootObjectSize,
            (nuint)sizeof(nuint),
            (nuint)RootReferenceCount,
            hasPointers: 1);
        MethodTable* childMethodTable = InitializeSingleSeriesMethodTable(
            childDescriptorStorage,
            childObjectSize,
            (nuint)sizeof(nuint),
            pointerCount: (nuint)1,
            hasPointers: 1);
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowestAddress = GCCommon.g_gc_lowest_address;
        byte* oldHighestAddress = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
        region_info* oldGenerationMap = gc_heap.map_region_to_generation;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
#if DEBUG
        nuint savedPromoted = gc_heap.g_promoted;
#endif

        try
        {
            ((CObjectHeader*)root)->RawSetMethodTable(rootMethodTable);
            ((CObjectHeader*)child)->RawSetMethodTable(childMethodTable);
            *(byte**)(child + sizeof(nuint)) = child;

            for (int index = 0; index < RootReferenceCount; index++)
            {
                rootReferences[index] = child;
            }

            pHeap->heap_number = 0;
            gc_heap.min_segment_size_shr = RegionShift;
            GCCommon.g_gc_lowest_address = root;
            GCCommon.g_gc_highest_address = child + (nint)childObjectSize;

            nuint minRegionIndex = (nuint)GCCommon.g_gc_lowest_address >> (int)RegionShift;
            nuint maxRegionIndex = ((nuint)GCCommon.g_gc_highest_address - 1) >> (int)RegionShift;
            nuint regionCount = maxRegionIndex - minRegionIndex + 1;
            region_info* generationMap = stackalloc region_info[(int)regionCount];
            seg_mapping* segmentMap = stackalloc seg_mapping[(int)regionCount];
            gc_heap.map_region_to_generation = generationMap;
            gc_heap.map_region_to_generation_skewed = generationMap - (nint)minRegionIndex;
            GCCommon.seg_mapping_table = segmentMap - (nint)minRegionIndex;

            for (nuint offset = 0; offset < regionCount; offset++)
            {
                generationMap[(nint)offset] = region_info.RI_GEN_1;
                segmentMap[(nint)offset] = default;
                segmentMap[(nint)offset].region_info.gen_num = (byte)gc_generation_num.soh_gen1;
            }

            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen1;
            byte** markList = stackalloc byte*[8];
            nuint* survived = stackalloc nuint[(int)regionCount];
            nuint* oldCardSurvived = stackalloc nuint[(int)regionCount];
            Assert.True(gc_heap.setup_mark_state_for_collection(markList, 8, survived, oldCardSurvived, regionCount));

            mark* markStack = stackalloc mark[4];
            gc_heap.mark_stack_array = markStack;
            gc_heap.mark_stack_array_length = 4;

#if DEBUG
            gc_heap.init_promoted_bytes();
#endif

            gc_heap.mark_object_simple1(pHeap, root, root);

            Assert.Equal(1, (int)(gc_heap.mark_list_index - gc_heap.mark_list));
            Assert.True(((CObjectHeader*)child)->IsMarked() != 0);
            Assert.Equal(childObjectSize, SumRegionCounters(survived, regionCount));
#if DEBUG
            Assert.Equal(childObjectSize, gc_heap.promoted_bytes(pHeap->heap_number));
#endif
            Assert.Equal((nuint)0, (nuint)gc_heap.mark_queue.get_next_marked());
            gc_heap.mark_queue.verify_empty();
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowestAddress;
            GCCommon.g_gc_highest_address = oldHighestAddress;
            GCCommon.seg_mapping_table = oldSegMappingTable;
            gc_heap.map_region_to_generation = oldGenerationMap;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
#if DEBUG
            gc_heap.g_promoted = savedPromoted;
#endif
        }
    }

    [Fact]
    public void MarkPhaseMarkObjectSimpleDelaysRootAndDrainProcessesPrefetchTailWithGenerationFiltering()
    {
        using MarkPhaseStateScope _ = new();
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        const int FilteredChildren = 3;
        const int ValidChildren = 17;
        const int TotalChildren = FilteredChildren + ValidChildren;
        const nuint RegionShift = 4;
        nuint pointerSize = (nuint)sizeof(byte*);
        nuint childObjectSize = 2 * pointerSize;
        nuint rootObjectSize = (nuint)(TotalChildren + 1) * pointerSize;
        nuint expectedPromoted = rootObjectSize + ((nuint)ValidChildren * childObjectSize);
        byte* objectStorage = stackalloc byte[
            checked((int)(rootObjectSize + ((nuint)TotalChildren * childObjectSize) + pointerSize))];
        byte* root = AlignUp(objectStorage, pointerSize);
        byte* childStart = root + (nint)rootObjectSize;
        byte** children = stackalloc byte*[TotalChildren];
        byte** rootReferences = (byte**)(root + sizeof(nuint));
        byte* rootDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        byte* childDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        MethodTable* rootMethodTable = InitializeSingleSeriesMethodTable(
            rootDescriptorStorage,
            rootObjectSize,
            (nuint)sizeof(nuint),
            (nuint)TotalChildren,
            hasPointers: 1);
        MethodTable* childMethodTable = InitializeSingleSeriesMethodTable(
            childDescriptorStorage,
            childObjectSize,
            (nuint)sizeof(nuint),
            pointerCount: 0,
            hasPointers: 0);
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowestAddress = GCCommon.g_gc_lowest_address;
        byte* oldHighestAddress = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
        region_info* oldGenerationMap = gc_heap.map_region_to_generation;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
#if DEBUG
        nuint savedPromoted = gc_heap.g_promoted;
#endif

        try
        {
            ((CObjectHeader*)root)->RawSetMethodTable(rootMethodTable);
            for (int index = 0; index < TotalChildren; index++)
            {
                byte* child = childStart + (nint)((nuint)index * childObjectSize);
                children[index] = child;
                ((CObjectHeader*)child)->RawSetMethodTable(childMethodTable);
                rootReferences[index] = child;
            }

            pHeap->heap_number = 0;
            gc_heap.min_segment_size_shr = RegionShift;
            GCCommon.g_gc_lowest_address = root;
            GCCommon.g_gc_highest_address = childStart + (nint)((nuint)TotalChildren * childObjectSize);

            nuint minRegionIndex = (nuint)GCCommon.g_gc_lowest_address >> (int)RegionShift;
            nuint maxRegionIndex = ((nuint)GCCommon.g_gc_highest_address - 1) >> (int)RegionShift;
            nuint regionCount = maxRegionIndex - minRegionIndex + 1;
            region_info* generationMap = stackalloc region_info[(int)regionCount];
            seg_mapping* segmentMap = stackalloc seg_mapping[(int)regionCount];
            gc_heap.map_region_to_generation = generationMap;
            gc_heap.map_region_to_generation_skewed = generationMap - (nint)minRegionIndex;
            GCCommon.seg_mapping_table = segmentMap - (nint)minRegionIndex;
            InitializeRegionGenerationMaps(
                generationMap,
                segmentMap,
                regionCount,
                (int)gc_generation_num.soh_gen1);

            // mark_object_simple assumes callers already did exact collection-range checks,
            // so this root stays eligible even when its generation is above condemned.
            SetRegionGenerationForAddress(
                root,
                minRegionIndex,
                generationMap,
                segmentMap,
                (int)gc_generation_num.soh_gen2);

            for (int index = 0; index < FilteredChildren; index++)
            {
                SetRegionGenerationForAddress(
                    children[index],
                    minRegionIndex,
                    generationMap,
                    segmentMap,
                    (int)gc_generation_num.soh_gen2);
            }

            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen1;
            byte** markList = stackalloc byte*[TotalChildren + 1];
            nuint* survived = stackalloc nuint[(int)regionCount];
            nuint* oldCardSurvived = stackalloc nuint[(int)regionCount];
            Assert.True(
                gc_heap.setup_mark_state_for_collection(
                    markList,
                    (nuint)(TotalChildren + 1),
                    survived,
                    oldCardSurvived,
                    regionCount));

#if DEBUG
            gc_heap.init_promoted_bytes();
#endif

            byte* rootReference = root;
            gc_heap.mark_object_simple(pHeap, &rootReference);

            Assert.Equal(0, ((CObjectHeader*)root)->IsMarked());
            Assert.Equal(0, (int)(gc_heap.mark_list_index - gc_heap.mark_list));
            Assert.Equal((nuint)0, SumRegionCounters(survived, regionCount));

            gc_heap.drain_mark_queue(pHeap);

            Assert.True(((CObjectHeader*)root)->IsMarked() != 0);
            for (int index = 0; index < FilteredChildren; index++)
            {
                Assert.Equal(0, ((CObjectHeader*)children[index])->IsMarked());
            }

            for (int index = FilteredChildren; index < TotalChildren; index++)
            {
                Assert.True(((CObjectHeader*)children[index])->IsMarked() != 0);
            }

            Assert.Equal(1 + ValidChildren, (int)(gc_heap.mark_list_index - gc_heap.mark_list));
            Assert.Equal((nuint)root, (nuint)gc_heap.slow);
            Assert.Equal((nuint)children[TotalChildren - 1], (nuint)gc_heap.shigh);
            Assert.Equal(expectedPromoted, SumRegionCounters(survived, regionCount));
            for (nuint offset = 0; offset < regionCount; offset++)
            {
                Assert.Equal((nuint)0, oldCardSurvived[(nint)offset]);
            }

            Assert.Equal(nuint.MaxValue, (nuint)gc_heap.min_overflow_address);
            Assert.Equal((nuint)0, (nuint)gc_heap.max_overflow_address);
#if DEBUG
            Assert.Equal(expectedPromoted, gc_heap.promoted_bytes(pHeap->heap_number));
#endif
            Assert.Equal((nuint)0, (nuint)gc_heap.mark_queue.get_next_marked());
            gc_heap.mark_queue.verify_empty();
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowestAddress;
            GCCommon.g_gc_highest_address = oldHighestAddress;
            GCCommon.seg_mapping_table = oldSegMappingTable;
            gc_heap.map_region_to_generation = oldGenerationMap;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
#if DEBUG
            gc_heap.g_promoted = savedPromoted;
#endif
        }
    }

    [Fact]
    public void MarkPhaseMarkObjectSimpleProcessesLivePrefetchEntryBeforeDrainAndPreservesTailState()
    {
        using MarkPhaseStateScope _ = new();
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        const int MarkQueueSlotCount = 16;
        const nuint RegionShift = 4;
        nuint pointerSize = (nuint)sizeof(byte*);
        nuint pointerObjectSize = 2 * pointerSize;
        nuint leafObjectSize = 2 * pointerSize;
        nuint expectedPromotedBeforeDrain = 2 * pointerObjectSize;
        nuint expectedPromotedAfterDrain = expectedPromotedBeforeDrain + (3 * leafObjectSize);
        byte* objectStorage = stackalloc byte[
            checked((int)((2 * pointerObjectSize) + (3 * leafObjectSize) + pointerSize))];
        byte* queuedRoot = AlignUp(objectStorage, pointerSize);
        byte* queuedPointerChild = queuedRoot + (nint)pointerObjectSize;
        byte* deferredRoot = queuedPointerChild + (nint)pointerObjectSize;
        byte* deferredFromRootChild = deferredRoot + (nint)leafObjectSize;
        byte* deferredFromSimple1Child = deferredFromRootChild + (nint)leafObjectSize;
        byte** queuedRootReferences = (byte**)(queuedRoot + sizeof(nuint));
        byte** queuedPointerChildReferences = (byte**)(queuedPointerChild + sizeof(nuint));
        byte* pointerDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        byte* leafDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        MethodTable* pointerMethodTable = InitializeSingleSeriesMethodTable(
            pointerDescriptorStorage,
            pointerObjectSize,
            (nuint)sizeof(nuint),
            pointerCount: 1,
            hasPointers: 1);
        MethodTable* leafMethodTable = InitializeSingleSeriesMethodTable(
            leafDescriptorStorage,
            leafObjectSize,
            (nuint)sizeof(nuint),
            pointerCount: 0,
            hasPointers: 0);
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowestAddress = GCCommon.g_gc_lowest_address;
        byte* oldHighestAddress = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
        region_info* oldGenerationMap = gc_heap.map_region_to_generation;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
#if DEBUG
        nuint savedPromoted = gc_heap.g_promoted;
#endif

        try
        {
            ((CObjectHeader*)queuedRoot)->RawSetMethodTable(pointerMethodTable);
            ((CObjectHeader*)queuedPointerChild)->RawSetMethodTable(pointerMethodTable);
            ((CObjectHeader*)deferredRoot)->RawSetMethodTable(leafMethodTable);
            ((CObjectHeader*)deferredFromRootChild)->RawSetMethodTable(leafMethodTable);
            ((CObjectHeader*)deferredFromSimple1Child)->RawSetMethodTable(leafMethodTable);
            queuedRootReferences[0] = deferredFromRootChild;
            queuedPointerChildReferences[0] = deferredFromSimple1Child;

            pHeap->heap_number = 0;
            gc_heap.min_segment_size_shr = RegionShift;
            GCCommon.g_gc_lowest_address = queuedRoot;
            GCCommon.g_gc_highest_address = deferredFromSimple1Child + (nint)leafObjectSize;

            nuint minRegionIndex = (nuint)GCCommon.g_gc_lowest_address >> (int)RegionShift;
            nuint maxRegionIndex = ((nuint)GCCommon.g_gc_highest_address - 1) >> (int)RegionShift;
            nuint regionCount = maxRegionIndex - minRegionIndex + 1;
            region_info* generationMap = stackalloc region_info[(int)regionCount];
            seg_mapping* segmentMap = stackalloc seg_mapping[(int)regionCount];
            gc_heap.map_region_to_generation = generationMap;
            gc_heap.map_region_to_generation_skewed = generationMap - (nint)minRegionIndex;
            GCCommon.seg_mapping_table = segmentMap - (nint)minRegionIndex;
            InitializeRegionGenerationMaps(
                generationMap,
                segmentMap,
                regionCount,
                (int)gc_generation_num.soh_gen1);

            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen1;
            byte** markList = stackalloc byte*[8];
            nuint* survived = stackalloc nuint[(int)regionCount];
            nuint* oldCardSurvived = stackalloc nuint[(int)regionCount];
            Assert.True(gc_heap.setup_mark_state_for_collection(markList, 8, survived, oldCardSurvived, regionCount));

            mark* markStack = stackalloc mark[4];
            gc_heap.mark_stack_array = markStack;
            gc_heap.mark_stack_array_length = 4;

#if DEBUG
            gc_heap.init_promoted_bytes();
#endif

            Assert.Equal((nuint)0, (nuint)gc_heap.mark_queue.queue_mark(queuedRoot));
            Assert.Equal((nuint)0, (nuint)gc_heap.mark_queue.queue_mark(queuedPointerChild));
            for (int slotIndex = 2; slotIndex < MarkQueueSlotCount; slotIndex++)
            {
                Assert.Equal((nuint)0, (nuint)gc_heap.mark_queue.queue_mark(null));
            }

            Assert.Equal((nuint)0, ReadMarkQueueTailSlotIndex());
            Assert.Equal((nuint)queuedRoot, (nuint)ReadMarkQueueSlot(0));
            Assert.Equal((nuint)queuedPointerChild, (nuint)ReadMarkQueueSlot(1));

            byte* rootReference = deferredRoot;
            gc_heap.mark_object_simple(pHeap, &rootReference);

            Assert.True(((CObjectHeader*)queuedRoot)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)queuedPointerChild)->IsMarked() != 0);
            Assert.Equal(0, ((CObjectHeader*)deferredRoot)->IsMarked());
            Assert.Equal(0, ((CObjectHeader*)deferredFromRootChild)->IsMarked());
            Assert.Equal(0, ((CObjectHeader*)deferredFromSimple1Child)->IsMarked());
            Assert.Equal(2, (int)(gc_heap.mark_list_index - gc_heap.mark_list));
            Assert.Equal((nuint)queuedRoot, (nuint)markList[0]);
            Assert.Equal((nuint)queuedPointerChild, (nuint)markList[1]);
            Assert.Equal((nuint)queuedRoot, (nuint)gc_heap.slow);
            Assert.Equal((nuint)queuedPointerChild, (nuint)gc_heap.shigh);
            Assert.Equal(expectedPromotedBeforeDrain, SumRegionCounters(survived, regionCount));
            for (nuint offset = 0; offset < regionCount; offset++)
            {
                Assert.Equal((nuint)0, oldCardSurvived[(nint)offset]);
            }

            Assert.Equal(nuint.MaxValue, (nuint)gc_heap.min_overflow_address);
            Assert.Equal((nuint)0, (nuint)gc_heap.max_overflow_address);
#if DEBUG
            Assert.Equal(expectedPromotedBeforeDrain, gc_heap.promoted_bytes(pHeap->heap_number));
#endif

            Assert.Equal((nuint)3, ReadMarkQueueTailSlotIndex());
            Assert.Equal((nuint)deferredRoot, (nuint)ReadMarkQueueSlot(0));
            Assert.Equal((nuint)deferredFromRootChild, (nuint)ReadMarkQueueSlot(1));
            Assert.Equal((nuint)deferredFromSimple1Child, (nuint)ReadMarkQueueSlot(2));
            for (int slotIndex = 3; slotIndex < MarkQueueSlotCount; slotIndex++)
            {
                Assert.Equal((nuint)0, (nuint)ReadMarkQueueSlot(slotIndex));
            }

            gc_heap.drain_mark_queue(pHeap);

            Assert.True(((CObjectHeader*)deferredRoot)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)deferredFromRootChild)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)deferredFromSimple1Child)->IsMarked() != 0);
            Assert.Equal(5, (int)(gc_heap.mark_list_index - gc_heap.mark_list));
            Assert.Equal((nuint)queuedRoot, (nuint)markList[0]);
            Assert.Equal((nuint)queuedPointerChild, (nuint)markList[1]);
            Assert.Equal((nuint)deferredRoot, (nuint)markList[2]);
            Assert.Equal((nuint)deferredFromRootChild, (nuint)markList[3]);
            Assert.Equal((nuint)deferredFromSimple1Child, (nuint)markList[4]);
            Assert.Equal((nuint)queuedRoot, (nuint)gc_heap.slow);
            Assert.Equal((nuint)deferredFromSimple1Child, (nuint)gc_heap.shigh);
            Assert.Equal(expectedPromotedAfterDrain, SumRegionCounters(survived, regionCount));
            for (nuint offset = 0; offset < regionCount; offset++)
            {
                Assert.Equal((nuint)0, oldCardSurvived[(nint)offset]);
            }

            Assert.Equal(nuint.MaxValue, (nuint)gc_heap.min_overflow_address);
            Assert.Equal((nuint)0, (nuint)gc_heap.max_overflow_address);
#if DEBUG
            Assert.Equal(expectedPromotedAfterDrain, gc_heap.promoted_bytes(pHeap->heap_number));
#endif
            Assert.Equal((nuint)3, ReadMarkQueueTailSlotIndex());
            for (int slotIndex = 0; slotIndex < MarkQueueSlotCount; slotIndex++)
            {
                Assert.Equal((nuint)0, (nuint)ReadMarkQueueSlot(slotIndex));
            }

            Assert.Equal((nuint)0, (nuint)gc_heap.mark_queue.get_next_marked());
            gc_heap.mark_queue.verify_empty();
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowestAddress;
            GCCommon.g_gc_highest_address = oldHighestAddress;
            GCCommon.seg_mapping_table = oldSegMappingTable;
            gc_heap.map_region_to_generation = oldGenerationMap;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
#if DEBUG
            gc_heap.g_promoted = savedPromoted;
#endif
        }
    }

    [Fact]
    public void MarkPhaseDrainMarkQueueTraversesTransitiveCycleAndSkipsAlreadyMarkedReferences()
    {
        using MarkPhaseStateScope _ = new();
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        const int RootReferenceCount = 17;
        const nuint RegionShift = 4;
        nuint pointerSize = (nuint)sizeof(byte*);
        nuint rootObjectSize = (nuint)(RootReferenceCount + 1) * pointerSize;
        nuint nodeObjectSize = 2 * pointerSize;
        nuint expectedPromoted = rootObjectSize + (2 * nodeObjectSize);
        byte* objectStorage = stackalloc byte[
            checked((int)(rootObjectSize + (2 * nodeObjectSize) + pointerSize))];
        byte* root = AlignUp(objectStorage, pointerSize);
        byte* nodeA = root + (nint)rootObjectSize;
        byte* nodeB = nodeA + (nint)nodeObjectSize;
        byte** rootReferences = (byte**)(root + sizeof(nuint));
        byte** nodeAReferences = (byte**)(nodeA + sizeof(nuint));
        byte** nodeBReferences = (byte**)(nodeB + sizeof(nuint));
        byte* rootDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        byte* nodeDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        MethodTable* rootMethodTable = InitializeSingleSeriesMethodTable(
            rootDescriptorStorage,
            rootObjectSize,
            (nuint)sizeof(nuint),
            pointerCount: RootReferenceCount,
            hasPointers: 1);
        MethodTable* nodeMethodTable = InitializeSingleSeriesMethodTable(
            nodeDescriptorStorage,
            nodeObjectSize,
            (nuint)sizeof(nuint),
            pointerCount: 1,
            hasPointers: 1);
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowestAddress = GCCommon.g_gc_lowest_address;
        byte* oldHighestAddress = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
        region_info* oldGenerationMap = gc_heap.map_region_to_generation;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
#if DEBUG
        nuint savedPromoted = gc_heap.g_promoted;
#endif

        try
        {
            ((CObjectHeader*)root)->RawSetMethodTable(rootMethodTable);
            ((CObjectHeader*)nodeA)->RawSetMethodTable(nodeMethodTable);
            ((CObjectHeader*)nodeB)->RawSetMethodTable(nodeMethodTable);

            for (int index = 0; index < RootReferenceCount; index++)
            {
                rootReferences[index] = nodeA;
            }

            nodeAReferences[0] = nodeB;
            nodeBReferences[0] = nodeA;

            pHeap->heap_number = 0;
            gc_heap.min_segment_size_shr = RegionShift;
            GCCommon.g_gc_lowest_address = root;
            GCCommon.g_gc_highest_address = nodeB + (nint)nodeObjectSize;

            nuint minRegionIndex = (nuint)GCCommon.g_gc_lowest_address >> (int)RegionShift;
            nuint maxRegionIndex = ((nuint)GCCommon.g_gc_highest_address - 1) >> (int)RegionShift;
            nuint regionCount = maxRegionIndex - minRegionIndex + 1;
            region_info* generationMap = stackalloc region_info[(int)regionCount];
            seg_mapping* segmentMap = stackalloc seg_mapping[(int)regionCount];
            gc_heap.map_region_to_generation = generationMap;
            gc_heap.map_region_to_generation_skewed = generationMap - (nint)minRegionIndex;
            GCCommon.seg_mapping_table = segmentMap - (nint)minRegionIndex;
            InitializeRegionGenerationMaps(
                generationMap,
                segmentMap,
                regionCount,
                (int)gc_generation_num.soh_gen1);

            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen1;
            byte** markList = stackalloc byte*[8];
            nuint* survived = stackalloc nuint[(int)regionCount];
            nuint* oldCardSurvived = stackalloc nuint[(int)regionCount];
            Assert.True(gc_heap.setup_mark_state_for_collection(markList, 8, survived, oldCardSurvived, regionCount));

            mark* markStack = stackalloc mark[4];
            gc_heap.mark_stack_array = markStack;
            gc_heap.mark_stack_array_length = 4;

#if DEBUG
            gc_heap.init_promoted_bytes();
#endif

            byte* rootReference = root;
            gc_heap.mark_object_simple(pHeap, &rootReference);
            gc_heap.drain_mark_queue(pHeap);

            Assert.True(((CObjectHeader*)root)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)nodeA)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)nodeB)->IsMarked() != 0);
            Assert.Equal(3, (int)(gc_heap.mark_list_index - gc_heap.mark_list));
            Assert.Equal((nuint)root, (nuint)markList[0]);
            Assert.Equal((nuint)nodeA, (nuint)markList[1]);
            Assert.Equal((nuint)nodeB, (nuint)markList[2]);
            Assert.Equal(expectedPromoted, SumRegionCounters(survived, regionCount));
            for (nuint offset = 0; offset < regionCount; offset++)
            {
                Assert.Equal((nuint)0, oldCardSurvived[(nint)offset]);
            }

#if DEBUG
            Assert.Equal(expectedPromoted, gc_heap.promoted_bytes(pHeap->heap_number));
#endif
            Assert.Equal((nuint)0, (nuint)gc_heap.mark_queue.get_next_marked());
            gc_heap.mark_queue.verify_empty();
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowestAddress;
            GCCommon.g_gc_highest_address = oldHighestAddress;
            GCCommon.seg_mapping_table = oldSegMappingTable;
            gc_heap.map_region_to_generation = oldGenerationMap;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
#if DEBUG
            gc_heap.g_promoted = savedPromoted;
#endif
        }
    }

    [Fact]
    public void MarkPhaseMarkObjectSimpleUsesBoundaryInFullGcWithCapacityOneMarkList()
    {
        using MarkPhaseStateScope _ = new();
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        const int ChildCount = 17;
        const nuint RegionShift = 4;
        nuint pointerSize = (nuint)sizeof(byte*);
        nuint childObjectSize = 2 * pointerSize;
        nuint rootObjectSize = (nuint)(ChildCount + 1) * pointerSize;
        nuint expectedPromoted = rootObjectSize + ((nuint)ChildCount * childObjectSize);
        byte* objectStorage = stackalloc byte[
            checked((int)(rootObjectSize + ((nuint)ChildCount * childObjectSize) + pointerSize))];
        byte* root = AlignUp(objectStorage, pointerSize);
        byte* childStart = root + (nint)rootObjectSize;
        byte** rootReferences = (byte**)(root + sizeof(nuint));
        byte** children = stackalloc byte*[ChildCount];
        byte* rootDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        byte* childDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        MethodTable* rootMethodTable = InitializeSingleSeriesMethodTable(
            rootDescriptorStorage,
            rootObjectSize,
            (nuint)sizeof(nuint),
            pointerCount: ChildCount,
            hasPointers: 1);
        MethodTable* childMethodTable = InitializeSingleSeriesMethodTable(
            childDescriptorStorage,
            childObjectSize,
            (nuint)sizeof(nuint),
            pointerCount: 0,
            hasPointers: 0);
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowestAddress = GCCommon.g_gc_lowest_address;
        byte* oldHighestAddress = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
        region_info* oldGenerationMap = gc_heap.map_region_to_generation;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
#if DEBUG
        nuint savedPromoted = gc_heap.g_promoted;
#endif

        try
        {
            ((CObjectHeader*)root)->RawSetMethodTable(rootMethodTable);
            for (int index = 0; index < ChildCount; index++)
            {
                byte* child = childStart + (nint)((nuint)index * childObjectSize);
                children[index] = child;
                ((CObjectHeader*)child)->RawSetMethodTable(childMethodTable);
                rootReferences[index] = child;
            }

            pHeap->heap_number = 0;
            gc_heap.min_segment_size_shr = RegionShift;
            GCCommon.g_gc_lowest_address = root;
            GCCommon.g_gc_highest_address = childStart + (nint)((nuint)ChildCount * childObjectSize);

            nuint minRegionIndex = (nuint)GCCommon.g_gc_lowest_address >> (int)RegionShift;
            nuint maxRegionIndex = ((nuint)GCCommon.g_gc_highest_address - 1) >> (int)RegionShift;
            nuint regionCount = maxRegionIndex - minRegionIndex + 1;
            region_info* generationMap = stackalloc region_info[(int)regionCount];
            seg_mapping* segmentMap = stackalloc seg_mapping[(int)regionCount];
            gc_heap.map_region_to_generation = generationMap;
            gc_heap.map_region_to_generation_skewed = generationMap - (nint)minRegionIndex;
            GCCommon.seg_mapping_table = segmentMap - (nint)minRegionIndex;
            InitializeRegionGenerationMaps(
                generationMap,
                segmentMap,
                regionCount,
                (int)gc_generation_num.soh_gen2);

            gc_heap.settings.condemned_generation = GCInterfaceOffsets.max_generation;
            byte** markList = stackalloc byte*[1];
            markList[0] = (byte*)0xDEAD;
            nuint* survived = stackalloc nuint[(int)regionCount];
            nuint* oldCardSurvived = stackalloc nuint[(int)regionCount];
            Assert.True(gc_heap.setup_mark_state_for_collection(markList, 1, survived, oldCardSurvived, regionCount));

#if DEBUG
            gc_heap.init_promoted_bytes();
#endif

            byte* rootReference = root;
            gc_heap.mark_object_simple(pHeap, &rootReference);
            gc_heap.drain_mark_queue(pHeap);

            Assert.Equal((nuint)root, (nuint)markList[0]);
            Assert.Equal((nuint)(markList + 1), (nuint)gc_heap.mark_list_index);
            Assert.Equal((nuint)markList, (nuint)gc_heap.mark_list_end);
            Assert.Equal((nuint)root, (nuint)gc_heap.slow);
            Assert.Equal((nuint)children[ChildCount - 1], (nuint)gc_heap.shigh);
            Assert.True(((CObjectHeader*)root)->IsMarked() != 0);
            for (int index = 0; index < ChildCount; index++)
            {
                Assert.True(((CObjectHeader*)children[index])->IsMarked() != 0);
            }

            Assert.Equal(expectedPromoted, SumRegionCounters(survived, regionCount));
            for (nuint offset = 0; offset < regionCount; offset++)
            {
                Assert.Equal((nuint)0, oldCardSurvived[(nint)offset]);
            }

#if DEBUG
            Assert.Equal(expectedPromoted, gc_heap.promoted_bytes(pHeap->heap_number));
#endif
            Assert.Equal((nuint)0, (nuint)gc_heap.mark_queue.get_next_marked());
            gc_heap.mark_queue.verify_empty();
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowestAddress;
            GCCommon.g_gc_highest_address = oldHighestAddress;
            GCCommon.seg_mapping_table = oldSegMappingTable;
            gc_heap.map_region_to_generation = oldGenerationMap;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
#if DEBUG
            gc_heap.g_promoted = savedPromoted;
#endif
        }
    }

    [Fact]
    public void MarkPhaseDrainMarkQueuePropagatesOverflowExtremaFromMarkObjectSimple1()
    {
        using MarkPhaseStateScope _ = new();
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        const int ReferencesPerChild = 17;
        const int TotalReferences = 2 * ReferencesPerChild;
        const nuint RegionShift = 4;
        nuint pointerSize = (nuint)sizeof(byte*);
        int childPointerCount = (int)((nuint)sizeof(mark) / pointerSize) + 4;
        nuint childObjectSize = ((nuint)childPointerCount + 1) * pointerSize;
        nuint rootObjectSize = (nuint)(TotalReferences + 1) * pointerSize;
        nuint expectedPromoted = rootObjectSize + (2 * childObjectSize);
        byte* objectStorage = stackalloc byte[
            checked((int)(rootObjectSize + (2 * childObjectSize) + pointerSize))];
        byte* root = AlignUp(objectStorage, pointerSize);
        byte* childLow = root + (nint)rootObjectSize;
        byte* childHigh = childLow + (nint)childObjectSize;
        byte** rootReferences = (byte**)(root + sizeof(nuint));
        byte** childLowReferences = (byte**)(childLow + sizeof(nuint));
        byte** childHighReferences = (byte**)(childHigh + sizeof(nuint));
        byte* rootDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        byte* childDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        MethodTable* rootMethodTable = InitializeSingleSeriesMethodTable(
            rootDescriptorStorage,
            rootObjectSize,
            (nuint)sizeof(nuint),
            pointerCount: TotalReferences,
            hasPointers: 1);
        MethodTable* childMethodTable = InitializeSingleSeriesMethodTable(
            childDescriptorStorage,
            childObjectSize,
            (nuint)sizeof(nuint),
            pointerCount: (nuint)childPointerCount,
            hasPointers: 1);
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowestAddress = GCCommon.g_gc_lowest_address;
        byte* oldHighestAddress = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
        region_info* oldGenerationMap = gc_heap.map_region_to_generation;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
#if DEBUG
        nuint savedPromoted = gc_heap.g_promoted;
#endif

        try
        {
            ((CObjectHeader*)root)->RawSetMethodTable(rootMethodTable);
            ((CObjectHeader*)childLow)->RawSetMethodTable(childMethodTable);
            ((CObjectHeader*)childHigh)->RawSetMethodTable(childMethodTable);

            for (int index = 0; index < ReferencesPerChild; index++)
            {
                rootReferences[index] = childHigh;
            }

            for (int index = ReferencesPerChild; index < TotalReferences; index++)
            {
                rootReferences[index] = childLow;
            }

            for (int index = 0; index < childPointerCount; index++)
            {
                childLowReferences[index] = null;
                childHighReferences[index] = null;
            }

            pHeap->heap_number = 0;
            gc_heap.min_segment_size_shr = RegionShift;
            GCCommon.g_gc_lowest_address = root;
            GCCommon.g_gc_highest_address = childHigh + (nint)childObjectSize;

            nuint minRegionIndex = (nuint)GCCommon.g_gc_lowest_address >> (int)RegionShift;
            nuint maxRegionIndex = ((nuint)GCCommon.g_gc_highest_address - 1) >> (int)RegionShift;
            nuint regionCount = maxRegionIndex - minRegionIndex + 1;
            region_info* generationMap = stackalloc region_info[(int)regionCount];
            seg_mapping* segmentMap = stackalloc seg_mapping[(int)regionCount];
            gc_heap.map_region_to_generation = generationMap;
            gc_heap.map_region_to_generation_skewed = generationMap - (nint)minRegionIndex;
            GCCommon.seg_mapping_table = segmentMap - (nint)minRegionIndex;
            InitializeRegionGenerationMaps(
                generationMap,
                segmentMap,
                regionCount,
                (int)gc_generation_num.soh_gen1);

            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen1;
            byte** markList = stackalloc byte*[8];
            nuint* survived = stackalloc nuint[(int)regionCount];
            nuint* oldCardSurvived = stackalloc nuint[(int)regionCount];
            Assert.True(gc_heap.setup_mark_state_for_collection(markList, 8, survived, oldCardSurvived, regionCount));

            mark* markStack = stackalloc mark[1];
            gc_heap.mark_stack_array = markStack;
            gc_heap.mark_stack_array_length = 1;

#if DEBUG
            gc_heap.init_promoted_bytes();
#endif

            byte* rootReference = root;
            gc_heap.mark_object_simple(pHeap, &rootReference);
            gc_heap.drain_mark_queue(pHeap);

            Assert.True(((CObjectHeader*)root)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)childLow)->IsMarked() != 0);
            Assert.True(((CObjectHeader*)childHigh)->IsMarked() != 0);
            Assert.Equal((nuint)childLow, (nuint)gc_heap.min_overflow_address);
            Assert.Equal((nuint)childHigh, (nuint)gc_heap.max_overflow_address);
            Assert.Equal(3, (int)(gc_heap.mark_list_index - gc_heap.mark_list));
            Assert.Equal(expectedPromoted, SumRegionCounters(survived, regionCount));
            for (nuint offset = 0; offset < regionCount; offset++)
            {
                Assert.Equal((nuint)0, oldCardSurvived[(nint)offset]);
            }

#if DEBUG
            Assert.Equal(expectedPromoted, gc_heap.promoted_bytes(pHeap->heap_number));
#endif
            Assert.Equal((nuint)0, (nuint)gc_heap.mark_queue.get_next_marked());
            gc_heap.mark_queue.verify_empty();
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowestAddress;
            GCCommon.g_gc_highest_address = oldHighestAddress;
            GCCommon.seg_mapping_table = oldSegMappingTable;
            gc_heap.map_region_to_generation = oldGenerationMap;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
#if DEBUG
            gc_heap.g_promoted = savedPromoted;
#endif
        }
    }

    [Fact]
    public void ComputeGcAndEphemeralRangeAndPredicatesPreserveWksRegionBounds()
    {
        using MarkPhaseStateScope _ = new();
        gc_heap heap = default;
        heap_segment* segments = stackalloc heap_segment[3];
        generation* generationTable = gc_heap.generation_table_of(&heap);
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowestAddress = GCCommon.g_gc_lowest_address;
        byte* oldHighestAddress = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
        region_info* oldGenerationMap = gc_heap.map_region_to_generation;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;

        try
        {
            byte* gen0FirstStart = (byte*)0x1000;
            byte* gen0SecondStart = (byte*)0x2000;
            byte* gen1Start = (byte*)0x3000;
            byte* heapEnd = (byte*)0x5000;
            ConfigureRegion(&segments[0], gen0FirstStart, (byte*)0x1800, &segments[1]);
            ConfigureRegion(&segments[1], gen0SecondStart, (byte*)0x2800, null);
            ConfigureRegion(&segments[2], gen1Start, (byte*)0x3800, null);
            generation.generation_start_segment(generationTable + (int)gc_generation_num.soh_gen0) = &segments[0];
            generation.generation_start_segment(generationTable + (int)gc_generation_num.soh_gen1) = &segments[2];

            const nuint RegionShift = 12;
            gc_heap.min_segment_size_shr = RegionShift;
            GCCommon.g_gc_lowest_address = gen0FirstStart;
            GCCommon.g_gc_highest_address = heapEnd;
            region_info* generationMap = stackalloc region_info[4];
            seg_mapping* segmentMap = stackalloc seg_mapping[4];
            gc_heap.map_region_to_generation = generationMap;
            gc_heap.map_region_to_generation_skewed = generationMap - 1;
            GCCommon.seg_mapping_table = segmentMap - 1;
            InitializeRegionGenerationMaps(
                generationMap,
                segmentMap,
                4,
                (int)gc_generation_num.soh_gen2);
            SetRegionGenerationForAddress(
                gen0FirstStart,
                1,
                generationMap,
                segmentMap,
                (int)gc_generation_num.soh_gen0);
            SetRegionGenerationForAddress(
                gen1Start,
                1,
                generationMap,
                segmentMap,
                (int)gc_generation_num.soh_gen1);

            gc_heap.compute_gc_and_ephemeral_range(
                &heap,
                (int)gc_generation_num.soh_gen0,
                end_of_gc_p: false);

            Assert.Equal((nuint)gen0FirstStart, (nuint)gc_heap.ephemeral_low);
            Assert.Equal((nuint)0x3800, (nuint)gc_heap.ephemeral_high);
            Assert.Equal((nuint)gen0FirstStart, (nuint)gc_heap.gc_low);
            Assert.Equal((nuint)0x2800, (nuint)gc_heap.gc_high);
            Assert.True(gc_heap.is_in_gc_range(gen0FirstStart));
            Assert.False(gc_heap.is_in_gc_range(gen1Start));

            gc_heap.compute_gc_and_ephemeral_range(
                &heap,
                (int)gc_generation_num.soh_gen2,
                end_of_gc_p: false);

            Assert.Equal((nuint)GCCommon.g_gc_lowest_address, (nuint)gc_heap.gc_low);
            Assert.Equal((nuint)GCCommon.g_gc_highest_address, (nuint)gc_heap.gc_high);
            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen0;
            Assert.True(gc_heap.is_in_condemned_gc(gen0FirstStart));
            Assert.False(gc_heap.is_in_condemned_gc(gen1Start));

            gc_heap.compute_gc_and_ephemeral_range(
                &heap,
                (int)gc_generation_num.soh_gen0,
                end_of_gc_p: true);

            Assert.Equal((nuint)GCCommon.g_gc_lowest_address, (nuint)gc_heap.ephemeral_low);
            Assert.Equal((nuint)GCCommon.g_gc_highest_address, (nuint)gc_heap.ephemeral_high);
            Assert.Equal((nuint)GCCommon.g_gc_lowest_address, (nuint)gc_heap.gc_low);
            Assert.Equal((nuint)GCCommon.g_gc_highest_address, (nuint)gc_heap.gc_high);
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowestAddress;
            GCCommon.g_gc_highest_address = oldHighestAddress;
            GCCommon.seg_mapping_table = oldSegMappingTable;
            gc_heap.map_region_to_generation = oldGenerationMap;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
        }
    }

    [Fact]
    public void MarkObjectAndMarkThroughObjectHonorActiveCollectionRange()
    {
        using MarkPhaseStateScope _ = new();
        gc_heap heap = default;
        gc_heap* pHeap = &heap;
        const int RegionShift = 6;
        const nuint RegionSize = (nuint)1 << RegionShift;
        nuint pointerSize = (nuint)sizeof(byte*);
        nuint objectSize = 2 * pointerSize;
        byte* objectStorage = stackalloc byte[checked((int)(4 * RegionSize))];
        byte* root = AlignUp(objectStorage, RegionSize);
        byte* child = root + (nint)RegionSize;
        byte* nonCondemned = child + (nint)RegionSize;
        byte** rootReference = (byte**)(root + (nint)pointerSize);
        byte* rootDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        byte* leafDescriptorStorage = stackalloc byte[sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(MethodTable)];
        MethodTable* rootMethodTable = InitializeSingleSeriesMethodTable(
            rootDescriptorStorage,
            objectSize,
            pointerSize,
            pointerCount: 1,
            hasPointers: 1);
        MethodTable* leafMethodTable = InitializeSingleSeriesMethodTable(
            leafDescriptorStorage,
            objectSize,
            pointerSize,
            pointerCount: 0,
            hasPointers: 0);
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowestAddress = GCCommon.g_gc_lowest_address;
        byte* oldHighestAddress = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
        region_info* oldGenerationMap = gc_heap.map_region_to_generation;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;

        try
        {
            ((CObjectHeader*)root)->RawSetMethodTable(rootMethodTable);
            ((CObjectHeader*)child)->RawSetMethodTable(leafMethodTable);
            ((CObjectHeader*)nonCondemned)->RawSetMethodTable(leafMethodTable);
            *rootReference = child;

            gc_heap.min_segment_size_shr = RegionShift;
            GCCommon.g_gc_lowest_address = root;
            GCCommon.g_gc_highest_address = nonCondemned + (nint)objectSize;
            nuint minRegionIndex = (nuint)root >> RegionShift;
            region_info* generationMap = stackalloc region_info[3];
            seg_mapping* segmentMap = stackalloc seg_mapping[3];
            gc_heap.map_region_to_generation = generationMap;
            gc_heap.map_region_to_generation_skewed = generationMap - (nint)minRegionIndex;
            GCCommon.seg_mapping_table = segmentMap - (nint)minRegionIndex;
            InitializeRegionGenerationMaps(
                generationMap,
                segmentMap,
                3,
                (int)gc_generation_num.soh_gen2);
            SetRegionGenerationForAddress(
                child,
                minRegionIndex,
                generationMap,
                segmentMap,
                (int)gc_generation_num.soh_gen0);

            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen0;
            gc_heap.gc_low = child;
            gc_heap.gc_high = nonCondemned + (nint)objectSize;
            byte** markList = stackalloc byte*[2];
            nuint* survived = stackalloc nuint[3];
            nuint* oldCardSurvived = stackalloc nuint[3];
            Assert.True(gc_heap.setup_mark_state_for_collection(markList, 2, survived, oldCardSurvived, 3));

            gc_heap.mark_object(pHeap, root);
            gc_heap.mark_object(pHeap, nonCondemned);
            gc_heap.drain_mark_queue(pHeap);

            Assert.Equal(0, ((CObjectHeader*)root)->IsMarked());
            Assert.Equal(0, ((CObjectHeader*)nonCondemned)->IsMarked());
            Assert.Equal(0, ((CObjectHeader*)child)->IsMarked());

            gc_heap.mark_object(pHeap, child);
            gc_heap.drain_mark_queue(pHeap);

            Assert.True(((CObjectHeader*)child)->IsMarked() != 0);
            ((CObjectHeader*)child)->ClearMarked();

            gc_heap.mark_through_object(pHeap, nonCondemned, mark_class_object_p: 1);
            gc_heap.drain_mark_queue(pHeap);
            Assert.Equal(0, ((CObjectHeader*)nonCondemned)->IsMarked());

            gc_heap.mark_through_object(pHeap, root, mark_class_object_p: 1);
            gc_heap.drain_mark_queue(pHeap);

            Assert.Equal(0, ((CObjectHeader*)root)->IsMarked());
            Assert.True(((CObjectHeader*)child)->IsMarked() != 0);
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowestAddress;
            GCCommon.g_gc_highest_address = oldHighestAddress;
            GCCommon.seg_mapping_table = oldSegMappingTable;
            gc_heap.map_region_to_generation = oldGenerationMap;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
        }
    }

    private static void ConfigureRegion(heap_segment* region, byte* start, byte* reserved, heap_segment* next)
    {
        heap_segment.heap_segment_mem(region) = start + sizeof(aligned_plug_and_gap);
        heap_segment.heap_segment_reserved(region) = reserved;
        heap_segment.heap_segment_next(region) = next;
    }

    private static byte* AlignUp(byte* value, nuint alignment)
    {
        return (byte*)(((nuint)value + (alignment - 1)) & ~(alignment - 1));
    }

    private static MethodTable* InitializeSingleSeriesMethodTable(
        byte* descriptorStorage,
        nuint objectSize,
        nuint startOffset,
        nuint pointerCount,
        int hasPointers)
    {
        int descriptorSize = sizeof(nuint) + sizeof(CGCDescSeries);
        MethodTable* methodTable = (MethodTable*)(descriptorStorage + descriptorSize);
        CGCDescSeries* series = (CGCDescSeries*)descriptorStorage;

        *methodTable = default;
        methodTable->m_uFlags = hasPointers != 0 ? MethodTable.HasPointersFlag : 0;
        methodTable->m_uBaseSize = (uint)objectSize;
        *((nuint*)methodTable - 1) = 1;

        series->startoffset = startOffset;
        series->seriessize = unchecked(
            (nuint)((nint)(pointerCount * (nuint)sizeof(byte*)) - (nint)objectSize));
        return methodTable;
    }

    private static MethodTable* InitializeRepeatingSeriesMethodTable(
        byte* descriptorStorage,
        nuint baseSize,
        nuint componentSize,
        nuint startOffset,
        nuint pointersPerComponent,
        nuint skipBytes)
    {
        int descriptorSize = sizeof(nuint) + sizeof(CGCDescSeries);
        MethodTable* methodTable = (MethodTable*)(descriptorStorage + descriptorSize);
        CGCDescSeries* series = (CGCDescSeries*)descriptorStorage;

        *methodTable = default;
        methodTable->m_uFlags = MethodTable.HasPointersFlag | MethodTable.HasComponentSizeFlag;
        methodTable->m_uBaseSize = (uint)baseSize;
        methodTable->m_usComponentSize = checked((ushort)componentSize);
        *((nint*)methodTable - 1) = -1;

        series->startoffset = startOffset;
#if TARGET_64BIT
        series->val_serie.set_val_serie_item((uint)pointersPerComponent, (uint)skipBytes);
#else
        series->val_serie.set_val_serie_item((ushort)pointersPerComponent, (ushort)skipBytes);
#endif
        return methodTable;
    }

    private static int MarkStackSlotsPerEntry()
    {
        return checked((int)((nuint)sizeof(mark) / (nuint)sizeof(byte*)));
    }

    private static int MarkStackSlotCapacity(nuint markStackLength)
    {
        return checked((int)(markStackLength * (nuint)MarkStackSlotsPerEntry()));
    }

    private static void InitializeRegionGenerationMaps(
        region_info* generationMap,
        seg_mapping* segmentMap,
        nuint regionCount,
        int generation)
    {
        for (nuint offset = 0; offset < regionCount; offset++)
        {
            generationMap[(nint)offset] = (region_info)generation;
            segmentMap[(nint)offset] = default;
            segmentMap[(nint)offset].region_info.gen_num = (byte)generation;
        }
    }

    private static void SetRegionGenerationForAddress(
        byte* address,
        nuint minRegionIndex,
        region_info* generationMap,
        seg_mapping* segmentMap,
        int generation)
    {
        nuint regionIndex = (nuint)address >> (int)gc_heap.min_segment_size_shr;
        nint offset = (nint)(regionIndex - minRegionIndex);
        generationMap[offset] = (region_info)generation;
        segmentMap[offset].region_info.gen_num = (byte)generation;
    }

    private static nuint SumRegionCounters(nuint* counters, nuint count)
    {
        nuint total = 0;
        for (nuint index = 0; index < count; index++)
        {
            total += counters[(nint)index];
        }

        return total;
    }

    private static byte* ReadMarkQueueSlot(int slotIndex)
    {
        const int MarkQueueSlotCount = 16;
        Assert.InRange(slotIndex, 0, MarkQueueSlotCount - 1);

        fixed (mark_queue_t* queue = &gc_heap.mark_queue)
        {
            return (byte*)((nuint*)queue)[slotIndex];
        }
    }

    private static nuint ReadMarkQueueTailSlotIndex()
    {
        const int MarkQueueSlotCount = 16;
        fixed (mark_queue_t* queue = &gc_heap.mark_queue)
        {
            return ((nuint*)queue)[MarkQueueSlotCount];
        }
    }
#endif
#endif

    private static FieldInfo GetGCConfigField(string name)
    {
        FieldInfo field = typeof(GCConfig).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(field is not null, $"GCConfig is missing {name}.");
        return field;
    }

#if USE_REGIONS && !MULTIPLE_HEAPS
    private static void ResetOwnedMarkListState()
    {
        gc_heap.g_mark_list = null;
        gc_heap.g_mark_list_copy = null;
        gc_heap.mark_list_size = 0;
        gc_heap.g_mark_list_total_size = 0;
        gc_heap.mark_list_overflow = false;
        gc_heap.g_mark_list_piece = null;
        gc_heap.g_mark_list_piece_size = 0;
        gc_heap.g_mark_list_piece_total_size = 0;
        gc_heap.region_count = 0;
    }
#endif

    private static gap_reloc_pair Pair(nuint gap, nuint reloc, short left, short right) =>
        new() { gap = gap, reloc = reloc, m_pair = new pair { left = left, right = right } };

    private static void AssertPair(gap_reloc_pair actual, nuint gap, nuint reloc, short left, short right)
    {
        Assert.Equal(gap, actual.gap);
        Assert.Equal(reloc, actual.reloc);
        Assert.Equal(left, actual.m_pair.left);
        Assert.Equal(right, actual.m_pair.right);
    }

    [Fact]
    public void CardTableInfoDefaultStateIsZeroed()
    {
        card_table_info info = default;
        card_table_info* p = &info;

        Assert.Equal(0u, p->recount);
        Assert.Equal((nuint)0, p->size);
        Assert.Equal((nuint)0, (nuint)p->next_card_table);
        Assert.Equal((nuint)0, (nuint)p->lowest_address);
        Assert.Equal((nuint)0, (nuint)p->highest_address);
        Assert.Equal((nuint)0, (nuint)p->brick_table);
        Assert.Equal((nuint)0, (nuint)p->card_bundle_table);
#if BACKGROUND_GC
        Assert.Equal((nuint)0, (nuint)p->mark_array);
#endif
    }

    [Fact]
    public void CardTableInfoFieldsFollowNativeOrderAndDacPrefix()
    {
        card_table_info info = default;
        card_table_info* p = &info;
        dac_card_table_info dac = default;
        nuint previous = 0;

        Assert.Equal((nuint)0, OffsetOf(&p->recount, p));
        Assert.Equal(OffsetOf(&dac.recount, &dac), OffsetOf(&p->recount, p));
        previous = Ascending(OffsetOf(&p->size, p), previous);
        Assert.Equal(OffsetOf(&dac.size, &dac), OffsetOf(&p->size, p));
        previous = Ascending(OffsetOf(&p->next_card_table, p), previous);
        Assert.Equal(OffsetOf(&dac.next_card_table, &dac), OffsetOf(&p->next_card_table, p));
        previous = Ascending(OffsetOf(&p->lowest_address, p), previous);
        previous = Ascending(OffsetOf(&p->highest_address, p), previous);
        previous = Ascending(OffsetOf(&p->brick_table, p), previous);
        previous = Ascending(OffsetOf(&p->card_bundle_table, p), previous);
#if BACKGROUND_GC
        _ = Ascending(OffsetOf(&p->mark_array, p), previous);
#endif
    }

    [Fact]
    public void CardTableInfoPureHelpersPreserveNativeArithmetic()
    {
        Assert.Equal((nuint)0, card_table_info.gib(0));
        Assert.Equal((nuint)0, card_table_info.gib(((nuint)1 << 30) - 1));
        Assert.Equal((nuint)1, card_table_info.gib((nuint)1 << 30));
        Assert.Equal((nuint)3, card_table_info.gib(((nuint)3 << 30) + ((nuint)1 << 29)));

        nuint brick = card_table_info.brick_size;
        Assert.Equal(brick, (nuint)card_table_info.align_on_brick((byte*)1));
        Assert.Equal(brick, (nuint)card_table_info.align_on_brick((byte*)(brick - 1)));
        Assert.Equal(brick, (nuint)card_table_info.align_on_brick((byte*)brick));
        Assert.Equal((nuint)0, (nuint)card_table_info.align_on_brick((byte*)(nuint.MaxValue - (brick - 2))));
    }

    [Theory]
    [InlineData(0U, 0U, 0U)]
    [InlineData(31U, 0U, 31U)]
    [InlineData(32U, 1U, 0U)]
    [InlineData(33U, 1U, 1U)]
    [InlineData(63U, 1U, 31U)]
    [InlineData(64U, 2U, 0U)]
    [InlineData(uint.MaxValue, 134217727U, 31U)]
    public void CardTableInfoCardWordAndBitPreserveNativeArithmetic(uint card, uint word, uint bit)
    {
        Assert.Equal((nuint)word, card_table_info.card_word((nuint)card));
        Assert.Equal(bit, card_table_info.card_bit((nuint)card));
    }

    [Theory]
    [InlineData(0UL, 0UL)]
#if TARGET_64BIT
    [InlineData(0xFFUL, 0UL)]
    [InlineData(0x100UL, 1UL)]
    [InlineData(0x101UL, 1UL)]
    [InlineData(0x12345678UL, 0x123456UL)]
#else
    [InlineData(0x7FUL, 0UL)]
    [InlineData(0x80UL, 1UL)]
    [InlineData(0x81UL, 1UL)]
    [InlineData(0x12345678UL, 0x2468ACUL)]
#endif
    public void CardTableInfoGcardOfPreservesPointerToNuintDivision(ulong objectAddress, ulong card)
    {
        Assert.Equal((nuint)card, card_table_info.gcard_of((byte*)(nuint)objectAddress));
    }

    [Theory]
    [InlineData(0U, 0U, 0U)]
    [InlineData(31U, 0U, 31U)]
    [InlineData(32U, 1U, 0U)]
    [InlineData(33U, 1U, 1U)]
    [InlineData(63U, 1U, 31U)]
    [InlineData(64U, 2U, 0U)]
    [InlineData(uint.MaxValue, 134217727U, 31U)]
    public void CardTableInfoCardBundleWordAndBitPreserveNativeArithmetic(uint cardBundle, uint word, uint bit)
    {
        Assert.Equal((nuint)word, card_table_info.card_bundle_word((nuint)cardBundle));
        Assert.Equal(bit, card_table_info.card_bundle_bit((nuint)cardBundle));
    }

    [Theory]
    [InlineData(0U, 0U, 0U)]
    [InlineData(1U, 32U, 0U)]
    [InlineData(31U, 32U, 0U)]
    [InlineData(32U, 32U, 1U)]
    [InlineData(33U, 64U, 1U)]
    [InlineData(63U, 64U, 1U)]
    [InlineData(64U, 64U, 2U)]
    public void CardTableInfoCardBundleConversionsPreserveNativeArithmetic(
        uint cardWord,
        uint alignedCardWord,
        uint cardBundle)
    {
        Assert.Equal((nuint)alignedCardWord, card_table_info.align_cardw_on_bundle(cardWord));
        Assert.Equal((nuint)cardBundle, card_table_info.cardw_card_bundle(cardWord));
        Assert.Equal((nuint)(cardBundle * 32), card_table_info.card_bundle_cardw(cardBundle));
    }

    [Fact]
    public void CardTableInfoTranslatedBundleTablePreservesNativeSkew()
    {
        const nuint BundleTable = 0x100000;
        nuint heapBytesForBundleWord =
            card_table_info.card_size
            * card_table_info.card_word_width
            * card_table_info.card_bundle_size
            * card_table_info.card_bundle_word_width;

        Assert.Equal(
            BundleTable,
            (nuint)card_table_info.translate_card_bundle_table((uint*)BundleTable, (byte*)0));
        Assert.Equal(
            BundleTable - sizeof(uint),
            (nuint)card_table_info.translate_card_bundle_table(
                (uint*)BundleTable,
                (byte*)heapBytesForBundleWord));
        Assert.Equal(
            BundleTable - (3 * sizeof(uint)),
            (nuint)card_table_info.translate_card_bundle_table(
                (uint*)BundleTable,
                (byte*)((3 * heapBytesForBundleWord) + (heapBytesForBundleWord - 1))));
    }

    [Theory]
    [InlineData(0x1000UL, 0x1000UL, 0UL)]
    [InlineData(0x1000UL, 0x2000UL, 2UL)]
    [InlineData(0x1000UL, 0x5000UL, 8UL)]
    public void CardTableInfoBrickTableSizePreservesNativeArithmetic(ulong from, ulong end, ulong size)
    {
#if TARGET_64BIT
        Assert.Equal((nuint)size, card_table_info.size_brick_of((byte*)from, (byte*)end));
#else
        Assert.Equal((nuint)(size * 2), card_table_info.size_brick_of((byte*)from, (byte*)end));
#endif
    }

    [Theory]
#if TARGET_64BIT
    [InlineData(0x1000UL, 0x1100UL, 1UL, 4UL)]
    [InlineData(0x1000UL, 0x2000UL, 1UL, 4UL)]
    [InlineData(0x1000UL, 0x2001UL, 2UL, 8UL)]
#else
    [InlineData(0x1000UL, 0x1080UL, 1UL, 4UL)]
    [InlineData(0x1000UL, 0x2000UL, 1UL, 4UL)]
    [InlineData(0x1000UL, 0x2001UL, 2UL, 8UL)]
#endif
    public void CardTableInfoCardTableSizeCoversHalfOpenRange(
        ulong from,
        ulong end,
        ulong count,
        ulong size)
    {
        Assert.Equal((nuint)count, card_table_info.count_card_of((byte*)from, (byte*)end));
        Assert.Equal((nuint)size, card_table_info.size_card_of((byte*)from, (byte*)end));
    }

    [Fact]
    public void CardTableInfoMetadataAccessorsAliasPrecedingRecord()
    {
        card_table_info info = default;
        uint* cardTable = (uint*)((byte*)&info + sizeof(card_table_info));

        card_table_info.card_table_refcount(cardTable) = 7;
        card_table_info.card_table_size(cardTable) = 0x1234;
        card_table_info.card_table_next(cardTable) = (uint*)0x2000;
        card_table_info.card_table_lowest_address(cardTable) = (byte*)0x3000;
        card_table_info.card_table_highest_address(cardTable) = (byte*)0x4000;
        card_table_info.card_table_brick_table(cardTable) = (short*)0x5000;
        card_table_info.card_table_card_bundle_table(cardTable) = (uint*)0x6000;
#if BACKGROUND_GC
        card_table_info.card_table_mark_array(cardTable) = (uint*)0x7000;
#endif

        Assert.Equal(7u, info.recount);
        Assert.Equal((nuint)0x1234, info.size);
        Assert.Equal((nuint)0x2000, (nuint)info.next_card_table);
        Assert.Equal((nuint)0x3000, (nuint)info.lowest_address);
        Assert.Equal((nuint)0x4000, (nuint)info.highest_address);
        Assert.Equal((nuint)0x5000, (nuint)info.brick_table);
        Assert.Equal((nuint)0x6000, (nuint)info.card_bundle_table);
#if BACKGROUND_GC
        Assert.Equal((nuint)0x7000, (nuint)info.mark_array);
#endif
    }

    [Fact]
    public void CardTableInfoTranslatedCardTablePreservesNativeSkew()
    {
        card_table_info info = default;
        uint* cardTable = (uint*)((byte*)&info + sizeof(card_table_info));

        info.lowest_address = (byte*)0;
        Assert.Equal((nuint)cardTable, (nuint)card_table_info.translate_card_table(cardTable));

        info.lowest_address = (byte*)(card_table_info.card_size * card_table_info.card_word_width);
        Assert.Equal(
            (nuint)cardTable - sizeof(uint),
            (nuint)card_table_info.translate_card_table(cardTable));
    }

#if BACKGROUND_GC
    [Theory]
    [InlineData(0UL, 0UL, 0U, 0UL)]
    [InlineData(1UL, 0UL, 0U, 0UL)]
#if TARGET_64BIT
    [InlineData(15UL, 0UL, 0U, 0UL)]
    [InlineData(16UL, 1UL, 1U, 0UL)]
    [InlineData(511UL, 31UL, 31U, 0UL)]
    [InlineData(512UL, 32UL, 0U, 1UL)]
#else
    [InlineData(7UL, 0UL, 0U, 0UL)]
    [InlineData(8UL, 1UL, 1U, 0UL)]
    [InlineData(255UL, 31UL, 31U, 0UL)]
    [InlineData(256UL, 32UL, 0U, 1UL)]
#endif
    public void CardTableInfoMarkIndexesPreserveNativeArithmetic(
        ulong address,
        ulong markBit,
        uint bitInWord,
        ulong markWord)
    {
        Assert.Equal((nuint)markBit, card_table_info.mark_bit_of((byte*)address));
        Assert.Equal(bitInWord, card_table_info.mark_bit_bit((nuint)markBit));
        Assert.Equal((nuint)bitInWord, card_table_info.mark_bit_bit_of((byte*)address));
        Assert.Equal((nuint)markWord, card_table_info.mark_bit_word((nuint)markBit));
        Assert.Equal((nuint)markWord, card_table_info.mark_word_of((byte*)address));
        Assert.Equal((nuint)(markBit * card_table_info.mark_bit_pitch), (nuint)card_table_info.mark_bit_address((nuint)markBit));
    }

    [Fact]
    public void CardTableInfoMarkAlignmentAndSizingPreserveNativeArithmetic()
    {
        nuint pitch = card_table_info.mark_bit_pitch;
        nuint word = card_table_info.mark_word_size;

        Assert.Equal(pitch, (nuint)card_table_info.align_on_mark_bit((byte*)1));
        Assert.Equal((nuint)0, (nuint)card_table_info.align_lower_mark_bit((byte*)(pitch - 1)));
        Assert.Equal(word, (nuint)card_table_info.align_on_mark_word((byte*)1));
        Assert.Equal((nuint)0, (nuint)card_table_info.align_lower_mark_word((byte*)(word - 1)));
        Assert.Equal(1, card_table_info.is_aligned_on_mark_word((byte*)word));
        Assert.Equal(0, card_table_info.is_aligned_on_mark_word((byte*)(word - 1)));
        Assert.Equal((nuint)8, card_table_info.size_mark_array_of((byte*)word, (byte*)(3 * word)));
    }
#endif

    [Fact]
    public void CardTableInfoAlignmentHelpersPreserveNativeArithmetic()
    {
        nuint brick = card_table_info.brick_size;
        Assert.Equal((nuint)0, (nuint)card_table_info.align_lower_brick((byte*)0));
        Assert.Equal((nuint)0, (nuint)card_table_info.align_lower_brick((byte*)(brick - 1)));
        Assert.Equal(brick, (nuint)card_table_info.align_lower_brick((byte*)brick));
        Assert.Equal(nuint.MaxValue & ~(brick - 1), (nuint)card_table_info.align_lower_brick((byte*)nuint.MaxValue));

        nuint card = card_table_info.card_size;
        Assert.Equal((nuint)0, (nuint)card_table_info.align_on_card((byte*)0));
        Assert.Equal(card, (nuint)card_table_info.align_on_card((byte*)1));
        Assert.Equal(card, (nuint)card_table_info.align_on_card((byte*)card));
        Assert.Equal((nuint)0, (nuint)card_table_info.align_on_card((byte*)(nuint.MaxValue - (card - 2))));
        Assert.Equal((nuint)0, (nuint)card_table_info.align_lower_card((byte*)(card - 1)));
        Assert.Equal(card, (nuint)card_table_info.align_lower_card((byte*)card));

        nuint cardWord = card * card_table_info.card_word_width;
        Assert.Equal((nuint)0, (nuint)card_table_info.align_on_card_word((byte*)0));
        Assert.Equal(cardWord, (nuint)card_table_info.align_on_card_word((byte*)1));
        Assert.Equal(cardWord, (nuint)card_table_info.align_on_card_word((byte*)cardWord));
        Assert.Equal((nuint)0, (nuint)card_table_info.align_on_card_word((byte*)(nuint.MaxValue - (cardWord - 2))));
    }

    [Fact]
    public void CardTableInfoConstantsMatchNativeValues()
    {
#if TARGET_64BIT
        Assert.Equal((nuint)4096, card_table_info.brick_size);
        Assert.Equal(85u, card_table_info.MAX_ALLOWED_MEM_LOAD);
        Assert.Equal((nuint)(16 * 1024 * 1024), card_table_info.MIN_YOUNGEST_GEN_DESIRED);
#else
        Assert.Equal((nuint)2048, card_table_info.brick_size);
#endif
        Assert.Equal((nuint)4096, card_table_info.GC_PAGE_SIZE);
        Assert.Equal((nuint)32, card_table_info.card_word_width);
#if TARGET_64BIT
        Assert.Equal((nuint)256, card_table_info.card_size);
#else
        Assert.Equal((nuint)128, card_table_info.card_size);
#endif
        Assert.Equal((nuint)32, card_table_info.card_bundle_word_width);
        Assert.Equal((nuint)32, card_table_info.card_bundle_size);
#if BACKGROUND_GC
#if TARGET_64BIT
        Assert.Equal((nuint)16, card_table_info.mark_bit_pitch);
        Assert.Equal((nuint)512, card_table_info.mark_word_size);
#else
        Assert.Equal((nuint)8, card_table_info.mark_bit_pitch);
        Assert.Equal((nuint)256, card_table_info.mark_word_size);
#endif
        Assert.Equal((nuint)32, card_table_info.mark_word_width);
#endif
        Assert.Equal(40u * 1024 * 1024, card_table_info.SH_TH_CARD_BUNDLE);
        Assert.Equal(180u * 1024 * 1024, card_table_info.MH_TH_CARD_BUNDLE);
        Assert.Equal(100u, card_table_info.DECOMMIT_TIME_STEP_MILLISECONDS);
        Assert.Equal((nuint)(160 * 1024), gc_heap.DECOMMIT_SIZE_PER_MILLISECOND);
    }

    private static nuint OffsetOf(void* field, card_table_info* info) => (nuint)((byte*)field - (byte*)info);

    private static nuint OffsetOf(void* field, dac_card_table_info* info) => (nuint)((byte*)field - (byte*)info);

    [Theory]
    [InlineData(1u, 1)]
    [InlineData(2u, 0)]
    [InlineData(4u, 0)]
    [InlineData(8u, 0)]
    public void ConstructionReportsBucketCountAndDiscardPredicate(uint numBuckets, int expectedDiscard)
    {
        allocator a = new(numBuckets, fbb: 3, b: null);

        Assert.Equal(numBuckets, a.number_of_buckets());
        Assert.Equal(expectedDiscard, a.discard_if_no_fit_p());
    }

    [Fact]
    public void DefaultInitializationMatchesYoungGenerationSemantics()
    {
        allocator a = default;
        allocator.initialize(&a);

        Assert.Equal(1u, a.number_of_buckets());
        Assert.Equal(1, a.discard_if_no_fit_p());
#if TARGET_64BIT && !TARGET_WASM
        Assert.False(a.is_doubly_linked_p());
#endif
    }

    [Theory]
    [InlineData(3, 4u, 0u, 0u)]
    [InlineData(3, 4u, 7u, 0u)]
    [InlineData(3, 4u, 8u, 0u)]
    [InlineData(3, 4u, 15u, 0u)]
    [InlineData(3, 4u, 16u, 1u)]
    [InlineData(3, 4u, 31u, 1u)]
    [InlineData(3, 4u, 32u, 2u)]
    [InlineData(3, 4u, 63u, 2u)]
    [InlineData(3, 4u, 64u, 3u)]
    [InlineData(3, 4u, 127u, 3u)]
    // the last bucket fits everything, so oversized requests are clamped to num_buckets - 1
    [InlineData(3, 4u, 128u, 3u)]
    [InlineData(3, 4u, 1000000u, 3u)]
    [InlineData(0, 4u, 1u, 0u)]
    [InlineData(0, 4u, 2u, 1u)]
    [InlineData(0, 4u, 4u, 2u)]
    [InlineData(0, 4u, 8u, 3u)]
    [InlineData(0, 4u, 16u, 3u)]
    // a single-bucket allocator always maps to bucket 0
    [InlineData(3, 1u, 12345u, 0u)]
    public void FirstSuitableBucketMapsSizeToBucket(int fbb, uint numBuckets, uint size, uint expected)
    {
        allocator a = new(numBuckets, fbb, b: null);

        Assert.Equal(expected, a.first_suitable_bucket(size));
    }

    [Theory]
    [InlineData(0, 2u)]
    [InlineData(1, 4u)]
    [InlineData(2, 8u)]
    [InlineData(3, 16u)]
    [InlineData(5, 64u)]
    [InlineData(9, 1024u)]
    [InlineData(10, 2048u)]
    public void FirstBucketSizeIsTwoToTheBucketBitsPlusOne(int fbb, uint expected)
    {
        allocator a = new(1u, fbb, b: null);

        Assert.Equal((nuint)expected, a.first_bucket_size());
    }

    [Fact]
    public void BucketZeroUsesFirstBucketAndOthersUseTheExternalArray()
    {
        alloc_list* buckets = stackalloc alloc_list[3];
        for (int i = 0; i < 3; i++)
        {
            buckets[i] = default;
        }

        allocator a = new(4u, fbb: 3, buckets);

        Assert.Equal(3, *(int*)&a);
        Assert.Equal(4u, *(uint*)((byte*)&a + sizeof(int)));

        nuint firstBucketOffset = 2 * sizeof(uint);
#if TARGET_64BIT && !TARGET_WASM
        nuint firstBucketHeadOffset = firstBucketOffset + (2 * (nuint)sizeof(void*));
#else
        nuint firstBucketHeadOffset = firstBucketOffset;
#endif
        fixed (byte** field = &allocator.alloc_list_head_of(&a, 0))
        {
            Assert.Equal(firstBucketHeadOffset, OffsetOf(field, &a));
        }

        nuint bucketsOffset = firstBucketOffset + (nuint)sizeof(alloc_list);
        Assert.Equal((nuint)buckets, *(nuint*)((byte*)&a + bucketsOffset));
        Assert.Equal(-1, *(int*)((byte*)&a + bucketsOffset + (nuint)sizeof(void*)));

        // Bucket 0 is the allocator's own first_bucket, not part of the external array.
        allocator.alloc_list_head_of(&a, 0) = (byte*)0x100;
        Assert.Equal((nuint)0x100, (nuint)allocator.alloc_list_head_of(&a, 0));
        Assert.Equal((nuint)0, (nuint)alloc_list.alloc_list_head(&buckets[0]));
        Assert.Equal((nuint)0, (nuint)alloc_list.alloc_list_head(&buckets[1]));
        Assert.Equal((nuint)0, (nuint)alloc_list.alloc_list_head(&buckets[2]));

        // Buckets 1..n-1 land in buckets[bn - 1].
        allocator.alloc_list_head_of(&a, 1) = (byte*)0x200;
        allocator.alloc_list_head_of(&a, 2) = (byte*)0x300;
        allocator.alloc_list_head_of(&a, 3) = (byte*)0x400;
        Assert.Equal((nuint)0x200, (nuint)alloc_list.alloc_list_head(&buckets[0]));
        Assert.Equal((nuint)0x300, (nuint)alloc_list.alloc_list_head(&buckets[1]));
        Assert.Equal((nuint)0x400, (nuint)alloc_list.alloc_list_head(&buckets[2]));

        // Bucket 0's damage count is also internal; buckets 1..n-1 route into the array.
        allocator.alloc_list_damage_count_of(&a, 0) = 11;
        allocator.alloc_list_damage_count_of(&a, 1) = 22;
        Assert.Equal((nuint)11, allocator.alloc_list_damage_count_of(&a, 0));
        Assert.Equal((nuint)22, alloc_list.alloc_list_damage_count(&buckets[0]));
    }

    [Fact]
    public void RefAccessorsMutateTheUnderlyingList()
    {
        alloc_list* buckets = stackalloc alloc_list[1];
        buckets[0] = default;

        allocator a = new(2u, fbb: 3, buckets);

        allocator.alloc_list_head_of(&a, 1) = (byte*)0x1000;
        allocator.alloc_list_tail_of(&a, 1) = (byte*)0x2000;
        Assert.Equal((nuint)0x1000, (nuint)alloc_list.alloc_list_head(&buckets[0]));
        Assert.Equal((nuint)0x2000, (nuint)alloc_list.alloc_list_tail(&buckets[0]));

#if TARGET_64BIT && !TARGET_WASM
        allocator.added_alloc_list_head_of(&a, 1) = (byte*)0x3000;
        allocator.added_alloc_list_tail_of(&a, 1) = (byte*)0x4000;
        Assert.Equal((nuint)0x3000, (nuint)alloc_list.added_alloc_list_head(&buckets[0]));
        Assert.Equal((nuint)0x4000, (nuint)alloc_list.added_alloc_list_tail(&buckets[0]));
#endif
    }

    [Fact]
    public void ClearResetsEveryActiveBucketHeadAndTail()
    {
        alloc_list* buckets = stackalloc alloc_list[3];
        for (int i = 0; i < 3; i++)
        {
            buckets[i] = default;
        }

        allocator a = new(4u, fbb: 3, buckets);

        for (uint bn = 0; bn < 4; bn++)
        {
            allocator.alloc_list_head_of(&a, bn) = (byte*)(0x10 + bn);
            allocator.alloc_list_tail_of(&a, bn) = (byte*)(0x20 + bn);
        }

        allocator.clear(&a);

        for (uint bn = 0; bn < 4; bn++)
        {
            Assert.Equal((nuint)0, (nuint)allocator.alloc_list_head_of(&a, bn));
            Assert.Equal((nuint)0, (nuint)allocator.alloc_list_tail_of(&a, bn));
        }
    }

    private static nuint OffsetOf(void* field, allocator* a) => (nuint)((byte*)field - (byte*)a);

#if TARGET_64BIT && !TARGET_WASM
    [Theory]
    [InlineData(2, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(-1, false)]
    public void IsDoublyLinkedOnlyForMaxGeneration(int gen, bool expected)
    {
        allocator a = new(1u, fbb: 3, b: null, gen);

        Assert.Equal(expected, a.is_doubly_linked_p());
    }
#endif

    private static nuint OffsetOf(void* field, dynamic_data* dd) => (nuint)((byte*)field - (byte*)dd);

    [Fact]
    public void DefaultDynamicDataIsZeroInitialized()
    {
        dynamic_data dd = default;
        dynamic_data* p = &dd;

        Assert.Equal((nint)0, dynamic_data.dd_new_allocation(p));
        Assert.Equal((nint)0, dynamic_data.dd_gc_new_allocation(p));
        Assert.Equal(0f, dynamic_data.dd_surv(p));
        Assert.Equal((nuint)0, dynamic_data.dd_desired_allocation(p));
        Assert.Equal((nuint)0, dynamic_data.dd_begin_data_size(p));
        Assert.Equal((nuint)0, dynamic_data.dd_survived_size(p));
        Assert.Equal((nuint)0, dynamic_data.dd_pinned_survived_size(p));
        Assert.Equal((nuint)0, dynamic_data.dd_artificial_pinned_survived_size(p));
        Assert.Equal((nuint)0, dynamic_data.dd_added_pinned_size(p));
        Assert.Equal((nuint)0, dynamic_data.dd_padding_size(p));
#if TARGET_ARM || TARGET_WASM
        Assert.Equal((nuint)0, dynamic_data.dd_num_npinned_plugs(p));
#endif
        Assert.Equal((nuint)0, dynamic_data.dd_current_size(p));
        Assert.Equal((nuint)0, dynamic_data.dd_collection_count(p));
        Assert.Equal((nuint)0, dynamic_data.dd_promoted_size(p));
        Assert.Equal((nuint)0, dynamic_data.dd_freach_previous_promotion(p));
        Assert.Equal((nuint)0, dynamic_data.dd_fragmentation(p));
        Assert.Equal((nuint)0, dynamic_data.dd_gc_clock(p));
        Assert.Equal(0UL, dynamic_data.dd_time_clock(p));
        Assert.Equal(0UL, dynamic_data.dd_previous_time_clock(p));
        Assert.Equal((nuint)0, dynamic_data.dd_gc_elapsed_time(p));
        Assert.Equal((nuint)0, dynamic_data.dd_min_size(p));
        Assert.Equal((nuint)0, (nuint)dd.sdata);
    }

    [Fact]
    public void DirectAccessorsReferToFieldsInNativeOrder()
    {
        dynamic_data dd = default;
        dynamic_data* p = &dd;
        nuint previous = 0;

        fixed (nint* f = &dynamic_data.dd_new_allocation(p))
        {
            Assert.True(f == &p->new_allocation);
            Assert.Equal((nuint)0, OffsetOf(f, p));
        }
        fixed (nint* f = &dynamic_data.dd_gc_new_allocation(p))
        {
            Assert.True(f == &p->gc_new_allocation);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (float* f = &dynamic_data.dd_surv(p))
        {
            Assert.True(f == &p->surv);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_desired_allocation(p))
        {
            Assert.True(f == &p->desired_allocation);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_begin_data_size(p))
        {
            Assert.True(f == &p->begin_data_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_survived_size(p))
        {
            Assert.True(f == &p->survived_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_pinned_survived_size(p))
        {
            Assert.True(f == &p->pinned_survived_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_artificial_pinned_survived_size(p))
        {
            Assert.True(f == &p->artificial_pinned_survived_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_added_pinned_size(p))
        {
            Assert.True(f == &p->added_pinned_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_padding_size(p))
        {
            Assert.True(f == &p->padding_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
#if TARGET_ARM || TARGET_WASM
        fixed (nuint* f = &dynamic_data.dd_num_npinned_plugs(p))
        {
            Assert.True(f == &p->num_npinned_plugs);
            previous = Ascending(OffsetOf(f, p), previous);
        }
#endif
        fixed (nuint* f = &dynamic_data.dd_current_size(p))
        {
            Assert.True(f == &p->current_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_collection_count(p))
        {
            Assert.True(f == &p->collection_count);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_promoted_size(p))
        {
            Assert.True(f == &p->promoted_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_freach_previous_promotion(p))
        {
            Assert.True(f == &p->freach_previous_promotion);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_fragmentation(p))
        {
            Assert.True(f == &p->fragmentation);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_gc_clock(p))
        {
            Assert.True(f == &p->gc_clock);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (ulong* f = &dynamic_data.dd_time_clock(p))
        {
            Assert.True(f == &p->time_clock);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (ulong* f = &dynamic_data.dd_previous_time_clock(p))
        {
            Assert.True(f == &p->previous_time_clock);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_gc_elapsed_time(p))
        {
            Assert.True(f == &p->gc_elapsed_time);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_min_size(p))
        {
            Assert.True(f == &p->min_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }

        // sdata is the last field; it has no direct accessor but closes the layout.
        Assert.True(OffsetOf(&p->sdata, p) > previous);
    }

    private static nuint Ascending(nuint offset, nuint previous)
    {
        Assert.True(offset > previous);
        return offset;
    }

    [Fact]
    public void DirectAccessorsMutateTheirFields()
    {
        dynamic_data dd = default;
        dynamic_data* p = &dd;

        dynamic_data.dd_new_allocation(p) = -11;
        dynamic_data.dd_gc_new_allocation(p) = -22;
        dynamic_data.dd_surv(p) = 1.5f;
        dynamic_data.dd_desired_allocation(p) = 33;
        dynamic_data.dd_begin_data_size(p) = 44;
        dynamic_data.dd_survived_size(p) = 55;
        dynamic_data.dd_pinned_survived_size(p) = 66;
        dynamic_data.dd_artificial_pinned_survived_size(p) = 77;
        dynamic_data.dd_added_pinned_size(p) = 88;
        dynamic_data.dd_padding_size(p) = 99;
#if TARGET_ARM || TARGET_WASM
        dynamic_data.dd_num_npinned_plugs(p) = 100;
#endif
        dynamic_data.dd_current_size(p) = 111;
        dynamic_data.dd_collection_count(p) = 122;
        dynamic_data.dd_promoted_size(p) = 133;
        dynamic_data.dd_freach_previous_promotion(p) = 144;
        dynamic_data.dd_fragmentation(p) = 155;
        dynamic_data.dd_gc_clock(p) = 166;
        dynamic_data.dd_time_clock(p) = 0x1122334455667788UL;
        dynamic_data.dd_previous_time_clock(p) = 0x8877665544332211UL;
        dynamic_data.dd_gc_elapsed_time(p) = 177;
        dynamic_data.dd_min_size(p) = 188;

        Assert.Equal((nint)(-11), dd.new_allocation);
        Assert.Equal((nint)(-22), dd.gc_new_allocation);
        Assert.Equal(1.5f, dd.surv);
        Assert.Equal((nuint)33, dd.desired_allocation);
        Assert.Equal((nuint)44, dd.begin_data_size);
        Assert.Equal((nuint)55, dd.survived_size);
        Assert.Equal((nuint)66, dd.pinned_survived_size);
        Assert.Equal((nuint)77, dd.artificial_pinned_survived_size);
        Assert.Equal((nuint)88, dd.added_pinned_size);
        Assert.Equal((nuint)99, dd.padding_size);
#if TARGET_ARM || TARGET_WASM
        Assert.Equal((nuint)100, dd.num_npinned_plugs);
#endif
        Assert.Equal((nuint)111, dd.current_size);
        Assert.Equal((nuint)122, dd.collection_count);
        Assert.Equal((nuint)133, dd.promoted_size);
        Assert.Equal((nuint)144, dd.freach_previous_promotion);
        Assert.Equal((nuint)155, dd.fragmentation);
        Assert.Equal((nuint)166, dd.gc_clock);
        Assert.Equal(0x1122334455667788UL, dd.time_clock);
        Assert.Equal(0x8877665544332211UL, dd.previous_time_clock);
        Assert.Equal((nuint)177, dd.gc_elapsed_time);
        Assert.Equal((nuint)188, dd.min_size);

        // The accessors read back the same values they set.
        Assert.Equal((nuint)166, dynamic_data.dd_gc_clock(p));
        Assert.Equal((nuint)188, dynamic_data.dd_min_size(p));
    }

    [Fact]
    public void SdataAccessorsReadAndWriteThroughSdata()
    {
        static_data sd = default;
        dynamic_data dd = default;
        dd.sdata = &sd;
        dynamic_data* p = &dd;

        fixed (float* f = &dynamic_data.dd_limit(p))
        {
            Assert.True(f == &sd.limit);
        }
        fixed (float* f = &dynamic_data.dd_max_limit(p))
        {
            Assert.True(f == &sd.max_limit);
        }
        fixed (nuint* f = &dynamic_data.dd_max_size(p))
        {
            Assert.True(f == &sd.max_size);
        }
        fixed (nuint* f = &dynamic_data.dd_fragmentation_limit(p))
        {
            Assert.True(f == &sd.fragmentation_limit);
        }
        fixed (float* f = &dynamic_data.dd_fragmentation_burden_limit(p))
        {
            Assert.True(f == &sd.fragmentation_burden_limit);
        }
        fixed (nuint* f = &dynamic_data.dd_gc_clock_interval(p))
        {
            Assert.True(f == &sd.gc_clock);
        }
        fixed (ulong* f = &dynamic_data.dd_time_clock_interval(p))
        {
            Assert.True(f == &sd.time_clock);
        }

        dynamic_data.dd_limit(p) = 0.5f;
        dynamic_data.dd_max_limit(p) = 0.25f;
        dynamic_data.dd_max_size(p) = 4096;
        dynamic_data.dd_fragmentation_limit(p) = 512;
        dynamic_data.dd_fragmentation_burden_limit(p) = 0.125f;
        dynamic_data.dd_gc_clock_interval(p) = 7;
        dynamic_data.dd_time_clock_interval(p) = 0xdeadbeefUL;

        Assert.Equal(0.5f, sd.limit);
        Assert.Equal(0.25f, sd.max_limit);
        Assert.Equal((nuint)4096, sd.max_size);
        Assert.Equal((nuint)512, sd.fragmentation_limit);
        Assert.Equal(0.125f, sd.fragmentation_burden_limit);
        Assert.Equal((nuint)7, sd.gc_clock);
        Assert.Equal(0xdeadbeefUL, sd.time_clock);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.125f, 0.25f)]
    [InlineData(0.3125f, 0.625f)]
    // 2 * 0.375 == 0.75, which the cap keeps rather than exceeds.
    [InlineData(0.375f, 0.75f)]
    [InlineData(0.5f, 0.75f)]
    [InlineData(1f, 0.75f)]
    [InlineData(float.NaN, float.NaN)]
    public void VFragmentationBurdenLimitDoublesAndCapsAt075(float burden, float expected)
    {
        static_data sd = default;
        sd.fragmentation_burden_limit = burden;
        dynamic_data dd = default;
        dd.sdata = &sd;

        Assert.Equal(expected, dynamic_data.dd_v_fragmentation_burden_limit(&dd));
    }

    private static nuint OffsetOf(void* field, generation* g) => (nuint)((byte*)field - (byte*)g);

    [Fact]
    public void GenerationInitializeBringsUpEmbeddedAllocatorAndLeavesOtherFieldsZero()
    {
        generation g = default;
        generation* p = &g;

        generation.initialize(p);

        // The load-bearing part of native default construction is the embedded allocator: a young
        // generation must come up with a single bucket, which the C# struct default would not give.
        allocator* a = generation.generation_allocator(p);
        Assert.Equal(1u, a->number_of_buckets());
        Assert.Equal(1, a->discard_if_no_fit_p());
#if TARGET_64BIT && !TARGET_WASM
        Assert.False(a->is_doubly_linked_p());
#endif

        // initialize touches only the embedded allocator; every other field stays zero.
        Assert.Equal((nuint)0, (nuint)p->start_segment);
        Assert.Equal((nuint)0, (nuint)p->allocation_segment);
        Assert.Equal((nuint)0, (nuint)p->allocation_context_start_region);
        Assert.Equal((nuint)0, (nuint)p->allocation_context.alloc_ptr);
        Assert.Equal((nuint)0, (nuint)p->allocation_context.alloc_limit);
        Assert.Equal((nuint)0, p->free_list_space);
        Assert.Equal((nuint)0, p->free_obj_space);
        Assert.Equal((nuint)0, p->allocation_size);
        Assert.Equal(0, p->allocate_end_seg_p);
        Assert.Equal(0, p->gen_num);
#if USE_REGIONS
        Assert.Equal((nuint)0, (nuint)p->tail_region);
        Assert.Equal((nuint)0, (nuint)p->tail_ro_region);
#else
        Assert.Equal((nuint)0, (nuint)p->allocation_start);
        Assert.Equal((nuint)0, (nuint)p->plan_allocation_start);
        Assert.Equal((nuint)0, p->plan_allocation_start_size);
#endif
#if TARGET_64BIT && !TARGET_WASM
        Assert.Equal(0, p->set_bgc_mark_bit_p);
        Assert.Equal((nuint)0, (nuint)p->last_free_list_allocated);
#endif
    }

    [Fact]
    public void GenerationAccessorsReferToFieldsInNativeOrder()
    {
        generation g = default;
        generation* p = &g;
        nuint previous = 0;

        // allocation_context is the first field; alloc_context adds nothing over gc_alloc_context.
        Assert.True(generation.generation_alloc_context(p) == &p->allocation_context);
        Assert.Equal((nuint)0, OffsetOf(&p->allocation_context, p));

        fixed (heap_segment** f = &generation.generation_start_segment(p))
        {
            Assert.True(f == &p->start_segment);
            previous = Ascending(OffsetOf(f, p), previous);
        }
#if !USE_REGIONS
        fixed (byte** f = &generation.generation_allocation_start(p))
        {
            Assert.True(f == &p->allocation_start);
            previous = Ascending(OffsetOf(f, p), previous);
        }
#endif
        fixed (heap_segment** f = &generation.generation_allocation_segment(p))
        {
            Assert.True(f == &p->allocation_segment);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (byte** f = &generation.generation_allocation_context_start_region(p))
        {
            Assert.True(f == &p->allocation_context_start_region);
            previous = Ascending(OffsetOf(f, p), previous);
        }
#if USE_REGIONS
        fixed (heap_segment** f = &generation.generation_tail_region(p))
        {
            Assert.True(f == &p->tail_region);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (heap_segment** f = &generation.generation_tail_ro_region(p))
        {
            Assert.True(f == &p->tail_ro_region);
            previous = Ascending(OffsetOf(f, p), previous);
        }
#endif
        Assert.True(generation.generation_allocator(p) == &p->free_list_allocator);
        previous = Ascending(OffsetOf(&p->free_list_allocator, p), previous);

        fixed (nuint* f = &generation.generation_free_list_allocated(p))
        {
            Assert.True(f == &p->free_list_allocated);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &generation.generation_end_seg_allocated(p))
        {
            Assert.True(f == &p->end_seg_allocated);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &generation.generation_condemned_allocated(p))
        {
            Assert.True(f == &p->condemned_allocated);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &generation.generation_sweep_allocated(p))
        {
            Assert.True(f == &p->sweep_allocated);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (int* f = &generation.generation_allocate_end_seg_p(p))
        {
            Assert.True(f == &p->allocate_end_seg_p);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &generation.generation_free_list_space(p))
        {
            Assert.True(f == &p->free_list_space);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &generation.generation_free_obj_space(p))
        {
            Assert.True(f == &p->free_obj_space);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &generation.generation_allocation_size(p))
        {
            Assert.True(f == &p->allocation_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
#if !USE_REGIONS
        fixed (byte** f = &generation.generation_plan_allocation_start(p))
        {
            Assert.True(f == &p->plan_allocation_start);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &generation.generation_plan_allocation_start_size(p))
        {
            Assert.True(f == &p->plan_allocation_start_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
#endif
        fixed (nuint* f = &generation.generation_pinned_allocation_compact_size(p))
        {
            Assert.True(f == &p->pinned_allocation_compact_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &generation.generation_pinned_allocation_sweep_size(p))
        {
            Assert.True(f == &p->pinned_allocation_sweep_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }

        // gen_num has no accessor of its own; it closes the unconditional part of the layout.
        previous = Ascending(OffsetOf(&p->gen_num, p), previous);
#if TARGET_64BIT && !TARGET_WASM
        fixed (int* f = &generation.generation_set_bgc_mark_bit_p(p))
        {
            Assert.True(f == &p->set_bgc_mark_bit_p);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (byte** f = &generation.generation_last_free_list_allocated(p))
        {
            Assert.True(f == &p->last_free_list_allocated);
            previous = Ascending(OffsetOf(f, p), previous);
        }
#endif
    }

    [Fact]
    public void GenerationRefAndPointerAccessorsMutateTheirFields()
    {
        generation g = default;
        generation* p = &g;

        // generation_alloc_context returns the embedded context; the pointer accessors reach into it.
        Assert.True(generation.generation_alloc_context(p) == &p->allocation_context);
        generation.generation_allocation_pointer(p) = (byte*)0x11;
        generation.generation_allocation_limit(p) = (byte*)0x22;
        Assert.Equal((nuint)0x11, (nuint)p->allocation_context.alloc_ptr);
        Assert.Equal((nuint)0x22, (nuint)p->allocation_context.alloc_limit);

        generation.generation_start_segment(p) = (heap_segment*)0x100;
        generation.generation_allocation_segment(p) = (heap_segment*)0x200;
        generation.generation_allocation_context_start_region(p) = (byte*)0x300;
        Assert.Equal((nuint)0x100, (nuint)p->start_segment);
        Assert.Equal((nuint)0x200, (nuint)p->allocation_segment);
        Assert.Equal((nuint)0x300, (nuint)p->allocation_context_start_region);

        generation.generation_free_list_allocated(p) = 5;
        generation.generation_end_seg_allocated(p) = 7;
        generation.generation_condemned_allocated(p) = 9;
        Assert.Equal((nuint)5, p->free_list_allocated);
        Assert.Equal((nuint)7, p->end_seg_allocated);
        Assert.Equal((nuint)9, p->condemned_allocated);

        // generation_total_plan_allocated sums the three planning allocation counters.
        Assert.Equal((nuint)21, generation.generation_total_plan_allocated(p));

        generation.generation_sweep_allocated(p) = 13;
        generation.generation_allocate_end_seg_p(p) = 1;
        generation.generation_free_list_space(p) = 41;
        generation.generation_free_obj_space(p) = 42;
        generation.generation_allocation_size(p) = 43;
        generation.generation_pinned_allocation_compact_size(p) = 44;
        generation.generation_pinned_allocation_sweep_size(p) = 45;
        Assert.Equal((nuint)13, p->sweep_allocated);
        Assert.Equal(1, p->allocate_end_seg_p);
        Assert.Equal((nuint)41, p->free_list_space);
        Assert.Equal((nuint)42, p->free_obj_space);
        Assert.Equal((nuint)43, p->allocation_size);
        Assert.Equal((nuint)44, p->pinned_allocation_compact_size);
        Assert.Equal((nuint)45, p->pinned_allocation_sweep_size);

#if USE_REGIONS
        generation.generation_tail_region(p) = (heap_segment*)0x400;
        generation.generation_tail_ro_region(p) = (heap_segment*)0x500;
        Assert.Equal((nuint)0x400, (nuint)p->tail_region);
        Assert.Equal((nuint)0x500, (nuint)p->tail_ro_region);

        // start_segment_rw returns a non-null tail_ro_region and otherwise the start segment.
        Assert.Equal((nuint)0x500, (nuint)generation.generation_start_segment_rw(p));
        generation.generation_tail_ro_region(p) = null;
        Assert.Equal((nuint)0x100, (nuint)generation.generation_start_segment_rw(p));
#else
        generation.generation_allocation_start(p) = (byte*)0x600;
        generation.generation_plan_allocation_start(p) = (byte*)0x700;
        generation.generation_plan_allocation_start_size(p) = 0x800;
        Assert.Equal((nuint)0x600, (nuint)p->allocation_start);
        Assert.Equal((nuint)0x700, (nuint)p->plan_allocation_start);
        Assert.Equal((nuint)0x800, p->plan_allocation_start_size);
#endif

#if TARGET_64BIT && !TARGET_WASM
        generation.generation_set_bgc_mark_bit_p(p) = 1;
        generation.generation_last_free_list_allocated(p) = (byte*)0x900;
        Assert.Equal(1, p->set_bgc_mark_bit_p);
        Assert.Equal((nuint)0x900, (nuint)p->last_free_list_allocated);
#endif
    }

#if USE_REGIONS
    [Fact]
    public void MakeGenerationResetsSohStateAndPreservesListPointers()
    {
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generation.initialize(&generations[i]);
        }

        heap_segment segment = default;
        heap_segment.heap_segment_mem(&segment) = (byte*)0x1000;
        generation* gen = &generations[(int)gc_generation_num.soh_gen1];
        gen->allocation_context.alloc_ptr = (byte*)0x1;
        gen->allocation_context.alloc_limit = (byte*)0x2;
        gen->allocation_context.alloc_bytes = 3;
        gen->allocation_context.alloc_bytes_uoh = 4;
        gen->allocation_context_start_region = (byte*)0x5;
        gen->start_segment = (heap_segment*)0x6;
        gen->tail_region = (heap_segment*)0x7;
        gen->tail_ro_region = (heap_segment*)0x8;
        gen->allocation_segment = (heap_segment*)0x9;
        gen->free_list_space = 10;
        gen->free_list_allocated = 11;
        gen->end_seg_allocated = 12;
        gen->condemned_allocated = 13;
        gen->sweep_allocated = 14;
        gen->free_obj_space = 15;
        gen->allocation_size = 16;
        gen->pinned_allocation_sweep_size = 17;
        gen->pinned_allocation_compact_size = 18;
        gen->allocate_end_seg_p = 1;
#if TARGET_64BIT && !TARGET_WASM
        gen->set_bgc_mark_bit_p = 1;
#endif
        allocator.alloc_list_head_of(&gen->free_list_allocator, 0) = (byte*)0xA;
        allocator.alloc_list_tail_of(&gen->free_list_allocator, 0) = (byte*)0xB;

        gc_heap.make_generation(
            generations,
            (int)gc_generation_num.soh_gen1,
            &segment,
            heap_segment.heap_segment_mem(&segment));

        Assert.Equal((int)gc_generation_num.soh_gen1, gen->gen_num);
        Assert.Equal((nuint)0, (nuint)gen->allocation_context.alloc_ptr);
        Assert.Equal((nuint)0, (nuint)gen->allocation_context.alloc_limit);
        Assert.Equal(0L, gen->allocation_context.alloc_bytes);
        Assert.Equal(0L, gen->allocation_context.alloc_bytes_uoh);
        Assert.Equal((nuint)0, (nuint)gen->allocation_context_start_region);
        Assert.Equal((nuint)(&segment), (nuint)gen->start_segment);
        Assert.Equal((nuint)(&segment), (nuint)gen->tail_region);
        Assert.Equal((nuint)0, (nuint)gen->tail_ro_region);
        Assert.Equal((nuint)(&segment), (nuint)gen->allocation_segment);
        Assert.Equal((nuint)0, gen->free_list_space);
        Assert.Equal((nuint)0, gen->free_list_allocated);
        Assert.Equal((nuint)0, gen->end_seg_allocated);
        Assert.Equal((nuint)0, gen->condemned_allocated);
        Assert.Equal((nuint)0, gen->sweep_allocated);
        Assert.Equal((nuint)0, gen->free_obj_space);
        Assert.Equal((nuint)0, gen->allocation_size);
        Assert.Equal((nuint)0, gen->pinned_allocation_sweep_size);
        Assert.Equal((nuint)0, gen->pinned_allocation_compact_size);
        Assert.Equal(0, gen->allocate_end_seg_p);
#if TARGET_64BIT && !TARGET_WASM
        Assert.Equal(0, gen->set_bgc_mark_bit_p);
#endif
        Assert.Equal((nuint)0, (nuint)allocator.alloc_list_head_of(&gen->free_list_allocator, 0));
        Assert.Equal((nuint)0, (nuint)allocator.alloc_list_tail_of(&gen->free_list_allocator, 0));
    }

    [Fact]
    public void ThreadUohSegmentAppendsAfterEmptyAndNonemptyWritableLists()
    {
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generation.initialize(&generations[i]);
        }

        heap_segment first = default;
        heap_segment second = default;
        heap_segment third = default;
        heap_segment fourth = default;
        heap_segment readOnly = default;
        readOnly.flags = heap_segment.heap_segment_flags_readonly;
        heap_segment.heap_segment_next(&readOnly) = &third;

        gc_heap.make_generation(
            generations,
            (int)gc_generation_num.loh_generation,
            &first,
            (byte*)0x1000);
        generation* loh = gc_heap.generation_of(generations, (int)gc_generation_num.loh_generation);

        Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_next(&first));
        gc_heap.thread_uoh_segment(generations, (int)gc_generation_num.loh_generation, &second);
        Assert.Equal((nuint)(&second), (nuint)heap_segment.heap_segment_next(&first));
        Assert.Equal((nuint)(&first), (nuint)generation.generation_allocation_segment(loh));

        heap_segment.heap_segment_next(&second) = &readOnly;
        Assert.Equal((nuint)(&third), (nuint)gc_heap.heap_segment_next_rw(&second));
        gc_heap.thread_uoh_segment(generations, (int)gc_generation_num.loh_generation, &fourth);

        Assert.Equal((nuint)(&readOnly), (nuint)heap_segment.heap_segment_next(&second));
        Assert.Equal((nuint)(&third), (nuint)heap_segment.heap_segment_next(&readOnly));
        Assert.Equal((nuint)(&fourth), (nuint)heap_segment.heap_segment_next(&third));
        Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_next(&fourth));
    }
#endif

#if USE_REGIONS
    [Fact]
    public void GenerationRegionInfoHasTwoSegmentPointers()
    {
        generation_region_info info = default;

        Assert.Equal((nuint)0, (nuint)info.head);
        Assert.Equal((nuint)0, (nuint)info.tail);
        Assert.Equal((nuint)(2 * sizeof(void*)), (nuint)sizeof(generation_region_info));
    }
#endif

    [Fact]
    public void SegMappingDefaultStateIsZeroed()
    {
        seg_mapping mapping = default;
        byte* bytes = (byte*)&mapping;

        for (int i = 0; i < sizeof(seg_mapping); i++)
        {
            Assert.Equal((byte)0, bytes[i]);
        }
    }

    [Fact]
    public void SegMappingFieldsFollowNativeOrder()
    {
        seg_mapping mapping = default;
        seg_mapping* p = &mapping;

#if USE_REGIONS
        Assert.Equal((nuint)0, OffsetOf(&p->region_info, p));
        Assert.Equal((nuint)sizeof(heap_segment), (nuint)sizeof(seg_mapping));
#else
        nuint previous = 0;

        Assert.Equal((nuint)0, OffsetOf(&p->boundary, p));
#if MULTIPLE_HEAPS
        previous = Ascending(OffsetOf(&p->h0, p), previous);
        previous = Ascending(OffsetOf(&p->h1, p), previous);
#endif
        previous = Ascending(OffsetOf(&p->seg0, p), previous);
        previous = Ascending(OffsetOf(&p->seg1, p), previous);
#endif
    }

#if USE_REGIONS
    [Fact]
    public void SegMappingUseRegionsSchemaEmbedsHeapSegmentAtNativeOffset()
    {
        seg_mapping mapping = default;

        Assert.Equal((nuint)0, OffsetOf(&mapping.region_info, &mapping));
        Assert.Equal((nuint)sizeof(heap_segment), (nuint)sizeof(seg_mapping));
        Assert.Equal((nuint)sizeof(void*), AlignmentOfSegMapping());
    }

    [Fact]
    public void SegMappingEmbedsFullHeapSegmentAsRegionInfo()
    {
        seg_mapping mapping = default;
        mapping.region_info.flags = heap_segment.heap_segment_flags_poh;

        Assert.Equal(heap_segment.heap_segment_flags_poh, mapping.region_info.flags);
        Assert.Equal((nuint)sizeof(heap_segment), (nuint)sizeof(seg_mapping));
    }

    [Fact]
    public void RegionMappingIndexHelpersPreserveAbsoluteShiftArithmetic()
    {
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowest = GCCommon.g_gc_lowest_address;
        byte* oldHighest = GCCommon.g_gc_highest_address;

        try
        {
            gc_heap.min_segment_size_shr = 12;
            GCCommon.g_gc_lowest_address = (byte*)0x2000;
            GCCommon.g_gc_highest_address = (byte*)0xA000;

            Assert.Equal((nuint)0, gc_heap.seg_mapping_word_of((byte*)0x0FFF));
            Assert.Equal((nuint)1, gc_heap.seg_mapping_word_of((byte*)0x1000));
            Assert.Equal((nuint)1, gc_heap.seg_mapping_word_of((byte*)0x1FFF));
            Assert.Equal((nuint)2, gc_heap.seg_mapping_word_of((byte*)0x2000));
            Assert.Equal((nuint)7, gc_heap.seg_mapping_word_of((byte*)0x7ABC));
            Assert.Equal((nuint)0x7000, (nuint)gc_heap.align_lower_segment((byte*)0x7ABC));

            Assert.Equal((nuint)2, gc_heap.get_skewed_basic_region_index_for_address((byte*)0x2000));
            Assert.Equal((nuint)4, gc_heap.get_skewed_basic_region_index_for_address((byte*)0x4FFF));
            Assert.Equal((nuint)0, gc_heap.get_basic_region_index_for_address((byte*)0x2000));
            Assert.Equal((nuint)1, gc_heap.get_basic_region_index_for_address((byte*)0x3000));
            Assert.Equal((nuint)7, gc_heap.get_basic_region_index_for_address((byte*)0x9000));
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowest;
            GCCommon.g_gc_highest_address = oldHighest;
        }
    }

    [Fact]
    public void RegionSegmentMappingSizeHelpersPreserveNativeAlignmentRules()
    {
        nuint oldShift = gc_heap.min_segment_size_shr;

        try
        {
            gc_heap.min_segment_size_shr = 12;

            Assert.Equal((nuint)0x2000, (nuint)gc_heap.align_on_segment((byte*)0x1001));
            Assert.Equal((nuint)0x2000, (nuint)gc_heap.align_on_segment((byte*)0x2000));
            Assert.Equal((nuint)(4 * sizeof(seg_mapping)), gc_heap.size_seg_mapping_table_of((byte*)0x1800, (byte*)0x4100));
            Assert.Equal((nuint)3, gc_heap.size_region_to_generation_table_of((byte*)0x1800, (byte*)0x4800));
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
        }
    }

    [Fact]
    public void ReadOnlyRegionMappingMarksOnlyClippedIntersectingEntries()
    {
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowest = GCCommon.g_gc_lowest_address;
        byte* oldHighest = GCCommon.g_gc_highest_address;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        seg_mapping* table = stackalloc seg_mapping[8];
        heap_segment segment = default;

        try
        {
            gc_heap.min_segment_size_shr = 12;
            GCCommon.g_gc_lowest_address = (byte*)0x2000;
            GCCommon.g_gc_highest_address = (byte*)0x5000;
            GCCommon.seg_mapping_table = table;

            segment.mem = (byte*)0x1000;
            segment.reserved = (byte*)0x7000;

            Assert.Equal((nuint)2, gc_heap.ro_seg_begin_index(&segment));
            Assert.Equal((nuint)5, gc_heap.ro_seg_end_index(&segment));

            gc_heap.seg_mapping_table_add_ro_segment(&segment);

            Assert.Equal((nuint)0, (nuint)table[1].region_info.allocated);
            Assert.Equal(seg_mapping.ro_in_entry, (nuint)table[2].region_info.allocated);
            Assert.Equal(seg_mapping.ro_in_entry, (nuint)table[3].region_info.allocated);
            Assert.Equal(seg_mapping.ro_in_entry, (nuint)table[4].region_info.allocated);
            Assert.Equal(seg_mapping.ro_in_entry, (nuint)table[5].region_info.allocated);
            Assert.Equal((nuint)0, (nuint)table[6].region_info.allocated);

            segment.mem = (byte*)0x5000;
            segment.reserved = (byte*)0x6000;
            gc_heap.seg_mapping_table_add_ro_segment(&segment);
            Assert.Equal((nuint)0, (nuint)table[6].region_info.allocated);

            gc_heap.seg_mapping_table_remove_ro_segment(&segment);
            Assert.Equal((nuint)0, (nuint)table[6].region_info.allocated);
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowest;
            GCCommon.g_gc_highest_address = oldHighest;
            GCCommon.seg_mapping_table = oldTable;
        }
    }

    [Fact]
    public void RegionMappingDirectLookupReinterpretsSegMappingEntryAsHeapSegment()
    {
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        seg_mapping* table = stackalloc seg_mapping[8];

        try
        {
            gc_heap.min_segment_size_shr = 12;
            GCCommon.seg_mapping_table = table;
            table[3].region_info.mem = (byte*)0x3456;
            table[3].region_info.allocated = (byte*)0x3ABC;

            heap_segment* region = gc_heap.get_region_info((byte*)0x3000);

            Assert.Equal((nuint)(&table[3]), (nuint)region);
            Assert.Equal((nuint)(&table[3].region_info), (nuint)region);
            Assert.Equal((nuint)0x3456, (nuint)heap_segment.heap_segment_mem(region));
            Assert.Equal((nuint)0x3ABC, (nuint)heap_segment.heap_segment_allocated(region));
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.seg_mapping_table = oldTable;
        }
    }

    [Fact]
    public void RegionOfAndGetRegionAtIndexPreserveSkewedAbsoluteIndexing()
    {
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowest = GCCommon.g_gc_lowest_address;
        byte* oldHighest = GCCommon.g_gc_highest_address;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        seg_mapping* table = stackalloc seg_mapping[4];

        try
        {
            gc_heap.min_segment_size_shr = 12;
            GCCommon.g_gc_lowest_address = (byte*)0x5000;
            GCCommon.g_gc_highest_address = (byte*)0x9000;
            GCCommon.seg_mapping_table = table - 5;
            table[2].region_info.gen_num = 2;

            heap_segment* regionOf = gc_heap.region_of((byte*)0x7001);
            heap_segment* regionAtIndex = gc_heap.get_region_at_index(2);

            Assert.Equal((nuint)(&table[2]), (nuint)regionOf);
            Assert.Equal((nuint)(&table[2]), (nuint)regionAtIndex);
            Assert.Equal(2, gc_heap.get_region_gen_num(regionOf));
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowest;
            GCCommon.g_gc_highest_address = oldHighest;
            GCCommon.seg_mapping_table = oldTable;
        }
    }

    [Fact]
    public void RegionGenerationMapReadsUseSkewedAbsoluteIndicesAndPackedFields()
    {
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowest = GCCommon.g_gc_lowest_address;
        byte* oldHighest = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
        region_info* oldGenerationMap = gc_heap.map_region_to_generation;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
        seg_mapping* segMappingTable = stackalloc seg_mapping[4];
        region_info* generationMap = stackalloc region_info[4];

        try
        {
            gc_heap.min_segment_size_shr = 12;
            GCCommon.g_gc_lowest_address = (byte*)0x5000;
            GCCommon.g_gc_highest_address = (byte*)0x9000;
            GCCommon.seg_mapping_table = segMappingTable - 5;
            gc_heap.map_region_to_generation = generationMap;
            gc_heap.map_region_to_generation_skewed = generationMap - 5;

            Assert.Equal((nuint)1, (nuint)sizeof(region_info));

            segMappingTable[1].region_info.gen_num = 1;
            segMappingTable[1].region_info.plan_gen_num = 1;
            segMappingTable[1].region_info.flags = heap_segment.heap_segment_flags_demoted;
            generationMap[1] = (region_info)((byte)region_info.RI_GEN_1 | (byte)region_info.RI_PLAN_GEN_1 | (byte)region_info.RI_DEMOTED);

            segMappingTable[2].region_info.gen_num = 2;
            segMappingTable[2].region_info.plan_gen_num = 2;
            generationMap[2] = (region_info)((byte)region_info.RI_GEN_2 | (byte)region_info.RI_PLAN_GEN_2);

            Assert.Equal(1, gc_heap.get_region_gen_num((byte*)0x6000));
            Assert.Equal(1, gc_heap.get_region_gen_num((byte*)0x6FFF));
            Assert.Equal(1, gc_heap.get_region_plan_gen_num((byte*)0x6000));
            Assert.True(gc_heap.is_region_demoted((byte*)0x6FFF));

            Assert.Equal(2, gc_heap.get_region_gen_num((byte*)0x7000));
            Assert.Equal(2, gc_heap.get_region_plan_gen_num((byte*)0x7000));
            Assert.False(gc_heap.is_region_demoted((byte*)0x7000));
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowest;
            GCCommon.g_gc_highest_address = oldHighest;
            GCCommon.seg_mapping_table = oldSegMappingTable;
            gc_heap.map_region_to_generation = oldGenerationMap;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
        }
    }

    [Fact]
    public void ShouldCheckBrickForRelocUsesSkewedRegionGenerationMap()
    {
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowest = GCCommon.g_gc_lowest_address;
        byte* oldHighest = GCCommon.g_gc_highest_address;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
        gc_mechanisms oldSettings = gc_heap.settings;
        region_info* generationMap = stackalloc region_info[4];

        try
        {
            gc_heap.min_segment_size_shr = 12;
            GCCommon.g_gc_lowest_address = (byte*)0x5000;
            GCCommon.g_gc_highest_address = (byte*)0x9000;
            gc_heap.map_region_to_generation_skewed = generationMap - 5;
            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen1;
            generationMap[0] = region_info.RI_GEN_0;
            generationMap[1] = region_info.RI_GEN_1;
            generationMap[2] = region_info.RI_GEN_2;
            generationMap[3] = (region_info)((byte)region_info.RI_GEN_0 | (byte)region_info.RI_SIP);

            Assert.True(gc_heap.should_check_brick_for_reloc((byte*)0x5000));
            Assert.True(gc_heap.should_check_brick_for_reloc((byte*)0x6000));
            Assert.False(gc_heap.should_check_brick_for_reloc((byte*)0x7000));
            Assert.False(gc_heap.should_check_brick_for_reloc((byte*)0x8000));
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowest;
            GCCommon.g_gc_highest_address = oldHighest;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
            gc_heap.settings = oldSettings;
        }
    }

    [Fact]
    public void CheckDemotionHelperSipIgnoresNonHeapChild()
    {
        byte* oldLowest = GCCommon.g_gc_lowest_address;
        byte* oldHighest = GCCommon.g_gc_highest_address;
        uint* oldCardTable = gc_heap.card_table;
        uint* cards = stackalloc uint[4];

        try
        {
            GCCommon.g_gc_lowest_address = (byte*)0x5000;
            GCCommon.g_gc_highest_address = (byte*)0x9000;
            cards[0] = 0xAAAAAAAA;
            cards[1] = 0x55555555;
            cards[2] = 0xCCCCCCCC;
            cards[3] = 0x33333333;
            gc_heap.card_table = cards;

            byte* child = (byte*)0x4000;
            gc_heap.check_demotion_helper_sip(&child, (int)gc_generation_num.soh_gen2, (byte*)0x6000);

            Assert.Equal(0xAAAAAAAAu, cards[0]);
            Assert.Equal(0x55555555u, cards[1]);
            Assert.Equal(0xCCCCCCCCu, cards[2]);
            Assert.Equal(0x33333333u, cards[3]);
        }
        finally
        {
            GCCommon.g_gc_lowest_address = oldLowest;
            GCCommon.g_gc_highest_address = oldHighest;
            gc_heap.card_table = oldCardTable;
        }
    }

    [Fact]
    public void CheckDemotionHelperSipSetsOnlyParentCardForLowerChildPlanGeneration()
    {
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowest = GCCommon.g_gc_lowest_address;
        byte* oldHighest = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
        uint* oldCardTable = gc_heap.card_table;
        seg_mapping* segMappingTable = stackalloc seg_mapping[4];
        region_info* generationMap = stackalloc region_info[4];
        uint* cards = stackalloc uint[8];

        try
        {
            gc_heap.min_segment_size_shr = 12;
            GCCommon.g_gc_lowest_address = (byte*)0x5000;
            GCCommon.g_gc_highest_address = (byte*)0x9000;
            GCCommon.seg_mapping_table = segMappingTable - 5;
            gc_heap.map_region_to_generation_skewed = generationMap - 5;
            gc_heap.card_table = cards;

            for (nuint i = 0; i < 8; i++)
            {
                cards[(nint)i] = 0;
            }

            segMappingTable[1].region_info.plan_gen_num = (int)gc_generation_num.soh_gen1;
            generationMap[1] = (region_info)((byte)region_info.RI_GEN_1 | (byte)region_info.RI_PLAN_GEN_1);

            byte* child = (byte*)0x6000;
            byte* parentLoc = (byte*)0x6800;
            nuint card = gc_heap.card_of(parentLoc);
            nuint word = card_table_info.card_word(card);
            uint expectedCard = 1u << (int)card_table_info.card_bit(card);

            gc_heap.check_demotion_helper_sip(&child, (int)gc_generation_num.soh_gen2, parentLoc);

            for (nuint i = 0; i < 8; i++)
            {
                Assert.Equal(i == word ? expectedCard : 0u, cards[(nint)i]);
            }
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowest;
            GCCommon.g_gc_highest_address = oldHighest;
            GCCommon.seg_mapping_table = oldSegMappingTable;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
            gc_heap.card_table = oldCardTable;
        }
    }

    [Theory]
    [InlineData((int)gc_generation_num.soh_gen1)]
    [InlineData((int)gc_generation_num.soh_gen0)]
    public void CheckDemotionHelperSipIgnoresEqualOrHigherChildPlanGeneration(int parentPlanGeneration)
    {
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowest = GCCommon.g_gc_lowest_address;
        byte* oldHighest = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
        uint* oldCardTable = gc_heap.card_table;
        seg_mapping* segMappingTable = stackalloc seg_mapping[4];
        region_info* generationMap = stackalloc region_info[4];
        uint* cards = stackalloc uint[8];

        try
        {
            gc_heap.min_segment_size_shr = 12;
            GCCommon.g_gc_lowest_address = (byte*)0x5000;
            GCCommon.g_gc_highest_address = (byte*)0x9000;
            GCCommon.seg_mapping_table = segMappingTable - 5;
            gc_heap.map_region_to_generation_skewed = generationMap - 5;
            gc_heap.card_table = cards;

            for (nuint i = 0; i < 8; i++)
            {
                cards[(nint)i] = 0;
            }

            segMappingTable[1].region_info.plan_gen_num = (int)gc_generation_num.soh_gen1;
            generationMap[1] = (region_info)((byte)region_info.RI_GEN_1 | (byte)region_info.RI_PLAN_GEN_1);

            byte* child = (byte*)0x6000;
            gc_heap.check_demotion_helper_sip(&child, parentPlanGeneration, (byte*)0x6800);

            for (nuint i = 0; i < 8; i++)
            {
                Assert.Equal(0u, cards[(nint)i]);
            }
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowest;
            GCCommon.g_gc_highest_address = oldHighest;
            GCCommon.seg_mapping_table = oldSegMappingTable;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
            gc_heap.card_table = oldCardTable;
        }
    }

    [Fact]
    public void RegionGenerationMapFlagsKeepSegmentFieldsConsistent()
    {
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowest = GCCommon.g_gc_lowest_address;
        byte* oldHighest = GCCommon.g_gc_highest_address;
        region_allocator oldGlobalRegionAllocator = gc_heap.global_region_allocator;
        region_info* oldGenerationMap = gc_heap.map_region_to_generation;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
        region_info* generationMap = stackalloc region_info[4];
        heap_segment region = default;

        try
        {
            gc_heap.min_segment_size_shr = 12;
            GCCommon.g_gc_lowest_address = (byte*)0x5000;
            GCCommon.g_gc_highest_address = (byte*)0x9000;
            gc_heap.global_region_allocator.initialize_alignment(0x1000);
            gc_heap.map_region_to_generation = generationMap;
            gc_heap.map_region_to_generation_skewed = generationMap - 5;
            generationMap[1] = (region_info)((byte)region_info.RI_GEN_1 | (byte)region_info.RI_PLAN_GEN_2 | (byte)region_info.RI_DEMOTED);

            region.mem = (byte*)0x6000 + sizeof(aligned_plug_and_gap);
            region.reserved = (byte*)0x7000;
            region.flags = heap_segment.heap_segment_flags_demoted;

            gc_heap.set_region_sweep_in_plan(&region);
            Assert.Equal((byte)1, region.swept_in_plan_p);
            Assert.Equal(
                (byte)region_info.RI_GEN_1 | (byte)region_info.RI_PLAN_GEN_2 | (byte)region_info.RI_DEMOTED | (byte)region_info.RI_SIP,
                (byte)generationMap[1]);

            gc_heap.clear_region_sweep_in_plan(&region);
            Assert.Equal((byte)0, region.swept_in_plan_p);
            Assert.Equal(
                (byte)region_info.RI_GEN_1 | (byte)region_info.RI_PLAN_GEN_2 | (byte)region_info.RI_DEMOTED,
                (byte)generationMap[1]);

            gc_heap.clear_region_demoted(&region);
            Assert.Equal((nuint)0, region.flags & heap_segment.heap_segment_flags_demoted);
            Assert.Equal(
                (byte)region_info.RI_GEN_1 | (byte)region_info.RI_PLAN_GEN_2,
                (byte)generationMap[1]);
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowest;
            GCCommon.g_gc_highest_address = oldHighest;
            gc_heap.global_region_allocator = oldGlobalRegionAllocator;
            gc_heap.map_region_to_generation = oldGenerationMap;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
        }
    }

    [Fact]
    public void RegionStartObjectHelpersUseRegionMemory()
    {
        heap_segment region = default;
        generation gen = default;
        region.mem = (byte*)0x12345678;

        Assert.Equal((nuint)region.mem, (nuint)gc_heap.get_uoh_start_object(&region, &gen));
        Assert.Equal((nuint)region.mem, (nuint)gc_heap.get_soh_start_object(&region, &gen));
        Assert.Equal((nuint)0, gc_heap.get_soh_start_obj_len(region.mem));
    }

    [Fact]
    public void RegionMappingForAddressBacktracksLargeRegionContinuationSentinel()
    {
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        seg_mapping* table = stackalloc seg_mapping[8];

        try
        {
            gc_heap.min_segment_size_shr = 12;
            GCCommon.seg_mapping_table = table;
            table[4].region_info.mem = (byte*)0x4000;
            table[4].region_info.allocated = (byte*)0x4ABC;
            table[5].region_info.allocated = (byte*)(nint)(-1);
            table[6].region_info.allocated = (byte*)(nint)(-2);

            Assert.Equal((nuint)(&table[4]), (nuint)gc_heap.get_region_info_for_address((byte*)0x4000));
            Assert.Equal((nuint)(&table[4]), (nuint)gc_heap.get_region_info_for_address((byte*)0x5FFF));
            Assert.Equal((nuint)(&table[4]), (nuint)gc_heap.get_region_info_for_address((byte*)0x6123));
            Assert.Equal((nuint)0x4ABC, (nuint)heap_segment.heap_segment_allocated(gc_heap.get_region_info_for_address((byte*)0x6FFF)));
        }

        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.seg_mapping_table = oldTable;
        }
    }

    [Fact]
    public void RegionSegmentLookupUsesMappedSegmentsAndRejectsFreeOrWrongHeapRegions()
    {
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowest = GCCommon.g_gc_lowest_address;
        byte* oldHighest = GCCommon.g_gc_highest_address;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        byte* oldBookkeepingCoveredCommitted = gc_heap.bookkeeping_covered_committed;
        seg_mapping* table = stackalloc seg_mapping[4];

        try
        {
            gc_heap.min_segment_size_shr = 12;
            GCCommon.g_gc_lowest_address = (byte*)0x5000;
            GCCommon.g_gc_highest_address = (byte*)0x9000;
            GCCommon.seg_mapping_table = table - 5;
            gc_heap.bookkeeping_covered_committed = (byte*)0x9000;

            table[1].region_info.mem = (byte*)0x6000;
            table[1].region_info.allocated = (byte*)0x6100;
            table[1].region_info.reserved = (byte*)0x7000;
            table[1].region_info.gen_num = (byte)gc_generation_num.soh_gen1;

            table[2].region_info.mem = (byte*)0x7000;
            table[2].region_info.allocated = (byte*)0x7100;
            table[2].region_info.reserved = (byte*)0x8000;
            table[2].region_info.flags = heap_segment.heap_segment_flags_loh;
            table[2].region_info.gen_num = (byte)gc_generation_num.max_generation;

            Assert.True(gc_heap.try_get_region_segment((byte*)0x6001, small_heap_only: false, out heap_segment* old));
            Assert.Equal((nuint)(&table[1]), (nuint)old);
            Assert.Equal((byte)gc_generation_num.soh_gen1, heap_segment.heap_segment_gen_num(old));

            Assert.True(gc_heap.try_get_region_segment((byte*)0x7001, small_heap_only: false, out heap_segment* loh));
            Assert.Equal((nuint)(&table[2]), (nuint)loh);
            Assert.False(gc_heap.try_get_region_segment((byte*)0x7001, small_heap_only: true, out _));

            Assert.False(gc_heap.try_get_region_segment((byte*)0x8001, small_heap_only: false, out _));
            Assert.False(gc_heap.try_get_region_segment((byte*)0x9000, small_heap_only: false, out _));
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowest;
            GCCommon.g_gc_highest_address = oldHighest;
            GCCommon.seg_mapping_table = oldTable;
            gc_heap.bookkeeping_covered_committed = oldBookkeepingCoveredCommitted;
        }
    }

    [Fact]
    public void RegionMappingFreeRegionClassificationUsesAllocatedNull()
    {
        heap_segment region = default;

        region.allocated = null;
        region.mem = (byte*)0x1000;
        Assert.True(gc_heap.is_free_region(&region));

        region.allocated = (byte*)1;
        Assert.False(gc_heap.is_free_region(&region));

        region.allocated = (byte*)(nint)(-1);
        Assert.False(gc_heap.is_free_region(&region));
    }

    [Theory]
    [InlineData(0UL, -1)]
    [InlineData(1UL, 0)]
    [InlineData(0x1000UL, 12)]
    [InlineData(0x400000UL, 22)]
    public void MinSegmentSizeShiftInitializationUsesHighestSetBit(ulong size, int expectedShift)
    {
        nuint oldShift = gc_heap.min_segment_size_shr;
        try
        {
            gc_heap.initialize_min_segment_size_shr((nuint)size);

            Assert.Equal((nuint)expectedShift, gc_heap.min_segment_size_shr);
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
        }
    }

    [Theory]
    [InlineData(0UL, true)]
    [InlineData(1UL, true)]
    [InlineData(2UL, true)]
    [InlineData(3UL, false)]
    [InlineData(0x400000UL, true)]
    public void RegionSizePowerOfTwoCheckMatchesNative(ulong size, bool expected)
    {
        Assert.Equal(expected, gc_heap.power_of_two_p((nuint)size));
    }
#endif

    [Fact]
    public void SegMappingReadOnlyEntryFlagUsesLowBit()
    {
        const nuint SegmentAddress = 0x100;
        nuint taggedSegment = SegmentAddress | seg_mapping.ro_in_entry;

        Assert.Equal((nuint)1, seg_mapping.ro_in_entry);
        Assert.Equal(seg_mapping.ro_in_entry, taggedSegment & seg_mapping.ro_in_entry);
        Assert.Equal(SegmentAddress, taggedSegment & ~seg_mapping.ro_in_entry);
    }

    [Fact]
    public void HeapSegmentDefaultStateIsZeroed()
    {
        heap_segment segment = default;
        heap_segment* p = &segment;

        Assert.Equal((nuint)0, (nuint)p->allocated);
        Assert.Equal((nuint)0, (nuint)p->committed);
        Assert.Equal((nuint)0, (nuint)p->reserved);
        Assert.Equal((nuint)0, (nuint)p->used);
        Assert.Equal((nuint)0, (nuint)p->mem);
        Assert.Equal((nuint)0, p->flags);
        Assert.Equal((nuint)0, (nuint)p->next);
        Assert.Equal((nuint)0, (nuint)p->background_allocated);
        Assert.Equal((nuint)0, (nuint)p->plan_allocated);
        Assert.Equal((nuint)0, (nuint)p->saved_allocated);
        Assert.Equal((nuint)0, (nuint)p->saved_bg_allocated);
#if !USE_REGIONS || MULTIPLE_HEAPS
        Assert.Equal((nuint)0, (nuint)p->decommit_target);
#endif
#if USE_REGIONS
        Assert.Equal((nuint)0, p->survived);
        Assert.Equal((byte)0, p->gen_num);
        Assert.Equal((byte)0, p->swept_in_plan_p);
        Assert.Equal(0, p->plan_gen_num);
        Assert.Equal(0, p->old_card_survived);
        Assert.Equal(0, p->pinned_survived);
        Assert.Equal(0, p->age_in_free);
        Assert.Equal((nuint)0, (nuint)p->free_list_head);
        Assert.Equal((nuint)0, (nuint)p->free_list_tail);
        Assert.Equal((nuint)0, p->free_list_size);
        Assert.Equal((nuint)0, p->free_obj_size);
        Assert.Equal((nuint)0, (nuint)p->prev_free_region);
        Assert.Equal((nuint)0, (nuint)p->containing_free_list);
#endif
    }

    [Fact]
    public void HeapSegmentFieldsAndReferenceAccessorsFollowNativeOrder()
    {
        heap_segment segment = default;
        heap_segment* p = &segment;
        nuint previous = 0;

        Assert.Equal((nuint)0, OffsetOf(&p->allocated, p));
        previous = Ascending(OffsetOf(&p->committed, p), previous);
        previous = Ascending(OffsetOf(&p->reserved, p), previous);
        previous = Ascending(OffsetOf(&p->used, p), previous);
        previous = Ascending(OffsetOf(&p->mem, p), previous);
        previous = Ascending(OffsetOf(&p->flags, p), previous);
        previous = Ascending(OffsetOf(&p->next, p), previous);
        previous = Ascending(OffsetOf(&p->background_allocated, p), previous);
#if MULTIPLE_HEAPS
        previous = Ascending(OffsetOf(&p->heap, p), previous);
#if DEBUG && !USE_REGIONS
        previous = Ascending(OffsetOf(&p->saved_committed, p), previous);
        previous = Ascending(OffsetOf(&p->saved_desired_allocation, p), previous);
#endif
#endif
#if !USE_REGIONS || MULTIPLE_HEAPS
        previous = Ascending(OffsetOf(&p->decommit_target, p), previous);
#endif
        previous = Ascending(OffsetOf(&p->plan_allocated, p), previous);
        previous = Ascending(OffsetOf(&p->saved_allocated, p), previous);
        previous = Ascending(OffsetOf(&p->saved_bg_allocated, p), previous);
#if USE_REGIONS
        previous = Ascending(OffsetOf(&p->survived, p), previous);
        previous = Ascending(OffsetOf(&p->gen_num, p), previous);
        previous = Ascending(OffsetOf(&p->swept_in_plan_p, p), previous);
        previous = Ascending(OffsetOf(&p->plan_gen_num, p), previous);
        previous = Ascending(OffsetOf(&p->old_card_survived, p), previous);
        previous = Ascending(OffsetOf(&p->pinned_survived, p), previous);
        previous = Ascending(OffsetOf(&p->age_in_free, p), previous);
        previous = Ascending(OffsetOf(&p->free_list_head, p), previous);
        previous = Ascending(OffsetOf(&p->free_list_tail, p), previous);
        previous = Ascending(OffsetOf(&p->free_list_size, p), previous);
        previous = Ascending(OffsetOf(&p->free_obj_size, p), previous);
        previous = Ascending(OffsetOf(&p->prev_free_region, p), previous);
        previous = Ascending(OffsetOf(&p->containing_free_list, p), previous);
#else
        previous = Ascending(OffsetOf(&p->padandplug, p), previous);
#endif

        heap_segment.heap_segment_reserved(p) = (byte*)1;
        heap_segment.heap_segment_committed(p) = (byte*)2;
        heap_segment.heap_segment_used(p) = (byte*)3;
        heap_segment.heap_segment_allocated(p) = (byte*)4;
        heap_segment.heap_segment_next(p) = (heap_segment*)5;
        heap_segment.heap_segment_mem(p) = (byte*)6;
        heap_segment.heap_segment_plan_allocated(p) = (byte*)7;
        heap_segment.heap_segment_saved_allocated(p) = (byte*)8;
#if BACKGROUND_GC
        heap_segment.heap_segment_background_allocated(p) = (byte*)9;
        heap_segment.heap_segment_saved_bg_allocated(p) = (byte*)10;
#endif

        Assert.Equal((nuint)1, (nuint)p->reserved);
        Assert.Equal((nuint)2, (nuint)p->committed);
        Assert.Equal((nuint)3, (nuint)p->used);
        Assert.Equal((nuint)4, (nuint)p->allocated);
        Assert.Equal((nuint)5, (nuint)p->next);
        Assert.Equal((nuint)6, (nuint)p->mem);
        Assert.Equal((nuint)7, (nuint)p->plan_allocated);
        Assert.Equal((nuint)8, (nuint)p->saved_allocated);
#if BACKGROUND_GC
        Assert.Equal((nuint)9, (nuint)p->background_allocated);
        Assert.Equal((nuint)10, (nuint)p->saved_bg_allocated);
#endif
    }

    [Theory]
    [InlineData(0UL, 0, 1)]
    [InlineData(1UL, 1, 0)]
    [InlineData(2UL, 0, 1)]
    [InlineData(3UL, 1, 1)]
    public void HeapSegmentReadOnlyAndInRangeFlagsHaveNativeTruthTable(ulong flags, int readOnly, int inRange)
    {
        heap_segment segment = default;
        segment.flags = (nuint)flags;

        Assert.Equal(readOnly, heap_segment.heap_segment_read_only_p(&segment));
        Assert.Equal(inRange, heap_segment.heap_segment_in_range_p(&segment));
    }

    [Fact]
    public void HeapSegmentRangeTraversalSkipsOutOfRangeReadOnlySegments()
    {
        heap_segment first = default;
        heap_segment skipped = default;
        heap_segment included = default;

        first.next = &skipped;
        skipped.flags = heap_segment.heap_segment_flags_readonly;
        skipped.next = &included;
        included.flags = heap_segment.heap_segment_flags_readonly | heap_segment.heap_segment_flags_inrange;

        Assert.Equal((nuint)0, (nuint)gc_heap.heap_segment_in_range(null));
        Assert.Equal((nuint)(&first), (nuint)gc_heap.heap_segment_in_range(&first));
        Assert.Equal((nuint)(&included), (nuint)gc_heap.heap_segment_in_range(&skipped));
        Assert.Equal((nuint)(&included), (nuint)gc_heap.heap_segment_next_in_range(&first));

        included.next = &skipped;
        skipped.next = null;
        Assert.Equal((nuint)0, (nuint)gc_heap.heap_segment_next_in_range(&included));
    }

    [Fact]
    public void HeapSegmentAddressRangeUsesHalfOpenBounds()
    {
        heap_segment segment = default;
        segment.mem = (byte*)0x1000;
        segment.reserved = (byte*)0x2000;

        Assert.Equal(0, gc_heap.in_range_for_segment((byte*)0xFFF, &segment));
        Assert.Equal(1, gc_heap.in_range_for_segment((byte*)0x1000, &segment));
        Assert.Equal(1, gc_heap.in_range_for_segment((byte*)0x1FFF, &segment));
        Assert.Equal(0, gc_heap.in_range_for_segment((byte*)0x2000, &segment));
    }

    [Fact]
    public void HeapSegmentGenerationIterationBoundsMatchNativeConfiguration()
    {
#if USE_REGIONS
        Assert.Equal(0, gc_heap.get_start_generation_index());
        Assert.Equal(0, gc_heap.get_stop_generation_index(2));
#else
        Assert.Equal(GCInterfaceOffsets.max_generation, gc_heap.get_start_generation_index());
        Assert.Equal(2, gc_heap.get_stop_generation_index(2));
#endif
    }

    [Fact]
    public void HeapSegmentObjectHeapAndBackgroundPredicatesPreserveNativePrecedence()
    {
        heap_segment segment = default;

        Assert.Equal(0, heap_segment.heap_segment_loh_p(&segment));
        Assert.Equal(0, heap_segment.heap_segment_poh_p(&segment));
        Assert.Equal(0, heap_segment.heap_segment_uoh_p(&segment));
        Assert.Equal(gc_oh_num.soh, heap_segment.heap_segment_oh(&segment));

        segment.flags = heap_segment.heap_segment_flags_poh;
        Assert.Equal(1, heap_segment.heap_segment_poh_p(&segment));
        Assert.Equal(1, heap_segment.heap_segment_uoh_p(&segment));
        Assert.Equal(gc_oh_num.poh, heap_segment.heap_segment_oh(&segment));

        segment.flags |= heap_segment.heap_segment_flags_loh;
        Assert.Equal(1, heap_segment.heap_segment_loh_p(&segment));
        Assert.Equal(1, heap_segment.heap_segment_poh_p(&segment));
        Assert.Equal(1, heap_segment.heap_segment_uoh_p(&segment));
        Assert.Equal(gc_oh_num.loh, heap_segment.heap_segment_oh(&segment));

#if BACKGROUND_GC
        segment.flags = heap_segment.heap_segment_flags_decommitted | heap_segment.heap_segment_flags_swept;
        Assert.Equal(1, heap_segment.heap_segment_decommitted_p(&segment));
        Assert.Equal(1, heap_segment.heap_segment_swept_p(&segment));
        segment.flags = 0;
        Assert.Equal(0, heap_segment.heap_segment_decommitted_p(&segment));
        Assert.Equal(0, heap_segment.heap_segment_swept_p(&segment));
#endif
#if BACKGROUND_GC && USE_REGIONS
        segment.flags = heap_segment.heap_segment_flags_overflow;
        Assert.True(heap_segment.heap_segment_overflow_p(&segment));
        segment.flags = 0;
        Assert.False(heap_segment.heap_segment_overflow_p(&segment));
#endif
#if USE_REGIONS
        segment.flags = heap_segment.heap_segment_flags_demoted;
        Assert.True(heap_segment.heap_segment_demoted_p(&segment));
        segment.flags = 0;
        Assert.False(heap_segment.heap_segment_demoted_p(&segment));
#endif
    }

#if USE_REGIONS
    [Fact]
    public void HeapSegmentRegionAccessorsAndFreeListInitializationMutateOnlyTheirFields()
    {
        heap_segment segment = default;
        heap_segment* p = &segment;

        heap_segment.heap_segment_containing_free_list(p) = (region_free_list*)1;
        heap_segment.heap_segment_prev_free_region(p) = (heap_segment*)2;
        heap_segment.heap_segment_gen_num(p) = 3;
        heap_segment.heap_segment_swept_in_plan(p) = 1;
        heap_segment.heap_segment_plan_gen_num(p) = 4;
        heap_segment.heap_segment_age_in_free(p) = 5;
        heap_segment.heap_segment_survived(p) = 6;
        heap_segment.heap_segment_old_card_survived(p) = 7;
        heap_segment.heap_segment_pinned_survived(p) = 8;
        p->free_list_head = (byte*)9;
        p->free_list_tail = (byte*)10;
        p->free_list_size = 11;
        p->free_obj_size = 12;

        Assert.Equal((nuint)1, (nuint)p->containing_free_list);
        Assert.Equal((nuint)2, (nuint)p->prev_free_region);
        Assert.Equal((byte)3, p->gen_num);
        Assert.Equal((byte)1, p->swept_in_plan_p);
        Assert.Equal(4, p->plan_gen_num);
        Assert.Equal(5, p->age_in_free);
        Assert.Equal((nuint)6, p->survived);
        Assert.Equal(7, p->old_card_survived);
        Assert.Equal(8, p->pinned_survived);
        Assert.Equal((nuint)9, (nuint)heap_segment.heap_segment_free_list_head(p));
        Assert.Equal((nuint)10, (nuint)heap_segment.heap_segment_free_list_tail(p));
        Assert.Equal((nuint)11, heap_segment.heap_segment_free_list_size(p));
        Assert.Equal((nuint)12, heap_segment.heap_segment_free_obj_size(p));

        p->init_free_list();

        Assert.Equal((nuint)0, (nuint)p->free_list_head);
        Assert.Equal((nuint)0, (nuint)p->free_list_tail);
        Assert.Equal((nuint)0, p->free_list_size);
        Assert.Equal((nuint)0, p->free_obj_size);
        Assert.Equal((byte)3, p->gen_num);
        Assert.Equal(5, p->age_in_free);
    }

    [Fact]
    public void HeapSegmentThreadFreeObjAppendsSweepGapsAndAccountsSmallGaps()
    {
        nuint minFreeList = unchecked(2 * (nuint)GCInterfaceOffsets.min_obj_size);
        nuint secondSize = unchecked(minFreeList + (nuint)GCInterfaceOffsets.min_obj_size);
        nuint belowMinFreeList = unchecked(minFreeList - 1);
        byte* storage = stackalloc byte[256];
        byte* belowMinGap = storage + 32;
        byte* firstGap = storage + 96;
        byte* secondGap = storage + 160;
        heap_segment segment = default;

        allocator.free_list_slot(belowMinGap) = (byte*)1;
        segment.thread_free_obj(belowMinGap, belowMinFreeList);

        Assert.Equal((nuint)0, (nuint)segment.free_list_head);
        Assert.Equal((nuint)0, (nuint)segment.free_list_tail);
        Assert.Equal((nuint)0, segment.free_list_size);
        Assert.Equal(belowMinFreeList, segment.free_obj_size);
        Assert.Equal((nuint)1, (nuint)allocator.free_list_slot(belowMinGap));

        allocator.free_list_slot(firstGap) = (byte*)1;
        segment.thread_free_obj(firstGap, minFreeList);

        Assert.Equal((nuint)firstGap, (nuint)segment.free_list_head);
        Assert.Equal((nuint)firstGap, (nuint)segment.free_list_tail);
        Assert.Equal(minFreeList, segment.free_list_size);
        Assert.Equal(belowMinFreeList, segment.free_obj_size);
        Assert.Equal((nuint)0, (nuint)allocator.free_list_slot(firstGap));

        allocator.free_list_slot(secondGap) = (byte*)1;
        segment.thread_free_obj(secondGap, secondSize);

        Assert.Equal((nuint)firstGap, (nuint)segment.free_list_head);
        Assert.Equal((nuint)secondGap, (nuint)segment.free_list_tail);
        Assert.Equal(unchecked(minFreeList + secondSize), segment.free_list_size);
        Assert.Equal(belowMinFreeList, segment.free_obj_size);
        Assert.Equal((nuint)secondGap, (nuint)allocator.free_list_slot(firstGap));
        Assert.Equal((nuint)0, (nuint)allocator.free_list_slot(secondGap));
    }

    [Fact]
    public void RegionHelpersPreserveHeaderSkewedSizeArithmetic()
    {
        heap_segment region = default;
        region.mem = (byte*)0x2000;
        region.committed = (byte*)0x2A00;
        region.reserved = (byte*)0x3000;

        byte* expectedStart = region.mem - sizeof(aligned_plug_and_gap);

        Assert.Equal((nuint)expectedStart, (nuint)gc_heap.get_region_start(&region));
        Assert.Equal((nuint)(region.reserved - expectedStart), gc_heap.get_region_size(&region));
        Assert.Equal((nuint)(region.committed - expectedStart), gc_heap.get_region_committed_size(&region));
    }

    [Fact]
    public void RegionFreeListAddAndUnlinkFrontPreserveNativeBookkeeping()
    {
        region_free_list list = default;
        region_free_list* pList = &list;
        heap_segment first = default;
        heap_segment second = default;

        InitializeRegion(&first, 0x1000, 0x1900, 0x2000, age: 3);
        InitializeRegion(&second, 0x3000, 0x3700, 0x4000, age: 7);

        region_free_list.add_region_front(pList, &first);
        region_free_list.add_region_front(pList, &second);

        Assert.Equal((nuint)2, region_free_list.get_num_free_regions(pList));
        Assert.Equal((nuint)(&second), (nuint)list.get_first_free_region());
        Assert.Equal((nuint)(&first), (nuint)heap_segment.heap_segment_next(&second));
        Assert.Equal((nuint)(&second), (nuint)heap_segment.heap_segment_prev_free_region(&first));
        Assert.Equal((nuint)pList, (nuint)heap_segment.heap_segment_containing_free_list(&first));
        Assert.Equal((nuint)pList, (nuint)heap_segment.heap_segment_containing_free_list(&second));

        nuint expectedSize = gc_heap.get_region_size(&first) + gc_heap.get_region_size(&second);
        nuint expectedCommitted = gc_heap.get_region_committed_size(&first) + gc_heap.get_region_committed_size(&second);
        Assert.Equal(expectedSize, list.get_size_free_regions());
        Assert.Equal(expectedCommitted, list.get_size_committed_in_free());

        heap_segment* unlinked = region_free_list.unlink_region_front(pList);
        Assert.Equal((nuint)(&second), (nuint)unlinked);
        Assert.Equal((nuint)1, region_free_list.get_num_free_regions(pList));
        Assert.Equal((nuint)(&first), (nuint)list.get_first_free_region());
        Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_containing_free_list(unlinked));
        Assert.Equal(gc_heap.get_region_size(&first), list.get_size_free_regions());
        Assert.Equal(gc_heap.get_region_committed_size(&first), list.get_size_committed_in_free());
    }

    [Fact]
    public void RegionFreeListSortUsesCommittedSizeThenAge()
    {
        region_free_list list = default;
        heap_segment highCommitted = default;
        heap_segment youngerMid = default;
        heap_segment olderMid = default;

        InitializeRegion(&highCommitted, 0x1000, 0x1C00, 0x2600, age: 4);
        InitializeRegion(&youngerMid, 0x3000, 0x3800, 0x4200, age: 1);
        InitializeRegion(&olderMid, 0x5000, 0x5800, 0x6200, age: 9);

        region_free_list* pList = &list;
        region_free_list.add_region_front(pList, &youngerMid);
        region_free_list.add_region_front(pList, &highCommitted);
        region_free_list.add_region_front(pList, &olderMid);

        heap_segment.heap_segment_age_in_free(&highCommitted) = 4;
        heap_segment.heap_segment_age_in_free(&youngerMid) = 1;
        heap_segment.heap_segment_age_in_free(&olderMid) = 9;

        list.sort_by_committed_and_age();

        heap_segment* first = list.get_first_free_region();
        heap_segment* second = heap_segment.heap_segment_next(first);
        heap_segment* third = heap_segment.heap_segment_next(second);

        Assert.Equal((nuint)(&highCommitted), (nuint)first);
        Assert.Equal((nuint)(&youngerMid), (nuint)second);
        Assert.Equal((nuint)(&olderMid), (nuint)third);
        Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_prev_free_region(first));
        Assert.Equal((nuint)(&highCommitted), (nuint)heap_segment.heap_segment_prev_free_region(second));
        Assert.Equal((nuint)(&youngerMid), (nuint)heap_segment.heap_segment_prev_free_region(third));
    }

    [Fact]
    public void RegionFreeListDescendingInsertionOrdersCommittedSizesAndFullyCommittedFirst()
    {
        region_free_list list = default;
        region_free_list* pList = &list;
        heap_segment small = default;
        heap_segment large = default;
        heap_segment middle = default;
        heap_segment fullyCommitted = default;

        InitializeRegion(&small, 0x1000, 0x1400, 0x2000, age: 1);
        InitializeRegion(&large, 0x3000, 0x3C00, 0x5000, age: 2);
        InitializeRegion(&middle, 0x6000, 0x6800, 0x8000, age: 3);
        InitializeRegion(&fullyCommitted, 0x9000, 0xA000, 0xA000, age: 4);

        region_free_list.add_region_in_descending_order(pList, &small);
        region_free_list.add_region_in_descending_order(pList, &large);
        region_free_list.add_region_in_descending_order(pList, &middle);
        region_free_list.add_region_in_descending_order(pList, &fullyCommitted);

        heap_segment* first = list.get_first_free_region();
        heap_segment* second = heap_segment.heap_segment_next(first);
        heap_segment* third = heap_segment.heap_segment_next(second);
        heap_segment* fourth = heap_segment.heap_segment_next(third);

        Assert.Equal((nuint)(&fullyCommitted), (nuint)first);
        Assert.Equal((nuint)(&large), (nuint)second);
        Assert.Equal((nuint)(&middle), (nuint)third);
        Assert.Equal((nuint)(&small), (nuint)fourth);
        Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_prev_free_region(first));
        Assert.Equal((nuint)first, (nuint)heap_segment.heap_segment_prev_free_region(second));
        Assert.Equal((nuint)second, (nuint)heap_segment.heap_segment_prev_free_region(third));
        Assert.Equal((nuint)third, (nuint)heap_segment.heap_segment_prev_free_region(fourth));
        Assert.Equal(0, heap_segment.heap_segment_age_in_free(&small));
        Assert.Equal(0, heap_segment.heap_segment_age_in_free(&large));
        Assert.Equal(0, heap_segment.heap_segment_age_in_free(&middle));
        Assert.Equal(0, heap_segment.heap_segment_age_in_free(&fullyCommitted));
        Assert.Equal((nuint)4, region_free_list.get_num_free_regions(pList));
    }

    [Fact]
    public void GCSpinLockInitializeSetsNativeLockFreeSentinel()
    {
        GCSpinLock spinLock = default;

        GCSpinLock.initialize(&spinLock);

        Assert.Equal(GCSpinLock.lock_free, spinLock.@lock);
#if DEBUG
        Assert.Equal(-1, (nint)spinLock.holding_thread);
#endif
    }

    [Fact]
    public void RegionAllocatorSchemaExtendsThroughMapFieldsInNativeOrder()
    {
        static nuint AlignUp(nuint value, nuint alignment)
        {
            return unchecked((value + (alignment - 1)) & ~(alignment - 1));
        }

        nuint pointerSize = (nuint)sizeof(void*);
        nuint uintSize = (nuint)sizeof(uint);
        nuint nuintSize = (nuint)sizeof(nuint);
        nuint offset = 0;

        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("global_region_start"));
        offset += pointerSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("global_region_end"));
        offset += pointerSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("global_region_left_used"));
        offset += pointerSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("global_region_right_used"));
        offset += pointerSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("total_free_units"));
        offset += uintSize;
        offset = AlignUp(offset, nuintSize);
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("region_alignment"));
        offset += nuintSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("large_region_alignment"));
        offset += nuintSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("region_allocator_lock"));
        offset += (nuint)sizeof(GCSpinLock);
        offset = AlignUp(offset, pointerSize);
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("region_map_left_start"));
        offset += pointerSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("region_map_left_end"));
        offset += pointerSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("region_map_right_start"));
        offset += pointerSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("region_map_right_end"));
        offset += pointerSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("num_left_used_free_units"));
        offset += uintSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("num_right_used_free_units"));
        offset += uintSize;

        Assert.Equal(AlignUp(offset, pointerSize), (nuint)sizeof(region_allocator));
    }

    [Fact]
    public void RegionAllocatorMapAddressAndIndexHelpersPreserveNativeArithmetic()
    {
        region_allocator allocator = default;
        byte* allocatorBytes = (byte*)&allocator;

        byte* regionStart = (byte*)0x0010_0000;
        uint* mapStart = (uint*)0x0020_0000;
        nuint regionAlignment = 0x1000;

        *(byte**)(allocatorBytes + (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("global_region_start")) = regionStart;
        *(nuint*)(allocatorBytes + (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("region_alignment")) = regionAlignment;
        *(uint**)(allocatorBytes + (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("region_map_left_start")) = mapStart;

        uint* mapIndex = mapStart + 9;
        byte* address = allocator.region_address_of(mapIndex);
        Assert.Equal((nuint)0x0010_9000, (nuint)address);
        Assert.Equal((nuint)mapIndex, (nuint)allocator.region_map_index_of(address));

        byte* unalignedAddress = (byte*)0x0010_5ABC;
        Assert.Equal((nuint)(mapStart + 5), (nuint)allocator.region_map_index_of(unalignedAddress));
    }

    [Fact]
    public void RegionAllocatorAlignmentSliceComputesLargeRegionFactor()
    {
        gc_heap.global_region_allocator.initialize_alignment(0x1000);

        Assert.Equal(8, region_allocator.LARGE_REGION_FACTOR);
        Assert.Equal(unchecked((int)0x80000000), region_allocator.region_alloc_free_bit);
        Assert.Equal(1, (int)allocate_direction.allocate_forward);
        Assert.Equal(-1, (int)allocate_direction.allocate_backward);
        Assert.Equal((nuint)0x1000, gc_heap.global_region_allocator.get_region_alignment());
        Assert.Equal((nuint)(region_allocator.LARGE_REGION_FACTOR * 0x1000), gc_heap.global_region_allocator.get_large_region_alignment());
    }

    [Theory]
    [InlineData(0xA000ul, 0x0000ul, 0x0000ul, 0u)]
    [InlineData(0xA000ul, 0x3000ul, 0x0000ul, 30u)]
    [InlineData(0xA000ul, 0x0000ul, 0x2000ul, 20u)]
    [InlineData(0xA000ul, 0x3000ul, 0x2000ul, 50u)]
    [InlineData(0x3000ul, 0x1000ul, 0x0000ul, 33u)]
    public void RegionAllocatorVaMemoryLoadPreservesNativeArithmetic(ulong totalBytes, ulong leftUsedBytes, ulong rightUsedBytes, uint expectedLoad)
    {
        region_allocator allocator = default;
        byte* start = (byte*)0x0010_0000;
        byte* end = start + (nint)totalBytes;

        WriteRegionAllocatorPointerField(&allocator, "global_region_start", start);
        WriteRegionAllocatorPointerField(&allocator, "global_region_end", end);
        WriteRegionAllocatorPointerField(&allocator, "global_region_left_used", start + (nint)leftUsedBytes);
        WriteRegionAllocatorPointerField(&allocator, "global_region_right_used", end - (nint)rightUsedBytes);

        Assert.Equal(expectedLoad, allocator.get_va_memory_load());
    }

    [Fact]
    public void RegionAllocatorGetFreePreservesNativeTargetWidthProduct()
    {
        region_allocator allocator = default;

        WriteRegionAllocatorField(&allocator, "total_free_units", 5u);
        WriteRegionAllocatorField(&allocator, "region_alignment", (nuint)0x1000);
        Assert.Equal((nuint)0x5000, allocator.get_free());

#if TARGET_64BIT
        nuint overflowAlignment = ((nuint)1 << 32) + 3;
#else
        nuint overflowAlignment = 0x1001;
#endif
        WriteRegionAllocatorField(&allocator, "total_free_units", uint.MaxValue);
        WriteRegionAllocatorField(&allocator, "region_alignment", overflowAlignment);
        Assert.Equal(unchecked((nuint)uint.MaxValue * overflowAlignment), allocator.get_free());
    }

    [Fact]
    public void RegionAllocatorGetUsedRegionCountReturnsLeftMapCount()
    {
        region_allocator allocator = default;
        uint* map = (uint*)0x0020_0000;

        WriteRegionAllocatorPointerField(&allocator, "region_map_left_start", map);
        WriteRegionAllocatorPointerField(&allocator, "region_map_left_end", map + 5);
        WriteRegionAllocatorPointerField(&allocator, "region_map_right_start", map + 12);
        WriteRegionAllocatorPointerField(&allocator, "region_map_right_end", map + 12);

        Assert.Equal((nuint)5, allocator.get_used_region_count());
    }

    [Fact]
    public void RegionAllocatorUnsafePointerGettersReturnNativeFields()
    {
        region_allocator allocator = default;
        byte* start = (byte*)0x0012_3400;
        byte* leftUsed = (byte*)0x0056_7800;

        WriteRegionAllocatorPointerField(&allocator, "global_region_start", start);
        WriteRegionAllocatorPointerField(&allocator, "global_region_left_used", leftUsed);

        Assert.Equal((nuint)start, (nuint)allocator.get_start());
        Assert.Equal((nuint)leftUsed, (nuint)allocator.get_left_used_unsafe());
    }

    [Fact]
    public void RegionAllocatorInitializeConstructsEmbeddedSpinLock()
    {
        region_allocator allocator = default;

        allocator.initialize();

        int lockOffset = System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("region_allocator_lock").ToInt32();
        Assert.Equal(GCSpinLock.lock_free, *(int*)((byte*)&allocator + lockOffset));
    }

    [Fact]
    public void RegionAllocatorSpinLockAcquiresAndReleasesUncontended()
    {
        region_allocator allocator = default;
        allocator.initialize();

        allocator.enter_spin_lock();

        Assert.Equal(0, ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock").@lock);

        allocator.leave_spin_lock();

        Assert.Equal(GCSpinLock.lock_free, ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock").@lock);
    }

    [Fact]
    public void RegionAllocatorSpinLockWorkerWaitsUntilRelease()
    {
        region_allocator* allocator = (region_allocator*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)sizeof(region_allocator));
        var workerReady = new ManualResetEventSlim(false);
        var workerAcquired = new ManualResetEventSlim(false);
        var workerCanLeave = new ManualResetEventSlim(false);
        Thread? worker = null;
        bool mainHoldsLock = false;

        try
        {
            allocator->initialize();
            allocator->enter_spin_lock();
            mainHoldsLock = true;

            nuint allocatorAddress = (nuint)allocator;
            worker = new Thread(() =>
            {
                region_allocator* workerAllocator = (region_allocator*)allocatorAddress;
                workerReady.Set();
                workerAllocator->enter_spin_lock();
                workerAcquired.Set();
                workerCanLeave.Wait();
                workerAllocator->leave_spin_lock();
            })
            {
                IsBackground = true,
            };

            worker.Start();
            Assert.True(workerReady.Wait(30000));
            Assert.False(workerAcquired.Wait(0));

            allocator->leave_spin_lock();
            mainHoldsLock = false;

            Assert.True(workerAcquired.Wait(30000));
            workerCanLeave.Set();
            Assert.True(worker.Join(30000));
            Assert.Equal(GCSpinLock.lock_free, ReadRegionAllocatorField<GCSpinLock>(allocator, "region_allocator_lock").@lock);
        }
        finally
        {
            if (mainHoldsLock)
            {
                allocator->leave_spin_lock();
            }

            workerCanLeave.Set();
            bool workerStopped = worker is null || worker.Join(30000);
            if (workerStopped)
            {
                workerReady.Dispose();
                workerAcquired.Dispose();
                workerCanLeave.Dispose();
                System.Runtime.InteropServices.NativeMemory.Free(allocator);
            }
        }
    }

    [Fact]
    public void RegionAllocatorSpinLockPreservesMutualExclusionUnderConcurrency()
    {
        const int ThreadCount = 4;
        const int IterationsPerThread = 2000;

        region_allocator* allocator = (region_allocator*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)sizeof(region_allocator));
        var start = new ManualResetEventSlim(false);
        Thread[] threads = new Thread[ThreadCount];
        int inCritical = 0;
        int protectedCounter = 0;
        int acquisitions = 0;
        int violations = 0;

        try
        {
            allocator->initialize();
            nuint allocatorAddress = (nuint)allocator;

            for (int threadIndex = 0; threadIndex < ThreadCount; threadIndex++)
            {
                threads[threadIndex] = new Thread(() =>
                {
                    region_allocator* workerAllocator = (region_allocator*)allocatorAddress;
                    start.Wait();

                    for (int iteration = 0; iteration < IterationsPerThread; iteration++)
                    {
                        workerAllocator->enter_spin_lock();

                        if (SysInterlocked.Increment(ref inCritical) != 1)
                        {
                            SysVolatile.Write(ref violations, 1);
                        }

                        int value = protectedCounter;
                        GCEnv.YieldProcessor();
                        protectedCounter = value + 1;

                        if (SysInterlocked.Decrement(ref inCritical) != 0)
                        {
                            SysVolatile.Write(ref violations, 1);
                        }

                        workerAllocator->leave_spin_lock();
                        SysInterlocked.Increment(ref acquisitions);
                    }
                })
                {
                    IsBackground = true,
                };
                threads[threadIndex].Start();
            }

            start.Set();

            foreach (Thread thread in threads)
            {
                Assert.True(thread.Join(30000));
            }

            Assert.Equal(0, SysVolatile.Read(ref violations));
            Assert.Equal(ThreadCount * IterationsPerThread, protectedCounter);
            Assert.Equal(ThreadCount * IterationsPerThread, acquisitions);
            Assert.Equal(GCSpinLock.lock_free, ReadRegionAllocatorField<GCSpinLock>(allocator, "region_allocator_lock").@lock);
        }
        finally
        {
            start.Set();
            bool allThreadsStopped = true;
            foreach (Thread? thread in threads)
            {
                allThreadsStopped &= thread is null || thread.Join(30000);
            }

            start.Dispose();
            if (allThreadsStopped)
            {
                System.Runtime.InteropServices.NativeMemory.Free(allocator);
            }
        }
    }

#if DEBUG
    [Fact]
    public void RegionAllocatorSpinLockRecordsCurrentThreadAndRestoresSentinelInDebug()
    {
        GCToEEInterface.Reset();
        GCToEEInterface.CurrentThread = (void*)0x12345678;
        region_allocator allocator = default;
        allocator.initialize();

        allocator.enter_spin_lock();

        GCSpinLock held = ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock");
        Assert.Equal(0, held.@lock);
        Assert.Equal((nuint)0x12345678, (nuint)held.holding_thread);
        Assert.Equal(1, GCToEEInterface.GetThreadCallCount);

        allocator.leave_spin_lock();

        GCSpinLock released = ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock");
        Assert.Equal(GCSpinLock.lock_free, released.@lock);
        Assert.Equal(nuint.MaxValue, (nuint)released.holding_thread);
    }
#endif

    [Fact]
    public void RegionAllocatorInitAlignsRangeAllocatesZeroedMapAndPreservesSpinLock()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        byte* lowest = (byte*)0x1111;
        byte* highest = (byte*)0x2222;
        WriteRegionAllocatorField(&allocator, "region_allocator_lock", new GCSpinLock { @lock = 1234 });

        Assert.True(allocator.init((byte*)0x1003, (byte*)0xAFFF, 0x1000, &lowest, &highest));

        uint* map = (uint*)ReadRegionAllocatorPointerField(&allocator, "region_map_left_start");
        try
        {
            Assert.Equal((nuint)0x2000, (nuint)lowest);
            Assert.Equal((nuint)0xA000, (nuint)highest);
            Assert.Equal((nuint)0x2000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_start"));
            Assert.Equal((nuint)0xA000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_end"));
            Assert.Equal((nuint)0x2000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)0xA000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
            Assert.Equal(8u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            Assert.Equal((nuint)0x1000, ReadRegionAllocatorField<nuint>(&allocator, "region_alignment"));
            Assert.Equal((nuint)0x8000, ReadRegionAllocatorField<nuint>(&allocator, "large_region_alignment"));
            Assert.Equal(1234, ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock").@lock);
            Assert.Equal((nuint)map, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
            Assert.Equal((nuint)(map + 8), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            Assert.Equal((nuint)(map + 8), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_end"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
            Assert.Equal(1, SyncImports.AllocCount);
            Assert.Equal((nuint)(8 * sizeof(uint)), SyncImports.LastAllocSize);

            for (int i = 0; i < 8; i++)
            {
                Assert.Equal(0u, map[i]);
            }

            Assert.Equal((nuint)0x5000, (nuint)allocator.region_address_of(map + 3));
            Assert.Equal((nuint)(map + 3), (nuint)allocator.region_map_index_of((byte*)0x5123));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorInitFailureDoesNotWriteOutputsOrMapPointers()
    {
        SyncImports.ResetRecording();
        SyncImports.FailNextAlloc = true;
        region_allocator allocator = default;
        byte* lowest = (byte*)0x1111;
        byte* highest = (byte*)0x2222;
        uint* oldMapLeftStart = (uint*)0x3333;
        uint* oldMapLeftEnd = (uint*)0x4444;
        uint* oldMapRightStart = (uint*)0x5555;
        uint* oldMapRightEnd = (uint*)0x6666;
        WriteRegionAllocatorField(&allocator, "region_allocator_lock", new GCSpinLock { @lock = 5678 });
        WriteRegionAllocatorPointerField(&allocator, "region_map_left_start", oldMapLeftStart);
        WriteRegionAllocatorPointerField(&allocator, "region_map_left_end", oldMapLeftEnd);
        WriteRegionAllocatorPointerField(&allocator, "region_map_right_start", oldMapRightStart);
        WriteRegionAllocatorPointerField(&allocator, "region_map_right_end", oldMapRightEnd);

        Assert.False(allocator.init((byte*)0x1003, (byte*)0xAFFF, 0x1000, &lowest, &highest));

        Assert.Equal((nuint)0x1111, (nuint)lowest);
        Assert.Equal((nuint)0x2222, (nuint)highest);
        Assert.Equal((nuint)oldMapLeftStart, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_start"));
        Assert.Equal((nuint)oldMapLeftEnd, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
        Assert.Equal((nuint)oldMapRightStart, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
        Assert.Equal((nuint)oldMapRightEnd, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_end"));
        Assert.Equal(5678, ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock").@lock);
        Assert.Equal((nuint)0x2000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_start"));
        Assert.Equal((nuint)0xA000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_end"));
        Assert.Equal(8u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
        Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
        Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
        Assert.Equal(1, SyncImports.AllocCount);
        Assert.Equal((nuint)(8 * sizeof(uint)), SyncImports.LastAllocSize);
    }

    [Fact]
    public void RegionAllocatorInitMapByteOverflowFailsBeforeAllocation()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        byte* lowest = (byte*)0x1111;
        byte* highest = (byte*)0x2222;
        uint* oldMapLeftStart = (uint*)0x3333;
        WriteRegionAllocatorPointerField(&allocator, "region_map_left_start", oldMapLeftStart);

        Assert.False(allocator.init((byte*)0, (byte*)nuint.MaxValue, 1, &lowest, &highest));

        Assert.Equal((nuint)0x1111, (nuint)lowest);
        Assert.Equal((nuint)0x2222, (nuint)highest);
        Assert.Equal((nuint)oldMapLeftStart, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_start"));
        Assert.Equal(uint.MaxValue, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
        Assert.Equal(0, SyncImports.AllocCount);
        Assert.Equal((nuint)0, SyncImports.LastAllocSize);
    }

    [Fact]
    public void RegionAllocatorInitReinitializationReplacesReservationState()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        allocator.initialize();
        uint* firstMap = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);
        uint* secondMap = null;

        try
        {
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(2, allocate_direction.allocate_forward));

            byte* lowest = null;
            byte* highest = null;
            Assert.True(allocator.init((byte*)0x2003, (byte*)0xEFFF, 0x1000, &lowest, &highest));
            secondMap = (uint*)ReadRegionAllocatorPointerField(&allocator, "region_map_left_start");

            Assert.Equal((nuint)0x3000, (nuint)lowest);
            Assert.Equal((nuint)0xE000, (nuint)highest);
            Assert.NotEqual((nuint)firstMap, (nuint)secondMap);
            Assert.Equal((nuint)0x3000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_start"));
            Assert.Equal((nuint)0xE000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_end"));
            Assert.Equal((nuint)0x3000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)0xE000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
            Assert.Equal(11u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            Assert.Equal((nuint)secondMap, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
            Assert.Equal((nuint)(secondMap + 11), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            Assert.Equal((nuint)(secondMap + 11), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_end"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
            Assert.Equal(GCSpinLock.lock_free, ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock").@lock);

            for (int i = 0; i < 11; i++)
            {
                Assert.Equal(0u, secondMap[i]);
            }
        }
        finally
        {
            SyncImports.ManagedGC_Free(firstMap);
            if (secondMap is not null)
            {
                SyncImports.ManagedGC_Free(secondMap);
            }
        }
    }

    [Fact]
    public void InitialRegionReservationPreservesNativeLayoutAndAllocatorBoundaries()
    {
        const nuint RegionSize = 0x1000;
        region_allocator oldAllocator = gc_heap.global_region_allocator;
        byte** oldInitialRegions = gc_heap.initial_regions;
        byte* oldBookkeepingCoverage = gc_heap.bookkeeping_covered_committed;
        uint* map = null;
        byte** initialRegions = null;

        try
        {
            SyncImports.ResetRecording();
            region_allocator allocator = default;
            allocator.initialize();
            map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x29000, RegionSize);
            gc_heap.global_region_allocator = allocator;
            gc_heap.initial_regions = null;
            gc_heap.bookkeeping_covered_committed = (byte*)0x7654_0000;

            Assert.True(gc_heap.allocate_initial_regions(1));
            initialRegions = gc_heap.initial_regions;

            Assert.Equal(2, SyncImports.AllocCount);
            Assert.Equal((nuint)(2 * (int)gc_generation_num.total_generation_count * sizeof(byte*)), SyncImports.LastAllocSize);
            AssertInitialRegion((int)gc_generation_num.poh_generation, (byte*)0x1000, (byte*)0x9000);
            AssertInitialRegion((int)gc_generation_num.soh_gen2, (byte*)0x9000, (byte*)0xA000);
            AssertInitialRegion((int)gc_generation_num.soh_gen1, (byte*)0xA000, (byte*)0xB000);
            AssertInitialRegion((int)gc_generation_num.soh_gen0, (byte*)0xB000, (byte*)0xC000);
            AssertInitialRegion((int)gc_generation_num.loh_generation, (byte*)0xC000, (byte*)0x14000);
            Assert.Equal((nuint)0x14000, (nuint)gc_heap.global_region_allocator.get_left_used_unsafe());
            region_allocator current = gc_heap.global_region_allocator;
            Assert.Equal((nuint)0x29000, (nuint)ReadRegionAllocatorPointerField(&current, "global_region_right_used"));
            Assert.Equal((nuint)0x7654_0000, (nuint)gc_heap.bookkeeping_covered_committed);
            Assert.Equal(8u, map[0]);
            Assert.Equal(8u, map[7]);
            Assert.Equal(1u, map[8]);
            Assert.Equal(1u, map[9]);
            Assert.Equal(1u, map[10]);
            Assert.Equal(8u, map[11]);
            Assert.Equal(8u, map[18]);

            byte* forwardStart = null;
            byte* forwardEnd = null;
            Assert.True(gc_heap.global_region_allocator.allocate_region(
                (int)gc_generation_num.soh_gen0,
                RegionSize,
                &forwardStart,
                &forwardEnd,
                allocate_direction.allocate_forward,
                null));
            Assert.Equal((nuint)0x14000, (nuint)forwardStart);
            Assert.Equal((nuint)0x15000, (nuint)forwardEnd);

            byte* backwardStart = null;
            byte* backwardEnd = null;
            Assert.True(gc_heap.global_region_allocator.allocate_region(
                (int)gc_generation_num.soh_gen0,
                RegionSize,
                &backwardStart,
                &backwardEnd,
                allocate_direction.allocate_backward,
                null));
            Assert.Equal((nuint)0x28000, (nuint)backwardStart);
            Assert.Equal((nuint)0x29000, (nuint)backwardEnd);
            Assert.Equal((nuint)0x15000, (nuint)gc_heap.global_region_allocator.get_left_used_unsafe());
            current = gc_heap.global_region_allocator;
            Assert.Equal((nuint)0x28000, (nuint)ReadRegionAllocatorPointerField(&current, "global_region_right_used"));
            Assert.Equal(1u, map[19]);
            Assert.Equal(1u, map[39]);
            Assert.Equal((nuint)19 * RegionSize, gc_heap.global_region_allocator.get_free());
        }
        finally
        {
            if (initialRegions is not null)
            {
                SyncImports.ManagedGC_Free(initialRegions);
            }

            gc_heap.initial_regions = oldInitialRegions;
            gc_heap.global_region_allocator = oldAllocator;
            gc_heap.bookkeeping_covered_committed = oldBookkeepingCoverage;
            if (map is not null)
            {
                SyncImports.ManagedGC_Free(map);
            }
        }
    }

    [Fact]
    public void InitialRegionReservationFailureDoesNotAllocateOrMutateAllocatorState()
    {
        region_allocator oldAllocator = gc_heap.global_region_allocator;
        byte** oldInitialRegions = gc_heap.initial_regions;
        byte* oldBookkeepingCoverage = gc_heap.bookkeeping_covered_committed;
        uint* map = null;

        try
        {
            SyncImports.ResetRecording();
            region_allocator allocator = default;
            allocator.initialize();
            map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x29000, 0x1000);
            gc_heap.global_region_allocator = allocator;
            gc_heap.initial_regions = (byte**)0x1234;
            gc_heap.bookkeeping_covered_committed = (byte*)0x7654_0000;
            region_allocator current = gc_heap.global_region_allocator;
            RegionAllocatorSnapshot expected = CaptureRegionAllocatorSnapshot(&current);

            SyncImports.FailNextAlloc = true;
            Assert.False(gc_heap.allocate_initial_regions(1));

            current = gc_heap.global_region_allocator;
            AssertRegionAllocatorSnapshotEqual(expected, &current);
            Assert.Equal((nuint)0, (nuint)gc_heap.initial_regions);
            Assert.Equal((nuint)0x7654_0000, (nuint)gc_heap.bookkeeping_covered_committed);
            Assert.Equal(2, SyncImports.AllocCount);
            Assert.Equal((nuint)(2 * (int)gc_generation_num.total_generation_count * sizeof(byte*)), SyncImports.LastAllocSize);
        }
        finally
        {
            gc_heap.initial_regions = oldInitialRegions;
            gc_heap.global_region_allocator = oldAllocator;
            gc_heap.bookkeeping_covered_committed = oldBookkeepingCoverage;
            if (map is not null)
            {
                SyncImports.ManagedGC_Free(map);
            }
        }
    }

    private static void AssertInitialRegion(int gen, byte* expectedStart, byte* expectedEnd)
    {
        byte* start = null;
        byte* end = null;
        gc_heap.get_initial_region(gen, 0, &start, &end);

        Assert.Equal((nuint)expectedStart, (nuint)start);
        Assert.Equal((nuint)expectedEnd, (nuint)end);
    }

    [Fact]
    public void RegionAllocatorAlignmentHelpersMatchNativeBitMath()
    {
        gc_heap.global_region_allocator.initialize_alignment(0x1000);

        Assert.Equal((nuint)0x1000, gc_heap.global_region_allocator.align_region_up(0x1));
        Assert.Equal((nuint)0x2000, gc_heap.global_region_allocator.align_region_up(0x1001));
        Assert.Equal((nuint)0x2000, gc_heap.global_region_allocator.align_region_up(0x2000));
        Assert.Equal((nuint)0, gc_heap.global_region_allocator.align_region_up(nuint.MaxValue));
        Assert.Equal((nuint)0x0000, gc_heap.global_region_allocator.align_region_down(0x001));
        Assert.Equal((nuint)0x1000, gc_heap.global_region_allocator.align_region_down(0x1ABC));
        Assert.Equal((nuint)0x2000, gc_heap.global_region_allocator.align_region_down(0x2000));
        Assert.Equal((nuint)1, gc_heap.global_region_allocator.is_region_aligned((byte*)0x3000));
        Assert.Equal((nuint)0, gc_heap.global_region_allocator.is_region_aligned((byte*)0x3001));
    }

    [Theory]
    [InlineData(0x80000001u, true, 1u)]
    [InlineData(0x00000001u, false, 1u)]
    [InlineData(0x80000000u, true, 0u)]
    [InlineData(0x7fffffffu, false, 0x7fffffffu)]
    public void RegionAllocatorUnitDecodePreservesFreeBitEncoding(uint encoded, bool expectedFree, uint expectedUnits)
    {
        Assert.Equal(expectedFree, region_allocator.is_unit_memory_free(encoded));
        Assert.Equal(expectedUnits, region_allocator.get_num_units(encoded));
    }

    [Fact]
    public void RegionAllocatorBusyAndFreeBlocksEncodeEndpoints()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            allocator.make_busy_block(map + 1, 3);

            Assert.Equal(0u, map[0]);
            Assert.Equal(3u, map[1]);
            Assert.Equal(0u, map[2]);
            Assert.Equal(3u, map[3]);

            allocator.make_free_block(map + 4, 2);
            uint encodedFreeBlock = unchecked((uint)region_allocator.region_alloc_free_bit) | 2u;

            Assert.Equal(encodedFreeBlock, map[4]);
            Assert.Equal(encodedFreeBlock, map[5]);
            Assert.True(region_allocator.is_unit_memory_free(map[4]));
            Assert.Equal(2u, region_allocator.get_num_units(map[4]));
            Assert.Equal(0u, map[6]);

            allocator.make_busy_block(map + 7, 1);
            Assert.Equal(1u, map[7]);

            allocator.make_free_block(map, 1);
            Assert.Equal(unchecked((uint)region_allocator.region_alloc_free_bit) | 1u, map[0]);
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateEndForwardMarksBusyBlockAndAdvancesLeftEnd()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            byte* allocation = allocator.allocate_end(2, allocate_direction.allocate_forward);

            Assert.Equal((nuint)0x1000, (nuint)allocation);
            Assert.Equal(2u, map[0]);
            Assert.Equal(2u, map[1]);
            Assert.Equal((nuint)0x3000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)0x9000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
            Assert.Equal((nuint)(map + 2), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
            Assert.Equal((nuint)(map + 8), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            Assert.Equal(8u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateEndBackwardMarksBusyBlockAndRetreatsRightEnd()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            byte* allocation = allocator.allocate_end(2, allocate_direction.allocate_backward);

            Assert.Equal((nuint)0x7000, (nuint)allocation);
            Assert.Equal(2u, map[6]);
            Assert.Equal(2u, map[7]);
            Assert.Equal((nuint)0x1000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)0x7000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
            Assert.Equal((nuint)map, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
            Assert.Equal((nuint)(map + 6), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            Assert.Equal((nuint)(map + 8), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_end"));
            Assert.Equal(8u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateEndInsufficientSpaceFailsWithoutMutation()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x5000, 0x1000);

        try
        {
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(3, allocate_direction.allocate_forward));
            RegionAllocatorSnapshot before = CaptureRegionAllocatorSnapshot(&allocator);
            uint* mapBefore = stackalloc uint[4];
            for (int i = 0; i < 4; i++)
            {
                mapBefore[i] = map[i];
            }

            byte* allocation = allocator.allocate_end(2, allocate_direction.allocate_backward);

            Assert.Equal((nuint)0, (nuint)allocation);
            AssertRegionAllocatorSnapshotEqual(before, &allocator);
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(mapBefore[i], map[i]);
            }
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Theory]
    [InlineData((int)allocate_direction.allocate_forward)]
    [InlineData((int)allocate_direction.allocate_backward)]
    public void RegionAllocatorAllocateEndExactFitConsumesBoundaryAndStops(int directionValue)
    {
        SyncImports.ResetRecording();
        allocate_direction direction = (allocate_direction)directionValue;
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x10000, 0x14000, 0x1000);

        try
        {
            byte* allocation = allocator.allocate_end(4, direction);

            Assert.Equal((nuint)0x10000, (nuint)allocation);
            Assert.Equal(4u, map[0]);
            Assert.Equal(4u, map[3]);

            if (direction == allocate_direction.allocate_forward)
            {
                Assert.Equal((nuint)0x14000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
                Assert.Equal((nuint)0x14000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
                Assert.Equal((nuint)(map + 4), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
                Assert.Equal((nuint)(map + 4), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            }
            else
            {
                Assert.Equal((nuint)0x10000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
                Assert.Equal((nuint)0x10000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
                Assert.Equal((nuint)map, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
                Assert.Equal((nuint)map, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            }

            RegionAllocatorSnapshot exactFit = CaptureRegionAllocatorSnapshot(&allocator);
            Assert.Equal((nuint)0, (nuint)allocator.allocate_end(1, direction));
            AssertRegionAllocatorSnapshotEqual(exactFit, &allocator);
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorBlockAndEndAllocationPreserveFreeUnitCounters()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            WriteRegionAllocatorField(&allocator, "total_free_units", 123u);
            WriteRegionAllocatorField(&allocator, "num_left_used_free_units", 45u);
            WriteRegionAllocatorField(&allocator, "num_right_used_free_units", 67u);

            allocator.make_free_block(map + 2, 2);
            allocator.make_busy_block(map + 4, 2);
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));

            Assert.Equal(123u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            Assert.Equal(45u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(67u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Theory]
    [InlineData((int)allocate_direction.allocate_forward)]
    [InlineData((int)allocate_direction.allocate_backward)]
    public void RegionAllocatorAllocateReusesExactFreeBlockInDirection(int directionValue)
    {
        SyncImports.ResetRecording();
        allocate_direction direction = (allocate_direction)directionValue;
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0xB000, 0x1000);

        try
        {
            allocator.initialize();
            if (direction == allocate_direction.allocate_forward)
            {
                Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(2, direction));
                Assert.Equal((nuint)0x3000, (nuint)allocator.allocate_end(2, direction));
                Assert.Equal((nuint)0x5000, (nuint)allocator.allocate_end(1, direction));
                WriteRegionAllocatorField(&allocator, "total_free_units", 5u);

                allocator.delete_region((byte*)0x3000);

                Assert.Equal((nuint)0x3000, (nuint)allocator.allocate(2, direction, null));
                Assert.Equal(2u, map[2]);
                Assert.Equal(2u, map[3]);
                Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
                Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
                Assert.Equal(5u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
                Assert.Equal((nuint)0x6000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
                Assert.Equal((nuint)(map + 5), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
            }
            else
            {
                Assert.Equal((nuint)0x9000, (nuint)allocator.allocate_end(2, direction));
                Assert.Equal((nuint)0x7000, (nuint)allocator.allocate_end(2, direction));
                Assert.Equal((nuint)0x6000, (nuint)allocator.allocate_end(1, direction));
                WriteRegionAllocatorField(&allocator, "total_free_units", 5u);

                allocator.delete_region((byte*)0x7000);

                Assert.Equal((nuint)0x7000, (nuint)allocator.allocate(2, direction, null));
                Assert.Equal(2u, map[6]);
                Assert.Equal(2u, map[7]);
                Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
                Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
                Assert.Equal(5u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
                Assert.Equal((nuint)0x6000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
                Assert.Equal((nuint)(map + 5), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            }

            Assert.Equal(GCSpinLock.lock_free, ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock").@lock);
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Theory]
    [InlineData((int)allocate_direction.allocate_forward)]
    [InlineData((int)allocate_direction.allocate_backward)]
    public void RegionAllocatorAllocateSplitsOversizedFreeBlockInDirection(int directionValue)
    {
        SyncImports.ResetRecording();
        allocate_direction direction = (allocate_direction)directionValue;
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0xC000, 0x1000);

        try
        {
            allocator.initialize();
            if (direction == allocate_direction.allocate_forward)
            {
                Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(1, direction));
                Assert.Equal((nuint)0x2000, (nuint)allocator.allocate_end(3, direction));
                Assert.Equal((nuint)0x5000, (nuint)allocator.allocate_end(1, direction));
                WriteRegionAllocatorField(&allocator, "total_free_units", 6u);

                allocator.delete_region((byte*)0x2000);

                Assert.Equal((nuint)0x2000, (nuint)allocator.allocate(2, direction, null));
                Assert.Equal(1u, map[0]);
                Assert.Equal(2u, map[1]);
                Assert.Equal(2u, map[2]);
                Assert.Equal(EncodedFreeRegionBlock(1), map[3]);
                Assert.Equal(1u, map[4]);
                Assert.Equal(1u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
                Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
                Assert.Equal(7u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            }
            else
            {
                Assert.Equal((nuint)0xB000, (nuint)allocator.allocate_end(1, direction));
                Assert.Equal((nuint)0x8000, (nuint)allocator.allocate_end(3, direction));
                Assert.Equal((nuint)0x7000, (nuint)allocator.allocate_end(1, direction));
                WriteRegionAllocatorField(&allocator, "total_free_units", 6u);

                allocator.delete_region((byte*)0x8000);

                Assert.Equal((nuint)0x9000, (nuint)allocator.allocate(2, direction, null));
                Assert.Equal(1u, map[6]);
                Assert.Equal(EncodedFreeRegionBlock(1), map[7]);
                Assert.Equal(2u, map[8]);
                Assert.Equal(2u, map[9]);
                Assert.Equal(1u, map[10]);
                Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
                Assert.Equal(1u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
                Assert.Equal(7u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            }
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateSkipsBusyBlocksBeforeReusableFreeBlock()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            allocator.initialize();
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x2000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x3000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x4000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            WriteRegionAllocatorField(&allocator, "total_free_units", 4u);

            allocator.delete_region((byte*)0x3000);

            Assert.Equal((nuint)0x3000, (nuint)allocator.allocate(1, allocate_direction.allocate_forward, null));
            Assert.Equal(1u, map[0]);
            Assert.Equal(1u, map[1]);
            Assert.Equal(1u, map[2]);
            Assert.Equal(1u, map[3]);
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(4u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateFastGateFallsBackToEndWhenFreeCounterTooSmall()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            allocator.initialize();
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(2, allocate_direction.allocate_forward));
            allocator.make_free_block(map, 2);
            WriteRegionAllocatorField(&allocator, "total_free_units", 6u);
            WriteRegionAllocatorField(&allocator, "num_left_used_free_units", 0u);

            Assert.Equal((nuint)0x3000, (nuint)allocator.allocate(1, allocate_direction.allocate_forward, null));

            Assert.Equal(EncodedFreeRegionBlock(2), map[0]);
            Assert.Equal(EncodedFreeRegionBlock(2), map[1]);
            Assert.Equal(1u, map[2]);
            Assert.Equal((nuint)0x4000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)(map + 3), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(5u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateNullCallbackAllocatesAtEndWithoutInvocation()
    {
        SyncImports.ResetRecording();
        ResetRegionAllocatorCallbackRecorder();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            allocator.initialize();

            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate(2, allocate_direction.allocate_forward, null));

            Assert.Equal(0, s_regionAllocatorCallbackCount);
            Assert.Equal(2u, map[0]);
            Assert.Equal(2u, map[1]);
            Assert.Equal((nuint)0x3000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)(map + 2), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
            Assert.Equal(6u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateCallbackSuccessReceivesGlobalLeftUsed()
    {
        SyncImports.ResetRecording();
        ResetRegionAllocatorCallbackRecorder();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            allocator.initialize();
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            WriteRegionAllocatorField(&allocator, "total_free_units", 7u);

            Assert.Equal((nuint)0x7000, (nuint)allocator.allocate(2, allocate_direction.allocate_backward, &RegionAllocatorCallbackSuccess));

            Assert.Equal(1, s_regionAllocatorCallbackCount);
            Assert.Equal((nuint)0x2000, s_regionAllocatorCallbackLastLeftUsed);
            Assert.Equal(2u, map[6]);
            Assert.Equal(2u, map[7]);
            Assert.Equal((nuint)0x2000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)0x7000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
            Assert.Equal(5u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateCallbackFailureRollsBackEndAllocation()
    {
        SyncImports.ResetRecording();
        ResetRegionAllocatorCallbackRecorder();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            allocator.initialize();
            RegionAllocatorSnapshot before = CaptureRegionAllocatorSnapshot(&allocator);

            Assert.Equal((nuint)0, (nuint)allocator.allocate(2, allocate_direction.allocate_forward, &RegionAllocatorCallbackFailure));

            Assert.Equal(1, s_regionAllocatorCallbackCount);
            Assert.Equal((nuint)0x3000, s_regionAllocatorCallbackLastLeftUsed);
            AssertRegionAllocatorSnapshotEqual(before, &allocator);
            Assert.Equal(2u, map[0]);
            Assert.Equal(2u, map[1]);
            Assert.Equal(GCSpinLock.lock_free, ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock").@lock);
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateBasicRegionUsesOneBasicUnitAndFiresSegmentEvent()
    {
        SyncImports.ResetRecording();
        ResetCreateSegmentEventRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            allocator.initialize();
            byte* start = (byte*)0x1111;
            byte* end = (byte*)0x2222;

            Assert.True(allocator.allocate_basic_region(
                (int)gc_generation_num.soh_gen2,
                &start,
                &end,
                &RegionAllocatorCallbackSuccess));

            Assert.Equal((nuint)0x1000, (nuint)start);
            Assert.Equal((nuint)0x2000, (nuint)end);
            Assert.Equal(1u, map[0]);
            Assert.Equal((nuint)0x2000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal(1, s_regionAllocatorCallbackCount);
            Assert.Equal((nuint)0x2000, s_regionAllocatorCallbackLastLeftUsed);
            AssertCreateSegmentEvent(
                (byte*)((nuint)0x1000 + (nuint)sizeof(aligned_plug_and_gap)),
                (nuint)0x1000 - (nuint)sizeof(aligned_plug_and_gap),
                gc_etw_segment_type.gc_etw_segment_small_object_heap);
        }
        finally
        {
            DisableCreateSegmentEvents();
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateLargeRegionUsesDefaultLargeSizeAndBackwardDirection()
    {
        SyncImports.ResetRecording();
        ResetCreateSegmentEventRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x19000, 0x1000);

        try
        {
            allocator.initialize();
            byte* start = null;
            byte* end = null;

            Assert.True(allocator.allocate_large_region(
                (int)gc_generation_num.loh_generation,
                &start,
                &end,
                allocate_direction.allocate_backward,
                0,
                null));

            Assert.Equal((nuint)0x11000, (nuint)start);
            Assert.Equal((nuint)0x19000, (nuint)end);
            Assert.Equal(8u, map[16]);
            Assert.Equal(8u, map[23]);
            Assert.Equal((nuint)0x11000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
            Assert.Equal((nuint)(map + 16), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            Assert.Equal(16u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            AssertCreateSegmentEvent(
                (byte*)((nuint)0x11000 + (nuint)sizeof(aligned_plug_and_gap)),
                (nuint)0x8000 - (nuint)sizeof(aligned_plug_and_gap),
                gc_etw_segment_type.gc_etw_segment_large_object_heap);
        }
        finally
        {
            DisableCreateSegmentEvents();
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateLargeRegionRoundsCustomSizeToLargeAlignment()
    {
        SyncImports.ResetRecording();
        ResetCreateSegmentEventRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x21000, 0x1000);

        try
        {
            allocator.initialize();
            byte* start = null;
            byte* end = null;

            Assert.True(allocator.allocate_large_region(
                (int)gc_generation_num.soh_gen0,
                &start,
                &end,
                allocate_direction.allocate_forward,
                0x9000,
                null));

            Assert.Equal((nuint)0x1000, (nuint)start);
            Assert.Equal((nuint)0x11000, (nuint)end);
            Assert.Equal(16u, map[0]);
            Assert.Equal(16u, map[15]);
            Assert.Equal((nuint)0x11000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)(map + 16), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
            Assert.Equal(16u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            AssertCreateSegmentEvent(
                (byte*)((nuint)0x1000 + (nuint)sizeof(aligned_plug_and_gap)),
                (nuint)0x10000 - (nuint)sizeof(aligned_plug_and_gap),
                gc_etw_segment_type.gc_etw_segment_small_object_heap);
        }
        finally
        {
            DisableCreateSegmentEvents();
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateRegionAlignsAllocationSizeButFiresRequestedSize()
    {
        SyncImports.ResetRecording();
        ResetCreateSegmentEventRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            allocator.initialize();
            byte* start = null;
            byte* end = null;

            Assert.True(allocator.allocate_region(
                (int)gc_generation_num.soh_gen1,
                0x1801,
                &start,
                &end,
                allocate_direction.allocate_forward,
                null));

            Assert.Equal((nuint)0x1000, (nuint)start);
            Assert.Equal((nuint)0x3000, (nuint)end);
            Assert.Equal(2u, map[0]);
            Assert.Equal(2u, map[1]);
            Assert.Equal(6u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            AssertCreateSegmentEvent(
                (byte*)((nuint)0x1000 + (nuint)sizeof(aligned_plug_and_gap)),
                (nuint)0x1801 - (nuint)sizeof(aligned_plug_and_gap),
                gc_etw_segment_type.gc_etw_segment_small_object_heap);
        }
        finally
        {
            DisableCreateSegmentEvents();
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Theory]
    [InlineData((int)gc_generation_num.soh_gen0, (int)gc_etw_segment_type.gc_etw_segment_small_object_heap)]
    [InlineData((int)gc_generation_num.loh_generation, (int)gc_etw_segment_type.gc_etw_segment_large_object_heap)]
    [InlineData((int)gc_generation_num.poh_generation, (int)gc_etw_segment_type.gc_etw_segment_pinned_object_heap)]
    public void RegionAllocatorAllocateRegionClassifiesGenerationSegmentTypes(int generation, int expectedSegmentType)
    {
        SyncImports.ResetRecording();
        ResetCreateSegmentEventRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x5000, 0x1000);

        try
        {
            allocator.initialize();
            byte* start = null;
            byte* end = null;

            Assert.True(allocator.allocate_region(generation, 0x1000, &start, &end, allocate_direction.allocate_forward, null));

            Assert.Equal((nuint)0x1000, (nuint)start);
            Assert.Equal((nuint)0x2000, (nuint)end);
            Assert.Equal((uint)expectedSegmentType, GCToEEInterface.LastGCCreateSegmentType);
        }
        finally
        {
            DisableCreateSegmentEvents();
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateRegionCallbackFailureWritesOutputsAndFiresFailedAllocationEvent()
    {
        SyncImports.ResetRecording();
        ResetCreateSegmentEventRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            allocator.initialize();
            RegionAllocatorSnapshot before = CaptureRegionAllocatorSnapshot(&allocator);
            byte* start = (byte*)0x1111;
            byte* end = (byte*)0x2222;

            Assert.False(allocator.allocate_region(
                (int)gc_generation_num.poh_generation,
                0x1000,
                &start,
                &end,
                allocate_direction.allocate_forward,
                &RegionAllocatorCallbackFailure));

            Assert.Equal((nuint)0, (nuint)start);
            Assert.Equal((nuint)0x1000, (nuint)end);
            Assert.Equal(1, s_regionAllocatorCallbackCount);
            Assert.Equal((nuint)0x2000, s_regionAllocatorCallbackLastLeftUsed);
            AssertRegionAllocatorSnapshotEqual(before, &allocator);
            AssertCreateSegmentEvent(
                (byte*)(nuint)sizeof(aligned_plug_and_gap),
                (nuint)0x1000 - (nuint)sizeof(aligned_plug_and_gap),
                gc_etw_segment_type.gc_etw_segment_pinned_object_heap);
        }
        finally
        {
            DisableCreateSegmentEvents();
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateRegionNoSpaceFailureStillWritesEndAndFiresEvent()
    {
        SyncImports.ResetRecording();
        ResetCreateSegmentEventRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x2000, 0x1000);

        try
        {
            allocator.initialize();
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            WriteRegionAllocatorField(&allocator, "total_free_units", 0u);
            RegionAllocatorSnapshot before = CaptureRegionAllocatorSnapshot(&allocator);
            byte* start = (byte*)0x1111;
            byte* end = (byte*)0x2222;

            Assert.False(allocator.allocate_region(
                (int)gc_generation_num.loh_generation,
                0x1000,
                &start,
                &end,
                allocate_direction.allocate_forward,
                &RegionAllocatorCallbackSuccess));

            Assert.Equal((nuint)0, (nuint)start);
            Assert.Equal((nuint)0x1000, (nuint)end);
            Assert.Equal(0, s_regionAllocatorCallbackCount);
            AssertRegionAllocatorSnapshotEqual(before, &allocator);
            AssertCreateSegmentEvent(
                (byte*)(nuint)sizeof(aligned_plug_and_gap),
                (nuint)0x1000 - (nuint)sizeof(aligned_plug_and_gap),
                gc_etw_segment_type.gc_etw_segment_large_object_heap);
        }
        finally
        {
            DisableCreateSegmentEvents();
            SyncImports.ManagedGC_Free(map);
        }
    }

#if !DEBUG
    [Fact]
    public void RegionAllocatorAllocateInvalidDirectionFallsBackToBackwardEndInRelease()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            allocator.initialize();

            Assert.Equal((nuint)0x7000, (nuint)allocator.allocate(2, (allocate_direction)1234, null));

            Assert.Equal(2u, map[6]);
            Assert.Equal(2u, map[7]);
            Assert.Equal((nuint)0x7000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
            Assert.Equal((nuint)(map + 6), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            Assert.Equal(6u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }
#endif

    [Fact]
    public void RegionAllocatorDeleteRegionWrapperLocksDeletesInteriorBusyBlockAndReleases()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0xB000, 0x1000);

        try
        {
            allocator.initialize();
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x2000, (nuint)allocator.allocate_end(2, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x4000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            WriteRegionAllocatorField(&allocator, "total_free_units", 6u);

            allocator.delete_region((byte*)0x2000);

            Assert.Equal(1u, map[0]);
            Assert.Equal(EncodedFreeRegionBlock(2), map[1]);
            Assert.Equal(EncodedFreeRegionBlock(2), map[2]);
            Assert.Equal(1u, map[3]);
            Assert.Equal((nuint)0x5000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)(map + 4), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
            Assert.Equal(2u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
            Assert.Equal(8u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            Assert.Equal(GCSpinLock.lock_free, ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock").@lock);
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorDeleteRegionCoalescesPreviousFreeBlock()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0xB000, 0x1000);

        try
        {
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x2000, (nuint)allocator.allocate_end(2, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x4000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x5000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            allocator.make_free_block(map + 1, 2);
            WriteRegionAllocatorField(&allocator, "num_left_used_free_units", 2u);
            WriteRegionAllocatorField(&allocator, "total_free_units", 7u);

            DeleteRegionImplUnderLock(&allocator, (byte*)0x4000);

            Assert.Equal(1u, map[0]);
            Assert.Equal(EncodedFreeRegionBlock(3), map[1]);
            Assert.Equal(EncodedFreeRegionBlock(2), map[2]);
            Assert.Equal(EncodedFreeRegionBlock(3), map[3]);
            Assert.Equal(1u, map[4]);
            Assert.Equal(3u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
            Assert.Equal(8u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            Assert.Equal((nuint)0x6000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)(map + 5), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorDeleteRegionCoalescesNextFreeBlock()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0xB000, 0x1000);

        try
        {
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x2000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x3000, (nuint)allocator.allocate_end(2, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x5000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            allocator.make_free_block(map + 2, 2);
            WriteRegionAllocatorField(&allocator, "num_left_used_free_units", 2u);
            WriteRegionAllocatorField(&allocator, "total_free_units", 7u);

            DeleteRegionImplUnderLock(&allocator, (byte*)0x2000);

            Assert.Equal(1u, map[0]);
            Assert.Equal(EncodedFreeRegionBlock(3), map[1]);
            Assert.Equal(EncodedFreeRegionBlock(2), map[2]);
            Assert.Equal(EncodedFreeRegionBlock(3), map[3]);
            Assert.Equal(1u, map[4]);
            Assert.Equal(3u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
            Assert.Equal(8u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            Assert.Equal((nuint)0x6000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)(map + 5), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorDeleteRegionCoalescesBothFreeNeighbors()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0xB000, 0x1000);

        try
        {
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x2000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x3000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x4000, (nuint)allocator.allocate_end(2, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x6000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            allocator.make_free_block(map + 1, 1);
            allocator.make_free_block(map + 3, 2);
            WriteRegionAllocatorField(&allocator, "num_left_used_free_units", 3u);
            WriteRegionAllocatorField(&allocator, "total_free_units", 7u);

            DeleteRegionImplUnderLock(&allocator, (byte*)0x3000);

            Assert.Equal(1u, map[0]);
            Assert.Equal(EncodedFreeRegionBlock(4), map[1]);
            Assert.Equal(1u, map[2]);
            Assert.Equal(EncodedFreeRegionBlock(2), map[3]);
            Assert.Equal(EncodedFreeRegionBlock(4), map[4]);
            Assert.Equal(1u, map[5]);
            Assert.Equal(4u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
            Assert.Equal(8u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            Assert.Equal((nuint)0x7000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)(map + 6), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorDeleteRegionContractsLeftEndAfterCoalescingPrevious()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0xB000, 0x1000);

        try
        {
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x2000, (nuint)allocator.allocate_end(2, allocate_direction.allocate_forward));
            allocator.make_free_block(map, 1);
            WriteRegionAllocatorField(&allocator, "num_left_used_free_units", 1u);
            WriteRegionAllocatorField(&allocator, "total_free_units", 8u);

            DeleteRegionImplUnderLock(&allocator, (byte*)0x2000);

            Assert.Equal(EncodedFreeRegionBlock(1), map[0]);
            Assert.Equal(2u, map[1]);
            Assert.Equal(2u, map[2]);
            Assert.Equal((nuint)0x1000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)map, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
            Assert.Equal(10u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorDeleteRegionContractsRightEndAfterCoalescingNext()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0xB000, 0x1000);

        try
        {
            Assert.Equal((nuint)0xA000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_backward));
            Assert.Equal((nuint)0x8000, (nuint)allocator.allocate_end(2, allocate_direction.allocate_backward));
            allocator.make_free_block(map + 9, 1);
            WriteRegionAllocatorField(&allocator, "num_right_used_free_units", 1u);
            WriteRegionAllocatorField(&allocator, "total_free_units", 8u);

            DeleteRegionImplUnderLock(&allocator, (byte*)0x8000);

            Assert.Equal(2u, map[7]);
            Assert.Equal(2u, map[8]);
            Assert.Equal(EncodedFreeRegionBlock(1), map[9]);
            Assert.Equal((nuint)0xB000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
            Assert.Equal((nuint)(map + 10), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
            Assert.Equal(10u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorDeleteRegionRoutesRightSideFreeCounters()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0xB000, 0x1000);

        try
        {
            Assert.Equal((nuint)0x9000, (nuint)allocator.allocate_end(2, allocate_direction.allocate_backward));
            Assert.Equal((nuint)0x7000, (nuint)allocator.allocate_end(2, allocate_direction.allocate_backward));
            WriteRegionAllocatorField(&allocator, "total_free_units", 6u);

            DeleteRegionImplUnderLock(&allocator, (byte*)0x9000);

            Assert.Equal(2u, map[6]);
            Assert.Equal(2u, map[7]);
            Assert.Equal(EncodedFreeRegionBlock(2), map[8]);
            Assert.Equal(EncodedFreeRegionBlock(2), map[9]);
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(2u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
            Assert.Equal(8u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            Assert.Equal((nuint)0x7000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
            Assert.Equal((nuint)(map + 6), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            Assert.Equal((nuint)(map + 10), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_end"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorMoveHighestFreeRegionsTraversesDescendingEndpoints()
    {
        const nuint Alignment = 0x1000;
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        region_allocator oldGlobalAllocator = gc_heap.global_region_allocator;
        seg_mapping* table = stackalloc seg_mapping[5];
        uint* map = stackalloc uint[4];
        region_free_list* source = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_free_list* destination = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_allocator allocator = default;

        try
        {
            InitializeRegionMoveGlobals(table, Alignment);
            InitializeRegionAllocatorForMove(&allocator, map, 3, Alignment, (byte*)0x1000);
            ClearRegionFreeLists(source);
            ClearRegionFreeLists(destination);
            allocator.make_busy_block(map, 1);
            allocator.make_busy_block(map + 1, 1);
            allocator.make_busy_block(map + 2, 1);

            heap_segment* lowest = InitializeMappedRegion(table, 0x1000, 1, Alignment);
            heap_segment* middle = InitializeMappedRegion(table, 0x2000, 1, Alignment);
            heap_segment* highest = InitializeMappedRegion(table, 0x3000, 1, Alignment);
            region_free_list.add_region(lowest, source);
            region_free_list.add_region(middle, source);
            region_free_list.add_region(highest, source);

            allocator.move_highest_free_regions(2, small_region_p: true, destination);

            region_free_list* destinationBasic = &destination[(int)free_region_kind.basic_free_region];
            Assert.Equal((nuint)2, region_free_list.get_num_free_regions(destinationBasic));
            Assert.Equal((nuint)middle, (nuint)destinationBasic->get_first_free_region());
            Assert.Equal((nuint)highest, (nuint)heap_segment.heap_segment_next(middle));
            Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_next(highest));
            Assert.True(region_free_list.is_on_free_list(lowest, source));
            Assert.True(region_free_list.is_on_free_list(middle, destination));
            Assert.True(region_free_list.is_on_free_list(highest, destination));
        }
        finally
        {
            RestoreRegionMoveGlobals(oldShift, oldTable, oldGlobalAllocator);
        }
    }

    [Fact]
    public void RegionAllocatorMoveHighestFreeRegionsFiltersBasicRegions()
    {
        const nuint Alignment = 0x1000;
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        region_allocator oldGlobalAllocator = gc_heap.global_region_allocator;
        seg_mapping* table = stackalloc seg_mapping[12];
        uint* map = stackalloc uint[10];
        region_free_list* source = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_free_list* destination = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_allocator allocator = default;

        try
        {
            InitializeRegionMoveGlobals(table, Alignment);
            InitializeRegionAllocatorForMove(&allocator, map, 10, Alignment, (byte*)0x1000);
            ClearRegionFreeLists(source);
            ClearRegionFreeLists(destination);
            allocator.make_busy_block(map, 1);
            allocator.make_busy_block(map + 1, 8);
            allocator.make_busy_block(map + 9, 1);

            heap_segment* lowBasic = InitializeMappedRegion(table, 0x1000, 1, Alignment);
            heap_segment* large = InitializeMappedRegion(table, 0x2000, 8, Alignment);
            heap_segment* highBasic = InitializeMappedRegion(table, 0xA000, 1, Alignment);
            region_free_list.add_region(lowBasic, source);
            region_free_list.add_region(large, source);
            region_free_list.add_region(highBasic, source);

            allocator.move_highest_free_regions(10, small_region_p: true, destination);

            Assert.Equal((nuint)2, region_free_list.get_num_free_regions(&destination[(int)free_region_kind.basic_free_region]));
            Assert.Equal((nuint)0, region_free_list.get_num_free_regions(&destination[(int)free_region_kind.large_free_region]));
            Assert.True(region_free_list.is_on_free_list(lowBasic, destination));
            Assert.True(region_free_list.is_on_free_list(highBasic, destination));
            Assert.True(region_free_list.is_on_free_list(large, source));
        }
        finally
        {
            RestoreRegionMoveGlobals(oldShift, oldTable, oldGlobalAllocator);
        }
    }

    [Fact]
    public void RegionAllocatorMoveHighestFreeRegionsFiltersLargeRegions()
    {
        const nuint Alignment = 0x1000;
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        region_allocator oldGlobalAllocator = gc_heap.global_region_allocator;
        seg_mapping* table = stackalloc seg_mapping[12];
        uint* map = stackalloc uint[10];
        region_free_list* source = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_free_list* destination = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_allocator allocator = default;

        try
        {
            InitializeRegionMoveGlobals(table, Alignment);
            InitializeRegionAllocatorForMove(&allocator, map, 10, Alignment, (byte*)0x1000);
            ClearRegionFreeLists(source);
            ClearRegionFreeLists(destination);
            allocator.make_busy_block(map, 1);
            allocator.make_busy_block(map + 1, 8);
            allocator.make_busy_block(map + 9, 1);

            heap_segment* lowBasic = InitializeMappedRegion(table, 0x1000, 1, Alignment);
            heap_segment* large = InitializeMappedRegion(table, 0x2000, 8, Alignment);
            heap_segment* highBasic = InitializeMappedRegion(table, 0xA000, 1, Alignment);
            region_free_list.add_region(lowBasic, source);
            region_free_list.add_region(large, source);
            region_free_list.add_region(highBasic, source);

            allocator.move_highest_free_regions(8, small_region_p: false, destination);

            Assert.Equal((nuint)0, region_free_list.get_num_free_regions(&destination[(int)free_region_kind.basic_free_region]));
            Assert.Equal((nuint)1, region_free_list.get_num_free_regions(&destination[(int)free_region_kind.large_free_region]));
            Assert.True(region_free_list.is_on_free_list(lowBasic, source));
            Assert.True(region_free_list.is_on_free_list(highBasic, source));
            Assert.True(region_free_list.is_on_free_list(large, destination));
        }
        finally
        {
            RestoreRegionMoveGlobals(oldShift, oldTable, oldGlobalAllocator);
        }
    }

    [Fact]
    public void RegionAllocatorMoveHighestFreeRegionsSkipsMapFreeAndAllocatedSegments()
    {
        const nuint Alignment = 0x1000;
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        region_allocator oldGlobalAllocator = gc_heap.global_region_allocator;
        seg_mapping* table = stackalloc seg_mapping[6];
        uint* map = stackalloc uint[4];
        region_free_list* source = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_free_list* destination = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_allocator allocator = default;

        try
        {
            InitializeRegionMoveGlobals(table, Alignment);
            InitializeRegionAllocatorForMove(&allocator, map, 4, Alignment, (byte*)0x1000);
            ClearRegionFreeLists(source);
            ClearRegionFreeLists(destination);
            allocator.make_busy_block(map, 1);
            allocator.make_free_block(map + 1, 1);
            allocator.make_busy_block(map + 2, 1);
            allocator.make_busy_block(map + 3, 1);

            heap_segment* lowBusy = InitializeMappedRegion(table, 0x1000, 1, Alignment);
            heap_segment* mapFree = InitializeMappedRegion(table, 0x2000, 1, Alignment);
            heap_segment* allocated = InitializeMappedRegion(table, 0x3000, 1, Alignment);
            heap_segment* highBusy = InitializeMappedRegion(table, 0x4000, 1, Alignment);
            heap_segment.heap_segment_allocated(allocated) = (byte*)0x3333;
            region_free_list.add_region(lowBusy, source);
            region_free_list.add_region(mapFree, source);
            region_free_list.add_region(highBusy, source);

            allocator.move_highest_free_regions(4, small_region_p: true, destination);

            Assert.Equal((nuint)2, region_free_list.get_num_free_regions(&destination[(int)free_region_kind.basic_free_region]));
            Assert.True(region_free_list.is_on_free_list(lowBusy, destination));
            Assert.True(region_free_list.is_on_free_list(highBusy, destination));
            Assert.True(region_free_list.is_on_free_list(mapFree, source));
            Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_containing_free_list(allocated));
        }
        finally
        {
            RestoreRegionMoveGlobals(oldShift, oldTable, oldGlobalAllocator);
        }
    }

    [Fact]
    public void RegionAllocatorMoveHighestFreeRegionsSkipsDestinationMembersAndUsesExactQuota()
    {
        const nuint Alignment = 0x1000;
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        region_allocator oldGlobalAllocator = gc_heap.global_region_allocator;
        seg_mapping* table = stackalloc seg_mapping[6];
        uint* map = stackalloc uint[4];
        region_free_list* source = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_free_list* destination = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_allocator allocator = default;

        try
        {
            InitializeRegionMoveGlobals(table, Alignment);
            InitializeRegionAllocatorForMove(&allocator, map, 4, Alignment, (byte*)0x1000);
            ClearRegionFreeLists(source);
            ClearRegionFreeLists(destination);
            allocator.make_busy_block(map, 1);
            allocator.make_busy_block(map + 1, 1);
            allocator.make_busy_block(map + 2, 1);
            allocator.make_busy_block(map + 3, 1);
            WriteRegionAllocatorField(&allocator, "region_allocator_lock", new GCSpinLock { @lock = 1234 });

            heap_segment* lowest = InitializeMappedRegion(table, 0x1000, 1, Alignment);
            heap_segment* movedLow = InitializeMappedRegion(table, 0x2000, 1, Alignment);
            heap_segment* movedHigh = InitializeMappedRegion(table, 0x3000, 1, Alignment);
            heap_segment* alreadyDestination = InitializeMappedRegion(table, 0x4000, 1, Alignment);
            region_free_list.add_region(lowest, source);
            region_free_list.add_region(movedLow, source);
            region_free_list.add_region(movedHigh, source);
            region_free_list.add_region(alreadyDestination, destination);

            allocator.move_highest_free_regions(2, small_region_p: true, destination);

            Assert.Equal((nuint)3, region_free_list.get_num_free_regions(&destination[(int)free_region_kind.basic_free_region]));
            Assert.True(region_free_list.is_on_free_list(alreadyDestination, destination));
            Assert.True(region_free_list.is_on_free_list(movedHigh, destination));
            Assert.True(region_free_list.is_on_free_list(movedLow, destination));
            Assert.True(region_free_list.is_on_free_list(lowest, source));
            Assert.Equal(1234, ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock").@lock);
        }
        finally
        {
            RestoreRegionMoveGlobals(oldShift, oldTable, oldGlobalAllocator);
        }
    }

    [Fact]
    public void RegionAllocatorMoveHighestFreeRegionsBreaksWithoutContinuingToLowerFit()
    {
        const nuint Alignment = 0x1000;
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        region_allocator oldGlobalAllocator = gc_heap.global_region_allocator;
        seg_mapping* table = stackalloc seg_mapping[10];
        uint* map = stackalloc uint[17];
        region_free_list* source = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_free_list* destination = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_allocator allocator = default;

        try
        {
            InitializeRegionMoveGlobals(table, Alignment);
            InitializeRegionAllocatorForMove(&allocator, map, 17, Alignment, (byte*)0x1000);
            ClearRegionFreeLists(source);
            ClearRegionFreeLists(destination);
            allocator.make_busy_block(map, 8);
            allocator.make_busy_block(map + 8, 9);

            heap_segment* lowerLarge = InitializeMappedRegion(table, 0x1000, 8, Alignment);
            heap_segment* higherHuge = InitializeMappedRegion(table, 0x9000, 9, Alignment);
            region_free_list.add_region(lowerLarge, source);
            region_free_list.add_region(higherHuge, source);

            allocator.move_highest_free_regions(8, small_region_p: false, destination);

            Assert.Equal((nuint)0, region_free_list.get_num_free_regions(&destination[(int)free_region_kind.large_free_region]));
            Assert.Equal((nuint)0, region_free_list.get_num_free_regions(&destination[(int)free_region_kind.huge_free_region]));
            Assert.True(region_free_list.is_on_free_list(lowerLarge, source));
            Assert.True(region_free_list.is_on_free_list(higherHuge, source));
        }
        finally
        {
            RestoreRegionMoveGlobals(oldShift, oldTable, oldGlobalAllocator);
        }
    }

    [Fact]
    public void RegionAllocatorMoveHighestFreeRegionsQuotaSpansMultipleLargeRegions()
    {
        const nuint Alignment = 0x1000;
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        region_allocator oldGlobalAllocator = gc_heap.global_region_allocator;
        seg_mapping* table = stackalloc seg_mapping[10];
        uint* map = stackalloc uint[16];
        region_free_list* source = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_free_list* destination = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_allocator allocator = default;

        try
        {
            InitializeRegionMoveGlobals(table, Alignment);
            InitializeRegionAllocatorForMove(&allocator, map, 16, Alignment, (byte*)0x1000);
            ClearRegionFreeLists(source);
            ClearRegionFreeLists(destination);
            allocator.make_busy_block(map, 8);
            allocator.make_busy_block(map + 8, 8);

            heap_segment* lowerLarge = InitializeMappedRegion(table, 0x1000, 8, Alignment);
            heap_segment* higherLarge = InitializeMappedRegion(table, 0x9000, 8, Alignment);
            region_free_list.add_region(lowerLarge, source);
            region_free_list.add_region(higherLarge, source);

            allocator.move_highest_free_regions(16, small_region_p: false, destination);

            region_free_list* destinationLarge = &destination[(int)free_region_kind.large_free_region];
            Assert.Equal((nuint)2, region_free_list.get_num_free_regions(destinationLarge));
            Assert.Equal((nuint)lowerLarge, (nuint)destinationLarge->get_first_free_region());
            Assert.Equal((nuint)higherLarge, (nuint)heap_segment.heap_segment_next(lowerLarge));
            Assert.Equal((nuint)0, region_free_list.get_num_free_regions(&source[(int)free_region_kind.large_free_region]));
        }
        finally
        {
            RestoreRegionMoveGlobals(oldShift, oldTable, oldGlobalAllocator);
        }
    }

    [Fact]
    public void RegionAllocatorMoveHighestFreeRegionsUpdatesSourceAndDestinationIntegrity()
    {
        const nuint Alignment = 0x1000;
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        region_allocator oldGlobalAllocator = gc_heap.global_region_allocator;
        seg_mapping* table = stackalloc seg_mapping[5];
        uint* map = stackalloc uint[3];
        region_free_list* source = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_free_list* destination = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_allocator allocator = default;

        try
        {
            InitializeRegionMoveGlobals(table, Alignment);
            InitializeRegionAllocatorForMove(&allocator, map, 3, Alignment, (byte*)0x1000);
            ClearRegionFreeLists(source);
            ClearRegionFreeLists(destination);
            allocator.make_busy_block(map, 1);
            allocator.make_busy_block(map + 1, 1);
            allocator.make_busy_block(map + 2, 1);

            heap_segment* low = InitializeMappedRegion(table, 0x1000, 1, Alignment);
            heap_segment* middle = InitializeMappedRegion(table, 0x2000, 1, Alignment);
            heap_segment* high = InitializeMappedRegion(table, 0x3000, 1, Alignment);
            region_free_list.add_region(low, source);
            region_free_list.add_region(middle, source);
            region_free_list.add_region(high, source);

            nuint movedSize = gc_heap.get_region_size(high);
            nuint movedCommitted = gc_heap.get_region_committed_size(high);
            nuint sourceSizeBefore = source[(int)free_region_kind.basic_free_region].get_size_free_regions();
            nuint sourceCommittedBefore = source[(int)free_region_kind.basic_free_region].get_size_committed_in_free();

            allocator.move_highest_free_regions(1, small_region_p: true, destination);

            Assert.Equal((nuint)2, region_free_list.get_num_free_regions(&source[(int)free_region_kind.basic_free_region]));
            Assert.Equal(sourceSizeBefore - movedSize, source[(int)free_region_kind.basic_free_region].get_size_free_regions());
            Assert.Equal(sourceCommittedBefore - movedCommitted, source[(int)free_region_kind.basic_free_region].get_size_committed_in_free());
            Assert.Equal((nuint)1, region_free_list.get_num_free_regions(&destination[(int)free_region_kind.basic_free_region]));
            Assert.Equal(movedSize, destination[(int)free_region_kind.basic_free_region].get_size_free_regions());
            Assert.Equal(movedCommitted, destination[(int)free_region_kind.basic_free_region].get_size_committed_in_free());
            Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_prev_free_region(high));
            Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_next(high));
            Assert.True(region_free_list.is_on_free_list(low, source));
            Assert.True(region_free_list.is_on_free_list(middle, source));
            Assert.True(region_free_list.is_on_free_list(high, destination));
        }
        finally
        {
            RestoreRegionMoveGlobals(oldShift, oldTable, oldGlobalAllocator);
        }
    }

    [Fact]
    public void RegionAllocatorMoveHighestFreeRegionsHonorsLeftMapTraversalBoundary()
    {
        const nuint Alignment = 0x1000;
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        region_allocator oldGlobalAllocator = gc_heap.global_region_allocator;
        seg_mapping* table = stackalloc seg_mapping[5];
        uint* map = stackalloc uint[4];
        region_free_list* source = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_free_list* destination = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_allocator allocator = default;

        try
        {
            InitializeRegionMoveGlobals(table, Alignment);
            InitializeRegionAllocatorForMove(&allocator, map + 1, 3, Alignment, (byte*)0x1000);
            ClearRegionFreeLists(source);
            ClearRegionFreeLists(destination);
            allocator.make_busy_block(map, 1);
            allocator.make_busy_block(map + 1, 1);
            allocator.make_busy_block(map + 2, 1);
            allocator.make_busy_block(map + 3, 1);

            heap_segment* beforeLeftStart = InitializeMappedRegion(table, 0x0, 1, Alignment);
            heap_segment* lowest = InitializeMappedRegion(table, 0x1000, 1, Alignment);
            heap_segment* middle = InitializeMappedRegion(table, 0x2000, 1, Alignment);
            heap_segment* highest = InitializeMappedRegion(table, 0x3000, 1, Alignment);
            region_free_list.add_region(beforeLeftStart, source);
            region_free_list.add_region(lowest, source);
            region_free_list.add_region(middle, source);
            region_free_list.add_region(highest, source);

            allocator.move_highest_free_regions(3, small_region_p: true, destination);

            Assert.Equal((nuint)3, region_free_list.get_num_free_regions(&destination[(int)free_region_kind.basic_free_region]));
            Assert.True(region_free_list.is_on_free_list(lowest, destination));
            Assert.True(region_free_list.is_on_free_list(middle, destination));
            Assert.True(region_free_list.is_on_free_list(highest, destination));
            Assert.True(region_free_list.is_on_free_list(beforeLeftStart, source));
        }
        finally
        {
            RestoreRegionMoveGlobals(oldShift, oldTable, oldGlobalAllocator);
        }
    }

    [Fact]
    public void RegionFreeListKindDispatchHelpersUseGlobalAllocatorAlignment()
    {
        gc_heap.global_region_allocator.initialize_alignment(0x1000);

        region_free_list* lists = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        heap_segment basic = default;
        heap_segment large = default;
        heap_segment huge = default;

        InitializeRegion(&basic, 0x1000, 0x1800, 0x2000, age: 0);
        InitializeRegion(&large, 0x3000, 0x5800, 0xB000, age: 0);
        InitializeRegion(&huge, 0xC000, 0xE000, 0x17000, age: 0);

        region_free_list.add_region(&basic, lists);
        region_free_list.add_region(&large, lists);
        region_free_list.add_region(&huge, lists);

        Assert.Equal((nuint)1, region_free_list.get_num_free_regions(&lists[(int)free_region_kind.basic_free_region]));
        Assert.Equal((nuint)1, region_free_list.get_num_free_regions(&lists[(int)free_region_kind.large_free_region]));
        Assert.Equal((nuint)1, region_free_list.get_num_free_regions(&lists[(int)free_region_kind.huge_free_region]));
        Assert.True(region_free_list.is_on_free_list(&basic, lists));
        Assert.True(region_free_list.is_on_free_list(&large, lists));
        Assert.True(region_free_list.is_on_free_list(&huge, lists));
        Assert.False(region_free_list.is_on_free_list(&basic, &lists[(int)free_region_kind.large_free_region]));
    }

    [Fact]
    public void RegionFreeListAddRegionDescendingDispatchesByKind()
    {
        gc_heap.global_region_allocator.initialize_alignment(0x1000);

        region_free_list* lists = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        heap_segment lessCommitted = default;
        heap_segment moreCommitted = default;

        InitializeRegion(&lessCommitted, 0x10000, 0x12000, 0x18000, age: 7);
        InitializeRegion(&moreCommitted, 0x20000, 0x26000, 0x28000, age: 3);

        region_free_list.add_region_descending(&lessCommitted, lists);
        region_free_list.add_region_descending(&moreCommitted, lists);

        region_free_list* largeList = &lists[(int)free_region_kind.large_free_region];
        Assert.Equal((nuint)2, region_free_list.get_num_free_regions(largeList));
        Assert.Equal((nuint)(&moreCommitted), (nuint)largeList->get_first_free_region());
        Assert.Equal((nuint)(&lessCommitted), (nuint)heap_segment.heap_segment_next(&moreCommitted));
        Assert.True(region_free_list.is_on_free_list(&moreCommitted, lists));
        Assert.True(region_free_list.is_on_free_list(&lessCommitted, lists));
    }

    [Fact]
    public void RegionFreeListUnlinkSmallestRegionUsesLargeAlignmentMinimum()
    {
        gc_heap.global_region_allocator.initialize_alignment(0x1000);

        nuint largeSize = gc_heap.global_region_allocator.get_large_region_alignment();
        region_free_list list = default;
        region_free_list* pList = &list;
        heap_segment twoLarge = default;
        heap_segment threeLarge = default;
        heap_segment fourLarge = default;

        InitializeRegion(&twoLarge, 0x100000, 0x118000, 0x100000 + (2 * largeSize), age: 0);
        InitializeRegion(&threeLarge, 0x200000, 0x225000, 0x200000 + (3 * largeSize), age: 0);
        InitializeRegion(&fourLarge, 0x300000, 0x330000, 0x300000 + (4 * largeSize), age: 0);

        region_free_list.add_region_front(pList, &fourLarge);
        region_free_list.add_region_front(pList, &twoLarge);
        region_free_list.add_region_front(pList, &threeLarge);

        heap_segment* selected = region_free_list.unlink_smallest_region(pList, largeSize);

        Assert.Equal((nuint)(&twoLarge), (nuint)selected);
        Assert.Equal((nuint)2, region_free_list.get_num_free_regions(pList));
        Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_containing_free_list(&twoLarge));
    }

    [Fact]
    public void RegionFreeListTransferAndAgeArrayPreserveOwnershipAndCap()
    {
        region_free_list* lists = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        heap_segment basic = default;
        heap_segment large = default;
        heap_segment huge = default;

        InitializeRegion(&basic, 0x1000, 0x1800, 0x2000, age: heap_segment.MAX_AGE_IN_FREE - 1);
        InitializeRegion(&large, 0x3000, 0x3800, 0x5000, age: heap_segment.MAX_AGE_IN_FREE);
        InitializeRegion(&huge, 0x6000, 0x7400, 0x9000, age: 0);

        region_free_list.add_region_front(&lists[(int)free_region_kind.basic_free_region], &basic);
        region_free_list.add_region_front(&lists[(int)free_region_kind.large_free_region], &large);
        region_free_list.add_region_front(&lists[(int)free_region_kind.huge_free_region], &huge);

        region_free_list.age_free_regions(lists);
        Assert.Equal(heap_segment.MAX_AGE_IN_FREE, heap_segment.heap_segment_age_in_free(&basic));
        Assert.Equal(heap_segment.MAX_AGE_IN_FREE, heap_segment.heap_segment_age_in_free(&large));
        Assert.Equal(1, heap_segment.heap_segment_age_in_free(&huge));

        region_free_list destination = default;
        region_free_list* pDestination = &destination;
        region_free_list.transfer_regions(pDestination, &lists[(int)free_region_kind.basic_free_region]);

        Assert.Equal((nuint)1, region_free_list.get_num_free_regions(pDestination));
        Assert.Equal((nuint)0, region_free_list.get_num_free_regions(&lists[(int)free_region_kind.basic_free_region]));
        Assert.Equal((nuint)pDestination, (nuint)heap_segment.heap_segment_containing_free_list(&basic));
    }

    [Fact]
    public void ClearRegionInfoClearsBrickAndCardsAndRecordsBackgroundChange()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: false);
        uint* cards = stackalloc uint[3];
        short* bricks = stackalloc short[4];
        for (int i = 0; i < 3; i++)
        {
            cards[i] = uint.MaxValue;
        }
        for (int i = 0; i < 4; i++)
        {
            bricks[i] = -1;
        }

        gc_heap.card_table = cards;
        gc_heap.brick_table = bricks;
        gc_heap.settings.gc_index = 42;
#if BACKGROUND_GC
        gc_heap.current_bgc_state = bgc_state.bgc_sweep_soh;
        gc_heap.gc_background_running = 0;
#endif

        heap_segment region = default;
        InitializeRegion(&region, 0, card_table_info.brick_size * 4, card_table_info.brick_size * 4, age: 0);
        heap_segment.heap_segment_allocated(&region) = heap_segment.heap_segment_mem(&region);

        gc_heap.clear_region_info(&region);

        Assert.Equal(0u, cards[0]);
        Assert.Equal(0u, cards[1]);
        Assert.Equal(uint.MaxValue, cards[2]);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(0, bricks[i]);
        }

#if BACKGROUND_GC
        changed_seg changed = GCCommon.saved_changed_segs[(int)(GCCommon.saved_changed_segs_count & (GCCommon.max_saved_changed_segs - 1))];
        Assert.Equal((nuint)(&region), (nuint)changed.start);
        Assert.Equal((nuint)heap_segment.heap_segment_reserved(&region), (nuint)changed.end);
        Assert.Equal((nuint)42, changed.gc_index);
        Assert.Equal(bgc_state.bgc_sweep_soh, changed.bgc);
        Assert.Equal(changed_seg_state.seg_deleted, changed.changed);
#endif
    }

    [Fact]
    public void BrickOfUsesLowestAddress()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: false);
        gc_heap.lowest_address = (byte*)0x100000;

        Assert.Equal((nuint)0, gc_heap.brick_of(gc_heap.lowest_address));
        Assert.Equal((nuint)3, gc_heap.brick_of(gc_heap.lowest_address + (3 * card_table_info.brick_size)));
    }

    [Fact]
    public void ClearBrickTableIndexesRelativeToLowestAddress()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: false);
        short* bricks = stackalloc short[5];
        for (int i = 0; i < 5; i++)
        {
            bricks[i] = -1;
        }

        gc_heap.lowest_address = (byte*)0x100000;
        gc_heap.brick_table = bricks;

        gc_heap.clear_brick_table(
            gc_heap.lowest_address + card_table_info.brick_size,
            gc_heap.lowest_address + (4 * card_table_info.brick_size));

        Assert.Equal(-1, bricks[0]);
        Assert.Equal(0, bricks[1]);
        Assert.Equal(0, bricks[2]);
        Assert.Equal(0, bricks[3]);
        Assert.Equal(-1, bricks[4]);
    }

#if USE_REGIONS
    [Fact]
    public void FixBrickToHighestWritesPositiveAndNegativeBrickEntries()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: false);
        int storageSize = checked((int)(6 * card_table_info.brick_size));
        byte* storage = stackalloc byte[storageSize];
        byte* lowest = card_table_info.align_on_brick(storage);
        short* bricks = stackalloc short[5];
        for (int i = 0; i < 5; i++)
        {
            bricks[i] = 17;
        }

        gc_heap.lowest_address = lowest;
        gc_heap.brick_table = bricks;

        byte* o = lowest + (nint)card_table_info.brick_size + 64;
        byte* next_o = lowest + (nint)(4 * card_table_info.brick_size) + 64;
        gc_heap.fix_brick_to_highest(o, next_o);

        Assert.Equal(17, gc_heap.get_brick_entry(0));
        Assert.Equal(65, gc_heap.get_brick_entry(1));
        Assert.Equal(-1, gc_heap.get_brick_entry(2));
        Assert.Equal(-2, gc_heap.get_brick_entry(3));
        Assert.Equal(17, gc_heap.get_brick_entry(4));
    }

    [Fact]
    public void FindFirstObjectFollowsLaterNegativeBrickEntryToContainingObject()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: false);
        int storageSize = checked((int)(6 * card_table_info.brick_size));
        byte* storage = stackalloc byte[storageSize];
        byte* lowest = card_table_info.align_on_brick(storage);
        short* bricks = stackalloc short[5];
        MethodTable methodTable = default;
        MethodTable* pMethodTable = &methodTable;
        nuint objectSize = 3 * card_table_info.brick_size;
        byte* o = lowest + 64;

        methodTable.m_uBaseSize = (uint)objectSize;
        ((CObjectHeader*)o)->RawSetMethodTable(pMethodTable);
        gc_heap.lowest_address = lowest;
        gc_heap.brick_table = bricks;
        gc_heap.fix_brick_to_highest(o, o + (nint)objectSize);

        byte* result = gc_heap.find_first_object(
            lowest + (nint)(3 * card_table_info.brick_size) + 32,
            o);

        Assert.Equal((nuint)o, (nuint)result);
        Assert.Equal(-2, gc_heap.get_brick_entry(2));
    }

    [Fact]
    public void FindFirstObjectHonorsObjectAndBrickBoundariesAndFirstShortcut()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: false);
        int storageSize = checked((int)(4 * card_table_info.brick_size));
        byte* storage = stackalloc byte[storageSize];
        byte* lowest = card_table_info.align_on_brick(storage);
        short* bricks = stackalloc short[3];
        MethodTable methodTable = default;
        MethodTable* pMethodTable = &methodTable;
        nuint objectSize = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size);
        byte* first = lowest + (nint)card_table_info.brick_size - (nint)objectSize;
        byte* second = first + (nint)objectSize;

        methodTable.m_uBaseSize = (uint)objectSize;
        ((CObjectHeader*)first)->RawSetMethodTable(pMethodTable);
        ((CObjectHeader*)second)->RawSetMethodTable(pMethodTable);
        gc_heap.lowest_address = lowest;
        gc_heap.brick_table = bricks;

        bricks[0] = -1;
        Assert.Equal((nuint)first, (nuint)gc_heap.find_first_object(first, first));

        gc_heap.fix_brick_to_highest(first, second);
        Assert.Equal((nuint)second, (nuint)gc_heap.find_first_object(second, first));
    }
#endif

#if USE_REGIONS && !MULTIPLE_HEAPS
    [Fact]
    public void ClearGen0BricksClearsAllGen0RegionsOnlyOnce()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: false);
        short* bricks = stackalloc short[6];
        for (int i = 0; i < 6; i++)
        {
            bricks[i] = 17;
        }

        byte* lowestAddress = (byte*)0x100000;
        gc_heap.lowest_address = lowestAddress;
        gc_heap.brick_table = bricks;

        heap_segment firstRegion = default;
        heap_segment secondRegion = default;
        heap_segment.heap_segment_mem(&firstRegion) = lowestAddress + (nint)card_table_info.brick_size;
        heap_segment.heap_segment_allocated(&firstRegion) =
            lowestAddress + (nint)(2 * card_table_info.brick_size) + 1;
        heap_segment.heap_segment_next(&firstRegion) = &secondRegion;
        heap_segment.heap_segment_mem(&secondRegion) = lowestAddress + (nint)(3 * card_table_info.brick_size);
        heap_segment.heap_segment_allocated(&secondRegion) =
            lowestAddress + (nint)(4 * card_table_info.brick_size) + 1;

        gc_heap heap = default;
        generation* gen0 = gc_heap.generation_of(
            gc_heap.generation_table_of(&heap),
            (int)gc_generation_num.soh_gen0);
        generation.generation_start_segment(gen0) = &firstRegion;

        gc_heap.clear_gen0_bricks(&heap);

        Assert.Equal(1, gc_heap.gen0_bricks_cleared);
        Assert.Equal(17, gc_heap.get_brick_entry(0));
        for (nuint brick = 1; brick < 5; brick++)
        {
            Assert.Equal(-1, gc_heap.get_brick_entry(brick));
        }
        Assert.Equal(17, gc_heap.get_brick_entry(5));

        bricks[2] = 23;
        gc_heap.clear_gen0_bricks(&heap);

        Assert.Equal(1, gc_heap.gen0_bricks_cleared);
        Assert.Equal(23, gc_heap.get_brick_entry(2));
    }

    [Fact]
    public void FindObjectLazilyRepairsSohBricksAndScansZeroBrickUohObjects()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: false);
        nuint pageSize = GCToOSInterface.GetPageSize();
        nuint regionSize = 2 * card_table_info.brick_size > pageSize
            ? 2 * card_table_info.brick_size
            : pageSize;
        byte* heapMemory = GCToOSInterface.VirtualReserve(2 * regionSize, regionSize, (uint)VirtualReserveFlags.None);
        Assert.True(heapMemory is not null);

        try
        {
            Assert.True(GCToOSInterface.VirtualCommit(heapMemory, 2 * regionSize));

            int regionShift = 0;
            while (((nuint)1 << regionShift) < regionSize)
            {
                regionShift++;
            }

            Assert.Equal(regionSize, (nuint)1 << regionShift);

            int brickCount = checked((int)((2 * regionSize) / card_table_info.brick_size));
            short* bricks = stackalloc short[brickCount];
            seg_mapping* segments = stackalloc seg_mapping[2];
            nuint firstRegionIndex = (nuint)heapMemory >> regionShift;
            GCCommon.g_gc_lowest_address = heapMemory;
            GCCommon.g_gc_highest_address = heapMemory + (nint)(2 * regionSize);
            GCCommon.seg_mapping_table = segments - (nint)firstRegionIndex;
            gc_heap.bookkeeping_covered_committed = GCCommon.g_gc_highest_address;
            gc_heap.min_segment_size_shr = (nuint)regionShift;
            gc_heap.lowest_address = heapMemory;
            gc_heap.brick_table = bricks;

            heap_segment* soh = (heap_segment*)segments;
            nuint sohObjectSize = 2 * card_table_info.brick_size;
            MethodTable sohMethodTable = default;
            sohMethodTable.m_uBaseSize = (uint)sohObjectSize;
            ((CObjectHeader*)heapMemory)->RawSetMethodTable(&sohMethodTable);
            heap_segment.heap_segment_mem(soh) = heapMemory;
            heap_segment.heap_segment_allocated(soh) = heapMemory + (nint)sohObjectSize;
            heap_segment.heap_segment_reserved(soh) = heapMemory + (nint)regionSize;

            byte* uohMemory = heapMemory + (nint)regionSize;
            heap_segment* uoh = (heap_segment*)&segments[1];
            nuint uohObjectSize = gc_heap.Align(
                (nuint)GCInterfaceOffsets.min_obj_size,
                gc_heap.get_alignment_constant(small_object_p: false));
            MethodTable uohMethodTable = default;
            uohMethodTable.m_uBaseSize = (uint)uohObjectSize;
            ((CObjectHeader*)uohMemory)->RawSetMethodTable(&uohMethodTable);
            heap_segment.heap_segment_mem(uoh) = uohMemory;
            heap_segment.heap_segment_allocated(uoh) = uohMemory + (nint)uohObjectSize;
            heap_segment.heap_segment_reserved(uoh) = uohMemory + (nint)regionSize;
            uoh->flags = heap_segment.heap_segment_flags_loh;

            gc_heap heap = default;
            generation* gen0 = gc_heap.generation_of(
                gc_heap.generation_table_of(&heap),
                (int)gc_generation_num.soh_gen0);
            generation.generation_start_segment(gen0) = soh;

            gc_heap.gen0_bricks_cleared = 0;
            gc_heap.gen0_must_clear_bricks = 1;
            byte* sohInterior = heapMemory + (nint)card_table_info.brick_size + (nint)sizeof(nuint);
            Assert.Equal((nuint)heapMemory, (nuint)gc_heap.find_object(sohInterior, &heap));
            Assert.Equal(1, gc_heap.gen0_bricks_cleared);
            Assert.Equal(gc_heap.FFIND_DECAY, gc_heap.gen0_must_clear_bricks);
            Assert.Equal(1, gc_heap.get_brick_entry(0));

            byte* uohInterior = uohMemory + (nint)sizeof(nuint);
            Assert.Equal(0, gc_heap.get_brick_entry(gc_heap.brick_of(uohInterior)));
            Assert.True(gc_heap.try_get_region_segment(uohInterior, small_heap_only: false, out heap_segment* foundUoh));
            Assert.Equal((nuint)uoh, (nuint)foundUoh);
            Assert.Equal((nuint)uohMemory, (nuint)gc_heap.find_object(uohInterior, &heap));
            Assert.True(gc_heap.find_object(uohMemory + (nint)uohObjectSize, &heap) is null);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(heapMemory, 2 * regionSize);
        }
    }

    [Fact]
    public void AdjustLimitClrMaintainsOrInvalidatesGen0FindObjectBricks()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: false);
        nuint pageSize = GCToOSInterface.GetPageSize();
        nuint reservationSize = 4 * pageSize;
        byte* storage = GCToOSInterface.VirtualReserve(reservationSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(storage is not null);

        try
        {
            Assert.True(GCToOSInterface.VirtualCommit(storage, reservationSize));

            int brickCount = checked((int)(reservationSize / card_table_info.brick_size));
            short* bricks = stackalloc short[brickCount];
            for (int i = 0; i < brickCount; i++)
            {
                bricks[i] = 17;
            }

            gc_heap.lowest_address = storage;
            gc_heap.brick_table = bricks;
            byte* start = storage + 64;
            nuint limitSize = 2 * card_table_info.brick_size;
            int alignment = gc_heap.get_alignment_constant(small_object_p: true);
            generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
            InitializeAllocationGenerations(generations);

            heap_segment segment = default;
            heap_segment.heap_segment_mem(&segment) = start;
            heap_segment.heap_segment_used(&segment) = start;
            heap_segment.heap_segment_committed(&segment) = storage + (nint)reservationSize;
            heap_segment.heap_segment_reserved(&segment) = storage + (nint)reservationSize;
            byte* allocAllocated = start + (nint)limitSize;
            ulong totalAllocatedBytes = 0;
            gc_alloc_context context = default;
            context.alloc_limit = start;

            gc_heap.gen0_must_clear_bricks = 1;
            gc_heap.adjust_limit_clr(
                start,
                limitSize,
                limitSize,
                &context,
                0,
                &segment,
                alignment,
                (int)gc_generation_num.soh_gen0,
                generations,
                &segment,
                allocAllocated,
                &totalAllocatedBytes,
                null,
                null);

            nuint firstBrick = gc_heap.brick_of(context.alloc_ptr);
            nuint endBrick = gc_heap.brick_of(card_table_info.align_on_brick(start + (nint)limitSize));
            Assert.Equal((int)(context.alloc_ptr - gc_heap.brick_address(firstBrick)) + 1, gc_heap.get_brick_entry(firstBrick));
            for (nuint brick = firstBrick + 1; brick < endBrick; brick++)
            {
                Assert.Equal(-1, gc_heap.get_brick_entry(brick));
            }

            for (int i = 0; i < brickCount; i++)
            {
                bricks[i] = 17;
            }

            context = default;
            context.alloc_limit = start;
            gc_heap.gen0_must_clear_bricks = 1;
            nuint largeLimitSize = gc_heap.DefaultAllocationQuantum / 2;
            gc_heap.adjust_limit_clr(
                start,
                largeLimitSize,
                largeLimitSize,
                &context,
                0,
                null,
                alignment,
                (int)gc_generation_num.soh_gen0,
                generations,
                null,
                null,
                &totalAllocatedBytes,
                null,
                null);

            firstBrick = gc_heap.brick_of(context.alloc_ptr);
            endBrick = gc_heap.brick_of(card_table_info.align_on_brick(start + (nint)largeLimitSize));
            Assert.Equal((int)(context.alloc_ptr - gc_heap.brick_address(firstBrick)) + 1, gc_heap.get_brick_entry(firstBrick));
            for (nuint brick = firstBrick + 1; brick < endBrick; brick++)
            {
                Assert.Equal(-1, gc_heap.get_brick_entry(brick));
            }

            gc_heap.gen0_bricks_cleared = 1;
            gc_heap.gen0_must_clear_bricks = 0;
            context = default;
            context.alloc_limit = start;
            heap_segment.heap_segment_used(&segment) = start;
            gc_heap.adjust_limit_clr(
                start,
                limitSize,
                limitSize,
                &context,
                0,
                &segment,
                alignment,
                (int)gc_generation_num.soh_gen0,
                generations,
                &segment,
                allocAllocated,
                &totalAllocatedBytes,
                null,
                null);

            Assert.Equal(0, gc_heap.gen0_bricks_cleared);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(storage, reservationSize);
        }
    }
#endif

#if BACKGROUND_GC
    [Fact]
    public void InitTableForRegionCommitsMarkArrayAndInitializesOnlyTheFirstSohBrick()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: true);
        nuint pageSize = GCToOSInterface.GetPageSize();
        byte* regionStart = GCToOSInterface.VirtualReserve(pageSize, pageSize, (uint)VirtualReserveFlags.None);
        byte* markStorage = GCToOSInterface.VirtualReserve(pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(regionStart is not null);
        Assert.True(markStorage is not null);

        try
        {
            Assert.True(GCToOSInterface.VirtualCommit(regionStart, pageSize));

            heap_segment region = default;
            InitializeRegion(&region, (nuint)regionStart, (nuint)(regionStart + (nint)pageSize), (nuint)(regionStart + (nint)pageSize), age: 0);
            heap_segment.heap_segment_used(&region) = heap_segment.heap_segment_reserved(&region);

            nuint firstMarkWord = card_table_info.mark_word_of(heap_segment.heap_segment_mem(&region));
            gc_heap.mark_array = (uint*)markStorage - (nint)firstMarkWord;
            gc_heap.background_saved_lowest_address = heap_segment.heap_segment_mem(&region);
            gc_heap.background_saved_highest_address = heap_segment.heap_segment_reserved(&region);
            gc_heap.lowest_address = heap_segment.heap_segment_mem(&region);

            short* bricks = stackalloc short[3];
            bricks[0] = 17;
            bricks[1] = 23;
            bricks[2] = 29;
            gc_heap.brick_table = bricks;

            Assert.True(gc_heap.init_table_for_region((int)gc_generation_num.soh_gen0, &region));
            Assert.Equal(heap_segment.heap_segment_flags_ma_committed, region.flags & heap_segment.heap_segment_flags_ma_committed);
            Assert.Equal(-1, bricks[0]);
            Assert.Equal(23, bricks[1]);
            Assert.Equal(29, bricks[2]);
            Assert.Equal(0u, markStorage[0]);
            Assert.Equal(pageSize, gc_heap.committed_by_oh[gc_heap.recorded_committed_mark_array_bucket]);
            Assert.Equal(pageSize, gc_heap.current_total_committed);
            Assert.Equal(pageSize, gc_heap.current_total_committed_bookkeeping);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(markStorage, pageSize);
            GCToOSInterface.VirtualRelease(regionStart, pageSize);
        }
    }

    [Fact]
    public void InitTableForRegionPreservesExistingMarkCommitAndUohFirstBrick()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: true);
        nuint pageSize = GCToOSInterface.GetPageSize();
        byte* regionStart = GCToOSInterface.VirtualReserve(pageSize, pageSize, (uint)VirtualReserveFlags.None);
        byte* markStorage = GCToOSInterface.VirtualReserve(pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(regionStart is not null);
        Assert.True(markStorage is not null);

        try
        {
            Assert.True(GCToOSInterface.VirtualCommit(regionStart, pageSize));
            Assert.True(GCToOSInterface.VirtualCommit(markStorage, pageSize));

            heap_segment region = default;
            InitializeRegion(&region, (nuint)regionStart, (nuint)(regionStart + (nint)pageSize), (nuint)(regionStart + (nint)pageSize), age: 0);
            heap_segment.heap_segment_used(&region) = heap_segment.heap_segment_reserved(&region);
            region.flags = heap_segment.heap_segment_flags_ma_committed | heap_segment.heap_segment_flags_loh;

            nuint firstMarkWord = card_table_info.mark_word_of(heap_segment.heap_segment_mem(&region));
            gc_heap.mark_array = (uint*)markStorage - (nint)firstMarkWord;
            gc_heap.background_saved_lowest_address = heap_segment.heap_segment_mem(&region);
            gc_heap.background_saved_highest_address = heap_segment.heap_segment_reserved(&region);
            gc_heap.lowest_address = heap_segment.heap_segment_mem(&region);

            short* bricks = stackalloc short[2];
            bricks[0] = 0;
            bricks[1] = 31;
            gc_heap.brick_table = bricks;

            Assert.True(gc_heap.init_table_for_region((int)gc_generation_num.loh_generation, &region));
            Assert.Equal(heap_segment.heap_segment_flags_ma_committed, region.flags & heap_segment.heap_segment_flags_ma_committed);
            Assert.Equal(0, bricks[0]);
            Assert.Equal(31, bricks[1]);
            Assert.Equal((nuint)0, gc_heap.committed_by_oh[gc_heap.recorded_committed_mark_array_bucket]);
            Assert.Equal((nuint)0, gc_heap.current_total_committed);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(markStorage, pageSize);
            GCToOSInterface.VirtualRelease(regionStart, pageSize);
        }
    }

    [Fact]
    public void InitTableForRegionDecommitsAndFailsWhenMarkArrayCommitExceedsTheHardLimit()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: true);
        nuint pageSize = GCToOSInterface.GetPageSize();
        byte* regionReservation = GCToOSInterface.VirtualReserve(pageSize, pageSize, (uint)VirtualReserveFlags.None);
        byte* markStorage = GCToOSInterface.VirtualReserve(pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(regionReservation is not null);
        Assert.True(markStorage is not null);

        uint* map = null;
        try
        {
            byte* lowest = null;
            byte* highest = null;
            Assert.True(gc_heap.global_region_allocator.init(regionReservation, regionReservation + (nint)pageSize, pageSize, &lowest, &highest));
            gc_heap.global_region_allocator.initialize();
            map = gc_heap.global_region_allocator.region_map_index_of(regionReservation);

            byte* regionStart = null;
            byte* regionEnd = null;
            Assert.True(gc_heap.global_region_allocator.allocate_basic_region((int)gc_generation_num.soh_gen0, &regionStart, &regionEnd, null));
            Assert.True(GCToOSInterface.VirtualCommit(regionStart, pageSize));

            heap_segment region = default;
            InitializeRegion(&region, (nuint)regionStart, (nuint)regionEnd, (nuint)regionEnd, age: 0);
            heap_segment.heap_segment_used(&region) = heap_segment.heap_segment_reserved(&region);

            nuint firstMarkWord = card_table_info.mark_word_of(heap_segment.heap_segment_mem(&region));
            gc_heap.mark_array = (uint*)markStorage - (nint)firstMarkWord;
            gc_heap.background_saved_lowest_address = heap_segment.heap_segment_mem(&region);
            gc_heap.background_saved_highest_address = heap_segment.heap_segment_reserved(&region);
            gc_heap.lowest_address = heap_segment.heap_segment_mem(&region);
            gc_heap.heap_hard_limit = pageSize;
            gc_heap.committed_by_oh[(int)gc_oh_num.soh] = pageSize;
            gc_heap.current_total_committed = pageSize;

            short* bricks = stackalloc short[2];
            bricks[0] = 37;
            bricks[1] = 41;
            gc_heap.brick_table = bricks;

            Assert.False(gc_heap.init_table_for_region((int)gc_generation_num.soh_gen0, &region));
            Assert.Equal((nuint)heap_segment.heap_segment_mem(&region), (nuint)heap_segment.heap_segment_committed(&region));
            Assert.Equal((nuint)0, gc_heap.committed_by_oh[(int)gc_oh_num.soh]);
            Assert.Equal((nuint)0, gc_heap.current_total_committed);
            Assert.Equal(pageSize, gc_heap.global_region_allocator.get_free());
            Assert.Equal(37, bricks[0]);
            Assert.Equal(41, bricks[1]);
        }
        finally
        {
            if (map is not null)
            {
                SyncImports.ManagedGC_Free(map);
            }

            GCToOSInterface.VirtualRelease(markStorage, pageSize);
            GCToOSInterface.VirtualRelease(regionReservation, pageSize);
        }
    }

    [Fact]
    public void BackgroundGcDiagnosticEnumsMatchNativeOrder()
    {
        Assert.Equal(0, (int)bgc_state.bgc_not_in_process);
        Assert.Equal(1, (int)bgc_state.bgc_initialized);
        Assert.Equal(2, (int)bgc_state.bgc_reset_ww);
        Assert.Equal(3, (int)bgc_state.bgc_mark_handles);
        Assert.Equal(4, (int)bgc_state.bgc_mark_stack);
        Assert.Equal(5, (int)bgc_state.bgc_revisit_soh);
        Assert.Equal(6, (int)bgc_state.bgc_revisit_uoh);
        Assert.Equal(7, (int)bgc_state.bgc_overflow_soh);
        Assert.Equal(8, (int)bgc_state.bgc_overflow_uoh);
        Assert.Equal(9, (int)bgc_state.bgc_final_marking);
        Assert.Equal(10, (int)bgc_state.bgc_sweep_soh);
        Assert.Equal(11, (int)bgc_state.bgc_sweep_uoh);
        Assert.Equal(12, (int)bgc_state.bgc_plan_phase);
        Assert.Equal(0, (int)changed_seg_state.seg_deleted);
        Assert.Equal(1, (int)changed_seg_state.seg_added);
    }
#endif

    [Fact]
    public void ClearRegionInfoSkipsBrickClearingForUohRegions()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: false);
        uint* cards = stackalloc uint[3];
        short* bricks = stackalloc short[4];
        for (int i = 0; i < 3; i++)
        {
            cards[i] = uint.MaxValue;
        }
        for (int i = 0; i < 4; i++)
        {
            bricks[i] = -1;
        }

        gc_heap.card_table = cards;
        gc_heap.brick_table = bricks;

        heap_segment region = default;
        InitializeRegion(&region, 0, card_table_info.brick_size * 4, card_table_info.brick_size * 4, age: 0);
        region.flags = heap_segment.heap_segment_flags_loh;
        heap_segment.heap_segment_allocated(&region) = heap_segment.heap_segment_mem(&region);

        gc_heap.clear_region_info(&region);

        Assert.Equal(0u, cards[0]);
        Assert.Equal(0u, cards[1]);
        Assert.Equal(uint.MaxValue, cards[2]);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(-1, bricks[i]);
        }
    }

    [Fact]
    public void ReturnFreeRegionTransfersAccountingAddsToFreeListAndClearsBasicRegionSentinels()
    {
        const nuint Alignment = 0x1000;
        using RegionSegmentsStateScope _ = new(initializeCommitLock: true);
        seg_mapping* table = stackalloc seg_mapping[region_allocator.LARGE_REGION_FACTOR];
        uint* cards = stackalloc uint[5];
        short* bricks = stackalloc short[8];
        for (int i = 0; i < 5; i++)
        {
            cards[i] = uint.MaxValue;
        }
        for (int i = 0; i < 8; i++)
        {
            bricks[i] = -1;
        }

        gc_heap.card_table = cards;
        gc_heap.brick_table = bricks;
        gc_heap.min_segment_size_shr = (nuint)gc_heap.index_of_highest_set_bit(Alignment);
        gc_heap.global_region_allocator.initialize_alignment(Alignment);
        GCCommon.seg_mapping_table = table;

        heap_segment* region = &table[0].region_info;
        InitializeRegion(region, 0, 6 * Alignment, region_allocator.LARGE_REGION_FACTOR * Alignment, age: 7);
        heap_segment.heap_segment_allocated(region) = heap_segment.heap_segment_mem(region);
        heap_segment.heap_segment_gen_num(region) = 2;
        heap_segment.heap_segment_plan_gen_num(region) = 1;
        for (int i = 1; i < region_allocator.LARGE_REGION_FACTOR; i++)
        {
            heap_segment* basicRegion = &table[i].region_info;
            basicRegion->allocated = (byte*)(nint)(-i);
            basicRegion->gen_num = 2;
            basicRegion->plan_gen_num = 1;
        }

        gc_heap.committed_by_oh[(int)gc_oh_num.soh] = 6 * Alignment;

        gc_heap.return_free_region(region);

        Assert.Equal((nuint)0, gc_heap.committed_by_oh[(int)gc_oh_num.soh]);
        Assert.Equal(6 * Alignment, gc_heap.committed_by_oh[gc_heap.recorded_committed_free_bucket]);
        Assert.Equal((nuint)1, region_free_list.get_num_free_regions(gc_heap.free_regions_of((int)free_region_kind.large_free_region)));
        Assert.Equal((nuint)region, (nuint)gc_heap.free_regions_of((int)free_region_kind.large_free_region)->get_first_free_region());
        Assert.Equal(region_allocator.LARGE_REGION_FACTOR * Alignment, gc_heap.free_regions_of((int)free_region_kind.large_free_region)->get_size_free_regions());
        Assert.Equal(6 * Alignment, gc_heap.free_regions_of((int)free_region_kind.large_free_region)->get_size_committed_in_free());

        for (int i = 0; i < region_allocator.LARGE_REGION_FACTOR; i++)
        {
            heap_segment* basicRegion = &table[i].region_info;
            Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_allocated(basicRegion));
            Assert.Equal((byte)2, heap_segment.heap_segment_gen_num(basicRegion));
            Assert.Equal(1, heap_segment.heap_segment_plan_gen_num(basicRegion));
        }

        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(0u, cards[i]);
        }
        Assert.Equal(uint.MaxValue, cards[4]);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(0, bricks[i]);
        }
    }

    [Fact]
    public void AllocationContextAlignmentAndSizeFitPreserveNativeBoundaryAndOverflowArithmetic()
    {
        int sohAlignment = gc_heap.get_alignment_constant(small_object_p: true);
        int uohAlignment = gc_heap.get_alignment_constant(small_object_p: false);
        nuint alignedMinObjectSize = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, sohAlignment);

        Assert.Equal(GCEnv.DATA_ALIGNMENT - 1, sohAlignment);
        Assert.Equal(7, uohAlignment);
        Assert.Equal(unchecked(((nuint)0x11 + (nuint)sohAlignment) & ~(nuint)sohAlignment), gc_heap.Align(0x11, sohAlignment));
        Assert.Equal((nuint)0, gc_heap.Align(nuint.MaxValue, 7));

        gc_alloc_context context = default;
        context.alloc_ptr = (byte*)0x1000;
        context.alloc_limit = context.alloc_ptr + (nint)alignedMinObjectSize;
        byte* originalPointer = context.alloc_ptr;
        byte* originalLimit = context.alloc_limit;

        Assert.True(gc_heap.a_size_fit_p(0, context.alloc_ptr, context.alloc_limit, sohAlignment));
        Assert.False(gc_heap.a_size_fit_p(1, context.alloc_ptr, context.alloc_limit, sohAlignment));
        Assert.False(gc_heap.a_size_fit_p(0, context.alloc_limit, context.alloc_ptr, sohAlignment));
        byte* overflowLimit = context.alloc_ptr + (nint)(alignedMinObjectSize - 1);
        Assert.True(gc_heap.a_size_fit_p(nuint.MaxValue, context.alloc_ptr, overflowLimit, sohAlignment));
        Assert.Equal((nuint)originalPointer, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)originalLimit, (nuint)context.alloc_limit);
    }

    [Theory]
    [InlineData(0UL, 0UL)]
    [InlineData(1UL, 8UL)]
    [InlineData(7UL, 8UL)]
    [InlineData(8UL, 8UL)]
    [InlineData(9UL, 16UL)]
    [InlineData(ulong.MaxValue, 0UL)]
    public void AlignQwordPreservesEightByteBoundariesAndUncheckedOverflow(ulong value, ulong expected)
    {
        Assert.Equal((nuint)expected, gc_heap.AlignQword((nuint)value));
    }

    [Fact]
    public void AllocationContextRetirementAndVoidPreserveAccountingAndNullBehavior()
    {
        gc_alloc_context context = default;
        context.alloc_ptr = (byte*)0x1020;
        context.alloc_limit = (byte*)0x1040;
        context.alloc_bytes = 100;
        context.alloc_bytes_uoh = 17;
        ulong totalAllocatedBytesSoh = 500;

        gc_heap.retire_allocation_context(&context, &totalAllocatedBytesSoh);

        Assert.Equal(68, context.alloc_bytes);
        Assert.Equal(17, context.alloc_bytes_uoh);
        Assert.Equal(468ul, totalAllocatedBytesSoh);
        Assert.Equal((nuint)0, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)0, (nuint)context.alloc_limit);

        context.alloc_limit = (byte*)0x2000;
        gc_heap.void_allocation(&context);

        Assert.Equal((nuint)0, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)0x2000, (nuint)context.alloc_limit);
        Assert.Equal(68, context.alloc_bytes);
        Assert.Equal(468ul, totalAllocatedBytesSoh);
    }

#if USE_REGIONS
    [Fact]
    public void FixAllocationContextFormatsTheTailAndRetiresOnlyForGc()
    {
        void* oldFreeObjectMethodTable = GCCommon.g_gc_pFreeObjectMethodTable;
        byte* storage = stackalloc byte[128];
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        heap_segment segment = default;
        gc_alloc_context context = default;
        ulong totalAllocatedBytesSoh = 100;
        byte* allocAllocated = storage + 128;
        nuint alignedMinObjectSize = gc_heap.Align(
            (nuint)GCInterfaceOffsets.min_obj_size,
            gc_heap.get_alignment_constant(small_object_p: true));

        try
        {
            GCCommon.g_gc_pFreeObjectMethodTable = (void*)0x1234;
            segment.mem = storage;
            segment.reserved = storage + 128;

            context.alloc_ptr = storage + 32;
            context.alloc_limit = storage + 96;
            context.alloc_bytes = 100;
            gc_heap.fix_allocation_context(
                &context,
                false,
                false,
                generations,
                &segment,
                &allocAllocated,
                &totalAllocatedBytesSoh);

            Assert.Equal((nuint)(storage + 32), (nuint)context.alloc_ptr);
            Assert.Equal((nuint)(storage + 96), (nuint)context.alloc_limit);
            Assert.Equal(100L, context.alloc_bytes);
            Assert.Equal(100UL, totalAllocatedBytesSoh);
            Assert.Equal((nuint)GCCommon.g_gc_pFreeObjectMethodTable, *(nuint*)(storage + 32));

            context.alloc_ptr = storage + 32;
            context.alloc_limit = storage + 96;
            context.alloc_bytes = 100;
            gc_heap.fix_allocation_context(
                &context,
                true,
                false,
                generations,
                &segment,
                &allocAllocated,
                &totalAllocatedBytesSoh);

            Assert.Equal((nuint)0, (nuint)context.alloc_ptr);
            Assert.Equal((nuint)0, (nuint)context.alloc_limit);
            Assert.Equal(36L, context.alloc_bytes);
            Assert.Equal(36UL, totalAllocatedBytesSoh);
            Assert.Equal((nuint)(64 + alignedMinObjectSize), generation.generation_free_obj_space(generations));
            Assert.Equal((nuint)GCCommon.g_gc_pFreeObjectMethodTable, *(nuint*)(storage + 32));
        }
        finally
        {
            GCCommon.g_gc_pFreeObjectMethodTable = oldFreeObjectMethodTable;
        }
    }

    [Fact]
    public void FixAllocationContextIgnoresAnEmptyContext()
    {
        gc_alloc_context context = default;
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        heap_segment segment = default;
        byte* allocAllocated = (byte*)0x1000;
        ulong totalAllocatedBytesSoh = 17;

        gc_heap.fix_allocation_context(
            &context,
            true,
            false,
            generations,
            &segment,
            &allocAllocated,
            &totalAllocatedBytesSoh);

        Assert.Equal((nuint)0, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)0, (nuint)context.alloc_limit);
        Assert.Equal((nuint)0x1000, (nuint)allocAllocated);
        Assert.Equal(17UL, totalAllocatedBytesSoh);
    }

    [Fact]
    public void FixAllocationContextsEnumeratesEeContextsAndRepairsTheYoungestBoundary()
    {
        void* oldFreeObjectMethodTable = GCCommon.g_gc_pFreeObjectMethodTable;
        nuint oldAllocContextsUsed = gc_heap.alloc_contexts_used;
        byte* freeListStorage = stackalloc byte[128];
        byte* ephemeralStorage = stackalloc byte[256];
        gc_heap heap = default;
        heap_segment ephemeralSegment = default;
        gc_alloc_context nullContext = default;
        gc_alloc_context freeListContext = default;
        gc_alloc_context ephemeralContext = default;
        gc_alloc_context* nullContextPointer = &nullContext;
        gc_alloc_context* freeListContextPointer = &freeListContext;
        gc_alloc_context* ephemeralContextPointer = &ephemeralContext;
        nuint alignedMinObjectSize = gc_heap.Align(
            (nuint)GCInterfaceOffsets.min_obj_size,
            gc_heap.get_alignment_constant(small_object_p: true));

        try
        {
            GCToEEInterface.Reset();
            GCCommon.g_gc_pFreeObjectMethodTable = (void*)0x1234;
            gc_heap.alloc_contexts_used = 0;
            gc_heap.initialize_allocation_state(&heap);

            ephemeralSegment.mem = ephemeralStorage;
            ephemeralSegment.committed = ephemeralStorage + 256;
            ephemeralSegment.reserved = ephemeralStorage + 256;
            ephemeralSegment.allocated = ephemeralStorage + 32;
            heap.ephemeral_heap_segment = &ephemeralSegment;
            heap.total_alloc_bytes_soh = 1000;

            freeListContext.alloc_ptr = freeListStorage + 16;
            freeListContext.alloc_limit = freeListStorage + 48;
            freeListContext.alloc_bytes = 200;

            ephemeralContext.alloc_ptr = ephemeralStorage + 48;
            ephemeralContext.alloc_limit = ephemeralStorage + 80;
            ephemeralContext.alloc_bytes = 300;
            heap.alloc_allocated = ephemeralContext.alloc_limit + (nint)alignedMinObjectSize;

            GCToEEInterface.AllocContexts.Add((nuint)nullContextPointer);
            GCToEEInterface.AllocContexts.Add((nuint)freeListContextPointer);
            GCToEEInterface.AllocContexts.Add((nuint)ephemeralContextPointer);

            gc_heap.fix_allocation_contexts(&heap, for_gc_p: true);

            Assert.Equal(1, GCToEEInterface.GcEnumAllocContextsCallCount);
            Assert.NotEqual((nuint)0, GCToEEInterface.LastGcEnumAllocContextsCallback);
            Assert.NotEqual((nuint)0, GCToEEInterface.LastGcEnumAllocContextsParameter);
            Assert.Equal(3, GCToEEInterface.EnumeratedAllocContexts.Count);
            Assert.Equal((nuint)nullContextPointer, GCToEEInterface.EnumeratedAllocContexts[0]);
            Assert.Equal((nuint)freeListContextPointer, GCToEEInterface.EnumeratedAllocContexts[1]);
            Assert.Equal((nuint)ephemeralContextPointer, GCToEEInterface.EnumeratedAllocContexts[2]);

            Assert.Equal((nuint)0, (nuint)nullContext.alloc_ptr);
            Assert.Equal((nuint)0, (nuint)nullContext.alloc_limit);
            Assert.Equal((nuint)0, (nuint)freeListContext.alloc_ptr);
            Assert.Equal((nuint)0, (nuint)freeListContext.alloc_limit);
            Assert.Equal(168L, freeListContext.alloc_bytes);
            Assert.Equal((nuint)GCCommon.g_gc_pFreeObjectMethodTable, *(nuint*)(freeListStorage + 16));
            Assert.Equal(
                (nuint)(32 + alignedMinObjectSize - (nuint)GCInterfaceOffsets.min_obj_size),
                *(nuint*)(freeListStorage + 16 + (nint)sizeof(nuint)));

            Assert.Equal((nuint)0, (nuint)ephemeralContext.alloc_ptr);
            Assert.Equal((nuint)0, (nuint)ephemeralContext.alloc_limit);
            Assert.Equal(268L, ephemeralContext.alloc_bytes);
            Assert.Equal(936UL, heap.total_alloc_bytes_soh);
            Assert.Equal((nuint)32 + alignedMinObjectSize, generation.generation_free_obj_space(&heap.generation_table0));
            Assert.Equal((nuint)2, gc_heap.alloc_contexts_used);
            Assert.Equal((nuint)(ephemeralStorage + 48), (nuint)heap.alloc_allocated);
            Assert.Equal((nuint)(ephemeralStorage + 48), (nuint)ephemeralSegment.allocated);
        }
        finally
        {
            GCToEEInterface.Reset();
            GCCommon.g_gc_pFreeObjectMethodTable = oldFreeObjectMethodTable;
            gc_heap.alloc_contexts_used = oldAllocContextsUsed;
        }
    }
#endif

    [Fact]
    public void AllocationContextAccountingKeepsSohAndUohCountersDistinct()
    {
        gc_alloc_context context = default;
        context.alloc_bytes = 100;
        context.alloc_bytes_uoh = 200;
        ulong totalAllocatedBytesSoh = 300;

        gc_heap.add_alloc_bytes(&context, 24, &totalAllocatedBytesSoh);
        gc_heap.add_uoh_alloc_bytes(&context, 40);

        Assert.Equal(124, context.alloc_bytes);
        Assert.Equal(240, context.alloc_bytes_uoh);
        Assert.Equal(324ul, totalAllocatedBytesSoh);
    }

    [Fact]
    public void AllocationContextLimitAndSizeHelpersPreservePolicyBoundariesAndOverflow()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint alignedMinObjectSize = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        dynamic_data data = default;
        data.new_allocation = 64;

        Assert.Equal((nuint)48, gc_heap.new_allocation_limit(&data, 32, 48, (int)gc_generation_num.soh_gen0));
        Assert.Equal((nuint)48, gc_heap.limit_from_size(&data, 48, 8, 0, 128, (int)gc_generation_num.soh_gen0, alignment));
        Assert.Equal((nuint)32, gc_heap.limit_from_size(&data, 48, 8, (uint)GC_ALLOC_FLAGS.GC_ALLOC_ZEROING_OPTIONAL, 128, (int)gc_generation_num.soh_gen0, alignment));
        Assert.Equal((nuint)48, gc_heap.limit_from_size(&data, 48, unchecked(nuint.MaxValue - alignedMinObjectSize + 1), 0, 64, (int)gc_generation_num.soh_gen0, alignment));

        gc_alloc_context context = default;
        context.alloc_ptr = (byte*)0x1000;
        context.alloc_bytes = 100;
        ulong totalAllocatedBytes = 200;

        gc_heap.set_alloc_context_limit(&context, (byte*)0x1000, 64, (int)gc_generation_num.soh_gen0, alignment, &totalAllocatedBytes);

        Assert.Equal((nuint)0x1028, (nuint)context.alloc_limit);
        Assert.Equal(140, context.alloc_bytes);
        Assert.Equal(240ul, totalAllocatedBytes);

        context.alloc_bytes = 0;
        totalAllocatedBytes = 0;
        gc_heap.set_alloc_context_limit(&context, null, 0, (int)gc_generation_num.soh_gen0, alignment, &totalAllocatedBytes);

        Assert.Equal(unchecked(nuint.MaxValue - alignedMinObjectSize + 1), (nuint)context.alloc_limit);
        Assert.Equal(-(long)alignedMinObjectSize, context.alloc_bytes);
        Assert.Equal(unchecked(0ul - (ulong)alignedMinObjectSize), totalAllocatedBytes);
    }

    [Fact]
    public void MakeUnusedArrayAndFreeObjectWriteNativeObjectBytesAndAccounting()
    {
        byte* storage = stackalloc byte[128];
        byte* unusedArray = storage + sizeof(nuint);
        nuint minimumObjectSize = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size);
        void* savedFreeObjectMethodTable = GCCommon.g_gc_pFreeObjectMethodTable;
        GCCommon.g_gc_pFreeObjectMethodTable = (void*)0x12345000;

        try
        {
            for (int i = 0; i < 128; i++)
            {
                storage[i] = 0xcc;
            }

            gc_heap.make_unused_array(unusedArray, minimumObjectSize);

            Assert.Equal((nuint)0x12345000, *(nuint*)unusedArray);
            Assert.Equal((nuint)0, *(nuint*)(unusedArray + (nint)sizeof(nuint)));
            for (nint i = 2 * sizeof(nuint); i < (nint)minimumObjectSize; i++)
            {
                Assert.Equal((byte)0xcc, unusedArray[i]);
            }

            gc_heap.clear_unused_array(unusedArray, minimumObjectSize);

            Assert.Equal((nuint)0, *((nuint*)unusedArray - 1));
            Assert.Equal((nuint)0, *(nuint*)unusedArray);
            Assert.Equal((nuint)0, *(nuint*)(unusedArray + (nint)sizeof(nuint)));
            for (nint i = 2 * sizeof(nuint); i < (nint)minimumObjectSize; i++)
            {
                Assert.Equal((byte)0xcc, unusedArray[i]);
            }

            generation gen = default;
            nuint freeObjectSize = unchecked(2 * minimumObjectSize);
            gc_heap.make_free_obj(&gen, unusedArray, freeObjectSize);

            Assert.Equal((nuint)0x12345000, *(nuint*)unusedArray);
            Assert.Equal(minimumObjectSize, *(nuint*)(unusedArray + (nint)sizeof(nuint)));
            Assert.Equal((byte)0xcc, unusedArray[2 * sizeof(nuint)]);
#if TARGET_64BIT && !TARGET_WASM
            Assert.Equal((nuint)1, (nuint)((byte**)unusedArray)[3]);
#endif
            Assert.Equal(freeObjectSize, generation.generation_free_obj_space(&gen));
        }
        finally
        {
            GCCommon.g_gc_pFreeObjectMethodTable = savedFreeObjectMethodTable;
        }
    }

#if USE_REGIONS
    [Fact]
    public void AdjustLimitClrFillsDiscontinuousContextHoleAndPreservesAccounting()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint alignedMinObjectSize = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        byte* storage = stackalloc byte[256];
        byte* hole = storage + 32;
        byte* start = storage + 128;
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generation.initialize(&generations[i]);
        }

        heap_segment ephemeral = default;
        gc_alloc_context context = default;
        context.alloc_ptr = hole;
        context.alloc_limit = hole + 32;
        context.alloc_bytes = 100;
        ulong totalAllocatedBytes = 200;
        void* savedFreeObjectMethodTable = GCCommon.g_gc_pFreeObjectMethodTable;
        GCCommon.g_gc_pFreeObjectMethodTable = (void*)0x12345000;

        try
        {
            gc_heap.adjust_limit_clr(
                start,
                64,
                64,
                &context,
                0,
                null,
                alignment,
                (int)gc_generation_num.soh_gen0,
                generations,
                &ephemeral,
                null,
                &totalAllocatedBytes,
                null,
                null);

            Assert.Equal((nuint)start, (nuint)context.alloc_ptr);
            Assert.Equal((nuint)(start + (nint)((nuint)64 - alignedMinObjectSize)), (nuint)context.alloc_limit);
            Assert.Equal(108, context.alloc_bytes);
            Assert.Equal(208ul, totalAllocatedBytes);
            Assert.Equal(32 + alignedMinObjectSize, generation.generation_free_obj_space(generations));
            Assert.Equal((nuint)0x12345000, *(nuint*)hole);
            Assert.Equal((nuint)32, *(nuint*)(hole + (nint)sizeof(nuint)));
        }
        finally
        {
            GCCommon.g_gc_pFreeObjectMethodTable = savedFreeObjectMethodTable;
        }
    }

    [Fact]
    public void AdjustLimitClrStartsNullRegionContextAndPublishesEphemeralUsed()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint alignedMinObjectSize = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        byte* storage = stackalloc byte[256];
        byte* start = storage + 32;
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generation.initialize(&generations[i]);
        }

        heap_segment segment = default;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = storage + 192;
        heap_segment.heap_segment_reserved(&segment) = storage + 192;
        gc_alloc_context context = default;
        context.alloc_limit = start;
        byte* allocAllocated = start;
        ulong totalAllocatedBytes = 0;

        gc_heap.advance_allocated(&allocAllocated, &segment, 64, (int)gc_generation_num.soh_gen0);
        gc_heap.adjust_limit_clr(
            start,
            64,
            64,
            &context,
            0,
            &segment,
            alignment,
            (int)gc_generation_num.soh_gen0,
            generations,
            &segment,
            allocAllocated,
            &totalAllocatedBytes,
            null,
            null);

        Assert.Equal((nuint)start, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)(start + (nint)((nuint)64 - alignedMinObjectSize)), (nuint)context.alloc_limit);
        Assert.Equal((long)((nuint)64 - alignedMinObjectSize), context.alloc_bytes);
        Assert.Equal((ulong)((nuint)64 - alignedMinObjectSize), totalAllocatedBytes);
        Assert.Equal((nuint)(start + 64), (nuint)allocAllocated);
        Assert.Equal((nuint)(allocAllocated - sizeof(nuint)), (nuint)heap_segment.heap_segment_used(&segment));
        Assert.True(heap_segment.heap_segment_mem(&segment) <= heap_segment.heap_segment_used(&segment));
        Assert.True(heap_segment.heap_segment_used(&segment) <= heap_segment.heap_segment_committed(&segment));
        Assert.True(heap_segment.heap_segment_used(&segment) <= heap_segment.heap_segment_reserved(&segment));
    }

    [Fact]
    public void AdjustLimitClrClearsFullyDirtyAndPartiallyUnusedSpans()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint limitSize = 64;
        byte* storage = stackalloc byte[384];
        for (int i = 0; i < 384; i++)
        {
            storage[i] = 0xcc;
        }

        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        InitializeAllocationGenerations(generations);
        gc_alloc_context fullyDirtyContext = default;
        byte* fullyDirtyStart = storage + 64;
        fullyDirtyContext.alloc_limit = fullyDirtyStart;
        heap_segment fullyDirtySegment = default;
        heap_segment.heap_segment_used(&fullyDirtySegment) = fullyDirtyStart + (nint)(limitSize - (nuint)sizeof(nuint));
        heap_segment.heap_segment_committed(&fullyDirtySegment) = storage + 384;
        heap_segment.heap_segment_reserved(&fullyDirtySegment) = storage + 384;
        ulong totalAllocatedBytes = 0;

        gc_heap.adjust_limit_clr(
            fullyDirtyStart,
            limitSize,
            limitSize,
            &fullyDirtyContext,
            0,
            &fullyDirtySegment,
            alignment,
            (int)gc_generation_num.soh_gen0,
            generations,
            null,
            null,
            &totalAllocatedBytes,
            null,
            null);

        byte* fullyDirtyLimit = fullyDirtyStart + (nint)(limitSize - (nuint)sizeof(nuint));
        for (byte* p = fullyDirtyStart - sizeof(nuint); p < fullyDirtyLimit; p++)
        {
            Assert.Equal((byte)0, *p);
        }

        Assert.Equal((nuint)fullyDirtyLimit, (nuint)heap_segment.heap_segment_used(&fullyDirtySegment));

        gc_alloc_context partiallyUnusedContext = default;
        byte* partiallyUnusedStart = storage + 192;
        partiallyUnusedContext.alloc_limit = partiallyUnusedStart;
        heap_segment partiallyUnusedSegment = default;
        byte* oldUsed = partiallyUnusedStart + 24;
        heap_segment.heap_segment_used(&partiallyUnusedSegment) = oldUsed;
        heap_segment.heap_segment_committed(&partiallyUnusedSegment) = storage + 384;
        heap_segment.heap_segment_reserved(&partiallyUnusedSegment) = storage + 384;
        try_allocate_more_space_context moreSpaceContext = default;
        moreSpaceContext.state = allocation_state.a_state_can_allocate;
        moreSpaceContext.more_space_lock_held_p = 1;
        s_adjustLimitSegment = &partiallyUnusedSegment;
        s_adjustLimitExpectedUsed = partiallyUnusedStart + (nint)(limitSize - (nuint)sizeof(nuint));
        s_adjustLimitUsedPublishedAtRelease = 0;

        gc_heap.adjust_limit_clr(
            partiallyUnusedStart,
            limitSize,
            limitSize,
            &partiallyUnusedContext,
            0,
            &partiallyUnusedSegment,
            alignment,
            (int)gc_generation_num.soh_gen0,
            generations,
            null,
            null,
            &totalAllocatedBytes,
            &moreSpaceContext,
            &AdjustLimitReleaseCallback);

        byte* partiallyUnusedLimit = partiallyUnusedStart + (nint)(limitSize - (nuint)sizeof(nuint));
        for (byte* p = partiallyUnusedStart - sizeof(nuint); p < oldUsed; p++)
        {
            Assert.Equal((byte)0, *p);
        }

        for (byte* p = oldUsed; p < partiallyUnusedLimit; p++)
        {
            Assert.Equal((byte)0xcc, *p);
        }

        Assert.Equal((nuint)partiallyUnusedLimit, (nuint)heap_segment.heap_segment_used(&partiallyUnusedSegment));
        Assert.Equal(1, s_adjustLimitUsedPublishedAtRelease);
        Assert.Equal((byte)0, moreSpaceContext.more_space_lock_held_p);
    }

    [Fact]
    public void AdjustLimitClrZeroingOptionalClearsSyncBlockAndSkipsObject()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint size = 32;
        nuint limitSize = 64;
        byte* storage = stackalloc byte[192];
        for (int i = 0; i < 192; i++)
        {
            storage[i] = 0xcc;
        }

        byte* start = storage + 64;
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        InitializeAllocationGenerations(generations);
        gc_alloc_context context = default;
        context.alloc_limit = start;
        heap_segment segment = default;
        byte* clearLimit = start + (nint)(limitSize - (nuint)sizeof(nuint));
        heap_segment.heap_segment_used(&segment) = clearLimit;
        heap_segment.heap_segment_committed(&segment) = storage + 192;
        heap_segment.heap_segment_reserved(&segment) = storage + 192;
        ulong totalAllocatedBytes = 0;

        gc_heap.adjust_limit_clr(
            start,
            limitSize,
            size,
            &context,
            (uint)GC_ALLOC_FLAGS.GC_ALLOC_ZEROING_OPTIONAL,
            &segment,
            alignment,
            (int)gc_generation_num.soh_gen0,
            generations,
            null,
            null,
            &totalAllocatedBytes,
            null,
            null);

        byte* objectEnd = start + (nint)(size - (nuint)sizeof(nuint));
        Assert.Equal((nuint)0, *(nuint*)(start - sizeof(nuint)));
        for (byte* p = start; p < objectEnd; p++)
        {
            Assert.Equal((byte)0xcc, *p);
        }

        for (byte* p = objectEnd; p < clearLimit; p++)
        {
            Assert.Equal((byte)0, *p);
        }
    }

    [Fact]
    public void AdjustLimitClrPadsContiguousGen0Context()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint alignedMinObjectSize = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        byte* storage = stackalloc byte[256];
        byte* start = storage + 64;
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generation.initialize(&generations[i]);
        }

        heap_segment ephemeral = default;
        gc_alloc_context context = default;
        context.alloc_ptr = start;
        context.alloc_limit = start;
        context.alloc_bytes = 5;
        ulong totalAllocatedBytes = 7;
        void* savedFreeObjectMethodTable = GCCommon.g_gc_pFreeObjectMethodTable;
        GCCommon.g_gc_pFreeObjectMethodTable = (void*)0x12345000;

        try
        {
            gc_heap.adjust_limit_clr(
                start,
                64,
                64,
                &context,
                0,
                null,
                alignment,
                (int)gc_generation_num.soh_gen0,
                generations,
                &ephemeral,
                null,
                &totalAllocatedBytes,
                null,
                null);

            Assert.Equal((nuint)(start + (nint)alignedMinObjectSize), (nuint)context.alloc_ptr);
            Assert.Equal((nuint)(start + (nint)((nuint)64 - alignedMinObjectSize)), (nuint)context.alloc_limit);
            Assert.Equal(45, context.alloc_bytes);
            Assert.Equal(47ul, totalAllocatedBytes);
            Assert.Equal((nuint)0, *(nuint*)start);
            Assert.Equal((nuint)0, *(nuint*)(start + (nint)sizeof(nuint)));
            Assert.Equal((nuint)0, generation.generation_free_obj_space(generations));
        }
        finally
        {
            GCCommon.g_gc_pFreeObjectMethodTable = savedFreeObjectMethodTable;
        }
    }

    [Fact]
    public void AdjustLimitClrKeepsUohAccountingAndAdvancesSegmentAllocation()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: false);
        nuint alignedMinObjectSize = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        byte* storage = stackalloc byte[256];
        byte* start = storage + 32;
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generation.initialize(&generations[i]);
        }

        heap_segment segment = default;
        heap_segment ephemeral = default;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = storage + 192;
        heap_segment.heap_segment_reserved(&segment) = storage + 192;
        gc_alloc_context context = default;
        context.alloc_ptr = start;
        context.alloc_limit = start;
        context.alloc_bytes = 100;
        context.alloc_bytes_uoh = 200;
        ulong totalAllocatedBytesUoh = 300;

        gc_heap.advance_allocated(null, &segment, 64, (int)gc_generation_num.loh_generation);
        gc_heap.adjust_limit_clr(
            start,
            64,
            64,
            &context,
            0,
            &segment,
            alignment,
            (int)gc_generation_num.loh_generation,
            generations,
            &ephemeral,
            null,
            &totalAllocatedBytesUoh,
            null,
            null);

        Assert.Equal((nuint)start, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)(start + (nint)((nuint)64 - alignedMinObjectSize)), (nuint)context.alloc_limit);
        Assert.Equal(164, context.alloc_bytes);
        Assert.Equal(200, context.alloc_bytes_uoh);
        Assert.Equal(364ul, totalAllocatedBytesUoh);
        Assert.Equal((nuint)(start + 64), (nuint)heap_segment.heap_segment_allocated(&segment));
        Assert.Equal((nuint)(start + (nint)((nuint)64 - (nuint)sizeof(nuint))), (nuint)heap_segment.heap_segment_used(&segment));
    }

    [Fact]
    public void FitSegmentEndUsesExactCommittedSohSpaceAndHandsOffAllocationContext()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[256];
        byte* start = storage + 32;
        heap_segment segment = default;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = (byte*)unchecked((nuint)start + size + (2 * pad));
        heap_segment.heap_segment_reserved(&segment) = heap_segment.heap_segment_committed(&segment);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(size + pad));
        gc_alloc_context context = default;
        byte* allocAllocated = start;
        ulong totalAllocatedBytes = 0;
        bool commitFailed = true;

        Assert.True(gc_heap.a_fit_segment_end_p(
            (int)gc_generation_num.soh_gen0,
            &segment,
            size,
            &context,
            0,
            alignment,
            &commitFailed,
            &data,
            0,
            generations,
            &segment,
            &allocAllocated,
            &totalAllocatedBytes,
            0));

        Assert.False(commitFailed);
        Assert.Equal((nuint)(start + (nint)(size + pad)), (nuint)allocAllocated);
        Assert.Equal((nuint)start, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)(start + (nint)size), (nuint)context.alloc_limit);
        Assert.Equal((long)size, context.alloc_bytes);
        Assert.Equal((ulong)size, totalAllocatedBytes);
        Assert.Equal((nint)0, data.new_allocation);
        Assert.Equal((nuint)(allocAllocated - sizeof(nuint)), (nuint)heap_segment.heap_segment_used(&segment));
    }

    [Fact]
    public void FitSegmentEndRejectsShortCommittedSegmentWithoutChangingState()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[256];
        byte* start = storage + 32;
        heap_segment segment = default;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = (byte*)unchecked((nuint)start + (2 * pad));
        heap_segment.heap_segment_reserved(&segment) = heap_segment.heap_segment_committed(&segment);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(size + pad));
        gc_alloc_context context = default;
        byte* allocAllocated = start;
        ulong totalAllocatedBytes = 0;
        bool commitFailed = true;

        Assert.False(gc_heap.a_fit_segment_end_p(
            (int)gc_generation_num.soh_gen0,
            &segment,
            size,
            &context,
            0,
            alignment,
            &commitFailed,
            &data,
            0,
            generations,
            &segment,
            &allocAllocated,
            &totalAllocatedBytes,
            0));

        Assert.False(commitFailed);
        Assert.Equal((nuint)start, (nuint)allocAllocated);
        Assert.Equal((nuint)start, (nuint)heap_segment.heap_segment_allocated(&segment));
        Assert.Equal((nuint)start, (nuint)heap_segment.heap_segment_used(&segment));
        Assert.Equal((nint)(size + pad), data.new_allocation);
        Assert.Equal((nuint)0, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)0, (nuint)context.alloc_limit);
        Assert.Equal((ulong)0, totalAllocatedBytes);
    }

    [Fact]
    public void FitSegmentEndGrowsCommittedRegionAndUpdatesCommitAccounting()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: true);
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint pageSize = GCToOSInterface.GetPageSize();
        byte* reservation = GCToOSInterface.VirtualReserve(4 * pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation is not null);

        try
        {
            Assert.True(GCToOSInterface.VirtualCommit(reservation, pageSize));

            byte* start = reservation + sizeof(aligned_plug_and_gap);
            heap_segment segment = default;
            heap_segment.heap_segment_mem(&segment) = start;
            heap_segment.heap_segment_allocated(&segment) = start;
            heap_segment.heap_segment_used(&segment) = start;
            heap_segment.heap_segment_committed(&segment) = reservation + (nint)pageSize;
            heap_segment.heap_segment_reserved(&segment) = reservation + (nint)(4 * pageSize);
            generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
            for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
            {
                generations[i] = default;
                generation.initialize(&generations[i]);
            }

            dynamic_data data = default;
            data.new_allocation = unchecked((nint)(pageSize + pad));
            gc_alloc_context context = default;
            byte* allocAllocated = start;
            ulong totalAllocatedBytes = 0;
            bool commitFailed = true;

            Assert.True(gc_heap.a_fit_segment_end_p(
                (int)gc_generation_num.soh_gen0,
                &segment,
                pageSize,
                &context,
                0,
                alignment,
                &commitFailed,
                &data,
                0,
                generations,
                &segment,
                &allocAllocated,
                &totalAllocatedBytes,
                0));

            Assert.False(commitFailed);
            Assert.Equal((nuint)(reservation + (nint)(4 * pageSize)), (nuint)heap_segment.heap_segment_committed(&segment));
            Assert.Equal((nuint)(start + (nint)(pageSize + pad)), (nuint)allocAllocated);
            Assert.Equal((nuint)(start + (nint)pageSize), (nuint)context.alloc_limit);
            Assert.Equal((ulong)pageSize, totalAllocatedBytes);
            Assert.Equal((nint)0, data.new_allocation);
            Assert.Equal(3 * pageSize, gc_heap.committed_by_oh[(int)gc_oh_num.soh]);
            Assert.Equal(3 * pageSize, gc_heap.current_total_committed);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(reservation, 4 * pageSize);
        }
    }

    [Fact]
    public void FitSegmentEndPropagatesCommitFailure()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: true);
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint pageSize = GCToOSInterface.GetPageSize();
        byte* start = (byte*)0x2000;
        heap_segment segment = default;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = start + (nint)pageSize;
        heap_segment.heap_segment_reserved(&segment) = start + (nint)(2 * pageSize);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(pageSize + pad));
        gc_alloc_context context = default;
        byte* allocAllocated = start;
        ulong totalAllocatedBytes = 0;
        bool commitFailed = false;

        Assert.False(gc_heap.a_fit_segment_end_p(
            (int)gc_generation_num.soh_gen0,
            &segment,
            pageSize,
            &context,
            0,
            alignment,
            &commitFailed,
            &data,
            0,
            generations,
            &segment,
            &allocAllocated,
            &totalAllocatedBytes,
            0));

        Assert.True(commitFailed);
        Assert.Equal((nuint)(start + (nint)pageSize), (nuint)heap_segment.heap_segment_committed(&segment));
        Assert.Equal((nuint)start, (nuint)allocAllocated);
        Assert.Equal((nint)(pageSize + pad), data.new_allocation);
        Assert.Equal((nuint)0, gc_heap.current_total_committed);
    }

    [Fact]
    public void GrowHeapSegmentReportsHardLimitBeforeCommitting()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: true);
        nuint pageSize = GCToOSInterface.GetPageSize();
        byte* committed = (byte*)0x3000;
        heap_segment segment = default;
        heap_segment.heap_segment_committed(&segment) = committed;
        heap_segment.heap_segment_reserved(&segment) = committed + (nint)pageSize;
        gc_heap.heap_hard_limit = pageSize - 1;
        bool hardLimitExceeded = false;

        Assert.False(gc_heap.grow_heap_segment(&segment, committed + 1, 0, &hardLimitExceeded));

        Assert.True(hardLimitExceeded);
        Assert.Equal((nuint)committed, (nuint)heap_segment.heap_segment_committed(&segment));
        Assert.Equal((nuint)0, gc_heap.current_total_committed);
    }

    [Fact]
    public void FitSegmentEndSelectsUohSegmentAllocationPointer()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: false);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[256];
        byte* start = storage + 32;
        heap_segment segment = default;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = (byte*)unchecked((nuint)start + size + (2 * pad));
        heap_segment.heap_segment_reserved(&segment) = heap_segment.heap_segment_committed(&segment);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(size + pad));
        gc_alloc_context context = default;
        byte* allocAllocated = storage + 224;
        ulong totalAllocatedBytes = 0;
        bool commitFailed = true;

        Assert.True(gc_heap.a_fit_segment_end_p(
            (int)gc_generation_num.loh_generation,
            &segment,
            size,
            &context,
            0,
            alignment,
            &commitFailed,
            &data,
            0,
            generations,
            null,
            &allocAllocated,
            &totalAllocatedBytes,
            0));

        Assert.False(commitFailed);
        Assert.Equal((nuint)(start + (nint)(size + pad)), (nuint)heap_segment.heap_segment_allocated(&segment));
        Assert.Equal((nuint)(storage + 224), (nuint)allocAllocated);
        Assert.Equal((nuint)start, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)(start + (nint)size), (nuint)context.alloc_limit);
        Assert.Equal((nuint)(start + (nint)(size + pad - (nuint)sizeof(nuint))), (nuint)heap_segment.heap_segment_used(&segment));
        Assert.Equal((long)(size + pad), context.alloc_bytes);
        Assert.Equal((ulong)(size + pad), totalAllocatedBytes);
    }

    [Fact]
    public void UohFitSegmentEndSkipsShortSegmentAndTracksEndSegmentAllocation()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: false);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[512];
        heap_segment* segments = stackalloc heap_segment[2];
        segments[0] = default;
        segments[1] = default;
        byte* firstStart = storage + 32;
        byte* secondStart = storage + 256;
        heap_segment.heap_segment_mem(&segments[0]) = firstStart;
        heap_segment.heap_segment_allocated(&segments[0]) = firstStart;
        heap_segment.heap_segment_used(&segments[0]) = firstStart;
        heap_segment.heap_segment_committed(&segments[0]) = (byte*)unchecked((nuint)firstStart + (2 * pad));
        heap_segment.heap_segment_reserved(&segments[0]) = heap_segment.heap_segment_committed(&segments[0]);
        heap_segment.heap_segment_next(&segments[0]) = &segments[1];
        heap_segment.heap_segment_mem(&segments[1]) = secondStart;
        heap_segment.heap_segment_allocated(&segments[1]) = secondStart;
        heap_segment.heap_segment_used(&segments[1]) = secondStart;
        heap_segment.heap_segment_committed(&segments[1]) = (byte*)unchecked((nuint)secondStart + size + pad);
        heap_segment.heap_segment_reserved(&segments[1]) = heap_segment.heap_segment_committed(&segments[1]);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        generation.generation_allocation_segment(
            gc_heap.generation_of(generations, (int)gc_generation_num.loh_generation)) = &segments[0];
        dynamic_data data = default;
        data.new_allocation = unchecked((nint)size);
        gc_alloc_context context = default;
        ulong totalAllocatedBytes = 0;
        bool commitFailed = true;
        oom_reason oomReason = oom_reason.oom_no_failure;

        Assert.True(gc_heap.uoh_a_fit_segment_end_p(
            (int)gc_generation_num.loh_generation,
            size,
            &context,
            0,
            alignment,
            &commitFailed,
            &oomReason,
            &data,
            0,
            generations,
            null,
            null,
            &totalAllocatedBytes,
            0));

        Assert.False(commitFailed);
        Assert.Equal(oom_reason.oom_no_failure, oomReason);
        Assert.Equal((nuint)firstStart, (nuint)heap_segment.heap_segment_allocated(&segments[0]));
        Assert.Equal((nuint)(secondStart + (nint)size), (nuint)heap_segment.heap_segment_allocated(&segments[1]));
        Assert.Equal((nuint)secondStart, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)(secondStart + (nint)size), (nuint)context.alloc_limit);
        Assert.Equal(size, generation.generation_end_seg_allocated(
            gc_heap.generation_of(generations, (int)gc_generation_num.loh_generation)));
        Assert.Equal((nint)0, data.new_allocation);
        Assert.Equal((ulong)size, totalAllocatedBytes);
    }

    [Fact]
    public void SohFreeListExactFitHandsOffAllocationContextWithPadding()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        nuint freeSize = unchecked(size + pad);
        byte* storage = stackalloc byte[128];
        for (int i = 0; i < 128; i++)
        {
            storage[i] = 0;
        }

        byte* freeItem = storage + sizeof(nuint);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        dynamic_data data = default;
        data.new_allocation = (nint)freeSize;
        gc_alloc_context context = default;
        ulong totalAllocatedBytesSoh = 0;
        generation* gen = gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0);
        gc_heap.thread_free_item_front(gen, freeItem, freeSize);

        Assert.True(gc_heap.a_fit_free_list_p(
            (int)gc_generation_num.soh_gen0,
            size,
            &context,
            0,
            alignment,
            &data,
            0,
            generations,
            &totalAllocatedBytesSoh));

        Assert.Equal((nuint)freeItem, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)(freeItem + (nint)size), (nuint)context.alloc_limit);
        Assert.Equal((nuint)(context.alloc_limit + (nint)pad), (nuint)(freeItem + (nint)freeSize));
        Assert.Equal((long)size, context.alloc_bytes);
        Assert.Equal((ulong)size, totalAllocatedBytesSoh);
        Assert.Equal((nint)0, data.new_allocation);
        Assert.Equal((nuint)0, generation.generation_free_list_space(gen));
        Assert.Equal((nuint)0, (nuint)allocator.alloc_list_head_of(generation.generation_allocator(gen), 0));
        Assert.Equal((nuint)0, (nuint)allocator.alloc_list_tail_of(generation.generation_allocator(gen), 0));
    }

#if TARGET_64BIT && !TARGET_WASM
    [Fact]
    public void AllocatorFrontThreadAndUnlinkPreserveDoublyLinkedMetadata()
    {
        byte* storage = stackalloc byte[256];
        byte* first = storage + 32;
        byte* second = storage + 128;
        nuint freeSize = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size * 3);
        alloc_list bucket = default;
        allocator freeListAllocator = new(
            num_b: 2,
            fbb: 5,
            b: &bucket,
            gen: (int)gc_generation_num.max_generation);
        void* savedFreeObjectMethodTable = GCCommon.g_gc_pFreeObjectMethodTable;
        GCCommon.g_gc_pFreeObjectMethodTable = (void*)0x12345000;

        try
        {
            gc_heap.make_unused_array(first, freeSize);
            gc_heap.make_unused_array(second, freeSize);

            allocator.thread_item_front(&freeListAllocator, first, freeSize);
            allocator.thread_item_front(&freeListAllocator, second, freeSize);

            uint bucketIndex = freeListAllocator.first_suitable_bucket(freeSize);
            Assert.Equal((nuint)second, (nuint)allocator.alloc_list_head_of(&freeListAllocator, bucketIndex));
            Assert.Equal((nuint)first, (nuint)allocator.alloc_list_tail_of(&freeListAllocator, bucketIndex));
            Assert.Equal((nuint)0, (nuint)((byte**)second)[3]);
            Assert.Equal((nuint)second, (nuint)((byte**)first)[3]);

            allocator.unlink_item(&freeListAllocator, bucketIndex, second, null, use_undo_p: false);

            Assert.Equal((nuint)first, (nuint)allocator.alloc_list_head_of(&freeListAllocator, bucketIndex));
            Assert.Equal((nuint)first, (nuint)allocator.alloc_list_tail_of(&freeListAllocator, bucketIndex));
            Assert.Equal((nuint)1, (nuint)((byte**)second)[3]);
        }
        finally
        {
            GCCommon.g_gc_pFreeObjectMethodTable = savedFreeObjectMethodTable;
        }
    }
#endif

    [Fact]
    public void SohFreeListSplitRetainsMinimumRemainder()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        nuint limit = unchecked(size + pad);
        nuint remainderSize = unchecked(2 * pad);
        byte* storage = stackalloc byte[192];
        for (int i = 0; i < 192; i++)
        {
            storage[i] = 0;
        }

        byte* freeItem = storage + sizeof(nuint);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        dynamic_data data = default;
        data.new_allocation = (nint)limit;
        gc_alloc_context context = default;
        ulong totalAllocatedBytesSoh = 0;
        generation* gen = gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0);
        gc_heap.thread_free_item_front(gen, freeItem, unchecked(limit + remainderSize));

        Assert.True(gc_heap.a_fit_free_list_p(
            (int)gc_generation_num.soh_gen0,
            size,
            &context,
            0,
            alignment,
            &data,
            0,
            generations,
            &totalAllocatedBytesSoh));

        byte* remainder = freeItem + (nint)limit;
        Assert.Equal((nuint)remainder, (nuint)allocator.alloc_list_head_of(generation.generation_allocator(gen), 0));
        Assert.Equal((nuint)remainder, (nuint)allocator.alloc_list_tail_of(generation.generation_allocator(gen), 0));
        Assert.Equal(remainderSize, gc_heap.unused_array_size(remainder));
        Assert.Equal(remainderSize, generation.generation_free_list_space(gen));
        Assert.Equal((nuint)(freeItem + (nint)size), (nuint)context.alloc_limit);
        Assert.Equal((long)size, context.alloc_bytes);
        Assert.Equal((ulong)size, totalAllocatedBytesSoh);
    }

    [Fact]
    public void SohFreeListAbsorbsTooSmallRemainder()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        nuint limit = unchecked(size + pad);
        byte* storage = stackalloc byte[160];
        for (int i = 0; i < 160; i++)
        {
            storage[i] = 0;
        }

        byte* freeItem = storage + sizeof(nuint);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        dynamic_data data = default;
        data.new_allocation = (nint)limit;
        gc_alloc_context context = default;
        ulong totalAllocatedBytesSoh = 0;
        generation* gen = gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0);
        gc_heap.thread_free_item_front(gen, freeItem, unchecked(limit + pad));

        Assert.True(gc_heap.a_fit_free_list_p(
            (int)gc_generation_num.soh_gen0,
            size,
            &context,
            0,
            alignment,
            &data,
            0,
            generations,
            &totalAllocatedBytesSoh));

        Assert.Equal((nuint)0, generation.generation_free_list_space(gen));
        Assert.Equal((nuint)0, generation.generation_free_obj_space(gen));
        Assert.Equal((nuint)0, (nuint)allocator.alloc_list_head_of(generation.generation_allocator(gen), 0));
        Assert.Equal((nuint)(freeItem + (nint)(size + pad)), (nuint)context.alloc_limit);
        Assert.Equal((long)(size + pad), context.alloc_bytes);
        Assert.Equal((ulong)(size + pad), totalAllocatedBytesSoh);
        Assert.Equal((nint)0, data.new_allocation);
    }

    [Fact]
    public void SohFreeListTraversesBucketChainAndRemovesMatchedTail()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        byte* storage = stackalloc byte[512];
        for (int i = 0; i < 512; i++)
        {
            storage[i] = 0;
        }

        alloc_list* buckets = stackalloc alloc_list[2];
        for (int i = 0; i < 2; i++)
        {
            buckets[i] = default;
        }

        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        generation* gen = gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0);
        gen->free_list_allocator = new allocator(3, 6, buckets);
        byte* fittingItem = storage + sizeof(nuint);
        byte* tooSmallItem = storage + 256 + sizeof(nuint);
        gc_heap.thread_free_item_front(gen, fittingItem, 192);
        gc_heap.thread_free_item_front(gen, tooSmallItem, 128);

        dynamic_data data = default;
        data.new_allocation = 168;
        gc_alloc_context context = default;
        ulong totalAllocatedBytesSoh = 0;

        Assert.True(gc_heap.a_fit_free_list_p(
            (int)gc_generation_num.soh_gen0,
            144,
            &context,
            0,
            alignment,
            &data,
            0,
            generations,
            &totalAllocatedBytesSoh));

        uint bucket = generation.generation_allocator(gen)->first_suitable_bucket(144);
        Assert.Equal((nuint)tooSmallItem, (nuint)allocator.alloc_list_head_of(generation.generation_allocator(gen), bucket));
        Assert.Equal((nuint)tooSmallItem, (nuint)allocator.alloc_list_tail_of(generation.generation_allocator(gen), bucket));
        Assert.Equal((nuint)0, (nuint)allocator.free_list_slot(tooSmallItem));
        Assert.Equal((nuint)128, generation.generation_free_list_space(gen));
        Assert.Equal((nuint)fittingItem, (nuint)context.alloc_ptr);
        Assert.Equal((nint)0, data.new_allocation);
    }

    [Fact]
    public void UohFreeListFitPreservesUohAccountingAndPadding()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: false);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[192];
        for (int i = 0; i < 192; i++)
        {
            storage[i] = 0;
        }

        byte* freeItem = storage + sizeof(nuint);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        dynamic_data data = default;
        data.new_allocation = (nint)size;
        gc_alloc_context context = default;
        context.alloc_bytes = 10;
        context.alloc_bytes_uoh = 20;
        ulong totalAllocatedBytesUoh = 30;
        generation* gen = gc_heap.generation_of(generations, (int)gc_generation_num.loh_generation);
        gc_heap.thread_free_item_front(gen, freeItem, unchecked(2 * size));

        Assert.True(gc_heap.a_fit_free_list_uoh_p(
            size,
            &context,
            0,
            alignment,
            (int)gc_generation_num.loh_generation,
            &data,
            0,
            generations,
            &totalAllocatedBytesUoh));

        byte* remainder = freeItem + (nint)size;
        Assert.Equal((nuint)freeItem, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)(freeItem + (nint)size), (nuint)context.alloc_limit);
        Assert.Equal((long)size + 10, context.alloc_bytes);
        Assert.Equal(20, context.alloc_bytes_uoh);
        Assert.Equal((ulong)size + 30, totalAllocatedBytesUoh);
        Assert.Equal(size, generation.generation_free_list_allocated(gen));
        Assert.Equal(size, generation.generation_free_list_space(gen));
        Assert.Equal((nuint)remainder, (nuint)allocator.alloc_list_head_of(generation.generation_allocator(gen), 0));
        Assert.Equal(size, gc_heap.unused_array_size(remainder));
        Assert.Equal((nint)0, data.new_allocation);
    }

    [Fact]
    public void SohTryFitUsesFreeListBeforeSegmentEnd()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[256];
        for (int i = 0; i < 256; i++)
        {
            storage[i] = 0;
        }

        heap_segment segment = default;
        byte* segmentStart = storage + 160;
        heap_segment.heap_segment_mem(&segment) = segmentStart;
        heap_segment.heap_segment_allocated(&segment) = segmentStart;
        heap_segment.heap_segment_used(&segment) = segmentStart;
        heap_segment.heap_segment_committed(&segment) = segmentStart + (nint)pad;
        heap_segment.heap_segment_reserved(&segment) = heap_segment.heap_segment_committed(&segment);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        generation* gen0 = gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0);
        byte* freeItem = storage + sizeof(nuint);
        gc_heap.thread_free_item_front(gen0, freeItem, unchecked(size + pad));
        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(size + pad));
        gc_alloc_context context = default;
        heap_segment* ephemeral = &segment;
        byte* allocAllocated = segmentStart;
        ulong totalAllocatedBytesSoh = 0;
        bool commitFailed = false;
        bool shortSegmentEnd = true;

        Assert.True(gc_heap.soh_try_fit(
            (int)gc_generation_num.soh_gen0,
            size,
            &context,
            0,
            alignment,
            &commitFailed,
            &shortSegmentEnd,
            sufficient_space_regions_for_allocation_p: false,
            sufficient_gen0_space_p: false,
            &data,
            0,
            generations,
            &ephemeral,
            &allocAllocated,
            &totalAllocatedBytesSoh,
            0,
            null));

        Assert.False(commitFailed);
        Assert.False(shortSegmentEnd);
        Assert.Equal((nuint)freeItem, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)(freeItem + (nint)size), (nuint)context.alloc_limit);
        Assert.Equal((nuint)segmentStart, (nuint)allocAllocated);
        Assert.Equal((nuint)0, (nuint)allocator.alloc_list_head_of(generation.generation_allocator(gen0), 0));
        Assert.Equal((nint)0, data.new_allocation);
    }

    [Fact]
    public void SohTryFitFallsBackToSegmentEnd()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[256];
        byte* start = storage + 32;
        heap_segment segment = default;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = start + (nint)(size + (2 * pad));
        heap_segment.heap_segment_reserved(&segment) = heap_segment.heap_segment_committed(&segment);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        generation.generation_tail_region(gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0)) = &segment;
        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(size + pad));
        gc_alloc_context context = default;
        heap_segment* ephemeral = &segment;
        byte* allocAllocated = start;
        ulong totalAllocatedBytesSoh = 0;
        bool commitFailed = true;
        bool shortSegmentEnd = true;

        Assert.True(gc_heap.soh_try_fit(
            (int)gc_generation_num.soh_gen0,
            size,
            &context,
            0,
            alignment,
            &commitFailed,
            &shortSegmentEnd,
            sufficient_space_regions_for_allocation_p: true,
            sufficient_gen0_space_p: false,
            &data,
            0,
            generations,
            &ephemeral,
            &allocAllocated,
            &totalAllocatedBytesSoh,
            0,
            null));

        Assert.False(commitFailed);
        Assert.False(shortSegmentEnd);
        Assert.Equal((nuint)(start + (nint)(size + pad)), (nuint)allocAllocated);
        Assert.Equal((nuint)start, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)(start + (nint)size), (nuint)context.alloc_limit);
        Assert.Equal((nint)0, data.new_allocation);
        Assert.Equal((ulong)size, totalAllocatedBytesSoh);
    }

    [Fact]
    public void SohTryFitSuppressesShortEndWithoutChangingAllocationState()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        byte* storage = stackalloc byte[128];
        byte* start = storage + 32;
        heap_segment segment = default;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = start + (nint)(2 * pad);
        heap_segment.heap_segment_reserved(&segment) = heap_segment.heap_segment_committed(&segment);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(3 * pad));
        gc_alloc_context context = default;
        heap_segment* ephemeral = &segment;
        byte* allocAllocated = start;
        ulong totalAllocatedBytesSoh = 17;
        bool commitFailed = false;
        bool shortSegmentEnd = false;

        Assert.False(gc_heap.soh_try_fit(
            (int)gc_generation_num.soh_gen0,
            unchecked(2 * pad),
            &context,
            0,
            alignment,
            &commitFailed,
            &shortSegmentEnd,
            sufficient_space_regions_for_allocation_p: false,
            sufficient_gen0_space_p: false,
            &data,
            0,
            generations,
            &ephemeral,
            &allocAllocated,
            &totalAllocatedBytesSoh,
            0,
            null));

        Assert.False(commitFailed);
        Assert.True(shortSegmentEnd);
        Assert.Equal((nuint)(&segment), (nuint)ephemeral);
        Assert.Equal((nuint)start, (nuint)allocAllocated);
        Assert.Equal((nuint)start, (nuint)heap_segment.heap_segment_allocated(&segment));
        Assert.Equal((nuint)0, (nuint)context.alloc_ptr);
        Assert.Equal((nint)(3 * pad), data.new_allocation);
        Assert.Equal(17ul, totalAllocatedBytesSoh);
    }

    [Fact]
    public void SohTryFitRollsToNextRegionAndFixesAllocationContext()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[512];
        heap_segment* segments = stackalloc heap_segment[2];
        segments[0] = default;
        segments[1] = default;
        byte* firstStart = storage + 32;
        byte* secondStart = storage + 256;
        heap_segment.heap_segment_mem(&segments[0]) = firstStart;
        heap_segment.heap_segment_allocated(&segments[0]) = firstStart;
        heap_segment.heap_segment_used(&segments[0]) = firstStart;
        heap_segment.heap_segment_committed(&segments[0]) = firstStart + (nint)(6 * pad);
        heap_segment.heap_segment_reserved(&segments[0]) = heap_segment.heap_segment_committed(&segments[0]);
        heap_segment.heap_segment_next(&segments[0]) = &segments[1];
        heap_segment.heap_segment_mem(&segments[1]) = secondStart;
        heap_segment.heap_segment_allocated(&segments[1]) = secondStart;
        heap_segment.heap_segment_used(&segments[1]) = secondStart;
        heap_segment.heap_segment_committed(&segments[1]) = secondStart + (nint)(size + (2 * pad));
        heap_segment.heap_segment_reserved(&segments[1]) = heap_segment.heap_segment_committed(&segments[1]);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        generation.generation_tail_region(gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0)) = &segments[1];
        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(size + pad));
        gc_alloc_context context = default;
        context.alloc_ptr = firstStart + (nint)pad;
        context.alloc_limit = firstStart + (nint)(2 * pad);
        context.alloc_bytes = 100;
        heap_segment* ephemeral = &segments[0];
        byte* allocAllocated = firstStart + (nint)(3 * pad);
        ulong totalAllocatedBytesSoh = 200;
        bool commitFailed = false;
        bool shortSegmentEnd = true;

        Assert.True(gc_heap.soh_try_fit(
            (int)gc_generation_num.soh_gen0,
            size,
            &context,
            0,
            alignment,
            &commitFailed,
            &shortSegmentEnd,
            sufficient_space_regions_for_allocation_p: true,
            sufficient_gen0_space_p: false,
            &data,
            0,
            generations,
            &ephemeral,
            &allocAllocated,
            &totalAllocatedBytesSoh,
            0,
            null));

        Assert.False(commitFailed);
        Assert.False(shortSegmentEnd);
        Assert.Equal((nuint)(&segments[1]), (nuint)ephemeral);
        Assert.Equal((nuint)(firstStart + (nint)pad), (nuint)heap_segment.heap_segment_allocated(&segments[0]));
        Assert.Equal((nuint)(secondStart + (nint)(size + pad)), (nuint)allocAllocated);
        Assert.Equal((nuint)secondStart, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)(secondStart + (nint)size), (nuint)context.alloc_limit);
        Assert.Equal(100 - (long)pad + (long)size, context.alloc_bytes);
        Assert.Equal((ulong)((nuint)200 - pad + size), totalAllocatedBytesSoh);
        Assert.Equal((nint)0, data.new_allocation);
    }

    [Fact]
    public void UohTryFitPropagatesCommitFailureAsOomWithoutChangingState()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: true);
        int alignment = gc_heap.get_alignment_constant(small_object_p: false);
        nuint pageSize = GCToOSInterface.GetPageSize();
        byte* start = (byte*)0x2000;
        heap_segment segment = default;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = start;
        heap_segment.heap_segment_reserved(&segment) = start + (nint)(2 * pageSize);
        gc_heap.heap_hard_limit = pageSize - 1;
        gc_heap.heap_hard_limit_oh = default;
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        generation* loh = gc_heap.generation_of(generations, (int)gc_generation_num.loh_generation);
        generation.generation_allocation_segment(loh) = &segment;
        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(2 * pageSize));
        gc_alloc_context context = default;
        context.alloc_bytes = 17;
        ulong totalAllocatedBytesUoh = 19;
        bool commitFailed = false;
        oom_reason oomReason = oom_reason.oom_no_failure;

        Assert.False(gc_heap.uoh_try_fit(
            (int)gc_generation_num.loh_generation,
            pageSize,
            &context,
            0,
            alignment,
            &commitFailed,
            &oomReason,
            &data,
            0,
            generations,
            null,
            null,
            &totalAllocatedBytesUoh,
            0));

        Assert.True(commitFailed);
        Assert.Equal(oom_reason.oom_cant_commit, oomReason);
        Assert.Equal((nuint)start, (nuint)heap_segment.heap_segment_allocated(&segment));
        Assert.Equal((nuint)start, (nuint)heap_segment.heap_segment_used(&segment));
        Assert.Equal((nuint)start, (nuint)heap_segment.heap_segment_committed(&segment));
        Assert.Equal((nuint)0, (nuint)context.alloc_ptr);
        Assert.Equal(17, context.alloc_bytes);
        Assert.Equal((ulong)19, totalAllocatedBytesUoh);
        Assert.Equal((nint)(2 * pageSize), data.new_allocation);
    }

    [Fact]
    public void FreeListFitFailureLeavesSohAndUohStateUnchanged()
    {
        int sohAlignment = gc_heap.get_alignment_constant(small_object_p: true);
        int uohAlignment = gc_heap.get_alignment_constant(small_object_p: false);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, sohAlignment);
        byte* storage = stackalloc byte[512];
        for (int i = 0; i < 512; i++)
        {
            storage[i] = 0;
        }

        alloc_list* buckets = stackalloc alloc_list[1];
        buckets[0] = default;
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        generation* sohGen = gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0);
        sohGen->free_list_allocator = new allocator(2, (sizeof(nuint) * 8) - 1, buckets);
        byte* sohFreeItem = storage + sizeof(nuint);
        gc_heap.thread_free_item_front(sohGen, sohFreeItem, unchecked(2 * pad));
        dynamic_data sohData = default;
        sohData.new_allocation = (nint)(2 * pad);
        gc_alloc_context sohContext = default;
        ulong totalAllocatedBytesSoh = 17;

        Assert.False(gc_heap.a_fit_free_list_p(
            (int)gc_generation_num.soh_gen0,
            unchecked(2 * pad),
            &sohContext,
            0,
            sohAlignment,
            &sohData,
            0,
            generations,
            &totalAllocatedBytesSoh));

        Assert.Equal((nuint)sohFreeItem, (nuint)allocator.alloc_list_head_of(generation.generation_allocator(sohGen), 0));
        Assert.Equal((nuint)sohFreeItem, (nuint)allocator.alloc_list_tail_of(generation.generation_allocator(sohGen), 0));
        Assert.Equal(unchecked(2 * pad), generation.generation_free_list_space(sohGen));
        Assert.Equal((nint)(2 * pad), sohData.new_allocation);
        Assert.Equal((nuint)0, (nuint)sohContext.alloc_ptr);
        Assert.Equal((ulong)17, totalAllocatedBytesSoh);

        generation* uohGen = gc_heap.generation_of(generations, (int)gc_generation_num.loh_generation);
        byte* uohFreeItem = storage + 256 + sizeof(nuint);
        gc_heap.thread_free_item_front(uohGen, uohFreeItem, unchecked(2 * pad));
        dynamic_data uohData = default;
        uohData.new_allocation = (nint)(3 * pad);
        gc_alloc_context uohContext = default;
        ulong totalAllocatedBytesUoh = 19;

        Assert.False(gc_heap.a_fit_free_list_uoh_p(
            unchecked(3 * pad),
            &uohContext,
            0,
            uohAlignment,
            (int)gc_generation_num.loh_generation,
            &uohData,
            0,
            generations,
            &totalAllocatedBytesUoh));

        Assert.Equal((nuint)uohFreeItem, (nuint)allocator.alloc_list_head_of(generation.generation_allocator(uohGen), 0));
        Assert.Equal((nuint)uohFreeItem, (nuint)allocator.alloc_list_tail_of(generation.generation_allocator(uohGen), 0));
        Assert.Equal(unchecked(2 * pad), generation.generation_free_list_space(uohGen));
        Assert.Equal((nint)(3 * pad), uohData.new_allocation);
        Assert.Equal((nuint)0, (nuint)uohContext.alloc_ptr);
        Assert.Equal((ulong)19, totalAllocatedBytesUoh);
    }

    [Fact]
    public void ResetAllocationPointersSelectsTheFirstWritableRegionAndChecksHalfOpenBounds()
    {
        heap_segment readOnly = default;
        heap_segment writable = default;
        generation gen = default;

        InitializeRegion(&readOnly, 0x1000, 0x1800, 0x2000, age: 0);
        InitializeRegion(&writable, 0x2000, 0x2800, 0x3000, age: 0);
        readOnly.flags = heap_segment.heap_segment_flags_readonly;
        readOnly.next = &writable;
        gen.start_segment = &readOnly;
        gen.allocation_context.alloc_ptr = (byte*)0x2500;
        gen.allocation_context.alloc_limit = (byte*)0x2800;

        gc_heap.reset_allocation_pointers(&gen, (byte*)0x2000);

        Assert.Equal((nuint)0, (nuint)generation.generation_allocation_pointer(&gen));
        Assert.Equal((nuint)0, (nuint)generation.generation_allocation_limit(&gen));
        Assert.Equal((nuint)(&writable), (nuint)generation.generation_allocation_segment(&gen));
        Assert.Equal(1, gc_heap.in_range_for_segment(heap_segment.heap_segment_mem(&writable), &writable));
        Assert.Equal(1, gc_heap.in_range_for_segment(heap_segment.heap_segment_reserved(&writable) - 1, &writable));
        Assert.Equal(0, gc_heap.in_range_for_segment(heap_segment.heap_segment_reserved(&writable), &writable));
    }

    [Fact]
    public void TryAllocateMoreSpaceInitialSohFreeListFitReachesCanAllocate()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[128];
        for (int i = 0; i < 128; i++)
        {
            storage[i] = 0;
        }

        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        void* savedFreeObjectMethodTable = GCCommon.g_gc_pFreeObjectMethodTable;
        GCCommon.g_gc_pFreeObjectMethodTable = (void*)0x12345000;

        try
        {
            byte* freeItem = storage + sizeof(nuint);
            gc_heap.thread_free_item_front(
                gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0),
                freeItem,
                unchecked(size + pad));

            gc_alloc_context allocContext = default;
            dynamic_data data = default;
            data.new_allocation = unchecked((nint)(size + pad));
            heap_segment segment = default;
            heap_segment* ephemeralHeapSegment = &segment;
            byte* allocAllocated = null;
            ulong totalAllocatedBytesSoh = 0;
            ulong totalAllocatedBytesUoh = 0;
            try_allocate_more_space_context allocation = default;
            allocation.acontext = &allocContext;
            allocation.dd = &data;
            allocation.generation_table = generations;
            allocation.ephemeral_heap_segment = &ephemeralHeapSegment;
            allocation.alloc_allocated = &allocAllocated;
            allocation.total_alloc_bytes_soh = &totalAllocatedBytesSoh;
            allocation.total_alloc_bytes_uoh = &totalAllocatedBytesUoh;
            allocation.size = size;
            allocation.gen_number = (int)gc_generation_num.soh_gen0;
            allocation.align_const = alignment;
            allocation.state = allocation_state.a_state_start;
            allocation.more_space_lock_held_p = 1;
            allocation.budget_checked_p = 1;

            Assert.Equal(allocation_state.a_state_can_allocate, gc_heap.try_allocate_more_space(&allocation));
            Assert.Equal(allocation_deferred_operation.none, allocation.deferred_operation);
            Assert.Equal((nuint)freeItem, (nuint)allocContext.alloc_ptr);
            Assert.Equal((nuint)(freeItem + (nint)size), (nuint)allocContext.alloc_limit);
            Assert.Equal((nint)0, data.new_allocation);
            Assert.Equal((ulong)size, totalAllocatedBytesSoh);
            Assert.Equal((ulong)0, totalAllocatedBytesUoh);
        }
        finally
        {
            GCCommon.g_gc_pFreeObjectMethodTable = savedFreeObjectMethodTable;
        }
    }

    [Fact]
    public void AllocateMoreSpaceRetriesFromInitialStateAndUpdatesAllocationContext()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[128];
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        InitializeAllocationGenerations(generations);
        void* savedFreeObjectMethodTable = GCCommon.g_gc_pFreeObjectMethodTable;
        GCCommon.g_gc_pFreeObjectMethodTable = (void*)0x12345000;

        try
        {
            byte* freeItem = storage + sizeof(nuint);
            gc_heap.thread_free_item_front(
                gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0),
                freeItem,
                unchecked(size + pad));

            gc_alloc_context allocContext = default;
            dynamic_data data = new() { new_allocation = unchecked((nint)(size + pad)) };
            heap_segment* ephemeralHeapSegment = null;
            byte* allocAllocated = null;
            ulong totalAllocatedBytesSoh = 0;
            ulong totalAllocatedBytesUoh = 0;
            try_allocate_more_space_context allocation = CreateAllocationContext(
                &allocContext,
                &data,
                generations,
                &ephemeralHeapSegment,
                &allocAllocated,
                &totalAllocatedBytesSoh,
                &totalAllocatedBytesUoh,
                size,
                (int)gc_generation_num.soh_gen0,
                alignment);
            ResetAllocateMoreSpaceRecorder();
            delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback =
                &AllocateMoreSpaceRetryCallback;

            Assert.True(gc_heap.allocate_more_space(&allocation, callback));
            Assert.Equal(2, s_allocateMoreSpaceEnterCount);
            Assert.Equal(1, s_allocateMoreSpaceLeaveCount);
            Assert.Equal(allocation_state.a_state_start, s_allocateMoreSpaceRetryState);
            Assert.Equal(oom_reason.oom_no_failure, s_allocateMoreSpaceRetryOomReason);
            Assert.Equal(allocation_state.a_state_can_allocate, allocation.state);
            Assert.Equal(allocation_deferred_operation.none, allocation.deferred_operation);
            Assert.Equal((byte)0, allocation.more_space_lock_held_p);
            Assert.Equal((nuint)freeItem, (nuint)allocContext.alloc_ptr);
            Assert.Equal((nuint)(freeItem + (nint)size), (nuint)allocContext.alloc_limit);
            Assert.Equal((nint)0, data.new_allocation);
            Assert.Equal((ulong)size, totalAllocatedBytesSoh);
            Assert.Equal((ulong)0, totalAllocatedBytesUoh);
        }
        finally
        {
            GCCommon.g_gc_pFreeObjectMethodTable = savedFreeObjectMethodTable;
        }
    }

    [Fact]
    public void AllocateMoreSpaceReleasesMoreSpaceLockBeforeClearingWithoutDoubleRelease()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[128];
        for (int i = 0; i < 128; i++)
        {
            storage[i] = 0xcc;
        }

        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        InitializeAllocationGenerations(generations);
        void* savedFreeObjectMethodTable = GCCommon.g_gc_pFreeObjectMethodTable;
        GCCommon.g_gc_pFreeObjectMethodTable = (void*)0x12345000;

        try
        {
            byte* freeItem = storage + sizeof(nuint);
            gc_heap.thread_free_item_front(
                gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0),
                freeItem,
                unchecked(size + pad));

            gc_alloc_context allocContext = default;
            dynamic_data data = new() { new_allocation = unchecked((nint)(size + pad)) };
            heap_segment* ephemeralHeapSegment = null;
            byte* allocAllocated = null;
            ulong totalAllocatedBytesSoh = 0;
            ulong totalAllocatedBytesUoh = 0;
            try_allocate_more_space_context allocation = CreateAllocationContext(
                &allocContext,
                &data,
                generations,
                &ephemeralHeapSegment,
                &allocAllocated,
                &totalAllocatedBytesSoh,
                &totalAllocatedBytesUoh,
                size,
                (int)gc_generation_num.soh_gen0,
                alignment);
            ResetAllocateMoreSpaceRecorder();

            Assert.True(gc_heap.allocate_more_space(&allocation, &AllocationClearCallback));
            Assert.Equal(1, s_allocateMoreSpaceLeaveCount);
            Assert.Equal((byte)0, allocation.more_space_lock_held_p);
            Assert.Equal((nuint)0, *(nuint*)(freeItem - sizeof(nuint)));
        }
        finally
        {
            GCCommon.g_gc_pFreeObjectMethodTable = savedFreeObjectMethodTable;
        }
    }

    [Fact]
    public void AllocateMoreSpaceSelectsSohAndUohGenerations()
    {
        int sohAlignment = gc_heap.get_alignment_constant(small_object_p: true);
        int uohAlignment = gc_heap.get_alignment_constant(small_object_p: false);
        nuint sohPad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, sohAlignment);
        nuint uohPad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, uohAlignment);
        nuint size = unchecked(2 * sohPad);
        byte* storage = stackalloc byte[256];
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        InitializeAllocationGenerations(generations);
        void* savedFreeObjectMethodTable = GCCommon.g_gc_pFreeObjectMethodTable;
        GCCommon.g_gc_pFreeObjectMethodTable = (void*)0x12345000;

        try
        {
            byte* sohFreeItem = storage + sizeof(nuint);
            gc_heap.thread_free_item_front(
                gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0),
                sohFreeItem,
                unchecked(size + sohPad));
            gc_alloc_context sohAllocContext = default;
            dynamic_data sohData = new() { new_allocation = unchecked((nint)(size + sohPad)) };
            heap_segment* sohEphemeralHeapSegment = null;
            byte* sohAllocAllocated = null;
            ulong totalAllocatedBytesSoh = 0;
            ulong totalAllocatedBytesUoh = 0;
            try_allocate_more_space_context sohAllocation = CreateAllocationContext(
                &sohAllocContext,
                &sohData,
                generations,
                &sohEphemeralHeapSegment,
                &sohAllocAllocated,
                &totalAllocatedBytesSoh,
                &totalAllocatedBytesUoh,
                size,
                (int)gc_generation_num.soh_gen0,
                sohAlignment);
            ResetAllocateMoreSpaceRecorder();
            delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback =
                &AllocateMoreSpaceFitCallback;

            Assert.True(gc_heap.allocate_more_space(&sohAllocation, callback));
            Assert.Equal((int)gc_generation_num.soh_gen0, s_allocateMoreSpaceGeneration);
            Assert.Equal(sohAlignment, s_allocateMoreSpaceAlignment);
            Assert.Equal(1, s_allocateMoreSpaceLeaveCount);
            Assert.Equal((byte)0, sohAllocation.more_space_lock_held_p);
            Assert.Equal((nuint)sohFreeItem, (nuint)sohAllocContext.alloc_ptr);
            Assert.Equal((nuint)(sohFreeItem + (nint)size), (nuint)sohAllocContext.alloc_limit);
            Assert.Equal((ulong)size, totalAllocatedBytesSoh);
            Assert.Equal((ulong)0, totalAllocatedBytesUoh);

            byte* uohFreeItem = storage + 128 + sizeof(nuint);
            gc_heap.thread_free_item_front(
                gc_heap.generation_of(generations, (int)gc_generation_num.loh_generation),
                uohFreeItem,
                unchecked(size + uohPad));
            gc_alloc_context uohAllocContext = default;
            dynamic_data uohData = new() { new_allocation = unchecked((nint)size) };
            heap_segment* uohEphemeralHeapSegment = null;
            byte* uohAllocAllocated = null;
            try_allocate_more_space_context uohAllocation = CreateAllocationContext(
                &uohAllocContext,
                &uohData,
                generations,
                &uohEphemeralHeapSegment,
                &uohAllocAllocated,
                &totalAllocatedBytesSoh,
                &totalAllocatedBytesUoh,
                size,
                (int)gc_generation_num.loh_generation,
                uohAlignment);
            ResetAllocateMoreSpaceRecorder();

            Assert.True(gc_heap.allocate_more_space(&uohAllocation, callback));
            Assert.Equal((int)gc_generation_num.loh_generation, s_allocateMoreSpaceGeneration);
            Assert.Equal(uohAlignment, s_allocateMoreSpaceAlignment);
            Assert.Equal(1, s_allocateMoreSpaceLeaveCount);
            Assert.Equal((byte)0, uohAllocation.more_space_lock_held_p);
            Assert.Equal((nuint)uohFreeItem, (nuint)uohAllocContext.alloc_ptr);
            Assert.Equal((nuint)(uohFreeItem + (nint)size), (nuint)uohAllocContext.alloc_limit);
            Assert.Equal((ulong)size, totalAllocatedBytesSoh);
            Assert.Equal((ulong)size, totalAllocatedBytesUoh);
        }
        finally
        {
            GCCommon.g_gc_pFreeObjectMethodTable = savedFreeObjectMethodTable;
        }
    }

    [Fact]
    public void AllocateMoreSpaceFailsWhenAnUntranslatedOperationHasNoCallback()
    {
        try_allocate_more_space_context allocation = default;
        allocation.state = allocation_state.a_state_can_allocate;
        allocation.oom_r = oom_reason.oom_loh;

        Assert.False(gc_heap.allocate_more_space(&allocation));
        Assert.Equal(allocation_state.a_state_start, allocation.state);
        Assert.Equal(oom_reason.oom_no_failure, allocation.oom_r);
        Assert.Equal(allocation_deferred_operation.enter_more_space_lock, allocation.deferred_operation);
    }

    [Fact]
    public void AllocateMoreSpaceWaitsForRunningGcBeforeRetrying()
    {
        try_allocate_more_space_context allocation = default;
        allocation.gc_started_p = 1;
        ResetAllocationCallbackRecorder();
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback =
            &WaitForGcThenDeferCallback;

        Assert.False(gc_heap.allocate_more_space(&allocation, callback));
        Assert.Equal((byte)0, allocation.gc_started_p);
        Assert.Equal(3, s_allocationCallbackCount);
        Assert.Equal(allocation_deferred_operation.enter_more_space_lock, s_lastAllocationDeferredOperation);
        Assert.Equal(allocation_state.a_state_start, allocation.state);
        Assert.Equal(allocation_deferred_operation.enter_more_space_lock, allocation.deferred_operation);
    }

    [Fact]
    public void TryAllocateMoreSpaceUohCommitFailureDefersFullCompactGcWithoutMutatingAllocation()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: true);
        int alignment = gc_heap.get_alignment_constant(small_object_p: false);
        nuint pageSize = GCToOSInterface.GetPageSize();
        byte* start = (byte*)0x2000;
        heap_segment segment = default;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = start;
        heap_segment.heap_segment_reserved(&segment) = start + (nint)(2 * pageSize);
        gc_heap.heap_hard_limit = pageSize - 1;
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        generation.generation_allocation_segment(
            gc_heap.generation_of(generations, (int)gc_generation_num.loh_generation)) = &segment;
        gc_alloc_context allocContext = default;
        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(2 * pageSize));
        heap_segment* ephemeralHeapSegment = null;
        byte* allocAllocated = null;
        ulong totalAllocatedBytesSoh = 17;
        ulong totalAllocatedBytesUoh = 19;
        try_allocate_more_space_context allocation = default;
        allocation.acontext = &allocContext;
        allocation.dd = &data;
        allocation.generation_table = generations;
        allocation.ephemeral_heap_segment = &ephemeralHeapSegment;
        allocation.alloc_allocated = &allocAllocated;
        allocation.total_alloc_bytes_soh = &totalAllocatedBytesSoh;
        allocation.total_alloc_bytes_uoh = &totalAllocatedBytesUoh;
        allocation.size = pageSize;
        allocation.gen_number = (int)gc_generation_num.loh_generation;
        allocation.align_const = alignment;
        allocation.state = allocation_state.a_state_start;
        allocation.more_space_lock_held_p = 1;
        allocation.budget_checked_p = 1;

        Assert.Equal(allocation_state.a_state_trigger_full_compact_gc, gc_heap.try_allocate_more_space(&allocation));
        Assert.Equal(allocation_deferred_operation.trigger_full_compact_gc, allocation.deferred_operation);
        Assert.Equal(oom_reason.oom_cant_commit, allocation.oom_r);
        Assert.Equal((nuint)start, (nuint)heap_segment.heap_segment_allocated(&segment));
        Assert.Equal((nuint)start, (nuint)heap_segment.heap_segment_used(&segment));
        Assert.Equal((nuint)start, (nuint)heap_segment.heap_segment_committed(&segment));
        Assert.Equal((nuint)0, (nuint)allocContext.alloc_ptr);
        Assert.Equal((nint)(2 * pageSize), data.new_allocation);
        Assert.Equal((ulong)17, totalAllocatedBytesSoh);
        Assert.Equal((ulong)19, totalAllocatedBytesUoh);
    }

    [Fact]
    public void TryAllocateMoreSpaceSohShortEndAfterBgcDefersSecondEphemeralGc()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        heap_segment segment = default;
        byte* start = (byte*)0x2000;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = start + (nint)(2 * pad);
        heap_segment.heap_segment_reserved(&segment) = heap_segment.heap_segment_committed(&segment);
        gc_alloc_context allocContext = default;
        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(3 * pad));
        heap_segment* ephemeralHeapSegment = &segment;
        byte* allocAllocated = start;
        ulong totalAllocatedBytesSoh = 23;
        ulong totalAllocatedBytesUoh = 0;
        try_allocate_more_space_context allocation = default;
        allocation.acontext = &allocContext;
        allocation.dd = &data;
        allocation.generation_table = generations;
        allocation.ephemeral_heap_segment = &ephemeralHeapSegment;
        allocation.alloc_allocated = &allocAllocated;
        allocation.total_alloc_bytes_soh = &totalAllocatedBytesSoh;
        allocation.total_alloc_bytes_uoh = &totalAllocatedBytesUoh;
        allocation.size = unchecked(2 * pad);
        allocation.gen_number = (int)gc_generation_num.soh_gen0;
        allocation.align_const = alignment;
        allocation.state = allocation_state.a_state_try_fit_after_bgc;
        allocation.more_space_lock_held_p = 1;
        allocation.budget_checked_p = 1;
        allocation.sufficient_space_regions_for_allocation_p = 0;
        allocation.sufficient_gen0_space_p = 0;

        Assert.Equal(allocation_state.a_state_trigger_2nd_ephemeral_gc, gc_heap.try_allocate_more_space(&allocation));
        Assert.Equal(allocation_deferred_operation.trigger_2nd_ephemeral_gc, allocation.deferred_operation);
        Assert.Equal((byte)1, allocation.short_seg_end_p);
        Assert.Equal((byte)0, allocation.commit_failed_p);
        Assert.Equal((nuint)start, (nuint)allocAllocated);
        Assert.Equal((nuint)0, (nuint)allocContext.alloc_ptr);
        Assert.Equal((nint)(3 * pad), data.new_allocation);
        Assert.Equal((ulong)23, totalAllocatedBytesSoh);
    }

    [Fact]
    public void TryAllocateMoreSpaceUohOomRunsExplicitCallbacksAndPreservesAllocationState()
    {
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        gc_alloc_context allocContext = default;
        dynamic_data data = default;
        data.new_allocation = 64;
        heap_segment* ephemeralHeapSegment = null;
        byte* allocAllocated = null;
        ulong totalAllocatedBytesSoh = 29;
        ulong totalAllocatedBytesUoh = 31;
        try_allocate_more_space_context allocation = default;
        allocation.acontext = &allocContext;
        allocation.dd = &data;
        allocation.generation_table = generations;
        allocation.ephemeral_heap_segment = &ephemeralHeapSegment;
        allocation.alloc_allocated = &allocAllocated;
        allocation.total_alloc_bytes_soh = &totalAllocatedBytesSoh;
        allocation.total_alloc_bytes_uoh = &totalAllocatedBytesUoh;
        allocation.size = 32;
        allocation.gen_number = (int)gc_generation_num.loh_generation;
        allocation.align_const = gc_heap.get_alignment_constant(small_object_p: false);
        allocation.state = allocation_state.a_state_try_fit_after_cg;
        allocation.more_space_lock_held_p = 1;
        allocation.budget_checked_p = 1;
        ResetAllocationCallbackRecorder();
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback =
            &UohOomCallback;

        Assert.Equal(allocation_state.a_state_cant_allocate, gc_heap.try_allocate_more_space(&allocation, callback));
        Assert.Equal(oom_reason.oom_loh, allocation.oom_r);
        Assert.Equal(allocation_deferred_operation.none, allocation.deferred_operation);
        Assert.Equal((byte)1, allocation.oom_handled_p);
        Assert.Equal((byte)0, allocation.more_space_lock_held_p);
        Assert.Equal(5, s_allocationCallbackCount);
        Assert.Equal(allocation_deferred_operation.leave_more_space_lock, s_lastAllocationDeferredOperation);
        Assert.Equal((nuint)0, (nuint)allocContext.alloc_ptr);
        Assert.Equal((nint)64, data.new_allocation);
        Assert.Equal((ulong)29, totalAllocatedBytesSoh);
        Assert.Equal((ulong)31, totalAllocatedBytesUoh);
    }

    [Fact]
    public void TryAllocateMoreSpacePreservesRetryStatesForGcAndOtherHeap()
    {
        try_allocate_more_space_context gcStarted = default;
        gcStarted.state = allocation_state.a_state_start;
        gcStarted.gc_started_p = 1;

        Assert.Equal(allocation_state.a_state_retry_allocate, gc_heap.try_allocate_more_space(&gcStarted));
        Assert.Equal(allocation_deferred_operation.wait_for_gc_done, gcStarted.deferred_operation);

        try_allocate_more_space_context otherHeap = default;
        otherHeap.state = allocation_state.a_state_cant_allocate;
        otherHeap.gen_number = (int)gc_generation_num.loh_generation;
        otherHeap.oom_r = oom_reason.oom_loh;
        otherHeap.more_space_lock_held_p = 1;
        ResetAllocationCallbackRecorder();
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback =
            &RetryOtherHeapCallback;

        Assert.Equal(allocation_state.a_state_retry_allocate, gc_heap.try_allocate_more_space(&otherHeap, callback));
        Assert.Equal(allocation_deferred_operation.none, otherHeap.deferred_operation);
        Assert.Equal((byte)0, otherHeap.more_space_lock_held_p);
        Assert.Equal(2, s_allocationCallbackCount);
        Assert.Equal(allocation_deferred_operation.leave_more_space_lock, s_lastAllocationDeferredOperation);
    }

    [Fact]
    public void TryAllocateMoreSpaceDefersAndResumesEphemeralGcAtTheNativeState()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[128];
        for (int i = 0; i < 128; i++)
        {
            storage[i] = 0;
        }

        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        void* savedFreeObjectMethodTable = GCCommon.g_gc_pFreeObjectMethodTable;
        GCCommon.g_gc_pFreeObjectMethodTable = (void*)0x12345000;

        try
        {
            byte* freeItem = storage + sizeof(nuint);
            gc_heap.thread_free_item_front(
                gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0),
                freeItem,
                unchecked(size + pad));

            gc_alloc_context allocContext = default;
            dynamic_data data = default;
            data.new_allocation = unchecked((nint)(size + pad));
            heap_segment* ephemeralHeapSegment = null;
            byte* allocAllocated = null;
            ulong totalAllocatedBytesSoh = 0;
            ulong totalAllocatedBytesUoh = 0;
            try_allocate_more_space_context allocation = default;
            allocation.acontext = &allocContext;
            allocation.dd = &data;
            allocation.generation_table = generations;
            allocation.ephemeral_heap_segment = &ephemeralHeapSegment;
            allocation.alloc_allocated = &allocAllocated;
            allocation.total_alloc_bytes_soh = &totalAllocatedBytesSoh;
            allocation.total_alloc_bytes_uoh = &totalAllocatedBytesUoh;
            allocation.size = size;
            allocation.gen_number = (int)gc_generation_num.soh_gen0;
            allocation.align_const = alignment;
            allocation.state = allocation_state.a_state_trigger_ephemeral_gc;
            allocation.more_space_lock_held_p = 1;
            allocation.budget_checked_p = 1;

            Assert.Equal(allocation_state.a_state_trigger_ephemeral_gc, gc_heap.try_allocate_more_space(&allocation));
            Assert.Equal(allocation_deferred_operation.trigger_ephemeral_gc, allocation.deferred_operation);

            ResetAllocationCallbackRecorder();
            delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback =
                &NoFullEphemeralGcCallback;
            Assert.Equal(allocation_state.a_state_can_allocate, gc_heap.try_allocate_more_space(&allocation, callback));
            Assert.Equal(2, s_allocationCallbackCount);
            Assert.Equal(allocation_deferred_operation.leave_more_space_lock, s_lastAllocationDeferredOperation);
            Assert.Equal((nuint)freeItem, (nuint)allocContext.alloc_ptr);
            Assert.Equal((nint)0, data.new_allocation);
        }
        finally
        {
            GCCommon.g_gc_pFreeObjectMethodTable = savedFreeObjectMethodTable;
        }
    }

    [Fact]
    public void TryAllocateMoreSpaceUsesDistinctBackgroundQueryOperation()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        heap_segment segment = default;
        byte* start = (byte*)0x2000;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = start + (nint)(2 * pad);
        heap_segment.heap_segment_reserved(&segment) = heap_segment.heap_segment_committed(&segment);
        gc_alloc_context allocContext = default;
        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(3 * pad));
        heap_segment* ephemeralHeapSegment = &segment;
        byte* allocAllocated = start;
        ulong totalAllocatedBytesSoh = 0;
        ulong totalAllocatedBytesUoh = 0;
        try_allocate_more_space_context allocation = default;
        allocation.acontext = &allocContext;
        allocation.dd = &data;
        allocation.generation_table = generations;
        allocation.ephemeral_heap_segment = &ephemeralHeapSegment;
        allocation.alloc_allocated = &allocAllocated;
        allocation.total_alloc_bytes_soh = &totalAllocatedBytesSoh;
        allocation.total_alloc_bytes_uoh = &totalAllocatedBytesUoh;
        allocation.size = unchecked(2 * pad);
        allocation.state = allocation_state.a_state_trigger_ephemeral_gc;
        allocation.gen_number = (int)gc_generation_num.soh_gen0;
        allocation.align_const = alignment;
        allocation.more_space_lock_held_p = 1;
        allocation.budget_checked_p = 1;
        allocation.sufficient_space_regions_for_allocation_p = 0;
        allocation.sufficient_gen0_space_p = 0;
        ResetAllocationCallbackRecorder();
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback =
            &BackgroundQueryCallback;

        Assert.Equal(allocation_state.a_state_trigger_full_compact_gc, gc_heap.try_allocate_more_space(&allocation, callback));
        Assert.Equal(allocation_deferred_operation.trigger_full_compact_gc, allocation.deferred_operation);
        Assert.Equal(1, s_backgroundQueryCallbackCount);
    }

    [Fact]
    public void TryAllocateMoreSpaceOverwritesStaleOomReasonAfterUnproductiveFullGc()
    {
        try_allocate_more_space_context allocation = default;
        allocation.state = allocation_state.a_state_trigger_full_compact_gc;
        allocation.gen_number = (int)gc_generation_num.loh_generation;
        allocation.oom_r = oom_reason.oom_cant_commit;
        allocation.more_space_lock_held_p = 1;
        allocation.budget_checked_p = 1;
        ResetAllocationCallbackRecorder();
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback =
            &UnproductiveFullGcCallback;

        Assert.Equal(allocation_state.a_state_cant_allocate, gc_heap.try_allocate_more_space(&allocation, callback));
        Assert.Equal(oom_reason.oom_unproductive_full_gc, allocation.oom_r);
        Assert.Equal((byte)1, allocation.oom_handled_p);
        Assert.Equal((byte)0, allocation.more_space_lock_held_p);
        Assert.Equal(4, s_allocationCallbackCount);
    }

    [Fact]
    public void TryAllocateMoreSpaceRechecksBudgetOnceAfterHighMemoryWait()
    {
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        gc_alloc_context allocContext = default;
        dynamic_data data = default;
        heap_segment* ephemeralHeapSegment = null;
        byte* allocAllocated = null;
        ulong totalAllocatedBytesSoh = 0;
        ulong totalAllocatedBytesUoh = 0;
        try_allocate_more_space_context allocation = default;
        allocation.acontext = &allocContext;
        allocation.dd = &data;
        allocation.generation_table = generations;
        allocation.ephemeral_heap_segment = &ephemeralHeapSegment;
        allocation.alloc_allocated = &allocAllocated;
        allocation.total_alloc_bytes_soh = &totalAllocatedBytesSoh;
        allocation.total_alloc_bytes_uoh = &totalAllocatedBytesUoh;
        allocation.gen_number = (int)gc_generation_num.soh_gen0;
        allocation.align_const = gc_heap.get_alignment_constant(small_object_p: true);
        allocation.full_gc_notification_p = 1;
        allocation.state = allocation_state.a_state_start;
        ResetAllocationCallbackRecorder();
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback =
            &BudgetRecheckCallback;

        Assert.Equal(allocation_state.a_state_trigger_ephemeral_gc, gc_heap.try_allocate_more_space(&allocation, callback));
        Assert.Equal(allocation_deferred_operation.trigger_ephemeral_gc, allocation.deferred_operation);
        Assert.Equal(2, s_budgetCheckCallbackCount);
        Assert.Equal(1, s_highMemoryCallbackCount);
        Assert.Equal(1, s_budgetTriggerCallbackCount);
        Assert.Equal(2, s_fullGcCheckCallbackCount);
        Assert.Equal((byte)1, allocation.bgc_high_memory_waited_p);
        Assert.Equal((byte)1, allocation.budget_full_gc_checked_p);
        Assert.Equal((byte)1, allocation.budget_checked_p);
    }

    [Fact]
    public void TryAllocateMoreSpaceRetryReleasesMoreSpaceLockOwnership()
    {
        try_allocate_more_space_context allocation = default;
        allocation.gen_number = (int)gc_generation_num.soh_gen0;
        allocation.state = allocation_state.a_state_start;
        allocation.more_space_lock_held_p = 1;
        allocation.full_gc_checked_p = 1;
        allocation.bgc_high_memory_waited_p = 1;
        ResetAllocationCallbackRecorder();
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback =
            &BudgetRetryCallback;

        Assert.Equal(allocation_state.a_state_retry_allocate, gc_heap.try_allocate_more_space(&allocation, callback));
        Assert.Equal(allocation_deferred_operation.none, allocation.deferred_operation);
        Assert.Equal((byte)0, allocation.more_space_lock_held_p);
        Assert.Equal(2, s_allocationCallbackCount);
        Assert.Equal(allocation_deferred_operation.trigger_gc_for_budget, s_lastAllocationDeferredOperation);
    }

    [Fact]
    public void TryAllocateMoreSpaceTreatsNoBackgroundGcAsCompletedSohWait()
    {
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        gc_alloc_context allocContext = default;
        dynamic_data data = default;
        heap_segment* ephemeralHeapSegment = null;
        byte* allocAllocated = null;
        ulong totalAllocatedBytesSoh = 0;
        try_allocate_more_space_context allocation = default;
        allocation.acontext = &allocContext;
        allocation.dd = &data;
        allocation.generation_table = generations;
        allocation.ephemeral_heap_segment = &ephemeralHeapSegment;
        allocation.alloc_allocated = &allocAllocated;
        allocation.total_alloc_bytes_soh = &totalAllocatedBytesSoh;
        allocation.gen_number = (int)gc_generation_num.soh_gen0;
        allocation.align_const = gc_heap.get_alignment_constant(small_object_p: true);
        allocation.state = allocation_state.a_state_check_and_wait_for_bgc;
        allocation.more_space_lock_held_p = 1;
        allocation.budget_checked_p = 1;
        ResetAllocationCallbackRecorder();
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback =
            &NoBackgroundGcWaitCallback;

        Assert.Equal(allocation_state.a_state_trigger_2nd_ephemeral_gc, gc_heap.try_allocate_more_space(&allocation, callback));
        Assert.Equal(allocation_deferred_operation.trigger_2nd_ephemeral_gc, allocation.deferred_operation);
    }

    private static void InitializeAllocationGenerations(generation* generations)
    {
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }
    }

    private static try_allocate_more_space_context CreateAllocationContext(
        gc_alloc_context* allocContext,
        dynamic_data* data,
        generation* generations,
        heap_segment** ephemeralHeapSegment,
        byte** allocAllocated,
        ulong* totalAllocatedBytesSoh,
        ulong* totalAllocatedBytesUoh,
        nuint size,
        int generationNumber,
        int alignment)
    {
        return new try_allocate_more_space_context
        {
            acontext = allocContext,
            dd = data,
            generation_table = generations,
            ephemeral_heap_segment = ephemeralHeapSegment,
            alloc_allocated = allocAllocated,
            total_alloc_bytes_soh = totalAllocatedBytesSoh,
            total_alloc_bytes_uoh = totalAllocatedBytesUoh,
            size = size,
            gen_number = generationNumber,
            align_const = alignment,
        };
    }

    private static void ResetAllocateMoreSpaceRecorder()
    {
        s_allocateMoreSpaceEnterCount = 0;
        s_allocateMoreSpaceLeaveCount = 0;
        s_allocateMoreSpaceRetryState = allocation_state.a_state_start;
        s_allocateMoreSpaceRetryOomReason = oom_reason.oom_no_failure;
        s_allocateMoreSpaceGeneration = -1;
        s_allocateMoreSpaceAlignment = -1;
    }

    private static void AdjustLimitReleaseCallback(
        try_allocate_more_space_context* context,
        int operationValue,
        allocation_callback_result* result)
    {
        _ = context;
        allocation_deferred_operation operation = (allocation_deferred_operation)operationValue;
        if (operation == allocation_deferred_operation.leave_more_space_lock)
        {
            s_adjustLimitUsedPublishedAtRelease =
                heap_segment.heap_segment_used(s_adjustLimitSegment) == s_adjustLimitExpectedUsed ? 1 : 0;
            result->kind = allocation_callback_result_kind.completed;
        }
    }

    private static void AllocateMoreSpaceRetryCallback(
        try_allocate_more_space_context* context,
        int operationValue,
        allocation_callback_result* result)
    {
        allocation_deferred_operation operation = (allocation_deferred_operation)operationValue;

        if (operation == allocation_deferred_operation.enter_more_space_lock)
        {
            s_allocateMoreSpaceEnterCount++;
            if (s_allocateMoreSpaceEnterCount == 1)
            {
                context->oom_r = oom_reason.oom_loh;
                *result = new allocation_callback_result
                {
                    kind = allocation_callback_result_kind.retry_allocate,
                };
            }
            else
            {
                s_allocateMoreSpaceRetryState = context->state;
                s_allocateMoreSpaceRetryOomReason = context->oom_r;
                *result = new allocation_callback_result
                {
                    kind = allocation_callback_result_kind.completed,
                };
            }

            return;
        }

        if (operation == allocation_deferred_operation.leave_more_space_lock)
        {
            s_allocateMoreSpaceLeaveCount++;
            *result = new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            };
            return;
        }

        *result = operation == allocation_deferred_operation.check_allocation_budget
            ? new allocation_callback_result
            {
                kind = allocation_callback_result_kind.allocation_allowed,
            }
            : default;
    }

    private static void AllocateMoreSpaceFitCallback(
        try_allocate_more_space_context* context,
        int operationValue,
        allocation_callback_result* result)
    {
        allocation_deferred_operation operation = (allocation_deferred_operation)operationValue;

        if (operation == allocation_deferred_operation.enter_more_space_lock)
        {
            s_allocateMoreSpaceGeneration = context->gen_number;
            s_allocateMoreSpaceAlignment = context->align_const;
        }
        else if (operation == allocation_deferred_operation.leave_more_space_lock)
        {
            s_allocateMoreSpaceLeaveCount++;
        }

        *result = operation is allocation_deferred_operation.enter_more_space_lock or
            allocation_deferred_operation.leave_more_space_lock
            ? new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            }
            : operation == allocation_deferred_operation.check_allocation_budget
                ? new allocation_callback_result
                {
                    kind = allocation_callback_result_kind.allocation_allowed,
                }
                : default;
    }

    private static void AllocationClearCallback(
        try_allocate_more_space_context* context,
        int operationValue,
        allocation_callback_result* result)
    {
        allocation_deferred_operation operation = (allocation_deferred_operation)operationValue;

        if (operation == allocation_deferred_operation.leave_more_space_lock)
        {
            s_allocateMoreSpaceLeaveCount++;
            *(nuint*)(context->acontext->alloc_ptr - sizeof(nuint)) = nuint.MaxValue;
        }

        *result = operation is allocation_deferred_operation.enter_more_space_lock or
            allocation_deferred_operation.leave_more_space_lock
            ? new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            }
            : operation == allocation_deferred_operation.check_allocation_budget
                ? new allocation_callback_result
                {
                    kind = allocation_callback_result_kind.allocation_allowed,
                }
                : default;
    }

    private static void WaitForGcThenDeferCallback(
        try_allocate_more_space_context* context,
        int operationValue,
        allocation_callback_result* result)
    {
        allocation_deferred_operation operation = (allocation_deferred_operation)operationValue;
        s_allocationCallbackCount++;
        s_lastAllocationDeferredOperation = operation;
        if (operation == allocation_deferred_operation.wait_for_gc_done)
        {
            context->gc_started_p = s_allocationCallbackCount == 1 ? (byte)1 : (byte)0;
        }

        *result = operation == allocation_deferred_operation.wait_for_gc_done
            ? new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            }
            : default;
    }

    [Theory]
    [InlineData(-128L, 0, 0)]
    [InlineData(-128L, 1, 0)]
    [InlineData(-128L, 2, 2)]
    [InlineData(-128L, 3, 2)]
    public void NodeRelocationDistanceMasksFlagsAndNodeLeftPreservesItsBit(
        long distance,
        int flags,
        int expectedNodeLeft)
    {
        byte* storage = stackalloc byte[128];
        byte* node = storage + 64;
        ((plug_and_reloc*)node)[-1].reloc = (nint)distance | flags;

        Assert.Equal((nint)distance, gc_heap.node_relocation_distance(node));
        Assert.Equal((nint)expectedNodeLeft, gc_heap.node_left_p(node));
    }

    [Theory]
    [InlineData(256, 256)]
    [InlineData(512, 512)]
    [InlineData(700, 512)]
    [InlineData(900, 768)]
    public void TreeSearchReturnsExactNodeOrPredecessor(int addressOffset, int expectedOffset)
    {
        byte* storage = stackalloc byte[1024];
        byte* left = storage + 256;
        byte* root = storage + 512;
        byte* right = storage + 768;

        ((plug_and_pair*)left)[-1].m_pair = default;
        ((plug_and_pair*)root)[-1].m_pair = new pair
        {
            left = (short)(left - root),
            right = (short)(right - root),
        };
        ((plug_and_pair*)right)[-1].m_pair = default;

        Assert.Equal(
            (nuint)(storage + expectedOffset),
            (nuint)gc_heap.tree_search(root, storage + addressOffset));
    }

#if USE_REGIONS
    [Fact]
    public void RelocateAddressFollowsBrickBacklink()
    {
        int storageSize = checked((int)(5 * card_table_info.brick_size));
        byte* storage = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)storageSize);
        short* bricks = stackalloc short[4];
        region_info* generationMap = stackalloc region_info[4];

        try
        {
            byte* firstBrick = card_table_info.align_on_brick(storage);
            using RelocateAddressStateScope _ = new(
                firstBrick,
                firstBrick + (nint)(4 * card_table_info.brick_size),
                bricks,
                generationMap);

            byte* node = firstBrick + 512;
            ((plug_and_reloc*)node)[-1].reloc = -64;
            ((plug_and_pair*)node)[-1].m_pair = default;
            gc_heap.set_brick(0, (nint)(node - firstBrick));
            gc_heap.set_brick(1, -1);
            gc_heap.set_brick(2, -2);

            byte* oldAddress = firstBrick + (nint)(2 * card_table_info.brick_size) + 128;
            byte* relocatedAddress = oldAddress;

            gc_heap.relocate_address(&relocatedAddress);

            Assert.Equal((nuint)(oldAddress - 64), (nuint)relocatedAddress);
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(storage);
        }
    }

    [Fact]
    public void RelocateAddressUsesLeftNodeGap()
    {
        int storageSize = checked((int)(5 * card_table_info.brick_size));
        byte* storage = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)storageSize);
        short* bricks = stackalloc short[4];
        region_info* generationMap = stackalloc region_info[4];

        try
        {
            byte* firstBrick = card_table_info.align_on_brick(storage);
            using RelocateAddressStateScope _ = new(
                firstBrick,
                firstBrick + (nint)(4 * card_table_info.brick_size),
                bricks,
                generationMap);

            byte* brick = firstBrick + (nint)card_table_info.brick_size;
            byte* node = brick + 512;
            ((plug_and_gap*)node)[-1].gap = 64;
            ((plug_and_gap*)node)[-1].reloc = -128 | 2;
            ((plug_and_gap*)node)[-1].m_pair = default;
            gc_heap.set_brick(1, (nint)(node - brick));

            byte* oldAddress = brick + 256;
            byte* relocatedAddress = oldAddress;

            gc_heap.relocate_address(&relocatedAddress);

            Assert.Equal((nuint)(oldAddress - 64), (nuint)relocatedAddress);
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(storage);
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    public void RelocateAddressLeavesOutOfRangeOrUncondemnedReferenceUnchanged(
        bool inGcRange,
        int generation)
    {
        int storageSize = checked((int)(5 * card_table_info.brick_size));
        byte* storage = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)storageSize);
        short* bricks = stackalloc short[4];
        region_info* generationMap = stackalloc region_info[4];

        try
        {
            byte* firstBrick = card_table_info.align_on_brick(storage);
            using RelocateAddressStateScope _ = new(
                firstBrick,
                firstBrick + (nint)(4 * card_table_info.brick_size),
                bricks,
                generationMap);

            byte* node = firstBrick + 512;
            ((plug_and_gap*)node)[-1].gap = 64;
            ((plug_and_gap*)node)[-1].reloc = -128 | 2;
            ((plug_and_gap*)node)[-1].m_pair = default;
            gc_heap.set_brick(0, (nint)(node - firstBrick));
            generationMap[0] = generation == 2 ? region_info.RI_GEN_2 : region_info.RI_GEN_0;

            byte* oldAddress = inGcRange ? firstBrick + 256 : firstBrick - 8;
            byte* relocatedAddress = oldAddress;

            gc_heap.relocate_address(&relocatedAddress);

            Assert.Equal((nuint)oldAddress, (nuint)relocatedAddress);
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(storage);
        }
    }

    [Fact]
    public void PlanPhaseInsertNodeBuildsNativeBrickTreeAtSequenceBoundaries()
    {
        const int NodeStride = 128;
        using RegionSegmentsStateScope _ = new(initializeCommitLock: false);
        int storageSize = checked((int)(2 * card_table_info.brick_size));
        byte* storage = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)storageSize);

        try
        {
            byte* firstBrick = card_table_info.align_on_brick(storage);
            byte* firstNode = firstBrick + NodeStride;
            byte* tree = firstNode;
            byte* lastNode = null;

            for (nuint sequenceNumber = 1; sequenceNumber <= 7; sequenceNumber++)
            {
                byte* newNode = firstNode + (nint)((sequenceNumber - 1) * NodeStride);
                tree = gc_heap.insert_node(newNode, sequenceNumber, tree, lastNode);
                lastNode = newNode;
            }

            byte* secondNode = firstNode + NodeStride;
            byte* thirdNode = secondNode + NodeStride;
            byte* fourthNode = thirdNode + NodeStride;
            byte* fifthNode = fourthNode + NodeStride;
            byte* sixthNode = fifthNode + NodeStride;
            byte* seventhNode = sixthNode + NodeStride;

            Assert.Equal((nuint)fourthNode, (nuint)tree);
            Assert.Equal(unchecked((short)(secondNode - fourthNode)), gc_heap.node_left_child(fourthNode));
            Assert.Equal(unchecked((short)(sixthNode - fourthNode)), gc_heap.node_right_child(fourthNode));
            Assert.Equal(unchecked((short)(firstNode - secondNode)), gc_heap.node_left_child(secondNode));
            Assert.Equal(unchecked((short)(thirdNode - secondNode)), gc_heap.node_right_child(secondNode));
            Assert.Equal(unchecked((short)(fifthNode - sixthNode)), gc_heap.node_left_child(sixthNode));
            Assert.Equal(unchecked((short)(seventhNode - sixthNode)), gc_heap.node_right_child(sixthNode));
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(storage);
        }
    }

    [Fact]
    public void PlanPhaseUpdateBrickTablePreservesRootsBacktrackingAndBoundarySentinels()
    {
        const int BrickCount = 7;
        const int StorageBrickCount = BrickCount + 1;
        using RegionSegmentsStateScope _ = new(initializeCommitLock: false);
        int storageSize = checked((int)(StorageBrickCount * card_table_info.brick_size));
        byte* storage = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)storageSize);
        short* bricks = stackalloc short[BrickCount];

        try
        {
            for (int i = 0; i < BrickCount; i++)
            {
                bricks[i] = 0;
            }

            byte* firstBrick = card_table_info.align_on_brick(storage);
            gc_heap.lowest_address = firstBrick;
            gc_heap.brick_table = bricks;

            byte* firstRoot = firstBrick + 256;
            nuint nextBrick = gc_heap.update_brick_table(
                firstRoot,
                current_brick: 0,
                x: firstBrick + (nint)(3 * card_table_info.brick_size),
                plug_end: firstBrick + (nint)(3 * card_table_info.brick_size));

            Assert.Equal((nuint)3, nextBrick);
            Assert.Equal(257, gc_heap.get_brick_entry(0));
            Assert.Equal(-1, gc_heap.get_brick_entry(1));
            Assert.Equal(-2, gc_heap.get_brick_entry(2));
            Assert.Equal(0, gc_heap.get_brick_entry(3));

            byte* root = firstBrick + (nint)(3 * card_table_info.brick_size) + 256;
            nextBrick = gc_heap.update_brick_table(
                root,
                current_brick: 3,
                x: firstBrick + (nint)(6 * card_table_info.brick_size) + 128,
                plug_end: firstBrick + (nint)(5 * card_table_info.brick_size) + 256);

            Assert.Equal((nuint)6, nextBrick);
            Assert.Equal(257, gc_heap.get_brick_entry(3));
            Assert.Equal(-1, gc_heap.get_brick_entry(4));
            Assert.Equal(-2, gc_heap.get_brick_entry(5));
            Assert.Equal(-1, gc_heap.get_brick_entry(6));
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(storage);
        }
    }

    [Fact]
    public void SweepBrickTreeThreadsPostorderGapsAndUpdatesBricks()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: false);
        byte* storage = stackalloc byte[1024];
        byte* left = storage + 256;
        byte* root = storage + 512;
        byte* right = storage + 768;
        nuint gapSize = gc_heap.Align(unchecked(2 * (nuint)GCInterfaceOffsets.min_obj_size));

        ((plug_and_gap*)left)[-1].gap = (nint)gapSize;
        ((plug_and_gap*)root)[-1].gap = (nint)gapSize;
        ((plug_and_gap*)right)[-1].gap = (nint)gapSize;
        ((plug_and_pair*)left)[-1].m_pair = default;
        ((plug_and_pair*)root)[-1].m_pair.left = (short)(left - root);
        ((plug_and_pair*)root)[-1].m_pair.right = (short)(right - root);
        ((plug_and_pair*)right)[-1].m_pair = default;

        short* bricks = stackalloc short[4];
        gc_heap.lowest_address = storage;
        gc_heap.brick_table = bricks;
        gc_heap.set_brick(0, 0);
        gc_heap.set_brick(1, -1);
        gc_heap.set_brick(2, 32766);
        gc_heap.set_brick(3, -32768);

        Assert.Equal((nuint)(storage + (nint)(2 * card_table_info.brick_size)), (nuint)gc_heap.brick_address(2));
        Assert.Equal((short)1, bricks[0]);
        Assert.Equal((short)-1, bricks[1]);
        Assert.Equal(short.MaxValue, bricks[2]);
        Assert.Equal((short)-32767, bricks[3]);

        generation gen = default;
        generation.initialize(&gen);
        gc_heap.make_free_args args = new()
        {
            free_list_gen = &gen,
        };

        ((CObjectHeader*)root)->RawSetMethodTable((MethodTable*)8);
        gc_heap.set_plug_padded(root);
#if TARGET_64BIT && !TARGET_WASM
        ((CObjectHeader*)root)->SetBGCMarkBit();
        ((CObjectHeader*)root)->SetFreeObjInCompactBit();
#endif
        gc_heap.make_free_list_in_brick(root, &args);

        Assert.Equal((nuint)right, (nuint)args.highest_plug);
        Assert.Equal(0, gc_heap.is_plug_padded(root));
#if TARGET_64BIT && !TARGET_WASM
        Assert.Equal(0, ((CObjectHeader*)root)->IsBGCMarkBitSet());
        Assert.Equal(0, ((CObjectHeader*)root)->IsFreeObjInCompactBitSet());
#endif
        Assert.Equal(unchecked(3 * gapSize), generation.generation_free_list_space(&gen));
        Assert.Equal((nuint)0, generation.generation_free_obj_space(&gen));

        allocator* gen_allocator = generation.generation_allocator(&gen);
        byte* left_gap = left - (nint)gapSize;
        byte* root_gap = root - (nint)gapSize;
        byte* right_gap = right - (nint)gapSize;
        Assert.Equal((nuint)left_gap, (nuint)allocator.alloc_list_head_of(gen_allocator, 0));
        Assert.Equal((nuint)right_gap, (nuint)allocator.alloc_list_tail_of(gen_allocator, 0));
        Assert.Equal((nuint)root_gap, (nuint)allocator.free_list_slot(left_gap));
        Assert.Equal((nuint)right_gap, (nuint)allocator.free_list_slot(root_gap));
        Assert.Equal((nuint)0, (nuint)allocator.free_list_slot(right_gap));
    }

    [Fact]
    public void SweepNormalPlanPromotesAndRebuildsSohBrickTrees()
    {
        const int BrickCount = 5;
        const int StorageBrickCount = BrickCount + 1;
        const int PlugOffset = 256;
        using RegionSegmentsStateScope _ = new(initializeCommitLock: false);
        using PlanPhaseStateScope __ = new();
        void* savedFreeObjectMethodTable = GCCommon.g_gc_pFreeObjectMethodTable;
        int storageSize = checked((int)(StorageBrickCount * card_table_info.brick_size));
        byte* storage = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)storageSize);
        short* bricks = stackalloc short[BrickCount];

        for (int i = 0; i < BrickCount; i++)
        {
            bricks[i] = 0;
        }

        try
        {
            MethodTable freeObjectMethodTable = default;
            GCCommon.g_gc_pFreeObjectMethodTable = &freeObjectMethodTable;

            byte* firstBrick = card_table_info.align_on_brick(storage);
            nuint gapSize = gc_heap.Align(unchecked(2 * (nuint)GCInterfaceOffsets.min_obj_size));
            gc_heap.lowest_address = firstBrick;
            gc_heap.brick_table = bricks;
            gc_heap.settings.promotion = 1;

            heap_segment gen0First = default;
            heap_segment sweptInPlan = default;
            heap_segment gen0Second = default;
            heap_segment gen1First = default;
            byte* gen0FirstMem = firstBrick;
            byte* sweptInPlanMem = gen0FirstMem + (nint)card_table_info.brick_size;
            byte* gen0SecondMem = sweptInPlanMem + (nint)card_table_info.brick_size;
            byte* gen1FirstMem = gen0SecondMem + (2 * (nint)card_table_info.brick_size);

            heap_segment.heap_segment_mem(&gen0First) = gen0FirstMem;
            heap_segment.heap_segment_allocated(&gen0First) = sweptInPlanMem;
            heap_segment.heap_segment_next(&gen0First) = &sweptInPlan;
            heap_segment.heap_segment_mem(&sweptInPlan) = sweptInPlanMem;
            heap_segment.heap_segment_allocated(&sweptInPlan) = gen0SecondMem;
            heap_segment.heap_segment_swept_in_plan(&sweptInPlan) = 1;
            heap_segment.heap_segment_next(&sweptInPlan) = &gen0Second;
            heap_segment.heap_segment_mem(&gen0Second) = gen0SecondMem;
            heap_segment.heap_segment_allocated(&gen0Second) = gen1FirstMem;
            heap_segment.heap_segment_mem(&gen1First) = gen1FirstMem;
            heap_segment.heap_segment_allocated(&gen1First) = gen1FirstMem + (nint)card_table_info.brick_size;

            byte* gen0FirstPlug = gen0FirstMem + PlugOffset;
            byte* gen0Root = gen0SecondMem + PlugOffset;
            byte* gen0Right = gen0Root + PlugOffset;
            byte* gen1Plug = gen1FirstMem + PlugOffset;
            ((plug_and_gap*)gen0FirstPlug)[-1].gap = (nint)gapSize;
            ((plug_and_pair*)gen0FirstPlug)[-1].m_pair = default;
            ((plug_and_gap*)gen0Root)[-1].gap = (nint)gapSize;
            ((plug_and_gap*)gen0Right)[-1].gap = (nint)gapSize;
            ((plug_and_pair*)gen0Right)[-1].m_pair = default;
            ((plug_and_gap*)gen1Plug)[-1].gap = (nint)gapSize;
            ((plug_and_pair*)gen1Plug)[-1].m_pair = default;

            byte* gen0FirstTree = gc_heap.insert_node(gen0FirstPlug, 1, gen0FirstPlug, null);
            gc_heap.update_brick_table(
                gen0FirstTree,
                current_brick: 0,
                x: sweptInPlanMem,
                plug_end: sweptInPlanMem);

            byte* gen0Tree = gc_heap.insert_node(gen0Root, 1, gen0Root, null);
            gen0Tree = gc_heap.insert_node(gen0Right, 2, gen0Tree, gen0Root);
            gc_heap.update_brick_table(
                gen0Tree,
                current_brick: 2,
                x: gen1FirstMem,
                plug_end: gen0SecondMem + PlugOffset);

            byte* gen1Tree = gc_heap.insert_node(gen1Plug, 1, gen1Plug, null);
            gc_heap.update_brick_table(
                gen1Tree,
                current_brick: 4,
                x: gen1FirstMem + (nint)card_table_info.brick_size,
                plug_end: gen1FirstMem + (nint)card_table_info.brick_size);

            gc_heap heap = default;
            generation* generations = gc_heap.generation_table_of(&heap);
            for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
            {
                generation* gen = gc_heap.generation_of(generations, i);
                generation.initialize(gen);
                gen->gen_num = i;
            }

            generation.generation_start_segment(gc_heap.generation_of(generations, 0)) = &gen0First;
            generation.generation_start_segment(gc_heap.generation_of(generations, 1)) = &gen1First;

            Assert.Equal((nuint)(void*)&gen0Second, (nuint)gc_heap.heap_segment_next_non_sip(&gen0First));
            Assert.Equal(1, gc_heap.get_plan_gen_num(0));
            Assert.Equal(2, gc_heap.get_plan_gen_num(1));
            Assert.Equal(2, gc_heap.get_plan_gen_num(2));

            gc_heap.make_free_lists(&heap, (int)gc_generation_num.soh_gen1);

            generation* gen1 = gc_heap.generation_of(generations, 1);
            generation* gen2 = gc_heap.generation_of(generations, 2);
            byte* gen0FirstGap = gen0FirstPlug - (nint)gapSize;
            byte* gen0RootGap = gen0Root - (nint)gapSize;
            byte* gen0RightGap = gen0Right - (nint)gapSize;
            byte* gen1Gap = gen1Plug - (nint)gapSize;
            allocator* gen1Allocator = generation.generation_allocator(gen1);
            allocator* gen2Allocator = generation.generation_allocator(gen2);
            uint bucket = gen1Allocator->first_suitable_bucket(gapSize);

            Assert.Equal(unchecked(3 * gapSize), generation.generation_free_list_space(gen1));
            Assert.Equal((nuint)0, generation.generation_free_obj_space(gen1));
            Assert.Equal((nuint)gen0FirstGap, (nuint)allocator.alloc_list_head_of(gen1Allocator, bucket));
            Assert.Equal((nuint)gen0RightGap, (nuint)allocator.alloc_list_tail_of(gen1Allocator, bucket));
            Assert.Equal((nuint)gen0RootGap, (nuint)allocator.free_list_slot(gen0FirstGap));
            Assert.Equal((nuint)gen0RightGap, (nuint)allocator.free_list_slot(gen0RootGap));
            Assert.Equal((nuint)0, (nuint)allocator.free_list_slot(gen0RightGap));
            Assert.Equal(unchecked((short)(gen0Right - gen0SecondMem + 1)), gc_heap.get_brick_entry(2));
            Assert.Equal((short)-1, gc_heap.get_brick_entry(3));

            Assert.Equal(gapSize, generation.generation_free_list_space(gen2));
            Assert.Equal((nuint)0, generation.generation_free_obj_space(gen2));
            Assert.Equal((nuint)gen1Gap, (nuint)allocator.alloc_list_head_of(gen2Allocator, bucket));
            Assert.Equal((nuint)gen1Gap, (nuint)allocator.alloc_list_tail_of(gen2Allocator, bucket));
        }
        finally
        {
            GCCommon.g_gc_pFreeObjectMethodTable = savedFreeObjectMethodTable;
            System.Runtime.InteropServices.NativeMemory.Free(storage);
        }
    }

    [Fact]
    public void SweepUohThreadGapFrontThreadsPreformattedGaps()
    {
        void* savedFreeObjectMethodTable = GCCommon.g_gc_pFreeObjectMethodTable;
        try
        {
            GCCommon.g_gc_pFreeObjectMethodTable = (MethodTable*)0x40;
            nuint minFreeList = unchecked(2 * (nuint)GCInterfaceOffsets.min_obj_size);
            nuint belowMinFreeList = unchecked(minFreeList - (nuint)GCInterfaceOffsets.min_obj_size);
            byte* storage = stackalloc byte[512];
            byte* belowMinFreeListGap = storage + 32;
            byte* firstGap = storage + 160;
            byte* secondGap = storage + 320;
            generation gen = default;
            generation.initialize(&gen);

            gc_heap.make_unused_array(belowMinFreeListGap, belowMinFreeList);
            gc_heap.make_unused_array(firstGap, minFreeList);
            gc_heap.make_unused_array(secondGap, minFreeList);
            ((byte**)firstGap)[3] = (byte*)0x50;
            ((byte**)secondGap)[3] = (byte*)0x60;

            gc_heap.uoh_thread_gap_front(belowMinFreeListGap, belowMinFreeList, &gen);

            allocator* genAllocator = generation.generation_allocator(&gen);
            uint bucket = genAllocator->first_suitable_bucket(minFreeList);
            Assert.Equal((nuint)0, generation.generation_free_list_space(&gen));
            Assert.Equal((nuint)0, generation.generation_free_obj_space(&gen));
            Assert.Equal((nuint)0, (nuint)allocator.alloc_list_head_of(genAllocator, bucket));
            Assert.Equal((nuint)0, (nuint)allocator.alloc_list_tail_of(genAllocator, bucket));
            Assert.Equal(belowMinFreeList, gc_heap.unused_array_size(belowMinFreeListGap));

            gc_heap.uoh_thread_gap_front(firstGap, minFreeList, &gen);
            gc_heap.uoh_thread_gap_front(secondGap, minFreeList, &gen);

            Assert.Equal(unchecked(2 * minFreeList), generation.generation_free_list_space(&gen));
            Assert.Equal((nuint)0, generation.generation_free_obj_space(&gen));
            Assert.Equal((nuint)secondGap, (nuint)allocator.alloc_list_head_of(genAllocator, bucket));
            Assert.Equal((nuint)firstGap, (nuint)allocator.alloc_list_tail_of(genAllocator, bucket));
            Assert.Equal((nuint)firstGap, (nuint)allocator.free_list_slot(secondGap));
            Assert.Equal((nuint)0, (nuint)allocator.free_list_slot(firstGap));
            Assert.Equal(minFreeList, gc_heap.unused_array_size(firstGap));
            Assert.Equal(minFreeList, gc_heap.unused_array_size(secondGap));
            Assert.Equal((nuint)0x50, (nuint)((byte**)firstGap)[3]);
            Assert.Equal((nuint)0x60, (nuint)((byte**)secondGap)[3]);
        }
        finally
        {
            GCCommon.g_gc_pFreeObjectMethodTable = savedFreeObjectMethodTable;
        }
    }
#endif

    private static void ResetAllocationCallbackRecorder()
    {
        s_allocationCallbackCount = 0;
        s_lastAllocationDeferredOperation = allocation_deferred_operation.none;
        s_backgroundQueryCallbackCount = 0;
        s_budgetCheckCallbackCount = 0;
        s_highMemoryCallbackCount = 0;
        s_budgetTriggerCallbackCount = 0;
        s_fullGcCheckCallbackCount = 0;
    }

    private static void UohOomCallback(
        try_allocate_more_space_context* context,
        int operationValue,
        allocation_callback_result* result)
    {
        _ = context;
        allocation_deferred_operation operation = (allocation_deferred_operation)operationValue;
        s_allocationCallbackCount++;
        s_lastAllocationDeferredOperation = operation;

        *result = operation switch
        {
            allocation_deferred_operation.acquire_uoh_segment => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.segment_unavailable,
                oom_r = oom_reason.oom_loh,
            },
            allocation_deferred_operation.check_retry_uoh_segment => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            },
            allocation_deferred_operation.check_retry_other_heap => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            },
            allocation_deferred_operation.handle_oom => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            },
            allocation_deferred_operation.leave_more_space_lock => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            },
            _ => default,
        };
    }

    private static void RetryOtherHeapCallback(
        try_allocate_more_space_context* context,
        int operationValue,
        allocation_callback_result* result)
    {
        _ = context;
        allocation_deferred_operation operation = (allocation_deferred_operation)operationValue;
        s_allocationCallbackCount++;
        s_lastAllocationDeferredOperation = operation;

        *result = operation switch
        {
            allocation_deferred_operation.check_retry_other_heap => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.retry_other_heap,
            },
            allocation_deferred_operation.leave_more_space_lock => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            },
            _ => default,
        };
    }

    private static void NoFullEphemeralGcCallback(
        try_allocate_more_space_context* context,
        int operationValue,
        allocation_callback_result* result)
    {
        _ = context;
        allocation_deferred_operation operation = (allocation_deferred_operation)operationValue;
        s_allocationCallbackCount++;
        s_lastAllocationDeferredOperation = operation;

        *result = operation == allocation_deferred_operation.trigger_ephemeral_gc
            ? new allocation_callback_result
            {
                kind = allocation_callback_result_kind.no_full_compact_gc,
            }
            : operation == allocation_deferred_operation.leave_more_space_lock
                ? new allocation_callback_result
                {
                    kind = allocation_callback_result_kind.completed,
                }
                : default;
    }

    private static void BackgroundQueryCallback(
        try_allocate_more_space_context* context,
        int operationValue,
        allocation_callback_result* result)
    {
        _ = context;
        allocation_deferred_operation operation = (allocation_deferred_operation)operationValue;
        s_allocationCallbackCount++;
        s_lastAllocationDeferredOperation = operation;

        if (operation == allocation_deferred_operation.query_background_running)
        {
            s_backgroundQueryCallbackCount++;
        }

        *result = operation switch
        {
            allocation_deferred_operation.trigger_ephemeral_gc => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.no_full_compact_gc,
            },
            allocation_deferred_operation.query_background_running => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.background_not_running,
            },
            _ => default,
        };
    }

    private static void UnproductiveFullGcCallback(
        try_allocate_more_space_context* context,
        int operationValue,
        allocation_callback_result* result)
    {
        _ = context;
        allocation_deferred_operation operation = (allocation_deferred_operation)operationValue;
        s_allocationCallbackCount++;
        s_lastAllocationDeferredOperation = operation;

        *result = operation switch
        {
            allocation_deferred_operation.trigger_full_compact_gc => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.no_full_compact_gc,
            },
            allocation_deferred_operation.check_retry_other_heap => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            },
            allocation_deferred_operation.handle_oom => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            },
            allocation_deferred_operation.leave_more_space_lock => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            },
            _ => default,
        };
    }

    private static void BudgetRecheckCallback(
        try_allocate_more_space_context* context,
        int operationValue,
        allocation_callback_result* result)
    {
        _ = context;
        allocation_deferred_operation operation = (allocation_deferred_operation)operationValue;
        s_allocationCallbackCount++;
        s_lastAllocationDeferredOperation = operation;

        *result = operation switch
        {
            allocation_deferred_operation.enter_more_space_lock => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            },
            allocation_deferred_operation.check_for_full_gc => FullGcCheckedResult(),
            allocation_deferred_operation.check_allocation_budget => BudgetDisallowedResult(),
            allocation_deferred_operation.wait_for_bgc_high_memory => HighMemoryWaitedResult(),
            allocation_deferred_operation.trigger_gc_for_budget => BudgetTriggeredResult(),
            _ => default,
        };
    }

    private static void BudgetRetryCallback(
        try_allocate_more_space_context* context,
        int operationValue,
        allocation_callback_result* result)
    {
        _ = context;
        allocation_deferred_operation operation = (allocation_deferred_operation)operationValue;
        s_allocationCallbackCount++;
        s_lastAllocationDeferredOperation = operation;

        *result = operation switch
        {
            allocation_deferred_operation.check_allocation_budget => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.allocation_disallowed,
            },
            allocation_deferred_operation.trigger_gc_for_budget => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.retry_allocate,
            },
            _ => default,
        };
    }

    private static void NoBackgroundGcWaitCallback(
        try_allocate_more_space_context* context,
        int operationValue,
        allocation_callback_result* result)
    {
        _ = context;
        allocation_deferred_operation operation = (allocation_deferred_operation)operationValue;
        s_allocationCallbackCount++;
        s_lastAllocationDeferredOperation = operation;

        *result = operation == allocation_deferred_operation.check_and_wait_for_bgc
            ? new allocation_callback_result
            {
                kind = allocation_callback_result_kind.background_not_running,
            }
            : default;
    }

    private static allocation_callback_result BudgetDisallowedResult()
    {
        s_budgetCheckCallbackCount++;
        return new allocation_callback_result
        {
            kind = allocation_callback_result_kind.allocation_disallowed,
        };
    }

    private static allocation_callback_result HighMemoryWaitedResult()
    {
        s_highMemoryCallbackCount++;
        return new allocation_callback_result
        {
            kind = allocation_callback_result_kind.background_running,
        };
    }

    private static allocation_callback_result BudgetTriggeredResult()
    {
        s_budgetTriggerCallbackCount++;
        return new allocation_callback_result
        {
            kind = allocation_callback_result_kind.completed,
        };
    }

    private static allocation_callback_result FullGcCheckedResult()
    {
        s_fullGcCheckCallbackCount++;
        return new allocation_callback_result
        {
            kind = allocation_callback_result_kind.completed,
        };
    }
#endif

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private unsafe struct SegMappingAlignmentProbe
    {
        public byte prefix;
        public seg_mapping value;
    }

    private static nuint AlignmentOfSegMapping()
    {
        return (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<SegMappingAlignmentProbe>(nameof(SegMappingAlignmentProbe.value));
    }

    private static void ResetRegionAllocatorCallbackRecorder()
    {
        s_regionAllocatorCallbackCount = 0;
        s_regionAllocatorCallbackLastLeftUsed = 0;
    }

    private static void ResetCreateSegmentEventRecording()
    {
        ResetRegionAllocatorCallbackRecorder();
        GCToEEInterface.Reset();
        GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Information);
    }

    private static void DisableCreateSegmentEvents()
    {
        GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.None, GCEventLevel.None);
        GCToEEInterface.Reset();
    }

    private static void AssertCreateSegmentEvent(byte* address, nuint size, gc_etw_segment_type type)
    {
        Assert.Equal(GCToEEInterface.FiredEvent.GCCreateSegment_V1, GCToEEInterface.LastFiredEvent);
        Assert.Equal(1, GCToEEInterface.GCCreateSegmentCallCount);
        Assert.Equal((nuint)address, (nuint)GCToEEInterface.LastGCCreateSegmentAddress);
        Assert.Equal(size, GCToEEInterface.LastGCCreateSegmentSize);
        Assert.Equal((uint)type, GCToEEInterface.LastGCCreateSegmentType);
    }

    private static byte RegionAllocatorCallbackSuccess(byte* globalRegionLeftUsed)
    {
        s_regionAllocatorCallbackCount++;
        s_regionAllocatorCallbackLastLeftUsed = (nuint)globalRegionLeftUsed;
        return 1;
    }

    private static byte RegionAllocatorCallbackFailure(byte* globalRegionLeftUsed)
    {
        s_regionAllocatorCallbackCount++;
        s_regionAllocatorCallbackLastLeftUsed = (nuint)globalRegionLeftUsed;
        return 0;
    }

    private static void InitializeRegion(heap_segment* region, nuint start, nuint committed, nuint reserved, int age)
    {
        region->mem = (byte*)(start + (nuint)sizeof(aligned_plug_and_gap));
        region->committed = (byte*)committed;
        region->reserved = (byte*)reserved;
        region->next = null;
        region->prev_free_region = null;
        region->containing_free_list = null;
        region->age_in_free = age;
    }

    private static void InitializeRegionMoveGlobals(seg_mapping* table, nuint alignment)
    {
        gc_heap.min_segment_size_shr = (nuint)gc_heap.index_of_highest_set_bit(alignment);
        GCCommon.seg_mapping_table = table;
        gc_heap.global_region_allocator.initialize_alignment(alignment);
    }

    private static void RestoreRegionMoveGlobals(nuint oldShift, seg_mapping* oldTable, region_allocator oldGlobalAllocator)
    {
        gc_heap.min_segment_size_shr = oldShift;
        GCCommon.seg_mapping_table = oldTable;
        gc_heap.global_region_allocator = oldGlobalAllocator;
    }

    private static void InitializeRegionAllocatorForMove(region_allocator* allocator, uint* mapLeftStart, int usedUnits, nuint alignment, byte* globalStart)
    {
        WriteRegionAllocatorPointerField(allocator, "global_region_start", globalStart);
        WriteRegionAllocatorPointerField(allocator, "global_region_end", globalStart + (nint)((nuint)usedUnits * alignment));
        WriteRegionAllocatorPointerField(allocator, "global_region_left_used", globalStart + (nint)((nuint)usedUnits * alignment));
        WriteRegionAllocatorPointerField(allocator, "global_region_right_used", globalStart + (nint)((nuint)usedUnits * alignment));
        WriteRegionAllocatorField(allocator, "total_free_units", 0u);
        WriteRegionAllocatorField(allocator, "region_alignment", alignment);
        WriteRegionAllocatorField(allocator, "large_region_alignment", (nuint)region_allocator.LARGE_REGION_FACTOR * alignment);
        WriteRegionAllocatorPointerField(allocator, "region_map_left_start", mapLeftStart);
        WriteRegionAllocatorPointerField(allocator, "region_map_left_end", mapLeftStart + usedUnits);
        WriteRegionAllocatorPointerField(allocator, "region_map_right_start", mapLeftStart + usedUnits);
        WriteRegionAllocatorPointerField(allocator, "region_map_right_end", mapLeftStart + usedUnits);
        WriteRegionAllocatorField(allocator, "num_left_used_free_units", 0u);
        WriteRegionAllocatorField(allocator, "num_right_used_free_units", 0u);
    }

    private static heap_segment* InitializeMappedRegion(seg_mapping* table, nuint start, uint numUnits, nuint alignment)
    {
        heap_segment* region = &table[(int)(start >> (int)gc_heap.min_segment_size_shr)].region_info;
        *region = default;
        nuint size = (nuint)numUnits * alignment;
        InitializeRegion(region, start, start + size, start + size, age: 0);
        return region;
    }

    private static void ClearRegionFreeLists(region_free_list* lists)
    {
        for (int kind = (int)free_region_kind.basic_free_region;
             kind < (int)free_region_kind.count_free_region_kinds;
             kind++)
        {
            lists[kind] = default;
        }
    }

    private static uint* InitializeRegionAllocatorMap(region_allocator* allocator, nuint start, nuint end, nuint alignment)
    {
        byte* lowest = null;
        byte* highest = null;

        Assert.True(allocator->init((byte*)start, (byte*)end, alignment, &lowest, &highest));
        return (uint*)ReadRegionAllocatorPointerField(allocator, "region_map_left_start");
    }

    private static void DeleteRegionImplUnderLock(region_allocator* allocator, byte* regionStart)
    {
        allocator->initialize();
        allocator->enter_spin_lock();
        try
        {
            allocator->delete_region_impl(regionStart);
        }
        finally
        {
            allocator->leave_spin_lock();
        }
    }

    private static uint EncodedFreeRegionBlock(uint numUnits)
    {
        return unchecked((uint)region_allocator.region_alloc_free_bit) | numUnits;
    }

    private sealed unsafe class MarkPhaseStateScope : System.IDisposable
    {
        private readonly gc_mechanisms _settings;
        private readonly mark_queue_t _markQueue;
        private readonly nuint _markStackTos;
        private readonly nuint _markStackBos;
        private readonly byte* _oldestPinnedPlug;
        private readonly mark* _markStackArray;
        private readonly nuint _markStackArrayLength;
        private readonly byte* _minOverflowAddress;
        private readonly byte* _maxOverflowAddress;
#if USE_REGIONS && !MULTIPLE_HEAPS
        private readonly byte** _gMarkList;
        private readonly byte** _gMarkListCopy;
        private readonly nuint _markListSize;
        private readonly nuint _gMarkListTotalSize;
        private readonly bool _markListOverflow;
        private readonly byte*** _gMarkListPiece;
        private readonly nuint _gMarkListPieceSize;
        private readonly nuint _gMarkListPieceTotalSize;
        private readonly byte** _markList;
        private readonly byte** _markListEnd;
        private readonly byte** _markListIndex;
        private readonly byte* _gcLow;
        private readonly byte* _gcHigh;
        private readonly byte* _ephemeralLow;
        private readonly byte* _ephemeralHigh;
        private readonly byte* _slow;
        private readonly byte* _shigh;
        private readonly nuint _regionCount;
        private readonly nuint* _survivedPerRegion;
        private readonly nuint* _oldCardSurvivedPerRegion;
#endif

        public MarkPhaseStateScope()
        {
            _settings = gc_heap.settings;
            _markQueue = gc_heap.mark_queue;
            _markStackTos = gc_heap.mark_stack_tos;
            _markStackBos = gc_heap.mark_stack_bos;
            _oldestPinnedPlug = gc_heap.oldest_pinned_plug;
            _markStackArray = gc_heap.mark_stack_array;
            _markStackArrayLength = gc_heap.mark_stack_array_length;
            _minOverflowAddress = gc_heap.min_overflow_address;
            _maxOverflowAddress = gc_heap.max_overflow_address;
#if USE_REGIONS && !MULTIPLE_HEAPS
            _gMarkList = gc_heap.g_mark_list;
            _gMarkListCopy = gc_heap.g_mark_list_copy;
            _markListSize = gc_heap.mark_list_size;
            _gMarkListTotalSize = gc_heap.g_mark_list_total_size;
            _markListOverflow = gc_heap.mark_list_overflow;
            _gMarkListPiece = gc_heap.g_mark_list_piece;
            _gMarkListPieceSize = gc_heap.g_mark_list_piece_size;
            _gMarkListPieceTotalSize = gc_heap.g_mark_list_piece_total_size;
            _markList = gc_heap.mark_list;
            _markListEnd = gc_heap.mark_list_end;
            _markListIndex = gc_heap.mark_list_index;
            _gcLow = gc_heap.gc_low;
            _gcHigh = gc_heap.gc_high;
            _ephemeralLow = gc_heap.ephemeral_low;
            _ephemeralHigh = gc_heap.ephemeral_high;
            _slow = gc_heap.slow;
            _shigh = gc_heap.shigh;
            _regionCount = gc_heap.region_count;
            _survivedPerRegion = gc_heap.survived_per_region;
            _oldCardSurvivedPerRegion = gc_heap.old_card_survived_per_region;
#endif

            gc_heap.settings = default;
            gc_heap.initialize_mark_phase_state();
        }

        public void Dispose()
        {
            gc_heap.settings = _settings;
            gc_heap.mark_queue = _markQueue;
            gc_heap.mark_stack_tos = _markStackTos;
            gc_heap.mark_stack_bos = _markStackBos;
            gc_heap.oldest_pinned_plug = _oldestPinnedPlug;
            gc_heap.mark_stack_array = _markStackArray;
            gc_heap.mark_stack_array_length = _markStackArrayLength;
            gc_heap.min_overflow_address = _minOverflowAddress;
            gc_heap.max_overflow_address = _maxOverflowAddress;
#if USE_REGIONS && !MULTIPLE_HEAPS
            gc_heap.g_mark_list = _gMarkList;
            gc_heap.g_mark_list_copy = _gMarkListCopy;
            gc_heap.mark_list_size = _markListSize;
            gc_heap.g_mark_list_total_size = _gMarkListTotalSize;
            gc_heap.mark_list_overflow = _markListOverflow;
            gc_heap.g_mark_list_piece = _gMarkListPiece;
            gc_heap.g_mark_list_piece_size = _gMarkListPieceSize;
            gc_heap.g_mark_list_piece_total_size = _gMarkListPieceTotalSize;
            gc_heap.mark_list = _markList;
            gc_heap.mark_list_end = _markListEnd;
            gc_heap.mark_list_index = _markListIndex;
            gc_heap.gc_low = _gcLow;
            gc_heap.gc_high = _gcHigh;
            gc_heap.ephemeral_low = _ephemeralLow;
            gc_heap.ephemeral_high = _ephemeralHigh;
            gc_heap.slow = _slow;
            gc_heap.shigh = _shigh;
            gc_heap.region_count = _regionCount;
            gc_heap.survived_per_region = _survivedPerRegion;
            gc_heap.old_card_survived_per_region = _oldCardSurvivedPerRegion;
#endif
        }
    }

#if USE_REGIONS && !MULTIPLE_HEAPS
    private sealed class InitRecordsStateScope : System.IDisposable
    {
        private readonly gc_history_per_heap _gcDataPerHeap;
        private readonly gc_history_global _gcDataGlobal;
        private readonly fgm_history _fgmResult;
        private readonly nuint _endGen0RegionSpace;
        private readonly nuint _endGen0RegionCommittedSpace;
        private readonly nuint _gen0PinnedFreeSpace;
        private readonly bool _gen0LargeChunkFound;
        private readonly int _numRegionsFreedInSweep;
        private readonly int _sufficientGen0Space;

        public InitRecordsStateScope()
        {
            _gcDataPerHeap = gc_heap.gc_data_per_heap;
            _gcDataGlobal = gc_heap.gc_data_global;
            _fgmResult = gc_heap.fgm_result;
            _endGen0RegionSpace = gc_heap.end_gen0_region_space;
            _endGen0RegionCommittedSpace = gc_heap.end_gen0_region_committed_space;
            _gen0PinnedFreeSpace = gc_heap.gen0_pinned_free_space;
            _gen0LargeChunkFound = gc_heap.gen0_large_chunk_found;
            _numRegionsFreedInSweep = gc_heap.num_regions_freed_in_sweep;
            _sufficientGen0Space = gc_heap.sufficient_gen0_space_p;
        }

        public void Dispose()
        {
            gc_heap.gc_data_per_heap = _gcDataPerHeap;
            gc_heap.gc_data_global = _gcDataGlobal;
            gc_heap.fgm_result = _fgmResult;
            gc_heap.end_gen0_region_space = _endGen0RegionSpace;
            gc_heap.end_gen0_region_committed_space = _endGen0RegionCommittedSpace;
            gc_heap.gen0_pinned_free_space = _gen0PinnedFreeSpace;
            gc_heap.gen0_large_chunk_found = _gen0LargeChunkFound;
            gc_heap.num_regions_freed_in_sweep = _numRegionsFreedInSweep;
            gc_heap.sufficient_gen0_space_p = _sufficientGen0Space;
        }
    }
#endif

#if USE_REGIONS
    private sealed unsafe class RelocateAddressStateScope : System.IDisposable
    {
        private readonly nuint _minSegmentSizeShr;
        private readonly region_info* _mapRegionToGenerationSkewed;
        private readonly seg_mapping* _segMappingTable;
        private readonly byte* _gcLow;
        private readonly byte* _gcHigh;
        private readonly byte* _lowestAddress;
        private readonly short* _brickTable;
        private readonly byte* _globalLowestAddress;
        private readonly byte* _globalHighestAddress;
        private readonly gc_mechanisms _settings;
        private readonly int _lohCompacted;

        public RelocateAddressStateScope(
            byte* lowestAddress,
            byte* highestAddress,
            short* brickTable,
            region_info* generationMap,
            seg_mapping* segmentMap = null)
        {
            _minSegmentSizeShr = gc_heap.min_segment_size_shr;
            _mapRegionToGenerationSkewed = gc_heap.map_region_to_generation_skewed;
            _segMappingTable = GCCommon.seg_mapping_table;
            _gcLow = gc_heap.gc_low;
            _gcHigh = gc_heap.gc_high;
            _lowestAddress = gc_heap.lowest_address;
            _brickTable = gc_heap.brick_table;
            _globalLowestAddress = GCCommon.g_gc_lowest_address;
            _globalHighestAddress = GCCommon.g_gc_highest_address;
            _settings = gc_heap.settings;
            _lohCompacted = gc_heap.loh_compacted_p;

            gc_heap.min_segment_size_shr = 12;
            gc_heap.map_region_to_generation_skewed =
                generationMap - (nint)((nuint)lowestAddress >> (int)gc_heap.min_segment_size_shr);
            gc_heap.gc_low = lowestAddress;
            gc_heap.gc_high = highestAddress;
            gc_heap.lowest_address = lowestAddress;
            gc_heap.brick_table = brickTable;
            GCCommon.g_gc_lowest_address = lowestAddress;
            GCCommon.g_gc_highest_address = highestAddress;
            gc_heap.settings = default;
            gc_heap.settings.condemned_generation = (int)gc_generation_num.soh_gen1;
            gc_heap.loh_compacted_p = 0;

            for (int i = 0; i < 4; i++)
            {
                brickTable[i] = 0;
                generationMap[i] = region_info.RI_GEN_0;
            }

            if (segmentMap is not null)
            {
                nuint firstRegionIndex =
                    (nuint)lowestAddress >> (int)gc_heap.min_segment_size_shr;
                GCCommon.seg_mapping_table = segmentMap - (nint)firstRegionIndex;
                for (int i = 0; i < 4; i++)
                {
                    segmentMap[i] = default;
                    heap_segment.heap_segment_gen_num(&segmentMap[i].region_info) =
                        (byte)gc_generation_num.soh_gen0;
                    heap_segment.heap_segment_plan_gen_num(&segmentMap[i].region_info) =
                        (int)gc_generation_num.soh_gen0;
                }
            }
        }

        public void Dispose()
        {
            gc_heap.min_segment_size_shr = _minSegmentSizeShr;
            gc_heap.map_region_to_generation_skewed = _mapRegionToGenerationSkewed;
            GCCommon.seg_mapping_table = _segMappingTable;
            gc_heap.gc_low = _gcLow;
            gc_heap.gc_high = _gcHigh;
            gc_heap.lowest_address = _lowestAddress;
            gc_heap.brick_table = _brickTable;
            GCCommon.g_gc_lowest_address = _globalLowestAddress;
            GCCommon.g_gc_highest_address = _globalHighestAddress;
            gc_heap.settings = _settings;
            gc_heap.loh_compacted_p = _lohCompacted;
        }
    }

    private static MethodTable* InitializePointerMethodTable(
        byte* descriptorStorage,
        nuint objectSize,
        nuint pointerCount)
    {
        int descriptorSize = sizeof(nuint) + sizeof(CGCDescSeries);
        MethodTable* methodTable = (MethodTable*)(descriptorStorage + descriptorSize);
        methodTable->m_uFlags = MethodTable.HasPointersFlag;
        methodTable->m_uBaseSize = (uint)objectSize;
        *((nuint*)methodTable - 1) = 1;

        CGCDescSeries* series = (CGCDescSeries*)descriptorStorage;
        series->seriessize = unchecked(
            (nuint)(-(nint)(objectSize - (pointerCount * (nuint)sizeof(byte*)))));
        series->startoffset = (nuint)sizeof(byte*);
        return methodTable;
    }

    private sealed class PlanPhaseStateScope : System.IDisposable
    {
        private readonly gc_mechanisms _settings;
        private readonly nuint _endGen0RegionSpace;
        private readonly nuint _gen0PinnedFreeSpace;
        private readonly bool _gen0LargeChunkFound;

        public PlanPhaseStateScope()
        {
            _settings = gc_heap.settings;
            _endGen0RegionSpace = gc_heap.end_gen0_region_space;
            _gen0PinnedFreeSpace = gc_heap.gen0_pinned_free_space;
            _gen0LargeChunkFound = gc_heap.gen0_large_chunk_found;

            gc_heap.settings = default;
            gc_heap.end_gen0_region_space = 0;
            gc_heap.gen0_pinned_free_space = 0;
            gc_heap.gen0_large_chunk_found = false;
        }

        public void Dispose()
        {
            gc_heap.settings = _settings;
            gc_heap.end_gen0_region_space = _endGen0RegionSpace;
            gc_heap.gen0_pinned_free_space = _gen0PinnedFreeSpace;
            gc_heap.gen0_large_chunk_found = _gen0LargeChunkFound;
        }
    }
#endif

#if USE_REGIONS
    private sealed unsafe class CardTableStateScope : System.IDisposable
    {
        private readonly uint* _cardTable;

        public CardTableStateScope()
        {
            _cardTable = gc_heap.card_table;
            gc_heap.card_table = null;
        }

        public void Dispose()
        {
            gc_heap.card_table = _cardTable;
        }
    }
#endif

    private sealed unsafe class RegionSegmentsStateScope : System.IDisposable
    {
        private readonly nuint _minSegmentSizeShr;
        private readonly seg_mapping* _segMappingTable;
        private readonly region_allocator _globalRegionAllocator;
        private readonly gc_heap.region_free_list_array _freeRegions;
        private readonly uint* _cardTable;
        private readonly short* _brickTable;
        private readonly byte* _gcLowestAddress;
        private readonly byte* _gcHighestAddress;
        private readonly byte* _bookkeepingCoveredCommitted;
#if USE_REGIONS && !MULTIPLE_HEAPS
        private readonly int _gen0BricksCleared;
        private readonly int _gen0MustClearBricks;
#endif
        private readonly gc_heap.recorded_committed_bucket_array _committedByOh;
        private readonly nuint _currentTotalCommitted;
        private readonly nuint _currentTotalCommittedBookkeeping;
        private readonly nuint _heapHardLimit;
        private readonly gc_heap.object_heap_array _heapHardLimitOh;
        private readonly bool _neverDecommit;
        private readonly nuint _reservedMemory;
        private readonly gc_mechanisms _settings;
        private readonly CLRCriticalSection _checkCommitCs;
        private readonly heap_segment* _freeableUohSegment;
        private readonly bool _initializedCommitLock;
#if BACKGROUND_GC
        private readonly GCCommon.changed_seg_array _savedChangedSegs;
        private readonly ulong _savedChangedSegsCount;
        private readonly bgc_state _currentBgcState;
        private readonly byte* _backgroundSavedLowestAddress;
        private readonly byte* _backgroundSavedHighestAddress;
        private readonly int _gcBackgroundRunning;
        private readonly uint* _markArray;
        private readonly byte* _lowestAddress;
        private readonly byte* _highestAddress;
#endif

        public RegionSegmentsStateScope(bool initializeCommitLock)
        {
            _minSegmentSizeShr = gc_heap.min_segment_size_shr;
            _segMappingTable = GCCommon.seg_mapping_table;
            _globalRegionAllocator = gc_heap.global_region_allocator;
            _freeRegions = gc_heap.free_regions;
            _cardTable = gc_heap.card_table;
            _brickTable = gc_heap.brick_table;
            _gcLowestAddress = GCCommon.g_gc_lowest_address;
            _gcHighestAddress = GCCommon.g_gc_highest_address;
            _bookkeepingCoveredCommitted = gc_heap.bookkeeping_covered_committed;
#if USE_REGIONS && !MULTIPLE_HEAPS
            _gen0BricksCleared = gc_heap.gen0_bricks_cleared;
            _gen0MustClearBricks = gc_heap.gen0_must_clear_bricks;
#endif
            _committedByOh = gc_heap.committed_by_oh;
            _currentTotalCommitted = gc_heap.current_total_committed;
            _currentTotalCommittedBookkeeping = gc_heap.current_total_committed_bookkeeping;
            _heapHardLimit = gc_heap.heap_hard_limit;
            _heapHardLimitOh = gc_heap.heap_hard_limit_oh;
            _neverDecommit = gc_heap.never_decommit_p;
            _reservedMemory = gc_heap.reserved_memory;
            _settings = gc_heap.settings;
            _checkCommitCs = gc_heap.check_commit_cs;
            _freeableUohSegment = gc_heap.freeable_uoh_segment;
#if BACKGROUND_GC
            _savedChangedSegs = GCCommon.saved_changed_segs;
            _savedChangedSegsCount = GCCommon.saved_changed_segs_count;
            _currentBgcState = gc_heap.current_bgc_state;
            _backgroundSavedLowestAddress = gc_heap.background_saved_lowest_address;
            _backgroundSavedHighestAddress = gc_heap.background_saved_highest_address;
            _gcBackgroundRunning = gc_heap.gc_background_running;
            _markArray = gc_heap.mark_array;
            _lowestAddress = gc_heap.lowest_address;
            _highestAddress = gc_heap.highest_address;
#endif

            gc_heap.free_regions = default;
            gc_heap.card_table = null;
            gc_heap.brick_table = null;
            gc_heap.bookkeeping_covered_committed = null;
            gc_heap.committed_by_oh = default;
            gc_heap.current_total_committed = 0;
            gc_heap.current_total_committed_bookkeeping = 0;
            gc_heap.heap_hard_limit = 0;
            gc_heap.heap_hard_limit_oh = default;
            gc_heap.never_decommit_p = false;
            gc_heap.reserved_memory = 0;
            gc_heap.settings = default;
            gc_heap.freeable_uoh_segment = null;
#if BACKGROUND_GC
            GCCommon.saved_changed_segs = default;
            GCCommon.initialize();
            gc_heap.current_bgc_state = default;
            gc_heap.background_saved_lowest_address = null;
            gc_heap.background_saved_highest_address = null;
            gc_heap.gc_background_running = 0;
            gc_heap.mark_array = null;
            gc_heap.lowest_address = null;
            gc_heap.highest_address = null;
#endif

            if (initializeCommitLock)
            {
                gc_heap.check_commit_cs = default;
                _initializedCommitLock = gc_heap.check_commit_cs.Initialize();
                Assert.True(_initializedCommitLock);
            }
        }

        public void Dispose()
        {
            if (_initializedCommitLock)
            {
                gc_heap.check_commit_cs.Destroy();
            }

            gc_heap.min_segment_size_shr = _minSegmentSizeShr;
            GCCommon.seg_mapping_table = _segMappingTable;
            gc_heap.global_region_allocator = _globalRegionAllocator;
            gc_heap.free_regions = _freeRegions;
            gc_heap.card_table = _cardTable;
            gc_heap.brick_table = _brickTable;
            GCCommon.g_gc_lowest_address = _gcLowestAddress;
            GCCommon.g_gc_highest_address = _gcHighestAddress;
            gc_heap.bookkeeping_covered_committed = _bookkeepingCoveredCommitted;
#if USE_REGIONS && !MULTIPLE_HEAPS
            gc_heap.gen0_bricks_cleared = _gen0BricksCleared;
            gc_heap.gen0_must_clear_bricks = _gen0MustClearBricks;
#endif
            gc_heap.committed_by_oh = _committedByOh;
            gc_heap.current_total_committed = _currentTotalCommitted;
            gc_heap.current_total_committed_bookkeeping = _currentTotalCommittedBookkeeping;
            gc_heap.heap_hard_limit = _heapHardLimit;
            gc_heap.heap_hard_limit_oh = _heapHardLimitOh;
            gc_heap.never_decommit_p = _neverDecommit;
            gc_heap.reserved_memory = _reservedMemory;
            gc_heap.settings = _settings;
            gc_heap.check_commit_cs = _checkCommitCs;
            gc_heap.freeable_uoh_segment = _freeableUohSegment;
#if BACKGROUND_GC
            GCCommon.saved_changed_segs = _savedChangedSegs;
            GCCommon.saved_changed_segs_count = _savedChangedSegsCount;
            gc_heap.current_bgc_state = _currentBgcState;
            gc_heap.background_saved_lowest_address = _backgroundSavedLowestAddress;
            gc_heap.background_saved_highest_address = _backgroundSavedHighestAddress;
            gc_heap.gc_background_running = _gcBackgroundRunning;
            gc_heap.mark_array = _markArray;
            gc_heap.lowest_address = _lowestAddress;
            gc_heap.highest_address = _highestAddress;
#endif
        }
    }

    private unsafe struct RegionAllocatorSnapshot
    {
        public byte* GlobalRegionStart;
        public byte* GlobalRegionEnd;
        public byte* GlobalRegionLeftUsed;
        public byte* GlobalRegionRightUsed;
        public uint TotalFreeUnits;
        public uint* RegionMapLeftStart;
        public uint* RegionMapLeftEnd;
        public uint* RegionMapRightStart;
        public uint* RegionMapRightEnd;
        public uint NumLeftUsedFreeUnits;
        public uint NumRightUsedFreeUnits;
    }

    private static RegionAllocatorSnapshot CaptureRegionAllocatorSnapshot(region_allocator* allocator)
    {
        return new RegionAllocatorSnapshot
        {
            GlobalRegionStart = (byte*)ReadRegionAllocatorPointerField(allocator, "global_region_start"),
            GlobalRegionEnd = (byte*)ReadRegionAllocatorPointerField(allocator, "global_region_end"),
            GlobalRegionLeftUsed = (byte*)ReadRegionAllocatorPointerField(allocator, "global_region_left_used"),
            GlobalRegionRightUsed = (byte*)ReadRegionAllocatorPointerField(allocator, "global_region_right_used"),
            TotalFreeUnits = ReadRegionAllocatorField<uint>(allocator, "total_free_units"),
            RegionMapLeftStart = (uint*)ReadRegionAllocatorPointerField(allocator, "region_map_left_start"),
            RegionMapLeftEnd = (uint*)ReadRegionAllocatorPointerField(allocator, "region_map_left_end"),
            RegionMapRightStart = (uint*)ReadRegionAllocatorPointerField(allocator, "region_map_right_start"),
            RegionMapRightEnd = (uint*)ReadRegionAllocatorPointerField(allocator, "region_map_right_end"),
            NumLeftUsedFreeUnits = ReadRegionAllocatorField<uint>(allocator, "num_left_used_free_units"),
            NumRightUsedFreeUnits = ReadRegionAllocatorField<uint>(allocator, "num_right_used_free_units"),
        };
    }

    private static void AssertRegionAllocatorSnapshotEqual(RegionAllocatorSnapshot expected, region_allocator* allocator)
    {
        Assert.Equal((nuint)expected.GlobalRegionStart, (nuint)ReadRegionAllocatorPointerField(allocator, "global_region_start"));
        Assert.Equal((nuint)expected.GlobalRegionEnd, (nuint)ReadRegionAllocatorPointerField(allocator, "global_region_end"));
        Assert.Equal((nuint)expected.GlobalRegionLeftUsed, (nuint)ReadRegionAllocatorPointerField(allocator, "global_region_left_used"));
        Assert.Equal((nuint)expected.GlobalRegionRightUsed, (nuint)ReadRegionAllocatorPointerField(allocator, "global_region_right_used"));
        Assert.Equal(expected.TotalFreeUnits, ReadRegionAllocatorField<uint>(allocator, "total_free_units"));
        Assert.Equal((nuint)expected.RegionMapLeftStart, (nuint)ReadRegionAllocatorPointerField(allocator, "region_map_left_start"));
        Assert.Equal((nuint)expected.RegionMapLeftEnd, (nuint)ReadRegionAllocatorPointerField(allocator, "region_map_left_end"));
        Assert.Equal((nuint)expected.RegionMapRightStart, (nuint)ReadRegionAllocatorPointerField(allocator, "region_map_right_start"));
        Assert.Equal((nuint)expected.RegionMapRightEnd, (nuint)ReadRegionAllocatorPointerField(allocator, "region_map_right_end"));
        Assert.Equal(expected.NumLeftUsedFreeUnits, ReadRegionAllocatorField<uint>(allocator, "num_left_used_free_units"));
        Assert.Equal(expected.NumRightUsedFreeUnits, ReadRegionAllocatorField<uint>(allocator, "num_right_used_free_units"));
    }

    private static T ReadRegionAllocatorField<T>(region_allocator* allocator, string fieldName)
        where T : unmanaged
    {
        return *(T*)((byte*)allocator + (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>(fieldName));
    }

    private static void WriteRegionAllocatorField<T>(region_allocator* allocator, string fieldName, T value)
        where T : unmanaged
    {
        *(T*)((byte*)allocator + (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>(fieldName)) = value;
    }

    private static void* ReadRegionAllocatorPointerField(region_allocator* allocator, string fieldName)
    {
        return *(void**)((byte*)allocator + (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>(fieldName));
    }

    private static void WriteRegionAllocatorPointerField(region_allocator* allocator, string fieldName, void* value)
    {
        *(void**)((byte*)allocator + (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>(fieldName)) = value;
    }
#endif

    private static nuint OffsetOf(void* field, seg_mapping* mapping) => (nuint)((byte*)field - (byte*)mapping);

    private static nuint OffsetOf(void* field, heap_segment* segment) => (nuint)((byte*)field - (byte*)segment);
}
