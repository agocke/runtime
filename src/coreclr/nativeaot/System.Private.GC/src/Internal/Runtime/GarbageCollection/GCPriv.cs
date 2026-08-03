// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from the dependency-free data records of src/coreclr/gc/gcpriv.h.

using System.Diagnostics;
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

    // Dynamic data is maintained per generation. The native class groups its fields into
    // calculated logical data, physical data, and the const data it reads through sdata; it has no
    // constructor, so zero initialization matches the native default. All fields are public in the
    // C++ class, and every native accessor hands out a reference into the instance, so they are
    // translated as static ref-returning helpers taking a dynamic_data* -- mirroring the native
    // reference-return API without introducing a managed reference to collector state.
    [StructLayout(LayoutKind.Sequential)]
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
        // SHORT_PLUGS is defined unconditionally in gcpriv.h.
        public nuint padding_size;
#if TARGET_ARM || TARGET_WASM
        // RESPECT_LARGE_ALIGNMENT || FEATURE_STRUCTALIGN. RESPECT_LARGE_ALIGNMENT tracks the GC's
        // FEATURE_64BIT_ALIGNMENT, which gcenv.object.h defines for TARGET_ARM and TARGET_WASM;
        // FEATURE_STRUCTALIGN is never defined in this codebase.
        public nuint num_npinned_plugs;
#endif
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

        public static ref nuint dd_begin_data_size(dynamic_data* inst) => ref inst->begin_data_size;

        public static ref nuint dd_survived_size(dynamic_data* inst) => ref inst->survived_size;

#if TARGET_ARM || TARGET_WASM
        public static ref nuint dd_num_npinned_plugs(dynamic_data* inst) => ref inst->num_npinned_plugs;
#endif

        public static ref nuint dd_pinned_survived_size(dynamic_data* inst) => ref inst->pinned_survived_size;

        public static ref nuint dd_added_pinned_size(dynamic_data* inst) => ref inst->added_pinned_size;

        public static ref nuint dd_artificial_pinned_survived_size(dynamic_data* inst) => ref inst->artificial_pinned_survived_size;

        public static ref nuint dd_padding_size(dynamic_data* inst) => ref inst->padding_size;

        public static ref nuint dd_current_size(dynamic_data* inst) => ref inst->current_size;

        public static ref float dd_surv(dynamic_data* inst) => ref inst->surv;

        public static ref nuint dd_freach_previous_promotion(dynamic_data* inst) => ref inst->freach_previous_promotion;

        public static ref nuint dd_desired_allocation(dynamic_data* inst) => ref inst->desired_allocation;

        public static ref nuint dd_collection_count(dynamic_data* inst) => ref inst->collection_count;

        public static ref nuint dd_promoted_size(dynamic_data* inst) => ref inst->promoted_size;

        public static ref float dd_limit(dynamic_data* inst) => ref inst->sdata->limit;

        public static ref float dd_max_limit(dynamic_data* inst) => ref inst->sdata->max_limit;

        public static ref nuint dd_max_size(dynamic_data* inst) => ref inst->sdata->max_size;

        public static ref nuint dd_min_size(dynamic_data* inst) => ref inst->min_size;

        public static ref nint dd_new_allocation(dynamic_data* inst) => ref inst->new_allocation;

        public static ref nint dd_gc_new_allocation(dynamic_data* inst) => ref inst->gc_new_allocation;

        public static ref nuint dd_fragmentation_limit(dynamic_data* inst) => ref inst->sdata->fragmentation_limit;

        public static ref float dd_fragmentation_burden_limit(dynamic_data* inst) => ref inst->sdata->fragmentation_burden_limit;

        public static float dd_v_fragmentation_burden_limit(dynamic_data* inst)
        {
            float doubled = 2f * dd_fragmentation_burden_limit(inst);
            return 0.75f < doubled ? 0.75f : doubled;
        }

        public static ref nuint dd_fragmentation(dynamic_data* inst) => ref inst->fragmentation;

        public static ref nuint dd_gc_clock(dynamic_data* inst) => ref inst->gc_clock;

        public static ref ulong dd_time_clock(dynamic_data* inst) => ref inst->time_clock;

        public static ref ulong dd_previous_time_clock(dynamic_data* inst) => ref inst->previous_time_clock;

        public static ref nuint dd_gc_clock_interval(dynamic_data* inst) => ref inst->sdata->gc_clock;

        public static ref ulong dd_time_clock_interval(dynamic_data* inst) => ref inst->sdata->time_clock;

        public static ref nuint dd_gc_elapsed_time(dynamic_data* inst) => ref inst->gc_elapsed_time;
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

