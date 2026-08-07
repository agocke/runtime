// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the dependency-closed WKS USE_REGIONS sweep region threading from sweep.cpp,
// plan_phase.cpp, allocation.cpp, regions_segments.cpp, and gcinternal.h.

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection;

#if USE_REGIONS
internal unsafe partial struct gc_heap
{
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
                    heap_segment.heap_segment_next(seg) = freeable_uoh_segment;
                    freeable_uoh_segment = seg;
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

    public static void make_free_lists(gc_heap* hp, int condemned_gen_number)
    {
        Debug.Assert(settings.promotion != 0);

        make_free_args args = default;
        int stop_gen_idx = get_stop_generation_index(condemned_gen_number);
        generation* generation_table = generation_table_of(hp);
        for (int i = condemned_gen_number; i >= stop_gen_idx; i--)
        {
            generation* condemned_gen = generation_of(generation_table, i);
            heap_segment* current_heap_segment = get_start_segment(condemned_gen);
            if (current_heap_segment is null)
            {
                continue;
            }

            byte* start_address = get_soh_start_object(current_heap_segment, condemned_gen);
            nuint current_brick = brick_of(start_address);

            Debug.Assert(current_heap_segment is not null);

            byte* end_address = heap_segment.heap_segment_allocated(current_heap_segment);
            nuint end_brick = brick_of(end_address - 1);

            int current_gen_num = i;
            args.free_list_gen_number =
                special_sweep_p ? current_gen_num : get_plan_gen_num(current_gen_num);
            args.free_list_gen = generation_of(generation_table, args.free_list_gen_number);
            args.highest_plug = null;

            while (true)
            {
                if (current_brick > end_brick)
                {
                    if (heap_segment_next_non_sip(current_heap_segment) is not null)
                    {
                        current_heap_segment = heap_segment_next_non_sip(current_heap_segment);
                    }
                    else
                    {
                        break;
                    }

                    current_brick = brick_of(heap_segment.heap_segment_mem(current_heap_segment));
                    end_brick = brick_of(heap_segment.heap_segment_allocated(current_heap_segment) - 1);
                    continue;
                }

                int brick_entry = brick_table[(nint)current_brick];
                if (brick_entry >= 0)
                {
                    make_free_list_in_brick(brick_address(current_brick) + brick_entry - 1, &args);
                    set_brick(current_brick, unchecked((nint)(args.highest_plug - brick_address(current_brick))));
                }
                else if (brick_entry > short.MinValue)
                {
#if DEBUG
                    nint offset = (nint)brick_of(args.highest_plug) - (nint)current_brick;
                    if ((brick_entry != -32767) && (offset != brick_entry))
                    {
                        Debug.Assert(brick_entry == -1);
                    }
#endif
                    set_brick(current_brick, -1);
                }

                current_brick++;
            }
        }

        check_seg_gen_num(
            generation.generation_allocation_segment(
                generation_of(generation_table, (int)gc_generation_num.max_generation)));

        thread_final_regions(hp, compact_p: false);

        generation* gen_gen0 = generation_of(generation_table, 0);
        hp->ephemeral_heap_segment = generation.generation_start_segment(gen_gen0);
        hp->alloc_allocated =
            heap_segment.heap_segment_allocated(hp->ephemeral_heap_segment);
    }

    public static void make_free_list_in_brick(byte* tree, make_free_args* args)
    {
        Debug.Assert(tree is not null);
        {
            short right_node = node_right_child(tree);
            short left_node = node_left_child(tree);
            args->highest_plug = null;
            if (tree is not null)
            {
                if (left_node != 0)
                {
                    make_free_list_in_brick(tree + left_node, args);
                }

                {
                    byte* plug = tree;
                    nuint gap_size = node_gap_size(tree);
                    byte* gap = plug - (nint)gap_size;
                    args->highest_plug = tree;

                    if (is_plug_padded(plug) != 0)
                    {
                        clear_plug_padded(plug);
                    }

#if TARGET_64BIT && !TARGET_WASM
                    if (is_plug_bgc_mark_bit_set(plug) != 0)
                    {
                        clear_plug_bgc_mark_bit(plug);
                    }

                    if (is_free_obj_in_compact_bit_set(plug) != 0)
                    {
                        clear_free_obj_in_compact_bit(plug);
                    }
#endif

                    thread_gap(gap, gap_size, args->free_list_gen);
                }

                if (right_node != 0)
                {
                    make_free_list_in_brick(tree + right_node, args);
                }
            }
        }
    }

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

    public static int dt_high_memory_load_p()
    {
        return settings.entry_memory_load >= high_memory_load_th || g_low_memory_status != 0
            ? 1
            : 0;
    }

