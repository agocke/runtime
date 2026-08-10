// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Server generation condemnation and cross-heap condemned-generation agreement, ported from the
// SVR-namespace compilation of plan_phase.cpp (generation_to_condemn / joined_generation_to_condemn),
// dynamic_tuning.cpp (dt_high_frag_p, get_memory_info, get_total_gen_*), and gcinternal.h
// (is_induced, is_induced_blocking, estimated_reclaim, generation_allocator helpers). The
// SERVER_GC -> MULTIPLE_HEAPS -> DYNAMIC_HEAP_COUNT -> USE_REGIONS feature chain is selected; the
// BGC_SERVO_TUNING, STRESS_HEAP, STRESS_DYNAMIC_HEAP_COUNT, HEAP_ANALYZE, and !USE_REGIONS branches
// are excluded exactly as they are for the active x64 Linux configuration. No collection entry
// point is routed by this slice: the two condemnation deciders and their supporting tuning helpers
// are translated so the future parallel collection driver can call them.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection;

internal unsafe partial struct gc_heap
{
    // If every heap's gen2 or gen3 size is less than this threshold we will do a blocking GC.
    private const nuint bgc_min_per_heap = 4 * 1024 * 1024;

    // PER_HEAP_ISOLATED_FIELD_INIT_ONLY int generation_skip_ratio_threshold;
    public static int generation_skip_ratio_threshold;

    // PER_HEAP_ISOLATED_FIELD_MAINTAINED bool trigger_initial_gen2_p / trigger_bgc_for_rethreading_p;
    public static bool trigger_initial_gen2_p;
    public static bool trigger_bgc_for_rethreading_p;

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

    private static bool is_induced_blocking(gc_reason reason) =>
        reason is
            gc_reason.reason_induced or
            gc_reason.reason_lowmemory_blocking or
            gc_reason.reason_induced_compacting or
            gc_reason.reason_induced_aggressive or
            gc_reason.reason_lowmemory_host_blocking;

    private static nint get_new_allocation(gc_heap* hp, int gen_number) =>
        dynamic_data.dd_new_allocation(dynamic_data_of(hp, gen_number));

    private static nuint current_generation_size(gc_heap* hp, int gen_number)
    {
        dynamic_data* dd = dynamic_data_of(hp, gen_number);
        return unchecked(dynamic_data.dd_current_size(dd) +
                         (nuint)dynamic_data.dd_desired_allocation(dd) -
                         (nuint)dynamic_data.dd_new_allocation(dd));
    }

    private static nuint generation_size(gc_heap* hp, int gen_number)
    {
        nuint result = 0;
        generation* generation_table = generation_table_of(hp);
        heap_segment* seg = heap_segment_rw(
            generation.generation_start_segment(generation_of(generation_table, gen_number)));

        while (seg is not null)
        {
            result += (nuint)(heap_segment.heap_segment_allocated(seg) - heap_segment.heap_segment_mem(seg));
            seg = heap_segment.heap_segment_next(seg);
        }

        return result;
    }

    private static nuint estimated_reclaim(gc_heap* hp, int gen_number)
    {
        dynamic_data* dd = dynamic_data_of(hp, gen_number);
        nuint gen_allocated = unchecked(
            dynamic_data.dd_desired_allocation(dd) - (nuint)dynamic_data.dd_new_allocation(dd));
        nuint gen_total_size = unchecked(gen_allocated + dynamic_data.dd_current_size(dd));
        nuint est_gen_surv = (nuint)((float)gen_total_size * dynamic_data.dd_surv(dd));
        nuint est_gen_free = unchecked(gen_total_size - est_gen_surv + dynamic_data.dd_fragmentation(dd));

        return est_gen_free;
    }

    private static nuint generation_unusable_fragmentation(generation* inst, int hn)
    {
        _ = hn;
        if (dynamic_adaptation_mode ==
            (int)gc_dynamic_adaptation_mode.dynamic_adaptation_to_application_sizes)
        {
            ulong total_plan_allocated = generation.generation_total_plan_allocated(inst);
            ulong condemned_allocated = generation.generation_condemned_allocated(inst);
            ulong unusable_frag = 0;
            nuint fo_space = (nint)generation.generation_free_obj_space(inst) < 0
                ? 0
                : generation.generation_free_obj_space(inst);

            if (total_plan_allocated != 0)
            {
                unusable_frag = fo_space +
                    (condemned_allocated * generation.generation_free_list_space(inst) / total_plan_allocated);
            }

            return (nuint)unusable_frag;
        }
        else
        {
            ulong free_obj_space = generation.generation_free_obj_space(inst);
            ulong free_list_allocated = generation.generation_free_list_allocated(inst);
            ulong free_list_space = generation.generation_free_list_space(inst);
            if ((free_list_allocated + free_obj_space) == 0)
            {
                return 0;
            }

            return (nuint)(free_obj_space + (free_obj_space * free_list_space) / (free_list_allocated + free_obj_space));
        }
    }

