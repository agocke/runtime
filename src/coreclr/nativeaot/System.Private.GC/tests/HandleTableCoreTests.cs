// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Direct tests for the segment lifecycle and handle-to-segment helpers ported from
// handletablecore.cpp.

using System.Collections.Generic;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

[Collection(SyncImportsCollection.Name)]
public sealed unsafe class HandleTableCoreTests
{
    [Fact]
    public void SegmentAllocCreatesAnAlignedInitializedSegment()
    {
        byte tableStorage = 0;
        HandleTable* table = (HandleTable*)&tableStorage;
        TableSegment* segment = HandleTableCore.SegmentAlloc(table);

        Assert.True(segment != null);

        try
        {
            _TableSegmentHeader* header = (_TableSegmentHeader*)segment;
            Assert.Equal((nuint)0, (nuint)segment & (HandleTableConstants.HANDLE_SEGMENT_ALIGNMENT - 1));
            Assert.True(header->pHandleTable == table);
            Assert.True(header->pNextSegment == null);

            nuint pageSize = GCToOSInterface.GetPageSize();
            nuint committed = HandleTableCore.GetInitialCommitSize(pageSize);
            Assert.Equal(HandleTableCore.GetInitialCommitLine(committed), header->bCommitLine);

            Assert.Equal((byte)0, header->flags);
            Assert.Equal((byte)0, header->bFreeList);
            Assert.Equal((byte)0, header->bEmptyLine);
            Assert.Equal((byte)0, header->bDecommitLine);
            Assert.Equal((byte)0, header->bSequence);

            for (int i = 0; i < HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT * sizeof(uint); i++)
            {
                Assert.Equal((byte)0xFF, header->rgGeneration[i]);
            }

            for (int i = 0; i < HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT; i++)
            {
                Assert.Equal(
                    i == HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT - 1 ? HandleTableConstants.BLOCK_INVALID : (byte)(i + 1),
                    header->rgAllocation[i]);
                Assert.Equal(HandleTableConstants.TYPE_INVALID, header->rgBlockType[i]);
                Assert.Equal(HandleTableConstants.BLOCK_INVALID, header->rgUserData[i]);
                Assert.Equal((byte)0, header->rgLocks[i]);
            }

            for (int i = 0; i < HandleTableConstants.HANDLE_MASKS_PER_SEGMENT; i++)
            {
                Assert.Equal(HandleTableConstants.MASK_EMPTY, header->rgFreeMask[i]);
            }

            for (int i = 0; i < HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES; i++)
            {
                Assert.Equal(HandleTableConstants.BLOCK_INVALID, header->rgTail[i]);
                Assert.Equal(HandleTableConstants.BLOCK_INVALID, header->rgHint[i]);
                Assert.Equal(0u, header->rgFreeCount[i]);
            }
        }
        finally
        {
            HandleTableCore.SegmentFree(segment);
        }
    }

    [Theory]
    [MemberData(nameof(HandleIndices))]
    public void HandleFetchSegmentPointerMasksToTheSegmentBase(int handleIndex)
    {
        TableSegment* segment = HandleTableCore.SegmentAlloc(null);
        Assert.True(segment != null);

        try
        {
            void* handle = (byte*)segment
                + HandleTableConstants.HANDLE_HEADER_SIZE
                + (handleIndex * HandleTableConstants.HANDLE_SIZE);

            Assert.True(HandleTableCore.HandleFetchSegmentPointer(new OBJECTHANDLE(handle)) == (_TableSegmentHeader*)segment);
        }
        finally
        {
            HandleTableCore.SegmentFree(segment);
        }
    }

    [Fact]
    public void SegmentInitializeReturnsFalseForAnUnreservedAddress()
    {
        Assert.False(HandleTableCore.SegmentInitialize((TableSegment*)(nuint)0x1000, null));
    }

#if TARGET_64BIT
    [Theory]
    [InlineData(4096, 4096, 0)]
    [InlineData(16384, 16384, 24)]
    [InlineData(65536, 65536, 120)]
#else
    [Theory]
    [InlineData(4096, 4096, 0)]
    [InlineData(16384, 16384, 48)]
    [InlineData(65536, 65536, 240)]
#endif
    public void InitialCommitCalculationMatchesTheNativePageRounding(
        uint pageSize,
        uint expectedCommitSize,
        byte expectedCommitLine)
    {
        nuint commitSize = HandleTableCore.GetInitialCommitSize(pageSize);
        Assert.Equal((nuint)expectedCommitSize, commitSize);
        Assert.Equal(expectedCommitLine, HandleTableCore.GetInitialCommitLine(commitSize));
    }