    public static void reset_memory(byte* o, nuint sizeo)
    {
        if (never_decommit_p)
        {
            return;
        }

        if (sizeo > 128 * 1024)
        {
            nuint size_to_skip =
                unchecked(2 * (nuint)GCInterfaceOffsets.min_obj_size - plug_skew);
            byte* page_start = align_on_page(o + (nint)size_to_skip);
            byte* page_end = align_lower_page(
                o + (nint)sizeo - (nint)size_to_skip - (nint)plug_skew);
            nuint size = (nuint)(page_end - page_start);

            if (reset_mm_p != 0 && dt_high_memory_load_p() != 0)
            {
                reset_mm_p = GCToOSInterface.VirtualReset(page_start, size, false) ? 1 : 0;
            }
        }
    }

    public static heap_segment* find_first_valid_region(
        gc_heap* hp,
        heap_segment* region,
        bool compact_p,
        int* num_returned_regions)
    {
        generation* generation_table = generation_table_of(hp);
        check_seg_gen_num(
            generation.generation_allocation_segment(
                generation_of(generation_table, (int)gc_generation_num.max_generation)));

        if (region is null)
        {
            return null;
        }

        heap_segment* current_region = region;

        do
        {
            int gen_num = heap_segment.heap_segment_gen_num(current_region);
            int plan_gen_num;
            if (compact_p)
            {
                Debug.Assert(settings.compaction != 0);
                plan_gen_num = heap_segment.heap_segment_plan_gen_num(current_region);
            }
            else
            {
                plan_gen_num =
                    special_sweep_p ? gen_num : get_plan_gen_num(gen_num);
            }

            byte* allocated = compact_p
                ? heap_segment.heap_segment_plan_allocated(current_region)
                : heap_segment.heap_segment_allocated(current_region);
            if (heap_segment.heap_segment_mem(current_region) == allocated)
            {
                heap_segment* region_to_delete = current_region;
                current_region = heap_segment.heap_segment_next(current_region);
                return_free_region(region_to_delete);
                (*num_returned_regions)++;

                if (current_region is null)
                {
                    return null;
                }
            }
            else
            {
                if (compact_p)
                {
                    if (heap_segment.heap_segment_swept_in_plan(current_region) != 0)
                    {
                        Debug.Assert(
                            heap_segment.heap_segment_allocated(current_region) ==
                            heap_segment.heap_segment_plan_allocated(current_region));
                    }
                    else
                    {
                        heap_segment.heap_segment_allocated(current_region) =
                            heap_segment.heap_segment_plan_allocated(current_region);
                    }
                }
                else
                {
                    set_region_plan_gen_num(current_region, plan_gen_num);
                }

                if (gen_num >= (int)gc_generation_num.soh_gen2)
                {
                    decommit_heap_segment_pages(current_region, 0, hp->heap_number);
                }

                set_region_gen_num(current_region, plan_gen_num);
                break;
            }
        }
        while (current_region is not null);

        Debug.Assert(current_region is not null);

        if (heap_segment.heap_segment_swept_in_plan(current_region) != 0)
        {
            int gen_num = heap_segment.heap_segment_gen_num(current_region);
            generation* gen = generation_of(generation_table, gen_num);
            allocator.thread_sip_fl(generation.generation_allocator(gen), current_region);
            generation.generation_free_list_space(gen) = unchecked(
                generation.generation_free_list_space(gen) +
                heap_segment.heap_segment_free_list_size(current_region));
            generation.generation_free_obj_space(gen) = unchecked(
                generation.generation_free_obj_space(gen) +
                heap_segment.heap_segment_free_obj_size(current_region));
        }

        clear_region_sweep_in_plan(current_region);
        clear_region_demoted(current_region);

        return current_region;
    }

