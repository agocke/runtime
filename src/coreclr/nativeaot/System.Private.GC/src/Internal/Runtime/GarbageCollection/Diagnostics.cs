// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from the active WKS USE_REGIONS paths in diagnostics.cpp, gcee.cpp, gc.cpp, and
// interface.cpp.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
#pragma warning restore CS8981
{
    private const nuint EtwAllocationTick = 100 * 1024;
    private const int MaxEtwGcTimeInfo = 8;

    [InlineArray((int)gc_generation_num.total_generation_count)]
    private struct generation_size_array
    {
        private nuint _element0;
    }

    [InlineArray(total_oh_count)]
    private struct allocation_amount_array
    {
        private nuint _element0;
    }

    [InlineArray(3)]
    private struct generation_field_offset_array
    {
        private int _element0;
    }

    private struct etw_loh_compact_info
    {
        public uint time_plan;
        public uint time_compact;
        public uint time_relocate;
        public nuint total_refs;
        public nuint zero_refs;
    }

    private static byte s_build_variant;
    private static byte s_built_with_svr;
    private static uint s_max_generation;
    private static GcDacVars* s_dac_vars;
    private static generation_field_offset_array s_generation_field_offsets;
    private static generation_size_array s_generation_sizes;
    private static generation_size_array s_generation_promoted_sizes;
    private static allocation_amount_array s_etw_allocation_running_amount;
    private static ulong s_total_time_in_gc;
    private static ulong s_total_time_since_last_gc_end;
    private static uint s_percent_time_in_gc_since_last_gc;
    private static etw_loh_compact_info s_loh_compact_info;
    private static ulong s_loh_phase_start;

    public static oom_history oom_info;
    public static nuint physical_memory_from_config;
    public static nuint gen0_min_budget_from_config;
    public static nuint gen0_max_budget_from_config;
    public static uint high_mem_percent_from_config;
    public static byte use_large_pages_p;
    public static byte use_frozen_segments_p;

    public static void PopulateDacVars(GcDacVars* gcDacVars)
    {
#if USE_REGIONS && !MULTIPLE_HEAPS
        Debug.Assert(gcDacVars is not null);

        bool v2 = gcDacVars->minor_version_number >= 2;
        bool v4 = gcDacVars->minor_version_number >= 4;
        bool v6 = gcDacVars->minor_version_number >= 6;
        bool v8 = gcDacVars->minor_version_number >= 8;

        gcDacVars->major_version_number = 2;
        gcDacVars->minor_version_number = 8;

        if (v2)
        {
            gcDacVars->total_bookkeeping_elements =
                (int)bookkeeping_element.total_bookkeeping_elements;
            gcDacVars->count_free_region_kinds = (int)free_region_kind.count_free_region_kinds;
            gcDacVars->card_table_info_size = (nuint)sizeof(card_table_info);
        }

        s_build_variant = GCInterfaceDacConstants.build_variant_use_region;
#if BACKGROUND_GC
        s_build_variant |= GCInterfaceDacConstants.build_variant_background_gc;
#endif
        s_built_with_svr = 0;
        s_max_generation = GCInterfaceOffsets.max_generation;

        generation generationValue = default;
        s_generation_field_offsets[0] = 0;
        s_generation_field_offsets[1] =
            unchecked((int)((byte*)&generationValue.start_segment - (byte*)&generationValue));
        s_generation_field_offsets[2] = -1;

        s_dac_vars = gcDacVars;
        gcDacVars->generation_size = (nuint)sizeof(generation);
        gcDacVars->total_generation_count = (nuint)gc_generation_num.total_generation_count;
        gcDacVars->build_variant = (byte*)Unsafe.AsPointer(ref s_build_variant);
        gcDacVars->built_with_svr = (byte*)Unsafe.AsPointer(ref s_built_with_svr);
        gcDacVars->gc_global_mechanisms = null;
        gcDacVars->generation_table = null;
        gcDacVars->max_gen = (uint*)Unsafe.AsPointer(ref s_max_generation);
        fixed (uint** markArrayAddress = &mark_array)
        {
            gcDacVars->mark_array = markArrayAddress;
        }
#if BACKGROUND_GC
        gcDacVars->current_c_gc_state =
            (c_gc_state*)Unsafe.AsPointer(ref current_c_gc_state);
        fixed (byte** backgroundLowestAddress =
                   &background_saved_lowest_address)
        {
            gcDacVars->background_saved_lowest_address =
                backgroundLowestAddress;
        }

        fixed (byte** backgroundHighestAddress =
                   &background_saved_highest_address)
        {
            gcDacVars->background_saved_highest_address =
                backgroundHighestAddress;
        }

        fixed (byte** nextSweepObjectAddress = &current_sweep_pos)
        {
            gcDacVars->next_sweep_obj = nextSweepObjectAddress;
        }

        if (v2)
        {
            fixed (heap_segment** freeableSohAddress =
                       &freeable_soh_segment)
            {
                gcDacVars->freeable_soh_segment =
                    (dac_heap_segment**)freeableSohAddress;
            }

            fixed (heap_segment** freeableUohAddress =
                       &freeable_uoh_segment)
            {
                gcDacVars->freeable_uoh_segment =
                    (dac_heap_segment**)freeableUohAddress;
            }
        }
#else
        gcDacVars->current_c_gc_state = null;
        gcDacVars->background_saved_lowest_address = null;
        gcDacVars->background_saved_highest_address = null;
        gcDacVars->next_sweep_obj = null;
        if (v2)
        {
            gcDacVars->freeable_soh_segment = null;
            gcDacVars->freeable_uoh_segment = null;
        }
#endif
        gcDacVars->saved_sweep_ephemeral_seg = null;
        gcDacVars->saved_sweep_ephemeral_start = null;
        gcDacVars->alloc_allocated = null;
        gcDacVars->oom_info = (oom_history*)Unsafe.AsPointer(ref oom_info);
        fixed (CFinalize** finalizeQueueAddress = &finalize_queue)
        {
            gcDacVars->finalize_queue =
                (dac_finalize_queue**)finalizeQueueAddress;
        }
        gcDacVars->internal_root_array = null;
        gcDacVars->internal_root_array_index = null;
        gcDacVars->heap_analyze_success = null;
        gcDacVars->n_heaps = null;
        gcDacVars->g_heaps = null;
        gcDacVars->gc_structures_invalid_cnt = GCScan.DacInvalidCountAddress();
        gcDacVars->interesting_data_per_heap = null;
        gcDacVars->compact_reasons_per_heap = null;
        gcDacVars->expand_mechanisms_per_heap = null;
        gcDacVars->interesting_mechanism_bits_per_heap = null;
        gcDacVars->handle_table_map =
            (dac_handle_table_map*)Unsafe.AsPointer(ref ObjectHandle.g_HandleTableMap);
        gcDacVars->gc_heap_field_offsets = null;
        gcDacVars->generation_field_offsets =
            (int**)Unsafe.AsPointer(ref s_generation_field_offsets[0]);
        if (v2)
        {
            fixed (byte** bookkeepingStartAddress = &bookkeeping_start)
            {
                gcDacVars->bookkeeping_start = bookkeepingStartAddress;
            }
            gcDacVars->global_regions_to_decommit =
                (dac_region_free_list**)Unsafe.AsPointer(ref global_regions_to_decommit[0]);
            gcDacVars->global_free_huge_regions =
                (dac_region_free_list**)Unsafe.AsPointer(ref global_free_huge_regions);
            gcDacVars->free_regions =
                (dac_region_free_list**)Unsafe.AsPointer(ref free_regions[0]);
        }

        if (v4)
        {
            gcDacVars->dynamic_adaptation_mode = null;
        }

        if (v6)
        {
            gcDacVars->gc_descriptor = null;
        }

        if (v8)
        {
            gcDacVars->g_totalCpuCount = null;
        }
#else
        gcDacVars->major_version_number = 0;
        gcDacVars->minor_version_number = 0;
#endif
    }

    public static void PublishHeapDacVars(gc_heap* hp)
    {
#if USE_REGIONS && !MULTIPLE_HEAPS
        if (s_dac_vars is null || hp is null)
        {
            return;
        }

        s_dac_vars->generation_table =
            (unused_generation**)generation_table_of(hp);
        s_dac_vars->ephemeral_heap_segment =
            (dac_heap_segment**)&hp->ephemeral_heap_segment;
        s_dac_vars->alloc_allocated = &hp->alloc_allocated;
#else
        _ = hp;
#endif
    }

    public static void UnpublishHeapDacVars()
    {
#if USE_REGIONS && !MULTIPLE_HEAPS
        if (s_dac_vars is null)
        {
            return;
        }

        s_dac_vars->generation_table = null;
        s_dac_vars->ephemeral_heap_segment = null;
        s_dac_vars->alloc_allocated = null;
#endif
    }

#if MANAGED_GC_TEST_HOST
    public static void ResetDacPublicationForTest()
    {
        s_dac_vars = null;
    }

    public static void ResetDiagnosticEventStateForTest()
    {
        s_generation_sizes = default;
        s_generation_promoted_sizes = default;
        s_etw_allocation_running_amount = default;
        s_total_time_in_gc = 0;
        s_total_time_since_last_gc_end = 0;
        s_percent_time_in_gc_since_last_gc = 0;
    }
#endif

#if USE_REGIONS
    public static void DiagDescribeGenerations(
        delegate* unmanaged<void*, int, byte*, byte*, byte*, void> fn,
        void* context)
    {
        ManagedGCRegionBootstrap.DescribeGenerations(fn, context);
    }
#endif

    public static void DiagTraceSegments()
    {
#if USE_REGIONS && !MULTIPLE_HEAPS
        gc_heap* hp = ManagedGCRegionBootstrap.Heap;
        if (hp is null)
        {
            return;
        }

        generation* generationTable = generation_table_of(hp);
        generation* maxGeneration =
            generation_of(generationTable, GCInterfaceOffsets.max_generation);
        for (heap_segment* segment =
                 generation.generation_start_segment(maxGeneration);
             segment is not null;
             segment = heap_segment.heap_segment_next(segment))
        {
            uint type = heap_segment.heap_segment_read_only_p(segment) != 0
                ? (uint)gc_etw_segment_type.gc_etw_segment_read_only_heap
                : (uint)gc_etw_segment_type.gc_etw_segment_small_object_heap;
            GCEvents.GCEventFireGCCreateSegment_V1(
                heap_segment.heap_segment_mem(segment),
                unchecked((nuint)(
                    heap_segment.heap_segment_reserved(segment) -
                    heap_segment.heap_segment_mem(segment))),
                type);
        }

        ManagedGCHeap.DiagTraceFrozenSegments();

        for (int generationNumber = (int)gc_generation_num.uoh_start_generation;
             generationNumber < (int)gc_generation_num.total_generation_count;
             generationNumber++)
        {
            generation* gen =
                generation_of(generationTable, generationNumber);
            for (heap_segment* segment =
                     generation.generation_start_segment(gen);
                 segment is not null;
                 segment = heap_segment.heap_segment_next(segment))
            {
                uint type = generationNumber == (int)gc_generation_num.loh_generation
                    ? (uint)gc_etw_segment_type.gc_etw_segment_large_object_heap
                    : (uint)gc_etw_segment_type.gc_etw_segment_pinned_object_heap;
                GCEvents.GCEventFireGCCreateSegment_V1(
                    heap_segment.heap_segment_mem(segment),
                    unchecked((nuint)(
                        heap_segment.heap_segment_reserved(segment) -
                        heap_segment.heap_segment_mem(segment))),
                    type);
            }
        }
#endif
    }

    public static void DiagGetSettings(
        EtwGCSettingsInfo* etwSettings,
        nuint lohThreshold)
    {
        *etwSettings = default;
        etwSettings->heap_hard_limit = heap_hard_limit;
        etwSettings->loh_threshold = lohThreshold;
        etwSettings->physical_memory_from_config = physical_memory_from_config;
        etwSettings->gen0_min_budget_from_config = gen0_min_budget_from_config;
        etwSettings->gen0_max_budget_from_config = gen0_max_budget_from_config;
        etwSettings->high_mem_percent_from_config = high_mem_percent_from_config;
#if BACKGROUND_GC
        etwSettings->concurrent_gc_p = gc_can_use_concurrent ? (byte)1 : (byte)0;
#endif
        etwSettings->use_large_pages_p = use_large_pages_p;
        etwSettings->use_frozen_segments_p = use_frozen_segments_p;
        etwSettings->hard_limit_config_p = hard_limit_config_p ? (byte)1 : (byte)0;
        etwSettings->no_affinitize_p = 1;
    }

    public static void UpdatePreGCCounters(gc_heap* hp)
    {
        s_total_time_in_gc = unchecked(
            (ulong)GCToOSInterface.QueryPerformanceCounter());

        uint type = 0;
        if (settings.concurrent != 0)
        {
            type = 1;
        }
#if BACKGROUND_GC
        else if (settings.condemned_generation < GCInterfaceOffsets.max_generation &&
                 settings.background_p != 0)
        {
            type = 2;
        }
#endif

        GCEvents.GCEventFireGCStart_V2(
            unchecked((uint)settings.gc_index),
            unchecked((uint)settings.condemned_generation),
            unchecked((uint)settings.reason),
            type);
        ReportGenerationBounds(hp);
    }

    public static void UpdateEventStatusForLinux()
    {
#if TARGET_LINUX
        GCToEEInterface.UpdateGCEventStatus(
            (int)GCEventStatus.GetEnabledLevel(GCEventProvider.Default),
            (int)GCEventStatus.GetEnabledKeywords(GCEventProvider.Default),
            (int)GCEventStatus.GetEnabledLevel(GCEventProvider.Private),
            (int)GCEventStatus.GetEnabledKeywords(GCEventProvider.Private));
#endif
    }

    public static void UpdatePostGCCounters(gc_heap* hp)
    {
        int condemnedGeneration = settings.condemned_generation;
        for (int generationNumber = 0;
             generationNumber < (int)gc_generation_num.total_generation_count;
             generationNumber++)
        {
            s_generation_sizes[generationNumber] =
                generation_size(hp, generationNumber);
            s_generation_promoted_sizes[generationNumber] = 0;

            dynamic_data* dd = dynamic_data_of(hp, generationNumber);
            if (generationNumber <= condemnedGeneration ||
                (generationNumber == (int)gc_generation_num.loh_generation &&
                 condemnedGeneration == GCInterfaceOffsets.max_generation))
            {
                s_generation_promoted_sizes[generationNumber] =
                    dynamic_data.dd_promoted_size(dd);
            }
        }

        ReportGenerationBounds(hp);
        GCEvents.GCEventFireGCEnd_V1(
            unchecked((uint)settings.gc_index),
            unchecked((uint)condemnedGeneration));

        uint handleCount = HandleTableManager.HndCountAllHandles(
            fUseLocks: !ManagedGCHeap.CollectionInProgressForDiagnostics);
        uint syncBlockCount = GCToEEInterface.GetActiveSyncBlockCount();
        ulong finalizationPromotedCount =
            finalize_queue is null ? 0 : finalize_queue->GetPromotedCount();

        GCEvents.GCEventFireGCHeapStats_V2(
            s_generation_sizes[0],
            s_generation_promoted_sizes[0],
            s_generation_sizes[1],
            s_generation_promoted_sizes[1],
            s_generation_sizes[2],
            s_generation_promoted_sizes[2],
            s_generation_sizes[3],
            s_generation_promoted_sizes[3],
            s_generation_sizes[4],
            s_generation_promoted_sizes[4],
            dynamic_data.dd_freach_previous_promotion(
                dynamic_data_of(hp, (int)gc_generation_num.soh_gen0)),
            finalizationPromotedCount,
            unchecked((uint)num_pinned_objects),
            syncBlockCount,
            handleCount);

        ulong current = unchecked(
            (ulong)GCToOSInterface.QueryPerformanceCounter());
        s_total_time_in_gc = current - s_total_time_in_gc;
        ulong timeInGcBase = current - s_total_time_since_last_gc_end;
        if (timeInGcBase < s_total_time_in_gc)
        {
            s_total_time_in_gc = 0;
        }

        while (timeInGcBase > uint.MaxValue)
        {
            timeInGcBase >>= 8;
            s_total_time_in_gc >>= 8;
        }

        s_percent_time_in_gc_since_last_gc = timeInGcBase == 0
            ? 0
            : unchecked((uint)(s_total_time_in_gc * 100 / timeInGcBase));
        s_total_time_since_last_gc_end = current;
    }

    public static int GetLastGCPercentTimeInGC() =>
        unchecked((int)s_percent_time_in_gc_since_last_gc);

    public static nuint GetLastGCGenerationSize(int generation) =>
        s_generation_sizes[generation];

    private static void ReportGenerationBounds(gc_heap* hp)
    {
        if (!GCEvents.GCEventEnabledGCGenerationRange() || hp is null)
        {
            return;
        }

        generation* generationTable = generation_table_of(hp);
        for (int generationNumber =
                 (int)gc_generation_num.total_generation_count - 1;
             generationNumber >= 0;
             generationNumber--)
        {
            generation* gen =
                generation_of(generationTable, generationNumber);
            for (heap_segment* segment = heap_segment_rw(
                     generation.generation_start_segment(gen));
                 segment is not null;
                 segment = heap_segment_next_rw(segment))
            {
                byte* start = heap_segment.heap_segment_mem(segment);
                byte* allocated = heap_segment.heap_segment_allocated(segment);
                byte* reserved = heap_segment.heap_segment_reserved(segment);
                GCEvents.GCEventFireGCGenerationRange(
                    unchecked((byte)generationNumber),
                    start,
                    unchecked((ulong)(allocated - start)),
                    unchecked((ulong)(reserved - start)));
            }
        }
    }

    public static void FirePerHeapHistory(gc_history_per_heap* history)
    {
        maxgen_size_increase* maxgenSizeInfo = &history->maxgen_size_info;
        GCEvents.GCEventFireGCPerHeapHistory_V3(
            (void*)maxgenSizeInfo->free_list_allocated,
            (void*)maxgenSizeInfo->free_list_rejected,
            (void*)maxgenSizeInfo->end_seg_allocated,
            (void*)maxgenSizeInfo->condemned_allocated,
            (void*)maxgenSizeInfo->pinned_allocated,
            (void*)maxgenSizeInfo->pinned_allocated_advance,
            maxgenSizeInfo->running_free_list_efficiency,
            history->gen_to_condemn_reasons.get_reasons0(),
            history->gen_to_condemn_reasons.get_reasons1(),
            history->mechanisms[(int)gc_mechanism_per_heap.gc_heap_compact],
            history->mechanisms[(int)gc_mechanism_per_heap.gc_heap_expand],
            history->heap_index,
            (void*)history->extra_gen0_committed,
            (uint)gc_generation_num.total_generation_count,
            (uint)sizeof(gc_generation_data),
            &history->gen_data0);
    }

    public static void SnapshotGen2GrowthHistory(
        generation* olderGeneration,
        nuint savedFreeObjectSpace,
        nuint savedFreeListAllocated,
        nuint savedCondemnedAllocated,
        nuint savedEndSegmentAllocated)
    {
        gc_history_per_heap* history =
            (gc_history_per_heap*)Unsafe.AsPointer(ref gc_data_per_heap);
        maxgen_size_increase* maxgenSizeInfo =
            &history->maxgen_size_info;
        nuint currentFreeObjectSpace =
            generation.generation_free_obj_space(olderGeneration);
        maxgenSizeInfo->free_list_allocated = unchecked(
            generation.generation_free_list_allocated(olderGeneration) -
            savedFreeListAllocated);
        maxgenSizeInfo->free_list_rejected =
            currentFreeObjectSpace >= savedFreeObjectSpace
                ? currentFreeObjectSpace - savedFreeObjectSpace
                : 0;
        maxgenSizeInfo->end_seg_allocated = unchecked(
            generation.generation_end_seg_allocated(olderGeneration) -
            savedEndSegmentAllocated);
        maxgenSizeInfo->condemned_allocated = unchecked(
            generation.generation_condemned_allocated(olderGeneration) -
            savedCondemnedAllocated);
        maxgenSizeInfo->pinned_allocated =
            maxgen_pinned_compact_before_advance;
        nuint pinnedAllocation =
            generation.generation_pinned_allocation_compact_size(
                olderGeneration);
        maxgenSizeInfo->pinned_allocated_advance =
            pinnedAllocation >= maxgen_pinned_compact_before_advance
                ? pinnedAllocation - maxgen_pinned_compact_before_advance
                : 0;
    }

    public static void FirePrivateEvents()
    {
        gc_history_global* global;
        gc_history_per_heap* perHeap;
#if BACKGROUND_GC
        if (settings.concurrent != 0)
        {
            global = (gc_history_global*)Unsafe.AsPointer(ref bgc_data_global);
            perHeap = (gc_history_per_heap*)Unsafe.AsPointer(ref bgc_data_per_heap);
        }
        else
#endif
        {
            global = (gc_history_global*)Unsafe.AsPointer(ref gc_data_global);
            perHeap = (gc_history_per_heap*)Unsafe.AsPointer(ref gc_data_per_heap);
        }

        settings.record(global);
        if (!GCEvents.GCEventEnabledGCGlobalHeapHistory_V4())
        {
            return;
        }

        uint count = settings.concurrent != 0
            ? 5u
            : settings.compaction != 0 ? 8u : 7u;
        uint* timeInfo = stackalloc uint[MaxEtwGcTimeInfo];
        for (int i = 0; i < MaxEtwGcTimeInfo; i++)
        {
            timeInfo[i] = 0;
        }

        GCEvents.GCEventFireGCGlobalHeapHistory_V4(
            global->final_youngest_desired,
            unchecked((int)global->num_heaps),
            unchecked((uint)global->condemned_generation),
            unchecked((uint)global->gen0_reduction_count),
            unchecked((uint)global->reason),
            global->global_mechanisms_p,
            unchecked((uint)global->pause_mode),
            global->mem_pressure,
            global->gen_to_condemn_reasons.get_reasons0(),
            global->gen_to_condemn_reasons.get_reasons1(),
            count,
            sizeof(uint),
            timeInfo);
        FirePerHeapHistory(perHeap);
        if (settings.concurrent == 0 &&
            settings.loh_compaction != 0)
        {
            GCEvents.GCEventFireGCLOHCompact(
                1,
                (uint)sizeof(etw_loh_compact_info),
                Unsafe.AsPointer(ref s_loh_compact_info));
        }
    }

    public static void FireCommittedUsageEvent()
    {
#if USE_REGIONS
        if (!GCEvents.GCEventEnabledCommittedUsage_V1())
        {
            return;
        }

        nuint committedDecommit = 0;
        nuint committedFree = 0;
        for (int kind = 0;
             kind < (int)free_region_kind.count_free_region_kinds;
             kind++)
        {
            committedDecommit = unchecked(
                committedDecommit +
                global_regions_to_decommit[kind].get_size_committed_in_free());
            committedFree = unchecked(
                committedFree +
                free_regions[kind].get_size_committed_in_free());
        }

        nuint committedInUse = unchecked(
            committed_by_oh[(int)gc_oh_num.soh] +
            committed_by_oh[(int)gc_oh_num.loh] +
            committed_by_oh[(int)gc_oh_num.poh]);
        nuint committedGlobalFree = committed_by_oh[recorded_committed_free_bucket];
        committedGlobalFree = committedGlobalFree >=
            unchecked(committedFree + committedDecommit)
                ? unchecked(committedGlobalFree - committedFree - committedDecommit)
                : 0;

        GCEvents.GCEventFireCommittedUsage_V1(
            committedInUse,
            committedDecommit,
            committedFree,
            committedGlobalFree,
            current_total_committed_bookkeeping);
#endif
    }

    public static bool UpdateAllocationInfo(
        int generationNumber,
        nuint allocatedSize,
        nuint* allocationAmount)
    {
        int objectHeap = (int)gen_to_oh(generationNumber);
        ref nuint runningAmount =
            ref s_etw_allocation_running_amount[objectHeap];
        runningAmount = unchecked(runningAmount + allocatedSize);
        if (runningAmount <= EtwAllocationTick)
        {
            return false;
        }

        *allocationAmount = runningAmount;
        runningAmount = 0;
        return true;
    }

    public static void FireAllocationEvent(
        nuint allocationAmount,
        int generationNumber,
        byte* objectAddress,
        nuint objectSize)
    {
        GCEvents.GCEventFireGCAllocationTick_V4(
            allocationAmount,
            unchecked((uint)gen_to_oh(generationNumber)),
            0,
            objectAddress,
            objectSize);
    }

    public static void FireMarkEvent(
        int rootType,
        gc_heap* hp,
        ref nuint lastPromotedBytes)
    {
        if (!GCEvents.GCEventEnabledGCMarkWithType())
        {
            return;
        }

        nuint currentPromotedBytes = get_promoted_bytes(hp);
        nuint promotedBytes = unchecked(
            currentPromotedBytes - lastPromotedBytes);
        GCEvents.GCEventFireGCMarkWithType(
            unchecked((uint)hp->heap_number),
            unchecked((uint)rootType),
            promotedBytes);
        lastPromotedBytes = currentPromotedBytes;
    }

    public static void BeginLohPlan()
    {
        if (!GCEvents.GCEventEnabledGCLOHCompact())
        {
            return;
        }

        s_loh_phase_start = GCCommon.GetHighPrecisionTimeStamp();
    }

    public static void ResetLohCompactInfo()
    {
        if (GCEvents.GCEventEnabledGCLOHCompact())
        {
            s_loh_compact_info = default;
        }
    }

    public static void EndLohPlan()
    {
        if (!GCEvents.GCEventEnabledGCLOHCompact())
        {
            return;
        }

        s_loh_compact_info.time_plan = LimitEventTime(
            GCCommon.GetHighPrecisionTimeStamp() - s_loh_phase_start);
    }

    public static void BeginLohRelocate()
    {
        if (GCEvents.GCEventEnabledGCLOHCompact())
        {
            s_loh_phase_start = GCCommon.GetHighPrecisionTimeStamp();
        }
    }

    public static void RecordLohReference(byte* value)
    {
        if (!GCEvents.GCEventEnabledGCLOHCompact())
        {
            return;
        }

        s_loh_compact_info.total_refs++;
        if (value is null)
        {
            s_loh_compact_info.zero_refs++;
        }
    }

    public static void EndLohRelocate()
    {
        if (!GCEvents.GCEventEnabledGCLOHCompact())
        {
            return;
        }

        s_loh_compact_info.time_relocate = LimitEventTime(
            GCCommon.GetHighPrecisionTimeStamp() - s_loh_phase_start);
    }

    public static void BeginLohCompact()
    {
        if (GCEvents.GCEventEnabledGCLOHCompact())
        {
            s_loh_phase_start = GCCommon.GetHighPrecisionTimeStamp();
        }
    }

    public static void EndLohCompact()
    {
        if (!GCEvents.GCEventEnabledGCLOHCompact())
        {
            return;
        }

        s_loh_compact_info.time_compact = LimitEventTime(
            GCCommon.GetHighPrecisionTimeStamp() - s_loh_phase_start);
    }

    private static uint LimitEventTime(ulong elapsed) =>
        elapsed > uint.MaxValue ? uint.MaxValue : (uint)elapsed;

    public static void RecordOom(
        try_allocate_more_space_context* context)
    {
        oom_reason reason = context->oom_r;
        nuint allocationSize = context->size;
        if (reason == oom_reason.oom_budget)
        {
            allocationSize = dynamic_data.dd_min_size(
                dynamic_data_of(
                    context->hp,
                    (int)gc_generation_num.soh_gen0)) / 2;
        }

        ref fgm_history currentFgmResult = ref
#if MULTIPLE_HEAPS
            context->hp->fgm_result;
#else
            fgm_result;
#endif

        if (reason == oom_reason.oom_budget &&
            currentFgmResult.loh_p == 0 &&
            currentFgmResult.fgm != failure_get_memory.fgm_no_failure)
        {
            reason = oom_reason.oom_low_mem;
        }

        byte* allocated = null;
        byte* reserved = null;
        if (context->gen_number == (int)gc_generation_num.soh_gen0 &&
            context->ephemeral_heap_segment is not null &&
            *context->ephemeral_heap_segment is not null)
        {
            allocated = heap_segment.heap_segment_allocated(
                *context->ephemeral_heap_segment);
            reserved = heap_segment.heap_segment_reserved(
                *context->ephemeral_heap_segment);
        }

        oom_info.reason = reason;
        oom_info.alloc_size = allocationSize;
        oom_info.reserved = reserved;
        oom_info.allocated = allocated;
        oom_info.gc_index = settings.gc_index;
        oom_info.fgm = currentFgmResult.fgm;
        oom_info.size = currentFgmResult.size;
        oom_info.available_pagefile_mb = currentFgmResult.available_pagefile_mb;
        oom_info.loh_p = currentFgmResult.loh_p;
        currentFgmResult.fgm = failure_get_memory.fgm_no_failure;
    }
}
