// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the dependency-closed WKS USE_REGIONS allocation-context helpers from allocation.cpp,
// sweep.cpp, gcinternal.h, and dynamic_tuning.cpp.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection;

internal unsafe partial struct gc_heap
{
    public static nuint Align(nuint nbytes, int alignment)
    {
        return unchecked((nbytes + (nuint)alignment) & ~(nuint)alignment);
    }

    public static nuint Align(nuint nbytes)
    {
        return Align(nbytes, get_alignment_constant(true));
    }

    public static nuint AlignQword(nuint nbytes)
    {
#if FEATURE_STRUCTALIGN
        // This function is used to align everything on the large object
        // heap to an 8-byte boundary, to reduce the number of unaligned
        // accesses to (say) arrays of doubles.  With FEATURE_STRUCTALIGN,
        // the compiler dictates the optimal alignment instead of having
        // a heuristic in the GC.
        return Align(nbytes);
#else // FEATURE_STRUCTALIGN
        return unchecked((nbytes + 7) & ~(nuint)7);
#endif // FEATURE_STRUCTALIGN
    }

    public static nuint switch_alignment_size(int already_padded_p)
    {
#if !TARGET_ARM && !TARGET_WASM
        System.Diagnostics.Debug.Fail("Should not be called");
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

    public static int get_alignment_constant(bool small_object_p)
    {
        return small_object_p ? GCEnv.DATA_ALIGNMENT - 1 : 7;
    }

    public static void set_padding_in_expand(
        byte* old_loc,
        int set_padding_on_saved_p,
        mark* pinned_plug_entry)
    {
        if (set_padding_on_saved_p != 0)
        {
            set_plug_padded(get_plug_start_in_saved(old_loc, pinned_plug_entry));
        }
        else
        {
            set_plug_padded(old_loc);
        }
    }

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

        System.Diagnostics.Debug.Assert(size == Align((nuint)GCInterfaceOffsets.min_obj_size));
        return (nuint)(alloc_limit - alloc_pointer) >= size;
    }

    public static bool a_size_fit_p(nuint size, byte* alloc_pointer, byte* alloc_limit, int align_const)
    {
        if (alloc_limit < alloc_pointer)
        {
            return false;
        }

        return (nuint)(alloc_limit - alloc_pointer) >= unchecked(size + Align((nuint)GCInterfaceOffsets.min_obj_size, align_const));
    }

    public static void void_allocation(gc_alloc_context* acontext)
    {
        if (acontext->alloc_ptr is not null)
        {
            acontext->alloc_ptr = null;
            acontext->alloc_limit = acontext->alloc_ptr;
        }
    }

    public static void retire_allocation_context(gc_alloc_context* acontext, ulong* total_alloc_bytes_soh)
    {
        byte* alloc_ptr = acontext->alloc_ptr;
        if (alloc_ptr is null)
        {
            return;
        }

        nuint unused_bytes = unchecked((nuint)(acontext->alloc_limit - alloc_ptr));
        acontext->alloc_bytes = unchecked(acontext->alloc_bytes - (long)unused_bytes);
        *total_alloc_bytes_soh = unchecked(*total_alloc_bytes_soh - unused_bytes);
        acontext->alloc_ptr = null;
        acontext->alloc_limit = acontext->alloc_ptr;
    }

    public static void add_alloc_bytes(gc_alloc_context* acontext, nuint added_bytes, ulong* total_alloc_bytes)
    {
        acontext->alloc_bytes = unchecked(acontext->alloc_bytes + (long)added_bytes);
        *total_alloc_bytes = unchecked(*total_alloc_bytes + added_bytes);
    }

    public static void add_uoh_alloc_bytes(gc_alloc_context* acontext, nuint allocated_size)
    {
        acontext->alloc_bytes_uoh = unchecked(acontext->alloc_bytes_uoh + (long)allocated_size);
    }

    private static void SetFree(byte* x, nuint size)
    {
        nuint free_object_base_size = (nuint)GCInterfaceOffsets.min_obj_size;

        System.Diagnostics.Debug.Assert(size >= free_object_base_size);

        *(void**)x = GCCommon.g_gc_pFreeObjectMethodTable;
        *(nuint*)(x + (nint)sizeof(nuint)) = unchecked(size - free_object_base_size);

#if TARGET_64BIT && !TARGET_WASM
        if (size >= unchecked(2 * free_object_base_size))
        {
            ((byte**)x)[3] = (byte*)1;
        }
#endif
    }

    public static void make_unused_array(byte* x, nuint size, int clearp = 0, int resetp = 0)
    {
        System.Diagnostics.Debug.Assert(size >= Align((nuint)GCInterfaceOffsets.min_obj_size));

        if (resetp != 0)
        {
            reset_memory(x, size);
        }

        SetFree(x, size);

#if TARGET_64BIT
        nuint free_object_base_size = (nuint)GCInterfaceOffsets.min_obj_size;
        nuint size_as_object = unchecked((nuint)(uint)(size - free_object_base_size) + free_object_base_size);

        if (size_as_object < size)
        {
            byte* tmp = (byte*)unchecked((nuint)x + size_as_object);
            nuint remaining_size = unchecked(size - size_as_object);

            while (remaining_size > (nuint)uint.MaxValue)
            {
                nuint current_size = unchecked(
                    (nuint)uint.MaxValue
                    - (nuint)get_alignment_constant(false)
                    - Align((nuint)GCInterfaceOffsets.min_obj_size, get_alignment_constant(false)));

                SetFree(tmp, current_size);

                remaining_size = unchecked(remaining_size - current_size);
                tmp = (byte*)unchecked((nuint)tmp + current_size);
            }

            SetFree(tmp, remaining_size);
        }
#endif

        if (clearp != 0)
        {
            clear_card_for_addresses(x, x + (nint)Align(size));
        }
    }

    public static nuint unused_array_size(byte* p)
    {
        System.Diagnostics.Debug.Assert(*(void**)p == GCCommon.g_gc_pFreeObjectMethodTable);

        return unchecked((nuint)GCInterfaceOffsets.min_obj_size + *(nuint*)(p + (nint)sizeof(nuint)));
    }

    // The WKS free-list fit paths own the allocation context but not the dynamic data,
    // allocation quantum, generation table, or selected allocation-byte counter that a later
    // try_allocate_more_space will provide. Keep that state explicit while preserving the native
    // refill handoff through adjust_limit_clr.
    public static void thread_free_item_front(generation* gen, byte* free_start, nuint free_size)
    {
        make_unused_array(free_start, free_size);
        generation.generation_free_list_space(gen) = unchecked(generation.generation_free_list_space(gen) + free_size);
        allocator.thread_item_front(generation.generation_allocator(gen), free_start, free_size);
    }

    public static void make_free_obj(generation* gen, byte* free_start, nuint free_size)
    {
        make_unused_array(free_start, free_size);
        generation.generation_free_obj_space(gen) = unchecked(generation.generation_free_obj_space(gen) + free_size);
    }

    public static nuint new_allocation_limit(dynamic_data* dd, nuint size, nuint physical_limit, int gen_number)
    {
        nint new_alloc = dynamic_data.dd_new_allocation(dd);
        System.Diagnostics.Debug.Assert(new_alloc == unchecked((nint)Align(unchecked((nuint)new_alloc), get_alignment_constant(gen_number < (int)gc_generation_num.uoh_start_generation))));

        nint logical_limit = new_alloc > unchecked((nint)size) ? new_alloc : unchecked((nint)size);
        nint physical_limit_signed = unchecked((nint)physical_limit);
        nuint limit = unchecked((nuint)(logical_limit < physical_limit_signed ? logical_limit : physical_limit_signed));

        System.Diagnostics.Debug.Assert(limit == Align(limit, get_alignment_constant(gen_number <= (int)gc_generation_num.max_generation)));

        return limit;
    }

    public static nuint limit_from_size(dynamic_data* dd, nuint allocation_quantum, nuint size, uint flags, nuint physical_limit, int gen_number, int align_const)
    {
        nuint padded_size = unchecked(size + Align((nuint)GCInterfaceOffsets.min_obj_size, align_const));
        System.Diagnostics.Debug.Assert((gen_number != 0) || (physical_limit >= padded_size));

        nuint min_size_to_allocate = ((gen_number == 0) && ((flags & (uint)GC_ALLOC_FLAGS.GC_ALLOC_ZEROING_OPTIONAL) == 0))
            ? allocation_quantum
            : 0;

        nuint desired_size_to_allocate = padded_size > min_size_to_allocate ? padded_size : min_size_to_allocate;
        nuint new_physical_limit = physical_limit < desired_size_to_allocate ? physical_limit : desired_size_to_allocate;
        nuint new_limit = new_allocation_limit(dd, padded_size, new_physical_limit, gen_number);

        System.Diagnostics.Debug.Assert(new_limit >= unchecked(size + Align((nuint)GCInterfaceOffsets.min_obj_size, align_const)));

        return new_limit;
    }

    public static void set_alloc_context_limit(gc_alloc_context* acontext, byte* start, nuint limit_size, int gen_number, int align_const, ulong* total_alloc_bytes)
    {
        nuint aligned_min_obj_size = Align((nuint)GCInterfaceOffsets.min_obj_size, align_const);

        acontext->alloc_limit = (byte*)unchecked((nuint)start + limit_size - aligned_min_obj_size);
        nuint added_bytes = unchecked(limit_size - ((gen_number <= (int)gc_generation_num.max_generation) ? aligned_min_obj_size : 0));
        add_alloc_bytes(acontext, added_bytes, total_alloc_bytes);
    }

#if USE_REGIONS
    public static bool a_fit_free_list_p(
        int gen_number,
        nuint size,
        gc_alloc_context* acontext,
        uint flags,
        int align_const,
        dynamic_data* dd,
        nuint allocation_quantum,
        generation* generation_table,
        ulong* total_alloc_bytes,
        try_allocate_more_space_context* more_space_context = null,
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback = null)
    {
        bool can_fit = false;
        generation* gen = generation_of(generation_table, gen_number);
        allocator* gen_allocator = generation.generation_allocator(gen);

        for (uint a_l_idx = gen_allocator->first_suitable_bucket(size);
             a_l_idx < gen_allocator->number_of_buckets();
             a_l_idx++)
        {
            byte* free_list = allocator.alloc_list_head_of(gen_allocator, a_l_idx);
            byte* prev_free_item = null;

            while (free_list is not null)
            {
                nuint free_list_size = unused_array_size(free_list);
                if (unchecked(size + Align((nuint)GCInterfaceOffsets.min_obj_size, align_const)) <= free_list_size)
                {
                    allocator.unlink_item(gen_allocator, a_l_idx, free_list, prev_free_item, false);

                    nuint limit = limit_from_size(
                        dd,
                        allocation_quantum,
                        size,
                        flags,
                        free_list_size,
                        gen_number,
                        align_const);
                    dynamic_data.dd_new_allocation(dd) = unchecked(dynamic_data.dd_new_allocation(dd) - (nint)limit);

                    byte* remain = (byte*)unchecked((nuint)free_list + limit);
                    nuint remain_size = unchecked(free_list_size - limit);
                    if (remain_size >= Align(unchecked((nuint)2 * (nuint)GCInterfaceOffsets.min_obj_size), align_const))
                    {
                        make_unused_array(remain, remain_size);
                        allocator.thread_item_front(gen_allocator, remain, remain_size);
                        System.Diagnostics.Debug.Assert(remain_size >= Align((nuint)GCInterfaceOffsets.min_obj_size, align_const));
                    }
                    else
                    {
                        limit = unchecked(limit + remain_size);
                    }

                    generation.generation_free_list_space(gen) = unchecked(generation.generation_free_list_space(gen) - limit);
                    System.Diagnostics.Debug.Assert(unchecked((nint)generation.generation_free_list_space(gen)) >= 0);

                    adjust_limit_clr(
                        free_list,
                        limit,
                        size,
                        acontext,
                        flags,
                        null,
                        align_const,
                        gen_number,
                        generation_table,
                        null,
                        null,
                        total_alloc_bytes,
                        more_space_context,
                        callback);

                    can_fit = true;
                    goto end;
                }
                else if (gen_allocator->discard_if_no_fit_p() != 0)
                {
                    generation.generation_free_obj_space(gen) = unchecked(generation.generation_free_obj_space(gen) + free_list_size);

                    allocator.unlink_item(gen_allocator, a_l_idx, free_list, prev_free_item, false);
                    generation.generation_free_list_space(gen) = unchecked(generation.generation_free_list_space(gen) - free_list_size);
                    System.Diagnostics.Debug.Assert(unchecked((nint)generation.generation_free_list_space(gen)) >= 0);
                }
                else
                {
                    prev_free_item = free_list;
                }

                free_list = allocator.free_list_slot(free_list);
            }
        }

    end:
        return can_fit;
    }

