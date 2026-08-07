// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dependency-free, WKS brick-tree, and region-survivor helpers in
// src/coreclr/gc/plan_phase.cpp.

using System;
using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection;

internal unsafe partial struct gc_heap
{
    public static bool is_induced_blocking(gc_reason reason)
    {
        return reason == gc_reason.reason_induced ||
            reason == gc_reason.reason_lowmemory_blocking ||
            reason == gc_reason.reason_induced_compacting ||
            reason == gc_reason.reason_induced_aggressive ||
            reason == gc_reason.reason_lowmemory_host_blocking;
    }

    public static int relative_index_power2_plug(nuint power2)
    {
        int index = index_of_highest_set_bit(power2);
        Debug.Assert(index <= MAX_INDEX_POWER2);

        return index < MIN_INDEX_POWER2 ? 0 : index - MIN_INDEX_POWER2;
    }

    public static int relative_index_power2_free_space(nuint power2)
    {
        int index = index_of_highest_set_bit(power2);
        Debug.Assert(index <= MAX_INDEX_POWER2);

        return index < MIN_INDEX_POWER2 ? -1 : index - MIN_INDEX_POWER2;
    }

    public static bool oddp(nuint integer)
    {
        return (integer & 1) != 0;
    }

    public static nuint logcount(nuint word)
    {
        Debug.Assert(word < 0x10000);
        nuint count;
        count = (word & 0x5555) + ((word >> 1) & 0x5555);
        count = (count & 0x3333) + ((count >> 2) & 0x3333);
        count = (count & 0x0F0F) + ((count >> 4) & 0x0F0F);
        count = (count & 0x00FF) + ((count >> 8) & 0x00FF);
        return count;
    }

    public static void clear_padding_in_expand(
        byte* old_loc,
        int set_padding_on_saved_p,
        mark* pinned_plug_entry)
    {
        if (set_padding_on_saved_p != 0)
        {
            clear_plug_padded(get_plug_start_in_saved(old_loc, pinned_plug_entry));
        }
        else
        {
            clear_plug_padded(old_loc);
        }
    }

    public static byte* insert_node(
        byte* new_node,
        nuint sequence_number,
        byte* tree,
        byte* last_node)
    {
        if (power_of_two_p(sequence_number))
        {
            set_node_left_child(new_node, unchecked((nint)(tree - new_node)));
            tree = new_node;
        }
        else if (oddp(sequence_number))
        {
            set_node_right_child(last_node, unchecked((nint)(new_node - last_node)));
        }
        else
        {
            byte* earlier_node = tree;
            nuint imax = logcount(sequence_number) - 2;
            for (nuint i = 0; i != imax; i++)
            {
                earlier_node += node_right_child(earlier_node);
            }

            short tmp_offset = node_right_child(earlier_node);
            Debug.Assert(tmp_offset != 0);
            set_node_left_child(new_node, unchecked((nint)((earlier_node + tmp_offset) - new_node)));
            set_node_right_child(earlier_node, unchecked((nint)(new_node - earlier_node)));
        }

        return tree;
    }

    public static nuint update_brick_table(
        byte* tree,
        nuint current_brick,
        byte* x,
        byte* plug_end)
    {
        if (tree is not null)
        {
            set_brick(current_brick, unchecked((nint)(tree - brick_address(current_brick))));
        }
        else
        {
            set_brick(current_brick, -1);
        }

        nuint b = 1 + current_brick;
        nint offset = 0;
        nuint last_br = brick_of(plug_end - 1);
        current_brick = brick_of(x - 1);
        while (b <= current_brick)
        {
            if (b <= last_br)
            {
                set_brick(b, --offset);
            }
            else
            {
                set_brick(b, -1);
            }

            b++;
        }
        return brick_of(x);
    }

#if !MULTIPLE_HEAPS
    public static nuint current_generation_size(gc_heap* hp, int gen_number)
    {
        dynamic_data* dd = dynamic_data_of(hp, gen_number);
        nuint gen_size = unchecked(
            dynamic_data.dd_current_size(dd) +
            dynamic_data.dd_desired_allocation(dd) -
            (nuint)dynamic_data.dd_new_allocation(dd));

        return gen_size;
    }
#endif

#if USE_REGIONS && !MULTIPLE_HEAPS
    public static bool expand_reused_seg_p()
    {
        return false;
    }

    public static void initialize_loh_pinned_queue_state()
    {
        loh_pinned_queue_tos = 0;
        loh_pinned_queue_bos = 0;
        loh_pinned_queue_length = 0;
        loh_pinned_queue_decay = LOH_PIN_DECAY;
        loh_pinned_queue = null;
    }

    public static void decay_loh_pinned_queue()
    {
        if (loh_pinned_queue is not null)
        {
            loh_pinned_queue_decay--;
            if (loh_pinned_queue_decay == 0)
            {
                SyncImports.ManagedGC_Free(loh_pinned_queue);
                loh_pinned_queue = null;
            }
        }
    }

    public static int loh_pinned_plug_que_empty_p()
    {
        return loh_pinned_queue_bos == loh_pinned_queue_tos ? 1 : 0;
    }

    public static mark* loh_pinned_plug_of(nuint bos)
    {
        return &loh_pinned_queue[bos];
    }

    public static void loh_set_allocator_next_pin(gc_heap* hp)
    {
        if (loh_pinned_plug_que_empty_p() == 0)
        {
            mark* oldest_entry = loh_oldest_pin();
            byte* plug = pinned_plug(oldest_entry);
            generation* gen = generation_of(
                generation_table_of(hp),
                (int)gc_generation_num.loh_generation);
            if (plug >= generation.generation_allocation_pointer(gen) &&
                plug < generation.generation_allocation_limit(gen))
            {
                generation.generation_allocation_limit(gen) = plug;
            }
            else
            {
                Debug.Assert(
                    !(plug < generation.generation_allocation_pointer(gen) &&
                      plug >= heap_segment.heap_segment_mem(
                          generation.generation_allocation_segment(gen))));
            }
        }
    }

    public static nuint loh_deque_pinned_plug()
    {
        nuint m = loh_pinned_queue_bos;
        loh_pinned_queue_bos++;
        return m;
    }

    public static mark* loh_oldest_pin()
    {
        return loh_pinned_plug_of(loh_pinned_queue_bos);
    }

    public static int loh_enque_pinned_plug(gc_heap* hp, byte* plug, nuint len)
    {
        Debug.Assert(
            len >= Align(
                (nuint)GCInterfaceOffsets.min_obj_size,
                get_alignment_constant(small_object_p: false)));

        if (loh_pinned_queue_length <= loh_pinned_queue_tos)
        {
            if (grow_mark_stack(
                    ref loh_pinned_queue,
                    ref loh_pinned_queue_length,
                    LOH_PIN_QUEUE_LENGTH) == 0)
            {
                return 0;
            }
        }

        mark* m = &loh_pinned_queue[loh_pinned_queue_tos];
        m->first = plug;
        m->len = len;
        loh_pinned_queue_tos++;
        loh_set_allocator_next_pin(hp);
        return 1;
    }

    public static bool loh_size_fit_p(
        nuint size,
        byte* alloc_pointer,
        byte* alloc_limit,
        bool end_p)
    {
        if (alloc_pointer > alloc_limit)
        {
            return false;
        }

        nuint pad = unchecked((nuint)(end_p ? 1 : 2) * AlignQword((nuint)sizeof(loh_padding_obj)));
        nuint available = (nuint)(alloc_limit - alloc_pointer);
        return pad <= available && size <= available - pad;
    }

    public static byte* loh_allocate_in_condemned(gc_heap* hp, nuint size)
    {
        generation* gen = generation_of(
            generation_table_of(hp),
            (int)gc_generation_num.loh_generation);

        while (true)
        {
            heap_segment* seg = generation.generation_allocation_segment(gen);
            if (seg is null)
            {
                return null;
            }

            byte* alloc_pointer = generation.generation_allocation_pointer(gen);
            byte* alloc_limit = generation.generation_allocation_limit(gen);
            bool end_p = alloc_limit == heap_segment.heap_segment_plan_allocated(seg);
            if (loh_size_fit_p(size, alloc_pointer, alloc_limit, end_p))
            {
                Debug.Assert(alloc_pointer >= heap_segment.heap_segment_mem(seg));
                byte* result = alloc_pointer;
                nuint loh_pad = AlignQword((nuint)sizeof(loh_padding_obj));

                generation.generation_allocation_pointer(gen) =
                    alloc_pointer + (nint)unchecked(size + loh_pad);
                Debug.Assert(
                    generation.generation_allocation_pointer(gen) <=
                    generation.generation_allocation_limit(gen));

                return result + (nint)loh_pad;
            }

            if (loh_pinned_plug_que_empty_p() == 0 &&
                alloc_limit == pinned_plug(loh_oldest_pin()))
            {
                mark* m = loh_pinned_plug_of(loh_deque_pinned_plug());
                nuint len = pinned_len(m);
                byte* plug = pinned_plug(m);
                if (plug < alloc_pointer)
                {
                    return null;
                }

                m->len = (nuint)(plug - alloc_pointer);
                generation.generation_allocation_pointer(gen) = plug + (nint)len;
                generation.generation_allocation_limit(gen) =
                    heap_segment.heap_segment_plan_allocated(seg);
                loh_set_allocator_next_pin(hp);
                continue;
            }

            if (alloc_limit != heap_segment.heap_segment_plan_allocated(seg))
            {
                generation.generation_allocation_limit(gen) =
                    heap_segment.heap_segment_plan_allocated(seg);
            }
            else if (heap_segment.heap_segment_plan_allocated(seg) !=
                     heap_segment.heap_segment_committed(seg))
            {
                heap_segment.heap_segment_plan_allocated(seg) =
                    heap_segment.heap_segment_committed(seg);
                generation.generation_allocation_limit(gen) =
                    heap_segment.heap_segment_plan_allocated(seg);
            }
            else
            {
                nuint loh_pad = AlignQword((nuint)sizeof(loh_padding_obj));
                if (loh_size_fit_p(
                        size,
                        generation.generation_allocation_pointer(gen),
                        heap_segment.heap_segment_reserved(seg),
                        end_p: true) &&
                    grow_heap_segment(
                        seg,
                        generation.generation_allocation_pointer(gen) +
                            (nint)unchecked(size + loh_pad),
                        hp->heap_number))
                {
                    heap_segment.heap_segment_plan_allocated(seg) =
                        heap_segment.heap_segment_committed(seg);
                    generation.generation_allocation_limit(gen) =
                        heap_segment.heap_segment_plan_allocated(seg);
                }
                else
                {
                    heap_segment* next_seg = heap_segment.heap_segment_next(seg);
                    Debug.Assert(
                        generation.generation_allocation_pointer(gen) >=
                        heap_segment.heap_segment_mem(seg));
                    if (loh_pinned_plug_que_empty_p() == 0)
                    {
                        byte* oldest_plug = pinned_plug(loh_oldest_pin());
                        if (oldest_plug < heap_segment.heap_segment_allocated(seg) &&
                            oldest_plug >= generation.generation_allocation_pointer(gen))
                        {
                            GCToEEInterface.HandleFatalError(CORINFO_EXCEPTION_GC);
                            return null;
                        }
                    }

                    Debug.Assert(
                        generation.generation_allocation_pointer(gen) <=
                        heap_segment.heap_segment_committed(seg));
                    heap_segment.heap_segment_plan_allocated(seg) =
                        generation.generation_allocation_pointer(gen);

                    if (next_seg is null)
                    {
                        GCToEEInterface.HandleFatalError(CORINFO_EXCEPTION_GC);
                        return null;
                    }

                    generation.generation_allocation_segment(gen) = next_seg;
                    generation.generation_allocation_pointer(gen) =
                        heap_segment.heap_segment_mem(next_seg);
                    generation.generation_allocation_limit(gen) =
                        generation.generation_allocation_pointer(gen);
                }
            }

            loh_set_allocator_next_pin(hp);
        }
    }

