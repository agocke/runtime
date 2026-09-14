// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

#pragma warning disable CA1823, CS0169, CS8981

namespace Internal.Runtime.GC
{
    internal enum gc_reason : int
    {
        reason_alloc_soh = 0,
        reason_induced = 1,
        reason_lowmemory = 2,
        reason_empty = 3,
        reason_alloc_loh = 4,
        reason_oos_soh = 5,
        reason_oos_loh = 6,
        reason_induced_noforce = 7,
        reason_gcstress = 8,
        reason_lowmemory_blocking = 9,
        reason_induced_compacting = 10,
        reason_lowmemory_host = 11,
        reason_pm_full_gc = 12,
        reason_lowmemory_host_blocking = 13,
        reason_bgc_tuning_soh = 14,
        reason_bgc_tuning_loh = 15,
        reason_bgc_stepping = 16,
        reason_induced_aggressive = 17,
        reason_max = 18,
    }

    internal enum gc_etw_type : int
    {
        gc_etw_type_ngc = 0,
        gc_etw_type_bgc = 1,
        gc_etw_type_fgc = 2,
    }

    internal enum gc_etw_segment_type : int
    {
        gc_etw_segment_small_object_heap = 0,
        gc_etw_segment_large_object_heap = 1,
        gc_etw_segment_read_only_heap = 2,
        gc_etw_segment_pinned_object_heap = 3,
    }

    internal enum gc_generation_num : int
    {
        soh_gen0 = 0,
        soh_gen1 = 1,
        soh_gen2 = 2,
        max_generation = soh_gen2,
        loh_generation = 3,
        poh_generation = 4,
        uoh_start_generation = loh_generation,
        ephemeral_generation_count = max_generation,
        total_generation_count = poh_generation + 1,
        uoh_generation_count = total_generation_count - uoh_start_generation,
    }

    internal enum bgc_state : int
    {
        bgc_not_in_process = 0,
        bgc_initialized,
        bgc_reset_ww,
        bgc_mark_handles,
        bgc_mark_stack,
        bgc_revisit_soh,
        bgc_revisit_uoh,
        bgc_overflow_soh,
        bgc_overflow_uoh,
        bgc_final_marking,
        bgc_sweep_soh,
        bgc_sweep_uoh,
        bgc_plan_phase,
    }

    internal enum changed_seg_state : int
    {
        seg_deleted,
        seg_added,
    }

    internal enum gc_pause_mode : int
    {
        pause_batch = 0,
        pause_interactive = 1,
        pause_low_latency = 2,
        pause_sustained_low_latency = 3,
        pause_no_gc = 4,
    }

    internal enum gc_loh_compaction_mode : int
    {
        loh_compaction_default = 1,
        loh_compaction_once = 2,
        loh_compaction_auto = 4,
    }

    internal enum set_pause_mode_status : int
    {
        set_pause_mode_success = 0,
        set_pause_mode_no_gc = 1,
    }

    internal enum gc_latency_level : int
    {
        latency_level_first = 0,
        latency_level_memory_footprint = latency_level_first,
        latency_level_balanced = 1,
        latency_level_last = latency_level_balanced,
        latency_level_default = latency_level_balanced,
    }

    internal enum gc_tuning_point : int
    {
        tuning_deciding_condemned_gen = 0,
        tuning_deciding_full_gc = 1,
        tuning_deciding_compaction = 2,
        tuning_deciding_expansion = 3,
        tuning_deciding_promote_ephemeral = 4,
        tuning_deciding_short_on_seg = 5,
    }

    internal enum gc_oh_num : int
    {
        soh = 0,
        loh = 1,
        poh = 2,
        unknown = -1,
    }

    internal enum memory_type : int
    {
        memory_type_reserved = 0,
        memory_type_committed = 1,
    }

    internal enum allocation_state : int
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

    internal enum enter_msl_status : int
    {
        msl_entered,
        msl_retry_different_heap,
    }

    internal enum gc_type : int
    {
        gc_type_compacting = 0,
        gc_type_blocking = 1,
        gc_type_max = 3,
    }

    internal enum alloc_wait_reason : int
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

    internal enum msl_take_state : int
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

    internal enum msl_enter_state : int
    {
        me_acquire,
        me_release,
    }

    internal enum bookkeeping_element : int
    {
        card_table_element,
        brick_table_element,
        seg_mapping_table_element,
        total_bookkeeping_elements,
    }

    internal enum oom_reason : int
    {
        oom_no_failure = 0,
        oom_budget = 1,
        oom_cant_commit = 2,
        oom_cant_reserve = 3,
        oom_loh = 4,
        oom_low_mem = 5,
        oom_unproductive_full_gc = 6,
    }

