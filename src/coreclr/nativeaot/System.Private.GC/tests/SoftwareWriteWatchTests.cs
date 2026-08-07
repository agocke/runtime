// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Behavior tests for the port of softwarewritewatch.h / softwarewritewatch.cpp: the dirty-state
// table, its (re)initialization and sizing, StaticClose, Enable/DisableForGCHeap, and
// ClearDirty/SetDirty/SetDirtyRegion/GetDirty.
//
// SoftwareWriteWatch's shipping body is the code under test, compiled directly into this
// assembly. What is substituted underneath it is GCToEEInterface.StompWriteBarrier -- there is
// no NativeAOT write barrier to bash in a test process -- and GCEnv.MemoryBarrierProcessWide,
// which in the shipping build is a real cross-thread barrier and here only counts its calls.
//
// The heap itself is a synthetic block of unmanaged memory rather than GCHeapMemory's bump
// allocator: SoftwareWriteWatch never dereferences a heap address, only shifts it to compute a
// table index, so a heap-shaped range of addresses is all any of these tests need. The table is
// a second, separately allocated unmanaged buffer, sized by the port's own GetTableByteSize, so
// that the "translated" pointer SoftwareWriteWatch computes can be checked against the raw bytes
// of the buffer the test owns.

using System;
using System.Runtime.InteropServices;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

[Collection(SyncImportsCollection.Name)]
public sealed unsafe class SoftwareWriteWatchTests : IDisposable
{
    // WRITE_WATCH_UNIT_SIZE / SOFTWARE_WRITE_WATCH_AddressToTableByteIndexShift's page size.
    private const nuint PageSize = 0x1000;

    public SoftwareWriteWatchTests()
    {
        ResetState();
    }

    public void Dispose()
    {
        ResetState();
    }

    private static void ResetState()
    {
        SoftwareWriteWatch.g_gc_sw_ww_table = null;
        SoftwareWriteWatch.g_gc_sw_ww_enabled_for_gc_heap = false;
        GCCommon.g_gc_lowest_address = null;
        GCCommon.g_gc_highest_address = null;
        GCToEEInterface.Reset();
        GCEnv.ResetMemoryBarrierProcessWideRecording();
    }

    /// <summary>
    /// A synthetic heap: a block of unmanaged memory whose bounds are published to
    /// <see cref="GCCommon.g_gc_lowest_address"/>/<see cref="GCCommon.g_gc_highest_address"/>,
    /// and a second unmanaged buffer, sized by <see cref="SoftwareWriteWatch.GetTableByteSize"/>,
    /// that becomes the write watch table over it.
    /// </summary>
    private sealed class SyntheticHeap : IDisposable
    {
        public byte* HeapStart { get; }

        public byte* HeapEnd { get; }

        public byte* UntranslatedTable { get; }

        public nuint TableByteSize { get; }

        public SyntheticHeap(int pageCount, bool initializeTable = true)
        {
            nuint heapByteSize = (nuint)pageCount * PageSize;
            HeapStart = (byte*)NativeMemory.AlignedAlloc(heapByteSize, (nuint)PageSize);
            HeapEnd = HeapStart + heapByteSize;

            GCCommon.g_gc_lowest_address = HeapStart;
            GCCommon.g_gc_highest_address = HeapEnd;

            TableByteSize = SoftwareWriteWatch.GetTableByteSize(HeapStart, HeapEnd);
            UntranslatedTable = (byte*)NativeMemory.AlignedAlloc(TableByteSize, (nuint)sizeof(nuint));
            NativeMemory.Clear(UntranslatedTable, TableByteSize);

            if (initializeTable)
            {
                SoftwareWriteWatch.InitializeUntranslatedTable(UntranslatedTable, HeapStart);
            }
        }

        /// <summary>The address of the first byte of page <paramref name="pageIndex"/>.</summary>
        public byte* Page(int pageIndex) => HeapStart + (nuint)pageIndex * PageSize;

