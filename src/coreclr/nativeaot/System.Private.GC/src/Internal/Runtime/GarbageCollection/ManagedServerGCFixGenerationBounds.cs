// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Server plan-time region-threading family that repairs the generation region chains after the plug
// walk, translated from the SVR-namespace compilation of plan_phase.cpp, allocation.cpp, and
// background.cpp for the active x64 Linux SERVER_GC -> MULTIPLE_HEAPS -> DYNAMIC_HEAP_COUNT ->
// USE_REGIONS feature chain. This is the closed family the plan_phase driver calls (through
// fix_generation_bounds) once the condemned generations' regions have their planned generation
// numbers and plan-allocated tails set: it converts the planned layout into the final region chains.
//
//   * find_first_valid_region walks a generation's region chain from a starting region, returning the
//     next non-empty region while, in the process, returning every empty region it crosses to the
//     free-region pool, setting the surviving region's plan/gen numbers, decommitting the tail of a
//     gen2+ region, and threading a swept-in-plan region's saved free list back onto its generation.
//   * thread_final_regions rebuilds every generation's start/tail region links from the planned
//     layout: it returns the reserved SIP regions, seeds the non-condemned generations with their
//     existing head/tail, threads each condemned region onto its planned generation via
//     find_first_valid_region, null-terminates every tail, gets a fresh region for any generation that
//     ended up empty, and resets each condemned generation's allocation pointers. When
//     should_update_end_mark_size() is true it also accumulates this heap's post-mark max_generation
//     size into background_soh_size_end_mark for the BGC end-mark accounting.
//   * fix_generation_bounds finalizes the ephemeral-GC older-generation region allocated tails, runs
//     thread_final_regions for a compacting GC, and re-points the ephemeral heap segment / alloc
//     pointers at the (planned) gen0 start region.
//   * reset_allocation_pointers / set_allocation_heap_segment clear a generation's allocation
//     pointer/limit and re-seat its allocation segment at the (rw) start region.
//   * should_update_end_mark_size reports whether a background gen1 planning phase should record the
//     end-mark SOH size.
//
// gcpriv.h scoping is preserved. special_sweep_p / new_regions_in_threading /
// reserved_free_regions_sip are PER_HEAP_FIELD_SINGLE_GC, so they are instance-owned in the
// MULTIPLE_HEAPS build and reached through the gc_heap* parameter here; background_soh_size_end_mark
// is PER_HEAP_FIELD_DIAG_ONLY, likewise instance-owned, so the BGC end-mark accounting accumulates
// into hp->background_soh_size_end_mark. should_update_end_mark_size is PER_HEAP_ISOLATED_METHOD, so
// it stays static, reading the shared settings and the shared current_c_gc_state
// (ManagedServerGCBackgroundState.cs). ephemeral_heap_segment / alloc_allocated are PER_HEAP and
// reached through hp. thread_start_region (ManagedServerGCMarkPhase.cs), the region-flag / free-region
// leaves (check_seg_gen_num / get_plan_gen_num / set_region_plan_gen_num / set_region_gen_num /
// clear_region_sweep_in_plan / clear_region_demoted / decommit_heap_segment_pages / return_free_region
// / get_free_region in GCRegionsSegments.cs), allocator.thread_sip_fl (GCPriv.cs), and the generation
// / heap_segment accessors are reused as-is.
//
// No collection is routed by this slice: the plan_phase driver that calls fix_generation_bounds (the
// plug walk that establishes the planned layout it consumes, the LOH compaction gating, the plan-phase
// gc_joins), and the relocate / compact / make_free_lists execution that follows all remain deferred,
// so nothing here runs against a live heap yet.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    // allocation.cpp set_allocation_heap_segment: re-seat the generation's allocation segment at its
    // (rw) start region.
    public static void set_allocation_heap_segment(generation* gen)
    {
        generation.generation_allocation_segment(gen) =
            heap_segment_rw(generation.generation_start_segment(gen));
    }

    // allocation.cpp reset_allocation_pointers: clear the generation's allocation pointer / limit and
    // re-seat its allocation segment. Under USE_REGIONS generation_allocation_start is not set (the
    // #ifndef USE_REGIONS assignment is dropped), so start is unused.
    public static void reset_allocation_pointers(generation* gen, byte* start)
    {
        _ = start;
        generation.generation_allocation_pointer(gen) = null;
        generation.generation_allocation_limit(gen) = null;
        set_allocation_heap_segment(gen);
    }

#if BACKGROUND_GC
    // background.cpp should_update_end_mark_size: a background gen1 planning phase records the end-mark
    // SOH size. PER_HEAP_ISOLATED_METHOD, so it stays static and reads the shared settings and the
    // shared current_c_gc_state.
    public static bool should_update_end_mark_size()
    {
        return settings.condemned_generation == ((int)gc_generation_num.max_generation - 1) &&
               current_c_gc_state == c_gc_state.c_gc_state_planning;
    }
