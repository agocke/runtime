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
            && VerifyDacEnums();

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