    private static bool validate_loh_compaction_prerequisites(gc_heap* hp)
    {
        if (hp is null ||
            settings.condemned_generation != GCInterfaceOffsets.max_generation ||
            (loh_compacted_p != 0 && settings.loh_compaction == 0) ||
            settings.concurrent != 0
#if BACKGROUND_GC
            || settings.background_p != 0 ||
            background_running_p()
#endif
            )
        {
            return false;
        }

        generation* gen = generation_of(
            generation_table_of(hp),
            (int)gc_generation_num.loh_generation);
        heap_segment* start_seg = generation.generation_start_segment(gen);
        heap_segment* slow_prefix = start_seg;
        heap_segment* fast_prefix = start_seg;
        while (start_seg is not null &&
               heap_segment.heap_segment_read_only_p(start_seg) != 0)
        {
            start_seg = heap_segment.heap_segment_next(start_seg);
            slow_prefix = slow_prefix is null
                ? null
                : heap_segment.heap_segment_next(slow_prefix);
            fast_prefix = fast_prefix is null
                ? null
                : heap_segment.heap_segment_next(fast_prefix);
            if (fast_prefix is not null)
            {
                fast_prefix = heap_segment.heap_segment_next(fast_prefix);
            }

            if (slow_prefix is not null && slow_prefix == fast_prefix)
            {
                return false;
            }
        }

        if (start_seg is null)
        {
            return false;
        }

        heap_segment* slow_seg = start_seg;
        heap_segment* fast_seg = start_seg;
        nuint pinned_index = 0;
        for (heap_segment* seg = start_seg;
             seg is not null;
             seg = heap_segment.heap_segment_next(seg))
        {
            byte* mem = heap_segment.heap_segment_mem(seg);
            byte* allocated = heap_segment.heap_segment_allocated(seg);
            if (heap_segment.heap_segment_read_only_p(seg) != 0 ||
                mem is null ||
                allocated < mem ||
                heap_segment.heap_segment_committed(seg) < allocated ||
                heap_segment.heap_segment_reserved(seg) <
                    heap_segment.heap_segment_committed(seg))
            {
                return false;
            }

            byte* o = mem;
            while (o < allocated)
            {
                CObjectHeader* header = (CObjectHeader*)o;
                if (header->GetMethodTable() is null)
                {
                    return false;
                }

                nuint object_size = size(o);
                nuint aligned_size = AlignQword(object_size);
                if (object_size == 0 ||
                    aligned_size <
                        Align(
                            (nuint)GCInterfaceOffsets.min_obj_size,
                            get_alignment_constant(small_object_p: false)) ||
                    aligned_size > (nuint)(allocated - o) ||
                    (header->IsPinned() != 0 && header->IsMarked() == 0))
                {
                    return false;
                }

                if (loh_compacted_p != 0 && header->IsPinned() != 0)
                {
                    if (loh_pinned_queue is null ||
                        pinned_index >= loh_pinned_queue_tos ||
                        pinned_plug(&loh_pinned_queue[pinned_index]) != o)
                    {
                        return false;
                    }

                    pinned_index++;
                }

                o += (nint)aligned_size;
            }

            if (o != allocated)
            {
                return false;
            }

            slow_seg = slow_seg is null
                ? null
                : heap_segment.heap_segment_next(slow_seg);
            fast_seg = fast_seg is null
                ? null
                : heap_segment.heap_segment_next(fast_seg);
            if (fast_seg is not null)
            {
                fast_seg = heap_segment.heap_segment_next(fast_seg);
            }

            if (slow_seg is not null && slow_seg == fast_seg)
            {
                return false;
            }
        }

        return loh_compacted_p == 0 ||
            (loh_pinned_queue_bos <= loh_pinned_queue_tos &&
             loh_pinned_queue_tos <= loh_pinned_queue_length &&
             pinned_index == loh_pinned_queue_tos);
    }

    public static bool plan_loh(gc_heap* hp)
    {
        if (!validate_loh_compaction_prerequisites(hp))
        {
            return false;
        }

        if (loh_pinned_queue is null)
        {
            nuint bytes = LOH_PIN_QUEUE_LENGTH * (nuint)sizeof(mark);
            loh_pinned_queue =
                (mark*)SyncImports.ManagedGC_AllocZeroed(bytes);
            if (loh_pinned_queue is null)
            {
                return false;
            }

            loh_pinned_queue_length = LOH_PIN_QUEUE_LENGTH;
        }

        loh_pinned_queue_decay = LOH_PIN_DECAY;
        loh_pinned_queue_tos = 0;
        loh_pinned_queue_bos = 0;

        generation* gen = generation_of(
            generation_table_of(hp),
            (int)gc_generation_num.loh_generation);
        heap_segment* start_seg =
            heap_segment_rw(generation.generation_start_segment(gen));
        Debug.Assert(start_seg is not null);
        heap_segment* seg = start_seg;
        byte* o = get_uoh_start_object(seg, gen);

        while (seg is not null)
        {
            heap_segment.heap_segment_plan_allocated(seg) =
                heap_segment.heap_segment_mem(seg);
            seg = heap_segment.heap_segment_next(seg);
        }

        seg = start_seg;
        heap_segment.heap_segment_plan_allocated(seg) = o;
        generation.generation_allocation_pointer(gen) = o;
        generation.generation_allocation_limit(gen) = o;
        generation.generation_allocation_segment(gen) = start_seg;

        while (true)
        {
            if (o >= heap_segment.heap_segment_allocated(seg))
            {
                seg = heap_segment.heap_segment_next(seg);
                if (seg is null)
                {
                    break;
                }

                o = heap_segment.heap_segment_mem(seg);
            }

            if (((CObjectHeader*)o)->IsMarked() != 0)
            {
                nuint object_size = AlignQword(size(o));
                byte* new_address;
                if (((CObjectHeader*)o)->IsPinned() != 0)
                {
                    if (loh_enque_pinned_plug(hp, o, object_size) == 0)
                    {
                        return false;
                    }

                    new_address = o;
                }
                else
                {
                    new_address = loh_allocate_in_condemned(hp, object_size);
                    if (new_address is null)
                    {
                        return false;
                    }
                }

                loh_set_node_relocation_distance(
                    o,
                    unchecked((nint)(new_address - o)));
                o += (nint)object_size;
                if (o < heap_segment.heap_segment_allocated(seg))
                {
                    Debug.Assert(((CObjectHeader*)o)->IsMarked() == 0);
                }
            }
            else
            {
                while (o < heap_segment.heap_segment_allocated(seg) &&
                       ((CObjectHeader*)o)->IsMarked() == 0)
                {
                    o += (nint)AlignQword(size(o));
                }
            }
        }

        while (loh_pinned_plug_que_empty_p() == 0)
        {
            mark* m = loh_pinned_plug_of(loh_deque_pinned_plug());
            nuint len = pinned_len(m);
            byte* plug = pinned_plug(m);
            heap_segment* nseg = generation.generation_allocation_segment(gen);
            if (nseg is null)
            {
                return false;
            }

            while (plug < generation.generation_allocation_pointer(gen) ||
                   plug >= heap_segment.heap_segment_allocated(nseg))
            {
                Debug.Assert(
                    plug < heap_segment.heap_segment_mem(nseg) ||
                    plug > heap_segment.heap_segment_reserved(nseg));
                Debug.Assert(
                    generation.generation_allocation_pointer(gen) >=
                    heap_segment.heap_segment_mem(nseg));
                Debug.Assert(
                    generation.generation_allocation_pointer(gen) <=
                    heap_segment.heap_segment_committed(nseg));

                heap_segment.heap_segment_plan_allocated(nseg) =
                    generation.generation_allocation_pointer(gen);
                nseg = heap_segment.heap_segment_next(nseg);
                if (nseg is null)
                {
                    return false;
                }

                generation.generation_allocation_segment(gen) = nseg;
                generation.generation_allocation_pointer(gen) =
                    heap_segment.heap_segment_mem(nseg);
            }

            if (plug < generation.generation_allocation_pointer(gen))
            {
                return false;
            }

            m->len = (nuint)(plug - generation.generation_allocation_pointer(gen));
            generation.generation_allocation_pointer(gen) = plug + (nint)len;
        }

        heap_segment.heap_segment_plan_allocated(
            generation.generation_allocation_segment(gen)) =
            generation.generation_allocation_pointer(gen);
        generation.generation_allocation_pointer(gen) = null;
        generation.generation_allocation_limit(gen) = null;
        return true;
    }

    public static nuint generation_plan_size(gc_heap* hp, int gen_number)
    {
        nuint result = 0;
        generation* generationTable = generation_table_of(hp);
        heap_segment* seg = heap_segment_rw(
            generation.generation_start_segment(generation_of(generationTable, gen_number)));

        while (seg is not null)
        {
            result += (nuint)(heap_segment.heap_segment_plan_allocated(seg) - heap_segment.heap_segment_mem(seg));
            seg = heap_segment.heap_segment_next(seg);
        }

        return result;
    }

    public static nuint generation_size(gc_heap* hp, int gen_number)
    {
        nuint result = 0;
        generation* generationTable = generation_table_of(hp);
        heap_segment* seg = heap_segment_rw(
            generation.generation_start_segment(generation_of(generationTable, gen_number)));

        while (seg is not null)
        {
            result += (nuint)(heap_segment.heap_segment_allocated(seg) - heap_segment.heap_segment_mem(seg));
            seg = heap_segment.heap_segment_next(seg);
        }

        return result;
    }

    // For SOH this returns the total sizes of the generation and its younger generations.
    // For LOH and POH this returns just that generation's size.
    public static nuint generation_sizes(gc_heap* hp, generation* gen, bool use_saved_p = false)
    {
        nuint result = 0;
        int gen_num = gen->gen_num;
        int start_gen_index = gen_num > GCInterfaceOffsets.max_generation ? gen_num : 0;
        generation* generationTable = generation_table_of(hp);

        for (int i = start_gen_index; i <= gen_num; i++)
        {
            heap_segment* seg = heap_segment_in_range(
                generation.generation_start_segment(generation_of(generationTable, i)));

            while (seg is not null)
            {
                byte* end = use_saved_p
                    ? heap_segment.heap_segment_saved_allocated(seg)
                    : heap_segment.heap_segment_allocated(seg);
                result = unchecked(result + (nuint)(end - heap_segment.heap_segment_mem(seg)));
                seg = heap_segment_next_in_range(seg);
            }
        }

        return result;
    }

    public static nuint get_current_allocated(gc_heap* hp)
    {
        dynamic_data* dd = dynamic_data_of(hp, (int)gc_generation_num.soh_gen0);
        nuint current_alloc = unchecked(
            dynamic_data.dd_desired_allocation(dd) - (nuint)dynamic_data.dd_new_allocation(dd));
        for (int i = (int)gc_generation_num.uoh_start_generation;
             i < (int)gc_generation_num.total_generation_count;
             i++)
        {
            dd = dynamic_data_of(hp, i);
            current_alloc = unchecked(
                current_alloc + dynamic_data.dd_desired_allocation(dd) -
                (nuint)dynamic_data.dd_new_allocation(dd));
        }

        return current_alloc;
    }

    public static nuint get_total_allocated(gc_heap* hp)
    {
        return get_current_allocated(hp);
    }

    public static nuint get_total_promoted(gc_heap* hp)
    {
        nuint total_promoted_size = 0;
        int highest_gen = settings.condemned_generation == (int)gc_generation_num.max_generation
            ? (int)gc_generation_num.total_generation_count - 1
            : settings.condemned_generation;

        for (int gen_number = 0; gen_number <= highest_gen; gen_number++)
        {
            total_promoted_size = unchecked(
                total_promoted_size +
                dynamic_data.dd_promoted_size(dynamic_data_of(hp, gen_number)));
        }

        return total_promoted_size;
    }

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
#endif

#if USE_REGIONS
    public static nuint END_SPACE_AFTER_GC_FL
    {
        get
        {
            nuint loh_size_threshold = (nuint)GCConfig.GetLOHThreshold();
            return unchecked(
                loh_size_threshold + Align((nuint)GCInterfaceOffsets.min_obj_size));
        }
    }

    public static void get_gen0_end_plan_space(gc_heap* hp)
    {
        end_gen0_region_space = 0;
        generation* generation_table = generation_table_of(hp);
        for (int gen_idx = settings.condemned_generation; gen_idx >= 0; gen_idx--)
        {
            generation* gen = generation_of(generation_table, gen_idx);
            heap_segment* region = heap_segment_rw(generation.generation_start_segment(gen));
            while (region is not null)
            {
                if (heap_segment.heap_segment_plan_gen_num(region) == 0)
                {
                    nuint end_plan_space = (nuint)(heap_segment.heap_segment_reserved(region) -
                                                    heap_segment.heap_segment_plan_allocated(region));
                    if (!gen0_large_chunk_found)
                    {
                        gen0_large_chunk_found = end_plan_space >= END_SPACE_AFTER_GC_FL;
                    }

                    end_gen0_region_space += end_plan_space;
                }

                region = heap_segment.heap_segment_next(region);
            }
        }
    }

