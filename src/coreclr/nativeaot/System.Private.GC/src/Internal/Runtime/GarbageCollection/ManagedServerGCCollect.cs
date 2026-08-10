// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Server (SERVER_GC / MULTIPLE_HEAPS / DYNAMIC_HEAP_COUNT / USE_REGIONS, x64 Linux) blocking
// collection orchestration, translated from the SVR compilation of collect.cpp / init.cpp /
// interface.cpp. This is the routing slice that drives the already-translated server mark
// (ManagedServerGCMarkPhase.cs) and plan (ManagedServerGCPlanDriver.cs) phases through the real
// server worker/join/event lifecycle:
//
//   * GarbageCollectGenerationServer (GCHeap::GarbageCollectGeneration, MULTIPLE_HEAPS path) runs
//     on the user thread: it takes the gc_lock, resets every heap's gc_done_event, sets gc_started,
//     wakes heap 0 through ee_suspend_event, and blocks in wait_for_gc_done until the collection
//     finishes.
//   * gc_thread_function (init.cpp) is the per-heap server worker loop. Heap 0 waits for the user
//     thread, suspends the EE, and starts the other workers through gc_start_event; every worker
//     then runs garbage_collect on its own heap. Heap 0 restarts the EE, drops the gc_lock, and
//     signals gc_done.
//   * garbage_collect (collect.cpp) fixes allocation contexts, joins at gc_join_generation_determined
//     for the cross-heap condemned-generation agreement (joined_generation_to_condemn), publishes the
//     settings, and calls gc1.
//   * gc1 (collect.cpp) runs mark_phase -> plan_phase (which itself relocates/compacts or sweeps),
//     recomputes the dynamic data, and joins at gc_join_done for the cross-heap desired-size
//     equalization, ephemeral-range recomputation, and write-barrier publication.
//
// Consistent with the "server event / diagnostics integration deferred" state of the port, the
// diagnostic tail is bounded exactly as the WKS gc1 in Collect.cs is: the ETW pre/post-GC counters,
// private/committed-usage events, full-GC notifications, profiler survivor walks, background-GC
// routing, dynamic heap-count changes, provisional-mode servo tuning, and the region-return
// maintenance (distribute_free_regions / age_free_regions / decommit_ephemeral_segment_pages) are
// not wired here. The exact joins and per-heap ownership required for a correct blocking collection
// are preserved.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

