// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Server (SERVER_GC / MULTIPLE_HEAPS / DYNAMIC_HEAP_COUNT / USE_REGIONS) post-collection
// dynamic-data recompute helpers, translated from the SVR compilation of dynamic_tuning.cpp,
// collect.cpp, and gcee.cpp. These mirror the WKS versions in DynamicTuning.cs but read this
// heap's own gc_data_per_heap through get_gc_data_per_heap(hp).

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    // gc.cpp: extern const size_t low_latency_alloc = 256*1024;
    private const nuint LowLatencyAllocation = 256 * 1024;

    // gcpriv.h PER_HEAP_ISOLATED_FIELD_MAINTAINED smoothed_desired_total[total_generation_count].
    // Zero-initialized here; gen 0 is seeded to dynamic_data_of(0)->min_size * n_heaps elsewhere
    // (init.cpp:1759 / interface.cpp:681).
    [InlineArray((int)gc_generation_num.total_generation_count)]
    private struct smoothed_desired_total_array
    {
        private nuint _element0;
    }

    private static smoothed_desired_total_array smoothed_desired_total;

    // dynamic_tuning.cpp surv_to_growth / linear_allocation_model. The WKS copies live in the
    // server-excluded DynamicTuning.cs, so they are re-translated here for the server build.
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

    private static nuint desired_new_allocation(
        gc_heap* hp,
        dynamic_data* dd,
        nuint @out,
        int gen_number,
        int pass)
    {
        gc_history_per_heap* history = get_gc_data_per_heap(hp);
        if (dynamic_data.dd_begin_data_size(dd) == 0)
        {
            nuint minimum = dynamic_data.dd_min_size(dd);
            gc_history_per_heap.gen_data(history, gen_number).new_allocation = minimum;
            return minimum;
        }

        nuint previousDesiredAllocation = dynamic_data.dd_desired_allocation(dd);
        nuint currentSize = dynamic_data.dd_current_size(dd);
        float maxLimit = dynamic_data.dd_max_limit(dd);
        float limit = dynamic_data.dd_limit(dd);
        nuint minGcSize = dynamic_data.dd_min_size(dd);
        nuint maxSize = dynamic_data.dd_max_size(dd);
        float timeSincePreviousCollectionSeconds =
            (dynamic_data.dd_time_clock(dd) -
             dynamic_data.dd_previous_time_clock(dd)) * 1e-6f;
        nint consumedAllocation = unchecked(
            (nint)previousDesiredAllocation -
            dynamic_data.dd_gc_new_allocation(dd));
        float allocationFraction = previousDesiredAllocation == 0
            ? 0
            : (float)consumedAllocation / previousDesiredAllocation;

        float survival;
        float growth;
        nuint newAllocation;
        if (gen_number >= GCInterfaceOffsets.max_generation)
        {
            survival = MathF.Min(
                1.0f,
                (float)@out / dynamic_data.dd_begin_data_size(dd));
            growth = surv_to_growth(survival, limit, maxLimit);
            if (conserve_mem_setting != 0)
            {
                float conserveGrowth =
                    ((10.0f / conserve_mem_setting) - 1) * 0.5f + 1.0f;
                growth = MathF.Min(growth, conserveGrowth);
            }

            nuint maxGrowthSize = unchecked((nuint)(maxSize / growth));
            nuint newSize = currentSize >= maxGrowthSize
                ? maxSize
                : Math.Min(
                    Math.Max(unchecked((nuint)(growth * currentSize)), minGcSize),
                    maxSize);

            if (gen_number == GCInterfaceOffsets.max_generation)
            {
                nuint growthAllocation = newSize >= currentSize
                    ? newSize - currentSize
                    : 0;
                newAllocation = Math.Max(growthAllocation, minGcSize);
                newAllocation = linear_allocation_model(
                    allocationFraction,
                    newAllocation,
                    previousDesiredAllocation,
                    timeSincePreviousCollectionSeconds);

                if (conserve_mem_setting == 0 &&
                    dynamic_data.dd_fragmentation(dd) >
                        unchecked((nuint)((growth - 1) * currentSize)))
                {
                    nuint denominator = unchecked(
                        currentSize + 2 * dynamic_data.dd_fragmentation(dd));
                    nuint reduced = denominator == 0
                        ? minGcSize
                        : unchecked((nuint)(
                            (float)newAllocation * currentSize / denominator));
                    newAllocation = Math.Max(minGcSize, reduced);
                }
            }
            else
            {
                uint memoryLoad = 0;
                ulong availablePhysical = 0;
                get_memory_info(&memoryLoad, &availablePhysical);
                settings.exit_memory_load = memoryLoad;
                if (availablePhysical > 1024 * 1024)
                {
                    availablePhysical -= 1024 * 1024;
                }

                generation* gen =
                    generation_of(generation_table_of(hp), gen_number);
                ulong availableFree = unchecked(
                    availablePhysical +
                    generation.generation_free_list_space(gen));
                if (availableFree < availablePhysical)
                {
                    availableFree = nuint.MaxValue;
                }

                nuint growthAllocation = newSize >= currentSize
                    ? newSize - currentSize
                    : 0;
                nuint baseAllocation = Math.Max(
                    growthAllocation,
                    dynamic_data.dd_desired_allocation(
                        dynamic_data_of(hp, GCInterfaceOffsets.max_generation)));
                baseAllocation = Math.Min(
                    baseAllocation,
                    unchecked((nuint)Math.Min(availableFree, (ulong)nuint.MaxValue)));
                newAllocation = Math.Max(
                    baseAllocation,
                    Math.Max(currentSize / 4, minGcSize));
                newAllocation = linear_allocation_model(
                    allocationFraction,
                    newAllocation,
                    previousDesiredAllocation,
                    timeSincePreviousCollectionSeconds);
            }
        }
        else
        {
            nuint survivors = @out;
            survival = (float)survivors / dynamic_data.dd_begin_data_size(dd);
            growth = surv_to_growth(survival, limit, maxLimit);
            newAllocation = Math.Min(
                Math.Max(unchecked((nuint)(growth * survivors)), minGcSize),
                maxSize);
            newAllocation = linear_allocation_model(
                allocationFraction,
                newAllocation,
                previousDesiredAllocation,
                timeSincePreviousCollectionSeconds);

            if (gen_number == (int)gc_generation_num.soh_gen0)
            {
                if (pass == 0)
                {
                    generation* gen =
                        generation_of(generation_table_of(hp), gen_number);
                    if (generation.generation_free_list_space(gen) > minGcSize)
                    {
                        settings.gen0_reduction_count = 2;
                    }
                    else if (settings.gen0_reduction_count > 0)
                    {
                        settings.gen0_reduction_count--;
                    }
                }

                if (settings.gen0_reduction_count > 0)
                {
                    newAllocation = Math.Min(
                        newAllocation,
                        Math.Max(minGcSize, maxSize / 3));
                }
            }
        }

        nuint alignedAllocation = Align(
            newAllocation,
            get_alignment_constant(
                gen_number <= GCInterfaceOffsets.max_generation));
        gc_history_per_heap.gen_data(history, gen_number).new_allocation =
            alignedAllocation;
        dynamic_data.dd_surv(dd) = survival;
        return alignedAllocation;
    }