    public static bool a_fit_free_list_uoh_p(
        nuint size,
        gc_alloc_context* acontext,
        uint flags,
        int align_const,
        int gen_number,
        dynamic_data* dd,
        nuint allocation_quantum,
        generation* generation_table,
        ulong* total_alloc_bytes,
        try_allocate_more_space_context* more_space_context = null,
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback = null)
    {
        bool can_fit = false;
        generation* gen = generation_of(generation_table, gen_number);
        allocator* gen_allocator = generation.generation_allocator(gen);

        for (uint a_l_idx = gen_allocator->first_suitable_bucket(size);
             a_l_idx < gen_allocator->number_of_buckets();
             a_l_idx++)
        {
            byte* free_list = allocator.alloc_list_head_of(gen_allocator, a_l_idx);
            byte* prev_free_item = null;

            while (free_list is not null)
            {
                nuint free_list_size = unused_array_size(free_list);
                nint diff = unchecked((nint)(free_list_size - size));

                if ((diff == 0) || (diff >= unchecked((nint)Align((nuint)GCInterfaceOffsets.min_obj_size, align_const))))
                {
                    allocator.unlink_item(gen_allocator, a_l_idx, free_list, prev_free_item, false);

                    nuint limit = limit_from_size(
                        dd,
                        allocation_quantum,
                        unchecked(size - Align((nuint)GCInterfaceOffsets.min_obj_size, align_const)),
                        flags,
                        free_list_size,
                        gen_number,
                        align_const);
                    dynamic_data.dd_new_allocation(dd) = unchecked(dynamic_data.dd_new_allocation(dd) - (nint)limit);

                    nuint saved_free_list_size = free_list_size;
                    byte* remain = (byte*)unchecked((nuint)free_list + limit);
                    nuint remain_size = unchecked(free_list_size - limit);
                    if (remain_size != 0)
                    {
                        System.Diagnostics.Debug.Assert(remain_size >= Align((nuint)GCInterfaceOffsets.min_obj_size, align_const));
                        make_unused_array(remain, remain_size);
                    }

                    if (remain_size >= Align(unchecked((nuint)2 * (nuint)GCInterfaceOffsets.min_obj_size), align_const))
                    {
                        generation.generation_free_list_space(gen) = unchecked(generation.generation_free_list_space(gen) + remain_size);
                        allocator.thread_item_front(gen_allocator, remain, remain_size);
                        System.Diagnostics.Debug.Assert(remain_size >= Align((nuint)GCInterfaceOffsets.min_obj_size, align_const));
                    }
                    else
                    {
                        generation.generation_free_obj_space(gen) = unchecked(generation.generation_free_obj_space(gen) + remain_size);
                    }

                    generation.generation_free_list_space(gen) = unchecked(generation.generation_free_list_space(gen) - saved_free_list_size);
                    System.Diagnostics.Debug.Assert(unchecked((nint)generation.generation_free_list_space(gen)) >= 0);
                    generation.generation_free_list_allocated(gen) = unchecked(generation.generation_free_list_allocated(gen) + limit);

                    adjust_limit_clr(
                        free_list,
                        limit,
                        size,
                        acontext,
                        flags,
                        null,
                        align_const,
                        gen_number,
                        generation_table,
                        null,
                        null,
                        total_alloc_bytes,
                        more_space_context,
                        callback);

                    // Fix the limit to compensate for adjust_limit_clr making it too short.
                    acontext->alloc_limit = (byte*)unchecked(
                        (nuint)acontext->alloc_limit + Align((nuint)GCInterfaceOffsets.min_obj_size, align_const));
                    can_fit = true;
                    goto exit;
                }

                prev_free_item = free_list;
                free_list = allocator.free_list_slot(free_list);
            }
        }

    exit:
        return can_fit;
    }

    // gcpriv.h defines plug_skew as sizeof(ObjHeader). NativeAOT's ObjHeader has the native
    // pointer width, so this preserves the native expression without a managed object header.
    private static nuint plug_skew => (nuint)sizeof(nuint);

#if TARGET_64BIT && !TARGET_WASM
    private static nuint min_free_item_no_prev =>
        (nuint)GCInterfaceOffsets.min_obj_size + (nuint)sizeof(byte*);
#endif

    // This is the `uint8_t*& allocated` selection and `allocated += limit` from
    // a_fit_segment_end_p. alloc_allocated is a deferred gc_heap field, so it remains an
    // explicit pointer for the SOH branch; UOH updates the supplied segment directly.
    public static void advance_allocated(byte** alloc_allocated, heap_segment* seg, nuint limit, int gen_number)
    {
        if (gen_number == 0)
        {
            *alloc_allocated = (byte*)unchecked((nuint)(*alloc_allocated) + limit);
        }
        else
        {
            heap_segment.heap_segment_allocated(seg) =
                (byte*)unchecked((nuint)heap_segment.heap_segment_allocated(seg) + limit);
        }
    }

    private static nuint commit_min_th => unchecked((nuint)16 * GCToOSInterface.GetPageSize());

    // The heap number remains explicit until the translated gc_heap owns the native heap_number
    // field. The other state belongs to the segment and the existing commit accounting helpers.
    public static bool grow_heap_segment(
        heap_segment* seg,
        byte* high_address,
        int heap_number,
        bool* hard_limit_exceeded_p = null)
    {
        System.Diagnostics.Debug.Assert(high_address <= heap_segment.heap_segment_reserved(seg));

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

            System.Diagnostics.Debug.Assert(heap_segment.heap_segment_committed(seg) <= heap_segment.heap_segment_reserved(seg));
            System.Diagnostics.Debug.Assert(high_address <= heap_segment.heap_segment_committed(seg));
        }

        return ret;
    }

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

#if USE_REGIONS && !MULTIPLE_HEAPS
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
                System.Diagnostics.Debug.Fail("ran out regions when getting the next alloc seg!");
            }
        }

        if (region != saved_region)
        {
            init_alloc_info(gen, region);
        }

        return region;
    }

    public static bool decide_on_gen1_pin_promotion(float pin_frag_ratio, float pin_surv_ratio)
    {
        return pin_frag_ratio > 0.15f && pin_surv_ratio > 0.30f;
    }

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

            // plan_gen_num is not set until a region is planned. For a pin in the region being
            // planned, use the destination generation supplied by the caller.
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

    public static void attribute_pin_higher_gen_alloc(
        gc_heap* hp,
        int frgn,
        int togn,
        nuint len)
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
        System.Diagnostics.Debug.Assert(size >= Align((nuint)GCInterfaceOffsets.min_obj_size));
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
                    update_planned_gen0_free_space(pinned_len(pinned_plug_entry), plug);
                }

                System.Diagnostics.Debug.Assert(
                    mark_stack_array[entry].len == 0 ||
                    mark_stack_array[entry].len >= Align((nuint)GCInterfaceOffsets.min_obj_size));
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
                System.Diagnostics.Debug.Assert(
                    generation.generation_allocation_pointer(gen) >=
                    heap_segment.heap_segment_mem(seg));

                if (pinned_plug_que_empty_p(hp) == 0 &&
                    pinned_plug(oldest_pin(hp)) < heap_segment.heap_segment_allocated(seg) &&
                    pinned_plug(oldest_pin(hp)) >= generation.generation_allocation_pointer(gen))
                {
                    GCToEEInterface.HandleFatalError(CORINFO_EXCEPTION_GC);
                }

                System.Diagnostics.Debug.Assert(
                    generation.generation_allocation_pointer(gen) >=
                    heap_segment.heap_segment_mem(seg));
                System.Diagnostics.Debug.Assert(
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
                    System.Diagnostics.Debug.Fail("should not happen for regions!");
                }
            }

            set_allocator_next_pin(hp, gen);
            goto retry;
        }

        System.Diagnostics.Debug.Assert(
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
            System.Diagnostics.Debug.Assert(same_large_alignment_p(result + (nint)pad, old_loc));
        }

        if (next_pinned_plug is not null &&
            pad != 0 &&
            generation.generation_allocation_segment(gen) == current_seg)
        {
            System.Diagnostics.Debug.Assert(old_loc is not null);
            nint dist_to_next_pin = unchecked(
                (nint)(next_pinned_plug -
                    (generation.generation_allocation_pointer(gen) + (nint)size + (nint)pad)));
            System.Diagnostics.Debug.Assert(dist_to_next_pin >= 0);

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
        System.Diagnostics.Debug.Assert(
            generation.generation_allocation_pointer(gen) <=
            generation.generation_allocation_limit(gen));

        if (pad > 0 && to_gen_number >= 0)
        {
            generation.generation_free_obj_space(
                generation_of(generation_table_of(hp), to_gen_number)) += pad;
        }

        System.Diagnostics.Debug.Assert(result + (nint)pad is not null);
        return result + (nint)pad;
    }
