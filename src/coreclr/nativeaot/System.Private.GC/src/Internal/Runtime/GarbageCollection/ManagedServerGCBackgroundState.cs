// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Minimal faithful SERVER_GC / MULTIPLE_HEAPS / USE_REGIONS translation of the background-GC
// sweep/mark-state predicates that the foreground cross-generation card scan consults, from the
// SVR compilation of background.cpp. Only the state and helpers the blocking foreground mark
// phase's card scan (mark_through_cards_for_segments / mark_through_cards_for_uoh_objects) reads
// are ported here; the rest of the server background collector (the BGC thread lifecycle,
// concurrent mark/revisit, and region sweep) remains deferred.
//
// gcpriv.h scoping is preserved: current_c_gc_state is PER_HEAP_ISOLATED_FIELD_SINGLE_GC (a single
// shared static observed by every heap during a background sweep), while current_sweep_pos and
// current_sweep_seg are PER_HEAP_FIELD_SINGLE_GC and so instance-owned in the MULTIPLE_HEAPS build.
// mark_array and background_saved_lowest/highest_address are laid out with the shared card-table
// bookkeeping in GCRegionsSegments.cs. Because the server background collector is not yet routed,
// current_c_gc_state is always c_gc_state_free at collection time, so should_check_bgc_mark reports
// "no bgc mark bit to consult" and fgc_should_consider_object returns TRUE, exactly matching native
// when no background GC is in progress; the code paths for an active background sweep are
// translated faithfully so they engage unchanged once server BGC lands.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    // gcpriv.h PER_HEAP_ISOLATED_FIELD_SINGLE_GC VOLATILE(c_gc_state) current_c_gc_state: the
    // shared background sweep/mark phase indicator every heap observes.
    public static volatile c_gc_state current_c_gc_state;

    // background.cpp gc_heap::background_object_marked: TRUE if o is outside the background range or
    // its background mark bit is set; the foreground card scan only ever calls this with
    // clear_p == false.
    private static bool background_object_marked(byte* o, bool clear_p)
    {
        if (o < background_saved_lowest_address ||
            o >= background_saved_highest_address)
        {
            return true;
        }

        if (mark_array_marked(o) == 0)
        {
            return false;
        }

        if (clear_p)
        {
            mark_array_clear_marked(o);
        }

        return true;
    }

    // background.cpp gc_heap::mark_array_clear_marked.
    private static void mark_array_clear_marked(byte* add)
    {
        mark_array[(nint)card_table_info.mark_word_of(add)] &=
            ~(1u << (int)card_table_info.mark_bit_bit_of(add));
    }

    // background.cpp gc_heap::fgc_should_consider_object, USE_REGIONS variant (check_saved_sweep_p
    // is asserted false and dropped). TRUE means the object should be considered by the foreground
    // card scan; FALSE means it is dead per the background mark and must be skipped.
    public static bool fgc_should_consider_object(
        gc_heap* heap,
        byte* o,
        heap_segment* seg,
        bool consider_bgc_mark_p,
        bool check_current_sweep_p)
    {
        // TRUE means we don't need to check the bgc mark bit.
        bool no_bgc_mark_p = false;

        if (consider_bgc_mark_p)
        {
            if (check_current_sweep_p && o < heap->current_sweep_pos)
            {
                no_bgc_mark_p = true;
            }

            if (!no_bgc_mark_p)
            {
                byte* background_allocated =
                    heap_segment.heap_segment_background_allocated(seg);

                // background_allocated could be 0 for the new segments acquired during bgc sweep
                // and we still want no_bgc_mark_p to be true.
                if (o >= background_allocated)
                {
                    no_bgc_mark_p = true;
                }
            }
        }
        else
        {
            no_bgc_mark_p = true;
        }

        return no_bgc_mark_p || background_object_marked(o, clear_p: false);
    }

    // background.cpp gc_heap::should_check_bgc_mark, USE_REGIONS variant (check_saved_sweep_p is
    // dropped). consider_bgc_mark_p tells the card scan whether the bgc mark bit matters at all;
    // check_current_sweep_p tells it whether to also consult the current sweep position.
    public static void should_check_bgc_mark(
        gc_heap* heap,
        heap_segment* seg,
        out bool consider_bgc_mark_p,
        out bool check_current_sweep_p)
    {
        consider_bgc_mark_p = false;
        check_current_sweep_p = false;

        if (current_c_gc_state == c_gc_state.c_gc_state_planning)
        {
            // We compare against current_sweep_pos here because we have yet to turn on the swept
            // flag for the segment, but in_range_for_segment returns FALSE when the address equals
            // reserved.
            if (heap_segment.heap_segment_swept_p(seg) != 0 ||
                heap->current_sweep_pos == heap_segment.heap_segment_reserved(seg))
            {
                return;
            }

            if (heap_segment.heap_segment_background_allocated(seg) is null)
            {
                return;
            }

            consider_bgc_mark_p = true;

            if (heap->current_sweep_pos is not null &&
                in_range_for_segment(heap->current_sweep_pos, seg) != 0)
            {
                check_current_sweep_p = true;
            }
        }
    }
}

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
