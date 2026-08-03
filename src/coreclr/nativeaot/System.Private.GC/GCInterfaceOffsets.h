// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// This file pins the layout of the types that are shared across the GC/EE interface, so that the
// C# port of the GC in System.Private.GC stays binary compatible with the C++ definitions in
// src/coreclr/gc/gcinterface.h and src/coreclr/gc/gcinterface.ee.h.
//
// It is consumed twice:
//
//   * GCInterfaceOffsetsVerify.cpp turns each entry into a static_assert against the real C++
//     header, so the native build fails if the C++ layout ever drifts from this table.
//   * GCInterfaceOffsets.cspp turns each entry into a C# constant, which GCInterfaceLayout
//     checks against the managed struct definitions.
//
// The entries come in five kinds:
//
//   * GC_OFFSET pins the offset of one field, GC_SIZEOF the size of one type and GC_ALIGNOF its
//     alignment. Between them they pin every byte of a layout: the offsets pin the internal
//     padding, and the size and alignment pin the trailing padding and how an array or an
//     embedded instance of the type is placed.
//   * GC_CONST pins the value of a C++ macro or unscoped enumerator, whose name is a valid
//     identifier in both languages and is therefore used verbatim as the C# constant name.
//   * GC_VALUE pins the value of a C++ expression that is not a C# identifier -- a scoped
//     enumerator, for instance -- and names the C# constant separately.
//
// Enumerator values are not a memory layout, but they are just as much part of the ABI: they
// cross the GC/EE boundary as arguments and return values, and several of them are also baked
// into System.GC, into the ETW manifest, or into the cDAC contracts.
//
// You must #define PLAT_GC_OFFSET, PLAT_GC_SIZEOF, PLAT_GC_ALIGNOF, PLAT_GC_CONST and
// PLAT_GC_VALUE before you #include this file.
//

#ifdef HOST_64BIT
#define GC_OFFSET(offset32, offset64, cls, member) PLAT_GC_OFFSET(offset64, cls, member)
#define GC_SIZEOF(sizeof32, sizeof64, cls        ) PLAT_GC_SIZEOF(sizeof64, cls)
#define GC_ALIGNOF(align32, align64, cls         ) PLAT_GC_ALIGNOF(align64, cls)
#define GC_CONST(const32, const64, expr)           PLAT_GC_CONST(const64, expr)
#define GC_VALUE(const32, const64, name, expr)     PLAT_GC_VALUE(const64, name, expr)
#else
#define GC_OFFSET(offset32, offset64, cls, member) PLAT_GC_OFFSET(offset32, cls, member)
#define GC_SIZEOF(sizeof32, sizeof64, cls        ) PLAT_GC_SIZEOF(sizeof32, cls)
#define GC_ALIGNOF(align32, align64, cls         ) PLAT_GC_ALIGNOF(align32, cls)
#define GC_CONST(const32, const64, expr)           PLAT_GC_CONST(const32, expr)
#define GC_VALUE(const32, const64, name, expr)     PLAT_GC_VALUE(const32, name, expr)
#endif

// NOTE: the values MUST be in hex notation WITHOUT the 0x prefix.

//        32-bit,64-bit, class, member
GC_OFFSET(     0,     0, gc_alloc_context, alloc_ptr)
GC_OFFSET(     4,     8, gc_alloc_context, alloc_limit)
GC_OFFSET(     8,    10, gc_alloc_context, alloc_bytes)
GC_OFFSET(    10,    18, gc_alloc_context, alloc_bytes_uoh)
GC_OFFSET(    18,    20, gc_alloc_context, gc_reserved_1)
GC_OFFSET(    1c,    28, gc_alloc_context, gc_reserved_2)
GC_OFFSET(    20,    30, gc_alloc_context, alloc_count)
GC_SIZEOF(    28,    38, gc_alloc_context)
GC_ALIGNOF(     8,     8, gc_alloc_context)

GC_OFFSET(     0,     0, segment_info, pvMem)
GC_OFFSET(     4,     8, segment_info, ibFirstObject)
GC_OFFSET(     8,    10, segment_info, ibAllocated)
GC_OFFSET(     c,    18, segment_info, ibCommit)
GC_OFFSET(    10,    20, segment_info, ibReserved)
GC_SIZEOF(    14,    28, segment_info)
GC_ALIGNOF(     4,     8, segment_info)

GC_OFFSET(     0,     0, WriteBarrierParameters, operation)
GC_OFFSET(     4,     4, WriteBarrierParameters, is_runtime_suspended)
GC_OFFSET(     5,     5, WriteBarrierParameters, requires_upper_bounds_check)
GC_OFFSET(     8,     8, WriteBarrierParameters, card_table)
GC_OFFSET(     c,    10, WriteBarrierParameters, card_bundle_table)
GC_OFFSET(    10,    18, WriteBarrierParameters, lowest_address)
GC_OFFSET(    14,    20, WriteBarrierParameters, highest_address)
GC_OFFSET(    18,    28, WriteBarrierParameters, ephemeral_low)
GC_OFFSET(    1c,    30, WriteBarrierParameters, ephemeral_high)
GC_OFFSET(    20,    38, WriteBarrierParameters, write_watch_table)
GC_OFFSET(    24,    40, WriteBarrierParameters, region_to_generation_table)
GC_OFFSET(    28,    48, WriteBarrierParameters, region_shr)
GC_OFFSET(    29,    49, WriteBarrierParameters, region_use_bitwise_write_barrier)
GC_SIZEOF(    2c,    50, WriteBarrierParameters)
GC_ALIGNOF(     4,     8, WriteBarrierParameters)

GC_OFFSET(     0,     0, FinalizerWorkItem, next)
GC_OFFSET(     4,     8, FinalizerWorkItem, callback)
GC_SIZEOF(     8,    10, FinalizerWorkItem)
GC_ALIGNOF(     4,     8, FinalizerWorkItem)

GC_OFFSET(     8,    10, NoGCRegionCallbackFinalizerWorkItem, scheduled)
GC_OFFSET(     9,    11, NoGCRegionCallbackFinalizerWorkItem, abandoned)
GC_SIZEOF(     c,    18, NoGCRegionCallbackFinalizerWorkItem)
GC_ALIGNOF(     4,     8, NoGCRegionCallbackFinalizerWorkItem)

GC_OFFSET(     0,     0, EtwGCSettingsInfo, heap_hard_limit)
GC_OFFSET(     4,     8, EtwGCSettingsInfo, loh_threshold)
GC_OFFSET(     8,    10, EtwGCSettingsInfo, physical_memory_from_config)
GC_OFFSET(     c,    18, EtwGCSettingsInfo, gen0_min_budget_from_config)
GC_OFFSET(    10,    20, EtwGCSettingsInfo, gen0_max_budget_from_config)
GC_OFFSET(    14,    28, EtwGCSettingsInfo, high_mem_percent_from_config)
GC_OFFSET(    18,    2c, EtwGCSettingsInfo, concurrent_gc_p)
GC_OFFSET(    19,    2d, EtwGCSettingsInfo, use_large_pages_p)
GC_OFFSET(    1a,    2e, EtwGCSettingsInfo, use_frozen_segments_p)
GC_OFFSET(    1b,    2f, EtwGCSettingsInfo, hard_limit_config_p)
GC_OFFSET(    1c,    30, EtwGCSettingsInfo, no_affinitize_p)
GC_SIZEOF(    20,    38, EtwGCSettingsInfo)
GC_ALIGNOF(     4,     8, EtwGCSettingsInfo)

GC_OFFSET(     0,     0, StronglyConnectedComponent, Count)
GC_OFFSET(     4,     8, StronglyConnectedComponent, Contexts)
GC_SIZEOF(     8,    10, StronglyConnectedComponent)
GC_ALIGNOF(     4,     8, StronglyConnectedComponent)

GC_OFFSET(     0,     0, ComponentCrossReference, SourceGroupIndex)
GC_OFFSET(     4,     8, ComponentCrossReference, DestinationGroupIndex)
GC_SIZEOF(     8,    10, ComponentCrossReference)
GC_ALIGNOF(     4,     8, ComponentCrossReference)

GC_OFFSET(     0,     0, MarkCrossReferencesArgs, ComponentCount)
GC_OFFSET(     4,     8, MarkCrossReferencesArgs, Components)
GC_OFFSET(     8,    10, MarkCrossReferencesArgs, CrossReferenceCount)
GC_OFFSET(     c,    18, MarkCrossReferencesArgs, CrossReferences)
GC_SIZEOF(    10,    20, MarkCrossReferencesArgs)
GC_ALIGNOF(     4,     8, MarkCrossReferencesArgs)

GC_OFFSET(     0,     0, ScanContext, thread_under_crawl)
GC_OFFSET(     4,     8, ScanContext, thread_number)
GC_OFFSET(     8,     c, ScanContext, thread_count)
GC_OFFSET(     c,    10, ScanContext, stack_limit)
GC_OFFSET(    10,    18, ScanContext, promotion)
GC_OFFSET(    11,    19, ScanContext, concurrent)
GC_OFFSET(    14,    20, ScanContext, _unused1)
GC_OFFSET(    18,    28, ScanContext, pMD)
GC_SIZEOF(    20,    38, ScanContext)
GC_ALIGNOF(     4,     8, ScanContext)

GC_OFFSET(     0,     0, VersionInfo, MajorVersion)
GC_OFFSET(     4,     4, VersionInfo, MinorVersion)
GC_OFFSET(     8,     8, VersionInfo, BuildVersion)
GC_OFFSET(     c,    10, VersionInfo, Name)
GC_SIZEOF(    10,    18, VersionInfo)
GC_ALIGNOF(     4,     8, VersionInfo)

//        32-bit,64-bit, constant symbol
GC_CONST(     5,     5, GC_INTERFACE_MAJOR_VERSION)
GC_CONST(     8,     8, GC_INTERFACE_MINOR_VERSION)
GC_CONST(     4,     4, EE_INTERFACE_MAJOR_VERSION)
GC_CONST( 14C08, 14C08, LARGE_OBJECT_SIZE)
GC_CONST(     c,    18, min_obj_size)
GC_CONST(     c,     c, SOFTWARE_WRITE_WATCH_AddressToTableByteIndexShift)