    internal enum failure_get_memory : int
    {
        fgm_no_failure = 0,
        fgm_reserve_segment = 1,
        fgm_commit_segment_beg = 2,
        fgm_commit_eph_segment = 3,
        fgm_grow_table = 4,
        fgm_commit_table = 5,
    }

    internal enum c_gc_state : int
    {
        c_gc_state_marking,
        c_gc_state_planning,
        c_gc_state_free,
    }

    internal enum start_no_gc_region_status : int
    {
        start_no_gc_success = 0,
        start_no_gc_no_memory = 1,
        start_no_gc_too_large = 2,
        start_no_gc_in_progress = 3,
    }

    internal unsafe struct alloc_context
    {
        public byte* alloc_ptr;
        public byte* alloc_limit;
        public long alloc_bytes;
        public long alloc_bytes_uoh;
        public void* gc_reserved_1;
        public void* gc_reserved_2;
        public int alloc_count;
    }

    internal unsafe struct GCDebugSpinLock
    {
        public int lock_value;
        public Thread* holding_thread;
        public int released_by_gc_p;
    }

    internal unsafe struct GCSpinLock
    {
        public int lock_value;
        public Thread* holding_thread;
        public int released_by_gc_p;
    }

    internal unsafe partial struct EEThreadId
    {
        public nuint m_id;
        public bool m_isValid;
    }

    internal unsafe partial struct CLRCriticalSection
    {
        public fixed ulong m_cs[5];
    }

    internal unsafe partial struct GCEvent
    {
        public void* m_impl;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct mark
    {
        public byte* first;
        public nuint len;
        public gap_reloc_pair saved_pre_plug;
        public gap_reloc_pair saved_pre_plug_reloc;
        public gap_reloc_pair saved_post_plug;
        public gap_reloc_pair saved_post_plug_reloc;
        public byte* saved_pre_plug_info_reloc_start;
        public byte* saved_post_plug_info_start;
        public byte* allocation_context_start_region;
        public int saved_pre_p;
        public int saved_post_p;
        public gap_reloc_pair saved_post_plug_debug;

        public static nuint GetMaxShortBits()
        {
            return (nuint)(sizeof(gap_reloc_pair) / sizeof(byte*));
        }

        public static nuint GetPreShortStartBit()
        {
            return (nuint)(sizeof(int) * 8 - 1) - GetMaxShortBits();
        }

        public bool PreShortP()
        {
            return (saved_pre_p & (1 << (sizeof(int) * 8 - 1))) != 0;
        }

        public void SetPreShort()
        {
            saved_pre_p |= 1 << (sizeof(int) * 8 - 1);
        }

        public void SetPreShortBit(nuint bit)
        {
            saved_pre_p |= 1 << (int)(GetPreShortStartBit() + bit);
        }

        public bool PreShortBitP(nuint bit)
        {
            return (saved_pre_p & (1 << (int)(GetPreShortStartBit() + bit))) != 0;
        }

        public void SetPreShortCollectible()
        {
            saved_pre_p |= 2;
        }

        public bool PreShortCollectibleP()
        {
            return (saved_pre_p & 2) != 0;
        }

        public static nuint GetPostShortStartBit()
        {
            return (nuint)(sizeof(int) * 8 - 1) - GetMaxShortBits();
        }

        public bool PostShortP()
        {
            return (saved_post_p & (1 << (sizeof(int) * 8 - 1))) != 0;
        }

        public void SetPostShort()
        {
            saved_post_p |= 1 << (sizeof(int) * 8 - 1);
        }

        public void SetPostShortBit(nuint bit)
        {
            saved_post_p |= 1 << (int)(GetPostShortStartBit() + bit);
        }

        public bool PostShortBitP(nuint bit)
        {
            return (saved_post_p & (1 << (int)(GetPostShortStartBit() + bit))) != 0;
        }

        public void SetPostShortCollectible()
        {
            saved_post_p |= 2;
        }

        public bool PostShortCollectibleP()
        {
            return (saved_post_p & 2) != 0;
        }

        public bool HasPrePlugInfo()
        {
            return saved_pre_p != 0;
        }

        public bool HasPostPlugInfo()
        {
            return saved_post_p != 0;
        }
    }

    internal unsafe struct CObjectHeader
    {
        private byte _opaque;
    }

    internal unsafe struct sorted_table
    {
        private byte _opaque;
    }

    internal unsafe struct seg_free_spaces
    {
        private byte _opaque;
    }

