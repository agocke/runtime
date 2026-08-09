// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Server plan-phase older-generation free-list plan allocator, translated from the SVR-namespace
// compilation of allocation.cpp for the active x64 Linux SERVER_GC -> MULTIPLE_HEAPS ->
// DYNAMIC_HEAP_COUNT -> USE_REGIONS feature chain. This is the allocator the plan_phase plug walk
// uses on the non-max-gen SOH branch (settings.condemned_generation < max_generation) to place a
// surviving non-pinned plug into the *older* generation's already-swept free list, before falling
// back to allocate_in_condemned_generations when the free list can't hold the plug:
//
//   * allocate_in_older_generation walks the older generation allocator's size-segregated free
//     lists (and, for max_generation, the DOUBLY_LINKED_FL "added" list that BGC threads), fitting
//     the plug with the same size_fit_p / pad_in_front / large-alignment rules the compactor
//     expects; when no free-list item fits it grows the current region or advances to the next
//     region's plan-allocated space (end-of-seg allocation),
//   * fix_older_allocation_area threads the unused tail of the older generation's current alloc
//     window back onto its free list (or records it as free-object space) once the plug walk
//     switches to a different older generation, and
//   * commit_alloc_list_changes (already translated as a shared allocator method in GCPriv.cs)
//     repairs the undo/added-list bookkeeping the free-list unlink paths above leave behind.
//
// Every function reaches the owning heap through its gc_heap* parameter. The free-list mutation
// operates on the generation allocator (generation_allocator (gen)) and the generation's
// free-list / free-object accounting fields, all reached through the generation pointer, so no
// per-heap static state is captured except:
//   * gen2_removed_no_undo (PER_HEAP_FIELD_SINGLE_GC, now instance-owned for MULTIPLE_HEAPS in
//     GCPriv.cs) which allocate_in_older_generation increments when it removes a max_gen free-list
//     item without recording undo info, and
//   * saved_pinned_plug_index (PER_HEAP_FIELD_SINGLE_GC, instance-owned for MULTIPLE_HEAPS) which
//     adjust_limit consults to set the free-obj-in-compact bit on a saved pinned-plug reloc word.
// The background-sweep predicates should_set_bgc_mark_bit consults are PER_HEAP_FIELD_SINGLE_GC
// (current_sweep_pos / current_sweep_seg, instance) and PER_HEAP_ISOLATED (current_bgc_state /
// background_saved_lowest_address / background_saved_highest_address, shared), matching native.
//
// The dependency-free leaves the allocator needs but that live in the WKS-only GCAllocation.cs /
// BackgroundGC.cs / MarkPhase.cs (unused_array_size, make_free_obj, thread_free_item_front,
// thread_item_front_added, adjust_limit, should_set_bgc_mark_bit, set_plug_bgc_mark_bit,
// set_free_obj_in_compact_bit) are re-translated here; they are identical for the WKS and SVR
// compilations apart from the instance/static field ownership noted above. size_fit_p /
// switch_alignment_size / grow_heap_segment / set_plug_padded / clear_plug_padded are reused from
// ManagedServerGCPlanCondemned.cs, and same_large_alignment_p / set_node_realigned are shared.
//
// No collection is routed by this slice: the plan_phase driver that sequences this allocator
// alongside allocate_in_condemned_generations (its per-GC region-planning / gen2_removed_no_undo /
// saved_pinned_plug_index resets, the plug walk, the gc_join_decide_on_compaction join), plan_loh /
// plan_poh, fix_generation_bounds, and the relocate / compact / sweep execution all remain
// deferred, so nothing here runs against a live heap yet.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    // gcpriv.h min_free_item_no_prev (min_obj_size + sizeof(uint8_t*)): the smallest allocated
    // context that can carry a doubly-linked free-list prev pointer. The server build is always
    // 64-bit (regions require it), so this is unconditionally available.
    public static nuint min_free_item_no_prev =>
        (nuint)GCInterfaceOffsets.min_obj_size + (nuint)sizeof(byte*);

    // gcinternal.h unused_array_size: recover a free object's total byte length from the
    // num-components word make_unused_array wrote after the free method table.
    public static nuint unused_array_size(byte* p)
    {
        Debug.Assert(((CObjectHeader*)p)->IsFree() != 0);
        return unchecked((nuint)GCInterfaceOffsets.min_obj_size + *(nuint*)(p + (nint)sizeof(nuint)));
    }

    // allocation.cpp make_free_obj: lay a free object over the hole and charge it to the
    // generation's free-object (non-threaded) space.
    public static void make_free_obj(generation* gen, byte* free_start, nuint free_size)
    {
        make_unused_array(free_start, free_size);
        generation.generation_free_obj_space(gen) = unchecked(
            generation.generation_free_obj_space(gen) + free_size);
    }

    // allocation.cpp thread_free_item_front: lay a free object over the hole and thread it onto the
    // front of the generation's free list (add_gen_free is a FREE_USAGE_STATS no-op here).
    public static void thread_free_item_front(generation* gen, byte* free_start, nuint free_size)
    {
        make_unused_array(free_start, free_size);
        generation.generation_free_list_space(gen) = unchecked(
            generation.generation_free_list_space(gen) + free_size);
        allocator.thread_item_front(
            generation.generation_allocator(gen), free_start, free_size);
    }

