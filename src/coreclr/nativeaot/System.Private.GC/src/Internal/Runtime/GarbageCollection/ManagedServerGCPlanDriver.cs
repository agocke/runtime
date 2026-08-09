// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Server plan-phase driver, translated from the SVR-namespace compilation of gc_heap::plan_phase in
// plan_phase.cpp for the active x64 Linux
// SERVER_GC -> MULTIPLE_HEAPS -> DYNAMIC_HEAP_COUNT -> USE_REGIONS feature chain. Every server GC
// worker runs plan_phase on its own heap; the gc_t_join call keeps the workers in lock-step at the
// compaction-decision boundary. This slice sequences the already-translated plan-phase helpers -
// the pinned-plug-queue / brick-threading leaves (ManagedServerGCPlanBrick.cs), the condemned and
// older-generation plan allocators (ManagedServerGCPlanCondemned.cs / ManagedServerGCPlanOlder.cs),
// the region-planning consumers should_sweep_in_plan / sweep_region_in_plan /
// process_last_np_surv_region / process_remaining_regions (ManagedServerGCPlanRegions.cs), the
// compaction-vs-sweep deciders (ManagedServerGCPlanPhase.cs), plan_loh / decay_loh_pinned_queue
// (ManagedServerGCPlanUOH.cs), and the plan-time UOH sweep sweep_uoh_objects
// (ManagedServerGCPlanSweep.cs) - up to, but not through, the relocate / compact / make_free_lists
// execution.
//
// Ownership follows gcpriv.h: the per-GC region-planning counters (regions_per_gen /
// planned_regions_per_gen / sip_maxgen_regions_per_gen / reserved_free_regions_sip), the plan-space
// accounting (num_regions_freed_in_sweep, gen0_*), the pinned-plug queue, decide_promote_gen1_pins_p,
// gen2_removed_no_undo, saved_pinned_plug_index, gc_policy, and loh_alloc_since_cg are
// PER_HEAP_FIELD_SINGLE_GC[_ALLOC] and instance-owned in the MULTIPLE_HEAPS build, so plan_phase
// resets and consults them through the heap parameter; maxgen_size_inc_p / pm_trigger_full_gc /
// pm_stress_on / provisional_mode_triggered / full_gc_counts are PER_HEAP_ISOLATED and stay static.
//
// The cross-heap gc_join_decide_on_compaction join closes here: after every worker publishes its
// gc_policy the joined worker runs the pol_max reduction (folding joined_special_sweep_p across all
// heaps, forcing sweep when any heap must special-sweep, and counting a full compacting GC). The
// GC_CONFIG_DRIVEN mandatory-compaction / should_do_sweeping_gc branch, the !USE_REGIONS
// rearrange_uoh_segments / demotion-bit reduction / soh_get_segment_to_expand, and the
// FEATURE_EVENT_TRACE timing are excluded exactly as for the active configuration / deferred
// subsystems.
//
// The driver now runs both the full compact-branch execution (relocate_phase -> compact_phase ->
// fix_generation_bounds -> the gc_join_adjust_handle_age_compact join -> UpdatePromotedGenerations /
// GcPromotionsGranted / GcDemote -> the pinned-gap threading -> clear_gen1_cards) and the full
// sweep-branch execution (make_free_lists (ManagedServerGCSweep.cs) -> recover_saved_pinned_info with
// the gen2 free-object deduction -> end_gen0_region_committed_space -> the gc_join_adjust_handle_age_sweep
// join running GcPromotionsGranted / verify_region_to_generation_map when !special_sweep_p ->
// UpdatePromotedGenerations / clear_gen1_cards when !special_sweep_p), but it is still not routed by any
// collection entry point, so nothing runs against a live heap yet. gc_join_rearrange_segs_compaction is
// #ifndef USE_REGIONS and so does not exist in this configuration, and the FEATURE_EVENT_TRACE timing /
// _DEBUG verify_committed_bytes / EE diagnostic survivor walks in the compact and sweep tails are
// omitted with the deferred server event / heap-verify integration.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    // gcpriv.h plan-phase policy result constants.
    private const int policy_sweep = 0;
    private const int policy_compact = 1;
    private const int policy_expand = 2;

    // gcpriv.h PER_HEAP_ISOLATED_FIELD_SINGLE_GC maxgen_size_inc_p and
    // PER_HEAP_ISOLATED_FIELD_MAINTAINED pm_trigger_full_gc / pm_stress_on: process-wide provisional
    // mode state, static in both builds. pm_stress_on is init-ed by the GCProvModeStress config,
    // which is not wired here, so it stays false and its stress path is inert exactly as native when
    // the config is off.
    public static bool maxgen_size_inc_p;
    public static bool pm_trigger_full_gc;
    public static bool pm_stress_on;

    // gc.cpp is_bgc_in_progress (PER_HEAP_ISOLATED_METHOD): a background GC is in progress if the
    // background collector is running or any heap has reached the bgc_initialized state (the state is
    // shared across heaps during the start-of-BGC suspension, so native reads g_heaps[0]).
    private static bool is_bgc_in_progress()
    {
        return background_running_p() ||
            current_bgc_state == bgc_state.bgc_initialized;
    }

    // mark_phase.cpp is_plug_padded: the SHORT_PLUGS padded-plug marker reuses the object's
    // GC_MARKED bit while the plug is being planned (re-translated for the server compilation; it
    // lives in the WKS-only MarkPhase.cs).
    private static int is_plug_padded(byte* node)
    {
        return ((CObjectHeader*)node)->IsMarked();
    }

    // allocation.cpp add_gen_plug / init_free_and_plug / print_free_and_plug and the descr_generations
    // / sweep_ro_segments diagnostics: FREE_USAGE_STATS / SIMPLE_DPRINTF / !USE_REGIONS no-ops for
    // this configuration, translated as empty leaves so plan_phase invokes them exactly as native.
    private static void add_gen_plug(int gen_number, nuint plug_size)
    {
        _ = gen_number;
        _ = plug_size;
    }

    private static void init_free_and_plug()
    {
    }

    private static void print_free_and_plug()
    {
    }

    private static void descr_generations()
    {
    }

    private static void sweep_ro_segments()
    {
    }

    // mark_phase.cpp binary_search: find the first slot in the sorted [left, right) mark-list range
    // whose entry is not less than e.
    private static byte** binary_search(byte** left, byte** right, byte* e)
    {
        if (left == right)
        {
            return left;
        }

        Debug.Assert(left < right);
        byte** a = left;
        nuint l = 0;
        nuint r = (nuint)(right - left);
        while ((r - l) >= 2)
        {
            nuint m = l + ((r - l) / 2);
            Debug.Assert(l < m && m < r);
            if (a[m] < e)
            {
                l = m;
            }
            else
            {
                r = m;
            }
        }

        return a[l] < e ? a + (nint)l + 1 : a + (nint)l;
    }

    // mark_phase.cpp USE_REGIONS get_region_mark_list: binary-search this heap's sorted mark list for
    // the [start, end) region range, returning the region's first entry and writing its end pointer.
    private static byte** get_region_mark_list(
        gc_heap* hp,
        ref int use_mark_list,
        byte* start,
        byte* end,
        byte*** mark_list_end_ptr)
    {
        _ = use_mark_list;
        *mark_list_end_ptr = binary_search(hp->mark_list, hp->mark_list_index, end);
        return binary_search(hp->mark_list, *mark_list_end_ptr, start);
    }

    // gc_heap::plan_phase (SERVER_GC / MULTIPLE_HEAPS / USE_REGIONS). Runs on each server worker's
    // own heap. Translated up to the relocate / compact / make_free_lists boundary; see the file
    // header for the deferred tail.
    public static void plan_phase(gc_heap* hp, int condemned_gen_number)
    {
        Debug.Assert(settings.concurrent == 0);

        generation* generation_table = generation_table_of(hp);
        generation* condemned_gen1 = generation_of(generation_table, condemned_gen_number);

        // In Server GC we check for mark list overflow in sort_mark_list, so no sort happens here.
        // The GC_CONFIG_DRIVEN overflow clamp is excluded (not defined for this port).
        int use_mark_list = 0;
        if (condemned_gen_number < GCInterfaceOffsets.max_generation &&
            hp->mark_list_index <= hp->mark_list_end)
        {
            use_mark_list = 1;
            get_gc_data_per_heap(hp)->set_mechanism_bit(
                gc_mechanism_bit_per_heap.gc_mark_list_bit);
        }

        sweep_ro_segments();

        // The !MULTIPLE_HEAPS per-generation shigh/slow segment-shortening loop is server-side handled
        // elsewhere, so plan_phase does not run it in the MULTIPLE_HEAPS build.

        heap_segment* seg1 = heap_segment_rw(generation.generation_start_segment(condemned_gen1));
        Debug.Assert(seg1 is not null);

        byte* end = heap_segment.heap_segment_allocated(seg1);
        byte* first_condemned_address = get_soh_start_object(seg1, condemned_gen1);
        byte* x = first_condemned_address;

        hp->regions_per_gen = default;
        hp->planned_regions_per_gen = default;
        hp->sip_maxgen_regions_per_gen = default;
        hp->reserved_free_regions_sip = default;
        int pinned_survived_region = 0;
        byte** local_mark_list_index = null;
        byte** mark_list_next = null;
        if (use_mark_list != 0)
        {
            mark_list_next = get_region_mark_list(
                hp,
                ref use_mark_list,
                x,
                end,
                &local_mark_list_index);
        }

        byte* plug_end = x;
        byte* tree = null;
        nuint sequence_number = 0;
        byte* last_node = null;
        nuint current_brick = brick_of(x);
        int allocate_in_condemned =
            condemned_gen_number == GCInterfaceOffsets.max_generation ||
            settings.promotion == 0
                ? 1
                : 0;
        int active_old_gen_number = condemned_gen_number;
        int active_new_gen_number = allocate_in_condemned != 0
            ? condemned_gen_number
            : condemned_gen_number + 1;

        generation* consing_gen = condemned_gen1;
        generation* older_gen = null;
        alloc_list* saved_free_list =
            stackalloc alloc_list[GCInterfaceOffsets.MAX_BUCKET_COUNT];
        nuint saved_free_list_space = 0;
        nuint saved_free_obj_space = 0;
        nuint saved_free_list_allocated = 0;
        nuint saved_condemned_allocated = 0;
        nuint saved_end_seg_allocated = 0;
        byte* saved_allocation_pointer = null;
        byte* saved_allocation_limit = null;
        byte* saved_allocation_start_region = null;
        heap_segment* saved_allocation_segment = null;

        if (condemned_gen_number < GCInterfaceOffsets.max_generation)
        {
            older_gen = generation_of(generation_table, condemned_gen_number + 1);
            allocator.copy_to_alloc_list(
                generation.generation_allocator(older_gen),
                saved_free_list);
            saved_free_list_space = generation.generation_free_list_space(older_gen);
            saved_free_obj_space = generation.generation_free_obj_space(older_gen);
            generation.generation_allocate_end_seg_p(older_gen) = 0;
#if TARGET_64BIT && !TARGET_WASM
            if (older_gen->gen_num == GCInterfaceOffsets.max_generation)
            {
                generation.generation_set_bgc_mark_bit_p(older_gen) = 0;
                generation.generation_last_free_list_allocated(older_gen) = null;
            }
#endif
            saved_free_list_allocated =
                generation.generation_free_list_allocated(older_gen);
            saved_condemned_allocated =
                generation.generation_condemned_allocated(older_gen);
            saved_end_seg_allocated =
                generation.generation_end_seg_allocated(older_gen);
            saved_allocation_pointer =
                generation.generation_allocation_pointer(older_gen);
            saved_allocation_limit =
                generation.generation_allocation_limit(older_gen);
            saved_allocation_start_region =
                generation.generation_allocation_context_start_region(older_gen);
            saved_allocation_segment =
                generation.generation_allocation_segment(older_gen);

            for (heap_segment* region =
                    generation.generation_start_segment_rw(older_gen);
                 region is not null;
                 region = heap_segment.heap_segment_next(region))
            {
                heap_segment.heap_segment_plan_allocated(region) =
                    heap_segment.heap_segment_allocated(region);
            }
        }

        // Reset every condemned region's plan_allocated to its start, counting regions per gen.
        for (int gen_index = 0; gen_index <= condemned_gen_number; gen_index++)
        {
            generation* current_gen = generation_of(generation_table, gen_index);
            heap_segment* seg2 =
                heap_segment_rw(generation.generation_start_segment(current_gen));
            Debug.Assert(seg2 is not null);
            while (seg2 is not null)
            {
                hp->regions_per_gen[gen_index]++;
                heap_segment.heap_segment_plan_allocated(seg2) =
                    heap_segment.heap_segment_mem(seg2);
                seg2 = heap_segment_next_rw(seg2);
            }
        }

        init_free_and_plug();

        for (int condemned_gn = condemned_gen_number;
             condemned_gn >= 0;
             condemned_gn--)
        {
            generation* condemned_gen2 = generation_of(generation_table, condemned_gn);
            allocator.clear(generation.generation_allocator(condemned_gen2));
            generation.generation_free_list_space(condemned_gen2) = 0;
            generation.generation_free_obj_space(condemned_gen2) = 0;
            generation.generation_allocation_size(condemned_gen2) = 0;
            generation.generation_condemned_allocated(condemned_gen2) = 0;
            generation.generation_sweep_allocated(condemned_gen2) = 0;
            generation.generation_free_list_allocated(condemned_gen2) = 0;
            generation.generation_end_seg_allocated(condemned_gen2) = 0;
            generation.generation_pinned_allocation_sweep_size(condemned_gen2) = 0;
            generation.generation_pinned_allocation_compact_size(condemned_gen2) = 0;

            generation.generation_allocation_segment(condemned_gen2) =
                heap_segment_rw(generation.generation_start_segment(condemned_gen2));
            Debug.Assert(
                generation.generation_allocation_segment(condemned_gen2) is not null);
            generation.generation_allocation_pointer(condemned_gen2) =
                heap_segment.heap_segment_mem(
                    generation.generation_allocation_segment(condemned_gen2));
            generation.generation_allocation_limit(condemned_gen2) =
                generation.generation_allocation_pointer(condemned_gen2);
            generation.generation_allocation_context_start_region(condemned_gen2) =
                generation.generation_allocation_pointer(condemned_gen2);
        }

        // Normally pins left after plan allocation are demoted, but a gen1 GC done only for cards must
        // decide whether to promote these pins from gen1. That condemnation reason
        // (gen_low_card_p-only) is not produced by the translated condemnation prefix, so this is
        // false for now, matching native when the condition does not hold.
        hp->decide_promote_gen1_pins_p =
            settings.promotion != 0 &&
            settings.condemned_generation == GCInterfaceOffsets.max_generation - 1 &&
            hp->gen_to_condemn_reasons.is_only_condition(
                gc_condemn_reason_condition.gen_low_card_p);

        if (should_sweep_in_plan(hp, seg1))
        {
            sweep_region_in_plan(hp, seg1, use_mark_list, ref mark_list_next, local_mark_list_index);
            x = end;
        }

#if TARGET_64BIT && !TARGET_WASM
        hp->gen2_removed_no_undo = 0;
        hp->saved_pinned_plug_index = nuint.MaxValue;
#endif

        nuint last_plug_len = 0;
        while (true)
        {
            if (x >= end)
            {
                if (use_mark_list == 0)
                {
                    Debug.Assert(x == end);
                }

                if (heap_segment.heap_segment_swept_in_plan(seg1) != 0)
                {
                    Debug.Assert(
                        heap_segment.heap_segment_gen_num(seg1) == active_old_gen_number);
                    dynamic_data.dd_survived_size(
                        dynamic_data_of(hp, active_old_gen_number)) +=
                        heap_segment.heap_segment_survived(seg1);
                }
                else
                {
                    Debug.Assert(heap_segment.heap_segment_allocated(seg1) == end);
                    save_allocated(seg1);
                    heap_segment.heap_segment_allocated(seg1) = plug_end;
                    current_brick = update_brick_table(tree, current_brick, x, plug_end);
                    sequence_number = 0;
                    tree = null;
                }

                heap_segment.heap_segment_pinned_survived(seg1) = pinned_survived_region;
                pinned_survived_region = 0;
                if (heap_segment.heap_segment_mem(seg1) ==
                    heap_segment.heap_segment_allocated(seg1))
                {
                    hp->num_regions_freed_in_sweep++;
                }

                if (heap_segment_next_rw(seg1) is not null)
                {
                    seg1 = heap_segment_next_rw(seg1);
                    end = heap_segment.heap_segment_allocated(seg1);
                    plug_end = x = heap_segment.heap_segment_mem(seg1);
                    current_brick = brick_of(x);
                    if (use_mark_list != 0)
                    {
                        mark_list_next = get_region_mark_list(
                            hp,
                            ref use_mark_list,
                            x,
                            end,
                            &local_mark_list_index);
                    }
                    if (should_sweep_in_plan(hp, seg1))
                    {
                        sweep_region_in_plan(
                            hp, seg1, use_mark_list, ref mark_list_next, local_mark_list_index);
                        x = end;
                    }

                    continue;
                }

                // Ran out of regions for active_old_gen_number: finish planning it, set the consing
                // gen's alloc ptr/limit and the planned gen for the remaining regions, then step down
                // to the next older generation (or drain the leftover pins and stop).
                int saved_active_new_gen_number = active_new_gen_number;
                if (active_old_gen_number <=
                    (settings.promotion != 0
                        ? GCInterfaceOffsets.max_generation - 1
                        : GCInterfaceOffsets.max_generation))
                {
                    active_new_gen_number--;
                    allocate_in_condemned = 1;
                }

                if (active_new_gen_number >= 0)
                {
                    process_last_np_surv_region(
                        hp, consing_gen, saved_active_new_gen_number, active_new_gen_number);
                }

                if (active_old_gen_number == 0)
                {
                    process_remaining_regions(hp, active_new_gen_number, consing_gen);
                    break;
                }

                active_old_gen_number--;
                seg1 = heap_segment_rw(
                    generation.generation_start_segment(
                        generation_of(generation_table, active_old_gen_number)));
                end = heap_segment.heap_segment_allocated(seg1);
                plug_end = x = heap_segment.heap_segment_mem(seg1);
                current_brick = brick_of(x);
                if (use_mark_list != 0)
                {
                    mark_list_next = get_region_mark_list(
                        hp,
                        ref use_mark_list,
                        x,
                        end,
                        &local_mark_list_index);
                }
                if (should_sweep_in_plan(hp, seg1))
                {
                    sweep_region_in_plan(
                        hp, seg1, use_mark_list, ref mark_list_next, local_mark_list_index);
                    x = end;
                }

                continue;
            }

            int last_npinned_plug_p = 0;
            int last_pinned_plug_p = 0;
            byte* last_pinned_plug = null;
            byte* last_object_in_plug = null;

            while (x < end && ((CObjectHeader*)x)->IsMarked() != 0)
            {
                byte* plug_start = x;
                byte* saved_plug_end = plug_end;
                int pinned_plug_p = 0;
                int npin_before_pin_p = 0;
                int saved_last_npinned_plug_p = last_npinned_plug_p;
                int merge_with_last_pin_p = 0;
                nuint added_pinning_size = 0;
                nuint artificial_pinned_size = 0;

                store_plug_gap_info(
                    hp,
                    plug_start,
                    plug_end,
                    ref last_npinned_plug_p,
                    ref last_pinned_plug_p,
                    ref last_pinned_plug,
                    ref pinned_plug_p,
                    last_object_in_plug,
                    ref merge_with_last_pin_p,
                    last_plug_len);

                byte* xl = x;
                while (xl < end &&
                       ((CObjectHeader*)xl)->IsMarked() != 0 &&
                       ((((CObjectHeader*)xl)->IsPinned() != 0 ? 1 : 0) == pinned_plug_p))
                {
                    if (((CObjectHeader*)xl)->IsPinned() != 0)
                    {
                        ((CObjectHeader*)xl)->GetHeader()->ClrGCBit();
                    }

                    ((CObjectHeader*)xl)->ClearMarked();
                    nuint object_size = size(xl);
                    Debug.Assert(object_size > 0);
                    Debug.Assert(object_size <= (nuint)GCConfig.GetLOHThreshold());
                    last_object_in_plug = xl;
                    xl += (nint)Align(object_size);
                }

                bool next_object_marked_p =
                    xl < end && ((CObjectHeader*)xl)->IsMarked() != 0;
                if (pinned_plug_p != 0)
                {
                    if (next_object_marked_p)
                    {
                        ((CObjectHeader*)xl)->ClearMarked();
                        last_object_in_plug = xl;
                        nuint extra_size = Align(size(xl));
                        xl += (nint)extra_size;
                        added_pinning_size = extra_size;
                    }
                }
                else if (next_object_marked_p)
                {
                    npin_before_pin_p = 1;
                }

                Debug.Assert(xl <= end);
                x = xl;
                plug_end = x;
                nuint ps = (nuint)(plug_end - plug_start);
                last_plug_len = ps;
                byte* new_address = null;

                if (pinned_plug_p == 0 &&
                    allocate_in_condemned != 0 &&
                    settings.condemned_generation == GCInterfaceOffsets.max_generation &&
                    ps > GCToOSInterface.GetPageSize())
                {
                    nint reloc = unchecked((nint)(
                        plug_start - generation.generation_allocation_pointer(consing_gen)));
                    if (ps > 8 * GCToOSInterface.GetPageSize() &&
                        reloc > 0 &&
                        (nuint)reloc < ps / 16)
                    {
                        Debug.Assert(saved_last_npinned_plug_p == 0);
                        if (last_pinned_plug is not null)
                        {
                            merge_with_last_pin_p = 1;
                        }
                        else
                        {
                            enque_pinned_plug(hp, plug_start, 0, null);
                            last_pinned_plug = plug_start;
                        }

                        convert_to_pinned_plug(
                            ref last_npinned_plug_p,
                            ref last_pinned_plug_p,
                            ref pinned_plug_p,
                            ps,
                            ref artificial_pinned_size);
                    }
                }

                dynamic_data* dd_active_old = dynamic_data_of(hp, active_old_gen_number);
                dynamic_data.dd_survived_size(dd_active_old) += ps;
                int convert_to_pinned_p = 0;

                if (pinned_plug_p == 0)
                {
                    add_gen_plug(active_old_gen_number, ps);

                    if (allocate_in_condemned != 0)
                    {
                        new_address = allocate_in_condemned_generations(
                            hp,
                            consing_gen,
                            ps,
                            active_old_gen_number,
                            &convert_to_pinned_p,
                            npin_before_pin_p != 0 ? plug_end : null,
                            seg1,
                            plug_start);
                    }
                    else
                    {
                        Debug.Assert(older_gen is not null);
                        new_address = allocate_in_older_generation(
                            hp, older_gen, ps, active_old_gen_number, plug_start);
                        if (new_address is null)
                        {
                            if (generation.generation_allocator(older_gen)
                                ->discard_if_no_fit_p() != 0)
                            {
                                allocate_in_condemned = 1;
                            }

                            new_address = allocate_in_condemned_generations(
                                hp,
                                consing_gen,
                                ps,
                                active_old_gen_number,
                                &convert_to_pinned_p,
                                npin_before_pin_p != 0 ? plug_end : null,
                                seg1,
                                plug_start);
                        }
                    }

                    if (convert_to_pinned_p != 0)
                    {
                        Debug.Assert(last_npinned_plug_p != 0);
                        Debug.Assert(last_pinned_plug_p == 0);
                        convert_to_pinned_plug(
                            ref last_npinned_plug_p,
                            ref last_pinned_plug_p,
                            ref pinned_plug_p,
                            ps,
                            ref artificial_pinned_size);
                        enque_pinned_plug(hp, plug_start, 0, null);
                        last_pinned_plug = plug_start;
                    }
                    else
                    {
                        Debug.Assert(new_address is not null);
                        if (is_plug_padded(plug_start) != 0)
                        {
                            dynamic_data.dd_padding_size(dd_active_old) +=
                                Align((nuint)GCInterfaceOffsets.min_obj_size);
                        }
                    }
                }

                if (pinned_plug_p != 0)
                {
                    GCEvents.GCEventFirePinPlugAtGCTime(
                        plug_start,
                        plug_end,
                        merge_with_last_pin_p != 0 ? null : (byte*)node_gap_size(plug_start));

                    if (merge_with_last_pin_p != 0)
                    {
                        merge_with_last_pinned_plug(hp, last_pinned_plug, ps);
                    }
                    else
                    {
                        Debug.Assert(last_pinned_plug == plug_start);
                        set_pinned_info(hp, plug_start, ps, consing_gen);
                    }

                    new_address = plug_start;
                    nuint pinned_plug_size = (nuint)(plug_end - plug_start);
                    pinned_survived_region = unchecked(
                        pinned_survived_region + (int)pinned_plug_size);
                    dynamic_data.dd_pinned_survived_size(dd_active_old) += pinned_plug_size;
                    dynamic_data.dd_added_pinned_size(dd_active_old) += added_pinning_size;
                    dynamic_data.dd_artificial_pinned_survived_size(dd_active_old) +=
                        artificial_pinned_size;
                }

                Debug.Assert(
                    !(new_address > plug_start &&
                      new_address < heap_segment.heap_segment_reserved(seg1)));

                if (merge_with_last_pin_p == 0)
                {
                    if (current_brick != brick_of(plug_start))
                    {
                        current_brick = update_brick_table(
                            tree, current_brick, plug_start, saved_plug_end);
                        sequence_number = 0;
                        tree = null;
                    }

                    set_node_relocation_distance(
                        plug_start, unchecked((nint)(new_address - plug_start)));
                    if (last_node is not null &&
                        node_relocation_distance(last_node) ==
                            node_relocation_distance(plug_start) +
                            (nint)node_gap_size(plug_start))
                    {
                        set_node_left(plug_start);
                    }

                    if (sequence_number == 0)
                    {
                        tree = plug_start;
                    }

                    tree = insert_node(plug_start, ++sequence_number, tree, last_node);
                    last_node = plug_start;
                }
            }

            x = find_next_marked(x, end, use_mark_list, ref mark_list_next, local_mark_list_index);
        }

        descr_generations();
        print_free_and_plug();

        // Record gen2 growth during a gen1 GC and flag maxgen size increase for provisional mode.
        if (condemned_gen_number == GCInterfaceOffsets.max_generation - 1)
        {
            Debug.Assert(older_gen is not null);
            nuint currentFreeObjSpace = generation.generation_free_obj_space(older_gen);
            nuint rejected_free_space = currentFreeObjSpace >= saved_free_obj_space
                ? currentFreeObjSpace - saved_free_obj_space
                : 0;
            nuint free_list_allocated = unchecked(
                generation.generation_free_list_allocated(older_gen) - saved_free_list_allocated);
            nuint end_seg_allocated = unchecked(
                generation.generation_end_seg_allocated(older_gen) - saved_end_seg_allocated);
            nuint condemned_allocated = unchecked(
                generation.generation_condemned_allocated(older_gen) - saved_condemned_allocated);
            nuint growth = unchecked(end_seg_allocated + condemned_allocated);

            if (growth > 0)
            {
                maxgen_size_inc_p = true;
            }

            maxgen_size_increase* maxgen_size_info = &get_gc_data_per_heap(hp)->maxgen_size_info;
            maxgen_size_info->free_list_allocated = free_list_allocated;
            maxgen_size_info->free_list_rejected = rejected_free_space;
            maxgen_size_info->end_seg_allocated = end_seg_allocated;
            maxgen_size_info->condemned_allocated = condemned_allocated;
            maxgen_size_info->pinned_allocated = hp->maxgen_pinned_compact_before_advance;
            nuint pinnedAllocation = generation.generation_pinned_allocation_compact_size(
                generation_of(generation_table, GCInterfaceOffsets.max_generation));
            maxgen_size_info->pinned_allocated_advance =
                pinnedAllocation >= hp->maxgen_pinned_compact_before_advance
                    ? pinnedAllocation - hp->maxgen_pinned_compact_before_advance
                    : 0;
        }

        nuint fragmentation = generation_fragmentation(
            hp,
            generation_of(generation_table, condemned_gen_number),
            consing_gen,
            heap_segment.heap_segment_allocated(hp->ephemeral_heap_segment));

        bool should_expand = false;
        bool should_compact;

#if TARGET_64BIT
        if (settings.concurrent == 0 &&
            !hp->special_sweep_p &&
            !provisional_mode_triggered &&
            condemned_gen_number < GCInterfaceOffsets.max_generation &&
            (settings.gen0_reduction_count > 0 || settings.entry_memory_load >= 95))
        {
            should_compact = true;
            get_gc_data_per_heap(hp)->set_mechanism(
                gc_mechanism_per_heap.gc_heap_compact,
                (uint)(settings.gen0_reduction_count > 0
                    ? gc_heap_compact_reason.compact_fragmented_gen0
                    : gc_heap_compact_reason.compact_high_mem_load));
        }
        else
#endif
        {
            should_compact = decide_on_compacting(
                hp, condemned_gen_number, fragmentation, ref should_expand);
        }

        if (condemned_gen_number == GCInterfaceOffsets.max_generation)
        {
            if (settings.loh_compaction != 0)
            {
                should_compact = true;
                get_gc_data_per_heap(hp)->set_mechanism(
                    gc_mechanism_per_heap.gc_heap_compact,
                    (uint)gc_heap_compact_reason.compact_loh_forced);
            }
            else
            {
                sweep_uoh_objects(hp, (int)gc_generation_num.loh_generation);
            }

            sweep_uoh_objects(hp, (int)gc_generation_num.poh_generation);
        }
        else
        {
            settings.loh_compaction = 0;
        }

        hp->gc_policy = should_compact && should_expand
            ? policy_expand
            : should_compact ? policy_compact : policy_sweep;

        gc_t_join.join(hp, (int)gc_join_stage.gc_join_decide_on_compaction);
        if (gc_t_join.joined())
        {
            if (maxgen_size_inc_p && provisional_mode_triggered
#if BACKGROUND_GC
                && !is_bgc_in_progress()
#endif
                )
            {
                pm_trigger_full_gc = true;
            }
            else
            {
                bool joined_special_sweep_p = false;
                int pol_max = policy_sweep;

                for (int i = 0; i < n_heaps; i++)
                {
                    if (pol_max < g_heaps[i]->gc_policy)
                    {
                        pol_max = policy_compact;
                    }

                    joined_special_sweep_p |= g_heaps[i]->special_sweep_p;
                }

                for (int i = 0; i < n_heaps; i++)
                {
                    g_heaps[i]->special_sweep_p = joined_special_sweep_p;
                    if (joined_special_sweep_p)
                    {
                        g_heaps[i]->gc_policy = policy_sweep;
                    }
                    else if (pol_max > g_heaps[i]->gc_policy)
                    {
                        g_heaps[i]->gc_policy = pol_max;
                    }
                }

                bool is_full_compacting_gc = false;
                if (hp->gc_policy >= policy_compact &&
                    condemned_gen_number == GCInterfaceOffsets.max_generation)
                {
                    full_gc_counts[gc_type_compacting]++;
                    is_full_compacting_gc = true;
                }

                for (int i = 0; i < n_heaps; i++)
                {
                    if (is_full_compacting_gc)
                    {
                        g_heaps[i]->loh_alloc_since_cg = 0;
                    }
                }
            }

            gc_t_join.restart();
        }

        should_compact = hp->gc_policy >= policy_compact;
        should_expand = hp->gc_policy >= policy_expand;

        hp->loh_compacted_p = 0;
        if (condemned_gen_number == GCInterfaceOffsets.max_generation)
        {
            if (settings.loh_compaction != 0)
            {
                if (should_compact && plan_loh(hp))
                {
                    hp->loh_compacted_p = 1;
                }
                else
                {
                    sweep_uoh_objects(hp, (int)gc_generation_num.loh_generation);
                }
            }
            else if (hp->loh_pinned_queue is not null)
            {
                decay_loh_pinned_queue(hp);
            }
        }

        if (!pm_trigger_full_gc && pm_stress_on && provisional_mode_triggered)
        {
            if (settings.condemned_generation == GCInterfaceOffsets.max_generation - 1 &&
                (settings.gc_index % 5) == 0
#if BACKGROUND_GC
                && !is_bgc_in_progress()
#endif
                )
            {
                pm_trigger_full_gc = true;
            }
        }

        if (settings.condemned_generation == GCInterfaceOffsets.max_generation - 1)
        {
            if (provisional_mode_triggered && should_expand)
            {
                should_expand = false;
            }

            if (pm_trigger_full_gc)
            {
                should_compact = false;
            }
        }

        if (should_compact)
        {
#if BACKGROUND_GC
            if (should_update_end_mark_size())
            {
                Debug.Assert(older_gen is not null);
                hp->background_soh_size_end_mark += unchecked(
                    generation.generation_end_seg_allocated(older_gen) - saved_end_seg_allocated);
            }
#endif

            generation.generation_allocation_limit(condemned_gen1) =
                generation.generation_allocation_pointer(condemned_gen1);
            if (older_gen is not null)
            {
                allocator.commit_alloc_list_changes(
                    generation.generation_allocator(older_gen));
                fix_older_allocation_area(hp, older_gen);
            }

            // GCToEEInterface::DiagWalkSurvivors is a diagnostic EE survivor walk deferred with the
            // rest of the server event / EE diagnostic integration.

            relocate_phase(hp, condemned_gen_number, first_condemned_address);
            compact_phase(
                hp,
                condemned_gen_number,
                first_condemned_address,
                settings.demotion == 0 && settings.promotion != 0 ? 1 : 0);
            fix_generation_bounds(hp, condemned_gen_number, consing_gen);
            Debug.Assert(
                generation.generation_allocation_limit(generation_of(generation_table, 0)) ==
                generation.generation_allocation_pointer(generation_of(generation_table, 0)));

            hp->end_gen0_region_committed_space =
                get_gen0_end_space(hp, memory_type.memory_type_committed);

            gc_t_join.join(hp, (int)gc_join_stage.gc_join_adjust_handle_age_compact);
            if (gc_t_join.joined())
            {
                // FEATURE_EVENT_TRACE gc_time_info[time_compact] timing and the _DEBUG
                // verify_committed_bytes check are deferred with the server event / heap-verify
                // integration; the join still synchronizes every worker before promotions are granted.
                gc_t_join.restart();
            }

            hp->server_finalize_queue->UpdatePromotedGenerations(
                condemned_gen_number,
                settings.demotion == 0 && settings.promotion != 0 ? 1 : 0);

            ScanContext sc = default;
            sc.thread_number = hp->heap_number;
            sc.thread_count = n_heaps;
            sc.promotion = 0;
            sc.concurrent = 0;
            if (settings.promotion != 0 && settings.demotion == 0)
            {
                GCScan.GcPromotionsGranted(
                    condemned_gen_number,
                    GCInterfaceOffsets.max_generation,
                    &sc);
            }
            else if (settings.demotion != 0)
            {
                GCScan.GcDemote(
                    condemned_gen_number,
                    GCInterfaceOffsets.max_generation,
                    &sc);
            }

            thread_pinned_plug_gaps(hp);

            clear_gen1_cards(hp);
        }
        else
        {
            settings.promotion = 1;
            settings.compaction = 0;
            settings.demotion = 0;

            if (older_gen is not null)
            {
                allocator.copy_from_alloc_list(
                    generation.generation_allocator(older_gen),
                    saved_free_list);
                generation.generation_free_list_space(older_gen) = saved_free_list_space;
                generation.generation_free_obj_space(older_gen) = saved_free_obj_space;
#if TARGET_64BIT && !TARGET_WASM
                if (condemned_gen_number == GCInterfaceOffsets.max_generation - 1)
                {
                    generation.generation_free_list_space(older_gen) = unchecked(
                        generation.generation_free_list_space(older_gen) - hp->gen2_removed_no_undo);
                    generation.generation_free_obj_space(older_gen) = unchecked(
                        generation.generation_free_obj_space(older_gen) + hp->gen2_removed_no_undo);
                }
#endif
                generation.generation_free_list_allocated(older_gen) = saved_free_list_allocated;
                generation.generation_end_seg_allocated(older_gen) = saved_end_seg_allocated;
                generation.generation_condemned_allocated(older_gen) = saved_condemned_allocated;
                generation.generation_sweep_allocated(older_gen) = unchecked(
                    generation.generation_sweep_allocated(older_gen) +
                    dynamic_data.dd_survived_size(dynamic_data_of(hp, condemned_gen_number)));
                generation.generation_allocation_limit(older_gen) = saved_allocation_limit;
                generation.generation_allocation_pointer(older_gen) = saved_allocation_pointer;
                generation.generation_allocation_context_start_region(older_gen) =
                    saved_allocation_start_region;
                generation.generation_allocation_segment(older_gen) = saved_allocation_segment;
                fix_older_allocation_area(hp, older_gen);
            }

            // GCToEEInterface::DiagWalkSurvivors is a diagnostic EE survivor walk deferred with the
            // rest of the server event / EE diagnostic integration.

            make_free_lists(hp, condemned_gen_number);
            nuint total_recovered_sweep_size = recover_saved_pinned_info(hp);
            if (total_recovered_sweep_size > 0)
            {
                generation* max_gen = generation_of(
                    generation_table, (int)gc_generation_num.max_generation);
                generation.generation_free_obj_space(max_gen) = unchecked(
                    generation.generation_free_obj_space(max_gen) - total_recovered_sweep_size);
            }

            hp->end_gen0_region_committed_space =
                get_gen0_end_space(hp, memory_type.memory_type_committed);

            ScanContext sc = default;
            sc.thread_number = hp->heap_number;
            sc.thread_count = n_heaps;
            sc.promotion = 0;
            sc.concurrent = 0;

            gc_t_join.join(hp, (int)gc_join_stage.gc_join_adjust_handle_age_sweep);
            if (gc_t_join.joined())
            {
                // FEATURE_EVENT_TRACE gc_time_info[time_sweep] timing is deferred with the server
                // event integration; the join still synchronizes every worker before promotions are
                // granted.
                if (!hp->special_sweep_p)
                {
                    GCScan.GcPromotionsGranted(
                        condemned_gen_number,
                        GCInterfaceOffsets.max_generation,
                        &sc);
                }

                verify_region_to_generation_map();

                gc_t_join.restart();
            }

            if (!hp->special_sweep_p)
            {
                hp->server_finalize_queue->UpdatePromotedGenerations(
                    condemned_gen_number,
                    1);
            }

            if (!hp->special_sweep_p)
            {
                clear_gen1_cards(hp);
            }
        }
    }
}

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
