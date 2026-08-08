// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from the WKS USE_REGIONS tuning, memory-limit, collection-accounting, and public
// metric paths in dynamic_tuning.cpp, init.cpp, collect.cpp, diagnostics.cpp, and interface.cpp.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection;

internal unsafe partial struct gc_heap
{
    private const nuint LowLatencyAllocation = 256 * 1024;

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

#if USE_REGIONS && !MULTIPLE_HEAPS
    private static gc_history_per_heap* get_gc_data_per_heap()
    {
#if BACKGROUND_GC
        if (settings.concurrent != 0)
        {
            return (gc_history_per_heap*)Unsafe.AsPointer(ref bgc_data_per_heap);
        }
#endif
        return (gc_history_per_heap*)Unsafe.AsPointer(ref gc_data_per_heap);
    }

    public static void get_memory_info(
        uint* memory_load,
        ulong* available_physical = null,
        ulong* available_page_file = null)
    {
        GCToOSInterface.GetMemoryStatus(
            is_restricted_physical_mem != 0 ? total_physical_mem : 0,
            memory_load,
            available_physical,
            available_page_file);
    }

    public static void record_entry_memory_load()
    {
        uint memoryLoad = 0;
        ulong availablePhysical = 0;
        get_memory_info(&memoryLoad, &availablePhysical);

        uint virtualAddressLoad = global_region_allocator.get_va_memory_load();
        if (virtualAddressLoad > memoryLoad)
        {
            memoryLoad = virtualAddressLoad;
        }

        settings.entry_memory_load = memoryLoad;
        settings.entry_available_physical_mem = availablePhysical;
    }

    public static uint get_memory_load()
    {
        if (settings.exit_memory_load != 0)
        {
            return settings.exit_memory_load;
        }

        return settings.entry_memory_load;
    }

    public static ulong get_generation_budget(gc_heap* hp, int generation)
    {
        if (hp is null ||
            (uint)generation >= (uint)gc_generation_num.total_generation_count)
        {
            return 0;
        }

        return dynamic_data.dd_desired_allocation(
            dynamic_data_of(hp, generation));
    }

    public static int get_gc_latency_mode() => (int)settings.pause_mode;

    public static int set_gc_latency_mode(int new_latency_mode)
    {
        if (settings.pause_mode == gc_pause_mode.pause_no_gc)
        {
            return (int)set_pause_mode_status.set_pause_mode_no_gc;
        }

        gc_pause_mode newMode = (gc_pause_mode)new_latency_mode;
        if (newMode == gc_pause_mode.pause_low_latency)
        {
            settings.pause_mode = newMode;
        }
        else if (newMode == gc_pause_mode.pause_sustained_low_latency)
        {
#if BACKGROUND_GC
            if (gc_can_use_concurrent)
            {
                settings.pause_mode = newMode;
            }
#endif
        }
        else
        {
            settings.pause_mode = newMode;
        }

#if BACKGROUND_GC
        if (background_running_p() &&
            saved_bgc_settings.pause_mode != newMode)
        {
            saved_bgc_settings.pause_mode = newMode;
        }
#endif
        return (int)set_pause_mode_status.set_pause_mode_success;
    }

    private static bool compute_hard_limit_from_heap_limits()
    {
        nuint sohLimit = heap_hard_limit_oh[(int)gc_oh_num.soh];
        nuint lohLimit = heap_hard_limit_oh[(int)gc_oh_num.loh];
        nuint pohLimit = heap_hard_limit_oh[(int)gc_oh_num.poh];
        nuint combined = unchecked(sohLimit + lohLimit + pohLimit);
        if (combined < sohLimit || combined < lohLimit || combined < pohLimit)
        {
            return false;
        }

        heap_hard_limit = combined;
        return true;
    }