    public static nuint get_gen0_end_space(gc_heap* hp, memory_type type)
    {
        nuint end_space = 0;
        generation* generation_table = generation_table_of(hp);
        heap_segment* seg = generation.generation_start_segment(
            generation_of(generation_table, (int)gc_generation_num.soh_gen0));

        while (seg is not null)
        {
            byte* allocated = heap_segment.heap_segment_allocated(seg);
            byte* end = type == memory_type.memory_type_reserved
                ? heap_segment.heap_segment_reserved(seg)
                : heap_segment.heap_segment_committed(seg);

            end_space += (nuint)(end - allocated);
            seg = heap_segment.heap_segment_next(seg);
        }

        return end_space;
    }

    public static void save_current_survived()
    {
        if (survived_per_region is null)
        {
            return;
        }

        nuint region_info_to_copy = unchecked(region_count * (nuint)sizeof(nuint));
        Buffer.MemoryCopy(
            survived_per_region,
            old_card_survived_per_region,
            (long)region_info_to_copy,
            (long)region_info_to_copy);
    }

    public static void update_old_card_survived()
    {
        if (survived_per_region is null)
        {
            return;
        }

        for (nuint region_index = 0; region_index < region_count; region_index++)
        {
            old_card_survived_per_region[(nint)region_index] = unchecked(
                survived_per_region[(nint)region_index] - old_card_survived_per_region[(nint)region_index]);
        }
    }

    public static void update_planned_gen0_free_space(nuint free_size, byte* plug)
    {
        _ = plug;

        gen0_pinned_free_space += free_size;
        if (!gen0_large_chunk_found)
        {
            gen0_large_chunk_found = free_size >= END_SPACE_AFTER_GC_FL;
        }
    }

    public const nuint demotion_plug_len_th = 6 * 1024 * 1024;
    public const int sip_surv_ratio_th = 90;
    public const int sip_old_card_surv_ratio_th = 90;

    public static void convert_to_pinned_plug(
        ref int last_npinned_plug_p,
        ref int last_pinned_plug_p,
        ref int pinned_plug_p,
        nuint ps,
        ref nuint artificial_pinned_size)
    {
        last_npinned_plug_p = 0;
        last_pinned_plug_p = 1;
        pinned_plug_p = 1;
        artificial_pinned_size = ps;
    }

    public static void store_plug_gap_info(
        gc_heap* hp,
        byte* plug_start,
        byte* plug_end,
        ref int last_npinned_plug_p,
        ref int last_pinned_plug_p,
        ref byte* last_pinned_plug,
        ref int pinned_plug_p,
        byte* last_object_in_last_plug,
        ref int merge_with_last_pin_p,
        nuint last_plug_len)
    {
        _ = last_plug_len;

        if (last_npinned_plug_p == 0 && last_pinned_plug_p == 0)
        {
            Debug.Assert(
                plug_start == plug_end ||
                (nuint)(plug_start - plug_end) >= Align((nuint)GCInterfaceOffsets.min_obj_size));
            set_gap_size(plug_start, (nuint)(plug_start - plug_end));
        }

        if (((CObjectHeader*)plug_start)->IsPinned() != 0)
        {
            int save_pre_plug_info_p = 0;
            if (last_npinned_plug_p != 0 || last_pinned_plug_p != 0)
            {
                save_pre_plug_info_p = 1;
            }

            pinned_plug_p = 1;
            last_npinned_plug_p = 0;

            if (last_pinned_plug_p != 0)
            {
                merge_with_last_pin_p = 1;
            }
            else
            {
                last_pinned_plug_p = 1;
                last_pinned_plug = plug_start;
                enque_pinned_plug(
                    hp,
                    last_pinned_plug,
                    save_pre_plug_info_p,
                    last_object_in_last_plug);

                if (save_pre_plug_info_p != 0)
                {
                    set_gap_size(plug_start, (nuint)sizeof(gap_reloc_pair));
                }
            }
        }
        else
        {
            if (last_pinned_plug_p != 0)
            {
                save_post_plug_info(
                    hp,
                    last_pinned_plug,
                    last_object_in_last_plug,
                    plug_start);
                set_gap_size(plug_start, (nuint)sizeof(gap_reloc_pair));
            }

            last_npinned_plug_p = 1;
            last_pinned_plug_p = 0;
        }
    }

    public static byte* find_next_marked(
        byte* x,
        byte* end,
        int use_mark_list,
        ref byte** mark_list_next,
        byte** mark_list_index)
    {
        if (use_mark_list != 0)
        {
            while (mark_list_next < mark_list_index && *mark_list_next <= x)
            {
                mark_list_next++;
            }

            x = end;
            if (mark_list_next < mark_list_index)
            {
                x = *mark_list_next;
            }
        }
        else
        {
            byte* xl = x;
            while (xl < end && ((CObjectHeader*)xl)->IsMarked() == 0)
            {
                nuint object_size = size(xl);
                Debug.Assert(object_size > 0);
                xl += (nint)Align(object_size);
            }

            Debug.Assert(xl <= end);
            x = xl;
        }

        return x;
    }

    public static void save_allocated(heap_segment* seg)
    {
        if (heap_segment.heap_segment_saved_allocated(seg) is null)
        {
            heap_segment.heap_segment_saved_allocated(seg) =
                heap_segment.heap_segment_allocated(seg);
        }
    }

    public static void skip_pins_in_alloc_region(
        gc_heap* hp,
        generation* consing_gen,
        int plan_gen_num)
    {
        heap_segment* alloc_region = generation.generation_allocation_segment(consing_gen);
        nuint skipped_pins_len = 0;
        while (pinned_plug_que_empty_p(hp) == 0)
        {
            byte* oldest_plug = pinned_plug(oldest_pin(hp));
            if (oldest_plug >= generation.generation_allocation_pointer(consing_gen) &&
                oldest_plug < heap_segment.heap_segment_allocated(alloc_region))
            {
                mark* m = pinned_plug_of(hp, deque_pinned_plug(hp));
                byte* plug = pinned_plug(m);
                nuint len = pinned_len(m);

                skipped_pins_len = unchecked(skipped_pins_len + len);
                set_new_pin_info(m, generation.generation_allocation_pointer(consing_gen));
                generation.generation_allocation_pointer(consing_gen) =
                    (byte*)unchecked((nuint)plug + len);
            }
            else
            {
                break;
            }
        }

        attribute_pin_higher_gen_alloc(
            hp,
            heap_segment.heap_segment_gen_num(alloc_region),
            plan_gen_num,
            skipped_pins_len);
        set_region_plan_gen_num_sip(alloc_region, plan_gen_num);
        heap_segment.heap_segment_plan_allocated(alloc_region) =
            generation.generation_allocation_pointer(consing_gen);
    }

    public static void decide_on_demotion_pin_surv(
        gc_heap* hp,
        heap_segment* region,
        int* no_pinned_surv_region_count,
        bool promote_gen1_pins_p,
        bool large_pins_p)
    {
        int gen_num = heap_segment.heap_segment_gen_num(region);
        int new_gen_num = 0;
        int pinned_surv = heap_segment.heap_segment_pinned_survived(region);
        bool promote_pins_p = large_pins_p;

        if (pinned_surv == 0)
        {
            (*no_pinned_surv_region_count)++;
        }
        else
        {
            if (!promote_pins_p &&
                gen_num == GCInterfaceOffsets.max_generation - 1 &&
                promote_gen1_pins_p)
            {
                promote_pins_p = true;
            }

            if (promote_pins_p)
            {
                new_gen_num = get_plan_gen_num(gen_num);
            }

            attribute_pin_higher_gen_alloc(hp, gen_num, new_gen_num, unchecked((nuint)pinned_surv));
        }

        set_region_plan_gen_num(region, new_gen_num);
    }

    public static void process_last_np_surv_region(
        gc_heap* hp,
        generation* consing_gen,
        int current_plan_gen_num,
        int next_plan_gen_num)
    {
        heap_segment* alloc_region = generation.generation_allocation_segment(consing_gen);
        byte* consing_gen_alloc_ptr = generation.generation_allocation_pointer(consing_gen);
        Debug.Assert(
            consing_gen_alloc_ptr >= heap_segment.heap_segment_mem(alloc_region) &&
            consing_gen_alloc_ptr <= heap_segment.heap_segment_reserved(alloc_region));

        if (current_plan_gen_num == next_plan_gen_num)
        {
            return;
        }

        if (generation.generation_allocation_pointer(consing_gen) ==
            heap_segment.heap_segment_mem(alloc_region))
        {
            return;
        }

        skip_pins_in_alloc_region(hp, consing_gen, current_plan_gen_num);
        heap_segment* next_region = heap_segment_next_non_sip(alloc_region);
        if (next_region is null)
        {
            int gen_num = heap_segment.heap_segment_gen_num(alloc_region);
            if (gen_num > 0)
            {
                next_region = generation.generation_start_segment(
                    generation_of(generation_table_of(hp), gen_num - 1));
            }
            else if (settings.promotion != 0)
            {
                Debug.Assert(next_plan_gen_num == 0);
                next_region = get_new_region(generation_table_of(hp), hp, 0);
                if (next_region is not null)
                {
                    regions_per_gen[0]++;
                    new_gen0_regions_in_plns++;
                }
                else
                {
                    special_sweep_p = true;
                }
            }
            else
            {
                Debug.Fail("ran out of regions for non-promotion planning");
            }
        }

        if (next_region is not null)
        {
            init_alloc_info(consing_gen, next_region);
        }
        else
        {
            Debug.Assert(special_sweep_p);
        }
    }

