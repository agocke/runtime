// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Behavior tests for the virtual memory port of GCToOSInterface -- the translation of the
// mmap/mprotect/madvise sequences of gc/unix/gcenv.unix.cpp and of the VirtualAlloc/VirtualFree
// flag combinations of gc/windows/gcenv.windows.cpp.
//
// The ported bodies are the code under test. Only the libc/Win32 declarations underneath them
// are substituted, by GCToOSInterface.Imports.*.TestHost.cs, which forwards each call to the
// real kernel and records its arguments. So these tests check two things at once: that the
// arguments the port passes to the operating system are the ones the C++ passes, and that the
// resulting memory behaves the way the collector requires.
//
// The expected flag values are written out here rather than read from the constants of the port,
// so that a wrong constant fails a test instead of being confirmed by it. The C++ values they
// stand for are named in the comments.

using System;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

[Collection(SyncImportsCollection.Name)]
public sealed unsafe class GCVirtualMemoryTests
{
    private static nuint PageSize => GCToOSInterface.GetPageSize();

    /// <summary>
    /// An address that no mapping can ever occupy: Linux refuses to map below
    /// <c>vm.mmap_min_addr</c> and Windows reserves the first 64 KB as the null pointer region.
    /// The negative tests use it rather than an address they have just released, which another
    /// thread of the test process could have taken over in the meantime.
    /// </summary>
    private static void* NeverMappedAddress => (void*)(nint)0x1000;

    [Fact]
    public void GetPageSizeIsTheOperatingSystemPageSize()
    {
        Assert.Equal((nuint)Environment.SystemPageSize, GCToOSInterface.GetPageSize());
    }

    [Fact]
    public void GetVirtualMemoryLimitAndMaxAddressAreUsable()
    {
        nuint maxAddress = GCToOSInterface.GetVirtualMemoryMaxAddress();
        nuint limit = GCToOSInterface.GetVirtualMemoryLimit();

        Assert.NotEqual((nuint)0, maxAddress);
        Assert.NotEqual((nuint)0, limit);

#if TARGET_WINDOWS
        // GetVirtualMemoryMaxAddress is GetVirtualMemoryLimit on Windows. The two calls sample
        // the available address space at different moments, so they are only equal in order of
        // magnitude; what is being checked is that one is the other.
        Assert.True(maxAddress >= limit / 2 && maxAddress <= limit * 2);
#else
        // On Unix the maximum address is a constant -- 128TB on 64-bit, except RISC-V -- and
        // the limit is either RLIMIT_AS or that same constant.
        if (sizeof(nint) == 8)
        {
            Assert.True(maxAddress == unchecked((nuint)(1UL << 47)) || maxAddress == unchecked((nuint)(1UL << 38)));
        }
        else
        {
            Assert.Equal(unchecked((nuint)(-1)), maxAddress);
        }

        Assert.True(limit <= maxAddress);
#endif
    }

    /// <summary>
    /// The exercise the collector actually performs on a region: reserve address space, commit
    /// part of it, use it, reset it, decommit it, commit it again and release the whole
    /// reservation. It runs on raw pages throughout and never touches the managed heap.
    /// </summary>
    [Fact]
    public void ReservedPagesCanBeCommittedWrittenResetDecommittedAndReleased()
    {
        nuint pageSize = PageSize;
        nuint size = 4 * pageSize;
        const nuint Alignment = 64 * 1024;

        byte* region = GCToOSInterface.VirtualReserve(size, Alignment, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);
        Assert.Equal((nuint)0, (nuint)region & (Alignment - 1));

        try
        {
            Assert.True(GCToOSInterface.VirtualCommit(region, 2 * pageSize));

            // Freshly committed pages must read as zero: the collector hands them out without
            // clearing them.
            AssertRangeIsZero(region, 2 * pageSize);

            Fill(region, 2 * pageSize, 0xCD);
            AssertRangeIs(region, 2 * pageSize, 0xCD);

            // A reset says the contents are no longer of interest but leaves the range
            // committed and accessible. What it does not promise is that the contents survive:
            // MEM_RESET and MADV_FREE both allow the pages to be dropped at any moment, so the
            // range is committed again -- which the GC does too -- before it is used.
            Assert.True(GCToOSInterface.VirtualReset(region, 2 * pageSize, false));
            Assert.True(GCToOSInterface.VirtualCommit(region, 2 * pageSize));
            Fill(region, 2 * pageSize, 0xAB);
            AssertRangeIs(region, 2 * pageSize, 0xAB);

            // A decommit followed by a commit must produce zeroed pages again.
            Assert.True(GCToOSInterface.VirtualDecommit(region, pageSize));
            Assert.True(GCToOSInterface.VirtualCommit(region, pageSize));
            AssertRangeIsZero(region, pageSize);

            // The rest of the reservation is untouched by the decommit.
            AssertRangeIs(region + pageSize, pageSize, 0xAB);

            // Committing the remaining reserved pages still works.
            Assert.True(GCToOSInterface.VirtualCommit(region + 2 * pageSize, 2 * pageSize));
            AssertRangeIsZero(region + 2 * pageSize, 2 * pageSize);
            Fill(region + 2 * pageSize, 2 * pageSize, 0x5A);
            AssertRangeIs(region + 2 * pageSize, 2 * pageSize, 0x5A);

            Assert.True(GCToOSInterface.VirtualRelease(region, size));
            region = null;
        }
        finally
        {
            if (region != null)
            {
                GCToOSInterface.VirtualRelease(region, size);
            }
        }
    }

    [Fact]
    public void ReserveReturnsNullWhenTheAddressSpaceCannotSatisfyTheRequest()
    {
        // Half the address space, which no process can reserve.
        nuint size = unchecked((nuint)(-1)) / 2;
        Assert.True(GCToOSInterface.VirtualReserve(size, 0, (uint)VirtualReserveFlags.None) == null);
    }

    [Fact]
    public void CommitAndResetFailOnARangeThatIsNotReserved()
    {
        // Nothing is mapped there, so the operations on it have to report failure rather than
        // succeed silently.
        Assert.False(GCToOSInterface.VirtualCommit(NeverMappedAddress, PageSize));
        Assert.False(GCToOSInterface.VirtualReset(NeverMappedAddress, PageSize, false));
    }

    [Fact]
    public void HeapVirtualCommitTracksBookkeepingAndRollsBackFailedCommit()
    {
        using MemoryAccountingScope scope = new();
        nuint pageSize = PageSize;
        byte* region = GCToOSInterface.VirtualReserve(pageSize, 0, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);

        try
        {
            bool hardLimitExceeded = true;
            Assert.True(gc_heap.virtual_commit(region, pageSize, gc_heap.recorded_committed_bookkeeping_bucket, -1, &hardLimitExceeded));
            Assert.False(hardLimitExceeded);
            Assert.Equal(pageSize, gc_heap.committed_by_oh[gc_heap.recorded_committed_bookkeeping_bucket]);
            Assert.Equal(pageSize, gc_heap.current_total_committed);
            Assert.Equal(pageSize, gc_heap.current_total_committed_bookkeeping);

            Assert.False(gc_heap.virtual_commit(NeverMappedAddress, pageSize, gc_heap.recorded_committed_bookkeeping_bucket, -1, &hardLimitExceeded));
            Assert.False(hardLimitExceeded);
            Assert.Equal(pageSize, gc_heap.committed_by_oh[gc_heap.recorded_committed_bookkeeping_bucket]);
            Assert.Equal(pageSize, gc_heap.current_total_committed);
            Assert.Equal(pageSize, gc_heap.current_total_committed_bookkeeping);

            Assert.True(gc_heap.virtual_decommit(region, pageSize, gc_heap.recorded_committed_bookkeeping_bucket, -1));
            Assert.Equal((nuint)0, gc_heap.committed_by_oh[gc_heap.recorded_committed_bookkeeping_bucket]);
            Assert.Equal((nuint)0, gc_heap.current_total_committed);
            Assert.Equal((nuint)0, gc_heap.current_total_committed_bookkeeping);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(region, pageSize);
        }
    }

    [Fact]
    public void HeapVirtualCommitReportsHardLimitBeforeCallingTheOS()
    {
        using MemoryAccountingScope scope = new();
        nuint pageSize = PageSize;

        gc_heap.heap_hard_limit = pageSize;
        gc_heap.current_total_committed = pageSize;
        gc_heap.committed_by_oh[gc_heap.recorded_committed_bookkeeping_bucket] = pageSize;
        gc_heap.current_total_committed_bookkeeping = pageSize;

        GCToOSInterface.ResetRecording();
        bool hardLimitExceeded = false;
        Assert.False(gc_heap.virtual_commit(NeverMappedAddress, pageSize, gc_heap.recorded_committed_bookkeeping_bucket, -1, &hardLimitExceeded));
        Assert.True(hardLimitExceeded);
        AssertNoVirtualCommitWasRequested();
        Assert.Equal(pageSize, gc_heap.committed_by_oh[gc_heap.recorded_committed_bookkeeping_bucket]);
        Assert.Equal(pageSize, gc_heap.current_total_committed);
        Assert.Equal(pageSize, gc_heap.current_total_committed_bookkeeping);

        ResetMemoryAccounting();
        gc_heap.heap_hard_limit = 3 * pageSize;
        gc_heap.heap_hard_limit_oh[(int)gc_oh_num.soh] = pageSize;
        gc_heap.committed_by_oh[(int)gc_oh_num.soh] = pageSize;
        gc_heap.current_total_committed = pageSize;

        GCToOSInterface.ResetRecording();
        hardLimitExceeded = false;
        Assert.False(gc_heap.virtual_commit(NeverMappedAddress, pageSize, (int)gc_oh_num.soh, 0, &hardLimitExceeded));
        Assert.True(hardLimitExceeded);
        AssertNoVirtualCommitWasRequested();
        Assert.Equal(pageSize, gc_heap.committed_by_oh[(int)gc_oh_num.soh]);
        Assert.Equal(pageSize, gc_heap.current_total_committed);
    }

    [Fact]
    public void HeapVirtualCommitSkipsOSForNeverDecommitHeapMemory()
    {
        using MemoryAccountingScope scope = new();
        nuint pageSize = PageSize;

        gc_heap.never_decommit_p = true;
        GCToOSInterface.ResetRecording();
        Assert.True(gc_heap.virtual_commit(NeverMappedAddress, pageSize, (int)gc_oh_num.soh, 0));
        AssertNoVirtualCommitWasRequested();
        Assert.Equal(pageSize, gc_heap.committed_by_oh[(int)gc_oh_num.soh]);
        Assert.Equal(pageSize, gc_heap.current_total_committed);
        Assert.Equal((nuint)0, gc_heap.current_total_committed_bookkeeping);
    }

