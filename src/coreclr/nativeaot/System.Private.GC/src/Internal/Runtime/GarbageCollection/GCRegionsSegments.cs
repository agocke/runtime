// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the dependency-closed WKS USE_REGIONS helpers from init.cpp,
// regions_segments.cpp, plan_phase.cpp, background.cpp, diagnostics.cpp, and gc.cpp.

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
#if USE_REGIONS
internal enum bookkeeping_element
{
    card_table_element,
    brick_table_element,
    card_bundle_table_element,
    region_to_generation_table_element,
    seg_mapping_table_element,
#if BACKGROUND_GC
    mark_array_element,
#endif
    total_bookkeeping_elements,
}
#endif

internal unsafe partial struct gc_heap
{
#if USE_REGIONS
    public static uint* card_table;
    public static short* brick_table;
    public static region_free_list_array free_regions;
    public static byte** initial_regions;

    [InlineArray((int)bookkeeping_element.total_bookkeeping_elements + 1)]
    internal struct bookkeeping_layout_array
    {
        private nuint _element0;
    }

    [InlineArray((int)bookkeeping_element.total_bookkeeping_elements)]
    internal struct bookkeeping_size_array
    {
        private nuint _element0;
    }

    public static bookkeeping_layout_array card_table_element_layout;
    public static byte* bookkeeping_covered_committed;
    public static bookkeeping_size_array bookkeeping_sizes;
    public static byte* bookkeeping_start;
#if BACKGROUND_GC
    public static volatile bgc_state current_bgc_state;
    public static byte* background_saved_lowest_address;
    public static byte* background_saved_highest_address;
    public static volatile int gc_background_running;
#endif

    public static region_free_list* free_regions_of(int kind)
    {
        Debug.Assert(kind >= (int)free_region_kind.basic_free_region && kind < (int)free_region_kind.count_free_region_kinds);
        return (region_free_list*)Unsafe.AsPointer(ref free_regions[kind]);
    }

    public static bool allocate_initial_regions(int number_of_heaps)
    {
        const nuint InitialRegionsPerHeap = (nuint)gc_generation_num.total_generation_count * 2;
        nuint heap_count = unchecked((nuint)number_of_heaps);
        if (heap_count > nuint.MaxValue / InitialRegionsPerHeap ||
            heap_count * InitialRegionsPerHeap > nuint.MaxValue / (nuint)sizeof(byte*))
        {
            initial_regions = null;
            return false;
        }

        initial_regions = (byte**)SyncImports.ManagedGC_AllocZeroed(
            heap_count * InitialRegionsPerHeap * (nuint)sizeof(byte*));
        if (initial_regions is null)
        {
            return false;
        }

        for (int i = 0; i < number_of_heaps; i++)
        {
            bool succeed = global_region_allocator.allocate_large_region(
                (int)gc_generation_num.poh_generation,
                initial_region_start(i, (int)gc_generation_num.poh_generation),
                initial_region_end(i, (int)gc_generation_num.poh_generation),
                allocate_direction.allocate_forward,
                0,
                null);
            Debug.Assert(succeed);
        }

        for (int i = 0; i < number_of_heaps; i++)
        {
            for (int gen_num = (int)gc_generation_num.max_generation; gen_num >= 0; gen_num--)
            {
                bool succeed = global_region_allocator.allocate_basic_region(
                    gen_num,
                    initial_region_start(i, gen_num),
                    initial_region_end(i, gen_num),
                    null);
                Debug.Assert(succeed);
            }
        }

        for (int i = 0; i < number_of_heaps; i++)
        {
            bool succeed = global_region_allocator.allocate_large_region(
                (int)gc_generation_num.loh_generation,
                initial_region_start(i, (int)gc_generation_num.loh_generation),
                initial_region_end(i, (int)gc_generation_num.loh_generation),
                allocate_direction.allocate_forward,
                0,
                null);
            Debug.Assert(succeed);
        }

        return true;
    }

    public static void get_initial_region(int gen, int hn, byte** region_start, byte** region_end)
    {
        *region_start = *initial_region_start(hn, gen);
        *region_end = *initial_region_end(hn, gen);
    }

    private static byte** initial_region_start(int hn, int gen)
    {
        return initial_regions + (((nint)hn * (int)gc_generation_num.total_generation_count + gen) * 2);
    }

    private static byte** initial_region_end(int hn, int gen)
    {
        return initial_region_start(hn, gen) + 1;
    }

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

