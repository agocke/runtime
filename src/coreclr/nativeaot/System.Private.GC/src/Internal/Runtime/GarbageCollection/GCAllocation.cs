// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the dependency-closed WKS USE_REGIONS allocation-context helpers from allocation.cpp,
// sweep.cpp, and gcinternal.h.

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

    public static int get_alignment_constant(bool small_object_p)
    {
        return small_object_p ? GCEnv.DATA_ALIGNMENT - 1 : 7;
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

    public static void make_unused_array(byte* x, nuint size)
    {
        System.Diagnostics.Debug.Assert(size >= Align((nuint)GCInterfaceOffsets.min_obj_size));

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
        ulong* total_alloc_bytes)
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
                        acontext,
                        null,
                        align_const,
                        gen_number,
                        generation_table,
                        null,
                        null,
                        total_alloc_bytes);

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
        ulong* total_alloc_bytes)
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
                        acontext,
                        null,
                        align_const,
                        gen_number,
                        generation_table,
                        null,
                        null,
                        total_alloc_bytes);

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
        int heap_number)
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
            acontext,
            seg,
            align_const,
            gen_number,
            generation_table,
            ephemeral_heap_segment,
            alloc_allocated is not null ? *alloc_allocated : null,
            total_alloc_bytes);

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
        int heap_number)
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
                heap_number))
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

    // This is precisely the for_gc_p == true, record_ac_p == false call made before an
    // ephemeral-region rollover. The complete fix_allocation_context also handles concurrent
    // verification and allocation-context statistics, which remain with the heap/collection
    // slices that own those states.
    public static void fix_allocation_context_for_region_rollover(
        gc_alloc_context* acontext,
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
            unchecked((nuint)(*alloc_allocated - acontext->alloc_limit)) > aligned_min_obj_size)
        {
            byte* point = acontext->alloc_ptr;
            nuint size = unchecked((nuint)(acontext->alloc_limit - acontext->alloc_ptr) + aligned_min_obj_size);
            make_unused_array(point, size);
            generation* gen0 = generation_of(generation_table, (int)gc_generation_num.soh_gen0);
            generation.generation_free_obj_space(gen0) =
                unchecked(generation.generation_free_obj_space(gen0) + size);
        }
        else
        {
            *alloc_allocated = acontext->alloc_ptr;
            System.Diagnostics.Debug.Assert(
                heap_segment.heap_segment_allocated(ephemeral_heap_segment) <=
                heap_segment.heap_segment_committed(ephemeral_heap_segment));
        }

        retire_allocation_context(acontext, total_alloc_bytes_soh);
    }

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
        gc_heap* hp)
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
            total_alloc_bytes_soh);

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
                heap_number);
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
        int heap_number)
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
            total_alloc_bytes))
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
            heap_number);
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
    }

    private static bool invoke_allocation_callback(
        try_allocate_more_space_context* context,
        allocation_deferred_operation operation,
        delegate* unmanaged<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback,
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
        delegate* unmanaged<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback)
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
        delegate* unmanaged<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback)
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
        bool report_short_seg_end_p)
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
            context->hp);

        context->commit_failed_p = commit_failed_p ? (byte)1 : (byte)0;
        context->short_seg_end_p = short_seg_end_p ? (byte)1 : (byte)0;
        return can_allocate;
    }

    private static bool uoh_try_fit(try_allocate_more_space_context* context)
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
            context->heap_number);

        context->commit_failed_p = commit_failed_p ? (byte)1 : (byte)0;
        return can_allocate;
    }

    private static allocation_state allocate_soh(
        try_allocate_more_space_context* context,
        delegate* unmanaged<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback)
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
                    context->state = soh_try_fit(context, report_short_seg_end_p: false)
                        ? allocation_state.a_state_can_allocate
                        : (context->commit_failed_p != 0
                            ? allocation_state.a_state_trigger_full_compact_gc
                            : allocation_state.a_state_trigger_ephemeral_gc);
                    break;

                case allocation_state.a_state_try_fit_after_bgc:
                    if (soh_try_fit(context, report_short_seg_end_p: true))
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
                    if (soh_try_fit(context, report_short_seg_end_p: true))
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

                    if (soh_try_fit(context, report_short_seg_end_p: true))
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
        delegate* unmanaged<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback)
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
                    context->state = uoh_try_fit(context)
                        ? allocation_state.a_state_can_allocate
                        : (context->commit_failed_p != 0
                            ? allocation_state.a_state_trigger_full_compact_gc
                            : allocation_state.a_state_acquire_seg);
                    break;

                case allocation_state.a_state_try_fit_new_seg:
                    context->state = uoh_try_fit(context)
                        ? allocation_state.a_state_can_allocate
                        : allocation_state.a_state_try_fit;
                    break;

                case allocation_state.a_state_try_fit_after_cg:
                    context->state = uoh_try_fit(context)
                        ? allocation_state.a_state_can_allocate
                        : (context->commit_failed_p != 0
                            ? allocation_state.a_state_cant_allocate
                            : allocation_state.a_state_acquire_seg_after_cg);
                    break;

                case allocation_state.a_state_try_fit_after_bgc:
                    context->state = uoh_try_fit(context)
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
        delegate* unmanaged<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback = null)
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
        delegate* unmanaged<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback = null)
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
            return false;
        }

        return leave_more_space_lock(context, callback) == allocation_state.a_state_can_allocate &&
            context->deferred_operation == allocation_deferred_operation.none;
    }

    // This is the dependency-closed refill-state portion of adjust_limit_clr. The deferred
    // gc_heap owns generation_table, alloc_allocated, ephemeral_heap_segment, and the selected
    // SOH/UOH total_alloc_bytes counter; they are explicit here until try_allocate_more_space
    // supplies the locking, budget, clearing, and event paths that own the rest of the method.
    public static void adjust_limit_clr(
        byte* start,
        nuint limit_size,
        gc_alloc_context* acontext,
        heap_segment* seg,
        int align_const,
        int gen_number,
        generation* generation_table,
        heap_segment* ephemeral_heap_segment,
        byte* alloc_allocated,
        ulong* total_alloc_bytes)
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