    [Fact]
    public void ReduceCommittedBytesIgnoresFailedDecommitAndVirtualFreeCountsOnlySuccessfulRelease()
    {
        using MemoryAccountingScope scope = new();
        nuint pageSize = PageSize;

        gc_heap.committed_by_oh[gc_heap.recorded_committed_bookkeeping_bucket] = pageSize;
        gc_heap.current_total_committed = pageSize;
        gc_heap.current_total_committed_bookkeeping = pageSize;
        gc_heap.reduce_committed_bytes(NeverMappedAddress, pageSize, gc_heap.recorded_committed_bookkeeping_bucket, -1, false);
        Assert.Equal(pageSize, gc_heap.committed_by_oh[gc_heap.recorded_committed_bookkeeping_bucket]);
        Assert.Equal(pageSize, gc_heap.current_total_committed);
        Assert.Equal(pageSize, gc_heap.current_total_committed_bookkeeping);

        gc_heap.reduce_committed_bytes(NeverMappedAddress, pageSize, gc_heap.recorded_committed_bookkeeping_bucket, -1, true);
        Assert.Equal((nuint)0, gc_heap.committed_by_oh[gc_heap.recorded_committed_bookkeeping_bucket]);
        Assert.Equal((nuint)0, gc_heap.current_total_committed);
        Assert.Equal((nuint)0, gc_heap.current_total_committed_bookkeeping);

        byte* region = GCToOSInterface.VirtualReserve(pageSize, 0, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);
        gc_heap.reserved_memory = pageSize;
        gc_heap.virtual_free(region, pageSize, null);
        Assert.Equal((nuint)0, gc_heap.reserved_memory);

        gc_heap.reserved_memory = pageSize;
        gc_heap.virtual_free((byte*)0x1001, pageSize, null);
        Assert.Equal(pageSize, gc_heap.reserved_memory);
    }

#if USE_REGIONS
    [Fact]
    public void OnUsedChangedExtendsBookkeepingCoverageAndRoundsCommitsToPages()
    {
        using MemoryAccountingScope accounting = new();
        nuint pageSize = PageSize;
        const nuint ReservationSize = 128 * 1024 * 1024;
        byte* reservation = GCToOSInterface.VirtualReserve(ReservationSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation != null);

        using RegionAllocationScope regions = new(reservation, ReservationSize, pageSize);
        try
        {
            Assert.True(regions.InitializeBookkeeping());
            byte* newUsed = reservation + (nint)(33 * pageSize + 1);
            nuint committedBefore = gc_heap.current_total_committed_bookkeeping;

            Assert.Equal((byte)1, gc_heap.on_used_changed(newUsed));

            Assert.Equal((nuint)newUsed, (nuint)gc_heap.bookkeeping_covered_committed);
            Assert.Equal(
                gc_heap.size_seg_mapping_table_of(reservation, newUsed),
                gc_heap.bookkeeping_sizes[(int)bookkeeping_element.seg_mapping_table_element]);
            Assert.True(gc_heap.current_total_committed_bookkeeping > committedBefore);
            Assert.Equal(
                (nuint)0,
                (gc_heap.current_total_committed_bookkeeping - committedBefore) & (pageSize - 1));
        }
        finally
        {
            GCToOSInterface.VirtualRelease(reservation, ReservationSize);
        }
    }

    [Fact]
    public void OnUsedChangedIsANoOpWithinCommittedCoverage()
    {
        using MemoryAccountingScope accounting = new();
        nuint pageSize = PageSize;
        byte* reservation = GCToOSInterface.VirtualReserve(8 * pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation != null);

        using RegionAllocationScope regions = new(reservation, 8 * pageSize, pageSize);
        try
        {
            Assert.True(regions.InitializeBookkeeping());
            byte* coverage = gc_heap.bookkeeping_covered_committed;
            nuint committed = gc_heap.current_total_committed_bookkeeping;

            GCToOSInterface.ResetRecording();
            Assert.Equal((byte)1, gc_heap.on_used_changed(coverage));

            Assert.Equal((nuint)coverage, (nuint)gc_heap.bookkeeping_covered_committed);
            Assert.Equal(committed, gc_heap.current_total_committed_bookkeeping);
            AssertNoVirtualCommitWasRequested();
        }
        finally
        {
            GCToOSInterface.VirtualRelease(reservation, 8 * pageSize);
        }
    }

    [Fact]
    public void OnUsedChangedFailureLeavesCoverageAndSizesUnchanged()
    {
        using MemoryAccountingScope accounting = new();
        nuint pageSize = PageSize;
        const nuint ReservationSize = 128 * 1024 * 1024;
        byte* reservation = GCToOSInterface.VirtualReserve(ReservationSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation != null);

        using RegionAllocationScope regions = new(reservation, ReservationSize, pageSize);
        try
        {
            Assert.True(regions.InitializeBookkeeping());
            byte* coverage = gc_heap.bookkeeping_covered_committed;
            nuint cardSize = gc_heap.bookkeeping_sizes[(int)bookkeeping_element.card_table_element];
            nuint brickSize = gc_heap.bookkeeping_sizes[(int)bookkeeping_element.brick_table_element];
            nuint regionSize = gc_heap.bookkeeping_sizes[(int)bookkeeping_element.region_to_generation_table_element];
            nuint mappingSize = gc_heap.bookkeeping_sizes[(int)bookkeeping_element.seg_mapping_table_element];
            gc_heap.heap_hard_limit = 1;

            Assert.Equal((byte)0, gc_heap.on_used_changed(reservation + (nint)(65 * pageSize)));

            Assert.Equal((nuint)coverage, (nuint)gc_heap.bookkeeping_covered_committed);
            Assert.Equal(cardSize, gc_heap.bookkeeping_sizes[(int)bookkeeping_element.card_table_element]);
            Assert.Equal(brickSize, gc_heap.bookkeeping_sizes[(int)bookkeeping_element.brick_table_element]);
            Assert.Equal(regionSize, gc_heap.bookkeeping_sizes[(int)bookkeeping_element.region_to_generation_table_element]);
            Assert.Equal(mappingSize, gc_heap.bookkeeping_sizes[(int)bookkeeping_element.seg_mapping_table_element]);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(reservation, ReservationSize);
        }
    }

    [Fact]
    public void SubsequentRegionAllocationUsesCommittedBookkeeping()
    {
        using MemoryAccountingScope accounting = new();
        nuint pageSize = PageSize;
        byte* reservation = GCToOSInterface.VirtualReserve(8 * pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation != null);

        using RegionAllocationScope regions = new(reservation, 8 * pageSize, pageSize);
        try
        {
            Assert.True(regions.InitializeBookkeeping());

            heap_segment* region = gc_heap.get_free_region(
                (gc_heap*)0x1234,
                (int)gc_generation_num.soh_gen2);

            Assert.True(region != null);
            Assert.Equal((nint)(-1), gc_heap.brick_table[(nint)gc_heap.brick_of(heap_segment.heap_segment_mem(region))]);
            Assert.Equal((nuint)region, (nuint)gc_heap.get_region_info(gc_heap.get_region_start(region)));
            Assert.Equal(
                (byte)((int)gc_generation_num.soh_gen2 | ((int)gc_generation_num.soh_gen2 << (int)region_info.RI_PLAN_GEN_SHR)),
                (byte)gc_heap.map_region_to_generation[0]);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(reservation, 8 * pageSize);
        }
    }

