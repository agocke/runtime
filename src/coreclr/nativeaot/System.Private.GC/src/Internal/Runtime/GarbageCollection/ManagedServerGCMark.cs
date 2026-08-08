// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Server per-heap mark-phase reconciliation, ported from the SVR-namespace compilation of
// mark_phase.cpp (sync_promoted_bytes, decide_on_promotion_surv) for the
// SERVER_GC -> MULTIPLE_HEAPS -> DYNAMIC_HEAP_COUNT -> USE_REGIONS feature chain. These are the
// cross-heap aggregation steps that run in the joined region of the server mark phase after every
// heap has finished promoting its portion of the roots and cards: sync_promoted_bytes folds every
// heap's per-region survivor counters into the owning region's segment fields (right before
// sort_mark_list starts reusing that storage), and decide_on_promotion_surv scans every heap's
// promoted-byte total against the gen(n) demotion threshold. Both are PER_HEAP_ISOLATED in effect
// (they walk g_heaps) so the port makes them static. The !MULTIPLE_HEAPS, TRACE_GC, and
// FEATURE_STRUCTALIGN branches are excluded exactly as they are for the active configuration. No
// collection entry point is routed by this slice; these deciders are translated so the future
// parallel mark driver can call them.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

using System;

namespace Internal.Runtime.GarbageCollection;

internal unsafe partial struct gc_heap
{
    // We gather all the promoted bytes for a region recorded by all threads into that region's
    // survived for plan phase. sort_mark_list will be called shortly and will start using the same
    // storage that the GC threads used to record promoted bytes.
    public static void sync_promoted_bytes()
    {
        int condemned_gen_number = settings.condemned_generation;
        int highest_gen_number = (condemned_gen_number == (int)gc_generation_num.max_generation)
            ? ((int)gc_generation_num.total_generation_count - 1)
            : settings.condemned_generation;
        int stop_gen_idx = get_stop_generation_index(condemned_gen_number);

        for (int i = 0; i < n_heaps; i++)
        {
            gc_heap* hp = g_heaps[i];

            for (int gen_idx = highest_gen_number; gen_idx >= stop_gen_idx; gen_idx--)
            {
                generation* condemned_gen = generation_of(generation_table_of(hp), gen_idx);
                heap_segment* current_region =
                    heap_segment_rw(generation.generation_start_segment(condemned_gen));

                while (current_region is not null)
                {
                    nuint region_index = get_basic_region_index_for_address(
                        heap_segment.heap_segment_mem(current_region));

                    nuint total_surv = 0;
                    nuint total_old_card_surv = 0;

                    for (int hp_idx = 0; hp_idx < n_heaps; hp_idx++)
                    {
                        total_surv = unchecked(
                            total_surv + g_heaps[hp_idx]->survived_per_region[(nint)region_index]);
                        total_old_card_surv = unchecked(
                            total_old_card_surv +
                            g_heaps[hp_idx]->old_card_survived_per_region[(nint)region_index]);
                    }

                    heap_segment.heap_segment_survived(current_region) = total_surv;
                    heap_segment.heap_segment_old_card_survived(current_region) = (int)total_old_card_surv;

                    current_region = heap_segment.heap_segment_next(current_region);
                }
            }
        }
    }

    public static bool decide_on_promotion_surv(nuint threshold)
    {
        for (int i = 0; i < n_heaps; i++)
        {
            gc_heap* hp = g_heaps[i];

            dynamic_data* dd = dynamic_data_of(
                hp,
                Math.Min(
                    settings.condemned_generation + 1,
                    (int)gc_generation_num.max_generation));
            nuint older_gen_size = unchecked(
                dynamic_data.dd_current_size(dd) +
                ((nuint)dynamic_data.dd_desired_allocation(dd) - (nuint)dynamic_data.dd_new_allocation(dd)));

            nuint promoted = hp->total_promoted_bytes;

            if ((threshold > older_gen_size) || (promoted > threshold))
            {
                return true;
            }
        }

        return false;
    }
}

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
