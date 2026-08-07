// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the allocation-free relocation copy primitive and dependency-closed WKS USE_REGIONS
// helpers, including brick-tree reference relocation, from relocate_compact.cpp.

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    public static void memcopy(byte* dmem, byte* smem, nuint size)
    {
        nuint sz4ptr = (nuint)sizeof(void*) * 4;
        nuint sz2ptr = (nuint)sizeof(void*) * 2;
        nuint sz1ptr = (nuint)sizeof(void*) * 1;

        Debug.Assert((size & ((nuint)sizeof(void*) - 1)) == 0);
        Debug.Assert(sizeof(void*) == GCEnv.DATA_ALIGNMENT);

        // copy in groups of four pointer sized things at a time
        if (size >= sz4ptr)
        {
            do
            {
                ((nuint*)dmem)[0] = ((nuint*)smem)[0];
                ((nuint*)dmem)[1] = ((nuint*)smem)[1];
                ((nuint*)dmem)[2] = ((nuint*)smem)[2];
                ((nuint*)dmem)[3] = ((nuint*)smem)[3];
                dmem += (int)sz4ptr;
                smem += (int)sz4ptr;
            }
            while ((size -= sz4ptr) >= sz4ptr);
        }

        // still two pointer sized things or more left to copy?
        if ((size & sz2ptr) != 0)
        {
            ((nuint*)dmem)[0] = ((nuint*)smem)[0];
            ((nuint*)dmem)[1] = ((nuint*)smem)[1];
            dmem += (int)sz2ptr;
            smem += (int)sz2ptr;
        }

        // still one pointer sized thing left to copy?
        if ((size & sz1ptr) != 0)
        {
            ((nuint*)dmem)[0] = ((nuint*)smem)[0];
        }
    }

    public static mark* get_oldest_pinned_entry(
        gc_heap* heap,
        int* has_pre_plug_info_p,
        int* has_post_plug_info_p)
    {
        Debug.Assert(pinned_plug_que_empty_p(heap) == 0);

        mark* oldest_entry = oldest_pin(heap);
        *has_pre_plug_info_p = mark.has_pre_plug_info(oldest_entry);
        *has_post_plug_info_p = mark.has_post_plug_info(oldest_entry);

        deque_pinned_plug(heap);
        update_oldest_pinned_plug(heap);
        return oldest_entry;
    }

    public static mark* get_next_pinned_entry(
        gc_heap* heap,
        byte* tree,
        int* has_pre_plug_info_p,
        int* has_post_plug_info_p,
        int deque_p)
    {
        if (pinned_plug_que_empty_p(heap) == 0)
        {
            mark* oldest_entry = oldest_pin(heap);
            byte* oldest_plug = pinned_plug(oldest_entry);
            if (tree == oldest_plug)
            {
                *has_pre_plug_info_p = mark.has_pre_plug_info(oldest_entry);
                *has_post_plug_info_p = mark.has_post_plug_info(oldest_entry);

                if (deque_p != 0)
                {
                    deque_pinned_plug(heap);
                }

                return oldest_entry;
            }
        }

        return null;
    }

    public static nuint recover_saved_pinned_info()
    {
        reset_pinned_queue_bos(null);
        nuint total_recovered_sweep_size = 0;

        while (pinned_plug_que_empty_p(null) == 0)
        {
            mark* oldest_entry = oldest_pin(null);
            nuint recovered_sweep_size = mark.recover_plug_info(oldest_entry);

            if (recovered_sweep_size > 0)
            {
                byte* plug = pinned_plug(oldest_entry);
                if (object_gennum(plug) == GCInterfaceOffsets.max_generation)
                {
                    total_recovered_sweep_size += recovered_sweep_size;
                }
            }

            deque_pinned_plug(null);
        }

        return total_recovered_sweep_size;
    }