    [Fact]
    public void AllocateNewRegionBuildsAndMapsBasicRegion()
    {
        using MemoryAccountingScope accounting = new();
        nuint pageSize = PageSize;
        byte* reservation = GCToOSInterface.VirtualReserve(4 * pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation != null);

        using RegionAllocationScope regions = new(reservation, 4 * pageSize, pageSize);
        try
        {
            heap_segment* region = gc_heap.allocate_new_region(
                (gc_heap*)0x1234,
                (int)gc_generation_num.soh_gen2,
                uoh_p: false);

            Assert.True(region != null);
            Assert.Equal((nuint)(reservation + sizeof(aligned_plug_and_gap)), (nuint)heap_segment.heap_segment_mem(region));
            Assert.Equal((nuint)heap_segment.heap_segment_mem(region), (nuint)heap_segment.heap_segment_used(region));
            Assert.Equal((nuint)heap_segment.heap_segment_mem(region), (nuint)heap_segment.heap_segment_allocated(region));
            Assert.Equal((nuint)heap_segment.heap_segment_mem(region), (nuint)heap_segment.heap_segment_plan_allocated(region));
            Assert.Equal((nuint)heap_segment.heap_segment_mem(region), (nuint)heap_segment.heap_segment_saved_allocated(region));
            Assert.Equal((nuint)(reservation + (nint)pageSize), (nuint)heap_segment.heap_segment_reserved(region));
            Assert.Equal((nuint)(reservation + (nint)pageSize), (nuint)heap_segment.heap_segment_committed(region));
            Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_next(region));
            Assert.Equal((int)gc_generation_num.soh_gen2, heap_segment.heap_segment_gen_num(region));
            Assert.Equal((int)gc_generation_num.soh_gen2, heap_segment.heap_segment_plan_gen_num(region));
            Assert.Equal((nuint)region, (nuint)gc_heap.get_region_info(reservation));
            Assert.Equal(
                (byte)((int)gc_generation_num.soh_gen2 | ((int)gc_generation_num.soh_gen2 << (int)region_info.RI_PLAN_GEN_SHR)),
                (byte)gc_heap.map_region_to_generation[0]);
            Assert.Equal(pageSize, gc_heap.committed_by_oh[(int)gc_oh_num.soh]);
            Assert.Equal(pageSize, gc_heap.committed_by_oh[gc_heap.recorded_committed_bookkeeping_bucket]);
            Assert.Equal(2 * pageSize, gc_heap.current_total_committed);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(reservation, 4 * pageSize);
        }
    }

    [Fact]
    public void InitialMakeSohRegionsBuildsGen2ThroughGen0AndPublishesGen0()
    {
        using MemoryAccountingScope accounting = new();
        nuint pageSize = PageSize;
        byte* reservation = GCToOSInterface.VirtualReserve(24 * pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation != null);

        using RegionAllocationScope regions = new(reservation, 24 * pageSize, pageSize);
        byte** initialRegions = null;
        try
        {
            GCWriteBarrier.initialize();
            Assert.True(gc_heap.allocate_initial_regions(1));
            initialRegions = gc_heap.initial_regions;

            generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
            InitializeGenerations(generations);
            heap_segment* ephemeralHeapSegment = (heap_segment*)0x1234;
            byte* allocAllocated = (byte*)0x5678;

            Assert.True(gc_heap.initial_make_soh_regions(
                generations,
                &ephemeralHeapSegment,
                &allocAllocated,
                (gc_heap*)0x9ABC));

            AssertInitialSohGeneration(generations, (int)gc_generation_num.soh_gen2, reservation + (nint)(8 * pageSize));
            AssertInitialSohGeneration(generations, (int)gc_generation_num.soh_gen1, reservation + (nint)(9 * pageSize));
            AssertInitialSohGeneration(generations, (int)gc_generation_num.soh_gen0, reservation + (nint)(10 * pageSize));

            generation* gen0 = gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0);
            Assert.Equal((nuint)generation.generation_allocation_segment(gen0), (nuint)ephemeralHeapSegment);
            Assert.Equal((nuint)heap_segment.heap_segment_allocated(ephemeralHeapSegment), (nuint)allocAllocated);
            Assert.Equal((nuint)(reservation + (nint)(9 * pageSize)), (nuint)gc_heap.ephemeral_low);
            Assert.Equal((nuint)(reservation + (nint)(11 * pageSize)), (nuint)gc_heap.ephemeral_high);
            Assert.Equal(3 * pageSize, gc_heap.committed_by_oh[(int)gc_oh_num.soh]);
            Assert.Equal(3 * pageSize, gc_heap.current_total_committed);
        }
        finally
        {
            if (initialRegions is not null)
            {
                SyncImports.ManagedGC_Free(initialRegions);
            }

            GCToOSInterface.VirtualRelease(reservation, 24 * pageSize);
        }
    }

    [Fact]
    public void InitialMakeSohRegionsStopsAfterMakeHeapSegmentFailure()
    {
        using MemoryAccountingScope accounting = new();
        nuint pageSize = PageSize;
        byte* reservation = GCToOSInterface.VirtualReserve(24 * pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation != null);

        using RegionAllocationScope regions = new(reservation, 24 * pageSize, pageSize);
        byte** initialRegions = null;
        try
        {
            GCWriteBarrier.initialize();
            Assert.True(gc_heap.allocate_initial_regions(1));
            initialRegions = gc_heap.initial_regions;

            generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
            InitializeGenerations(generations);
            generation* gen1 = gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen1);
            generation* gen0 = gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0);
            gen1->gen_num = 101;
            gen0->gen_num = 102;
            generation.generation_start_segment(gen1) = (heap_segment*)0x1111;
            generation.generation_start_segment(gen0) = (heap_segment*)0x2222;
            heap_segment* ephemeralHeapSegment = (heap_segment*)0x3333;
            byte* allocAllocated = (byte*)0x4444;
            gc_heap.heap_hard_limit = pageSize;

            Assert.False(gc_heap.initial_make_soh_regions(
                generations,
                &ephemeralHeapSegment,
                &allocAllocated,
                (gc_heap*)0x9ABC));

            AssertInitialSohGeneration(generations, (int)gc_generation_num.soh_gen2, reservation + (nint)(8 * pageSize));
            Assert.Equal(101, gen1->gen_num);
            Assert.Equal(102, gen0->gen_num);
            Assert.Equal((nuint)0x1111, (nuint)generation.generation_start_segment(gen1));
            Assert.Equal((nuint)0x2222, (nuint)generation.generation_start_segment(gen0));
            Assert.Equal((nuint)0x3333, (nuint)ephemeralHeapSegment);
            Assert.Equal((nuint)0x4444, (nuint)allocAllocated);
            Assert.Equal(pageSize, gc_heap.committed_by_oh[(int)gc_oh_num.soh]);
            Assert.Equal(pageSize, gc_heap.current_total_committed);
        }
        finally
        {
            if (initialRegions is not null)
            {
                SyncImports.ManagedGC_Free(initialRegions);
            }

            GCToOSInterface.VirtualRelease(reservation, 24 * pageSize);
        }
    }

    [Fact]
    public void InitialMakeUohRegionsSetsLohAndPohFlagsAndAccountsCommit()
    {
        using MemoryAccountingScope accounting = new();
        nuint pageSize = PageSize;
        byte* reservation = GCToOSInterface.VirtualReserve(24 * pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation != null);

        using RegionAllocationScope regions = new(reservation, 24 * pageSize, pageSize);
        byte** initialRegions = null;
        try
        {
            Assert.True(gc_heap.allocate_initial_regions(1));
            initialRegions = gc_heap.initial_regions;

            generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
            InitializeGenerations(generations);

            Assert.True(gc_heap.initial_make_uoh_regions(
                (int)gc_generation_num.loh_generation,
                generations,
                (gc_heap*)0x9ABC));
            Assert.True(gc_heap.initial_make_uoh_regions(
                (int)gc_generation_num.poh_generation,
                generations,
                (gc_heap*)0x9ABC));

            AssertInitialUohGeneration(
                generations,
                (int)gc_generation_num.loh_generation,
                reservation + (nint)(11 * pageSize),
                heap_segment.heap_segment_flags_loh);
            AssertInitialUohGeneration(
                generations,
                (int)gc_generation_num.poh_generation,
                reservation,
                heap_segment.heap_segment_flags_poh);
            Assert.Equal(pageSize, gc_heap.committed_by_oh[(int)gc_oh_num.loh]);
            Assert.Equal(pageSize, gc_heap.committed_by_oh[(int)gc_oh_num.poh]);
            Assert.Equal(2 * pageSize, gc_heap.current_total_committed);
        }
        finally
        {
            if (initialRegions is not null)
            {
                SyncImports.ManagedGC_Free(initialRegions);
            }

            GCToOSInterface.VirtualRelease(reservation, 24 * pageSize);
        }
    }

    [Fact]
    public void InitialMakeUohRegionsLeavesGenerationAndAccountingOnFailure()
    {
        using MemoryAccountingScope accounting = new();
        nuint pageSize = PageSize;
        byte* reservation = GCToOSInterface.VirtualReserve(24 * pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation != null);

        using RegionAllocationScope regions = new(reservation, 24 * pageSize, pageSize);
        byte** initialRegions = null;
        try
        {
            Assert.True(gc_heap.allocate_initial_regions(1));
            initialRegions = gc_heap.initial_regions;

            generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
            InitializeGenerations(generations);
            generation* loh = gc_heap.generation_of(generations, (int)gc_generation_num.loh_generation);
            loh->gen_num = 201;
            generation.generation_start_segment(loh) = (heap_segment*)0x1234;
            gc_heap.heap_hard_limit = pageSize - 1;

            Assert.False(gc_heap.initial_make_uoh_regions(
                (int)gc_generation_num.loh_generation,
                generations,
                (gc_heap*)0x9ABC));

            Assert.Equal(201, loh->gen_num);
            Assert.Equal((nuint)0x1234, (nuint)generation.generation_start_segment(loh));
            Assert.Equal((nuint)0, gc_heap.committed_by_oh[(int)gc_oh_num.loh]);
            Assert.Equal((nuint)0, gc_heap.current_total_committed);
        }
        finally
        {
            if (initialRegions is not null)
            {
                SyncImports.ManagedGC_Free(initialRegions);
            }

            GCToOSInterface.VirtualRelease(reservation, 24 * pageSize);
        }
    }

    [Fact]
    public void AllocateNewRegionRoundsUohBoundariesAndRollsBackCommitFailure()
    {
        using MemoryAccountingScope accounting = new();
        nuint pageSize = PageSize;
        nuint largeRegionSize = region_allocator.LARGE_REGION_FACTOR * pageSize;
        byte* reservation = GCToOSInterface.VirtualReserve(40 * pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation != null);

        using RegionAllocationScope regions = new(reservation, 40 * pageSize, pageSize);
        try
        {
            heap_segment* large = gc_heap.allocate_new_region(
                (gc_heap*)0x1234,
                (int)gc_generation_num.loh_generation,
                uoh_p: true,
                largeRegionSize - 1);
            heap_segment* huge = gc_heap.allocate_new_region(
                (gc_heap*)0x1234,
                (int)gc_generation_num.poh_generation,
                uoh_p: true,
                largeRegionSize + 1);

            Assert.True(large != null);
            Assert.True(huge != null);
            Assert.Equal(largeRegionSize, gc_heap.get_region_size(large));
            Assert.Equal(2 * largeRegionSize, gc_heap.get_region_size(huge));
            Assert.Equal((nuint)0, large->flags & (heap_segment.heap_segment_flags_loh | heap_segment.heap_segment_flags_poh));
            Assert.Equal((nuint)0, huge->flags & (heap_segment.heap_segment_flags_loh | heap_segment.heap_segment_flags_poh));
            Assert.Equal((byte)GCInterfaceOffsets.max_generation, heap_segment.heap_segment_gen_num(huge));
            Assert.Equal(GCInterfaceOffsets.max_generation, heap_segment.heap_segment_plan_gen_num(huge));

            byte* hugeStart = gc_heap.get_region_start(huge);
            int hugeBasicRegionCount = (int)(gc_heap.get_region_size(huge) / pageSize);
            for (int i = 1; i < hugeBasicRegionCount; i++)
            {
                heap_segment* continuation = gc_heap.get_region_info(hugeStart + ((nuint)i * pageSize));
                Assert.Equal((nint)(-i), (nint)heap_segment.heap_segment_allocated(continuation));
                Assert.Equal((byte)GCInterfaceOffsets.max_generation, heap_segment.heap_segment_gen_num(continuation));
                Assert.Equal(GCInterfaceOffsets.max_generation, heap_segment.heap_segment_plan_gen_num(continuation));
            }

            nuint freeBeforeFailure = gc_heap.global_region_allocator.get_free();
            nuint committedBeforeFailure = gc_heap.current_total_committed;
            gc_heap.heap_hard_limit = committedBeforeFailure + pageSize - 1;
            heap_segment* failed = gc_heap.allocate_new_region(
                (gc_heap*)0x1234,
                (int)gc_generation_num.soh_gen2,
                uoh_p: false);

            Assert.True(failed is null);
            Assert.Equal(freeBeforeFailure, gc_heap.global_region_allocator.get_free());
            Assert.Equal(committedBeforeFailure, gc_heap.current_total_committed);
            Assert.Equal((nuint)0, (nuint)gc_heap.get_region_info(hugeStart + (nint)(2 * largeRegionSize))->allocated);

            gc_heap.heap_hard_limit = 0;
            Assert.True(gc_heap.allocate_new_region(
                (gc_heap*)0x1234,
                (int)gc_generation_num.loh_generation,
                uoh_p: true) != null);
            Assert.True(gc_heap.allocate_new_region(
                (gc_heap*)0x1234,
                (int)gc_generation_num.poh_generation,
                uoh_p: true) != null);
            Assert.True(gc_heap.allocate_new_region(
                (gc_heap*)0x1234,
                (int)gc_generation_num.soh_gen2,
                uoh_p: false) is null);
            Assert.Equal((nuint)0, gc_heap.global_region_allocator.get_free());
        }
        finally
        {
            GCToOSInterface.VirtualRelease(reservation, 40 * pageSize);
        }
    }

    [Fact]
    [Trait("Category", "GetFreeRegion")]
    public void GetFreeRegionReusesBasicAndLargeRegionsAndTransfersCommittedAccounting()
    {
        using MemoryAccountingScope accounting = new();
        nuint pageSize = PageSize;
        nuint largeRegionSize = region_allocator.LARGE_REGION_FACTOR * pageSize;
        byte* reservation = GCToOSInterface.VirtualReserve(16 * pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation != null);

        using RegionAllocationScope regions = new(reservation, 16 * pageSize, pageSize);
        try
        {
            short* bricks = stackalloc short[16];
            gc_heap.brick_table = bricks;
#if BACKGROUND_GC
            gc_heap.lowest_address = reservation;
            gc_heap.highest_address = reservation + (nint)(16 * pageSize);
#endif

            heap_segment* basic = gc_heap.allocate_new_region((gc_heap*)0x1234, (int)gc_generation_num.soh_gen2, uoh_p: false);
            heap_segment* large = gc_heap.allocate_new_region((gc_heap*)0x1234, (int)gc_generation_num.loh_generation, uoh_p: true, largeRegionSize);
            Assert.True(basic != null);
            Assert.True(large != null);

            AddRegionToFreeList(basic, (int)gc_oh_num.soh);
            AddRegionToFreeList(large, (int)gc_oh_num.loh);
            heap_segment.heap_segment_allocated(basic) = (byte*)0x1234;
            heap_segment.heap_segment_plan_allocated(basic) = (byte*)0x5678;
            heap_segment.heap_segment_saved_allocated(basic) = (byte*)0x9ABC;
            bricks[(nint)gc_heap.brick_of(heap_segment.heap_segment_mem(basic))] = 17;

            heap_segment* reusedBasic = gc_heap.get_free_region((gc_heap*)0x1234, (int)gc_generation_num.soh_gen2);
            bricks[(nint)gc_heap.brick_of(heap_segment.heap_segment_mem(large))] = 0;
            heap_segment* reusedLarge = gc_heap.get_free_region(
                (gc_heap*)0x1234,
                (int)gc_generation_num.loh_generation,
                largeRegionSize);

            Assert.Equal((nuint)basic, (nuint)reusedBasic);
            Assert.Equal((nuint)large, (nuint)reusedLarge);
            Assert.Equal((nuint)heap_segment.heap_segment_mem(basic), (nuint)heap_segment.heap_segment_allocated(basic));
            Assert.Equal((nuint)heap_segment.heap_segment_mem(basic), (nuint)heap_segment.heap_segment_plan_allocated(basic));
            Assert.Equal((nuint)heap_segment.heap_segment_mem(basic), (nuint)heap_segment.heap_segment_saved_allocated(basic));
            Assert.Equal((int)gc_generation_num.soh_gen2, heap_segment.heap_segment_gen_num(basic));
            Assert.Equal((int)gc_generation_num.soh_gen2, heap_segment.heap_segment_plan_gen_num(basic));
            Assert.Equal(-1, bricks[(nint)gc_heap.brick_of(heap_segment.heap_segment_mem(basic))]);
            Assert.Equal(0, heap_segment.heap_segment_uoh_p(large));
            Assert.Equal((nuint)0, gc_heap.committed_by_oh[gc_heap.recorded_committed_free_bucket]);
            Assert.Equal(pageSize, gc_heap.committed_by_oh[(int)gc_oh_num.soh]);
            Assert.Equal(pageSize, gc_heap.committed_by_oh[(int)gc_oh_num.loh]);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(reservation, 16 * pageSize);
        }
    }

    [Fact]
    [Trait("Category", "GetNewRegion")]
    public void GetNewRegionThreadsUohTailSetsFlagsAndLeavesStateOnFailure()
    {
        using MemoryAccountingScope accounting = new();
        nuint pageSize = PageSize;
        nuint largeRegionSize = region_allocator.LARGE_REGION_FACTOR * pageSize;
        byte* reservation = GCToOSInterface.VirtualReserve(32 * pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation != null);

        using RegionAllocationScope regions = new(reservation, 32 * pageSize, pageSize);
        try
        {
            short* bricks = stackalloc short[32];
            gc_heap.brick_table = bricks;
#if BACKGROUND_GC
            gc_heap.lowest_address = reservation;
            gc_heap.highest_address = reservation + (nint)(32 * pageSize);
#endif

            heap_segment* lohFree = gc_heap.allocate_new_region(
                (gc_heap*)0x1234,
                (int)gc_generation_num.loh_generation,
                uoh_p: true,
                largeRegionSize);
            heap_segment* pohFree = gc_heap.allocate_new_region(
                (gc_heap*)0x1234,
                (int)gc_generation_num.poh_generation,
                uoh_p: true,
                largeRegionSize);
            Assert.True(lohFree != null);
            Assert.True(pohFree != null);
            nuint lohCommitted = gc_heap.get_region_committed_size(lohFree);
            gc_heap.committed_by_oh[(int)gc_oh_num.loh] -= lohCommitted;
            gc_heap.committed_by_oh[gc_heap.recorded_committed_free_bucket] += lohCommitted;
            region_free_list.add_region(
                lohFree,
                gc_heap.free_regions_of((int)free_region_kind.basic_free_region));
            Assert.Equal(largeRegionSize, gc_heap.global_region_allocator.get_large_region_alignment());
            Assert.Equal((nuint)1, region_free_list.get_num_free_regions(
                gc_heap.free_regions_of((int)free_region_kind.large_free_region)));

            generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
            for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
            {
                generation.initialize(&generations[i]);
            }

            heap_segment lohInitial = default;
            heap_segment pohInitial = default;
            heap_segment sohInitial = default;
            gc_heap.make_generation(
                generations,
                (int)gc_generation_num.loh_generation,
                &lohInitial,
                (byte*)0x1000);
            gc_heap.make_generation(
                generations,
                (int)gc_generation_num.poh_generation,
                &pohInitial,
                (byte*)0x2000);
            gc_heap.make_generation(
                generations,
                (int)gc_generation_num.soh_gen0,
                &sohInitial,
                (byte*)0x3000);

            heap_segment* loh = gc_heap.get_new_region(
                generations,
                (gc_heap*)0x1234,
                (int)gc_generation_num.loh_generation,
                largeRegionSize);

            nuint pohCommitted = gc_heap.get_region_committed_size(pohFree);
            gc_heap.committed_by_oh[(int)gc_oh_num.poh] -= pohCommitted;
            gc_heap.committed_by_oh[gc_heap.recorded_committed_free_bucket] += pohCommitted;
            region_free_list.add_region(
                pohFree,
                gc_heap.free_regions_of((int)free_region_kind.basic_free_region));
            heap_segment* poh = gc_heap.get_new_region(
                generations,
                (gc_heap*)0x1234,
                (int)gc_generation_num.poh_generation,
                largeRegionSize);

            Assert.Equal((nuint)lohFree, (nuint)loh);
            Assert.Equal((nuint)pohFree, (nuint)poh);
            Assert.Equal(1, heap_segment.heap_segment_loh_p(loh));
            Assert.Equal(0, heap_segment.heap_segment_poh_p(loh));
            Assert.Equal(0, heap_segment.heap_segment_loh_p(poh));
            Assert.Equal(1, heap_segment.heap_segment_poh_p(poh));
            Assert.Equal((nuint)loh, (nuint)heap_segment.heap_segment_next(&lohInitial));
            Assert.Equal((nuint)poh, (nuint)heap_segment.heap_segment_next(&pohInitial));
            Assert.Equal((nuint)loh, (nuint)generation.generation_tail_region(
                gc_heap.generation_of(generations, (int)gc_generation_num.loh_generation)));
            Assert.Equal((nuint)poh, (nuint)generation.generation_tail_region(
                gc_heap.generation_of(generations, (int)gc_generation_num.poh_generation)));

            gc_heap.heap_hard_limit = gc_heap.current_total_committed + pageSize - 1;
            heap_segment* failed = gc_heap.get_new_region(
                generations,
                (gc_heap*)0x1234,
                (int)gc_generation_num.soh_gen0);

            Assert.True(failed is null);
            Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_next(&sohInitial));
            Assert.Equal((nuint)(&sohInitial), (nuint)generation.generation_tail_region(
                gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0)));
        }
        finally
        {
            GCToOSInterface.VirtualRelease(reservation, 32 * pageSize);
        }
    }

    [Fact]
    [Trait("Category", "GetFreeRegion")]
    public void GetFreeRegionSelectsLocalHugeBeforeGlobalHugeUnderGcLock()
    {
        using MemoryAccountingScope accounting = new();
        nuint pageSize = PageSize;
        nuint largeRegionSize = region_allocator.LARGE_REGION_FACTOR * pageSize;
        byte* reservation = GCToOSInterface.VirtualReserve(64 * pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation != null);

        using RegionAllocationScope regions = new(reservation, 64 * pageSize, pageSize);
        try
        {
            short* bricks = stackalloc short[64];
            gc_heap.brick_table = bricks;
#if BACKGROUND_GC
            gc_heap.lowest_address = reservation;
            gc_heap.highest_address = reservation + (nint)(64 * pageSize);
#endif

            heap_segment* localHuge = gc_heap.allocate_new_region(
                (gc_heap*)0x1234,
                (int)gc_generation_num.loh_generation,
                uoh_p: true,
                2 * largeRegionSize);
            heap_segment* globalHuge = gc_heap.allocate_new_region(
                (gc_heap*)0x1234,
                (int)gc_generation_num.poh_generation,
                uoh_p: true,
                3 * largeRegionSize);
            Assert.True(localHuge != null);
            Assert.True(globalHuge != null);

            AddRegionToFreeList(localHuge, (int)gc_oh_num.loh);
            AddRegionToFreeList(globalHuge, (int)gc_oh_num.poh);
            region_free_list.unlink_region(globalHuge);
            region_free_list.add_region_in_descending_order(
                (region_free_list*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref gc_heap.global_free_huge_regions),
                globalHuge);

            heap_segment* local = gc_heap.get_free_region(
                (gc_heap*)0x1234,
                (int)gc_generation_num.loh_generation,
                2 * largeRegionSize);
            Assert.Equal((nuint)localHuge, (nuint)local);
            Assert.Equal((nuint)1, region_free_list.get_num_free_regions(
                (region_free_list*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref gc_heap.global_free_huge_regions)));

            gc_heap.enter_gc_lock();
            heap_segment* global;
            try
            {
                global = gc_heap.get_free_region(
                    (gc_heap*)0x1234,
                    (int)gc_generation_num.poh_generation,
                    2 * largeRegionSize);
            }
            finally
            {
                gc_heap.leave_gc_lock();
            }

            Assert.Equal((nuint)globalHuge, (nuint)global);
            Assert.Equal(0, heap_segment.heap_segment_uoh_p(local));
            Assert.Equal(0, heap_segment.heap_segment_uoh_p(global));
            Assert.Equal((nuint)0, gc_heap.committed_by_oh[gc_heap.recorded_committed_free_bucket]);
            Assert.Equal(pageSize, gc_heap.committed_by_oh[(int)gc_oh_num.loh]);
            Assert.Equal(pageSize, gc_heap.committed_by_oh[(int)gc_oh_num.poh]);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(reservation, 64 * pageSize);
        }
    }

    [Fact]
    [Trait("Category", "GetFreeRegion")]
    public void GetFreeRegionFallsBackToAllocationAndPreservesOtherFreeListsOnFailure()
    {
        using MemoryAccountingScope accounting = new();
        nuint pageSize = PageSize;
        nuint largeRegionSize = region_allocator.LARGE_REGION_FACTOR * pageSize;
        byte* reservation = GCToOSInterface.VirtualReserve(16 * pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation != null);

        using RegionAllocationScope regions = new(reservation, 16 * pageSize, pageSize);
        try
        {
            short* bricks = stackalloc short[16];
            gc_heap.brick_table = bricks;
#if BACKGROUND_GC
            gc_heap.lowest_address = reservation;
            gc_heap.highest_address = reservation + (nint)(16 * pageSize);
#endif

            heap_segment* fallback = gc_heap.get_free_region((gc_heap*)0x1234, (int)gc_generation_num.soh_gen2);
            Assert.True(fallback != null);
            Assert.Equal(-1, bricks[0]);
            Assert.Equal(pageSize, gc_heap.committed_by_oh[(int)gc_oh_num.soh]);

            heap_segment* large = gc_heap.allocate_new_region(
                (gc_heap*)0x1234,
                (int)gc_generation_num.loh_generation,
                uoh_p: true,
                largeRegionSize);
            Assert.True(large != null);
            AddRegionToFreeList(large, (int)gc_oh_num.loh);

            nuint freeBeforeFailure = gc_heap.global_region_allocator.get_free();
            nuint committedBeforeFailure = gc_heap.current_total_committed;
            gc_heap.heap_hard_limit = committedBeforeFailure + pageSize - 1;

            heap_segment* failed = gc_heap.get_free_region((gc_heap*)0x1234, (int)gc_generation_num.soh_gen2);

            Assert.True(failed is null);
            Assert.Equal(freeBeforeFailure, gc_heap.global_region_allocator.get_free());
            Assert.Equal(committedBeforeFailure, gc_heap.current_total_committed);
            Assert.Equal((nuint)1, region_free_list.get_num_free_regions(
                gc_heap.free_regions_of((int)free_region_kind.large_free_region)));
            Assert.Equal(pageSize, gc_heap.committed_by_oh[gc_heap.recorded_committed_free_bucket]);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(reservation, 16 * pageSize);
        }
    }

    [Fact]
    public void DecommitRegionWithNeverDecommitCountsDirectlyAndClearsOnlyUsedBytes()
    {
        using MemoryAccountingScope scope = new();
        GCEventKeyword oldKeywords = GCEventStatus.GetEnabledKeywords(GCEventProvider.Default);
        GCEventLevel oldLevel = GCEventStatus.GetEnabledLevel(GCEventProvider.Default);
        nuint pageSize = PageSize;
        byte* reservation = GCToOSInterface.VirtualReserve(pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation != null);

        region_allocator oldAllocator = gc_heap.global_region_allocator;
        uint* map = null;
        try
        {
            map = InitializeGlobalRegionAllocator(reservation, pageSize, pageSize);
            byte* regionStart = AllocateMappedRegion(pageSize, out byte* regionEnd);
            Assert.True(GCToOSInterface.VirtualCommit(regionStart, pageSize));

            heap_segment region = default;
            byte* originalUsed = regionStart + (nint)(pageSize / 2);
            InitializeRegionSegment(&region, regionStart, pageSize, originalUsed);
            Fill(regionStart, pageSize, 0xCD);

            gc_heap.never_decommit_p = true;
            gc_heap.committed_by_oh[gc_heap.recorded_committed_free_bucket] = pageSize;
            gc_heap.current_total_committed = pageSize;
            GCToEEInterface.Reset();
            GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Information);
            GCToOSInterface.ResetRecording();

            nuint decommitted = gc_heap.decommit_region(&region, gc_heap.recorded_committed_free_bucket, -1);

            Assert.Equal(pageSize, decommitted);
            Assert.Equal(GCToEEInterface.FiredEvent.GCFreeSegment_V1, GCToEEInterface.LastFiredEvent);
            AssertNoVirtualDecommitWasRequested();
            Assert.Equal((nuint)0, gc_heap.committed_by_oh[gc_heap.recorded_committed_free_bucket]);
            Assert.Equal((nuint)0, gc_heap.current_total_committed);
            Assert.Equal((nuint)heap_segment.heap_segment_mem(&region), (nuint)heap_segment.heap_segment_used(&region));
            Assert.Equal((nuint)regionEnd, (nuint)heap_segment.heap_segment_committed(&region));
            AssertRangeIsZero(regionStart, (nuint)(originalUsed - regionStart));
            AssertRangeIs(originalUsed, (nuint)(regionEnd - originalUsed), 0xCD);
        }
        finally
        {
            RestoreGlobalRegionAllocator(oldAllocator, map);
            GCEventStatus.Set(GCEventProvider.Default, oldKeywords, oldLevel);
            gc_heap.never_decommit_p = false;
            GCToOSInterface.VirtualRelease(reservation, pageSize);
        }
    }

    [Fact]
    public void ResetHeapSegmentPagesRoundsAllocatedUpToTheNextPage()
    {
        nuint pageSize = PageSize;
        byte* reservation = GCToOSInterface.VirtualReserve(3 * pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation != null);

        try
        {
            Assert.True(GCToOSInterface.VirtualCommit(reservation, 3 * pageSize));

            heap_segment segment = default;
            heap_segment.heap_segment_allocated(&segment) = reservation + (nint)(pageSize / 2);
            heap_segment.heap_segment_committed(&segment) = reservation + (nint)(3 * pageSize);
            GCToOSInterface.ResetRecording();

            gc_heap.reset_heap_segment_pages(&segment);

            AssertVirtualResetWasRequested(reservation + (nint)pageSize, 2 * pageSize);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(reservation, 3 * pageSize);
        }
    }

    [Fact]
    public void ResetHeapSegmentPagesResetsTheCommittedTail()
    {
        nuint pageSize = PageSize;
        byte* reservation = GCToOSInterface.VirtualReserve(4 * pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation != null);

        try
        {
            Assert.True(GCToOSInterface.VirtualCommit(reservation, 4 * pageSize));

            heap_segment segment = default;
            heap_segment.heap_segment_allocated(&segment) = reservation + (nint)pageSize;
            heap_segment.heap_segment_committed(&segment) = reservation + (nint)(4 * pageSize);
            GCToOSInterface.ResetRecording();

            gc_heap.reset_heap_segment_pages(&segment);

            AssertVirtualResetWasRequested(reservation + (nint)pageSize, 3 * pageSize);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(reservation, 4 * pageSize);
        }
    }

    [Fact]
    public void ResetHeapSegmentPagesSkipsAnEmptyTail()
    {
        byte* committed = (byte*)0x1000;
        heap_segment segment = default;
        heap_segment.heap_segment_allocated(&segment) = committed;
        heap_segment.heap_segment_committed(&segment) = committed;
        GCToOSInterface.ResetRecording();

        gc_heap.reset_heap_segment_pages(&segment);

        AssertNoVirtualResetWasRequested();
    }

    [Fact]
    public void DecommitHeapSegmentPagesDoesNotDecommitBelowThreshold()
    {
        using MemoryAccountingScope scope = new();
        nuint pageSize = PageSize;
        byte* allocated = (byte*)0x1000;
        nuint committedSize = 100 * pageSize;
        heap_segment segment = default;
        heap_segment.heap_segment_allocated(&segment) = allocated;
        heap_segment.heap_segment_used(&segment) = allocated + (nint)(committedSize - pageSize);
        heap_segment.heap_segment_committed(&segment) = allocated + (nint)(committedSize - pageSize);

        GCToOSInterface.ResetRecording();

        gc_heap.decommit_heap_segment_pages(&segment, 0, 0);

        AssertNoVirtualDecommitWasRequested();
        Assert.Equal((nuint)(allocated + (nint)(committedSize - pageSize)), (nuint)heap_segment.heap_segment_committed(&segment));
        Assert.Equal((nuint)(allocated + (nint)(committedSize - pageSize)), (nuint)heap_segment.heap_segment_used(&segment));
    }

    [Fact]
    public void DecommitHeapSegmentPagesUpdatesObjectHeapAccountingAndClampsUsed()
    {
        using MemoryAccountingScope scope = new();
        nuint pageSize = PageSize;
        nuint committedSize = 100 * pageSize;
        byte* reservation = GCToOSInterface.VirtualReserve(committedSize + pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation != null);

        try
        {
            Assert.True(GCToOSInterface.VirtualCommit(reservation, committedSize + pageSize));

            byte* pageStart = reservation + (nint)pageSize;
            heap_segment segment = default;
            heap_segment.heap_segment_allocated(&segment) = reservation + sizeof(aligned_plug_and_gap);
            heap_segment.heap_segment_used(&segment) = pageStart + (nint)committedSize;
            heap_segment.heap_segment_committed(&segment) = pageStart + (nint)committedSize;
            segment.flags = heap_segment.heap_segment_flags_loh;
            gc_heap.committed_by_oh[(int)gc_oh_num.loh] = committedSize;
            gc_heap.current_total_committed = committedSize;
            GCToOSInterface.ResetRecording();

            gc_heap.decommit_heap_segment_pages(&segment, 0, 0);

            nuint retainedSize = 32 * pageSize;
            Assert.Equal(1, VirtualDecommitRequestCount());
            Assert.Equal((nuint)(pageStart + (nint)retainedSize), (nuint)heap_segment.heap_segment_committed(&segment));
            Assert.Equal((nuint)(pageStart + (nint)retainedSize), (nuint)heap_segment.heap_segment_used(&segment));
            Assert.Equal(retainedSize, gc_heap.committed_by_oh[(int)gc_oh_num.loh]);
            Assert.Equal((nuint)0, gc_heap.committed_by_oh[(int)gc_oh_num.soh]);
            Assert.Equal(retainedSize, gc_heap.current_total_committed);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(reservation, committedSize + pageSize);
        }
    }

    [Fact]
    public void DecommitHeapSegmentPagesSkipsNeverDecommit()
    {
        using MemoryAccountingScope scope = new();
        nuint pageSize = PageSize;
        byte* allocated = (byte*)0x1000;
        nuint committedSize = 100 * pageSize;
        heap_segment segment = default;
        heap_segment.heap_segment_allocated(&segment) = allocated;
        heap_segment.heap_segment_used(&segment) = allocated + (nint)committedSize;
        heap_segment.heap_segment_committed(&segment) = allocated + (nint)committedSize;
        gc_heap.committed_by_oh[(int)gc_oh_num.soh] = committedSize;
        gc_heap.current_total_committed = committedSize;
        gc_heap.never_decommit_p = true;
        GCToOSInterface.ResetRecording();

        gc_heap.decommit_heap_segment_pages(&segment, 0, 0);

        AssertNoVirtualDecommitWasRequested();
        Assert.Equal((nuint)(allocated + (nint)committedSize), (nuint)heap_segment.heap_segment_committed(&segment));
        Assert.Equal((nuint)(allocated + (nint)committedSize), (nuint)heap_segment.heap_segment_used(&segment));
        Assert.Equal(committedSize, gc_heap.committed_by_oh[(int)gc_oh_num.soh]);
        Assert.Equal(committedSize, gc_heap.current_total_committed);
    }

    [Fact]
    public void DecommitRegionFailedDecommitClearsCommittedBytesAndKeepsAccounting()
    {
        using MemoryAccountingScope scope = new();
        nuint pageSize = PageSize;
        byte* reservation = GCToOSInterface.VirtualReserve(pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation != null);

        region_allocator oldAllocator = gc_heap.global_region_allocator;
        uint* map = null;
        try
        {
            map = InitializeGlobalRegionAllocator(reservation, pageSize, pageSize);
            byte* regionStart = AllocateMappedRegion(pageSize, out byte* regionEnd);
            Assert.True(GCToOSInterface.VirtualCommit(regionStart, pageSize));

            heap_segment region = default;
            InitializeRegionSegment(&region, regionStart, pageSize, regionStart + sizeof(aligned_plug_and_gap) + 64);
            Fill(regionStart, pageSize, 0xCD);

            gc_heap.committed_by_oh[gc_heap.recorded_committed_free_bucket] = pageSize;
            gc_heap.current_total_committed = pageSize;
            GCToOSInterface.ResetRecording();
            GCToOSInterface.ForceVirtualDecommitFailureCount = 1;

#if DEBUG
            try
            {
                gc_heap.decommit_region(&region, gc_heap.recorded_committed_free_bucket, -1);
                Assert.Fail("The native debug assert for a failed region decommit should fire.");
            }
            catch (Exception ex) when (ex.GetType().Name == "DebugAssertException")
            {
            }
#else
            nuint decommitted = gc_heap.decommit_region(&region, gc_heap.recorded_committed_free_bucket, -1);

            Assert.Equal(pageSize, decommitted);
#endif
            Assert.Equal(1, VirtualDecommitRequestCount());
            Assert.Equal(pageSize, gc_heap.committed_by_oh[gc_heap.recorded_committed_free_bucket]);
            Assert.Equal(pageSize, gc_heap.current_total_committed);
            Assert.Equal((nuint)heap_segment.heap_segment_mem(&region), (nuint)heap_segment.heap_segment_used(&region));
            Assert.Equal((nuint)regionEnd, (nuint)heap_segment.heap_segment_committed(&region));
            AssertRangeIsZero(regionStart, pageSize);
        }
        finally
        {
            GCToOSInterface.ForceVirtualDecommitFailureCount = 0;
            RestoreGlobalRegionAllocator(oldAllocator, map);
            GCToOSInterface.VirtualRelease(reservation, pageSize);
        }
    }