#endif

    // plan_phase.cpp find_first_valid_region: return the first non-empty region from region, returning
    // every empty region crossed to the free-region pool and fixing up the surviving region's
    // plan/gen numbers, decommit, and swept-in-plan free-list handoff along the way.
    public static heap_segment* find_first_valid_region(
        gc_heap* hp,
        heap_segment* region,
        bool compact_p,
        int* num_returned_regions)
    {
        generation* generation_table = generation_table_of(hp);
        check_seg_gen_num(
            generation.generation_allocation_segment(
                generation_of(generation_table, (int)gc_generation_num.max_generation)));

        if (region is null)
        {
            return null;
        }

        heap_segment* current_region = region;

        do
        {
            int gen_num = heap_segment.heap_segment_gen_num(current_region);
            int plan_gen_num;
            if (compact_p)
            {
                Debug.Assert(settings.compaction != 0);
                plan_gen_num = heap_segment.heap_segment_plan_gen_num(current_region);
            }
            else
            {
                plan_gen_num =
                    hp->special_sweep_p ? gen_num : get_plan_gen_num(gen_num);
            }

            byte* allocated = compact_p
                ? heap_segment.heap_segment_plan_allocated(current_region)
                : heap_segment.heap_segment_allocated(current_region);
            if (heap_segment.heap_segment_mem(current_region) == allocated)
            {
                heap_segment* region_to_delete = current_region;
                current_region = heap_segment.heap_segment_next(current_region);
                return_free_region(region_to_delete);
                (*num_returned_regions)++;

                if (current_region is null)
                {
                    return null;
                }
            }
            else
            {
                if (compact_p)
                {
                    if (heap_segment.heap_segment_swept_in_plan(current_region) != 0)
                    {
                        Debug.Assert(
                            heap_segment.heap_segment_allocated(current_region) ==
                            heap_segment.heap_segment_plan_allocated(current_region));
                    }
                    else
                    {
                        heap_segment.heap_segment_allocated(current_region) =
                            heap_segment.heap_segment_plan_allocated(current_region);
                    }
                }
                else
                {
                    set_region_plan_gen_num(current_region, plan_gen_num);
                }

                if (gen_num >= (int)gc_generation_num.soh_gen2)
                {
                    decommit_heap_segment_pages(current_region, 0, hp->heap_number);
                }

                set_region_gen_num(current_region, plan_gen_num);
                break;
            }
        }
        while (current_region is not null);

        Debug.Assert(current_region is not null);

        if (heap_segment.heap_segment_swept_in_plan(current_region) != 0)
        {
            int gen_num = heap_segment.heap_segment_gen_num(current_region);
            generation* gen = generation_of(generation_table, gen_num);
            allocator.thread_sip_fl(generation.generation_allocator(gen), current_region);
            generation.generation_free_list_space(gen) = unchecked(
                generation.generation_free_list_space(gen) +
                heap_segment.heap_segment_free_list_size(current_region));
            generation.generation_free_obj_space(gen) = unchecked(
                generation.generation_free_obj_space(gen) +
                heap_segment.heap_segment_free_obj_size(current_region));
        }

        clear_region_sweep_in_plan(current_region);
        clear_region_demoted(current_region);

        return current_region;
    }

    // plan_phase.cpp thread_final_regions: rebuild every generation's start/tail region chain from the
    // planned layout produced by the plug walk.
    public static void thread_final_regions(gc_heap* hp, bool compact_p)
    {
        int num_returned_regions = 0;
        int num_new_regions = 0;

        for (int i = 0; i < (int)gc_generation_num.max_generation; i++)
        {
            heap_segment* reserved_free_region = reserved_free_region_sip(hp, i);
            if (reserved_free_region is not null)
            {
                return_free_region(reserved_free_region);
            }
        }

        int condemned_gen_number = settings.condemned_generation;
        generation_region_info* generation_final_regions =
            stackalloc generation_region_info[GCInterfaceOffsets.max_generation + 1];
        for (int i = 0; i <= GCInterfaceOffsets.max_generation; i++)
        {
            generation_final_regions[i] = default;
        }

        generation* generation_table = generation_table_of(hp);

        // Step 1: seed the non-condemned generations with their current head / tail.
        for (int gen_idx = (int)gc_generation_num.max_generation;
             gen_idx > condemned_gen_number;
             gen_idx--)
        {
            generation* gen = generation_of(generation_table, gen_idx);
            generation_final_regions[gen_idx].head =
                heap_segment_rw(generation.generation_start_segment(gen));
            generation_final_regions[gen_idx].tail =
                generation.generation_tail_region(gen);
        }

#if BACKGROUND_GC
        heap_segment* max_gen_tail_region = null;
        if (should_update_end_mark_size())
        {
            max_gen_tail_region =
                generation_final_regions[(int)gc_generation_num.max_generation].tail;
        }
#endif

        // Step 2: thread each condemned region onto its planned generation.
        for (int gen_idx = condemned_gen_number; gen_idx >= 0; gen_idx--)
        {
            heap_segment* current_region =
                heap_segment_rw(
                    generation.generation_start_segment(
                        generation_of(generation_table, gen_idx)));

            while ((current_region = find_first_valid_region(
                hp,
                current_region,
                compact_p,
                &num_returned_regions)) is not null)
            {
                Debug.Assert(
                    !compact_p ||
                    heap_segment.heap_segment_plan_gen_num(current_region) ==
                    heap_segment.heap_segment_gen_num(current_region));
                int new_gen_num =
                    heap_segment.heap_segment_plan_gen_num(current_region);
                heap_segment* next_region =
                    heap_segment.heap_segment_next(current_region);

                if (generation_final_regions[new_gen_num].head is not null)
                {
                    Debug.Assert(
                        generation_final_regions[new_gen_num].tail is not null);
                    heap_segment.heap_segment_next(
                        generation_final_regions[new_gen_num].tail) =
                        current_region;
                    generation_final_regions[new_gen_num].tail =
                        current_region;
                }
                else
                {
                    generation_final_regions[new_gen_num].head =
                        current_region;
                    generation_final_regions[new_gen_num].tail =
                        current_region;
                }

                current_region = next_region;
            }
        }

        // Step 3: null-terminate every tail region.
        for (int gen_idx = 0;
             gen_idx <= (int)gc_generation_num.max_generation;
             gen_idx++)
        {
            if (generation_final_regions[gen_idx].tail is not null)
            {
                heap_segment.heap_segment_next(
                    generation_final_regions[gen_idx].tail) = null;
            }
        }

#if BACKGROUND_GC
        if (max_gen_tail_region is not null)
        {
            max_gen_tail_region = heap_segment.heap_segment_next(max_gen_tail_region);

            while (max_gen_tail_region is not null)
            {
                hp->background_soh_size_end_mark = unchecked(
                    hp->background_soh_size_end_mark +
                    (nuint)(heap_segment.heap_segment_allocated(max_gen_tail_region) -
                            heap_segment.heap_segment_mem(max_gen_tail_region)));

                max_gen_tail_region = heap_segment.heap_segment_next(max_gen_tail_region);
            }
        }
#endif

        // Step 4: set each generation's start region (getting a fresh one when empty) and reset the
        // condemned generations' allocation pointers.
        for (int gen_idx = 0;
             gen_idx <= (int)gc_generation_num.max_generation;
             gen_idx++)
        {
            bool condemned_p = gen_idx <= condemned_gen_number;
            Debug.Assert(
                condemned_p ||
                generation_final_regions[gen_idx].head is not null);

            generation* gen = generation_of(generation_table, gen_idx);
            heap_segment* start_region;

            if (generation_final_regions[gen_idx].head is not null)
            {
                start_region = generation_final_regions[gen_idx].head;
                if (condemned_p)
                {
                    thread_start_region(gen, start_region);
                }

                generation.generation_tail_region(gen) =
                    generation_final_regions[gen_idx].tail;
            }
            else
            {
                start_region = get_free_region(hp, gen_idx);
                Debug.Assert(start_region is not null);
                num_new_regions++;
                thread_start_region(gen, start_region);
            }

            if (condemned_p)
            {
                byte* gen_start = heap_segment.heap_segment_mem(start_region);
                reset_allocation_pointers(gen, gen_start);
            }
        }

        int net_added_regions = num_new_regions - num_returned_regions;
        if ((settings.compaction != 0 || hp->special_sweep_p) &&
            net_added_regions > 0)
        {
            hp->new_regions_in_threading += net_added_regions;
            Debug.Assert(false, "we shouldn't be getting new regions in TFR!");
        }
    }

    // plan_phase.cpp fix_generation_bounds (USE_REGIONS path): finalize the ephemeral-GC older
    // generation region allocated tails, rebuild the region chains, and re-seat the ephemeral heap
    // segment / alloc pointers at the planned gen0 start region.
    public static void fix_generation_bounds(
        gc_heap* hp,
        int condemned_gen_number,
        generation* consing_gen)
    {
        _ = consing_gen;

        if (settings.promotion != 0 &&
            condemned_gen_number < GCInterfaceOffsets.max_generation)
        {
            generation* older_gen = generation_of(
                generation_table_of(hp),
                condemned_gen_number + 1);
            heap_segment* last_alloc_region =
                generation.generation_allocation_segment(older_gen);
            for (heap_segment* region =
                    generation.generation_start_segment_rw(older_gen);
                 region is not null;
                 region = heap_segment.heap_segment_next(region))
            {
                heap_segment.heap_segment_allocated(region) =
                    heap_segment.heap_segment_plan_allocated(region);
                if (region == last_alloc_region)
                {
                    break;
                }
            }
        }

        thread_final_regions(hp, compact_p: true);

        generation* youngestGeneration =
            generation_of(generation_table_of(hp), 0);
        hp->ephemeral_heap_segment =
            generation.generation_start_segment(youngestGeneration);
        hp->alloc_allocated =
            heap_segment.heap_segment_plan_allocated(hp->ephemeral_heap_segment);
        heap_segment.heap_segment_allocated(hp->ephemeral_heap_segment) =
            heap_segment.heap_segment_plan_allocated(hp->ephemeral_heap_segment);
    }
}
#pragma warning restore CS8981

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
