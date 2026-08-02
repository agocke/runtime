// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Direct constants and layout tests for the first handletableconstants.h/handletablepriv.h
// translation slice.

using System;
using System.Runtime.InteropServices;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

[Collection(SyncImportsCollection.Name)]
public sealed unsafe class HandleTableTests
{
    [Fact]
    public void ConstantsMatchTheNativeTargetLayout()
    {
        Assert.Equal(10, HandleTableConstants.INITIAL_HANDLE_TABLE_ARRAY_SIZE);
        Assert.Equal(13, HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES);
        Assert.Equal(12, HandleTableConstants.HANDLE_MAX_PUBLIC_TYPES);
        Assert.Equal(12, HandleTableConstants.HNDTYPE_INTERNAL_DATABLOCK);
        Assert.Equal(0u, HandleTableConstants.HNDF_NORMAL);
        Assert.Equal(1u, HandleTableConstants.HNDF_EXTRAINFO);

        Assert.Equal(65536, HandleTableConstants.HANDLE_SEGMENT_SIZE);
        Assert.Equal(4096, HandleTableConstants.HANDLE_HEADER_SIZE);
        Assert.Equal(65536, HandleTableConstants.HANDLE_SEGMENT_ALIGNMENT);
        Assert.Equal(HandleTableConstants.HANDLE_SIZE, IntPtr.Size);
        Assert.Equal(HandleTableConstants.HANDLE_HANDLES_PER_SEGMENT, (65536 - 4096) / IntPtr.Size);
        Assert.Equal(HandleTableConstants.HANDLE_HANDLES_PER_SEGMENT / 64, HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT);
        Assert.Equal(HandleTableConstants.HANDLE_HANDLES_PER_SEGMENT / 16, HandleTableConstants.HANDLE_CLUMPS_PER_SEGMENT);
        Assert.Equal(HandleTableConstants.HANDLE_HANDLES_PER_SEGMENT / 32, HandleTableConstants.HANDLE_MASKS_PER_SEGMENT);
        Assert.Equal(4, HandleTableConstants.HANDLE_CLUMPS_PER_BLOCK);
        Assert.Equal(HandleTableConstants.HANDLE_BYTES_PER_BLOCK, 64 * IntPtr.Size);
        Assert.Equal(32, HandleTableConstants.HANDLE_HANDLES_PER_MASK);
        Assert.Equal(2, HandleTableConstants.HANDLE_MASKS_PER_BLOCK);
        Assert.Equal(2, HandleTableConstants.HANDLE_CLUMPS_PER_MASK);
        Assert.Equal(
            HandleTableConstants.HANDLE_HANDLES_PER_BLOCK,
            HandleTableConstants.HANDLE_HANDLES_PER_MASK * 2);

        Assert.Equal(63, HandleTableConstants.HANDLES_PER_CACHE_BANK);
        Assert.Equal(21, HandleTableConstants.REBALANCE_TOLERANCE);
        Assert.Equal(42, HandleTableConstants.REBALANCE_LOWATER_MARK);
        Assert.Equal(84, HandleTableConstants.REBALANCE_HIWATER_MARK);
        Assert.Equal(6, HandleTableConstants.SMALL_ALLOC_COUNT);

        Assert.Equal(0x000000FFu, HandleTableConstants.GEN_CLUMP_0_MASK);
        Assert.Equal(0x00123456u, HandleTableConstants.NEXT_CLUMP_IN_MASK(0x12345678));
        Assert.Equal(0x0000FFFFul, (ulong)HandleTableConstants.HANDLE_SEGMENT_CONTENT_MASK);
        nuint expectedSegmentAlignMask = nuint.MaxValue;
        expectedSegmentAlignMask -= 0xFFFF;
        nuint actualSegmentAlignMask = unchecked((nuint)HandleTableConstants.HANDLE_SEGMENT_ALIGN_MASK);
        Assert.Equal(expectedSegmentAlignMask, actualSegmentAlignMask);
    }