#if BACKGROUND_GC
    [Fact]
    public void DecommitRegionDecommitsCommittedMarkArrayAndClearsFlag()
    {
        using MemoryAccountingScope scope = new();
        nuint regionSize = 1024 * 1024;
        nuint pageSize = PageSize;
        const nuint AllocatorAlignment = 64 * 1024;
        byte* reservation = GCToOSInterface.VirtualReserve(regionSize, AllocatorAlignment, (uint)VirtualReserveFlags.None);
        Assert.True(reservation != null);

        region_allocator oldAllocator = gc_heap.global_region_allocator;
        uint* oldMarkArray = gc_heap.mark_array;
        byte* oldLowestAddress = gc_heap.lowest_address;
        byte* oldHighestAddress = gc_heap.highest_address;
        uint* map = null;
        byte* markReservation = null;
        nuint markReservationSize = 0;
        try
        {
            map = InitializeGlobalRegionAllocator(reservation, regionSize, AllocatorAlignment);
            byte* regionStart = AllocateMappedRegion(regionSize, out _);
            Assert.True(GCToOSInterface.VirtualCommit(regionStart, regionSize));

            heap_segment region = default;
            InitializeRegionSegment(&region, regionStart, regionSize, regionStart + sizeof(aligned_plug_and_gap));
            region.flags = heap_segment.heap_segment_flags_ma_committed;

            nuint begWord = card_table_info.mark_word_of(heap_segment.heap_segment_mem(&region));
            nuint endWord = card_table_info.mark_word_of(card_table_info.align_on_mark_word(heap_segment.heap_segment_reserved(&region)));
            nuint markBytes = gc_heap.align_lower_page((endWord - begWord) * (nuint)sizeof(uint));
            Assert.NotEqual((nuint)0, markBytes);
            markReservationSize = gc_heap.align_on_page(markBytes);
            markReservation = GCToOSInterface.VirtualReserve(markReservationSize, pageSize, (uint)VirtualReserveFlags.None);
            Assert.True(markReservation != null);
            Assert.True(GCToOSInterface.VirtualCommit(markReservation, markReservationSize));

            gc_heap.mark_array = (uint*)(markReservation - (nint)(begWord * (nuint)sizeof(uint)));
            gc_heap.lowest_address = heap_segment.heap_segment_mem(&region);
            gc_heap.highest_address = heap_segment.heap_segment_reserved(&region);
            gc_heap.committed_by_oh[gc_heap.recorded_committed_free_bucket] = regionSize;
            gc_heap.committed_by_oh[gc_heap.recorded_committed_mark_array_bucket] += markBytes;
            gc_heap.current_total_committed = regionSize + markBytes;
            gc_heap.current_total_committed_bookkeeping = markBytes;
            GCToOSInterface.ResetRecording();

            nuint decommitted = gc_heap.decommit_region(&region, gc_heap.recorded_committed_free_bucket, -1);

            Assert.Equal(regionSize, decommitted);
            Assert.Equal(2, VirtualDecommitRequestCount());
            Assert.Equal((nuint)0, region.flags & heap_segment.heap_segment_flags_ma_committed);
            Assert.Equal((nuint)0, gc_heap.committed_by_oh[gc_heap.recorded_committed_free_bucket]);
            Assert.Equal((nuint)0, gc_heap.committed_by_oh[gc_heap.recorded_committed_mark_array_bucket]);
            Assert.Equal((nuint)0, gc_heap.current_total_committed);
            Assert.Equal((nuint)0, gc_heap.current_total_committed_bookkeeping);
        }
        finally
        {
            gc_heap.mark_array = oldMarkArray;
            gc_heap.lowest_address = oldLowestAddress;
            gc_heap.highest_address = oldHighestAddress;
            RestoreGlobalRegionAllocator(oldAllocator, map);
            if (markReservation is not null)
            {
                GCToOSInterface.VirtualRelease(markReservation, markReservationSize);
            }

            GCToOSInterface.VirtualRelease(reservation, regionSize);
        }
    }