    public static void init_heap_segment(heap_segment* seg, gc_heap* hp, byte* start, nuint size, int gen_num, bool existing_region_p = false)
    {
#if BACKGROUND_GC
        seg->flags = existing_region_p ? seg->flags & heap_segment.heap_segment_flags_ma_committed : 0;
#else
        seg->flags = 0;
#endif
        heap_segment.heap_segment_next(seg) = null;
        heap_segment.heap_segment_plan_allocated(seg) = heap_segment.heap_segment_mem(seg);
        heap_segment.heap_segment_allocated(seg) = heap_segment.heap_segment_mem(seg);
        heap_segment.heap_segment_saved_allocated(seg) = heap_segment.heap_segment_mem(seg);
#if !USE_REGIONS || MULTIPLE_HEAPS
        heap_segment.heap_segment_decommit_target(seg) = heap_segment.heap_segment_reserved(seg);
#endif
#if BACKGROUND_GC
        heap_segment.heap_segment_background_allocated(seg) = null;
        heap_segment.heap_segment_saved_bg_allocated(seg) = null;
#endif

#if MULTIPLE_HEAPS
        heap_segment.heap_segment_heap(seg) = hp;
#endif

        int gen_num_for_region = gen_num < GCInterfaceOffsets.max_generation ? gen_num : GCInterfaceOffsets.max_generation;
        set_region_gen_num(seg, gen_num_for_region);
        heap_segment.heap_segment_plan_gen_num(seg) = gen_num_for_region;
        heap_segment.heap_segment_swept_in_plan(seg) = 0;
        int num_basic_regions = (int)(size >> (int)min_segment_size_shr);
        nuint basic_region_size = (nuint)1 << (int)min_segment_size_shr;
        if (num_basic_regions > 1)
        {
            for (int i = 1; i < num_basic_regions; i++)
            {
                byte* basic_region_start = start + ((nuint)i * basic_region_size);
                heap_segment* basic_region = get_region_info(basic_region_start);
                heap_segment.heap_segment_allocated(basic_region) = (byte*)(nint)(-i);
                heap_segment.heap_segment_gen_num(basic_region) = (byte)gen_num_for_region;
                heap_segment.heap_segment_plan_gen_num(basic_region) = gen_num_for_region;

#if MULTIPLE_HEAPS
                heap_segment.heap_segment_heap(basic_region) = hp;
#endif
            }
        }
    }

    private static nuint get_card_table_element_alignment(bookkeeping_element element)
    {
        switch (element)
        {
            case bookkeeping_element.card_table_element:
            case bookkeeping_element.card_bundle_table_element:
                return (nuint)sizeof(uint);

            case bookkeeping_element.brick_table_element:
                return (nuint)sizeof(short);

            case bookkeeping_element.region_to_generation_table_element:
                return (nuint)sizeof(byte);

            case bookkeeping_element.seg_mapping_table_element:
                return (nuint)sizeof(byte*);

#if BACKGROUND_GC
            case bookkeeping_element.mark_array_element:
#endif
            case bookkeeping_element.total_bookkeeping_elements:
                return (nuint)GCToOSInterface.GetPageSize();

            default:
                Debug.Assert(false);
                return (nuint)1;
        }
    }

    private static void get_card_table_element_sizes(byte* start, byte* end, nuint* sizes)
    {
        for (int i = (int)bookkeeping_element.card_table_element;
             i < (int)bookkeeping_element.total_bookkeeping_elements;
             i++)
        {
            sizes[i] = 0;
        }

        if (start == end)
        {
            return;
        }

        sizes[(int)bookkeeping_element.card_table_element] = card_table_info.size_card_of(start, end);
        sizes[(int)bookkeeping_element.brick_table_element] = card_table_info.size_brick_of(start, end);
        sizes[(int)bookkeeping_element.region_to_generation_table_element] = size_region_to_generation_table_of(start, end);
        sizes[(int)bookkeeping_element.seg_mapping_table_element] = size_seg_mapping_table_of(start, end);
#if BACKGROUND_GC
        sizes[(int)bookkeeping_element.mark_array_element] = card_table_info.size_mark_array_of(start, end);
#endif
    }

    private static void get_card_table_element_layout(byte* start, byte* end, nuint* layout)
    {
        nuint* sizes = stackalloc nuint[(int)bookkeeping_element.total_bookkeeping_elements];
        get_card_table_element_sizes(start, end, sizes);

        layout[(int)bookkeeping_element.card_table_element] =
            align_on_size((nuint)sizeof(card_table_info), get_card_table_element_alignment(bookkeeping_element.card_table_element));
        for (int i = (int)bookkeeping_element.brick_table_element;
             i <= (int)bookkeeping_element.total_bookkeeping_elements;
             i++)
        {
            layout[i] = unchecked(layout[i - 1] + sizes[i - 1]);
            if (i != (int)bookkeeping_element.total_bookkeeping_elements && sizes[i] != 0)
            {
                layout[i] = align_on_size(layout[i], get_card_table_element_alignment((bookkeeping_element)i));
            }
        }
    }

    private static nuint align_on_size(nuint value, nuint alignment)
    {
        return unchecked((value + alignment - 1) & ~(alignment - 1));
    }

    private static bool get_card_table_commit_layout(
        byte* from,
        byte* to,
        byte** commit_begins,
        nuint* commit_sizes,
        nuint* new_sizes)
    {
        byte* start = GCCommon.g_gc_lowest_address;

        bool initial_commit = from == start;
        bool additional_commit = !initial_commit && to > from;
        if (!initial_commit && !additional_commit)
        {
            return false;
        }

#if DEBUG
        nuint* layout = stackalloc nuint[(int)bookkeeping_element.total_bookkeeping_elements + 1];
        get_card_table_element_layout(start, GCCommon.g_gc_highest_address, layout);
        for (int i = (int)bookkeeping_element.card_table_element;
             i <= (int)bookkeeping_element.total_bookkeeping_elements;
             i++)
        {
            Debug.Assert(layout[i] == card_table_element_layout[i]);
        }
#endif

        get_card_table_element_sizes(start, to, new_sizes);

        for (int i = (int)bookkeeping_element.card_table_element;
             i <= (int)bookkeeping_element.seg_mapping_table_element;
             i++)
        {
            byte* required_begin;
            byte* required_end;
            byte* commit_begin;

            if (initial_commit)
            {
                required_begin = bookkeeping_start + (i == (int)bookkeeping_element.card_table_element ? 0 : (nint)card_table_element_layout[i]);
                required_end = bookkeeping_start + (nint)card_table_element_layout[i] + (nint)new_sizes[i];
                commit_begin = align_lower_page(required_begin);
            }
            else
            {
                Debug.Assert(additional_commit);
                required_begin = bookkeeping_start + (nint)card_table_element_layout[i] + (nint)bookkeeping_sizes[i];
                required_end = required_begin + (nint)(new_sizes[i] - bookkeeping_sizes[i]);
                commit_begin = align_on_page(required_begin);
            }

            Debug.Assert(required_begin <= required_end);
            byte* commit_end = align_on_page(required_end);
            byte* element_end = align_lower_page(bookkeeping_start + (nint)card_table_element_layout[i + 1]);
            if (commit_end > element_end)
            {
                commit_end = element_end;
            }

            if (commit_begin > commit_end)
            {
                commit_begin = commit_end;
            }

            commit_begins[i] = commit_begin;
            commit_sizes[i] = (nuint)(commit_end - commit_begin);
        }

        return true;
    }

