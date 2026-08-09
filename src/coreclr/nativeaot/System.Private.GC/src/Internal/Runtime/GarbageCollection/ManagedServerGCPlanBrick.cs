// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Server plan-phase brick/tree threading and pinned-plug-queue write leaves, translated from the
// SVR-namespace compilation of plan_phase.cpp and mark_phase.cpp for the active x64 Linux
// SERVER_GC -> MULTIPLE_HEAPS -> DYNAMIC_HEAP_COUNT -> USE_REGIONS feature chain. These are the
// dependency-closed leaves the plan_phase driver invokes as it walks a heap's condemned plugs to
// build the plug tree and record the pinned-plug free-space queue:
//
//   * the 16-bit population-count / parity helpers oddp and logcount that insert_node uses to place
//     each plug in the balanced brick tree,
//   * the brick-tree threading leaves insert_node (link a plug into the tree) and update_brick_table
//     (publish a finished tree into the shared brick table and fill the intervening -1 bricks),
//   * the special-method-table-bit save/restore leaves clear_special_bits / set_special_bits and the
//     short-plug bit-marking callback (short_plug_context / set_short_plug_bit) that record which
//     gap words of a short object right next to a pin still contain live references,
//   * the pinned-plug-queue growth leaf grow_mark_stack and the queue writers enque_pinned_plug /
//     save_post_plug_info that snapshot the pre-plug and post-plug gap-reloc pairs a pin overwrites,
//   * convert_to_pinned_plug (turn an over-large npinned plug into an artificial pin) and
//     store_plug_gap_info (the per-plug dispatch that records the free gap size and enqueues /
//     merges / post-annotates the pin), and
//   * the pin-consuming allocator positioning leaves set_allocator_next_pin, set_pinned_info and
//     merge_with_last_pinned_plug.
//
// gcpriv.h marks the pinned-plug queue (mark_stack_array / mark_stack_array_length / mark_stack_tos /
// mark_stack_bos) and saved_pinned_plug_index as PER_HEAP_FIELD_SINGLE_GC, so they are instance-owned
// in the MULTIPLE_HEAPS build and every writer here reaches them through the heap parameter, exactly
// as the native per-heap methods do through the implicit this. The brick table itself is a single
// process-wide bookkeeping array (static brick_table, shared by every heap over the whole address
// range), so insert_node / update_brick_table and their set_brick / brick_of / brick_address leaves
// stay static, matching native.
//
// No collection is routed by this slice: the plan_phase driver that sequences these leaves (its own
// per-GC region-planning counter reset, allocate_in_condemned_generations, plan_generation_start,
// the plug walk, and the gc_join_decide_on_compaction join), plan_loh / plan_poh,
// fix_generation_bounds and the relocate / compact / sweep execution all remain deferred, so nothing
// here runs against a live heap yet.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

