// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from the WKS accounting and pure scalar tuning helpers in
// src/coreclr/gc/dynamic_tuning.cpp, and collection accounting in collect.cpp.

namespace Internal.Runtime.GarbageCollection;

internal unsafe partial struct gc_heap
{
    public static float surv_to_growth(float cst, float limit, float max_limit)
    {
        if (cst < ((max_limit - limit) / (limit * (max_limit - 1.0f))))
        {
            return (limit - limit * cst) / (1.0f - (cst * limit));
        }

        return max_limit;
    }

    private static nuint linear_allocation_model(
        float allocation_fraction,
        nuint new_allocation,
        nuint previous_desired_allocation,
        float time_since_previous_collection_secs)
    {
        if ((allocation_fraction < 0.95) && (allocation_fraction > 0.0))
        {
            const float DecayTime = 5 * 60.0f;
            float decay_factor = DecayTime <= time_since_previous_collection_secs
                ? 0
                : (DecayTime - time_since_previous_collection_secs) / DecayTime;
            float previous_allocation_factor = (1.0f - allocation_fraction) * decay_factor;
            new_allocation = unchecked((nuint)(
                (1.0 - previous_allocation_factor) * new_allocation +
                previous_allocation_factor * previous_desired_allocation));
        }

        return new_allocation;
    }

#if !MULTIPLE_HEAPS
    public static void update_end_gc_time_per_heap(gc_heap* hp)
    {
        for (int gen_number = 0; gen_number <= settings.condemned_generation; gen_number++)
        {
            dynamic_data* dd = dynamic_data_of(hp, gen_number);
            dynamic_data.dd_gc_elapsed_time(dd) = unchecked((nuint)(end_gc_time - dynamic_data.dd_time_clock(dd)));
        }
    }

    public static void update_end_ngc_time()
    {
        end_gc_time = GCCommon.GetHighPrecisionTimeStamp();
        last_alloc_reset_suspended_end_time = end_gc_time;
    }

    // update counters
    public static void update_collection_counts(gc_heap* hp)
    {
        dynamic_data* dd0 = dynamic_data_of(hp, (int)gc_generation_num.soh_gen0);
        dynamic_data.dd_gc_clock(dd0) += 1;

        ulong now = GCCommon.GetHighPrecisionTimeStamp();

        for (int i = 0; i <= settings.condemned_generation; i++)
        {
            dynamic_data* dd = dynamic_data_of(hp, i);
            dynamic_data.dd_collection_count(dd)++;
            // this is needed by the linear allocation model
            if (i == (int)gc_generation_num.max_generation)
            {
                dynamic_data.dd_collection_count(dynamic_data_of(hp, (int)gc_generation_num.loh_generation))++;
                dynamic_data.dd_collection_count(dynamic_data_of(hp, (int)gc_generation_num.poh_generation))++;
            }

            dynamic_data.dd_gc_clock(dd) = dynamic_data.dd_gc_clock(dd0);
            dynamic_data.dd_previous_time_clock(dd) = dynamic_data.dd_time_clock(dd);
            dynamic_data.dd_time_clock(dd) = now;
        }
    }
#endif

#if USE_REGIONS && !MULTIPLE_HEAPS
    public static nuint get_total_heap_size(gc_heap* heap)
    {
        nuint total_heap_size = 0;
        generation* generationTable = generation_table_of(heap);

        // generation_sizes returns all SOH sizes when passed max_generation.
        for (int i = (int)gc_generation_num.max_generation;
             i < (int)gc_generation_num.total_generation_count;
             i++)
        {
            total_heap_size = unchecked(total_heap_size + generation_sizes(
                heap,
                generation_of(generationTable, i)));
        }

        return total_heap_size;
    }
#endif
}
