// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the dependency-closed WKS region memory helpers from memory.cpp.

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    public const nuint DECOMMIT_SIZE_PER_MILLISECOND = 160 * 1024;

    public const int total_oh_count = (int)gc_oh_num.poh + 1;

#if USE_REGIONS
    public const int recorded_committed_free_bucket = total_oh_count;
    public const int recorded_committed_bookkeeping_bucket = recorded_committed_free_bucket + 1;
    public const int recorded_committed_mark_array_bucket = recorded_committed_bookkeeping_bucket;
#else
    public const int recorded_committed_ignored_bucket = total_oh_count;
    public const int recorded_committed_bookkeeping_bucket = recorded_committed_ignored_bucket + 1;
    public const int recorded_committed_mark_array_bucket = recorded_committed_ignored_bucket;
#endif
    public const int recorded_committed_bucket_counts = recorded_committed_bookkeeping_bucket + 1;

    [InlineArray(recorded_committed_bucket_counts)]
    internal struct recorded_committed_bucket_array
    {
        private nuint _element0;
    }

    [InlineArray(total_oh_count)]
    internal struct object_heap_array
    {
        private nuint _element0;
    }

    public static nuint reserved_memory;
    public static nuint reserved_memory_limit;
    public static CLRCriticalSection check_commit_cs;
    public static nuint current_total_committed;
    public static recorded_committed_bucket_array committed_by_oh;
    public static nuint current_total_committed_bookkeeping;
    public static nuint heap_hard_limit;
    public static object_heap_array heap_hard_limit_oh;
    public static bool never_decommit_p;
    public static gc_mechanisms settings;

#if USE_REGIONS
    [InlineArray((int)free_region_kind.count_free_region_kinds)]
    internal struct region_free_list_array
    {
        private region_free_list _element0;
    }

    public static region_free_list_array global_regions_to_decommit;

    public static region_free_list* global_regions_to_decommit_of(int kind)
    {
        Debug.Assert(kind >= (int)free_region_kind.basic_free_region && kind < (int)free_region_kind.count_free_region_kinds);
        return (region_free_list*)Unsafe.AsPointer(ref global_regions_to_decommit[kind]);
    }
#endif

#if BACKGROUND_GC
    public static byte* lowest_address;
    public static byte* highest_address;
    public static uint* mark_array;
