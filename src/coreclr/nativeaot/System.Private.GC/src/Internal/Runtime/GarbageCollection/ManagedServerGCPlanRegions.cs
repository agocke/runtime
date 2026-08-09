// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Server plan-phase per-heap plug/region planning loop, translated from the SVR-namespace
// compilation of plan_phase.cpp for the active x64 Linux
// SERVER_GC -> MULTIPLE_HEAPS -> DYNAMIC_HEAP_COUNT -> USE_REGIONS feature chain. These are the
// per-heap helpers that walk a heap's condemned regions, consume its pinned-plug queue and set the
// heap_segment_plan_allocated bounds that ManagedServerGCPlanPhase.cs's compaction-vs-sweep
// deciders read:
//
//   * the pinned-plug-queue consumers (pinned_plug_que_empty_p, oldest_pin, deque_pinned_plug,
//     set_new_pin_info) that hand out the queued pins in address order, plus the linear-walk leaf
//     find_next_marked and the saved-allocated snapshot save_allocated,
//   * the plan-space and demotion attribution leaves (update_planned_gen0_free_space,
//     attribute_pin_higher_gen_alloc, decide_on_gen1_pin_promotion, decide_on_demotion_pin_surv,
//     skip_pins_in_alloc_region),
//   * the region-planning deciders should_sweep_in_plan / sweep_region_in_plan and the
//     consing-region walkers process_last_np_surv_region / process_remaining_regions, and
//   * the region preparation leaf clear_gen1_cards and the per-heap init_records reset of the
//     plan-space accounting.
//
// gcpriv.h marks the region-planning state (regions_per_gen, planned_regions_per_gen,
// sip_maxgen_regions_per_gen, reserved_free_regions_sip, decide_promote_gen1_pins_p,
// special_sweep_p, maxgen_pinned_compact_before_advance, new_gen0_regions_in_plns,
// new_regions_in_prr) and the pinned-plug queue (mark_stack_array / mark_stack_bos /
// mark_stack_tos) as PER_HEAP_FIELD_SINGLE_GC / PER_HEAP_FIELD_DIAG_ONLY, so they are instance-owned
// in the MULTIPLE_HEAPS build and every function here reads and writes them through the heap
// parameter, exactly as the native per-heap methods do through the implicit this. enable_special_regions_p
// is PER_HEAP_ISOLATED_FIELD_INIT_ONLY, so it stays static.
//
// No collection is routed by this slice: the plan_phase driver that invokes these helpers in order,
// the gc_join plan-phase joins, allocate_in_condemned_generations, plan_loh/plan_poh, the
// brick-tree threading, fix_generation_bounds/thread_final_regions and the relocate/compact/sweep
// execution all remain deferred, so nothing here runs against a live heap yet.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    // plan_phase.cpp region-planning thresholds.
    public const nuint demotion_plug_len_th = 6 * 1024 * 1024;
    public const int sip_surv_ratio_th = 90;
    public const int sip_old_card_surv_ratio_th = 90;

    // The pinned-plug queue consumers (gcinternal.h). The queue is this heap's own mark_stack_array,
    // consumed from bos (bottom of stack) up to tos (top of stack) in address order.
    public static int pinned_plug_que_empty_p(gc_heap* heap)
    {
        return heap->mark_stack_bos == heap->mark_stack_tos ? 1 : 0;
    }

    public static mark* oldest_pin(gc_heap* heap)
    {
        return pinned_plug_of(heap, heap->mark_stack_bos);
    }

    public static nuint deque_pinned_plug(gc_heap* heap)
    {
        nuint m = heap->mark_stack_bos;
        heap->mark_stack_bos++;
        return m;
    }

    public static void set_new_pin_info(mark* m, byte* pin_free_space_start)
    {
        m->len = (nuint)(pinned_plug(m) - pin_free_space_start);
        m->allocation_context_start_region = pin_free_space_start;
    }

    // Find the next marked object at or after x, either by consuming the sorted mark list or by
    // walking objects linearly (plan_phase.cpp).
    public static byte* find_next_marked(
        byte* x,
        byte* end,
        int use_mark_list,
        ref byte** mark_list_next,
        byte** mark_list_index)
    {
        if (use_mark_list != 0)
        {
            while (mark_list_next < mark_list_index && *mark_list_next <= x)
            {
                mark_list_next++;
            }

            x = end;
            if (mark_list_next < mark_list_index)
            {
                x = *mark_list_next;
            }
        }
        else
        {
            byte* xl = x;
            while (xl < end && ((CObjectHeader*)xl)->IsMarked() == 0)
            {
                nuint object_size = size(xl);
                Debug.Assert(object_size > 0);
                xl += (nint)Align(object_size);
            }

            Debug.Assert(xl <= end);
            x = xl;
        }

        return x;
    }

    public static void save_allocated(heap_segment* seg)
    {
        if (heap_segment.heap_segment_saved_allocated(seg) is null)
        {
            heap_segment.heap_segment_saved_allocated(seg) =
                heap_segment.heap_segment_allocated(seg);
        }
    }

    // Record a pinned-plug free-space gap in gen0's planned free space, tracking whether any gap is
    // large enough to satisfy END_SPACE_AFTER_GC_FL (plan_phase.cpp).
    public static void update_planned_gen0_free_space(gc_heap* hp, nuint free_size, byte* plug)
    {
        _ = plug;

        hp->gen0_pinned_free_space += free_size;
        if (!hp->gen0_large_chunk_found)
        {
            hp->gen0_large_chunk_found = free_size >= END_SPACE_AFTER_GC_FL;
        }
    }

    // Attribute a pinned plug's survival to the sweep/compact accounting of its destination
    // generation (allocation.cpp). This is the overload used by the region planners, where the
    // destination generation is supplied directly rather than derived from a segment.
    public static void attribute_pin_higher_gen_alloc(gc_heap* hp, int frgn, int togn, nuint len)
    {
        if (frgn != GCInterfaceOffsets.max_generation && settings.promotion != 0)
        {
            generation.generation_pinned_allocation_sweep_size(
                generation_of(generation_table_of(hp), frgn + 1)) += len;

            if (frgn < togn)
            {
                generation.generation_pinned_allocation_compact_size(
                    generation_of(generation_table_of(hp), togn)) += len;
            }
        }
    }

    public static bool decide_on_gen1_pin_promotion(float pin_frag_ratio, float pin_surv_ratio)
    {
        return pin_frag_ratio > 0.15f && pin_surv_ratio > 0.30f;
    }

    // Skip past the pins that fall within the current consing alloc region, recording each pin's
    // free-space gap and advancing the consing gen's alloc pointer, then plan the region
    // (plan_phase.cpp).
    public static void skip_pins_in_alloc_region(
        gc_heap* hp,
        generation* consing_gen,
        int plan_gen_num)
    {
        heap_segment* alloc_region = generation.generation_allocation_segment(consing_gen);
        nuint skipped_pins_len = 0;
        while (pinned_plug_que_empty_p(hp) == 0)
        {
            byte* oldest_plug = pinned_plug(oldest_pin(hp));
            if (oldest_plug >= generation.generation_allocation_pointer(consing_gen) &&
                oldest_plug < heap_segment.heap_segment_allocated(alloc_region))
            {
                mark* m = pinned_plug_of(hp, deque_pinned_plug(hp));
                byte* plug = pinned_plug(m);
                nuint len = pinned_len(m);

                skipped_pins_len = unchecked(skipped_pins_len + len);
                set_new_pin_info(m, generation.generation_allocation_pointer(consing_gen));
                generation.generation_allocation_pointer(consing_gen) =
                    (byte*)unchecked((nuint)plug + len);
            }
            else
            {
                break;
            }
        }

        attribute_pin_higher_gen_alloc(
            hp,
            heap_segment.heap_segment_gen_num(alloc_region),
            plan_gen_num,
            skipped_pins_len);
        set_region_plan_gen_num_sip(alloc_region, plan_gen_num);
        heap_segment.heap_segment_plan_allocated(alloc_region) =
            generation.generation_allocation_pointer(consing_gen);
    }

    // For a region with no more pins to plan, decide whether to promote or demote its pinned
    // survivors and record the region's plan generation (plan_phase.cpp).
    public static void decide_on_demotion_pin_surv(
        gc_heap* hp,
        heap_segment* region,
        int* no_pinned_surv_region_count,
        bool promote_gen1_pins_p,
        bool large_pins_p)
    {
        int gen_num = heap_segment.heap_segment_gen_num(region);
        int new_gen_num = 0;
        int pinned_surv = heap_segment.heap_segment_pinned_survived(region);
        bool promote_pins_p = large_pins_p;

        if (pinned_surv == 0)
        {
            (*no_pinned_surv_region_count)++;
        }
        else
        {
            if (!promote_pins_p &&
                gen_num == GCInterfaceOffsets.max_generation - 1 &&
                promote_gen1_pins_p)
            {
                promote_pins_p = true;
            }

            if (promote_pins_p)
            {
                new_gen_num = get_plan_gen_num(gen_num);
            }

            attribute_pin_higher_gen_alloc(hp, gen_num, new_gen_num, unchecked((nuint)pinned_surv));
        }

        set_region_plan_gen_num(region, new_gen_num);
    }

    // Decide, based on survival ratios, whether a condemned region should be swept in place instead
    // of being compacted (plan_phase.cpp). The STRESS_REGIONS testing path is excluded.
    public static bool should_sweep_in_plan(gc_heap* hp, heap_segment* region)
    {
        Debug.Assert(hp is not null);

        if (!enable_special_regions_p ||
            settings.reason == gc_reason.reason_induced_aggressive)
        {
            return false;
        }

        bool sip_p = false;
        int gen_num = get_region_gen_num(region);
        int new_gen_num = get_plan_gen_num(gen_num);
        heap_segment.heap_segment_swept_in_plan(region) = 0;

        nuint basic_region_size = (nuint)1 << (int)min_segment_size_shr;
        Debug.Assert(
            heap_segment.heap_segment_gen_num(region) ==
            heap_segment.heap_segment_plan_gen_num(region));
        byte surv_ratio = unchecked((byte)(
            ((double)heap_segment.heap_segment_survived(region) * 100.0) /
            (double)basic_region_size));
        if (surv_ratio >= sip_surv_ratio_th)
        {
            set_region_plan_gen_num(region, new_gen_num);
            sip_p = true;
        }

        if (settings.promotion != 0 && new_gen_num < GCInterfaceOffsets.max_generation)
        {
            int old_card_surv_ratio = (int)(
                ((double)heap_segment.heap_segment_old_card_survived(region) * 100.0) /
                (double)basic_region_size);
            if (old_card_surv_ratio >= sip_old_card_surv_ratio_th)
            {
                set_region_plan_gen_num(
                    region,
                    GCInterfaceOffsets.max_generation,
                    replace_p: true);
                hp->sip_maxgen_regions_per_gen[gen_num]++;
                sip_p = true;
            }
        }

        if (sip_p &&
            new_gen_num < GCInterfaceOffsets.max_generation &&
            hp->sip_maxgen_regions_per_gen[gen_num] == hp->regions_per_gen[gen_num])
        {
            Debug.Assert(get_region_gen_num(region) == 0);
            Debug.Assert(new_gen_num < GCInterfaceOffsets.max_generation);
            heap_segment* reserved_free_region = get_free_region(hp, gen_num);
            if (reserved_free_region is not null)
            {
                reserved_free_region_sip(hp, gen_num) = reserved_free_region;
            }
            else
            {
                hp->sip_maxgen_regions_per_gen[gen_num]--;
                set_region_plan_gen_num(region, new_gen_num, replace_p: true);
            }
        }

        return sip_p;
    }

    // Sweep a region in place: set the swept_in_plan flag, rebuild the region's free list from its
    // unmarked gaps, fix its bricks and record its final allocated/plan-allocated bounds
    // (plan_phase.cpp).
    public static void sweep_region_in_plan(
        gc_heap* hp,
        heap_segment* region,
        int use_mark_list,
        ref byte** mark_list_next,
        byte** mark_list_index)
    {
        Debug.Assert(hp is not null);

        set_region_sweep_in_plan(region);
        region->init_free_list();

        byte* x = heap_segment.heap_segment_mem(region);
        byte* last_marked_obj_start = null;
        byte* last_marked_obj_end = null;
        byte* end = heap_segment.heap_segment_allocated(region);
#if DEBUG
        nuint survived = 0;
#endif
        while (x < end)
        {
            byte* obj = x;
            nuint obj_brick = (nuint)obj / card_table_info.brick_size;
            byte* next_obj;
            if (((CObjectHeader*)obj)->IsMarked() != 0)
            {
                if (((CObjectHeader*)obj)->IsPinned() != 0)
                {
                    ((CObjectHeader*)obj)->GetHeader()->ClrGCBit();
                }

                ((CObjectHeader*)obj)->ClearMarked();
                nuint object_size = size(obj);
                next_obj = obj + (nint)Align(object_size);
                last_marked_obj_start = obj;
                last_marked_obj_end = next_obj;
#if DEBUG
                survived += object_size;
#endif
            }
            else
            {
                next_obj = find_next_marked(
                    x,
                    end,
                    use_mark_list,
                    ref mark_list_next,
                    mark_list_index);
                if (next_obj > obj && next_obj != end)
                {
                    nuint free_obj_size = (nuint)(next_obj - obj);
                    make_unused_array(obj, free_obj_size);
                    region->thread_free_obj(obj, free_obj_size);
                }
            }

            nuint next_obj_brick = (nuint)next_obj / card_table_info.brick_size;
            if (next_obj_brick != obj_brick)
            {
                fix_brick_to_highest(obj, next_obj);
            }

            x = next_obj;
        }

        if (last_marked_obj_start is not null)
        {
            nuint last_marked_obj_start_b = brick_of(last_marked_obj_start);
            nuint last_marked_obj_end_b = brick_of(last_marked_obj_end - 1);
            if (last_marked_obj_start_b == last_marked_obj_end_b)
            {
                set_brick(
                    last_marked_obj_start_b,
                    unchecked((nint)(
                        last_marked_obj_start -
                        brick_address(last_marked_obj_start_b))));
            }
            else
            {
                set_brick(
                    last_marked_obj_end_b,
                    unchecked((nint)(last_marked_obj_start_b - last_marked_obj_end_b)));
            }
        }
        else
        {
            last_marked_obj_end = heap_segment.heap_segment_mem(region);
        }

#if DEBUG
        // MULTIPLE_HEAPS: a region's recorded survived is an upper bound (equalized across heaps),
        // so the walk's survived total must not exceed it.
        Debug.Assert(survived <= heap_segment.heap_segment_survived(region));
#endif
        Debug.Assert(last_marked_obj_end is not null);
        save_allocated(region);
        heap_segment.heap_segment_allocated(region) = last_marked_obj_end;
        heap_segment.heap_segment_plan_allocated(region) = last_marked_obj_end;

        int plan_gen_num = heap_segment.heap_segment_plan_gen_num(region);
        if (plan_gen_num < heap_segment.heap_segment_gen_num(region))
        {
            generation.generation_allocation_size(
                generation_of(
                    generation_table_of(hp),
                    plan_gen_num)) += heap_segment.heap_segment_survived(region);
        }
    }

    // Handle the last consing alloc region when the plan generation changes: reuse it, switch to the
    // next region or (for promotion) get a new gen0 region, falling back to special sweep if none is
    // available (plan_phase.cpp).
    public static void process_last_np_surv_region(
        gc_heap* hp,
        generation* consing_gen,
        int current_plan_gen_num,
        int next_plan_gen_num)
    {
        heap_segment* alloc_region = generation.generation_allocation_segment(consing_gen);
        byte* consing_gen_alloc_ptr = generation.generation_allocation_pointer(consing_gen);
        Debug.Assert(
            consing_gen_alloc_ptr >= heap_segment.heap_segment_mem(alloc_region) &&
            consing_gen_alloc_ptr <= heap_segment.heap_segment_reserved(alloc_region));

        if (current_plan_gen_num == next_plan_gen_num)
        {
            return;
        }

        if (generation.generation_allocation_pointer(consing_gen) ==
            heap_segment.heap_segment_mem(alloc_region))
        {
            return;
        }

        skip_pins_in_alloc_region(hp, consing_gen, current_plan_gen_num);
        heap_segment* next_region = heap_segment_next_non_sip(alloc_region);
        if (next_region is null)
        {
            int gen_num = heap_segment.heap_segment_gen_num(alloc_region);
            if (gen_num > 0)
            {
                next_region = generation.generation_start_segment(
                    generation_of(generation_table_of(hp), gen_num - 1));
            }
            else if (settings.promotion != 0)
            {
                Debug.Assert(next_plan_gen_num == 0);
                next_region = get_new_region(generation_table_of(hp), hp, 0);
                if (next_region is not null)
                {
                    hp->regions_per_gen[0]++;
                    hp->new_gen0_regions_in_plns++;
                }
                else
                {
                    hp->special_sweep_p = true;
                }
            }
            else
            {
                Debug.Fail("ran out of regions for non-promotion planning");
            }
        }

        if (next_region is not null)
        {
            init_alloc_info(consing_gen, next_region);
        }
        else
        {
            Debug.Assert(hp->special_sweep_p);
        }
    }

    // Plan the regions that still have no planned allocation: consume any remaining pins, decide the
    // plan generation of each remaining non-SIP region, and make sure every condemned generation
    // ends up with at least one region (or fall back to special sweep) (plan_phase.cpp).
    public static void process_remaining_regions(
        gc_heap* hp,
        int current_plan_gen_num,
        generation* consing_gen)
    {
        Debug.Assert(
            current_plan_gen_num == 0 ||
            (settings.promotion == 0 && current_plan_gen_num == -1));

        if (hp->special_sweep_p)
        {
            Debug.Assert(pinned_plug_que_empty_p(hp) != 0);
        }

        if (current_plan_gen_num == -1)
        {
            Debug.Assert(settings.promotion == 0);
            current_plan_gen_num = 0;

            heap_segment* alloc_region = generation.generation_allocation_segment(consing_gen);
            if (generation.generation_allocation_pointer(consing_gen) >
                heap_segment.heap_segment_mem(alloc_region))
            {
                skip_pins_in_alloc_region(hp, consing_gen, current_plan_gen_num);
                heap_segment* next_region = heap_segment_next_non_sip(alloc_region);
                if (next_region is null &&
                    heap_segment.heap_segment_gen_num(alloc_region) > 0)
                {
                    next_region = generation.generation_start_segment(
                        generation_of(
                            generation_table_of(hp),
                            heap_segment.heap_segment_gen_num(alloc_region) - 1));
                }

                if (next_region is not null)
                {
                    init_alloc_info(consing_gen, next_region);
                }
                else
                {
                    Debug.Assert(pinned_plug_que_empty_p(hp) != 0);
                    generation.generation_allocation_segment(consing_gen) = null;
                    generation.generation_allocation_pointer(consing_gen) = null;
                    generation.generation_allocation_limit(consing_gen) = null;
                }
            }
        }

        int to_be_empty_regions = 0;
        heap_segment* current_region = generation.generation_allocation_segment(consing_gen);
        bool actual_promote_gen1_pins_p = false;

        if (hp->decide_promote_gen1_pins_p)
        {
            nuint gen1_pins_left = 0;
            nuint total_space_to_skip = 0;
            while (current_region is not null)
            {
                int gen_num = heap_segment.heap_segment_gen_num(current_region);
                if (gen_num == 0)
                {
                    break;
                }

                Debug.Assert(gen_num == GCInterfaceOffsets.max_generation - 1);
                if (heap_segment.heap_segment_swept_in_plan(current_region) == 0)
                {
                    gen1_pins_left = unchecked(
                        gen1_pins_left +
                        (nuint)heap_segment.heap_segment_pinned_survived(current_region));
                    total_space_to_skip = unchecked(
                        total_space_to_skip + get_region_size(current_region));
                }

                current_region = heap_segment.heap_segment_next(current_region);
            }

            if (total_space_to_skip != 0)
            {
                nuint gen1_surv = dynamic_data.dd_survived_size(
                    dynamic_data_of(hp, GCInterfaceOffsets.max_generation - 1));
                if (gen1_surv != 0)
                {
                    float pin_frag_ratio =
                        (float)gen1_pins_left / (float)total_space_to_skip;
                    float pin_surv_ratio = (float)gen1_pins_left / (float)gen1_surv;
                    actual_promote_gen1_pins_p =
                        decide_on_gen1_pin_promotion(pin_frag_ratio, pin_surv_ratio);
                }
            }
        }

        hp->maxgen_pinned_compact_before_advance =
            generation.generation_pinned_allocation_compact_size(
                generation_of(
                    generation_table_of(hp),
                    GCInterfaceOffsets.max_generation));

        bool large_pins_p = false;
        while (pinned_plug_que_empty_p(hp) == 0)
        {
            byte* oldest_plug = pinned_plug(oldest_pin(hp));
            heap_segment* nseg = generation.generation_allocation_segment(consing_gen);

            while (oldest_plug < generation.generation_allocation_pointer(consing_gen) ||
                   oldest_plug >= heap_segment.heap_segment_allocated(nseg))
            {
                Debug.Assert(
                    oldest_plug < heap_segment.heap_segment_mem(nseg) ||
                    oldest_plug > heap_segment.heap_segment_reserved(nseg));
                Debug.Assert(
                    generation.generation_allocation_pointer(consing_gen) >=
                    heap_segment.heap_segment_mem(nseg));
                Debug.Assert(
                    generation.generation_allocation_pointer(consing_gen) <=
                    heap_segment.heap_segment_committed(nseg));
                Debug.Assert(heap_segment.heap_segment_swept_in_plan(nseg) == 0);

                heap_segment.heap_segment_plan_allocated(nseg) =
                    generation.generation_allocation_pointer(consing_gen);
                decide_on_demotion_pin_surv(
                    hp,
                    nseg,
                    &to_be_empty_regions,
                    actual_promote_gen1_pins_p,
                    large_pins_p);

                heap_segment* next_seg = heap_segment_next_non_sip(nseg);
                if (next_seg is null && heap_segment.heap_segment_gen_num(nseg) > 0)
                {
                    next_seg = generation.generation_start_segment(
                        generation_of(
                            generation_table_of(hp),
                            heap_segment.heap_segment_gen_num(nseg) - 1));
                }

                Debug.Assert(next_seg is not null);
                nseg = next_seg;
                large_pins_p = false;
                generation.generation_allocation_segment(consing_gen) = nseg;
                generation.generation_allocation_pointer(consing_gen) =
                    heap_segment.heap_segment_mem(nseg);
            }

            mark* m = pinned_plug_of(hp, deque_pinned_plug(hp));
            byte* plug = pinned_plug(m);
            nuint len = pinned_len(m);
            if (!large_pins_p)
            {
                large_pins_p = len >= demotion_plug_len_th;
            }

            set_new_pin_info(m, generation.generation_allocation_pointer(consing_gen));
            nuint free_size = pinned_len(m);
            update_planned_gen0_free_space(hp, free_size, plug);
            generation.generation_allocation_pointer(consing_gen) =
                (byte*)unchecked((nuint)plug + len);
            generation.generation_allocation_limit(consing_gen) =
                generation.generation_allocation_pointer(consing_gen);
        }

        current_region = generation.generation_allocation_segment(consing_gen);
        if (hp->special_sweep_p)
        {
            Debug.Assert(
                current_region is null ||
                heap_segment_next_rw(current_region) is null);
            return;
        }

        current_region = heap_segment_non_sip(current_region);
        if (current_region is not null)
        {
            decide_on_demotion_pin_surv(
                hp,
                current_region,
                &to_be_empty_regions,
                actual_promote_gen1_pins_p,
                large_pins_p);

            if (heap_segment.heap_segment_swept_in_plan(current_region) == 0)
            {
                heap_segment.heap_segment_plan_allocated(current_region) =
                    generation.generation_allocation_pointer(consing_gen);
            }

            heap_segment* region_no_pins =
                heap_segment.heap_segment_next(current_region);
            int region_no_pins_gen_num =
                heap_segment.heap_segment_gen_num(current_region);
            do
            {
                region_no_pins = heap_segment_non_sip(region_no_pins);
                if (region_no_pins is not null)
                {
                    set_region_plan_gen_num(region_no_pins, current_plan_gen_num);
                    to_be_empty_regions++;
                    heap_segment.heap_segment_plan_allocated(region_no_pins) =
                        heap_segment.heap_segment_mem(region_no_pins);
                    region_no_pins = heap_segment.heap_segment_next(region_no_pins);
                }

                if (region_no_pins is null)
                {
                    if (region_no_pins_gen_num > 0)
                    {
                        region_no_pins_gen_num--;
                        region_no_pins = generation.generation_start_segment(
                            generation_of(
                                generation_table_of(hp),
                                region_no_pins_gen_num));
                    }
                    else
                    {
                        break;
                    }
                }
            }
            while (region_no_pins is not null);
        }

        if (to_be_empty_regions != 0)
        {
            Debug.Assert(hp->planned_regions_per_gen[0] != 0);
        }

        int saved_planned_gen0 = hp->planned_regions_per_gen[0];
        int saved_planned_gen1 = hp->planned_regions_per_gen[1];
        int saved_planned_gen2 = hp->planned_regions_per_gen[2];
        Debug.Assert(saved_planned_gen0 >= to_be_empty_regions);
        saved_planned_gen0 -= to_be_empty_regions;

        int plan_regions_needed = 0;
        for (int gen_idx = settings.condemned_generation; gen_idx >= 0; gen_idx--)
        {
            int planned = gen_idx switch
            {
                0 => saved_planned_gen0,
                1 => saved_planned_gen1,
                _ => saved_planned_gen2,
            };
            if (planned == 0)
            {
                plan_regions_needed++;
            }
        }

        if (plan_regions_needed > to_be_empty_regions)
        {
            plan_regions_needed -= to_be_empty_regions;
            while (plan_regions_needed != 0 &&
                   get_new_region(generation_table_of(hp), hp, 0) is not null)
            {
                hp->new_regions_in_prr++;
                plan_regions_needed--;
            }

            if (plan_regions_needed > 0)
            {
                hp->special_sweep_p = true;
            }
        }
    }

    // Clear the cards over gen1's regions when we promote without demotion (plan_phase.cpp).
    public static void clear_gen1_cards(gc_heap* hp)
    {
        if (settings.demotion == 0 && settings.promotion != 0)
        {
            generation* gen1 = generation_of(
                generation_table_of(hp),
                (int)gc_generation_num.soh_gen1);
            heap_segment* region = generation.generation_start_segment(gen1);
            while (region is not null)
            {
                clear_card_for_addresses(
                    get_region_start(region),
                    heap_segment.heap_segment_reserved(region));
                region = heap_segment.heap_segment_next(region);
            }
        }
    }

    // Per-heap reset of the plan-space accounting at the start of each collection (init.cpp
    // gc_heap::init_records). special_sweep_p is reset in mark_phase, and the region-planning
    // counters (regions_per_gen etc.) are reset by the plan-phase driver, so they are not reset here.
    public static void init_records(gc_heap* hp)
    {
        hp->gc_data_per_heap = default;
        hp->gc_data_per_heap.heap_index = unchecked((uint)hp->heap_number);
        if (hp->heap_number == 0)
        {
            gc_data_global = default;
        }

        hp->fgm_result = default;

        gc_history_per_heap* current_gc_data_per_heap =
            (gc_history_per_heap*)Unsafe.AsPointer(ref hp->gc_data_per_heap);
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            ref gc_generation_data gen_data =
                ref gc_history_per_heap.gen_data(current_gc_data_per_heap, i);
            gen_data.size_before = generation_size(hp, i);
            generation* gen = generation_of(generation_table_of(hp), i);
            gen_data.free_list_space_before = generation.generation_free_list_space(gen);
            gen_data.free_obj_space_before = generation.generation_free_obj_space(gen);
        }

        hp->end_gen0_region_space = uninitialized_end_gen0_region_space;
        hp->end_gen0_region_committed_space = 0;
        hp->gen0_pinned_free_space = 0;
        hp->gen0_large_chunk_found = false;
        hp->num_regions_freed_in_sweep = 0;

        hp->sufficient_gen0_space_p = 0;
    }
}

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