    public static bool inplace_commit_card_table(byte* from, byte* to)
    {
        byte** commit_begins = stackalloc byte*[(int)bookkeeping_element.total_bookkeeping_elements];
        nuint* commit_sizes = stackalloc nuint[(int)bookkeeping_element.total_bookkeeping_elements];
        nuint* new_sizes = stackalloc nuint[(int)bookkeeping_element.total_bookkeeping_elements];

        if (!get_card_table_commit_layout(from, to, commit_begins, commit_sizes, new_sizes))
        {
            return true;
        }

        int failed_commit = -1;
        for (int i = (int)bookkeeping_element.card_table_element;
             i <= (int)bookkeeping_element.seg_mapping_table_element;
             i++)
        {
            if (commit_sizes[i] != 0 &&
                !virtual_commit(commit_begins[i], commit_sizes[i], recorded_committed_bookkeeping_bucket, -1))
            {
                failed_commit = i;
                break;
            }
        }

        if (failed_commit == -1)
        {
            for (int i = (int)bookkeeping_element.card_table_element;
                 i < (int)bookkeeping_element.total_bookkeeping_elements;
                 i++)
            {
                bookkeeping_sizes[i] = new_sizes[i];
            }

            return true;
        }

        for (int i = (int)bookkeeping_element.card_table_element; i < failed_commit; i++)
        {
            if (commit_sizes[i] != 0)
            {
                bool succeeded = virtual_decommit(
                    commit_begins[i],
                    commit_sizes[i],
                    recorded_committed_bookkeeping_bucket,
                    -1);
                Debug.Assert(succeeded);
            }
        }

        return false;
    }

    public static uint* make_card_table(byte* start, byte* end)
    {
        Debug.Assert(GCCommon.g_gc_lowest_address == start);
        Debug.Assert(GCCommon.g_gc_highest_address == end);

        nuint* layout = stackalloc nuint[(int)bookkeeping_element.total_bookkeeping_elements + 1];
        get_card_table_element_layout(start, end, layout);
        for (int i = (int)bookkeeping_element.card_table_element;
             i <= (int)bookkeeping_element.total_bookkeeping_elements;
             i++)
        {
            card_table_element_layout[i] = layout[i];
        }

        nuint alloc_size = card_table_element_layout[(int)bookkeeping_element.total_bookkeeping_elements];
        byte* mem = GCToOSInterface.VirtualReserve(alloc_size, 0, (uint)VirtualReserveFlags.None);
        bookkeeping_start = mem;
        if (mem is null)
        {
            return null;
        }

        if (!inplace_commit_card_table(start, global_region_allocator.get_left_used_unsafe()))
        {
            GCToOSInterface.VirtualRelease(mem, alloc_size);
            bookkeeping_start = null;
            return null;
        }

        bookkeeping_covered_committed = global_region_allocator.get_left_used_unsafe();

        uint* ct = (uint*)(mem + (nint)card_table_element_layout[(int)bookkeeping_element.card_table_element]);
        card_table_info.card_table_refcount(ct) = 0;
        card_table_info.card_table_lowest_address(ct) = start;
        card_table_info.card_table_highest_address(ct) = end;
        card_table_info.card_table_brick_table(ct) =
            (short*)(mem + (nint)card_table_element_layout[(int)bookkeeping_element.brick_table_element]);
        card_table_info.card_table_size(ct) = alloc_size;
        card_table_info.card_table_next(ct) = null;
        card_table_info.card_table_card_bundle_table(ct) =
            (uint*)(mem + (nint)card_table_element_layout[(int)bookkeeping_element.card_bundle_table_element]);

        map_region_to_generation =
            (region_info*)(mem + (nint)card_table_element_layout[(int)bookkeeping_element.region_to_generation_table_element]);
        map_region_to_generation_skewed = map_region_to_generation -
            (nint)size_region_to_generation_table_of(null, GCCommon.g_gc_lowest_address);

        GCCommon.seg_mapping_table =
            (seg_mapping*)(mem + (nint)card_table_element_layout[(int)bookkeeping_element.seg_mapping_table_element]);
        GCCommon.seg_mapping_table = (seg_mapping*)((byte*)GCCommon.seg_mapping_table -
            (nint)size_seg_mapping_table_of(null, align_lower_segment(GCCommon.g_gc_lowest_address)));

#if BACKGROUND_GC
        card_table_info.card_table_mark_array(ct) =
            (uint*)(mem + (nint)card_table_element_layout[(int)bookkeeping_element.mark_array_element]);
#endif

        return card_table_info.translate_card_table(ct);
    }