#endif

    // The untranslated gc_heap layout owns dynamic_data_table, allocation_quantum,
    // generation_table, alloc_allocated, ephemeral_heap_segment, the selected allocation-byte
    // counter, and heap_number. They remain explicit so this leaf can preserve native state
    // transitions without introducing a partial managed heap.
    public static bool a_fit_segment_end_p(
        int gen_number,
        heap_segment* seg,
        nuint size,
        gc_alloc_context* acontext,
        uint flags,
        int align_const,
        bool* commit_failed_p,
        dynamic_data* dd,
        nuint allocation_quantum,
        generation* generation_table,
        heap_segment* ephemeral_heap_segment,
        byte** alloc_allocated,
        ulong* total_alloc_bytes,
        int heap_number,
        try_allocate_more_space_context* more_space_context = null,
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback = null)
    {
        *commit_failed_p = false;
        nuint limit;
        bool hard_limit_short_seg_end_p = false;

        byte* allocated = gen_number == 0
            ? *alloc_allocated
            : heap_segment.heap_segment_allocated(seg);
        nuint pad = Align((nuint)GCInterfaceOffsets.min_obj_size, align_const);

        byte* end = (byte*)unchecked((nuint)heap_segment.heap_segment_committed(seg) - pad);
        if (a_size_fit_p(size, allocated, end, align_const))
        {
            limit = limit_from_size(
                dd,
                allocation_quantum,
                size,
                flags,
                unchecked((nuint)(end - allocated)),
                gen_number,
                align_const);
        }
        else
        {
            end = (byte*)unchecked((nuint)heap_segment.heap_segment_reserved(seg) - pad);
            if ((heap_segment.heap_segment_reserved(seg) == heap_segment.heap_segment_committed(seg)) ||
                !a_size_fit_p(size, allocated, end, align_const))
            {
                return false;
            }

            limit = limit_from_size(
                dd,
                allocation_quantum,
                size,
                flags,
                unchecked((nuint)(end - allocated)),
                gen_number,
                align_const);

            if (!grow_heap_segment(
                seg,
                (byte*)unchecked((nuint)allocated + limit),
                heap_number,
                &hard_limit_short_seg_end_p))
            {
                // The USE_REGIONS native branch reports every grow failure to its caller. The
                // caller distinguishes it from a short segment through commit_failed_p.
                *commit_failed_p = true;
                return false;
            }
        }

        dynamic_data.dd_new_allocation(dd) = unchecked(dynamic_data.dd_new_allocation(dd) - (nint)limit);

        if ((flags & (uint)GC_ALLOC_FLAGS.GC_ALLOC_ZEROING_OPTIONAL) != 0 &&
            ((allocated == acontext->alloc_limit) ||
             (allocated == (byte*)unchecked((nuint)acontext->alloc_limit + pad))))
        {
            System.Diagnostics.Debug.Assert(gen_number == 0);
            System.Diagnostics.Debug.Assert(allocated > acontext->alloc_ptr);

            nuint extra = unchecked((nuint)(allocated - acontext->alloc_ptr));
            limit = unchecked(limit - extra);
            dynamic_data.dd_new_allocation(dd) = unchecked(dynamic_data.dd_new_allocation(dd) + (nint)extra);
            limit = unchecked(limit + pad);
        }

        byte* old_alloc = allocated;
        advance_allocated(alloc_allocated, seg, limit, gen_number);
        adjust_limit_clr(
            old_alloc,
            limit,
            size,
            acontext,
            flags,
            seg,
            align_const,
            gen_number,
            generation_table,
            ephemeral_heap_segment,
            alloc_allocated is not null ? *alloc_allocated : null,
            total_alloc_bytes,
            more_space_context,
            callback);

        return true;
    }

    public static bool uoh_a_fit_segment_end_p(
        int gen_number,
        nuint size,
        gc_alloc_context* acontext,
        uint flags,
        int align_const,
        bool* commit_failed_p,
        oom_reason* oom_r,
        dynamic_data* dd,
        nuint allocation_quantum,
        generation* generation_table,
        heap_segment* ephemeral_heap_segment,
        byte** alloc_allocated,
        ulong* total_alloc_bytes,
        int heap_number,
        try_allocate_more_space_context* more_space_context = null,
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback = null)
    {
        *commit_failed_p = false;

        generation* gen = generation_of(generation_table, gen_number);
        heap_segment* seg = generation.generation_allocation_segment(gen);
        bool can_allocate_p = false;
        nuint pad = Align((nuint)GCInterfaceOffsets.min_obj_size, align_const);

        while (seg is not null)
        {
            if (a_fit_segment_end_p(
                gen_number,
                seg,
                unchecked(size - pad),
                acontext,
                flags,
                align_const,
                commit_failed_p,
                dd,
                allocation_quantum,
                generation_table,
                ephemeral_heap_segment,
                alloc_allocated,
                total_alloc_bytes,
                heap_number,
                more_space_context,
                callback))
            {
                acontext->alloc_limit = (byte*)unchecked((nuint)acontext->alloc_limit + pad);
                can_allocate_p = true;
                break;
            }

            if (*commit_failed_p)
            {
                *oom_r = oom_reason.oom_cant_commit;
                break;
            }

            seg = heap_segment_next_rw(seg);
        }

        if (can_allocate_p)
        {
            generation.generation_end_seg_allocated(gen) =
                unchecked(generation.generation_end_seg_allocated(gen) + size);
        }

        return can_allocate_p;
    }

    // The dynamic plan computes sufficient_space_regions_for_allocation before this allocation
    // path runs. Its inputs are explicit here because the heap fields and the planning policy
    // that produces them are not translated yet.
    public static bool short_on_end_of_seg(
        bool sufficient_space_regions_for_allocation_p,
        bool sufficient_gen0_space_p)
    {
        bool sufficient_p = sufficient_space_regions_for_allocation_p;
        if (!sufficient_p)
        {
            sufficient_p = sufficient_gen0_space_p;
        }

        return !sufficient_p;
    }

    public static void fix_allocation_context(
        gc_alloc_context* acontext,
        bool for_gc_p,
        bool record_ac_p,
        generation* generation_table,
        heap_segment* ephemeral_heap_segment,
        byte** alloc_allocated,
        ulong* total_alloc_bytes_soh)
    {
        if (acontext->alloc_ptr is null)
        {
            return;
        }

        int align_const = get_alignment_constant(true);
        nuint aligned_min_obj_size = Align((nuint)GCInterfaceOffsets.min_obj_size, align_const);
        bool is_ephemeral_heap_segment =
            in_range_for_segment(acontext->alloc_limit, ephemeral_heap_segment) != 0;

        if (!is_ephemeral_heap_segment ||
            unchecked((nuint)(*alloc_allocated - acontext->alloc_limit)) > aligned_min_obj_size ||
            !for_gc_p)
        {
            byte* point = acontext->alloc_ptr;
            nuint size = unchecked((nuint)(acontext->alloc_limit - acontext->alloc_ptr) + aligned_min_obj_size);
            make_unused_array(point, size);
            if (for_gc_p)
            {
                generation* gen0 = generation_of(generation_table, (int)gc_generation_num.soh_gen0);
                generation.generation_free_obj_space(gen0) =
                    unchecked(generation.generation_free_obj_space(gen0) + size);
            }
        }
        else if (for_gc_p)
        {
            *alloc_allocated = acontext->alloc_ptr;
            System.Diagnostics.Debug.Assert(
                heap_segment.heap_segment_allocated(ephemeral_heap_segment) <=
                heap_segment.heap_segment_committed(ephemeral_heap_segment));
        }

        if (for_gc_p)
        {
            retire_allocation_context(acontext, total_alloc_bytes_soh);
#if USE_REGIONS
            if (record_ac_p)
            {
                alloc_contexts_used++;
            }
#endif
        }
    }

    // This is precisely the for_gc_p == true call made before an ephemeral-region rollover.
    public static void fix_allocation_context_for_region_rollover(
        gc_alloc_context* acontext,
        generation* generation_table,
        heap_segment* ephemeral_heap_segment,
        byte** alloc_allocated,
        ulong* total_alloc_bytes_soh)
    {
        fix_allocation_context(
            acontext,
            true,
            false,
            generation_table,
            ephemeral_heap_segment,
            alloc_allocated,
            total_alloc_bytes_soh);
    }

#if USE_REGIONS
    private struct fix_alloc_context_args
    {
        public int for_gc_p;
        public gc_heap* heap;
    }

    [UnmanagedCallersOnly]
    public static void fix_alloc_context(gc_alloc_context* acontext, void* param)
    {
        fix_alloc_context_args* args = (fix_alloc_context_args*)param;
        gc_heap* heap = args->heap;
        fix_allocation_context(
            acontext,
            args->for_gc_p != 0,
            true,
            generation_table_of(heap),
            heap->ephemeral_heap_segment,
            &heap->alloc_allocated,
            &heap->total_alloc_bytes_soh);
    }

    public static void fix_allocation_contexts(gc_heap* heap, bool for_gc_p)
    {
        fix_alloc_context_args args = default;
        args.for_gc_p = for_gc_p ? 1 : 0;
        args.heap = heap;

        GCToEEInterface.GcEnumAllocContexts(&fix_alloc_context, &args);
        fix_youngest_allocation_area(
            generation_of(generation_table_of(heap), (int)gc_generation_num.soh_gen0),
            heap->ephemeral_heap_segment,
            heap->alloc_allocated);
    }