#if TARGET_64BIT && !TARGET_WASM
    // allocation.cpp thread_item_front_added (DOUBLY_LINKED_FL): lay a free object over the hole and
    // thread it onto the max_gen "added" list BGC folds back in commit_alloc_list_changes.
    public static void thread_item_front_added(generation* gen, byte* free_start, nuint free_size)
    {
        make_unused_array(free_start, free_size);
        generation.generation_free_list_space(gen) = unchecked(
            generation.generation_free_list_space(gen) + free_size);
        allocator.thread_item_front_added(
            generation.generation_allocator(gen), free_start, free_size);
    }

    // background.cpp should_set_bgc_mark_bit: when a max_gen free-list item is consumed during the
    // planning phase of a concurrent GC, decide whether the plug placed there must carry the
    // BGC-marked bit so the background sweep does not reclaim it.
    public static bool should_set_bgc_mark_bit(gc_heap* hp, byte* o)
    {
        if (hp->current_sweep_seg is null)
        {
            Debug.Assert(current_bgc_state == bgc_state.bgc_not_in_process);
            return false;
        }

        if (in_range_for_segment(o, hp->current_sweep_seg) != 0)
        {
            return o >= hp->current_sweep_pos &&
                o < heap_segment.heap_segment_background_allocated(hp->current_sweep_seg);
        }

        if (o < background_saved_lowest_address ||
            o >= background_saved_highest_address)
        {
            return false;
        }

        heap_segment* seg = region_of(o);
        byte* background_allocated =
            heap_segment.heap_segment_background_allocated(seg);
        return background_allocated is not null &&
            o < background_allocated &&
            heap_segment.heap_segment_swept_p(seg) == 0;
    }

    // gcinternal.h set_plug_bgc_mark_bit / set_free_obj_in_compact_bit reuse the lower method-table
    // bits (BGC_MARKED_BY_FGC / MAKE_FREE_OBJ_IN_COMPACT) while the plug is being planned.
    public static void set_plug_bgc_mark_bit(byte* node)
    {
        ((CObjectHeader*)node)->SetBGCMarkBit();
    }

    public static void set_free_obj_in_compact_bit(byte* node)
    {
        ((CObjectHeader*)node)->SetFreeObjInCompactBit();
    }