#if TARGET_64BIT
    private static nuint trim_youngest_desired(
        uint memory_load,
        nuint total_new_allocation,
        nuint total_min_allocation)
    {
        if (memory_load < card_table_info.MAX_ALLOWED_MEM_LOAD)
        {
            nuint remaining = unchecked((nuint)(
                (card_table_info.MAX_ALLOWED_MEM_LOAD - memory_load) *
                mem_one_percent));
            return Math.Min(total_new_allocation, remaining);
        }

        nuint maximum = Math.Max(
            unchecked((nuint)mem_one_percent),
            total_min_allocation);
        return Math.Min(total_new_allocation, maximum);
    }

    private static nuint joined_youngest_desired(nuint new_allocation)
    {
        nuint finalAllocation = new_allocation;
        if (new_allocation > card_table_info.MIN_YOUNGEST_GEN_DESIRED)
        {
            nuint minimum = card_table_info.MIN_YOUNGEST_GEN_DESIRED;
            if (settings.entry_memory_load >= card_table_info.MAX_ALLOWED_MEM_LOAD ||
                new_allocation > Math.Max(youngest_gen_desired_th, minimum))
            {
                uint memoryLoad = 0;
                get_memory_info(&memoryLoad);
                settings.exit_memory_load = memoryLoad;
                nuint finalTotal = trim_youngest_desired(
                    memoryLoad,
                    new_allocation,
                    minimum);
                nuint maxAllocation = dynamic_data.dd_max_size(
                    dynamic_data_of(
                        g_heaps[0],
                        (int)gc_generation_num.soh_gen0));
                finalAllocation = Math.Min(
                    Align(finalTotal, get_alignment_constant(true)),
                    maxAllocation);
            }
        }

        if (finalAllocation < new_allocation)
        {
            settings.gen0_reduction_count = 2;
        }

        return finalAllocation;
    }