#endif

    [Fact]
    public void DecommitStepHonorsPauseModeQuotaAndGlobalFreeListOrder()
    {
        using MemoryAccountingScope scope = new();
        nuint pageSize = PageSize;
        byte* reservation = GCToOSInterface.VirtualReserve(2 * pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation != null);

        region_allocator oldAllocator = gc_heap.global_region_allocator;
        gc_heap.region_free_list_array oldRegionsToDecommit = gc_heap.global_regions_to_decommit;
        gc_mechanisms oldSettings = gc_heap.settings;
        uint* map = null;
        try
        {
            map = InitializeGlobalRegionAllocator(reservation, 2 * pageSize, pageSize);
            byte* firstStart = AllocateMappedRegion(pageSize, out _);
            byte* secondStart = AllocateMappedRegion(pageSize, out _);
            Assert.True(GCToOSInterface.VirtualCommit(firstStart, pageSize));
            Assert.True(GCToOSInterface.VirtualCommit(secondStart, pageSize));

            heap_segment first = default;
            heap_segment second = default;
            InitializeRegionSegment(&first, firstStart, pageSize, firstStart + sizeof(aligned_plug_and_gap));
            InitializeRegionSegment(&second, secondStart, pageSize, secondStart + sizeof(aligned_plug_and_gap));

            gc_heap.global_regions_to_decommit = default;
            region_free_list* regionsToDecommit = gc_heap.global_regions_to_decommit_of((int)free_region_kind.basic_free_region);
            region_free_list.add_region(&first, regionsToDecommit);
            region_free_list.add_region(&second, regionsToDecommit);
            gc_heap.committed_by_oh[gc_heap.recorded_committed_free_bucket] = 2 * pageSize;
            gc_heap.current_total_committed = 2 * pageSize;

            gc_heap.settings.pause_mode = gc_pause_mode.pause_no_gc;
            Assert.False(gc_heap.decommit_step(0));
            Assert.Equal((nuint)2, region_free_list.get_num_free_regions(regionsToDecommit));
            Assert.Equal(2 * pageSize, gc_heap.current_total_committed);

            gc_heap.settings.pause_mode = gc_pause_mode.pause_batch;
            GCToOSInterface.ResetRecording();
            Assert.True(gc_heap.decommit_step(0));
            Assert.Equal(1, VirtualDecommitRequestCount());
            Assert.Equal((nuint)1, region_free_list.get_num_free_regions(regionsToDecommit));
            Assert.Equal(pageSize, gc_heap.current_total_committed);

            Assert.True(gc_heap.decommit_step(1));
            Assert.Equal((nuint)0, region_free_list.get_num_free_regions(regionsToDecommit));
            Assert.Equal((nuint)0, gc_heap.current_total_committed);
        }
        finally
        {
            RestoreGlobalRegionAllocator(oldAllocator, map);
            gc_heap.global_regions_to_decommit = oldRegionsToDecommit;
            gc_heap.settings = oldSettings;
            GCToOSInterface.VirtualRelease(reservation, 2 * pageSize);
        }
    }
