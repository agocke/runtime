// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the bounded WKS USE_REGIONS sweep leaves from sweep.cpp and gcinternal.h.

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
            heap_segment* current_heap_segment = generation.generation_start_segment(condemned_gen);
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
            args.free_list_gen_number = get_plan_gen_num(current_gen_num);
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
                    if (((CObjectHeader*)plug)->IsBGCMarkBitSet() != 0)
                    {
                        ((CObjectHeader*)plug)->ClearBGCMarkBit();
                    }

                    if (((CObjectHeader*)plug)->IsFreeObjInCompactBitSet() != 0)
                    {
                        ((CObjectHeader*)plug)->ClearFreeObjInCompactBit();
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
            make_unused_array(gap_start, size);

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
