// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Checks that the managed definitions of the GC/EE interface types agree with the layout table in
// GCInterfaceOffsets.h, and that the managed copies of the GC/EE interface enums agree with the
// constants in that same table. The other half of that table is turned into static_asserts
// against the real C++ headers by nativeaot/Runtime/GCInterfaceOffsetsVerify.cpp, so agreement
// here means the managed structs are laid out exactly like their native counterparts and the
// managed enumerators have exactly the native values.
//
// Field offsets, type sizes and type alignments are all checked: the offsets pin the internal
// padding of a type, and the size and alignment pin its trailing padding and how an array or an
// embedded instance of it is placed. Enum sizes are checked because an enum whose underlying
// type changed would silently change every signature and structure it appears in.
//
// The slot order and signatures of the vtables are checked separately, against the native
// headers themselves, by tools/verify-gc-interface-vtables.py during the native build.
//
// The check is a runtime one because C# has no compile-time offsetof, but it is cheap, has no
// dependencies, and is called once during GC startup.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe class GCInterfaceLayout
    {
        /// <summary>
        /// Returns true if every managed GC/EE interface type matches the pinned native layout.
        /// </summary>
        public static bool Verify() =>
            VerifySharedStructs()
            && VerifyEnvironmentTypes()
            && VerifyHandleTableTypes()
            && VerifyGCDescTypes()
            && VerifyGCRecordTypes()
            && VerifyGCPrivTypes()
            && VerifyDacTypes()
            && VerifyVtables()
            && VerifyEnumSizes()
            && VerifyEnumValues();

        /// <summary>
        /// The structures of gcinterface.h and gcinterface.ee.h that the EE and the GC pass to
        /// each other by value or by pointer.
        /// </summary>
        private static bool VerifySharedStructs()
        {
            gc_alloc_context allocContext;
            if (sizeof(gc_alloc_context) != GCInterfaceOffsets.SIZEOF__gc_alloc_context
                || AlignOf<gc_alloc_context>() != GCInterfaceOffsets.ALIGNOF__gc_alloc_context
                || OffsetOf(&allocContext, &allocContext.alloc_ptr) != GCInterfaceOffsets.OFFSETOF__gc_alloc_context__alloc_ptr
                || OffsetOf(&allocContext, &allocContext.alloc_limit) != GCInterfaceOffsets.OFFSETOF__gc_alloc_context__alloc_limit
                || OffsetOf(&allocContext, &allocContext.alloc_bytes) != GCInterfaceOffsets.OFFSETOF__gc_alloc_context__alloc_bytes
                || OffsetOf(&allocContext, &allocContext.alloc_bytes_uoh) != GCInterfaceOffsets.OFFSETOF__gc_alloc_context__alloc_bytes_uoh
                || OffsetOf(&allocContext, &allocContext.gc_reserved_1) != GCInterfaceOffsets.OFFSETOF__gc_alloc_context__gc_reserved_1
                || OffsetOf(&allocContext, &allocContext.gc_reserved_2) != GCInterfaceOffsets.OFFSETOF__gc_alloc_context__gc_reserved_2
                || OffsetOf(&allocContext, &allocContext.alloc_count) != GCInterfaceOffsets.OFFSETOF__gc_alloc_context__alloc_count)
            {
                return false;
            }

            segment_info segmentInfo;
            if (sizeof(segment_info) != GCInterfaceOffsets.SIZEOF__segment_info
                || AlignOf<segment_info>() != GCInterfaceOffsets.ALIGNOF__segment_info
                || OffsetOf(&segmentInfo, &segmentInfo.pvMem) != GCInterfaceOffsets.OFFSETOF__segment_info__pvMem
                || OffsetOf(&segmentInfo, &segmentInfo.ibFirstObject) != GCInterfaceOffsets.OFFSETOF__segment_info__ibFirstObject
                || OffsetOf(&segmentInfo, &segmentInfo.ibAllocated) != GCInterfaceOffsets.OFFSETOF__segment_info__ibAllocated
                || OffsetOf(&segmentInfo, &segmentInfo.ibCommit) != GCInterfaceOffsets.OFFSETOF__segment_info__ibCommit
                || OffsetOf(&segmentInfo, &segmentInfo.ibReserved) != GCInterfaceOffsets.OFFSETOF__segment_info__ibReserved)
            {
                return false;
            }

            WriteBarrierParameters args;
            if (sizeof(WriteBarrierParameters) != GCInterfaceOffsets.SIZEOF__WriteBarrierParameters
                || AlignOf<WriteBarrierParameters>() != GCInterfaceOffsets.ALIGNOF__WriteBarrierParameters
                || OffsetOf(&args, &args.operation) != GCInterfaceOffsets.OFFSETOF__WriteBarrierParameters__operation
                || OffsetOf(&args, &args.is_runtime_suspended) != GCInterfaceOffsets.OFFSETOF__WriteBarrierParameters__is_runtime_suspended
                || OffsetOf(&args, &args.requires_upper_bounds_check) != GCInterfaceOffsets.OFFSETOF__WriteBarrierParameters__requires_upper_bounds_check
                || OffsetOf(&args, &args.card_table) != GCInterfaceOffsets.OFFSETOF__WriteBarrierParameters__card_table
                || OffsetOf(&args, &args.card_bundle_table) != GCInterfaceOffsets.OFFSETOF__WriteBarrierParameters__card_bundle_table
                || OffsetOf(&args, &args.lowest_address) != GCInterfaceOffsets.OFFSETOF__WriteBarrierParameters__lowest_address
                || OffsetOf(&args, &args.highest_address) != GCInterfaceOffsets.OFFSETOF__WriteBarrierParameters__highest_address
                || OffsetOf(&args, &args.ephemeral_low) != GCInterfaceOffsets.OFFSETOF__WriteBarrierParameters__ephemeral_low
                || OffsetOf(&args, &args.ephemeral_high) != GCInterfaceOffsets.OFFSETOF__WriteBarrierParameters__ephemeral_high
                || OffsetOf(&args, &args.write_watch_table) != GCInterfaceOffsets.OFFSETOF__WriteBarrierParameters__write_watch_table
                || OffsetOf(&args, &args.region_to_generation_table) != GCInterfaceOffsets.OFFSETOF__WriteBarrierParameters__region_to_generation_table
                || OffsetOf(&args, &args.region_shr) != GCInterfaceOffsets.OFFSETOF__WriteBarrierParameters__region_shr
                || OffsetOf(&args, &args.region_use_bitwise_write_barrier) != GCInterfaceOffsets.OFFSETOF__WriteBarrierParameters__region_use_bitwise_write_barrier)
            {
                return false;
            }

            FinalizerWorkItem workItem;
            if (sizeof(FinalizerWorkItem) != GCInterfaceOffsets.SIZEOF__FinalizerWorkItem
                || AlignOf<FinalizerWorkItem>() != GCInterfaceOffsets.ALIGNOF__FinalizerWorkItem
                || OffsetOf(&workItem, &workItem.next) != GCInterfaceOffsets.OFFSETOF__FinalizerWorkItem__next
                || OffsetOf(&workItem, &workItem.callback) != GCInterfaceOffsets.OFFSETOF__FinalizerWorkItem__callback)
            {
                return false;
            }

            NoGCRegionCallbackFinalizerWorkItem noGCWorkItem;
            if (sizeof(NoGCRegionCallbackFinalizerWorkItem) != GCInterfaceOffsets.SIZEOF__NoGCRegionCallbackFinalizerWorkItem
                || AlignOf<NoGCRegionCallbackFinalizerWorkItem>() != GCInterfaceOffsets.ALIGNOF__NoGCRegionCallbackFinalizerWorkItem
                || OffsetOf(&noGCWorkItem, &noGCWorkItem.scheduled) != GCInterfaceOffsets.OFFSETOF__NoGCRegionCallbackFinalizerWorkItem__scheduled
                || OffsetOf(&noGCWorkItem, &noGCWorkItem.abandoned) != GCInterfaceOffsets.OFFSETOF__NoGCRegionCallbackFinalizerWorkItem__abandoned)
            {
                return false;
            }

            // The base subobject the native type inherits from FinalizerWorkItem has to land
            // where FinalizerWorkItem itself does, since the EE passes one as the other.
            if (OffsetOf(&noGCWorkItem, &noGCWorkItem.next) != GCInterfaceOffsets.OFFSETOF__FinalizerWorkItem__next
                || OffsetOf(&noGCWorkItem, &noGCWorkItem.callback) != GCInterfaceOffsets.OFFSETOF__FinalizerWorkItem__callback)
            {
                return false;
            }

            EtwGCSettingsInfo settings;
            if (sizeof(EtwGCSettingsInfo) != GCInterfaceOffsets.SIZEOF__EtwGCSettingsInfo
                || AlignOf<EtwGCSettingsInfo>() != GCInterfaceOffsets.ALIGNOF__EtwGCSettingsInfo
                || OffsetOf(&settings, &settings.heap_hard_limit) != GCInterfaceOffsets.OFFSETOF__EtwGCSettingsInfo__heap_hard_limit
                || OffsetOf(&settings, &settings.loh_threshold) != GCInterfaceOffsets.OFFSETOF__EtwGCSettingsInfo__loh_threshold
                || OffsetOf(&settings, &settings.physical_memory_from_config) != GCInterfaceOffsets.OFFSETOF__EtwGCSettingsInfo__physical_memory_from_config
                || OffsetOf(&settings, &settings.gen0_min_budget_from_config) != GCInterfaceOffsets.OFFSETOF__EtwGCSettingsInfo__gen0_min_budget_from_config
                || OffsetOf(&settings, &settings.gen0_max_budget_from_config) != GCInterfaceOffsets.OFFSETOF__EtwGCSettingsInfo__gen0_max_budget_from_config
                || OffsetOf(&settings, &settings.high_mem_percent_from_config) != GCInterfaceOffsets.OFFSETOF__EtwGCSettingsInfo__high_mem_percent_from_config
                || OffsetOf(&settings, &settings.concurrent_gc_p) != GCInterfaceOffsets.OFFSETOF__EtwGCSettingsInfo__concurrent_gc_p
                || OffsetOf(&settings, &settings.use_large_pages_p) != GCInterfaceOffsets.OFFSETOF__EtwGCSettingsInfo__use_large_pages_p
                || OffsetOf(&settings, &settings.use_frozen_segments_p) != GCInterfaceOffsets.OFFSETOF__EtwGCSettingsInfo__use_frozen_segments_p
                || OffsetOf(&settings, &settings.hard_limit_config_p) != GCInterfaceOffsets.OFFSETOF__EtwGCSettingsInfo__hard_limit_config_p
                || OffsetOf(&settings, &settings.no_affinitize_p) != GCInterfaceOffsets.OFFSETOF__EtwGCSettingsInfo__no_affinitize_p)
            {
                return false;
            }

            StronglyConnectedComponent component;
            if (sizeof(StronglyConnectedComponent) != GCInterfaceOffsets.SIZEOF__StronglyConnectedComponent
                || AlignOf<StronglyConnectedComponent>() != GCInterfaceOffsets.ALIGNOF__StronglyConnectedComponent
                || OffsetOf(&component, &component.Count) != GCInterfaceOffsets.OFFSETOF__StronglyConnectedComponent__Count
                || OffsetOf(&component, &component.Contexts) != GCInterfaceOffsets.OFFSETOF__StronglyConnectedComponent__Contexts)
            {
                return false;
            }

            ComponentCrossReference crossReference;
            if (sizeof(ComponentCrossReference) != GCInterfaceOffsets.SIZEOF__ComponentCrossReference
                || AlignOf<ComponentCrossReference>() != GCInterfaceOffsets.ALIGNOF__ComponentCrossReference
                || OffsetOf(&crossReference, &crossReference.SourceGroupIndex) != GCInterfaceOffsets.OFFSETOF__ComponentCrossReference__SourceGroupIndex
                || OffsetOf(&crossReference, &crossReference.DestinationGroupIndex) != GCInterfaceOffsets.OFFSETOF__ComponentCrossReference__DestinationGroupIndex)
            {
                return false;
            }

            MarkCrossReferencesArgs crossReferences;
            if (sizeof(MarkCrossReferencesArgs) != GCInterfaceOffsets.SIZEOF__MarkCrossReferencesArgs
                || AlignOf<MarkCrossReferencesArgs>() != GCInterfaceOffsets.ALIGNOF__MarkCrossReferencesArgs
                || OffsetOf(&crossReferences, &crossReferences.ComponentCount) != GCInterfaceOffsets.OFFSETOF__MarkCrossReferencesArgs__ComponentCount
                || OffsetOf(&crossReferences, &crossReferences.Components) != GCInterfaceOffsets.OFFSETOF__MarkCrossReferencesArgs__Components
                || OffsetOf(&crossReferences, &crossReferences.CrossReferenceCount) != GCInterfaceOffsets.OFFSETOF__MarkCrossReferencesArgs__CrossReferenceCount
                || OffsetOf(&crossReferences, &crossReferences.CrossReferences) != GCInterfaceOffsets.OFFSETOF__MarkCrossReferencesArgs__CrossReferences)
            {
                return false;
            }

            ScanContext scanContext;
            if (sizeof(ScanContext) != GCInterfaceOffsets.SIZEOF__ScanContext
                || AlignOf<ScanContext>() != GCInterfaceOffsets.ALIGNOF__ScanContext
                || OffsetOf(&scanContext, &scanContext.thread_under_crawl) != GCInterfaceOffsets.OFFSETOF__ScanContext__thread_under_crawl
                || OffsetOf(&scanContext, &scanContext.thread_number) != GCInterfaceOffsets.OFFSETOF__ScanContext__thread_number
                || OffsetOf(&scanContext, &scanContext.thread_count) != GCInterfaceOffsets.OFFSETOF__ScanContext__thread_count
                || OffsetOf(&scanContext, &scanContext.stack_limit) != GCInterfaceOffsets.OFFSETOF__ScanContext__stack_limit
                || OffsetOf(&scanContext, &scanContext.promotion) != GCInterfaceOffsets.OFFSETOF__ScanContext__promotion
                || OffsetOf(&scanContext, &scanContext.concurrent) != GCInterfaceOffsets.OFFSETOF__ScanContext__concurrent
                || OffsetOf(&scanContext, &scanContext.pMD) != GCInterfaceOffsets.OFFSETOF__ScanContext__pMD)
            {
                return false;
            }

            // The last field of the native ScanContext is named dwEtwRootKind or _unused3
            // depending on whether the runtime is built with GC_PROFILING or FEATURE_EVENT_TRACE,
            // so the table cannot name it without producing a different C# constant per build.
            // Its offset is the same either way, which is what is checked here.
            if (OffsetOf(&scanContext, &scanContext.dwEtwRootKind) != GCInterfaceOffsets.OFFSETOF__ScanContext__pMD + sizeof(void*))
            {
                return false;
            }

            VersionInfo versionInfo;
            if (sizeof(VersionInfo) != GCInterfaceOffsets.SIZEOF__VersionInfo
                || AlignOf<VersionInfo>() != GCInterfaceOffsets.ALIGNOF__VersionInfo
                || OffsetOf(&versionInfo, &versionInfo.MajorVersion) != GCInterfaceOffsets.OFFSETOF__VersionInfo__MajorVersion
                || OffsetOf(&versionInfo, &versionInfo.MinorVersion) != GCInterfaceOffsets.OFFSETOF__VersionInfo__MinorVersion
                || OffsetOf(&versionInfo, &versionInfo.BuildVersion) != GCInterfaceOffsets.OFFSETOF__VersionInfo__BuildVersion
                || OffsetOf(&versionInfo, &versionInfo.Name) != GCInterfaceOffsets.OFFSETOF__VersionInfo__Name)
            {
                return false;
            }

            return true;
        }

        private static bool VerifyHandleTableTypes()
        {
            byte storage = 0;
            HandleTable* table = (HandleTable*)&storage;
            if (sizeof(HandleTable) != GCInterfaceOffsets.SIZEOF__HandleTable
                || AlignOf<HandleTable>() != GCInterfaceOffsets.ALIGNOF__HandleTable
                || OffsetOf(table, &table->rgTypeFlags[0]) != GCInterfaceOffsets.OFFSETOF__HandleTable__rgTypeFlags
                || OffsetOf(table, &table->pSegmentList) != GCInterfaceOffsets.OFFSETOF__HandleTable__pSegmentList
                || OffsetOf(table, &table->Lock) != GCInterfaceOffsets.OFFSETOF__HandleTable__Lock
                || OffsetOf(table, &table->uTypeCount) != GCInterfaceOffsets.OFFSETOF__HandleTable__uTypeCount
                || OffsetOf(table, &table->dwCount) != GCInterfaceOffsets.OFFSETOF__HandleTable__dwCount
                || OffsetOf(table, &table->pAsyncScanInfo) != GCInterfaceOffsets.OFFSETOF__HandleTable__pAsyncScanInfo
                || OffsetOf(table, &table->uTableIndex) != GCInterfaceOffsets.OFFSETOF__HandleTable__uTableIndex
                || OffsetOf(table, &table->rgQuickCache[0]) != GCInterfaceOffsets.OFFSETOF__HandleTable__rgQuickCache
#if DEBUG
                || OffsetOf(table, &table->_DEBUG_iMaxGen) != GCInterfaceOffsets.OFFSETOF__HandleTable___DEBUG_iMaxGen
                || OffsetOf(table, &table->_DEBUG_TotalBlocksScanned[0]) != GCInterfaceOffsets.OFFSETOF__HandleTable___DEBUG_TotalBlocksScanned
                || OffsetOf(table, &table->_DEBUG_TotalBlocksScannedNonTrivially[0]) != GCInterfaceOffsets.OFFSETOF__HandleTable___DEBUG_TotalBlocksScannedNonTrivially
                || OffsetOf(table, &table->_DEBUG_TotalHandleSlotsScanned[0]) != GCInterfaceOffsets.OFFSETOF__HandleTable___DEBUG_TotalHandleSlotsScanned
                || OffsetOf(table, &table->_DEBUG_TotalHandlesActuallyScanned[0]) != GCInterfaceOffsets.OFFSETOF__HandleTable___DEBUG_TotalHandlesActuallyScanned
#endif
                || HandleTableConstants.HNDF_NORMAL != GCInterfaceOffsets.HNDF_NORMAL
                || HandleTableConstants.HNDF_EXTRAINFO != GCInterfaceOffsets.HNDF_EXTRAINFO)
            {
                return false;
            }

            _TableSegmentHeader* header = (_TableSegmentHeader*)&storage;
            if (sizeof(_TableSegmentHeader) != GCInterfaceOffsets.SIZEOF___TableSegmentHeader
                || AlignOf<_TableSegmentHeader>() != GCInterfaceOffsets.ALIGNOF___TableSegmentHeader
                || OffsetOf(header, &header->rgGeneration[0]) != GCInterfaceOffsets.OFFSETOF___TableSegmentHeader__rgGeneration
                || OffsetOf(header, &header->rgAllocation[0]) != GCInterfaceOffsets.OFFSETOF___TableSegmentHeader__rgAllocation
                || OffsetOf(header, &header->rgFreeMask[0]) != GCInterfaceOffsets.OFFSETOF___TableSegmentHeader__rgFreeMask
                || OffsetOf(header, &header->rgBlockType[0]) != GCInterfaceOffsets.OFFSETOF___TableSegmentHeader__rgBlockType
                || OffsetOf(header, &header->rgUserData[0]) != GCInterfaceOffsets.OFFSETOF___TableSegmentHeader__rgUserData
                || OffsetOf(header, &header->rgLocks[0]) != GCInterfaceOffsets.OFFSETOF___TableSegmentHeader__rgLocks
                || OffsetOf(header, &header->rgTail[0]) != GCInterfaceOffsets.OFFSETOF___TableSegmentHeader__rgTail
                || OffsetOf(header, &header->rgHint[0]) != GCInterfaceOffsets.OFFSETOF___TableSegmentHeader__rgHint
                || OffsetOf(header, &header->rgFreeCount[0]) != GCInterfaceOffsets.OFFSETOF___TableSegmentHeader__rgFreeCount
                || OffsetOf(header, &header->pNextSegment) != GCInterfaceOffsets.OFFSETOF___TableSegmentHeader__pNextSegment
                || OffsetOf(header, &header->pHandleTable) != GCInterfaceOffsets.OFFSETOF___TableSegmentHeader__pHandleTable
                || OffsetOf(header, &header->bFreeList) != GCInterfaceOffsets.OFFSETOF___TableSegmentHeader__bFreeList
                || OffsetOf(header, &header->bEmptyLine) != GCInterfaceOffsets.OFFSETOF___TableSegmentHeader__bEmptyLine
                || OffsetOf(header, &header->bCommitLine) != GCInterfaceOffsets.OFFSETOF___TableSegmentHeader__bCommitLine
                || OffsetOf(header, &header->bDecommitLine) != GCInterfaceOffsets.OFFSETOF___TableSegmentHeader__bDecommitLine
                || OffsetOf(header, &header->bSequence) != GCInterfaceOffsets.OFFSETOF___TableSegmentHeader__bSequence)
            {
                return false;
            }

            TableSegment* segment = (TableSegment*)&storage;
            if (sizeof(TableSegment) != GCInterfaceOffsets.SIZEOF__TableSegment
                || AlignOf<TableSegment>() != GCInterfaceOffsets.ALIGNOF__TableSegment
                || OffsetOf(segment, &segment->rgUnused[0]) != GCInterfaceOffsets.OFFSETOF__TableSegment__rgUnused
                || OffsetOf(segment, &segment->rgValue[0]) != GCInterfaceOffsets.OFFSETOF__TableSegment__rgValue)
            {
                return false;
            }

            HandleTypeCache* cache = (HandleTypeCache*)&storage;
            if (sizeof(HandleTypeCache) != GCInterfaceOffsets.SIZEOF__HandleTypeCache
                || AlignOf<HandleTypeCache>() != GCInterfaceOffsets.ALIGNOF__HandleTypeCache
                || OffsetOf(cache, &cache->rgReserveBank[0]) != GCInterfaceOffsets.OFFSETOF__HandleTypeCache__rgReserveBank
                || OffsetOf(cache, &cache->lReserveIndex) != GCInterfaceOffsets.OFFSETOF__HandleTypeCache__lReserveIndex
                || OffsetOf(cache, &cache->rgFreeBank[0]) != GCInterfaceOffsets.OFFSETOF__HandleTypeCache__rgFreeBank
                || OffsetOf(cache, &cache->lFreeIndex) != GCInterfaceOffsets.OFFSETOF__HandleTypeCache__lFreeIndex)
            {
                return false;
            }

            return true;
        }

        private static bool VerifyGCDescTypes()
        {
            val_serie_item item;
            if (sizeof(val_serie_item) != GCInterfaceOffsets.SIZEOF__val_serie_item
                || AlignOf<val_serie_item>() != GCInterfaceOffsets.ALIGNOF__val_serie_item
                || OffsetOf(&item, &item.nptrs) != GCInterfaceOffsets.OFFSETOF__val_serie_item__nptrs
                || OffsetOf(&item, &item.skip) != GCInterfaceOffsets.OFFSETOF__val_serie_item__skip)
            {
                return false;
            }

            CGCDescSeries series;
            if (sizeof(CGCDescSeries) != GCInterfaceOffsets.SIZEOF__CGCDescSeries
                || AlignOf<CGCDescSeries>() != GCInterfaceOffsets.ALIGNOF__CGCDescSeries
                || OffsetOf(&series, &series.seriessize) != GCInterfaceOffsets.OFFSETOF__CGCDescSeries__seriessize
                || OffsetOf(&series, &series.val_serie) != GCInterfaceOffsets.OFFSETOF__CGCDescSeries__val_serie
                || OffsetOf(&series, &series.startoffset) != GCInterfaceOffsets.OFFSETOF__CGCDescSeries__startoffset)
            {
                return false;
            }

            return true;
        }

        private static bool VerifyGCRecordTypes()
        {
            gc_history_per_heap history;
            if (sizeof(gc_history_per_heap) != GCInterfaceOffsets.SIZEOF__gc_history_per_heap
                || AlignOf<gc_history_per_heap>() != GCInterfaceOffsets.ALIGNOF__gc_history_per_heap
                || OffsetOf(&history, &history.gen_data0) != GCInterfaceOffsets.OFFSETOF__gc_history_per_heap__gen_data
                || OffsetOf(&history, &history.gen_data1) != GCInterfaceOffsets.OFFSETOF__gc_history_per_heap__gen_data + sizeof(gc_generation_data)
                || OffsetOf(&history, &history.gen_data2) != GCInterfaceOffsets.OFFSETOF__gc_history_per_heap__gen_data + (2 * sizeof(gc_generation_data))
                || OffsetOf(&history, &history.gen_data3) != GCInterfaceOffsets.OFFSETOF__gc_history_per_heap__gen_data + (3 * sizeof(gc_generation_data))
                || OffsetOf(&history, &history.gen_data4) != GCInterfaceOffsets.OFFSETOF__gc_history_per_heap__gen_data + (4 * sizeof(gc_generation_data))
                || OffsetOf(&history, &history.maxgen_size_info) != GCInterfaceOffsets.OFFSETOF__gc_history_per_heap__maxgen_size_info
                || OffsetOf(&history, &history.gen_to_condemn_reasons) != GCInterfaceOffsets.OFFSETOF__gc_history_per_heap__gen_to_condemn_reasons
                || OffsetOf(&history, &history.mechanisms[0]) != GCInterfaceOffsets.OFFSETOF__gc_history_per_heap__mechanisms
                || OffsetOf(&history, &history.machanism_bits) != GCInterfaceOffsets.OFFSETOF__gc_history_per_heap__machanism_bits
                || OffsetOf(&history, &history.heap_index) != GCInterfaceOffsets.OFFSETOF__gc_history_per_heap__heap_index
                || OffsetOf(&history, &history.extra_gen0_committed) != GCInterfaceOffsets.OFFSETOF__gc_history_per_heap__extra_gen0_committed)
            {
                return false;
            }

            gc_history_global globalHistory;
            if (sizeof(gc_history_global) != GCInterfaceOffsets.SIZEOF__gc_history_global
                || AlignOf<gc_history_global>() != GCInterfaceOffsets.ALIGNOF__gc_history_global
                || OffsetOf(&globalHistory, &globalHistory.final_youngest_desired) != GCInterfaceOffsets.OFFSETOF__gc_history_global__final_youngest_desired
                || OffsetOf(&globalHistory, &globalHistory.num_heaps) != GCInterfaceOffsets.OFFSETOF__gc_history_global__num_heaps
                || OffsetOf(&globalHistory, &globalHistory.condemned_generation) != GCInterfaceOffsets.OFFSETOF__gc_history_global__condemned_generation
                || OffsetOf(&globalHistory, &globalHistory.gen0_reduction_count) != GCInterfaceOffsets.OFFSETOF__gc_history_global__gen0_reduction_count
                || OffsetOf(&globalHistory, &globalHistory.reason) != GCInterfaceOffsets.OFFSETOF__gc_history_global__reason
                || OffsetOf(&globalHistory, &globalHistory.pause_mode) != GCInterfaceOffsets.OFFSETOF__gc_history_global__pause_mode
                || OffsetOf(&globalHistory, &globalHistory.mem_pressure) != GCInterfaceOffsets.OFFSETOF__gc_history_global__mem_pressure
                || OffsetOf(&globalHistory, &globalHistory.global_mechanisms_p) != GCInterfaceOffsets.OFFSETOF__gc_history_global__global_mechanisms_p
                || OffsetOf(&globalHistory, &globalHistory.gen_to_condemn_reasons) != GCInterfaceOffsets.OFFSETOF__gc_history_global__gen_to_condemn_reasons)
            {
                return false;
            }

            return true;
        }

        private static bool VerifyGCPrivTypes()
        {
            mark markValue;
            if (sizeof(mark) != GCInterfaceOffsets.SIZEOF__mark
                || AlignOf<mark>() != GCInterfaceOffsets.ALIGNOF__mark
                || OffsetOf(&markValue, &markValue.first) != GCInterfaceOffsets.OFFSETOF__mark__first
                || OffsetOf(&markValue, &markValue.len) != GCInterfaceOffsets.OFFSETOF__mark__len
                || OffsetOf(&markValue, &markValue.saved_pre_plug) != GCInterfaceOffsets.OFFSETOF__mark__saved_pre_plug
                || OffsetOf(&markValue, &markValue.saved_pre_plug_reloc) != GCInterfaceOffsets.OFFSETOF__mark__saved_pre_plug_reloc
                || OffsetOf(&markValue, &markValue.saved_post_plug) != GCInterfaceOffsets.OFFSETOF__mark__saved_post_plug
                || OffsetOf(&markValue, &markValue.saved_post_plug_reloc) != GCInterfaceOffsets.OFFSETOF__mark__saved_post_plug_reloc
                || OffsetOf(&markValue, &markValue.saved_pre_plug_info_reloc_start) != GCInterfaceOffsets.OFFSETOF__mark__saved_pre_plug_info_reloc_start
                || OffsetOf(&markValue, &markValue.saved_post_plug_info_start) != GCInterfaceOffsets.OFFSETOF__mark__saved_post_plug_info_start
                || OffsetOf(&markValue, &markValue.allocation_context_start_region) != GCInterfaceOffsets.OFFSETOF__mark__allocation_context_start_region
                || OffsetOf(&markValue, &markValue.saved_pre_p) != GCInterfaceOffsets.OFFSETOF__mark__saved_pre_p
                || OffsetOf(&markValue, &markValue.saved_post_p) != GCInterfaceOffsets.OFFSETOF__mark__saved_post_p
#if DEBUG
                || OffsetOf(&markValue, &markValue.saved_post_plug_debug) != GCInterfaceOffsets.OFFSETOF__mark__saved_post_plug_debug
#endif
                )
            {
                return false;
            }

            static_data staticData;
            if (sizeof(static_data) != GCInterfaceOffsets.SIZEOF__static_data
                || AlignOf<static_data>() != GCInterfaceOffsets.ALIGNOF__static_data
                || OffsetOf(&staticData, &staticData.min_size) != GCInterfaceOffsets.OFFSETOF__static_data__min_size
                || OffsetOf(&staticData, &staticData.max_size) != GCInterfaceOffsets.OFFSETOF__static_data__max_size
                || OffsetOf(&staticData, &staticData.fragmentation_limit) != GCInterfaceOffsets.OFFSETOF__static_data__fragmentation_limit
                || OffsetOf(&staticData, &staticData.fragmentation_burden_limit) != GCInterfaceOffsets.OFFSETOF__static_data__fragmentation_burden_limit
                || OffsetOf(&staticData, &staticData.limit) != GCInterfaceOffsets.OFFSETOF__static_data__limit
                || OffsetOf(&staticData, &staticData.max_limit) != GCInterfaceOffsets.OFFSETOF__static_data__max_limit
                || OffsetOf(&staticData, &staticData.time_clock) != GCInterfaceOffsets.OFFSETOF__static_data__time_clock
                || OffsetOf(&staticData, &staticData.gc_clock) != GCInterfaceOffsets.OFFSETOF__static_data__gc_clock)
            {
                return false;
            }

            dynamic_data dynamicData;
            if (sizeof(dynamic_data) != GCInterfaceOffsets.SIZEOF__dynamic_data
                || AlignOf<dynamic_data>() != GCInterfaceOffsets.ALIGNOF__dynamic_data
                || OffsetOf(&dynamicData, &dynamicData.new_allocation) != GCInterfaceOffsets.OFFSETOF__dynamic_data__new_allocation
                || OffsetOf(&dynamicData, &dynamicData.gc_new_allocation) != GCInterfaceOffsets.OFFSETOF__dynamic_data__gc_new_allocation
                || OffsetOf(&dynamicData, &dynamicData.surv) != GCInterfaceOffsets.OFFSETOF__dynamic_data__surv
                || OffsetOf(&dynamicData, &dynamicData.desired_allocation) != GCInterfaceOffsets.OFFSETOF__dynamic_data__desired_allocation
                || OffsetOf(&dynamicData, &dynamicData.begin_data_size) != GCInterfaceOffsets.OFFSETOF__dynamic_data__begin_data_size
                || OffsetOf(&dynamicData, &dynamicData.survived_size) != GCInterfaceOffsets.OFFSETOF__dynamic_data__survived_size
                || OffsetOf(&dynamicData, &dynamicData.pinned_survived_size) != GCInterfaceOffsets.OFFSETOF__dynamic_data__pinned_survived_size
                || OffsetOf(&dynamicData, &dynamicData.artificial_pinned_survived_size) != GCInterfaceOffsets.OFFSETOF__dynamic_data__artificial_pinned_survived_size
                || OffsetOf(&dynamicData, &dynamicData.added_pinned_size) != GCInterfaceOffsets.OFFSETOF__dynamic_data__added_pinned_size
                || OffsetOf(&dynamicData, &dynamicData.padding_size) != GCInterfaceOffsets.OFFSETOF__dynamic_data__padding_size
#if TARGET_ARM || TARGET_WASM
                || OffsetOf(&dynamicData, &dynamicData.num_npinned_plugs) != GCInterfaceOffsets.OFFSETOF__dynamic_data__num_npinned_plugs
#endif
                || OffsetOf(&dynamicData, &dynamicData.current_size) != GCInterfaceOffsets.OFFSETOF__dynamic_data__current_size
                || OffsetOf(&dynamicData, &dynamicData.collection_count) != GCInterfaceOffsets.OFFSETOF__dynamic_data__collection_count
                || OffsetOf(&dynamicData, &dynamicData.promoted_size) != GCInterfaceOffsets.OFFSETOF__dynamic_data__promoted_size
                || OffsetOf(&dynamicData, &dynamicData.freach_previous_promotion) != GCInterfaceOffsets.OFFSETOF__dynamic_data__freach_previous_promotion
                || OffsetOf(&dynamicData, &dynamicData.fragmentation) != GCInterfaceOffsets.OFFSETOF__dynamic_data__fragmentation
                || OffsetOf(&dynamicData, &dynamicData.gc_clock) != GCInterfaceOffsets.OFFSETOF__dynamic_data__gc_clock
                || OffsetOf(&dynamicData, &dynamicData.time_clock) != GCInterfaceOffsets.OFFSETOF__dynamic_data__time_clock
                || OffsetOf(&dynamicData, &dynamicData.previous_time_clock) != GCInterfaceOffsets.OFFSETOF__dynamic_data__previous_time_clock
                || OffsetOf(&dynamicData, &dynamicData.gc_elapsed_time) != GCInterfaceOffsets.OFFSETOF__dynamic_data__gc_elapsed_time
                || OffsetOf(&dynamicData, &dynamicData.min_size) != GCInterfaceOffsets.OFFSETOF__dynamic_data__min_size
                || OffsetOf(&dynamicData, &dynamicData.sdata) != GCInterfaceOffsets.OFFSETOF__dynamic_data__sdata)
            {
                return false;
            }

            recorded_generation_info generationInfo;
            if (sizeof(recorded_generation_info) != GCInterfaceOffsets.SIZEOF__recorded_generation_info
                || AlignOf<recorded_generation_info>() != GCInterfaceOffsets.ALIGNOF__recorded_generation_info
                || OffsetOf(&generationInfo, &generationInfo.size_before) != GCInterfaceOffsets.OFFSETOF__recorded_generation_info__size_before
                || OffsetOf(&generationInfo, &generationInfo.fragmentation_before) != GCInterfaceOffsets.OFFSETOF__recorded_generation_info__fragmentation_before
                || OffsetOf(&generationInfo, &generationInfo.size_after) != GCInterfaceOffsets.OFFSETOF__recorded_generation_info__size_after
                || OffsetOf(&generationInfo, &generationInfo.fragmentation_after) != GCInterfaceOffsets.OFFSETOF__recorded_generation_info__fragmentation_after)
            {
                return false;
            }

            last_recorded_gc_info recordedInfo;
            if (sizeof(last_recorded_gc_info) != GCInterfaceOffsets.SIZEOF__last_recorded_gc_info
                || AlignOf<last_recorded_gc_info>() != GCInterfaceOffsets.ALIGNOF__last_recorded_gc_info
                || OffsetOf(&recordedInfo, &recordedInfo.index) != GCInterfaceOffsets.OFFSETOF__last_recorded_gc_info__index
                || OffsetOf(&recordedInfo, &recordedInfo.total_committed) != GCInterfaceOffsets.OFFSETOF__last_recorded_gc_info__total_committed
                || OffsetOf(&recordedInfo, &recordedInfo.promoted) != GCInterfaceOffsets.OFFSETOF__last_recorded_gc_info__promoted
                || OffsetOf(&recordedInfo, &recordedInfo.pinned_objects) != GCInterfaceOffsets.OFFSETOF__last_recorded_gc_info__pinned_objects
                || OffsetOf(&recordedInfo, &recordedInfo.finalize_promoted_objects) != GCInterfaceOffsets.OFFSETOF__last_recorded_gc_info__finalize_promoted_objects
                || OffsetOf(&recordedInfo, &recordedInfo.pause_durations0) != GCInterfaceOffsets.OFFSETOF__last_recorded_gc_info__pause_durations
                || OffsetOf(&recordedInfo, &recordedInfo.pause_durations1) != GCInterfaceOffsets.OFFSETOF__last_recorded_gc_info__pause_durations + sizeof(nuint)
                || OffsetOf(&recordedInfo, &recordedInfo.pause_percentage) != GCInterfaceOffsets.OFFSETOF__last_recorded_gc_info__pause_percentage
                || OffsetOf(&recordedInfo, &recordedInfo.gen_info0) != GCInterfaceOffsets.OFFSETOF__last_recorded_gc_info__gen_info
                || OffsetOf(&recordedInfo, &recordedInfo.gen_info1) != GCInterfaceOffsets.OFFSETOF__last_recorded_gc_info__gen_info + sizeof(recorded_generation_info)
                || OffsetOf(&recordedInfo, &recordedInfo.gen_info2) != GCInterfaceOffsets.OFFSETOF__last_recorded_gc_info__gen_info + (2 * sizeof(recorded_generation_info))
                || OffsetOf(&recordedInfo, &recordedInfo.gen_info3) != GCInterfaceOffsets.OFFSETOF__last_recorded_gc_info__gen_info + (3 * sizeof(recorded_generation_info))
                || OffsetOf(&recordedInfo, &recordedInfo.gen_info4) != GCInterfaceOffsets.OFFSETOF__last_recorded_gc_info__gen_info + (4 * sizeof(recorded_generation_info))
                || OffsetOf(&recordedInfo, &recordedInfo.heap_size) != GCInterfaceOffsets.OFFSETOF__last_recorded_gc_info__heap_size
                || OffsetOf(&recordedInfo, &recordedInfo.fragmentation) != GCInterfaceOffsets.OFFSETOF__last_recorded_gc_info__fragmentation
                || OffsetOf(&recordedInfo, &recordedInfo.memory_load) != GCInterfaceOffsets.OFFSETOF__last_recorded_gc_info__memory_load
                || OffsetOf(&recordedInfo, &recordedInfo.condemned_generation) != GCInterfaceOffsets.OFFSETOF__last_recorded_gc_info__condemned_generation
                || OffsetOf(&recordedInfo, &recordedInfo.compaction) != GCInterfaceOffsets.OFFSETOF__last_recorded_gc_info__compaction
                || OffsetOf(&recordedInfo, &recordedInfo.concurrent) != GCInterfaceOffsets.OFFSETOF__last_recorded_gc_info__concurrent)
            {
                return false;
            }

            etw_opt_info optInfo;
            if (sizeof(etw_opt_info) != GCInterfaceOffsets.SIZEOF__etw_opt_info
                || AlignOf<etw_opt_info>() != GCInterfaceOffsets.ALIGNOF__etw_opt_info
                || OffsetOf(&optInfo, &optInfo.desired_allocation) != GCInterfaceOffsets.OFFSETOF__etw_opt_info__desired_allocation
                || OffsetOf(&optInfo, &optInfo.new_allocation) != GCInterfaceOffsets.OFFSETOF__etw_opt_info__new_allocation
                || OffsetOf(&optInfo, &optInfo.gen_number) != GCInterfaceOffsets.OFFSETOF__etw_opt_info__gen_number)
            {
                return false;
            }

            if (sizeof(alloc_list) != GCInterfaceOffsets.SIZEOF__alloc_list
                || AlignOf<alloc_list>() != GCInterfaceOffsets.ALIGNOF__alloc_list)
            {
                return false;
            }

            if (sizeof(allocator) != GCInterfaceOffsets.SIZEOF__allocator
                || AlignOf<allocator>() != GCInterfaceOffsets.ALIGNOF__allocator)
            {
                return false;
            }

            generation gen;
            if (sizeof(generation) != GCInterfaceOffsets.SIZEOF__generation
                || AlignOf<generation>() != GCInterfaceOffsets.ALIGNOF__generation
                || OffsetOf(&gen, &gen.allocation_context) != GCInterfaceOffsets.OFFSETOF__generation__allocation_context
                || OffsetOf(&gen, &gen.start_segment) != GCInterfaceOffsets.OFFSETOF__generation__start_segment
#if !USE_REGIONS
                || OffsetOf(&gen, &gen.allocation_start) != GCInterfaceOffsets.OFFSETOF__generation__allocation_start
#endif
                || OffsetOf(&gen, &gen.allocation_segment) != GCInterfaceOffsets.OFFSETOF__generation__allocation_segment
                || OffsetOf(&gen, &gen.allocation_context_start_region) != GCInterfaceOffsets.OFFSETOF__generation__allocation_context_start_region
#if USE_REGIONS
                || OffsetOf(&gen, &gen.tail_region) != GCInterfaceOffsets.OFFSETOF__generation__tail_region
                || OffsetOf(&gen, &gen.tail_ro_region) != GCInterfaceOffsets.OFFSETOF__generation__tail_ro_region
#endif
                || OffsetOf(&gen, &gen.free_list_allocator) != GCInterfaceOffsets.OFFSETOF__generation__free_list_allocator
                || OffsetOf(&gen, &gen.free_list_allocated) != GCInterfaceOffsets.OFFSETOF__generation__free_list_allocated
                || OffsetOf(&gen, &gen.end_seg_allocated) != GCInterfaceOffsets.OFFSETOF__generation__end_seg_allocated
                || OffsetOf(&gen, &gen.condemned_allocated) != GCInterfaceOffsets.OFFSETOF__generation__condemned_allocated
                || OffsetOf(&gen, &gen.sweep_allocated) != GCInterfaceOffsets.OFFSETOF__generation__sweep_allocated
                || OffsetOf(&gen, &gen.allocate_end_seg_p) != GCInterfaceOffsets.OFFSETOF__generation__allocate_end_seg_p
                || OffsetOf(&gen, &gen.free_list_space) != GCInterfaceOffsets.OFFSETOF__generation__free_list_space
                || OffsetOf(&gen, &gen.free_obj_space) != GCInterfaceOffsets.OFFSETOF__generation__free_obj_space
                || OffsetOf(&gen, &gen.allocation_size) != GCInterfaceOffsets.OFFSETOF__generation__allocation_size
#if !USE_REGIONS
                || OffsetOf(&gen, &gen.plan_allocation_start) != GCInterfaceOffsets.OFFSETOF__generation__plan_allocation_start
                || OffsetOf(&gen, &gen.plan_allocation_start_size) != GCInterfaceOffsets.OFFSETOF__generation__plan_allocation_start_size
#endif
                || OffsetOf(&gen, &gen.pinned_allocation_compact_size) != GCInterfaceOffsets.OFFSETOF__generation__pinned_allocation_compact_size
                || OffsetOf(&gen, &gen.pinned_allocation_sweep_size) != GCInterfaceOffsets.OFFSETOF__generation__pinned_allocation_sweep_size
                || OffsetOf(&gen, &gen.gen_num) != GCInterfaceOffsets.OFFSETOF__generation__gen_num
#if TARGET_64BIT && !TARGET_WASM
                || OffsetOf(&gen, &gen.set_bgc_mark_bit_p) != GCInterfaceOffsets.OFFSETOF__generation__set_bgc_mark_bit_p
                || OffsetOf(&gen, &gen.last_free_list_allocated) != GCInterfaceOffsets.OFFSETOF__generation__last_free_list_allocated
#endif
                )
            {
                return false;
            }

#if !TARGET_WASM
            etw_bucket_info bucketInfo;
            if (sizeof(etw_bucket_info) != GCInterfaceOffsets.SIZEOF__etw_bucket_info
                || AlignOf<etw_bucket_info>() != GCInterfaceOffsets.ALIGNOF__etw_bucket_info
                || OffsetOf(&bucketInfo, &bucketInfo.index) != GCInterfaceOffsets.OFFSETOF__etw_bucket_info__index
                || OffsetOf(&bucketInfo, &bucketInfo.count) != GCInterfaceOffsets.OFFSETOF__etw_bucket_info__count
                || OffsetOf(&bucketInfo, &bucketInfo.size) != GCInterfaceOffsets.OFFSETOF__etw_bucket_info__size)
            {
                return false;
            }
#endif

            alloc_thread_wait_data waitData;
            if (sizeof(alloc_thread_wait_data) != GCInterfaceOffsets.SIZEOF__alloc_thread_wait_data
                || AlignOf<alloc_thread_wait_data>() != GCInterfaceOffsets.ALIGNOF__alloc_thread_wait_data
                || OffsetOf(&waitData, &waitData.awr) != GCInterfaceOffsets.OFFSETOF__alloc_thread_wait_data__awr)
            {
                return false;
            }

            no_gc_region_info noGCInfo;
            if (sizeof(no_gc_region_info) != GCInterfaceOffsets.SIZEOF__no_gc_region_info
                || AlignOf<no_gc_region_info>() != GCInterfaceOffsets.ALIGNOF__no_gc_region_info
                || OffsetOf(&noGCInfo, &noGCInfo.soh_allocation_size) != GCInterfaceOffsets.OFFSETOF__no_gc_region_info__soh_allocation_size
                || OffsetOf(&noGCInfo, &noGCInfo.loh_allocation_size) != GCInterfaceOffsets.OFFSETOF__no_gc_region_info__loh_allocation_size
                || OffsetOf(&noGCInfo, &noGCInfo.started) != GCInterfaceOffsets.OFFSETOF__no_gc_region_info__started
                || OffsetOf(&noGCInfo, &noGCInfo.num_gcs) != GCInterfaceOffsets.OFFSETOF__no_gc_region_info__num_gcs
                || OffsetOf(&noGCInfo, &noGCInfo.num_gcs_induced) != GCInterfaceOffsets.OFFSETOF__no_gc_region_info__num_gcs_induced
                || OffsetOf(&noGCInfo, &noGCInfo.start_status) != GCInterfaceOffsets.OFFSETOF__no_gc_region_info__start_status
                || OffsetOf(&noGCInfo, &noGCInfo.saved_pause_mode) != GCInterfaceOffsets.OFFSETOF__no_gc_region_info__saved_pause_mode
                || OffsetOf(&noGCInfo, &noGCInfo.saved_gen0_min_size) != GCInterfaceOffsets.OFFSETOF__no_gc_region_info__saved_gen0_min_size
                || OffsetOf(&noGCInfo, &noGCInfo.saved_gen3_min_size) != GCInterfaceOffsets.OFFSETOF__no_gc_region_info__saved_gen3_min_size
                || OffsetOf(&noGCInfo, &noGCInfo.minimal_gc_p) != GCInterfaceOffsets.OFFSETOF__no_gc_region_info__minimal_gc_p
                || OffsetOf(&noGCInfo, &noGCInfo.soh_withheld_budget) != GCInterfaceOffsets.OFFSETOF__no_gc_region_info__soh_withheld_budget
                || OffsetOf(&noGCInfo, &noGCInfo.loh_withheld_budget) != GCInterfaceOffsets.OFFSETOF__no_gc_region_info__loh_withheld_budget
                || OffsetOf(&noGCInfo, &noGCInfo.callback) != GCInterfaceOffsets.OFFSETOF__no_gc_region_info__callback)
            {
                return false;
            }

#if USE_REGIONS
            generation_region_info regionInfo;
            if (sizeof(generation_region_info) != GCInterfaceOffsets.SIZEOF__generation_region_info
                || AlignOf<generation_region_info>() != GCInterfaceOffsets.ALIGNOF__generation_region_info
                || OffsetOf(&regionInfo, &regionInfo.head) != GCInterfaceOffsets.OFFSETOF__generation_region_info__head
                || OffsetOf(&regionInfo, &regionInfo.tail) != GCInterfaceOffsets.OFFSETOF__generation_region_info__tail)
            {
                return false;
            }
#endif

            heap_segment heapSegment;
            if (sizeof(heap_segment) != GCInterfaceOffsets.SIZEOF__heap_segment
                || AlignOf<heap_segment>() != GCInterfaceOffsets.ALIGNOF__heap_segment
                || OffsetOf(&heapSegment, &heapSegment.allocated) != GCInterfaceOffsets.OFFSETOF__heap_segment__allocated
                || OffsetOf(&heapSegment, &heapSegment.committed) != GCInterfaceOffsets.OFFSETOF__heap_segment__committed
                || OffsetOf(&heapSegment, &heapSegment.reserved) != GCInterfaceOffsets.OFFSETOF__heap_segment__reserved
                || OffsetOf(&heapSegment, &heapSegment.used) != GCInterfaceOffsets.OFFSETOF__heap_segment__used
                || OffsetOf(&heapSegment, &heapSegment.mem) != GCInterfaceOffsets.OFFSETOF__heap_segment__mem
                || OffsetOf(&heapSegment, &heapSegment.flags) != GCInterfaceOffsets.OFFSETOF__heap_segment__flags
                || OffsetOf(&heapSegment, &heapSegment.next) != GCInterfaceOffsets.OFFSETOF__heap_segment__next
                || OffsetOf(&heapSegment, &heapSegment.background_allocated) != GCInterfaceOffsets.OFFSETOF__heap_segment__background_allocated
#if MULTIPLE_HEAPS
                || OffsetOf(&heapSegment, &heapSegment.heap) != GCInterfaceOffsets.OFFSETOF__heap_segment__heap
#if DEBUG && !USE_REGIONS
                || OffsetOf(&heapSegment, &heapSegment.saved_committed) != GCInterfaceOffsets.OFFSETOF__heap_segment__saved_committed
                || OffsetOf(&heapSegment, &heapSegment.saved_desired_allocation) != GCInterfaceOffsets.OFFSETOF__heap_segment__saved_desired_allocation
#endif
#endif
#if !USE_REGIONS || MULTIPLE_HEAPS
                || OffsetOf(&heapSegment, &heapSegment.decommit_target) != GCInterfaceOffsets.OFFSETOF__heap_segment__decommit_target
#endif
                || OffsetOf(&heapSegment, &heapSegment.plan_allocated) != GCInterfaceOffsets.OFFSETOF__heap_segment__plan_allocated
                || OffsetOf(&heapSegment, &heapSegment.saved_allocated) != GCInterfaceOffsets.OFFSETOF__heap_segment__saved_allocated
                || OffsetOf(&heapSegment, &heapSegment.saved_bg_allocated) != GCInterfaceOffsets.OFFSETOF__heap_segment__saved_bg_allocated
#if USE_REGIONS
                || OffsetOf(&heapSegment, &heapSegment.survived) != GCInterfaceOffsets.OFFSETOF__heap_segment__survived
                || OffsetOf(&heapSegment, &heapSegment.gen_num) != GCInterfaceOffsets.OFFSETOF__heap_segment__gen_num
                || OffsetOf(&heapSegment, &heapSegment.swept_in_plan_p) != GCInterfaceOffsets.OFFSETOF__heap_segment__swept_in_plan_p
                || OffsetOf(&heapSegment, &heapSegment.plan_gen_num) != GCInterfaceOffsets.OFFSETOF__heap_segment__plan_gen_num
                || OffsetOf(&heapSegment, &heapSegment.old_card_survived) != GCInterfaceOffsets.OFFSETOF__heap_segment__old_card_survived
                || OffsetOf(&heapSegment, &heapSegment.pinned_survived) != GCInterfaceOffsets.OFFSETOF__heap_segment__pinned_survived
                || OffsetOf(&heapSegment, &heapSegment.age_in_free) != GCInterfaceOffsets.OFFSETOF__heap_segment__age_in_free
                || OffsetOf(&heapSegment, &heapSegment.free_list_head) != GCInterfaceOffsets.OFFSETOF__heap_segment__free_list_head
                || OffsetOf(&heapSegment, &heapSegment.free_list_tail) != GCInterfaceOffsets.OFFSETOF__heap_segment__free_list_tail
                || OffsetOf(&heapSegment, &heapSegment.free_list_size) != GCInterfaceOffsets.OFFSETOF__heap_segment__free_list_size
                || OffsetOf(&heapSegment, &heapSegment.free_obj_size) != GCInterfaceOffsets.OFFSETOF__heap_segment__free_obj_size
                || OffsetOf(&heapSegment, &heapSegment.prev_free_region) != GCInterfaceOffsets.OFFSETOF__heap_segment__prev_free_region
                || OffsetOf(&heapSegment, &heapSegment.containing_free_list) != GCInterfaceOffsets.OFFSETOF__heap_segment__containing_free_list
#else
                || OffsetOf(&heapSegment, &heapSegment.padandplug) != GCInterfaceOffsets.OFFSETOF__heap_segment__padandplug
#endif
                )
            {
                return false;
            }

            seg_mapping segmentMapping;
            if (sizeof(seg_mapping) != GCInterfaceOffsets.SIZEOF__seg_mapping
                || AlignOf<seg_mapping>() != GCInterfaceOffsets.ALIGNOF__seg_mapping
                || seg_mapping.ro_in_entry != (nuint)GCInterfaceOffsets.ro_in_entry
#if USE_REGIONS
                || OffsetOf(&segmentMapping, &segmentMapping.region_info) != GCInterfaceOffsets.OFFSETOF__seg_mapping__region_info
#else
                || OffsetOf(&segmentMapping, &segmentMapping.boundary) != GCInterfaceOffsets.OFFSETOF__seg_mapping__boundary
#if MULTIPLE_HEAPS
                || OffsetOf(&segmentMapping, &segmentMapping.h0) != GCInterfaceOffsets.OFFSETOF__seg_mapping__h0
                || OffsetOf(&segmentMapping, &segmentMapping.h1) != GCInterfaceOffsets.OFFSETOF__seg_mapping__h1
#endif
                || OffsetOf(&segmentMapping, &segmentMapping.seg0) != GCInterfaceOffsets.OFFSETOF__seg_mapping__seg0
                || OffsetOf(&segmentMapping, &segmentMapping.seg1) != GCInterfaceOffsets.OFFSETOF__seg_mapping__seg1
#endif
                )
            {
                return false;
            }

            plug plugValue;
            pair pairValue;
            plug_and_pair plugAndPair;
            plug_and_reloc plugAndReloc;
            plug_and_gap plugAndGap;
            gap_reloc_pair gapRelocPair;
            aligned_plug_and_gap alignedPlugAndGap;
            loh_obj_and_pad lohObjAndPad;
            loh_padding_obj lohPaddingObj;
            return sizeof(plug) == GCInterfaceOffsets.SIZEOF__plug
                && AlignOf<plug>() == GCInterfaceOffsets.ALIGNOF__plug
                && OffsetOf(&plugValue, &plugValue.skew0) == GCInterfaceOffsets.OFFSETOF__plug__skew
                && sizeof(pair) == GCInterfaceOffsets.SIZEOF__pair
                && AlignOf<pair>() == GCInterfaceOffsets.ALIGNOF__pair
                && OffsetOf(&pairValue, &pairValue.left) == GCInterfaceOffsets.OFFSETOF__pair__left
                && OffsetOf(&pairValue, &pairValue.right) == GCInterfaceOffsets.OFFSETOF__pair__right
                && sizeof(plug_and_pair) == GCInterfaceOffsets.SIZEOF__plug_and_pair
                && AlignOf<plug_and_pair>() == GCInterfaceOffsets.ALIGNOF__plug_and_pair
                && OffsetOf(&plugAndPair, &plugAndPair.m_pair) == GCInterfaceOffsets.OFFSETOF__plug_and_pair__m_pair
                && OffsetOf(&plugAndPair, &plugAndPair.m_plug) == GCInterfaceOffsets.OFFSETOF__plug_and_pair__m_plug
                && sizeof(plug_and_reloc) == GCInterfaceOffsets.SIZEOF__plug_and_reloc
                && AlignOf<plug_and_reloc>() == GCInterfaceOffsets.ALIGNOF__plug_and_reloc
                && OffsetOf(&plugAndReloc, &plugAndReloc.reloc) == GCInterfaceOffsets.OFFSETOF__plug_and_reloc__reloc
                && OffsetOf(&plugAndReloc, &plugAndReloc.m_pair) == GCInterfaceOffsets.OFFSETOF__plug_and_reloc__m_pair
                && OffsetOf(&plugAndReloc, &plugAndReloc.m_plug) == GCInterfaceOffsets.OFFSETOF__plug_and_reloc__m_plug
                && sizeof(plug_and_gap) == GCInterfaceOffsets.SIZEOF__plug_and_gap
                && AlignOf<plug_and_gap>() == GCInterfaceOffsets.ALIGNOF__plug_and_gap
                && OffsetOf(&plugAndGap, &plugAndGap.gap) == GCInterfaceOffsets.OFFSETOF__plug_and_gap__gap
                && OffsetOf(&plugAndGap, &plugAndGap.reloc) == GCInterfaceOffsets.OFFSETOF__plug_and_gap__reloc
                && OffsetOf(&plugAndGap, &plugAndGap.m_pair) == GCInterfaceOffsets.OFFSETOF__plug_and_gap__m_pair
                && OffsetOf(&plugAndGap, &plugAndGap.lr) == GCInterfaceOffsets.OFFSETOF__plug_and_gap__lr
                && OffsetOf(&plugAndGap, &plugAndGap.m_plug) == GCInterfaceOffsets.OFFSETOF__plug_and_gap__m_plug
                && sizeof(gap_reloc_pair) == GCInterfaceOffsets.SIZEOF__gap_reloc_pair
                && AlignOf<gap_reloc_pair>() == GCInterfaceOffsets.ALIGNOF__gap_reloc_pair
                && OffsetOf(&gapRelocPair, &gapRelocPair.gap) == GCInterfaceOffsets.OFFSETOF__gap_reloc_pair__gap
                && OffsetOf(&gapRelocPair, &gapRelocPair.reloc) == GCInterfaceOffsets.OFFSETOF__gap_reloc_pair__reloc
                && OffsetOf(&gapRelocPair, &gapRelocPair.m_pair) == GCInterfaceOffsets.OFFSETOF__gap_reloc_pair__m_pair
                && sizeof(aligned_plug_and_gap) == GCInterfaceOffsets.SIZEOF__aligned_plug_and_gap
                && AlignOf<aligned_plug_and_gap>() == GCInterfaceOffsets.ALIGNOF__aligned_plug_and_gap
                && OffsetOf(&alignedPlugAndGap, &alignedPlugAndGap.additional_pad) == GCInterfaceOffsets.OFFSETOF__aligned_plug_and_gap__additional_pad
                && OffsetOf(&alignedPlugAndGap, &alignedPlugAndGap.plugandgap) == GCInterfaceOffsets.OFFSETOF__aligned_plug_and_gap__plugandgap
                && sizeof(loh_obj_and_pad) == GCInterfaceOffsets.SIZEOF__loh_obj_and_pad
                && AlignOf<loh_obj_and_pad>() == GCInterfaceOffsets.ALIGNOF__loh_obj_and_pad
                && OffsetOf(&lohObjAndPad, &lohObjAndPad.reloc) == GCInterfaceOffsets.OFFSETOF__loh_obj_and_pad__reloc
                && OffsetOf(&lohObjAndPad, &lohObjAndPad.m_plug) == GCInterfaceOffsets.OFFSETOF__loh_obj_and_pad__m_plug
                && sizeof(loh_padding_obj) == GCInterfaceOffsets.SIZEOF__loh_padding_obj
                && AlignOf<loh_padding_obj>() == GCInterfaceOffsets.ALIGNOF__loh_padding_obj
                && OffsetOf(&lohPaddingObj, &lohPaddingObj.mt) == GCInterfaceOffsets.OFFSETOF__loh_padding_obj__mt
                && OffsetOf(&lohPaddingObj, &lohPaddingObj.len) == GCInterfaceOffsets.OFFSETOF__loh_padding_obj__len
                && OffsetOf(&lohPaddingObj, &lohPaddingObj.reloc) == GCInterfaceOffsets.OFFSETOF__loh_padding_obj__reloc
                && OffsetOf(&lohPaddingObj, &lohPaddingObj.m_plug) == GCInterfaceOffsets.OFFSETOF__loh_padding_obj__m_plug;
        }

        /// <summary>
        /// The types of the environment layer -- gcenv.structs.h and gcenv.os.h. They do not
        /// cross the GC/EE boundary, but they do cross the boundary between the managed GC and
        /// the C++ GCToOSInterface it still forwards to.
        /// </summary>
        /// <remarks>
        /// Several of the C++ classes keep their members private, so the table pins their size
        /// and alignment only; each of those has a single field or two pointer-sized fields in a
        /// fixed order, which size and alignment together determine.
        /// </remarks>
        private static bool VerifyEnvironmentTypes()
        {
            GCSystemInfo systemInfo;
            if (sizeof(GCSystemInfo) != GCInterfaceOffsets.SIZEOF__GCSystemInfo
                || AlignOf<GCSystemInfo>() != GCInterfaceOffsets.ALIGNOF__GCSystemInfo
                || OffsetOf(&systemInfo, &systemInfo.dwNumberOfProcessors) != GCInterfaceOffsets.OFFSETOF__GCSystemInfo__dwNumberOfProcessors
                || OffsetOf(&systemInfo, &systemInfo.dwPageSize) != GCInterfaceOffsets.OFFSETOF__GCSystemInfo__dwPageSize
                || OffsetOf(&systemInfo, &systemInfo.dwAllocationGranularity) != GCInterfaceOffsets.OFFSETOF__GCSystemInfo__dwAllocationGranularity)
            {
                return false;
            }

            if (sizeof(AffinitySet) != GCInterfaceOffsets.SIZEOF__AffinitySet
                || AlignOf<AffinitySet>() != GCInterfaceOffsets.ALIGNOF__AffinitySet
                || sizeof(GCEvent) != GCInterfaceOffsets.SIZEOF__GCEvent
                || AlignOf<GCEvent>() != GCInterfaceOffsets.ALIGNOF__GCEvent)
            {
                return false;
            }

            return GCToOSInterface.NUMA_NODE_UNDEFINED == GCInterfaceOffsets.NUMA_NODE_UNDEFINED
                && GCToOSInterface.MAX_SUPPORTED_HEAPS == GCInterfaceOffsets.MAX_SUPPORTED_HEAPS
                && GCToOSInterface.MAX_SUPPORTED_NODES == GCInterfaceOffsets.MAX_SUPPORTED_NODES
                && (int)VirtualReserveFlags.None == GCInterfaceOffsets.VirtualReserveFlags_None
                && (int)VirtualReserveFlags.WriteWatch == GCInterfaceOffsets.VirtualReserveFlags_WriteWatch
                && GCEnv.WAIT_OBJECT_0 == GCInterfaceOffsets.WAIT_OBJECT_0
                && GCEnv.WAIT_TIMEOUT == GCInterfaceOffsets.WAIT_TIMEOUT;
        }

        /// <summary>
        /// The DAC-facing types of gcinterface.dac.h. <c>GcDacVars</c> is the fourth argument of
        /// <c>GC_Initialize</c>, and the types below it are the analogues the DAC reads GC state
        /// through.
        /// </summary>
        private static bool VerifyDacTypes()
        {
            oom_history oomHistory;
            if (sizeof(oom_history) != GCInterfaceOffsets.SIZEOF__oom_history
                || AlignOf<oom_history>() != GCInterfaceOffsets.ALIGNOF__oom_history
                || OffsetOf(&oomHistory, &oomHistory.reason) != GCInterfaceOffsets.OFFSETOF__oom_history__reason
                || OffsetOf(&oomHistory, &oomHistory.alloc_size) != GCInterfaceOffsets.OFFSETOF__oom_history__alloc_size
                || OffsetOf(&oomHistory, &oomHistory.reserved) != GCInterfaceOffsets.OFFSETOF__oom_history__reserved
                || OffsetOf(&oomHistory, &oomHistory.allocated) != GCInterfaceOffsets.OFFSETOF__oom_history__allocated
                || OffsetOf(&oomHistory, &oomHistory.gc_index) != GCInterfaceOffsets.OFFSETOF__oom_history__gc_index
                || OffsetOf(&oomHistory, &oomHistory.fgm) != GCInterfaceOffsets.OFFSETOF__oom_history__fgm
                || OffsetOf(&oomHistory, &oomHistory.size) != GCInterfaceOffsets.OFFSETOF__oom_history__size
                || OffsetOf(&oomHistory, &oomHistory.available_pagefile_mb) != GCInterfaceOffsets.OFFSETOF__oom_history__available_pagefile_mb
                || OffsetOf(&oomHistory, &oomHistory.loh_p) != GCInterfaceOffsets.OFFSETOF__oom_history__loh_p)
            {
                return false;
            }

            dac_heap_segment segment;
            if (sizeof(dac_heap_segment) != GCInterfaceOffsets.SIZEOF__dac_heap_segment
                || AlignOf<dac_heap_segment>() != GCInterfaceOffsets.ALIGNOF__dac_heap_segment
                || OffsetOf(&segment, &segment.allocated) != GCInterfaceOffsets.OFFSETOF__dac_heap_segment__allocated
                || OffsetOf(&segment, &segment.committed) != GCInterfaceOffsets.OFFSETOF__dac_heap_segment__committed
                || OffsetOf(&segment, &segment.reserved) != GCInterfaceOffsets.OFFSETOF__dac_heap_segment__reserved
                || OffsetOf(&segment, &segment.used) != GCInterfaceOffsets.OFFSETOF__dac_heap_segment__used
                || OffsetOf(&segment, &segment.mem) != GCInterfaceOffsets.OFFSETOF__dac_heap_segment__mem
                || OffsetOf(&segment, &segment.flags) != GCInterfaceOffsets.OFFSETOF__dac_heap_segment__flags
                || OffsetOf(&segment, &segment.next) != GCInterfaceOffsets.OFFSETOF__dac_heap_segment__next
                || OffsetOf(&segment, &segment.background_allocated) != GCInterfaceOffsets.OFFSETOF__dac_heap_segment__background_allocated
                || OffsetOf(&segment, &segment.heap) != GCInterfaceOffsets.OFFSETOF__dac_heap_segment__heap)
            {
                return false;
            }

            dac_region_free_list freeList;
            if (sizeof(dac_region_free_list) != GCInterfaceOffsets.SIZEOF__dac_region_free_list
                || AlignOf<dac_region_free_list>() != GCInterfaceOffsets.ALIGNOF__dac_region_free_list
                || OffsetOf(&freeList, &freeList.num_free_regions) != GCInterfaceOffsets.OFFSETOF__dac_region_free_list__num_free_regions
                || OffsetOf(&freeList, &freeList.size_free_regions) != GCInterfaceOffsets.OFFSETOF__dac_region_free_list__size_free_regions
                || OffsetOf(&freeList, &freeList.size_committed_in_free_regions) != GCInterfaceOffsets.OFFSETOF__dac_region_free_list__size_committed_in_free_regions
                || OffsetOf(&freeList, &freeList.num_free_regions_added) != GCInterfaceOffsets.OFFSETOF__dac_region_free_list__num_free_regions_added
                || OffsetOf(&freeList, &freeList.num_free_regions_removed) != GCInterfaceOffsets.OFFSETOF__dac_region_free_list__num_free_regions_removed
                || OffsetOf(&freeList, &freeList.head_free_region) != GCInterfaceOffsets.OFFSETOF__dac_region_free_list__head_free_region
                || OffsetOf(&freeList, &freeList.tail_free_region) != GCInterfaceOffsets.OFFSETOF__dac_region_free_list__tail_free_region)
            {
                return false;
            }

            dac_generation generation;
            if (sizeof(dac_generation) != GCInterfaceOffsets.SIZEOF__dac_generation
                || AlignOf<dac_generation>() != GCInterfaceOffsets.ALIGNOF__dac_generation
                || OffsetOf(&generation, &generation.allocation_context) != GCInterfaceOffsets.OFFSETOF__dac_generation__allocation_context
                || OffsetOf(&generation, &generation.start_segment) != GCInterfaceOffsets.OFFSETOF__dac_generation__start_segment
                || OffsetOf(&generation, &generation.allocation_start) != GCInterfaceOffsets.OFFSETOF__dac_generation__allocation_start)
            {
                return false;
            }

            // The native m_FillPointers is one array, so what has to hold is that the run of
            // fields standing in for it starts where the array does and is contiguous.
            dac_finalize_queue finalizeQueue;
            if (sizeof(dac_finalize_queue) != GCInterfaceOffsets.SIZEOF__dac_finalize_queue
                || AlignOf<dac_finalize_queue>() != GCInterfaceOffsets.ALIGNOF__dac_finalize_queue
                || OffsetOf(&finalizeQueue, &finalizeQueue.m_FillPointers0) != GCInterfaceOffsets.OFFSETOF__dac_finalize_queue__m_FillPointers
                || OffsetOf(&finalizeQueue, &finalizeQueue.m_FillPointers1) != GCInterfaceOffsets.OFFSETOF__dac_finalize_queue__m_FillPointers + sizeof(void*)
                || OffsetOf(&finalizeQueue, &finalizeQueue.m_FillPointers2) != GCInterfaceOffsets.OFFSETOF__dac_finalize_queue__m_FillPointers + (2 * sizeof(void*))
                || OffsetOf(&finalizeQueue, &finalizeQueue.m_FillPointers3) != GCInterfaceOffsets.OFFSETOF__dac_finalize_queue__m_FillPointers + (3 * sizeof(void*))
                || OffsetOf(&finalizeQueue, &finalizeQueue.m_FillPointers4) != GCInterfaceOffsets.OFFSETOF__dac_finalize_queue__m_FillPointers + (4 * sizeof(void*))
                || OffsetOf(&finalizeQueue, &finalizeQueue.m_FillPointers5) != GCInterfaceOffsets.OFFSETOF__dac_finalize_queue__m_FillPointers + (5 * sizeof(void*)))
            {
                return false;
            }

            byte handleTableStorage = 0;
            dac_handle_table_segment* handleTableSegment = (dac_handle_table_segment*)&handleTableStorage;
            if (sizeof(dac_handle_table_segment) != GCInterfaceOffsets.SIZEOF__dac_handle_table_segment
                || AlignOf<dac_handle_table_segment>() != GCInterfaceOffsets.ALIGNOF__dac_handle_table_segment
                || OffsetOf(handleTableSegment, &handleTableSegment->rgGeneration[0]) != GCInterfaceOffsets.OFFSETOF__dac_handle_table_segment__rgGeneration
                || OffsetOf(handleTableSegment, &handleTableSegment->rgAllocation[0]) != GCInterfaceOffsets.OFFSETOF__dac_handle_table_segment__rgAllocation
                || OffsetOf(handleTableSegment, &handleTableSegment->rgFreeMask[0]) != GCInterfaceOffsets.OFFSETOF__dac_handle_table_segment__rgFreeMask
                || OffsetOf(handleTableSegment, &handleTableSegment->rgBlockType[0]) != GCInterfaceOffsets.OFFSETOF__dac_handle_table_segment__rgBlockType
                || OffsetOf(handleTableSegment, &handleTableSegment->rgUserData[0]) != GCInterfaceOffsets.OFFSETOF__dac_handle_table_segment__rgUserData
                || OffsetOf(handleTableSegment, &handleTableSegment->rgLocks[0]) != GCInterfaceOffsets.OFFSETOF__dac_handle_table_segment__rgLocks
                || OffsetOf(handleTableSegment, &handleTableSegment->rgTail[0]) != GCInterfaceOffsets.OFFSETOF__dac_handle_table_segment__rgTail
                || OffsetOf(handleTableSegment, &handleTableSegment->rgHint[0]) != GCInterfaceOffsets.OFFSETOF__dac_handle_table_segment__rgHint
                || OffsetOf(handleTableSegment, &handleTableSegment->rgFreeCount[0]) != GCInterfaceOffsets.OFFSETOF__dac_handle_table_segment__rgFreeCount
                || OffsetOf(handleTableSegment, &handleTableSegment->pNextSegment) != GCInterfaceOffsets.OFFSETOF__dac_handle_table_segment__pNextSegment)
            {
                return false;
            }

            dac_handle_table handleTable;
            if (sizeof(dac_handle_table) != GCInterfaceOffsets.SIZEOF__dac_handle_table
                || AlignOf<dac_handle_table>() != GCInterfaceOffsets.ALIGNOF__dac_handle_table
                || OffsetOf(&handleTable, &handleTable.padding[0]) != GCInterfaceOffsets.OFFSETOF__dac_handle_table__padding
                || OffsetOf(&handleTable, &handleTable.pSegmentList) != GCInterfaceOffsets.OFFSETOF__dac_handle_table__pSegmentList)
            {
                return false;
            }

            dac_handle_table_bucket bucket;
            if (sizeof(dac_handle_table_bucket) != GCInterfaceOffsets.SIZEOF__dac_handle_table_bucket
                || AlignOf<dac_handle_table_bucket>() != GCInterfaceOffsets.ALIGNOF__dac_handle_table_bucket
                || OffsetOf(&bucket, &bucket.pTable) != GCInterfaceOffsets.OFFSETOF__dac_handle_table_bucket__pTable
                || OffsetOf(&bucket, &bucket.HandleTableIndex) != GCInterfaceOffsets.OFFSETOF__dac_handle_table_bucket__HandleTableIndex)
            {
                return false;
            }

            dac_handle_table_map map;
            if (sizeof(dac_handle_table_map) != GCInterfaceOffsets.SIZEOF__dac_handle_table_map
                || AlignOf<dac_handle_table_map>() != GCInterfaceOffsets.ALIGNOF__dac_handle_table_map
                || OffsetOf(&map, &map.pBuckets) != GCInterfaceOffsets.OFFSETOF__dac_handle_table_map__pBuckets
                || OffsetOf(&map, &map.pNext) != GCInterfaceOffsets.OFFSETOF__dac_handle_table_map__pNext
                || OffsetOf(&map, &map.dwMaxIndex) != GCInterfaceOffsets.OFFSETOF__dac_handle_table_map__dwMaxIndex)
            {
                return false;
            }

            dac_card_table_info cardTableInfo;
            if (sizeof(dac_card_table_info) != GCInterfaceOffsets.SIZEOF__dac_card_table_info
                || AlignOf<dac_card_table_info>() != GCInterfaceOffsets.ALIGNOF__dac_card_table_info
                || OffsetOf(&cardTableInfo, &cardTableInfo.recount) != GCInterfaceOffsets.OFFSETOF__dac_card_table_info__recount
                || OffsetOf(&cardTableInfo, &cardTableInfo.size) != GCInterfaceOffsets.OFFSETOF__dac_card_table_info__size
                || OffsetOf(&cardTableInfo, &cardTableInfo.next_card_table) != GCInterfaceOffsets.OFFSETOF__dac_card_table_info__next_card_table)
            {
                return false;
            }

            unused_gc_heap unusedHeap;
            unused_generation unusedGeneration;
            if (sizeof(unused_gc_heap) != GCInterfaceOffsets.SIZEOF__unused_gc_heap
                || AlignOf<unused_gc_heap>() != GCInterfaceOffsets.ALIGNOF__unused_gc_heap
                || OffsetOf(&unusedHeap, &unusedHeap.unused) != GCInterfaceOffsets.OFFSETOF__unused_gc_heap__unused
                || sizeof(unused_generation) != GCInterfaceOffsets.SIZEOF__unused_generation
                || AlignOf<unused_generation>() != GCInterfaceOffsets.ALIGNOF__unused_generation
                || OffsetOf(&unusedGeneration, &unusedGeneration.unused) != GCInterfaceOffsets.OFFSETOF__unused_generation__unused)
            {
                return false;
            }

            dac_gc_heap heap;
            if (sizeof(dac_gc_heap) != GCInterfaceOffsets.SIZEOF__dac_gc_heap
                || AlignOf<dac_gc_heap>() != GCInterfaceOffsets.ALIGNOF__dac_gc_heap
                || OffsetOf(&heap, &heap.alloc_allocated) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__alloc_allocated
                || OffsetOf(&heap, &heap.ephemeral_heap_segment) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__ephemeral_heap_segment
                || OffsetOf(&heap, &heap.finalize_queue) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__finalize_queue
                || OffsetOf(&heap, &heap.oom_info) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__oom_info
                || OffsetOf(&heap, &heap.interesting_data_per_heap[0]) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__interesting_data_per_heap
                || OffsetOf(&heap, &heap.compact_reasons_per_heap[0]) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__compact_reasons_per_heap
                || OffsetOf(&heap, &heap.expand_mechanisms_per_heap[0]) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__expand_mechanisms_per_heap
                || OffsetOf(&heap, &heap.interesting_mechanism_bits_per_heap[0]) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__interesting_mechanism_bits_per_heap
                || OffsetOf(&heap, &heap.internal_root_array) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__internal_root_array
                || OffsetOf(&heap, &heap.internal_root_array_index) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__internal_root_array_index
                || OffsetOf(&heap, &heap.heap_analyze_success) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__heap_analyze_success
                || OffsetOf(&heap, &heap.card_table) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__card_table
                || OffsetOf(&heap, &heap.mark_array) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__mark_array
                || OffsetOf(&heap, &heap.next_sweep_obj) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__next_sweep_obj
                || OffsetOf(&heap, &heap.background_saved_lowest_address) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__background_saved_lowest_address
                || OffsetOf(&heap, &heap.background_saved_highest_address) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__background_saved_highest_address
                || OffsetOf(&heap, &heap.saved_sweep_ephemeral_seg) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__saved_sweep_ephemeral_seg
                || OffsetOf(&heap, &heap.saved_sweep_ephemeral_start) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__saved_sweep_ephemeral_start
                || OffsetOf(&heap, &heap.generation_table) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__generation_table
                || OffsetOf(&heap, &heap.freeable_soh_segment) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__freeable_soh_segment
                || OffsetOf(&heap, &heap.freeable_uoh_segment) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__freeable_uoh_segment
                || OffsetOf(&heap, &heap.free_regions0) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__free_regions
                || OffsetOf(&heap, &heap.free_regions1) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__free_regions + sizeof(dac_region_free_list)
                || OffsetOf(&heap, &heap.free_regions2) != GCInterfaceOffsets.OFFSETOF__dac_gc_heap__free_regions + (2 * sizeof(dac_region_free_list)))
            {
                return false;
            }

            return VerifyDacVars() && VerifyDacConstants();
        }

        private static bool VerifyDacVars()
        {
            GcDacVars dacVars;
            if (sizeof(GcDacVars) != GCInterfaceOffsets.SIZEOF__GcDacVars
                || AlignOf<GcDacVars>() != GCInterfaceOffsets.ALIGNOF__GcDacVars
                || OffsetOf(&dacVars, &dacVars.major_version_number) != GCInterfaceOffsets.OFFSETOF__GcDacVars__major_version_number
                || OffsetOf(&dacVars, &dacVars.minor_version_number) != GCInterfaceOffsets.OFFSETOF__GcDacVars__minor_version_number
                || OffsetOf(&dacVars, &dacVars.generation_size) != GCInterfaceOffsets.OFFSETOF__GcDacVars__generation_size
                || OffsetOf(&dacVars, &dacVars.total_generation_count) != GCInterfaceOffsets.OFFSETOF__GcDacVars__total_generation_count
                || OffsetOf(&dacVars, &dacVars.build_variant) != GCInterfaceOffsets.OFFSETOF__GcDacVars__build_variant
                || OffsetOf(&dacVars, &dacVars.built_with_svr) != GCInterfaceOffsets.OFFSETOF__GcDacVars__built_with_svr
                || OffsetOf(&dacVars, &dacVars.gc_global_mechanisms) != GCInterfaceOffsets.OFFSETOF__GcDacVars__gc_global_mechanisms
                || OffsetOf(&dacVars, &dacVars.generation_table) != GCInterfaceOffsets.OFFSETOF__GcDacVars__generation_table
                || OffsetOf(&dacVars, &dacVars.max_gen) != GCInterfaceOffsets.OFFSETOF__GcDacVars__max_gen
                || OffsetOf(&dacVars, &dacVars.mark_array) != GCInterfaceOffsets.OFFSETOF__GcDacVars__mark_array
                || OffsetOf(&dacVars, &dacVars.current_c_gc_state) != GCInterfaceOffsets.OFFSETOF__GcDacVars__current_c_gc_state
                || OffsetOf(&dacVars, &dacVars.ephemeral_heap_segment) != GCInterfaceOffsets.OFFSETOF__GcDacVars__ephemeral_heap_segment
                || OffsetOf(&dacVars, &dacVars.saved_sweep_ephemeral_seg) != GCInterfaceOffsets.OFFSETOF__GcDacVars__saved_sweep_ephemeral_seg
                || OffsetOf(&dacVars, &dacVars.saved_sweep_ephemeral_start) != GCInterfaceOffsets.OFFSETOF__GcDacVars__saved_sweep_ephemeral_start
                || OffsetOf(&dacVars, &dacVars.background_saved_lowest_address) != GCInterfaceOffsets.OFFSETOF__GcDacVars__background_saved_lowest_address
                || OffsetOf(&dacVars, &dacVars.background_saved_highest_address) != GCInterfaceOffsets.OFFSETOF__GcDacVars__background_saved_highest_address
                || OffsetOf(&dacVars, &dacVars.alloc_allocated) != GCInterfaceOffsets.OFFSETOF__GcDacVars__alloc_allocated
                || OffsetOf(&dacVars, &dacVars.next_sweep_obj) != GCInterfaceOffsets.OFFSETOF__GcDacVars__next_sweep_obj
                || OffsetOf(&dacVars, &dacVars.oom_info) != GCInterfaceOffsets.OFFSETOF__GcDacVars__oom_info
                || OffsetOf(&dacVars, &dacVars.finalize_queue) != GCInterfaceOffsets.OFFSETOF__GcDacVars__finalize_queue
                || OffsetOf(&dacVars, &dacVars.internal_root_array) != GCInterfaceOffsets.OFFSETOF__GcDacVars__internal_root_array
                || OffsetOf(&dacVars, &dacVars.internal_root_array_index) != GCInterfaceOffsets.OFFSETOF__GcDacVars__internal_root_array_index
                || OffsetOf(&dacVars, &dacVars.heap_analyze_success) != GCInterfaceOffsets.OFFSETOF__GcDacVars__heap_analyze_success
                || OffsetOf(&dacVars, &dacVars.n_heaps) != GCInterfaceOffsets.OFFSETOF__GcDacVars__n_heaps
                || OffsetOf(&dacVars, &dacVars.g_heaps) != GCInterfaceOffsets.OFFSETOF__GcDacVars__g_heaps
                || OffsetOf(&dacVars, &dacVars.gc_structures_invalid_cnt) != GCInterfaceOffsets.OFFSETOF__GcDacVars__gc_structures_invalid_cnt
                || OffsetOf(&dacVars, &dacVars.interesting_data_per_heap) != GCInterfaceOffsets.OFFSETOF__GcDacVars__interesting_data_per_heap
                || OffsetOf(&dacVars, &dacVars.compact_reasons_per_heap) != GCInterfaceOffsets.OFFSETOF__GcDacVars__compact_reasons_per_heap
                || OffsetOf(&dacVars, &dacVars.expand_mechanisms_per_heap) != GCInterfaceOffsets.OFFSETOF__GcDacVars__expand_mechanisms_per_heap
                || OffsetOf(&dacVars, &dacVars.interesting_mechanism_bits_per_heap) != GCInterfaceOffsets.OFFSETOF__GcDacVars__interesting_mechanism_bits_per_heap
                || OffsetOf(&dacVars, &dacVars.handle_table_map) != GCInterfaceOffsets.OFFSETOF__GcDacVars__handle_table_map
                || OffsetOf(&dacVars, &dacVars.gc_heap_field_offsets) != GCInterfaceOffsets.OFFSETOF__GcDacVars__gc_heap_field_offsets
                || OffsetOf(&dacVars, &dacVars.generation_field_offsets) != GCInterfaceOffsets.OFFSETOF__GcDacVars__generation_field_offsets
                || OffsetOf(&dacVars, &dacVars.bookkeeping_start) != GCInterfaceOffsets.OFFSETOF__GcDacVars__bookkeeping_start
                || OffsetOf(&dacVars, &dacVars.global_regions_to_decommit) != GCInterfaceOffsets.OFFSETOF__GcDacVars__global_regions_to_decommit
                || OffsetOf(&dacVars, &dacVars.global_free_huge_regions) != GCInterfaceOffsets.OFFSETOF__GcDacVars__global_free_huge_regions
                || OffsetOf(&dacVars, &dacVars.free_regions) != GCInterfaceOffsets.OFFSETOF__GcDacVars__free_regions
                || OffsetOf(&dacVars, &dacVars.freeable_soh_segment) != GCInterfaceOffsets.OFFSETOF__GcDacVars__freeable_soh_segment
                || OffsetOf(&dacVars, &dacVars.freeable_uoh_segment) != GCInterfaceOffsets.OFFSETOF__GcDacVars__freeable_uoh_segment
                || OffsetOf(&dacVars, &dacVars.total_bookkeeping_elements) != GCInterfaceOffsets.OFFSETOF__GcDacVars__total_bookkeeping_elements
                || OffsetOf(&dacVars, &dacVars.count_free_region_kinds) != GCInterfaceOffsets.OFFSETOF__GcDacVars__count_free_region_kinds
                || OffsetOf(&dacVars, &dacVars.card_table_info_size) != GCInterfaceOffsets.OFFSETOF__GcDacVars__card_table_info_size
                || OffsetOf(&dacVars, &dacVars.dynamic_adaptation_mode) != GCInterfaceOffsets.OFFSETOF__GcDacVars__dynamic_adaptation_mode
                || OffsetOf(&dacVars, &dacVars.gc_descriptor) != GCInterfaceOffsets.OFFSETOF__GcDacVars__gc_descriptor
                || OffsetOf(&dacVars, &dacVars.g_totalCpuCount) != GCInterfaceOffsets.OFFSETOF__GcDacVars__g_totalCpuCount)
            {
                return false;
            }

            return true;
        }

        private static bool VerifyDacConstants() =>
            GCInterfaceDacConstants.HEAP_SEGMENT_FLAGS_READONLY == GCInterfaceOffsets.HEAP_SEGMENT_FLAGS_READONLY
            && GCInterfaceDacConstants.NUM_GC_DATA_POINTS == GCInterfaceOffsets.NUM_GC_DATA_POINTS
            && GCInterfaceDacConstants.MAX_COMPACT_REASONS_COUNT == GCInterfaceOffsets.MAX_COMPACT_REASONS_COUNT
            && GCInterfaceDacConstants.MAX_EXPAND_MECHANISMS_COUNT == GCInterfaceOffsets.MAX_EXPAND_MECHANISMS_COUNT
            && GCInterfaceDacConstants.MAX_GC_MECHANISM_BITS_COUNT == GCInterfaceOffsets.MAX_GC_MECHANISM_BITS_COUNT
            && GCInterfaceDacConstants.MAX_GLOBAL_GC_MECHANISMS_COUNT == GCInterfaceOffsets.MAX_GLOBAL_GC_MECHANISMS_COUNT
            && GCInterfaceDacConstants.FREE_REGION_KINDS == GCInterfaceOffsets.FREE_REGION_KINDS
            && GCInterfaceDacConstants.NUMBERGENERATIONS == GCInterfaceOffsets.NUMBERGENERATIONS
            && GCInterfaceDacConstants.GENERATION_TABLE_FIELD_INDEX == GCInterfaceOffsets.GENERATION_TABLE_FIELD_INDEX
            && GCInterfaceDacConstants.build_variant_use_region == GCInterfaceOffsets.build_variant_use_region
            && GCInterfaceDacConstants.build_variant_background_gc == GCInterfaceOffsets.build_variant_background_gc
            && GCInterfaceDacConstants.build_variant_dynamic_heap_count == GCInterfaceOffsets.build_variant_dynamic_heap_count
            // The native m_FillPointers array is NUMBERGENERATIONS + ExtraSegCount long, and the
            // fields standing in for it are checked against that length above.
            && GCInterfaceOffsets.NUMBERGENERATIONS + dac_finalize_queue.ExtraSegCount == 6;

        /// <summary>
        /// The vtable structs have one pointer-sized field per virtual slot. Their slot order and
        /// signatures are checked against the native headers at build time by
        /// verify-gc-interface-vtables.py; this startup check covers the managed size used by the
        /// running application, including that the derived interface's base slots are laid out
        /// before its own.
        /// </summary>
        private static bool VerifyVtables()
        {
            IGCHeapInternalVtable heapVtable;
            if (sizeof(IGCHandleStoreVtable) != IGCHandleStoreVtable.SlotCount * sizeof(void*)
                || sizeof(IGCHandleManagerVtable) != IGCHandleManagerVtable.SlotCount * sizeof(void*)
                || sizeof(IGCHeapVtable) != IGCHeapVtable.SlotCount * sizeof(void*)
                || sizeof(IGCHeapInternalVtable) != IGCHeapInternalVtable.SlotCount * sizeof(void*)
                || sizeof(IGCToCLRVtable) != IGCToCLRVtable.SlotCount * sizeof(void*)
                || sizeof(IGCToCLREventSinkVtable) != IGCToCLREventSinkVtable.SlotCount * sizeof(void*)
                || OffsetOf(&heapVtable, &heapVtable.IGCHeap) != 0
                || OffsetOf(&heapVtable, &heapVtable.GetNumberOfHeaps) != IGCHeapVtable.SlotCount * sizeof(void*))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Returns true if every enum that crosses the GC/EE boundary has the native underlying
        /// size. An enum that changed size would change the size of every signature and structure
        /// it appears in.
        /// </summary>
        private static bool VerifyEnumSizes() =>
            sizeof(SUSPEND_REASON) == GCInterfaceOffsets.SIZEOF__SUSPEND_REASON
            && sizeof(walk_surv_type) == GCInterfaceOffsets.SIZEOF__walk_surv_type
            && sizeof(WriteBarrierOp) == GCInterfaceOffsets.SIZEOF__WriteBarrierOp
            && sizeof(collection_mode) == GCInterfaceOffsets.SIZEOF__collection_mode
            && sizeof(wait_full_gc_status) == GCInterfaceOffsets.SIZEOF__wait_full_gc_status
            && sizeof(start_no_gc_region_status) == GCInterfaceOffsets.SIZEOF__start_no_gc_region_status
            && sizeof(end_no_gc_region_status) == GCInterfaceOffsets.SIZEOF__end_no_gc_region_status
            && sizeof(refresh_memory_limit_status) == GCInterfaceOffsets.SIZEOF__refresh_memory_limit_status
            && sizeof(enable_no_gc_region_callback_status) == GCInterfaceOffsets.SIZEOF__enable_no_gc_region_callback_status
            && sizeof(gc_kind) == GCInterfaceOffsets.SIZEOF__gc_kind
            && sizeof(HandleType) == GCInterfaceOffsets.SIZEOF__HandleType
            && sizeof(GCHeapType) == GCInterfaceOffsets.SIZEOF__GCHeapType
            && sizeof(GCConfigurationType) == GCInterfaceOffsets.SIZEOF__GCConfigurationType
            && sizeof(GC_ALLOC_FLAGS) == GCInterfaceOffsets.SIZEOF__GC_ALLOC_FLAGS
            && sizeof(EtwGCRootKind) == GCInterfaceOffsets.SIZEOF__EtwGCRootKind
            && sizeof(EtwGCRootFlags) == GCInterfaceOffsets.SIZEOF__EtwGCRootFlags
            && sizeof(GCEventProvider) == GCInterfaceOffsets.SIZEOF__GCEventProvider
            && sizeof(GCEventLevel) == GCInterfaceOffsets.SIZEOF__GCEventLevel
            && sizeof(GCEventKeyword) == GCInterfaceOffsets.SIZEOF__GCEventKeyword
            && sizeof(c_gc_state) == GCInterfaceOffsets.SIZEOF__c_gc_state
            && sizeof(oom_reason) == GCInterfaceOffsets.SIZEOF__oom_reason
            && sizeof(failure_get_memory) == GCInterfaceOffsets.SIZEOF__failure_get_memory;

        /// <summary>
        /// Returns true if the managed copies of the GC/EE interface enums still agree with the
        /// native enumerators. These are values rather than offsets, but they are just as much
        /// part of the ABI: they cross the boundary as arguments and return values, several are
        /// duplicated in System.GC, the event keyword bits are the ones in the ETW manifest, and
        /// the handle types are depended upon by the cDAC contracts.
        /// </summary>
        /// <remarks>
        /// Written as one expression per enum rather than with early returns because both sides
        /// are compile-time constants: a branch on one would be unreachable code in a build that
        /// agrees, and the whole check folds away in a build that does.
        /// </remarks>
        private static bool VerifyEnumValues() =>
            VerifySuspensionEnums()
            && VerifyCollectionEnums()
            && VerifyHandleTypes()
            && VerifyAllocationEnums()
            && VerifyEventEnums()
            && VerifyDacEnums()
            && VerifyGCRecordEnums()
            && VerifyGCPrivEnums();

        private static bool VerifyGCPrivEnums() =>
            (int)alloc_wait_reason.awr_ignored == GCInterfaceOffsets.awr_ignored
            && (int)alloc_wait_reason.awr_low_memory == GCInterfaceOffsets.awr_low_memory
            && (int)alloc_wait_reason.awr_low_ephemeral == GCInterfaceOffsets.awr_low_ephemeral
            && (int)alloc_wait_reason.awr_gen0_alloc == GCInterfaceOffsets.awr_gen0_alloc
            && (int)alloc_wait_reason.awr_loh_alloc == GCInterfaceOffsets.awr_loh_alloc
            && (int)alloc_wait_reason.awr_alloc_loh_low_mem == GCInterfaceOffsets.awr_alloc_loh_low_mem
            && (int)alloc_wait_reason.awr_loh_oos == GCInterfaceOffsets.awr_loh_oos
            && (int)alloc_wait_reason.awr_gen0_oos_bgc == GCInterfaceOffsets.awr_gen0_oos_bgc
            && (int)alloc_wait_reason.awr_loh_oos_bgc == GCInterfaceOffsets.awr_loh_oos_bgc
            && (int)alloc_wait_reason.awr_fgc_wait_for_bgc == GCInterfaceOffsets.awr_fgc_wait_for_bgc
            && (int)alloc_wait_reason.awr_get_loh_seg == GCInterfaceOffsets.awr_get_loh_seg
            && (int)alloc_wait_reason.awr_loh_alloc_during_plan == GCInterfaceOffsets.awr_loh_alloc_during_plan
            && (int)alloc_wait_reason.awr_uoh_alloc_during_bgc == GCInterfaceOffsets.awr_uoh_alloc_during_bgc
            && (int)msl_take_state.mt_get_large_seg == GCInterfaceOffsets.mt_get_large_seg
            && (int)msl_take_state.mt_bgc_uoh_sweep == GCInterfaceOffsets.mt_bgc_uoh_sweep
            && (int)msl_take_state.mt_wait_bgc == GCInterfaceOffsets.mt_wait_bgc
            && (int)msl_take_state.mt_block_gc == GCInterfaceOffsets.mt_block_gc
            && (int)msl_take_state.mt_clr_mem == GCInterfaceOffsets.mt_clr_mem
            && (int)msl_take_state.mt_clr_large_mem == GCInterfaceOffsets.mt_clr_large_mem
            && (int)msl_take_state.mt_t_eph_gc == GCInterfaceOffsets.mt_t_eph_gc
            && (int)msl_take_state.mt_t_full_gc == GCInterfaceOffsets.mt_t_full_gc
            && (int)msl_take_state.mt_alloc_small == GCInterfaceOffsets.mt_alloc_small
            && (int)msl_take_state.mt_alloc_large == GCInterfaceOffsets.mt_alloc_large
            && (int)msl_take_state.mt_alloc_small_cant == GCInterfaceOffsets.mt_alloc_small_cant
            && (int)msl_take_state.mt_alloc_large_cant == GCInterfaceOffsets.mt_alloc_large_cant
            && (int)msl_take_state.mt_try_alloc == GCInterfaceOffsets.mt_try_alloc
            && (int)msl_take_state.mt_try_budget == GCInterfaceOffsets.mt_try_budget
            && (int)msl_take_state.mt_try_servo_budget == GCInterfaceOffsets.mt_try_servo_budget
            && (int)msl_take_state.mt_decommit_step == GCInterfaceOffsets.mt_decommit_step
            && (int)gc_pause_mode.pause_batch == GCInterfaceOffsets.pause_batch
            && (int)gc_pause_mode.pause_interactive == GCInterfaceOffsets.pause_interactive
            && (int)gc_pause_mode.pause_low_latency == GCInterfaceOffsets.pause_low_latency
            && (int)gc_pause_mode.pause_sustained_low_latency == GCInterfaceOffsets.pause_sustained_low_latency
            && (int)gc_pause_mode.pause_no_gc == GCInterfaceOffsets.pause_no_gc
            && (int)gc_loh_compaction_mode.loh_compaction_default == GCInterfaceOffsets.loh_compaction_default
            && (int)gc_loh_compaction_mode.loh_compaction_once == GCInterfaceOffsets.loh_compaction_once
            && (int)gc_loh_compaction_mode.loh_compaction_auto == GCInterfaceOffsets.loh_compaction_auto
            && (int)set_pause_mode_status.set_pause_mode_success == GCInterfaceOffsets.set_pause_mode_success
            && (int)set_pause_mode_status.set_pause_mode_no_gc == GCInterfaceOffsets.set_pause_mode_no_gc
            && (int)gc_latency_level.latency_level_first == GCInterfaceOffsets.latency_level_first
            && (int)gc_latency_level.latency_level_memory_footprint == GCInterfaceOffsets.latency_level_memory_footprint
            && (int)gc_latency_level.latency_level_balanced == GCInterfaceOffsets.latency_level_balanced
            && (int)gc_latency_level.latency_level_last == GCInterfaceOffsets.latency_level_last
            && (int)gc_latency_level.latency_level_default == GCInterfaceOffsets.latency_level_default
            && (int)gc_tuning_point.tuning_deciding_condemned_gen == GCInterfaceOffsets.tuning_deciding_condemned_gen
            && (int)gc_tuning_point.tuning_deciding_full_gc == GCInterfaceOffsets.tuning_deciding_full_gc
            && (int)gc_tuning_point.tuning_deciding_compaction == GCInterfaceOffsets.tuning_deciding_compaction
            && (int)gc_tuning_point.tuning_deciding_expansion == GCInterfaceOffsets.tuning_deciding_expansion
            && (int)gc_tuning_point.tuning_deciding_promote_ephemeral == GCInterfaceOffsets.tuning_deciding_promote_ephemeral
            && (int)gc_tuning_point.tuning_deciding_short_on_seg == GCInterfaceOffsets.tuning_deciding_short_on_seg
            && (int)gc_oh_num.soh == GCInterfaceOffsets.soh
            && (int)gc_oh_num.loh == GCInterfaceOffsets.loh
            && (int)gc_oh_num.poh == GCInterfaceOffsets.poh
            && (int)gc_oh_num.unknown == GCInterfaceOffsets.unknown
            && (int)memory_type.memory_type_reserved == GCInterfaceOffsets.memory_type_reserved
            && (int)memory_type.memory_type_committed == GCInterfaceOffsets.memory_type_committed
            && (int)allocation_state.a_state_start == GCInterfaceOffsets.a_state_start
            && (int)allocation_state.a_state_can_allocate == GCInterfaceOffsets.a_state_can_allocate
            && (int)allocation_state.a_state_cant_allocate == GCInterfaceOffsets.a_state_cant_allocate
            && (int)allocation_state.a_state_retry_allocate == GCInterfaceOffsets.a_state_retry_allocate
            && (int)allocation_state.a_state_try_fit == GCInterfaceOffsets.a_state_try_fit
            && (int)allocation_state.a_state_try_fit_new_seg == GCInterfaceOffsets.a_state_try_fit_new_seg
            && (int)allocation_state.a_state_try_fit_after_cg == GCInterfaceOffsets.a_state_try_fit_after_cg
            && (int)allocation_state.a_state_try_fit_after_bgc == GCInterfaceOffsets.a_state_try_fit_after_bgc
            && (int)allocation_state.a_state_try_free_full_seg_in_bgc == GCInterfaceOffsets.a_state_try_free_full_seg_in_bgc
            && (int)allocation_state.a_state_try_free_after_bgc == GCInterfaceOffsets.a_state_try_free_after_bgc
            && (int)allocation_state.a_state_try_seg_end == GCInterfaceOffsets.a_state_try_seg_end
            && (int)allocation_state.a_state_acquire_seg == GCInterfaceOffsets.a_state_acquire_seg
            && (int)allocation_state.a_state_acquire_seg_after_cg == GCInterfaceOffsets.a_state_acquire_seg_after_cg
            && (int)allocation_state.a_state_acquire_seg_after_bgc == GCInterfaceOffsets.a_state_acquire_seg_after_bgc
            && (int)allocation_state.a_state_check_and_wait_for_bgc == GCInterfaceOffsets.a_state_check_and_wait_for_bgc
            && (int)allocation_state.a_state_trigger_full_compact_gc == GCInterfaceOffsets.a_state_trigger_full_compact_gc
            && (int)allocation_state.a_state_trigger_ephemeral_gc == GCInterfaceOffsets.a_state_trigger_ephemeral_gc
            && (int)allocation_state.a_state_trigger_2nd_ephemeral_gc == GCInterfaceOffsets.a_state_trigger_2nd_ephemeral_gc
            && (int)allocation_state.a_state_check_retry_seg == GCInterfaceOffsets.a_state_check_retry_seg
            && (int)allocation_state.a_state_max == GCInterfaceOffsets.a_state_max
            && (int)enter_msl_status.msl_entered == GCInterfaceOffsets.msl_entered
            && (int)enter_msl_status.msl_retry_different_heap == GCInterfaceOffsets.msl_retry_different_heap
            && (int)msl_enter_state.me_acquire == GCInterfaceOffsets.me_acquire
            && (int)msl_enter_state.me_release == GCInterfaceOffsets.me_release
            && (int)interesting_data_point.idp_pre_short == GCInterfaceOffsets.idp_pre_short
            && (int)interesting_data_point.idp_post_short == GCInterfaceOffsets.idp_post_short
            && (int)interesting_data_point.idp_merged_pin == GCInterfaceOffsets.idp_merged_pin
            && (int)interesting_data_point.idp_converted_pin == GCInterfaceOffsets.idp_converted_pin
            && (int)interesting_data_point.idp_pre_pin == GCInterfaceOffsets.idp_pre_pin
            && (int)interesting_data_point.idp_post_pin == GCInterfaceOffsets.idp_post_pin
            && (int)interesting_data_point.idp_pre_and_post_pin == GCInterfaceOffsets.idp_pre_and_post_pin
            && (int)interesting_data_point.idp_pre_short_padded == GCInterfaceOffsets.idp_pre_short_padded
            && (int)interesting_data_point.idp_post_short_padded == GCInterfaceOffsets.idp_post_short_padded
            && (int)interesting_data_point.max_idp_count == GCInterfaceOffsets.max_idp_count;

        private static bool VerifyGCRecordEnums() =>
            (int)gc_reason.reason_alloc_soh == GCInterfaceOffsets.reason_alloc_soh
            && (int)gc_reason.reason_induced == GCInterfaceOffsets.reason_induced
            && (int)gc_reason.reason_lowmemory == GCInterfaceOffsets.reason_lowmemory
            && (int)gc_reason.reason_empty == GCInterfaceOffsets.reason_empty
            && (int)gc_reason.reason_alloc_loh == GCInterfaceOffsets.reason_alloc_loh
            && (int)gc_reason.reason_oos_soh == GCInterfaceOffsets.reason_oos_soh
            && (int)gc_reason.reason_oos_loh == GCInterfaceOffsets.reason_oos_loh
            && (int)gc_reason.reason_induced_noforce == GCInterfaceOffsets.reason_induced_noforce
            && (int)gc_reason.reason_gcstress == GCInterfaceOffsets.reason_gcstress
            && (int)gc_reason.reason_lowmemory_blocking == GCInterfaceOffsets.reason_lowmemory_blocking
            && (int)gc_reason.reason_induced_compacting == GCInterfaceOffsets.reason_induced_compacting
            && (int)gc_reason.reason_lowmemory_host == GCInterfaceOffsets.reason_lowmemory_host
            && (int)gc_reason.reason_pm_full_gc == GCInterfaceOffsets.reason_pm_full_gc
            && (int)gc_reason.reason_lowmemory_host_blocking == GCInterfaceOffsets.reason_lowmemory_host_blocking
            && (int)gc_reason.reason_bgc_tuning_soh == GCInterfaceOffsets.reason_bgc_tuning_soh
            && (int)gc_reason.reason_bgc_tuning_loh == GCInterfaceOffsets.reason_bgc_tuning_loh
            && (int)gc_reason.reason_bgc_stepping == GCInterfaceOffsets.reason_bgc_stepping
            && (int)gc_reason.reason_induced_aggressive == GCInterfaceOffsets.reason_induced_aggressive
            && (int)gc_reason.reason_max == GCInterfaceOffsets.reason_max
            && (int)gc_condemn_reason_gen.gen_initial == GCInterfaceOffsets.gen_initial
            && (int)gc_condemn_reason_gen.gen_final_per_heap == GCInterfaceOffsets.gen_final_per_heap
            && (int)gc_condemn_reason_gen.gen_alloc_budget == GCInterfaceOffsets.gen_alloc_budget
            && (int)gc_condemn_reason_gen.gen_time_tuning == GCInterfaceOffsets.gen_time_tuning
            && (int)gc_condemn_reason_gen.gcrg_max == GCInterfaceOffsets.gcrg_max
            && (int)gc_condemn_reason_condition.gen_induced_fullgc_p == GCInterfaceOffsets.gen_induced_fullgc_p
            && (int)gc_condemn_reason_condition.gen_expand_fullgc_p == GCInterfaceOffsets.gen_expand_fullgc_p
            && (int)gc_condemn_reason_condition.gen_high_mem_p == GCInterfaceOffsets.gen_high_mem_p
            && (int)gc_condemn_reason_condition.gen_very_high_mem_p == GCInterfaceOffsets.gen_very_high_mem_p
            && (int)gc_condemn_reason_condition.gen_low_ephemeral_p == GCInterfaceOffsets.gen_low_ephemeral_p
            && (int)gc_condemn_reason_condition.gen_low_card_p == GCInterfaceOffsets.gen_low_card_p
            && (int)gc_condemn_reason_condition.gen_eph_high_frag_p == GCInterfaceOffsets.gen_eph_high_frag_p
            && (int)gc_condemn_reason_condition.gen_max_high_frag_p == GCInterfaceOffsets.gen_max_high_frag_p
            && (int)gc_condemn_reason_condition.gen_max_high_frag_e_p == GCInterfaceOffsets.gen_max_high_frag_e_p
            && (int)gc_condemn_reason_condition.gen_max_high_frag_m_p == GCInterfaceOffsets.gen_max_high_frag_m_p
            && (int)gc_condemn_reason_condition.gen_max_high_frag_vm_p == GCInterfaceOffsets.gen_max_high_frag_vm_p
            && (int)gc_condemn_reason_condition.gen_max_gen1 == GCInterfaceOffsets.gen_max_gen1
            && (int)gc_condemn_reason_condition.gen_before_oom == GCInterfaceOffsets.gen_before_oom
            && (int)gc_condemn_reason_condition.gen_gen2_too_small == GCInterfaceOffsets.gen_gen2_too_small
            && (int)gc_condemn_reason_condition.gen_induced_noforce_p == GCInterfaceOffsets.gen_induced_noforce_p
            && (int)gc_condemn_reason_condition.gen_before_bgc == GCInterfaceOffsets.gen_before_bgc
            && (int)gc_condemn_reason_condition.gen_almost_max_alloc == GCInterfaceOffsets.gen_almost_max_alloc
            && (int)gc_condemn_reason_condition.gen_joined_avoid_unproductive == GCInterfaceOffsets.gen_joined_avoid_unproductive
            && (int)gc_condemn_reason_condition.gen_joined_pm_induced_fullgc_p == GCInterfaceOffsets.gen_joined_pm_induced_fullgc_p
            && (int)gc_condemn_reason_condition.gen_joined_pm_alloc_loh == GCInterfaceOffsets.gen_joined_pm_alloc_loh
            && (int)gc_condemn_reason_condition.gen_joined_gen1_in_pm == GCInterfaceOffsets.gen_joined_gen1_in_pm
            && (int)gc_condemn_reason_condition.gen_joined_limit_before_oom == GCInterfaceOffsets.gen_joined_limit_before_oom
            && (int)gc_condemn_reason_condition.gen_joined_limit_loh_frag == GCInterfaceOffsets.gen_joined_limit_loh_frag
            && (int)gc_condemn_reason_condition.gen_joined_limit_loh_reclaim == GCInterfaceOffsets.gen_joined_limit_loh_reclaim
            && (int)gc_condemn_reason_condition.gen_joined_servo_initial == GCInterfaceOffsets.gen_joined_servo_initial
            && (int)gc_condemn_reason_condition.gen_joined_servo_ngc == GCInterfaceOffsets.gen_joined_servo_ngc
            && (int)gc_condemn_reason_condition.gen_joined_servo_bgc == GCInterfaceOffsets.gen_joined_servo_bgc
            && (int)gc_condemn_reason_condition.gen_joined_servo_postpone == GCInterfaceOffsets.gen_joined_servo_postpone
            && (int)gc_condemn_reason_condition.gen_joined_stress_mix == GCInterfaceOffsets.gen_joined_stress_mix
            && (int)gc_condemn_reason_condition.gen_joined_stress == GCInterfaceOffsets.gen_joined_stress
            && (int)gc_condemn_reason_condition.gen_joined_aggressive == GCInterfaceOffsets.gen_joined_aggressive
            && (int)gc_condemn_reason_condition.gcrc_max == GCInterfaceOffsets.gcrc_max
            && (int)gc_heap_expand_mechanism.expand_reuse_normal == GCInterfaceOffsets.expand_reuse_normal
            && (int)gc_heap_expand_mechanism.expand_reuse_bestfit == GCInterfaceOffsets.expand_reuse_bestfit
            && (int)gc_heap_expand_mechanism.expand_new_seg_ep == GCInterfaceOffsets.expand_new_seg_ep
            && (int)gc_heap_expand_mechanism.expand_new_seg == GCInterfaceOffsets.expand_new_seg
            && (int)gc_heap_expand_mechanism.expand_no_memory == GCInterfaceOffsets.expand_no_memory
            && (int)gc_heap_expand_mechanism.expand_next_full_gc == GCInterfaceOffsets.expand_next_full_gc
            && (int)gc_heap_expand_mechanism.max_expand_mechanisms_count == GCInterfaceOffsets.max_expand_mechanisms_count
            && (int)gc_heap_compact_reason.compact_low_ephemeral == GCInterfaceOffsets.compact_low_ephemeral
            && (int)gc_heap_compact_reason.compact_high_frag == GCInterfaceOffsets.compact_high_frag
            && (int)gc_heap_compact_reason.compact_no_gaps == GCInterfaceOffsets.compact_no_gaps
            && (int)gc_heap_compact_reason.compact_loh_forced == GCInterfaceOffsets.compact_loh_forced
            && (int)gc_heap_compact_reason.compact_last_gc == GCInterfaceOffsets.compact_last_gc
            && (int)gc_heap_compact_reason.compact_induced_compacting == GCInterfaceOffsets.compact_induced_compacting
            && (int)gc_heap_compact_reason.compact_fragmented_gen0 == GCInterfaceOffsets.compact_fragmented_gen0
            && (int)gc_heap_compact_reason.compact_high_mem_load == GCInterfaceOffsets.compact_high_mem_load
            && (int)gc_heap_compact_reason.compact_high_mem_frag == GCInterfaceOffsets.compact_high_mem_frag
            && (int)gc_heap_compact_reason.compact_vhigh_mem_frag == GCInterfaceOffsets.compact_vhigh_mem_frag
            && (int)gc_heap_compact_reason.compact_no_gc_mode == GCInterfaceOffsets.compact_no_gc_mode
            && (int)gc_heap_compact_reason.compact_aggressive_compacting == GCInterfaceOffsets.compact_aggressive_compacting
            && (int)gc_heap_compact_reason.max_compact_reasons_count == GCInterfaceOffsets.max_compact_reasons_count
            && (int)gc_mechanism_per_heap.gc_heap_expand == GCInterfaceOffsets.gc_heap_expand
            && (int)gc_mechanism_per_heap.gc_heap_compact == GCInterfaceOffsets.gc_heap_compact
            && (int)gc_mechanism_per_heap.max_mechanism_per_heap == GCInterfaceOffsets.max_mechanism_per_heap
            && (int)gc_mechanism_bit_per_heap.gc_mark_list_bit == GCInterfaceOffsets.gc_mark_list_bit
            && (int)gc_mechanism_bit_per_heap.gc_demotion_bit == GCInterfaceOffsets.gc_demotion_bit
            && (int)gc_mechanism_bit_per_heap.max_gc_mechanism_bits_count == GCInterfaceOffsets.max_gc_mechanism_bits_count
            && (int)gc_global_mechanism_p.global_concurrent == GCInterfaceOffsets.global_concurrent
            && (int)gc_global_mechanism_p.global_compaction == GCInterfaceOffsets.global_compaction
            && (int)gc_global_mechanism_p.global_promotion == GCInterfaceOffsets.global_promotion
            && (int)gc_global_mechanism_p.global_demotion == GCInterfaceOffsets.global_demotion
            && (int)gc_global_mechanism_p.global_card_bundles == GCInterfaceOffsets.global_card_bundles
            && (int)gc_global_mechanism_p.global_elevation == GCInterfaceOffsets.global_elevation
            && (int)gc_global_mechanism_p.max_global_mechanisms_count == GCInterfaceOffsets.max_global_mechanisms_count;

        private static bool VerifySuspensionEnums() =>
            (int)SUSPEND_REASON.SUSPEND_FOR_GC == GCInterfaceOffsets.SUSPEND_FOR_GC
            && (int)SUSPEND_REASON.SUSPEND_FOR_GC_PREP == GCInterfaceOffsets.SUSPEND_FOR_GC_PREP
            && (int)walk_surv_type.walk_for_gc == GCInterfaceOffsets.walk_for_gc
            && (int)walk_surv_type.walk_for_bgc == GCInterfaceOffsets.walk_for_bgc
            && (int)walk_surv_type.walk_for_uoh == GCInterfaceOffsets.walk_for_uoh
            && (int)WriteBarrierOp.StompResize == GCInterfaceOffsets.WriteBarrierOp_StompResize
            && (int)WriteBarrierOp.StompEphemeral == GCInterfaceOffsets.WriteBarrierOp_StompEphemeral
            && (int)WriteBarrierOp.Initialize == GCInterfaceOffsets.WriteBarrierOp_Initialize
            && (int)WriteBarrierOp.SwitchToWriteWatch == GCInterfaceOffsets.WriteBarrierOp_SwitchToWriteWatch
            && (int)WriteBarrierOp.SwitchToNonWriteWatch == GCInterfaceOffsets.WriteBarrierOp_SwitchToNonWriteWatch;

        private static bool VerifyCollectionEnums() =>
            // collection_gcstress is absent from the table: the native enumerator only exists in
            // a STRESS_HEAP build, so there is nothing to assert it against.
            (int)collection_mode.collection_non_blocking == GCInterfaceOffsets.collection_non_blocking
            && (int)collection_mode.collection_blocking == GCInterfaceOffsets.collection_blocking
            && (int)collection_mode.collection_optimized == GCInterfaceOffsets.collection_optimized
            && (int)collection_mode.collection_compacting == GCInterfaceOffsets.collection_compacting
            && (int)collection_mode.collection_aggressive == GCInterfaceOffsets.collection_aggressive
            && (int)wait_full_gc_status.wait_full_gc_success == GCInterfaceOffsets.wait_full_gc_success
            && (int)wait_full_gc_status.wait_full_gc_failed == GCInterfaceOffsets.wait_full_gc_failed
            && (int)wait_full_gc_status.wait_full_gc_cancelled == GCInterfaceOffsets.wait_full_gc_cancelled
            && (int)wait_full_gc_status.wait_full_gc_timeout == GCInterfaceOffsets.wait_full_gc_timeout
            && (int)wait_full_gc_status.wait_full_gc_na == GCInterfaceOffsets.wait_full_gc_na
            && (int)start_no_gc_region_status.start_no_gc_success == GCInterfaceOffsets.start_no_gc_success
            && (int)start_no_gc_region_status.start_no_gc_no_memory == GCInterfaceOffsets.start_no_gc_no_memory
            && (int)start_no_gc_region_status.start_no_gc_too_large == GCInterfaceOffsets.start_no_gc_too_large
            && (int)start_no_gc_region_status.start_no_gc_in_progress == GCInterfaceOffsets.start_no_gc_in_progress
            && (int)end_no_gc_region_status.end_no_gc_success == GCInterfaceOffsets.end_no_gc_success
            && (int)end_no_gc_region_status.end_no_gc_not_in_progress == GCInterfaceOffsets.end_no_gc_not_in_progress
            && (int)end_no_gc_region_status.end_no_gc_induced == GCInterfaceOffsets.end_no_gc_induced
            && (int)end_no_gc_region_status.end_no_gc_alloc_exceeded == GCInterfaceOffsets.end_no_gc_alloc_exceeded
            && (int)refresh_memory_limit_status.refresh_success == GCInterfaceOffsets.refresh_success
            && (int)refresh_memory_limit_status.refresh_hard_limit_too_low == GCInterfaceOffsets.refresh_hard_limit_too_low
            && (int)refresh_memory_limit_status.refresh_hard_limit_invalid == GCInterfaceOffsets.refresh_hard_limit_invalid
            && (int)enable_no_gc_region_callback_status.succeed == GCInterfaceOffsets.succeed
            && (int)enable_no_gc_region_callback_status.not_started == GCInterfaceOffsets.not_started
            && (int)enable_no_gc_region_callback_status.insufficient_budget == GCInterfaceOffsets.insufficient_budget
            && (int)enable_no_gc_region_callback_status.already_registered == GCInterfaceOffsets.already_registered
            && (int)gc_kind.gc_kind_any == GCInterfaceOffsets.gc_kind_any
            && (int)gc_kind.gc_kind_ephemeral == GCInterfaceOffsets.gc_kind_ephemeral
            && (int)gc_kind.gc_kind_full_blocking == GCInterfaceOffsets.gc_kind_full_blocking
            && (int)gc_kind.gc_kind_background == GCInterfaceOffsets.gc_kind_background;

        private static bool VerifyHandleTypes() =>
            (int)HandleType.HNDTYPE_WEAK_SHORT == GCInterfaceOffsets.HNDTYPE_WEAK_SHORT
            && (int)HandleType.HNDTYPE_WEAK_LONG == GCInterfaceOffsets.HNDTYPE_WEAK_LONG
            && (int)HandleType.HNDTYPE_WEAK_DEFAULT == GCInterfaceOffsets.HNDTYPE_WEAK_DEFAULT
            && (int)HandleType.HNDTYPE_STRONG == GCInterfaceOffsets.HNDTYPE_STRONG
            && (int)HandleType.HNDTYPE_DEFAULT == GCInterfaceOffsets.HNDTYPE_DEFAULT
            && (int)HandleType.HNDTYPE_PINNED == GCInterfaceOffsets.HNDTYPE_PINNED
            && (int)HandleType.HNDTYPE_VARIABLE == GCInterfaceOffsets.HNDTYPE_VARIABLE
            && (int)HandleType.HNDTYPE_REFCOUNTED == GCInterfaceOffsets.HNDTYPE_REFCOUNTED
            && (int)HandleType.HNDTYPE_DEPENDENT == GCInterfaceOffsets.HNDTYPE_DEPENDENT
            && (int)HandleType.HNDTYPE_ASYNCPINNED == GCInterfaceOffsets.HNDTYPE_ASYNCPINNED
            && (int)HandleType.HNDTYPE_SIZEDREF == GCInterfaceOffsets.HNDTYPE_SIZEDREF
            && (int)HandleType.HNDTYPE_WEAK_NATIVE_COM == GCInterfaceOffsets.HNDTYPE_WEAK_NATIVE_COM
            && (int)HandleType.HNDTYPE_WEAK_INTERIOR_POINTER == GCInterfaceOffsets.HNDTYPE_WEAK_INTERIOR_POINTER
            && (int)HandleType.HNDTYPE_CROSSREFERENCE == GCInterfaceOffsets.HNDTYPE_CROSSREFERENCE;

        private static bool VerifyAllocationEnums() =>
            (int)GCHeapType.GC_HEAP_INVALID == GCInterfaceOffsets.GC_HEAP_INVALID
            && (int)GCHeapType.GC_HEAP_WKS == GCInterfaceOffsets.GC_HEAP_WKS
            && (int)GCHeapType.GC_HEAP_SVR == GCInterfaceOffsets.GC_HEAP_SVR
            && (int)GCConfigurationType.Int64 == GCInterfaceOffsets.GCConfigurationType_Int64
            && (int)GCConfigurationType.StringUtf8 == GCInterfaceOffsets.GCConfigurationType_StringUtf8
            && (int)GCConfigurationType.Boolean == GCInterfaceOffsets.GCConfigurationType_Boolean
            && (int)GC_ALLOC_FLAGS.GC_ALLOC_NO_FLAGS == GCInterfaceOffsets.GC_ALLOC_NO_FLAGS
            && (int)GC_ALLOC_FLAGS.GC_ALLOC_FINALIZE == GCInterfaceOffsets.GC_ALLOC_FINALIZE
            && (int)GC_ALLOC_FLAGS.GC_ALLOC_CONTAINS_REF == GCInterfaceOffsets.GC_ALLOC_CONTAINS_REF
            && (int)GC_ALLOC_FLAGS.GC_ALLOC_ALIGN8_BIAS == GCInterfaceOffsets.GC_ALLOC_ALIGN8_BIAS
            && (int)GC_ALLOC_FLAGS.GC_ALLOC_ALIGN8 == GCInterfaceOffsets.GC_ALLOC_ALIGN8
            && (int)GC_ALLOC_FLAGS.GC_ALLOC_ZEROING_OPTIONAL == GCInterfaceOffsets.GC_ALLOC_ZEROING_OPTIONAL
            && (int)GC_ALLOC_FLAGS.GC_ALLOC_LARGE_OBJECT_HEAP == GCInterfaceOffsets.GC_ALLOC_LARGE_OBJECT_HEAP
            && (int)GC_ALLOC_FLAGS.GC_ALLOC_PINNED_OBJECT_HEAP == GCInterfaceOffsets.GC_ALLOC_PINNED_OBJECT_HEAP
            && (int)GC_ALLOC_FLAGS.GC_ALLOC_USER_OLD_HEAP == GCInterfaceOffsets.GC_ALLOC_USER_OLD_HEAP
            && (int)GCCallFlags.GC_CALL_INTERIOR == GCInterfaceOffsets.GC_CALL_INTERIOR
            && (int)GCCallFlags.GC_CALL_PINNED == GCInterfaceOffsets.GC_CALL_PINNED;

        private static bool VerifyEventEnums() =>
            VerifyProviders() && VerifyLevels() && VerifyKeywords() && VerifyRootKinds();

        private static bool VerifyProviders() =>
            (int)GCEventProvider.Default == GCInterfaceOffsets.GCEventProvider_Default
            && (int)GCEventProvider.Private == GCInterfaceOffsets.GCEventProvider_Private
            // Count is not a native enumerator: it is the length of the two per-provider arrays
            // that gceventstatus.cpp declares, which GCEventStatus indexes by provider.
            && (int)GCEventProvider.Count == GCInterfaceOffsets.GCEventProvider_Private + 1;

        private static bool VerifyLevels() =>
            (int)GCEventLevel.None == GCInterfaceOffsets.GCEventLevel_None
            && (int)GCEventLevel.Fatal == GCInterfaceOffsets.GCEventLevel_Fatal
            && (int)GCEventLevel.Error == GCInterfaceOffsets.GCEventLevel_Error
            && (int)GCEventLevel.Warning == GCInterfaceOffsets.GCEventLevel_Warning
            && (int)GCEventLevel.Information == GCInterfaceOffsets.GCEventLevel_Information
            && (int)GCEventLevel.Verbose == GCInterfaceOffsets.GCEventLevel_Verbose
            && (int)GCEventLevel.Max == GCInterfaceOffsets.GCEventLevel_Max
            && (int)GCEventLevel.LogAlways == GCInterfaceOffsets.GCEventLevel_LogAlways;

        private static bool VerifyKeywords() =>
            (int)GCEventKeyword.None == GCInterfaceOffsets.GCEventKeyword_None
            && (int)GCEventKeyword.GC == GCInterfaceOffsets.GCEventKeyword_GC
            && (int)GCEventKeyword.GCPrivate == GCInterfaceOffsets.GCEventKeyword_GCPrivate
            && (int)GCEventKeyword.GCHandle == GCInterfaceOffsets.GCEventKeyword_GCHandle
            && (int)GCEventKeyword.GCHandlePrivate == GCInterfaceOffsets.GCEventKeyword_GCHandlePrivate
            && (int)GCEventKeyword.GCHeapDump == GCInterfaceOffsets.GCEventKeyword_GCHeapDump
            && (int)GCEventKeyword.GCSampledObjectAllocationHigh == GCInterfaceOffsets.GCEventKeyword_GCSampledObjectAllocationHigh
            && (int)GCEventKeyword.GCHeapSurvivalAndMovement == GCInterfaceOffsets.GCEventKeyword_GCHeapSurvivalAndMovement
            && (int)GCEventKeyword.ManagedHeapCollect == GCInterfaceOffsets.GCEventKeyword_ManagedHeapCollect
            && (int)GCEventKeyword.GCHeapAndTypeNames == GCInterfaceOffsets.GCEventKeyword_GCHeapAndTypeNames
            && (int)GCEventKeyword.GCSampledObjectAllocationLow == GCInterfaceOffsets.GCEventKeyword_GCSampledObjectAllocationLow
            && (int)GCEventKeyword.All == GCInterfaceOffsets.GCEventKeyword_All;

        private static bool VerifyRootKinds() =>
            (int)EtwGCRootKind.kEtwGCRootKindStack == GCInterfaceOffsets.kEtwGCRootKindStack
            && (int)EtwGCRootKind.kEtwGCRootKindFinalizer == GCInterfaceOffsets.kEtwGCRootKindFinalizer
            && (int)EtwGCRootKind.kEtwGCRootKindHandle == GCInterfaceOffsets.kEtwGCRootKindHandle
            && (int)EtwGCRootKind.kEtwGCRootKindOther == GCInterfaceOffsets.kEtwGCRootKindOther
            && (int)EtwGCRootFlags.kEtwGCRootFlagsPinning == GCInterfaceOffsets.kEtwGCRootFlagsPinning
            && (int)EtwGCRootFlags.kEtwGCRootFlagsWeakRef == GCInterfaceOffsets.kEtwGCRootFlagsWeakRef
            && (int)EtwGCRootFlags.kEtwGCRootFlagsInterior == GCInterfaceOffsets.kEtwGCRootFlagsInterior
            && (int)EtwGCRootFlags.kEtwGCRootFlagsRefCounted == GCInterfaceOffsets.kEtwGCRootFlagsRefCounted;

        private static bool VerifyDacEnums() =>
            (int)c_gc_state.c_gc_state_marking == GCInterfaceOffsets.c_gc_state_marking
            && (int)c_gc_state.c_gc_state_planning == GCInterfaceOffsets.c_gc_state_planning
            && (int)c_gc_state.c_gc_state_free == GCInterfaceOffsets.c_gc_state_free
            && (int)oom_reason.oom_no_failure == GCInterfaceOffsets.oom_no_failure
            && (int)oom_reason.oom_budget == GCInterfaceOffsets.oom_budget
            && (int)oom_reason.oom_cant_commit == GCInterfaceOffsets.oom_cant_commit
            && (int)oom_reason.oom_cant_reserve == GCInterfaceOffsets.oom_cant_reserve
            && (int)oom_reason.oom_loh == GCInterfaceOffsets.oom_loh
            && (int)oom_reason.oom_low_mem == GCInterfaceOffsets.oom_low_mem
            && (int)oom_reason.oom_unproductive_full_gc == GCInterfaceOffsets.oom_unproductive_full_gc
            && (int)failure_get_memory.fgm_no_failure == GCInterfaceOffsets.fgm_no_failure
            && (int)failure_get_memory.fgm_reserve_segment == GCInterfaceOffsets.fgm_reserve_segment
            && (int)failure_get_memory.fgm_commit_segment_beg == GCInterfaceOffsets.fgm_commit_segment_beg
            && (int)failure_get_memory.fgm_commit_eph_segment == GCInterfaceOffsets.fgm_commit_eph_segment
            && (int)failure_get_memory.fgm_grow_table == GCInterfaceOffsets.fgm_grow_table
            && (int)failure_get_memory.fgm_commit_table == GCInterfaceOffsets.fgm_commit_table;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int OffsetOf(void* structure, void* field) => (int)((byte*)field - (byte*)structure);

        /// <summary>
        /// The alignment C# gives <typeparamref name="T"/>, which is where a sequential struct
        /// places it after a single byte.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int AlignOf<T>() where T : unmanaged => sizeof(AlignProbe<T>) - sizeof(T);

        [StructLayout(LayoutKind.Sequential)]
        private struct AlignProbe<T> where T : unmanaged
        {
            public byte Pad;
            public T Value;
        }
    }
}