#endif

    public static void fix_youngest_allocation_area(
        generation* youngest_generation,
        heap_segment* ephemeral_heap_segment,
        byte* alloc_allocated)
    {
        System.Diagnostics.Debug.Assert(generation.generation_allocation_pointer(youngest_generation) is null);
        System.Diagnostics.Debug.Assert(generation.generation_allocation_limit(youngest_generation) is null);

        heap_segment.heap_segment_allocated(ephemeral_heap_segment) = alloc_allocated;
        System.Diagnostics.Debug.Assert(
            heap_segment.heap_segment_mem(ephemeral_heap_segment) <=
            heap_segment.heap_segment_allocated(ephemeral_heap_segment));
        System.Diagnostics.Debug.Assert(
            heap_segment.heap_segment_allocated(ephemeral_heap_segment) <=
            heap_segment.heap_segment_reserved(ephemeral_heap_segment));
    }

    public static void fix_older_allocation_area(generation* older_gen)
    {
        heap_segment* older_gen_segment =
            generation.generation_allocation_segment(older_gen);
        if (generation.generation_allocation_limit(older_gen) !=
            heap_segment.heap_segment_plan_allocated(older_gen_segment))
        {
            byte* point = generation.generation_allocation_pointer(older_gen);
            nuint free_size = (nuint)(
                generation.generation_allocation_limit(older_gen) -
                generation.generation_allocation_pointer(older_gen));
            if (free_size != 0)
            {
                Debug.Assert(
                    free_size >= Align((nuint)GCInterfaceOffsets.min_obj_size));
                make_unused_array(point, free_size);
                if (free_size >=
                    2 * (nuint)GCInterfaceOffsets.min_obj_size)
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
            Debug.Assert(
                older_gen_segment !=
                ManagedGCRegionBootstrap.Heap->ephemeral_heap_segment);
            heap_segment.heap_segment_plan_allocated(older_gen_segment) =
                generation.generation_allocation_pointer(older_gen);
            generation.generation_allocation_limit(older_gen) =
                generation.generation_allocation_pointer(older_gen);
        }

        generation.generation_allocation_pointer(older_gen) = null;
        generation.generation_allocation_limit(older_gen) = null;
    }

    // The heap-owned inputs stay explicit until try_allocate_more_space owns the allocation
    // policy. Region acquisition uses the already-translated get_new_region helper; allocation
    // diagnostics remain deferred with the diagnostics and production-routing slices.
    public static bool soh_try_fit(
        int gen_number,
        nuint size,
        gc_alloc_context* acontext,
        uint flags,
        int align_const,
        bool* commit_failed_p,
        bool* short_seg_end_p,
        bool sufficient_space_regions_for_allocation_p,
        bool sufficient_gen0_space_p,
        dynamic_data* dd,
        nuint allocation_quantum,
        generation* generation_table,
        heap_segment** ephemeral_heap_segment,
        byte** alloc_allocated,
        ulong* total_alloc_bytes_soh,
        int heap_number,
        gc_heap* hp,
        try_allocate_more_space_context* more_space_context = null,
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback = null)
    {
        if (short_seg_end_p is not null)
        {
            *short_seg_end_p = false;
        }

        bool can_allocate = a_fit_free_list_p(
            gen_number,
            size,
            acontext,
            flags,
            align_const,
            dd,
            allocation_quantum,
            generation_table,
            total_alloc_bytes_soh,
            more_space_context,
            callback);

        if (can_allocate)
        {
            return true;
        }

        if (short_seg_end_p is not null)
        {
            *short_seg_end_p = short_on_end_of_seg(
                sufficient_space_regions_for_allocation_p,
                sufficient_gen0_space_p);
        }

        if (short_seg_end_p is not null && *short_seg_end_p)
        {
            return false;
        }

        while (*ephemeral_heap_segment is not null)
        {
            heap_segment* current_ephemeral_heap_segment = *ephemeral_heap_segment;
            can_allocate = a_fit_segment_end_p(
                gen_number,
                current_ephemeral_heap_segment,
                size,
                acontext,
                flags,
                align_const,
                commit_failed_p,
                dd,
                allocation_quantum,
                generation_table,
                current_ephemeral_heap_segment,
                alloc_allocated,
                total_alloc_bytes_soh,
                heap_number,
                more_space_context,
                callback);
            if (can_allocate)
            {
                return true;
            }

            fix_allocation_context_for_region_rollover(
                acontext,
                generation_table,
                current_ephemeral_heap_segment,
                alloc_allocated,
                total_alloc_bytes_soh);
            fix_youngest_allocation_area(
                generation_of(generation_table, (int)gc_generation_num.soh_gen0),
                current_ephemeral_heap_segment,
                *alloc_allocated);

            heap_segment* next_seg = heap_segment.heap_segment_next(current_ephemeral_heap_segment);
            if (next_seg is null)
            {
                generation* gen = generation_of(generation_table, gen_number);
                System.Diagnostics.Debug.Assert(current_ephemeral_heap_segment == generation.generation_tail_region(gen));
                next_seg = get_new_region(generation_table, hp, gen_number);
            }

            if (next_seg is null)
            {
                *commit_failed_p = true;
                return false;
            }

            *ephemeral_heap_segment = next_seg;
            *alloc_allocated = heap_segment.heap_segment_allocated(next_seg);
        }

        return false;
    }

    public static bool uoh_try_fit(
        int gen_number,
        nuint size,
        gc_alloc_context* acontext,
        uint flags,
        int align_const,
        bool* commit_failed_p,
        oom_reason* oom_r,
        dynamic_data* dd,
        nuint allocation_quantum,
        generation* generation_table,
        heap_segment* ephemeral_heap_segment,
        byte** alloc_allocated,
        ulong* total_alloc_bytes,
        int heap_number,
        try_allocate_more_space_context* more_space_context = null,
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback = null)
    {
        if (a_fit_free_list_uoh_p(
            size,
            acontext,
            flags,
            align_const,
            gen_number,
            dd,
            allocation_quantum,
            generation_table,
            total_alloc_bytes,
            more_space_context,
            callback))
        {
            return true;
        }

        return uoh_a_fit_segment_end_p(
            gen_number,
            size,
            acontext,
            flags,
            align_const,
            commit_failed_p,
            oom_r,
            dd,
            allocation_quantum,
            generation_table,
            ephemeral_heap_segment,
            alloc_allocated,
            total_alloc_bytes,
            heap_number,
            more_space_context,
            callback);
    }

    // `try_allocate_more_space` owns more-space locks, GC triggering, and dynamic allocation
    // policy in the native heap. Until those heap fields exist here, keep the complete mutable
    // allocator state in this unmanaged record and make every such operation an explicit
    // callback. A null callback returns the precise native state at which wiring must resume.
    internal enum allocation_deferred_operation : byte
    {
        none,
        wait_for_gc_done,
        enter_more_space_lock,
        check_for_full_gc,
        check_allocation_budget,
        wait_for_bgc_high_memory,
        trigger_gc_for_budget,
        query_background_running,
        check_and_wait_for_bgc,
        trigger_ephemeral_gc,
        trigger_2nd_ephemeral_gc,
        trigger_full_compact_gc,
        acquire_uoh_segment,
        check_retry_uoh_segment,
        check_retry_other_heap,
        handle_oom,
        leave_more_space_lock,
        invalid_allocation_state,
    }

    internal enum allocation_callback_result_kind : byte
    {
        unsupported,
        completed,
        retry_allocate,
        allocation_allowed,
        allocation_disallowed,
        full_compact_gc,
        no_full_compact_gc,
        background_running,
        background_not_running,
        segment_acquired,
        segment_unavailable,
        retry_full_compact_gc,
        retry_segment,
        retry_other_heap,
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct allocation_callback_result
    {
        public allocation_callback_result_kind kind;
        public oom_reason oom_r;
        public byte did_full_compacting_gc_p;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal unsafe struct try_allocate_more_space_context
    {
        public gc_alloc_context* acontext;
        public dynamic_data* dd;
        public generation* generation_table;
        public heap_segment** ephemeral_heap_segment;
        public byte** alloc_allocated;
        public ulong* total_alloc_bytes_soh;
        public ulong* total_alloc_bytes_uoh;
        public gc_heap* hp;
        public nuint size;
        public nuint allocation_quantum;
        public uint flags;
        public int gen_number;
        public int align_const;
        public int heap_number;
        public allocation_state state;
        public oom_reason oom_r;
        public allocation_deferred_operation deferred_operation;
        public byte gc_started_p;
        public byte more_space_lock_held_p;
        public byte full_gc_notification_p;
        public byte full_gc_checked_p;
        public byte budget_full_gc_checked_p;
        public byte budget_checked_p;
        public byte bgc_high_memory_waited_p;
        public byte sufficient_space_regions_for_allocation_p;
        public byte sufficient_gen0_space_p;
        public byte commit_failed_p;
        public byte short_seg_end_p;
        public byte oom_handled_p;
        public nuint full_compact_gc_count_before_uoh_acquire;
        // This is set only by the non-collecting managed-GC bootstrap. It permits consumption
        // of already-reserved regions after the native dynamic budget is depleted, without
        // reporting a collection that has not happened.
        public byte non_collecting_bootstrap_budget_p;
    }

#if USE_REGIONS
    // CLR_SIZE in gcinternal.h. Dynamic tuning owns later adjustments to this value.
    public const nuint DefaultAllocationQuantum = 8 * 1024 + 32;
    private const nuint MinGen0Size = 64 * 1024;
    private const nuint MinSegmentSize = 4 * 1024 * 1024;
    private const nuint InitialAlloc = 256 * 1024 * 1024;
    private const nuint MaxGen0Size = 200 * 1024 * 1024;
    private const nuint MinGen1MaxSize = 6 * 1024 * 1024;
    private const nuint MinUohSize = 3 * 1024 * 1024;

    // dynamic_tuning.cpp keeps this table in native static storage. These are individual
    // unmanaged fields rather than a managed array so initialization is explicit and no
    // managed reference enters the collector.
    private static static_data static_data_table_memory_footprint0;
    private static static_data static_data_table_memory_footprint1;
    private static static_data static_data_table_memory_footprint2;
    private static static_data static_data_table_memory_footprint3;
    private static static_data static_data_table_memory_footprint4;
    private static static_data static_data_table_balanced0;
    private static static_data static_data_table_balanced1;
    private static static_data static_data_table_balanced2;
    private static static_data static_data_table_balanced3;
    private static static_data static_data_table_balanced4;
    private static gc_latency_level latency_level;
    internal static bool gc_can_use_concurrent;

    private static static_data* static_data_of(gc_latency_level level, int gen_number)
    {
        System.Diagnostics.Debug.Assert(gen_number >= 0 && gen_number < (int)gc_generation_num.total_generation_count);

        if (level == gc_latency_level.latency_level_memory_footprint)
        {
            return gen_number switch
            {
                0 => (static_data*)Unsafe.AsPointer(ref static_data_table_memory_footprint0),
                1 => (static_data*)Unsafe.AsPointer(ref static_data_table_memory_footprint1),
                2 => (static_data*)Unsafe.AsPointer(ref static_data_table_memory_footprint2),
                3 => (static_data*)Unsafe.AsPointer(ref static_data_table_memory_footprint3),
                _ => (static_data*)Unsafe.AsPointer(ref static_data_table_memory_footprint4),
            };
        }

        return gen_number switch
        {
            0 => (static_data*)Unsafe.AsPointer(ref static_data_table_balanced0),
            1 => (static_data*)Unsafe.AsPointer(ref static_data_table_balanced1),
            2 => (static_data*)Unsafe.AsPointer(ref static_data_table_balanced2),
            3 => (static_data*)Unsafe.AsPointer(ref static_data_table_balanced3),
            _ => (static_data*)Unsafe.AsPointer(ref static_data_table_balanced4),
        };
    }

    private static void initialize_static_data(
        static_data* sdata,
        nuint min_size,
        nuint max_size,
        nuint fragmentation_limit,
        float fragmentation_burden_limit,
        float limit,
        float max_limit,
        ulong time_clock,
        nuint gc_clock)
    {
        sdata->min_size = min_size;
        sdata->max_size = max_size;
        sdata->fragmentation_limit = fragmentation_limit;
        sdata->fragmentation_burden_limit = fragmentation_burden_limit;
        sdata->limit = limit;
        sdata->max_limit = max_limit;
        sdata->time_clock = time_clock;
        sdata->gc_clock = gc_clock;
    }

    // This is the literal static_data_table initializer from dynamic_tuning.cpp. init_static_data
    // below supplies the gen0/gen1 sizes whose native initializer computes from configuration.
    private static void initialize_static_data_table()
    {
        nuint ssizeTMax = nuint.MaxValue >> 1;

        initialize_static_data(static_data_of(gc_latency_level.latency_level_memory_footprint, 0), 0, 0, 40_000, 0.5f, 9.0f, 20.0f, 1_000_000, 1);
        initialize_static_data(static_data_of(gc_latency_level.latency_level_memory_footprint, 1), 160 * 1024, 0, 80_000, 0.5f, 2.0f, 7.0f, 10_000_000, 10);
        initialize_static_data(static_data_of(gc_latency_level.latency_level_memory_footprint, 2), 256 * 1024, ssizeTMax, 200_000, 0.25f, 1.2f, 1.8f, 100_000_000, 100);
        initialize_static_data(static_data_of(gc_latency_level.latency_level_memory_footprint, 3), MinUohSize, ssizeTMax, 0, 0.0f, 1.25f, 4.5f, 0, 0);
        initialize_static_data(static_data_of(gc_latency_level.latency_level_memory_footprint, 4), MinUohSize, ssizeTMax, 0, 0.0f, 1.25f, 4.5f, 0, 0);

        initialize_static_data(static_data_of(gc_latency_level.latency_level_balanced, 0), 0, 0, 40_000, 0.5f, 9.0f, 20.0f, 1_000_000, 1);
        initialize_static_data(static_data_of(gc_latency_level.latency_level_balanced, 1), 256 * 1024, 0, 80_000, 0.5f, 2.0f, 7.0f, 10_000_000, 10);
        initialize_static_data(static_data_of(gc_latency_level.latency_level_balanced, 2), 256 * 1024, ssizeTMax, 200_000, 0.25f, 1.2f, 1.8f, 100_000_000, 100);
        initialize_static_data(static_data_of(gc_latency_level.latency_level_balanced, 3), MinUohSize, ssizeTMax, 0, 0.0f, 1.25f, 4.5f, 0, 0);
        initialize_static_data(static_data_of(gc_latency_level.latency_level_balanced, 4), MinUohSize, ssizeTMax, 0, 0.0f, 1.25f, 4.5f, 0, 0);
    }

    private static nuint round_up_power2(nuint size)
    {
        uint highest_set_bit_index;
#if TARGET_64BIT
        if (GCEnv.BitScanReverse64(&highest_set_bit_index, (ulong)(size - 1)) == 0)
#else
        if (GCEnv.BitScanReverse(&highest_set_bit_index, (uint)(size - 1)) == 0)
#endif
        {
            return 1;
        }

        return (nuint)2 << (int)highest_set_bit_index;
    }

    // get_valid_segment_size(FALSE) for the 64-bit workstation collector. Region size is
    // deliberately not substituted here: gc.cpp continues to use soh_segment_size for tuning
    // even when USE_REGIONS selects basic-region allocation.
    private static nuint get_valid_segment_size()
    {
        nuint segment_size = unchecked((nuint)GCConfig.GetSegmentSize());
        if ((segment_size & (1024 * 1024 - 1)) != 0 || (segment_size >> 22) == 0)
        {
            segment_size = ((segment_size >> 1) != 0 && (segment_size >> 22) == 0)
                ? MinSegmentSize
                : InitialAlloc;
        }

        return round_up_power2(segment_size);
    }

    // The physical-memory adjustment is the dependency-closed WKS half of
    // gc_heap::get_gen0_min_size. Hard-limit initialization is still owned by the unported
    // initialize_gc configuration path, so its zero state deliberately keeps that native branch
    // inactive rather than synthesizing a limit here.
    private static nuint get_gen0_min_size(nuint soh_segment_size)
    {
        nuint gen0size = unchecked((nuint)GCConfig.GetGen0Size());
        bool is_config_invalid = gen0size == 0 || gen0size < MinGen0Size;
        if (is_config_invalid)
        {
            nuint true_size = GCToOSInterface.GetCacheSizePerLogicalCpu(true);
            gen0size = unchecked(4 * true_size / 5);
            if (gen0size < 256 * 1024)
            {
                gen0size = 256 * 1024;
            }

            if (true_size < 256 * 1024)
            {
                true_size = 256 * 1024;
            }

            ulong total_physical_mem = GCConfig.GetGCTotalPhysicalMemory() != 0
                ? unchecked((ulong)GCConfig.GetGCTotalPhysicalMemory())
                : GCToOSInterface.GetPhysicalMemoryLimit();
            if (total_physical_mem != 0)
            {
                while (gen0size > total_physical_mem / 6)
                {
                    gen0size /= 2;
                    if (gen0size <= true_size)
                    {
                        gen0size = true_size;
                        break;
                    }
                }
            }
        }

        if (gen0size >= soh_segment_size / 2)
        {
            gen0size = soh_segment_size / 2;
        }

        if (is_config_invalid)
        {
            if (heap_hard_limit != 0)
            {
                nuint gen0size_seg = soh_segment_size / 8;
                if (gen0size >= gen0size_seg)
                {
                    gen0size = gen0size_seg;
                }
            }

            gen0size = gen0size / 8 * 5;
        }

        return Align(gen0size);
    }

    // init_static_data from dynamic_tuning.cpp. The collector has not yet ported
    // compute_hard_limit, so heap_hard_limit remains the native zero/unconfigured state here.
    private static void init_static_data()
    {
        nuint soh_segment_size = get_valid_segment_size();
        nuint gen0_min_size = get_gen0_min_size(soh_segment_size);
        nuint gen0_max_size;
        nuint gen0_max_size_config = unchecked((nuint)GCConfig.GetGCGen0MaxBudget());

        if (gen0_max_size_config != 0)
        {
            gen0_max_size = gen0_max_size_config;
        }
        else
        {
#if BACKGROUND_GC && !MULTIPLE_HEAPS
            if (gc_can_use_concurrent)
            {
                gen0_max_size = MinGen1MaxSize;
            }
            else
#endif
            {
                nuint default_max_size = soh_segment_size / 2;
                if (default_max_size > MaxGen0Size)
                {
                    default_max_size = MaxGen0Size;
                }

                gen0_max_size = default_max_size > MinGen1MaxSize ? default_max_size : MinGen1MaxSize;
            }

            if (gen0_max_size < gen0_min_size)
            {
                gen0_max_size = gen0_min_size;
            }

            if (heap_hard_limit != 0)
            {
                nuint gen0_max_size_seg = soh_segment_size / 4;
                if (gen0_max_size > gen0_max_size_seg)
                {
                    gen0_max_size = gen0_max_size_seg;
                }
            }
        }

        gen0_max_size = Align(gen0_max_size);
        if (gen0_min_size > gen0_max_size)
        {
            gen0_min_size = gen0_max_size;
        }

        GCConfig.SetGCGen0MaxBudget(unchecked((long)gen0_max_size));

#if BACKGROUND_GC && !MULTIPLE_HEAPS
        nuint gen1_max_size = gc_can_use_concurrent ? MinGen1MaxSize : soh_segment_size / 2;
#else
        nuint gen1_max_size = soh_segment_size / 2;
#endif
        if (gen1_max_size < MinGen1MaxSize)
        {
            gen1_max_size = MinGen1MaxSize;
        }

        nuint gen1_max_size_config = unchecked((nuint)GCConfig.GetGCGen1MaxBudget());
        if (gen1_max_size_config != 0 && gen1_max_size > gen1_max_size_config)
        {
            gen1_max_size = gen1_max_size_config;
        }

        gen1_max_size = Align(gen1_max_size);

        for (int i = (int)gc_latency_level.latency_level_first;
             i <= (int)gc_latency_level.latency_level_last;
             i++)
        {
            static_data* gen0 = static_data_of((gc_latency_level)i, (int)gc_generation_num.soh_gen0);
            static_data* gen1 = static_data_of((gc_latency_level)i, (int)gc_generation_num.soh_gen1);
            gen0->min_size = gen0_min_size;
            gen0->max_size = gen0_max_size;
            gen1->max_size = gen1_max_size;
        }
    }

    // This is the WRITE_WATCH / BACKGROUND_GC initialization branch of initialize_gc. It only
    // establishes the capability value consumed by init_static_data; it does not start or claim
    // to implement background collection.
    private static void initialize_concurrent_gc()
    {
#if BACKGROUND_GC
        // FEATURE_USE_SOFTWARE_WRITE_WATCH_FOR_GC_HEAP is enabled on every architecture that
        // builds this managed collector, so native can_use_write_watch_for_gc_heap() is true.
        gc_can_use_concurrent = GCConfig.GetConcurrentGC() != 0;
        GCConfig.SetConcurrentGC(gc_can_use_concurrent ? (byte)1 : (byte)0);
#else
        gc_can_use_concurrent = false;
        GCConfig.SetConcurrentGC(0);
#endif
    }

    // set_static_data and init_dynamic_data from dynamic_tuning.cpp. This initializes only the
    // data allocator policy consumes; collection-time tuning remains deferred.
    private static void set_static_data(gc_heap* hp)
    {
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            dynamic_data* dd = dynamic_data_of(hp, i);
            static_data* sdata = static_data_of(latency_level, i);
            dd->sdata = sdata;
            dd->min_size = sdata->min_size;
        }
    }

    private static void init_dynamic_data(gc_heap* hp)
    {
        initialize_static_data_table();
        initialize_concurrent_gc();

        latency_level = gc_latency_level.latency_level_default;
        int latency_level_from_config = unchecked((int)GCConfig.GetLatencyLevel());
        if (latency_level_from_config >= (int)gc_latency_level.latency_level_first &&
            latency_level_from_config <= (int)gc_latency_level.latency_level_last)
        {
            latency_level = (gc_latency_level)latency_level_from_config;
        }

        init_static_data();
        set_static_data(hp);

        ulong now = GCCommon.GetHighPrecisionTimeStamp();
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            dynamic_data* dd = dynamic_data_of(hp, i);
            dd->gc_clock = 0;
            dd->time_clock = now;
            dd->previous_time_clock = now;
            dd->current_size = 0;
            dd->promoted_size = 0;
            dd->collection_count = 0;
            dd->new_allocation = unchecked((nint)dd->min_size);
            dd->gc_new_allocation = dd->new_allocation;
            dd->desired_allocation = unchecked((nuint)dd->new_allocation);
            dd->fragmentation = 0;
        }
    }

    public static generation* generation_table_of(gc_heap* hp) => &hp->generation_table0;

    public static dynamic_data* dynamic_data_of(gc_heap* hp, int gen_number)
    {
        System.Diagnostics.Debug.Assert(gen_number >= 0 && gen_number < (int)gc_generation_num.total_generation_count);
        return &hp->dynamic_data_table0 + gen_number;
    }

    private static GCSpinLock* more_space_lock_of(gc_heap* hp, int gen_number)
    {
        return gen_number == (int)gc_generation_num.soh_gen0
            ? &hp->more_space_lock_soh
            : &hp->more_space_lock_uoh;
    }

    // This reproduces the allocation-owned initialization from gc_heap::initialize_gc and the
    // initial dynamic_data setup from dynamic_tuning.cpp.
    public static void initialize_allocation_state(gc_heap* hp)
    {
        generation* generation_table = generation_table_of(hp);
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generation.initialize(generation_table + i);
        }

        GCSpinLock.initialize(&hp->more_space_lock_soh);
        GCSpinLock.initialize(&hp->more_space_lock_uoh);
        init_dynamic_data(hp);
        hp->allocation_running_amount = dynamic_data.dd_min_size(dynamic_data_of(hp, (int)gc_generation_num.soh_gen0));
        hp->allocation_quantum = DefaultAllocationQuantum;
        hp->heap_number = 0;
#if !MULTIPLE_HEAPS
        gen0_bricks_cleared = 0;
#endif
    }

    public static void create_try_allocate_more_space_context(
        gc_heap* hp,
        gc_alloc_context* acontext,
        nuint size,
        uint flags,
        int gen_number,
        try_allocate_more_space_context* context)
    {
        System.Diagnostics.Debug.Assert(gen_number >= 0 && gen_number < (int)gc_generation_num.total_generation_count);

        *context = default;
        context->acontext = acontext;
        context->dd = dynamic_data_of(hp, gen_number);
        context->generation_table = generation_table_of(hp);
        context->ephemeral_heap_segment = &hp->ephemeral_heap_segment;
        context->alloc_allocated = &hp->alloc_allocated;
        context->total_alloc_bytes_soh = &hp->total_alloc_bytes_soh;
        context->total_alloc_bytes_uoh = &hp->total_alloc_bytes_uoh;
        context->hp = hp;
        context->size = size;
        context->allocation_quantum = hp->allocation_quantum;
        context->flags = flags;
        context->gen_number = gen_number;
        context->align_const = get_alignment_constant(gen_number <= (int)gc_generation_num.max_generation);
        context->heap_number = hp->heap_number;
        context->state = allocation_state.a_state_start;
        context->gc_started_p =
            ManagedGCHeap.CollectionStartedForAllocation() ? (byte)1 : (byte)0;
    }

    public static void enable_non_collecting_bootstrap_budget(try_allocate_more_space_context* context)
    {
        context->non_collecting_bootstrap_budget_p = 1;
    }

    public static delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void>
        managed_allocation_callback() => &managed_allocation_callback_impl;

    private static void managed_allocation_callback_impl(
        try_allocate_more_space_context* context,
        int operation_value,
        allocation_callback_result* result)
    {
        *result = default;
        allocation_deferred_operation operation = (allocation_deferred_operation)operation_value;
        gc_heap* hp = context->hp;
        if (hp is null)
        {
            return;
        }

        switch (operation)
        {
            case allocation_deferred_operation.enter_more_space_lock:
                GCSpinLock.enter(more_space_lock_of(hp, context->gen_number));
                result->kind = allocation_callback_result_kind.completed;
                return;

            case allocation_deferred_operation.leave_more_space_lock:
                GCSpinLock.leave(more_space_lock_of(hp, context->gen_number));
                result->kind = allocation_callback_result_kind.completed;
                return;

            case allocation_deferred_operation.check_allocation_budget:
                nint new_allocation = dynamic_data.dd_new_allocation(context->dd);
                if (new_allocation < 0)
                {
                    result->kind = allocation_callback_result_kind.allocation_disallowed;
                    return;
                }

                if (settings.pause_mode != gc_pause_mode.pause_no_gc &&
                    context->gen_number == (int)gc_generation_num.soh_gen0)
                {
                    dynamic_data* dd0 = dynamic_data_of(hp, (int)gc_generation_num.soh_gen0);
                    nuint current_new_allocation = unchecked((nuint)dynamic_data.dd_new_allocation(dd0));
                    if (unchecked(hp->allocation_running_amount - current_new_allocation) >
                        dynamic_data.dd_min_size(dd0))
                    {
                        ulong current_time = GCToOSInterface.GetLowPrecisionTimeStamp();
                        if (unchecked(current_time - hp->allocation_running_time) > 1000)
                        {
                            result->kind = allocation_callback_result_kind.allocation_disallowed;
                            return;
                        }

                        hp->allocation_running_amount = current_new_allocation;
                    }
                }

                result->kind = allocation_callback_result_kind.allocation_allowed;
                return;

            case allocation_deferred_operation.wait_for_gc_done:
                return;

            case allocation_deferred_operation.wait_for_bgc_high_memory:
                if (!background_running_p())
                {
                    result->kind = allocation_callback_result_kind.background_not_running;
                    return;
                }

                background_gc_wait();
                result->kind = allocation_callback_result_kind.background_running;
                return;

            case allocation_deferred_operation.query_background_running:
                result->kind = background_running_p()
                    ? allocation_callback_result_kind.background_running
                    : allocation_callback_result_kind.background_not_running;
                return;

            case allocation_deferred_operation.check_and_wait_for_bgc:
                if (!background_running_p())
                {
                    result->kind = allocation_callback_result_kind.background_not_running;
                    return;
                }

                nuint compactingCollections = full_gc_counts[gc_type_compacting];
                background_gc_wait();
                result->kind = full_gc_counts[gc_type_compacting] > compactingCollections
                    ? allocation_callback_result_kind.full_compact_gc
                    : allocation_callback_result_kind.no_full_compact_gc;
                return;

            case allocation_deferred_operation.trigger_gc_for_budget:
                result->kind = run_allocation_full_collection(
                    context,
                    context->gen_number == (int)gc_generation_num.soh_gen0
                        ? (int)gc_generation_num.soh_gen0
                        : GCInterfaceOffsets.max_generation,
                    context->gen_number == (int)gc_generation_num.soh_gen0
                        ? gc_reason.reason_alloc_soh
                        : gc_reason.reason_alloc_loh,
                    out _)
                    ? allocation_callback_result_kind.completed
                    : allocation_callback_result_kind.unsupported;
                return;

            case allocation_deferred_operation.trigger_ephemeral_gc:
            case allocation_deferred_operation.trigger_2nd_ephemeral_gc:
                bool ephemeralCollectionCompleted = run_allocation_full_collection(
                    context,
                    (int)gc_generation_num.soh_gen1,
                    gc_reason.reason_oos_soh,
                    out bool ephemeralCompacted);
                result->kind =
                    ephemeralCollectionCompleted && ephemeralCompacted
                        ? allocation_callback_result_kind.full_compact_gc
                        : allocation_callback_result_kind.no_full_compact_gc;
                return;

            case allocation_deferred_operation.trigger_full_compact_gc:
                last_gc_before_oom = 1;
                bool fullCollectionCompleted = run_allocation_full_collection(
                    context,
                    GCInterfaceOffsets.max_generation,
                    context->gen_number == (int)gc_generation_num.soh_gen0
                        ? gc_reason.reason_oos_soh
                        : gc_reason.reason_oos_loh,
                    out bool fullCompacted);
                result->kind =
                    fullCollectionCompleted && fullCompacted
                        ? allocation_callback_result_kind.full_compact_gc
                        : allocation_callback_result_kind.no_full_compact_gc;
                return;

            case allocation_deferred_operation.acquire_uoh_segment:
                acquire_uoh_segment(context, result);
                return;

            case allocation_deferred_operation.check_retry_uoh_segment:
                if (retry_full_compact_gc(context->size))
                {
                    result->kind = allocation_callback_result_kind.retry_full_compact_gc;
                }
                else if (full_gc_counts[gc_type_compacting] >
                    context->full_compact_gc_count_before_uoh_acquire)
                {
                    result->kind = allocation_callback_result_kind.retry_segment;
                }
                else
                {
                    result->kind = allocation_callback_result_kind.completed;
                }

                return;

            case allocation_deferred_operation.check_retry_other_heap:
            case allocation_deferred_operation.handle_oom:
                result->kind = allocation_callback_result_kind.completed;
                return;
        }
    }

    private static bool run_allocation_full_collection(
        try_allocate_more_space_context* context,
        int generation,
        gc_reason reason,
        out bool compacted_p)
    {
        bool uoh_p = context->gen_number != (int)gc_generation_num.soh_gen0;
        GCSpinLock* more_space_lock = more_space_lock_of(context->hp, context->gen_number);
        if (uoh_p)
        {
            GCSpinLock.leave(more_space_lock);
            context->more_space_lock_held_p = 0;
        }

        nuint full_compact_gc_count = full_gc_counts[gc_type_compacting];
        int collection_result =
            garbage_collect_synchronous_foreground_for_allocation(
                generation,
                reason);

        if (uoh_p)
        {
            GCSpinLock.enter(more_space_lock);
            context->more_space_lock_held_p = 1;
        }

        compacted_p = full_gc_counts[gc_type_compacting] > full_compact_gc_count;
        return collection_result == collection_s_ok;
    }

    private static nuint get_uoh_seg_size(nuint size)
    {
        nuint default_seg_size = global_region_allocator.get_large_region_alignment();
        nuint align_size = default_seg_size;
        int align_const = get_alignment_constant(small_object_p: false);
        nuint required_size = unchecked(
            size +
            2 * Align((nuint)GCInterfaceOffsets.min_obj_size, align_const) +
            GCToOSInterface.GetPageSize() +
            align_size);
        nuint large_seg_size = unchecked(required_size / align_size * align_size);
        return align_on_page(
            large_seg_size > default_seg_size ? large_seg_size : default_seg_size);
    }

    private static void acquire_uoh_segment(
        try_allocate_more_space_context* context,
        allocation_callback_result* result)
    {
        nuint seg_size = get_uoh_seg_size(context->size);
        context->full_compact_gc_count_before_uoh_acquire =
            full_gc_counts[gc_type_compacting];

        GCSpinLock* more_space_lock = more_space_lock_of(context->hp, context->gen_number);
        GCSpinLock.leave(more_space_lock);
        context->more_space_lock_held_p = 0;

        enter_gc_lock();
        nuint current_full_compact_gc_count = full_gc_counts[gc_type_compacting];
        heap_segment* new_segment = get_new_region(
            context->generation_table,
            context->hp,
            context->gen_number,
            seg_size);
        leave_gc_lock();

        GCSpinLock.enter(more_space_lock);
        context->more_space_lock_held_p = 1;

        result->did_full_compacting_gc_p =
            current_full_compact_gc_count >
                context->full_compact_gc_count_before_uoh_acquire
                ? (byte)1
                : (byte)0;
        if (new_segment is not null)
        {
            if (context->gen_number == (int)gc_generation_num.loh_generation)
            {
                loh_alloc_since_cg = unchecked(loh_alloc_since_cg + seg_size);
            }

            result->kind = allocation_callback_result_kind.segment_acquired;
            return;
        }

        result->kind = allocation_callback_result_kind.segment_unavailable;
        result->oom_r = oom_reason.oom_loh;
    }

    private static bool retry_full_compact_gc(nuint size)
    {
        nuint seg_size = get_uoh_seg_size(size);
        return loh_alloc_since_cg >= unchecked(2 * (ulong)seg_size);
    }
