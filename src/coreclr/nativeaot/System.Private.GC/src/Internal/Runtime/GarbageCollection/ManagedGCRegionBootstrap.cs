// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Internal.Runtime.GarbageCollection;

#if USE_REGIONS
// This class owns the WKS region layout and its write-barrier range. Managed allocations use
// the dependency-closed region allocation path while it can allocate from the initial regions.
internal static unsafe class ManagedGCRegionBootstrap
{
    private const int S_OK = 0;
    private const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    private const nuint DefaultRegionRange = 256 * 1024 * 1024;

    private static gc_heap* s_heap;
    private static byte* s_reservedRegionRange;
    private static nuint s_reservedRegionRangeSize;
    private static byte* s_previousLowestAddress;
    private static byte* s_previousHighestAddress;
    private static seg_mapping* s_previousSegMappingTable;
    private static gc_heap.recorded_committed_bucket_array s_previousCommittedByOh;
    private static nuint s_previousCurrentTotalCommitted;
    private static nuint s_previousCurrentTotalCommittedBookkeeping;
    private static bool s_stateSaved;
    private static bool s_allocatorInitialized;
    private static bool s_bookkeepingInitialized;
    private static bool s_initialRegionsAllocated;
    private static bool s_initialized;
    private static bool s_writeBarrierPublished;

    // This is the region-specific portion of gc_heap::initialize_gc's configuration work. It is
    // deliberately separate from reservation so GC_Initialize can reject invalid configuration
    // before it exposes the heap vtable to the EE.
    public static int Prepare()
    {
        long configuredRegionSize = GCConfig.GetGCRegionSize();
        nuint regionSize = unchecked((nuint)configuredRegionSize);
        if (regionSize >= gc_heap.MAX_REGION_SIZE)
        {
            return GCEnv.CLR_E_GC_BAD_REGION_SIZE;
        }

        if (regionSize == 0)
        {
            regionSize = gc_heap.DefaultMinSegmentSize;
        }

        if (!gc_heap.power_of_two_p(regionSize))
        {
            return E_OUTOFMEMORY;
        }

        // Startup owns this allocator. Its unmanaged map cannot be inherited from a previous
        // failed startup attempt, and zeroing it before initialization makes rollback ownership
        // unambiguous.
        gc_heap.global_region_allocator = default;
        gc_heap.initial_regions = null;
        gc_heap.initialize_min_segment_size_shr(regionSize);
        gc_heap.global_region_allocator.initialize();
        gc_heap.initialize_gc_lock();
        GCWriteBarrier.initialize();
        return S_OK;
    }

