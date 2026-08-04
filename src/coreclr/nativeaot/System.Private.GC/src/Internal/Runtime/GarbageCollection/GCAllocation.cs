// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the dependency-closed WKS USE_REGIONS allocation-context helpers from allocation.cpp.

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
