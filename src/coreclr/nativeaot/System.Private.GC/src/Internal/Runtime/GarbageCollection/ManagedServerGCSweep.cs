// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Server SOH mark-and-sweep execution, translated from the SVR-namespace compilation of sweep.cpp for
// the active x64 Linux SERVER_GC -> MULTIPLE_HEAPS -> DYNAMIC_HEAP_COUNT -> USE_REGIONS feature chain.
// This is gc_heap::make_free_lists (and the make_free_list_in_brick brick-tree walk it drives) that
// every server GC worker runs on its own heap when the plan phase decides to sweep rather than compact.
// It is the sweep-branch counterpart of the already-translated server compact_phase execution
// (ManagedServerGCCompact.cs) and relocate_phase (ManagedServerGCRelocate.cs).
//
//   * make_free_lists walks each condemned generation's regions in address order. For every brick that
//     roots a plug tree it calls make_free_list_in_brick, which threads each inter-plug gap onto its
//     planned free list, then it fixes the brick-table entry to the highest plug processed (or -1 for
//     an empty brick). Once every region is processed it re-threads the final region layout
//     (thread_final_regions(false)) and resets this heap's ephemeral segment / alloc_allocated.
//   * make_free_list_in_brick recurses left, threads the plug's preceding gap (clearing the SHORT_PLUGS
//     pad bit and the DOUBLY_LINKED_FL bgc-mark / free-obj-in-compact bits), then recurses right.
//
// Ownership follows gcpriv.h. special_sweep_p is PER_HEAP_FIELD_SINGLE_GC (instance-owned in the
// MULTIPLE_HEAPS build), so make_free_lists reaches it through hp->special_sweep_p; ephemeral_heap_segment
// and alloc_allocated are per-heap and reached through hp. brick_table / set_brick / brick_address are a
// single process-wide array (matching native under USE_REGIONS, where every heap's brick_table pointer
// aliases the same backing), so make_free_list_in_brick, which touches no per-heap state, stays static.
// thread_gap / uoh_thread_gap_front (ManagedServerGCPlanSweep.cs), make_unused_array / Align / size /
// get_alignment_constant (ManagedServerGC.cs), get_stop_generation_index (GCPriv.cs), generation_of /
// generation_table_of / get_soh_start_object / get_plan_gen_num / check_seg_gen_num / brick_of /
// brick_address / set_brick / heap_segment_next_non_sip (GCRegionsSegments.cs / ManagedServerGC.cs),
// get_start_segment (ManagedServerGCCompact.cs), thread_final_regions (ManagedServerGCFixGenerationBounds.cs),
// node_left_child / node_right_child / node_gap_size / make_free_args (GCPriv.cs), is_plug_padded
// (ManagedServerGCPlanDriver.cs), clear_plug_padded (ManagedServerGCPlanCondemned.cs), and the
// DOUBLY_LINKED_FL bit accessors (ManagedServerGCCompact.cs) are all reused as-is.
//
// FREE_USAGE_STATS is not defined for this port, so add_gen_free is a no-op and is omitted. SHORT_PLUGS
// is active, so the pad bit is cleared. The !USE_REGIONS gen-crossing / current_gen_limit / empty-start
// segment handling and allocate_at_end generation-start fixups are excluded exactly as native does for
// USE_REGIONS.
//
// No collection is routed by this slice. make_free_lists is wired into the plan_phase driver's sweep
// branch (ManagedServerGCPlanDriver.cs) alongside recover_saved_pinned_info, the
// gc_join_adjust_handle_age_sweep join, GcPromotionsGranted, UpdatePromotedGenerations, and
// clear_gen1_cards, but the plan_phase driver itself is still not reached by any collection entry point.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    public static void make_free_lists(gc_heap* hp, int condemned_gen_number)
    {
        Debug.Assert(settings.promotion != 0);

        make_free_args args = default;
        int stop_gen_idx = get_stop_generation_index(condemned_gen_number);
        generation* generation_table = generation_table_of(hp);
        for (int i = condemned_gen_number; i >= stop_gen_idx; i--)
        {
            generation* condemned_gen = generation_of(generation_table, i);
            heap_segment* current_heap_segment = get_start_segment(condemned_gen);
            if (current_heap_segment is null)
            {
                continue;
            }

            byte* start_address = get_soh_start_object(current_heap_segment, condemned_gen);
            nuint current_brick = brick_of(start_address);

            Debug.Assert(current_heap_segment is not null);

            byte* end_address = heap_segment.heap_segment_allocated(current_heap_segment);
            nuint end_brick = brick_of(end_address - 1);

            int current_gen_num = i;
            args.free_list_gen_number =
                hp->special_sweep_p ? current_gen_num : get_plan_gen_num(current_gen_num);
            args.free_list_gen = generation_of(generation_table, args.free_list_gen_number);
            args.highest_plug = null;

            while (true)
            {
                if (current_brick > end_brick)
                {
                    if (heap_segment_next_non_sip(current_heap_segment) is not null)
                    {
                        current_heap_segment = heap_segment_next_non_sip(current_heap_segment);
                    }
                    else
                    {
                        break;
                    }

                    current_brick = brick_of(heap_segment.heap_segment_mem(current_heap_segment));
                    end_brick = brick_of(heap_segment.heap_segment_allocated(current_heap_segment) - 1);
                    continue;
                }

                int brick_entry = brick_table[(nint)current_brick];
                if (brick_entry >= 0)
                {
                    make_free_list_in_brick(brick_address(current_brick) + brick_entry - 1, &args);
                    set_brick(current_brick, unchecked((nint)(args.highest_plug - brick_address(current_brick))));
                }
                else if (brick_entry > short.MinValue)
                {
#if DEBUG
                    nint offset = (nint)brick_of(args.highest_plug) - (nint)current_brick;
                    if ((brick_entry != -32767) && (offset != brick_entry))
                    {
                        Debug.Assert(brick_entry == -1);
                    }
#endif
                    set_brick(current_brick, -1);
                }

                current_brick++;
            }
        }

        check_seg_gen_num(
            generation.generation_allocation_segment(
                generation_of(generation_table, (int)gc_generation_num.max_generation)));

        thread_final_regions(hp, compact_p: false);

        generation* gen_gen0 = generation_of(generation_table, 0);
        hp->ephemeral_heap_segment = generation.generation_start_segment(gen_gen0);
        hp->alloc_allocated =
            heap_segment.heap_segment_allocated(hp->ephemeral_heap_segment);
    }

    public static void make_free_list_in_brick(byte* tree, make_free_args* args)
    {
        Debug.Assert(tree is not null);
        {
            short right_node = node_right_child(tree);
            short left_node = node_left_child(tree);
            args->highest_plug = null;
            if (tree is not null)
            {
                if (left_node != 0)
                {
                    make_free_list_in_brick(tree + left_node, args);
                }

                {
                    byte* plug = tree;
                    nuint gap_size = node_gap_size(tree);
                    byte* gap = plug - (nint)gap_size;
                    args->highest_plug = tree;

                    if (is_plug_padded(plug) != 0)
                    {
                        clear_plug_padded(plug);
                    }

#if TARGET_64BIT && !TARGET_WASM
                    if (is_plug_bgc_mark_bit_set(plug) != 0)
                    {
                        clear_plug_bgc_mark_bit(plug);
                    }

                    if (is_free_obj_in_compact_bit_set(plug) != 0)
                    {
                        clear_free_obj_in_compact_bit(plug);
                    }
#endif

                    thread_gap(gap, gap_size, args->free_list_gen);
                }

                if (right_node != 0)
                {
                    make_free_list_in_brick(tree + right_node, args);
                }
            }
        }
    }
}

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
