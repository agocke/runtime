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
