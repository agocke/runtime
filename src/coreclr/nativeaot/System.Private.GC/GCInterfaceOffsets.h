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

// -----------------------------------------------------------------------------------------
// The DAC-facing shared data of gcinterface.dac.h. GcDacVars is the fourth argument of
// GC_Initialize, so its layout is part of the loader protocol; the types below it are the
// analogues the DAC reads GC state through. The managed copies are in GCInterfaceDac.cs.
//
// dac_generation and dac_gc_heap are deliberately absent: they are generated from
// dac_generation_fields.h / dac_gcheap_fields.h, whose field lists name gcpriv.h types that are
// not ported yet.
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
