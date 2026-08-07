// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the bounded WKS USE_REGIONS synchronous foreground lifecycle from collect.cpp,
// plan_phase.cpp, and interface.cpp.

using System;
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

    public static bool synchronous_foreground_collection_supported(
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
            (generation < 0 || generation <= GCInterfaceOffsets.max_generation) &&
            (mode & unsupported_mode) == 0 &&
            (mode & ~supported_mode) == 0 &&
            GCConfig.GetServerGC() == 0 &&
            GCConfig.GetHeapVerifyLevel() == 0 &&
            !survivor_analysis_requested &&
            (GCEventStatus.GetEnabledKeywords(GCEventProvider.Default) &
                UnsupportedPublicCollectionKeywords) == 0 &&
            (GCEventStatus.GetEnabledKeywords(GCEventProvider.Private) &
                UnsupportedPrivateCollectionKeywords) == 0 &&
            (!background_running_p() ||
             (generation >= 0 &&
              generation < GCInterfaceOffsets.max_generation));
    }

    public static int garbage_collect_synchronous_foreground(
        int generation,
        byte low_memory_p,
        int mode)
    {
        return garbage_collect_synchronous_foreground(
            generation,
            low_memory_p,
            mode,
            gc_reason.reason_empty,
            allocation_triggered_p: false);
    }

    public static int garbage_collect_synchronous_full_gen2(
        int generation,
        byte low_memory_p,
        int mode)
    {
        if (generation >= 0 && generation < GCInterfaceOffsets.max_generation)
        {
            return collection_e_notimpl;
        }

        return garbage_collect_synchronous_foreground(
            generation,
            low_memory_p,
            mode);
    }

    public static int garbage_collect_synchronous_foreground_for_allocation(
        int generation,
        gc_reason reason)
    {
        Debug.Assert(reason is
            gc_reason.reason_alloc_soh or
            gc_reason.reason_alloc_loh or
            gc_reason.reason_oos_soh or
            gc_reason.reason_oos_loh);

        return garbage_collect_synchronous_foreground(
            generation,
            low_memory_p: 0,
            (int)collection_mode.collection_blocking,
            reason,
            allocation_triggered_p: true);
    }

    private static int garbage_collect_synchronous_foreground(
        int generation,
        byte low_memory_p,
        int mode,
        gc_reason reason,
        bool allocation_triggered_p)
    {
        int requested_generation = generation < 0
            ? GCInterfaceOffsets.max_generation
            : generation;
        bool survivor_analysis_requested =
            GCToEEInterface.AnalyzeSurvivorsRequested(requested_generation) != 0;
        if (!synchronous_foreground_collection_supported(
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

        dynamic_data* dd = dynamic_data_of(hp, requested_generation);
        nuint collection_count_at_entry = dynamic_data.dd_collection_count(dd);

        ManagedGCHeap.NotifyCollectionStarted();
        enter_gc_lock();
        if (collection_count_at_entry != dynamic_data.dd_collection_count(dd))
        {
            leave_gc_lock();
            ManagedGCHeap.NotifyCollectionEnded();
            return collection_s_ok;
        }

        bool collection_completed = false;
        bool foreground_during_bgc =
            background_running_p() &&
            requested_generation < GCInterfaceOffsets.max_generation;
#if BACKGROUND_GC
        if (foreground_during_bgc)
        {
            saved_bgc_settings = settings;
        }
#endif
        nuint completed_gc_index = 0;
        int found_finalizers = 0;
        int result = collection_e_fail;

        suspended_start_time = GCCommon.GetHighPrecisionTimeStamp();
        GCToEEInterface.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC);

        settings.init_mechanisms();
        if (garbage_collect(
            hp,
            requested_generation,
            low_memory_p,
            mode,
            reason,
            allocation_triggered_p))
        {
            collection_completed = true;
            completed_gc_index = settings.gc_index;
            found_finalizers = settings.found_finalizers;
            result = collection_s_ok;
        }

        if (foreground_during_bgc)
        {
            settings = saved_bgc_settings;
#if MANAGED_GC_TEST_HOST
            record_foreground_during_bgc_for_test();
#endif
            add_bgc_pause_duration_0();
        }

        GCToEEInterface.RestartEE(collection_completed ? (byte)1 : (byte)0);

        if (collection_completed)
        {
            ManagedGCHeap.RecordCollectionCount(unchecked((int)completed_gc_index));
            GCToEEInterface.EnableFinalization(
                found_finalizers != 0 ? (byte)1 : (byte)0);
        }

        leave_gc_lock();
        ManagedGCHeap.NotifyCollectionEnded();

        return result;
    }

    public static bool garbage_collect(
        gc_heap* hp,
        int requested_generation,
        byte low_memory_p,
        int mode,
        gc_reason reason,
        bool allocation_triggered_p)
    {
        alloc_contexts_used = 0;
        fix_allocation_contexts(hp, for_gc_p: true);
        delay_free_segments();
        init_records(hp);

        if (allocation_triggered_p)
        {
            settings.reason = reason;
        }
        else if (low_memory_p != 0)
        {
            settings.reason = gc_reason.reason_lowmemory_blocking;
        }
        else if (((collection_mode)mode & collection_mode.collection_compacting) != 0)
        {
            settings.reason = gc_reason.reason_induced_compacting;
        }
        else
        {
            settings.reason = gc_reason.reason_induced;
        }

        record_entry_memory_load();
        num_pinned_objects = 0;
        rearrange_uoh_segments();

        settings.condemned_generation =
            generation_to_condemn_minimal(hp, requested_generation);
        settings.promotion =
            settings.condemned_generation > (int)gc_generation_num.soh_gen1 ? 1 : 0;
        settings.concurrent = 0;
#if BACKGROUND_GC
        settings.background_p = background_running_p() ? 1 : 0;
        if (settings.background_p != 0 &&
            settings.condemned_generation == GCInterfaceOffsets.max_generation &&
            requested_generation < GCInterfaceOffsets.max_generation)
        {
            settings.condemned_generation =
                (int)gc_generation_num.soh_gen1;
        }
#endif
        settings.gc_index = dynamic_data.dd_collection_count(
            dynamic_data_of(hp, (int)gc_generation_num.soh_gen0)) + 1;

        GCToEEInterface.GcStartWork(
            settings.condemned_generation,
            GCInterfaceOffsets.max_generation);
        if (settings.condemned_generation == GCInterfaceOffsets.max_generation)
        {
            full_gc_counts[gc_type_blocking]++;
        }

        return gc1(hp);
    }

    public static int generation_to_condemn_minimal(
        gc_heap* hp,
        int initial_generation)
    {
        int condemned_generation = Math.Clamp(
            initial_generation,
            0,
            GCInterfaceOffsets.max_generation);

        for (int gen_number = 0;
             gen_number < (int)gc_generation_num.total_generation_count;
             gen_number++)
        {
            dynamic_data* dd = dynamic_data_of(hp, gen_number);
            dynamic_data.dd_gc_new_allocation(dd) =
                dynamic_data.dd_new_allocation(dd);
        }

        for (int gen_number = (int)gc_generation_num.loh_generation;
             gen_number < (int)gc_generation_num.total_generation_count;
             gen_number++)
        {
            if (dynamic_data.dd_new_allocation(
                    dynamic_data_of(hp, gen_number)) <= 0)
            {
                condemned_generation = GCInterfaceOffsets.max_generation;
                break;
            }
        }

        for (int gen_number = condemned_generation + 1;
             gen_number <= GCInterfaceOffsets.max_generation;
             gen_number++)
        {
            if (dynamic_data.dd_new_allocation(
                    dynamic_data_of(hp, gen_number)) <= 0)
            {
                condemned_generation = gen_number;
            }
            else
            {
                break;
            }
        }

        if (last_gc_before_oom != 0)
        {
            condemned_generation = GCInterfaceOffsets.max_generation;
        }

        if (settings.pause_mode == gc_pause_mode.pause_low_latency &&
            !is_induced(settings.reason))
        {
            condemned_generation = Math.Min(
                condemned_generation,
                GCInterfaceOffsets.max_generation - 1);
        }

        return condemned_generation;
    }

    private static bool is_induced(gc_reason reason) =>
        reason is
            gc_reason.reason_induced or
            gc_reason.reason_induced_noforce or
            gc_reason.reason_lowmemory or
            gc_reason.reason_lowmemory_blocking or
            gc_reason.reason_induced_compacting or
            gc_reason.reason_induced_aggressive or
            gc_reason.reason_lowmemory_host or
            gc_reason.reason_lowmemory_host_blocking;

    public static bool gc1(gc_heap* hp)
    {
        Debug.Assert(settings.concurrent == 0);

        int n = settings.condemned_generation;
        update_collection_counts(hp);

        if (!mark_phase_stack_roots() ||
            !plan_phase_synchronous_foreground(hp, n))
        {
            return false;
        }

        generation* generation_table = generation_table_of(hp);
        for (int gen_number = 0;
             gen_number <= Math.Min(
                 GCInterfaceOffsets.max_generation,
                 n + 1);
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
             gen_number <= n;
             gen_number++)
        {
            compute_new_dynamic_data(hp, gen_number);
        }

        if (n != GCInterfaceOffsets.max_generation)
        {
            for (int gen_number = n + 1;
                 gen_number < (int)gc_generation_num.total_generation_count;
                 gen_number++)
            {
                record_generation_size_after(hp, gen_number);
            }

            for (int gen_number = n + 1;
                 gen_number <= GCInterfaceOffsets.max_generation;
                 gen_number++)
            {
                compute_in(hp, gen_number);
                if (settings.promotion != 0)
                {
                    generation* gen = generation_of(
                        generation_table_of(hp),
                        gen_number);
                    dynamic_data.dd_fragmentation(
                        dynamic_data_of(hp, gen_number)) = unchecked(
                            generation.generation_free_list_space(gen) +
                            generation.generation_free_obj_space(gen));
                }
            }
        }

        rearrange_uoh_segments();
        compute_gc_and_ephemeral_range(hp, n, end_of_gc_p: true);
        GCWriteBarrier.stomp_write_barrier_ephemeral(
            ephemeral_low,
            ephemeral_high,
            map_region_to_generation_skewed,
            (byte)min_segment_size_shr);

        update_end_ngc_time();
        update_end_gc_time_per_heap(hp);
        record_gc_info(hp);
        last_gc_before_oom = 0;
        GCToEEInterface.GcDone(n);
        return true;
    }

    private static void record_generation_size_after(
        gc_heap* hp,
        int gen_number)
    {
        generation* gen = generation_of(generation_table_of(hp), gen_number);
        gc_history_per_heap* history =
            (gc_history_per_heap*)System.Runtime.CompilerServices.Unsafe.AsPointer(
                ref gc_data_per_heap);
        ref gc_generation_data gen_data =
            ref gc_history_per_heap.gen_data(history, gen_number);
        gen_data.size_after = generation_sizes(hp, gen);
        gen_data.free_list_space_after =
            generation.generation_free_list_space(gen);
        gen_data.free_obj_space_after =
            generation.generation_free_obj_space(gen);
    }
#else
    public static int garbage_collect_synchronous_foreground(
        int generation,
        byte low_memory_p,
        int mode)
    {
        _ = generation;
        _ = low_memory_p;
        _ = mode;
        return collection_e_notimpl;
    }

    public static int garbage_collect_synchronous_full_gen2(
        int generation,
        byte low_memory_p,
        int mode)
    {
        if (generation >= 0 && generation < GCInterfaceOffsets.max_generation)
        {
            return collection_e_notimpl;
        }

        return garbage_collect_synchronous_foreground(
            generation,
            low_memory_p,
            mode);
    }
#endif
}
#pragma warning restore CS8981