    private static gc_history_per_heap* get_gc_data_per_heap(gc_heap* hp)
    {
#if BACKGROUND_GC
        if (settings.concurrent != 0)
        {
            return &hp->bgc_data_per_heap;
        }
#endif
        return &hp->gc_data_per_heap;
    }

    private static void get_memory_info(
        uint* memory_load,
        ulong* available_physical = null,
        ulong* available_page_file = null)
    {
        GCToOSInterface.GetMemoryStatus(
            is_restricted_physical_mem != 0 ? total_physical_mem : 0,
            memory_load,
            available_physical,
            available_page_file);
    }

    private static nuint END_SPACE_AFTER_GC_FL
    {
        get
        {
            nuint loh_size_threshold = (nuint)GCConfig.GetLOHThreshold();
            return unchecked(loh_size_threshold + Align((nuint)GCInterfaceOffsets.min_obj_size));
        }
    }

    private static nuint get_gen0_end_space(gc_heap* hp, memory_type type)
    {
        nuint end_space = 0;
        generation* generation_table = generation_table_of(hp);
        heap_segment* seg = generation.generation_start_segment(
            generation_of(generation_table, (int)gc_generation_num.soh_gen0));

        while (seg is not null)
        {
            byte* allocated = heap_segment.heap_segment_allocated(seg);
            byte* end = type == memory_type.memory_type_reserved
                ? heap_segment.heap_segment_reserved(seg)
                : heap_segment.heap_segment_committed(seg);

            end_space += (nuint)(end - allocated);
            seg = heap_segment.heap_segment_next(seg);
        }

        return end_space;
    }

    private static nuint end_space_after_gc(gc_heap* hp)
    {
        nuint half_minimum = dynamic_data.dd_min_size(
            dynamic_data_of(hp, (int)gc_generation_num.soh_gen0)) / 2;
        return half_minimum > END_SPACE_AFTER_GC_FL ? half_minimum : END_SPACE_AFTER_GC_FL;
    }

    private static bool check_against_hard_limit(nuint space_required)
    {
        bool can_fit = true;

        if (heap_hard_limit != 0)
        {
            nuint left_in_commit = unchecked(heap_hard_limit - current_total_committed);
            left_in_commit /= (nuint)n_heaps;
            if (left_in_commit < space_required)
            {
                can_fit = false;
            }
        }

        return can_fit;
    }

    private static bool sufficient_space_regions(gc_heap* hp, nuint end_space, nuint end_space_required)
    {
        nuint free_regions_space = unchecked(
            region_free_list.get_num_free_regions(
                (region_free_list*)Unsafe.AsPointer(ref hp->server_free_regions[(int)free_region_kind.basic_free_region])) *
            ((nuint)1 << (int)min_segment_size_shr));
        free_regions_space = unchecked(free_regions_space + global_region_allocator.get_free());
        nuint total_alloc_space = unchecked(end_space + free_regions_space);

        if (total_alloc_space > end_space_required)
        {
            return check_against_hard_limit(end_space_required);
        }

        return false;
    }

    private static bool ephemeral_gen_fit_p(gc_heap* hp, gc_tuning_point tp)
    {
        Debug.Assert(
            tp == gc_tuning_point.tuning_deciding_condemned_gen ||
            tp == gc_tuning_point.tuning_deciding_full_gc);

        dynamic_data* dd = dynamic_data_of(hp, (int)gc_generation_num.soh_gen0);
        nuint twice_minimum = unchecked(2 * dynamic_data.dd_min_size(dd));
        nuint minimum_end_space = end_space_after_gc(hp);
        nuint end_space = twice_minimum > minimum_end_space ? twice_minimum : minimum_end_space;
        nuint gen0_end_space = get_gen0_end_space(hp, memory_type.memory_type_reserved);

        return sufficient_space_regions(hp, gen0_end_space, end_space);
    }

    private static nuint min_reclaim_fragmentation_threshold(gc_heap* hp, uint num_heaps)
    {
        // if the memory load is higher, the threshold we'd want to collect gets lower.
        uint memory_load_delta = unchecked(settings.entry_memory_load - high_memory_load_th);
        nuint min_mem_based_on_available = unchecked(
            (nuint)((500u - (memory_load_delta * 40u)) * 1024u * 1024u)) / num_heaps;

        nuint ten_percent_size = (nuint)((float)generation_size(
            hp,
            (int)gc_generation_num.max_generation) * 0.10);
        ulong three_percent_mem = unchecked(mem_one_percent * 3) / num_heaps;
        ulong minimum = (ulong)min_mem_based_on_available < (ulong)ten_percent_size
            ? (ulong)min_mem_based_on_available
            : (ulong)ten_percent_size;
        minimum = minimum < three_percent_mem ? minimum : three_percent_mem;

        return unchecked((nuint)minimum);
    }