// The GCEventProvider / GCEventLevel / GCEventKeyword enumerators of gcinterface.h. These are
// not a memory layout, but they are an ABI: they cross the GC/EE boundary in
// IGCHeap::ControlEvents, IGCHeap::ControlPrivateEvents and IGCToCLR::UpdateGCEventStatus, and
// the keyword values come from the ETW manifest. The managed copies are in GCEventEnums.cs.
//         32-bit, 64-bit, constant symbol
GC_CONST(      0,       0, GCEventProvider_Default)
GC_CONST(      1,       1, GCEventProvider_Private)

GC_CONST(      0,       0, GCEventLevel_None)
GC_CONST(      1,       1, GCEventLevel_Fatal)
GC_CONST(      2,       2, GCEventLevel_Error)
GC_CONST(      3,       3, GCEventLevel_Warning)
GC_CONST(      4,       4, GCEventLevel_Information)
GC_CONST(      5,       5, GCEventLevel_Verbose)
GC_CONST(      6,       6, GCEventLevel_Max)
GC_CONST(     ff,      ff, GCEventLevel_LogAlways)

GC_CONST(      0,       0, GCEventKeyword_None)
GC_CONST(      1,       1, GCEventKeyword_GC)
GC_CONST(      1,       1, GCEventKeyword_GCPrivate)
GC_CONST(      2,       2, GCEventKeyword_GCHandle)
GC_CONST(   4000,    4000, GCEventKeyword_GCHandlePrivate)
GC_CONST( 100000,  100000, GCEventKeyword_GCHeapDump)
GC_CONST( 200000,  200000, GCEventKeyword_GCSampledObjectAllocationHigh)
GC_CONST( 400000,  400000, GCEventKeyword_GCHeapSurvivalAndMovement)
GC_CONST( 800000,  800000, GCEventKeyword_ManagedHeapCollect)
GC_CONST(1000000, 1000000, GCEventKeyword_GCHeapAndTypeNames)
GC_CONST(2000000, 2000000, GCEventKeyword_GCSampledObjectAllocationLow)
GC_CONST(3f04003, 3f04003, GCEventKeyword_All)

// -----------------------------------------------------------------------------------------
// The packed handle-table segment schema and per-type cache of handletablepriv.h.
// -----------------------------------------------------------------------------------------

//        32-bit,64-bit, class, member
GC_OFFSET(     0,     0, HandleTable, rgTypeFlags)
GC_OFFSET(    34,    38, HandleTable, pSegmentList)
GC_OFFSET(    38,    40, HandleTable, Lock)
#if defined(_DEBUG) || defined(DEBUG)
GC_OFFSET(    58,    78, HandleTable, uTypeCount)
GC_OFFSET(    5c,    7c, HandleTable, dwCount)
GC_OFFSET(    60,    80, HandleTable, pAsyncScanInfo)
GC_OFFSET(    64,    88, HandleTable, uTableIndex)
GC_OFFSET(    68,    90, HandleTable, rgQuickCache)
GC_OFFSET(    9c,    f8, HandleTable, _DEBUG_iMaxGen)
GC_OFFSET(    a0,   100, HandleTable, _DEBUG_TotalBlocksScanned)
GC_OFFSET(    c8,   128, HandleTable, _DEBUG_TotalBlocksScannedNonTrivially)
GC_OFFSET(    f0,   150, HandleTable, _DEBUG_TotalHandleSlotsScanned)
GC_OFFSET(   118,   178, HandleTable, _DEBUG_TotalHandlesActuallyScanned)
GC_SIZEOF(   140,   1a0, HandleTable)
#else
GC_OFFSET(    50,    68, HandleTable, uTypeCount)
GC_OFFSET(    54,    6c, HandleTable, dwCount)
GC_OFFSET(    58,    70, HandleTable, pAsyncScanInfo)
GC_OFFSET(    5c,    78, HandleTable, uTableIndex)
GC_OFFSET(    60,    80, HandleTable, rgQuickCache)
GC_SIZEOF(    94,    e8, HandleTable)
#endif
GC_ALIGNOF(     4,     8, HandleTable)

GC_OFFSET(     0,     0, _TableSegmentHeader, rgGeneration)
GC_OFFSET(   3c0,   1e0, _TableSegmentHeader, rgAllocation)
GC_OFFSET(   4b0,   258, _TableSegmentHeader, rgFreeMask)
GC_OFFSET(   c30,   618, _TableSegmentHeader, rgBlockType)
GC_OFFSET(   d20,   690, _TableSegmentHeader, rgUserData)
GC_OFFSET(   e10,   708, _TableSegmentHeader, rgLocks)
GC_OFFSET(   f00,   780, _TableSegmentHeader, rgTail)
GC_OFFSET(   f0d,   78d, _TableSegmentHeader, rgHint)
GC_OFFSET(   f1a,   79a, _TableSegmentHeader, rgFreeCount)
GC_OFFSET(   f4e,   7ce, _TableSegmentHeader, pNextSegment)
GC_OFFSET(   f52,   7d6, _TableSegmentHeader, pHandleTable)
GC_OFFSET(   f57,   7df, _TableSegmentHeader, bFreeList)
GC_OFFSET(   f58,   7e0, _TableSegmentHeader, bEmptyLine)
GC_OFFSET(   f59,   7e1, _TableSegmentHeader, bCommitLine)
GC_OFFSET(   f5a,   7e2, _TableSegmentHeader, bDecommitLine)
GC_OFFSET(   f5b,   7e3, _TableSegmentHeader, bSequence)
GC_SIZEOF(   f5c,   7e4, _TableSegmentHeader)
GC_ALIGNOF(     1,     1, _TableSegmentHeader)

GC_OFFSET(   f5c,   7e4, TableSegment, rgUnused)
GC_OFFSET(  1000,  1000, TableSegment, rgValue)
GC_SIZEOF( 10000, 10000, TableSegment)
GC_ALIGNOF(     1,     1, TableSegment)

GC_OFFSET(     0,     0, HandleTypeCache, rgReserveBank)
GC_OFFSET(    fc,   1f8, HandleTypeCache, lReserveIndex)
GC_OFFSET(   100,   200, HandleTypeCache, rgFreeBank)
GC_OFFSET(   1fc,   3f8, HandleTypeCache, lFreeIndex)
GC_SIZEOF(   200,   400, HandleTypeCache)
GC_ALIGNOF(     4,     8, HandleTypeCache)

GC_CONST(       0,       0, HNDF_NORMAL)
GC_CONST(       1,       1, HNDF_EXTRAINFO)

GC_CONST(       0,       0, gen_initial)
GC_CONST(       1,       1, gen_final_per_heap)
GC_CONST(       2,       2, gen_alloc_budget)
GC_CONST(       3,       3, gen_time_tuning)
GC_CONST(       4,       4, gcrg_max)

GC_CONST(       0,       0, gen_induced_fullgc_p)
GC_CONST(       1,       1, gen_expand_fullgc_p)
GC_CONST(       2,       2, gen_high_mem_p)
GC_CONST(       3,       3, gen_very_high_mem_p)
GC_CONST(       4,       4, gen_low_ephemeral_p)
GC_CONST(       5,       5, gen_low_card_p)
GC_CONST(       6,       6, gen_eph_high_frag_p)
GC_CONST(       7,       7, gen_max_high_frag_p)
GC_CONST(       8,       8, gen_max_high_frag_e_p)
GC_CONST(       9,       9, gen_max_high_frag_m_p)
GC_CONST(       a,       a, gen_max_high_frag_vm_p)
GC_CONST(       b,       b, gen_max_gen1)
GC_CONST(       c,       c, gen_before_oom)
GC_CONST(       d,       d, gen_gen2_too_small)
GC_CONST(       e,       e, gen_induced_noforce_p)
GC_CONST(       f,       f, gen_before_bgc)
GC_CONST(      10,      10, gen_almost_max_alloc)
GC_CONST(      11,      11, gen_joined_avoid_unproductive)
GC_CONST(      12,      12, gen_joined_pm_induced_fullgc_p)
GC_CONST(      13,      13, gen_joined_pm_alloc_loh)
GC_CONST(      14,      14, gen_joined_gen1_in_pm)
GC_CONST(      15,      15, gen_joined_limit_before_oom)
GC_CONST(      16,      16, gen_joined_limit_loh_frag)
GC_CONST(      17,      17, gen_joined_limit_loh_reclaim)
GC_CONST(      18,      18, gen_joined_servo_initial)
GC_CONST(      19,      19, gen_joined_servo_ngc)
GC_CONST(      1a,      1a, gen_joined_servo_bgc)
GC_CONST(      1b,      1b, gen_joined_servo_postpone)
GC_CONST(      1c,      1c, gen_joined_stress_mix)
GC_CONST(      1d,      1d, gen_joined_stress)
GC_CONST(      1e,      1e, gen_joined_aggressive)
GC_CONST(      1f,      1f, gcrc_max)

GC_CONST(       0,       0, reason_alloc_soh)
GC_CONST(       1,       1, reason_induced)
GC_CONST(       2,       2, reason_lowmemory)
GC_CONST(       3,       3, reason_empty)
GC_CONST(       4,       4, reason_alloc_loh)
GC_CONST(       5,       5, reason_oos_soh)
GC_CONST(       6,       6, reason_oos_loh)
GC_CONST(       7,       7, reason_induced_noforce)
GC_CONST(       8,       8, reason_gcstress)
GC_CONST(       9,       9, reason_lowmemory_blocking)
GC_CONST(       a,       a, reason_induced_compacting)
GC_CONST(       b,       b, reason_lowmemory_host)
GC_CONST(       c,       c, reason_pm_full_gc)
GC_CONST(       d,       d, reason_lowmemory_host_blocking)
GC_CONST(       e,       e, reason_bgc_tuning_soh)
GC_CONST(       f,       f, reason_bgc_tuning_loh)
GC_CONST(      10,      10, reason_bgc_stepping)
GC_CONST(      11,      11, reason_induced_aggressive)
GC_CONST(      12,      12, reason_max)

GC_OFFSET(       0,       0, gc_generation_data, size_before)
GC_OFFSET(       4,       8, gc_generation_data, free_list_space_before)
GC_OFFSET(       8,      10, gc_generation_data, free_obj_space_before)
GC_OFFSET(       c,      18, gc_generation_data, size_after)
GC_OFFSET(      10,      20, gc_generation_data, free_list_space_after)
GC_OFFSET(      14,      28, gc_generation_data, free_obj_space_after)
GC_OFFSET(      18,      30, gc_generation_data, in)
GC_OFFSET(      1c,      38, gc_generation_data, pinned_surv)
GC_OFFSET(      20,      40, gc_generation_data, npinned_surv)
GC_OFFSET(      24,      48, gc_generation_data, new_allocation)
GC_SIZEOF(      28,      50, gc_generation_data)
GC_ALIGNOF(      4,       8, gc_generation_data)