    internal unsafe struct val_serie_item
    {
        public uint nptrs;
        public uint skip;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal unsafe struct CGCDescSeries
    {
        [FieldOffset(0)]
        public nuint seriessize;

        [FieldOffset(0)]
        public val_serie_item val_serie;

        [FieldOffset(8)]
        public nuint startoffset;
    }

    internal unsafe struct CGCDesc
    {
        private byte _opaque;
    }

    internal unsafe struct fgm_history
    {
        public failure_get_memory fgm;
        public nuint size;
        public nuint available_pagefile_mb;
        public int loh_p;
    }

    internal unsafe struct oom_history
    {
        public oom_reason reason;
        public nuint alloc_size;
        public byte* reserved;
        public byte* allocated;
        public nuint gc_index;
        public failure_get_memory fgm;
        public nuint size;
        public nuint available_pagefile_mb;
        public int loh_p;
    }

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

    internal unsafe struct GcMechanisms
    {
        public nuint gc_index;
        public int condemned_generation;
        public int promotion;
        public int compaction;
        public int loh_compaction;
        public int heap_expansion;
        public uint concurrent;
        public int demotion;
        public int card_bundles;
        public int gen0_reduction_count;
        public int should_lock_elevation;
        public int elevation_locked_count;
        public int elevation_reduced;
        public int minimal_gc;
        public gc_reason reason;
        public gc_pause_mode pause_mode;
        public int found_finalizers;
        public uint entry_memory_load;
        public ulong entry_available_physical_mem;
        public uint exit_memory_load;
    }

    internal unsafe struct gc_mechanisms_store
    {
        public nuint gc_index;
        public bool promotion;
        public bool compaction;
        public bool loh_compaction;
        public bool heap_expansion;
        public bool concurrent;
        public bool demotion;
        public bool card_bundles;
        public bool should_lock_elevation;
        public sbyte condemned_generation;
        public sbyte gen0_reduction_count;
        public sbyte elevation_locked_count;
        public sbyte reason;
        public sbyte pause_mode;
        public bool found_finalizers;
        private sbyte _padding;
        public uint entry_memory_load;
    }

    internal unsafe struct alloc_list
    {
        public byte* head;
        public byte* tail;
        public nuint damage_count;
    }


    internal unsafe struct AllocListArray6
    {
        public alloc_list Item0;
        public alloc_list Item1;
        public alloc_list Item2;
        public alloc_list Item3;
        public alloc_list Item4;
        public alloc_list Item5;
    }

    internal unsafe struct AllocListArray18
    {
        public alloc_list Item0;
        public alloc_list Item1;
        public alloc_list Item2;
        public alloc_list Item3;
        public alloc_list Item4;
        public alloc_list Item5;
        public alloc_list Item6;
        public alloc_list Item7;
        public alloc_list Item8;
        public alloc_list Item9;
        public alloc_list Item10;
        public alloc_list Item11;
        public alloc_list Item12;
        public alloc_list Item13;
        public alloc_list Item14;
        public alloc_list Item15;
        public alloc_list Item16;
        public alloc_list Item17;
    }

    internal unsafe struct AllocListArray11
    {
        public alloc_list Item0;
        public alloc_list Item1;
        public alloc_list Item2;
        public alloc_list Item3;
        public alloc_list Item4;
        public alloc_list Item5;
        public alloc_list Item6;
        public alloc_list Item7;
        public alloc_list Item8;
        public alloc_list Item9;
        public alloc_list Item10;
    }

    internal unsafe struct allocator
    {
        public int first_bucket_bits;
        public uint num_buckets;
        public alloc_list first_bucket;
        public alloc_list* buckets;
        public int gen_number;
    }

    internal unsafe struct generation
    {
        public alloc_context allocation_context;
        public heap_segment* start_segment;
        public byte* allocation_start;
        public heap_segment* allocation_segment;
        public byte* allocation_context_start_region;
        public allocator free_list_allocator;
        public nuint free_list_allocated;
        public nuint end_seg_allocated;
        public nuint condemned_allocated;
        public nuint sweep_allocated;
        public int allocate_end_seg_p;
        public nuint free_list_space;
        public nuint free_obj_space;
        public nuint allocation_size;
        public byte* plan_allocation_start;
        public nuint plan_allocation_start_size;
        public nuint pinned_allocation_compact_size;
        public nuint pinned_allocation_sweep_size;
        public int gen_num;
        public int set_bgc_mark_bit_p;
        public byte* last_free_list_allocated;
    }

    internal unsafe struct static_data
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

    internal unsafe struct StaticDataArray5
    {
        public static_data Item0;
        public static_data Item1;
        public static_data Item2;
        public static_data Item3;
        public static_data Item4;
    }