    public static void process_remaining_regions(
        gc_heap* hp,
        int current_plan_gen_num,
        generation* consing_gen)
    {
        Debug.Assert(
            current_plan_gen_num == 0 ||
            (settings.promotion == 0 && current_plan_gen_num == -1));

        if (special_sweep_p)
        {
            Debug.Assert(pinned_plug_que_empty_p(hp) != 0);
        }

        if (current_plan_gen_num == -1)
        {
            Debug.Assert(settings.promotion == 0);
            current_plan_gen_num = 0;

            heap_segment* alloc_region = generation.generation_allocation_segment(consing_gen);
            if (generation.generation_allocation_pointer(consing_gen) >
                heap_segment.heap_segment_mem(alloc_region))
            {
                skip_pins_in_alloc_region(hp, consing_gen, current_plan_gen_num);
                heap_segment* next_region = heap_segment_next_non_sip(alloc_region);
                if (next_region is null &&
                    heap_segment.heap_segment_gen_num(alloc_region) > 0)
                {
                    next_region = generation.generation_start_segment(
                        generation_of(
                            generation_table_of(hp),
                            heap_segment.heap_segment_gen_num(alloc_region) - 1));
                }

                if (next_region is not null)
                {
                    init_alloc_info(consing_gen, next_region);
                }
                else
                {
                    Debug.Assert(pinned_plug_que_empty_p(hp) != 0);
                    generation.generation_allocation_segment(consing_gen) = null;
                    generation.generation_allocation_pointer(consing_gen) = null;
                    generation.generation_allocation_limit(consing_gen) = null;
                }
            }
        }

        int to_be_empty_regions = 0;
        heap_segment* current_region = generation.generation_allocation_segment(consing_gen);
        bool actual_promote_gen1_pins_p = false;

        if (decide_promote_gen1_pins_p)
        {
            nuint gen1_pins_left = 0;
            nuint total_space_to_skip = 0;
            while (current_region is not null)
            {
                int gen_num = heap_segment.heap_segment_gen_num(current_region);
                if (gen_num == 0)
                {
                    break;
                }

                Debug.Assert(gen_num == GCInterfaceOffsets.max_generation - 1);
                if (heap_segment.heap_segment_swept_in_plan(current_region) == 0)
                {
                    gen1_pins_left = unchecked(
                        gen1_pins_left +
                        (nuint)heap_segment.heap_segment_pinned_survived(current_region));
                    total_space_to_skip = unchecked(
                        total_space_to_skip + get_region_size(current_region));
                }

                current_region = heap_segment.heap_segment_next(current_region);
            }

            if (total_space_to_skip != 0)
            {
                nuint gen1_surv = dynamic_data.dd_survived_size(
                    dynamic_data_of(hp, GCInterfaceOffsets.max_generation - 1));
                if (gen1_surv != 0)
                {
                    float pin_frag_ratio =
                        (float)gen1_pins_left / (float)total_space_to_skip;
                    float pin_surv_ratio = (float)gen1_pins_left / (float)gen1_surv;
                    actual_promote_gen1_pins_p =
                        decide_on_gen1_pin_promotion(pin_frag_ratio, pin_surv_ratio);
                }
            }
        }

        maxgen_pinned_compact_before_advance =
            generation.generation_pinned_allocation_compact_size(
                generation_of(
                    generation_table_of(hp),
                    GCInterfaceOffsets.max_generation));

        bool large_pins_p = false;
        while (pinned_plug_que_empty_p(hp) == 0)
        {
            byte* oldest_plug = pinned_plug(oldest_pin(hp));
            heap_segment* nseg = generation.generation_allocation_segment(consing_gen);

            while (oldest_plug < generation.generation_allocation_pointer(consing_gen) ||
                   oldest_plug >= heap_segment.heap_segment_allocated(nseg))
            {
                Debug.Assert(
                    oldest_plug < heap_segment.heap_segment_mem(nseg) ||
                    oldest_plug > heap_segment.heap_segment_reserved(nseg));
                Debug.Assert(
                    generation.generation_allocation_pointer(consing_gen) >=
                    heap_segment.heap_segment_mem(nseg));
                Debug.Assert(
                    generation.generation_allocation_pointer(consing_gen) <=
                    heap_segment.heap_segment_committed(nseg));
                Debug.Assert(heap_segment.heap_segment_swept_in_plan(nseg) == 0);

                heap_segment.heap_segment_plan_allocated(nseg) =
                    generation.generation_allocation_pointer(consing_gen);
                decide_on_demotion_pin_surv(
                    hp,
                    nseg,
                    &to_be_empty_regions,
                    actual_promote_gen1_pins_p,
                    large_pins_p);

                heap_segment* next_seg = heap_segment_next_non_sip(nseg);
                if (next_seg is null && heap_segment.heap_segment_gen_num(nseg) > 0)
                {
                    next_seg = generation.generation_start_segment(
                        generation_of(
                            generation_table_of(hp),
                            heap_segment.heap_segment_gen_num(nseg) - 1));
                }

                Debug.Assert(next_seg is not null);
                nseg = next_seg;
                large_pins_p = false;
                generation.generation_allocation_segment(consing_gen) = nseg;
                generation.generation_allocation_pointer(consing_gen) =
                    heap_segment.heap_segment_mem(nseg);
            }

            mark* m = pinned_plug_of(hp, deque_pinned_plug(hp));
            byte* plug = pinned_plug(m);
            nuint len = pinned_len(m);
            if (!large_pins_p)
            {
                large_pins_p = len >= demotion_plug_len_th;
            }

            set_new_pin_info(m, generation.generation_allocation_pointer(consing_gen));
            nuint free_size = pinned_len(m);
            update_planned_gen0_free_space(free_size, plug);
            generation.generation_allocation_pointer(consing_gen) =
                (byte*)unchecked((nuint)plug + len);
            generation.generation_allocation_limit(consing_gen) =
                generation.generation_allocation_pointer(consing_gen);
        }

        current_region = generation.generation_allocation_segment(consing_gen);
        if (special_sweep_p)
        {
            Debug.Assert(
                current_region is null ||
                heap_segment_next_rw(current_region) is null);
            return;
        }

        current_region = heap_segment_non_sip(current_region);
        if (current_region is not null)
        {
            decide_on_demotion_pin_surv(
                hp,
                current_region,
                &to_be_empty_regions,
                actual_promote_gen1_pins_p,
                large_pins_p);

            if (heap_segment.heap_segment_swept_in_plan(current_region) == 0)
            {
                heap_segment.heap_segment_plan_allocated(current_region) =
                    generation.generation_allocation_pointer(consing_gen);
            }

            heap_segment* region_no_pins =
                heap_segment.heap_segment_next(current_region);
            int region_no_pins_gen_num =
                heap_segment.heap_segment_gen_num(current_region);
            do
            {
                region_no_pins = heap_segment_non_sip(region_no_pins);
                if (region_no_pins is not null)
                {
                    set_region_plan_gen_num(region_no_pins, current_plan_gen_num);
                    to_be_empty_regions++;
                    heap_segment.heap_segment_plan_allocated(region_no_pins) =
                        heap_segment.heap_segment_mem(region_no_pins);
                    region_no_pins = heap_segment.heap_segment_next(region_no_pins);
                }

                if (region_no_pins is null)
                {
                    if (region_no_pins_gen_num > 0)
                    {
                        region_no_pins_gen_num--;
                        region_no_pins = generation.generation_start_segment(
                            generation_of(
                                generation_table_of(hp),
                                region_no_pins_gen_num));
                    }
                    else
                    {
                        break;
                    }
                }
            }
            while (region_no_pins is not null);
        }

        if (to_be_empty_regions != 0)
        {
            Debug.Assert(planned_regions_per_gen[0] != 0);
        }

        int saved_planned_gen0 = planned_regions_per_gen[0];
        int saved_planned_gen1 = planned_regions_per_gen[1];
        int saved_planned_gen2 = planned_regions_per_gen[2];
        Debug.Assert(saved_planned_gen0 >= to_be_empty_regions);
        saved_planned_gen0 -= to_be_empty_regions;

        int plan_regions_needed = 0;
        for (int gen_idx = settings.condemned_generation; gen_idx >= 0; gen_idx--)
        {
            int planned = gen_idx switch
            {
                0 => saved_planned_gen0,
                1 => saved_planned_gen1,
                _ => saved_planned_gen2,
            };
            if (planned == 0)
            {
                plan_regions_needed++;
            }
        }

        if (plan_regions_needed > to_be_empty_regions)
        {
            plan_regions_needed -= to_be_empty_regions;
            while (plan_regions_needed != 0 &&
                   get_new_region(generation_table_of(hp), hp, 0) is not null)
            {
                new_regions_in_prr++;
                plan_regions_needed--;
            }

            if (plan_regions_needed > 0)
            {
                special_sweep_p = true;
            }
        }
    }

    public static bool should_sweep_in_plan(gc_heap* hp, heap_segment* region)
    {
        Debug.Assert(hp is not null);

        if (!enable_special_regions_p ||
            settings.reason == gc_reason.reason_induced_aggressive)
        {
            return false;
        }

        bool sip_p = false;
        int gen_num = get_region_gen_num(region);
        int new_gen_num = get_plan_gen_num(gen_num);
        heap_segment.heap_segment_swept_in_plan(region) = 0;

        nuint basic_region_size = (nuint)1 << (int)min_segment_size_shr;
        Debug.Assert(
            heap_segment.heap_segment_gen_num(region) ==
            heap_segment.heap_segment_plan_gen_num(region));
        byte surv_ratio = unchecked((byte)(
            ((double)heap_segment.heap_segment_survived(region) * 100.0) /
            (double)basic_region_size));
        if (surv_ratio >= sip_surv_ratio_th)
        {
            set_region_plan_gen_num(region, new_gen_num);
            sip_p = true;
        }

        if (settings.promotion != 0 && new_gen_num < GCInterfaceOffsets.max_generation)
        {
            int old_card_surv_ratio = (int)(
                ((double)heap_segment.heap_segment_old_card_survived(region) * 100.0) /
                (double)basic_region_size);
            if (old_card_surv_ratio >= sip_old_card_surv_ratio_th)
            {
                set_region_plan_gen_num(
                    region,
                    GCInterfaceOffsets.max_generation,
                    replace_p: true);
                sip_maxgen_regions_per_gen[gen_num]++;
                sip_p = true;
            }
        }

        if (sip_p &&
            new_gen_num < GCInterfaceOffsets.max_generation &&
            sip_maxgen_regions_per_gen[gen_num] == regions_per_gen[gen_num])
        {
            Debug.Assert(get_region_gen_num(region) == 0);
            Debug.Assert(new_gen_num < GCInterfaceOffsets.max_generation);
            heap_segment* reserved_free_region = get_free_region(hp, gen_num);
            if (reserved_free_region is not null)
            {
                reserved_free_region_sip(gen_num) = reserved_free_region;
            }
            else
            {
                sip_maxgen_regions_per_gen[gen_num]--;
                set_region_plan_gen_num(region, new_gen_num, replace_p: true);
            }
        }

        return sip_p;
    }

    public static void sweep_region_in_plan(
        gc_heap* hp,
        heap_segment* region,
        int use_mark_list,
        ref byte** mark_list_next,
        byte** mark_list_index)
    {
        Debug.Assert(hp is not null);

        set_region_sweep_in_plan(region);
        region->init_free_list();

        byte* x = heap_segment.heap_segment_mem(region);
        byte* last_marked_obj_start = null;
        byte* last_marked_obj_end = null;
        byte* end = heap_segment.heap_segment_allocated(region);
#if DEBUG
        nuint survived = 0;
#endif
        while (x < end)
        {
            byte* obj = x;
            nuint obj_brick = (nuint)obj / card_table_info.brick_size;
            byte* next_obj;
            if (((CObjectHeader*)obj)->IsMarked() != 0)
            {
                if (((CObjectHeader*)obj)->IsPinned() != 0)
                {
                    ((CObjectHeader*)obj)->GetHeader()->ClrGCBit();
                }

                ((CObjectHeader*)obj)->ClearMarked();
                nuint object_size = size(obj);
                next_obj = obj + (nint)Align(object_size);
                last_marked_obj_start = obj;
                last_marked_obj_end = next_obj;
#if DEBUG
                survived += object_size;
#endif
            }
            else
            {
                next_obj = find_next_marked(
                    x,
                    end,
                    use_mark_list,
                    ref mark_list_next,
                    mark_list_index);
                if (next_obj > obj && next_obj != end)
                {
                    nuint free_obj_size = (nuint)(next_obj - obj);
                    make_unused_array(obj, free_obj_size);
                    region->thread_free_obj(obj, free_obj_size);
                }
            }

            nuint next_obj_brick = (nuint)next_obj / card_table_info.brick_size;
            if (next_obj_brick != obj_brick)
            {
                fix_brick_to_highest(obj, next_obj);
            }

            x = next_obj;
        }

        if (last_marked_obj_start is not null)
        {
            nuint last_marked_obj_start_b = brick_of(last_marked_obj_start);
            nuint last_marked_obj_end_b = brick_of(last_marked_obj_end - 1);
            if (last_marked_obj_start_b == last_marked_obj_end_b)
            {
                set_brick(
                    last_marked_obj_start_b,
                    unchecked((nint)(
                        last_marked_obj_start -
                        brick_address(last_marked_obj_start_b))));
            }
            else
            {
                set_brick(
                    last_marked_obj_end_b,
                    unchecked((nint)(last_marked_obj_start_b - last_marked_obj_end_b)));
            }
        }
        else
        {
            last_marked_obj_end = heap_segment.heap_segment_mem(region);
        }

#if DEBUG
        Debug.Assert(survived == heap_segment.heap_segment_survived(region));
#endif
        Debug.Assert(last_marked_obj_end is not null);
        save_allocated(region);
        heap_segment.heap_segment_allocated(region) = last_marked_obj_end;
        heap_segment.heap_segment_plan_allocated(region) = last_marked_obj_end;

        int plan_gen_num = heap_segment.heap_segment_plan_gen_num(region);
        if (plan_gen_num < heap_segment.heap_segment_gen_num(region))
        {
            generation.generation_allocation_size(
                generation_of(
                    generation_table_of(hp),
                    plan_gen_num)) += heap_segment.heap_segment_survived(region);
        }
    }