GC_OFFSET(       0,       0, maxgen_size_increase, free_list_allocated)
GC_OFFSET(       4,       8, maxgen_size_increase, free_list_rejected)
GC_OFFSET(       8,      10, maxgen_size_increase, end_seg_allocated)
GC_OFFSET(       c,      18, maxgen_size_increase, condemned_allocated)
GC_OFFSET(      10,      20, maxgen_size_increase, pinned_allocated)
GC_OFFSET(      14,      28, maxgen_size_increase, pinned_allocated_advance)
GC_OFFSET(      18,      30, maxgen_size_increase, running_free_list_efficiency)
GC_SIZEOF(      1c,      38, maxgen_size_increase)
GC_ALIGNOF(      4,       8, maxgen_size_increase)

GC_OFFSET(       0,       0, val_serie_item, nptrs)
GC_OFFSET(       2,       4, val_serie_item, skip)
GC_SIZEOF(        4,       8, val_serie_item)
GC_ALIGNOF(       2,       4, val_serie_item)

GC_OFFSET(       0,       0, CGCDescSeries, seriessize)
GC_OFFSET(       0,       0, CGCDescSeries, val_serie)
GC_OFFSET(       4,       8, CGCDescSeries, startoffset)
GC_SIZEOF(        8,      10, CGCDescSeries)
GC_ALIGNOF(       4,       8, CGCDescSeries)

GC_CONST(       0,       0, expand_reuse_normal)
GC_CONST(       1,       1, expand_reuse_bestfit)
GC_CONST(       2,       2, expand_new_seg_ep)
GC_CONST(       3,       3, expand_new_seg)
GC_CONST(       4,       4, expand_no_memory)
GC_CONST(       5,       5, expand_next_full_gc)
GC_CONST(       6,       6, max_expand_mechanisms_count)

GC_CONST(       0,       0, compact_low_ephemeral)
GC_CONST(       1,       1, compact_high_frag)
GC_CONST(       2,       2, compact_no_gaps)
GC_CONST(       3,       3, compact_loh_forced)
GC_CONST(       4,       4, compact_last_gc)
GC_CONST(       5,       5, compact_induced_compacting)
GC_CONST(       6,       6, compact_fragmented_gen0)
GC_CONST(       7,       7, compact_high_mem_load)
GC_CONST(       8,       8, compact_high_mem_frag)
GC_CONST(       9,       9, compact_vhigh_mem_frag)
GC_CONST(       a,       a, compact_no_gc_mode)
GC_CONST(       b,       b, compact_aggressive_compacting)
GC_CONST(       c,       c, max_compact_reasons_count)

GC_CONST(       0,       0, gc_heap_expand)
GC_CONST(       1,       1, gc_heap_compact)
GC_CONST(       2,       2, max_mechanism_per_heap)

GC_CONST(       0,       0, gc_mark_list_bit)
GC_CONST(       1,       1, gc_demotion_bit)
GC_CONST(       2,       2, max_gc_mechanism_bits_count)

GC_OFFSET(       0,       0, gc_history_per_heap, gen_data)
GC_OFFSET(      c8,     190, gc_history_per_heap, maxgen_size_info)
GC_OFFSET(      e4,     1c8, gc_history_per_heap, gen_to_condemn_reasons)
GC_OFFSET(      ec,     1d0, gc_history_per_heap, mechanisms)
GC_OFFSET(      f4,     1d8, gc_history_per_heap, machanism_bits)
GC_OFFSET(      f8,     1dc, gc_history_per_heap, heap_index)
GC_OFFSET(      fc,     1e0, gc_history_per_heap, extra_gen0_committed)
GC_SIZEOF(     100,     1e8, gc_history_per_heap)
GC_ALIGNOF(      4,       8, gc_history_per_heap)

GC_CONST(       0,       0, global_concurrent)
GC_CONST(       1,       1, global_compaction)
GC_CONST(       2,       2, global_promotion)
GC_CONST(       3,       3, global_demotion)
GC_CONST(       4,       4, global_card_bundles)
GC_CONST(       5,       5, global_elevation)
GC_CONST(       6,       6, max_global_mechanisms_count)

GC_OFFSET(       0,       0, gc_history_global, final_youngest_desired)
GC_OFFSET(       4,       8, gc_history_global, num_heaps)
GC_OFFSET(       8,       c, gc_history_global, condemned_generation)
GC_OFFSET(       c,      10, gc_history_global, gen0_reduction_count)
GC_OFFSET(      10,      14, gc_history_global, reason)
GC_OFFSET(      14,      18, gc_history_global, pause_mode)
GC_OFFSET(      18,      1c, gc_history_global, mem_pressure)
GC_OFFSET(      1c,      20, gc_history_global, global_mechanisms_p)
GC_OFFSET(      20,      24, gc_history_global, gen_to_condemn_reasons)
GC_SIZEOF(      28,      30, gc_history_global)
GC_ALIGNOF(      4,       8, gc_history_global)

// -----------------------------------------------------------------------------------------
// Dependency-free leaf records from gcpriv.h. These do not yet make the collector functional,
// but pin the schema used by dynamic tuning, diagnostics, and the later generation structures.
// -----------------------------------------------------------------------------------------

// The mark record of gcinternal.h. SHORT_PLUGS is unconditionally defined in gcpriv.h, so
// allocation_context_start_region is always present. COLLECTIBLE_CLASS changes only methods, not
// this layout. The DEBUG-only saved_post_plug_debug is retained because native DEBUG builds use it
// to detect corruption of post-plug information.
GC_OFFSET(       0,       0, mark, first)
GC_OFFSET(       4,       8, mark, len)
GC_OFFSET(       8,      10, mark, saved_pre_plug)
GC_OFFSET(      14,      28, mark, saved_pre_plug_reloc)
GC_OFFSET(      20,      40, mark, saved_post_plug)
GC_OFFSET(      2c,      58, mark, saved_post_plug_reloc)
GC_OFFSET(      38,      70, mark, saved_pre_plug_info_reloc_start)
GC_OFFSET(      3c,      78, mark, saved_post_plug_info_start)
GC_OFFSET(      40,      80, mark, allocation_context_start_region)
GC_OFFSET(      44,      88, mark, saved_pre_p)
GC_OFFSET(      48,      8c, mark, saved_post_p)
#if defined(_DEBUG) || defined(DEBUG)
GC_OFFSET(      4c,      90, mark, saved_post_plug_debug)
GC_SIZEOF(      58,      a8, mark)
#else
GC_SIZEOF(      4c,      90, mark)
#endif
GC_ALIGNOF(      4,       8, mark)

// The first three fields are the DAC-visible dac_card_table_info prefix. CARD_BUNDLE is
// unconditional in gcpriv.h, while BACKGROUND_GC is present on every non-WASM full runtime.
GC_OFFSET(       0,       0, card_table_info, recount)
GC_OFFSET(       4,       8, card_table_info, size)
GC_OFFSET(       8,      10, card_table_info, next_card_table)
GC_OFFSET(       c,      18, card_table_info, lowest_address)
GC_OFFSET(      10,      20, card_table_info, highest_address)
GC_OFFSET(      14,      28, card_table_info, brick_table)
GC_OFFSET(      18,      30, card_table_info, card_bundle_table)
#ifdef BACKGROUND_GC
GC_OFFSET(      1c,      38, card_table_info, mark_array)
GC_SIZEOF(      20,      40, card_table_info)
#else
GC_SIZEOF(      1c,      38, card_table_info)
#endif
GC_ALIGNOF(      4,       8, card_table_info)
GC_CONST(     800,    1000, brick_size)
GC_CONST(    1000,    1000, GC_PAGE_SIZE)
GC_CONST(      20,      20, card_word_width)
GC_CONST(      80,     100, card_size)
GC_CONST(      20,      20, card_bundle_word_width)
GC_CONST(      20,      20, card_bundle_size)
#ifdef BACKGROUND_GC
GC_CONST(       8,      10, mark_bit_pitch)
GC_CONST(      20,      20, mark_word_width)
GC_CONST(     100,     200, mark_word_size)
#endif
GC_CONST( 2800000, 2800000, SH_TH_CARD_BUNDLE)
GC_CONST( b400000, b400000, MH_TH_CARD_BUNDLE)
GC_CONST(      64,      64, DECOMMIT_TIME_STEP_MILLISECONDS)
GC_CONST(    8000,    8000, MAX_YP_SPIN_COUNT_UNIT)
GC_CONST(     190,     190, MIN_SOH_CROSS_GEN_REFS)
GC_CONST(     320,     320, MIN_LOH_CROSS_GEN_REFS)
GC_CONST(      80,     400, MARK_STACK_INITIAL_LENGTH)
#ifdef HOST_64BIT
GC_CONST(      55,      55, MAX_ALLOWED_MEM_LOAD)
GC_CONST( 1000000, 1000000, MIN_YOUNGEST_GEN_DESIRED)
#endif

GC_OFFSET(       0,       0, static_data, min_size)
GC_OFFSET(       4,       8, static_data, max_size)
GC_OFFSET(       8,      10, static_data, fragmentation_limit)
GC_OFFSET(       c,      18, static_data, fragmentation_burden_limit)
GC_OFFSET(      10,      1c, static_data, limit)
GC_OFFSET(      14,      20, static_data, max_limit)
GC_OFFSET(      18,      28, static_data, time_clock)
GC_OFFSET(      20,      30, static_data, gc_clock)
GC_SIZEOF(      28,      38, static_data)
GC_ALIGNOF(      8,       8, static_data)