using System;
using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    // gcinternal.h FATAL_GC_ERROR code used by the pinned-plug queue growth failure path.
    private const uint CORINFO_EXCEPTION_GC = 0xE0004743;

    // gcpriv.h plug_skew is sizeof(ObjHeader); NativeAOT's object header is one pointer-sized word.
    private static nuint plug_skew => (nuint)sizeof(nuint);

    // plan_phase.cpp oddp: odd-integer parity, used to pick the brick-tree link direction.
    public static bool oddp(nuint integer)
    {
        return (integer & 1) != 0;
    }

    // plan_phase.cpp logcount: number of set bits in a 16-bit word, used to find the earlier tree
    // node an even-sequence plug attaches under.
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

    // plan_phase.cpp: link a plug into the balanced binary brick tree. A power-of-two sequence number
    // starts a new tree rooted at the plug; an odd number becomes the previous node's right child; an
    // even number becomes the left child of the node reached by walking logcount-2 right edges from
    // the root. The tree is threaded as byte offsets so it survives relocation.
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

    // plan_phase.cpp: publish the just-built tree into the brick that starts it (or -1 if empty),
    // then set every brick between it and the next plug's brick to a decreasing back-offset (or -1
    // once past the finished plug end) so a lookup can walk back to the owning tree.
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

    // gc.cpp: strip and restore the object-header special bits so the saved pre/post-plug info copies
    // hold the unmarked method table while the original object keeps its collector bookkeeping bits.
    public static nuint clear_special_bits(byte* node)
    {
        return ((CObjectHeader*)node)->ClearSpecialBits();
    }

    public static void set_special_bits(byte* node, nuint special_bits)
    {
        ((CObjectHeader*)node)->SetSpecialBits(special_bits);
    }

    // gcinternal.h grow_mark_stack: double the pinned-plug queue (or grow to the initial length),
    // copy the existing entries, and hand back the new buffer and length by reference. Overflow of
    // the byte-size computation is treated as an allocation failure, exactly as the native new[]
    // would fail. Returns 0 on failure so enque_pinned_plug can report a fatal error.
    public static int grow_mark_stack(ref mark* m, ref nuint len, nuint init_len)
    {
        if (len > nuint.MaxValue / 2)
        {
            return 0;
        }

        nuint new_size = 2 * len;
        if (new_size < init_len)
        {
            new_size = init_len;
        }

        if (new_size > nuint.MaxValue / (nuint)sizeof(mark))
        {
            return 0;
        }

        nuint bytes = new_size * (nuint)sizeof(mark);
        if (bytes > unchecked((nuint)long.MaxValue))
        {
            return 0;
        }

        mark* tmp = (mark*)SyncImports.ManagedGC_AllocZeroed(bytes);
        if (tmp is not null)
        {
            nuint bytes_to_copy = len * (nuint)sizeof(mark);
            if (bytes_to_copy != 0)
            {
                Buffer.MemoryCopy(m, tmp, (long)bytes, (long)bytes_to_copy);
            }

            if (m is not null)
            {
                SyncImports.ManagedGC_Free(m);
            }

            m = tmp;
            len = new_size;
            return 1;
        }

        return 0;
    }

    // mark_phase.cpp go_through_object_nostart callback context for a short object right beside a pin:
    // the pin's mark entry, the pin's plug address, and whether it is the pre- or post-plug side.
    private unsafe struct short_plug_context
    {
        public mark* m;
        public byte* plug;
        public int pre_p;
    }

    // mark_phase.cpp: mark, in the saved plug info, which gap-sized slot of the short object still
    // holds a live reference so relocation can fix it up after the gap overwrites the object.
    private static void set_short_plug_bit(byte** pval, void* context)
    {
        short_plug_context* short_context = (short_plug_context*)context;
        nuint gap_offset = unchecked(
            ((nuint)pval - ((nuint)short_context->plug - (nuint)sizeof(gap_reloc_pair) - plug_skew))
            / (nuint)sizeof(byte*));

        if (short_context->pre_p != 0)
        {
            mark.set_pre_short_bit(short_context->m, gap_offset);
        }
        else
        {
            mark.set_post_short_bit(short_context->m, gap_offset);
        }
    }

    // mark_phase.cpp: push a pin onto this heap's queue, growing it if full. When the plug abuts a
    // live plug we also snapshot the pre-plug gap-reloc pair (which the pin's gap will overwrite) and,
    // if the preceding object is shorter than a gap-reloc pair, record its short-object reference bits.
    // Runtime.ManagedGC does not define GC_CONFIG_DRIVEN, so the native idp_* diagnostic counters are
    // compiled out.
    public static void enque_pinned_plug(
        gc_heap* heap,
        byte* plug,
        int save_pre_plug_info_p,
        byte* last_object_in_last_plug)
    {
        if (heap->mark_stack_array_length <= heap->mark_stack_tos)
        {
            if (grow_mark_stack(
                    ref heap->mark_stack_array,
                    ref heap->mark_stack_array_length,
                    gc_rand.MARK_STACK_INITIAL_LENGTH) == 0)
            {
                // Continuing after this failure risks corrupting the mark stack.
                GCToEEInterface.HandleFatalError(CORINFO_EXCEPTION_GC);
            }
        }

        mark* m = &heap->mark_stack_array[heap->mark_stack_tos];
        m->first = plug;
        m->saved_pre_p = save_pre_plug_info_p;

        if (save_pre_plug_info_p != 0)
        {
            nuint special_bits = clear_special_bits(last_object_in_last_plug);
            m->saved_pre_plug = *(gap_reloc_pair*)(((plug_and_gap*)plug) - 1);
            set_special_bits(last_object_in_last_plug, special_bits);

            m->saved_pre_plug_reloc = *(gap_reloc_pair*)(((plug_and_gap*)plug) - 1);

            nuint last_obj_size = (nuint)(plug - last_object_in_last_plug);
            if (last_obj_size < min_pre_pin_obj_size)
            {
                mark.set_pre_short(m);

                if (contain_pointers(last_object_in_last_plug) != 0)
                {
                    short_plug_context context = new()
                    {
                        m = m,
                        plug = plug,
                        pre_p = 1,
                    };
                    go_through_object_nostart(
                        method_table(last_object_in_last_plug),
                        last_object_in_last_plug,
                        last_obj_size,
                        &context,
                        &set_short_plug_bit);
                }
            }
        }

        m->saved_post_p = 0;
    }

    // mark_phase.cpp: snapshot the post-plug gap-reloc pair a pin's trailing gap will overwrite, and
    // record the short-object reference bits of the object right after the pin when it is too short.
    public static void save_post_plug_info(
        gc_heap* heap,
        byte* last_pinned_plug,
        byte* last_object_in_last_plug,
        byte* post_plug)
    {
        mark* m = &heap->mark_stack_array[heap->mark_stack_tos - 1];
        Debug.Assert(last_pinned_plug == m->first);
        m->saved_post_plug_info_start = (byte*)(((plug_and_gap*)post_plug) - 1);

        nuint special_bits = clear_special_bits(last_object_in_last_plug);
        m->saved_post_plug = *(gap_reloc_pair*)m->saved_post_plug_info_start;
        set_special_bits(last_object_in_last_plug, special_bits);

        m->saved_post_plug_reloc = *(gap_reloc_pair*)m->saved_post_plug_info_start;
        m->saved_post_p = 1;

#if DEBUG
        m->saved_post_plug_debug.gap = 1;
#endif

        nuint last_obj_size = (nuint)(post_plug - last_object_in_last_plug);
        if (last_obj_size < min_pre_pin_obj_size)
        {
            mark.set_post_short(m);

            if (contain_pointers(last_object_in_last_plug) != 0)
            {
                short_plug_context context = new()
                {
                    m = m,
                    plug = post_plug,
                    pre_p = 0,
                };
                go_through_object_nostart(
                    method_table(last_object_in_last_plug),
                    last_object_in_last_plug,
                    last_obj_size,
                    &context,
                    &set_short_plug_bit);
            }
        }
    }

    // plan_phase.cpp: force an over-large npinned plug to be treated as an artificial pin so a large
    // demoted object is never folded into a younger generation's plan allocation.
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

    // plan_phase.cpp: per-plug bookkeeping for the plan walk. A free gap between two npinned plugs
    // records its size; a pinned plug is enqueued (or merged into the immediately preceding pin) with
    // its pre-plug info; the object right after a pin gets its post-plug info saved. Because of the
    // artificial pinning we cannot assume pinned and npinned plugs strictly interleave.
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
                    // DOUBLY_LINKED_FL (BACKGROUND_GC && 64-bit; USE_REGIONS implies 64-bit here):
                    // remember the pin that just captured gen2's last free-list-allocated object so
                    // make_free_list can restore it.