    [Fact]
    public void HandleTableTypeFlagsStayAtTheNativePrefix()
    {
        AssertOffset<HandleTable>(nameof(HandleTable.rgTypeFlags), GCInterfaceOffsets.OFFSETOF__HandleTable__rgTypeFlags);
        AssertOffset<HandleTable>(nameof(HandleTable.pSegmentList), GCInterfaceOffsets.OFFSETOF__HandleTable__pSegmentList);
        AssertOffset<HandleTable>(nameof(HandleTable.Lock), GCInterfaceOffsets.OFFSETOF__HandleTable__Lock);
        AssertOffset<HandleTable>(nameof(HandleTable.uTypeCount), GCInterfaceOffsets.OFFSETOF__HandleTable__uTypeCount);
        AssertOffset<HandleTable>(nameof(HandleTable.dwCount), GCInterfaceOffsets.OFFSETOF__HandleTable__dwCount);
        AssertOffset<HandleTable>(nameof(HandleTable.pAsyncScanInfo), GCInterfaceOffsets.OFFSETOF__HandleTable__pAsyncScanInfo);
        AssertOffset<HandleTable>(nameof(HandleTable.uTableIndex), GCInterfaceOffsets.OFFSETOF__HandleTable__uTableIndex);
        AssertOffset<HandleTable>(nameof(HandleTable.rgQuickCache), GCInterfaceOffsets.OFFSETOF__HandleTable__rgQuickCache);
#if DEBUG
        AssertOffset<HandleTable>(nameof(HandleTable._DEBUG_iMaxGen), GCInterfaceOffsets.OFFSETOF__HandleTable___DEBUG_iMaxGen);
        AssertOffset<HandleTable>(nameof(HandleTable._DEBUG_TotalBlocksScanned), GCInterfaceOffsets.OFFSETOF__HandleTable___DEBUG_TotalBlocksScanned);
        AssertOffset<HandleTable>(nameof(HandleTable._DEBUG_TotalBlocksScannedNonTrivially), GCInterfaceOffsets.OFFSETOF__HandleTable___DEBUG_TotalBlocksScannedNonTrivially);
        AssertOffset<HandleTable>(nameof(HandleTable._DEBUG_TotalHandleSlotsScanned), GCInterfaceOffsets.OFFSETOF__HandleTable___DEBUG_TotalHandleSlotsScanned);
        AssertOffset<HandleTable>(nameof(HandleTable._DEBUG_TotalHandlesActuallyScanned), GCInterfaceOffsets.OFFSETOF__HandleTable___DEBUG_TotalHandlesActuallyScanned);
#endif
        Assert.Equal(GCInterfaceOffsets.SIZEOF__HandleTable, sizeof(HandleTable));
    }

    [Fact]
    public void HandleTableLifecycleInitializesSegmentsFlagsAndCaches()
    {
        const uint TypeCount = 12;

        uint* typeFlags = stackalloc uint[(int)TypeCount];
        typeFlags[4] = HandleTableConstants.HNDF_EXTRAINFO;
        typeFlags[6] = HandleTableConstants.HNDF_EXTRAINFO;

        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, TypeCount);
        Assert.True(table != null);

        try
        {
            Assert.Equal(TypeCount, table->uTypeCount);
            Assert.Equal(uint.MaxValue, table->uTableIndex);
            Assert.Equal(HandleTableConstants.HNDF_EXTRAINFO, table->rgTypeFlags[4]);
            Assert.Equal(HandleTableConstants.HNDF_EXTRAINFO, table->rgTypeFlags[6]);
            Assert.Equal(HandleTableConstants.HNDF_NORMAL, table->rgTypeFlags[12]);
            Assert.True(table->pSegmentList != null);
            Assert.True(table->pSegmentList->Header.pHandleTable == table);

            HandleTypeCache* mainCache = HandleTableManager.GetMainCache(table);
            for (uint type = 0; type < TypeCount; type++)
            {
                Assert.Equal(HandleTableConstants.HANDLES_PER_CACHE_BANK, mainCache[type].lFreeIndex);
            }

            HandleTableManager.HndSetHandleTableIndex(table, 17);
            Assert.Equal(17u, HandleTableManager.HndGetHandleTableIndex(table));
        }
        finally
        {
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Fact]
    public void HandleMetadataAndContainmentFollowOwningSegment()
    {
        const uint Type = 0;

        uint* typeFlags = stackalloc uint[1];
        typeFlags[Type] = HandleTableConstants.HNDF_EXTRAINFO;
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, 1);
        HandleTable* otherTable = HandleTableManager.HndCreateHandleTable(typeFlags, 1);
        Assert.True(table != null);
        Assert.True(otherTable != null);

