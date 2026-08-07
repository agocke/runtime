// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the bounded WKS USE_REGIONS synchronous full-Gen2 lifecycle from collect.cpp and
// interface.cpp.

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    public const int collection_s_ok = 0;
    public const int collection_e_fail = unchecked((int)0x80004005);
    public const int collection_e_notimpl = unchecked((int)0x80004001);

#if USE_REGIONS && !MULTIPLE_HEAPS
    private const GCEventKeyword UnsupportedPublicCollectionKeywords =
        GCEventKeyword.GC |
        GCEventKeyword.GCHeapDump |
        GCEventKeyword.GCHeapSurvivalAndMovement |
        GCEventKeyword.ManagedHeapCollect |
        GCEventKeyword.GCHeapAndTypeNames;

    private const GCEventKeyword UnsupportedPrivateCollectionKeywords =
        GCEventKeyword.GCPrivate;

    public static bool synchronous_full_gen2_collection_supported(
        int generation,
        int mode,
        bool survivor_analysis_requested)
    {
        int unsupported_mode =
            (int)collection_mode.collection_non_blocking |
            (int)collection_mode.collection_optimized |
            (int)collection_mode.collection_aggressive |
            (int)collection_mode.collection_gcstress;
        int supported_mode =
            (int)collection_mode.collection_blocking |
            (int)collection_mode.collection_compacting;

        return
            (generation < 0 || generation >= GCInterfaceOffsets.max_generation) &&
            (mode & unsupported_mode) == 0 &&
            (mode & ~supported_mode) == 0 &&
            GCConfig.GetServerGC() == 0 &&
            GCConfig.GetHeapVerifyLevel() == 0 &&
            !survivor_analysis_requested &&
            (GCEventStatus.GetEnabledKeywords(GCEventProvider.Default) &
                UnsupportedPublicCollectionKeywords) == 0 &&
            (GCEventStatus.GetEnabledKeywords(GCEventProvider.Private) &
                UnsupportedPrivateCollectionKeywords) == 0 &&
            !background_running_p() &&
            current_bgc_state == bgc_state.bgc_not_in_process;
    }

    public static int garbage_collect_synchronous_full_gen2(
        int generation,
        byte low_memory_p,
        int mode)
    {
        bool survivor_analysis_requested =
            GCToEEInterface.AnalyzeSurvivorsRequested(GCInterfaceOffsets.max_generation) != 0;
        if (!synchronous_full_gen2_collection_supported(
            generation,
            mode,
            survivor_analysis_requested))
        {
            return collection_e_notimpl;
        }

        gc_heap* hp = ManagedGCRegionBootstrap.Heap;
        if (hp is null || finalize_queue is null || g_mark_list is null || mark_list_size == 0)
        {
            return collection_e_fail;
        }

        dynamic_data* dd = dynamic_data_of(hp, GCInterfaceOffsets.max_generation);
        nuint collection_count_at_entry = dynamic_data.dd_collection_count(dd);

        enter_gc_lock();
        if (collection_count_at_entry != dynamic_data.dd_collection_count(dd))
        {
            leave_gc_lock();
            return collection_s_ok;
        }

        bool collection_completed = false;
        int result = collection_e_fail;

        GCToEEInterface.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC);

        settings.init_mechanisms();
        if (garbage_collect(hp, low_memory_p, mode))
        {
            collection_completed = true;
            result = collection_s_ok;
        }

        GCToEEInterface.RestartEE(collection_completed ? (byte)1 : (byte)0);

        leave_gc_lock();

        if (collection_completed)
        {
            GCToEEInterface.EnableFinalization(
                settings.found_finalizers != 0 ? (byte)1 : (byte)0);
        }

        return result;
    }

    public static bool garbage_collect(gc_heap* hp, byte low_memory_p, int mode)
    {
        alloc_contexts_used = 0;
        fix_allocation_contexts(hp, for_gc_p: true);
        init_records(hp);

        collection_mode collectionMode = (collection_mode)mode;
        if (low_memory_p != 0)
        {
            settings.reason = gc_reason.reason_lowmemory_blocking;
        }
        else if ((collectionMode & collection_mode.collection_compacting) != 0)
        {
            settings.reason = gc_reason.reason_induced_compacting;
        }
        else
        {
            settings.reason = gc_reason.reason_induced;
        }

        num_pinned_objects = 0;
        rearrange_uoh_segments();

        settings.condemned_generation = GCInterfaceOffsets.max_generation;
        settings.promotion = 1;
        settings.concurrent = 0;
#if BACKGROUND_GC
        settings.background_p = 0;
#endif
        settings.gc_index = dynamic_data.dd_collection_count(
            dynamic_data_of(hp, (int)gc_generation_num.soh_gen0)) + 1;

        GCToEEInterface.GcStartWork(
            settings.condemned_generation,
            GCInterfaceOffsets.max_generation);
        full_gc_counts[gc_type_blocking]++;

        return gc1(hp);
    }

    public static bool gc1(gc_heap* hp)
    {
        Debug.Assert(settings.concurrent == 0);
        Debug.Assert(settings.condemned_generation == GCInterfaceOffsets.max_generation);

        int n = settings.condemned_generation;
        update_collection_counts(hp);

        if (!mark_phase_stack_roots() ||
            !plan_phase_synchronous_full_gen2(hp, n))
        {
            return false;
        }

        generation* generation_table = generation_table_of(hp);
        for (int gen_number = 0;
             gen_number <= GCInterfaceOffsets.max_generation;
             gen_number++)
        {
            generation* gen = generation_of(generation_table, gen_number);
            if (settings.compaction != 0)
            {
                generation.generation_allocation_size(gen) = unchecked(
                    generation.generation_allocation_size(gen) +
                    generation.generation_pinned_allocation_compact_size(gen));
            }
            else
            {
                generation.generation_allocation_size(gen) = unchecked(
                    generation.generation_allocation_size(gen) +
                    generation.generation_pinned_allocation_sweep_size(gen));
            }

            generation.generation_pinned_allocation_sweep_size(gen) = 0;
            generation.generation_pinned_allocation_compact_size(gen) = 0;
        }

        for (int gen_number = 0;
             gen_number <= GCInterfaceOffsets.max_generation;
             gen_number++)
        {
            compute_new_dynamic_data_minimal(hp, gen_number);
        }

        compute_new_dynamic_data_minimal(
            hp,
            (int)gc_generation_num.loh_generation);
        compute_new_dynamic_data_minimal(
            hp,
            (int)gc_generation_num.poh_generation);

        rearrange_uoh_segments();
        compute_gc_and_ephemeral_range(hp, n, end_of_gc_p: true);
        GCWriteBarrier.stomp_write_barrier_ephemeral(
            ephemeral_low,
            ephemeral_high,
            map_region_to_generation_skewed,
            (byte)min_segment_size_shr);

        update_end_ngc_time();
        update_end_gc_time_per_heap(hp);
        record_full_blocking_gc_info_minimal(hp);
        last_gc_before_oom = 0;
        GCToEEInterface.GcDone(n);
        return true;
    }

    public static void record_full_blocking_gc_info_minimal(gc_heap* hp)
    {
        last_full_blocking_gc_info = default;
        last_full_blocking_gc_info.index = settings.gc_index;
        last_full_blocking_gc_info.total_committed = current_total_committed;
        last_full_blocking_gc_info.promoted = get_total_promoted(hp);
        last_full_blocking_gc_info.pinned_objects = num_pinned_objects;
        last_full_blocking_gc_info.finalize_promoted_objects =
            finalize_queue is null ? 0 : finalize_queue->GetPromotedCount();
        last_full_blocking_gc_info.heap_size = get_total_heap_size(hp);
        last_full_blocking_gc_info.condemned_generation =
            unchecked((byte)settings.condemned_generation);
        last_full_blocking_gc_info.compaction =
            settings.compaction != 0 ? (byte)1 : (byte)0;
        last_full_blocking_gc_info.concurrent =
            settings.concurrent != 0 ? (byte)1 : (byte)0;
    }

    private static void compute_new_dynamic_data_minimal(gc_heap* hp, int gen_number)
    {
        dynamic_data* dd = dynamic_data_of(hp, gen_number);
        generation* gen = generation_of(generation_table_of(hp), gen_number);
        nuint total_gen_size = generation_sizes(hp, gen);
        nuint fragmentation = unchecked(
            generation.generation_free_list_space(gen) +
            generation.generation_free_obj_space(gen));

        dynamic_data.dd_fragmentation(dd) = fragmentation;
        dynamic_data.dd_current_size(dd) =
            fragmentation <= total_gen_size
                ? total_gen_size - fragmentation
                : 0;
        dynamic_data.dd_promoted_size(dd) = dynamic_data.dd_survived_size(dd);
        generation.generation_condemned_allocated(gen) = 0;
        generation.generation_free_list_allocated(gen) = 0;
        generation.generation_end_seg_allocated(gen) = 0;

        nuint desired_allocation = dynamic_data.dd_desired_allocation(dd);
        if (desired_allocation < dynamic_data.dd_min_size(dd))
        {
            desired_allocation = dynamic_data.dd_min_size(dd);
            dynamic_data.dd_desired_allocation(dd) = desired_allocation;
        }

        dynamic_data.dd_gc_new_allocation(dd) = unchecked((nint)desired_allocation);
        dynamic_data.dd_new_allocation(dd) = unchecked((nint)desired_allocation);

        gc_history_per_heap* history =
            (gc_history_per_heap*)System.Runtime.CompilerServices.Unsafe.AsPointer(
                ref gc_data_per_heap);
        ref gc_generation_data gen_data =
            ref gc_history_per_heap.gen_data(history, gen_number);
        gen_data.size_after = total_gen_size;
        gen_data.free_list_space_after = generation.generation_free_list_space(gen);
        gen_data.free_obj_space_after = generation.generation_free_obj_space(gen);
        gen_data.pinned_surv = dynamic_data.dd_pinned_survived_size(dd);
        gen_data.npinned_surv = unchecked(
            dynamic_data.dd_survived_size(dd) -
            dynamic_data.dd_pinned_survived_size(dd));
    }
#else
    public static int garbage_collect_synchronous_full_gen2(
        int generation,
        byte low_memory_p,
        int mode)
    {
        _ = generation;
        _ = low_memory_p;
        _ = mode;
        return collection_e_notimpl;
    }
#endif
}
#pragma warning restore CS8981
