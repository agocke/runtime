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