    public static bool initialize_region_bookkeeping()
    {
        uint* new_card_table = make_card_table(GCCommon.g_gc_lowest_address, GCCommon.g_gc_highest_address);
        if (new_card_table is null)
        {
            return false;
        }

        uint* ct = &new_card_table[(nint)card_table_info.card_word(card_table_info.gcard_of(GCCommon.g_gc_lowest_address))];
        brick_table = card_table_info.card_table_brick_table(ct);
        lowest_address = card_table_info.card_table_lowest_address(ct);
        highest_address = card_table_info.card_table_highest_address(ct);
#if BACKGROUND_GC
        mark_array = (uint*)((byte*)card_table_info.card_table_mark_array(ct) -
            (nint)card_table_info.size_mark_array_of(null, GCCommon.g_gc_lowest_address));
#endif
        card_table = new_card_table;
        return true;
    }

    public static byte on_used_changed(byte* new_used)
    {
        if (new_used <= bookkeeping_covered_committed)
        {
            return 1;
        }

        if (bookkeeping_start is null)
        {
            return 0;
        }

        bool speculative_commit_tried = false;
        while (true)
        {
            byte* new_bookkeeping_covered_committed;
            if (speculative_commit_tried)
            {
                new_bookkeeping_covered_committed = new_used;
            }
            else
            {
                nuint committed_size = (nuint)(bookkeeping_covered_committed - GCCommon.g_gc_lowest_address);
                nuint total_size = (nuint)(GCCommon.g_gc_highest_address - GCCommon.g_gc_lowest_address);
                Debug.Assert(committed_size <= total_size);
                Debug.Assert(committed_size < nuint.MaxValue / 2);
                nuint new_committed_size = committed_size * 2;
                if (new_committed_size > total_size)
                {
                    new_committed_size = total_size;
                }

                Debug.Assert(nuint.MaxValue - new_committed_size > (nuint)GCCommon.g_gc_lowest_address);
                byte* double_commit = GCCommon.g_gc_lowest_address + (nint)new_committed_size;
                new_bookkeeping_covered_committed = double_commit > new_used ? double_commit : new_used;
            }

            if (inplace_commit_card_table(bookkeeping_covered_committed, new_bookkeeping_covered_committed))
            {
                bookkeeping_covered_committed = new_bookkeeping_covered_committed;
                break;
            }

            if (new_bookkeeping_covered_committed == new_used)
            {
                return 0;
            }

            speculative_commit_tried = true;
        }

        return 1;
    }

    public static heap_segment* make_heap_segment(byte* new_pages, nuint size, gc_heap* hp, int gen_num)
    {
        gc_oh_num oh = gen_to_oh(gen_num);
        nuint initial_commit = never_decommit_p ? size : GCToOSInterface.GetPageSize();
        int h_number =
#if MULTIPLE_HEAPS
            hp->heap_number;
#else
            0;
#endif

        if (!virtual_commit(new_pages, initial_commit, (int)oh, h_number))
        {
            return null;
        }

        heap_segment* new_segment = get_region_info(new_pages);
        byte* start = new_pages + sizeof(aligned_plug_and_gap);
        heap_segment.heap_segment_mem(new_segment) = start;
        heap_segment.heap_segment_used(new_segment) = start;
        heap_segment.heap_segment_reserved(new_segment) = new_pages + (nint)size;
        heap_segment.heap_segment_committed(new_segment) = new_pages + (nint)initial_commit;

        init_heap_segment(new_segment, hp, new_pages, size, gen_num);

        return new_segment;
    }

    public static heap_segment* allocate_new_region(gc_heap* hp, int gen_num, bool uoh_p, nuint size = 0)
    {
        byte* start = null;
        byte* end = null;

        Debug.Assert(uoh_p || size == 0);

        bool allocated_p = uoh_p
            ? global_region_allocator.allocate_large_region(gen_num, &start, &end, allocate_direction.allocate_forward, size, &on_used_changed)
            : global_region_allocator.allocate_basic_region(gen_num, &start, &end, &on_used_changed);

        if (!allocated_p)
        {
            return null;
        }

        heap_segment* res = make_heap_segment(start, (nuint)(end - start), hp, gen_num);

        if (res is null)
        {
            global_region_allocator.delete_region(start);
        }

        return res;
    }

    // USE_REGIONS TODO: SOH should be able to get a large region and split it up into basic regions
    // if needed.
    // USE_REGIONS TODO: In Server GC we should allow to get a free region from another heap.
    public static heap_segment* get_free_region(gc_heap* hp, int gen_number, nuint size = 0)
    {
        heap_segment* region;

        if (gen_number <= GCInterfaceOffsets.max_generation)
        {
            Debug.Assert(size == 0);
            region = region_free_list.unlink_region_front(free_regions_of((int)free_region_kind.basic_free_region));
        }
        else
        {
            nuint LARGE_REGION_SIZE = global_region_allocator.get_large_region_alignment();

            Debug.Assert(size >= LARGE_REGION_SIZE);
            if (size == LARGE_REGION_SIZE)
            {
                region = region_free_list.unlink_region_front(free_regions_of((int)free_region_kind.large_free_region));
            }
            else
            {
                region = region_free_list.unlink_smallest_region(
                    free_regions_of((int)free_region_kind.huge_free_region),
                    size);
                if (region is null)
                {
                    if (settings.pause_mode == gc_pause_mode.pause_no_gc)
                    {
                        // In case of no-gc-region, the gc lock is being held by the thread
                        // triggering the GC.
                        assert_holding_gc_lock();
                    }
                    else
                    {
                        assert_holding_gc_lock_by_current_thread();
                    }

                    region = region_free_list.unlink_smallest_region(
                        (region_free_list*)Unsafe.AsPointer(ref global_free_huge_regions),
                        size);
                }
            }
        }

        if (region is not null)
        {
            byte* region_start = get_region_start(region);
            byte* region_end = heap_segment.heap_segment_reserved(region);
            init_heap_segment(region, hp, region_start, (nuint)(region_end - region_start), gen_number, true);

            gc_oh_num oh = gen_to_oh(gen_number);
            nuint committed = (nuint)(heap_segment.heap_segment_committed(region) - get_region_start(region));
            if (committed > 0)
            {
                check_commit_cs.Enter();
                committed_by_oh[(int)oh] += committed;
                Debug.Assert(committed_by_oh[recorded_committed_free_bucket] >= committed);
                committed_by_oh[recorded_committed_free_bucket] -= committed;
                check_commit_cs.Leave();
            }

            Debug.Assert(heap_segment.heap_segment_allocated(region) == heap_segment.heap_segment_mem(region));
        }
        else
        {
            region = allocate_new_region(hp, gen_number, gen_number > GCInterfaceOffsets.max_generation, size);
        }

        if (region is not null && !init_table_for_region(gen_number, region))
        {
            region = null;
        }

        return region;
    }

