// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the WKS USE_REGIONS path in no_gc.cpp.

using System;
using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
#if USE_REGIONS && !MULTIPLE_HEAPS
    public static void update_collection_counts_for_no_gc(gc_heap* hp)
    {
        Debug.Assert(settings.pause_mode == gc_pause_mode.pause_no_gc);

        settings.condemned_generation = GCInterfaceOffsets.max_generation;
        update_collection_counts(hp);
        full_gc_counts[gc_type_blocking]++;
    }

    public static bool should_proceed_with_gc(gc_heap* hp)
    {
        if (settings.pause_mode == gc_pause_mode.pause_no_gc)
        {
            if (current_no_gc_region_info.started != 0)
            {
                if (current_no_gc_region_info.soh_withheld_budget != 0)
                {
                    dynamic_data.dd_new_allocation(
                        dynamic_data_of(hp, (int)gc_generation_num.soh_gen0)) +=
                        (nint)current_no_gc_region_info.soh_withheld_budget;
                    dynamic_data.dd_new_allocation(
                        dynamic_data_of(hp, (int)gc_generation_num.loh_generation)) +=
                        (nint)current_no_gc_region_info.loh_withheld_budget;
                    current_no_gc_region_info.soh_withheld_budget = 0;
                    current_no_gc_region_info.loh_withheld_budget = 0;

                    schedule_no_gc_callback(abandoned: false);
                    current_no_gc_region_info.callback = null;
                    return false;
                }

                restore_data_for_no_gc();
                if (current_no_gc_region_info.callback is not null)
                {
                    schedule_no_gc_callback(abandoned: true);
                }

                current_no_gc_region_info = default;
            }
            else
            {
                return should_proceed_for_no_gc(hp);
            }
        }

        return true;
    }

    public static void save_data_for_no_gc()
    {
        current_no_gc_region_info.saved_pause_mode = settings.pause_mode;
    }

    public static void restore_data_for_no_gc()
    {
        settings.pause_mode = current_no_gc_region_info.saved_pause_mode;
    }

    public static start_no_gc_region_status prepare_for_no_gc_region(
        ulong total_size,
        bool loh_size_known,
        ulong loh_size,
        bool disallow_full_blocking)
    {
        if (current_no_gc_region_info.started != 0)
        {
            return start_no_gc_region_status.start_no_gc_in_progress;
        }

        start_no_gc_region_status status =
            start_no_gc_region_status.start_no_gc_success;

        save_data_for_no_gc();
        settings.pause_mode = gc_pause_mode.pause_no_gc;
        current_no_gc_region_info.start_status =
            start_no_gc_region_status.start_no_gc_success;

        ulong allocation_no_gc_loh;
        ulong allocation_no_gc_soh;
        Debug.Assert(total_size != 0);
        if (loh_size_known)
        {
            Debug.Assert(loh_size != 0);
            Debug.Assert(loh_size <= total_size);
            allocation_no_gc_loh = loh_size;
            allocation_no_gc_soh = total_size - loh_size;
        }
        else
        {
            allocation_no_gc_soh = total_size;
            allocation_no_gc_loh = total_size;
        }

        int soh_align_const = get_alignment_constant(small_object_p: true);
        nuint max_soh_allocated = nuint.MaxValue;
        const double ScaleFactor = 1.05;

        ulong total_allowed_soh_allocation = (ulong)max_soh_allocated;
        ulong total_allowed_loh_allocation = (ulong)nuint.MaxValue;
        ulong total_allowed_soh_alloc_scaled = allocation_no_gc_soh > 0
            ? (ulong)(total_allowed_soh_allocation / ScaleFactor)
            : 0;
        ulong total_allowed_loh_alloc_scaled = allocation_no_gc_loh > 0
            ? (ulong)(total_allowed_loh_allocation / ScaleFactor)
            : 0;

        if (allocation_no_gc_soh > total_allowed_soh_alloc_scaled ||
            allocation_no_gc_loh > total_allowed_loh_alloc_scaled)
        {
            status = start_no_gc_region_status.start_no_gc_too_large;
            goto done;
        }

        if (allocation_no_gc_soh > 0)
        {
            allocation_no_gc_soh = (ulong)(allocation_no_gc_soh * ScaleFactor);
            allocation_no_gc_soh =
                Math.Min(allocation_no_gc_soh, total_allowed_soh_alloc_scaled);
        }

        if (allocation_no_gc_loh > 0)
        {
            allocation_no_gc_loh = (ulong)(allocation_no_gc_loh * ScaleFactor);
            allocation_no_gc_loh =
                Math.Min(allocation_no_gc_loh, total_allowed_loh_alloc_scaled);
        }

        if (disallow_full_blocking)
        {
            current_no_gc_region_info.minimal_gc_p = 1;
        }

        if (allocation_no_gc_soh != 0)
        {
            current_no_gc_region_info.soh_allocation_size =
                (nuint)allocation_no_gc_soh;
            soh_allocation_no_gc = Math.Min(
                Align(current_no_gc_region_info.soh_allocation_size, soh_align_const),
                max_soh_allocated);
        }

        if (allocation_no_gc_loh != 0)
        {
            current_no_gc_region_info.loh_allocation_size =
                (nuint)allocation_no_gc_loh;
            loh_allocation_no_gc = Align(
                current_no_gc_region_info.loh_allocation_size,
                get_alignment_constant(small_object_p: false));
        }

    done:
        if (status != start_no_gc_region_status.start_no_gc_success)
        {
            restore_data_for_no_gc();
        }

        return status;
    }

    public static void handle_failure_for_no_gc()
    {
        restore_data_for_no_gc();
        current_no_gc_region_info = default;
    }

    public static start_no_gc_region_status get_start_no_gc_region_status() =>
        current_no_gc_region_info.start_status;

    public static void record_gcs_during_no_gc()
    {
        if (current_no_gc_region_info.started != 0)
        {
            current_no_gc_region_info.num_gcs++;
            if (is_induced(settings.reason))
            {
                current_no_gc_region_info.num_gcs_induced++;
            }
        }
    }

    public static bool find_loh_free_for_no_gc(gc_heap* hp)
    {
        generation* generation_table = generation_table_of(hp);
        allocator* loh_allocator = generation.generation_allocator(
            generation_of(
                generation_table,
                (int)gc_generation_num.loh_generation));
        nuint size = loh_allocation_no_gc;

        for (uint a_l_idx = loh_allocator->first_suitable_bucket(size);
             a_l_idx < loh_allocator->number_of_buckets();
             a_l_idx++)
        {
            byte* free_list = allocator.alloc_list_head_of(loh_allocator, a_l_idx);
            while (free_list is not null)
            {
                if (unused_array_size(free_list) > size)
                {
                    return true;
                }

                free_list = allocator.free_list_slot(free_list);
            }
        }

        return false;
    }

    public static bool find_loh_space_for_no_gc(gc_heap* hp)
    {
        saved_loh_segment_no_gc = null;

        if (find_loh_free_for_no_gc(hp))
        {
            return true;
        }

        generation* generation_table = generation_table_of(hp);
        generation* loh_generation = generation_of(
            generation_table,
            (int)gc_generation_num.loh_generation);
        heap_segment* seg = generation.generation_allocation_segment(loh_generation);

        while (seg is not null)
        {
            nuint remaining = unchecked((nuint)(
                heap_segment.heap_segment_reserved(seg) -
                heap_segment.heap_segment_allocated(seg)));
            if (remaining >= loh_allocation_no_gc)
            {
                saved_loh_segment_no_gc = seg;
                break;
            }

            seg = heap_segment.heap_segment_next(seg);
        }

        if (saved_loh_segment_no_gc is null &&
            current_no_gc_region_info.minimal_gc_p != 0)
        {
            saved_loh_segment_no_gc = get_new_region(
                generation_table,
                hp,
                (int)gc_generation_num.loh_generation,
                get_uoh_seg_size(loh_allocation_no_gc));
        }

        return saved_loh_segment_no_gc is not null;
    }

    public static bool commit_loh_for_no_gc(gc_heap* hp, heap_segment* seg)
    {
        byte* end_committed =
            heap_segment.heap_segment_allocated(seg) + (nint)loh_allocation_no_gc;
        Debug.Assert(end_committed <= heap_segment.heap_segment_reserved(seg));
        return grow_heap_segment(seg, end_committed, hp->heap_number);
    }

    public static void set_loh_allocations_for_no_gc(gc_heap* hp)
    {
        if (current_no_gc_region_info.loh_allocation_size != 0)
        {
            dynamic_data* dd = dynamic_data_of(
                hp,
                (int)gc_generation_num.loh_generation);
            dynamic_data.dd_new_allocation(dd) = (nint)loh_allocation_no_gc;
            dynamic_data.dd_gc_new_allocation(dd) =
                dynamic_data.dd_new_allocation(dd);
        }
    }

    public static void set_soh_allocations_for_no_gc(gc_heap* hp)
    {
        if (current_no_gc_region_info.soh_allocation_size != 0)
        {
            dynamic_data* dd = dynamic_data_of(
                hp,
                (int)gc_generation_num.soh_gen0);
            dynamic_data.dd_new_allocation(dd) = (nint)soh_allocation_no_gc;
            dynamic_data.dd_gc_new_allocation(dd) =
                dynamic_data.dd_new_allocation(dd);
        }
    }

    public static void set_allocations_for_no_gc(gc_heap* hp)
    {
        set_loh_allocations_for_no_gc(hp);
        set_soh_allocations_for_no_gc(hp);
    }

    public static bool should_proceed_for_no_gc(gc_heap* hp)
    {
        bool gc_requested = false;
        bool loh_full_gc_requested = false;
        bool soh_full_gc_requested = false;
        bool no_gc_requested;

        if (current_no_gc_region_info.soh_allocation_size != 0 &&
            !extend_soh_for_no_gc(hp))
        {
            soh_full_gc_requested = true;
        }

        if (current_no_gc_region_info.minimal_gc_p == 0 && gc_requested)
        {
            soh_full_gc_requested = true;
        }

        no_gc_requested = !(soh_full_gc_requested || gc_requested);

        if (soh_full_gc_requested &&
            current_no_gc_region_info.minimal_gc_p != 0)
        {
            current_no_gc_region_info.start_status =
                start_no_gc_region_status.start_no_gc_no_memory;
            goto done;
        }

        if (!soh_full_gc_requested &&
            current_no_gc_region_info.loh_allocation_size != 0)
        {
            if (!find_loh_space_for_no_gc(hp))
            {
                loh_full_gc_requested = true;
            }

            if (!loh_full_gc_requested &&
                saved_loh_segment_no_gc is not null &&
                !commit_loh_for_no_gc(hp, saved_loh_segment_no_gc))
            {
                loh_full_gc_requested = true;
            }
        }

        if ((loh_full_gc_requested || soh_full_gc_requested) &&
            current_no_gc_region_info.minimal_gc_p != 0)
        {
            current_no_gc_region_info.start_status =
                start_no_gc_region_status.start_no_gc_no_memory;
        }

        no_gc_requested =
            !(loh_full_gc_requested || soh_full_gc_requested || gc_requested);

        if (current_no_gc_region_info.start_status ==
                start_no_gc_region_status.start_no_gc_success &&
            no_gc_requested)
        {
            set_allocations_for_no_gc(hp);
        }

    done:
        if (current_no_gc_region_info.start_status ==
                start_no_gc_region_status.start_no_gc_success &&
            !no_gc_requested)
        {
            return true;
        }

        current_no_gc_region_info.started = 1;
        return false;
    }

    public static end_no_gc_region_status end_no_gc_region()
    {
        end_no_gc_region_status status =
            end_no_gc_region_status.end_no_gc_success;

        if (current_no_gc_region_info.started == 0)
        {
            status = end_no_gc_region_status.end_no_gc_not_in_progress;
        }

        if (current_no_gc_region_info.num_gcs_induced != 0)
        {
            status = end_no_gc_region_status.end_no_gc_induced;
        }
        else if (current_no_gc_region_info.num_gcs != 0)
        {
            status = end_no_gc_region_status.end_no_gc_alloc_exceeded;
        }

        if (settings.pause_mode == gc_pause_mode.pause_no_gc)
        {
            restore_data_for_no_gc();
            if (current_no_gc_region_info.callback is not null)
            {
                schedule_no_gc_callback(abandoned: true);
            }
        }

        current_no_gc_region_info = default;
        return status;
    }

    public static void schedule_finalizer_work(FinalizerWorkItem* callback)
    {
        FinalizerWorkItem* previous;
        do
        {
            previous = finalizer_work;
            callback->next = previous;
        }
        while (CompareExchangeFinalizerWork(callback, previous) != previous);

        if (previous is null)
        {
            GCToEEInterface.EnableFinalization(1);
        }
    }

    private static FinalizerWorkItem* CompareExchangeFinalizerWork(
        FinalizerWorkItem* callback,
        FinalizerWorkItem* comparand)
    {
        fixed (FinalizerWorkItem** finalizer_work_address = &finalizer_work)
        {
            return (FinalizerWorkItem*)Interlocked.CompareExchangePointer(
                (void**)finalizer_work_address,
                callback,
                comparand);
        }
    }

    public static FinalizerWorkItem* get_extra_work_for_finalization()
    {
        fixed (FinalizerWorkItem** finalizer_work_address = &finalizer_work)
        {
            return (FinalizerWorkItem*)Interlocked.ExchangePointer(
                (void**)finalizer_work_address,
                null);
        }
    }

    public static void schedule_no_gc_callback(bool abandoned)
    {
        current_no_gc_region_info.callback->abandoned =
            abandoned ? (byte)1 : (byte)0;

        if (current_no_gc_region_info.callback->scheduled == 0)
        {
            current_no_gc_region_info.callback->scheduled = 1;
            schedule_finalizer_work(
                (FinalizerWorkItem*)current_no_gc_region_info.callback);
        }
    }

    public static bool extend_soh_for_no_gc(gc_heap* hp)
    {
        nuint required = soh_allocation_no_gc;
        heap_segment* region = hp->ephemeral_heap_segment;

        while (true)
        {
            byte* allocated = region == hp->ephemeral_heap_segment
                ? hp->alloc_allocated
                : heap_segment.heap_segment_allocated(region);
            nuint available = unchecked((nuint)(
                heap_segment.heap_segment_reserved(region) - allocated));
            nuint commit = Math.Min(available, required);

            if (grow_heap_segment(
                region,
                allocated + (nint)commit,
                hp->heap_number))
            {
                required -= commit;
                if (required == 0)
                {
                    break;
                }

                region = heap_segment.heap_segment_next(region);
                if (region is null)
                {
                    region = get_new_region(
                        generation_table_of(hp),
                        hp,
                        (int)gc_generation_num.soh_gen0);
                    if (region is null)
                    {
                        break;
                    }

                    GCToEEInterface.DiagAddNewRegion(
                        0,
                        heap_segment.heap_segment_mem(region),
                        heap_segment.heap_segment_allocated(region),
                        heap_segment.heap_segment_reserved(region));
                }
            }
            else
            {
                break;
            }
        }

        return required == 0;
    }

    public static void allocate_for_no_gc_after_gc(gc_heap* hp)
    {
        no_gc_oom_p = false;

        if (current_no_gc_region_info.start_status !=
            start_no_gc_region_status.start_no_gc_no_memory)
        {
            if (current_no_gc_region_info.soh_allocation_size != 0)
            {
                no_gc_oom_p = !extend_soh_for_no_gc(hp);
                if (no_gc_oom_p)
                {
                    current_no_gc_region_info.start_status =
                        start_no_gc_region_status.start_no_gc_no_memory;
                    no_gc_oom_p = false;
                }
            }

            if (current_no_gc_region_info.start_status ==
                    start_no_gc_region_status.start_no_gc_success &&
                current_no_gc_region_info.minimal_gc_p == 0 &&
                current_no_gc_region_info.loh_allocation_size != 0)
            {
                saved_loh_segment_no_gc = null;

                if (!find_loh_free_for_no_gc(hp))
                {
                    generation* loh_generation = generation_of(
                        generation_table_of(hp),
                        (int)gc_generation_num.loh_generation);
                    heap_segment* seg =
                        generation.generation_allocation_segment(loh_generation);
                    bool found_seg_p = false;
                    while (seg is not null)
                    {
                        if ((nuint)(
                                heap_segment.heap_segment_reserved(seg) -
                                heap_segment.heap_segment_allocated(seg)) >=
                            loh_allocation_no_gc)
                        {
                            found_seg_p = true;
                            if (!commit_loh_for_no_gc(hp, seg))
                            {
                                no_gc_oom_p = true;
                                break;
                            }
                        }

                        seg = heap_segment.heap_segment_next(seg);
                    }

                    if (!found_seg_p)
                    {
                        saved_loh_segment_no_gc = get_new_region(
                            generation_table_of(hp),
                            hp,
                            (int)gc_generation_num.loh_generation,
                            get_uoh_seg_size(loh_allocation_no_gc));
                        if (saved_loh_segment_no_gc is null)
                        {
                            current_no_gc_region_info.start_status =
                                start_no_gc_region_status.start_no_gc_no_memory;
                        }
                    }
                }

                if (current_no_gc_region_info.start_status ==
                        start_no_gc_region_status.start_no_gc_success &&
                    saved_loh_segment_no_gc is not null &&
                    !commit_loh_for_no_gc(hp, saved_loh_segment_no_gc))
                {
                    no_gc_oom_p = true;
                }
            }
        }

        if (no_gc_oom_p)
        {
            current_no_gc_region_info.start_status =
                start_no_gc_region_status.start_no_gc_no_memory;
            no_gc_oom_p = false;
        }

        if (current_no_gc_region_info.start_status ==
            start_no_gc_region_status.start_no_gc_success)
        {
            set_allocations_for_no_gc(hp);
            current_no_gc_region_info.started = 1;
        }
    }

    public static enable_no_gc_region_callback_status enable_no_gc_callback(
        gc_heap* hp,
        NoGCRegionCallbackFinalizerWorkItem* callback,
        ulong callback_threshold)
    {
        enable_no_gc_region_callback_status status =
            enable_no_gc_region_callback_status.succeed;

        suspended_start_time = GCCommon.GetHighPrecisionTimeStamp();
        GCToEEInterface.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC);
        if (current_no_gc_region_info.started == 0)
        {
            status = enable_no_gc_region_callback_status.not_started;
        }
        else if (current_no_gc_region_info.callback is not null)
        {
            status = enable_no_gc_region_callback_status.already_registered;
        }
        else
        {
            ulong total_original_soh_budget = (ulong)soh_allocation_no_gc;
            ulong total_original_loh_budget = (ulong)loh_allocation_no_gc;
            ulong total_original_budget =
                total_original_soh_budget + total_original_loh_budget;
            if (total_original_budget >= callback_threshold)
            {
                ulong total_withheld =
                    total_original_budget - callback_threshold;
                float soh_ratio =
                    (float)total_original_soh_budget / total_original_budget;
                float loh_ratio =
                    (float)total_original_loh_budget / total_original_budget;

                nuint soh_withheld_budget =
                    (nuint)(soh_ratio * total_withheld);
                nuint loh_withheld_budget =
                    (nuint)(loh_ratio * total_withheld);
                soh_withheld_budget = Math.Max(soh_withheld_budget, (nuint)1);
                soh_withheld_budget = Align(
                    soh_withheld_budget,
                    get_alignment_constant(small_object_p: true));
                loh_withheld_budget = Align(
                    loh_withheld_budget,
                    get_alignment_constant(small_object_p: false));

                dynamic_data* soh_dd = dynamic_data_of(
                    hp,
                    (int)gc_generation_num.soh_gen0);
                dynamic_data* loh_dd = dynamic_data_of(
                    hp,
                    (int)gc_generation_num.loh_generation);
                if (dynamic_data.dd_new_allocation(soh_dd) <=
                        (nint)soh_withheld_budget ||
                    dynamic_data.dd_new_allocation(loh_dd) <=
                        (nint)loh_withheld_budget)
                {
                    status =
                        enable_no_gc_region_callback_status.insufficient_budget;
                }

                if (status == enable_no_gc_region_callback_status.succeed)
                {
                    dynamic_data.dd_new_allocation(soh_dd) -=
                        (nint)soh_withheld_budget;
                    dynamic_data.dd_new_allocation(loh_dd) -=
                        (nint)loh_withheld_budget;
                    current_no_gc_region_info.soh_withheld_budget =
                        soh_withheld_budget;
                    current_no_gc_region_info.loh_withheld_budget =
                        loh_withheld_budget;
                    current_no_gc_region_info.callback = callback;
                }
            }
            else
            {
                status =
                    enable_no_gc_region_callback_status.insufficient_budget;
            }
        }

        GCToEEInterface.RestartEE(0);
        return status;
    }
#endif
}
#pragma warning restore CS8981