    [Fact]
    public void BlockLockHelpersMaintainTheNativeByteCount()
    {
        TableSegment* segment = HandleTableCore.SegmentAlloc(null);
        Assert.True(segment != null);

        try
        {
            Assert.False(HandleTableCore.BlockIsLocked(segment, 0));

            HandleTableCore.BlockLock(segment, 0);
            HandleTableCore.BlockLock(segment, 0);
            Assert.True(HandleTableCore.BlockIsLocked(segment, 0));
            Assert.Equal((byte)2, segment->Header.rgLocks[0]);

            HandleTableCore.BlockUnlock(segment, 0);
            HandleTableCore.BlockUnlock(segment, 0);
            Assert.False(HandleTableCore.BlockIsLocked(segment, 0));
        }
        finally
        {
            HandleTableCore.SegmentFree(segment);
        }
    }

    [Fact]
    public void SegmentInsertBlockFromFreeListWorkerCommitsAndLinksBlocks()
    {
        const uint Type = 2;

        TableSegment* segment = HandleTableCore.SegmentAlloc(null);
        Assert.True(segment != null);

        try
        {
            byte initialCommitLine = segment->Header.bCommitLine;
            nuint blocksPerPage = GCToOSInterface.GetPageSize() / HandleTableConstants.HANDLE_BYTES_PER_BLOCK;
            byte expectedCommitLine = initialCommitLine == 0
                ? (byte)blocksPerPage
                : initialCommitLine;

            Assert.Equal(0u, HandleTableCore.SegmentInsertBlockFromFreeListWorker(segment, Type, false));
            Assert.Equal((byte)1, segment->Header.bFreeList);
            Assert.Equal((byte)1, segment->Header.bEmptyLine);
            Assert.Equal((byte)0, segment->Header.bDecommitLine);
            Assert.Equal(expectedCommitLine, segment->Header.bCommitLine);
            Assert.Equal((byte)0, segment->Header.rgAllocation[0]);
            Assert.Equal((byte)0, segment->Header.rgTail[Type]);
            Assert.Equal((byte)0, segment->Header.rgHint[Type]);
            Assert.Equal((byte)Type, segment->Header.rgBlockType[0]);
            Assert.Equal((uint)HandleTableConstants.HANDLE_HANDLES_PER_BLOCK, segment->Header.rgFreeCount[Type]);
            Assert.False(segment->Header.fResortChains);

            Assert.Equal(1u, HandleTableCore.SegmentInsertBlockFromFreeListWorker(segment, Type, false));
            Assert.Equal((byte)2, segment->Header.bFreeList);
            Assert.Equal((byte)2, segment->Header.bEmptyLine);
            Assert.Equal((byte)0, segment->Header.rgAllocation[1]);
            Assert.Equal((byte)1, segment->Header.rgAllocation[0]);
            Assert.Equal((byte)1, segment->Header.rgTail[Type]);
            Assert.Equal((byte)0, segment->Header.rgHint[Type]);
            Assert.Equal((byte)Type, segment->Header.rgBlockType[1]);
            Assert.Equal((uint)(2 * HandleTableConstants.HANDLE_HANDLES_PER_BLOCK), segment->Header.rgFreeCount[Type]);
            Assert.True(segment->Header.fResortChains);
        }
        finally
        {
            HandleTableCore.SegmentFree(segment);
        }
    }

    [Fact]
    public void SegmentInsertBlockFromFreeListWorkerCommitsTheNextPage()
    {
        const uint Type = 3;

        TableSegment* segment = HandleTableCore.SegmentAlloc(null);
        Assert.True(segment != null);

        try
        {
            byte initialCommitLine = segment->Header.bCommitLine;
            nuint blocksPerPage = GCToOSInterface.GetPageSize() / HandleTableConstants.HANDLE_BYTES_PER_BLOCK;

            if (initialCommitLine == HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT)
            {
                Assert.Equal((nuint)HandleTableConstants.HANDLE_SEGMENT_SIZE, GCToOSInterface.GetPageSize());
                return;
            }

            segment->Header.bFreeList = initialCommitLine;
            segment->Header.bEmptyLine = initialCommitLine;
            segment->Header.rgAllocation[initialCommitLine] = HandleTableConstants.BLOCK_INVALID;

            Assert.Equal(
                (uint)initialCommitLine,
                HandleTableCore.SegmentInsertBlockFromFreeListWorker(segment, Type, false));
            Assert.Equal(initialCommitLine, segment->Header.bDecommitLine);
            Assert.Equal((byte)(initialCommitLine + blocksPerPage), segment->Header.bCommitLine);
            Assert.Equal((byte)(initialCommitLine + 1), segment->Header.bEmptyLine);
        }
        finally
        {
            HandleTableCore.SegmentFree(segment);
        }
    }

    public static IEnumerable<object[]> HandleIndices()
    {
        yield return new object[] { 0 };
        yield return new object[] { 1 };
        yield return new object[] { HandleTableConstants.HANDLE_HANDLES_PER_SEGMENT - 1 };
    }
}