    public static generation* generation_of(generation* generation_table, int n)
    {
        Debug.Assert(n < (int)gc_generation_num.total_generation_count && n >= 0);
        return generation_table + n;
    }

    public static void make_generation(generation* generation_table, int gen_num, heap_segment* seg, byte* start)
    {
        generation* gen = generation_of(generation_table, gen_num);

        gen->gen_num = gen_num;
#if !USE_REGIONS
        gen->allocation_start = start;
        gen->plan_allocation_start = null;
#endif
        gen->allocation_context.alloc_ptr = null;
        gen->allocation_context.alloc_limit = null;
        gen->allocation_context.alloc_bytes = 0;
        gen->allocation_context.alloc_bytes_uoh = 0;
        gen->allocation_context_start_region = null;
        gen->start_segment = seg;

#if USE_REGIONS
        gen->tail_region = seg;
        gen->tail_ro_region = null;
#endif
        gen->allocation_segment = seg;
        gen->free_list_space = 0;
        gen->free_list_allocated = 0;
        gen->end_seg_allocated = 0;
        gen->condemned_allocated = 0;
        gen->sweep_allocated = 0;
        gen->free_obj_space = 0;
        gen->allocation_size = 0;
        gen->pinned_allocation_sweep_size = 0;
        gen->pinned_allocation_compact_size = 0;
        gen->allocate_end_seg_p = 0;
        allocator.clear(&gen->free_list_allocator);

#if TARGET_64BIT && !TARGET_WASM
        gen->set_bgc_mark_bit_p = 0;
#endif
    }

    public static heap_segment* heap_segment_rw(heap_segment* ns)
    {
        if (ns is null || heap_segment.heap_segment_read_only_p(ns) == 0)
        {
            return ns;
        }

        do
        {
            ns = heap_segment.heap_segment_next(ns);
        }
        while (ns is not null && heap_segment.heap_segment_read_only_p(ns) != 0);

        return ns;
    }

    public static heap_segment* heap_segment_next_rw(heap_segment* seg)
    {
        heap_segment* ns = heap_segment.heap_segment_next(seg);
        return heap_segment_rw(ns);
    }

    public static void thread_uoh_segment(generation* generation_table, int gen_number, heap_segment* new_seg)
    {
        heap_segment* seg = generation.generation_allocation_segment(generation_of(generation_table, gen_number));

        while (heap_segment_next_rw(seg) is not null)
        {
            seg = heap_segment_next_rw(seg);
        }

        heap_segment.heap_segment_next(seg) = new_seg;
    }

    public static heap_segment* get_new_region(
        generation* generation_table,
        gc_heap* hp,
        int gen_number,
        nuint size = 0)
    {
        heap_segment* new_region = get_free_region(hp, gen_number, size);

        if (new_region is not null)
        {
            switch (gen_number)
            {
                default:
                    Debug.Assert((new_region->flags &
                        (heap_segment.heap_segment_flags_loh | heap_segment.heap_segment_flags_poh)) == 0);
                    break;

                case (int)gc_generation_num.loh_generation:
                    new_region->flags |= heap_segment.heap_segment_flags_loh;
                    break;

                case (int)gc_generation_num.poh_generation:
                    new_region->flags |= heap_segment.heap_segment_flags_poh;
                    break;
            }

            generation* gen = generation_of(generation_table, gen_number);
            heap_segment.heap_segment_next(generation.generation_tail_region(gen)) = new_region;
            generation.generation_tail_region(gen) = new_region;
        }

        return new_region;
    }

    public static gc_oh_num gen_to_oh(int gen)
    {
        switch (gen)
        {
            case (int)gc_generation_num.soh_gen0:
            case (int)gc_generation_num.soh_gen1:
            case (int)gc_generation_num.soh_gen2:
                return gc_oh_num.soh;

            case (int)gc_generation_num.loh_generation:
                return gc_oh_num.loh;

            case (int)gc_generation_num.poh_generation:
                return gc_oh_num.poh;

            default:
                Debug.Assert(false);
                return gc_oh_num.unknown;
        }
    }

#if BACKGROUND_GC
    public static void verify_mark_array_cleared(byte* begin, byte* end, uint* mark_array_addr)
    {
#if DEBUG
        nuint markw = card_table_info.mark_word_of(begin);
        nuint markw_end = card_table_info.mark_word_of(end);

        while (markw < markw_end)
        {
            Debug.Assert(mark_array_addr[(nint)markw] == 0);
            markw++;
        }
#else
        _ = begin;
        _ = end;
        _ = mark_array_addr;
#endif
    }