    internal unsafe struct StaticDataTable2
    {
        public StaticDataArray5 Item0;
        public StaticDataArray5 Item1;
    }

    internal unsafe struct DynamicDataArray5
    {
        public dynamic_data Item0;
        public dynamic_data Item1;
        public dynamic_data Item2;
        public dynamic_data Item3;
        public dynamic_data Item4;
    }

    internal unsafe struct NuintArray23
    {
        public nuint Item0;
        public nuint Item1;
        public nuint Item2;
        public nuint Item3;
        public nuint Item4;
        public nuint Item5;
        public nuint Item6;
        public nuint Item7;
        public nuint Item8;
        public nuint Item9;
        public nuint Item10;
        public nuint Item11;
        public nuint Item12;
        public nuint Item13;
        public nuint Item14;
        public nuint Item15;
        public nuint Item16;
        public nuint Item17;
        public nuint Item18;
        public nuint Item19;
        public nuint Item20;
        public nuint Item21;
        public nuint Item22;
    }

    internal unsafe struct NuintArray5
    {
        public nuint Item0;
        public nuint Item1;
        public nuint Item2;
        public nuint Item3;
        public nuint Item4;
    }

    internal unsafe struct NuintArray3
    {
        public nuint Item0;
        public nuint Item1;
        public nuint Item2;
    }

    internal unsafe struct NuintArray9
    {
        public nuint Item0;
        public nuint Item1;
        public nuint Item2;
        public nuint Item3;
        public nuint Item4;
        public nuint Item5;
        public nuint Item6;
        public nuint Item7;
        public nuint Item8;
    }

    internal unsafe struct NuintArray12
    {
        public nuint Item0;
        public nuint Item1;
        public nuint Item2;
        public nuint Item3;
        public nuint Item4;
        public nuint Item5;
        public nuint Item6;
        public nuint Item7;
        public nuint Item8;
        public nuint Item9;
        public nuint Item10;
        public nuint Item11;
    }

    internal unsafe struct ByteStorage2048
    {
        public fixed byte Items[2048];
    }

    internal unsafe struct ByteStorage4608
    {
        public fixed byte Items[4608];
    }

    internal unsafe struct ByteStorage6144
    {
        public fixed byte Items[6144];
    }

    internal unsafe struct ByteStorage288
    {
        public fixed byte Items[288];
    }

    internal unsafe struct ByteStorage192
    {
        public fixed byte Items[192];
    }

    internal unsafe struct dynamic_data
    {
        public nint new_allocation;
        public nint gc_new_allocation;
        public float surv;
        public nuint desired_allocation;
        public nuint begin_data_size;
        public nuint survived_size;
        public nuint pinned_survived_size;
        public nuint artificial_pinned_survived_size;
        public nuint added_pinned_size;
        public nuint padding_size;
        public nuint current_size;
        public nuint collection_count;
        public nuint promoted_size;
        public nuint freach_previous_promotion;
        public nuint fragmentation;
        public nuint gc_clock;
        public ulong time_clock;
        public ulong previous_time_clock;
        public nuint gc_elapsed_time;
        public nuint min_size;
        public static_data* sdata;
    }

    internal unsafe struct recorded_generation_info
    {
        public nuint size_before;
        public nuint fragmentation_before;
        public nuint size_after;
        public nuint fragmentation_after;
    }

    internal unsafe struct RecordedGenerationInfoArray5
    {
        public recorded_generation_info Item0;
        public recorded_generation_info Item1;
        public recorded_generation_info Item2;
        public recorded_generation_info Item3;
        public recorded_generation_info Item4;
    }

    internal unsafe struct last_recorded_gc_info
    {
        public nuint index;
        public nuint total_committed;
        public nuint promoted;
        public nuint pinned_objects;
        public nuint finalize_promoted_objects;
        public nuint pause_duration_0;
        public nuint pause_duration_1;
        public float pause_percentage;
        public RecordedGenerationInfoArray5 gen_info;
        public nuint heap_size;
        public nuint fragmentation;
        public uint memory_load;
        public byte condemned_generation;
        public bool compaction;
        public bool concurrent;
    }

    internal unsafe struct gc_generation_data
    {
        public nuint size_before;
        public nuint free_list_space_before;
        public nuint free_obj_space_before;
        public nuint size_after;
        public nuint free_list_space_after;
        public nuint free_obj_space_after;
        public nuint @in;
        public nuint pinned_surv;
        public nuint npinned_surv;
        public nuint new_allocation;
    }