        public void Dispose()
        {
            NativeMemory.AlignedFree(HeapStart);
            NativeMemory.AlignedFree(UntranslatedTable);
        }
    }

    //
    // Table size, alignment, and the translated/untranslated table pointers.
    //

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(20)]
    [InlineData(32)]
    public void GetTableByteSizeIsWordAlignedAndCoversEveryPage(int pageCount)
    {
        using SyntheticHeap heap = new(pageCount, initializeTable: false);

        nuint rawSize = (nuint)pageCount;
        nuint expected = (rawSize + (nuint)sizeof(nuint) - 1) & ~((nuint)sizeof(nuint) - 1);

        Assert.Equal(expected, heap.TableByteSize);
        Assert.Equal((nuint)0, heap.TableByteSize % (nuint)sizeof(nuint));
        Assert.True(heap.TableByteSize >= rawSize);
    }

    [Fact]
    public void InitializeUntranslatedTablePublishesATranslatedTableAndLeavesWriteWatchDisabled()
    {
        using SyntheticHeap heap = new(pageCount: 4);

        Assert.True(SoftwareWriteWatch.GetTable() != null);
        Assert.True(SoftwareWriteWatch.GetTable() != heap.UntranslatedTable);
        Assert.False(SoftwareWriteWatch.IsEnabledForGCHeap());
    }

    [Fact]
    public void SetDirtyWritesThroughTheTranslatedPointerIntoTheUnderlyingBuffer()
    {
        using SyntheticHeap heap = new(pageCount: 8);

        SoftwareWriteWatch.SetDirty(heap.Page(0), (nuint)sizeof(nuint));
        SoftwareWriteWatch.SetDirty(heap.Page(3), (nuint)sizeof(nuint));
        SoftwareWriteWatch.SetDirty(heap.HeapEnd - 1, 1);

        for (int i = 0; i < 8; i++)
        {
            byte expected = i is 0 or 3 or 7 ? (byte)0xff : (byte)0;
            Assert.Equal(expected, heap.UntranslatedTable[i]);
        }
    }

#if FEATURE_USE_SOFTWARE_WRITE_WATCH_FOR_GC_HEAP
    [Fact]
    public void GcMemCopyMarksOnlyTheCopiedDestinationRegionWhenWriteWatchIsEnabled()
    {
        using SyntheticHeap heap = new(pageCount: 6);
        uint* savedCardTable = gc_heap.card_table;
        uint* savedCardBundleTable = gc_heap.card_bundle_table;

        try
        {
            const nuint Length = 64;
            nuint copiedDestinationSize = Length - (nuint)sizeof(nuint);
            byte* dest = heap.Page(1) - (nint)copiedDestinationSize;
            byte* src = heap.Page(4) - (nint)copiedDestinationSize;

            for (nint offset = -(nint)sizeof(nuint);
                 offset < (nint)Length - sizeof(nuint);
                 offset++)
            {
                src[offset] = unchecked((byte)(offset + 0x40));
            }

            nuint firstCard = gc_heap.card_of(heap.HeapStart);
            nuint lastCard = gc_heap.card_of(heap.HeapEnd - 1);
            nuint firstCardWord = card_table_info.card_word(firstCard);
            nuint lastCardWord = card_table_info.card_word(lastCard);
            int cardWordCount = checked((int)(lastCardWord - firstCardWord + 1));
            uint* cardWords = stackalloc uint[cardWordCount];
            for (int i = 0; i < cardWordCount; i++)
            {
                cardWords[i] = 0;
            }

            gc_heap.card_table = cardWords - (nint)firstCardWord;
            nuint firstCardBundle =
                card_table_info.cardw_card_bundle(firstCardWord);
            nuint lastCardBundle =
                card_table_info.cardw_card_bundle(lastCardWord);
            nuint firstCardBundleWord =
                card_table_info.card_bundle_word(firstCardBundle);
            nuint lastCardBundleWord =
                card_table_info.card_bundle_word(lastCardBundle);
            int cardBundleWordCount = checked((int)(
                lastCardBundleWord - firstCardBundleWord + 1));
            uint* cardBundleWords = stackalloc uint[cardBundleWordCount];
            for (int i = 0; i < cardBundleWordCount; i++)
            {
                cardBundleWords[i] = 0;
            }

            gc_heap.card_bundle_table =
                cardBundleWords - (nint)firstCardBundleWord;

            gc_heap.gcmemcopy(dest, src, Length, copy_cards_p: 1);
            for (int i = 0; i < 6; i++)
            {
                Assert.Equal((byte)0, heap.UntranslatedTable[i]);
            }

            SoftwareWriteWatch.EnableForGCHeap();
            gc_heap.gcmemcopy(dest, src, Length, copy_cards_p: 1);

            Assert.Equal((byte)0xff, heap.UntranslatedTable[0]);
            for (int i = 1; i < 6; i++)
            {
                Assert.Equal((byte)0, heap.UntranslatedTable[i]);
            }
        }
        finally
        {
            gc_heap.card_table = savedCardTable;
            gc_heap.card_bundle_table = savedCardBundleTable;
        }
    }
