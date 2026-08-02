// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from the segment lifecycle and handle-to-segment helpers of
// src/coreclr/gc/handletablecore.cpp.

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe class HandleTableCore
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static _TableSegmentHeader* HandleFetchSegmentPointer(OBJECTHANDLE handle)
        {
            _TableSegmentHeader* pSegment = (_TableSegmentHeader*)((nuint)handle.Value & unchecked((nuint)HandleTableConstants.HANDLE_SEGMENT_ALIGN_MASK));

            Debug.Assert(pSegment != null);

            return pSegment;
        }

        public static bool SegmentInitialize(TableSegment* pSegment, HandleTable* pTable)
        {
            nuint pageSize = GCToOSInterface.GetPageSize();

            // We want to commit enough for the header PLUS some handles.
            nuint dwCommit = GetInitialCommitSize(pageSize);

            if (!GCToOSInterface.VirtualCommit(pSegment, dwCommit))
            {
                return false;
            }

            _TableSegmentHeader* pHeader = (_TableSegmentHeader*)pSegment;
            pHeader->bCommitLine = GetInitialCommitLine(dwCommit);

            Unsafe.InitBlockUnaligned(pHeader->rgGeneration, 0xFF, (uint)(HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT * sizeof(uint)));
            Unsafe.InitBlockUnaligned(pHeader->rgTail, HandleTableConstants.BLOCK_INVALID, (uint)HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES);
            Unsafe.InitBlockUnaligned(pHeader->rgHint, HandleTableConstants.BLOCK_INVALID, (uint)HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES);
            Unsafe.InitBlockUnaligned(pHeader->rgFreeMask, 0xFF, (uint)(HandleTableConstants.HANDLE_MASKS_PER_SEGMENT * sizeof(uint)));
            Unsafe.InitBlockUnaligned(pHeader->rgBlockType, HandleTableConstants.TYPE_INVALID, (uint)HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT);
            Unsafe.InitBlockUnaligned(pHeader->rgUserData, HandleTableConstants.BLOCK_INVALID, (uint)HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT);

            Debug.Assert(HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT <= byte.MaxValue);
            byte u = 0;
            while (u < HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT - 1)
            {
                byte next = (byte)(u + 1);
                pHeader->rgAllocation[u] = next;
                u = next;
            }

            pHeader->rgAllocation[u] = HandleTableConstants.BLOCK_INVALID;
            pHeader->pHandleTable = pTable;

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static nuint GetInitialCommitSize(nuint pageSize) =>
            GCEnv.ALIGN_UP((nuint)HandleTableConstants.HANDLE_HEADER_SIZE, pageSize);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static byte GetInitialCommitLine(nuint commitSize) =>
            (byte)((commitSize - HandleTableConstants.HANDLE_HEADER_SIZE) / HandleTableConstants.HANDLE_BYTES_PER_BLOCK);

        public static void SegmentFree(TableSegment* pSegment)
        {
            GCToOSInterface.VirtualRelease(pSegment, HandleTableConstants.HANDLE_SEGMENT_SIZE);
        }

        public static TableSegment* SegmentAlloc(HandleTable* pTable)
        {
            Debug.Assert(HandleTableConstants.HANDLE_SEGMENT_ALIGNMENT >= HandleTableConstants.HANDLE_SEGMENT_SIZE);
            Debug.Assert(HandleTableConstants.HANDLE_SEGMENT_ALIGNMENT == 0x10000);

            TableSegment* pSegment = (TableSegment*)GCToOSInterface.VirtualReserve(
                HandleTableConstants.HANDLE_SEGMENT_SIZE,
                HandleTableConstants.HANDLE_SEGMENT_ALIGNMENT,
                (uint)VirtualReserveFlags.None);

            Debug.Assert((nuint)pSegment % HandleTableConstants.HANDLE_SEGMENT_ALIGNMENT == 0);

            if (pSegment == null)
            {
                return null;
            }

            if (!SegmentInitialize(pSegment, pTable))
            {
                SegmentFree(pSegment);
                pSegment = null;
            }

            return pSegment;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool BlockIsLocked(TableSegment* pSegment, uint uBlock)
        {
            Debug.Assert(uBlock < HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT);

            return pSegment->Header.rgLocks[uBlock] != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void BlockLock(TableSegment* pSegment, uint uBlock)
        {
            byte bLocks = pSegment->Header.rgLocks[uBlock];

            Debug.Assert(bLocks < byte.MaxValue);

            pSegment->Header.rgLocks[uBlock] = (byte)(bLocks + 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void BlockUnlock(TableSegment* pSegment, uint uBlock)
        {
            byte bLocks = pSegment->Header.rgLocks[uBlock];

            Debug.Assert(bLocks > 0);

            pSegment->Header.rgLocks[uBlock] = (byte)(bLocks - 1);
        }

        public static uint SegmentInsertBlockFromFreeListWorker(TableSegment* pSegment, uint uType, bool fUpdateHint)
        {
            byte uBlock = pSegment->Header.bFreeList;

            if (uBlock != HandleTableConstants.BLOCK_INVALID)
            {
                if (uBlock >= pSegment->Header.bEmptyLine)
                {
                    uint uCommitLine = pSegment->Header.bCommitLine;

                    if (uBlock >= uCommitLine)
                    {
                        void* pvCommit = &pSegment->rgValue[uCommitLine * HandleTableConstants.HANDLE_HANDLES_PER_BLOCK];
                        nuint dwCommit = GCToOSInterface.GetPageSize();

                        if (!GCToOSInterface.VirtualCommit(pvCommit, dwCommit))
                        {
                            return HandleTableConstants.BLOCK_INVALID;
                        }

                        pSegment->Header.bDecommitLine = (byte)uCommitLine;
                        pSegment->Header.bCommitLine = (byte)(uCommitLine + (dwCommit / HandleTableConstants.HANDLE_BYTES_PER_BLOCK));
                    }

                    pSegment->Header.bEmptyLine = (byte)(uBlock + 1);
                }

                pSegment->Header.bFreeList = pSegment->Header.rgAllocation[uBlock];

                uint uOldTail = pSegment->Header.rgTail[uType];
                if (uOldTail == HandleTableConstants.BLOCK_INVALID)
                {
                    pSegment->Header.rgAllocation[uBlock] = uBlock;
                    fUpdateHint = true;
                }
                else
                {
                    pSegment->Header.rgAllocation[uBlock] = pSegment->Header.rgAllocation[uOldTail];
                    pSegment->Header.rgAllocation[uOldTail] = uBlock;
                    pSegment->Header.fResortChains = true;
                }

                pSegment->Header.rgBlockType[uBlock] = (byte)uType;
                pSegment->Header.rgTail[uType] = uBlock;

                if (fUpdateHint)
                {
                    pSegment->Header.rgHint[uType] = uBlock;
                }

                pSegment->Header.rgFreeCount[uType] += HandleTableConstants.HANDLE_HANDLES_PER_BLOCK;
            }

            return uBlock;
        }

        public static uint BlockAllocHandlesInMask(
            TableSegment* pSegment,
            uint uBlock,
            uint* pdwMask,
            uint uHandleMaskDisplacement,
            OBJECTHANDLE* pHandleBase,
            uint uCount)
        {
            uint uRemain = uCount;
            uint dwFree = *pdwMask;
            uint uByteDisplacement = 0;

            do
            {
                uint dwLowByte = dwFree & HandleTableConstants.MASK_LOBYTE;

                if (dwLowByte != 0)
                {
                    uint dwAlloc = 0;

                    do
                    {
                        // Allocation-free equivalent of the native c_rgLowBitIndex lookup.
                        uint uIndex = uint.TrailingZeroCount(dwLowByte);

                        dwAlloc |= 1u << (int)uIndex;
                        dwLowByte &= ~dwAlloc;
                        uIndex += uHandleMaskDisplacement + uByteDisplacement;

                        *pHandleBase = new OBJECTHANDLE(&pSegment->rgValue[uIndex]);

                        uRemain--;
                        pHandleBase++;
                    }
                    while (dwLowByte != 0 && uRemain != 0);

                    dwAlloc <<= (int)uByteDisplacement;
                    *pdwMask &= ~dwAlloc;
                }

                dwFree >>= HandleTableConstants.BITS_PER_BYTE;
                uByteDisplacement += HandleTableConstants.BITS_PER_BYTE;
            }
            while (uRemain != 0 && dwFree != 0);

            return uCount - uRemain;
        }

        public static uint BlockAllocHandlesInitial(
            TableSegment* pSegment,
            uint uType,
            uint uBlock,
            OBJECTHANDLE* pHandleBase,
            uint uCount)
        {
            Debug.Assert(uCount != 0);

            if (uCount > HandleTableConstants.HANDLE_HANDLES_PER_BLOCK)
            {
                Debug.Assert(false);
                uCount = HandleTableConstants.HANDLE_HANDLES_PER_BLOCK;
            }

            uint uRemain = uCount;
            uint* pdwMask = pSegment->Header.rgFreeMask + (uBlock * HandleTableConstants.HANDLE_MASKS_PER_BLOCK);

            do
            {
                Debug.Assert(*pdwMask == HandleTableConstants.MASK_EMPTY);

                uint uAlloc = uRemain;
                uint dwNewMask;
                if (uAlloc >= HandleTableConstants.HANDLE_HANDLES_PER_MASK)
                {
                    dwNewMask = HandleTableConstants.MASK_FULL;
                    uAlloc = HandleTableConstants.HANDLE_HANDLES_PER_MASK;
                }
                else
                {
                    dwNewMask = HandleTableConstants.MASK_EMPTY << (int)uAlloc;
                }

                *pdwMask = dwNewMask;
                uRemain -= uAlloc;
                pdwMask++;
            }
            while (uRemain != 0);

            byte* pValue = (byte*)&pSegment->rgValue[uBlock * HandleTableConstants.HANDLE_HANDLES_PER_BLOCK];
            byte* pLast = pValue + (uCount * HandleTableConstants.HANDLE_SIZE);

            do
            {
                *pHandleBase = new OBJECTHANDLE(pValue);

                pValue += HandleTableConstants.HANDLE_SIZE;
                pHandleBase++;
            }
            while (pValue < pLast);

            return uCount;
        }

        public static uint BlockAllocHandles(
            TableSegment* pSegment,
            uint uBlock,
            OBJECTHANDLE* pHandleBase,
            uint uCount)
        {
            uint uRemain = uCount;
            uint* pdwMask = pSegment->Header.rgFreeMask + (uBlock * HandleTableConstants.HANDLE_MASKS_PER_BLOCK);
            uint* pdwMaskLast = pdwMask + HandleTableConstants.HANDLE_MASKS_PER_BLOCK;
            uint uDisplacement = uBlock * HandleTableConstants.HANDLE_HANDLES_PER_BLOCK;

            do
            {
                if (*pdwMask != 0)
                {
                    uint uSatisfied = BlockAllocHandlesInMask(
                        pSegment,
                        uBlock,
                        pdwMask,
                        uDisplacement,
                        pHandleBase,
                        uRemain);

                    uRemain -= uSatisfied;
                    pHandleBase += uSatisfied;

                    if (uRemain == 0)
                    {
                        break;
                    }
                }

                pdwMask++;
                uDisplacement += HandleTableConstants.HANDLE_HANDLES_PER_MASK;
            }
            while (pdwMask < pdwMaskLast);

            return uCount - uRemain;
        }

        public static uint SegmentAllocHandlesFromTypeChain(
            TableSegment* pSegment,
            uint uType,
            OBJECTHANDLE* pHandleBase,
            uint uCount)
        {
            uint uAvail = pSegment->Header.rgFreeCount[uType];

            if (uAvail > uCount)
            {
                uAvail = uCount;
            }
            else
            {
                uCount = uAvail;
            }

            if (uAvail != 0)
            {
                uint uBlock = pSegment->Header.rgHint[uType];
                uint uLast = uBlock;

                for (;;)
                {
                    uint uSatisfied = BlockAllocHandles(pSegment, uBlock, pHandleBase, uAvail);

                    if (uSatisfied == uAvail)
                    {
                        pSegment->Header.rgHint[uType] = (byte)uBlock;
                        break;
                    }

                    uAvail -= uSatisfied;
                    pHandleBase += uSatisfied;
                    uBlock = pSegment->Header.rgAllocation[uBlock];

                    if (uBlock == uLast)
                    {
                        Debug.Assert(false);
                        uCount -= uAvail;
                        break;
                    }
                }

                pSegment->Header.rgFreeCount[uType] -= uCount;
            }

            return uCount;
        }
    }
}