#if !TARGET_WASM
    [StructLayout(LayoutKind.Sequential)]
    internal struct etw_bucket_info
    {
        public ushort index;
        public uint count;
        public nuint size;

        public void set(ushort _index, uint _count, nuint _size)
        {
            index = _index;
            count = _count;
            size = _size;
        }
    }
#endif

    // The free-list allocator of gcpriv.h. Its state is entirely private in the C++ class, so the
    // shared offsets table pins only the size and alignment; the managed tests pin the field order
    // and the accessor behavior directly. Every native member function that hands out a reference
    // into the object is a static ref-returning helper taking an allocator*, mirroring the C++
    // reference-return API without introducing a managed reference to collector state.
#pragma warning disable CS8981 // Native type names are intentionally preserved.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct allocator
#pragma warning restore CS8981
    {
        private int first_bucket_bits;
        private uint num_buckets;
        private alloc_list first_bucket;
        private alloc_list* buckets;
        private int gen_number;

        public allocator(uint num_b, int fbb, alloc_list* b, int gen = -1)
        {
            Debug.Assert(num_b < GCInterfaceOffsets.MAX_BUCKET_COUNT);
            num_buckets = num_b;
            first_bucket_bits = fbb;
            first_bucket = default;
            buckets = b;
            gen_number = gen;
        }

        // C# does not run a struct constructor for embedded or unmanaged storage. Keep the native
        // default-construction semantics explicit so generation initialization cannot accidentally
        // leave a zero-bucket allocator behind.
        public static void initialize(allocator* a)
        {
            a->num_buckets = 1;
            a->first_bucket_bits = sizeof(nuint) * 8 - 1;
            a->first_bucket = default;
            a->buckets = null;
            // for young gens we just set it to 0 since we don't treat
            // them differently from each other
            a->gen_number = 0;
        }

        private static alloc_list* alloc_list_of(allocator* a, uint bn)
        {
            Debug.Assert(bn < a->num_buckets);
            if (bn == 0)
                return &a->first_bucket;
            else
                return &a->buckets[bn - 1];
        }

        public static ref nuint alloc_list_damage_count_of(allocator* a, uint bn)
        {
            Debug.Assert(bn < a->num_buckets);
            if (bn == 0)
                return ref alloc_list.alloc_list_damage_count(&a->first_bucket);
            else
                return ref alloc_list.alloc_list_damage_count(&a->buckets[bn - 1]);
        }

        public uint number_of_buckets()
        {
            return num_buckets;
        }

        // skip buckets that cannot possibly fit "size" and return the next one
        // there is always such bucket since the last one fits everything
        public uint first_suitable_bucket(nuint size)
        {
            // sizes taking first_bucket_bits or less are mapped to bucket 0
            // others are mapped to buckets 0, 1, 2 respectively
            size = (size >> first_bucket_bits) | 1;

            uint highest_set_bit_index;
#if TARGET_64BIT
            GCEnv.BitScanReverse64(&highest_set_bit_index, size);
#else
            GCEnv.BitScanReverse(&highest_set_bit_index, (uint)size);
#endif

            return (highest_set_bit_index < num_buckets) ? highest_set_bit_index : (num_buckets - 1);
        }

        public nuint first_bucket_size()
        {
            return (nuint)1 << (first_bucket_bits + 1);
        }

        public static ref byte* alloc_list_head_of(allocator* a, uint bn)
        {
            return ref alloc_list.alloc_list_head(alloc_list_of(a, bn));
        }

        public static ref byte* alloc_list_tail_of(allocator* a, uint bn)
        {
            return ref alloc_list.alloc_list_tail(alloc_list_of(a, bn));
        }

#if TARGET_64BIT && !TARGET_WASM
        public static ref byte* added_alloc_list_head_of(allocator* a, uint bn)
        {
            return ref alloc_list.added_alloc_list_head(alloc_list_of(a, bn));
        }

        public static ref byte* added_alloc_list_tail_of(allocator* a, uint bn)
        {
            return ref alloc_list.added_alloc_list_tail(alloc_list_of(a, bn));
        }
#endif

        public static void clear(allocator* a)
        {
            for (uint i = 0; i < a->num_buckets; i++)
            {
                alloc_list_head_of(a, i) = null;
                alloc_list_tail_of(a, i) = null;
            }
        }

        public int discard_if_no_fit_p()
        {
            return (num_buckets == 1) ? 1 : 0;
        }

#if TARGET_64BIT && !TARGET_WASM
        public bool is_doubly_linked_p()
        {
            return (gen_number == GCInterfaceOffsets.max_generation);
        }
#endif
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