    private static ulong min_high_fragmentation_threshold(ulong available_mem, uint num_heaps)
    {
        const ulong MaximumThreshold = 256UL * 1024 * 1024;
        return (available_mem < MaximumThreshold ? available_mem : MaximumThreshold) / num_heaps;
    }

    private static bool dt_low_ephemeral_space_p(gc_heap* hp, gc_tuning_point tp)
    {
        bool ret = false;

        switch (tp)
        {
            case gc_tuning_point.tuning_deciding_condemned_gen:
            case gc_tuning_point.tuning_deciding_full_gc:
                ret = !ephemeral_gen_fit_p(hp, tp);
                break;

            default:
                Debug.Fail("invalid tuning reason");
                break;
        }

        return ret;
    }

    private static bool dt_estimate_reclaim_space_p(gc_heap* hp, gc_tuning_point tp, int gen_number)
    {
        bool ret = false;

        switch (tp)
        {
            case gc_tuning_point.tuning_deciding_condemned_gen:
                if (gen_number == (int)gc_generation_num.max_generation)
                {
                    nuint est_maxgen_free = estimated_reclaim(hp, gen_number);
                    uint num_heaps = (uint)n_heaps;
                    nuint min_frag_th = min_reclaim_fragmentation_threshold(hp, num_heaps);
                    ret = est_maxgen_free >= min_frag_th;
                }
                else
                {
                    Debug.Fail("only valid for max_generation");
                }

                break;

            default:
                break;
        }

        return ret;
    }

    private static bool dt_estimate_high_frag_p(gc_heap* hp, gc_tuning_point tp, int gen_number, ulong available_mem)
    {
        bool ret = false;

        switch (tp)
        {
            case gc_tuning_point.tuning_deciding_condemned_gen:
                if (gen_number == (int)gc_generation_num.max_generation)
                {
                    dynamic_data* dd = dynamic_data_of(hp, gen_number);
                    float est_frag_ratio;
                    if (dynamic_data.dd_current_size(dd) == 0)
                    {
                        est_frag_ratio = 1;
                    }
                    else if ((dynamic_data.dd_fragmentation(dd) == 0) ||
                             (dynamic_data.dd_fragmentation(dd) + dynamic_data.dd_current_size(dd) == 0))
                    {
                        est_frag_ratio = 0;
                    }
                    else
                    {
                        est_frag_ratio = (float)dynamic_data.dd_fragmentation(dd) /
                            (float)(dynamic_data.dd_fragmentation(dd) + dynamic_data.dd_current_size(dd));
                    }

                    nuint est_frag = unchecked(
                        dynamic_data.dd_fragmentation(dd) +
                        (nuint)((float)(dynamic_data.dd_desired_allocation(dd) - (nuint)dynamic_data.dd_new_allocation(dd)) * est_frag_ratio));

                    uint num_heaps = (uint)n_heaps;
                    ulong min_frag_th = min_high_fragmentation_threshold(available_mem, num_heaps);
                    ret = est_frag >= min_frag_th;
                }
                else
                {
                    Debug.Fail("only valid for max_generation");
                }

                break;

            default:
                break;
        }

        return ret;
    }

    private static bool dt_low_card_table_efficiency_p(gc_heap* hp, gc_tuning_point tp)
    {
        bool ret = false;

        switch (tp)
        {
            case gc_tuning_point.tuning_deciding_condemned_gen:
                ret = hp->generation_skip_ratio < generation_skip_ratio_threshold;
                break;

            default:
                break;
        }

        return ret;
    }

    private static bool dt_high_frag_p(gc_heap* hp, gc_tuning_point tp, int gen_number, bool elevate_p = false)
    {
        bool ret = false;

        switch (tp)
        {
            case gc_tuning_point.tuning_deciding_condemned_gen:
            {
                dynamic_data* dd = dynamic_data_of(hp, gen_number);
                float fragmentation_burden = 0;

                if (elevate_p)
                {
                    ret = dynamic_data.dd_fragmentation(dynamic_data_of(hp, (int)gc_generation_num.max_generation)) >=
                        dynamic_data.dd_max_size(dd);
                }
                else
                {
                    nuint fr = generation_unusable_fragmentation(
                        generation_of(generation_table_of(hp), gen_number),
                        hp->heap_number);
                    ret = fr > dynamic_data.dd_fragmentation_limit(dd);
                    if (ret)
                    {
                        nuint gen_size = generation_size(hp, gen_number);
                        fragmentation_burden = gen_size != 0 ? ((float)fr / (float)gen_size) : 0.0f;
                        ret = fragmentation_burden > dynamic_data.dd_v_fragmentation_burden_limit(dd);
                    }
                }

                break;
            }

            default:
                break;
        }

        return ret;
    }