#if BACKGROUND_GC
                    generation* maxGeneration = generation_of(
                        generation_table_of(hp),
                        GCInterfaceOffsets.max_generation);
                    if (last_object_in_last_plug ==
                        generation.generation_last_free_list_allocated(maxGeneration))
                    {
                        hp->saved_pinned_plug_index = hp->mark_stack_tos;
                    }
#endif

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

    // mark_phase.cpp: if the oldest queued pin falls inside the consing generation's current alloc
    // window, cap the allocation limit at the pin so allocate_in_condemned_generations stops before
    // it, leaving the pinned object in place.
    public static void set_allocator_next_pin(gc_heap* heap, generation* gen)
    {
        if (pinned_plug_que_empty_p(heap) == 0)
        {
            mark* oldest_entry = oldest_pin(heap);
            byte* plug = pinned_plug(oldest_entry);
            if (plug >= generation.generation_allocation_pointer(gen) &&
                plug < generation.generation_allocation_limit(gen))
            {
#if DEBUG
                Debug.Assert(
                    region_of(generation.generation_allocation_pointer(gen)) ==
                    region_of(generation.generation_allocation_limit(gen) - 1));
#endif
                generation.generation_allocation_limit(gen) = pinned_plug(oldest_entry);
            }
            else
            {
                Debug.Assert(
                    !(plug < generation.generation_allocation_pointer(gen) &&
                      plug >= heap_segment.heap_segment_mem(generation.generation_allocation_segment(gen))));
            }
        }
    }

    // mark_phase.cpp: record the final length of the pin at the top of the queue, advance tos, then
    // reposition the allocator at the next pin. Called after the plan has consumed the pin's gap.
    public static void set_pinned_info(gc_heap* heap, byte* last_pinned_plug, nuint plug_len, generation* gen)
    {
        mark* m = &heap->mark_stack_array[heap->mark_stack_tos];
        Debug.Assert(last_pinned_plug == m->first);

        m->len = plug_len;
        heap->mark_stack_tos++;
        Debug.Assert(gen is not null);
        // Why are we checking here? gen is never 0.
        if (gen is not null)
        {
            set_allocator_next_pin(heap, gen);
        }
    }

    // mark_phase.cpp: fold an adjacent pin into the previous pin. If the previous pin already saved
    // post-plug info, restore the bytes its gap overwrote before extending its length. last_pinned_plug
    // is only for asserting.
    public static void merge_with_last_pinned_plug(gc_heap* heap, byte* last_pinned_plug, nuint plug_size)
    {
        if (last_pinned_plug is not null)
        {
            mark* last_m = &heap->mark_stack_array[heap->mark_stack_tos - 1];
            Debug.Assert(last_pinned_plug == last_m->first);
            if (last_m->saved_post_p != 0)
            {
                last_m->saved_post_p = 0;
                // We need to recover what the gap has overwritten.
                *(gap_reloc_pair*)(last_m->first + last_m->len - (nint)sizeof(plug_and_gap)) = last_m->saved_post_plug;
            }

            last_m->len += plug_size;
        }
    }
}

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
