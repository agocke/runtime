// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from src/coreclr/gc/gcinterface.dac.h.
//
// This is the interface between the GC and the DAC. It is two things: a set of variables whose
// addresses the GC publishes to the DAC through <see cref="GcDacVars"/>, which GC_Initialize
// receives as its fourth argument, and a set of types that are analogues of GC-internal types,
// exposing a subset of their fields while keeping the same layout.
//
// The interface is strictly versioned; see gcinterface.dacvars.def. Fields are only ever added
// at the end of GcDacVars, and a new field is only read by a DAC whose minor version is high
// enough, so an old runtime's DAC keeps working against a newer GC.
//
// The layouts here are pinned by GCInterfaceOffsets.h in the same way as the rest of the GC/EE
// interface: asserted against the C++ header by the native build and against these types by
// GCInterfaceLayout.
//
// Two groups of the header's types are deliberately absent, because they cannot be translated
// before the modules that define their shape are ported:
//
//   * dac_generation and dac_gc_heap are generated from dac_generation_fields.h and
//     dac_gcheap_fields.h, whose field lists name gcpriv.h types.
//   * dac_handle_table and dac_handle_table_segment are sized by the constants of
//     handletableconstants.h, which are part of the handle table.
//
// Nothing populates a GcDacVars yet: PopulateDacVars publishes the addresses of the collector's
// data structures, which do not exist in this GC. The runtime writes the DAC interface version
// it supports into the struct before calling GC_Initialize, and leaving it as it arrived is
// what tells a DAC that this GC has no state it knows how to read.