// dynamic_data has no native constructor, so zero initialization matches the native default, and
// all of its fields are public. padding_size is present because SHORT_PLUGS is defined
// unconditionally in gcpriv.h. num_npinned_plugs is gated on RESPECT_LARGE_ALIGNMENT ||
// FEATURE_STRUCTALIGN; RESPECT_LARGE_ALIGNMENT tracks the GC's FEATURE_64BIT_ALIGNMENT (defined
// for TARGET_ARM and TARGET_WASM) and FEATURE_STRUCTALIGN is never defined here, so the field and
// every field after it shift by one pointer only in a FEATURE_64BIT_ALIGNMENT build.
GC_OFFSET(       0,       0, dynamic_data, new_allocation)
GC_OFFSET(       4,       8, dynamic_data, gc_new_allocation)
GC_OFFSET(       8,      10, dynamic_data, surv)
GC_OFFSET(       c,      18, dynamic_data, desired_allocation)
GC_OFFSET(      10,      20, dynamic_data, begin_data_size)
GC_OFFSET(      14,      28, dynamic_data, survived_size)
GC_OFFSET(      18,      30, dynamic_data, pinned_survived_size)
GC_OFFSET(      1c,      38, dynamic_data, artificial_pinned_survived_size)
GC_OFFSET(      20,      40, dynamic_data, added_pinned_size)
GC_OFFSET(      24,      48, dynamic_data, padding_size)
#if defined(FEATURE_64BIT_ALIGNMENT)
GC_OFFSET(      28,      50, dynamic_data, num_npinned_plugs)
GC_OFFSET(      2c,      58, dynamic_data, current_size)
GC_OFFSET(      30,      60, dynamic_data, collection_count)
GC_OFFSET(      34,      68, dynamic_data, promoted_size)
GC_OFFSET(      38,      70, dynamic_data, freach_previous_promotion)
GC_OFFSET(      3c,      78, dynamic_data, fragmentation)
GC_OFFSET(      40,      80, dynamic_data, gc_clock)
GC_OFFSET(      48,      88, dynamic_data, time_clock)
GC_OFFSET(      50,      90, dynamic_data, previous_time_clock)
GC_OFFSET(      58,      98, dynamic_data, gc_elapsed_time)
GC_OFFSET(      5c,      a0, dynamic_data, min_size)
GC_OFFSET(      60,      a8, dynamic_data, sdata)
GC_SIZEOF(      68,      b0, dynamic_data)
#else
GC_OFFSET(      28,      50, dynamic_data, current_size)
GC_OFFSET(      2c,      58, dynamic_data, collection_count)
GC_OFFSET(      30,      60, dynamic_data, promoted_size)
GC_OFFSET(      34,      68, dynamic_data, freach_previous_promotion)
GC_OFFSET(      38,      70, dynamic_data, fragmentation)
GC_OFFSET(      3c,      78, dynamic_data, gc_clock)
GC_OFFSET(      40,      80, dynamic_data, time_clock)
GC_OFFSET(      48,      88, dynamic_data, previous_time_clock)
GC_OFFSET(      50,      90, dynamic_data, gc_elapsed_time)
GC_OFFSET(      54,      98, dynamic_data, min_size)
GC_OFFSET(      58,      a0, dynamic_data, sdata)
GC_SIZEOF(      60,      a8, dynamic_data)
#endif
GC_ALIGNOF(      8,       8, dynamic_data)

GC_OFFSET(       0,       0, recorded_generation_info, size_before)
GC_OFFSET(       4,       8, recorded_generation_info, fragmentation_before)
GC_OFFSET(       8,      10, recorded_generation_info, size_after)
GC_OFFSET(       c,      18, recorded_generation_info, fragmentation_after)
GC_SIZEOF(      10,      20, recorded_generation_info)
GC_ALIGNOF(      4,       8, recorded_generation_info)

GC_OFFSET(       0,       0, last_recorded_gc_info, index)
GC_OFFSET(       4,       8, last_recorded_gc_info, total_committed)
GC_OFFSET(       8,      10, last_recorded_gc_info, promoted)
GC_OFFSET(       c,      18, last_recorded_gc_info, pinned_objects)
GC_OFFSET(      10,      20, last_recorded_gc_info, finalize_promoted_objects)
GC_OFFSET(      14,      28, last_recorded_gc_info, pause_durations)
GC_OFFSET(      1c,      38, last_recorded_gc_info, pause_percentage)
GC_OFFSET(      20,      40, last_recorded_gc_info, gen_info)
GC_OFFSET(      70,      e0, last_recorded_gc_info, heap_size)
GC_OFFSET(      74,      e8, last_recorded_gc_info, fragmentation)
GC_OFFSET(      78,      f0, last_recorded_gc_info, memory_load)
GC_OFFSET(      7c,      f4, last_recorded_gc_info, condemned_generation)
GC_OFFSET(      7d,      f5, last_recorded_gc_info, compaction)
GC_OFFSET(      7e,      f6, last_recorded_gc_info, concurrent)
GC_SIZEOF(      80,      f8, last_recorded_gc_info)
GC_ALIGNOF(      4,       8, last_recorded_gc_info)

GC_OFFSET(       0,       0, etw_opt_info, desired_allocation)
GC_OFFSET(       4,       8, etw_opt_info, new_allocation)
GC_OFFSET(       8,      10, etw_opt_info, gen_number)
GC_SIZEOF(       c,      18, etw_opt_info)
GC_ALIGNOF(      4,       8, etw_opt_info)

// alloc_list keeps all of its fields private. The managed tests pin their declaration order;
// these entries pin the complete size under the collector's active feature conditionals.
#if defined(FL_VERIFICATION)
#if defined(TARGET_WASM)
GC_SIZEOF(      10,      20, alloc_list)
#else
GC_SIZEOF(      10,      30, alloc_list)
#endif
#else
#if defined(TARGET_WASM)
GC_SIZEOF(       c,      18, alloc_list)
#else
GC_SIZEOF(       c,      28, alloc_list)
#endif
#endif
GC_ALIGNOF(      4,       8, alloc_list)

// allocator embeds a first_bucket alloc_list and keeps every member private, so like alloc_list
// only its size and alignment are pinned here; the managed tests pin the field order. The size
// tracks alloc_list's own FL_VERIFICATION/TARGET_WASM combinations because it contains one.
#if defined(FL_VERIFICATION)
#if defined(TARGET_WASM)
GC_SIZEOF(      20,      38, allocator)
#else
GC_SIZEOF(      20,      48, allocator)
#endif
#else
#if defined(TARGET_WASM)
GC_SIZEOF(      1c,      30, allocator)
#else
GC_SIZEOF(      1c,      40, allocator)
#endif
#endif
GC_ALIGNOF(      4,       8, allocator)
GC_CONST(         2,         2, max_generation)
GC_CONST(        14,        14, MAX_BUCKET_COUNT)

// The per-heap generation. It has no native constructor, so a zero instance matches the native
// default for every field except the embedded free_list_allocator, whose one-bucket default the
// allocator constructor supplies; the managed port reproduces that in generation::initialize. Its
// allocation_context is an alloc_context, which derives from gc_alloc_context and adds no fields,
// so the port reuses the gc_alloc_context layout for it. The schema forks on USE_REGIONS -- the
// region layout replaces allocation_start / plan_allocation_start(_size) with tail_region /
// tail_ro_region -- and DOUBLY_LINKED_FL adds the two trailing gen2 fields. USE_REGIONS implies
// HOST_64BIT, so the 32-bit column of that branch is never evaluated; it is filled for
// completeness. FREE_USAGE_STATS is diagnostics-only and never defined, so its fields are omitted.
GC_OFFSET(       0,       0, generation, allocation_context)
GC_OFFSET(      28,      38, generation, start_segment)
#ifdef USE_REGIONS
GC_OFFSET(      2c,      40, generation, allocation_segment)
GC_OFFSET(      30,      48, generation, allocation_context_start_region)
GC_OFFSET(      34,      50, generation, tail_region)
GC_OFFSET(      38,      58, generation, tail_ro_region)
GC_OFFSET(      3c,      60, generation, free_list_allocator)
GC_OFFSET(      58,      a0, generation, free_list_allocated)
GC_OFFSET(      5c,      a8, generation, end_seg_allocated)
GC_OFFSET(      60,      b0, generation, condemned_allocated)
GC_OFFSET(      64,      b8, generation, sweep_allocated)
GC_OFFSET(      68,      c0, generation, allocate_end_seg_p)
GC_OFFSET(      6c,      c8, generation, free_list_space)
GC_OFFSET(      70,      d0, generation, free_obj_space)
GC_OFFSET(      74,      d8, generation, allocation_size)
GC_OFFSET(      78,      e0, generation, pinned_allocation_compact_size)
GC_OFFSET(      7c,      e8, generation, pinned_allocation_sweep_size)
GC_OFFSET(      80,      f0, generation, gen_num)
#ifdef DOUBLY_LINKED_FL
GC_OFFSET(      84,      f4, generation, set_bgc_mark_bit_p)
GC_OFFSET(      88,      f8, generation, last_free_list_allocated)
#endif
GC_SIZEOF(      88,     100, generation)
#else
GC_OFFSET(      2c,      40, generation, allocation_start)
GC_OFFSET(      30,      48, generation, allocation_segment)
GC_OFFSET(      34,      50, generation, allocation_context_start_region)
GC_OFFSET(      38,      58, generation, free_list_allocator)
GC_OFFSET(      54,      98, generation, free_list_allocated)
GC_OFFSET(      58,      a0, generation, end_seg_allocated)
GC_OFFSET(      5c,      a8, generation, condemned_allocated)
GC_OFFSET(      60,      b0, generation, sweep_allocated)
GC_OFFSET(      64,      b8, generation, allocate_end_seg_p)
GC_OFFSET(      68,      c0, generation, free_list_space)
GC_OFFSET(      6c,      c8, generation, free_obj_space)
GC_OFFSET(      70,      d0, generation, allocation_size)
GC_OFFSET(      74,      d8, generation, plan_allocation_start)
GC_OFFSET(      78,      e0, generation, plan_allocation_start_size)
GC_OFFSET(      7c,      e8, generation, pinned_allocation_compact_size)
GC_OFFSET(      80,      f0, generation, pinned_allocation_sweep_size)
GC_OFFSET(      84,      f8, generation, gen_num)
#ifdef DOUBLY_LINKED_FL
GC_OFFSET(      88,      fc, generation, set_bgc_mark_bit_p)
GC_OFFSET(      8c,     100, generation, last_free_list_allocated)
#endif
GC_SIZEOF(      88,     108, generation)
#endif
GC_ALIGNOF(      8,       8, generation)

#if !defined(TARGET_WASM)
GC_OFFSET(       0,       0, etw_bucket_info, index)
GC_OFFSET(       4,       4, etw_bucket_info, count)
GC_OFFSET(       8,       8, etw_bucket_info, size)
GC_SIZEOF(       c,      10, etw_bucket_info)
GC_ALIGNOF(      4,       8, etw_bucket_info)
#endif