#endif

    //
    // Initialize/resize preserving old bytes.
    //

    [Fact]
    public void SetResizedUntranslatedTablePreservesDirtyBitsAtTheSameAbsoluteAddresses()
    {
        // A 16 page block backs both the original 8 page heap (its middle) and the resized 16
        // page heap (all of it), so the addresses that were dirty before the resize are still
        // valid, real addresses afterwards.
        const int TotalPages = 16;
        const int OldFirstPage = 4;
        const int OldPageCount = 8;

        byte* backing = (byte*)NativeMemory.AlignedAlloc((nuint)TotalPages * PageSize, (nuint)PageSize);
        try
        {
            byte* oldHeapStart = backing + (nuint)OldFirstPage * PageSize;
            byte* oldHeapEnd = oldHeapStart + (nuint)OldPageCount * PageSize;
            GCCommon.g_gc_lowest_address = oldHeapStart;
            GCCommon.g_gc_highest_address = oldHeapEnd;

            nuint oldTableByteSize = SoftwareWriteWatch.GetTableByteSize(oldHeapStart, oldHeapEnd);
            byte* oldTable = (byte*)NativeMemory.AlignedAlloc(oldTableByteSize, (nuint)sizeof(nuint));
            NativeMemory.Clear(oldTable, oldTableByteSize);
            SoftwareWriteWatch.InitializeUntranslatedTable(oldTable, oldHeapStart);

            void* dirtyPage2 = oldHeapStart + 2 * PageSize;
            void* dirtyPage5 = oldHeapStart + 5 * PageSize;
            SoftwareWriteWatch.SetDirty(dirtyPage2, (nuint)sizeof(nuint));
            SoftwareWriteWatch.SetDirty(dirtyPage5, (nuint)sizeof(nuint));

            byte* newHeapStart = backing;
            byte* newHeapEnd = backing + (nuint)TotalPages * PageSize;
            nuint newTableByteSize = SoftwareWriteWatch.GetTableByteSize(newHeapStart, newHeapEnd);
            byte* newTable = (byte*)NativeMemory.AlignedAlloc(newTableByteSize, (nuint)sizeof(nuint));
            NativeMemory.Clear(newTable, newTableByteSize);

            // The caller resizes the table before it moves the published heap bounds: the
            // asserts inside SetResizedUntranslatedTable compare the new bounds against the
            // still-old GCCommon globals.
            SoftwareWriteWatch.SetResizedUntranslatedTable(newTable, newHeapStart, newHeapEnd);
            GCCommon.g_gc_lowest_address = newHeapStart;
            GCCommon.g_gc_highest_address = newHeapEnd;

            // The bytes physically moved: the old heap started 4 pages into the new one, so the
            // copied region begins 4 bytes into the new table.
            Assert.Equal((byte)0xff, newTable[OldFirstPage + 2]);
            Assert.Equal((byte)0xff, newTable[OldFirstPage + 5]);

            void** dirtyPages = stackalloc void*[TotalPages];
            nuint dirtyPageCount = (nuint)TotalPages;
            SoftwareWriteWatch.GetDirty(newHeapStart, (nuint)TotalPages * PageSize, dirtyPages, &dirtyPageCount, clearDirty: false, isRuntimeSuspended: true);

            Assert.Equal((nuint)2, dirtyPageCount);
            Assert.True(dirtyPages[0] == dirtyPage2);
            Assert.True(dirtyPages[1] == dirtyPage5);

            NativeMemory.AlignedFree(oldTable);
            NativeMemory.AlignedFree(newTable);
        }
        finally
        {
            NativeMemory.AlignedFree(backing);
        }
    }

    //
    // StaticClose.
    //

    [Fact]
    public void StaticCloseIsANoOpWhenNoTableWasEverCreated()
    {
        Assert.True(SoftwareWriteWatch.GetTable() == null);

        SoftwareWriteWatch.StaticClose();

        Assert.True(SoftwareWriteWatch.GetTable() == null);
        Assert.False(SoftwareWriteWatch.IsEnabledForGCHeap());
    }

    [Fact]
    public void StaticCloseClearsTheTableAndTheEnabledFlag()
    {
        using SyntheticHeap heap = new(pageCount: 4);
        SoftwareWriteWatch.EnableForGCHeap();
        Assert.True(SoftwareWriteWatch.IsEnabledForGCHeap());

        SoftwareWriteWatch.StaticClose();

        Assert.True(SoftwareWriteWatch.GetTable() == null);
        Assert.False(SoftwareWriteWatch.IsEnabledForGCHeap());
    }

    //
    // Enable/DisableForGCHeap: exact WriteBarrierOp, table pointer and suspended flag.
    //

    [Fact]
    public void EnableForGCHeapStompsSwitchToWriteWatchWithTheTableAndSuspendedFlag()
    {
        using SyntheticHeap heap = new(pageCount: 4);

        SoftwareWriteWatch.EnableForGCHeap();

        Assert.True(SoftwareWriteWatch.IsEnabledForGCHeap());
        Assert.Equal(1, GCToEEInterface.StompWriteBarrierCallCount);
        WriteBarrierParameters args = GCToEEInterface.LastStompWriteBarrier;
        Assert.Equal(WriteBarrierOp.SwitchToWriteWatch, args.operation);
        Assert.Equal((byte)1, args.is_runtime_suspended);
        Assert.True(args.write_watch_table == SoftwareWriteWatch.GetTable());
    }

    [Fact]
    public void DisableForGCHeapStompsSwitchToNonWriteWatchWithTheSuspendedFlag()
    {
        using SyntheticHeap heap = new(pageCount: 4);
        SoftwareWriteWatch.EnableForGCHeap();
        GCToEEInterface.Reset();

        SoftwareWriteWatch.DisableForGCHeap();

        Assert.False(SoftwareWriteWatch.IsEnabledForGCHeap());
        Assert.Equal(1, GCToEEInterface.StompWriteBarrierCallCount);
        WriteBarrierParameters args = GCToEEInterface.LastStompWriteBarrier;
        Assert.Equal(WriteBarrierOp.SwitchToNonWriteWatch, args.operation);
        Assert.Equal((byte)1, args.is_runtime_suspended);
        Assert.True(args.write_watch_table == null);
    }

    //
    // ClearDirty/SetDirty/SetDirtyRegion, including page-boundary behavior.
    //

    [Fact]
    public void SetDirtyAtAPageBoundaryOnlyTouchesItsOwnPage()
    {
        using SyntheticHeap heap = new(pageCount: 4);

        SoftwareWriteWatch.SetDirty(heap.Page(0) + PageSize - 1, 1);

        Assert.Equal((byte)0xff, heap.UntranslatedTable[0]);
        Assert.Equal((byte)0, heap.UntranslatedTable[1]);
    }

    [Fact]
    public void SetDirtyIsIdempotentOnceABitIsSet()
    {
        using SyntheticHeap heap = new(pageCount: 2);

        SoftwareWriteWatch.SetDirty(heap.Page(0), 1);
        SoftwareWriteWatch.SetDirty(heap.Page(0), 1);

        Assert.Equal((byte)0xff, heap.UntranslatedTable[0]);
    }

    [Fact]
    public void SetDirtyRegionSetsExactlyTheCoveredPagesAndNoOthers()
    {
        using SyntheticHeap heap = new(pageCount: 4);

        SoftwareWriteWatch.SetDirtyRegion(heap.Page(1), 2 * PageSize);

        Assert.Equal((byte)0, heap.UntranslatedTable[0]);
        Assert.Equal((byte)0xff, heap.UntranslatedTable[1]);
        Assert.Equal((byte)0xff, heap.UntranslatedTable[2]);
        Assert.Equal((byte)0, heap.UntranslatedTable[3]);
    }

    [Fact]
    public void ClearDirtyClearsExactlyTheCoveredPagesAndNoOthers()
    {
        using SyntheticHeap heap = new(pageCount: 4);
        SoftwareWriteWatch.SetDirtyRegion(heap.HeapStart, 4 * PageSize);

        SoftwareWriteWatch.ClearDirty(heap.Page(1), 2 * PageSize);

        Assert.Equal((byte)0xff, heap.UntranslatedTable[0]);
        Assert.Equal((byte)0, heap.UntranslatedTable[1]);
        Assert.Equal((byte)0, heap.UntranslatedTable[2]);
        Assert.Equal((byte)0xff, heap.UntranslatedTable[3]);
    }

    //
    // GetDirty: single-block, multi-block, subrange, output capacity, clear-vs-retain, and the
    // runtime-suspended-vs-active barrier calls. Endian-safe bit-to-byte mapping is covered by
    // DirtyBitAtEveryPositionOfAWordMapsToItsOwnPage.
    //

    [Fact]
    public void GetDirtyReportsASingleBlockOfDirtyPagesInAscendingOrder()
    {
        // 6 pages fit in one 8-byte/8-page table block.
        using SyntheticHeap heap = new(pageCount: 6);
        SoftwareWriteWatch.SetDirty(heap.Page(1), 1);
        SoftwareWriteWatch.SetDirty(heap.Page(3), 1);
        SoftwareWriteWatch.SetDirty(heap.Page(4), 1);

        void** dirtyPages = stackalloc void*[6];
        nuint dirtyPageCount = 6;
        SoftwareWriteWatch.GetDirty(heap.HeapStart, 6 * PageSize, dirtyPages, &dirtyPageCount, clearDirty: false, isRuntimeSuspended: true);

        Assert.Equal((nuint)3, dirtyPageCount);
        Assert.True(dirtyPages[0] == heap.Page(1));
        Assert.True(dirtyPages[1] == heap.Page(3));
        Assert.True(dirtyPages[2] == heap.Page(4));
    }

    [Fact]
    public void GetDirtyReportsDirtyPagesAcrossMultipleBlocksInAscendingOrder()
    {
        // 20 pages span three 8-page table blocks: [0,8), [8,16), [16,24) (the last partial).
        using SyntheticHeap heap = new(pageCount: 20);
        int[] dirtyPageIndexes = { 0, 7, 8, 15, 19 };
        foreach (int pageIndex in dirtyPageIndexes)
        {
            SoftwareWriteWatch.SetDirty(heap.Page(pageIndex), 1);
        }

        void** dirtyPages = stackalloc void*[20];
        nuint dirtyPageCount = 20;
        SoftwareWriteWatch.GetDirty(heap.HeapStart, 20 * PageSize, dirtyPages, &dirtyPageCount, clearDirty: false, isRuntimeSuspended: true);

        Assert.Equal((nuint)dirtyPageIndexes.Length, dirtyPageCount);
        for (int i = 0; i < dirtyPageIndexes.Length; i++)
        {
            Assert.True(dirtyPages[i] == heap.Page(dirtyPageIndexes[i]));
        }
    }

    [Fact]
    public void GetDirtyOverASubrangeOnlyReportsPagesWithinIt()
    {
        // Same layout as the multi-block test, but the query only covers pages [3, 13), which
        // starts and ends mid-block, so both a partial first block and a partial last block must
        // be trimmed correctly.
        using SyntheticHeap heap = new(pageCount: 20);
        int[] dirtyPageIndexes = { 0, 7, 8, 15, 19 };
        foreach (int pageIndex in dirtyPageIndexes)
        {
            SoftwareWriteWatch.SetDirty(heap.Page(pageIndex), 1);
        }

        void** dirtyPages = stackalloc void*[20];
        nuint dirtyPageCount = 20;
        SoftwareWriteWatch.GetDirty(heap.Page(3), 10 * PageSize, dirtyPages, &dirtyPageCount, clearDirty: false, isRuntimeSuspended: true);

        Assert.Equal((nuint)2, dirtyPageCount);
        Assert.True(dirtyPages[0] == heap.Page(7));
        Assert.True(dirtyPages[1] == heap.Page(8));
    }

    [Fact]
    public void GetDirtyStopsAtOutputCapacityAndOnlyClearsTheReportedPages()
    {
        using SyntheticHeap heap = new(pageCount: 6);
        for (int pageIndex = 0; pageIndex < 6; pageIndex++)
        {
            SoftwareWriteWatch.SetDirty(heap.Page(pageIndex), 1);
        }

        void** dirtyPages = stackalloc void*[2];
        nuint dirtyPageCount = 2;
        SoftwareWriteWatch.GetDirty(heap.HeapStart, 6 * PageSize, dirtyPages, &dirtyPageCount, clearDirty: true, isRuntimeSuspended: true);

        Assert.Equal((nuint)2, dirtyPageCount);
        Assert.True(dirtyPages[0] == heap.Page(0));
        Assert.True(dirtyPages[1] == heap.Page(1));

        // Only the two reported pages were cleared; the rest are still dirty.
        Assert.Equal((byte)0, heap.UntranslatedTable[0]);
        Assert.Equal((byte)0, heap.UntranslatedTable[1]);
        Assert.Equal((byte)0xff, heap.UntranslatedTable[2]);
        Assert.Equal((byte)0xff, heap.UntranslatedTable[3]);
        Assert.Equal((byte)0xff, heap.UntranslatedTable[4]);
        Assert.Equal((byte)0xff, heap.UntranslatedTable[5]);

        void** remainingPages = stackalloc void*[4];
        nuint remainingCount = 4;
        SoftwareWriteWatch.GetDirty(heap.HeapStart, 6 * PageSize, remainingPages, &remainingCount, clearDirty: false, isRuntimeSuspended: true);
        Assert.Equal((nuint)4, remainingCount);
        Assert.True(remainingPages[0] == heap.Page(2));
        Assert.True(remainingPages[3] == heap.Page(5));
    }

    [Fact]
    public void GetDirtyWithClearDirtyFalseLeavesTheTableUnchanged()
    {
        using SyntheticHeap heap = new(pageCount: 4);
        SoftwareWriteWatch.SetDirty(heap.Page(2), 1);

        void** dirtyPages = stackalloc void*[4];
        nuint dirtyPageCount = 4;
        SoftwareWriteWatch.GetDirty(heap.HeapStart, 4 * PageSize, dirtyPages, &dirtyPageCount, clearDirty: false, isRuntimeSuspended: true);
        Assert.Equal((nuint)1, dirtyPageCount);

        dirtyPageCount = 4;
        SoftwareWriteWatch.GetDirty(heap.HeapStart, 4 * PageSize, dirtyPages, &dirtyPageCount, clearDirty: false, isRuntimeSuspended: true);
        Assert.Equal((nuint)1, dirtyPageCount);
        Assert.True(dirtyPages[0] == heap.Page(2));
    }

    [Fact]
    public void GetDirtyWithZeroCapacityReturnsImmediatelyWithoutABarrier()
    {
        using SyntheticHeap heap = new(pageCount: 4);
        SoftwareWriteWatch.SetDirty(heap.Page(0), 1);

        void** dirtyPages = stackalloc void*[1];
        nuint dirtyPageCount = 0;
        SoftwareWriteWatch.GetDirty(heap.HeapStart, 4 * PageSize, dirtyPages, &dirtyPageCount, clearDirty: true, isRuntimeSuspended: false);

        Assert.Equal((nuint)0, dirtyPageCount);
        Assert.Equal(0, GCEnv.MemoryBarrierProcessWideCallCount);
        // Nothing was cleared: the page is still reported as dirty with real capacity.
        Assert.Equal((byte)0xff, heap.UntranslatedTable[0]);
    }

    [Theory]
    [InlineData(true, false, 0)] // Suspended: neither barrier is needed, regardless of clearDirty.
    [InlineData(true, true, 0)]
    [InlineData(false, false, 1)] // Active, not clearing: only the pre-scan visibility barrier.
    [InlineData(false, true, 2)] // Active, clearing a dirty page: pre-scan barrier and post-clear barrier.
    public void GetDirtyIssuesProcessWideBarriersOnlyWhenTheRuntimeIsActive(bool isRuntimeSuspended, bool clearDirty, int expectedBarrierCalls)
    {
        using SyntheticHeap heap = new(pageCount: 4);
        SoftwareWriteWatch.SetDirty(heap.Page(0), 1);

        void** dirtyPages = stackalloc void*[4];
        nuint dirtyPageCount = 4;
        SoftwareWriteWatch.GetDirty(heap.HeapStart, 4 * PageSize, dirtyPages, &dirtyPageCount, clearDirty, isRuntimeSuspended);

        Assert.Equal(expectedBarrierCalls, GCEnv.MemoryBarrierProcessWideCallCount);
    }

    [Fact]
    public void GetDirtySkipsThePostClearBarrierWhenNothingWasFoundDirty()
    {
        using SyntheticHeap heap = new(pageCount: 4);

        void** dirtyPages = stackalloc void*[4];
        nuint dirtyPageCount = 4;
        SoftwareWriteWatch.GetDirty(heap.HeapStart, 4 * PageSize, dirtyPages, &dirtyPageCount, clearDirty: true, isRuntimeSuspended: false);

        Assert.Equal((nuint)0, dirtyPageCount);
        // Only the pre-scan visibility barrier fires; there is nothing to make visible after.
        Assert.Equal(1, GCEnv.MemoryBarrierProcessWideCallCount);
    }

    [Fact]
    public void DirtyBitAtEveryPositionOfAWordMapsToItsOwnPage()
    {
        // One table block covers sizeof(nuint) pages, one page per byte of the word
        // the word GetDirtyFromBlock loads. A byte-order mixup between the loaded word and the
        // table's memory order would report the wrong page for at least one position; this
        // exercises every one of them individually.
        int pageCount = sizeof(nuint);
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            using SyntheticHeap heap = new(pageCount);
            SoftwareWriteWatch.SetDirty(heap.Page(pageIndex), 1);

            void** dirtyPages = stackalloc void*[pageCount];
            nuint dirtyPageCount = (nuint)pageCount;
            SoftwareWriteWatch.GetDirty(heap.HeapStart, (nuint)pageCount * PageSize, dirtyPages, &dirtyPageCount, clearDirty: false, isRuntimeSuspended: true);

            Assert.Equal((nuint)1, dirtyPageCount);
            Assert.True(dirtyPages[0] == heap.Page(pageIndex));

            ResetState();
        }
    }
}