    private static nuint get_total_gen_fragmentation(int gen_number)
    {
        nuint total_fragmentation = 0;

        for (int hn = 0; hn < n_heaps; hn++)
        {
            gc_heap* hp = g_heaps[hn];
            generation* gen = generation_of(generation_table_of(hp), gen_number);
            total_fragmentation += unchecked(
                generation.generation_free_list_space(gen) + generation.generation_free_obj_space(gen));
        }

        return total_fragmentation;
    }

    private static nuint get_total_gen_estimated_reclaim(int gen_number)
    {
        nuint total_estimated_reclaim = 0;

        for (int hn = 0; hn < n_heaps; hn++)
        {
            gc_heap* hp = g_heaps[hn];
            total_estimated_reclaim += estimated_reclaim(hp, gen_number);
        }

        return total_estimated_reclaim;
    }

    private static nuint get_total_gen_size(int gen_number)
    {
        nuint size = 0;
        for (int hn = 0; hn < n_heaps; hn++)
        {
            gc_heap* hp = g_heaps[hn];
            size += generation_size(hp, gen_number);
        }

        return size;
    }

    // We may need a new empty region while doing a GC so try to get one now, if we don't have any
    // reserve in the free region list.
    private static bool try_get_new_free_region(gc_heap* hp)
    {
        heap_segment* region = null;
        if (region_free_list.get_num_free_regions(
                (region_free_list*)Unsafe.AsPointer(ref hp->server_free_regions[(int)free_region_kind.basic_free_region])) > 0)
        {
            return true;
        }
        else
        {
            region = allocate_new_region(hp, 0, false);
            if (region is not null)
            {
                if (init_table_for_region(0, region))
                {
                    return_free_region(hp, region);
                }
                else
                {
                    region = null;
                }
            }
        }

        return region is not null;
    }

