// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from the dependency-free data records of src/coreclr/gc/gcpriv.h.

using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct static_data
    {
        public nuint min_size;
        public nuint max_size;
        public nuint fragmentation_limit;
        public float fragmentation_burden_limit;
        public float limit;
        public float max_limit;
        public ulong time_clock;
        public nuint gc_clock;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct recorded_generation_info
    {
        public nuint size_before;
        public nuint fragmentation_before;
        public nuint size_after;
        public nuint fragmentation_after;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct last_recorded_gc_info
    {
        // Native VOLATILE(size_t); access this through GCEnv's volatile helpers.
        public nuint index;
        public nuint total_committed;
        public nuint promoted;
        public nuint pinned_objects;
        public nuint finalize_promoted_objects;
        public nuint pause_durations0;
        public nuint pause_durations1;
        public float pause_percentage;
        public recorded_generation_info gen_info0;
        public recorded_generation_info gen_info1;
        public recorded_generation_info gen_info2;
        public recorded_generation_info gen_info3;
        public recorded_generation_info gen_info4;
        public nuint heap_size;
        public nuint fragmentation;
        public uint memory_load;
        public byte condemned_generation;
        public byte compaction;
        public byte concurrent;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct etw_opt_info
    {
        public nuint desired_allocation;
        public nuint new_allocation;
        public int gen_number;
    }

    internal enum alloc_wait_reason
    {
        awr_ignored = -1,
        awr_low_memory = 0,
        awr_low_ephemeral = 1,
        awr_gen0_alloc = 2,
        awr_loh_alloc = 3,
        awr_alloc_loh_low_mem = 4,
        awr_loh_oos = 5,
        awr_gen0_oos_bgc = 6,
        awr_loh_oos_bgc = 7,
        awr_fgc_wait_for_bgc = 8,
        awr_get_loh_seg = 9,
        awr_loh_alloc_during_plan = 10,
        awr_uoh_alloc_during_bgc = 11,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct alloc_thread_wait_data
    {
        public int awr;
    }

    internal enum msl_take_state
    {
        mt_get_large_seg = 0,
        mt_bgc_uoh_sweep,
        mt_wait_bgc,
        mt_block_gc,
        mt_clr_mem,
        mt_clr_large_mem,
        mt_t_eph_gc,
        mt_t_full_gc,
        mt_alloc_small,
        mt_alloc_large,
        mt_alloc_small_cant,
        mt_alloc_large_cant,
        mt_try_alloc,
        mt_try_budget,
        mt_try_servo_budget,
        mt_decommit_step,
    }

    internal enum gc_pause_mode
    {
        pause_batch = 0,
        pause_interactive = 1,
        pause_low_latency = 2,
        pause_sustained_low_latency = 3,
        pause_no_gc = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct no_gc_region_info
    {
        public nuint soh_allocation_size;
        public nuint loh_allocation_size;
        public nuint started;
        public nuint num_gcs;
        public nuint num_gcs_induced;
        public start_no_gc_region_status start_status;
        public gc_pause_mode saved_pause_mode;
        public nuint saved_gen0_min_size;
        public nuint saved_gen3_min_size;
        public int minimal_gc_p;
        public nuint soh_withheld_budget;
        public nuint loh_withheld_budget;
        public NoGCRegionCallbackFinalizerWorkItem* callback;
    }

    internal enum interesting_data_point
    {
        idp_pre_short = 0,
        idp_post_short = 1,
        idp_merged_pin = 2,
        idp_converted_pin = 3,
        idp_pre_pin = 4,
        idp_post_pin = 5,
        idp_pre_and_post_pin = 6,
        idp_pre_short_padded = 7,
        idp_post_short_padded = 8,
        max_idp_count,
    }
}