#endif

    public static bool virtual_alloc_commit_for_heap(void* addr, nuint size, int h_number)
    {
#if MULTIPLE_HEAPS
        if (GCToOSInterface.CanEnableGCNumaAware())
        {
            ushort numa_node = heap_select.find_numa_node_from_heap_no(h_number);
            if (GCToOSInterface.VirtualCommit(addr, size, numa_node))
            {
                return true;
            }
        }
#else
        _ = h_number;
#endif

        //numa aware not enabled, or call failed --> fallback to VirtualCommit()
        return GCToOSInterface.VirtualCommit(addr, size);
    }

    public static bool virtual_commit(void* address, nuint size, int bucket, int h_number, bool* hard_limit_exceeded_p = null)
    {
        /*
         * Here are all the possible cases for the commits:
         *
         * Case 1: This is for a particular generation - the bucket will be one of the gc_oh_num != unknown, and the h_number will be the right heap
         * Case 2: This is for bookkeeping - the bucket will be recorded_committed_bookkeeping_bucket, and the h_number will be -1
         *
         * Note  : We never commit into free directly, so bucket != recorded_committed_free_bucket
         */

        Debug.Assert(0 <= bucket && bucket < recorded_committed_bucket_counts);
        Debug.Assert(bucket < total_oh_count || h_number == -1);
#if USE_REGIONS
        Debug.Assert(bucket != recorded_committed_free_bucket);
#endif

#if USE_REGIONS
        bool should_count = true;
#else
        bool should_count = bucket != recorded_committed_ignored_bucket;
#endif

        if (should_count)
        {
            check_commit_cs.Enter();
            bool exceeded_p = false;

            if (heap_hard_limit_oh[(int)gc_oh_num.soh] != 0)
            {
                if ((bucket < total_oh_count) && unchecked(committed_by_oh[bucket] + size) > heap_hard_limit_oh[bucket])
                {
                    exceeded_p = true;
                }
            }
            else
            {
                nuint @base = current_total_committed;
                nuint limit = heap_hard_limit;

                if (unchecked(@base + size) > limit)
                {
                    exceeded_p = true;
                }
            }

            if (heap_hard_limit == 0)
            {
                exceeded_p = false;
            }

            if (!exceeded_p)
            {
                committed_by_oh[bucket] = unchecked(committed_by_oh[bucket] + size);
                current_total_committed = unchecked(current_total_committed + size);
                if (h_number < 0)
                {
                    current_total_committed_bookkeeping = unchecked(current_total_committed_bookkeeping + size);
                }
            }

            check_commit_cs.Leave();

            if (hard_limit_exceeded_p is not null)
            {
                *hard_limit_exceeded_p = exceeded_p;
            }

            if (exceeded_p)
            {
                return false;
            }
        }

        // If it's a valid heap number it means it's commiting for memory on the GC heap.
        // In addition if never-decommit is enabled (which is implied by large pages), we
        // set commit_succeeded_p to true because memory is already committed (and
        // VirtualCommit would be a no-op).
        bool commit_succeeded_p = h_number >= 0
            ? never_decommit_p || virtual_alloc_commit_for_heap(address, size, h_number)
            : GCToOSInterface.VirtualCommit(address, size);

        if (!commit_succeeded_p && should_count)
        {
            check_commit_cs.Enter();
            committed_by_oh[bucket] = unchecked(committed_by_oh[bucket] - size);
            current_total_committed = unchecked(current_total_committed - size);
            if (h_number < 0)
            {
                Debug.Assert(current_total_committed_bookkeeping >= size);
                current_total_committed_bookkeeping = unchecked(current_total_committed_bookkeeping - size);
            }

            check_commit_cs.Leave();
        }

        return commit_succeeded_p;
    }

    public static void reduce_committed_bytes(void* address, nuint size, int bucket, int h_number, bool decommit_succeeded_p)
    {
        Debug.Assert(0 <= bucket && bucket < recorded_committed_bucket_counts);
        Debug.Assert(bucket < total_oh_count || h_number == -1);
        _ = address;

#if !USE_REGIONS
        if (bucket != recorded_committed_ignored_bucket)
#endif
        if (decommit_succeeded_p)
        {
            check_commit_cs.Enter();
            Debug.Assert(committed_by_oh[bucket] >= size);
            committed_by_oh[bucket] = unchecked(committed_by_oh[bucket] - size);
            Debug.Assert(current_total_committed >= size);
            current_total_committed = unchecked(current_total_committed - size);
            if (bucket == recorded_committed_bookkeeping_bucket)
            {
                Debug.Assert(current_total_committed_bookkeeping >= size);
                current_total_committed_bookkeeping = unchecked(current_total_committed_bookkeeping - size);
            }

            check_commit_cs.Leave();
        }
    }

    public static bool virtual_decommit(void* address, nuint size, int bucket, int h_number = -1)
    {
        /*
         * Here are all possible cases for the decommits:
         *
         * Case 1: This is for a particular generation - the bucket will be one of the gc_oh_num != unknown, and the h_number will be the right heap
         * Case 2: This is for bookkeeping - the bucket will be recorded_committed_bookkeeping_bucket, and the h_number will be -1
         * Case 3: This is for free - the bucket will be recorded_committed_free_bucket, and the h_number will be -1
         */

        // With never-decommit (implied by large pages), VirtualDecommit on heap memory is
        // a no-op. All such callers should either skip the decommit or handle stale data
        // themselves (decommit_region does the latter by calling reduce_committed_bytes
        // directly and clearing memory).
        Debug.Assert(!never_decommit_p || bucket == recorded_committed_bookkeeping_bucket);

        bool decommit_succeeded_p = GCToOSInterface.VirtualDecommit(address, size);

        reduce_committed_bytes(address, size, bucket, h_number, decommit_succeeded_p);

        return decommit_succeeded_p;
    }

    public static void virtual_free(void* add, nuint allocated_size, heap_segment* sg)
    {
        _ = sg;
        bool release_succeeded_p = GCToOSInterface.VirtualRelease(add, allocated_size);
        if (release_succeeded_p)
        {
            reserved_memory = unchecked(reserved_memory - allocated_size);
        }
    }

    public static nuint align_on_page(nuint add)
    {
        nuint pageSize = GCToOSInterface.GetPageSize();
        return unchecked((add + pageSize - 1) & ~(pageSize - 1));
    }

    public static byte* align_on_page(byte* add)
    {
        return (byte*)align_on_page((nuint)add);
    }

    public static nuint align_lower_page(nuint add)
    {
        nuint pageSize = GCToOSInterface.GetPageSize();
        return add & ~(pageSize - 1);
    }

    public static byte* align_lower_page(byte* add)
    {
        return (byte*)align_lower_page((nuint)add);
    }

    public static void memclr(byte* mem, nuint size)
    {
        Debug.Assert((size & ((nuint)sizeof(nuint) - 1)) == 0);
        GCCommon.MemSet(mem, 0, size);
    }