    private static bool validate_foreground_plan_prerequisites(
        gc_heap* hp,
        int condemned_gen_number)
    {
        if (hp is null ||
            (uint)condemned_gen_number > (uint)GCInterfaceOffsets.max_generation ||
            settings.condemned_generation != condemned_gen_number ||
            (settings.compaction != 0 && settings.compaction != 1) ||
            settings.concurrent != 0 ||
            (settings.promotion != 0 && settings.promotion != 1) ||
            finalize_queue is null ||
            mark_stack_array is null ||
            mark_stack_array_length == 0 ||
            mark_stack_bos != 0 ||
            mark_stack_tos != 0 ||
            card_table is null ||
            brick_table is null ||
            map_region_to_generation is null ||
            map_region_to_generation_skewed is null ||
            GCCommon.seg_mapping_table is null ||
            GCCommon.g_gc_lowest_address is null ||
            GCCommon.g_gc_highest_address <= GCCommon.g_gc_lowest_address ||
            bookkeeping_covered_committed is null ||
            lowest_address is null ||
            highest_address <= lowest_address ||
            min_segment_size_shr == 0 ||
            region_count == 0 ||
            global_region_allocator.get_region_alignment() !=
                ((nuint)1 << (int)min_segment_size_shr))
        {
            return false;
        }

#if BACKGROUND_GC
        if (background_final_plan())
        {
            if (settings.background_p != 0 ||
                !background_running_p() ||
                current_bgc_state != bgc_state.bgc_plan_phase)
            {
                return false;
            }
        }
        else if (settings.background_p != 0 ||
            background_running_p() ||
            current_bgc_state != bgc_state.bgc_not_in_process)
        {
            return false;
        }
#endif

        byte* first_marked = null;
        byte* last_marked = null;
        generation* generation_table = generation_table_of(hp);
        for (int gen_num = 0; gen_num <= GCInterfaceOffsets.max_generation; gen_num++)
        {
            generation* gen = generation_of(generation_table, gen_num);
            if (gen->gen_num != gen_num ||
                generation.generation_start_segment(gen) is null ||
                generation.generation_tail_ro_region(gen) is not null)
            {
                return false;
            }

            heap_segment* seg = generation.generation_start_segment(gen);
            heap_segment* tail = null;
            nuint segment_count = 0;
            while (seg is not null)
            {
                if (++segment_count > region_count + 1 ||
                    heap_segment.heap_segment_read_only_p(seg) != 0 ||
                    heap_segment.heap_segment_uoh_p(seg) != 0 ||
                    heap_segment.heap_segment_gen_num(seg) != gen_num ||
                    heap_segment.heap_segment_plan_gen_num(seg) != gen_num ||
                    heap_segment.heap_segment_swept_in_plan(seg) != 0 ||
                    heap_segment.heap_segment_mem(seg) is null ||
                    heap_segment.heap_segment_mem(seg) <
                        GCCommon.g_gc_lowest_address ||
                    heap_segment.heap_segment_mem(seg) >=
                        GCCommon.g_gc_highest_address ||
                    heap_segment.heap_segment_mem(seg) >
                        heap_segment.heap_segment_allocated(seg) ||
                    heap_segment.heap_segment_allocated(seg) >
                        heap_segment.heap_segment_committed(seg) ||
                    heap_segment.heap_segment_committed(seg) >
                        heap_segment.heap_segment_reserved(seg) ||
                    heap_segment.heap_segment_reserved(seg) >
                        bookkeeping_covered_committed ||
                    heap_segment.heap_segment_reserved(seg) >
                        GCCommon.g_gc_highest_address ||
                    get_region_info_for_address(
                        heap_segment.heap_segment_mem(seg)) != seg ||
                    get_region_gen_num(
                        heap_segment.heap_segment_mem(seg)) != gen_num ||
                    get_region_plan_gen_num(
                        heap_segment.heap_segment_mem(seg)) != gen_num ||
                    get_region_size(seg) != ((nuint)1 << (int)min_segment_size_shr))
                {
                    return false;
                }

                byte* object_address = heap_segment.heap_segment_mem(seg);
                byte* allocated = heap_segment.heap_segment_allocated(seg);
                while (object_address < allocated)
                {
                    CObjectHeader* header = (CObjectHeader*)object_address;
                    if (header->GetMethodTable() is null)
                    {
                        return false;
                    }

                    nuint object_size = size(object_address);
                    nuint aligned_size = Align(object_size);
                    if (object_size == 0 ||
                        aligned_size < Align((nuint)GCInterfaceOffsets.min_obj_size) ||
                        aligned_size > (nuint)(allocated - object_address))
                    {
                        return false;
                    }

                    if (gen_num <= condemned_gen_number &&
                        header->IsPinned() != 0 &&
                        header->IsMarked() == 0)
                    {
                        return false;
                    }

                    if (gen_num <= condemned_gen_number &&
                        header->IsMarked() != 0)
                    {
                        if (first_marked is null || object_address < first_marked)
                        {
                            first_marked = object_address;
                        }

                        if (last_marked is null || object_address > last_marked)
                        {
                            last_marked = object_address;
                        }
                    }

                    object_address += (nint)aligned_size;
                }

                if (object_address != allocated)
                {
                    return false;
                }

                tail = seg;
                seg = heap_segment.heap_segment_next(seg);
            }

            if (tail != generation.generation_tail_region(gen))
            {
                return false;
            }
        }

        if (condemned_gen_number == GCInterfaceOffsets.max_generation)
        {
            for (int gen_num = (int)gc_generation_num.loh_generation;
                 gen_num <= (int)gc_generation_num.poh_generation;
                 gen_num++)
            {
                generation* gen = generation_of(generation_table, gen_num);
                heap_segment* segment = generation.generation_start_segment(gen);
                if (gen->gen_num != gen_num ||
                    segment is null ||
                    generation.generation_allocation_segment(gen) is null ||
                    generation.generation_tail_region(gen) is null)
                {
                    return false;
                }

                nuint segmentCount = 0;
                heap_segment* tail = null;
                while (segment is not null)
                {
                    bool readOnly = heap_segment.heap_segment_read_only_p(segment) != 0;
                    if (++segmentCount > region_count + 1 ||
                        heap_segment.heap_segment_uoh_p(segment) == 0 ||
                        (heap_segment.heap_segment_gen_num(segment) !=
                            GCInterfaceOffsets.max_generation &&
                         heap_segment.heap_segment_gen_num(segment) != gen_num) ||
                        heap_segment.heap_segment_mem(segment) is null ||
                        (!readOnly &&
                         heap_segment.heap_segment_mem(segment) < lowest_address) ||
                        heap_segment.heap_segment_mem(segment) >
                            heap_segment.heap_segment_allocated(segment) ||
                        heap_segment.heap_segment_allocated(segment) >
                            heap_segment.heap_segment_committed(segment) ||
                        heap_segment.heap_segment_committed(segment) >
                            heap_segment.heap_segment_reserved(segment) ||
                        (!readOnly &&
                         heap_segment.heap_segment_reserved(segment) > highest_address))
                    {
                        return false;
                    }

                    byte* objectAddress = heap_segment.heap_segment_mem(segment);
                    byte* allocated = heap_segment.heap_segment_allocated(segment);
                    while (objectAddress < allocated)
                    {
                        CObjectHeader* header = (CObjectHeader*)objectAddress;
                        if (header->GetMethodTable() is null)
                        {
                            return false;
                        }

                        nuint objectSize = size(objectAddress);
                        nuint alignedSize = AlignQword(objectSize);
                        if (objectSize == 0 ||
                            alignedSize <
                                Align(
                                    (nuint)GCInterfaceOffsets.min_obj_size,
                                    get_alignment_constant(small_object_p: false)) ||
                            alignedSize > (nuint)(allocated - objectAddress) ||
                            (header->IsPinned() != 0 && header->IsMarked() == 0))
                        {
                            return false;
                        }

                        if (header->IsMarked() != 0)
                        {
                            if (first_marked is null || objectAddress < first_marked)
                            {
                                first_marked = objectAddress;
                            }

                            if (last_marked is null || objectAddress > last_marked)
                            {
                                last_marked = objectAddress;
                            }
                        }

                        objectAddress += (nint)alignedSize;
                    }

                    if (objectAddress != allocated)
                    {
                        return false;
                    }

                    tail = segment;
                    segment = heap_segment.heap_segment_next(segment);
                }

                if (tail != generation.generation_tail_region(gen))
                {
                    return false;
                }
            }
        }

        if (last_marked is null)
        {
            return shigh is null;
        }

        return slow == first_marked && shigh == last_marked;
    }

