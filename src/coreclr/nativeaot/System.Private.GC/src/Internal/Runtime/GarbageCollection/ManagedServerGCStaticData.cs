// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Server (SERVER_GC / MULTIPLE_HEAPS / DYNAMIC_HEAP_COUNT / USE_REGIONS) per-heap dynamic-data /
// static-data initialization, translated from the SVR compilation of dynamic_tuning.cpp. The WKS
// copies live in the server-excluded GCAllocation.cs, so the static_data tables and the
// init_dynamic_data / set_static_data closure that points every heap's dynamic_data->sdata at the
// shared static_data are re-translated here. Without this, dd_max_size / dd_fragmentation_limit /
// dd_v_fragmentation_burden_limit (which read through dd->sdata) dereference a null sdata during the
// condemnation tuning of the first collection.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    private const nuint MinGen0Size = 64 * 1024;
    private const nuint MaxGen0Size = 200 * 1024 * 1024;
    private const nuint MinGen1MaxSize = 6 * 1024 * 1024;
    private const nuint MinUohSize = 3 * 1024 * 1024;
    private const nuint MinSegmentSize = 4 * 1024 * 1024;
    private const nuint InitialAlloc = 256 * 1024 * 1024;

    // dynamic_tuning.cpp gc_heap::get_valid_segment_size, translated here because GCAllocation.cs is
    // excluded from the server build. Under USE_REGIONS this sizes the gen0/gen1 budgets.
    private static nuint get_valid_segment_size()
    {
        nuint segment_size = unchecked((nuint)GCConfig.GetSegmentSize());
        if ((segment_size & (1024 * 1024 - 1)) != 0 || (segment_size >> 22) == 0)
        {
            segment_size = ((segment_size >> 1) != 0 && (segment_size >> 22) == 0)
                ? MinSegmentSize
                : InitialAlloc;
        }

        return round_up_power2_local(segment_size);
    }

    private static nuint round_up_power2_local(nuint size)
    {
        uint highest_set_bit_index;
#if TARGET_64BIT
        if (GCEnv.BitScanReverse64(&highest_set_bit_index, (ulong)(size - 1)) == 0)
#else
        if (GCEnv.BitScanReverse(&highest_set_bit_index, (uint)(size - 1)) == 0)
#endif
        {
            return 1;
        }

        return (nuint)2 << (int)highest_set_bit_index;
    }

    // dynamic_tuning.cpp keeps the static_data_table in native static storage. These are individual
    // unmanaged fields rather than a managed array so initialization is explicit and no managed
    // reference enters the collector. They are PER_HEAP_ISOLATED (shared across heaps).
    private static static_data static_data_table_memory_footprint0;
    private static static_data static_data_table_memory_footprint1;
    private static static_data static_data_table_memory_footprint2;
    private static static_data static_data_table_memory_footprint3;
    private static static_data static_data_table_memory_footprint4;
    private static static_data static_data_table_balanced0;
    private static static_data static_data_table_balanced1;
    private static static_data static_data_table_balanced2;
    private static static_data static_data_table_balanced3;
    private static static_data static_data_table_balanced4;
    private static gc_latency_level latency_level;

    private static static_data* static_data_of(gc_latency_level level, int gen_number)
    {
        System.Diagnostics.Debug.Assert(
            gen_number >= 0 && gen_number < (int)gc_generation_num.total_generation_count);

        if (level == gc_latency_level.latency_level_memory_footprint)
        {
            return gen_number switch
            {
                0 => (static_data*)Unsafe.AsPointer(ref static_data_table_memory_footprint0),
                1 => (static_data*)Unsafe.AsPointer(ref static_data_table_memory_footprint1),
                2 => (static_data*)Unsafe.AsPointer(ref static_data_table_memory_footprint2),
                3 => (static_data*)Unsafe.AsPointer(ref static_data_table_memory_footprint3),
                _ => (static_data*)Unsafe.AsPointer(ref static_data_table_memory_footprint4),
            };
        }

        return gen_number switch
        {
            0 => (static_data*)Unsafe.AsPointer(ref static_data_table_balanced0),
            1 => (static_data*)Unsafe.AsPointer(ref static_data_table_balanced1),
            2 => (static_data*)Unsafe.AsPointer(ref static_data_table_balanced2),
            3 => (static_data*)Unsafe.AsPointer(ref static_data_table_balanced3),
            _ => (static_data*)Unsafe.AsPointer(ref static_data_table_balanced4),
        };
    }

    private static void initialize_static_data(
        static_data* sdata,
        nuint min_size,
        nuint max_size,
        nuint fragmentation_limit,
        float fragmentation_burden_limit,
        float limit,
        float max_limit,
        ulong time_clock,
        nuint gc_clock)
    {
        sdata->min_size = min_size;
        sdata->max_size = max_size;
        sdata->fragmentation_limit = fragmentation_limit;
        sdata->fragmentation_burden_limit = fragmentation_burden_limit;
        sdata->limit = limit;
        sdata->max_limit = max_limit;
        sdata->time_clock = time_clock;
        sdata->gc_clock = gc_clock;
    }

    // The literal static_data_table initializer from dynamic_tuning.cpp; init_static_data below
    // supplies the gen0/gen1 sizes the native initializer computes from configuration.
    private static void initialize_static_data_table()
    {
        nuint ssizeTMax = nuint.MaxValue >> 1;

        initialize_static_data(static_data_of(gc_latency_level.latency_level_memory_footprint, 0), 0, 0, 40_000, 0.5f, 9.0f, 20.0f, 1_000_000, 1);
        initialize_static_data(static_data_of(gc_latency_level.latency_level_memory_footprint, 1), 160 * 1024, 0, 80_000, 0.5f, 2.0f, 7.0f, 10_000_000, 10);
        initialize_static_data(static_data_of(gc_latency_level.latency_level_memory_footprint, 2), 256 * 1024, ssizeTMax, 200_000, 0.25f, 1.2f, 1.8f, 100_000_000, 100);
        initialize_static_data(static_data_of(gc_latency_level.latency_level_memory_footprint, 3), MinUohSize, ssizeTMax, 0, 0.0f, 1.25f, 4.5f, 0, 0);
        initialize_static_data(static_data_of(gc_latency_level.latency_level_memory_footprint, 4), MinUohSize, ssizeTMax, 0, 0.0f, 1.25f, 4.5f, 0, 0);

        // Native dynamic_tuning.cpp uses 20.0f/40.0f for the balanced gen0 limit/max_limit under
        // MULTIPLE_HEAPS (9.0f/20.0f only in the WKS build). This table is compiled only for the
        // server build, so it uses the MULTIPLE_HEAPS values.
        initialize_static_data(static_data_of(gc_latency_level.latency_level_balanced, 0), 0, 0, 40_000, 0.5f, 20.0f, 40.0f, 1_000_000, 1);
        initialize_static_data(static_data_of(gc_latency_level.latency_level_balanced, 1), 256 * 1024, 0, 80_000, 0.5f, 2.0f, 7.0f, 10_000_000, 10);
        initialize_static_data(static_data_of(gc_latency_level.latency_level_balanced, 2), 256 * 1024, ssizeTMax, 200_000, 0.25f, 1.2f, 1.8f, 100_000_000, 100);
        initialize_static_data(static_data_of(gc_latency_level.latency_level_balanced, 3), MinUohSize, ssizeTMax, 0, 0.0f, 1.25f, 4.5f, 0, 0);
        initialize_static_data(static_data_of(gc_latency_level.latency_level_balanced, 4), MinUohSize, ssizeTMax, 0, 0.0f, 1.25f, 4.5f, 0, 0);
    }

    private static nuint get_gen0_min_size(nuint soh_segment_size)
    {
        nuint gen0size = unchecked((nuint)GCConfig.GetGen0Size());
        bool is_config_invalid = gen0size == 0 || gen0size < MinGen0Size;
        if (is_config_invalid)
        {
            nuint true_size = GCToOSInterface.GetCacheSizePerLogicalCpu(true);
            gen0size = unchecked(4 * true_size / 5);
            if (gen0size < 256 * 1024)
            {
                gen0size = 256 * 1024;
            }

            if (true_size < 256 * 1024)
            {
                true_size = 256 * 1024;
            }

            ulong total_physical_mem = GCConfig.GetGCTotalPhysicalMemory() != 0
                ? unchecked((ulong)GCConfig.GetGCTotalPhysicalMemory())
                : GCToOSInterface.GetPhysicalMemoryLimit();
            if (total_physical_mem != 0)
            {
                while (gen0size > total_physical_mem / 6)
                {
                    gen0size /= 2;
                    if (gen0size <= true_size)
                    {
                        gen0size = true_size;
                        break;
                    }
                }
            }
        }

        if (gen0size >= soh_segment_size / 2)
        {
            gen0size = soh_segment_size / 2;
        }

        if (is_config_invalid)
        {
            if (heap_hard_limit != 0)
            {
                nuint gen0size_seg = soh_segment_size / 8;
                if (gen0size >= gen0size_seg)
                {
                    gen0size = gen0size_seg;
                }
            }

            gen0size = gen0size / 8 * 5;
        }

        return Align(gen0size);
    }

    // init_static_data from dynamic_tuning.cpp.
    private static void init_static_data()
    {
        nuint soh_segment_size = get_valid_segment_size();
        long configuredGen0Min = GCConfig.GetGen0Size();
        gen0_min_budget_from_config = configuredGen0Min != 0
            ? unchecked((nuint)configuredGen0Min)
            : 0;
        nuint gen0_min_size = get_gen0_min_size(soh_segment_size);
        nuint gen0_max_size;
        nuint gen0_max_size_config = unchecked((nuint)GCConfig.GetGCGen0MaxBudget());
        gen0_max_budget_from_config = gen0_max_size_config;

        if (gen0_max_size_config != 0)
        {
            gen0_max_size = gen0_max_size_config;
        }
        else
        {
            nuint default_max_size = soh_segment_size / 2;
            if (default_max_size > MaxGen0Size)
            {
                default_max_size = MaxGen0Size;
            }

            gen0_max_size = default_max_size > MinGen1MaxSize ? default_max_size : MinGen1MaxSize;

            if (gen0_max_size < gen0_min_size)
            {
                gen0_max_size = gen0_min_size;
            }

            if (heap_hard_limit != 0)
            {
                nuint gen0_max_size_seg = soh_segment_size / 4;
                if (gen0_max_size > gen0_max_size_seg)
                {
                    gen0_max_size = gen0_max_size_seg;
                }
            }
        }

        gen0_max_size = Align(gen0_max_size);
        if (gen0_min_size > gen0_max_size)
        {
            gen0_min_size = gen0_max_size;
        }

        GCConfig.SetGCGen0MaxBudget(unchecked((long)gen0_max_size));

        nuint gen1_max_size = soh_segment_size / 2;
        if (gen1_max_size < MinGen1MaxSize)
        {
            gen1_max_size = MinGen1MaxSize;
        }

        nuint gen1_max_size_config = unchecked((nuint)GCConfig.GetGCGen1MaxBudget());
        if (gen1_max_size_config != 0 && gen1_max_size > gen1_max_size_config)
        {
            gen1_max_size = gen1_max_size_config;
        }

        gen1_max_size = Align(gen1_max_size);

        for (int i = (int)gc_latency_level.latency_level_first;
             i <= (int)gc_latency_level.latency_level_last;
             i++)
        {
            static_data* gen0 = static_data_of((gc_latency_level)i, (int)gc_generation_num.soh_gen0);
            static_data* gen1 = static_data_of((gc_latency_level)i, (int)gc_generation_num.soh_gen1);
            gen0->min_size = gen0_min_size;
            gen0->max_size = gen0_max_size;
            gen1->max_size = gen1_max_size;
        }
    }

    // set_static_data and init_dynamic_data from dynamic_tuning.cpp.
    private static void set_static_data(gc_heap* hp)
    {
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            dynamic_data* dd = dynamic_data_of(hp, i);
            static_data* sdata = static_data_of(latency_level, i);
            dd->sdata = sdata;
            dd->min_size = sdata->min_size;
        }
    }

    public static void init_dynamic_data(gc_heap* hp)
    {
        initialize_static_data_table();
        initialize_concurrent_gc();

        latency_level = gc_latency_level.latency_level_default;
        int latency_level_from_config = unchecked((int)GCConfig.GetLatencyLevel());
        if (latency_level_from_config >= (int)gc_latency_level.latency_level_first &&
            latency_level_from_config <= (int)gc_latency_level.latency_level_last)
        {
            latency_level = (gc_latency_level)latency_level_from_config;
        }

        init_static_data();
        set_static_data(hp);

        ulong now = GCCommon.GetHighPrecisionTimeStamp();
        process_start_time = now;
#if TARGET_64BIT
        youngest_gen_desired_th = unchecked((nuint)mem_one_percent);
#endif
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            dynamic_data* dd = dynamic_data_of(hp, i);
            dd->gc_clock = 0;
            dd->time_clock = now;
            dd->previous_time_clock = now;
            dd->current_size = 0;
            dd->promoted_size = 0;
            dd->collection_count = 0;
            dd->new_allocation = unchecked((nint)dd->min_size);
            dd->gc_new_allocation = dd->new_allocation;
            dd->desired_allocation = unchecked((nuint)dd->new_allocation);
            dd->fragmentation = 0;
        }
    }
}
#pragma warning restore CS8981

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