GC_CONST( ffffffff, ffffffff, awr_ignored)
GC_CONST(         0,         0, awr_low_memory)
GC_CONST(         1,         1, awr_low_ephemeral)
GC_CONST(         2,         2, awr_gen0_alloc)
GC_CONST(         3,         3, awr_loh_alloc)
GC_CONST(         4,         4, awr_alloc_loh_low_mem)
GC_CONST(         5,         5, awr_loh_oos)
GC_CONST(         6,         6, awr_gen0_oos_bgc)
GC_CONST(         7,         7, awr_loh_oos_bgc)
GC_CONST(         8,         8, awr_fgc_wait_for_bgc)
GC_CONST(         9,         9, awr_get_loh_seg)
GC_CONST(         a,         a, awr_loh_alloc_during_plan)
GC_CONST(         b,         b, awr_uoh_alloc_during_bgc)

GC_OFFSET(       0,       0, alloc_thread_wait_data, awr)
GC_SIZEOF(       4,       4, alloc_thread_wait_data)
GC_ALIGNOF(      4,       4, alloc_thread_wait_data)

GC_CONST(         0,         0, mt_get_large_seg)
GC_CONST(         1,         1, mt_bgc_uoh_sweep)
GC_CONST(         2,         2, mt_wait_bgc)
GC_CONST(         3,         3, mt_block_gc)
GC_CONST(         4,         4, mt_clr_mem)
GC_CONST(         5,         5, mt_clr_large_mem)
GC_CONST(         6,         6, mt_t_eph_gc)
GC_CONST(         7,         7, mt_t_full_gc)
GC_CONST(         8,         8, mt_alloc_small)
GC_CONST(         9,         9, mt_alloc_large)
GC_CONST(         a,         a, mt_alloc_small_cant)
GC_CONST(         b,         b, mt_alloc_large_cant)
GC_CONST(         c,         c, mt_try_alloc)
GC_CONST(         d,         d, mt_try_budget)
GC_CONST(         e,         e, mt_try_servo_budget)
GC_CONST(         f,         f, mt_decommit_step)

GC_CONST(         0,         0, pause_batch)
GC_CONST(         1,         1, pause_interactive)
GC_CONST(         2,         2, pause_low_latency)
GC_CONST(         3,         3, pause_sustained_low_latency)
GC_CONST(         4,         4, pause_no_gc)

GC_CONST(         1,         1, loh_compaction_default)
GC_CONST(         2,         2, loh_compaction_once)
GC_CONST(         4,         4, loh_compaction_auto)

GC_CONST(         0,         0, set_pause_mode_success)
GC_CONST(         1,         1, set_pause_mode_no_gc)

GC_CONST(         0,         0, latency_level_first)
GC_CONST(         0,         0, latency_level_memory_footprint)
GC_CONST(         1,         1, latency_level_balanced)
GC_CONST(         1,         1, latency_level_last)
GC_CONST(         1,         1, latency_level_default)

GC_CONST(         0,         0, tuning_deciding_condemned_gen)
GC_CONST(         1,         1, tuning_deciding_full_gc)
GC_CONST(         2,         2, tuning_deciding_compaction)
GC_CONST(         3,         3, tuning_deciding_expansion)
GC_CONST(         4,         4, tuning_deciding_promote_ephemeral)
GC_CONST(         5,         5, tuning_deciding_short_on_seg)

GC_VALUE(         0,         0, soh, gc_oh_num::soh)
GC_VALUE(         1,         1, loh, gc_oh_num::loh)
GC_VALUE(         2,         2, poh, gc_oh_num::poh)
GC_VALUE(  ffffffff,  ffffffff, unknown, gc_oh_num::unknown)

GC_CONST(         0,         0, memory_type_reserved)
GC_CONST(         1,         1, memory_type_committed)

GC_CONST(         0,         0, a_state_start)
GC_CONST(         1,         1, a_state_can_allocate)
GC_CONST(         2,         2, a_state_cant_allocate)
GC_CONST(         3,         3, a_state_retry_allocate)
GC_CONST(         4,         4, a_state_try_fit)
GC_CONST(         5,         5, a_state_try_fit_new_seg)
GC_CONST(         6,         6, a_state_try_fit_after_cg)
GC_CONST(         7,         7, a_state_try_fit_after_bgc)
GC_CONST(         8,         8, a_state_try_free_full_seg_in_bgc)
GC_CONST(         9,         9, a_state_try_free_after_bgc)
GC_CONST(         a,         a, a_state_try_seg_end)
GC_CONST(         b,         b, a_state_acquire_seg)
GC_CONST(         c,         c, a_state_acquire_seg_after_cg)
GC_CONST(         d,         d, a_state_acquire_seg_after_bgc)
GC_CONST(         e,         e, a_state_check_and_wait_for_bgc)
GC_CONST(         f,         f, a_state_trigger_full_compact_gc)
GC_CONST(        10,        10, a_state_trigger_ephemeral_gc)
GC_CONST(        11,        11, a_state_trigger_2nd_ephemeral_gc)
GC_CONST(        12,        12, a_state_check_retry_seg)
GC_CONST(        13,        13, a_state_max)

GC_CONST(         0,         0, msl_entered)
GC_CONST(         1,         1, msl_retry_different_heap)
GC_CONST(         0,         0, me_acquire)
GC_CONST(         1,         1, me_release)

GC_OFFSET(       0,       0, no_gc_region_info, soh_allocation_size)
GC_OFFSET(       4,       8, no_gc_region_info, loh_allocation_size)
GC_OFFSET(       8,      10, no_gc_region_info, started)
GC_OFFSET(       c,      18, no_gc_region_info, num_gcs)
GC_OFFSET(      10,      20, no_gc_region_info, num_gcs_induced)
GC_OFFSET(      14,      28, no_gc_region_info, start_status)
GC_OFFSET(      18,      2c, no_gc_region_info, saved_pause_mode)
GC_OFFSET(      1c,      30, no_gc_region_info, saved_gen0_min_size)
GC_OFFSET(      20,      38, no_gc_region_info, saved_gen3_min_size)
GC_OFFSET(      24,      40, no_gc_region_info, minimal_gc_p)
GC_OFFSET(      28,      48, no_gc_region_info, soh_withheld_budget)
GC_OFFSET(      2c,      50, no_gc_region_info, loh_withheld_budget)
GC_OFFSET(      30,      58, no_gc_region_info, callback)
GC_SIZEOF(      34,      60, no_gc_region_info)
GC_ALIGNOF(      4,       8, no_gc_region_info)

GC_CONST(         0,         0, idp_pre_short)
GC_CONST(         1,         1, idp_post_short)
GC_CONST(         2,         2, idp_merged_pin)
GC_CONST(         3,         3, idp_converted_pin)
GC_CONST(         4,         4, idp_pre_pin)
GC_CONST(         5,         5, idp_post_pin)
GC_CONST(         6,         6, idp_pre_and_post_pin)
GC_CONST(         7,         7, idp_pre_short_padded)
GC_CONST(         8,         8, idp_post_short_padded)
GC_CONST(         9,         9, max_idp_count)

GC_OFFSET(         0,         0, plug, skew)
GC_SIZEOF(         4,         8, plug)
GC_ALIGNOF(        4,         8, plug)

GC_OFFSET(         0,         0, pair, left)
GC_OFFSET(         2,         2, pair, right)
GC_SIZEOF(         4,         4, pair)
GC_ALIGNOF(        2,         2, pair)

GC_OFFSET(         0,         0, plug_and_pair, m_pair)
GC_OFFSET(         4,         8, plug_and_pair, m_plug)
GC_SIZEOF(         8,        10, plug_and_pair)
GC_ALIGNOF(        4,         8, plug_and_pair)

GC_OFFSET(         0,         0, plug_and_reloc, reloc)
GC_OFFSET(         4,         8, plug_and_reloc, m_pair)
GC_OFFSET(         8,        10, plug_and_reloc, m_plug)
GC_SIZEOF(         c,        18, plug_and_reloc)
GC_ALIGNOF(        4,         8, plug_and_reloc)

GC_OFFSET(         0,         0, plug_and_gap, gap)
GC_OFFSET(         4,         8, plug_and_gap, reloc)
GC_OFFSET(         8,        10, plug_and_gap, m_pair)
GC_OFFSET(         8,        10, plug_and_gap, lr)
GC_OFFSET(         c,        18, plug_and_gap, m_plug)
GC_SIZEOF(        10,        20, plug_and_gap)
GC_ALIGNOF(        4,         8, plug_and_gap)

GC_OFFSET(         0,         0, gap_reloc_pair, gap)
GC_OFFSET(         4,         8, gap_reloc_pair, reloc)
GC_OFFSET(         8,        10, gap_reloc_pair, m_pair)
GC_SIZEOF(         c,        18, gap_reloc_pair)
GC_ALIGNOF(        4,         8, gap_reloc_pair)

GC_OFFSET(         0,         0, aligned_plug_and_gap, additional_pad)
GC_OFFSET(         4,         8, aligned_plug_and_gap, plugandgap)
GC_SIZEOF(        18,        28, aligned_plug_and_gap)
GC_ALIGNOF(        8,         8, aligned_plug_and_gap)

GC_OFFSET(         0,         0, loh_obj_and_pad, reloc)
GC_OFFSET(         4,         8, loh_obj_and_pad, m_plug)
GC_SIZEOF(         8,        10, loh_obj_and_pad)
GC_ALIGNOF(        4,         8, loh_obj_and_pad)

GC_OFFSET(         0,         0, loh_padding_obj, mt)
GC_OFFSET(         4,         8, loh_padding_obj, len)
GC_OFFSET(         8,        10, loh_padding_obj, reloc)
GC_OFFSET(         c,        18, loh_padding_obj, m_plug)
GC_SIZEOF(        10,        20, loh_padding_obj)
GC_ALIGNOF(        4,         8, loh_padding_obj)

