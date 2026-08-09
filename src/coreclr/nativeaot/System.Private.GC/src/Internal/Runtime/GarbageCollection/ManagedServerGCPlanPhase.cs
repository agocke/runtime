// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Server plan-phase compaction-vs-sweep decision family, translated from the SVR-namespace
// compilation of plan_phase.cpp for the active x64 Linux
// SERVER_GC -> MULTIPLE_HEAPS -> DYNAMIC_HEAP_COUNT -> USE_REGIONS feature chain. This is the
// dependency-closed prefix of gc_heap::plan_phase that each server GC worker will run on its own
// heap to decide, once the per-heap plug/region planning has produced plan-allocated bounds,
// whether that heap compacts or sweeps its condemned generations:
//
//   * the per-heap plan-size / planned-fragmentation accounting (generation_plan_size,
//     generation_sizes, generation_fragmentation, approximate_new_allocation,
//     get_gen0_end_plan_space, and the pinned-plug-queue leaves pinned_plug_of / pinned_len that
//     generation_fragmentation consults), and
//   * the compaction-policy deciders themselves (decide_on_compaction_space,
//     is_full_compacting_gc_productive, decide_on_compacting, and the ensure_gap_allocation gate).
//
// gcpriv.h marks the plan-space accounting (num_regions_freed_in_sweep, end_gen0_region_space,
// end_gen0_region_committed_space, gen0_pinned_free_space, gen0_large_chunk_found,
// sufficient_gen0_space_p) and the pinned-plug queue (mark_stack_array / mark_stack_bos) as
// PER_HEAP_FIELD_SINGLE_GC, so they are instance-owned in the MULTIPLE_HEAPS build and every
// function here reads and writes them through the heap parameter; the deciders record their
// compaction mechanism through get_gc_data_per_heap(hp)->set_mechanism, exactly as the native
// per-heap methods do. The generation-size, hard-limit, sufficient-space, end-space, and
// fragmentation-threshold leaves the deciders consume are the ones already translated for server
// in ManagedServerGCCondemn.cs (generation_size, sufficient_space_regions, check_against_hard_limit,
// END_SPACE_AFTER_GC_FL, min_high_fragmentation_threshold, min_reclaim_fragmentation_threshold,
// get_gc_data_per_heap); the num_heaps divisor is n_heaps, not 1, so each heap decides against its
// share of the process budget.
//
// The STRESS_HEAP compaction force, the !USE_REGIONS ephemeral/low-ephemeral-space paths, and the
// !USE_REGIONS last_gc_before_oom reset are excluded exactly as for the active configuration. No
// collection is routed by this slice: the plug/region planning loop that fills in the plan-allocated
// bounds these deciders read, the gc_join_decide_on_compaction cross-heap join that consumes their
// result, and the relocate/compact/sweep execution that follows all remain deferred, so nothing here
// runs against a live heap yet.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    // The pinned plug queue leaves (mark_phase.cpp). mark_stack_array is PER_HEAP_FIELD_MAINTAINED,
    // so the queue is this heap's own; pinned_len aliases the plug's recorded free-space length.
    private static mark* pinned_plug_of(gc_heap* heap, nuint bos)
    {
        return &heap->mark_stack_array[bos];
    }

    private static ref nuint pinned_len(mark* m)
    {
        return ref m->len;
    }

    // The total of every condemned region's plan-allocated size (heap_segment_plan_allocated -
    // heap_segment_mem), used by the high-memory reclaim-space check in decide_on_compacting.
    private static nuint generation_plan_size(gc_heap* hp, int gen_number)
    {
        nuint result = 0;
        generation* generation_table = generation_table_of(hp);
        heap_segment* seg = heap_segment_rw(
            generation.generation_start_segment(generation_of(generation_table, gen_number)));

        while (seg is not null)
        {
            result += (nuint)(heap_segment.heap_segment_plan_allocated(seg) - heap_segment.heap_segment_mem(seg));
            seg = heap_segment.heap_segment_next(seg);
        }

        return result;
    }

    // For SOH this returns the total sizes of the generation and its younger generations; for LOH
    // and POH it returns just that generation's size. use_saved_p reads heap_segment_saved_allocated
    // (the pre-plan allocated bound) so decide_on_compacting measures against the surviving size.
    private static nuint generation_sizes(gc_heap* hp, generation* gen, bool use_saved_p = false)
    {
        nuint result = 0;
        int gen_num = gen->gen_num;
        int start_gen_index = gen_num > GCInterfaceOffsets.max_generation ? gen_num : 0;
        generation* generation_table = generation_table_of(hp);

        for (int i = start_gen_index; i <= gen_num; i++)
        {
            heap_segment* seg = heap_segment_in_range(
                generation.generation_start_segment(generation_of(generation_table, i)));

            while (seg is not null)
            {
                byte* end = use_saved_p
                    ? heap_segment.heap_segment_saved_allocated(seg)
                    : heap_segment.heap_segment_allocated(seg);
                result = unchecked(result + (nuint)(end - heap_segment.heap_segment_mem(seg)));
                seg = heap_segment_next_in_range(seg);
            }
        }

        return result;
    }

    // The planned fragmentation of the condemned generations: the sum of each region's saved-minus-
    // plan allocated gap plus the recorded pinned-plug free-space lengths.
    private static nuint generation_fragmentation(
        gc_heap* hp,
        generation* gen,
        generation* consing_gen,
        byte* end)
    {
        _ = consing_gen;
        _ = end;

        nint frag = 0;
        generation* generation_table = generation_table_of(hp);
        for (int gen_num = 0; gen_num <= gen->gen_num; gen_num++)
        {
            generation* current_gen = generation_of(generation_table, gen_num);
            heap_segment* seg = heap_segment_rw(generation.generation_start_segment(current_gen));
            while (seg is not null)
            {
                frag = unchecked(
                    frag +
                    (nint)(heap_segment.heap_segment_saved_allocated(seg) -
                           heap_segment.heap_segment_plan_allocated(seg)));
                seg = heap_segment_next_rw(seg);
            }
        }

        nuint bos = 0;
        while (bos < hp->mark_stack_bos)
        {
            frag = unchecked(frag + (nint)pinned_len(pinned_plug_of(hp, bos)));
            bos++;
        }

        return unchecked((nuint)frag);
    }

    // gen0's worth of new allocation to plan for after the GC: two minimum budgets or two-thirds of
    // the desired allocation, whichever is larger.
    private static nuint approximate_new_allocation(gc_heap* hp)
    {
        dynamic_data* dd0 = dynamic_data_of(hp, (int)gc_generation_num.soh_gen0);
        nuint twice_minimum = unchecked(2 * dynamic_data.dd_min_size(dd0));
        nuint desired_fraction = unchecked(dynamic_data.dd_desired_allocation(dd0) * 2) / 3;
        return twice_minimum > desired_fraction ? twice_minimum : desired_fraction;
    }

    // Sum the plan-space left at the end of the regions that plan to become gen0, tracking whether
    // any single region has a large enough end chunk to satisfy END_SPACE_AFTER_GC_FL.
    private static void get_gen0_end_plan_space(gc_heap* hp)
    {
        hp->end_gen0_region_space = 0;
        generation* generation_table = generation_table_of(hp);
        for (int gen_idx = settings.condemned_generation; gen_idx >= 0; gen_idx--)
        {
            generation* gen = generation_of(generation_table, gen_idx);
            heap_segment* region = heap_segment_rw(generation.generation_start_segment(gen));
            while (region is not null)
            {
                if (heap_segment.heap_segment_plan_gen_num(region) == 0)
                {
                    nuint end_plan_space = (nuint)(heap_segment.heap_segment_reserved(region) -
                                                    heap_segment.heap_segment_plan_allocated(region));
                    if (!hp->gen0_large_chunk_found)
                    {
                        hp->gen0_large_chunk_found = end_plan_space >= END_SPACE_AFTER_GC_FL;
                    }

                    hp->end_gen0_region_space += end_plan_space;
                }

                region = heap_segment.heap_segment_next(region);
            }
        }
    }

    // If we don't compact, is there enough region space for the next gen0 allocation? If not, decide
    // whether a compacting GC would recover a large enough contiguous chunk, and record the answer.
    private static bool decide_on_compaction_space(gc_heap* hp)
    {
        nuint gen0size = approximate_new_allocation(hp);
        nuint swept_region_space = unchecked(
            (nuint)hp->num_regions_freed_in_sweep * ((nuint)1 << (int)min_segment_size_shr));

        if (sufficient_space_regions(hp, swept_region_space, gen0size))
        {
            return false;
        }

        get_gen0_end_plan_space(hp);

        if (!hp->gen0_large_chunk_found)
        {
            hp->gen0_large_chunk_found =
                region_free_list.get_num_free_regions(
                    (region_free_list*)Unsafe.AsPointer(
                        ref hp->server_free_regions[(int)free_region_kind.basic_free_region])) > 0;
        }

        if (sufficient_space_regions(
                hp,
                unchecked(hp->gen0_pinned_free_space + hp->end_gen0_region_space),
                gen0size) &&
            hp->gen0_large_chunk_found)
        {
            hp->sufficient_gen0_space_p = 1;
        }

        return true;
    }

    // A full compacting GC is unproductive if gen1's start region was folded into gen2 or gen2's tail
    // region had to be extended, i.e. gen2 grew rather than shrank.
    private static bool is_full_compacting_gc_productive(gc_heap* hp)
    {
        generation* generation_table = generation_table_of(hp);
        heap_segment* gen1_start_region = generation.generation_start_segment(
            generation_of(generation_table, (int)gc_generation_num.soh_gen1));
        Debug.Assert(gen1_start_region is not null);
        if (heap_segment.heap_segment_plan_gen_num(gen1_start_region) ==
            (int)gc_generation_num.max_generation)
        {
            return false;
        }

        heap_segment* gen2_tail_region = generation.generation_tail_region(
            generation_of(generation_table, (int)gc_generation_num.max_generation));
        Debug.Assert(gen2_tail_region is not null);
        if (heap_segment.heap_segment_plan_allocated(gen2_tail_region) >=
            heap_segment.heap_segment_allocated(gen2_tail_region))
        {
            return false;
        }

        return true;
    }

    // Committing the memory for generation starts always succeeds under regions (there is no
    // ephemeral segment gap to reserve), so this is a constant TRUE gate, exactly as in native.
    private static bool ensure_gap_allocation(int condemned_gen_number)
    {
        _ = condemned_gen_number;
        return true;
    }

    // Decide whether this heap compacts (returns true) or sweeps its condemned generations, and
    // whether the ephemeral generations should be expanded, recording the compaction reason.
    private static bool decide_on_compacting(
        gc_heap* hp,
        int condemned_gen_number,
        nuint fragmentation,
        ref bool should_expand)
    {
        Debug.Assert(settings.concurrent == 0);

        bool should_compact = false;
        should_expand = false;
        generation* gen = generation_of(generation_table_of(hp), condemned_gen_number);
        dynamic_data* dd = dynamic_data_of(hp, condemned_gen_number);
        nuint gen_sizes = generation_sizes(hp, gen, use_saved_p: true);
        float fragmentation_burden = fragmentation == 0 || gen_sizes == 0
            ? 0.0f
            : (float)fragmentation / (float)gen_sizes;

        if (special_sweep_p)
        {
            return false;
        }

        if (GCConfig.GetForceCompact() != 0)
        {
            should_compact = true;
        }

        if (condemned_gen_number == (int)gc_generation_num.max_generation &&
            hp->last_gc_before_oom != 0)
        {
            should_compact = true;
            get_gc_data_per_heap(hp)->set_mechanism(
                gc_mechanism_per_heap.gc_heap_compact,
                (uint)gc_heap_compact_reason.compact_last_gc);
        }

        if (settings.reason == gc_reason.reason_induced_compacting)
        {
            should_compact = true;
            get_gc_data_per_heap(hp)->set_mechanism(
                gc_mechanism_per_heap.gc_heap_compact,
                (uint)gc_heap_compact_reason.compact_induced_compacting);
        }

        if (settings.reason == gc_reason.reason_induced_aggressive)
        {
            should_compact = true;
            get_gc_data_per_heap(hp)->set_mechanism(
                gc_mechanism_per_heap.gc_heap_compact,
                (uint)gc_heap_compact_reason.compact_aggressive_compacting);
        }

        if (settings.reason == gc_reason.reason_pm_full_gc)
        {
            Debug.Assert(condemned_gen_number == (int)gc_generation_num.max_generation);
            should_compact = true;
        }

        if (provisional_mode_triggered &&
            condemned_gen_number == (int)gc_generation_num.soh_gen1)
        {
            should_compact = true;
        }

        if (!should_compact)
        {
            should_compact = decide_on_compaction_space(hp);
        }

#if TARGET_64BIT
        bool high_memory = false;
#endif

        if (!should_compact)
        {
            bool frag_exceeded =
                fragmentation >= dynamic_data.dd_fragmentation_limit(dd) &&
                fragmentation_burden >= dynamic_data.dd_fragmentation_burden_limit(dd);

            if (frag_exceeded)
            {
#if BACKGROUND_GC
                Debug.Assert(settings.concurrent == 0);
#endif
                should_compact = true;
                get_gc_data_per_heap(hp)->set_mechanism(
                    gc_mechanism_per_heap.gc_heap_compact,
                    (uint)gc_heap_compact_reason.compact_high_frag);
            }

#if TARGET_64BIT
            if (!should_compact)
            {
                uint num_heaps = (uint)n_heaps;
                nint reclaim_space = unchecked((nint)(
                    generation_size(hp, (int)gc_generation_num.max_generation) -
                    generation_plan_size(hp, (int)gc_generation_num.max_generation)));

                if (settings.entry_memory_load >= high_memory_load_th &&
                    settings.entry_memory_load < v_high_memory_load_th)
                {
                    if (reclaim_space > unchecked((nint)min_high_fragmentation_threshold(
                            settings.entry_available_physical_mem,
                            num_heaps)))
                    {
                        should_compact = true;
                        get_gc_data_per_heap(hp)->set_mechanism(
                            gc_mechanism_per_heap.gc_heap_compact,
                            (uint)gc_heap_compact_reason.compact_high_mem_frag);
                    }

                    high_memory = true;
                }
                else if (settings.entry_memory_load >= v_high_memory_load_th)
                {
                    if (reclaim_space > unchecked((nint)min_reclaim_fragmentation_threshold(
                            hp,
                            num_heaps)))
                    {
                        should_compact = true;
                        get_gc_data_per_heap(hp)->set_mechanism(
                            gc_mechanism_per_heap.gc_heap_compact,
                            (uint)gc_heap_compact_reason.compact_vhigh_mem_frag);
                    }

                    high_memory = true;
                }
            }
#endif
        }

        if (!should_compact && !ensure_gap_allocation(condemned_gen_number))
        {
            should_compact = true;
            get_gc_data_per_heap(hp)->set_mechanism(
                gc_mechanism_per_heap.gc_heap_compact,
                (uint)gc_heap_compact_reason.compact_no_gaps);
        }

        if (settings.condemned_generation == (int)gc_generation_num.max_generation)
        {
            if (
#if TARGET_64BIT
                (high_memory && !should_compact) ||
#endif
                !is_full_compacting_gc_productive(hp))
            {
                settings.should_lock_elevation = 1;
            }
        }

        if (settings.pause_mode == gc_pause_mode.pause_no_gc)
        {
            should_compact = true;
            heap_segment* ephemeral_segment = hp->ephemeral_heap_segment;
            Debug.Assert(ephemeral_segment is not null);
            if ((nuint)(
                    heap_segment.heap_segment_reserved(ephemeral_segment) -
                    heap_segment.heap_segment_plan_allocated(ephemeral_segment)) <
                soh_allocation_no_gc)
            {
                should_expand = true;
            }
        }

        return should_compact;
    }
}

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
