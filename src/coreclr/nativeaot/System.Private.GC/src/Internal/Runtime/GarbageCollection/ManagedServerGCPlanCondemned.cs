// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Server plan-phase condemned-generation plan allocator, translated from the SVR-namespace
// compilation of allocation.cpp for the active x64 Linux SERVER_GC -> MULTIPLE_HEAPS ->
// DYNAMIC_HEAP_COUNT -> USE_REGIONS feature chain. This is the allocator the plan_phase plug walk
// uses to place every surviving non-pinned plug of a condemned generation into its destination
// generation's plan-allocated space:
//
//   * get_next_alloc_seg walks a generation's allocation region list (skipping SIP regions and
//     switching down to a younger generation's start segment when a region runs out) so the plan
//     allocator always has a region whose plan-allocated bytes can grow,
//   * the region overload of attribute_pin_higher_gen_alloc records a consumed pin's bytes against
//     the higher generation's pinned-allocation sweep/compact accounting, using the destination
//     generation supplied by the caller when the pin lives in the region currently being planned
//     (its plan_gen_num is not set yet), and
//   * allocate_in_condemned_generations itself, which fits the plug (front/tail padded and
//     large-alignment adjusted) into the consing generation's current plan window, consuming pins
//     from this heap's pinned-plug queue and advancing to the next region / committing more of the
//     current region as needed, and converts an npinned plug that would leave too small a gap
//     before the next pin into an artificial pin (SHORT_PLUGS, always defined here).
//
// Every function reaches the owning heap through its gc_heap* parameter. The pinned-plug queue
// (mark_stack_array / mark_stack_tos / mark_stack_bos) is PER_HEAP_FIELD_SINGLE_GC, so the queue
// consumers (pinned_plug_que_empty_p / oldest_pin / deque_pinned_plug / set_new_pin_info in
// ManagedServerGCPlanRegions.cs, pinned_plug_of / pinned_len in ManagedServerGCPlanPhase.cs), the
// pin-positioning leaf set_allocator_next_pin (ManagedServerGCPlanBrick.cs), and the gen0 free-space
// attribution update_planned_gen0_free_space are all instance-owned; the region planning leaf
// set_region_plan_gen_num routes planned_regions_per_gen through heap_segment_heap(region). The
// shared plan/segment leaves (size_fit_p, grow_heap_segment, init_alloc_info, heap_segment_non_sip)
// and the SHORT_PLUGS alignment helpers (same_large_alignment_p, switch_alignment_size,
// set_plug_padded / clear_plug_padded, set_node_realigned) are reused as-is.
//
// plan_generation_start / plan_generation_starts are not translated: gcpriv.h / plan_phase.cpp
// compile them only under !USE_REGIONS. The region plan places generation boundaries through
// set_region_plan_gen_num and process_remaining_regions / process_last_np_surv_region instead.
//
// No collection is routed by this slice: the plan_phase driver that sequences this allocator (its
// per-GC region-planning counter reset, the plug walk, the gc_join_decide_on_compaction join),
// allocate_in_older_generation (the older-generation free-list plan allocator the non-max-gen SOH
// plug branch also calls), plan_loh / plan_poh, and the relocate / compact / sweep execution all
// remain deferred, so nothing here runs against a live heap yet.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    // Dependency-free plan-allocator leaves shared verbatim with the WKS compilation (allocation.cpp
    // and gcpriv.h). They live in GCAllocation.cs / MarkPhase.cs, which are WKS-only in this port, so
    // the server plan allocator re-translates the members it needs here. All are pure pointer
    // arithmetic / bit operations with no MULTIPLE_HEAPS difference.

    // allocation.cpp switch_alignment_size: the extra bytes needed to realign an object of a
    // different large-alignment class. Only reachable on architectures with 64-bit alignment
    // requirements, which x64 does not have.
    public static nuint switch_alignment_size(int already_padded_p)
    {
#if !TARGET_ARM && !TARGET_WASM
        Debug.Fail("Should not be called");
#endif

        if (already_padded_p != 0)
        {
            return (nuint)GCEnv.DATA_ALIGNMENT;
        }
        else
        {
            return Align((nuint)GCInterfaceOffsets.min_obj_size) |
                (nuint)GCEnv.DATA_ALIGNMENT;
        }
    }

    // allocation.cpp size_fit_p: does a plug of the given size (with the requested front/tail padding
    // and large-alignment adjustment) fit between alloc_pointer and alloc_limit?
    public static bool size_fit_p(
        nuint size,
        byte* alloc_pointer,
        byte* alloc_limit,
        byte* old_loc = null,
        int use_padding = USE_PADDING_TAIL)
    {
        int already_padded = 0;
        if (old_loc is not null && (use_padding & USE_PADDING_FRONT) != 0)
        {
            alloc_pointer += (nint)Align((nuint)GCInterfaceOffsets.min_obj_size);
            already_padded = 1;
        }

        if (old_loc is not null && !same_large_alignment_p(old_loc, alloc_pointer))
        {
            size = unchecked(size + switch_alignment_size(already_padded));
        }

        // In allocate_in_condemned_generations this can happen when alloc_limit is set to
        // plan_allocated, which can be less than alloc_pointer.
        if (alloc_limit < alloc_pointer)
        {
            return false;
        }

        if (old_loc is not null)
        {
            nuint tail_padding = (use_padding & USE_PADDING_TAIL) != 0
                ? Align((nuint)GCInterfaceOffsets.min_obj_size)
                : 0;
            return (nuint)(alloc_limit - alloc_pointer) >= unchecked(size + tail_padding) ||
                ((use_padding & USE_PADDING_FRONT) == 0 &&
                 (byte*)unchecked((nuint)alloc_pointer + size) == alloc_limit);
        }

        Debug.Assert(size == Align((nuint)GCInterfaceOffsets.min_obj_size));
        return (nuint)(alloc_limit - alloc_pointer) >= size;
    }

    private static nuint commit_min_th => unchecked((nuint)16 * GCToOSInterface.GetPageSize());

    // allocation.cpp grow_heap_segment: commit more memory in the region so high_address becomes
    // usable, rounding up to commit_min_th and clamping to the region's reserved bound.
    public static bool grow_heap_segment(
        heap_segment* seg,
        byte* high_address,
        int heap_number,
        bool* hard_limit_exceeded_p = null)
    {
        Debug.Assert(high_address <= heap_segment.heap_segment_reserved(seg));

        if (hard_limit_exceeded_p is not null)
        {
            *hard_limit_exceeded_p = false;
        }

        if (align_on_page(high_address) > heap_segment.heap_segment_reserved(seg))
        {
            return false;
        }

        if (high_address <= heap_segment.heap_segment_committed(seg))
        {
            return true;
        }

        nuint c_size = align_on_page(unchecked((nuint)(high_address - heap_segment.heap_segment_committed(seg))));
        if (c_size < commit_min_th)
        {
            c_size = commit_min_th;
        }

        nuint remaining_size = unchecked((nuint)(heap_segment.heap_segment_reserved(seg) - heap_segment.heap_segment_committed(seg)));
        if (c_size > remaining_size)
        {
            c_size = remaining_size;
        }

        if (c_size == 0)
        {
            return false;
        }

        bool ret = virtual_commit(
            heap_segment.heap_segment_committed(seg),
            c_size,
            (int)heap_segment.heap_segment_oh(seg),
            heap_number,
            hard_limit_exceeded_p);
        if (ret)
        {
            heap_segment.heap_segment_committed(seg) =
                (byte*)unchecked((nuint)heap_segment.heap_segment_committed(seg) + c_size);

            Debug.Assert(heap_segment.heap_segment_committed(seg) <= heap_segment.heap_segment_reserved(seg));
            Debug.Assert(high_address <= heap_segment.heap_segment_committed(seg));
        }

        return ret;
    }

    // allocation.cpp grow_heap_segment (plan-allocation overload): apply the same front-padding and
    // large-alignment adjustment allocate_in_condemned_generations uses, then commit enough of the
    // region to hold the padded plug.
    public static bool grow_heap_segment(
        gc_heap* hp,
        heap_segment* seg,
        byte* allocated,
        byte* old_loc,
        nuint size,
        int pad_front_p)
    {
        int already_padded = 0;
        if (old_loc is not null && pad_front_p != 0)
        {
            allocated += (nint)Align((nuint)GCInterfaceOffsets.min_obj_size);
            already_padded = 1;
        }

        if (old_loc is not null && !same_large_alignment_p(old_loc, allocated))
        {
            size = unchecked(size + switch_alignment_size(already_padded));
        }

        return grow_heap_segment(
            seg,
            (byte*)unchecked((nuint)allocated + size),
            hp->heap_number);
    }

    // gcpriv.h set_plug_padded / clear_plug_padded (SHORT_PLUGS, unconditionally defined): the
    // padded-plug marker reuses the object's GC_MARKED bit while the plug is being planned.
    public static void set_plug_padded(byte* node)
    {
        ((CObjectHeader*)node)->SetMarked();
    }

    public static void clear_plug_padded(byte* node)
    {
        ((CObjectHeader*)node)->ClearMarked();
    }

    // allocation.cpp get_next_alloc_seg: return the region the consing generation should currently
    // plan-allocate into, skipping SIP (swept-in-plan) regions and switching down to a younger
    // generation's start segment when a region's list is exhausted so the alloc region stays in sync
    // with the pinned-plug queue.
    public static heap_segment* get_next_alloc_seg(gc_heap* hp, generation* gen)
    {
        heap_segment* saved_region = generation.generation_allocation_segment(gen);
        int gen_num = heap_segment.heap_segment_gen_num(saved_region);
        heap_segment* region = saved_region;

        while (true)
        {
            region = heap_segment_non_sip(region);

            if (region is not null)
            {
                break;
            }

            if (gen_num > 0)
            {
                gen_num--;
                region = generation.generation_start_segment(
                    generation_of(generation_table_of(hp), gen_num));
            }
            else
            {
                Debug.Fail("ran out regions when getting the next alloc seg!");
            }
        }

        if (region != saved_region)
        {
            init_alloc_info(gen, region);
        }

        return region;
    }

    // allocation.cpp attribute_pin_higher_gen_alloc (USE_REGIONS overload): add a consumed pin's
    // bytes to the higher generation's pinned-allocation sweep size, and to the destination
    // generation's pinned-allocation compact size when the pin is promoted. With regions the pin's
    // plan_gen_num is only set after its region is planned, so a pin inside the region currently
    // being planned uses the destination generation the plan allocator supplied.
    public static void attribute_pin_higher_gen_alloc(
        gc_heap* hp,
        heap_segment* seg,
        int to_gen_number,
        byte* plug,
        nuint len)
    {
        int frgn = object_gennum(plug);
        if (frgn != GCInterfaceOffsets.max_generation && settings.promotion != 0)
        {
            generation.generation_pinned_allocation_sweep_size(
                generation_of(generation_table_of(hp), frgn + 1)) += len;

            int togn = in_range_for_segment(plug, seg) != 0
                ? to_gen_number
                : object_gennum_plan(plug);
            if (frgn < togn)
            {
                generation.generation_pinned_allocation_compact_size(
                    generation_of(generation_table_of(hp), togn)) += len;
            }
        }
    }

    // allocation.cpp allocate_in_condemned_generations: plan-allocate a plug of the given size into
    // the consing generation. The retry loop advances the plan window past consumed pins, moves to
    // the next region (or grows the current one) when the window is exhausted, and finally lays down
    // the plug with the front/tail padding and large-alignment adjustments the compactor expects.
    public static byte* allocate_in_condemned_generations(
        gc_heap* hp,
        generation* gen,
        nuint size,
        int from_gen_number,
        int* convert_to_pinned_p,
        byte* next_pinned_plug,
        heap_segment* current_seg,
        byte* old_loc)
    {
        size = Align(size);
        Debug.Assert(size >= Align((nuint)GCInterfaceOffsets.min_obj_size));
        int to_gen_number = from_gen_number;
        if (from_gen_number != GCInterfaceOffsets.max_generation)
        {
            to_gen_number = from_gen_number + (settings.promotion != 0 ? 1 : 0);
        }

        int pad_in_front =
            old_loc is not null && to_gen_number != GCInterfaceOffsets.max_generation
                ? USE_PADDING_FRONT
                : 0;

        // A near-region-sized plug cannot fit with front padding even in an empty region.
        if ((pad_in_front & USE_PADDING_FRONT) != 0 &&
            unchecked(size + Align((nuint)GCInterfaceOffsets.min_obj_size)) >
            unchecked(((nuint)1 << (int)min_segment_size_shr) - (nuint)sizeof(aligned_plug_and_gap)))
        {
            pad_in_front = 0;
        }

        if (from_gen_number != -1 &&
            from_gen_number != GCInterfaceOffsets.max_generation &&
            settings.promotion != 0)
        {
            generation* to_gen = generation_of(
                generation_table_of(hp),
                from_gen_number + (settings.promotion != 0 ? 1 : 0));
            generation.generation_condemned_allocated(to_gen) += size;
            generation.generation_allocation_size(to_gen) += size;
        }

    retry:
        heap_segment* seg = get_next_alloc_seg(hp, gen);
        if (!size_fit_p(
                size,
                generation.generation_allocation_pointer(gen),
                generation.generation_allocation_limit(gen),
                old_loc,
                (generation.generation_allocation_limit(gen) !=
                    heap_segment.heap_segment_plan_allocated(seg)
                        ? USE_PADDING_TAIL
                        : 0) |
                    pad_in_front))
        {
            if (pinned_plug_que_empty_p(hp) == 0 &&
                generation.generation_allocation_limit(gen) == pinned_plug(oldest_pin(hp)))
            {
                nuint entry = deque_pinned_plug(hp);
                mark* pinned_plug_entry = pinned_plug_of(hp, entry);
                nuint len = pinned_len(pinned_plug_entry);
                byte* plug = pinned_plug(pinned_plug_entry);
                set_new_pin_info(pinned_plug_entry, generation.generation_allocation_pointer(gen));

                if (to_gen_number == 0)
                {
                    update_planned_gen0_free_space(hp, pinned_len(pinned_plug_entry), plug);
                }

                Debug.Assert(
                    hp->mark_stack_array[entry].len == 0 ||
                    hp->mark_stack_array[entry].len >= Align((nuint)GCInterfaceOffsets.min_obj_size));
                generation.generation_allocation_pointer(gen) =
                    (byte*)unchecked((nuint)plug + len);
                generation.generation_allocation_context_start_region(gen) =
                    generation.generation_allocation_pointer(gen);
                generation.generation_allocation_limit(gen) =
                    heap_segment.heap_segment_plan_allocated(seg);
                set_allocator_next_pin(hp, gen);
                attribute_pin_higher_gen_alloc(hp, seg, to_gen_number, plug, len);
                goto retry;
            }

            if (generation.generation_allocation_limit(gen) !=
                heap_segment.heap_segment_plan_allocated(seg))
            {
                generation.generation_allocation_limit(gen) =
                    heap_segment.heap_segment_plan_allocated(seg);
            }
            else if (heap_segment.heap_segment_plan_allocated(seg) !=
                heap_segment.heap_segment_committed(seg))
            {
                heap_segment.heap_segment_plan_allocated(seg) =
                    heap_segment.heap_segment_committed(seg);
                generation.generation_allocation_limit(gen) =
                    heap_segment.heap_segment_plan_allocated(seg);
            }
            else if (size_fit_p(
                         size,
                         generation.generation_allocation_pointer(gen),
                         heap_segment.heap_segment_reserved(seg),
                         old_loc,
                         USE_PADDING_TAIL | pad_in_front) &&
                     grow_heap_segment(
                         hp,
                         seg,
                         generation.generation_allocation_pointer(gen),
                         old_loc,
                         size,
                         pad_in_front))
            {
                heap_segment.heap_segment_plan_allocated(seg) =
                    heap_segment.heap_segment_committed(seg);
                generation.generation_allocation_limit(gen) =
                    heap_segment.heap_segment_plan_allocated(seg);
            }
            else
            {
                heap_segment* next_seg = heap_segment.heap_segment_next(seg);
                Debug.Assert(
                    generation.generation_allocation_pointer(gen) >=
                    heap_segment.heap_segment_mem(seg));

                if (pinned_plug_que_empty_p(hp) == 0 &&
                    pinned_plug(oldest_pin(hp)) < heap_segment.heap_segment_allocated(seg) &&
                    pinned_plug(oldest_pin(hp)) >= generation.generation_allocation_pointer(gen))
                {
                    GCToEEInterface.HandleFatalError(CORINFO_EXCEPTION_GC);
                }

                Debug.Assert(
                    generation.generation_allocation_pointer(gen) >=
                    heap_segment.heap_segment_mem(seg));
                Debug.Assert(
                    generation.generation_allocation_pointer(gen) <=
                    heap_segment.heap_segment_committed(seg));
                heap_segment.heap_segment_plan_allocated(seg) =
                    generation.generation_allocation_pointer(gen);

                set_region_plan_gen_num(seg, to_gen_number);
                if (next_seg is null && heap_segment.heap_segment_gen_num(seg) > 0)
                {
                    next_seg = generation.generation_start_segment(
                        generation_of(
                            generation_table_of(hp),
                            heap_segment.heap_segment_gen_num(seg) - 1));
                }

                if (next_seg is not null)
                {
                    init_alloc_info(gen, next_seg);
                }
                else
                {
                    Debug.Fail("should not happen for regions!");
                }
            }

            set_allocator_next_pin(hp, gen);
            goto retry;
        }

        Debug.Assert(
            generation.generation_allocation_pointer(gen) >=
            heap_segment.heap_segment_mem(generation.generation_allocation_segment(gen)));
        byte* result = generation.generation_allocation_pointer(gen);
        nuint pad = 0;
        if ((pad_in_front & USE_PADDING_FRONT) != 0 &&
            (generation.generation_allocation_pointer(gen) -
                 generation.generation_allocation_context_start_region(gen) ==
             0 ||
             generation.generation_allocation_pointer(gen) -
                 generation.generation_allocation_context_start_region(gen) >=
             DESIRED_PLUG_LENGTH))
        {
            nint dist = unchecked((nint)(old_loc - result));
            if (dist != 0)
            {
                if (dist > 0 && dist < (nint)Align((nuint)GCInterfaceOffsets.min_obj_size))
                {
                    GCToEEInterface.HandleFatalError(CORINFO_EXCEPTION_GC);
                }

                pad = Align((nuint)GCInterfaceOffsets.min_obj_size);
                set_plug_padded(old_loc);
            }
        }

        if (old_loc is not null && !same_large_alignment_p(old_loc, result + (nint)pad))
        {
            pad = unchecked(pad + switch_alignment_size(pad != 0 ? 1 : 0));
            set_node_realigned(old_loc);
            Debug.Assert(same_large_alignment_p(result + (nint)pad, old_loc));
        }

        if (next_pinned_plug is not null &&
            pad != 0 &&
            generation.generation_allocation_segment(gen) == current_seg)
        {
            Debug.Assert(old_loc is not null);
            nint dist_to_next_pin = unchecked(
                (nint)(next_pinned_plug -
                    (generation.generation_allocation_pointer(gen) + (nint)size + (nint)pad)));
            Debug.Assert(dist_to_next_pin >= 0);

            if (dist_to_next_pin >= 0 &&
                dist_to_next_pin < (nint)Align((nuint)GCInterfaceOffsets.min_obj_size))
            {
                clear_plug_padded(old_loc);
                pad = 0;
                *convert_to_pinned_p = 1;
                return null;
            }
        }

        if (old_loc is null || pad != 0)
        {
            generation.generation_allocation_context_start_region(gen) =
                generation.generation_allocation_pointer(gen);
        }

        generation.generation_allocation_pointer(gen) =
            (byte*)unchecked(
                (nuint)generation.generation_allocation_pointer(gen) + size + pad);
        Debug.Assert(
            generation.generation_allocation_pointer(gen) <=
            generation.generation_allocation_limit(gen));

        if (pad > 0 && to_gen_number >= 0)
        {
            generation.generation_free_obj_space(
                generation_of(generation_table_of(hp), to_gen_number)) += pad;
        }

        Debug.Assert(result + (nint)pad is not null);
        return result + (nint)pad;
    }
}

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