using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>
    /// The constants of gcinterface.dac.h. They describe the shapes the DAC expects, so they are
    /// as much part of the versioned contract as the structures below.
    /// </summary>
    internal static class GCInterfaceDacConstants
    {
        public const int HEAP_SEGMENT_FLAGS_READONLY = 1;
        public const int NUM_GC_DATA_POINTS = 9;
        public const int MAX_COMPACT_REASONS_COUNT = 11;
        public const int MAX_EXPAND_MECHANISMS_COUNT = 6;
        public const int MAX_GC_MECHANISM_BITS_COUNT = 2;
        public const int MAX_GLOBAL_GC_MECHANISMS_COUNT = 6;
        public const int FREE_REGION_KINDS = 3;

        /// <summary>
        /// The number of generations is hardcoded into the older DAC APIs, which size their
        /// arrays with it. It cannot change, and new APIs use
        /// <c>GcDacVars.total_generation_count</c> instead.
        /// </summary>
        public const int NUMBERGENERATIONS = 4;

        public const int GENERATION_TABLE_FIELD_INDEX = 18;

        public const int build_variant_use_region = 1;
        public const int build_variant_background_gc = 2;
        public const int build_variant_dynamic_heap_count = 4;
    }

    /// <summary>
    /// Possible values of the <c>current_c_gc_state</c> DAC variable, indicating the state of a
    /// background GC.
    /// </summary>
    internal enum c_gc_state
    {
        c_gc_state_marking,
        c_gc_state_planning,
        c_gc_state_free,
    }

    /// <summary>Reasons why an OOM might occur, recorded in <see cref="oom_history"/>.</summary>
    /// <remarks>
    /// If you modify <see cref="failure_get_memory"/> or this enum, make the corresponding
    /// changes in ClrMD.
    /// </remarks>
    internal enum oom_reason
    {
        oom_no_failure = 0,
        oom_budget = 1,
        oom_cant_commit = 2,
        oom_cant_reserve = 3,
        oom_loh = 4,
        oom_low_mem = 5,
        oom_unproductive_full_gc = 6,
    }

    internal enum failure_get_memory
    {
        fgm_no_failure = 0,
        fgm_reserve_segment = 1,
        fgm_commit_segment_beg = 2,
        fgm_commit_eph_segment = 3,
        fgm_grow_table = 4,
        fgm_commit_table = 5,
    }

    /// <summary>
    /// A record of the last OOM that occurred in the GC, with some additional information as to
    /// what triggered it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
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

        /// <summary>The native field is a <c>BOOL</c>, which is a 32-bit integer.</summary>
        public int loh_p;
    }

    /// <summary>
    /// Analogue for the GC <c>heap_segment</c> class, containing information regarding a single
    /// heap segment.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct dac_heap_segment
    {
        public byte* allocated;
        public byte* committed;
        public byte* reserved;
        public byte* used;
        public byte* mem;

        /// <summary>See <see cref="GCInterfaceDacConstants.HEAP_SEGMENT_FLAGS_READONLY"/>.</summary>
        public nuint flags;

        public dac_heap_segment* next;
        public byte* background_allocated;

        /// <summary>
        /// The <c>dac_gc_heap</c> this segment belongs to. It is a pointer rather than the
        /// translated type because dac_gc_heap is generated from the gcpriv.h field list.
        /// </summary>
        public void* heap;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct dac_region_free_list
    {
        public nuint num_free_regions;
        public nuint size_free_regions;
        public nuint size_committed_in_free_regions;
        public nuint num_free_regions_added;
        public nuint num_free_regions_removed;
        public dac_heap_segment* head_free_region;
        public dac_heap_segment* tail_free_region;
    }

    /// <summary>
    /// Analogue for the GC <c>CFinalize</c> class, containing information about the finalize
    /// queue.
    /// </summary>
    /// <remarks>
    /// The native declaration is
    /// <c>uint8_t** m_FillPointers[NUMBERGENERATIONS + ExtraSegCount]</c>. C# fixed-size buffers
    /// only accept primitive element types, so the elements are spelled out; they are contiguous
    /// and <c>m_FillPointers0</c> is at the offset of the native array.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct dac_finalize_queue
    {
        public const int ExtraSegCount = 2;

        public byte** m_FillPointers0;
        public byte** m_FillPointers1;
        public byte** m_FillPointers2;
        public byte** m_FillPointers3;
        public byte** m_FillPointers4;
        public byte** m_FillPointers5;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct dac_handle_table_bucket
    {
        /// <summary>
        /// Points at the <c>dac_handle_table</c> array of the bucket. The element type is not
        /// translated yet because its layout depends on the handle table constants.
        /// </summary>
        public void** pTable;

        public uint HandleTableIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct dac_handle_table_map
    {
        public dac_handle_table_bucket** pBuckets;
        public dac_handle_table_map* pNext;
        public uint dwMaxIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct dac_card_table_info
    {
        public uint recount;
        public nuint size;

        /// <summary>The native field is a <c>TADDR</c>, which is a pointer-sized integer.</summary>
        public nuint next_card_table;
    }

    /// <summary>
    /// Unlike the other DACized structures, the GC heap and generation types are loaded manually
    /// in the debugger. To avoid misuse, pointers to them are explicitly cast to these types.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct unused_gc_heap
    {
        public byte unused;
    }

    /// <inheritdoc cref="unused_gc_heap"/>
    [StructLayout(LayoutKind.Sequential)]
    internal struct unused_generation
    {
        public byte unused;
    }

    /// <summary>
    /// The structure containing the DAC variables, as GC_Initialize receives it. Each field is
    /// either a value the GC reports or the address of one of the GC's globals; the DAC build
    /// declares the same fields as marshalling pointers instead.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct GcDacVars
    {
        public byte major_version_number;
        public byte minor_version_number;
        public nuint generation_size;
        public nuint total_generation_count;
        public byte* build_variant;

        /// <summary>The native variable is a <c>bool</c>, which is one byte.</summary>
        public byte* built_with_svr;

        public nuint** gc_global_mechanisms;
        public unused_generation** generation_table;
        public uint* max_gen;
        public uint** mark_array;
        public c_gc_state* current_c_gc_state;
        public dac_heap_segment** ephemeral_heap_segment;
        public dac_heap_segment** saved_sweep_ephemeral_seg;
        public byte** saved_sweep_ephemeral_start;
        public byte** background_saved_lowest_address;
        public byte** background_saved_highest_address;
        public byte** alloc_allocated;
        public byte** next_sweep_obj;
        public oom_history* oom_info;
        public dac_finalize_queue** finalize_queue;
        public byte*** internal_root_array;
        public nuint* internal_root_array_index;

        /// <summary>The native variable is a <c>BOOL</c>, which is a 32-bit integer.</summary>
        public int* heap_analyze_success;

        public int* n_heaps;
        public unused_gc_heap*** g_heaps;
        public int* gc_structures_invalid_cnt;
        public nuint** interesting_data_per_heap;
        public nuint** compact_reasons_per_heap;
        public nuint** expand_mechanisms_per_heap;
        public nuint** interesting_mechanism_bits_per_heap;
        public dac_handle_table_map* handle_table_map;
        public int** gc_heap_field_offsets;
        public int** generation_field_offsets;
        public byte** bookkeeping_start;
        public dac_region_free_list** global_regions_to_decommit;
        public dac_region_free_list** global_free_huge_regions;
        public dac_region_free_list** free_regions;

        // Here is where v5.2 fields start.
        public dac_heap_segment** freeable_soh_segment;
        public dac_heap_segment** freeable_uoh_segment;
        public int total_bookkeeping_elements;
        public int count_free_region_kinds;
        public nuint card_table_info_size;

        // Here is where v5.4 fields start.
        public int* dynamic_adaptation_mode;

        // Here is where v5.6 fields start.
        public void* gc_descriptor;

        // Here is where v5.8 fields start.
        public uint* g_totalCpuCount;
    }
}