    public static bool commit_mark_array_by_range(byte* begin, byte* end, uint* mark_array_addr)
    {
        nuint beg_word = card_table_info.mark_word_of(begin);
        nuint end_word = card_table_info.mark_word_of(card_table_info.align_on_mark_word(end));
        byte* commit_start = align_lower_page((byte*)&mark_array_addr[(nint)beg_word]);
        byte* commit_end = align_on_page((byte*)&mark_array_addr[(nint)end_word]);
        nuint size = (nuint)(commit_end - commit_start);

        if (virtual_commit(commit_start, size, recorded_committed_mark_array_bucket, -1))
        {
            verify_mark_array_cleared(begin, end, mark_array_addr);
            return true;
        }

        return false;
    }

    public static bool commit_mark_array_new_seg(heap_segment* seg, uint* new_card_table = null, byte* new_lowest_address = null)
    {
        byte* start = get_start_address(seg);
        byte* end = heap_segment.heap_segment_reserved(seg);

        byte* lowest = background_saved_lowest_address;
        byte* highest = background_saved_highest_address;

        nuint commit_flag = 0;

        if ((highest >= start) && (lowest <= end))
        {
            if ((start >= lowest) && (end <= highest))
            {
                commit_flag = heap_segment.heap_segment_flags_ma_committed;
            }
            else
            {
                commit_flag = heap_segment.heap_segment_flags_ma_pcommitted;
                Debug.Assert(false);
            }

            byte* commit_start = lowest > start ? lowest : start;
            byte* commit_end = highest < end ? highest : end;

            if (!commit_mark_array_by_range(commit_start, commit_end, mark_array))
            {
                return false;
            }

            if (new_card_table is null)
            {
                new_card_table = card_table;
            }

            if (card_table != new_card_table)
            {
                if (new_lowest_address is null)
                {
                    new_lowest_address = GCCommon.g_gc_lowest_address;
                }

                uint* ct = &new_card_table[(nint)card_table_info.card_word(card_table_info.gcard_of(new_lowest_address))];
                uint* ma = (uint*)((byte*)card_table_info.card_table_mark_array(ct)
                    - card_table_info.size_mark_array_of(null, new_lowest_address));

                if (!commit_mark_array_by_range(commit_start, commit_end, ma))
                {
                    return false;
                }
            }

            seg->flags |= commit_flag;
        }

        return true;
    }
#endif

    public static bool init_table_for_region(int gen_number, heap_segment* region)
    {
#if BACKGROUND_GC
        if (((region->flags & heap_segment.heap_segment_flags_ma_committed) == 0) &&
            !commit_mark_array_new_seg(region))
        {
            decommit_region(region, (int)gen_to_oh(gen_number), 0);
            return false;
        }

        if ((region->flags & heap_segment.heap_segment_flags_ma_committed) != 0)
        {
            bgc_verify_mark_array_cleared(region, true);
        }
#endif

        if (gen_number <= GCInterfaceOffsets.max_generation)
        {
            nuint first_brick = brick_of(heap_segment.heap_segment_mem(region));
            brick_table[(nint)first_brick] = -1;
        }
        else
        {
            Debug.Assert(brick_table[(nint)brick_of(heap_segment.heap_segment_mem(region))] == 0);
        }

        return true;
    }

    // Note that this gets the basic region index for obj. If the obj is in a large region,
    // this region may not be the start of it.
    public static heap_segment* region_of(byte* obj)
    {
        nuint index = (nuint)obj >> (int)min_segment_size_shr;
        seg_mapping* entry = &GCCommon.seg_mapping_table[(nint)index];

        return (heap_segment*)entry;
    }

    public static heap_segment* get_region_at_index(nuint index)
    {
        index += (nuint)GCCommon.g_gc_lowest_address >> (int)min_segment_size_shr;
        return (heap_segment*)&GCCommon.seg_mapping_table[(nint)index];
    }

    public static int get_region_gen_num(heap_segment* region)
    {
        return heap_segment.heap_segment_gen_num(region);
    }

    public static void set_region_gen_num(heap_segment* region, int gen_num)
    {
        Debug.Assert(gen_num < (1 << (sizeof(byte) * 8)));
        Debug.Assert(gen_num >= 0);
        heap_segment.heap_segment_gen_num(region) = (byte)gen_num;

        byte* region_start = get_region_start(region);
        byte* region_end = heap_segment.heap_segment_reserved(region);

        nuint region_index_start = get_basic_region_index_for_address(region_start);
        nuint region_index_end = get_basic_region_index_for_address(region_end);
        region_info entry = (region_info)((gen_num << (int)region_info.RI_PLAN_GEN_SHR) | gen_num);
        for (nuint region_index = region_index_start; region_index < region_index_end; region_index++)
        {
            Debug.Assert(gen_num <= GCInterfaceOffsets.max_generation);
            map_region_to_generation[(nint)region_index] = entry;
        }

        if (gen_num <= (int)gc_generation_num.soh_gen1)
        {
            if ((region_start < ephemeral_low) || (ephemeral_high < region_end))
            {
                fixed (int* lock_address = &GCWriteBarrier.write_barrier_spin_lock.@lock)
                {
                    while (true)
                    {
                        if (Interlocked.CompareExchange(lock_address, 0, GCSpinLock.lock_free) < 0)
                        {
                            break;
                        }

                        if ((ephemeral_low <= region_start) && (region_end <= ephemeral_high))
                        {
                            return;
                        }

                        while (GCEnv.VolatileLoadWithoutBarrier(lock_address) >= 0)
                        {
                            GCEnv.YieldProcessor();
                        }
                    }

#if DEBUG
                    GCWriteBarrier.write_barrier_spin_lock.holding_thread = GCToEEInterface.GetThread();
#endif

                    if ((region_start < ephemeral_low) || (ephemeral_high < region_end))
                    {
                        byte* new_ephemeral_low = region_start < ephemeral_low ? region_start : ephemeral_low;
                        byte* new_ephemeral_high = ephemeral_high < region_end ? region_end : ephemeral_high;

                        GCWriteBarrier.stomp_write_barrier_ephemeral(
                            new_ephemeral_low,
                            new_ephemeral_high,
                            map_region_to_generation_skewed,
                            (byte)min_segment_size_shr);

                        if (ephemeral_low < new_ephemeral_low)
                        {
                            GCToOSInterface.DebugBreak();
                        }

                        if (new_ephemeral_high < ephemeral_high)
                        {
                            GCToOSInterface.DebugBreak();
                        }

                        ephemeral_low = new_ephemeral_low;
                        ephemeral_high = new_ephemeral_high;
                    }

#if DEBUG
                    GCWriteBarrier.write_barrier_spin_lock.holding_thread = (void*)(-1);
#endif
                    GCEnv.VolatileStore(lock_address, GCSpinLock.lock_free);
                }
            }
        }
    }