    public static bool plan_phase_synchronous_foreground(
        gc_heap* hp,
        int condemned_gen_number)
    {
        if (!validate_foreground_plan_prerequisites(hp, condemned_gen_number))
        {
            return false;
        }

        generation* generation_table = generation_table_of(hp);
        for (int gen_index = 0; gen_index <= condemned_gen_number; gen_index++)
        {
            generation* current_gen = generation_of(generation_table, gen_index);
            heap_segment* seg = generation.generation_start_segment(current_gen);
            if (shigh is not null)
            {
                do
                {
                    heap_segment.heap_segment_saved_allocated(seg) = null;
                    if (in_range_for_segment(slow, seg) != 0)
                    {
                        byte* start_unmarked = heap_segment.heap_segment_mem(seg);
                        nuint unmarked_size = (nuint)(slow - start_unmarked);
                        if (unmarked_size > 0)
                        {
                            Debug.Assert(
                                unmarked_size >=
                                Align((nuint)GCInterfaceOffsets.min_obj_size));
                            make_unused_array(start_unmarked, unmarked_size);
                        }
                    }

                    if (in_range_for_segment(shigh, seg) != 0)
                    {
                        save_allocated(seg);
                        heap_segment.heap_segment_allocated(seg) =
                            shigh + (nint)Align(size(shigh));
                    }

                    if (!(heap_segment.heap_segment_reserved(seg) >= slow &&
                          heap_segment.heap_segment_mem(seg) <= shigh))
                    {
                        save_allocated(seg);
                        heap_segment.heap_segment_allocated(seg) =
                            heap_segment.heap_segment_mem(seg);
                    }

                    seg = heap_segment_next_rw(seg);
                }
                while (seg is not null);
            }
            else
            {
                do
                {
                    heap_segment.heap_segment_saved_allocated(seg) = null;
                    byte* start_unmarked = heap_segment.heap_segment_mem(seg);
                    nuint unmarked_size =
                        (nuint)(heap_segment.heap_segment_allocated(seg) - start_unmarked);
                    if (unmarked_size > 0)
                    {
                        make_unused_array(start_unmarked, unmarked_size);
                    }

                    save_allocated(seg);
                    heap_segment.heap_segment_allocated(seg) = start_unmarked;
                    seg = heap_segment_next_rw(seg);
                }
                while (seg is not null);
            }
        }

        generation* condemned_gen1 =
            generation_of(generation_table, condemned_gen_number);
        heap_segment* seg1 =
            heap_segment_rw(generation.generation_start_segment(condemned_gen1));
        Debug.Assert(seg1 is not null);

        byte* end = heap_segment.heap_segment_allocated(seg1);
        byte* first_condemned_address = get_soh_start_object(seg1, condemned_gen1);
        byte* x = first_condemned_address;

        regions_per_gen = default;
        planned_regions_per_gen = default;
        sip_maxgen_regions_per_gen = default;
        reserved_free_regions_sip = default;
        int pinned_survived_region = 0;
        byte** local_mark_list_index = null;
        byte** mark_list_next = null;
        byte* plug_end = x;
        byte* tree = null;
        nuint sequence_number = 0;
        byte* last_node = null;
        nuint current_brick = brick_of(x);
        int allocate_in_condemned =
            condemned_gen_number == GCInterfaceOffsets.max_generation ||
            settings.promotion == 0
                ? 1
                : 0;
        int active_old_gen_number = condemned_gen_number;
        int active_new_gen_number = allocate_in_condemned != 0
            ? condemned_gen_number
            : condemned_gen_number + 1;
        generation* consing_gen = condemned_gen1;
        generation* older_gen = null;
        alloc_list* saved_free_list =
            stackalloc alloc_list[GCInterfaceOffsets.MAX_BUCKET_COUNT];
        nuint saved_free_list_space = 0;
        nuint saved_free_obj_space = 0;
        nuint saved_free_list_allocated = 0;
        nuint saved_condemned_allocated = 0;
        nuint saved_end_seg_allocated = 0;
        byte* saved_allocation_pointer = null;
        byte* saved_allocation_limit = null;
        byte* saved_allocation_start_region = null;
        heap_segment* saved_allocation_segment = null;

        if (condemned_gen_number < GCInterfaceOffsets.max_generation)
        {
            older_gen = generation_of(generation_table, condemned_gen_number + 1);
            allocator.copy_to_alloc_list(
                generation.generation_allocator(older_gen),
                saved_free_list);
            saved_free_list_space = generation.generation_free_list_space(older_gen);
            saved_free_obj_space = generation.generation_free_obj_space(older_gen);
            generation.generation_allocate_end_seg_p(older_gen) = 0;
            saved_free_list_allocated =
                generation.generation_free_list_allocated(older_gen);
            saved_condemned_allocated =
                generation.generation_condemned_allocated(older_gen);
            saved_end_seg_allocated =
                generation.generation_end_seg_allocated(older_gen);
            saved_allocation_pointer =
                generation.generation_allocation_pointer(older_gen);
            saved_allocation_limit =
                generation.generation_allocation_limit(older_gen);
            saved_allocation_start_region =
                generation.generation_allocation_context_start_region(older_gen);
            saved_allocation_segment =
                generation.generation_allocation_segment(older_gen);

            for (heap_segment* region =
                    generation.generation_start_segment_rw(older_gen);
                 region is not null;
                 region = heap_segment.heap_segment_next(region))
            {
                heap_segment.heap_segment_plan_allocated(region) =
                    heap_segment.heap_segment_allocated(region);
            }
        }

        for (int gen_index = 0; gen_index <= condemned_gen_number; gen_index++)
        {
            generation* current_gen = generation_of(generation_table, gen_index);
            heap_segment* seg = generation.generation_start_segment(current_gen);
            while (seg is not null)
            {
                regions_per_gen[gen_index]++;
                heap_segment.heap_segment_plan_allocated(seg) =
                    heap_segment.heap_segment_mem(seg);
                seg = heap_segment_next_rw(seg);
            }
        }

        for (int condemned_gn = condemned_gen_number;
             condemned_gn >= 0;
             condemned_gn--)
        {
            generation* condemned_gen2 =
                generation_of(generation_table, condemned_gn);
            allocator.clear(generation.generation_allocator(condemned_gen2));
            generation.generation_free_list_space(condemned_gen2) = 0;
            generation.generation_free_obj_space(condemned_gen2) = 0;
            generation.generation_allocation_size(condemned_gen2) = 0;
            generation.generation_condemned_allocated(condemned_gen2) = 0;
            generation.generation_sweep_allocated(condemned_gen2) = 0;
            generation.generation_free_list_allocated(condemned_gen2) = 0;
            generation.generation_end_seg_allocated(condemned_gen2) = 0;
            generation.generation_pinned_allocation_sweep_size(condemned_gen2) = 0;
            generation.generation_pinned_allocation_compact_size(condemned_gen2) = 0;

            generation.generation_allocation_segment(condemned_gen2) =
                heap_segment_rw(generation.generation_start_segment(condemned_gen2));
            Debug.Assert(
                generation.generation_allocation_segment(condemned_gen2) is not null);
            generation.generation_allocation_pointer(condemned_gen2) =
                heap_segment.heap_segment_mem(
                    generation.generation_allocation_segment(condemned_gen2));
            generation.generation_allocation_limit(condemned_gen2) =
                generation.generation_allocation_pointer(condemned_gen2);
            generation.generation_allocation_context_start_region(condemned_gen2) =
                generation.generation_allocation_pointer(condemned_gen2);
        }

        // The native path promotes Gen1 pins only for the low-card-efficiency-only
        // condemnation reason. That tuning reason is not produced by the translated
        // condemnation prefix.
        decide_promote_gen1_pins_p = false;

        if (should_sweep_in_plan(hp, seg1))
        {
            sweep_region_in_plan(
                hp,
                seg1,
                use_mark_list: 0,
                ref mark_list_next,
                local_mark_list_index);
            x = end;
        }

        nuint last_plug_len = 0;
        while (true)
        {
            if (x >= end)
            {
                Debug.Assert(x == end);
                if (heap_segment.heap_segment_swept_in_plan(seg1) != 0)
                {
                    Debug.Assert(
                        heap_segment.heap_segment_gen_num(seg1) ==
                        active_old_gen_number);
                    dynamic_data.dd_survived_size(
                        dynamic_data_of(hp, active_old_gen_number)) +=
                        heap_segment.heap_segment_survived(seg1);
                }
                else
                {
                    Debug.Assert(heap_segment.heap_segment_allocated(seg1) == end);
                    save_allocated(seg1);
                    heap_segment.heap_segment_allocated(seg1) = plug_end;
                    current_brick =
                        update_brick_table(tree, current_brick, x, plug_end);
                    sequence_number = 0;
                    tree = null;
                }

                heap_segment.heap_segment_pinned_survived(seg1) =
                    pinned_survived_region;
                pinned_survived_region = 0;
                if (heap_segment.heap_segment_mem(seg1) ==
                    heap_segment.heap_segment_allocated(seg1))
                {
                    num_regions_freed_in_sweep++;
                }

                if (heap_segment_next_rw(seg1) is not null)
                {
                    seg1 = heap_segment_next_rw(seg1);
                    end = heap_segment.heap_segment_allocated(seg1);
                    plug_end = x = heap_segment.heap_segment_mem(seg1);
                    current_brick = brick_of(x);
                    if (should_sweep_in_plan(hp, seg1))
                    {
                        sweep_region_in_plan(
                            hp,
                            seg1,
                            use_mark_list: 0,
                            ref mark_list_next,
                            local_mark_list_index);
                        x = end;
                    }

                    continue;
                }

                int saved_active_new_gen_number = active_new_gen_number;
                if (active_old_gen_number <=
                    (settings.promotion != 0
                        ? GCInterfaceOffsets.max_generation - 1
                        : GCInterfaceOffsets.max_generation))
                {
                    active_new_gen_number--;
                    allocate_in_condemned = 1;
                }

                if (active_new_gen_number >= 0)
                {
                    process_last_np_surv_region(
                        hp,
                        consing_gen,
                        saved_active_new_gen_number,
                        active_new_gen_number);
                }

                if (active_old_gen_number == 0)
                {
                    process_remaining_regions(
                        hp,
                        active_new_gen_number,
                        consing_gen);
                    break;
                }

                active_old_gen_number--;
                seg1 = heap_segment_rw(
                    generation.generation_start_segment(
                        generation_of(
                            generation_table,
                            active_old_gen_number)));
                end = heap_segment.heap_segment_allocated(seg1);
                plug_end = x = heap_segment.heap_segment_mem(seg1);
                current_brick = brick_of(x);
                if (should_sweep_in_plan(hp, seg1))
                {
                    sweep_region_in_plan(
                        hp,
                        seg1,
                        use_mark_list: 0,
                        ref mark_list_next,
                        local_mark_list_index);
                    x = end;
                }

                continue;
            }

            int last_npinned_plug_p = 0;
            int last_pinned_plug_p = 0;
            byte* last_pinned_plug = null;
            byte* last_object_in_plug = null;

            while (x < end && ((CObjectHeader*)x)->IsMarked() != 0)
            {
                byte* plug_start = x;
                byte* saved_plug_end = plug_end;
                int pinned_plug_p = 0;
                int npin_before_pin_p = 0;
                int saved_last_npinned_plug_p = last_npinned_plug_p;
                int merge_with_last_pin_p = 0;
                nuint added_pinning_size = 0;
                nuint artificial_pinned_size = 0;

                store_plug_gap_info(
                    hp,
                    plug_start,
                    plug_end,
                    ref last_npinned_plug_p,
                    ref last_pinned_plug_p,
                    ref last_pinned_plug,
                    ref pinned_plug_p,
                    last_object_in_plug,
                    ref merge_with_last_pin_p,
                    last_plug_len);

                byte* xl = x;
                while (xl < end &&
                       ((CObjectHeader*)xl)->IsMarked() != 0 &&
                       ((((CObjectHeader*)xl)->IsPinned() != 0 ? 1 : 0) ==
                           pinned_plug_p))
                {
                    if (((CObjectHeader*)xl)->IsPinned() != 0)
                    {
                        ((CObjectHeader*)xl)->GetHeader()->ClrGCBit();
                    }

                    ((CObjectHeader*)xl)->ClearMarked();
                    nuint object_size = size(xl);
                    Debug.Assert(object_size > 0);
                    Debug.Assert(object_size <= (nuint)GCConfig.GetLOHThreshold());
                    last_object_in_plug = xl;
                    xl += (nint)Align(object_size);
                }

                bool next_object_marked_p =
                    xl < end && ((CObjectHeader*)xl)->IsMarked() != 0;
                if (pinned_plug_p != 0)
                {
                    if (next_object_marked_p)
                    {
                        ((CObjectHeader*)xl)->ClearMarked();
                        last_object_in_plug = xl;
                        nuint extra_size = Align(size(xl));
                        xl += (nint)extra_size;
                        added_pinning_size = extra_size;
                    }
                }
                else if (next_object_marked_p)
                {
                    npin_before_pin_p = 1;
                }

                Debug.Assert(xl <= end);
                x = xl;
                plug_end = x;
                nuint ps = (nuint)(plug_end - plug_start);
                last_plug_len = ps;
                byte* new_address = null;

                if (pinned_plug_p == 0 &&
                    allocate_in_condemned != 0 &&
                    settings.condemned_generation ==
                        GCInterfaceOffsets.max_generation &&
                    ps > GCToOSInterface.GetPageSize())
                {
                    nint reloc = unchecked((nint)(
                        plug_start -
                        generation.generation_allocation_pointer(consing_gen)));
                    if (ps > 8 * GCToOSInterface.GetPageSize() &&
                        reloc > 0 &&
                        (nuint)reloc < ps / 16)
                    {
                        Debug.Assert(saved_last_npinned_plug_p == 0);
                        if (last_pinned_plug is not null)
                        {
                            merge_with_last_pin_p = 1;
                        }
                        else
                        {
                            enque_pinned_plug(hp, plug_start, 0, null);
                            last_pinned_plug = plug_start;
                        }

                        convert_to_pinned_plug(
                            ref last_npinned_plug_p,
                            ref last_pinned_plug_p,
                            ref pinned_plug_p,
                            ps,
                            ref artificial_pinned_size);
                    }
                }

                dynamic_data* dd_active_old =
                    dynamic_data_of(hp, active_old_gen_number);
                dynamic_data.dd_survived_size(dd_active_old) += ps;
                int convert_to_pinned_p = 0;
                if (pinned_plug_p == 0)
                {
                    Debug.Assert(allocate_in_condemned != 0);
                    new_address = allocate_in_condemned_generations(
                        hp,
                        consing_gen,
                        ps,
                        active_old_gen_number,
                        &convert_to_pinned_p,
                        npin_before_pin_p != 0 ? plug_end : null,
                        seg1,
                        plug_start);

                    if (convert_to_pinned_p != 0)
                    {
                        Debug.Assert(last_npinned_plug_p != 0);
                        Debug.Assert(last_pinned_plug_p == 0);
                        convert_to_pinned_plug(
                            ref last_npinned_plug_p,
                            ref last_pinned_plug_p,
                            ref pinned_plug_p,
                            ps,
                            ref artificial_pinned_size);
                        enque_pinned_plug(hp, plug_start, 0, null);
                        last_pinned_plug = plug_start;
                    }
                    else
                    {
                        Debug.Assert(new_address is not null);
                        if (is_plug_padded(plug_start) != 0)
                        {
                            dynamic_data.dd_padding_size(dd_active_old) +=
                                Align((nuint)GCInterfaceOffsets.min_obj_size);
                        }
                    }
                }

                if (pinned_plug_p != 0)
                {
                    if (merge_with_last_pin_p != 0)
                    {
                        merge_with_last_pinned_plug(hp, last_pinned_plug, ps);
                    }
                    else
                    {
                        Debug.Assert(last_pinned_plug == plug_start);
                        set_pinned_info(hp, plug_start, ps, consing_gen);
                    }

                    new_address = plug_start;
                    nuint pinned_plug_size = (nuint)(plug_end - plug_start);
                    pinned_survived_region = unchecked(
                        pinned_survived_region + (int)pinned_plug_size);
                    dynamic_data.dd_pinned_survived_size(dd_active_old) +=
                        pinned_plug_size;
                    dynamic_data.dd_added_pinned_size(dd_active_old) +=
                        added_pinning_size;
                    dynamic_data.dd_artificial_pinned_survived_size(dd_active_old) +=
                        artificial_pinned_size;
                }

                Debug.Assert(
                    !(new_address > plug_start &&
                      new_address < heap_segment.heap_segment_reserved(seg1)));

                if (merge_with_last_pin_p == 0)
                {
                    if (current_brick != brick_of(plug_start))
                    {
                        current_brick = update_brick_table(
                            tree,
                            current_brick,
                            plug_start,
                            saved_plug_end);
                        sequence_number = 0;
                        tree = null;
                    }

                    set_node_relocation_distance(
                        plug_start,
                        unchecked((nint)(new_address - plug_start)));
                    if (last_node is not null &&
                        node_relocation_distance(last_node) ==
                            node_relocation_distance(plug_start) +
                            (nint)node_gap_size(plug_start))
                    {
                        set_node_left(plug_start);
                    }

                    if (sequence_number == 0)
                    {
                        tree = plug_start;
                    }

                    tree = insert_node(
                        plug_start,
                        ++sequence_number,
                        tree,
                        last_node);
                    last_node = plug_start;
                }
            }

            x = find_next_marked(
                x,
                end,
                use_mark_list: 0,
                ref mark_list_next,
                local_mark_list_index);
        }

        nuint fragmentation = generation_fragmentation(
            hp,
            generation_of(generation_table, condemned_gen_number),
            consing_gen,
            heap_segment.heap_segment_allocated(hp->ephemeral_heap_segment));

        bool shouldExpand = false;
        bool shouldCompact = decide_on_compacting(
            hp,
            condemned_gen_number,
            fragmentation,
            ref shouldExpand);

#if BACKGROUND_GC
        if (background_final_plan())
        {
            shouldCompact = false;
        }
#endif

        if (condemned_gen_number == GCInterfaceOffsets.max_generation)
        {
            if (settings.loh_compaction != 0)
            {
                shouldCompact = true;
                gc_data_per_heap.set_mechanism(
                    gc_mechanism_per_heap.gc_heap_compact,
                    (uint)gc_heap_compact_reason.compact_loh_forced);
            }
            else
            {
                sweep_uoh_objects(hp, (int)gc_generation_num.loh_generation);
            }

            sweep_uoh_objects(hp, (int)gc_generation_num.poh_generation);
            if (shouldCompact)
            {
                full_gc_counts[gc_type_compacting]++;
                loh_alloc_since_cg = 0;
            }
        }

        if (special_sweep_p)
        {
            shouldCompact = false;
        }

        loh_compacted_p = 0;
        if (condemned_gen_number == GCInterfaceOffsets.max_generation &&
            settings.loh_compaction != 0)
        {
            if (shouldCompact && plan_loh(hp))
            {
                loh_compacted_p = 1;
            }
            else
            {
                sweep_uoh_objects(hp, (int)gc_generation_num.loh_generation);
            }
        }
        else if (condemned_gen_number == GCInterfaceOffsets.max_generation &&
            loh_pinned_queue is not null)
        {
            decay_loh_pinned_queue();
        }

        _ = shouldExpand;
        if (shouldCompact)
        {
            generation.generation_allocation_limit(condemned_gen1) =
                generation.generation_allocation_pointer(condemned_gen1);
            if (older_gen is not null)
            {
                allocator.commit_alloc_list_changes(
                    generation.generation_allocator(older_gen));
                fix_older_allocation_area(older_gen);
            }

            if (!relocate_phase(hp, condemned_gen_number, first_condemned_address) ||
                !compact_phase(
                    hp,
                    condemned_gen_number,
                    first_condemned_address,
                    settings.demotion == 0 && settings.promotion != 0 ? 1 : 0))
            {
                return false;
            }

            fix_generation_bounds(hp, condemned_gen_number, consing_gen);
            Debug.Assert(
                generation.generation_allocation_limit(
                    generation_of(generation_table, 0)) ==
                generation.generation_allocation_pointer(
                    generation_of(generation_table, 0)));

            end_gen0_region_committed_space =
                get_gen0_end_space(hp, memory_type.memory_type_committed);

            finalize_queue->UpdatePromotedGenerations(
                condemned_gen_number,
                settings.demotion == 0 && settings.promotion != 0 ? 1 : 0);

            ScanContext scanContext = default;
            scanContext.init();
            scanContext.thread_number = hp->heap_number;
            scanContext.thread_count = 1;
            scanContext.promotion = 0;
            scanContext.concurrent = 0;
            if (settings.promotion != 0 && settings.demotion == 0)
            {
                GCScan.GcPromotionsGranted(
                    condemned_gen_number,
                    GCInterfaceOffsets.max_generation,
                    &scanContext);
            }
            else if (settings.demotion != 0)
            {
                GCScan.GcDemote(
                    condemned_gen_number,
                    GCInterfaceOffsets.max_generation,
                    &scanContext);
            }

            thread_pinned_plug_gaps(hp);
            clear_gen1_cards(hp);
        }
        else
        {
            settings.promotion = 1;
            settings.compaction = 0;
            settings.demotion = 0;

            if (older_gen is not null)
            {
                allocator.copy_from_alloc_list(
                    generation.generation_allocator(older_gen),
                    saved_free_list);
                generation.generation_free_list_space(older_gen) =
                    saved_free_list_space;
                generation.generation_free_obj_space(older_gen) =
                    saved_free_obj_space;
                generation.generation_free_list_allocated(older_gen) =
                    saved_free_list_allocated;
                generation.generation_end_seg_allocated(older_gen) =
                    saved_end_seg_allocated;
                generation.generation_condemned_allocated(older_gen) =
                    saved_condemned_allocated;
                generation.generation_sweep_allocated(older_gen) = unchecked(
                    generation.generation_sweep_allocated(older_gen) +
                    dynamic_data.dd_survived_size(
                        dynamic_data_of(hp, condemned_gen_number)));
                generation.generation_allocation_limit(older_gen) =
                    saved_allocation_limit;
                generation.generation_allocation_pointer(older_gen) =
                    saved_allocation_pointer;
                generation.generation_allocation_context_start_region(older_gen) =
                    saved_allocation_start_region;
                generation.generation_allocation_segment(older_gen) =
                    saved_allocation_segment;
                fix_older_allocation_area(older_gen);
            }

            make_free_lists(hp, condemned_gen_number);
            nuint totalRecoveredSweepSize = recover_saved_pinned_info();
            if (totalRecoveredSweepSize > 0)
            {
                generation* maxGeneration =
                    generation_of(generation_table, GCInterfaceOffsets.max_generation);
                Debug.Assert(
                    generation.generation_free_obj_space(maxGeneration) >=
                    totalRecoveredSweepSize);
                generation.generation_free_obj_space(maxGeneration) -=
                    totalRecoveredSweepSize;
            }

            end_gen0_region_committed_space =
                get_gen0_end_space(hp, memory_type.memory_type_committed);

            if (!special_sweep_p)
            {
                ScanContext scanContext = default;
                scanContext.init();
                scanContext.thread_number = hp->heap_number;
                scanContext.thread_count = 1;
                scanContext.promotion = 0;
                scanContext.concurrent = 0;
                GCScan.GcPromotionsGranted(
                    condemned_gen_number,
                    GCInterfaceOffsets.max_generation,
                    &scanContext);

                finalize_queue->UpdatePromotedGenerations(
                    condemned_gen_number,
                    gen_0_empty_p: 1);
                clear_gen1_cards(hp);
            }
        }

        return true;
    }

