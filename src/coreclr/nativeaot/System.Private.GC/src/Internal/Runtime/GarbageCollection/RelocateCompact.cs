// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the allocation-free relocation and compaction copy primitives and dependency-closed
// WKS USE_REGIONS helpers, including brick-tree reference relocation, plug-level SOH relocation
// and compaction, and the bounded synchronous full-GC relocation and compaction orchestration,
// from relocate_compact.cpp.

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
    public static heap_segment* relocate_advance_to_non_sip(gc_heap* hp, heap_segment* region)
    {
        heap_segment* current_region = region;

        while (current_region is not null)
        {
            if (heap_segment.heap_segment_swept_in_plan(current_region) != 0)
            {
                int gen_num = heap_segment.heap_segment_gen_num(current_region);
                int plan_gen_num = heap_segment.heap_segment_plan_gen_num(current_region);
                int use_sip_demotion = plan_gen_num > get_plan_gen_num(gen_num) ? 1 : 0;
                byte* x = heap_segment.heap_segment_mem(current_region);
                byte* end = heap_segment.heap_segment_allocated(current_region);
                relocate_advance_to_non_sip_context context = new()
                {
                    plan_gen_num = plan_gen_num,
                    use_sip_demotion = use_sip_demotion,
                };

                // For SIP regions, we go linearly in the region and relocate each object's references.
                while (x < end)
                {
                    nuint s = size(x);
                    Debug.Assert(s > 0);
                    byte* next_obj = x + (nint)Align(s);
                    if (((CObjectHeader*)x)->IsFree() == 0)
                    {
                        if (contain_pointers(x) != 0)
                        {
                            go_through_object_nostart(
                                method_table(x),
                                x,
                                s,
                                &context,
                                &relocate_advance_to_non_sip_callback);
                        }

                        check_class_object_demotion(hp, x);
                    }

                    x = next_obj;
                }
            }
            else
            {
                return current_region;
            }

            current_region = heap_segment.heap_segment_next(current_region);
        }

        return null;
    }

    private struct relocate_advance_to_non_sip_context
    {
        public int plan_gen_num;
        public int use_sip_demotion;
    }

    private static void relocate_advance_to_non_sip_callback(byte** pval, void* contextPointer)
    {
        relocate_advance_to_non_sip_context* context =
            (relocate_advance_to_non_sip_context*)contextPointer;

        relocate_address(pval);
        if (context->use_sip_demotion != 0)
        {
            check_demotion_helper_sip(pval, context->plan_gen_num, (byte*)pval);
        }
        else
        {
            check_demotion_helper(pval, (byte*)pval);
        }
    }

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

    public static void gcmemcopy(byte* dest, byte* src, nuint len, int copy_cards_p)
    {
        if (dest != src)
        {
#if BACKGROUND_GC
            if (current_c_gc_state == c_gc_state.c_gc_state_marking)
            {
                copy_mark_bits_for_addresses(dest, src, len);
            }
#endif
#if TARGET_64BIT && !TARGET_WASM
            int set_bgc_mark_bits_p = is_plug_bgc_mark_bit_set(src);
            if (set_bgc_mark_bits_p != 0)
            {
                clear_plug_bgc_mark_bit(src);
            }

            int make_free_obj_p = 0;
            if (len <= min_free_item_no_prev)
            {
                make_free_obj_p = is_free_obj_in_compact_bit_set(src);

                if (make_free_obj_p != 0)
                {
                    clear_free_obj_in_compact_bit(src);
                }
            }
#endif

            memcopy(
                dest - (nint)plug_skew,
                src - (nint)plug_skew,
                len);

#if TARGET_64BIT && !TARGET_WASM
            if (set_bgc_mark_bits_p != 0)
            {
                byte* dest_o = dest;
                byte* dest_end_o = dest + (nint)len;
                while (dest_o < dest_end_o)
                {
                    byte* next_o = dest_o + (nint)Align(size(dest_o));
                    background_mark(
                        dest_o,
                        background_saved_lowest_address,
                        background_saved_highest_address);

                    dest_o = next_o;
                }
            }

            if (make_free_obj_p != 0)
            {
                nuint* filler_free_obj_size_location =
                    (nuint*)(dest + (nint)min_free_item_no_prev);
                nuint filler_free_obj_size = *filler_free_obj_size_location;
                make_unused_array(dest + (nint)len, filler_free_obj_size);
            }
#endif

#if FEATURE_USE_SOFTWARE_WRITE_WATCH_FOR_GC_HEAP
            if (SoftwareWriteWatch.IsEnabledForGCHeap())
            {
                SoftwareWriteWatch.SetDirtyRegion(dest, len - plug_skew);
            }
#endif
            copy_cards_range(dest, src, len, copy_cards_p != 0);
        }
    }

