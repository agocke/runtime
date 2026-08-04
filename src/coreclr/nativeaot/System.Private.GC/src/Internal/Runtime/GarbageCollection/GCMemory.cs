// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the dependency-closed address/commit accounting helpers from memory.cpp.

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
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
    public static CLRCriticalSection check_commit_cs;
    public static nuint current_total_committed;
    public static recorded_committed_bucket_array committed_by_oh;
    public static nuint current_total_committed_bookkeeping;
    public static nuint heap_hard_limit;
    public static object_heap_array heap_hard_limit_oh;
    public static bool never_decommit_p;

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

    public static bool virtual_decommit(void* address, nuint size, int bucket, int h_number)
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
}
#pragma warning restore CS8981