        try
        {
            OBJECTHANDLE handle;
            Assert.Equal(1u, HandleTableCore.TableAllocBulkHandles(table, Type, &handle, 1));

            Assert.Equal(Type, HandleTableCore.HandleFetchType(handle));
            Assert.True(HandleTableManager.HndGetHandleTable(handle) == table);
            Assert.True(HandleTableCore.TableContainHandle(table, handle));
            Assert.False(HandleTableCore.TableContainHandle(otherTable, handle));

            HandleTableManager.HndSetHandleExtraInfo(handle, Type, 0x1234);
            Assert.Equal((nuint)0x1234, HandleTableManager.HndGetHandleExtraInfo(handle));
            Assert.Equal(
                (nuint)0x1234,
                HandleTableManager.HndCompareExchangeHandleExtraInfo(handle, Type, 0x1234, 0x5678));
            Assert.Equal((nuint)0x5678, HandleTableManager.HndGetHandleExtraInfo(handle));
            Assert.Equal(
                (nuint)0x5678,
                HandleTableManager.HndCompareExchangeHandleExtraInfo(handle, Type, 0x1234, 0x9ABC));
            Assert.Equal((nuint)0x5678, HandleTableManager.HndGetHandleExtraInfo(handle));
        }
        finally
        {
            HandleTableManager.HndDestroyHandleTable(otherTable);
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Fact]
    public void HandleCountExcludesReserveFreeAndQuickCaches()
    {
        uint* typeFlags = stackalloc uint[1];
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, 1);
        Assert.True(table != null);

        try
        {
            OBJECTHANDLE handle = HandleTableCache.TableAllocSingleHandleFromCache(table, 0);

            Assert.Equal(1u, HandleTableManager.HndCountHandles(table));

            HandleTableCache.TableFreeSingleHandleToCache(table, 0, handle);

            Assert.Equal(0u, HandleTableManager.HndCountHandles(table));
        }
        finally
        {
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Fact]
    public void TypedAndUnknownTypeDestructionReturnHandlesToCache()
    {
        uint* typeFlags = stackalloc uint[2];
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, 2);
        Assert.True(table != null);