#endif

    private static void Fill(byte* address, nuint size, byte value)
    {
        for (nuint i = 0; i < size; i++)
        {
            address[i] = value;
        }
    }

    private static void AssertRangeIs(byte* address, nuint size, byte value)
    {
        for (nuint i = 0; i < size; i++)
        {
            if (address[i] != value)
            {
                Assert.Fail($"byte {i} of the range is {address[i]}, expected {value}");
            }
        }
    }

    private static void AssertRangeIsZero(byte* address, nuint size) => AssertRangeIs(address, size, 0);

    private static void ResetMemoryAccounting()
    {
        gc_heap.reserved_memory = 0;
        gc_heap.current_total_committed = 0;
        gc_heap.current_total_committed_bookkeeping = 0;
        gc_heap.heap_hard_limit = 0;
        gc_heap.never_decommit_p = false;

        for (int i = 0; i < gc_heap.recorded_committed_bucket_counts; i++)
        {
            gc_heap.committed_by_oh[i] = 0;
        }

        for (int i = 0; i < gc_heap.total_oh_count; i++)
        {
            gc_heap.heap_hard_limit_oh[i] = 0;
        }
    }

    private static void AssertNoVirtualCommitWasRequested()
    {
#if TARGET_WINDOWS
        Assert.Equal(0, GCToOSInterface.VirtualAllocCount);
#else
        Assert.Equal(0, GCToOSInterface.MprotectCount);
#endif
    }

    private static void AssertVirtualResetWasRequested(byte* address, nuint size)
    {
#if TARGET_WINDOWS
        Assert.Equal(1, GCToOSInterface.VirtualAllocCount);
        Assert.True(GCToOSInterface.LastVirtualAlloc.lpAddress == address);
        Assert.Equal(size, GCToOSInterface.LastVirtualAlloc.dwSize);
        Assert.Equal(MEM_RESET, GCToOSInterface.LastVirtualAlloc.flAllocationType);
#else
        Assert.Equal(HasCoredumpAdvice ? 2 : 1, GCToOSInterface.MadviseCount);
        Assert.True(GCToOSInterface.LastMadvise.addr == address);
        Assert.Equal(size, GCToOSInterface.LastMadvise.length);
        Assert.Equal(MADV_FREE, GCToOSInterface.LastMadvise.arg);
#endif
    }

    private static void AssertNoVirtualResetWasRequested()
    {
#if TARGET_WINDOWS
        Assert.Equal(0, GCToOSInterface.VirtualAllocCount);
#else
        Assert.Equal(0, GCToOSInterface.MadviseCount);
#endif
    }

#if USE_REGIONS
    private static void AssertNoVirtualDecommitWasRequested()
    {
        Assert.Equal(0, VirtualDecommitRequestCount());
    }

    private static int VirtualDecommitRequestCount()
    {
#if TARGET_WINDOWS
        return GCToOSInterface.VirtualFreeCount;
#else
        return GCToOSInterface.MmapCount;
#endif
    }

    private static uint* InitializeGlobalRegionAllocator(byte* reservation, nuint reservationSize, nuint alignment)
    {
        gc_heap.global_region_allocator = default;
        gc_heap.global_region_allocator.initialize();
        byte* lowest = null;
        byte* highest = null;
        Assert.True(gc_heap.global_region_allocator.init(reservation, reservation + (nint)reservationSize, alignment, &lowest, &highest));
        Assert.Equal((nuint)reservation, (nuint)lowest);
        Assert.Equal((nuint)(reservation + (nint)reservationSize), (nuint)highest);
        return gc_heap.global_region_allocator.region_map_index_of(reservation);
    }

    private static void RestoreGlobalRegionAllocator(region_allocator oldAllocator, uint* map)
    {
        if (map is not null)
        {
            SyncImports.ManagedGC_Free(map);
        }
        gc_heap.global_region_allocator = oldAllocator;
    }

    private static void AddRegionToFreeList(heap_segment* region, int sourceBucket)
    {
        nuint committed = gc_heap.get_region_committed_size(region);
        Assert.True(gc_heap.committed_by_oh[sourceBucket] >= committed);
        gc_heap.committed_by_oh[sourceBucket] -= committed;
        gc_heap.committed_by_oh[gc_heap.recorded_committed_free_bucket] += committed;
        region_free_list.add_region(region, gc_heap.free_regions_of((int)free_region_kind.basic_free_region));
    }

    private static void InitializeGenerations(generation* generations)
    {
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generation.initialize(&generations[i]);
        }
    }

    private static void AssertInitialSohGeneration(generation* generations, int genNum, byte* expectedRegionStart)
    {
        generation* gen = gc_heap.generation_of(generations, genNum);
        heap_segment* expectedSegment = gc_heap.get_region_info(expectedRegionStart);

        Assert.Equal(genNum, gen->gen_num);
        Assert.Equal((nuint)expectedSegment, (nuint)generation.generation_start_segment(gen));
        Assert.Equal((nuint)expectedSegment, (nuint)generation.generation_allocation_segment(gen));
        Assert.Equal((nuint)expectedSegment, (nuint)generation.generation_tail_region(gen));
        Assert.Equal((nuint)0, (nuint)generation.generation_tail_ro_region(gen));
        Assert.Equal((nuint)(expectedRegionStart + sizeof(aligned_plug_and_gap)), (nuint)heap_segment.heap_segment_mem(expectedSegment));
    }

    private static void AssertInitialUohGeneration(
        generation* generations,
        int genNum,
        byte* expectedRegionStart,
        nuint expectedFlag)
    {
        AssertInitialSohGeneration(generations, genNum, expectedRegionStart);
        heap_segment* segment = generation.generation_start_segment(gc_heap.generation_of(generations, genNum));
        Assert.Equal(expectedFlag, segment->flags & (heap_segment.heap_segment_flags_loh | heap_segment.heap_segment_flags_poh));
    }

    private sealed class RegionAllocationScope : IDisposable
    {
        private readonly region_allocator _oldAllocator;
        private readonly nuint _oldMinSegmentSizeShr;
        private readonly region_info* _oldMapRegionToGeneration;
        private readonly region_info* _oldMapRegionToGenerationSkewed;
        private readonly seg_mapping* _oldSegMappingTable;
        private readonly byte* _oldLowestAddress;
        private readonly byte* _oldHighestAddress;
        private readonly gc_heap.region_free_list_array _oldFreeRegions;
        private readonly region_free_list _oldGlobalFreeHugeRegions;
        private readonly GCSpinLock _oldGcLock;
        private readonly uint* _oldCardTable;
        private readonly short* _oldBrickTable;
        private readonly byte* _oldBookkeepingStart;
        private readonly byte* _oldBookkeepingCoveredCommitted;
        private readonly gc_heap.bookkeeping_layout_array _oldCardTableElementLayout;
        private readonly gc_heap.bookkeeping_size_array _oldBookkeepingSizes;
        private readonly byte* _oldHeapLowestAddress;
        private readonly byte* _oldHeapHighestAddress;
        private readonly byte** _oldInitialRegions;
        private readonly GCSpinLock _oldWriteBarrierSpinLock;
        private readonly byte* _oldEphemeralLow;
        private readonly byte* _oldEphemeralHigh;
#if BACKGROUND_GC
        private readonly uint* _oldMarkArray;
#endif
        private readonly seg_mapping* _mappings;
        private readonly region_info* _regions;
        private readonly uint* _regionMap;
        private byte* _bookkeepingStart;
        private nuint _bookkeepingSize;

        public RegionAllocationScope(byte* reservation, nuint reservationSize, nuint alignment)
        {
            _oldAllocator = gc_heap.global_region_allocator;
            _oldMinSegmentSizeShr = gc_heap.min_segment_size_shr;
            _oldMapRegionToGeneration = gc_heap.map_region_to_generation;
            _oldMapRegionToGenerationSkewed = gc_heap.map_region_to_generation_skewed;
            _oldSegMappingTable = GCCommon.seg_mapping_table;
            _oldLowestAddress = GCCommon.g_gc_lowest_address;
            _oldHighestAddress = GCCommon.g_gc_highest_address;
            _oldFreeRegions = gc_heap.free_regions;
            _oldGlobalFreeHugeRegions = gc_heap.global_free_huge_regions;
            _oldGcLock = gc_heap.gc_lock;
            _oldCardTable = gc_heap.card_table;
            _oldBrickTable = gc_heap.brick_table;
            _oldBookkeepingStart = gc_heap.bookkeeping_start;
            _oldBookkeepingCoveredCommitted = gc_heap.bookkeeping_covered_committed;
            _oldCardTableElementLayout = gc_heap.card_table_element_layout;
            _oldBookkeepingSizes = gc_heap.bookkeeping_sizes;
            _oldHeapLowestAddress = gc_heap.lowest_address;
            _oldHeapHighestAddress = gc_heap.highest_address;
            _oldInitialRegions = gc_heap.initial_regions;
            _oldWriteBarrierSpinLock = GCWriteBarrier.write_barrier_spin_lock;
            _oldEphemeralLow = gc_heap.ephemeral_low;
            _oldEphemeralHigh = gc_heap.ephemeral_high;
#if BACKGROUND_GC
            _oldMarkArray = gc_heap.mark_array;
#endif

            gc_heap.initialize_min_segment_size_shr(alignment);
            nuint regionCount = reservationSize / alignment;
            _mappings = (seg_mapping*)SyncImports.ManagedGC_AllocZeroed(regionCount * (nuint)sizeof(seg_mapping));
            _regions = (region_info*)SyncImports.ManagedGC_AllocZeroed(regionCount * (nuint)sizeof(region_info));
            Assert.True(_mappings != null);
            Assert.True(_regions != null);

            nuint firstIndex = (nuint)reservation >> (int)gc_heap.min_segment_size_shr;
            GCCommon.g_gc_lowest_address = reservation;
            GCCommon.g_gc_highest_address = reservation + (nint)reservationSize;
            GCCommon.seg_mapping_table = _mappings - (nint)firstIndex;
            gc_heap.map_region_to_generation = _regions;
            gc_heap.map_region_to_generation_skewed = _regions - (nint)firstIndex;
            gc_heap.free_regions = default;
            gc_heap.global_free_huge_regions = default;
            gc_heap.initialize_gc_lock();
            gc_heap.card_table = null;
            gc_heap.brick_table = null;

            gc_heap.global_region_allocator = default;
            gc_heap.global_region_allocator.initialize();
            byte* lowest = null;
            byte* highest = null;
            Assert.True(gc_heap.global_region_allocator.init(
                reservation,
                reservation + (nint)reservationSize,
                alignment,
                &lowest,
                &highest));
            Assert.Equal((nuint)reservation, (nuint)lowest);
            Assert.Equal((nuint)(reservation + (nint)reservationSize), (nuint)highest);
            _regionMap = gc_heap.global_region_allocator.region_map_index_of(reservation);
            Assert.True(InitializeBookkeeping());
            ResetMemoryAccounting();
        }

        public bool InitializeBookkeeping()
        {
            if (_bookkeepingStart != null)
            {
                return true;
            }

            if (!gc_heap.initialize_region_bookkeeping())
            {
                return false;
            }

            _bookkeepingStart = gc_heap.bookkeeping_start;
            _bookkeepingSize = gc_heap.card_table_element_layout[(int)bookkeeping_element.total_bookkeeping_elements];
            return true;
        }

        public void Dispose()
        {
            if (_bookkeepingStart != null)
            {
                GCToOSInterface.VirtualRelease(_bookkeepingStart, _bookkeepingSize);
            }

            SyncImports.ManagedGC_Free(_regionMap);
            SyncImports.ManagedGC_Free(_regions);
            SyncImports.ManagedGC_Free(_mappings);
            gc_heap.global_region_allocator = _oldAllocator;
            gc_heap.min_segment_size_shr = _oldMinSegmentSizeShr;
            gc_heap.map_region_to_generation = _oldMapRegionToGeneration;
            gc_heap.map_region_to_generation_skewed = _oldMapRegionToGenerationSkewed;
            GCCommon.seg_mapping_table = _oldSegMappingTable;
            GCCommon.g_gc_lowest_address = _oldLowestAddress;
            GCCommon.g_gc_highest_address = _oldHighestAddress;
            gc_heap.free_regions = _oldFreeRegions;
            gc_heap.global_free_huge_regions = _oldGlobalFreeHugeRegions;
            gc_heap.gc_lock = _oldGcLock;
            gc_heap.card_table = _oldCardTable;
            gc_heap.brick_table = _oldBrickTable;
            gc_heap.bookkeeping_start = _oldBookkeepingStart;
            gc_heap.bookkeeping_covered_committed = _oldBookkeepingCoveredCommitted;
            gc_heap.card_table_element_layout = _oldCardTableElementLayout;
            gc_heap.bookkeeping_sizes = _oldBookkeepingSizes;
            gc_heap.lowest_address = _oldHeapLowestAddress;
            gc_heap.highest_address = _oldHeapHighestAddress;
            gc_heap.initial_regions = _oldInitialRegions;
            GCWriteBarrier.write_barrier_spin_lock = _oldWriteBarrierSpinLock;
            gc_heap.ephemeral_low = _oldEphemeralLow;
            gc_heap.ephemeral_high = _oldEphemeralHigh;
#if BACKGROUND_GC
            gc_heap.mark_array = _oldMarkArray;
#endif
        }
    }

    private static byte* AllocateMappedRegion(nuint size, out byte* end)
    {
        byte* start = null;
        byte* localEnd = null;
        Assert.True(gc_heap.global_region_allocator.allocate_region(
            (int)gc_generation_num.soh_gen0,
            size,
            &start,
            &localEnd,
            allocate_direction.allocate_forward,
            null));
        end = localEnd;
        return start;
    }

    private static void InitializeRegionSegment(heap_segment* region, byte* regionStart, nuint regionSize, byte* used)
    {
        *region = default;
        byte* mem = regionStart + sizeof(aligned_plug_and_gap);
        heap_segment.heap_segment_mem(region) = mem;
        heap_segment.heap_segment_allocated(region) = mem;
        heap_segment.heap_segment_used(region) = used;
        heap_segment.heap_segment_committed(region) = regionStart + (nint)regionSize;
        heap_segment.heap_segment_reserved(region) = regionStart + (nint)regionSize;
    }