    public static bool Initialize()
    {
        if (s_initialized)
        {
            return true;
        }

        long configuredRange = GCConfig.GetGCRegionRange();
        if (configuredRange < 0)
        {
            return false;
        }

        nuint rangeSize = configuredRange == 0 ? DefaultRegionRange : unchecked((nuint)configuredRange);
        nuint regionSize = (nuint)1 << (int)gc_heap.min_segment_size_shr;
        nuint requiredSize = unchecked(
            ((nuint)GCInterfaceOffsets.max_generation + 1 +
            ((nuint)2 * region_allocator.LARGE_REGION_FACTOR)) * regionSize);
        rangeSize &= ~(regionSize - 1);

        if (rangeSize < requiredSize)
        {
            return false;
        }

        GCConfig.SetGCRegionRange((long)rangeSize);
        SaveState();

        s_reservedRegionRange = GCToOSInterface.VirtualReserve(
            rangeSize,
            regionSize,
            (uint)VirtualReserveFlags.None);
        if (s_reservedRegionRange is null)
        {
            goto Fail;
        }

        s_reservedRegionRangeSize = rangeSize;
        bool allocatorInitialized;
        fixed (byte** lowest = &GCCommon.g_gc_lowest_address)
        fixed (byte** highest = &GCCommon.g_gc_highest_address)
        {
            allocatorInitialized = gc_heap.global_region_allocator.init(
                s_reservedRegionRange,
                s_reservedRegionRange + (nint)rangeSize,
                regionSize,
                lowest,
                highest);
        }

        if (!allocatorInitialized)
        {
            goto Fail;
        }

        s_allocatorInitialized = true;
        if (!gc_heap.initialize_region_bookkeeping())
        {
            goto Fail;
        }

        s_bookkeepingInitialized = true;
        bool initialRegionsAllocated = gc_heap.allocate_initial_regions(1);
        s_initialRegionsAllocated = gc_heap.initial_regions is not null;
        if (!initialRegionsAllocated ||
            gc_heap.on_used_changed(gc_heap.global_region_allocator.get_left_used_unsafe()) == 0)
        {
            goto Fail;
        }

        s_heap = (gc_heap*)SyncImports.ManagedGC_AllocZeroed((nuint)sizeof(gc_heap));
        if (s_heap is null)
        {
            goto Fail;
        }

        gc_heap.initialize_allocation_state(s_heap);

        // A gen1/gen0 construction can publish the region map before a later LOH/POH commit
        // fails, so cleanup must stomp the barrier before it releases the map.
        s_writeBarrierPublished = true;
        generation* generationTable = gc_heap.generation_table_of(s_heap);
        bool initialRegionsConstructed =
            gc_heap.initial_make_soh_regions(
                generationTable,
                &s_heap->ephemeral_heap_segment,
                &s_heap->alloc_allocated,
                s_heap) &&
            gc_heap.initial_make_uoh_regions((int)gc_generation_num.loh_generation, generationTable, s_heap) &&
            gc_heap.initial_make_uoh_regions((int)gc_generation_num.poh_generation, generationTable, s_heap);

        if (!initialRegionsConstructed)
        {
            goto Fail;
        }

        GCWriteBarrier.stomp_write_barrier_initialize(
            gc_heap.ephemeral_low,
            gc_heap.ephemeral_high,
            gc_heap.map_region_to_generation_skewed,
            (byte)gc_heap.min_segment_size_shr);
        s_initialized = true;
        return true;

    Fail:
        Cleanup();
        return false;
    }

    public static void Shutdown() => Cleanup();

    internal static bool IsInitialized => s_initialized;
    internal static gc_heap* Heap => s_heap;
    internal static generation* GenerationTable => s_heap is null ? null : gc_heap.generation_table_of(s_heap);
    internal static heap_segment* EphemeralHeapSegment => s_heap is null ? null : s_heap->ephemeral_heap_segment;
    internal static byte* AllocAllocated => s_heap is null ? null : s_heap->alloc_allocated;
    internal static byte* ReservedRegionRange => s_reservedRegionRange;
    internal static nuint ReservedRegionRangeSize => s_reservedRegionRangeSize;

    internal static nuint GetValidSegmentSize(bool largeSegment) =>
        largeSegment
            ? gc_heap.global_region_allocator.get_large_region_alignment()
            : gc_heap.global_region_allocator.get_region_alignment();

    internal static void DescribeGenerations(
        delegate* unmanaged<void*, int, byte*, byte*, byte*, void> callback,
        void* context)
    {
        if (!s_initialized)
        {
            return;
        }

        generation* generationTable = GenerationTable;
        for (int generationNumber = (int)gc_generation_num.total_generation_count - 1;
             generationNumber >= 0;
             generationNumber--)
        {
            generation* currentGeneration = gc_heap.generation_of(generationTable, generationNumber);
            heap_segment* segment = gc_heap.heap_segment_rw(
                generation.generation_start_segment(currentGeneration));
            while (segment is not null)
            {
                callback(
                    context,
                    generationNumber,
                    heap_segment.heap_segment_mem(segment),
                    heap_segment.heap_segment_allocated(segment),
                    heap_segment.heap_segment_reserved(segment));
                segment = gc_heap.heap_segment_next_rw(segment);
            }
        }
    }