    public static int get_region_gen_num(byte* obj)
    {
        nuint skewed_basic_region_index = get_skewed_basic_region_index_for_address(obj);
        int gen_num = (byte)map_region_to_generation_skewed[(nint)skewed_basic_region_index] & (byte)region_info.RI_GEN_MASK;
        Debug.Assert((int)gc_generation_num.soh_gen0 <= gen_num && gen_num <= (int)gc_generation_num.soh_gen2);
        Debug.Assert(gen_num == heap_segment.heap_segment_gen_num(region_of(obj)));
        return gen_num;
    }

    public static int get_region_plan_gen_num(byte* obj)
    {
        nuint skewed_basic_region_index = get_skewed_basic_region_index_for_address(obj);
        int plan_gen_num = (byte)map_region_to_generation_skewed[(nint)skewed_basic_region_index] >> (int)region_info.RI_PLAN_GEN_SHR;
        Debug.Assert((int)gc_generation_num.soh_gen0 <= plan_gen_num && plan_gen_num <= (int)gc_generation_num.soh_gen2);
        Debug.Assert(plan_gen_num == heap_segment.heap_segment_plan_gen_num(region_of(obj)));
        return plan_gen_num;
    }

    public static bool is_region_demoted(byte* obj)
    {
        nuint skewed_basic_region_index = get_skewed_basic_region_index_for_address(obj);
        bool demoted_p = ((byte)map_region_to_generation_skewed[(nint)skewed_basic_region_index] & (byte)region_info.RI_DEMOTED) != 0;
        Debug.Assert(demoted_p == heap_segment.heap_segment_demoted_p(region_of(obj)));
        return demoted_p;
    }

    public static void set_region_sweep_in_plan(heap_segment* region)
    {
        heap_segment.heap_segment_swept_in_plan(region) = 1;

        Debug.Assert(get_region_size(region) == global_region_allocator.get_region_alignment());

        byte* region_start = get_region_start(region);
        nuint region_index = get_basic_region_index_for_address(region_start);
        map_region_to_generation[(nint)region_index] = (region_info)((byte)map_region_to_generation[(nint)region_index] | (byte)region_info.RI_SIP);
    }

    public static void clear_region_sweep_in_plan(heap_segment* region)
    {
        heap_segment.heap_segment_swept_in_plan(region) = 0;

        Debug.Assert(get_region_size(region) == global_region_allocator.get_region_alignment());

        byte* region_start = get_region_start(region);
        nuint region_index = get_basic_region_index_for_address(region_start);
        map_region_to_generation[(nint)region_index] = (region_info)((byte)map_region_to_generation[(nint)region_index] & ~(byte)region_info.RI_SIP);
    }

    public static void clear_region_demoted(heap_segment* region)
    {
        region->flags &= ~heap_segment.heap_segment_flags_demoted;

        Debug.Assert(get_region_size(region) == global_region_allocator.get_region_alignment());

        byte* region_start = get_region_start(region);
        nuint region_index = get_basic_region_index_for_address(region_start);
        map_region_to_generation[(nint)region_index] = (region_info)((byte)map_region_to_generation[(nint)region_index] & ~(byte)region_info.RI_DEMOTED);
    }

    public static byte* get_uoh_start_object(heap_segment* region, generation* gen)
    {
        _ = gen;
        return heap_segment.heap_segment_mem(region);
    }

    public static byte* get_soh_start_object(heap_segment* region, generation* gen)
    {
        _ = gen;
        return heap_segment.heap_segment_mem(region);
    }

    public static nuint get_soh_start_obj_len(byte* start_obj)
    {
        _ = start_obj;
        return 0;
    }

    public static nuint brick_of(byte* add)
    {
        return (nuint)(add - lowest_address) / card_table_info.brick_size;
    }

    public static void clear_brick_table(byte* from, byte* end)
    {
        nuint from_brick = brick_of(from);
        nuint end_brick = brick_of(end);
        GCCommon.MemSet((byte*)&brick_table[(nint)from_brick], 0, (end_brick - from_brick) * (nuint)sizeof(short));
    }

    public static nuint card_of(byte* add)
    {
        return card_table_info.gcard_of(add);
    }

