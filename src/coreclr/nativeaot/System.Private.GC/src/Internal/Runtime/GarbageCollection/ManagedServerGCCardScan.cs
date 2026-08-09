// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Server foreground cross-generation dirty-card scan, translated from the SVR compilation of
// gc_heap::mark_through_cards_for_segments / mark_through_cards_for_uoh_objects (mark_phase.cpp,
// background.cpp) for the active SERVER_GC / MULTIPLE_HEAPS / USE_REGIONS chain. This is the
// !full_p blocking-mark-phase card scan: each server GC worker scans its own heap's older-than-
// condemned SOH regions plus its LOH and POH regions, marking (through mark_object_simple) the
// cross-generation references recorded in the card table and clearing cards that no longer point
// across generations.
//
// The structure mirrors the workstation translation in MarkPhase.cs (find_card / find_first_object
// / go_through_object over each set card's objects, per-card cross-generation accounting, and
// card clearing), specialized to the per-heap server fields. The BACKGROUND_GC guards consult the
// server sweep/mark-state predicates in ManagedServerGCBackgroundState.cs so an in-progress
// background sweep's mark bits are honored once server BGC lands; with no background GC running
// the predicates report no bgc mark to consult, exactly as native.
//
// FEATURE_CARD_MARKING_STEALING is not defined for this port (as for the rest of the managed
// server build), so the non-stealing per-heap path is translated: no card_marking_enumerator, and
// each worker scans the segments it owns. mark_object_fn is always mark_object_simple and
// relocating is always false here (the mark phase never relocates); the relocate variant of the
// card scan belongs to the deferred server plan/relocate subsystem and is not reachable from this
// slice, so scan_card_reference translates only the mark branch and asserts relocating is false.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    // Per-card walk context threaded through go_through_object to scan_card_reference. cross_-
    // generation_pointers tracks whether the current card still holds a younger-than-parent
    // reference so the caller can clear cards that no longer do.
    private struct card_scan_context
    {
        public gc_heap* heap;
        public byte* card_end;
        public int parent_gen;
        public int condemned_gen;
        public int relocating;
        public nuint cross_generation_pointers;
    }

    // mark_through_cards_helper's per-slot body: promote (mark branch) or relocate (relocate branch)
    // a cross-generation child (into a condemned generation) and count references that still cross
    // into a younger generation than the region being scanned. The relocate branch is enabled by the
    // server relocate slice (ManagedServerGCRelocate.cs): it rewrites the child through
    // relocate_address and re-reads its planned generation number.
    private static void scan_card_reference(byte** slot, void* context_pointer)
    {
        card_scan_context* context = (card_scan_context*)context_pointer;
        if ((byte*)slot >= context->card_end)
        {
            return;
        }

        byte* child = *slot;
        if (child < ephemeral_low || child >= ephemeral_high)
        {
            return;
        }

        int child_gen = get_region_gen_num(child);
        if (child_gen <= context->condemned_gen)
        {
            if (context->relocating != 0)
            {
                relocate_address(slot);
                child_gen = get_region_plan_gen_num(*slot);
            }
            else
            {
                mark_object_simple(context->heap, slot);
            }
        }

        if (child_gen < context->parent_gen)
        {
            context->cross_generation_pointers++;
        }
    }

    public static void mark_through_cards_for_segments(gc_heap* heap, bool relocating)
    {
        int condemned_gen = settings.condemned_generation;
        generation* generation_table = generation_table_of(heap);
        for (int parent_gen = GCInterfaceOffsets.max_generation;
             parent_gen > condemned_gen;
             parent_gen--)
        {
            generation* gen = generation_of(generation_table, parent_gen);
            for (heap_segment* segment = generation.generation_start_segment_rw(gen);
                 segment is not null;
                 segment = heap_segment.heap_segment_next(segment))
            {
                scan_cards_for_segment(
                    heap,
                    segment,
                    parent_gen,
                    condemned_gen,
                    small_object_p: true,
                    relocating);
            }
        }
    }

    public static void mark_through_cards_for_uoh_objects(
        gc_heap* heap,
        int gen_number,
        bool relocating)
    {
        generation* gen = generation_of(generation_table_of(heap), gen_number);
        for (heap_segment* segment = generation.generation_start_segment_rw(gen);
             segment is not null;
             segment = heap_segment.heap_segment_next(segment))
        {
            if (heap_segment.heap_segment_read_only_p(segment) == 0)
            {
                scan_cards_for_segment(
                    heap,
                    segment,
                    GCInterfaceOffsets.max_generation,
                    settings.condemned_generation,
                    small_object_p: false,
                    relocating);
            }
        }
    }

    private static void scan_cards_for_segment(
        gc_heap* heap,
        heap_segment* segment,
        int parent_gen,
        int condemned_gen,
        bool small_object_p,
        bool relocating)
    {
        byte* segment_start = heap_segment.heap_segment_mem(segment);
        byte* segment_end = heap_segment.heap_segment_allocated(segment);
        if (segment_start >= segment_end)
        {
            return;
        }

        nuint first_card = card_of(segment_start);
        nuint last_card = card_of(segment_end - 1);
        nuint card_word_end = card_table_info.card_word(
            card_of(card_table_info.align_on_card_word(segment_end)));
        byte* first_object = segment_start;
        nuint search_card = first_card;
#if BACKGROUND_GC
        should_check_bgc_mark(
            heap,
            segment,
            out bool consider_bgc_mark_p,
            out bool check_current_sweep_p);
#endif
        while (find_card(ref search_card, card_word_end, out nuint end_card))
        {
            if (search_card > last_card)
            {
                break;
            }

            nuint batch_end = end_card <= last_card
                ? end_card
                : last_card + 1;
            for (nuint card = search_card; card < batch_end; card++)
            {
                byte* card_start = card_address(card);
                byte* card_end =
                    card_start + (nint)card_table_info.card_size;
                if (card_start < segment_start)
                {
                    card_start = segment_start;
                }

                if (card_end > segment_end)
                {
                    card_end = segment_end;
                }

                byte* current = small_object_p
                    ? find_first_object(card_start, first_object)
                    : find_uoh_object_for_card(
                        card_start,
                        segment_start,
                        segment_end);
                card_scan_context context = default;
                context.heap = heap;
                context.card_end = card_end;
                context.parent_gen = parent_gen;
                context.condemned_gen = condemned_gen;
                context.relocating = relocating ? 1 : 0;

                while (current < card_end)
                {
                    nuint object_size = size(current);
                    nuint aligned_size = small_object_p
                        ? Align(object_size)
                        : AlignQword(object_size);
                    byte* next = current + (nint)aligned_size;
                    if (next > card_start &&
#if BACKGROUND_GC
                        fgc_should_consider_object(
                            heap,
                            current,
                            segment,
                            consider_bgc_mark_p,
                            check_current_sweep_p) &&
#endif
                        contain_pointers(current) != 0)
                    {
                        go_through_object(
                            method_table(current),
                            current,
                            object_size,
                            &context,
                            &scan_card_reference,
                            card_start,
                            start_useful: 1);
                    }

                    current = next;
                }

                if (context.cross_generation_pointers == 0)
                {
                    clear_cards(card, card + 1);
                }
            }

            if (batch_end <= search_card)
            {
                break;
            }

            search_card = batch_end;
        }
    }

    private static byte* find_uoh_object_for_card(
        byte* card_start,
        byte* segment_start,
        byte* segment_end)
    {
        byte* current = segment_start;
        while (current < segment_end)
        {
            nuint object_size = size(current);
            byte* next = current + (nint)AlignQword(object_size);
            if (next > card_start)
            {
                return current;
            }

            current = next;
        }

        return segment_end;
    }
}

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