// heap_segment is the collector's segment record. BACKGROUND_GC is defined on every non-WASM
// full runtime. MULTIPLE_HEAPS is the server-GC branch selected by gcimpl.h from SERVER_GC; the
// native verifier builds the workstation branch as a full-runtime source and the server branch
// with Runtime.GC.Server, while Runtime.ManagedGC uses the workstation branch that generated
// GCInterfaceOffsets.cs. The table must consequently retain both layouts.
GC_CONST(         1,         1, heap_segment_flags_readonly)
GC_CONST(         2,         2, heap_segment_flags_inrange)
GC_CONST(         8,         8, heap_segment_flags_loh)
#ifdef BACKGROUND_GC
GC_CONST(        10,        10, heap_segment_flags_swept)
GC_CONST(        20,        20, heap_segment_flags_decommitted)
GC_CONST(        40,        40, heap_segment_flags_ma_committed)
GC_CONST(        80,        80, heap_segment_flags_ma_pcommitted)
GC_CONST(       100,       100, heap_segment_flags_uoh_delete)
#endif
GC_CONST(       200,       200, heap_segment_flags_poh)
#if defined(BACKGROUND_GC) && defined(USE_REGIONS)
GC_CONST(       400,       400, heap_segment_flags_overflow)
#endif

#ifdef USE_REGIONS
GC_CONST(       800,       800, heap_segment_flags_demoted)
GC_CONST(        63,        63, MAX_AGE_IN_FREE)
GC_CONST(        14,        14, AGE_IN_FREE_TO_DECOMMIT_BASIC)
GC_CONST(         5,         5, AGE_IN_FREE_TO_DECOMMIT_LARGE)
GC_CONST(         2,         2, AGE_IN_FREE_TO_DECOMMIT_HUGE)

GC_OFFSET(         0,         0, generation_region_info, head)
GC_OFFSET(         4,         8, generation_region_info, tail)
GC_SIZEOF(         8,        10, generation_region_info)
GC_ALIGNOF(         4,         8, generation_region_info)
#endif

GC_OFFSET(         0,         0, heap_segment, allocated)
GC_OFFSET(         4,         8, heap_segment, committed)
GC_OFFSET(         8,        10, heap_segment, reserved)
GC_OFFSET(         c,        18, heap_segment, used)
GC_OFFSET(        10,        20, heap_segment, mem)
GC_OFFSET(        14,        28, heap_segment, flags)
GC_OFFSET(        18,        30, heap_segment, next)
GC_OFFSET(        1c,        38, heap_segment, background_allocated)
#ifdef MULTIPLE_HEAPS
GC_OFFSET(        20,        40, heap_segment, heap)
#if defined(_DEBUG) && !defined(USE_REGIONS)
GC_OFFSET(        24,        48, heap_segment, saved_committed)
GC_OFFSET(        28,        50, heap_segment, saved_desired_allocation)
#endif
#endif
#if !defined(USE_REGIONS) || defined(MULTIPLE_HEAPS)
#if defined(MULTIPLE_HEAPS) && defined(_DEBUG) && !defined(USE_REGIONS)
GC_OFFSET(        2c,        58, heap_segment, decommit_target)
#elif defined(MULTIPLE_HEAPS)
GC_OFFSET(        24,        48, heap_segment, decommit_target)
#else
GC_OFFSET(        20,        40, heap_segment, decommit_target)
#endif
#endif
#if defined(MULTIPLE_HEAPS) && defined(_DEBUG) && !defined(USE_REGIONS)
GC_OFFSET(        30,        60, heap_segment, plan_allocated)
GC_OFFSET(        34,        68, heap_segment, saved_allocated)
GC_OFFSET(        38,        70, heap_segment, saved_bg_allocated)
#elif defined(MULTIPLE_HEAPS)
GC_OFFSET(        28,        50, heap_segment, plan_allocated)
GC_OFFSET(        2c,        58, heap_segment, saved_allocated)
GC_OFFSET(        30,        60, heap_segment, saved_bg_allocated)
#elif defined(USE_REGIONS)
GC_OFFSET(        20,        40, heap_segment, plan_allocated)
GC_OFFSET(        24,        48, heap_segment, saved_allocated)
GC_OFFSET(        28,        50, heap_segment, saved_bg_allocated)
#else
GC_OFFSET(        24,        48, heap_segment, plan_allocated)
GC_OFFSET(        28,        50, heap_segment, saved_allocated)
GC_OFFSET(        2c,        58, heap_segment, saved_bg_allocated)
#endif
#ifdef USE_REGIONS
#ifdef MULTIPLE_HEAPS
GC_OFFSET(        34,        68, heap_segment, survived)
GC_OFFSET(        38,        70, heap_segment, gen_num)
GC_OFFSET(        39,        71, heap_segment, swept_in_plan_p)
GC_OFFSET(        3c,        74, heap_segment, plan_gen_num)
GC_OFFSET(        40,        78, heap_segment, old_card_survived)
GC_OFFSET(        44,        7c, heap_segment, pinned_survived)
GC_OFFSET(        48,        80, heap_segment, age_in_free)
GC_OFFSET(        4c,        88, heap_segment, free_list_head)
GC_OFFSET(        50,        90, heap_segment, free_list_tail)
GC_OFFSET(        54,        98, heap_segment, free_list_size)
GC_OFFSET(        58,        a0, heap_segment, free_obj_size)
GC_OFFSET(        5c,        a8, heap_segment, prev_free_region)
GC_OFFSET(        60,        b0, heap_segment, containing_free_list)
GC_SIZEOF(        64,        b8, heap_segment)
#else
GC_OFFSET(        2c,        58, heap_segment, survived)
GC_OFFSET(        30,        60, heap_segment, gen_num)
GC_OFFSET(        31,        61, heap_segment, swept_in_plan_p)
GC_OFFSET(        34,        64, heap_segment, plan_gen_num)
GC_OFFSET(        38,        68, heap_segment, old_card_survived)
GC_OFFSET(        3c,        6c, heap_segment, pinned_survived)
GC_OFFSET(        40,        70, heap_segment, age_in_free)
GC_OFFSET(        44,        78, heap_segment, free_list_head)
GC_OFFSET(        48,        80, heap_segment, free_list_tail)
GC_OFFSET(        4c,        88, heap_segment, free_list_size)
GC_OFFSET(        50,        90, heap_segment, free_obj_size)
GC_OFFSET(        54,        98, heap_segment, prev_free_region)
GC_OFFSET(        58,        a0, heap_segment, containing_free_list)
GC_SIZEOF(        5c,        a8, heap_segment)
#endif
#else
#if defined(MULTIPLE_HEAPS) && defined(_DEBUG)
GC_OFFSET(        40,        78, heap_segment, padandplug)
GC_SIZEOF(        58,        a0, heap_segment)
#elif defined(MULTIPLE_HEAPS)
GC_OFFSET(        38,        68, heap_segment, padandplug)
GC_SIZEOF(        50,        90, heap_segment)
#else
GC_OFFSET(        30,        60, heap_segment, padandplug)
GC_SIZEOF(        48,        88, heap_segment)
#endif
#endif
GC_ALIGNOF(         8,         8, heap_segment)

// A basic-region mapping either carries its complete region segment record or, without regions,
// caches the address boundary, heap(s), and segment(s) used by the fast heap lookup. The low bit
// of seg1 marks a read-only segment in a non-region entry. The lookup/table-size algorithms that
// consume this shape remain with regions_segments.cpp and gc.cpp.
GC_CONST(         1,         1, ro_in_entry)
#ifdef USE_REGIONS
GC_OFFSET(         0,         0, seg_mapping, region_info)
#ifdef MULTIPLE_HEAPS
GC_SIZEOF(        64,        b8, seg_mapping)
#else
GC_SIZEOF(        5c,        a8, seg_mapping)
#endif
#else
GC_OFFSET(         0,         0, seg_mapping, boundary)
#ifdef MULTIPLE_HEAPS
GC_OFFSET(         4,         8, seg_mapping, h0)
GC_OFFSET(         8,        10, seg_mapping, h1)
GC_OFFSET(         c,        18, seg_mapping, seg0)
GC_OFFSET(        10,        20, seg_mapping, seg1)
GC_SIZEOF(        14,        28, seg_mapping)
#else
GC_OFFSET(         4,         8, seg_mapping, seg0)
GC_OFFSET(         8,        10, seg_mapping, seg1)
GC_SIZEOF(         c,        18, seg_mapping)
#endif
#endif
GC_ALIGNOF(         4,         8, seg_mapping)

// -----------------------------------------------------------------------------------------
// The DAC-facing shared data of gcinterface.dac.h. GcDacVars is the fourth argument of
// GC_Initialize, so its layout is part of the loader protocol; the types below it are the
// analogues the DAC reads GC state through. The managed copies are in GCInterfaceDac.cs.
// -----------------------------------------------------------------------------------------

//        32-bit,64-bit, class, member
GC_OFFSET(     0,     0, oom_history, reason)
GC_OFFSET(     4,     8, oom_history, alloc_size)
GC_OFFSET(     8,    10, oom_history, reserved)
GC_OFFSET(     c,    18, oom_history, allocated)
GC_OFFSET(    10,    20, oom_history, gc_index)
GC_OFFSET(    14,    28, oom_history, fgm)
GC_OFFSET(    18,    30, oom_history, size)
GC_OFFSET(    1c,    38, oom_history, available_pagefile_mb)
GC_OFFSET(    20,    40, oom_history, loh_p)
GC_SIZEOF(    24,    48, oom_history)
GC_ALIGNOF(     4,     8, oom_history)

GC_OFFSET(     0,     0, dac_heap_segment, allocated)
GC_OFFSET(     4,     8, dac_heap_segment, committed)
GC_OFFSET(     8,    10, dac_heap_segment, reserved)
GC_OFFSET(     c,    18, dac_heap_segment, used)
GC_OFFSET(    10,    20, dac_heap_segment, mem)
GC_OFFSET(    14,    28, dac_heap_segment, flags)
GC_OFFSET(    18,    30, dac_heap_segment, next)
GC_OFFSET(    1c,    38, dac_heap_segment, background_allocated)
GC_OFFSET(    20,    40, dac_heap_segment, heap)
GC_SIZEOF(    24,    48, dac_heap_segment)
GC_ALIGNOF(     4,     8, dac_heap_segment)

GC_OFFSET(     0,     0, dac_region_free_list, num_free_regions)
GC_OFFSET(     4,     8, dac_region_free_list, size_free_regions)
GC_OFFSET(     8,    10, dac_region_free_list, size_committed_in_free_regions)
GC_OFFSET(     c,    18, dac_region_free_list, num_free_regions_added)
GC_OFFSET(    10,    20, dac_region_free_list, num_free_regions_removed)
GC_OFFSET(    14,    28, dac_region_free_list, head_free_region)
GC_OFFSET(    18,    30, dac_region_free_list, tail_free_region)
GC_SIZEOF(    1c,    38, dac_region_free_list)
GC_ALIGNOF(     4,     8, dac_region_free_list)