    private static bool compute_hard_limit()
    {
        heap_hard_limit = unchecked((nuint)GCConfig.GetGCHeapHardLimit());
        heap_hard_limit_oh[(int)gc_oh_num.soh] =
            unchecked((nuint)GCConfig.GetGCHeapHardLimitSOH());
        heap_hard_limit_oh[(int)gc_oh_num.loh] =
            unchecked((nuint)GCConfig.GetGCHeapHardLimitLOH());
        heap_hard_limit_oh[(int)gc_oh_num.poh] =
            unchecked((nuint)GCConfig.GetGCHeapHardLimitPOH());

        nuint sohLimit = heap_hard_limit_oh[(int)gc_oh_num.soh];
        nuint lohLimit = heap_hard_limit_oh[(int)gc_oh_num.loh];
        nuint pohLimit = heap_hard_limit_oh[(int)gc_oh_num.poh];
        if (sohLimit != 0 || lohLimit != 0 || pohLimit != 0)
        {
            if (sohLimit == 0 || lohLimit == 0 || pohLimit == 0 ||
                !compute_hard_limit_from_heap_limits())
            {
                return false;
            }
        }
        else
        {
            uint sohPercent = unchecked((uint)GCConfig.GetGCHeapHardLimitSOHPercent());
            uint lohPercent = unchecked((uint)GCConfig.GetGCHeapHardLimitLOHPercent());
            uint pohPercent = unchecked((uint)GCConfig.GetGCHeapHardLimitPOHPercent());
            if (sohPercent != 0 || lohPercent != 0 || pohPercent != 0)
            {
                if (sohPercent is 0 or >= 100 ||
                    lohPercent is 0 or >= 100 ||
                    pohPercent >= 100 ||
                    unchecked(sohPercent + lohPercent + pohPercent) >= 100)
                {
                    return false;
                }

                heap_hard_limit_oh[(int)gc_oh_num.soh] =
                    unchecked((nuint)(total_physical_mem * sohPercent / 100));
                heap_hard_limit_oh[(int)gc_oh_num.loh] =
                    unchecked((nuint)(total_physical_mem * lohPercent / 100));
                heap_hard_limit_oh[(int)gc_oh_num.poh] =
                    unchecked((nuint)(total_physical_mem * pohPercent / 100));
                if (!compute_hard_limit_from_heap_limits())
                {
                    return false;
                }
            }
        }

        if (heap_hard_limit_oh[(int)gc_oh_num.soh] != 0 &&
            heap_hard_limit_oh[(int)gc_oh_num.poh] == 0)
        {
            return false;
        }

        if (heap_hard_limit == 0)
        {
            uint percent = unchecked((uint)GCConfig.GetGCHeapHardLimitPercent());
            if (percent is > 0 and < 100)
            {
                heap_hard_limit =
                    unchecked((nuint)(total_physical_mem * percent / 100));
            }
        }

        return true;
    }

    private static bool compute_memory_settings(bool is_initialization)
    {
        if (!hard_limit_config_p && is_restricted_physical_mem != 0)
        {
            ulong physicalMemoryForGc = total_physical_mem * 75 / 100;
            heap_hard_limit = unchecked((nuint)Math.Max(
                20UL * 1024 * 1024,
                physicalMemoryForGc));
        }

        if (heap_hard_limit != 0 && heap_hard_limit < current_total_committed)
        {
            return false;
        }

        initialize_compaction_policy(
            total_physical_mem,
            GCToOSInterface.GetTotalProcessorCount());
#if TARGET_64BIT
        youngest_gen_desired_th = unchecked((nuint)mem_one_percent);
#endif
        _ = is_initialization;
        return true;
    }

    public static bool initialize_memory_settings()
    {
        long configuredPhysicalMemory = GCConfig.GetGCTotalPhysicalMemory();
        if (configuredPhysicalMemory != 0)
        {
            total_physical_mem = unchecked((ulong)configuredPhysicalMemory);
            physical_memory_from_config =
                unchecked((nuint)configuredPhysicalMemory);
            is_restricted_physical_mem = 0;
        }
        else
        {
            physical_memory_from_config = 0;
            byte restricted = 0;
            total_physical_mem = GCToOSInterface.GetPhysicalMemoryLimit(&restricted);
            is_restricted_physical_mem = restricted;
        }

        if (total_physical_mem == 0 || !compute_hard_limit())
        {
            return false;
        }

        hard_limit_config_p = heap_hard_limit != 0;
        return compute_memory_settings(is_initialization: true);
    }

    public static int refresh_memory_limit()
    {
        refresh_memory_limit_status status = refresh_memory_limit_status.refresh_success;
        if (GCConfig.GetGCTotalPhysicalMemory() != 0)
        {
            return (int)status;
        }

        GCToEEInterface.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC);

        byte oldRestricted = is_restricted_physical_mem;
        ulong oldTotalPhysicalMemory = total_physical_mem;
        nuint oldHeapHardLimit = heap_hard_limit;
        object_heap_array oldHeapHardLimitByObjectHeap = heap_hard_limit_oh;
        bool oldHardLimitConfig = hard_limit_config_p;

        byte restricted = 0;
        total_physical_mem = GCToOSInterface.GetPhysicalMemoryLimit(&restricted);
        is_restricted_physical_mem = restricted;
        GCConfig.RefreshHeapHardLimitSettings();

        bool succeeded = total_physical_mem != 0 && compute_hard_limit();
        if (!succeeded)
        {
            status = refresh_memory_limit_status.refresh_hard_limit_invalid;
        }

        hard_limit_config_p = heap_hard_limit != 0;
        if (succeeded && !compute_memory_settings(is_initialization: false))
        {
            succeeded = false;
            status = refresh_memory_limit_status.refresh_hard_limit_too_low;
        }

        if (!succeeded)
        {
            is_restricted_physical_mem = oldRestricted;
            total_physical_mem = oldTotalPhysicalMemory;
            heap_hard_limit = oldHeapHardLimit;
            heap_hard_limit_oh = oldHeapHardLimitByObjectHeap;
            hard_limit_config_p = oldHardLimitConfig;
            initialize_compaction_policy(
                total_physical_mem,
                GCToOSInterface.GetTotalProcessorCount());
        }

