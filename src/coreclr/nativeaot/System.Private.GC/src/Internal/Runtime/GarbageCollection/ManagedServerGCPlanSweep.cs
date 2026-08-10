// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Server plan-time UOH *sweep* leaf and its per-heap segment-return / free-list family, translated
// from the SVR-namespace compilation of sweep.cpp, plan_phase.cpp, allocation.cpp, and
// regions_segments.cpp for the active x64 Linux SERVER_GC -> MULTIPLE_HEAPS -> DYNAMIC_HEAP_COUNT ->
// USE_REGIONS feature chain. sweep_uoh_objects is what the plan_phase driver runs for POH (which is
// never compacted, so plan_poh is just this sweep) and for a non-compacting LOH: it walks each UOH
// region rebuilding the generation's free list from the surviving (marked) objects, trimming or
// returning the emptied segments.
//
//   * sweep_uoh_objects clears the generation allocator and free-list/free-obj accounting, then
//     walks every UOH region. On crossing an empty non-start region it unlinks the region, threads
//     it onto this heap's freeable_uoh_segment list, and repairs the generation's start/tail region
//     bookkeeping (update_start_tail_regions); a partially-live region has its allocated tail trimmed
//     and its trailing pages decommitted. Between marked plugs the gap is threaded onto the free list
//     (thread_gap); marked objects are un-marked / un-pinned (uoh_object_marked) as they are scanned.
//   * thread_gap makes the inter-plug gap an unused array and threads it onto the generation's free
//     list (or records it as free-object space when below min_free_list); uoh_thread_gap_front threads
//     a gap onto the *front* of the free list (the UOH allocation path's fast-reuse leaf).
//   * update_start_tail_regions fixes a generation's start / tail region links when a region is
//     unlinked from the middle, start, or tail of its region chain.
//   * rearrange_uoh_segments / rearrange_small_heap_segments / delay_free_segments return this heap's
//     freeable segments to the free-region pool. freeable_uoh_segment / freeable_soh_segment are
//     PER_HEAP_FIELD_MAINTAINED, so they are instance-owned for MULTIPLE_HEAPS (static in WKS) and
//     reached through the gc_heap* parameter here; the static WKS versions stay in GCRegionsSegments.cs
//     under !MULTIPLE_HEAPS. return_free_region is likewise PER_HEAP under MULTIPLE_HEAPS: the server
//     overload targets the returning worker's own server_free_regions lists (the WKS static overload
//     stays for the single-heap build).
//
// make_unused_array / size / AlignQword / Align / get_alignment_constant / pinned_plug
// (ManagedServerGC.cs), heap_segment_rw / get_uoh_start_object / decommit_heap_segment_pages /
// return_free_region / background_running_p (GCRegionsSegments.cs), generation_of /
// generation_table_of / allocator.clear / thread_item / thread_item_front and the generation /
// heap_segment accessors (GCPriv.cs) are reused as-is. lowest_address / highest_address remain static
// in the managed model (their PER_HEAP_FIELD_INIT_ONLY conversion is deferred), so uoh_object_marked
// keeps its static signature exactly like the WKS translation.
//
// No collection is routed by this slice: the plan_phase driver that sequences sweep_uoh_objects (the
// settings.loh_compaction gating that picks plan_loh vs this sweep for the LOH, the plan_poh call, and
// the delay_free_segments / rearrange_uoh_segments segment cleanup it drives during gc1 /
// garbage_collect_background), fix_generation_bounds, the plan-phase joins, and the relocate / compact
// / make_free_lists execution all remain deferred, so nothing here runs against a live heap yet.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    // sweep.cpp uoh_object_marked: report (and optionally clear) the mark bit of a UOH object. Objects
    // outside [lowest_address, highest_address) are treated as marked so out-of-range read-only LOH
    // objects are never swept. lowest_address / highest_address stay static in the managed model, so
    // this is byte-for-byte identical to the WKS translation and needs no gc_heap* parameter.
    public static int uoh_object_marked(byte* o, int clearp)
    {
        int m = 0;
        if ((o >= lowest_address) && (o < highest_address))
        {
            if (((CObjectHeader*)o)->IsMarked() != 0)
            {
                if (clearp != 0)
                {
                    ((CObjectHeader*)o)->ClearMarked();
                    if (((CObjectHeader*)o)->IsPinned() != 0)
                    {
                        ((CObjectHeader*)o)->GetHeader()->ClrGCBit();
                    }
                }

                m = 1;
            }
        }
        else
        {
            m = 1;
        }

        return m;
    }

    // sweep.cpp thread_gap: turn the [gap_start, gap_start+size) inter-plug gap into an unused array
    // and thread it onto the generation's free list (or account it as free-object space when it is
    // smaller than min_free_list == 2 * min_obj_size).
    public static void thread_gap(byte* gap_start, nuint size, generation* gen)
    {
        if (size > 0)
        {
            Debug.Assert(size >= Align((nuint)GCInterfaceOffsets.min_obj_size));
            make_unused_array(
                gap_start,
                size,
                clearp: settings.concurrent == 0 && gen->gen_num != 0 ? 1 : 0,
                resetp: gen->gen_num == (int)gc_generation_num.max_generation ? 1 : 0);

            if (size >= unchecked(2 * (nuint)GCInterfaceOffsets.min_obj_size))
            {
                generation.generation_free_list_space(gen) =
                    unchecked(generation.generation_free_list_space(gen) + size);
                allocator.thread_item(generation.generation_allocator(gen), gap_start, size);
            }
            else
            {
                generation.generation_free_obj_space(gen) =
                    unchecked(generation.generation_free_obj_space(gen) + size);
            }
        }
    }

    // sweep.cpp uoh_thread_gap_front: thread a gap onto the *front* of the generation's free list (the
    // UOH allocation path's fast-reuse leaf). Only gaps at least min_free_list large are threaded.
    public static void uoh_thread_gap_front(byte* gap_start, nuint size, generation* gen)
    {
        if (size >= unchecked(2 * (nuint)GCInterfaceOffsets.min_obj_size))
        {
            generation.generation_free_list_space(gen) =
                unchecked(generation.generation_free_list_space(gen) + size);
            allocator.thread_item_front(generation.generation_allocator(gen), gap_start, size);
        }
    }

    // plan_phase.cpp update_start_tail_regions: repair a generation's start / tail region links after
    // region_to_delete is unlinked. When it was the start region, the tail read-only region (if any)
    // is re-pointed at next_region, otherwise the generation's start segment advances; when it was the
    // tail region, the tail falls back to prev_region. verify_regions is omitted to match the WKS
    // translation. No per-heap static state is touched, so this mirrors the WKS version exactly.
    public static void update_start_tail_regions(
        generation* gen,
        heap_segment* region_to_delete,
        heap_segment* prev_region,
        heap_segment* next_region)
    {
        if (region_to_delete == heap_segment_rw(generation.generation_start_segment(gen)))
        {
            Debug.Assert(prev_region is null);
            heap_segment* tail_ro_region = generation.generation_tail_ro_region(gen);

            if (tail_ro_region is not null)
            {
                heap_segment.heap_segment_next(tail_ro_region) = next_region;
            }
            else
            {
                generation.generation_start_segment(gen) = next_region;
            }
        }

        if (region_to_delete == generation.generation_tail_region(gen))
        {
            Debug.Assert(next_region is null);
            generation.generation_tail_region(gen) = prev_region;
        }
    }

    // sweep.cpp sweep_uoh_objects: sweep a UOH generation (POH, or a non-compacting LOH) in place,
    // rebuilding its free list from the surviving objects and returning / trimming emptied segments.
    // freeable_uoh_segment is instance-owned in this MULTIPLE_HEAPS build, so emptied regions are
    // threaded onto this heap's own list.
    public static void sweep_uoh_objects(gc_heap* hp, int gen_num)
    {
        generation* gen = generation_of(generation_table_of(hp), gen_num);
        heap_segment* start_seg = heap_segment_rw(generation.generation_start_segment(gen));

        Debug.Assert(start_seg is not null);

        heap_segment* seg = start_seg;
        heap_segment* prev_seg = null;
        byte* o = get_uoh_start_object(seg, gen);

        byte* plug_end = o;

        allocator.clear(generation.generation_allocator(gen));
        generation.generation_free_list_space(gen) = 0;
        generation.generation_free_obj_space(gen) = 0;
        generation.generation_free_list_allocated(gen) = 0;

        while (true)
        {
            if (o >= heap_segment.heap_segment_allocated(seg))
            {
                heap_segment* next_seg = heap_segment.heap_segment_next(seg);
                if (plug_end == heap_segment.heap_segment_mem(seg) &&
                    seg != start_seg &&
                    heap_segment.heap_segment_read_only_p(seg) == 0)
                {
                    Debug.Assert(prev_seg is not null);
                    heap_segment.heap_segment_next(prev_seg) = next_seg;
                    heap_segment.heap_segment_next(seg) = hp->freeable_uoh_segment;
                    hp->freeable_uoh_segment = seg;
                    update_start_tail_regions(gen, seg, prev_seg, next_seg);
                }
                else
                {
                    if (heap_segment.heap_segment_read_only_p(seg) == 0)
                    {
                        heap_segment.heap_segment_allocated(seg) = plug_end;
                        decommit_heap_segment_pages(seg, 0, hp->heap_number);
                    }

                    prev_seg = seg;
                }

                seg = next_seg;
                if (seg is null)
                {
                    break;
                }

                o = heap_segment.heap_segment_mem(seg);
                plug_end = o;
                continue;
            }

            if (uoh_object_marked(o, clearp: 1) != 0)
            {
                byte* plug_start = o;
                thread_gap(plug_end, (nuint)(plug_start - plug_end), gen);

                do
                {
                    o = (byte*)unchecked((nuint)o + Align(size(o), get_alignment_constant(small_object_p: false)));
                }
                while (o < heap_segment.heap_segment_allocated(seg) &&
                    uoh_object_marked(o, clearp: 1) != 0);

                plug_end = o;
            }
            else
            {
                while (o < heap_segment.heap_segment_allocated(seg) &&
                    uoh_object_marked(o, clearp: 0) == 0)
                {
                    o = (byte*)unchecked((nuint)o + Align(size(o), get_alignment_constant(small_object_p: false)));
                }
            }
        }

        generation.generation_allocation_segment(gen) =
            heap_segment_rw(generation.generation_start_segment(gen));
        Debug.Assert(generation.generation_allocation_segment(gen) is not null);
    }

    // regions_segments.cpp rearrange_uoh_segments: return every segment queued on this heap's
    // freeable_uoh_segment list to the free-region pool, then clear the list.
    public static void rearrange_uoh_segments(gc_heap* hp)
    {
        heap_segment* seg = hp->freeable_uoh_segment;
        while (seg is not null)
        {
            heap_segment* next_seg = heap_segment.heap_segment_next(seg);
            return_free_region(hp, seg);
            seg = next_seg;
        }

        hp->freeable_uoh_segment = null;
    }

#if BACKGROUND_GC
    // regions_segments.cpp rearrange_small_heap_segments: return this heap's freeable SOH segments to
    // the free-region pool.
    public static void rearrange_small_heap_segments(gc_heap* hp)
    {
        heap_segment* seg = hp->freeable_soh_segment;
        while (seg is not null)
        {
            heap_segment* next_seg = heap_segment.heap_segment_next(seg);
            return_free_region(hp, seg);
            seg = next_seg;
        }

        hp->freeable_soh_segment = null;
    }
#endif

    // regions_segments.cpp delay_free_segments: return this heap's freeable UOH segments and, when no
    // background GC is running, its freeable SOH segments. background_delay_delete_uoh_segments is not
    // yet translated, matching the WKS translation.
    public static void delay_free_segments(gc_heap* hp)
    {
        rearrange_uoh_segments(hp);
#if BACKGROUND_GC
        if (!background_running_p())
        {
            rearrange_small_heap_segments(hp);
        }
#endif
    }
}
#pragma warning restore CS8981

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