GC_OFFSET(     0,     0, dac_generation, allocation_context)
GC_OFFSET(    28,    38, dac_generation, start_segment)
GC_OFFSET(    2c,    40, dac_generation, allocation_start)
GC_SIZEOF(    30,    48, dac_generation)
GC_ALIGNOF(     8,     8, dac_generation)

GC_OFFSET(     0,     0, dac_finalize_queue, m_FillPointers)
GC_SIZEOF(    18,    30, dac_finalize_queue)
GC_ALIGNOF(     4,     8, dac_finalize_queue)

GC_OFFSET(     0,     0, dac_handle_table_segment, rgGeneration)
GC_OFFSET(   3c0,   1e0, dac_handle_table_segment, rgAllocation)
GC_OFFSET(   4b0,   258, dac_handle_table_segment, rgFreeMask)
GC_OFFSET(   c30,   618, dac_handle_table_segment, rgBlockType)
GC_OFFSET(   d20,   690, dac_handle_table_segment, rgUserData)
GC_OFFSET(   e10,   708, dac_handle_table_segment, rgLocks)
GC_OFFSET(   f00,   780, dac_handle_table_segment, rgTail)
GC_OFFSET(   f0d,   78d, dac_handle_table_segment, rgHint)
GC_OFFSET(   f1a,   79a, dac_handle_table_segment, rgFreeCount)
GC_OFFSET(   f4e,   7ce, dac_handle_table_segment, pNextSegment)
GC_SIZEOF(   f52,   7d6, dac_handle_table_segment)
GC_ALIGNOF(     1,     1, dac_handle_table_segment)

GC_OFFSET(     0,     0, dac_handle_table, padding)
GC_OFFSET(    34,    38, dac_handle_table, pSegmentList)
GC_SIZEOF(    38,    40, dac_handle_table)
GC_ALIGNOF(     4,     8, dac_handle_table)

GC_OFFSET(     0,     0, dac_handle_table_bucket, pTable)
GC_OFFSET(     4,     8, dac_handle_table_bucket, HandleTableIndex)
GC_SIZEOF(     8,    10, dac_handle_table_bucket)
GC_ALIGNOF(     4,     8, dac_handle_table_bucket)

GC_OFFSET(     0,     0, dac_handle_table_map, pBuckets)
GC_OFFSET(     4,     8, dac_handle_table_map, pNext)
GC_OFFSET(     8,    10, dac_handle_table_map, dwMaxIndex)
GC_SIZEOF(     c,    18, dac_handle_table_map)
GC_ALIGNOF(     4,     8, dac_handle_table_map)

GC_OFFSET(     0,     0, dac_card_table_info, recount)
GC_OFFSET(     4,     8, dac_card_table_info, size)
GC_OFFSET(     8,    10, dac_card_table_info, next_card_table)
GC_SIZEOF(     c,    18, dac_card_table_info)
GC_ALIGNOF(     4,     8, dac_card_table_info)

GC_OFFSET(     0,     0, unused_gc_heap, unused)
GC_SIZEOF(     1,     1, unused_gc_heap)
GC_ALIGNOF(     1,     1, unused_gc_heap)

GC_OFFSET(     0,     0, unused_generation, unused)
GC_SIZEOF(     1,     1, unused_generation)
GC_ALIGNOF(     1,     1, unused_generation)

GC_OFFSET(       0,       0, dac_gc_heap, alloc_allocated)
GC_OFFSET(       4,       8, dac_gc_heap, ephemeral_heap_segment)
GC_OFFSET(       8,      10, dac_gc_heap, finalize_queue)
GC_OFFSET(       c,      18, dac_gc_heap, oom_info)
GC_OFFSET(      30,      60, dac_gc_heap, interesting_data_per_heap)
GC_OFFSET(      54,      a8, dac_gc_heap, compact_reasons_per_heap)
GC_OFFSET(      80,     100, dac_gc_heap, expand_mechanisms_per_heap)
GC_OFFSET(      98,     130, dac_gc_heap, interesting_mechanism_bits_per_heap)
GC_OFFSET(      a0,     140, dac_gc_heap, internal_root_array)
GC_OFFSET(      a4,     148, dac_gc_heap, internal_root_array_index)
GC_OFFSET(      a8,     150, dac_gc_heap, heap_analyze_success)
GC_OFFSET(      ac,     158, dac_gc_heap, card_table)
GC_OFFSET(      b0,     160, dac_gc_heap, mark_array)
GC_OFFSET(      b4,     168, dac_gc_heap, next_sweep_obj)
GC_OFFSET(      b8,     170, dac_gc_heap, background_saved_lowest_address)
GC_OFFSET(      bc,     178, dac_gc_heap, background_saved_highest_address)
GC_OFFSET(      c0,     180, dac_gc_heap, saved_sweep_ephemeral_seg)
GC_OFFSET(      c4,     188, dac_gc_heap, saved_sweep_ephemeral_start)
GC_OFFSET(      c8,     190, dac_gc_heap, generation_table)
GC_OFFSET(      cc,     198, dac_gc_heap, freeable_soh_segment)
GC_OFFSET(      d0,     1a0, dac_gc_heap, freeable_uoh_segment)
GC_OFFSET(      d4,     1a8, dac_gc_heap, free_regions)
GC_SIZEOF(     128,     250, dac_gc_heap)
GC_ALIGNOF(      4,       8, dac_gc_heap)

GC_OFFSET(     0,     0, GcDacVars, major_version_number)
GC_OFFSET(     1,     1, GcDacVars, minor_version_number)
GC_OFFSET(     4,     8, GcDacVars, generation_size)
GC_OFFSET(     8,    10, GcDacVars, total_generation_count)
GC_OFFSET(     c,    18, GcDacVars, build_variant)
GC_OFFSET(    10,    20, GcDacVars, built_with_svr)
GC_OFFSET(    14,    28, GcDacVars, gc_global_mechanisms)
GC_OFFSET(    18,    30, GcDacVars, generation_table)
GC_OFFSET(    1c,    38, GcDacVars, max_gen)
GC_OFFSET(    20,    40, GcDacVars, mark_array)
GC_OFFSET(    24,    48, GcDacVars, current_c_gc_state)
GC_OFFSET(    28,    50, GcDacVars, ephemeral_heap_segment)
GC_OFFSET(    2c,    58, GcDacVars, saved_sweep_ephemeral_seg)
GC_OFFSET(    30,    60, GcDacVars, saved_sweep_ephemeral_start)
GC_OFFSET(    34,    68, GcDacVars, background_saved_lowest_address)
GC_OFFSET(    38,    70, GcDacVars, background_saved_highest_address)
GC_OFFSET(    3c,    78, GcDacVars, alloc_allocated)
GC_OFFSET(    40,    80, GcDacVars, next_sweep_obj)
GC_OFFSET(    44,    88, GcDacVars, oom_info)
GC_OFFSET(    48,    90, GcDacVars, finalize_queue)
GC_OFFSET(    4c,    98, GcDacVars, internal_root_array)
GC_OFFSET(    50,    a0, GcDacVars, internal_root_array_index)
GC_OFFSET(    54,    a8, GcDacVars, heap_analyze_success)
GC_OFFSET(    58,    b0, GcDacVars, n_heaps)
GC_OFFSET(    5c,    b8, GcDacVars, g_heaps)
GC_OFFSET(    60,    c0, GcDacVars, gc_structures_invalid_cnt)
GC_OFFSET(    64,    c8, GcDacVars, interesting_data_per_heap)
GC_OFFSET(    68,    d0, GcDacVars, compact_reasons_per_heap)
GC_OFFSET(    6c,    d8, GcDacVars, expand_mechanisms_per_heap)
GC_OFFSET(    70,    e0, GcDacVars, interesting_mechanism_bits_per_heap)
GC_OFFSET(    74,    e8, GcDacVars, handle_table_map)
GC_OFFSET(    78,    f0, GcDacVars, gc_heap_field_offsets)
GC_OFFSET(    7c,    f8, GcDacVars, generation_field_offsets)
GC_OFFSET(    80,   100, GcDacVars, bookkeeping_start)
GC_OFFSET(    84,   108, GcDacVars, global_regions_to_decommit)
GC_OFFSET(    88,   110, GcDacVars, global_free_huge_regions)
GC_OFFSET(    8c,   118, GcDacVars, free_regions)
GC_OFFSET(    90,   120, GcDacVars, freeable_soh_segment)
GC_OFFSET(    94,   128, GcDacVars, freeable_uoh_segment)
GC_OFFSET(    98,   130, GcDacVars, total_bookkeeping_elements)
GC_OFFSET(    9c,   134, GcDacVars, count_free_region_kinds)
GC_OFFSET(    a0,   138, GcDacVars, card_table_info_size)
GC_OFFSET(    a4,   140, GcDacVars, dynamic_adaptation_mode)
GC_OFFSET(    a8,   148, GcDacVars, gc_descriptor)
GC_OFFSET(    ac,   150, GcDacVars, g_totalCpuCount)
GC_SIZEOF(    b0,   158, GcDacVars)
GC_ALIGNOF(     4,     8, GcDacVars)

// -----------------------------------------------------------------------------------------
// The size of every enum that crosses the GC/EE boundary. An enum whose underlying type
// changed would silently change the size of every signature and structure it appears in.
// -----------------------------------------------------------------------------------------

//        32-bit,64-bit, class
GC_SIZEOF(     4,     4, SUSPEND_REASON)
GC_SIZEOF(     4,     4, walk_surv_type)
GC_SIZEOF(     4,     4, WriteBarrierOp)
GC_SIZEOF(     4,     4, collection_mode)
GC_SIZEOF(     4,     4, wait_full_gc_status)
GC_SIZEOF(     4,     4, start_no_gc_region_status)
GC_SIZEOF(     4,     4, end_no_gc_region_status)
GC_SIZEOF(     4,     4, refresh_memory_limit_status)
GC_SIZEOF(     4,     4, enable_no_gc_region_callback_status)
GC_SIZEOF(     4,     4, gc_kind)
GC_SIZEOF(     4,     4, HandleType)
GC_SIZEOF(     4,     4, GCHeapType)
GC_SIZEOF(     4,     4, GCConfigurationType)
GC_SIZEOF(     4,     4, GC_ALLOC_FLAGS)
GC_SIZEOF(     4,     4, EtwGCRootKind)
GC_SIZEOF(     4,     4, EtwGCRootFlags)
GC_SIZEOF(     4,     4, GCEventProvider)
GC_SIZEOF(     4,     4, GCEventLevel)
GC_SIZEOF(     4,     4, GCEventKeyword)
GC_SIZEOF(     4,     4, c_gc_state)
GC_SIZEOF(     4,     4, oom_reason)
GC_SIZEOF(     4,     4, failure_get_memory)