        try
        {
            OBJECTHANDLE first = HandleTableCache.TableAllocSingleHandleFromCache(table, 0);
            OBJECTHANDLE second = HandleTableCache.TableAllocSingleHandleFromCache(table, 1);
            *(nuint*)first.Value = 0x1234;
            *(nuint*)second.Value = 0x5678;

            HandleTableManager.HndDestroyHandle(table, 0, first);
            HandleTableManager.HndDestroyHandleOfUnknownType(table, second);

            Assert.Equal(0u, HandleTableManager.HndCountHandles(table));
            Assert.Equal((nuint)first.Value, (nuint)((OBJECTHANDLE*)table->rgQuickCache)[0].Value);
            Assert.Equal((nuint)second.Value, (nuint)((OBJECTHANDLE*)table->rgQuickCache)[1].Value);
        }
        finally
        {
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnpreparedBulkFreeSortsClearsAndFreesMoreThanOneBlock(bool failLargeScratchAllocation)
    {
        const uint Count = 130;

        uint* typeFlags = stackalloc uint[1];
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, 1);
        Assert.True(table != null);

        try
        {
            OBJECTHANDLE* handles = stackalloc OBJECTHANDLE[(int)Count];
            Assert.Equal(Count, HandleTableCore.TableAllocBulkHandles(table, 0, handles, Count));

            for (int i = 0; i < Count; i++)
            {
                *(nuint*)handles[i].Value = (nuint)(i + 1);
            }

            for (int i = 0; i < Count / 2; i++)
            {
                OBJECTHANDLE temporary = handles[i];
                handles[i] = handles[Count - i - 1];
                handles[Count - i - 1] = temporary;
            }

            SyncImports.FailNextAlloc = failLargeScratchAllocation;
            HandleTableCore.TableFreeBulkUnpreparedHandles(table, 0, handles, Count);

            Assert.False(SyncImports.FailNextAlloc);
            Assert.Equal(0u, table->dwCount);
            for (int i = 0; i < Count; i++)
            {
                Assert.Equal((nuint)0, *(nuint*)handles[i].Value);
            }
        }
        finally
        {
            SyncImports.FailNextAlloc = false;
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Fact]
    public void TableSegmentHeaderMatchesThePackedNativeFieldOrder()
    {
        int offset = 0;
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.rgGeneration), offset);
        offset += HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT * sizeof(uint);
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.rgAllocation), offset);
        offset += HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT;
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.rgFreeMask), offset);
        offset += HandleTableConstants.HANDLE_MASKS_PER_SEGMENT * sizeof(uint);
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.rgBlockType), offset);
        offset += HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT;
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.rgUserData), offset);
        offset += HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT;
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.rgLocks), offset);
        offset += HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT;
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.rgTail), offset);
        offset += HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES;
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.rgHint), offset);
        offset += HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES;
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.rgFreeCount), offset);
        offset += HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES * sizeof(uint);
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.pNextSegment), offset);
        offset += IntPtr.Size;
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.pHandleTable), offset);
        offset += IntPtr.Size;
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.flags), offset++);
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.bFreeList), offset++);
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.bEmptyLine), offset++);
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.bCommitLine), offset++);
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.bDecommitLine), offset++);
        AssertOffset<_TableSegmentHeader>(nameof(_TableSegmentHeader.bSequence), offset++);
        Assert.Equal(HandleTableConstants.TABLE_SEGMENT_HEADER_SIZE, offset);
        Assert.Equal(HandleTableConstants.TABLE_SEGMENT_HEADER_SIZE, sizeof(_TableSegmentHeader));
    }

    [Fact]
    public void TableSegmentIsExactlyOneAlignedNativeSegment()
    {
        AssertOffset<TableSegment>(nameof(TableSegment.Header), 0);
        AssertOffset<TableSegment>(nameof(TableSegment.rgUnused), HandleTableConstants.TABLE_SEGMENT_HEADER_SIZE);
        AssertOffset<TableSegment>(nameof(TableSegment.rgValue), HandleTableConstants.HANDLE_HEADER_SIZE);
        Assert.Equal(HandleTableConstants.HANDLE_SEGMENT_SIZE, sizeof(TableSegment));
    }

    [Fact]
    public void HandleTypeCacheKeepsItsIndicesInDifferentCacheLines()
    {
        AssertOffset<HandleTypeCache>(nameof(HandleTypeCache.rgReserveBank), 0);
        int reserveIndex = HandleTableConstants.HANDLES_PER_CACHE_BANK * IntPtr.Size;
        int freeBank = AlignUp(reserveIndex + sizeof(int), IntPtr.Size);
        int freeIndex = freeBank + (HandleTableConstants.HANDLES_PER_CACHE_BANK * IntPtr.Size);
        AssertOffset<HandleTypeCache>(nameof(HandleTypeCache.lReserveIndex), reserveIndex);
        AssertOffset<HandleTypeCache>(nameof(HandleTypeCache.rgFreeBank), freeBank);
        AssertOffset<HandleTypeCache>(nameof(HandleTypeCache.lFreeIndex), freeIndex);
        Assert.Equal(AlignUp(freeIndex + sizeof(int), IntPtr.Size), sizeof(HandleTypeCache));
    }

    [Fact]
    public void DacHandleTypesMatchTheirNativeAnalogues()
    {
        int nextSegment = HandleTableConstants.TABLE_SEGMENT_HEADER_SIZE - (2 * IntPtr.Size) - 6;
        AssertOffset<dac_handle_table_segment>(nameof(dac_handle_table_segment.rgGeneration), 0);
        AssertOffset<dac_handle_table_segment>(nameof(dac_handle_table_segment.pNextSegment), nextSegment);
        Assert.Equal(nextSegment + IntPtr.Size, sizeof(dac_handle_table_segment));

        AssertOffset<dac_handle_table>(nameof(dac_handle_table.padding), 0);
        int segmentList = AlignUp(HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES * sizeof(uint), IntPtr.Size);
        AssertOffset<dac_handle_table>(nameof(dac_handle_table.pSegmentList), segmentList);
        Assert.Equal(segmentList + IntPtr.Size, sizeof(dac_handle_table));

        AssertOffset<dac_handle_table_bucket>(nameof(dac_handle_table_bucket.pTable), 0);
        AssertOffset<dac_handle_table_bucket>(nameof(dac_handle_table_bucket.HandleTableIndex), IntPtr.Size);
        Assert.Equal(AlignUp(IntPtr.Size + sizeof(uint), IntPtr.Size), sizeof(dac_handle_table_bucket));
    }

    private static void AssertOffset<T>(string fieldName, int expected)
        where T : unmanaged
    {
        Assert.Equal((nint)expected, Marshal.OffsetOf<T>(fieldName));
    }

    private static int AlignUp(int value, int alignment) =>
        (value + (alignment - 1)) & ~(alignment - 1);
}