#endif

    private sealed class MemoryAccountingScope : IDisposable
    {
        private readonly bool _initialized;

        public MemoryAccountingScope()
        {
            gc_heap.check_commit_cs = default;
            ResetMemoryAccounting();
            _initialized = gc_heap.check_commit_cs.Initialize();
            Assert.True(_initialized);
        }

        public void Dispose()
        {
            if (_initialized)
            {
                gc_heap.check_commit_cs.Destroy();
            }

            gc_heap.check_commit_cs = default;
            ResetMemoryAccounting();
        }
    }

#if !TARGET_WINDOWS
    //
    // The Unix flag translation. The expected values are the <sys/mman.h> ones of the platform
    // this assembly was compiled for.
    //

    private const int PROT_NONE = 0x0;
    private const int PROT_READ_WRITE = 0x1 | 0x2;

#if TARGET_APPLE
    private const int MAP_ANON = 0x1000;
    private const int MAP_PRIVATE = 0x0002;
    private const int MAP_FIXED = 0x0010;
    private const int LargePagesFlag = 0x10000; // VM_FLAGS_SUPERPAGE_SIZE_ANY
    private const int MADV_FREE = 5;
    private static bool HasCoredumpAdvice => false; // MADV_DONTDUMP / MADV_DODUMP are Linux-only
#elif TARGET_FREEBSD
    private const int MAP_ANON = 0x1000;
    private const int MAP_PRIVATE = 0x0002;
    private const int MAP_FIXED = 0x0010;
    private const int LargePagesFlag = 0;
    private const int MADV_FREE = 5;
    private static bool HasCoredumpAdvice => false;
#elif TARGET_OPENBSD
    private const int MAP_ANON = 0x1000;
    private const int MAP_PRIVATE = 0x0002;
    private const int MAP_FIXED = 0x0010;
    private const int LargePagesFlag = 0;
    private const int MADV_FREE = 6;
    private static bool HasCoredumpAdvice => false;
#else
    private const int MAP_ANON = 0x20;
    private const int MAP_PRIVATE = 0x02;
    private const int MAP_FIXED = 0x10;
    private const int LargePagesFlag = 0x40000; // MAP_HUGETLB
    private const int MADV_DONTDUMP = 16;
    private const int MADV_DODUMP = 17;
    private const int MADV_FREE = 8;
    private static bool HasCoredumpAdvice => true;
#endif

    [Fact]
    public void ReserveMapsAnonymousPrivateMemoryWithNoAccess()
    {
        nuint pageSize = PageSize;
        nuint size = 2 * pageSize;

        GCToOSInterface.ResetRecording();
        byte* region = GCToOSInterface.VirtualReserve(size, 0, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);

        try
        {
            Assert.Equal(1, GCToOSInterface.MmapCount);
            Assert.True(GCToOSInterface.LastMmap.addr == null);
            Assert.Equal(PROT_NONE, GCToOSInterface.LastMmap.prot);
            Assert.Equal(MAP_ANON | MAP_PRIVATE, GCToOSInterface.LastMmap.flags);
            Assert.Equal(-1, GCToOSInterface.LastMmap.fd);
            Assert.Equal((nint)0, GCToOSInterface.LastMmap.offset);

            // An alignment below the page size is raised to it, which makes the over-allocation
            // zero and leaves nothing to trim.
            Assert.Equal(size, GCToOSInterface.LastMmap.length);
            Assert.Equal(0, GCToOSInterface.MunmapCount);

            if (HasCoredumpAdvice)
            {
                // A reservation is not committed, so it is kept out of coredumps.
                Assert.Equal(1, GCToOSInterface.MadviseCount);
                Assert.True(GCToOSInterface.LastMadvise.addr == region);
                Assert.Equal(size, GCToOSInterface.LastMadvise.length);
                Assert.Equal(16, GCToOSInterface.LastMadvise.arg); // MADV_DONTDUMP
            }
            else
            {
                Assert.Equal(0, GCToOSInterface.MadviseCount);
            }
        }
        finally
        {
            GCToOSInterface.VirtualRelease(region, size);
        }
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(16u)]
    public void ReserveOverAllocatesForAlignmentAndTrimsThePadding(uint alignmentInPages)
    {
        nuint pageSize = PageSize;
        nuint alignment = alignmentInPages * pageSize;
        nuint size = 3 * pageSize;

        GCToOSInterface.ResetRecording();
        byte* region = GCToOSInterface.VirtualReserve(size, alignment, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);

        try
        {
            nuint alignedSize = size + (alignment - pageSize);
            byte* rawMapping = (byte*)GCToOSInterface.LastMmap.result;

            Assert.Equal(alignedSize, GCToOSInterface.LastMmap.length);
            Assert.Equal((nuint)0, (nuint)region & (alignment - 1));
            Assert.True(region >= rawMapping);
            Assert.True(region + size <= rawMapping + alignedSize);

            // Exactly the over-allocated bytes are given back, one call per non-empty side, and
            // each call covers exactly that side. The ranges are checked against what was
            // unmapped rather than by probing the address space for them, which would race with
            // the other threads of the test process.
            nuint startPadding = (nuint)(region - rawMapping);
            nuint endPadding = alignedSize - (startPadding + size);
            int expectedCalls = (startPadding != 0 ? 1 : 0) + (endPadding != 0 ? 1 : 0);

            Assert.Equal(alignedSize - size, GCToOSInterface.MunmapTotalLength);
            Assert.Equal(expectedCalls, GCToOSInterface.MunmapCount);

            int call = 0;
            if (startPadding != 0)
            {
                Assert.True(GCToOSInterface.MunmapCalls[call].addr == rawMapping);
                Assert.Equal(startPadding, GCToOSInterface.MunmapCalls[call].length);
                Assert.Equal(0, GCToOSInterface.MunmapCalls[call].result);
                call++;
            }

            if (endPadding != 0)
            {
                Assert.True(GCToOSInterface.MunmapCalls[call].addr == region + size);
                Assert.Equal(endPadding, GCToOSInterface.MunmapCalls[call].length);
                Assert.Equal(0, GCToOSInterface.MunmapCalls[call].result);
            }

            // The kept range is intact.
            Assert.True(GCToOSInterface.VirtualCommit(region, size));
        }
        finally
        {
            GCToOSInterface.VirtualRelease(region, size);
        }
    }

    [Fact]
    public void CommitMakesTheRangeReadWriteAndDumpable()
    {
        nuint pageSize = PageSize;
        byte* region = GCToOSInterface.VirtualReserve(pageSize, 0, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);

        try
        {
            GCToOSInterface.ResetRecording();
            Assert.True(GCToOSInterface.VirtualCommit(region, pageSize));

            Assert.Equal(1, GCToOSInterface.MprotectCount);
            Assert.True(GCToOSInterface.LastMprotect.addr == region);
            Assert.Equal(pageSize, GCToOSInterface.LastMprotect.length);
            Assert.Equal(PROT_READ_WRITE, GCToOSInterface.LastMprotect.arg);

            if (HasCoredumpAdvice)
            {
                // Already reserved memory was advised out of the coredump; committing it puts
                // it back in.
                Assert.Equal(1, GCToOSInterface.MadviseCount);
                Assert.Equal(17, GCToOSInterface.LastMadvise.arg); // MADV_DODUMP
            }

            // No node was asked for, so the NUMA binding is not attempted.
            Assert.Equal(0, GCToOSInterface.BindMemoryPolicyCount);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(region, pageSize);
        }
    }