#if BACKGROUND_GC
    public static byte* get_start_address(heap_segment* seg)
    {
#if USE_REGIONS
        byte* start = heap_segment.heap_segment_mem(seg);
#else
        byte* start = heap_segment.heap_segment_read_only_p(seg) != 0 ? heap_segment.heap_segment_mem(seg) : (byte*)seg;
#endif
        return start;
    }

    public static void decommit_mark_array_by_seg(heap_segment* seg)
    {
        // if BGC is disabled (the finalize watchdog does this at shutdown), the mark array could have
        // been set to NULL.
        if (mark_array is null)
        {
            return;
        }

        nuint flags = seg->flags;

        if ((flags & heap_segment.heap_segment_flags_ma_committed) != 0 ||
            (flags & heap_segment.heap_segment_flags_ma_pcommitted) != 0)
        {
            byte* start = get_start_address(seg);
            byte* end = heap_segment.heap_segment_reserved(seg);

            if ((flags & heap_segment.heap_segment_flags_ma_pcommitted) != 0)
            {
                start = lowest_address > start ? lowest_address : start;
                end = highest_address < end ? highest_address : end;
            }

            nuint beg_word = card_table_info.mark_word_of(start);
            nuint end_word = card_table_info.mark_word_of(card_table_info.align_on_mark_word(end));
            byte* decommit_start = align_on_page((byte*)&mark_array[(nint)beg_word]);
            byte* decommit_end = align_lower_page((byte*)&mark_array[(nint)end_word]);
            nuint size = (nuint)(decommit_end - decommit_start);

            if (decommit_start < decommit_end)
            {
                bool decommitted = virtual_decommit(decommit_start, size, recorded_committed_mark_array_bucket);
                Debug.Assert(decommitted);
            }
        }
    }
#endif

#if USE_REGIONS
    // return true if we actually decommitted anything
    public static bool decommit_step(ulong step_milliseconds)
    {
        if (settings.pause_mode == gc_pause_mode.pause_no_gc)
        {
            // don't decommit at all if we have entered a no gc region
            return false;
        }

        nuint decommit_size = 0;

        nuint max_decommit_step_size = unchecked(DECOMMIT_SIZE_PER_MILLISECOND * (nuint)step_milliseconds);
        for (int kind = (int)free_region_kind.basic_free_region;
             kind < (int)free_region_kind.count_free_region_kinds;
             kind++)
        {
            region_free_list* regions_to_decommit = global_regions_to_decommit_of(kind);
            while (region_free_list.get_num_free_regions(regions_to_decommit) > 0)
            {
                heap_segment* region = region_free_list.unlink_region_front(regions_to_decommit);
                nuint size = decommit_region(region, recorded_committed_free_bucket, -1);
                decommit_size += size;
                if (decommit_size >= max_decommit_step_size)
                {
                    return true;
                }
            }
        }

        if (never_decommit_p)
        {
            return decommit_size != 0;
        }

        return decommit_size != 0;
    }

    public static nuint decommit_region(heap_segment* region, int bucket, int h_number)
    {
        GCEvents.GCEventFireGCFreeSegment_V1(heap_segment.heap_segment_mem(region));
        byte* page_start = align_lower_page(get_region_start(region));
        byte* decommit_end = heap_segment.heap_segment_committed(region);
        nuint decommit_size = (nuint)(decommit_end - page_start);
        bool decommit_succeeded_p;
        if (never_decommit_p)
        {
            // VirtualDecommit is a no-op when never_decommit_p is set, so skip it and
            // update committed bookkeeping directly. Memory clearing is handled below.
            decommit_succeeded_p = true;
            reduce_committed_bytes(page_start, decommit_size, bucket, h_number, true);
        }
        else
        {
            decommit_succeeded_p = virtual_decommit(page_start, decommit_size, bucket, h_number);
        }

        bool require_clearing_memory_p = !decommit_succeeded_p || never_decommit_p;
        if (require_clearing_memory_p)
        {
            byte* clear_end = never_decommit_p ? heap_segment.heap_segment_used(region) : heap_segment.heap_segment_committed(region);
            nuint clear_size = (nuint)(clear_end - page_start);
            memclr(page_start, clear_size);
            heap_segment.heap_segment_used(region) = heap_segment.heap_segment_mem(region);
        }
        else
        {
            heap_segment.heap_segment_committed(region) = heap_segment.heap_segment_mem(region);
        }

#if BACKGROUND_GC
        // Under USE_REGIONS, mark array is never partially committed. So we are only checking for this
        // flag here.
        if ((region->flags & heap_segment.heap_segment_flags_ma_committed) != 0)
        {
            decommit_mark_array_by_seg(region);
            region->flags &= ~heap_segment.heap_segment_flags_ma_committed;
        }
#endif

        Debug.Assert(
            never_decommit_p || !decommit_succeeded_p
                ? heap_segment.heap_segment_used(region) ==
                    heap_segment.heap_segment_mem(region)
                : heap_segment.heap_segment_committed(region) ==
                    heap_segment.heap_segment_mem(region));
#if BACKGROUND_GC
        Debug.Assert((region->flags & heap_segment.heap_segment_flags_ma_committed) == 0);
#endif

        global_region_allocator.delete_region(get_region_start(region));

        return decommit_size;
    }
#endif
}
#pragma warning restore CS8981
