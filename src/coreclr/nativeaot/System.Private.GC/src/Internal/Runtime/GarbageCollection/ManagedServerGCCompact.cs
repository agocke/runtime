// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Server compact-phase family, translated from the SVR-namespace compilation of relocate_compact.cpp
// for the active x64 Linux SERVER_GC -> MULTIPLE_HEAPS -> DYNAMIC_HEAP_COUNT -> USE_REGIONS feature
// chain. This is gc_heap::compact_phase (and the SOH / LOH compaction execution it drives) that every
// server GC worker runs on its own heap once relocate_phase has fixed every surviving reference. It is
// the counterpart of the already-translated server relocate_phase (ManagedServerGCRelocate.cs).
//
//   * compact_phase opens with the gc_join_relocate_phase_done join so every worker finishes relocating
//     before any worker starts moving objects (the joined worker runs the FEATURE_EVENT_TRACE timing
//     capture -- omitted with the deferred server event integration -- and restarts). It then compacts
//     the LOH plan (compact_loh, when this heap's per-GC loh_compacted_p is set), walks each condemned
//     SOH region's brick compaction tree (compact_in_brick / compact_plug), recovers the saved
//     pinned-plug info (recover_saved_pinned_info), and finalizes each region's used pointer.
//   * compact_in_brick walks a brick's plug tree in address order, threading the pinned-plug queue as
//     it reaches each oldest pin, and hands each plug to compact_plug.
//   * compact_plug moves a plug to its planned location through gcmemcopy, repairs the alignment/pad
//     unused arrays, and rethreads the destination brick table.
//   * gcmemcopy is the copy primitive: it copies the mark bits during a background mark
//     (copy_mark_bits_for_addresses), consumes the DOUBLY_LINKED_FL bgc-mark / free-obj-in-compact
//     bits, memcopies the plug, re-marks the moved objects for an in-progress background GC, sets the
//     software-write-watch dirty region, and copies or clears the cards (copy_cards_range).
//   * compact_loh slides every marked LOH object to its planned location, threading the pad gaps and
//     trimming / freeing emptied segments.
//
// Ownership follows gcpriv.h. loh_compacted_p (PER_HEAP_FIELD_SINGLE_GC), the pinned-plug queue
// (mark_stack_array etc.), the LOH pinned queue (loh_pinned_queue*), oldest_pinned_plug, and
// freeable_uoh_segment (PER_HEAP_FIELD_MAINTAINED) are instance-owned in the MULTIPLE_HEAPS build, so
// every compact routine reaches them through its gc_heap* parameter. brick_table / set_brick /
// brick_address are a single process-wide array (matching native under USE_REGIONS, where every heap's
// brick_table pointer aliases the same backing), so they stay static, as do gcmemcopy's
// current_c_gc_state and background_saved_lowest/highest_address (PER_HEAP_ISOLATED). expand_reused_seg_p
// returns FALSE under USE_REGIONS, so args.check_gennum_p is always 0 (asserted) and the reused-segment
// generation-attribution branch of compact_plug is dead exactly as native.
//
// The FEATURE_EVENT_TRACE loh_compact_info timing (BeginLohCompact / EndLohCompact) is omitted, matching
// the deferred server event integration and native's !informational_event_enabled_p path.
// FEATURE_STRUCTALIGN (node_alignpad) is not defined for this port. DOUBLY_LINKED_FL and BACKGROUND_GC
// are defined (TARGET_64BIT, non-WASM full runtime), so gcmemcopy's mark-bit / free-obj-in-compact
// handling is translated in full; because the server background collector is not yet routed,
// current_c_gc_state is c_gc_state_free at compaction time, so the copy_mark_bits_for_addresses and
// background_mark branches are inert at runtime but engage unchanged once server BGC lands.
//
// clear_unused_bricks_after_compaction mirrors the WKS translation of the compact_phase USE_REGIONS
// tail: after fixing each region's used pointer it clears the brick-table entries past the region's
// plan-allocated tail so a stale brick can never be walked.
//
// No collection is routed by this slice. compact_phase is wired into the plan_phase driver's
// should_compact branch (ManagedServerGCPlanDriver.cs) alongside relocate_phase / fix_generation_bounds
// and the gc_join_adjust_handle_age_compact tail, but the plan_phase driver itself is still not reached
// by any collection entry point, and the sweep branch (make_free_lists) remains deferred.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    // relocate_compact.cpp memcopy: copy len bytes in pointer-sized groups.
    public static void memcopy(byte* dmem, byte* smem, nuint size)
    {
        nuint sz4ptr = (nuint)sizeof(void*) * 4;
        nuint sz2ptr = (nuint)sizeof(void*) * 2;
        nuint sz1ptr = (nuint)sizeof(void*) * 1;

        Debug.Assert((size & ((nuint)sizeof(void*) - 1)) == 0);
        Debug.Assert(sizeof(void*) == GCEnv.DATA_ALIGNMENT);

        if (size >= sz4ptr)
        {
            do
            {
                ((nuint*)dmem)[0] = ((nuint*)smem)[0];
                ((nuint*)dmem)[1] = ((nuint*)smem)[1];
                ((nuint*)dmem)[2] = ((nuint*)smem)[2];
                ((nuint*)dmem)[3] = ((nuint*)smem)[3];
                dmem += (int)sz4ptr;
                smem += (int)sz4ptr;
            }
            while ((size -= sz4ptr) >= sz4ptr);
        }

        if ((size & sz2ptr) != 0)
        {
            ((nuint*)dmem)[0] = ((nuint*)smem)[0];
            ((nuint*)dmem)[1] = ((nuint*)smem)[1];
            dmem += (int)sz2ptr;
            smem += (int)sz2ptr;
        }

        if ((size & sz1ptr) != 0)
        {
            ((nuint*)dmem)[0] = ((nuint*)smem)[0];
        }
    }

    // gcpriv.h expand_reused_seg_p: FALSE under USE_REGIONS.
    public static bool expand_reused_seg_p()
    {
        return false;
    }

    // gcinternal.h DOUBLY_LINKED_FL plug bit accessors (TARGET_64BIT, non-WASM).
    public static int is_plug_bgc_mark_bit_set(byte* node)
    {
        return ((CObjectHeader*)node)->IsBGCMarkBitSet();
    }

    public static void clear_plug_bgc_mark_bit(byte* node)
    {
        ((CObjectHeader*)node)->ClearBGCMarkBit();
    }

    public static int is_free_obj_in_compact_bit_set(byte* node)
    {
        return ((CObjectHeader*)node)->IsFreeObjInCompactBitSet();
    }

    public static void clear_free_obj_in_compact_bit(byte* node)
    {
        ((CObjectHeader*)node)->ClearFreeObjInCompactBit();
    }

    // background.cpp gc_heap::mark_array_set_marked / background_mark1 / background_mark: set an object's
    // background mark bit when it falls inside the background range.
    public static void mark_array_set_marked(byte* add)
    {
        nuint index = card_table_info.mark_word_of(add);
        uint val = 1u << (int)card_table_info.mark_bit_bit_of(add);
        // MULTIPLE_HEAPS: mark_array is shared across heaps under USE_REGIONS, so the set is atomic.
        Interlocked.Or(&mark_array[(nint)index], val);
    }

    public static int background_mark1(byte* o)
    {
        int to_mark = mark_array_marked(o) == 0 ? 1 : 0;
        if (to_mark != 0)
        {
            mark_array_set_marked(o);
            return 1;
        }

        return 0;
    }

    public static int background_mark(byte* o, byte* low, byte* high)
    {
        int marked = 0;
        if ((o >= low) && (o < high))
        {
            marked = background_mark1(o);
        }

        return marked;
    }

    // relocate_compact.cpp gc_heap::copy_cards_range: copy the cards over the moved range, or clear
    // them when the destination is not tracking cross-generation references.
    public static void copy_cards_range(byte* dest, byte* src, nuint len, bool copy_cards_p)
    {
        if (copy_cards_p)
        {
            copy_cards_for_addresses(dest, src, len);
        }
        else
        {
            clear_card_for_addresses(dest, dest + (nint)len);
        }
    }

    // card_table.cpp gc_heap::copy_mark_bits_for_addresses: carry each surviving object's background
    // mark bit from src to dest as gcmemcopy moves the plug during a concurrent mark.
    public static void copy_mark_bits_for_addresses(byte* dest, byte* src, nuint len)
    {
        byte* src_o = src;
        byte* src_end = src + (nint)len;
        nint reloc = unchecked((nint)(dest - src));
        int align_const = get_alignment_constant(small_object_p: true);

        while (src_o < src_end)
        {
            byte* next_o = src_o + (nint)Align(size(src_o), align_const);
            if (background_object_marked(src_o, clear_p: true))
            {
                background_mark(
                    src_o + reloc,
                    background_saved_lowest_address,
                    background_saved_highest_address);
            }

            src_o = next_o;
        }
    }

    // relocate_compact.cpp gc_heap::gcmemcopy: move a plug and carry the bookkeeping (mark bits, cards,
    // write-watch) with it.
    public static void gcmemcopy(byte* dest, byte* src, nuint len, int copy_cards_p)
    {
        if (dest != src)
        {
            if (current_c_gc_state == c_gc_state.c_gc_state_marking)
            {
                copy_mark_bits_for_addresses(dest, src, len);
            }

            int set_bgc_mark_bits_p = is_plug_bgc_mark_bit_set(src);
            if (set_bgc_mark_bits_p != 0)
            {
                clear_plug_bgc_mark_bit(src);
            }

            int make_free_obj_p = 0;
            if (len <= min_free_item_no_prev)
            {
                make_free_obj_p = is_free_obj_in_compact_bit_set(src);

                if (make_free_obj_p != 0)
                {
                    clear_free_obj_in_compact_bit(src);
                }
            }

            memcopy(
                dest - (nint)plug_skew,
                src - (nint)plug_skew,
                len);

            if (set_bgc_mark_bits_p != 0)
            {
                byte* dest_o = dest;
                byte* dest_end_o = dest + (nint)len;
                while (dest_o < dest_end_o)
                {
                    byte* next_o = dest_o + (nint)Align(size(dest_o));
                    background_mark(
                        dest_o,
                        background_saved_lowest_address,
                        background_saved_highest_address);

                    dest_o = next_o;
                }
            }

            if (make_free_obj_p != 0)
            {
                nuint* filler_free_obj_size_location =
                    (nuint*)(dest + (nint)min_free_item_no_prev);
                nuint filler_free_obj_size = *filler_free_obj_size_location;
                make_unused_array(dest + (nint)len, filler_free_obj_size);
            }

#if FEATURE_USE_SOFTWARE_WRITE_WATCH_FOR_GC_HEAP
            if (SoftwareWriteWatch.IsEnabledForGCHeap())
            {
                SoftwareWriteWatch.SetDirtyRegion(dest, len - plug_skew);
            }
#endif
            copy_cards_range(dest, src, len, copy_cards_p != 0);
        }
    }

    // relocate_compact.cpp gc_heap::compact_plug: move one plug to its planned location and repair the
    // brick table so a subsequent walk finds it there.
    public static void compact_plug(
        gc_heap* hp,
        byte* plug,
        nuint size,
        int check_last_object_p,
        compact_args* args)
    {
        byte* reloc_plug = plug + args->last_plug_relocation;

        if (check_last_object_p != 0)
        {
            size += (nuint)sizeof(gap_reloc_pair);
            mark* entry = args->pinned_plug_entry;

            if (args->is_shortened != 0)
            {
                Debug.Assert(mark.has_post_plug_info(entry) != 0);
                mark.swap_post_plug_and_saved(entry);
            }
            else
            {
                Debug.Assert(mark.has_pre_plug_info(entry) != 0);
                mark.swap_pre_plug_and_saved(entry);
            }
        }

        int old_brick_entry = brick_table[(nint)brick_of(plug)];
        _ = old_brick_entry;

        Debug.Assert(node_relocation_distance(plug) == args->last_plug_relocation);

        nuint unused_arr_size = 0;
        int already_padded_p = 0;
        if (is_plug_padded(plug) != 0)
        {
            already_padded_p = 1;
            clear_plug_padded(plug);
            unused_arr_size = Align((nuint)GCInterfaceOffsets.min_obj_size);
        }

        if (node_realigned(plug) != 0)
        {
            unused_arr_size += switch_alignment_size(already_padded_p);
        }

        if (unused_arr_size != 0)
        {
            make_unused_array(reloc_plug - (nint)unused_arr_size, unused_arr_size);

            if (brick_of(reloc_plug - (nint)unused_arr_size) != brick_of(reloc_plug))
            {
                fix_brick_to_highest(reloc_plug - (nint)unused_arr_size, reloc_plug);
            }
        }

        if (is_plug_padded(plug) != 0)
        {
            nuint aligned_min_obj_size = Align((nuint)GCInterfaceOffsets.min_obj_size);
            make_unused_array(reloc_plug - (nint)aligned_min_obj_size, aligned_min_obj_size);

            if (brick_of(reloc_plug - (nint)aligned_min_obj_size) != brick_of(reloc_plug))
            {
                fix_brick_to_highest(reloc_plug - (nint)aligned_min_obj_size, reloc_plug);
            }
        }

        gcmemcopy(reloc_plug, plug, size, args->copy_cards_p);

        if (args->check_gennum_p != 0)
        {
            int src_gennum = args->src_gennum;
            if (src_gennum == -1)
            {
                src_gennum = object_gennum(plug);
            }

            int dest_gennum = object_gennum_plan(reloc_plug);

            if (src_gennum < dest_gennum)
            {
                generation.generation_allocation_size(
                    generation_of(generation_table_of(hp), dest_gennum)) += size;
            }
        }

        nuint current_reloc_brick = args->current_compacted_brick;

        if (brick_of(reloc_plug) != current_reloc_brick)
        {
            if (args->before_last_plug is not null)
            {
                set_brick(
                    current_reloc_brick,
                    (nint)(args->before_last_plug - brick_address(current_reloc_brick)));
            }

            current_reloc_brick = brick_of(reloc_plug);
        }

        nuint end_brick = brick_of(reloc_plug + (nint)size - 1);
        if (end_brick != current_reloc_brick)
        {
            set_brick(
                current_reloc_brick,
                (nint)(reloc_plug - brick_address(current_reloc_brick)));

            nuint brick = current_reloc_brick + 1;
            while (brick < end_brick)
            {
                set_brick(brick, -1);
                brick++;
            }

            args->before_last_plug = brick_address(end_brick) - 1;
            current_reloc_brick = end_brick;
        }
        else
        {
            args->before_last_plug = reloc_plug;
        }

        args->current_compacted_brick = current_reloc_brick;

        if (check_last_object_p != 0)
        {
            mark* entry = args->pinned_plug_entry;

            if (args->is_shortened != 0)
            {
                mark.swap_post_plug_and_saved(entry);
            }
            else
            {
                mark.swap_pre_plug_and_saved(entry);
            }
        }
    }

    // relocate_compact.cpp gc_heap::compact_in_brick: walk a brick's plug tree in address order and
    // compact each plug, threading the pinned-plug queue as each oldest pin is reached.
    public static void compact_in_brick(gc_heap* hp, byte* tree, compact_args* args)
    {
        Debug.Assert(tree is not null);
        int left_node = node_left_child(tree);
        int right_node = node_right_child(tree);
        nint relocation = node_relocation_distance(tree);

        if (left_node != 0)
        {
            compact_in_brick(hp, tree + left_node, args);
        }

        byte* plug = tree;
        int has_pre_plug_info_p = 0;
        int has_post_plug_info_p = 0;

        if (tree == hp->oldest_pinned_plug)
        {
            args->pinned_plug_entry = get_oldest_pinned_entry(
                hp,
                &has_pre_plug_info_p,
                &has_post_plug_info_p);
            Debug.Assert(tree == pinned_plug(args->pinned_plug_entry));
        }

        if (args->last_plug is not null)
        {
            nuint gap_size = node_gap_size(tree);
            byte* gap = plug - (nint)gap_size;
            byte* last_plug_end = gap;
            nuint last_plug_size = (nuint)(last_plug_end - args->last_plug);
            Debug.Assert((last_plug_size & ((nuint)sizeof(byte*) - 1)) == 0);

            int check_last_object_p =
                args->is_shortened != 0 || has_pre_plug_info_p != 0 ? 1 : 0;
            if (check_last_object_p == 0)
            {
                Debug.Assert(last_plug_size >= Align((nuint)GCInterfaceOffsets.min_obj_size));
            }

            compact_plug(hp, args->last_plug, last_plug_size, check_last_object_p, args);
        }
        else
        {
            Debug.Assert(has_pre_plug_info_p == 0);
        }

        args->last_plug = plug;
        args->last_plug_relocation = relocation;
        args->is_shortened = has_post_plug_info_p;

        if (right_node != 0)
        {
            compact_in_brick(hp, tree + right_node, args);
        }
    }

    // Clear the brick-table entries past the region's plan-allocated tail (WKS translation of the
    // compact_phase USE_REGIONS used-pointer fixup), so a stale brick can never be walked.
    public static void clear_unused_bricks_after_compaction(
        heap_segment* region,
        byte* plan_allocated)
    {
        byte* firstUnusedBrick = card_table_info.align_lower_brick(plan_allocated);
        if (firstUnusedBrick < plan_allocated)
        {
            firstUnusedBrick += (nint)card_table_info.brick_size;
        }

        byte* reserved = heap_segment.heap_segment_reserved(region);
        if (firstUnusedBrick < reserved)
        {
            clear_brick_table(firstUnusedBrick, reserved);
        }
    }

    // relocate_compact.cpp gc_heap::get_start_segment: this generation's first non-SIP region.
    public static heap_segment* get_start_segment(generation* gen)
    {
        heap_segment* start_heap_segment =
            heap_segment_rw(generation.generation_start_segment(gen));
        start_heap_segment = heap_segment_non_sip(start_heap_segment);
        return start_heap_segment;
    }

    // relocate_compact.cpp gc_heap::recover_saved_pinned_info: restore each pinned plug's saved
    // pre/post-plug words and return the gen2 sweep space they reclaimed.
    public static nuint recover_saved_pinned_info(gc_heap* hp)
    {
        reset_pinned_queue_bos(hp);
        nuint total_recovered_sweep_size = 0;

        while (pinned_plug_que_empty_p(hp) == 0)
        {
            mark* oldest_entry = oldest_pin(hp);
            nuint recovered_sweep_size = mark.recover_plug_info(oldest_entry);

            if (recovered_sweep_size > 0)
            {
                byte* plug = pinned_plug(oldest_entry);
                if (object_gennum(plug) == GCInterfaceOffsets.max_generation)
                {
                    total_recovered_sweep_size += recovered_sweep_size;
                }
            }

            deque_pinned_plug(hp);
        }

        return total_recovered_sweep_size;
    }

    // relocate_compact.cpp gc_heap::compact_loh (FEATURE_LOH_COMPACTION): slide every marked LOH object
    // to its planned location and rebuild the LOH free lists / segment tails.
    public static void compact_loh(gc_heap* hp)
    {
        Debug.Assert(
            loh_compaction_requested() != 0 ||
            heap_hard_limit != 0 ||
            conserve_mem_setting != 0 ||
            settings.reason == gc_reason.reason_induced_aggressive);

        generation* gen = generation_of(
            generation_table_of(hp),
            (int)gc_generation_num.loh_generation);
        heap_segment* start_seg =
            heap_segment_rw(generation.generation_start_segment(gen));
        Debug.Assert(start_seg is not null);
        heap_segment* seg = start_seg;
        heap_segment* prev_seg = null;
        byte* o = get_uoh_start_object(seg, gen);

        allocator.clear(generation.generation_allocator(gen));
        generation.generation_free_list_space(gen) = 0;
        generation.generation_free_obj_space(gen) = 0;
        hp->loh_pinned_queue_bos = 0;

        while (true)
        {
            if (o >= heap_segment.heap_segment_allocated(seg))
            {
                heap_segment* next_seg = heap_segment.heap_segment_next(seg);
                if (heap_segment.heap_segment_plan_allocated(seg) ==
                        heap_segment.heap_segment_mem(seg) &&
                    seg != start_seg &&
                    heap_segment.heap_segment_read_only_p(seg) == 0)
                {
                    Debug.Assert(prev_seg is not null);
                    heap_segment.heap_segment_next(prev_seg) = next_seg;
                    heap_segment.heap_segment_next(seg) = hp->freeable_uoh_segment;
                    hp->freeable_uoh_segment = seg;
                    update_start_tail_regions(gen, seg, prev_seg, next_seg);
                }
                else
                {
                    if (heap_segment.heap_segment_read_only_p(seg) == 0)
                    {
                        if (heap_segment.heap_segment_plan_allocated(seg) >
                            heap_segment.heap_segment_allocated(seg))
                        {
                            if (heap_segment.heap_segment_plan_allocated(seg) - (nint)plug_skew >
                                heap_segment.heap_segment_used(seg))
                            {
                                heap_segment.heap_segment_used(seg) =
                                    heap_segment.heap_segment_plan_allocated(seg) - (nint)plug_skew;
                            }
                        }

                        heap_segment.heap_segment_allocated(seg) =
                            heap_segment.heap_segment_plan_allocated(seg);
                        decommit_heap_segment_pages(seg, 0, hp->heap_number);
                    }

                    prev_seg = seg;
                }

                seg = next_seg;
                if (seg is null)
                {
                    break;
                }

                o = heap_segment.heap_segment_mem(seg);
            }

            if (((CObjectHeader*)o)->IsMarked() != 0)
            {
                nuint object_size = AlignQword(size(o));
                nuint loh_pad;
                byte* reloc = o;
                ((CObjectHeader*)o)->ClearMarked();

                if (((CObjectHeader*)o)->IsPinned() != 0)
                {
                    mark* m = loh_pinned_plug_of(hp, loh_deque_pinned_plug(hp));
                    byte* plug = pinned_plug(m);
                    Debug.Assert(plug == o);

                    loh_pad = pinned_len(m);
                    ((CObjectHeader*)o)->GetHeader()->ClrGCBit();
                }
                else
                {
                    loh_pad = AlignQword((nuint)sizeof(loh_padding_obj));
                    reloc += loh_node_relocation_distance(o);
                    gcmemcopy(reloc, o, object_size, copy_cards_p: 1);
                }

                thread_gap(reloc - (nint)loh_pad, loh_pad, gen);

                o += (nint)object_size;
                if (o < heap_segment.heap_segment_allocated(seg))
                {
                    Debug.Assert(((CObjectHeader*)o)->IsMarked() == 0);
                }
            }
            else
            {
                while (o < heap_segment.heap_segment_allocated(seg) &&
                       ((CObjectHeader*)o)->IsMarked() == 0)
                {
                    o += (nint)AlignQword(size(o));
                }
            }
        }

        Debug.Assert(loh_pinned_plug_que_empty_p(hp) != 0);
    }

    // relocate_compact.cpp gc_heap::compact_phase (SVR). Every server GC worker runs this on its own
    // heap after relocate_phase. It opens with the gc_join_relocate_phase_done join, then compacts the
    // LOH plan (if this heap compacted its LOH), walks each condemned SOH region's brick tree, recovers
    // the pinned-plug info, and finalizes each region's used pointer.
    public static void compact_phase(
        gc_heap* hp,
        int condemned_gen_number,
        byte* first_condemned_address,
        int clear_cards)
    {
        gc_t_join.join(hp, (int)gc_join_stage.gc_join_relocate_phase_done);
        if (gc_t_join.joined())
        {
            // FEATURE_EVENT_TRACE gc_time_info[time_compact] / [time_relocate] timing is deferred with
            // the rest of the server event integration.
            gc_t_join.restart();
        }

        _ = first_condemned_address;

        if (hp->loh_compacted_p != 0)
        {
            compact_loh(hp);
        }

        reset_pinned_queue_bos(hp);
        update_oldest_pinned_plug(hp);
        bool reused_seg = expand_reused_seg_p();
        if (reused_seg)
        {
            generation* generation_table = generation_table_of(hp);
            for (int i = 1; i <= GCInterfaceOffsets.max_generation; i++)
            {
                generation.generation_allocation_size(generation_of(generation_table, i)) = 0;
            }
        }

        int stop_gen_idx = get_stop_generation_index(condemned_gen_number);
        generation* generations = generation_table_of(hp);
        for (int i = condemned_gen_number; i >= stop_gen_idx; i--)
        {
            generation* condemned_gen = generation_of(generations, i);
            heap_segment* current_heap_segment = get_start_segment(condemned_gen);
            if (current_heap_segment is null)
            {
                continue;
            }

            nuint current_brick = brick_of(heap_segment.heap_segment_mem(current_heap_segment));
            byte* end_address = heap_segment.heap_segment_allocated(current_heap_segment);
            nuint end_brick = brick_of(end_address - 1);
            compact_args args = default;
            args.last_plug = null;
            args.before_last_plug = null;
            args.current_compacted_brick = ~(nuint)1;
            args.is_shortened = 0;
            args.pinned_plug_entry = null;
            args.copy_cards_p = condemned_gen_number >= 1 || clear_cards == 0 ? 1 : 0;
            args.check_gennum_p = reused_seg ? 1 : 0;
            if (args.check_gennum_p != 0)
            {
                args.src_gennum =
                    current_heap_segment == hp->ephemeral_heap_segment ? -1 : 2;
            }

            Debug.Assert(args.check_gennum_p == 0);

            while (true)
            {
                if (current_brick > end_brick)
                {
                    if (args.last_plug is not null)
                    {
                        compact_plug(
                            hp,
                            args.last_plug,
                            (nuint)(heap_segment.heap_segment_allocated(current_heap_segment) -
                                args.last_plug),
                            args.is_shortened,
                            &args);
                    }

                    heap_segment* next_heap_segment =
                        heap_segment_next_non_sip(current_heap_segment);
                    if (next_heap_segment is not null)
                    {
                        current_heap_segment = next_heap_segment;
                        current_brick = brick_of(
                            heap_segment.heap_segment_mem(current_heap_segment));
                        end_brick = brick_of(
                            heap_segment.heap_segment_allocated(current_heap_segment) - 1);
                        args.last_plug = null;
                        if (args.check_gennum_p != 0)
                        {
                            args.src_gennum =
                                current_heap_segment == hp->ephemeral_heap_segment ? -1 : 2;
                        }

                        continue;
                    }

                    if (args.before_last_plug is not null)
                    {
                        Debug.Assert(args.current_compacted_brick != unchecked((nuint)~1u));
                        set_brick(
                            args.current_compacted_brick,
                            (nint)(args.before_last_plug -
                                brick_address(args.current_compacted_brick)));
                    }

                    break;
                }

                int brick_entry = brick_table[(nint)current_brick];
                if (brick_entry >= 0)
                {
                    compact_in_brick(
                        hp,
                        brick_address(current_brick) + brick_entry - 1,
                        &args);
                }

                current_brick++;
            }
        }

        recover_saved_pinned_info(hp);

        int gen_limit = condemned_gen_number + 1 < GCInterfaceOffsets.max_generation
            ? condemned_gen_number + 1
            : GCInterfaceOffsets.max_generation;
        for (int i = 0; i <= gen_limit; i++)
        {
            generation* gen = generation_of(generations, i);
            for (heap_segment* region = generation.generation_start_segment_rw(gen);
                 region is not null;
                 region = heap_segment_next_rw(region))
            {
                byte* plan_allocated = heap_segment.heap_segment_plan_allocated(region);
                clear_unused_bricks_after_compaction(region, plan_allocated);
                if (plan_allocated > heap_segment.heap_segment_used(region))
                {
                    heap_segment.heap_segment_used(region) = plan_allocated;
                }
            }
        }
    }

    // plan_phase.cpp should_compact tail (USE_REGIONS): after compaction, lay a free object over each
    // pinned plug's saved gap and thread it onto its planned generation's free list, fixing the bricks
    // the free array straddles. add_gen_free is a FREE_USAGE_STATS no-op and is omitted.
    public static void thread_pinned_plug_gaps(gc_heap* hp)
    {
        reset_pinned_queue_bos(hp);
        while (pinned_plug_que_empty_p(hp) == 0)
        {
            mark* m = pinned_plug_of(hp, deque_pinned_plug(hp));
            nuint len = pinned_len(m);
            byte* arr = pinned_plug(m) - (nint)len;
            if (len != 0)
            {
                Debug.Assert(len >= Align((nuint)GCInterfaceOffsets.min_obj_size));
                make_unused_array(arr, len);

                nuint start_brick = brick_of(arr);
                nuint end_brick = brick_of(arr + (nint)len);
                if (end_brick != start_brick)
                {
                    set_brick(start_brick, (nint)(arr - brick_address(start_brick)));
                    nuint brick = start_brick + 1;
                    while (brick < end_brick)
                    {
                        set_brick(brick, (nint)start_brick - (nint)brick);
                        brick++;
                    }
                }

                int gen_number = object_gennum_plan(arr);
                generation* gen = generation_of(generation_table_of(hp), gen_number);

                thread_gap(arr, len, gen);
            }
        }
    }
}

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