        GCToEEInterface.RestartEE(1);
        return (int)status;
    }

    private static nuint desired_new_allocation(
        gc_heap* hp,
        dynamic_data* dd,
        nuint @out,
        int gen_number,
        int pass)
    {
        gc_history_per_heap* history = get_gc_data_per_heap();
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
                        ManagedGCRegionBootstrap.Heap,
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
            get_gc_data_per_heap(),
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
        gc_history_per_heap* history = get_gc_data_per_heap();
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
                    finalization_promoted_bytes,
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

    public static void record_gc_info(gc_heap* hp)
    {
        last_recorded_gc_info* info;
#if BACKGROUND_GC
        if (settings.concurrent != 0)
        {
            info = (last_recorded_gc_info*)Unsafe.AsPointer(
                ref background_gc_info(last_background_gc_info_index));
        }
        else
#endif
        {
            ref last_recorded_gc_info selected = ref (
                settings.condemned_generation == GCInterfaceOffsets.max_generation
                    ? ref last_full_blocking_gc_info
                    : ref last_ephemeral_gc_info);
            info = (last_recorded_gc_info*)Unsafe.AsPointer(ref selected);
            *info = default;
            info->index = settings.gc_index;
        }

        info->total_committed = current_total_committed;
        info->promoted = get_total_promoted(hp);
        info->pinned_objects = num_pinned_objects;
        info->finalize_promoted_objects =
            finalize_queue is null ? 0 : finalize_queue->GetPromotedCount();

        if (settings.concurrent == 0)
        {
            dynamic_data* dd =
                dynamic_data_of(hp, settings.condemned_generation);
            ulong gcStart = dynamic_data.dd_time_clock(dd);
            nuint pauseDuration = unchecked((nuint)(end_gc_time - gcStart));
            if (gcStart >= suspended_start_time)
            {
                pauseDuration = unchecked(
                    pauseDuration + (nuint)(gcStart - suspended_start_time));
            }

            info->pause_durations0 = pauseDuration;
            info->pause_durations1 = 0;
            total_suspended_time = unchecked(
                total_suspended_time + pauseDuration);
        }

        ulong totalProcessTime = end_gc_time - process_start_time;
        info->pause_percentage = totalProcessTime == 0
            ? 0
            : (float)((double)total_suspended_time /
                totalProcessTime * 100.0);
        update_recorded_gen_data(info, get_gc_data_per_heap());
        info->heap_size = get_total_heap_size(hp);
        info->fragmentation = get_total_fragmentation(hp);
        info->memory_load = settings.exit_memory_load != 0
            ? settings.exit_memory_load
            : settings.entry_memory_load;
        info->condemned_generation =
            unchecked((byte)settings.condemned_generation);
        info->compaction = settings.compaction != 0 ? (byte)1 : (byte)0;
        info->concurrent = settings.concurrent != 0 ? (byte)1 : (byte)0;
#if BACKGROUND_GC
        is_last_recorded_bgc = settings.concurrent != 0 ? 1 : 0;
#endif
    }

#if BACKGROUND_GC
    public static void add_bgc_pause_duration_0()
    {
        if (settings.concurrent == 0)
        {
            return;
        }

        ulong suspendedEnd = GCCommon.GetHighPrecisionTimeStamp();
        ref last_recorded_gc_info info =
            ref background_gc_info(last_background_gc_info_index);
        nuint pauseDuration = unchecked(
            (nuint)(suspendedEnd - suspended_start_time));
        if (info.index < last_ephemeral_gc_info.index &&
            pauseDuration >= last_ephemeral_gc_info.pause_durations0)
        {
            pauseDuration -= last_ephemeral_gc_info.pause_durations0;
        }

        info.pause_durations0 = pauseDuration;
        total_suspended_time = unchecked(
            total_suspended_time + pauseDuration);
    }

    public static void add_bgc_pause_duration_1()
    {
        ulong suspendedEnd = GCCommon.GetHighPrecisionTimeStamp();
        ref last_recorded_gc_info info =
            ref background_gc_info(last_background_gc_info_index);
        info.pause_durations1 = unchecked(
            (nuint)(suspendedEnd - suspended_start_time));
        total_suspended_time = unchecked(
            total_suspended_time + info.pause_durations1);
    }
#endif
#endif

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

    public static nuint get_total_fragmentation(gc_heap* heap)
    {
        nuint total_fragmentation = 0;
        generation* generationTable = generation_table_of(heap);
        for (int i = 0;
             i < (int)gc_generation_num.total_generation_count;
             i++)
        {
            generation* gen = generation_of(generationTable, i);
            total_fragmentation = unchecked(
                total_fragmentation +
                generation.generation_free_list_space(gen) +
                generation.generation_free_obj_space(gen));
        }

        return total_fragmentation;
    }
#endif
}
