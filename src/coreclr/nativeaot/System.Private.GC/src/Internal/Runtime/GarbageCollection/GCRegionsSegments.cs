// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the dependency-closed WKS USE_REGIONS helpers from regions_segments.cpp.

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
#if USE_REGIONS
    public static uint* card_table;
    public static short* brick_table;
    public static region_free_list_array free_regions;
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