    private static uint lowbits(uint wrd, uint bits)
    {
        return wrd & ((1u << (int)bits) - 1u);
    }

    private static uint highbits(uint wrd, uint bits)
    {
        return wrd & ~((1u << (int)bits) - 1u);
    }

    public static void clear_cards(nuint start_card, nuint end_card)
    {
        if (start_card < end_card)
        {
            nuint start_word = card_table_info.card_word(start_card);
            nuint end_word = card_table_info.card_word(end_card);
            if (start_word < end_word)
            {
                uint bits = card_table_info.card_bit(start_card);
                card_table[(nint)start_word] &= lowbits(uint.MaxValue, bits);
                for (nuint i = start_word + 1; i < end_word; i++)
                {
                    card_table[(nint)i] = 0;
                }

                bits = card_table_info.card_bit(end_card);
                if (bits != 0)
                {
                    card_table[(nint)end_word] &= highbits(uint.MaxValue, bits);
                }
            }
            else
            {
                card_table[(nint)start_word] &= lowbits(uint.MaxValue, card_table_info.card_bit(start_card))
                    | highbits(uint.MaxValue, card_table_info.card_bit(end_card));
            }
        }
    }

    public static void clear_card_for_addresses(byte* start_address, byte* end_address)
    {
        nuint start_card = card_of(card_table_info.align_on_card(start_address));
        nuint end_card = card_of(card_table_info.align_lower_card(end_address));
        clear_cards(start_card, end_card);
    }

#if BACKGROUND_GC
    public static bool background_running_p()
    {
        return gc_background_running != 0;
    }

    public static bool bgc_mark_array_range(heap_segment* seg, bool whole_seg_p, byte** range_beg, byte** range_end)
    {
        byte* seg_start = heap_segment.heap_segment_mem(seg);
        byte* seg_end = whole_seg_p
            ? heap_segment.heap_segment_reserved(seg)
            : card_table_info.align_on_mark_word(heap_segment.heap_segment_allocated(seg));

        if ((seg_start < background_saved_highest_address) && (seg_end > background_saved_lowest_address))
        {
            *range_beg = seg_start > background_saved_lowest_address ? seg_start : background_saved_lowest_address;
            *range_end = seg_end < background_saved_highest_address ? seg_end : background_saved_highest_address;
            return true;
        }

        return false;
    }

    public static uint mark_array_marked(byte* add)
    {
        return mark_array[(nint)card_table_info.mark_word_of(add)] & (1u << (int)card_table_info.mark_bit_bit_of(add));
    }

    public static void bgc_verify_mark_array_cleared(heap_segment* seg, bool always_verify_p = false)
    {
#if DEBUG
        if (background_running_p() || always_verify_p)
        {
            byte* range_beg = null;
            byte* range_end = null;

            if (bgc_mark_array_range(seg, true, &range_beg, &range_end) || always_verify_p)
            {
                if (always_verify_p)
                {
                    range_beg = heap_segment.heap_segment_mem(seg);
                    range_end = heap_segment.heap_segment_reserved(seg);
                }

                nuint markw = card_table_info.mark_word_of(range_beg);
                nuint markw_end = card_table_info.mark_word_of(range_end);
                while (markw < markw_end)
                {
                    Debug.Assert(mark_array[(nint)markw] == 0);
                    markw++;
                }

                byte* p = card_table_info.mark_bit_address(markw_end * card_table_info.mark_word_width);
                while (p < range_end)
                {
                    Debug.Assert(mark_array_marked(p) == 0);
                    p++;
                }
            }
        }
#else
        _ = seg;
        _ = always_verify_p;
#endif
    }
#endif

    public static void clear_region_info(heap_segment* region)
    {
        if (heap_segment.heap_segment_uoh_p(region) == 0)
        {
            clear_brick_table(heap_segment.heap_segment_mem(region), heap_segment.heap_segment_reserved(region));
        }

        clear_card_for_addresses(get_region_start(region), heap_segment.heap_segment_reserved(region));

#if BACKGROUND_GC
        GCCommon.record_changed_seg(
            (byte*)region,
            heap_segment.heap_segment_reserved(region),
            settings.gc_index,
            current_bgc_state,
            changed_seg_state.seg_deleted);

        bgc_verify_mark_array_cleared(region);
#endif
    }

    public static void return_free_region(heap_segment* region)
    {
        gc_oh_num oh = heap_segment.heap_segment_oh(region);
        nuint committed = (nuint)(heap_segment.heap_segment_committed(region) - get_region_start(region));
        if (committed > 0)
        {
            check_commit_cs.Enter();
            Debug.Assert(committed_by_oh[(int)oh] >= committed);
            committed_by_oh[(int)oh] -= committed;
            committed_by_oh[recorded_committed_free_bucket] += committed;
            check_commit_cs.Leave();
        }

        clear_region_info(region);

        region_free_list.add_region_descending(region, (region_free_list*)Unsafe.AsPointer(ref free_regions[0]));

        byte* region_start = get_region_start(region);
        byte* region_end = heap_segment.heap_segment_reserved(region);

        int num_basic_regions = (int)((region_end - region_start) >> (int)min_segment_size_shr);
        for (int i = 0; i < num_basic_regions; i++)
        {
            byte* basic_region_start = region_start + ((nuint)i << (int)min_segment_size_shr);
            heap_segment* basic_region = get_region_info(basic_region_start);
            heap_segment.heap_segment_allocated(basic_region) = null;
#if MULTIPLE_HEAPS
            heap_segment.heap_segment_heap(basic_region) = null;
#endif
        }
    }
#endif
}
#pragma warning restore CS8981