    public static int generation_to_condemn(
        gc_heap* hp,
        int n_initial,
        int* blocking_collection_p,
        int* elevation_requested_p,
        bool check_only_p)
    {
        gc_mechanisms temp_settings = settings;
        gen_to_condemn_tuning temp_condemn_reasons = default;
        gc_mechanisms* local_settings = check_only_p
            ? &temp_settings
            : (gc_mechanisms*)Unsafe.AsPointer(ref settings);
        gen_to_condemn_tuning* local_condemn_reasons = check_only_p
            ? &temp_condemn_reasons
            : &hp->gen_to_condemn_reasons;
        if (!check_only_p)
        {
            if ((local_settings->reason == gc_reason.reason_oos_soh) ||
                (local_settings->reason == gc_reason.reason_oos_loh))
            {
                Debug.Assert(n_initial >= 1);
            }

            Debug.Assert(settings.reason != gc_reason.reason_empty);
        }

        local_condemn_reasons->init();

        int n = n_initial;
        int n_alloc = n;
        int i;
        int temp_gen = 0;
        bool low_memory_detected = g_low_memory_status != 0;
        uint memory_load = 0;
        ulong available_physical = 0;
        ulong available_page_file = 0;
        bool check_memory = false;
        bool high_fragmentation = false;
        bool v_high_memory_load = false;
        bool high_memory_load = false;
        bool low_ephemeral_space = false;
        bool evaluate_elevation = true;
        *elevation_requested_p = 0;
        *blocking_collection_p = 0;

        bool check_max_gen_alloc = true;

        if (!check_only_p)
        {
            generation* youngest_generation = generation_of(generation_table_of(hp), (int)gc_generation_num.soh_gen0);
            dynamic_data.dd_fragmentation(dynamic_data_of(hp, 0)) = unchecked(
                generation.generation_free_list_space(youngest_generation) +
                generation.generation_free_obj_space(youngest_generation));

            for (i = (int)gc_generation_num.uoh_start_generation; i < (int)gc_generation_num.total_generation_count; i++)
            {
                generation* gen = generation_of(generation_table_of(hp), i);
                dynamic_data.dd_fragmentation(dynamic_data_of(hp, i)) = unchecked(
                    generation.generation_free_list_space(gen) +
                    generation.generation_free_obj_space(gen));
            }

            // save new_allocation
            for (i = 0; i < (int)gc_generation_num.total_generation_count; i++)
            {
                dynamic_data* dd = dynamic_data_of(hp, i);
                dynamic_data.dd_gc_new_allocation(dd) = dynamic_data.dd_new_allocation(dd);
            }

            local_condemn_reasons->set_gen(gc_condemn_reason_gen.gen_initial, (uint)n);
            temp_gen = n;

#if BACKGROUND_GC
            if (background_running_p())
            {
                check_max_gen_alloc = false;
            }
#endif

            if (check_max_gen_alloc)
            {
                // figure out if UOH objects need to be collected.
                for (i = (int)gc_generation_num.uoh_start_generation; i < (int)gc_generation_num.total_generation_count; i++)
                {
                    if (get_new_allocation(hp, i) <= 0)
                    {
                        n = (int)gc_generation_num.max_generation;
                        local_condemn_reasons->set_gen(gc_condemn_reason_gen.gen_alloc_budget, (uint)n);
                        break;
                    }
                }
            }

            // figure out which generation ran out of allocation
            for (i = n + 1;
                 i <= (check_max_gen_alloc ? (int)gc_generation_num.max_generation : ((int)gc_generation_num.max_generation - 1));
                 i++)
            {
                if (get_new_allocation(hp, i) <= 0)
                {
                    n = i;
                }
                else
                {
                    break;
                }
            }
        }

        if (n > temp_gen)
        {
            local_condemn_reasons->set_gen(gc_condemn_reason_gen.gen_alloc_budget, (uint)n);
        }

        n_alloc = n;

        // The time based tuning is #if defined(BACKGROUND_GC) && !defined(MULTIPLE_HEAPS), which is
        // excluded in the server configuration.

        if (n < ((int)gc_generation_num.max_generation - 1))
        {
            if (dt_low_card_table_efficiency_p(hp, gc_tuning_point.tuning_deciding_condemned_gen))
            {
                n = Math.Max(n, (int)gc_generation_num.max_generation - 1);
                local_settings->promotion = 1;
                local_condemn_reasons->set_condition(gc_condemn_reason_condition.gen_low_card_p);
            }
        }

        if (!check_only_p)
        {
            hp->generation_skip_ratio = 100;
        }

        if (dt_low_ephemeral_space_p(hp, check_only_p
                ? gc_tuning_point.tuning_deciding_full_gc
                : gc_tuning_point.tuning_deciding_condemned_gen))
        {
            low_ephemeral_space = true;

            n = Math.Max(n, (int)gc_generation_num.max_generation - 1);
            local_condemn_reasons->set_condition(gc_condemn_reason_condition.gen_low_ephemeral_p);

            if (!provisional_mode_triggered)
            {
#if BACKGROUND_GC
                if (!gc_can_use_concurrent ||
                    (generation.generation_free_list_space(
                        generation_of(generation_table_of(hp), (int)gc_generation_num.max_generation)) == 0))
#endif
                {
                    // It is better to defragment first if we are running out of space for
                    // the ephemeral generation but we have enough fragmentation to make up for it
                    // in the non ephemeral generation. Essentially we are trading a gen2 for
                    // having to expand heap in ephemeral collections.
                    if (dt_high_frag_p(
                            hp,
                            gc_tuning_point.tuning_deciding_condemned_gen,
                            (int)gc_generation_num.max_generation - 1,
                            true))
                    {
                        high_fragmentation = true;
                        local_condemn_reasons->set_condition(gc_condemn_reason_condition.gen_max_high_frag_e_p);
                    }
                }
            }
        }

        if (!check_only_p)
        {
            if (!try_get_new_free_region(hp))
            {
                hp->last_gc_before_oom = 1;
            }
        }

        // figure out which ephemeral generation is too fragmented
        temp_gen = n;
        for (i = n + 1; i < (int)gc_generation_num.max_generation; i++)
        {
            if (dt_high_frag_p(hp, gc_tuning_point.tuning_deciding_condemned_gen, i))
            {
                n = i;
            }
            else
            {
                break;
            }
        }

        if (low_ephemeral_space)
        {
            // enable promotion
            local_settings->promotion = 1;
        }

        if (n > temp_gen)
        {
            local_condemn_reasons->set_condition(gc_condemn_reason_condition.gen_eph_high_frag_p);
        }

        if (!check_only_p)
        {
            if (settings.pause_mode == gc_pause_mode.pause_low_latency)
            {
                if (!is_induced(settings.reason))
                {
                    n = Math.Min(n, (int)gc_generation_num.max_generation - 1);
                    evaluate_elevation = false;
                    goto exit;
                }
            }
        }

        // It's hard to catch when we get to the point that the memory load is so high
        // we get an induced GC from the finalizer thread so we are checking the memory load
        // for every gen0 GC.
        check_memory = check_only_p ? (n >= 0) : ((n >= 1) || low_memory_detected);

        if (check_memory)
        {
            // find out if we are short on memory
            uint local_memory_load;
            get_memory_info(&local_memory_load, &available_physical, &available_page_file);
            memory_load = local_memory_load;

            // For regions we want to take the VA range into consideration as well.
            uint va_memory_load = global_region_allocator.get_va_memory_load();
            memory_load = Math.Max(memory_load, va_memory_load);

            // Need to get it early enough for all heaps to use.
            local_settings->entry_available_physical_mem = available_physical;
            local_settings->entry_memory_load = memory_load;

            if (memory_load >= high_memory_load_th || low_memory_detected)
            {
                high_memory_load = true;

                if (memory_load >= v_high_memory_load_th || low_memory_detected)
                {
                    if (!high_fragmentation)
                    {
                        high_fragmentation = dt_estimate_reclaim_space_p(
                            hp,
                            gc_tuning_point.tuning_deciding_condemned_gen,
                            (int)gc_generation_num.max_generation);
                    }

                    v_high_memory_load = true;
                }
                else
                {
                    if (!high_fragmentation)
                    {
                        high_fragmentation = dt_estimate_high_frag_p(
                            hp,
                            gc_tuning_point.tuning_deciding_condemned_gen,
                            (int)gc_generation_num.max_generation,
                            available_physical);
                    }
                }

                if (high_fragmentation)
                {
                    if (high_memory_load)
                    {
                        local_condemn_reasons->set_condition(gc_condemn_reason_condition.gen_max_high_frag_m_p);
                    }
                    else if (v_high_memory_load)
                    {
                        local_condemn_reasons->set_condition(gc_condemn_reason_condition.gen_max_high_frag_vm_p);
                    }
                }
            }
        }

        // The should_expand_in_full_gc block is #ifndef USE_REGIONS and is excluded.

        if (hp->last_gc_before_oom != 0)
        {
            n = (int)gc_generation_num.max_generation;
            *blocking_collection_p = 1;

            if ((local_settings->reason == gc_reason.reason_oos_loh) ||
                (local_settings->reason == gc_reason.reason_alloc_loh))
            {
                evaluate_elevation = false;
            }

            local_condemn_reasons->set_condition(gc_condemn_reason_condition.gen_before_oom);
        }

        if (!check_only_p)
        {
            if (is_induced_blocking(settings.reason) &&
                n_initial == (int)gc_generation_num.max_generation)
            {
                *blocking_collection_p = 1;
                local_condemn_reasons->set_condition(gc_condemn_reason_condition.gen_induced_fullgc_p);
                evaluate_elevation = false;
            }

            if (settings.reason == gc_reason.reason_induced_noforce)
            {
                local_condemn_reasons->set_condition(gc_condemn_reason_condition.gen_induced_noforce_p);
                evaluate_elevation = false;
            }
        }

        if (!provisional_mode_triggered && evaluate_elevation && (low_ephemeral_space || high_memory_load || v_high_memory_load))
        {
            *elevation_requested_p = 1;
#if TARGET_64BIT
            // if we are in high memory load and have consumed 10% of the gen2 budget, do a gen2 now.
            if (high_memory_load || v_high_memory_load)
            {
                dynamic_data* dd_max = dynamic_data_of(hp, (int)gc_generation_num.max_generation);
                if (((float)dynamic_data.dd_new_allocation(dd_max) / (float)dynamic_data.dd_desired_allocation(dd_max)) < 0.9)
                {
                    n = (int)gc_generation_num.max_generation;
                    local_condemn_reasons->set_condition(gc_condemn_reason_condition.gen_almost_max_alloc);
                }
            }

            if (n <= (int)gc_generation_num.max_generation)
#endif
            {
                if (high_fragmentation)
                {
                    // elevate to max_generation
                    n = (int)gc_generation_num.max_generation;

#if BACKGROUND_GC
                    if (high_memory_load || v_high_memory_load)
                    {
                        // For background GC we want to do blocking collections more eagerly because we don't
                        // want to get into the situation where the memory load becomes high while we are in
                        // a background GC and we'd have to wait for the background GC to finish to start
                        // a blocking collection (right now the implementation doesn't handle converting
                        // a background GC to a blocking collection midway.
                        *blocking_collection_p = 1;
                    }
#else
                    if (v_high_memory_load)
                    {
                        *blocking_collection_p = 1;
                    }
#endif
                }
                else
                {
                    n = Math.Max(n, (int)gc_generation_num.max_generation - 1);
                }
            }
        }

        if (!provisional_mode_triggered &&
            (n == ((int)gc_generation_num.max_generation - 1)) &&
            (n_alloc < ((int)gc_generation_num.max_generation - 1)))
        {
            if (get_new_allocation(hp, (int)gc_generation_num.max_generation) <= 0)
            {
                n = (int)gc_generation_num.max_generation;
                local_condemn_reasons->set_condition(gc_condemn_reason_condition.gen_max_gen1);
            }
        }

        // figure out if max_generation is too fragmented -> blocking collection
        if (!provisional_mode_triggered && (n == (int)gc_generation_num.max_generation))
        {
            if (dt_high_frag_p(hp, gc_tuning_point.tuning_deciding_condemned_gen, n))
            {
                local_condemn_reasons->set_condition(gc_condemn_reason_condition.gen_max_high_frag_p);
                if (local_settings->pause_mode != gc_pause_mode.pause_sustained_low_latency)
                {
                    *blocking_collection_p = 1;
                }
            }
        }

#if BACKGROUND_GC
        if ((n == (int)gc_generation_num.max_generation) && (*blocking_collection_p == 0))
        {
            if (hp->heap_number == 0)
            {
                bool bgc_heap_too_small = true;
                for (i = 0; i < n_heaps; i++)
                {
                    if ((current_generation_size(g_heaps[i], (int)gc_generation_num.max_generation) > bgc_min_per_heap) ||
                        (current_generation_size(g_heaps[i], (int)gc_generation_num.loh_generation) > bgc_min_per_heap) ||
                        (current_generation_size(g_heaps[i], (int)gc_generation_num.poh_generation) > bgc_min_per_heap))
                    {
                        bgc_heap_too_small = false;
                        break;
                    }
                }

                if (bgc_heap_too_small)
                {
                    *blocking_collection_p = 1;
                    local_condemn_reasons->set_condition(gc_condemn_reason_condition.gen_gen2_too_small);
                }
            }
        }
#endif

    exit:
        if (!check_only_p)
        {
            if (check_memory)
            {
                hp->fgm_result.available_pagefile_mb = (nuint)(available_page_file / (1024 * 1024));
            }

            local_condemn_reasons->set_gen(gc_condemn_reason_gen.gen_final_per_heap, (uint)n);
            get_gc_data_per_heap(hp)->gen_to_condemn_reasons.init(local_condemn_reasons);

            if ((local_settings->reason == gc_reason.reason_oos_soh) ||
                (local_settings->reason == gc_reason.reason_oos_loh))
            {
                Debug.Assert(n >= 1);
            }
        }

        return n;
    }