//        32-bit,64-bit, constant symbol
GC_CONST(     1,     1, SUSPEND_FOR_GC)
GC_CONST(     6,     6, SUSPEND_FOR_GC_PREP)

GC_CONST(     1,     1, walk_for_gc)
GC_CONST(     2,     2, walk_for_bgc)
GC_CONST(     3,     3, walk_for_uoh)

//        32-bit,64-bit, C# name, C++ expression
GC_VALUE(     0,     0, WriteBarrierOp_StompResize,           (int)WriteBarrierOp::StompResize)
GC_VALUE(     1,     1, WriteBarrierOp_StompEphemeral,        (int)WriteBarrierOp::StompEphemeral)
GC_VALUE(     2,     2, WriteBarrierOp_Initialize,            (int)WriteBarrierOp::Initialize)
GC_VALUE(     3,     3, WriteBarrierOp_SwitchToWriteWatch,    (int)WriteBarrierOp::SwitchToWriteWatch)
GC_VALUE(     4,     4, WriteBarrierOp_SwitchToNonWriteWatch, (int)WriteBarrierOp::SwitchToNonWriteWatch)

GC_VALUE(     0,     0, GCConfigurationType_Int64,      (int)GCConfigurationType::Int64)
GC_VALUE(     1,     1, GCConfigurationType_StringUtf8, (int)GCConfigurationType::StringUtf8)
GC_VALUE(     2,     2, GCConfigurationType_Boolean,    (int)GCConfigurationType::Boolean)

// collection_gcstress is absent because the C++ enumerator only exists in a STRESS_HEAP build.
//        32-bit,64-bit, constant symbol
GC_CONST(     1,     1, collection_non_blocking)
GC_CONST(     2,     2, collection_blocking)
GC_CONST(     4,     4, collection_optimized)
GC_CONST(     8,     8, collection_compacting)
GC_CONST(    10,    10, collection_aggressive)

GC_CONST(     0,     0, wait_full_gc_success)
GC_CONST(     1,     1, wait_full_gc_failed)
GC_CONST(     2,     2, wait_full_gc_cancelled)
GC_CONST(     3,     3, wait_full_gc_timeout)
GC_CONST(     4,     4, wait_full_gc_na)

GC_CONST(     0,     0, start_no_gc_success)
GC_CONST(     1,     1, start_no_gc_no_memory)
GC_CONST(     2,     2, start_no_gc_too_large)
GC_CONST(     3,     3, start_no_gc_in_progress)

GC_CONST(     0,     0, end_no_gc_success)
GC_CONST(     1,     1, end_no_gc_not_in_progress)
GC_CONST(     2,     2, end_no_gc_induced)
GC_CONST(     3,     3, end_no_gc_alloc_exceeded)

GC_CONST(     0,     0, refresh_success)
GC_CONST(     1,     1, refresh_hard_limit_too_low)
GC_CONST(     2,     2, refresh_hard_limit_invalid)

GC_CONST(     0,     0, succeed)
GC_CONST(     1,     1, not_started)
GC_CONST(     2,     2, insufficient_budget)
GC_CONST(     3,     3, already_registered)

GC_CONST(     0,     0, gc_kind_any)
GC_CONST(     1,     1, gc_kind_ephemeral)
GC_CONST(     2,     2, gc_kind_full_blocking)
GC_CONST(     3,     3, gc_kind_background)

// The cDAC contracts and the EE depend on these values; they must not be renumbered.
GC_CONST(     0,     0, HNDTYPE_WEAK_SHORT)
GC_CONST(     1,     1, HNDTYPE_WEAK_LONG)
GC_CONST(     1,     1, HNDTYPE_WEAK_DEFAULT)
GC_CONST(     2,     2, HNDTYPE_STRONG)
GC_CONST(     2,     2, HNDTYPE_DEFAULT)
GC_CONST(     3,     3, HNDTYPE_PINNED)
GC_CONST(     4,     4, HNDTYPE_VARIABLE)
GC_CONST(     5,     5, HNDTYPE_REFCOUNTED)
GC_CONST(     6,     6, HNDTYPE_DEPENDENT)
GC_CONST(     7,     7, HNDTYPE_ASYNCPINNED)
GC_CONST(     8,     8, HNDTYPE_SIZEDREF)
GC_CONST(     9,     9, HNDTYPE_WEAK_NATIVE_COM)
GC_CONST(     a,     a, HNDTYPE_WEAK_INTERIOR_POINTER)
GC_CONST(     b,     b, HNDTYPE_CROSSREFERENCE)

GC_CONST(     0,     0, GC_HEAP_INVALID)
GC_CONST(     1,     1, GC_HEAP_WKS)
GC_CONST(     2,     2, GC_HEAP_SVR)

// Kept in sync with GC_ALLOC_FLAGS in GC.CoreCLR.cs.
GC_CONST(     0,     0, GC_ALLOC_NO_FLAGS)
GC_CONST(     1,     1, GC_ALLOC_FINALIZE)
GC_CONST(     2,     2, GC_ALLOC_CONTAINS_REF)
GC_CONST(     4,     4, GC_ALLOC_ALIGN8_BIAS)
GC_CONST(     8,     8, GC_ALLOC_ALIGN8)
GC_CONST(    10,    10, GC_ALLOC_ZEROING_OPTIONAL)
GC_CONST(    20,    20, GC_ALLOC_LARGE_OBJECT_HEAP)
GC_CONST(    40,    40, GC_ALLOC_PINNED_OBJECT_HEAP)
GC_CONST(    60,    60, GC_ALLOC_USER_OLD_HEAP)

GC_CONST(     1,     1, GC_CALL_INTERIOR)
GC_CONST(     2,     2, GC_CALL_PINNED)

GC_CONST(     0,     0, kEtwGCRootKindStack)
GC_CONST(     1,     1, kEtwGCRootKindFinalizer)
GC_CONST(     2,     2, kEtwGCRootKindHandle)
GC_CONST(     3,     3, kEtwGCRootKindOther)

GC_CONST(     1,     1, kEtwGCRootFlagsPinning)
GC_CONST(     2,     2, kEtwGCRootFlagsWeakRef)
GC_CONST(     4,     4, kEtwGCRootFlagsInterior)
GC_CONST(     8,     8, kEtwGCRootFlagsRefCounted)

// The DAC-facing constants and enumerators of gcinterface.dac.h.
GC_CONST(     0,     0, c_gc_state_marking)
GC_CONST(     1,     1, c_gc_state_planning)
GC_CONST(     2,     2, c_gc_state_free)

GC_CONST(     0,     0, oom_no_failure)
GC_CONST(     1,     1, oom_budget)
GC_CONST(     2,     2, oom_cant_commit)
GC_CONST(     3,     3, oom_cant_reserve)
GC_CONST(     4,     4, oom_loh)
GC_CONST(     5,     5, oom_low_mem)
GC_CONST(     6,     6, oom_unproductive_full_gc)

GC_CONST(     0,     0, fgm_no_failure)
GC_CONST(     1,     1, fgm_reserve_segment)
GC_CONST(     2,     2, fgm_commit_segment_beg)
GC_CONST(     3,     3, fgm_commit_eph_segment)
GC_CONST(     4,     4, fgm_grow_table)
GC_CONST(     5,     5, fgm_commit_table)

GC_CONST(     1,     1, HEAP_SEGMENT_FLAGS_READONLY)
GC_CONST(     9,     9, NUM_GC_DATA_POINTS)
GC_CONST(     b,     b, MAX_COMPACT_REASONS_COUNT)
GC_CONST(     6,     6, MAX_EXPAND_MECHANISMS_COUNT)
GC_CONST(     2,     2, MAX_GC_MECHANISM_BITS_COUNT)
GC_CONST(     6,     6, MAX_GLOBAL_GC_MECHANISMS_COUNT)
GC_CONST(     3,     3, FREE_REGION_KINDS)
GC_CONST(     4,     4, NUMBERGENERATIONS)
GC_CONST(    12,    12, GENERATION_TABLE_FIELD_INDEX)
GC_CONST(     1,     1, build_variant_use_region)
GC_CONST(     2,     2, build_variant_background_gc)
GC_CONST(     4,     4, build_variant_dynamic_heap_count)

//
// The environment layer: the types of gcenv.structs.h and gcenv.os.h that the C# port defines
// itself. These do not cross the GC/EE boundary, but they do cross the boundary between the
// managed GC and the C++ GCToOSInterface it still forwards to.
//
// Several of the C++ classes keep their members private, so the table can only pin their size
// and alignment; each of them has a single field or two pointer-sized fields in a fixed order,
// which size and alignment together determine.
//

GC_OFFSET(     0,     0, GCSystemInfo, dwNumberOfProcessors)
GC_OFFSET(     4,     4, GCSystemInfo, dwPageSize)
GC_OFFSET(     8,     8, GCSystemInfo, dwAllocationGranularity)
GC_SIZEOF(     c,     c, GCSystemInfo)
GC_ALIGNOF(     4,     4, GCSystemInfo)

GC_SIZEOF(     8,    10, AffinitySet)
GC_ALIGNOF(     4,     8, AffinitySet)

GC_SIZEOF(     4,     8, GCEvent)
GC_ALIGNOF(     4,     8, GCEvent)

GC_CONST(  ffff,  ffff, NUMA_NODE_UNDEFINED)
GC_CONST(    40,   400, MAX_SUPPORTED_HEAPS)
GC_CONST(    10,    40, MAX_SUPPORTED_NODES)

GC_VALUE(     0,     0, VirtualReserveFlags_None, VirtualReserveFlags::None)
GC_VALUE(     1,     1, VirtualReserveFlags_WriteWatch, VirtualReserveFlags::WriteWatch)

GC_CONST(     0,     0, WAIT_OBJECT_0)
GC_CONST(   102,   102, WAIT_TIMEOUT)