#if BACKGROUND_GC
    public static void copy_mark_bits_for_addresses(
        byte* dest,
        byte* src,
        nuint len)
    {
        byte* src_o = src;
        byte* src_end = src + (nint)len;
        nint reloc = unchecked((nint)(dest - src));
        int align_const = get_alignment_constant(small_object_p: true);

        while (src_o < src_end)
        {
            byte* next_o =
                src_o + (nint)Align(size(src_o), align_const);
            if (background_object_marked(src_o, clear_p: true))
            {
                background_mark(
                    src_o + reloc,
                    background_saved_lowest_address,
                    background_saved_highest_address);
            }

            src_o = next_o;
        }
    }
#endif

    public static void compact_plug(
        byte* plug,
        nuint size,
        int check_last_object_p,
        compact_args* args)
    {
        byte* reloc_plug = plug + args->last_plug_relocation;

        if (check_last_object_p != 0)
        {
            size += (nuint)sizeof(gap_reloc_pair);
            mark* entry = args->pinned_plug_entry;

            if (args->is_shortened != 0)
            {
                Debug.Assert(mark.has_post_plug_info(entry) != 0);
                mark.swap_post_plug_and_saved(entry);
            }
            else
            {
                Debug.Assert(mark.has_pre_plug_info(entry) != 0);
                mark.swap_pre_plug_and_saved(entry);
            }
        }

        int old_brick_entry = brick_table[(nint)brick_of(plug)];
        _ = old_brick_entry;

        Debug.Assert(node_relocation_distance(plug) == args->last_plug_relocation);

        nuint unused_arr_size = 0;
        int already_padded_p = 0;
        if (is_plug_padded(plug) != 0)
        {
            already_padded_p = 1;
            clear_plug_padded(plug);
            unused_arr_size = Align((nuint)GCInterfaceOffsets.min_obj_size);
        }

        if (node_realigned(plug) != 0)
        {
            unused_arr_size += switch_alignment_size(already_padded_p);
        }

        if (unused_arr_size != 0)
        {
            make_unused_array(reloc_plug - (nint)unused_arr_size, unused_arr_size);

            if (brick_of(reloc_plug - (nint)unused_arr_size) != brick_of(reloc_plug))
            {
                fix_brick_to_highest(reloc_plug - (nint)unused_arr_size, reloc_plug);
            }
        }

        if (is_plug_padded(plug) != 0)
        {
            nuint aligned_min_obj_size = Align((nuint)GCInterfaceOffsets.min_obj_size);
            make_unused_array(reloc_plug - (nint)aligned_min_obj_size, aligned_min_obj_size);

            if (brick_of(reloc_plug - (nint)aligned_min_obj_size) != brick_of(reloc_plug))
            {
                fix_brick_to_highest(reloc_plug - (nint)aligned_min_obj_size, reloc_plug);
            }
        }

        gcmemcopy(reloc_plug, plug, size, args->copy_cards_p);

        if (args->check_gennum_p != 0)
        {
            int src_gennum = args->src_gennum;
            if (src_gennum == -1)
            {
                src_gennum = object_gennum(plug);
            }

            int dest_gennum = object_gennum_plan(reloc_plug);

            if (src_gennum < dest_gennum)
            {
                generation.generation_allocation_size(
                    generation_of(generation_table_of(ManagedGCRegionBootstrap.Heap), dest_gennum)) += size;
            }
        }

        nuint current_reloc_brick = args->current_compacted_brick;

        if (brick_of(reloc_plug) != current_reloc_brick)
        {
            if (args->before_last_plug is not null)
            {
                set_brick(
                    current_reloc_brick,
                    (nint)(args->before_last_plug -
                        brick_address(current_reloc_brick)));
            }

            current_reloc_brick = brick_of(reloc_plug);
        }

        nuint end_brick = brick_of(reloc_plug + (nint)size - 1);
        if (end_brick != current_reloc_brick)
        {
            set_brick(
                current_reloc_brick,
                (nint)(reloc_plug - brick_address(current_reloc_brick)));

            nuint brick = current_reloc_brick + 1;
            while (brick < end_brick)
            {
                set_brick(brick, -1);
                brick++;
            }

            args->before_last_plug = brick_address(end_brick) - 1;
            current_reloc_brick = end_brick;
        }
        else
        {
            args->before_last_plug = reloc_plug;
        }

        args->current_compacted_brick = current_reloc_brick;

        if (check_last_object_p != 0)
        {
            mark* entry = args->pinned_plug_entry;

            if (args->is_shortened != 0)
            {
                mark.swap_post_plug_and_saved(entry);
            }
            else
            {
                mark.swap_pre_plug_and_saved(entry);
            }
        }
    }

    public static void compact_in_brick(byte* tree, compact_args* args)
    {
        Debug.Assert(tree is not null);
        int left_node = node_left_child(tree);
        int right_node = node_right_child(tree);
        nint relocation = node_relocation_distance(tree);

        if (left_node != 0)
        {
            compact_in_brick(tree + left_node, args);
        }

        byte* plug = tree;
        int has_pre_plug_info_p = 0;
        int has_post_plug_info_p = 0;

        if (tree == oldest_pinned_plug)
        {
            args->pinned_plug_entry = get_oldest_pinned_entry(
                ManagedGCRegionBootstrap.Heap,
                &has_pre_plug_info_p,
                &has_post_plug_info_p);
            Debug.Assert(tree == pinned_plug(args->pinned_plug_entry));
        }

        if (args->last_plug is not null)
        {
            nuint gap_size = node_gap_size(tree);
            byte* gap = plug - (nint)gap_size;
            byte* last_plug_end = gap;
            nuint last_plug_size = (nuint)(last_plug_end - args->last_plug);
            Debug.Assert((last_plug_size & ((nuint)sizeof(byte*) - 1)) == 0);

            int check_last_object_p =
                args->is_shortened != 0 || has_pre_plug_info_p != 0 ? 1 : 0;
            if (check_last_object_p == 0)
            {
                Debug.Assert(last_plug_size >= Align((nuint)GCInterfaceOffsets.min_obj_size));
            }

            compact_plug(
                args->last_plug,
                last_plug_size,
                check_last_object_p,
                args);
        }
        else
        {
            Debug.Assert(has_pre_plug_info_p == 0);
        }

        args->last_plug = plug;
        args->last_plug_relocation = relocation;
        args->is_shortened = has_post_plug_info_p;

        if (right_node != 0)
        {
            compact_in_brick(tree + right_node, args);
        }
    }

    public static void clear_unused_bricks_after_compaction(
        heap_segment* region,
        byte* plan_allocated)
    {
        byte* firstUnusedBrick =
            card_table_info.align_lower_brick(plan_allocated);
        if (firstUnusedBrick < plan_allocated)
        {
            firstUnusedBrick += (nint)card_table_info.brick_size;
        }

        byte* reserved = heap_segment.heap_segment_reserved(region);
        if (firstUnusedBrick < reserved)
        {
            clear_brick_table(firstUnusedBrick, reserved);
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

    public static int loh_object_p(byte* o)
    {
        int brick_entry = brick_table[(nint)brick_of(o)];
        return brick_entry == 0 ? 1 : 0;
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

    private static void reloc_loh_survivor_helper_callback(
        byte** pval,
        void* context)
    {
        RecordLohReference(*pval);
        reloc_survivor_helper((gc_heap*)context, pval);
    }

    public static void relocate_obj_helper(gc_heap* hp, byte* x, nuint s)
    {
        if (contain_pointers(x) != 0)
        {
            go_through_object_nostart(
                method_table(x),
                x,
                s,
                hp,
                &reloc_survivor_helper_callback);
        }

        check_class_object_demotion(hp, x);
    }

    public static void reloc_ref_in_shortened_obj(byte** address_to_set_card, byte** address_to_reloc)
    {
        relocate_address(address_to_reloc);

        check_demotion_helper(address_to_reloc, (byte*)address_to_set_card);
    }

    public static void relocate_pre_plug_info(mark* pinned_plug_entry)
    {
        byte* plug = pinned_plug(pinned_plug_entry);
        byte* pre_plug_start = plug - sizeof(plug_and_gap);
        // Note that we need to add one ptr size here otherwise we may not be able to find the
        // relocated address. Consider this scenario:
        // gen1 start | 3-ptr sized NP | PP
        // 0          | 0x18           | 0x30
        // If we are asking for the reloc address of 0x10 we will AV in relocate_address because
        // the first plug we saw in the brick is 0x18 which means 0x10 will cause us to go back a
        // brick which is 0, and then we'll AV in tree_search when we try to do
        // node_right_child(tree).
        pre_plug_start += sizeof(byte*);
        byte** old_address = &pre_plug_start;

        relocate_address(old_address);

        mark.set_pre_plug_info_reloc_start(
            pinned_plug_entry,
            pre_plug_start - sizeof(byte*));
    }

    private struct relocate_shortened_obj_context
    {
        public gc_heap* heap;
        public byte* end;
        public byte* saved_plug_info_start;
        public byte** saved_info_to_relocate;
    }

    private static void relocate_shortened_obj_callback(byte** pval, void* contextPointer)
    {
        relocate_shortened_obj_context* context =
            (relocate_shortened_obj_context*)contextPointer;

        if ((byte*)pval >= context->end)
        {
            nint savedIndex = (nint)(
                ((byte*)pval - context->saved_plug_info_start) / sizeof(byte**));
            byte** current_saved_info_to_relocate =
                context->saved_info_to_relocate + savedIndex;
            reloc_ref_in_shortened_obj(pval, current_saved_info_to_relocate);
        }
        else
        {
            reloc_survivor_helper(context->heap, pval);
        }
    }

    public static void relocate_shortened_obj_helper(
        gc_heap* hp,
        byte* x,
        nuint s,
        byte* end,
        mark* pinned_plug_entry,
        int is_pinned)
    {
        byte* plug = pinned_plug(pinned_plug_entry);

        if (is_pinned == 0)
        {
            relocate_pre_plug_info(pinned_plug_entry);
        }

        verify_pins_with_post_plug_info();

        byte* saved_plug_info_start;
        byte** saved_info_to_relocate;

        if (is_pinned != 0)
        {
            saved_plug_info_start = mark.get_post_plug_info_start(pinned_plug_entry);
            saved_info_to_relocate =
                (byte**)mark.get_post_plug_reloc_info(pinned_plug_entry);
        }
        else
        {
            saved_plug_info_start = plug - sizeof(plug_and_gap);
            saved_info_to_relocate =
                (byte**)mark.get_pre_plug_reloc_info(pinned_plug_entry);
        }

        if (contain_pointers(x) != 0)
        {
            relocate_shortened_obj_context context = new()
            {
                heap = hp,
                end = end,
                saved_plug_info_start = saved_plug_info_start,
                saved_info_to_relocate = saved_info_to_relocate,
            };

            go_through_object_nostart(
                method_table(x),
                x,
                s,
                &context,
                &relocate_shortened_obj_callback);
        }

        check_class_object_demotion(hp, x);
    }

    public static void relocate_survivor_helper(gc_heap* hp, byte* plug, byte* plug_end)
    {
        byte* x = plug;
        while (x < plug_end)
        {
            nuint s = size(x);
            byte* next_obj = x + (nint)Align(s);
            relocate_obj_helper(hp, x, s);
            Debug.Assert(s > 0);
            x = next_obj;
        }
    }

    // The native body is guarded by _DEBUG && VERIFY_HEAP. NativeAOT does not currently build
    // the verification-only pinned-queue state that body consumes, so this is its guarded no-op.
    public static void verify_pins_with_post_plug_info()
    {
#if DEBUG && VERIFY_HEAP
#endif
    }

#if COLLECTIBLE_CLASS
    // We don't want to burn another ptr size space for pinned plugs to record this so just
    // set the card unconditionally for collectible objects if we are demoting.
    public static void unconditional_set_card_collectible(byte* obj)
    {
        if (settings.demotion != 0)
        {
            set_card(card_of(obj));
        }
    }
#endif

    public static void relocate_shortened_survivor_helper(
        gc_heap* hp,
        byte* plug,
        byte* plug_end,
        mark* pinned_plug_entry)
    {
        byte* x = plug;
        byte* p_plug = pinned_plug(pinned_plug_entry);
        int is_pinned = plug == p_plug ? 1 : 0;
        int check_short_obj_p = is_pinned != 0
            ? mark.post_short_p(pinned_plug_entry)
            : mark.pre_short_p(pinned_plug_entry);

        plug_end += sizeof(gap_reloc_pair);

        verify_pins_with_post_plug_info();

        while (x < plug_end)
        {
            if (check_short_obj_p != 0 &&
                (uint)(plug_end - x) < (uint)min_pre_pin_obj_size)
            {
                if (is_pinned != 0)
                {
#if COLLECTIBLE_CLASS
                    if (mark.post_short_collectible_p(pinned_plug_entry) != 0)
                    {
                        unconditional_set_card_collectible(x);
                    }
#endif

                    // Relocate the saved references based on bits set.
                    byte** saved_plug_info_start =
                        (byte**)mark.get_post_plug_info_start(pinned_plug_entry);
                    byte** saved_info_to_relocate =
                        (byte**)mark.get_post_plug_reloc_info(pinned_plug_entry);
                    for (nuint i = 0; i < mark.get_max_short_bits(); i++)
                    {
                        if (mark.post_short_bit_p(pinned_plug_entry, i) != 0)
                        {
                            reloc_ref_in_shortened_obj(
                                saved_plug_info_start + (nint)i,
                                saved_info_to_relocate + (nint)i);
                        }
                    }
                }
                else
                {
#if COLLECTIBLE_CLASS
                    if (mark.pre_short_collectible_p(pinned_plug_entry) != 0)
                    {
                        unconditional_set_card_collectible(x);
                    }
#endif

                    relocate_pre_plug_info(pinned_plug_entry);

                    // Relocate the saved references based on bits set.
                    byte** saved_plug_info_start =
                        (byte**)(p_plug - sizeof(plug_and_gap));
                    byte** saved_info_to_relocate =
                        (byte**)mark.get_pre_plug_reloc_info(pinned_plug_entry);
                    for (nuint i = 0; i < mark.get_max_short_bits(); i++)
                    {
                        if (mark.pre_short_bit_p(pinned_plug_entry, i) != 0)
                        {
                            reloc_ref_in_shortened_obj(
                                saved_plug_info_start + (nint)i,
                                saved_info_to_relocate + (nint)i);
                        }
                    }
                }

                break;
            }

            nuint s = size(x);
            byte* next_obj = x + (nint)Align(s);

            if (next_obj >= plug_end)
            {
                verify_pins_with_post_plug_info();

                relocate_shortened_obj_helper(
                    hp,
                    x,
                    s,
                    x + (nint)Align(s) - sizeof(plug_and_gap),
                    pinned_plug_entry,
                    is_pinned);
            }
            else
            {
                relocate_obj_helper(hp, x, s);
            }

            Debug.Assert(s > 0);
            x = next_obj;
        }

        verify_pins_with_post_plug_info();
    }

    public static void relocate_survivors_in_plug(
        gc_heap* hp,
        byte* plug,
        byte* plug_end,
        int check_last_object_p,
        mark* pinned_plug_entry)
    {
        if (check_last_object_p != 0)
        {
            relocate_shortened_survivor_helper(hp, plug, plug_end, pinned_plug_entry);
        }
        else
        {
            relocate_survivor_helper(hp, plug, plug_end);
        }
    }

    public static void relocate_survivors_in_brick(
        gc_heap* hp,
        byte* tree,
        relocate_args* args)
    {
        Debug.Assert(tree is not null);

        if (node_left_child(tree) != 0)
        {
            relocate_survivors_in_brick(hp, tree + node_left_child(tree), args);
        }

        {
            byte* plug = tree;
            int has_post_plug_info_p = 0;
            int has_pre_plug_info_p = 0;

            if (tree == oldest_pinned_plug)
            {
                args->pinned_plug_entry = get_oldest_pinned_entry(
                    hp,
                    &has_pre_plug_info_p,
                    &has_post_plug_info_p);
                Debug.Assert(tree == pinned_plug(args->pinned_plug_entry));
            }

            if (args->last_plug is not null)
            {
                nuint gap_size = node_gap_size(tree);
                byte* gap = plug - (nint)gap_size;
                Debug.Assert(gap_size >= Align((nuint)GCInterfaceOffsets.min_obj_size));
                byte* last_plug_end = gap;
                int check_last_object_p =
                    args->is_shortened != 0 || has_pre_plug_info_p != 0 ? 1 : 0;

                relocate_survivors_in_plug(
                    hp,
                    args->last_plug,
                    last_plug_end,
                    check_last_object_p,
                    args->pinned_plug_entry);
            }
            else
            {
                Debug.Assert(has_pre_plug_info_p == 0);
            }

            args->last_plug = plug;
            args->is_shortened = has_post_plug_info_p;
        }

        if (node_right_child(tree) != 0)
        {
            relocate_survivors_in_brick(hp, tree + node_right_child(tree), args);
        }
    }

    public static heap_segment* get_start_segment(generation* gen)
    {
        heap_segment* start_heap_segment =
            heap_segment_rw(generation.generation_start_segment(gen));
        start_heap_segment = heap_segment_non_sip(start_heap_segment);
        return start_heap_segment;
    }

    public static void relocate_survivors(
        gc_heap* hp,
        int condemned_gen_number,
        byte* first_condemned_address)
    {
        reset_pinned_queue_bos(hp);
        update_oldest_pinned_plug(hp);

        int stop_gen_idx = get_stop_generation_index(condemned_gen_number);
        _ = first_condemned_address;

        generation* generation_table = generation_table_of(hp);
        for (int i = condemned_gen_number; i >= stop_gen_idx; i--)
        {
            generation* condemned_gen = generation_of(generation_table, i);
            heap_segment* current_heap_segment =
                heap_segment_rw(generation.generation_start_segment(condemned_gen));
            current_heap_segment = relocate_advance_to_non_sip(hp, current_heap_segment);
            if (current_heap_segment is null)
            {
                continue;
            }

            byte* start_address = get_soh_start_object(current_heap_segment, condemned_gen);
            nuint current_brick = brick_of(start_address);

            Debug.Assert(current_heap_segment is not null);

            byte* end_address = heap_segment.heap_segment_allocated(current_heap_segment);
            nuint end_brick = brick_of(end_address - 1);
            relocate_args args = default;

            while (true)
            {
                if (current_brick > end_brick)
                {
                    if (args.last_plug is not null)
                    {
                        Debug.Assert(args.is_shortened == 0);
                        relocate_survivors_in_plug(
                            hp,
                            args.last_plug,
                            heap_segment.heap_segment_allocated(current_heap_segment),
                            args.is_shortened,
                            args.pinned_plug_entry);

                        args.last_plug = null;
                    }

                    heap_segment* next_heap_segment =
                        heap_segment.heap_segment_next(current_heap_segment);
                    if (next_heap_segment is not null)
                    {
                        next_heap_segment = relocate_advance_to_non_sip(hp, next_heap_segment);
                        if (next_heap_segment is not null)
                        {
                            current_heap_segment = next_heap_segment;
                            current_brick = brick_of(
                                heap_segment.heap_segment_mem(current_heap_segment));
                            end_brick = brick_of(
                                heap_segment.heap_segment_allocated(current_heap_segment) - 1);
                            continue;
                        }
                        else
                        {
                            break;
                        }
                    }
                    else
                    {
                        break;
                    }
                }

                int brick_entry = brick_table[(nint)current_brick];
                if (brick_entry >= 0)
                {
                    relocate_survivors_in_brick(
                        hp,
                        brick_address(current_brick) + brick_entry - 1,
                        &args);
                }

                current_brick++;
            }
        }
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

    public static void relocate_in_loh_compact(gc_heap* hp)
    {
        BeginLohRelocate();
        generation* gen = generation_of(
            generation_table_of(hp),
            (int)gc_generation_num.loh_generation);
        heap_segment* seg =
            heap_segment_rw(generation.generation_start_segment(gen));
        Debug.Assert(seg is not null);
        byte* o = get_uoh_start_object(seg, gen);

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
                check_class_object_demotion(hp, o);
                if (contain_pointers(o) != 0)
                {
                    go_through_object_nostart(
                        method_table(o),
                        o,
                        size(o),
                        hp,
                        &reloc_loh_survivor_helper_callback);
                }

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

        EndLohRelocate();
    }

    public static void compact_loh(gc_heap* hp)
    {
        BeginLohCompact();
        Debug.Assert(
            loh_compaction_requested() != 0 ||
            heap_hard_limit != 0 ||
            conserve_mem_setting != 0 ||
            settings.reason == gc_reason.reason_induced_aggressive);

        generation* gen = generation_of(
            generation_table_of(hp),
            (int)gc_generation_num.loh_generation);
        heap_segment* start_seg =
            heap_segment_rw(generation.generation_start_segment(gen));
        Debug.Assert(start_seg is not null);
        heap_segment* seg = start_seg;
        heap_segment* prev_seg = null;
        byte* o = get_uoh_start_object(seg, gen);

        allocator.clear(generation.generation_allocator(gen));
        generation.generation_free_list_space(gen) = 0;
        generation.generation_free_obj_space(gen) = 0;
        loh_pinned_queue_bos = 0;

        while (true)
        {
            if (o >= heap_segment.heap_segment_allocated(seg))
            {
                heap_segment* next_seg = heap_segment.heap_segment_next(seg);
                if (heap_segment.heap_segment_plan_allocated(seg) ==
                        heap_segment.heap_segment_mem(seg) &&
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
                        if (heap_segment.heap_segment_plan_allocated(seg) >
                            heap_segment.heap_segment_allocated(seg))
                        {
                            if (heap_segment.heap_segment_plan_allocated(seg) -
                                    (nint)plug_skew >
                                heap_segment.heap_segment_used(seg))
                            {
                                heap_segment.heap_segment_used(seg) =
                                    heap_segment.heap_segment_plan_allocated(seg) -
                                    (nint)plug_skew;
                            }
                        }

                        heap_segment.heap_segment_allocated(seg) =
                            heap_segment.heap_segment_plan_allocated(seg);
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
            }

            if (((CObjectHeader*)o)->IsMarked() != 0)
            {
                nuint object_size = AlignQword(size(o));
                nuint loh_pad;
                byte* reloc = o;
                ((CObjectHeader*)o)->ClearMarked();

                if (((CObjectHeader*)o)->IsPinned() != 0)
                {
                    Debug.Assert(loh_pinned_plug_que_empty_p() == 0);
                    mark* m = loh_pinned_plug_of(loh_deque_pinned_plug());
                    byte* plug = pinned_plug(m);
                    Debug.Assert(plug == o);

                    loh_pad = pinned_len(m);
                    ((CObjectHeader*)o)->GetHeader()->ClrGCBit();
                }
                else
                {
                    loh_pad = AlignQword((nuint)sizeof(loh_padding_obj));
                    reloc += loh_node_relocation_distance(o);
                    gcmemcopy(reloc, o, object_size, copy_cards_p: 1);
                }

                thread_gap(reloc - (nint)loh_pad, loh_pad, gen);

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

        Debug.Assert(loh_pinned_plug_que_empty_p() != 0);
        EndLohCompact();
    }

#if !MULTIPLE_HEAPS
    public static bool relocate_phase(
        int condemned_gen_number,
        byte* first_condemned_address)
    {
        if (settings.compaction == 0)
        {
            return false;
        }

        return relocate_phase(
            ManagedGCRegionBootstrap.Heap,
            condemned_gen_number,
            first_condemned_address);
    }

    public static bool relocate_phase(
        gc_heap* hp,
        int condemned_gen_number,
        byte* first_condemned_address)
    {
        CFinalize* finalizeQueue = finalize_queue;
        if (hp is null ||
            finalizeQueue is null ||
            (uint)condemned_gen_number > (uint)GCInterfaceOffsets.max_generation ||
            settings.condemned_generation != condemned_gen_number ||
            settings.concurrent != 0 ||
#if BACKGROUND_GC
            ((settings.background_p != 0 || background_running_p()) &&
             condemned_gen_number == GCInterfaceOffsets.max_generation) ||
#endif
            (loh_compacted_p != 0 &&
             !validate_loh_compaction_prerequisites(hp)))
        {
            return false;
        }

        ScanContext sc = default;
        sc.init();
        sc.thread_number = hp->heap_number;
        sc.thread_count = 1;
        sc.promotion = 0;
        sc.concurrent = 0;

        GCScan.GcScanRoots(
            &relocate,
            condemned_gen_number,
            GCInterfaceOffsets.max_generation,
            &sc);

        if (condemned_gen_number != GCInterfaceOffsets.max_generation)
        {
            mark_through_cards_for_segments(hp, relocating: true);
            mark_through_cards_for_uoh_objects(
                hp,
                (int)gc_generation_num.loh_generation,
                relocating: true);
            mark_through_cards_for_uoh_objects(
                hp,
                (int)gc_generation_num.poh_generation,
                relocating: true);
        }
        else if (loh_compacted_p != 0)
        {
            Debug.Assert(settings.condemned_generation == GCInterfaceOffsets.max_generation);
            relocate_in_loh_compact(hp);
        }
        else
        {
            relocate_in_uoh_objects(hp, (int)gc_generation_num.loh_generation);
        }

        if (condemned_gen_number == GCInterfaceOffsets.max_generation)
        {
            relocate_in_uoh_objects(hp, (int)gc_generation_num.poh_generation);
        }

        relocate_survivors(
            hp,
            condemned_gen_number,
            first_condemned_address);

        finalizeQueue->RelocateFinalizationData(condemned_gen_number, hp);

        GCScan.GcScanHandles(
            &relocate,
            condemned_gen_number,
            GCInterfaceOffsets.max_generation,
            &sc);
        return true;
    }

    public static bool compact_phase(
        int condemned_gen_number,
        byte* first_condemned_address,
        int clear_cards)
    {
        if (settings.compaction == 0)
        {
            return false;
        }

        return compact_phase(
            ManagedGCRegionBootstrap.Heap,
            condemned_gen_number,
            first_condemned_address,
            clear_cards);
    }

    public static bool compact_phase(
        gc_heap* hp,
        int condemned_gen_number,
        byte* first_condemned_address,
        int clear_cards)
    {
        if (hp is null ||
            (uint)condemned_gen_number > (uint)GCInterfaceOffsets.max_generation ||
            settings.condemned_generation != condemned_gen_number ||
            settings.concurrent != 0 ||
#if BACKGROUND_GC
            ((settings.background_p != 0 || background_running_p()) &&
             condemned_gen_number == GCInterfaceOffsets.max_generation) ||
#endif
            (loh_compacted_p != 0 &&
             !validate_loh_compaction_prerequisites(hp)))
        {
            return false;
        }

        _ = first_condemned_address;

        if (loh_compacted_p != 0)
        {
            compact_loh(hp);
        }

        reset_pinned_queue_bos(hp);
        update_oldest_pinned_plug(hp);
        bool reused_seg = expand_reused_seg_p();
        if (reused_seg)
        {
            generation* generation_table = generation_table_of(hp);
            for (int i = 1; i <= GCInterfaceOffsets.max_generation; i++)
            {
                generation.generation_allocation_size(generation_of(generation_table, i)) = 0;
            }
        }

        int stop_gen_idx = get_stop_generation_index(condemned_gen_number);
        generation* generations = generation_table_of(hp);
        for (int i = condemned_gen_number; i >= stop_gen_idx; i--)
        {
            generation* condemned_gen = generation_of(generations, i);
            heap_segment* current_heap_segment = get_start_segment(condemned_gen);
            if (current_heap_segment is null)
            {
                continue;
            }

            nuint current_brick = brick_of(heap_segment.heap_segment_mem(current_heap_segment));
            byte* end_address = heap_segment.heap_segment_allocated(current_heap_segment);
            nuint end_brick = brick_of(end_address - 1);
            compact_args args = default;
            args.last_plug = null;
            args.before_last_plug = null;
            args.current_compacted_brick = ~(nuint)1;
            args.is_shortened = 0;
            args.pinned_plug_entry = null;
            args.copy_cards_p = condemned_gen_number >= 1 || clear_cards == 0 ? 1 : 0;
            args.check_gennum_p = reused_seg ? 1 : 0;
            if (args.check_gennum_p != 0)
            {
                args.src_gennum = 2;
            }

            Debug.Assert(args.check_gennum_p == 0);

            while (true)
            {
                if (current_brick > end_brick)
                {
                    if (args.last_plug is not null)
                    {
                        compact_plug(
                            args.last_plug,
                            (nuint)(heap_segment.heap_segment_allocated(current_heap_segment) -
                                args.last_plug),
                            args.is_shortened,
                            &args);
                    }

                    heap_segment* next_heap_segment =
                        heap_segment_next_non_sip(current_heap_segment);
                    if (next_heap_segment is not null)
                    {
                        current_heap_segment = next_heap_segment;
                        current_brick = brick_of(
                            heap_segment.heap_segment_mem(current_heap_segment));
                        end_brick = brick_of(
                            heap_segment.heap_segment_allocated(current_heap_segment) - 1);
                        args.last_plug = null;
                        if (args.check_gennum_p != 0)
                        {
                            args.src_gennum = 2;
                        }

                        continue;
                    }

                    if (args.before_last_plug is not null)
                    {
                        Debug.Assert(
                            args.current_compacted_brick != unchecked((nuint)~1u));
                        set_brick(
                            args.current_compacted_brick,
                            (nint)(args.before_last_plug -
                                brick_address(args.current_compacted_brick)));
                    }

                    break;
                }

                int brick_entry = brick_table[(nint)current_brick];
                if (brick_entry >= 0)
                {
                    compact_in_brick(
                        brick_address(current_brick) + brick_entry - 1,
                        &args);
                }

                current_brick++;
            }
        }

        recover_saved_pinned_info();

        int gen_limit = condemned_gen_number + 1 < GCInterfaceOffsets.max_generation
            ? condemned_gen_number + 1
            : GCInterfaceOffsets.max_generation;
        for (int i = 0; i <= gen_limit; i++)
        {
            generation* gen = generation_of(generations, i);
            for (heap_segment* region = generation.generation_start_segment_rw(gen);
                 region is not null;
                 region = heap_segment.heap_segment_next(region))
            {
                byte* plan_allocated = heap_segment.heap_segment_plan_allocated(region);
                clear_unused_bricks_after_compaction(region, plan_allocated);
                if (plan_allocated > heap_segment.heap_segment_used(region))
                {
                    heap_segment.heap_segment_used(region) = plan_allocated;
                }
            }
        }

        return true;
    }
#endif
#endif
}
#pragma warning restore CS8981
