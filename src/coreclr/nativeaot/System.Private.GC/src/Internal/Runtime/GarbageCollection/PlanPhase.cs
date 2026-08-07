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
#endif
}