using System;
using System.Diagnostics;
using System.Threading;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    // gcinternal.h CLR_SIZE: the max amount of gen0 zeroed in one chunk, the cap for the
    // per-heap allocation quantum recomputed at the end of gc1.
    private const nuint CLR_SIZE = 8 * 1024 + 32;

    // GCHeap::GcCondemnedGeneration: the condemned generation the user thread hands to the server
    // workers, read back after joined_generation_to_condemn elevates it. PER_HEAP_ISOLATED.
    public static uint GcCondemnedGeneration;

    // PER_HEAP_ISOLATED gc.cpp gc_heap::proceed_with_gc_p and gc_trigger_reason.
    public static bool proceed_with_gc_p;
    public static gc_reason gc_trigger_reason;

    // interface.cpp GCHeap::GarbageCollectGeneration, MULTIPLE_HEAPS path. Runs on the user thread
    // that requested the collection.
    public static nuint GarbageCollectGenerationServer(uint gen, gc_reason reason)
    {
        gc_heap* hpt = g_heaps[0];
        dynamic_data* dd = dynamic_data_of(hpt, (int)gen);
        nuint localCount = dynamic_data.dd_collection_count(dd);

        enter_gc_lock();

        // Don't trigger another GC if one was already in progress while waiting for the lock.
        nuint colCount = dynamic_data.dd_collection_count(dd);
        if (localCount != colCount)
        {
            leave_gc_lock();
            return colCount;
        }

        g_low_memory_status =
            (reason == gc_reason.reason_lowmemory ||
             reason == gc_reason.reason_lowmemory_blocking)
                ? 1
                : 0;
        gc_trigger_reason = reason;

        for (int i = 0; i < n_heaps; i++)
        {
            reset_gc_done(g_heaps[i]);
        }

        gc_started = 1;

        GcCondemnedGeneration = gen;

        bool cooperative_mode = enable_preemptive();
        ee_suspend_event.Set();
        wait_for_gc_done();
        disable_preemptive(cooperative_mode);

        GCToEEInterface.EnableFinalization(
            settings.concurrent == 0 && settings.found_finalizers != 0 ? (byte)1 : (byte)0);

        return dynamic_data.dd_collection_count(dd);
    }

    // Debug-only records to diagnose the server suspension handshake against a live heap.

    // init.cpp gc_heap::gc_thread_function, bounded to the blocking (non-background,
    // non-dynamic-heap-count-change) path. Each server worker runs this loop on its own heap.
    public static void gc_thread_function(gc_heap* hp)
    {
        heap_select.init_cpu_mapping(hp->heap_number);

        // The GC worker runs the collection in managed code. Like the native GC threads (which are
        // native and never trigger a GC), it must never observe a GC poll or be hijacked while the
        // runtime is suspended -- including while it is parked waiting for the next collection, so
        // that it is already protected the instant it wakes into cooperative managed code. It
        // therefore enters the managed-GC critical region (DoNotTriggerGc) once for its whole
        // lifetime, matching the DoNotTriggerGc the WKS foreground path holds while it drives a GC.
        GCHeapCriticalRegion criticalRegion = GCHeapCriticalRegion.Enter();

        while (Volatile.Read(ref server_gc_shutdown) == 0)
        {
            if (hp->heap_number == 0)
            {
                ee_suspend_event.Wait(GCEnv.INFINITE, alertable: false);
                if (Volatile.Read(ref server_gc_shutdown) != 0)
                {
                    break;
                }

                suspended_start_time = GCCommon.GetHighPrecisionTimeStamp();

                GCToEEInterface.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC);

                proceed_with_gc_p = true;
                if (!should_proceed_with_gc())
                {
                    update_collection_counts_for_no_gc();
                    proceed_with_gc_p = false;
                }
                else
                {
                    settings.init_mechanisms();
                    gc_start_event.Set();
                }
            }
            else
            {
                gc_start_event.Wait(GCEnv.INFINITE, alertable: false);
                if (Volatile.Read(ref server_gc_shutdown) != 0)
                {
                    break;
                }
            }

            Debug.Assert(hp->heap_number == 0 || proceed_with_gc_p);

            if (proceed_with_gc_p)
            {
                garbage_collect(hp, unchecked((int)GcCondemnedGeneration));

                if (pm_trigger_full_gc)
                {
                    garbage_collect_pm_full_gc(hp);
                }
            }

            if (hp->heap_number == 0)
            {
                if (proceed_with_gc_p && settings.concurrent == 0)
                {
                    do_post_gc(hp);
                }

                for (int i = 0; i < n_heaps; i++)
                {
                    GCSpinLock.leave(&g_heaps[i]->more_space_lock_soh);
                }

                gc_started = 0;

                GCToEEInterface.RestartEE(1);

                leave_gc_lock();

                internal_gc_done = true;

                if (proceed_with_gc_p)
                {
                    set_gc_done(hp);
                }
                else
                {
                    // We didn't wake the other workers, so release every heap's done event.
                    for (int i = 0; i < n_heaps; i++)
                    {
                        set_gc_done(g_heaps[i]);
                    }
                }
            }
            else
            {
                // Wait until heap 0 has progressed far enough to have restarted the EE before
                // signalling this heap's done event. SafeToRestartManagedThreads (the early-out in
                // native) is a scheduling optimization and is not required for correctness.
                while (!Volatile.Read(ref internal_gc_done))
                {
                    GCToOSInterface.YieldThread(0);
                }

                set_gc_done(hp);
            }
        }

        criticalRegion.Exit();
        Interlocked.Increment(ref server_gc_threads_exited);
    }

    // collect.cpp gc_heap::garbage_collect (int n). Every server worker runs it on its own heap;
    // gc_join_generation_determined keeps the workers in lock-step while the joined worker settles
    // the cross-heap condemned generation and publishes the settings.
    public static void garbage_collect(gc_heap* hp, int n)
    {
        // Reset the number of alloc contexts.
        hp->alloc_contexts_used = 0;

        fix_allocation_contexts(hp, for_gc_p: true);

        clear_gen0_bricks(hp);

        init_records(hp);

        settings.reason = gc_trigger_reason;
        hp->num_pinned_objects = 0;

        // Align all heaps on the max generation to condemn.
        hp->condemned_generation_num = generation_to_condemn(
            hp,
            n,
            &hp->blocking_collection,
            &hp->elevation_requested,
            check_only_p: false);

        gc_t_join.join(hp, (int)gc_join_stage.gc_join_generation_determined);
        if (gc_t_join.joined())
        {
            for (int i = 0; i < n_heaps; i++)
            {
                delay_free_segments(g_heaps[i]);
            }

            int should_evaluate_elevation = 1;
            int should_do_blocking_collection = 0;

            int gen_max = hp->condemned_generation_num;
            for (int i = 0; i < n_heaps; i++)
            {
                if (gen_max < g_heaps[i]->condemned_generation_num)
                {
                    gen_max = g_heaps[i]->condemned_generation_num;
                }
                if (should_evaluate_elevation != 0 &&
                    g_heaps[i]->elevation_requested == 0)
                {
                    should_evaluate_elevation = 0;
                }
                if (should_do_blocking_collection == 0 &&
                    g_heaps[i]->blocking_collection != 0)
                {
                    should_do_blocking_collection = 1;
                }
            }

            settings.condemned_generation = gen_max;
            settings.condemned_generation = joined_generation_to_condemn(
                should_evaluate_elevation != 0,
                n,
                settings.condemned_generation,
                &should_do_blocking_collection);

            record_gcs_during_no_gc();

            if (settings.condemned_generation > 1)
            {
                settings.promotion = 1;
            }

            settings.gc_index =
                dynamic_data.dd_collection_count(
                    dynamic_data_of(hp, (int)gc_generation_num.soh_gen0)) + 1;

            // Call the EE for start of GC work.
            GCToEEInterface.GcStartWork(
                settings.condemned_generation,
                GCInterfaceOffsets.max_generation);

            // Slice B/C: a non-blocking gen2 request routed through garbage_collect_background sets
            // background_gc_requested. On the joined worker, once the condemned generation has
            // settled to max_generation, run the background collection kickoff. It commits every
            // heap's mark array first (native collect.cpp do_concurrent_p gate); if any commit fails
            // it publishes no background state/count and returns false, so the collection falls back
            // to the ordinary blocking path here.
            bool backgroundRequested =
                System.Threading.Volatile.Read(ref background_gc_requested) != 0 &&
                settings.condemned_generation == GCInterfaceOffsets.max_generation;

            if (backgroundRequested && !server_background_gc_kickoff())
            {
                backgroundRequested = false;
            }

            // A background (non-blocking gen2) collection counts as a background collection
            // (full_gc_counts[gc_type_background], incremented in server_background_gc_kickoff), not a
            // blocking one. A normal (non-background) or fell-back max-generation collection counts as
            // blocking.
            if (settings.condemned_generation == GCInterfaceOffsets.max_generation &&
                !backgroundRequested)
            {
                full_gc_counts[gc_type_blocking]++;
            }

            gc_start_event.Reset();
            gc_t_join.restart();
        }

        descr_generations();

        gc1(hp);
    }

    // collect.cpp gc_heap::garbage_collect_pm_full_gc. The provisional-mode follow-up full GC is
    // deferred (pm_trigger_full_gc stays false), but the entry point is translated so the worker
    // loop invokes it exactly as native.
    public static void garbage_collect_pm_full_gc(gc_heap* hp)
    {
        Debug.Assert(settings.condemned_generation == GCInterfaceOffsets.max_generation);
        Debug.Assert(settings.reason == gc_reason.reason_pm_full_gc);
        Debug.Assert(settings.concurrent == 0);
        gc1(hp);
    }

    // collect.cpp gc_heap::gc1, MULTIPLE_HEAPS blocking path.
    public static void gc1(gc_heap* hp)
    {
        Debug.Assert(settings.concurrent == 0);

        verify_soh_segment_list(hp);

        int n = settings.condemned_generation;

        if (settings.reason == gc_reason.reason_pm_full_gc)
        {
            Debug.Assert(n == GCInterfaceOffsets.max_generation);
            init_records(hp);

            gen_to_condemn_tuning* local_condemn_reasons = &get_gc_data_per_heap(hp)->gen_to_condemn_reasons;
            local_condemn_reasons->init();
            local_condemn_reasons->set_gen(gc_condemn_reason_gen.gen_initial, (uint)n);
            local_condemn_reasons->set_gen(gc_condemn_reason_gen.gen_final_per_heap, (uint)n);
        }

        update_collection_counts(hp);

        mark_phase(hp, n);

        check_gen0_bricks(hp);

        GCScan.GcRuntimeStructuresValid(0);
        plan_phase(hp, n);
        GCScan.GcRuntimeStructuresValid(1);

        check_gen0_bricks(hp);

        // Adjust the allocation size from the pinned quantities.
        generation* generation_table = generation_table_of(hp);
        for (int gen_number = 0;
             gen_number <= Math.Min(GCInterfaceOffsets.max_generation, n + 1);
             gen_number++)
        {
            generation* gn = generation_of(generation_table, gen_number);
            if (settings.compaction != 0)
            {
                generation.generation_allocation_size(gn) = unchecked(
                    generation.generation_allocation_size(gn) +
                    generation.generation_pinned_allocation_compact_size(gn));
            }
            else
            {
                generation.generation_allocation_size(gn) = unchecked(
                    generation.generation_allocation_size(gn) +
                    generation.generation_pinned_allocation_sweep_size(gn));
            }

            generation.generation_pinned_allocation_sweep_size(gn) = 0;
            generation.generation_pinned_allocation_compact_size(gn) = 0;
        }

        for (int gen_number = 0; gen_number <= n; gen_number++)
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
        }

        if (n < GCInterfaceOffsets.max_generation)
        {
            for (int older_gen_idx = 1 + n;
                 older_gen_idx <= GCInterfaceOffsets.max_generation;
                 older_gen_idx++)
            {
                compute_in(hp, older_gen_idx);

                dynamic_data* dd = dynamic_data_of(hp, older_gen_idx);
                nuint new_fragmentation = unchecked(
                    generation.generation_free_list_space(
                        generation_of(generation_table, older_gen_idx)) +
                    generation.generation_free_obj_space(
                        generation_of(generation_table, older_gen_idx)));

                if (settings.promotion != 0)
                {
                    dynamic_data.dd_fragmentation(dd) = new_fragmentation;
                }
            }
        }

        // adjust_ephemeral_limits is a no-op under USE_REGIONS; the ephemeral range is recomputed
        // in the gc_join_done region below.

        // Decide on the next allocation quantum.
        if (hp->alloc_contexts_used >= 1)
        {
            hp->allocation_quantum = Align(
                Math.Min(
                    CLR_SIZE,
                    Math.Max(
                        (nuint)1024,
                        unchecked((nuint)get_new_allocation(hp, 0) / (2 * hp->alloc_contexts_used)))),
                get_alignment_constant(false));
        }

        if (hp->end_gen0_region_space == uninitialized_end_gen0_region_space)
        {
            hp->end_gen0_region_space = get_gen0_end_space(hp, memory_type.memory_type_reserved);
        }

        descr_generations();

        verify_soh_segment_list(hp);

        gc_t_join.join(hp, (int)gc_join_stage.gc_join_done);
        if (gc_t_join.joined())
        {
            internal_gc_done = false;

            // Equalize the new desired size of the generations across heaps.
            int limit = settings.condemned_generation;
            if (limit == GCInterfaceOffsets.max_generation)
            {
                limit = (int)gc_generation_num.total_generation_count - 1;
            }

            for (int gen = 0; gen <= limit; gen++)
            {
                nuint total_desired = 0;
                nuint total_already_consumed = 0;

                for (int i = 0; i < n_heaps; i++)
                {
                    dynamic_data* dd = dynamic_data_of(g_heaps[i], gen);
                    nuint temp_total_desired = unchecked(
                        total_desired + dynamic_data.dd_desired_allocation(dd));
                    if (temp_total_desired < total_desired)
                    {
                        total_desired = nuint.MaxValue;
                        break;
                    }
                    total_desired = temp_total_desired;

                    nuint already_consumed = unchecked(
                        dynamic_data.dd_desired_allocation(dd) -
                        (nuint)dynamic_data.dd_new_allocation(dd));
                    total_already_consumed = unchecked(
                        total_already_consumed + already_consumed);
                }

                nuint desired_per_heap = Align(
                    total_desired / (nuint)n_heaps,
                    get_alignment_constant(gen <= GCInterfaceOffsets.max_generation));

                nuint already_consumed_per_heap = total_already_consumed / (nuint)n_heaps;

                if (gen == 0)
                {
                    desired_per_heap = exponential_smoothing(
                        gen,
                        dynamic_data.dd_collection_count(dynamic_data_of(hp, gen)),
                        desired_per_heap);

                    if (heap_hard_limit == 0)
                    {
                        dynamic_data* dd0 = dynamic_data_of(g_heaps[0], gen);
                        nuint min_gc_size = dynamic_data.dd_min_size(dd0);
                        if (min_gc_size <= GCToOSInterface.GetCacheSizePerLogicalCpu(true) &&
                            desired_per_heap <= 2 * min_gc_size)
                        {
                            desired_per_heap = min_gc_size;
                        }
                    }
#if TARGET_64BIT
                    desired_per_heap = joined_youngest_desired(desired_per_heap);
#endif
                    gc_data_global.final_youngest_desired = desired_per_heap;
                }

                if (gen >= (int)gc_generation_num.uoh_start_generation)
                {
                    desired_per_heap = exponential_smoothing(
                        gen,
                        dynamic_data.dd_collection_count(
                            dynamic_data_of(hp, GCInterfaceOffsets.max_generation)),
                        desired_per_heap);
                }

                for (int i = 0; i < n_heaps; i++)
                {
                    dynamic_data* dd = dynamic_data_of(g_heaps[i], gen);
                    dynamic_data.dd_desired_allocation(dd) = desired_per_heap;
                    dynamic_data.dd_gc_new_allocation(dd) = unchecked((nint)desired_per_heap);
                    dynamic_data.dd_new_allocation(dd) = unchecked(
                        (nint)(desired_per_heap - already_consumed_per_heap));

                    if (gen == 0)
                    {
                        fgn_last_alloc = desired_per_heap;
                    }
                }
            }

            int max_gen0_must_clear_bricks = 0;
            for (int i = 0; i < n_heaps; i++)
            {
                gc_heap* hpi = g_heaps[i];
                rearrange_uoh_segments(hpi);
                max_gen0_must_clear_bricks =
                    Math.Max(max_gen0_must_clear_bricks, hpi->gen0_must_clear_bricks);
            }

            verify_region_to_generation_map();
            compute_gc_and_ephemeral_range(
                g_heaps[0],
                settings.condemned_generation,
                end_of_gc_p: true);
            GCWriteBarrier.stomp_write_barrier_ephemeral(
                ephemeral_low,
                ephemeral_high,
                map_region_to_generation_skewed,
                (byte)min_segment_size_shr);

            // If one heap encountered an interior pointer during this GC, the next GC might see one
            // on another heap, so distribute the highest gen0_must_clear_bricks to all heaps.
            if (max_gen0_must_clear_bricks > 0)
            {
                for (int i = 0; i < n_heaps; i++)
                {
                    g_heaps[i]->gen0_must_clear_bricks = max_gen0_must_clear_bricks;
                }
            }

            update_end_ngc_time();
            pm_full_gc_init_or_clear();

            gc_t_join.restart();
        }

        update_end_gc_time_per_heap(hp);
        hp->alloc_context_count = 0;

        if (settings.condemned_generation == GCInterfaceOffsets.max_generation)
        {
            hp->last_gc_before_oom = 0;
        }
    }

    // collect.cpp gc_heap::pm_full_gc_init_or_clear, bounded: provisional mode is not routed by this
    // slice, so pm_trigger_full_gc stays false and both branches are inert. The clear branch is kept
    // so a future provisional-mode follow-up sees consistent state.
    private static void pm_full_gc_init_or_clear()
    {
        if (settings.condemned_generation == GCInterfaceOffsets.max_generation - 1)
        {
            // pm_trigger_full_gc is never set in this slice.
        }
        else if (settings.reason == gc_reason.reason_pm_full_gc)
        {
            Debug.Assert(settings.condemned_generation == GCInterfaceOffsets.max_generation);
            pm_trigger_full_gc = false;
        }
    }

    private static void record_generation_size_after(gc_heap* hp, int gen_number)
    {
        generation* gen = generation_of(generation_table_of(hp), gen_number);
        gc_history_per_heap* history = get_gc_data_per_heap(hp);
        ref gc_generation_data gen_data =
            ref gc_history_per_heap.gen_data(history, gen_number);
        gen_data.size_after = generation_sizes(hp, gen);
        gen_data.free_list_space_after = generation.generation_free_list_space(gen);
        gen_data.free_obj_space_after = generation.generation_free_obj_space(gen);
    }

    // collect.cpp gc_heap::do_post_gc, bounded to the EE notification. Runs on heap 0 after gc1.
    private static void do_post_gc(gc_heap* hp)
    {
        _ = hp;
        GCToEEInterface.GcDone(settings.condemned_generation);
    }

    // no_gc.cpp gc_heap::should_proceed_with_gc / update_collection_counts_for_no_gc /
    // record_gcs_during_no_gc: the no-GC region is not routed for server, so these are bounded.
    // should_proceed_with_gc always proceeds because pause_no_gc is rejected before routing.
    private static bool should_proceed_with_gc() => true;

    private static void update_collection_counts_for_no_gc()
    {
        settings.condemned_generation = GCInterfaceOffsets.max_generation;
        for (int i = 0; i < n_heaps; i++)
        {
            update_collection_counts(g_heaps[i]);
        }
        full_gc_counts[gc_type_blocking]++;
    }

    private static void record_gcs_during_no_gc()
    {
        // The no-GC region is not routed for server; current_no_gc_region_info.started is never set.
    }

    // gc.cpp gc_heap::verify_soh_segment_list / check_gen0_bricks are VERIFY_HEAP / _DEBUG diagnostic
    // walks; they are no-ops in this configuration.
    private static void verify_soh_segment_list(gc_heap* hp)
    {
        _ = hp;
    }

    private static void check_gen0_bricks(gc_heap* hp)
    {
        _ = hp;
    }

    // allocation.cpp gc_heap::retire_allocation_context / fix_allocation_context /
    // fix_youngest_allocation_area / fix_alloc_context / fix_allocation_contexts. GCAllocation.cs
    // (which owns these for WKS) is excluded from the server build, so the allocation-context fixing
    // that turns each thread's unused allocation-context tail into a walkable free object before the
    // heap is scanned is re-translated here for the server heap.
    private static void retire_allocation_context(gc_alloc_context* acontext, ulong* total_alloc_bytes_soh)
    {
        byte* alloc_ptr = acontext->alloc_ptr;
        if (alloc_ptr is null)
        {
            return;
        }

        nuint unused_bytes = unchecked((nuint)(acontext->alloc_limit - alloc_ptr));
        acontext->alloc_bytes = unchecked(acontext->alloc_bytes - (long)unused_bytes);
        *total_alloc_bytes_soh = unchecked(*total_alloc_bytes_soh - unused_bytes);
        acontext->alloc_ptr = null;
        acontext->alloc_limit = acontext->alloc_ptr;
    }

    public static void fix_allocation_context(
        gc_alloc_context* acontext,
        bool for_gc_p,
        bool record_ac_p,
        generation* generation_table,
        heap_segment* ephemeral_heap_segment,
        byte** alloc_allocated,
        ulong* total_alloc_bytes_soh,
        nuint* alloc_contexts_used)
    {
        if (acontext->alloc_ptr is null)
        {
            return;
        }

        int align_const = get_alignment_constant(true);
        nuint aligned_min_obj_size = Align((nuint)GCInterfaceOffsets.min_obj_size, align_const);
        bool is_ephemeral_heap_segment =
            in_range_for_segment(acontext->alloc_limit, ephemeral_heap_segment) != 0;

        if (!is_ephemeral_heap_segment ||
            unchecked((nuint)(*alloc_allocated - acontext->alloc_limit)) > aligned_min_obj_size ||
            !for_gc_p)
        {
            byte* point = acontext->alloc_ptr;
            nuint size = unchecked(
                (nuint)(acontext->alloc_limit - acontext->alloc_ptr) + aligned_min_obj_size);
            make_unused_array(point, size);
            if (for_gc_p)
            {
                generation* gen0 = generation_of(generation_table, (int)gc_generation_num.soh_gen0);
                generation.generation_free_obj_space(gen0) =
                    unchecked(generation.generation_free_obj_space(gen0) + size);
            }
        }
        else if (for_gc_p)
        {
            *alloc_allocated = acontext->alloc_ptr;
            Debug.Assert(
                heap_segment.heap_segment_allocated(ephemeral_heap_segment) <=
                heap_segment.heap_segment_committed(ephemeral_heap_segment));
        }

        if (for_gc_p)
        {
            retire_allocation_context(acontext, total_alloc_bytes_soh);
            if (record_ac_p)
            {
                (*alloc_contexts_used)++;
            }
        }
    }

    private static void fix_youngest_allocation_area(
        generation* youngest_generation,
        heap_segment* ephemeral_heap_segment,
        byte* alloc_allocated)
    {
        Debug.Assert(generation.generation_allocation_pointer(youngest_generation) is null);
        Debug.Assert(generation.generation_allocation_limit(youngest_generation) is null);

        heap_segment.heap_segment_allocated(ephemeral_heap_segment) = alloc_allocated;
        Debug.Assert(
            heap_segment.heap_segment_mem(ephemeral_heap_segment) <=
            heap_segment.heap_segment_allocated(ephemeral_heap_segment));
        Debug.Assert(
            heap_segment.heap_segment_allocated(ephemeral_heap_segment) <=
            heap_segment.heap_segment_reserved(ephemeral_heap_segment));
    }

    private struct fix_alloc_context_args
    {
        public int for_gc_p;
        public gc_heap* heap;
    }

    // interface.cpp GCHeap::FixAllocContext, MULTIPLE_HEAPS body. The context is fixed against the
    // heap that owns its memory (heap_of(alloc_ptr)), not the requesting heap, and only when the
    // requesting heap is null (thread exit) or matches the owning heap. This keeps each context
    // filled exactly once, by its owning heap, so a context whose memory lives on another heap is
    // not turned into a free object using the wrong heap's ephemeral segment (which would leave the
    // owning heap's region with an unwalkable zero-filled tail). init_alloc_count heap-balancing is
    // deferred with the rest of the server dynamic heap-count tuning.
    public static void fix_alloc_context_for_heap(
        gc_alloc_context* acontext,
        bool for_gc_p,
        gc_heap* requesting_heap)
    {
        byte* alloc_ptr = acontext->alloc_ptr;
        if (alloc_ptr is null)
        {
            return;
        }

        gc_heap* hp = heap_of(alloc_ptr);
        if (requesting_heap is null || requesting_heap == hp)
        {
            fix_allocation_context(
                acontext,
                for_gc_p,
                record_ac_p: true,
                generation_table_of(hp),
                hp->ephemeral_heap_segment,
                &hp->alloc_allocated,
                &hp->total_alloc_bytes_soh,
                &hp->alloc_contexts_used);
        }
    }

    private static void fix_alloc_context(gc_alloc_context* acontext, void* param)
    {
        fix_alloc_context_args* args = (fix_alloc_context_args*)param;
        fix_alloc_context_for_heap(acontext, args->for_gc_p != 0, args->heap);
    }

    private static void fix_allocation_contexts(gc_heap* heap, bool for_gc_p)
    {
        fix_alloc_context_args args = default;
        args.for_gc_p = for_gc_p ? 1 : 0;
        args.heap = heap;

        GCToEEInterface.GcEnumAllocContexts(&fix_alloc_context, &args);
        fix_youngest_allocation_area(
            generation_of(generation_table_of(heap), (int)gc_generation_num.soh_gen0),
            heap->ephemeral_heap_segment,
            heap->alloc_allocated);
    }
}
#pragma warning restore CS8981

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
