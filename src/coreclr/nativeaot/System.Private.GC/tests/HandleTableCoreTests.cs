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

    [Fact]
    public void SegmentRemoveFreeBlocksReturnsEmptyBlocksToTheFreeList()
    {
        const uint Type = 5;

        TableSegment* segment = HandleTableCore.SegmentAlloc(null);
        Assert.True(segment != null);

        try
        {
            Assert.Equal(0u, HandleTableCore.SegmentInsertBlockFromFreeListWorker(segment, Type, false));
            Assert.Equal(1u, HandleTableCore.SegmentInsertBlockFromFreeListWorker(segment, Type, false));

            HandleTableCore.SegmentRemoveFreeBlocks(segment, Type, null);

            Assert.Equal((byte)0, segment->Header.bFreeList);
            Assert.Equal((byte)1, segment->Header.rgAllocation[0]);
            Assert.Equal((byte)2, segment->Header.rgAllocation[1]);
            Assert.Equal(HandleTableConstants.TYPE_INVALID, segment->Header.rgBlockType[0]);
            Assert.Equal(HandleTableConstants.TYPE_INVALID, segment->Header.rgBlockType[1]);
            Assert.Equal(HandleTableConstants.BLOCK_INVALID, segment->Header.rgTail[Type]);
            Assert.Equal(HandleTableConstants.BLOCK_INVALID, segment->Header.rgHint[Type]);
            Assert.Equal(0u, segment->Header.rgFreeCount[Type]);
            Assert.True(segment->Header.fResortChains);
        }
        finally
        {
            HandleTableCore.SegmentFree(segment);
        }
    }

    [Fact]
    public void SegmentRemoveFreeBlocksDefersLockedBlocks()
    {
        const uint Type = 6;

        TableSegment* segment = HandleTableCore.SegmentAlloc(null);
        Assert.True(segment != null);

        try
        {
            Assert.Equal(0u, HandleTableCore.SegmentInsertBlockFromFreeListWorker(segment, Type, false));
            HandleTableCore.BlockLock(segment, 0);
            bool scavengeLater = false;

            HandleTableCore.SegmentRemoveFreeBlocks(segment, Type, &scavengeLater);

            Assert.True(scavengeLater);
            Assert.Equal((byte)1, segment->Header.bFreeList);
            Assert.Equal((byte)Type, segment->Header.rgBlockType[0]);
            Assert.Equal((byte)0, segment->Header.rgTail[Type]);
            Assert.Equal((byte)0, segment->Header.rgHint[Type]);
            Assert.Equal((uint)HandleTableConstants.HANDLE_HANDLES_PER_BLOCK, segment->Header.rgFreeCount[Type]);
        }
        finally
        {
            HandleTableCore.SegmentFree(segment);
        }
    }

    [Fact]
    public void SegmentRemoveFreeBlocksReclaimsParallelUserDataBlock()
    {
        const uint Type = 7;
        const uint DataType = HandleTableConstants.HNDTYPE_INTERNAL_DATABLOCK;

        TableSegment* segment = HandleTableCore.SegmentAlloc(null);
        Assert.True(segment != null);

        try
        {
            Assert.Equal(0u, HandleTableCore.SegmentInsertBlockFromFreeListWorker(segment, Type, false));
            Assert.Equal(1u, HandleTableCore.SegmentInsertBlockFromFreeListWorker(segment, DataType, false));
            segment->Header.rgUserData[0] = 1;
            HandleTableCore.BlockLock(segment, 1);

            HandleTableCore.SegmentRemoveFreeBlocks(segment, Type, null);

            Assert.Equal(HandleTableConstants.BLOCK_INVALID, segment->Header.rgUserData[0]);
            Assert.False(HandleTableCore.BlockIsLocked(segment, 1));
            Assert.Equal(HandleTableConstants.TYPE_INVALID, segment->Header.rgBlockType[0]);
            Assert.Equal(HandleTableConstants.TYPE_INVALID, segment->Header.rgBlockType[1]);
            Assert.Equal((byte)1, segment->Header.bFreeList);
            Assert.Equal((byte)0, segment->Header.rgAllocation[1]);
            Assert.Equal((byte)2, segment->Header.rgAllocation[0]);
            Assert.Equal(0u, segment->Header.rgFreeCount[Type]);
            Assert.Equal(0u, segment->Header.rgFreeCount[DataType]);
        }
        finally
        {
            HandleTableCore.SegmentFree(segment);
        }
    }

    [Fact]
    public void SegmentRemoveFreeBlocksPreservesSurvivingChainAndFreedOrder()
    {
        const uint Type = 8;

        TableSegment* segment = HandleTableCore.SegmentAlloc(null);
        Assert.True(segment != null);

        try
        {
            Assert.Equal(0u, HandleTableCore.SegmentInsertBlockFromFreeListWorker(segment, Type, false));
            Assert.Equal(1u, HandleTableCore.SegmentInsertBlockFromFreeListWorker(segment, Type, false));
            Assert.Equal(2u, HandleTableCore.SegmentInsertBlockFromFreeListWorker(segment, Type, false));

            OBJECTHANDLE handle;
            Assert.Equal(1u, HandleTableCore.BlockAllocHandlesInitial(segment, Type, 1, &handle, 1));
            segment->Header.rgFreeCount[Type]--;

            HandleTableCore.SegmentRemoveFreeBlocks(segment, Type, null);

            Assert.Equal((byte)1, segment->Header.rgTail[Type]);
            Assert.Equal((byte)1, segment->Header.rgHint[Type]);
            Assert.Equal((byte)1, segment->Header.rgAllocation[1]);
            Assert.Equal((byte)0, segment->Header.bFreeList);
            Assert.Equal((byte)2, segment->Header.rgAllocation[0]);
            Assert.Equal((byte)3, segment->Header.rgAllocation[2]);
            Assert.Equal(HandleTableConstants.TYPE_INVALID, segment->Header.rgBlockType[0]);
            Assert.Equal((byte)Type, segment->Header.rgBlockType[1]);
            Assert.Equal(HandleTableConstants.TYPE_INVALID, segment->Header.rgBlockType[2]);
            Assert.Equal(63u, segment->Header.rgFreeCount[Type]);
        }
        finally
        {
            HandleTableCore.SegmentFree(segment);
        }
    }

    [Theory]
    [InlineData(0, 1, 0xFFFFFFFEu)]
    [InlineData(0, 31, 0x80000000u)]
    [InlineData(0, 32, 0u)]
    [InlineData(2, 40, 0u)]
    [InlineData(0, 64, 0u)]
    public void BlockAllocHandlesInitialMarksMasksAndReturnsSequentialSlots(uint block, uint count, uint firstMask)
    {
        TableSegment* segment = HandleTableCore.SegmentAlloc(null);
        Assert.True(segment != null);

        try
        {
            Assert.Equal(0u, HandleTableCore.SegmentInsertBlockFromFreeListWorker(segment, 0, false));

            OBJECTHANDLE* handles = stackalloc OBJECTHANDLE[HandleTableConstants.HANDLE_HANDLES_PER_BLOCK];
            Assert.Equal(count, HandleTableCore.BlockAllocHandlesInitial(segment, 0, block, handles, count));

            uint firstMaskIndex = block * HandleTableConstants.HANDLE_MASKS_PER_BLOCK;
            Assert.Equal(firstMask, segment->Header.rgFreeMask[firstMaskIndex]);

            if (count > HandleTableConstants.HANDLE_HANDLES_PER_MASK)
            {
                uint secondMask = count == HandleTableConstants.HANDLE_HANDLES_PER_BLOCK
                    ? HandleTableConstants.MASK_FULL
                    : HandleTableConstants.MASK_EMPTY << (int)(count - HandleTableConstants.HANDLE_HANDLES_PER_MASK);
                Assert.Equal(secondMask, segment->Header.rgFreeMask[firstMaskIndex + 1]);
            }

            for (uint i = 0; i < count; i++)
            {
                uint handleIndex = (block * HandleTableConstants.HANDLE_HANDLES_PER_BLOCK) + i;
                Assert.True(
                    handles[i].Value == &segment->rgValue[handleIndex],
                    $"Handle {i} did not point to its sequential slot.");
            }
        }
        finally
        {
            HandleTableCore.SegmentFree(segment);
        }
    }

    [Theory]
    [InlineData(0x000000FFu, 0, 0x000000F8u, 0, 1, 2)]
    [InlineData(0x0000FF00u, 32, 0x0000F800u, 40, 41, 42)]
    public void BlockAllocHandlesInMaskPreservesUnallocatedBits(
        uint initialMask,
        uint displacement,
        uint expectedMask,
        uint firstIndex,
        uint secondIndex,
        uint thirdIndex)
    {
        TableSegment* segment = HandleTableCore.SegmentAlloc(null);
        Assert.True(segment != null);

        try
        {
            uint mask = initialMask;
            OBJECTHANDLE* handles = stackalloc OBJECTHANDLE[3];

            Assert.Equal(3u, HandleTableCore.BlockAllocHandlesInMask(segment, 0, &mask, displacement, handles, 3));
            Assert.Equal(expectedMask, mask);
            Assert.True(handles[0].Value == &segment->rgValue[firstIndex]);
            Assert.True(handles[1].Value == &segment->rgValue[secondIndex]);
            Assert.True(handles[2].Value == &segment->rgValue[thirdIndex]);
        }
        finally
        {
            HandleTableCore.SegmentFree(segment);
        }
    }

    [Fact]
    public void BlockAllocHandlesInMaskTakesTheLowestFreeBits()
    {
        TableSegment* segment = HandleTableCore.SegmentAlloc(null);
        Assert.True(segment != null);

        try
        {
            uint mask = 0b1010_0100;
            OBJECTHANDLE* handles = stackalloc OBJECTHANDLE[3];

            Assert.Equal(3u, HandleTableCore.BlockAllocHandlesInMask(segment, 0, &mask, 32, handles, 3));
            Assert.Equal(0u, mask);
            Assert.True(handles[0].Value == &segment->rgValue[34]);
            Assert.True(handles[1].Value == &segment->rgValue[37]);
            Assert.True(handles[2].Value == &segment->rgValue[39]);
        }
        finally
        {
            HandleTableCore.SegmentFree(segment);
        }
    }

    [Fact]
    public void SegmentAllocHandlesFromTypeChainContinuesIntoTheNextBlock()
    {
        const uint Type = 4;

        TableSegment* segment = HandleTableCore.SegmentAlloc(null);
        Assert.True(segment != null);

        try
        {
            Assert.Equal(0u, HandleTableCore.SegmentInsertBlockFromFreeListWorker(segment, Type, false));
            Assert.Equal(1u, HandleTableCore.SegmentInsertBlockFromFreeListWorker(segment, Type, false));

            OBJECTHANDLE* initialHandles = stackalloc OBJECTHANDLE[63];
            Assert.Equal(63u, HandleTableCore.BlockAllocHandlesInitial(segment, Type, 0, initialHandles, 63));
            segment->Header.rgFreeCount[Type] -= 63;

            OBJECTHANDLE* handles = stackalloc OBJECTHANDLE[3];
            Assert.Equal(3u, HandleTableCore.SegmentAllocHandlesFromTypeChain(segment, Type, handles, 3));
            Assert.True(handles[0].Value == &segment->rgValue[63]);
            Assert.True(handles[1].Value == &segment->rgValue[64]);
            Assert.True(handles[2].Value == &segment->rgValue[65]);
            Assert.Equal((byte)1, segment->Header.rgHint[Type]);
            Assert.Equal(62u, segment->Header.rgFreeCount[Type]);
        }
        finally
        {
            HandleTableCore.SegmentFree(segment);
        }
    }

    [Fact]
    public void SegmentFreeHandlesReturnsEmptyBlocksToTheFreeList()
    {
        const uint Type = 9;

        TableSegment* segment = HandleTableCore.SegmentAlloc(null);
        Assert.True(segment != null);

        try
        {
            Assert.Equal(0u, HandleTableCore.SegmentInsertBlockFromFreeListWorker(segment, Type, false));
            Assert.Equal(1u, HandleTableCore.SegmentInsertBlockFromFreeListWorker(segment, Type, false));

            OBJECTHANDLE* handles = stackalloc OBJECTHANDLE[67];
            Assert.Equal(64u, HandleTableCore.BlockAllocHandlesInitial(segment, Type, 0, handles, 64));
            Assert.Equal(3u, HandleTableCore.BlockAllocHandlesInitial(segment, Type, 1, handles + 64, 3));
            segment->Header.rgFreeCount[Type] -= 67;

            Assert.Equal(67u, HandleTableCore.SegmentFreeHandles(segment, Type, handles, 67));

            Assert.Equal(HandleTableConstants.MASK_EMPTY, segment->Header.rgFreeMask[0]);
            Assert.Equal(HandleTableConstants.MASK_EMPTY, segment->Header.rgFreeMask[1]);
            Assert.Equal(HandleTableConstants.MASK_EMPTY, segment->Header.rgFreeMask[2]);
            Assert.Equal(HandleTableConstants.MASK_EMPTY, segment->Header.rgFreeMask[3]);
            Assert.Equal(HandleTableConstants.TYPE_INVALID, segment->Header.rgBlockType[0]);
            Assert.Equal(HandleTableConstants.TYPE_INVALID, segment->Header.rgBlockType[1]);
            Assert.Equal((byte)0, segment->Header.bFreeList);
            Assert.Equal((byte)1, segment->Header.rgAllocation[0]);
            Assert.Equal((byte)2, segment->Header.rgAllocation[1]);
            Assert.Equal(0u, segment->Header.rgFreeCount[Type]);
        }
        finally
        {
            HandleTableCore.SegmentFree(segment);
        }
    }

    [Fact]
    public void SegmentFreeHandlesClearsParallelUserData()
    {
        const uint Type = 10;
        const uint DataType = HandleTableConstants.HNDTYPE_INTERNAL_DATABLOCK;

        TableSegment* segment = HandleTableCore.SegmentAlloc(null);
        Assert.True(segment != null);

        try
        {
            Assert.Equal(0u, HandleTableCore.SegmentInsertBlockFromFreeListWorker(segment, Type, false));
            Assert.Equal(1u, HandleTableCore.SegmentInsertBlockFromFreeListWorker(segment, DataType, false));
            segment->Header.rgUserData[0] = 1;
            HandleTableCore.BlockLock(segment, 1);

            OBJECTHANDLE* handles = stackalloc OBJECTHANDLE[2];
            Assert.Equal(2u, HandleTableCore.BlockAllocHandlesInitial(segment, Type, 0, handles, 2));
            segment->Header.rgFreeCount[Type] -= 2;

            nuint* userData = HandleTableCore.BlockFetchUserDataPointer(&segment->Header, 0, true);
            Assert.True(userData != null);
            userData[0] = 0x1234;
            userData[1] = 0x5678;

            Assert.Equal(1u, HandleTableCore.SegmentFreeHandles(segment, Type, handles, 1));

            Assert.Equal((nuint)0, userData[0]);
            Assert.Equal((nuint)0x5678, userData[1]);
            Assert.Equal(0xFFFFFFFDu, segment->Header.rgFreeMask[0]);
            Assert.Equal(63u, segment->Header.rgFreeCount[Type]);
            Assert.Equal((byte)Type, segment->Header.rgBlockType[0]);
        }
        finally
        {
            HandleTableCore.SegmentFree(segment);
        }
    }

    [Fact]
    public void SegmentFreeHandlesStopsAtTheNextSegment()
    {
        const uint Type = 11;

        TableSegment* firstSegment = HandleTableCore.SegmentAlloc(null);
        TableSegment* secondSegment = HandleTableCore.SegmentAlloc(null);
        Assert.True(firstSegment != null);
        Assert.True(secondSegment != null);

        try
        {
            Assert.Equal(0u, HandleTableCore.SegmentInsertBlockFromFreeListWorker(firstSegment, Type, false));
            Assert.Equal(0u, HandleTableCore.SegmentInsertBlockFromFreeListWorker(secondSegment, Type, false));

            OBJECTHANDLE* handles = stackalloc OBJECTHANDLE[2];
            Assert.Equal(1u, HandleTableCore.BlockAllocHandlesInitial(firstSegment, Type, 0, handles, 1));
            Assert.Equal(1u, HandleTableCore.BlockAllocHandlesInitial(secondSegment, Type, 0, handles + 1, 1));
            firstSegment->Header.rgFreeCount[Type]--;
            secondSegment->Header.rgFreeCount[Type]--;

            Assert.Equal(1u, HandleTableCore.SegmentFreeHandles(firstSegment, Type, handles, 2));

            Assert.Equal(HandleTableConstants.TYPE_INVALID, firstSegment->Header.rgBlockType[0]);
            Assert.Equal(0xFFFFFFFEu, secondSegment->Header.rgFreeMask[0]);
            Assert.Equal(63u, secondSegment->Header.rgFreeCount[Type]);
            Assert.Equal((byte)Type, secondSegment->Header.rgBlockType[0]);
        }
        finally
        {
            HandleTableCore.SegmentFree(firstSegment);
            HandleTableCore.SegmentFree(secondSegment);
        }
    }

    [Fact]
    public void SegmentFreeHandlesDefersLockedEmptyBlocksForScavenging()
    {
        const uint Type = 12;

        TableSegment* segment = HandleTableCore.SegmentAlloc(null);
        Assert.True(segment != null);

        try
        {
            Assert.Equal(0u, HandleTableCore.SegmentInsertBlockFromFreeListWorker(segment, Type, false));
            Assert.Equal(1u, HandleTableCore.SegmentInsertBlockFromFreeListWorker(segment, Type, false));
            HandleTableCore.BlockLock(segment, 0);

            OBJECTHANDLE* handles = stackalloc OBJECTHANDLE[2];
            Assert.Equal(1u, HandleTableCore.BlockAllocHandlesInitial(segment, Type, 0, handles, 1));
            Assert.Equal(1u, HandleTableCore.BlockAllocHandlesInitial(segment, Type, 1, handles + 1, 1));
            segment->Header.rgFreeCount[Type] -= 2;

            Assert.Equal(2u, HandleTableCore.SegmentFreeHandles(segment, Type, handles, 2));

            Assert.Equal((byte)Type, segment->Header.rgBlockType[0]);
            Assert.Equal(HandleTableConstants.TYPE_INVALID, segment->Header.rgBlockType[1]);
            Assert.Equal((byte)0, segment->Header.rgAllocation[0]);
            Assert.Equal((byte)1, segment->Header.bFreeList);
            Assert.Equal(64u, segment->Header.rgFreeCount[Type]);
            Assert.True(segment->Header.fResortChains);
            Assert.True(segment->Header.fNeedsScavenging);
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