    public static bool plan_phase_synchronous_full_gen2(
        gc_heap* hp,
        int condemned_gen_number) =>
        plan_phase_synchronous_foreground(hp, condemned_gen_number);

    public static void fix_generation_bounds(
        gc_heap* hp,
        int condemned_gen_number,
        generation* consing_gen)
    {
        _ = consing_gen;

        if (settings.promotion != 0 &&
            condemned_gen_number < GCInterfaceOffsets.max_generation)
        {
            generation* older_gen = generation_of(
                generation_table_of(hp),
                condemned_gen_number + 1);
            heap_segment* last_alloc_region =
                generation.generation_allocation_segment(older_gen);
            for (heap_segment* region =
                    generation.generation_start_segment_rw(older_gen);
                 region is not null;
                 region = heap_segment.heap_segment_next(region))
            {
                heap_segment.heap_segment_allocated(region) =
                    heap_segment.heap_segment_plan_allocated(region);
                if (region == last_alloc_region)
                {
                    break;
                }
            }
        }

        thread_final_regions(hp, compact_p: true);

        generation* youngestGeneration =
            generation_of(generation_table_of(hp), 0);
        hp->ephemeral_heap_segment =
            generation.generation_start_segment(youngestGeneration);
        hp->alloc_allocated =
            heap_segment.heap_segment_plan_allocated(hp->ephemeral_heap_segment);
        heap_segment.heap_segment_allocated(hp->ephemeral_heap_segment) =
            heap_segment.heap_segment_plan_allocated(hp->ephemeral_heap_segment);
    }

    public static void clear_gen1_cards(gc_heap* hp)
    {
        if (settings.demotion == 0 && settings.promotion != 0)
        {
            generation* gen1 = generation_of(
                generation_table_of(hp),
                (int)gc_generation_num.soh_gen1);
            heap_segment* region = generation.generation_start_segment(gen1);
            while (region is not null)
            {
                clear_card_for_addresses(
                    get_region_start(region),
                    heap_segment.heap_segment_reserved(region));
                region = heap_segment.heap_segment_next(region);
            }
        }
    }

    private static void thread_pinned_plug_gaps(gc_heap* hp)
    {
        reset_pinned_queue_bos(hp);
        while (pinned_plug_que_empty_p(hp) == 0)
        {
            nuint entryIndex = deque_pinned_plug(hp);
            mark* entry = pinned_plug_of(hp, entryIndex);
            nuint length = pinned_len(entry);
            byte* gap = pinned_plug(entry) - (nint)length;
            if (length != 0)
            {
                Debug.Assert(
                    length >= Align((nuint)GCInterfaceOffsets.min_obj_size));
                make_unused_array(gap, length);

                nuint startBrick = brick_of(gap);
                nuint endBrick = brick_of(gap + (nint)length);
                if (endBrick != startBrick)
                {
                    set_brick(
                        startBrick,
                        unchecked((nint)(gap - brick_address(startBrick))));
                    for (nuint brick = startBrick + 1; brick < endBrick; brick++)
                    {
                        set_brick(
                            brick,
                            unchecked((nint)startBrick - (nint)brick));
                    }
                }

                int genNumber = object_gennum_plan(gap);
                generation* gen =
                    generation_of(generation_table_of(hp), genNumber);
                thread_gap(gap, length, gen);
            }
        }
    }
#endif

#if USE_REGIONS && !MULTIPLE_HEAPS
    public static void initialize_compaction_policy(ulong total_physical_mem, uint g_num_processors)
    {
        Debug.Assert(g_num_processors != 0);

        mem_one_percent = total_physical_mem / 100;
        mem_one_percent /= g_num_processors;

        uint highmem_th_from_config = unchecked((uint)GCConfig.GetGCHighMemPercent());
        if (highmem_th_from_config != 0)
        {
            high_memory_load_th = highmem_th_from_config < 99 ? highmem_th_from_config : 99;
            uint very_high_memory_load = unchecked(high_memory_load_th + 7);
            v_high_memory_load_th = very_high_memory_load < 99 ? very_high_memory_load : 99;
        }
        else
        {
            int available_mem_th = 10;
            if (total_physical_mem >= 80UL * 1024 * 1024 * 1024)
            {
                int adjusted_available_mem_th = 3 + (int)(47f / g_num_processors);
                available_mem_th = available_mem_th < adjusted_available_mem_th
                    ? available_mem_th
                    : adjusted_available_mem_th;
            }

            high_memory_load_th = unchecked((uint)(100 - available_mem_th));
            v_high_memory_load_th = 97;
        }

        GCConfig.SetGCHighMemPercent(high_memory_load_th);
    }