    internal unsafe struct maxgen_size_increase
    {
        public nuint free_list_allocated;
        public nuint free_list_rejected;
        public nuint end_seg_allocated;
        public nuint condemned_allocated;
        public nuint pinned_allocated;
        public nuint pinned_allocated_advance;
        public uint running_free_list_efficiency;
    }

    internal unsafe struct gen_to_condemn_tuning
    {
        public uint condemn_reasons_gen;
        public uint condemn_reasons_condition;
    }

    internal unsafe struct gc_history_global
    {
        public nuint final_youngest_desired;
        public uint num_heaps;
        public int condemned_generation;
        public int gen0_reduction_count;
        public gc_reason reason;
        public int pause_mode;
        public uint mem_pressure;
        public uint global_mechanisms_p;
        public gen_to_condemn_tuning gen_to_condemn_reasons;
    }

    internal unsafe struct gc_history_per_heap
    {
        public gc_generation_data Item0;
        public gc_generation_data Item1;
        public gc_generation_data Item2;
        public gc_generation_data Item3;
        public gc_generation_data Item4;
        public maxgen_size_increase maxgen_size_info;
        public gen_to_condemn_tuning gen_to_condemn_reasons;
        public uint mechanisms_0;
        public uint mechanisms_1;
        public uint machanism_bits;
        public uint heap_index;
        public nuint extra_gen0_committed;
    }

    internal unsafe struct gc_history
    {
        public nuint gc_index;
        public bgc_state current_bgc_state;
        public uint gc_time_ms;
        public nuint gc_efficiency;
        public byte* eph_low;
        public byte* gen0_start;
        public byte* eph_high;
        public byte* bgc_highest;
        public byte* bgc_lowest;
        public byte* fgc_highest;
        public byte* fgc_lowest;
        public byte* g_highest;
        public byte* g_lowest;
    }

    internal unsafe struct etw_bucket_info
    {
        public ushort index;
        private ushort _padding;
        public uint count;
        public nuint size;
    }

    internal unsafe struct etw_loh_compact_info
    {
        public uint time_plan;
        public uint time_compact;
        public uint time_relocate;
        private uint _padding;
        public nuint total_refs;
        public nuint zero_refs;
    }

    internal unsafe struct mark_queue_t
    {
        private byte _native_empty;
    }

    internal unsafe struct plug
    {
        public byte* skew;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct pair
    {
        public short left;
        public short right;
    }

    internal unsafe struct plug_and_pair
    {
        public pair m_pair;
        public plug m_plug;
    }

    internal unsafe struct plug_and_reloc
    {
        public nint reloc;
        public pair m_pair;
        public plug m_plug;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal unsafe struct plug_and_gap
    {
        [FieldOffset(0)]
        public nint gap;

        [FieldOffset(8)]
        public nint reloc;

        [FieldOffset(16)]
        public pair m_pair;

        [FieldOffset(16)]
        public int lr;

        [FieldOffset(24)]
        public plug m_plug;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct gap_reloc_pair
    {
        public nuint gap;
        public nuint reloc;
        public pair m_pair;
    }

    internal unsafe struct aligned_plug_and_gap
    {
        public nuint additional_pad;
        public plug_and_gap plugandgap;
    }

    internal unsafe struct loh_obj_and_pad
    {
        public nint reloc;
        public plug m_plug;
    }

    internal unsafe struct loh_padding_obj
    {
        public byte* mt;
        public nuint len;
        public nint reloc;
        public plug m_plug;
    }

    internal unsafe struct heap_segment
    {
        public byte* allocated;
        public byte* committed;
        public byte* reserved;
        public byte* used;
        public byte* mem;
        public nuint flags;
        public heap_segment* next;
        public byte* background_allocated;
        public byte* decommit_target;
        public byte* plan_allocated;
        public byte* saved_allocated;
        public byte* saved_bg_allocated;
        public aligned_plug_and_gap padandplug;
    }

    internal unsafe struct CFinalizeFillPointers
    {
        public Object** Item0;
        public Object** Item1;
        public Object** Item2;
        public Object** Item3;
        public Object** Item4;
        public Object** Item5;
        public Object** Item6;
    }

    internal unsafe struct CFinalize
    {
        public CFinalizeFillPointers m_FillPointers;
        public Object** m_Array;
        public Object** m_EndArray;
        public nuint m_PromotedCount;
        public int lock_value;
        public EEThreadId lockowner_threadid;
    }

    internal unsafe struct GenerationArray5
    {
        public generation Item0;
        public generation Item1;
        public generation Item2;
        public generation Item3;
        public generation Item4;
    }

    internal unsafe struct gc_heap
    {
        private byte _opaque;
    }

    internal unsafe struct GCHeap
    {
        public IGCHeapVtable* Vtable;
    }
}
