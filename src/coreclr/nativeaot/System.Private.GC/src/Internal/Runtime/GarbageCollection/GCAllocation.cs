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

        if (seg == ephemeral_heap_segment)
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
