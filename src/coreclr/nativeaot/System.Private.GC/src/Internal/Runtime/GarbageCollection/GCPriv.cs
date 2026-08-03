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

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct alloc_list
    {
#if TARGET_64BIT && !TARGET_WASM
        private byte* added_head;
        private byte* added_tail;
#endif

        private byte* head;
        private byte* tail;
        private nuint damage_count;

#if TARGET_64BIT && !TARGET_WASM
        public static ref byte* added_alloc_list_head(alloc_list* list) => ref list->added_head;

        public static ref byte* added_alloc_list_tail(alloc_list* list) => ref list->added_tail;
#endif

        public static ref byte* alloc_list_head(alloc_list* list) => ref list->head;

        public static ref byte* alloc_list_tail(alloc_list* list) => ref list->tail;

        public static ref nuint alloc_list_damage_count(alloc_list* list) => ref list->damage_count;
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

    internal enum gc_loh_compaction_mode
    {
        loh_compaction_default = 1,
        loh_compaction_once = 2,
        loh_compaction_auto = 4,
    }

    internal enum set_pause_mode_status
    {
        set_pause_mode_success = 0,
        set_pause_mode_no_gc = 1,
    }

    internal enum gc_latency_level
    {
        latency_level_first = 0,
        latency_level_memory_footprint = latency_level_first,
        latency_level_balanced = 1,
        latency_level_last = latency_level_balanced,
        latency_level_default = latency_level_balanced,
    }

    internal enum gc_tuning_point
    {
        tuning_deciding_condemned_gen = 0,
        tuning_deciding_full_gc = 1,
        tuning_deciding_compaction = 2,
        tuning_deciding_expansion = 3,
        tuning_deciding_promote_ephemeral = 4,
        tuning_deciding_short_on_seg = 5,
    }

    internal enum gc_oh_num
    {
        soh = 0,
        loh = 1,
        poh = 2,
        unknown = -1,
    }

    internal enum memory_type
    {
        memory_type_reserved = 0,
        memory_type_committed = 1,
    }

    internal enum allocation_state
    {
        a_state_start = 0,
        a_state_can_allocate,
        a_state_cant_allocate,
        a_state_retry_allocate,
        a_state_try_fit,
        a_state_try_fit_new_seg,
        a_state_try_fit_after_cg,
        a_state_try_fit_after_bgc,
        a_state_try_free_full_seg_in_bgc,
        a_state_try_free_after_bgc,
        a_state_try_seg_end,
        a_state_acquire_seg,
        a_state_acquire_seg_after_cg,
        a_state_acquire_seg_after_bgc,
        a_state_check_and_wait_for_bgc,
        a_state_trigger_full_compact_gc,
        a_state_trigger_ephemeral_gc,
        a_state_trigger_2nd_ephemeral_gc,
        a_state_check_retry_seg,
        a_state_max,
    }

    internal enum enter_msl_status
    {
        msl_entered,
        msl_retry_different_heap,
    }

    internal enum msl_enter_state
    {
        me_acquire,
        me_release,
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

#pragma warning disable CS8981 // Native type names are intentionally preserved.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct plug
    {
        public byte* skew0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct pair
    {
        public short left;
        public short right;
    }
#pragma warning restore CS8981

    [StructLayout(LayoutKind.Sequential)]
    internal struct plug_and_pair
    {
        public pair m_pair;
        public plug m_plug;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct plug_and_reloc
    {
        public nint reloc;
        public pair m_pair;
        public plug m_plug;
    }

#if TARGET_64BIT
    [StructLayout(LayoutKind.Explicit, Size = 0x20)]
#else
    [StructLayout(LayoutKind.Explicit, Size = 0x10)]
#endif
    internal struct plug_and_gap
    {
        [FieldOffset(0)]
        public nint gap;

#if TARGET_64BIT
        [FieldOffset(0x08)]
#else
        [FieldOffset(0x04)]
#endif
        public nint reloc;

#if TARGET_64BIT
        [FieldOffset(0x10)]
#else
        [FieldOffset(0x08)]
#endif
        public pair m_pair;

#if TARGET_64BIT
        [FieldOffset(0x10)]
#else
        [FieldOffset(0x08)]
#endif
        public int lr;

#if TARGET_64BIT
        [FieldOffset(0x18)]
#else
        [FieldOffset(0x0c)]
#endif
        public plug m_plug;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct gap_reloc_pair
    {
        public nuint gap;
        public nuint reloc;
        public pair m_pair;
    }

#if TARGET_64BIT
    [StructLayout(LayoutKind.Explicit, Size = 0x28)]
#else
    [StructLayout(LayoutKind.Explicit, Size = 0x18)]
#endif
    internal struct aligned_plug_and_gap
    {
        [FieldOffset(0)]
        public nuint additional_pad;

#if !TARGET_64BIT
        // DECLSPEC_ALIGN(8) raises the native struct alignment above that of its 32-bit fields.
        [FieldOffset(0)]
        private ulong _alignment;
#endif

#if TARGET_64BIT
        [FieldOffset(0x08)]
#else
        [FieldOffset(0x04)]
#endif
        public plug_and_gap plugandgap;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct loh_obj_and_pad
    {
        public nint reloc;
        public plug m_plug;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct loh_padding_obj
    {
        public byte* mt;
        public nuint len;
        public nint reloc;
        public plug m_plug;
    }
}