#endif

    private static void trim_youngest_desired_low_memory(dynamic_data* dd)
    {
        if (g_low_memory_status == 0)
        {
            return;
        }

        long keepPercent = GCConfig.GetGCTrimYoungestKeepPercent();
        if (keepPercent is <= 0 or > 100)
        {
            keepPercent = 10;
        }

        nuint candidate = Math.Max(
            Align(
                unchecked((nuint)(current_total_committed / 100.0 * keepPercent)),
                get_alignment_constant(false)),
            dynamic_data.dd_min_size(dd));
        dynamic_data.dd_desired_allocation(dd) = Math.Min(
            dynamic_data.dd_desired_allocation(dd),
            candidate);
    }

    public static nuint compute_in(gc_heap* hp, int gen_number)
    {
        Debug.Assert(gen_number != (int)gc_generation_num.soh_gen0);
        dynamic_data* dd = dynamic_data_of(hp, gen_number);
        generation* gen =
            generation_of(generation_table_of(hp), gen_number);
        nuint incoming = generation.generation_allocation_size(gen);

        dynamic_data.dd_gc_new_allocation(dd) = unchecked(
            dynamic_data.dd_gc_new_allocation(dd) - (nint)incoming);
        dynamic_data.dd_new_allocation(dd) =
            dynamic_data.dd_gc_new_allocation(dd);
        gc_history_per_heap.gen_data(
            get_gc_data_per_heap(hp),
            gen_number).@in = incoming;
        generation.generation_allocation_size(gen) = 0;
        return incoming;
    }

    public static void compute_new_dynamic_data(gc_heap* hp, int gen_number)
    {
        Debug.Assert(gen_number >= 0);
        Debug.Assert(gen_number <= GCInterfaceOffsets.max_generation);

        dynamic_data* dd = dynamic_data_of(hp, gen_number);
        generation* gen =
            generation_of(generation_table_of(hp), gen_number);
        nuint incoming = gen_number == 0 ? 0 : compute_in(hp, gen_number);
        nuint totalGenSize = generation_size(hp, gen_number);
        dynamic_data.dd_fragmentation(dd) = unchecked(
            generation.generation_free_list_space(gen) +
            generation.generation_free_obj_space(gen));
        generation.generation_condemned_allocated(gen) = 0;
        generation.generation_free_list_allocated(gen) = 0;
        generation.generation_end_seg_allocated(gen) = 0;
        dynamic_data.dd_current_size(dd) =
            dynamic_data.dd_fragmentation(dd) <= totalGenSize
                ? totalGenSize - dynamic_data.dd_fragmentation(dd)
                : 0;

        nuint survived = dynamic_data.dd_survived_size(dd);
        gc_history_per_heap* history = get_gc_data_per_heap(hp);
        ref gc_generation_data genData =
            ref gc_history_per_heap.gen_data(history, gen_number);
        genData.size_after = totalGenSize;
        genData.free_list_space_after =
            generation.generation_free_list_space(gen);
        genData.free_obj_space_after =
            generation.generation_free_obj_space(gen);

        if (settings.pause_mode == gc_pause_mode.pause_low_latency &&
            gen_number <= (int)gc_generation_num.soh_gen1)
        {
            dynamic_data.dd_desired_allocation(dd) = LowLatencyAllocation;
            dynamic_data.dd_gc_new_allocation(dd) =
                unchecked((nint)LowLatencyAllocation);
            dynamic_data.dd_new_allocation(dd) =
                dynamic_data.dd_gc_new_allocation(dd);
        }
        else
        {
            if (gen_number == (int)gc_generation_num.soh_gen0)
            {
                nuint finalPromoted = Math.Min(
                    hp->finalization_promoted_bytes,
                    survived);
                dynamic_data.dd_freach_previous_promotion(dd) = finalPromoted;
                nuint lowerBound = desired_new_allocation(
                    hp,
                    dd,
                    survived - finalPromoted,
                    gen_number,
                    pass: 0);
                if (settings.condemned_generation == 0)
                {
                    dynamic_data.dd_desired_allocation(dd) = lowerBound;
                }
                else
                {
                    nuint higherBound = desired_new_allocation(
                        hp,
                        dd,
                        survived,
                        gen_number,
                        pass: 1);
                    if (dynamic_data.dd_desired_allocation(dd) < lowerBound)
                    {
                        dynamic_data.dd_desired_allocation(dd) = lowerBound;
                    }
                    else if (dynamic_data.dd_desired_allocation(dd) > higherBound)
                    {
                        dynamic_data.dd_desired_allocation(dd) = higherBound;
                    }
#if TARGET_64BIT
                    dynamic_data.dd_desired_allocation(dd) =
                        joined_youngest_desired(
                            dynamic_data.dd_desired_allocation(dd));
#endif
                    trim_youngest_desired_low_memory(dd);
                }
            }
            else
            {
                dynamic_data.dd_desired_allocation(dd) =
                    desired_new_allocation(
                        hp,
                        dd,
                        survived,
                        gen_number,
                        pass: 0);
            }

            dynamic_data.dd_gc_new_allocation(dd) =
                unchecked((nint)dynamic_data.dd_desired_allocation(dd));
            dynamic_data.dd_new_allocation(dd) = unchecked(
                dynamic_data.dd_gc_new_allocation(dd) - (nint)incoming);
        }

        genData.pinned_surv = dynamic_data.dd_pinned_survived_size(dd);
        genData.npinned_surv = unchecked(
            dynamic_data.dd_survived_size(dd) -
            dynamic_data.dd_pinned_survived_size(dd));
        dynamic_data.dd_promoted_size(dd) = survived;

        if (gen_number == GCInterfaceOffsets.max_generation)
        {
            for (int i = (int)gc_generation_num.uoh_start_generation;
                 i < (int)gc_generation_num.total_generation_count;
                 i++)
            {
                dd = dynamic_data_of(hp, i);
                totalGenSize = generation_size(hp, i);
                gen = generation_of(generation_table_of(hp), i);
                dynamic_data.dd_fragmentation(dd) = unchecked(
                    generation.generation_free_list_space(gen) +
                    generation.generation_free_obj_space(gen));
                dynamic_data.dd_current_size(dd) =
                    totalGenSize - dynamic_data.dd_fragmentation(dd);
                dynamic_data.dd_survived_size(dd) =
                    dynamic_data.dd_current_size(dd);
                survived = dynamic_data.dd_current_size(dd);
                dynamic_data.dd_desired_allocation(dd) =
                    desired_new_allocation(
                        hp,
                        dd,
                        survived,
                        i,
                        pass: 0);
                dynamic_data.dd_gc_new_allocation(dd) = unchecked(
                    (nint)Align(
                        dynamic_data.dd_desired_allocation(dd),
                        get_alignment_constant(false)));
                dynamic_data.dd_new_allocation(dd) =
                    dynamic_data.dd_gc_new_allocation(dd);

                ref gc_generation_data uohData =
                    ref gc_history_per_heap.gen_data(history, i);
                uohData.size_after = totalGenSize;
                uohData.free_list_space_after =
                    generation.generation_free_list_space(gen);
                uohData.free_obj_space_after =
                    generation.generation_free_obj_space(gen);
                uohData.npinned_surv = survived;
                dynamic_data.dd_promoted_size(dd) = survived;
            }
        }
    }

    public static void update_recorded_gen_data(
        last_recorded_gc_info* gc_info,
        gc_history_per_heap* history)
    {
        recorded_generation_info* recorded =
            (recorded_generation_info*)Unsafe.AsPointer(ref gc_info->gen_info0);
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            ref gc_generation_data data =
                ref gc_history_per_heap.gen_data(history, i);
            recorded[i].size_before = data.size_before;
            recorded[i].fragmentation_before = unchecked(
                data.free_list_space_before + data.free_obj_space_before);
            recorded[i].size_after = data.size_after;
            recorded[i].fragmentation_after = unchecked(
                data.free_list_space_after + data.free_obj_space_after);
        }
    }

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

    public static nuint exponential_smoothing(int gen, nuint collection_count, nuint desired_per_heap)
    {
        // to avoid spikes in mem usage due to short terms fluctuations in survivorship,
        // apply some smoothing.
        nuint smoothing = Math.Min((nuint)3, collection_count);

        nuint desired_total = unchecked(desired_per_heap * (nuint)n_heaps);
        nuint new_smoothed_desired_total = unchecked(
            desired_total / smoothing +
            ((smoothed_desired_total[gen] / smoothing) * (smoothing - 1)));
        smoothed_desired_total[gen] = new_smoothed_desired_total;
        nuint new_smoothed_desired_per_heap = new_smoothed_desired_total / (nuint)n_heaps;

        // make sure we have at least dd_min_size
        gc_heap* hp = g_heaps[0];
        dynamic_data* dd = dynamic_data_of(hp, gen);
        new_smoothed_desired_per_heap = Math.Max(new_smoothed_desired_per_heap, dynamic_data.dd_min_size(dd));

        // align properly
        new_smoothed_desired_per_heap = Align(
            new_smoothed_desired_per_heap,
            get_alignment_constant(gen <= (int)gc_generation_num.soh_gen2));

        return new_smoothed_desired_per_heap;
    }
}
#pragma warning restore CS8981

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
