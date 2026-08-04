// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the dependency-closed WKS USE_REGIONS mapping helpers from regions_segments.cpp.

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
#if USE_REGIONS
    public static byte* align_on_segment(byte* add)
    {
        nuint alignment = (nuint)1 << (int)min_segment_size_shr;
        return (byte*)unchecked(((nuint)add + alignment - 1) & ~(alignment - 1));
    }

    public static nuint ro_seg_begin_index(heap_segment* seg)
    {
        nuint begin_index = (nuint)heap_segment.heap_segment_mem(seg) >> (int)min_segment_size_shr;
        nuint lowest_index = (nuint)GCCommon.g_gc_lowest_address >> (int)min_segment_size_shr;
        return begin_index > lowest_index ? begin_index : lowest_index;
    }

    public static nuint ro_seg_end_index(heap_segment* seg)
    {
        nuint end_index = (nuint)(heap_segment.heap_segment_reserved(seg) - 1) >> (int)min_segment_size_shr;
        nuint highest_index = (nuint)GCCommon.g_gc_highest_address >> (int)min_segment_size_shr;
        return end_index < highest_index ? end_index : highest_index;
    }

    public static nuint size_seg_mapping_table_of(byte* from, byte* end)
    {
        from = align_lower_segment(from);
        end = align_on_segment(end);
        return (nuint)sizeof(seg_mapping) * (((nuint)(end - from)) >> (int)min_segment_size_shr);
    }

    public static nuint size_region_to_generation_table_of(byte* from, byte* end)
    {
        return ((nuint)(end - from)) >> (int)min_segment_size_shr;
    }

    public static void seg_mapping_table_add_ro_segment(heap_segment* seg)
    {
        if ((heap_segment.heap_segment_reserved(seg) <= GCCommon.g_gc_lowest_address) ||
            (heap_segment.heap_segment_mem(seg) >= GCCommon.g_gc_highest_address))
        {
            return;
        }

        for (nuint entry_index = ro_seg_begin_index(seg); entry_index <= ro_seg_end_index(seg); entry_index++)
        {
            heap_segment* region = (heap_segment*)&GCCommon.seg_mapping_table[(nint)entry_index];
            heap_segment.heap_segment_allocated(region) = (byte*)seg_mapping.ro_in_entry;
        }
    }

    public static void seg_mapping_table_remove_ro_segment(heap_segment* seg)
    {
        _ = seg;
    }
#endif
}
#pragma warning restore CS8981