    public static nuint min_reclaim_fragmentation_threshold(gc_heap* hp, uint num_heaps)
    {
        uint memory_load_delta = unchecked(settings.entry_memory_load - high_memory_load_th);
        nuint min_mem_based_on_available = unchecked(
            (nuint)((500u - (memory_load_delta * 40u)) * 1024u * 1024u)) / num_heaps;

        nuint ten_percent_size = (nuint)((float)generation_size(
            hp,
            (int)gc_generation_num.max_generation) * 0.10);
        ulong three_percent_mem = unchecked(mem_one_percent * 3) / num_heaps;
        ulong minimum = (ulong)min_mem_based_on_available < (ulong)ten_percent_size
            ? (ulong)min_mem_based_on_available
            : (ulong)ten_percent_size;
        minimum = minimum < three_percent_mem ? minimum : three_percent_mem;

        return unchecked((nuint)minimum);
    }

    public static ulong min_high_fragmentation_threshold(ulong available_mem, uint num_heaps)
    {
        const ulong MaximumThreshold = 256UL * 1024 * 1024;
        return (available_mem < MaximumThreshold ? available_mem : MaximumThreshold) / num_heaps;
    }

    public static bool ensure_gap_allocation(int condemned_gen_number)
    {
        _ = condemned_gen_number;
        return true;
    }

    public static nuint generation_fragmentation(
        gc_heap* hp,
        generation* gen,
        generation* consing_gen,
        byte* end)
    {
        _ = consing_gen;
        _ = end;

        nint frag = 0;
        generation* generation_table = generation_table_of(hp);
        for (int gen_num = 0; gen_num <= gen->gen_num; gen_num++)
        {
            generation* current_gen = generation_of(generation_table, gen_num);
            heap_segment* seg = heap_segment_rw(generation.generation_start_segment(current_gen));
            while (seg is not null)
            {
                frag = unchecked(
                    frag +
                    (nint)(heap_segment.heap_segment_saved_allocated(seg) -
                           heap_segment.heap_segment_plan_allocated(seg)));
                seg = heap_segment_next_rw(seg);
            }
        }

        nuint bos = 0;
        while (bos < mark_stack_bos)
        {
            frag = unchecked(frag + (nint)pinned_len(pinned_plug_of(hp, bos)));
            bos++;
        }

        return unchecked((nuint)frag);
    }

    public static bool decide_on_compaction_space(gc_heap* hp)
    {
        nuint gen0size = approximate_new_allocation(hp);
        nuint swept_region_space = unchecked(
            (nuint)num_regions_freed_in_sweep * ((nuint)1 << (int)min_segment_size_shr));

        if (sufficient_space_regions(swept_region_space, gen0size))
        {
            return false;
        }

        get_gen0_end_plan_space(hp);

        if (!gen0_large_chunk_found)
        {
            gen0_large_chunk_found =
                region_free_list.get_num_free_regions(
                    free_regions_of((int)free_region_kind.basic_free_region)) > 0;
        }

        if (sufficient_space_regions(
                unchecked(gen0_pinned_free_space + end_gen0_region_space),
                gen0size) &&
            gen0_large_chunk_found)
        {
            sufficient_gen0_space_p = 1;
        }

        return true;
    }

    public static bool is_full_compacting_gc_productive(gc_heap* hp)
    {
        generation* generation_table = generation_table_of(hp);
        heap_segment* gen1_start_region = generation.generation_start_segment(
            generation_of(generation_table, (int)gc_generation_num.soh_gen1));
        Debug.Assert(gen1_start_region is not null);
        if (heap_segment.heap_segment_plan_gen_num(gen1_start_region) ==
            (int)gc_generation_num.max_generation)
        {
            return false;
        }

        heap_segment* gen2_tail_region = generation.generation_tail_region(
            generation_of(generation_table, (int)gc_generation_num.max_generation));
        Debug.Assert(gen2_tail_region is not null);
        if (heap_segment.heap_segment_plan_allocated(gen2_tail_region) >=
            heap_segment.heap_segment_allocated(gen2_tail_region))
        {
            return false;
        }

        return true;
    }

    public static bool decide_on_compacting(
        gc_heap* hp,
        int condemned_gen_number,
        nuint fragmentation,
        ref bool should_expand)
    {
        Debug.Assert(settings.concurrent == 0);

        bool should_compact = false;
        should_expand = false;
        generation* gen = generation_of(generation_table_of(hp), condemned_gen_number);
        dynamic_data* dd = dynamic_data_of(hp, condemned_gen_number);
        nuint gen_sizes = generation_sizes(hp, gen, use_saved_p: true);
        float fragmentation_burden = fragmentation == 0 || gen_sizes == 0
            ? 0.0f
            : (float)fragmentation / (float)gen_sizes;

        if (special_sweep_p)
        {
            return false;
        }

        if (GCConfig.GetForceCompact() != 0)
        {
            should_compact = true;
        }

        if (condemned_gen_number == (int)gc_generation_num.max_generation &&
            last_gc_before_oom != 0)
        {
            should_compact = true;
            gc_data_per_heap.set_mechanism(
                gc_mechanism_per_heap.gc_heap_compact,
                (uint)gc_heap_compact_reason.compact_last_gc);
        }

        if (settings.reason == gc_reason.reason_induced_compacting)
        {
            should_compact = true;
            gc_data_per_heap.set_mechanism(
                gc_mechanism_per_heap.gc_heap_compact,
                (uint)gc_heap_compact_reason.compact_induced_compacting);
        }

        if (settings.reason == gc_reason.reason_induced_aggressive)
        {
            should_compact = true;
            gc_data_per_heap.set_mechanism(
                gc_mechanism_per_heap.gc_heap_compact,
                (uint)gc_heap_compact_reason.compact_aggressive_compacting);
        }

        if (settings.reason == gc_reason.reason_pm_full_gc)
        {
            Debug.Assert(condemned_gen_number == (int)gc_generation_num.max_generation);
            should_compact = true;
        }

        if (provisional_mode_triggered &&
            condemned_gen_number == (int)gc_generation_num.soh_gen1)
        {
            should_compact = true;
        }

        if (!should_compact)
        {
            should_compact = decide_on_compaction_space(hp);
        }

#if TARGET_64BIT
        bool high_memory = false;
#endif

        if (!should_compact)
        {
            bool frag_exceeded =
                fragmentation >= dynamic_data.dd_fragmentation_limit(dd) &&
                fragmentation_burden >= dynamic_data.dd_fragmentation_burden_limit(dd);

            if (frag_exceeded)
            {
#if BACKGROUND_GC
                Debug.Assert(settings.concurrent == 0);
#endif
                should_compact = true;
                gc_data_per_heap.set_mechanism(
                    gc_mechanism_per_heap.gc_heap_compact,
                    (uint)gc_heap_compact_reason.compact_high_frag);
            }

#if TARGET_64BIT
            if (!should_compact)
            {
                const uint NumHeaps = 1;
                nint reclaim_space = unchecked((nint)(
                    generation_size(hp, (int)gc_generation_num.max_generation) -
                    generation_plan_size(hp, (int)gc_generation_num.max_generation)));

                if (settings.entry_memory_load >= high_memory_load_th &&
                    settings.entry_memory_load < v_high_memory_load_th)
                {
                    if (reclaim_space > unchecked((nint)min_high_fragmentation_threshold(
                            settings.entry_available_physical_mem,
                            NumHeaps)))
                    {
                        should_compact = true;
                        gc_data_per_heap.set_mechanism(
                            gc_mechanism_per_heap.gc_heap_compact,
                            (uint)gc_heap_compact_reason.compact_high_mem_frag);
                    }

                    high_memory = true;
                }
                else if (settings.entry_memory_load >= v_high_memory_load_th)
                {
                    if (reclaim_space > unchecked((nint)min_reclaim_fragmentation_threshold(
                            hp,
                            NumHeaps)))
                    {
                        should_compact = true;
                        gc_data_per_heap.set_mechanism(
                            gc_mechanism_per_heap.gc_heap_compact,
                            (uint)gc_heap_compact_reason.compact_vhigh_mem_frag);
                    }

                    high_memory = true;
                }
            }
#endif
        }

        if (!should_compact && !ensure_gap_allocation(condemned_gen_number))
        {
            should_compact = true;
            gc_data_per_heap.set_mechanism(
                gc_mechanism_per_heap.gc_heap_compact,
                (uint)gc_heap_compact_reason.compact_no_gaps);
        }

        if (settings.condemned_generation == (int)gc_generation_num.max_generation)
        {
            if (
#if TARGET_64BIT
                (high_memory && !should_compact) ||
#endif
                !is_full_compacting_gc_productive(hp))
            {
                settings.should_lock_elevation = 1;
            }
        }

        if (settings.pause_mode == gc_pause_mode.pause_no_gc)
        {
            should_compact = true;
            heap_segment* ephemeral_segment = hp->ephemeral_heap_segment;
            Debug.Assert(ephemeral_segment is not null);
            if ((nuint)(
                    heap_segment.heap_segment_reserved(ephemeral_segment) -
                    heap_segment.heap_segment_plan_allocated(ephemeral_segment)) <
                soh_allocation_no_gc)
            {
                should_expand = true;
            }
        }

        return should_compact;
    }

    public static nuint approximate_new_allocation(gc_heap* hp)
    {
        dynamic_data* dd0 = dynamic_data_of(hp, (int)gc_generation_num.soh_gen0);
        nuint twice_minimum = unchecked(2 * dynamic_data.dd_min_size(dd0));
        nuint desired_fraction = unchecked(dynamic_data.dd_desired_allocation(dd0) * 2) / 3;
        return twice_minimum > desired_fraction ? twice_minimum : desired_fraction;
    }

    public static bool check_against_hard_limit(nuint space_required)
    {
        bool can_fit = true;

        if (heap_hard_limit != 0)
        {
            nuint left_in_commit = unchecked(heap_hard_limit - current_total_committed);
            const int NumHeaps = 1;
            left_in_commit /= NumHeaps;
            if (left_in_commit < space_required)
            {
                can_fit = false;
            }
        }

        return can_fit;
    }

    public static bool sufficient_space_regions_for_allocation(
        nuint end_space,
        nuint end_space_required)
    {
        nuint free_regions_space = unchecked(
            region_free_list.get_num_free_regions(
                free_regions_of((int)free_region_kind.basic_free_region)) *
            ((nuint)1 << (int)min_segment_size_shr));
        free_regions_space = unchecked(free_regions_space + global_region_allocator.get_free());
        nuint total_alloc_space = unchecked(end_space + free_regions_space);
        nuint total_commit_space = unchecked(
            end_gen0_region_committed_space +
            free_regions[(int)free_region_kind.basic_free_region].get_size_committed_in_free());

        if (total_alloc_space > end_space_required)
        {
            if (end_space_required > total_commit_space)
            {
                return check_against_hard_limit(end_space_required - total_commit_space);
            }

            return true;
        }

        return false;
    }

    public static bool sufficient_space_regions(nuint end_space, nuint end_space_required)
    {
        nuint free_regions_space = unchecked(
            region_free_list.get_num_free_regions(
                free_regions_of((int)free_region_kind.basic_free_region)) *
            ((nuint)1 << (int)min_segment_size_shr));
        free_regions_space = unchecked(free_regions_space + global_region_allocator.get_free());
        nuint total_alloc_space = unchecked(end_space + free_regions_space);

        if (total_alloc_space > end_space_required)
        {
            return check_against_hard_limit(end_space_required);
        }

        return false;
    }

    public static nuint end_space_after_gc(gc_heap* hp)
    {
        nuint half_minimum = dynamic_data.dd_min_size(
            dynamic_data_of(hp, (int)gc_generation_num.soh_gen0)) / 2;
        return half_minimum > END_SPACE_AFTER_GC_FL ? half_minimum : END_SPACE_AFTER_GC_FL;
    }

    public static bool ephemeral_gen_fit_p(gc_heap* hp, gc_tuning_point tp)
    {
        Debug.Assert(
            tp == gc_tuning_point.tuning_deciding_condemned_gen ||
            tp == gc_tuning_point.tuning_deciding_full_gc);

        dynamic_data* dd = dynamic_data_of(hp, (int)gc_generation_num.soh_gen0);
        nuint twice_minimum = unchecked(2 * dynamic_data.dd_min_size(dd));
        nuint minimum_end_space = end_space_after_gc(hp);
        nuint end_space = twice_minimum > minimum_end_space ? twice_minimum : minimum_end_space;
        nuint gen0_end_space = get_gen0_end_space(hp, memory_type.memory_type_reserved);

        return sufficient_space_regions(gen0_end_space, end_space);
    }
#endif
}
