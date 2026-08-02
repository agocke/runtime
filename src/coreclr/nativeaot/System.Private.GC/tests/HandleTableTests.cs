// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Direct constants and layout tests for the first handletableconstants.h/handletablepriv.h
// translation slice.

using System;
using System.Runtime.InteropServices;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

public sealed unsafe class HandleTableTests
{
    [Fact]
    public void ConstantsMatchTheNativeTargetLayout()
    {
        Assert.Equal(10, HandleTableConstants.INITIAL_HANDLE_TABLE_ARRAY_SIZE);
        Assert.Equal(13, HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES);
        Assert.Equal(12, HandleTableConstants.HANDLE_MAX_PUBLIC_TYPES);
        Assert.Equal(12, HandleTableConstants.HNDTYPE_INTERNAL_DATABLOCK);

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