    public static int joined_generation_to_condemn(
        bool should_evaluate_elevation,
        int initial_gen,
        int current_gen,
        int* blocking_collection_p)
    {
        gc_data_global.gen_to_condemn_reasons.init();

        int n = current_gen;
        bool joined_last_gc_before_oom = false;
        for (int i = 0; i < n_heaps; i++)
        {
            if (g_heaps[i]->last_gc_before_oom != 0)
            {
                joined_last_gc_before_oom = true;
                break;
            }
        }

        if (joined_last_gc_before_oom && settings.pause_mode != gc_pause_mode.pause_low_latency)
        {
            Debug.Assert(*blocking_collection_p != 0);
        }

        if (should_evaluate_elevation && (n == (int)gc_generation_num.max_generation))
        {
            if (settings.should_lock_elevation != 0)
            {
                settings.elevation_locked_count++;
                if (settings.elevation_locked_count == 6)
                {
                    settings.elevation_locked_count = 0;
                }
                else
                {
                    n = (int)gc_generation_num.max_generation - 1;
                    gc_data_global.gen_to_condemn_reasons.set_condition(gc_condemn_reason_condition.gen_joined_avoid_unproductive);
                    settings.elevation_reduced = 1;
                }
            }
            else
            {
                settings.elevation_locked_count = 0;
            }
        }
        else
        {
            settings.should_lock_elevation = 0;
            settings.elevation_locked_count = 0;
        }

        if (provisional_mode_triggered && (n == (int)gc_generation_num.max_generation))
        {
            // There are a few cases where we should not reduce the generation.
            if ((initial_gen == (int)gc_generation_num.max_generation) || (settings.reason == gc_reason.reason_alloc_loh))
            {
                // If we are doing a full GC in the provisional mode, we always
                // make it blocking because we don't want to get into a situation
                // where foreground GCs are asking for a compacting full GC right away
                // and not getting it.
                if (initial_gen == (int)gc_generation_num.max_generation)
                {
                    gc_data_global.gen_to_condemn_reasons.set_condition(gc_condemn_reason_condition.gen_joined_pm_induced_fullgc_p);
                }
                else
                {
                    gc_data_global.gen_to_condemn_reasons.set_condition(gc_condemn_reason_condition.gen_joined_pm_alloc_loh);
                }

                *blocking_collection_p = 1;
            }
            else if (joined_last_gc_before_oom)
            {
                Debug.Assert(*blocking_collection_p != 0);
            }
            else
            {
                gc_data_global.gen_to_condemn_reasons.set_condition(gc_condemn_reason_condition.gen_joined_gen1_in_pm);
                n = (int)gc_generation_num.max_generation - 1;
            }
        }

        // The should_expand_in_full_gc reset is #ifndef USE_REGIONS and is excluded.

        if (heap_hard_limit != 0)
        {
            // If we have already consumed 90% of the limit, we should check to see if we should compact LOH.
            bool full_compact_gc_p = false;

            if (joined_last_gc_before_oom)
            {
                gc_data_global.gen_to_condemn_reasons.set_condition(gc_condemn_reason_condition.gen_joined_limit_before_oom);
                full_compact_gc_p = true;
            }
            else if (((ulong)current_total_committed * 10UL) >= ((ulong)heap_hard_limit * 9UL))
            {
                nuint loh_frag = get_total_gen_fragmentation((int)gc_generation_num.loh_generation);

                // If the LOH frag is >= 1/8 it's worth compacting it
                if (loh_frag >= heap_hard_limit / 8)
                {
                    gc_data_global.gen_to_condemn_reasons.set_condition(gc_condemn_reason_condition.gen_joined_limit_loh_frag);
                    full_compact_gc_p = true;
                }
                else
                {
                    // If there's not much fragmentation but it looks like it'll be productive to
                    // collect LOH, do that.
                    nuint est_loh_reclaim = get_total_gen_estimated_reclaim((int)gc_generation_num.loh_generation);
                    if (est_loh_reclaim >= heap_hard_limit / 8)
                    {
                        gc_data_global.gen_to_condemn_reasons.set_condition(gc_condemn_reason_condition.gen_joined_limit_loh_reclaim);
                        full_compact_gc_p = true;
                    }
                }
            }

            if (full_compact_gc_p)
            {
                n = (int)gc_generation_num.max_generation;
                *blocking_collection_p = 1;
                settings.loh_compaction = 1;
            }
        }

        if ((conserve_mem_setting != 0) && (n == (int)gc_generation_num.max_generation))
        {
            float frag_limit = 1.0f - conserve_mem_setting / 10.0f;

            nuint loh_size = get_total_gen_size((int)gc_generation_num.loh_generation);
            nuint gen2_size = get_total_gen_size((int)gc_generation_num.max_generation);
            float loh_frag_ratio = 0.0f;
            float combined_frag_ratio = 0.0f;
            if (loh_size != 0)
            {
                nuint loh_frag = get_total_gen_fragmentation((int)gc_generation_num.loh_generation);
                nuint gen2_frag = get_total_gen_fragmentation((int)gc_generation_num.max_generation);
                loh_frag_ratio = (float)loh_frag / (float)loh_size;
                combined_frag_ratio = (float)(gen2_frag + loh_frag) / (float)(gen2_size + loh_size);
            }

            if (combined_frag_ratio > frag_limit)
            {
                gc_data_global.gen_to_condemn_reasons.set_condition(gc_condemn_reason_condition.gen_max_high_frag_p);

                n = (int)gc_generation_num.max_generation;
                *blocking_collection_p = 1;
                if (loh_frag_ratio > frag_limit)
                {
                    settings.loh_compaction = 1;
                }
            }
        }

        if (settings.reason == gc_reason.reason_induced_aggressive)
        {
            gc_data_global.gen_to_condemn_reasons.set_condition(gc_condemn_reason_condition.gen_joined_aggressive);
            settings.loh_compaction = 1;
        }

        // The BGC_SERVO_TUNING servo triggers are excluded.

        if ((n == (int)gc_generation_num.max_generation) && (*blocking_collection_p == 0))
        {
            // If we are doing a gen2 we should reset elevation regardless and let the gen2
            // decide if we should lock again or in the bgc case by design we will not retract
            // gen1 start.
            settings.should_lock_elevation = 0;
            settings.elevation_locked_count = 0;
        }

        // The STRESS_HEAP concurrent-stress elevation block is excluded.

#if BACKGROUND_GC
#if DYNAMIC_HEAP_COUNT
        if (trigger_bgc_for_rethreading_p)
        {
            if (background_running_p())
            {
                // trigger_bgc_for_rethreading_p being true indicates we did not change gen2 FL items when we changed HC.
                // So some heaps could have no FL at all which means if we did a gen1 GC during this BGC we would increase
                // gen2 size. We chose to prioritize not increasing gen2 size so we disallow gen1 GCs.
                if (n != 0)
                {
                    n = 0;
                }
            }
            else
            {
                // If we already decided to do a blocking gen2 which would also achieve the purpose of building up a new
                // gen2 FL, let it happen; otherwise we want to trigger a BGC.
                if (!((n == (int)gc_generation_num.max_generation) && (*blocking_collection_p != 0)))
                {
                    n = (int)gc_generation_num.max_generation;
                }
            }
        }
        else
#endif
        if ((n == (int)gc_generation_num.max_generation) && background_running_p())
        {
            n = (int)gc_generation_num.max_generation - 1;
        }
#endif

#if DYNAMIC_HEAP_COUNT
        if (trigger_initial_gen2_p)
        {
#if BACKGROUND_GC
            Debug.Assert(!trigger_bgc_for_rethreading_p);
            Debug.Assert(!background_running_p());
#endif

            if (n != (int)gc_generation_num.max_generation)
            {
                n = (int)gc_generation_num.max_generation;
                *blocking_collection_p = 0;
            }

            trigger_initial_gen2_p = false;
        }
#endif

        return n;
    }
}

#endif