    public static void thread_final_regions(gc_heap* hp, bool compact_p)
    {
        int num_returned_regions = 0;
        int num_new_regions = 0;

        for (int i = 0; i < (int)gc_generation_num.max_generation; i++)
        {
            heap_segment* reserved_free_region = reserved_free_region_sip(i);
            if (reserved_free_region is not null)
            {
                return_free_region(reserved_free_region);
            }
        }

        int condemned_gen_number = settings.condemned_generation;
        generation_region_info* generation_final_regions =
            stackalloc generation_region_info[GCInterfaceOffsets.max_generation + 1];
        for (int i = 0; i <= GCInterfaceOffsets.max_generation; i++)
        {
            generation_final_regions[i] = default;
        }

        generation* generation_table = generation_table_of(hp);
        for (int gen_idx = (int)gc_generation_num.max_generation;
             gen_idx > condemned_gen_number;
             gen_idx--)
        {
            generation* gen = generation_of(generation_table, gen_idx);
            generation_final_regions[gen_idx].head =
                heap_segment_rw(generation.generation_start_segment(gen));
            generation_final_regions[gen_idx].tail =
                generation.generation_tail_region(gen);
        }

        for (int gen_idx = condemned_gen_number; gen_idx >= 0; gen_idx--)
        {
            heap_segment* current_region =
                heap_segment_rw(
                    generation.generation_start_segment(
                        generation_of(generation_table, gen_idx)));

            while ((current_region = find_first_valid_region(
                hp,
                current_region,
                compact_p,
                &num_returned_regions)) is not null)
            {
                Debug.Assert(
                    !compact_p ||
                    heap_segment.heap_segment_plan_gen_num(current_region) ==
                    heap_segment.heap_segment_gen_num(current_region));
                int new_gen_num =
                    heap_segment.heap_segment_plan_gen_num(current_region);
                heap_segment* next_region =
                    heap_segment.heap_segment_next(current_region);

                if (generation_final_regions[new_gen_num].head is not null)
                {
                    Debug.Assert(
                        generation_final_regions[new_gen_num].tail is not null);
                    heap_segment.heap_segment_next(
                        generation_final_regions[new_gen_num].tail) =
                        current_region;
                    generation_final_regions[new_gen_num].tail =
                        current_region;
                }
                else
                {
                    generation_final_regions[new_gen_num].head =
                        current_region;
                    generation_final_regions[new_gen_num].tail =
                        current_region;
                }

                current_region = next_region;
            }
        }

        for (int gen_idx = 0;
             gen_idx <= (int)gc_generation_num.max_generation;
             gen_idx++)
        {
            if (generation_final_regions[gen_idx].tail is not null)
            {
                heap_segment.heap_segment_next(
                    generation_final_regions[gen_idx].tail) = null;
            }
        }

        for (int gen_idx = 0;
             gen_idx <= (int)gc_generation_num.max_generation;
             gen_idx++)
        {
            bool condemned_p = gen_idx <= condemned_gen_number;
            Debug.Assert(
                condemned_p ||
                generation_final_regions[gen_idx].head is not null);

            generation* gen = generation_of(generation_table, gen_idx);
            heap_segment* start_region;

            if (generation_final_regions[gen_idx].head is not null)
            {
                start_region = generation_final_regions[gen_idx].head;
                if (condemned_p)
                {
                    thread_start_region(gen, start_region);
                }

                generation.generation_tail_region(gen) =
                    generation_final_regions[gen_idx].tail;
            }
            else
            {
                start_region = get_free_region(hp, gen_idx);
                Debug.Assert(start_region is not null);
                num_new_regions++;
                thread_start_region(gen, start_region);
            }

            if (condemned_p)
            {
                byte* gen_start = heap_segment.heap_segment_mem(start_region);
                reset_allocation_pointers(gen, gen_start);
            }
        }

        int net_added_regions = num_new_regions - num_returned_regions;
        if ((settings.compaction != 0 || special_sweep_p) &&
            net_added_regions > 0)
        {
            new_regions_in_threading += net_added_regions;
            Debug.Assert(false, "we shouldn't be getting new regions in TFR!");
        }
    }

    public static void thread_start_region(generation* gen, heap_segment* region)
    {
        heap_segment* prev_region = generation.generation_tail_ro_region(gen);

        if (prev_region is not null)
        {
            heap_segment.heap_segment_next(prev_region) = region;
        }
        else
        {
            generation.generation_start_segment(gen) = region;
        }

        generation.generation_tail_region(gen) = region;
    }

    public static void clear_unused_array(byte* x, nuint size)
    {
        *((nuint*)x - 1) = 0;
        ((CObjectHeader*)x)->UnsetFree();

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

                ((CObjectHeader*)tmp)->UnsetFree();

                remaining_size = unchecked(remaining_size - current_size);
                tmp = (byte*)unchecked((nuint)tmp + current_size);
            }

            ((CObjectHeader*)tmp)->UnsetFree();
        }
#else
        _ = size;
#endif
    }

    public static void uoh_thread_gap_front(byte* gap_start, nuint size, generation* gen)
    {
        if (size >= unchecked(2 * (nuint)GCInterfaceOffsets.min_obj_size))
        {
            generation.generation_free_list_space(gen) =
                unchecked(generation.generation_free_list_space(gen) + size);
            allocator.thread_item_front(generation.generation_allocator(gen), gap_start, size);
        }
    }
}
#endif