    internal static heap_segment* FindSegment(byte* address, bool smallHeapOnly)
    {
        if (!s_initialized ||
            address < GCCommon.g_gc_lowest_address ||
            address >= GCCommon.g_gc_highest_address)
        {
            return null;
        }

        return gc_heap.try_get_region_segment(address, smallHeapOnly, out heap_segment* segment)
            ? segment
            : null;
    }

    internal static bool IsEphemeral(byte* address)
    {
        heap_segment* segment = FindSegment(address, smallHeapOnly: false);
        return segment is not null &&
            heap_segment.heap_segment_gen_num(segment) < GCInterfaceOffsets.max_generation;
    }

    internal static uint GenerationOf(byte* address)
    {
        heap_segment* segment = FindSegment(address, smallHeapOnly: false);
        return segment is null ? ManagedGCHeap.MaxGeneration : (uint)heap_segment.heap_segment_gen_num(segment);
    }

    internal static bool TryGetGenerationWithRange(
        byte* address,
        byte** start,
        byte** allocated,
        byte** reserved,
        uint* generation)
    {
        heap_segment* segment = FindSegment(address, smallHeapOnly: false);
        if (segment is null)
        {
            return false;
        }

        *start = heap_segment.heap_segment_mem(segment);
        *allocated = heap_segment.heap_segment_allocated(segment);
        *reserved = heap_segment.heap_segment_reserved(segment);
        *generation = heap_segment.heap_segment_loh_p(segment) != 0
            ? (uint)gc_generation_num.loh_generation
            : heap_segment.heap_segment_poh_p(segment) != 0
                ? (uint)gc_generation_num.poh_generation
                : (uint)heap_segment.heap_segment_gen_num(segment);
        return true;
    }

    private static void SaveState()
    {
        s_previousLowestAddress = GCCommon.g_gc_lowest_address;
        s_previousHighestAddress = GCCommon.g_gc_highest_address;
        s_previousSegMappingTable = GCCommon.seg_mapping_table;
        s_previousCommittedByOh = gc_heap.committed_by_oh;
        s_previousCurrentTotalCommitted = gc_heap.current_total_committed;
        s_previousCurrentTotalCommittedBookkeeping = gc_heap.current_total_committed_bookkeeping;
        s_stateSaved = true;
    }

    private static void Cleanup()
    {
        if (!s_stateSaved)
        {
            return;
        }

        gc_heap.initialize_mark_phase_state();

        if (s_writeBarrierPublished)
        {
            GCWriteBarrier.stomp_write_barrier_ephemeral(
                (byte*)nuint.MaxValue,
                null,
                null,
                0);
            gc_heap.ephemeral_low = (byte*)nuint.MaxValue;
            gc_heap.ephemeral_high = null;
            s_writeBarrierPublished = false;
        }

        if (s_heap is not null)
        {
            SyncImports.ManagedGC_Free(s_heap);
            s_heap = null;
        }

        if (s_initialRegionsAllocated)
        {
            gc_heap.free_initial_regions();
            s_initialRegionsAllocated = false;
        }

        if (s_bookkeepingInitialized)
        {
            gc_heap.free_region_bookkeeping();
            s_bookkeepingInitialized = false;
        }

        if (s_allocatorInitialized)
        {
            gc_heap.global_region_allocator.destroy();
            s_allocatorInitialized = false;
        }

        if (s_reservedRegionRange is not null)
        {
            GCToOSInterface.VirtualRelease(s_reservedRegionRange, s_reservedRegionRangeSize);
            s_reservedRegionRange = null;
            s_reservedRegionRangeSize = 0;
        }

        GCCommon.g_gc_lowest_address = s_previousLowestAddress;
        GCCommon.g_gc_highest_address = s_previousHighestAddress;
        GCCommon.seg_mapping_table = s_previousSegMappingTable;
        gc_heap.committed_by_oh = s_previousCommittedByOh;
        gc_heap.current_total_committed = s_previousCurrentTotalCommitted;
        gc_heap.current_total_committed_bookkeeping = s_previousCurrentTotalCommittedBookkeeping;
        s_stateSaved = false;
        s_initialized = false;
    }
}
#endif