#endif

    private static bool invoke_allocation_callback(
        try_allocate_more_space_context* context,
        allocation_deferred_operation operation,
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback,
        out allocation_callback_result result)
    {
        context->deferred_operation = operation;
        result = default;

        if (callback is null)
        {
            return false;
        }

        allocation_callback_result callbackResult = default;
        callback(context, (int)operation, &callbackResult);
        result = callbackResult;
        if (result.oom_r != oom_reason.oom_no_failure)
        {
            context->oom_r = result.oom_r;
        }

        if (result.kind == allocation_callback_result_kind.unsupported)
        {
            return false;
        }

        context->deferred_operation = allocation_deferred_operation.none;
        return true;
    }

    private static bool retry_allocation(
        try_allocate_more_space_context* context,
        allocation_callback_result result)
    {
        if (result.kind != allocation_callback_result_kind.retry_allocate)
        {
            return false;
        }

        context->state = allocation_state.a_state_retry_allocate;
        context->more_space_lock_held_p = 0;
        return true;
    }

    private static allocation_state leave_more_space_lock(
        try_allocate_more_space_context* context,
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback)
    {
        if (context->more_space_lock_held_p == 0)
        {
            return context->state;
        }

        if (!invoke_allocation_callback(
            context,
            allocation_deferred_operation.leave_more_space_lock,
            callback,
            out allocation_callback_result result))
        {
            return context->state;
        }

        if (retry_allocation(context, result))
        {
            return context->state;
        }

        if (result.kind != allocation_callback_result_kind.completed)
        {
            context->deferred_operation = allocation_deferred_operation.leave_more_space_lock;
            return context->state;
        }

        context->more_space_lock_held_p = 0;
        return context->state;
    }

    private static allocation_state finish_allocation_failure(
        try_allocate_more_space_context* context,
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback)
    {
        if (context->oom_handled_p == 0)
        {
            if (!invoke_allocation_callback(
                context,
                allocation_deferred_operation.handle_oom,
                callback,
                out allocation_callback_result result))
            {
                return context->state;
            }

            if (retry_allocation(context, result))
            {
                return context->state;
            }

            if (result.kind != allocation_callback_result_kind.completed)
            {
                context->deferred_operation = allocation_deferred_operation.handle_oom;
                return context->state;
            }

            context->oom_handled_p = 1;
        }

        return leave_more_space_lock(context, callback);
    }

    private static bool soh_try_fit(
        try_allocate_more_space_context* context,
        bool report_short_seg_end_p,
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback)
    {
        bool commit_failed_p = false;
        bool short_seg_end_p = false;
        bool can_allocate = soh_try_fit(
            context->gen_number,
            context->size,
            context->acontext,
            context->flags,
            context->align_const,
            &commit_failed_p,
            report_short_seg_end_p ? &short_seg_end_p : null,
            context->sufficient_space_regions_for_allocation_p != 0,
            context->sufficient_gen0_space_p != 0,
            context->dd,
            context->allocation_quantum,
            context->generation_table,
            context->ephemeral_heap_segment,
            context->alloc_allocated,
            context->total_alloc_bytes_soh,
            context->heap_number,
            context->hp,
            context,
            callback);

        context->commit_failed_p = commit_failed_p ? (byte)1 : (byte)0;
        context->short_seg_end_p = short_seg_end_p ? (byte)1 : (byte)0;
        return can_allocate;
    }

    private static bool uoh_try_fit(
        try_allocate_more_space_context* context,
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback)
    {
        bool commit_failed_p = false;
        heap_segment* ephemeral_heap_segment =
            context->ephemeral_heap_segment is null ? null : *context->ephemeral_heap_segment;
        byte* alloc_allocated = context->alloc_allocated is null ? null : *context->alloc_allocated;
        bool can_allocate = uoh_try_fit(
            context->gen_number,
            context->size,
            context->acontext,
            context->flags,
            context->align_const,
            &commit_failed_p,
            &context->oom_r,
            context->dd,
            context->allocation_quantum,
            context->generation_table,
            ephemeral_heap_segment,
            context->alloc_allocated is null ? null : &alloc_allocated,
            context->total_alloc_bytes_uoh,
            context->heap_number,
            context,
            callback);

        context->commit_failed_p = commit_failed_p ? (byte)1 : (byte)0;
        return can_allocate;
    }

    private static allocation_state allocate_soh(
        try_allocate_more_space_context* context,
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback)
    {
        while (true)
        {
            switch (context->state)
            {
                case allocation_state.a_state_can_allocate:
                case allocation_state.a_state_retry_allocate:
                    return context->state;

                case allocation_state.a_state_cant_allocate:
                    if (context->oom_r == oom_reason.oom_no_failure)
                    {
                        context->deferred_operation = allocation_deferred_operation.invalid_allocation_state;
                        return context->state;
                    }

                    return finish_allocation_failure(context, callback);

                case allocation_state.a_state_start:
                    context->state = allocation_state.a_state_try_fit;
                    break;

                case allocation_state.a_state_try_fit:
                    context->state = soh_try_fit(context, report_short_seg_end_p: false, callback)
                        ? allocation_state.a_state_can_allocate
                        : (context->commit_failed_p != 0
                            ? allocation_state.a_state_trigger_full_compact_gc
                            : allocation_state.a_state_trigger_ephemeral_gc);
                    break;

                case allocation_state.a_state_try_fit_after_bgc:
                    if (soh_try_fit(context, report_short_seg_end_p: true, callback))
                    {
                        context->state = allocation_state.a_state_can_allocate;
                    }
                    else
                    {
                        context->state = context->short_seg_end_p != 0
                            ? allocation_state.a_state_trigger_2nd_ephemeral_gc
                            : allocation_state.a_state_trigger_full_compact_gc;
                    }

                    break;

                case allocation_state.a_state_try_fit_after_cg:
                    if (soh_try_fit(context, report_short_seg_end_p: true, callback))
                    {
                        context->state = allocation_state.a_state_can_allocate;
                    }
                    else if (context->short_seg_end_p != 0)
                    {
                        context->oom_r = oom_reason.oom_budget;
                        context->state = allocation_state.a_state_cant_allocate;
                    }
                    else
                    {
                        context->oom_r = oom_reason.oom_cant_commit;
                        context->state = allocation_state.a_state_cant_allocate;
                    }

                    break;

                case allocation_state.a_state_check_and_wait_for_bgc:
                    if (!invoke_allocation_callback(
                        context,
                        allocation_deferred_operation.check_and_wait_for_bgc,
                        callback,
                        out allocation_callback_result waitResult))
                    {
                        return context->state;
                    }

                    if (retry_allocation(context, waitResult))
                    {
                        return context->state;
                    }

                    if (waitResult.kind == allocation_callback_result_kind.full_compact_gc)
                    {
                        context->state = allocation_state.a_state_try_fit_after_cg;
                    }
                    else if (waitResult.kind is allocation_callback_result_kind.no_full_compact_gc or
                        allocation_callback_result_kind.background_not_running)
                    {
                        context->state = allocation_state.a_state_try_fit_after_bgc;
                    }
                    else
                    {
                        context->deferred_operation = allocation_deferred_operation.check_and_wait_for_bgc;
                        return context->state;
                    }

                    break;

                case allocation_state.a_state_trigger_ephemeral_gc:
                case allocation_state.a_state_trigger_2nd_ephemeral_gc:
                    allocation_deferred_operation ephemeralOperation =
                        context->state == allocation_state.a_state_trigger_ephemeral_gc
                            ? allocation_deferred_operation.trigger_ephemeral_gc
                            : allocation_deferred_operation.trigger_2nd_ephemeral_gc;
                    if (!invoke_allocation_callback(context, ephemeralOperation, callback, out allocation_callback_result ephemeralResult))
                    {
                        return context->state;
                    }

                    if (retry_allocation(context, ephemeralResult))
                    {
                        return context->state;
                    }

                    if (ephemeralResult.kind == allocation_callback_result_kind.full_compact_gc)
                    {
                        context->state = allocation_state.a_state_try_fit_after_cg;
                        break;
                    }

                    if (ephemeralResult.kind != allocation_callback_result_kind.no_full_compact_gc)
                    {
                        context->deferred_operation = ephemeralOperation;
                        return context->state;
                    }

                    if (soh_try_fit(context, report_short_seg_end_p: true, callback))
                    {
                        context->state = allocation_state.a_state_can_allocate;
                    }
                    else if (context->state == allocation_state.a_state_trigger_2nd_ephemeral_gc)
                    {
                        context->state =
                            (context->short_seg_end_p != 0 || context->commit_failed_p != 0)
                                ? allocation_state.a_state_trigger_full_compact_gc
                                : context->state;

                        if (context->state == allocation_state.a_state_trigger_2nd_ephemeral_gc)
                        {
                            context->deferred_operation = allocation_deferred_operation.invalid_allocation_state;
                            return context->state;
                        }
                    }
                    else if (context->commit_failed_p != 0)
                    {
                        context->state = allocation_state.a_state_trigger_full_compact_gc;
                    }
                    else if (context->short_seg_end_p != 0)
                    {
                        if (!invoke_allocation_callback(
                            context,
                            allocation_deferred_operation.query_background_running,
                            callback,
                            out allocation_callback_result backgroundResult))
                        {
                            return context->state;
                        }

                        if (retry_allocation(context, backgroundResult))
                        {
                            return context->state;
                        }

                        context->state = backgroundResult.kind == allocation_callback_result_kind.background_running
                            ? allocation_state.a_state_check_and_wait_for_bgc
                            : backgroundResult.kind == allocation_callback_result_kind.background_not_running
                                ? allocation_state.a_state_trigger_full_compact_gc
                                : context->state;

                        if (context->state == allocation_state.a_state_trigger_ephemeral_gc)
                        {
                            context->deferred_operation = allocation_deferred_operation.query_background_running;
                            return context->state;
                        }
                    }
                    else
                    {
                        context->deferred_operation = allocation_deferred_operation.invalid_allocation_state;
                        return context->state;
                    }

                    break;

                case allocation_state.a_state_trigger_full_compact_gc:
                    if (!invoke_allocation_callback(
                        context,
                        allocation_deferred_operation.trigger_full_compact_gc,
                        callback,
                        out allocation_callback_result fullCompactResult))
                    {
                        return context->state;
                    }

                    if (retry_allocation(context, fullCompactResult))
                    {
                        return context->state;
                    }

                    if (fullCompactResult.kind == allocation_callback_result_kind.full_compact_gc)
                    {
                        context->state = allocation_state.a_state_try_fit_after_cg;
                    }
                    else if (fullCompactResult.kind == allocation_callback_result_kind.no_full_compact_gc)
                    {
                        context->oom_r = oom_reason.oom_unproductive_full_gc;
                        context->state = allocation_state.a_state_cant_allocate;
                    }
                    else
                    {
                        context->deferred_operation = allocation_deferred_operation.trigger_full_compact_gc;
                        return context->state;
                    }

                    break;

                default:
                    context->deferred_operation = allocation_deferred_operation.invalid_allocation_state;
                    return context->state;
            }
        }
    }

    private static allocation_state allocate_uoh(
        try_allocate_more_space_context* context,
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback)
    {
        while (true)
        {
            switch (context->state)
            {
                case allocation_state.a_state_can_allocate:
                case allocation_state.a_state_retry_allocate:
                    return context->state;

                case allocation_state.a_state_cant_allocate:
                    if (context->oom_r == oom_reason.oom_no_failure)
                    {
                        context->deferred_operation = allocation_deferred_operation.invalid_allocation_state;
                        return context->state;
                    }

                    if (context->oom_r != oom_reason.oom_cant_commit)
                    {
                        if (!invoke_allocation_callback(
                            context,
                            allocation_deferred_operation.check_retry_other_heap,
                            callback,
                            out allocation_callback_result retryOtherHeapResult))
                        {
                            return context->state;
                        }

                        if (retryOtherHeapResult.kind == allocation_callback_result_kind.retry_other_heap)
                        {
                            context->state = allocation_state.a_state_retry_allocate;
                            return leave_more_space_lock(context, callback);
                        }

                        if (retryOtherHeapResult.kind != allocation_callback_result_kind.completed)
                        {
                            context->deferred_operation = allocation_deferred_operation.check_retry_other_heap;
                            return context->state;
                        }
                    }

                    return finish_allocation_failure(context, callback);

                case allocation_state.a_state_start:
                    context->state = allocation_state.a_state_try_fit;
                    break;

                case allocation_state.a_state_try_fit:
                    context->state = uoh_try_fit(context, callback)
                        ? allocation_state.a_state_can_allocate
                        : (context->commit_failed_p != 0
                            ? allocation_state.a_state_trigger_full_compact_gc
                            : allocation_state.a_state_acquire_seg);
                    break;

                case allocation_state.a_state_try_fit_new_seg:
                    context->state = uoh_try_fit(context, callback)
                        ? allocation_state.a_state_can_allocate
                        : allocation_state.a_state_try_fit;
                    break;

                case allocation_state.a_state_try_fit_after_cg:
                    context->state = uoh_try_fit(context, callback)
                        ? allocation_state.a_state_can_allocate
                        : (context->commit_failed_p != 0
                            ? allocation_state.a_state_cant_allocate
                            : allocation_state.a_state_acquire_seg_after_cg);
                    break;

                case allocation_state.a_state_try_fit_after_bgc:
                    context->state = uoh_try_fit(context, callback)
                        ? allocation_state.a_state_can_allocate
                        : (context->commit_failed_p != 0
                            ? allocation_state.a_state_trigger_full_compact_gc
                            : allocation_state.a_state_acquire_seg_after_bgc);
                    break;

                case allocation_state.a_state_acquire_seg:
                case allocation_state.a_state_acquire_seg_after_cg:
                case allocation_state.a_state_acquire_seg_after_bgc:
                    if (!invoke_allocation_callback(
                        context,
                        allocation_deferred_operation.acquire_uoh_segment,
                        callback,
                        out allocation_callback_result acquireResult))
                    {
                        return context->state;
                    }

                    if (retry_allocation(context, acquireResult))
                    {
                        return context->state;
                    }

                    if (acquireResult.kind == allocation_callback_result_kind.segment_acquired)
                    {
                        context->state = context->state == allocation_state.a_state_acquire_seg_after_cg
                            ? allocation_state.a_state_try_fit_after_cg
                            : allocation_state.a_state_try_fit_new_seg;
                    }
                    else if (acquireResult.kind == allocation_callback_result_kind.segment_unavailable)
                    {
                        context->state = context->state == allocation_state.a_state_acquire_seg
                            ? (acquireResult.did_full_compacting_gc_p != 0
                                ? allocation_state.a_state_check_retry_seg
                                : allocation_state.a_state_check_and_wait_for_bgc)
                            : context->state == allocation_state.a_state_acquire_seg_after_cg
                                ? allocation_state.a_state_check_retry_seg
                                : acquireResult.did_full_compacting_gc_p != 0
                                    ? allocation_state.a_state_check_retry_seg
                                    : allocation_state.a_state_trigger_full_compact_gc;
                    }
                    else
                    {
                        context->deferred_operation = allocation_deferred_operation.acquire_uoh_segment;
                        return context->state;
                    }

                    break;

                case allocation_state.a_state_check_and_wait_for_bgc:
                    if (!invoke_allocation_callback(
                        context,
                        allocation_deferred_operation.check_and_wait_for_bgc,
                        callback,
                        out allocation_callback_result waitResult))
                    {
                        return context->state;
                    }

                    if (retry_allocation(context, waitResult))
                    {
                        return context->state;
                    }

                    context->state = waitResult.kind == allocation_callback_result_kind.background_not_running
                        ? allocation_state.a_state_trigger_full_compact_gc
                        : waitResult.kind == allocation_callback_result_kind.full_compact_gc
                            ? allocation_state.a_state_try_fit_after_cg
                            : waitResult.kind == allocation_callback_result_kind.no_full_compact_gc
                                ? allocation_state.a_state_try_fit_after_bgc
                                : context->state;

                    if (context->state == allocation_state.a_state_check_and_wait_for_bgc)
                    {
                        context->deferred_operation = allocation_deferred_operation.check_and_wait_for_bgc;
                        return context->state;
                    }

                    break;

                case allocation_state.a_state_trigger_full_compact_gc:
                    if (!invoke_allocation_callback(
                        context,
                        allocation_deferred_operation.trigger_full_compact_gc,
                        callback,
                        out allocation_callback_result fullCompactResult))
                    {
                        return context->state;
                    }

                    if (retry_allocation(context, fullCompactResult))
                    {
                        return context->state;
                    }

                    if (fullCompactResult.kind == allocation_callback_result_kind.full_compact_gc)
                    {
                        context->state = allocation_state.a_state_try_fit_after_cg;
                    }
                    else if (fullCompactResult.kind == allocation_callback_result_kind.no_full_compact_gc)
                    {
                        context->oom_r = oom_reason.oom_unproductive_full_gc;
                        context->state = allocation_state.a_state_cant_allocate;
                    }
                    else
                    {
                        context->deferred_operation = allocation_deferred_operation.trigger_full_compact_gc;
                        return context->state;
                    }

                    break;

                case allocation_state.a_state_check_retry_seg:
                    if (!invoke_allocation_callback(
                        context,
                        allocation_deferred_operation.check_retry_uoh_segment,
                        callback,
                        out allocation_callback_result retrySegmentResult))
                    {
                        return context->state;
                    }

                    if (retry_allocation(context, retrySegmentResult))
                    {
                        return context->state;
                    }

                    context->state = retrySegmentResult.kind == allocation_callback_result_kind.retry_full_compact_gc
                        ? allocation_state.a_state_trigger_full_compact_gc
                        : retrySegmentResult.kind == allocation_callback_result_kind.retry_segment
                            ? allocation_state.a_state_try_fit_after_cg
                            : retrySegmentResult.kind == allocation_callback_result_kind.completed
                                ? allocation_state.a_state_cant_allocate
                                : context->state;

                    if (context->state == allocation_state.a_state_check_retry_seg)
                    {
                        context->deferred_operation = allocation_deferred_operation.check_retry_uoh_segment;
                        return context->state;
                    }

                    break;

                default:
                    context->deferred_operation = allocation_deferred_operation.invalid_allocation_state;
                    return context->state;
            }
        }
    }

    public static allocation_state try_allocate_more_space(
        try_allocate_more_space_context* context,
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback = null)
    {
        context->deferred_operation = allocation_deferred_operation.none;

        if (context->state == allocation_state.a_state_start && context->gc_started_p != 0)
        {
            context->state = allocation_state.a_state_retry_allocate;
            context->deferred_operation = allocation_deferred_operation.wait_for_gc_done;
            return context->state;
        }

        if (context->state == allocation_state.a_state_start && context->more_space_lock_held_p == 0)
        {
            if (!invoke_allocation_callback(
                context,
                allocation_deferred_operation.enter_more_space_lock,
                callback,
                out allocation_callback_result lockResult))
            {
                return context->state;
            }

            if (retry_allocation(context, lockResult))
            {
                return context->state;
            }

            if (lockResult.kind != allocation_callback_result_kind.completed)
            {
                context->deferred_operation = allocation_deferred_operation.enter_more_space_lock;
                return context->state;
            }

            context->more_space_lock_held_p = 1;
        }

        if (context->state == allocation_state.a_state_start &&
            context->full_gc_notification_p != 0 &&
            context->full_gc_checked_p == 0)
        {
            if (!invoke_allocation_callback(
                context,
                allocation_deferred_operation.check_for_full_gc,
                callback,
                out allocation_callback_result fullGcNotificationResult))
            {
                return context->state;
            }

            if (retry_allocation(context, fullGcNotificationResult))
            {
                return context->state;
            }

            if (fullGcNotificationResult.kind != allocation_callback_result_kind.completed)
            {
                context->deferred_operation = allocation_deferred_operation.check_for_full_gc;
                return context->state;
            }

            context->full_gc_checked_p = 1;
        }

        while (context->state == allocation_state.a_state_start && context->budget_checked_p == 0)
        {
            if (context->non_collecting_bootstrap_budget_p != 0)
            {
                if (dynamic_data.dd_new_allocation(context->dd) <= 0)
                {
                    dynamic_data.dd_new_allocation(context->dd) =
                        unchecked((nint)dynamic_data.dd_desired_allocation(context->dd));
                }

                context->budget_checked_p = 1;
                break;
            }

            if (!invoke_allocation_callback(
                context,
                allocation_deferred_operation.check_allocation_budget,
                callback,
                out allocation_callback_result budgetResult))
            {
                return context->state;
            }

            if (retry_allocation(context, budgetResult))
            {
                return context->state;
            }

            if (budgetResult.kind == allocation_callback_result_kind.allocation_allowed)
            {
                context->budget_checked_p = 1;
                break;
            }

            if (budgetResult.kind != allocation_callback_result_kind.allocation_disallowed)
            {
                context->deferred_operation = allocation_deferred_operation.check_allocation_budget;
                return context->state;
            }

            if (context->full_gc_notification_p != 0 &&
                context->gen_number == (int)gc_generation_num.soh_gen0 &&
                context->budget_full_gc_checked_p == 0)
            {
                if (!invoke_allocation_callback(
                    context,
                    allocation_deferred_operation.check_for_full_gc,
                    callback,
                    out allocation_callback_result budgetFullGcNotificationResult))
                {
                    return context->state;
                }

                if (retry_allocation(context, budgetFullGcNotificationResult))
                {
                    return context->state;
                }

                if (budgetFullGcNotificationResult.kind != allocation_callback_result_kind.completed)
                {
                    context->deferred_operation = allocation_deferred_operation.check_for_full_gc;
                    return context->state;
                }

                context->budget_full_gc_checked_p = 1;
            }

            if (context->bgc_high_memory_waited_p == 0)
            {
                if (!invoke_allocation_callback(
                    context,
                    allocation_deferred_operation.wait_for_bgc_high_memory,
                    callback,
                    out allocation_callback_result highMemoryResult))
                {
                    return context->state;
                }

                if (retry_allocation(context, highMemoryResult))
                {
                    return context->state;
                }

                if (highMemoryResult.kind == allocation_callback_result_kind.background_running)
                {
                    context->bgc_high_memory_waited_p = 1;
                    continue;
                }

                if (highMemoryResult.kind != allocation_callback_result_kind.background_not_running)
                {
                    context->deferred_operation = allocation_deferred_operation.wait_for_bgc_high_memory;
                    return context->state;
                }
            }

            if (!invoke_allocation_callback(
                context,
                allocation_deferred_operation.trigger_gc_for_budget,
                callback,
                out allocation_callback_result triggerBudgetResult))
            {
                return context->state;
            }

            if (retry_allocation(context, triggerBudgetResult))
            {
                return context->state;
            }

            if (triggerBudgetResult.kind != allocation_callback_result_kind.completed)
            {
                context->deferred_operation = allocation_deferred_operation.trigger_gc_for_budget;
                return context->state;
            }

            context->budget_checked_p = 1;
        }

        return context->gen_number == (int)gc_generation_num.soh_gen0
            ? allocate_soh(context, callback)
            : allocate_uoh(context, callback);
    }

    private static void reset_allocate_more_space_state(try_allocate_more_space_context* context)
    {
        context->state = allocation_state.a_state_start;
        context->oom_r = oom_reason.oom_no_failure;
        context->deferred_operation = allocation_deferred_operation.none;
        context->more_space_lock_held_p = 0;
        context->full_gc_checked_p = 0;
        context->budget_full_gc_checked_p = 0;
        context->budget_checked_p = 0;
        context->bgc_high_memory_waited_p = 0;
        context->commit_failed_p = 0;
        context->short_seg_end_p = 0;
        context->oom_handled_p = 0;
    }

    // The native WKS wrapper retries try_allocate_more_space from its initial state. The
    // context carries every deferred heap input explicitly until gc_heap owns those fields.
    public static bool allocate_more_space(
        try_allocate_more_space_context* context,
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback = null)
    {
        allocation_state status;

        do
        {
            reset_allocate_more_space_state(context);
            status = try_allocate_more_space(context, callback);

            if (status == allocation_state.a_state_retry_allocate &&
                context->deferred_operation == allocation_deferred_operation.wait_for_gc_done)
            {
                context->gc_started_p = 0;
                if (!invoke_allocation_callback(
                    context,
                    allocation_deferred_operation.wait_for_gc_done,
                    callback,
                    out allocation_callback_result waitResult) ||
                    waitResult.kind != allocation_callback_result_kind.completed)
                {
                    return false;
                }
            }
            else if (status == allocation_state.a_state_retry_allocate &&
                context->deferred_operation != allocation_deferred_operation.none)
            {
                return false;
            }
        }
        while (status == allocation_state.a_state_retry_allocate);

        if (status != allocation_state.a_state_can_allocate)
        {
            allocation_deferred_operation deferred_operation = context->deferred_operation;
            leave_more_space_lock(context, callback);
            context->deferred_operation = deferred_operation;
            return false;
        }

        return leave_more_space_lock(context, callback) == allocation_state.a_state_can_allocate &&
            context->deferred_operation == allocation_deferred_operation.none;
    }

    // This is the dependency-closed WKS USE_REGIONS portion of adjust_limit_clr. The BGC mark
    // bit, allocation event, and verification branches remain deferred with their owning
    // collector states; this leaf must not report that they ran.
    public static void adjust_limit_clr(
        byte* start,
        nuint limit_size,
        nuint size,
        gc_alloc_context* acontext,
        uint flags,
        heap_segment* seg,
        int align_const,
        int gen_number,
        generation* generation_table,
        heap_segment* ephemeral_heap_segment,
        byte* alloc_allocated,
        ulong* total_alloc_bytes,
        try_allocate_more_space_context* more_space_context,
        delegate*<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback)
    {
        nuint aligned_min_obj_size = Align((nuint)GCInterfaceOffsets.min_obj_size, align_const);

        if (seg is not null)
        {
            System.Diagnostics.Debug.Assert(heap_segment.heap_segment_used(seg) <= heap_segment.heap_segment_committed(seg));
        }

        if ((acontext->alloc_limit != start) &&
            ((byte*)unchecked((nuint)acontext->alloc_limit + aligned_min_obj_size) != start))
        {
            byte* hole = acontext->alloc_ptr;
            if (hole is not null)
            {
                nuint ac_size = unchecked((nuint)(acontext->alloc_limit - acontext->alloc_ptr));
                acontext->alloc_bytes = unchecked(acontext->alloc_bytes - (long)ac_size);
                *total_alloc_bytes = unchecked(*total_alloc_bytes - ac_size);

                nuint free_obj_size = unchecked(ac_size + aligned_min_obj_size);
                make_free_obj(generation_of(generation_table, gen_number), hole, free_obj_size);
            }

            acontext->alloc_ptr = start;
        }
        else if (gen_number == 0)
        {
            if (acontext->alloc_ptr is null)
            {
                acontext->alloc_ptr = start;
            }
            else
            {
                make_unused_array(acontext->alloc_ptr, aligned_min_obj_size);
                acontext->alloc_ptr = (byte*)unchecked((nuint)acontext->alloc_ptr + aligned_min_obj_size);
            }
        }

        set_alloc_context_limit(acontext, start, limit_size, gen_number, align_const, total_alloc_bytes);

        if (seg is not null && seg == ephemeral_heap_segment)
        {
            byte* allocated = (byte*)unchecked((nuint)alloc_allocated - plug_skew);
            if (heap_segment.heap_segment_used(seg) < allocated)
            {
                heap_segment.heap_segment_used(seg) = allocated;
                System.Diagnostics.Debug.Assert(heap_segment.heap_segment_mem(seg) <= heap_segment.heap_segment_used(seg));
                System.Diagnostics.Debug.Assert(heap_segment.heap_segment_used(seg) <= heap_segment.heap_segment_reserved(seg));
            }
        }

        // The size and limit_size include the sync block immediately before the object.
        byte* clear_start = (byte*)unchecked((nuint)start - plug_skew);
        byte* clear_limit = (byte*)unchecked((nuint)start + limit_size - plug_skew);

        if ((flags & (uint)GC_ALLOC_FLAGS.GC_ALLOC_ZEROING_OPTIONAL) != 0)
        {
            byte* obj_start = acontext->alloc_ptr;
            System.Diagnostics.Debug.Assert(start >= obj_start);
            byte* obj_end = (byte*)unchecked((nuint)obj_start + size - plug_skew);
            System.Diagnostics.Debug.Assert(obj_end >= clear_start);

            if (obj_start == start)
            {
                *(nuint*)clear_start = 0;
            }

            clear_start = obj_end;
        }

        // Capture this while holding the lock, as the native code does.
        heap_segment* gen0_segment = ephemeral_heap_segment;

        byte* clear_end;
        if (seg is null || clear_limit <= heap_segment.heap_segment_used(seg))
        {
            clear_end = clear_limit;
        }
        else
        {
            byte* used = heap_segment.heap_segment_used(seg);
            heap_segment.heap_segment_used(seg) = clear_limit;
            clear_end = used;
        }

        // The clear span and used high-water mark are selected while holding the lock. Clearing
        // can be expensive, so native releases the lock immediately before memclr.
        if (more_space_context is not null && callback is not null)
        {
            leave_more_space_lock(more_space_context, callback);
        }

        if (clear_start < clear_end)
        {
            // A concrete more-space lock release is synchronous. If test or incomplete wiring
            // defers it, clearing under the still-held lock is safer than publishing dirty memory.
            memclr(clear_start, unchecked((nuint)(clear_end - clear_start)));
        }

#if !MULTIPLE_HEAPS
        // this portion can be done after we release the lock
        if (seg == gen0_segment ||
            (seg is null &&
             gen_number == (int)gc_generation_num.soh_gen0 &&
             limit_size >= DefaultAllocationQuantum / 2))
        {
            if (gen0_must_clear_bricks > 0)
            {
                // set the brick table to speed up find_object
                nuint b = brick_of(acontext->alloc_ptr);
                set_brick(b, unchecked((nint)(acontext->alloc_ptr - brick_address(b))));
                b++;
                nuint end_brick = brick_of(card_table_info.align_on_brick(start + (nint)limit_size));

                for (; b < end_brick; b++)
                {
                    GCEnv.VolatileStore((ushort*)&brick_table[(nint)b], ushort.MaxValue);
                }
            }
            else
            {
                gen0_bricks_cleared = 0;
            }
        }
#endif
    }

    public static void set_allocation_heap_segment(generation* gen)
    {
        generation.generation_allocation_segment(gen) =
            heap_segment_rw(generation.generation_start_segment(gen));
    }

    public static void reset_allocation_pointers(generation* gen, byte* start)
    {
        _ = start;
        generation.generation_allocation_pointer(gen) = null;
        generation.generation_allocation_limit(gen) = null;
        set_allocation_heap_segment(gen);
    }
#endif
}