#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD && !TARGET_ANDROID
    [Fact]
    public void CommitBindsTheRangeWhenANodeIsRequested()
    {
        nuint pageSize = PageSize;
        byte* region = GCToOSInterface.VirtualReserve(pageSize, 0, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);

        try
        {
            GCToOSInterface.NumaAvailableValue = 1;
            GCToOSInterface.HighestNumaNodeValue = 3;
            GCToOSInterface.ResetRecording();
            Assert.True(GCToOSInterface.VirtualCommit(region, pageSize, 1));

            Assert.Equal(1, GCToOSInterface.BindMemoryPolicyCount);
            Assert.True(GCToOSInterface.LastBindMemoryPolicy.addr == region);
            Assert.Equal(pageSize, GCToOSInterface.LastBindMemoryPolicy.length);
            Assert.Equal(1, GCToOSInterface.LastBindMemoryPolicy.arg);

            // A failed commit must not try to place the range at all.
            GCToOSInterface.ResetRecording();
            Assert.False(GCToOSInterface.VirtualCommit(NeverMappedAddress, pageSize, 1));
            Assert.Equal(0, GCToOSInterface.BindMemoryPolicyCount);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(region, pageSize);
        }
    }
#endif

    [Fact]
    public void DecommitReplacesTheRangeWithAFreshInaccessibleMapping()
    {
        nuint pageSize = PageSize;
        byte* region = GCToOSInterface.VirtualReserve(pageSize, 0, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);

        try
        {
            Assert.True(GCToOSInterface.VirtualCommit(region, pageSize));

            GCToOSInterface.ResetRecording();
            Assert.True(GCToOSInterface.VirtualDecommit(region, pageSize));

            // mmap, not mprotect: the kernel is told the pages are no longer needed, and the
            // GC depends on re-committed pages reading as zero.
            Assert.Equal(1, GCToOSInterface.MmapCount);
            Assert.Equal(0, GCToOSInterface.MprotectCount);
            Assert.True(GCToOSInterface.LastMmap.addr == region);
            Assert.Equal(pageSize, GCToOSInterface.LastMmap.length);
            Assert.Equal(PROT_NONE, GCToOSInterface.LastMmap.prot);
            Assert.Equal(MAP_FIXED | MAP_ANON | MAP_PRIVATE, GCToOSInterface.LastMmap.flags);

            if (HasCoredumpAdvice)
            {
                Assert.Equal(1, GCToOSInterface.MadviseCount);
                Assert.Equal(16, GCToOSInterface.LastMadvise.arg); // MADV_DONTDUMP
            }
        }
        finally
        {
            GCToOSInterface.VirtualRelease(region, pageSize);
        }
    }

    [Fact]
    public void ResetAdvisesThatTheRangeIsNoLongerNeeded()
    {
        nuint pageSize = PageSize;
        byte* region = GCToOSInterface.VirtualReserve(pageSize, 0, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);

        try
        {
            Assert.True(GCToOSInterface.VirtualCommit(region, pageSize));

            GCToOSInterface.ResetRecording();
            Assert.True(GCToOSInterface.VirtualReset(region, pageSize, false));

            // The range stays committed: no remap and no protection change.
            Assert.Equal(0, GCToOSInterface.MmapCount);
            Assert.Equal(0, GCToOSInterface.MprotectCount);
            Assert.Equal(0, GCToOSInterface.MunmapCount);

            Assert.Equal(HasCoredumpAdvice ? 2 : 1, GCToOSInterface.MadviseCount);
            Assert.True(GCToOSInterface.LastMadvise.addr == region);
            Assert.Equal(pageSize, GCToOSInterface.LastMadvise.length);
            Assert.Equal(MADV_FREE, GCToOSInterface.LastMadvise.arg);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(region, pageSize);
        }
    }

    [Fact]
    public void ReleaseUnmapsTheWholeRangeAndRejectsAnEmptyOne()
    {
        nuint pageSize = PageSize;
        byte* region = GCToOSInterface.VirtualReserve(2 * pageSize, 0, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);

        GCToOSInterface.ResetRecording();

        // munmap rejects a zero length, so the failure must be reported rather than swallowed.
        Assert.False(GCToOSInterface.VirtualRelease(region, 0));

        Assert.True(GCToOSInterface.VirtualRelease(region, 2 * pageSize));
        Assert.Equal(2 * pageSize, GCToOSInterface.LastMunmap.length);
        Assert.True(GCToOSInterface.LastMunmap.addr == region);
    }

    [Fact]
    public void LargePagesAreRequestedFromTheKernelAndCommittedInOneStep()
    {
        nuint pageSize = PageSize;
        nuint size = 2 * pageSize;

        GCToOSInterface.ResetRecording();
        byte* region = GCToOSInterface.VirtualReserveAndCommitLargePages(size);

        try
        {
            Assert.Equal(1, GCToOSInterface.MmapCount);
            Assert.Equal(MAP_ANON | MAP_PRIVATE | LargePagesFlag, GCToOSInterface.LastMmap.flags);

            // The reservation is committing, so it is never advised out of the coredump, and
            // the memory it commits is new, so it is never advised back in either.
            Assert.Equal(0, GCToOSInterface.MadviseCount);

            // Huge pages are usually not configured, in which case the mapping fails and the
            // C++ still runs the commit against the null pointer, which fails in turn.
            if (region == null)
            {
                Assert.Equal(1, GCToOSInterface.MprotectCount);
                Assert.True(GCToOSInterface.LastMprotect.addr == null);
                Assert.NotEqual(0, GCToOSInterface.LastMprotect.result);
            }
            else
            {
                Assert.Equal(PROT_READ_WRITE, GCToOSInterface.LastMprotect.arg);
                region[0] = 1;
                Assert.Equal(1, region[0]);
            }
        }
        finally
        {
            if (region != null)
            {
                GCToOSInterface.VirtualRelease(region, size);
            }
        }
    }
#else
    //
    // The Windows flag translation. The expected values are the <windows.h> ones.
    //

    private const uint MEM_COMMIT = 0x00001000;
    private const uint MEM_RESERVE = 0x00002000;
    private const uint MEM_DECOMMIT = 0x00004000;
    private const uint MEM_RELEASE = 0x00008000;
    private const uint MEM_RESET = 0x00080000;
    private const uint MEM_WRITE_WATCH = 0x00200000;
    private const uint PAGE_READWRITE = 0x04;

    [Fact]
    public void GetPageSizeIsTheFixedWindowsPageSize()
    {
        Assert.Equal((nuint)4096, GCToOSInterface.GetPageSize());
    }

    [Fact]
    public void ReserveRequestsReadWriteAddressSpaceAndIgnoresTheAlignment()
    {
        nuint size = 4 * PageSize;

        GCToOSInterface.ResetRecording();
        byte* region = GCToOSInterface.VirtualReserve(size, 0x10000, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);

        try
        {
            Assert.Equal(1, GCToOSInterface.VirtualAllocCount);
            Assert.False(GCToOSInterface.LastVirtualAlloc.numaAware);
            Assert.True(GCToOSInterface.LastVirtualAlloc.lpAddress == null);
            Assert.Equal(size, GCToOSInterface.LastVirtualAlloc.dwSize);
            Assert.Equal(MEM_RESERVE, GCToOSInterface.LastVirtualAlloc.flAllocationType);
            Assert.Equal(PAGE_READWRITE, GCToOSInterface.LastVirtualAlloc.flProtect);

            // Windows returns allocation-granularity aligned address space of its own accord.
            Assert.Equal((nuint)0, (nuint)region & 0xFFFF);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(region, size);
        }
    }

    [Fact]
    public void ReserveAddsTheWriteWatchFlagWhenAsked()
    {
        nuint size = 4 * PageSize;

        GCToOSInterface.ResetRecording();
        byte* region = GCToOSInterface.VirtualReserve(size, 0, (uint)VirtualReserveFlags.WriteWatch);

        Assert.Equal(MEM_RESERVE | MEM_WRITE_WATCH, GCToOSInterface.LastVirtualAlloc.flAllocationType);

        if (region != null)
        {
            GCToOSInterface.VirtualRelease(region, size);
        }
    }

    [Fact]
    public void ReserveAndCommitTakeTheNumaPathOnlyWhenANodeIsRequested()
    {
        nuint size = 4 * PageSize;

        GCToOSInterface.ResetRecording();
        byte* region = GCToOSInterface.VirtualReserve(size, 0, (uint)VirtualReserveFlags.None, 0);

        // VirtualAllocExNuma fails on a machine with no such node, which is not what is being
        // checked here: the port must have chosen the NUMA entry point at all.
        Assert.True(GCToOSInterface.LastVirtualAlloc.numaAware);
        Assert.Equal(0u, GCToOSInterface.LastVirtualAlloc.nndPreferred);

        if (region != null)
        {
            GCToOSInterface.ResetRecording();
            GCToOSInterface.VirtualCommit(region, PageSize, 0);
            Assert.True(GCToOSInterface.LastVirtualAlloc.numaAware);
            Assert.Equal(MEM_COMMIT, GCToOSInterface.LastVirtualAlloc.flAllocationType);

            GCToOSInterface.VirtualRelease(region, size);
        }
    }

    [Fact]
    public void CommitDecommitResetAndReleaseUseTheirOwnFlags()
    {
        nuint pageSize = PageSize;
        nuint size = 2 * pageSize;

        byte* region = GCToOSInterface.VirtualReserve(size, 0, (uint)VirtualReserveFlags.None);
        Assert.True(region != null);

        GCToOSInterface.ResetRecording();
        Assert.True(GCToOSInterface.VirtualCommit(region, pageSize));
        Assert.False(GCToOSInterface.LastVirtualAlloc.numaAware);
        Assert.True(GCToOSInterface.LastVirtualAlloc.lpAddress == region);
        Assert.Equal(MEM_COMMIT, GCToOSInterface.LastVirtualAlloc.flAllocationType);
        Assert.Equal(PAGE_READWRITE, GCToOSInterface.LastVirtualAlloc.flProtect);

        GCToOSInterface.ResetRecording();
        Assert.True(GCToOSInterface.VirtualReset(region, pageSize, false));
        Assert.Equal(MEM_RESET, GCToOSInterface.LastVirtualAlloc.flAllocationType);
        Assert.Equal(0, GCToOSInterface.VirtualUnlockCount);

        // Only the unlocking form touches VirtualUnlock, and only after a successful reset.
        GCToOSInterface.ResetRecording();
        Assert.True(GCToOSInterface.VirtualReset(region, pageSize, true));
        Assert.Equal(1, GCToOSInterface.VirtualUnlockCount);

        GCToOSInterface.ResetRecording();
        Assert.True(GCToOSInterface.VirtualDecommit(region, pageSize));
        Assert.Equal(MEM_DECOMMIT, GCToOSInterface.LastVirtualFree.dwFreeType);
        Assert.Equal(pageSize, GCToOSInterface.LastVirtualFree.dwSize);

        // A release always passes a zero size, which is what MEM_RELEASE requires.
        GCToOSInterface.ResetRecording();
        Assert.True(GCToOSInterface.VirtualRelease(region, size));
        Assert.Equal(MEM_RELEASE, GCToOSInterface.LastVirtualFree.dwFreeType);
        Assert.Equal((nuint)0, GCToOSInterface.LastVirtualFree.dwSize);
        Assert.True(GCToOSInterface.LastVirtualFree.lpAddress == region);
    }
#endif
}