#if USE_REGIONS
    public static void copy_cards_range(byte* dest, byte* src, nuint len, bool copy_cards_p)
    {
        if (copy_cards_p)
        {
            copy_cards_for_addresses(dest, src, len);
        }
        else
        {
            clear_card_for_addresses(dest, dest + len);
        }
    }

    public static bool should_check_brick_for_reloc(byte* o)
    {
        Debug.Assert((o >= GCCommon.g_gc_lowest_address) && (o < GCCommon.g_gc_highest_address));

        nuint skewed_basic_region_index = get_skewed_basic_region_index_for_address(o);

        // return true if the region is not SIP and the generation is <= condemned generation
        return ((byte)map_region_to_generation_skewed[(nint)skewed_basic_region_index] &
            ((byte)region_info.RI_SIP | (byte)region_info.RI_GEN_MASK)) <= settings.condemned_generation;
    }

    public static void check_demotion_helper_sip(byte** pval, int parent_gen_num, byte* parent_loc)
    {
        byte* child_object = *pval;
        if (!is_in_heap_range(child_object))
        {
            return;
        }

        Debug.Assert(child_object is not null);
        int child_object_plan_gen = get_region_plan_gen_num(child_object);

        if (child_object_plan_gen < parent_gen_num)
        {
            set_card(card_of(parent_loc));
        }
    }

    public static void check_class_object_demotion(gc_heap* hp, byte* obj)
    {
#if COLLECTIBLE_CLASS
        if (is_collectible(obj) != 0)
        {
            check_class_object_demotion_internal(hp, obj);
        }
#else
        _ = hp;
        _ = obj;
#endif // COLLECTIBLE_CLASS
    }

    public static void check_demotion_helper(byte** pval, byte* parent_obj)
    {
        byte* child_object = *pval;
        if (!is_in_heap_range(child_object))
        {
            return;
        }

        int child_object_plan_gen = get_region_plan_gen_num(child_object);
        bool child_obj_demoted_p = is_region_demoted(child_object);

        if (child_obj_demoted_p)
        {
            set_card(card_of(parent_obj));
        }
    }

    public static void relocate_address(byte** pold_address)
    {
        byte* old_address = *pold_address;
        if (!is_in_gc_range(old_address) || !should_check_brick_for_reloc(old_address))
        {
            return;
        }

        // delta translates old_address into address_gc (old_address);
        nuint brick = brick_of(old_address);
        int brick_entry = brick_table[(nint)brick];
        byte* new_address = old_address;
        if (brick_entry != 0)
        {
        retry:
            {
                while (brick_entry < 0)
                {
                    brick = unchecked(brick + (nuint)brick_entry);
                    brick_entry = brick_table[(nint)brick];
                }

                byte* old_loc = old_address;

                byte* node = tree_search(
                    brick_address(brick) + brick_entry - 1,
                    old_loc);
                if (node <= old_loc)
                {
                    new_address = old_address + node_relocation_distance(node);
                }
                else
                {
                    if (node_left_p(node) != 0)
                    {
                        new_address = old_address +
                            (node_relocation_distance(node) + (nint)node_gap_size(node));
                    }
                    else
                    {
                        brick--;
                        brick_entry = brick_table[(nint)brick];
                        goto retry;
                    }
                }
            }

            *pold_address = new_address;
            return;
        }

        // FEATURE_LOH_COMPACTION is enabled unconditionally by gcpriv.h for this collector.
        if (settings.loh_compaction != 0)
        {
            _ = try_get_region_segment(old_address, small_heap_only: false, out heap_segment* pSegment);

            // pSegment could be 0 for regions, see comment for is_in_condemned.
            if (pSegment is null)
            {
                return;
            }

            if (loh_compacted_p != 0)
            {
                nuint flags = pSegment->flags;
                if ((flags & heap_segment.heap_segment_flags_loh) != 0 &&
                    (flags & heap_segment.heap_segment_flags_readonly) == 0)
                {
                    new_address = old_address + loh_node_relocation_distance(old_address);
                    *pold_address = new_address;
                }
            }
        }
    }

    public static void reloc_survivor_helper(gc_heap* hp, byte** pval)
    {
        _ = hp;
        relocate_address(pval);

        check_demotion_helper(pval, (byte*)pval);
    }

    private static void reloc_survivor_helper_callback(byte** pval, void* context)
    {
        reloc_survivor_helper((gc_heap*)context, pval);
    }

    public static void relocate_in_uoh_objects(gc_heap* hp, int gen_num)
    {
        generation* gen = generation_of(generation_table_of(hp), gen_num);

        heap_segment* seg = heap_segment_rw(generation.generation_start_segment(gen));

        Debug.Assert(seg is not null);

        byte* o = get_uoh_start_object(seg, gen);

        while (true)
        {
            if (o >= heap_segment.heap_segment_allocated(seg))
            {
                seg = heap_segment_next_rw(seg);
                if (seg is null)
                {
                    break;
                }
                else
                {
                    o = heap_segment.heap_segment_mem(seg);
                }
            }

            while (o < heap_segment.heap_segment_allocated(seg))
            {
                check_class_object_demotion(hp, o);
                if (contain_pointers(o) != 0)
                {
                    go_through_object_nostart(
                        method_table(o),
                        o,
                        size(o),
                        hp,
                        &reloc_survivor_helper_callback);
                }

                o = (byte*)unchecked((nuint)o + AlignQword(size(o)));
            }
        }
    }
#endif
}
#pragma warning restore CS8981