#endif // TARGET_64BIT && !TARGET_WASM

    // allocation.cpp adjust_limit: switch the generation's plan-allocation window to [start,
    // start + limit_size), turning the abandoned tail of the previous window into free objects /
    // threaded free-list items. leave_allocation_segment (adjust_limit (0, 0, gen)) is spelled with
    // start == null, limit_size == 0.
    public static void adjust_limit(
        gc_heap* hp,
        byte* start,
        nuint limit_size,
        generation* gen)
    {
        heap_segment* seg = generation.generation_allocation_segment(gen);
        if (generation.generation_allocation_limit(gen) != start ||
            start != heap_segment.heap_segment_plan_allocated(seg))
        {
            if (generation.generation_allocation_limit(gen) ==
                heap_segment.heap_segment_plan_allocated(seg))
            {
                Debug.Assert(
                    generation.generation_allocation_pointer(gen) >=
                    heap_segment.heap_segment_mem(seg));
                Debug.Assert(
                    generation.generation_allocation_pointer(gen) <=
                    heap_segment.heap_segment_committed(seg));
                heap_segment.heap_segment_plan_allocated(seg) =
                    generation.generation_allocation_pointer(gen);
            }
            else
            {
                byte* hole = generation.generation_allocation_pointer(gen);
                nuint hole_size = unchecked((nuint)(
                    generation.generation_allocation_limit(gen) -
                    generation.generation_allocation_pointer(gen)));
                if (hole_size != 0)
                {
                    nuint allocated_size = unchecked((nuint)(
                        generation.generation_allocation_pointer(gen) -
                        generation.generation_allocation_context_start_region(gen)));
#if TARGET_64BIT && !TARGET_WASM
                    if (gen->gen_num == GCInterfaceOffsets.max_generation)
                    {
                        if (allocated_size <= min_free_item_no_prev)
                        {
                            nuint filler_free_obj_size;
                            if (hole_size >= unchecked(
                                2 * (nuint)GCInterfaceOffsets.min_obj_size +
                                Align((nuint)GCInterfaceOffsets.min_obj_size)))
                            {
                                filler_free_obj_size =
                                    Align((nuint)GCInterfaceOffsets.min_obj_size);
                                thread_item_front_added(
                                    gen,
                                    hole + (nint)filler_free_obj_size,
                                    unchecked(hole_size - filler_free_obj_size));
                            }
                            else
                            {
                                filler_free_obj_size = hole_size;
                            }

                            generation.generation_free_obj_space(gen) = unchecked(
                                generation.generation_free_obj_space(gen) +
                                filler_free_obj_size);
                            *(nuint*)(
                                generation.generation_allocation_context_start_region(gen) +
                                (nint)min_free_item_no_prev) =
                                filler_free_obj_size;

                            byte* old_loc =
                                generation.generation_last_free_list_allocated(gen);
                            if (old_loc is not null)
                            {
                                byte* saved_plug_and_gap = null;
                                if (hp->saved_pinned_plug_index != nuint.MaxValue)
                                {
                                    saved_plug_and_gap =
                                        pinned_plug(
                                            pinned_plug_of(
                                                hp,
                                                hp->saved_pinned_plug_index)) -
                                        (nint)sizeof(plug_and_gap);
                                }

                                nuint offset = unchecked(
                                    (nuint)(old_loc - saved_plug_and_gap));
                                if (offset < (nuint)sizeof(gap_reloc_pair))
                                {
                                    mark* savedEntry = pinned_plug_of(
                                        hp,
                                        hp->saved_pinned_plug_index);
                                    set_free_obj_in_compact_bit(
                                        (byte*)&savedEntry->saved_pre_plug_reloc +
                                        (nint)offset);
                                }
                                else
                                {
                                    set_free_obj_in_compact_bit(old_loc);
                                }
                            }
                        }
                        else if (hole_size >=
                            unchecked(2 * (nuint)GCInterfaceOffsets.min_obj_size))
                        {
                            thread_item_front_added(gen, hole, hole_size);
                        }
                        else
                        {
                            make_free_obj(gen, hole, hole_size);
                        }
                    }
                    else
#endif // TARGET_64BIT && !TARGET_WASM
                    {
                        if (hole_size >= unchecked(
                            2 * (nuint)GCInterfaceOffsets.min_obj_size))
                        {
                            if (allocated_size < min_free_item_no_prev)
                            {
                                if (hole_size >= unchecked(
                                    2 * (nuint)GCInterfaceOffsets.min_obj_size +
                                    Align((nuint)GCInterfaceOffsets.min_obj_size)))
                                {
                                    nuint filler =
                                        Align((nuint)GCInterfaceOffsets.min_obj_size);
                                    make_free_obj(gen, hole, filler);
                                    thread_free_item_front(
                                        gen,
                                        hole + (nint)filler,
                                        unchecked(hole_size - filler));
                                }
                                else
                                {
                                    make_free_obj(gen, hole, hole_size);
                                }
                            }
                            else
                            {
                                thread_free_item_front(gen, hole, hole_size);
                            }
                        }
                        else
                        {
                            make_free_obj(gen, hole, hole_size);
                        }
                    }
                }
            }

            generation.generation_allocation_pointer(gen) = start;
            generation.generation_allocation_context_start_region(gen) = start;
            generation.generation_allocation_limit(gen) =
                start + (nint)limit_size;
        }
    }

    // allocation.cpp fix_older_allocation_area: close out the older generation's plan-allocation
    // window once the plug walk moves on. The unused tail between the alloc pointer and the alloc
    // limit is threaded back onto the free list (or accounted as free-object space) and the alloc
    // pointer / limit are cleared.
    public static void fix_older_allocation_area(gc_heap* hp, generation* older_gen)
    {
        heap_segment* older_gen_segment =
            generation.generation_allocation_segment(older_gen);
        if (generation.generation_allocation_limit(older_gen) !=
            heap_segment.heap_segment_plan_allocated(older_gen_segment))
        {
            byte* point = generation.generation_allocation_pointer(older_gen);
            nuint free_size = unchecked((nuint)(
                generation.generation_allocation_limit(older_gen) -
                generation.generation_allocation_pointer(older_gen)));
            if (free_size != 0)
            {
                Debug.Assert(
                    free_size >= Align((nuint)GCInterfaceOffsets.min_obj_size));
                make_unused_array(point, free_size);
                if (free_size >= 2 * (nuint)GCInterfaceOffsets.min_obj_size)
                {
                    allocator.thread_item_front(
                        generation.generation_allocator(older_gen),
                        point,
                        free_size);
                    generation.generation_free_list_space(older_gen) = unchecked(
                        generation.generation_free_list_space(older_gen) +
                        free_size);
                }
                else
                {
                    generation.generation_free_obj_space(older_gen) = unchecked(
                        generation.generation_free_obj_space(older_gen) +
                        free_size);
                }
            }
        }
        else
        {
            Debug.Assert(older_gen_segment != hp->ephemeral_heap_segment);
            heap_segment.heap_segment_plan_allocated(older_gen_segment) =
                generation.generation_allocation_pointer(older_gen);
            generation.generation_allocation_limit(older_gen) =
                generation.generation_allocation_pointer(older_gen);
        }

        generation.generation_allocation_pointer(older_gen) = null;
        generation.generation_allocation_limit(older_gen) = null;
    }

    // allocation.cpp allocate_in_older_generation: place a surviving plug of the condemned
    // generation into the already-swept older generation's free list / end-of-segment space. The
    // free-list search prefers size-segregated buckets (with the DOUBLY_LINKED_FL "added" list for
    // max_generation), discards non-fitting bucket-0 items, and falls back to committing / growing
    // the region's plan-allocated tail. Returns null when nothing fits, so the caller can retry
    // through allocate_in_condemned_generations.
    public static byte* allocate_in_older_generation(
        gc_heap* hp,
        generation* gen,
        nuint size,
        int from_gen_number,
        byte* old_loc)
    {
        size = Align(size);
        Debug.Assert(size >= Align((nuint)GCInterfaceOffsets.min_obj_size));
        Debug.Assert(from_gen_number >= 0);
        Debug.Assert(from_gen_number < GCInterfaceOffsets.max_generation);
        Debug.Assert(
            generation_of(generation_table_of(hp), from_gen_number + 1) == gen);

#if TARGET_64BIT && !TARGET_WASM
        bool try_added_list_p = gen->gen_num == GCInterfaceOffsets.max_generation;
        bool record_free_list_allocated_p =
            gen->gen_num == GCInterfaceOffsets.max_generation &&
            current_c_gc_state == c_gc_state.c_gc_state_planning;
#endif

        allocator* gen_allocator = generation.generation_allocator(gen);
        bool discard_p = gen_allocator->discard_if_no_fit_p() != 0;
        int pad_in_front =
            old_loc is not null &&
            from_gen_number + 1 != GCInterfaceOffsets.max_generation
                ? USE_PADDING_FRONT
                : 0;
        nuint real_size = unchecked(
            size +
            Align((nuint)GCInterfaceOffsets.min_obj_size) +
            (pad_in_front != 0
                ? Align((nuint)GCInterfaceOffsets.min_obj_size)
                : 0));

        if (!size_fit_p(
                size,
                generation.generation_allocation_pointer(gen),
                generation.generation_allocation_limit(gen),
                old_loc,
                USE_PADDING_TAIL | pad_in_front))
        {
            for (uint bucket = gen_allocator->first_suitable_bucket(
                    unchecked(real_size * 2));
                 bucket < gen_allocator->number_of_buckets();
                 bucket++)
            {
                byte* previous = null;
#if TARGET_64BIT && !TARGET_WASM
                bool use_undo_p = !discard_p && bucket != 0;
                if (try_added_list_p)
                {
                    byte* free_list =
                        allocator.added_alloc_list_head_of(gen_allocator, bucket);
                    while (free_list is not null)
                    {
                        nuint free_list_size = unused_array_size(free_list);
                        byte* next = allocator.free_list_slot(free_list);
                        if (size_fit_p(
                                size,
                                free_list,
                                free_list + (nint)free_list_size,
                                old_loc,
                                USE_PADDING_TAIL | pad_in_front))
                        {
                            allocator.unlink_item_no_undo_added(
                                gen_allocator,
                                bucket,
                                free_list,
                                previous);
                            generation.generation_free_list_space(gen) =
                                unchecked(
                                    generation.generation_free_list_space(gen) -
                                    free_list_size);
                            if (record_free_list_allocated_p)
                            {
                                generation.generation_set_bgc_mark_bit_p(gen) =
                                    should_set_bgc_mark_bit(hp, free_list) ? 1 : 0;
                            }

                            adjust_limit(hp, free_list, free_list_size, gen);
                            generation.generation_allocate_end_seg_p(gen) = 0;
                            goto finished;
                        }

                        if (bucket == 0)
                        {
                            generation.generation_free_obj_space(gen) = unchecked(
                                generation.generation_free_obj_space(gen) +
                                free_list_size);
                            allocator.unlink_item_no_undo_added(
                                gen_allocator,
                                bucket,
                                free_list,
                                previous);
                            generation.generation_free_list_space(gen) =
                                unchecked(
                                    generation.generation_free_list_space(gen) -
                                    free_list_size);
                        }
                        else
                        {
                            previous = free_list;
                        }

                        free_list = next;
                    }
                }
#else
                bool use_undo_p = !discard_p;
#endif

                byte* item = allocator.alloc_list_head_of(gen_allocator, bucket);
                previous = null;
                while (item is not null)
                {
                    nuint item_size = unused_array_size(item);
                    byte* next = allocator.free_list_slot(item);
                    if (size_fit_p(
                            size,
                            item,
                            item + (nint)item_size,
                            old_loc,
                            USE_PADDING_TAIL | pad_in_front))
                    {
                        allocator.unlink_item(
                            gen_allocator,
                            bucket,
                            item,
                            previous,
                            use_undo_p);
                        generation.generation_free_list_space(gen) = unchecked(
                            generation.generation_free_list_space(gen) - item_size);
#if TARGET_64BIT && !TARGET_WASM
                        if (!discard_p && !use_undo_p)
                        {
                            hp->gen2_removed_no_undo = unchecked(
                                hp->gen2_removed_no_undo + item_size);
                        }

                        if (record_free_list_allocated_p)
                        {
                            generation.generation_set_bgc_mark_bit_p(gen) =
                                should_set_bgc_mark_bit(hp, item) ? 1 : 0;
                        }
#endif
                        adjust_limit(hp, item, item_size, gen);
                        generation.generation_allocate_end_seg_p(gen) = 0;
                        goto finished;
                    }

                    if (discard_p || bucket == 0)
                    {
                        generation.generation_free_obj_space(gen) = unchecked(
                            generation.generation_free_obj_space(gen) + item_size);
                        allocator.unlink_item(
                            gen_allocator,
                            bucket,
                            item,
                            previous,
                            use_undo_p: false);
                        generation.generation_free_list_space(gen) = unchecked(
                            generation.generation_free_list_space(gen) - item_size);
#if TARGET_64BIT && !TARGET_WASM
                        if (!discard_p)
                        {
                            hp->gen2_removed_no_undo = unchecked(
                                hp->gen2_removed_no_undo + item_size);
                        }
#endif
                    }
                    else
                    {
                        previous = item;
                    }

                    item = next;
                }
            }

            heap_segment* seg = generation.generation_allocation_segment(gen);
            Debug.Assert(seg != hp->ephemeral_heap_segment);
            while (seg is not null)
            {
                byte* plan_allocated = heap_segment.heap_segment_plan_allocated(seg);
                if (size_fit_p(
                        size,
                        plan_allocated,
                        heap_segment.heap_segment_committed(seg),
                        old_loc,
                        USE_PADDING_TAIL | pad_in_front))
                {
                    adjust_limit(
                        hp,
                        plan_allocated,
                        unchecked((nuint)(
                            heap_segment.heap_segment_committed(seg) -
                            plan_allocated)),
                        gen);
                    generation.generation_allocate_end_seg_p(gen) = 1;
                    heap_segment.heap_segment_plan_allocated(seg) =
                        heap_segment.heap_segment_committed(seg);
                    goto finished;
                }

                if (size_fit_p(
                        size,
                        plan_allocated,
                        heap_segment.heap_segment_reserved(seg),
                        old_loc,
                        USE_PADDING_TAIL | pad_in_front) &&
                    grow_heap_segment(
                        hp,
                        seg,
                        plan_allocated,
                        old_loc,
                        size,
                        pad_in_front))
                {
                    adjust_limit(
                        hp,
                        plan_allocated,
                        unchecked((nuint)(
                            heap_segment.heap_segment_committed(seg) -
                            plan_allocated)),
                        gen);
                    generation.generation_allocate_end_seg_p(gen) = 1;
                    heap_segment.heap_segment_plan_allocated(seg) =
                        heap_segment.heap_segment_committed(seg);
                    goto finished;
                }

                adjust_limit(hp, null, 0, gen);
                seg = heap_segment.heap_segment_next(seg);
                if (seg is null)
                {
                    return null;
                }

                generation.generation_allocation_segment(gen) = seg;
                generation.generation_allocation_pointer(gen) =
                    heap_segment.heap_segment_mem(seg);
                generation.generation_allocation_limit(gen) =
                    generation.generation_allocation_pointer(gen);
            }
        }

    finished:
        byte* result = generation.generation_allocation_pointer(gen);
        nuint pad = 0;
        if ((pad_in_front & USE_PADDING_FRONT) != 0 &&
            (generation.generation_allocation_pointer(gen) -
                generation.generation_allocation_context_start_region(gen) == 0 ||
             generation.generation_allocation_pointer(gen) -
                generation.generation_allocation_context_start_region(gen) >=
                DESIRED_PLUG_LENGTH))
        {
            pad = Align((nuint)GCInterfaceOffsets.min_obj_size);
            set_plug_padded(old_loc);
        }

        if (old_loc is not null &&
            !same_large_alignment_p(old_loc, result + (nint)pad))
        {
            pad = unchecked(pad + switch_alignment_size(pad != 0 ? 1 : 0));
            set_node_realigned(old_loc);
        }

        if (old_loc is null || pad != 0)
        {
            generation.generation_allocation_context_start_region(gen) =
                generation.generation_allocation_pointer(gen);
        }

        byte* nextAllocationPointer =
            generation.generation_allocation_pointer(gen) + (nint)(size + pad);
        if (nextAllocationPointer > generation.generation_allocation_limit(gen))
        {
            if (pad != 0)
            {
                clear_plug_padded(old_loc);
            }

            adjust_limit(hp, null, 0, gen);
            return null;
        }

        generation.generation_allocation_pointer(gen) = nextAllocationPointer;
        generation.generation_free_obj_space(gen) = unchecked(
            generation.generation_free_obj_space(gen) + pad);

        if (generation.generation_allocate_end_seg_p(gen) != 0)
        {
            generation.generation_end_seg_allocated(gen) = unchecked(
                generation.generation_end_seg_allocated(gen) + size);
        }
        else
        {
#if TARGET_64BIT && !TARGET_WASM
            if (generation.generation_set_bgc_mark_bit_p(gen) != 0)
            {
                set_plug_bgc_mark_bit(old_loc);
            }

            generation.generation_last_free_list_allocated(gen) = old_loc;
#endif
            generation.generation_free_list_allocated(gen) = unchecked(
                generation.generation_free_list_allocated(gen) + size);
        }

        generation.generation_allocation_size(gen) = unchecked(
            generation.generation_allocation_size(gen) + size);
        return result + (nint)pad;
    }
}

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
