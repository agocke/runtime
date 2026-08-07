// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the allocation-free relocation copy primitive and dependency-closed WKS USE_REGIONS
// helpers, including brick-tree reference and plug-level SOH relocation and the bounded
// synchronous full-GC relocation orchestration, from relocate_compact.cpp.

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

#if !MULTIPLE_HEAPS
    public static bool relocate_phase(
        int condemned_gen_number,
        byte* first_condemned_address)
    {
        gc_heap* hp = ManagedGCRegionBootstrap.Heap;
        CFinalize* finalizeQueue = finalize_queue;
        if (hp is null ||
            finalizeQueue is null ||
            condemned_gen_number != GCInterfaceOffsets.max_generation ||
            settings.condemned_generation != condemned_gen_number ||
            settings.compaction == 0 ||
            settings.concurrent != 0 ||
#if BACKGROUND_GC
            settings.background_p != 0 ||
            background_running_p() ||
#endif
            loh_compacted_p != 0)
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

        relocate_in_uoh_objects(hp, (int)gc_generation_num.loh_generation);
        relocate_in_uoh_objects(hp, (int)gc_generation_num.poh_generation);

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
#endif
#endif
}
#pragma warning restore CS8981
