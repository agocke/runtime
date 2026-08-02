// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from the segment lifecycle and handle-to-segment helpers of
// src/coreclr/gc/handletablecore.cpp and HndIsNullOrDestroyedHandle of
// src/coreclr/gc/handletable.h.

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe class HandleTableCore
    {
        public static void QuickSort(nuint* pData, int left, int right, delegate*<nuint, nuint, int> pfnCompare)
        {
            do
            {
                int i = left;
                int j = right;

                nuint x = pData[(i + j + 1) / 2];

                do
                {
                    while (pfnCompare(pData[i], x) < 0)
                    {
                        i++;
                    }

                    while (pfnCompare(x, pData[j]) < 0)
                    {
                        j--;
                    }

                    if (i > j)
                    {
                        break;
                    }

                    if (i < j)
                    {
                        nuint t = pData[i];
                        pData[i] = pData[j];
                        pData[j] = t;
                    }

                    i++;
                    j--;
                }
                while (i <= j);

                if ((j - left) <= (right - i))
                {
                    if (left < j)
                    {
                        QuickSort(pData, left, j, pfnCompare);
                    }

                    left = i;
                }
                else
                {
                    if (i < right)
                    {
                        QuickSort(pData, i, right, pfnCompare);
                    }

                    right = j;
                }
            }
            while (left < right);
        }

        public static int CompareHandlesByFreeOrder(nuint p, nuint q)
        {
            TableSegment* pSegmentP = (TableSegment*)(p & unchecked((nuint)HandleTableConstants.HANDLE_SEGMENT_ALIGN_MASK));
            TableSegment* pSegmentQ = (TableSegment*)(q & unchecked((nuint)HandleTableConstants.HANDLE_SEGMENT_ALIGN_MASK));

            if (pSegmentP == pSegmentQ)
            {
                return (int)((nint)q - (nint)p);
            }
            else if (pSegmentP != null)
            {
                if (pSegmentQ != null)
                {
                    return pSegmentQ->Header.bSequence - pSegmentP->Header.bSequence;
                }

                return 1;
            }
            else if (pSegmentQ != null)
            {
                return -1;
            }

            return 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static _TableSegmentHeader* HandleFetchSegmentPointer(OBJECTHANDLE handle)
        {
            _TableSegmentHeader* pSegment = (_TableSegmentHeader*)((nuint)handle.Value & unchecked((nuint)HandleTableConstants.HANDLE_SEGMENT_ALIGN_MASK));

            Debug.Assert(pSegment != null);

            return pSegment;
        }

        private static bool HndIsNullOrDestroyedHandle(nuint value)
        {
#if DEBUG
            if (value == 0x7)
            {
                return true;
            }
#endif

            return value == 0;
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TypeHasUserData(HandleTable* pTable, uint uType)
        {
            Debug.Assert(uType < HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES);

            return (pTable->rgTypeFlags[uType] & HandleTableConstants.HNDF_EXTRAINFO) != 0;
        }

        public static nuint* HandleValidateAndFetchUserDataPointer(OBJECTHANDLE handle, uint uTypeExpected)
        {
            _TableSegmentHeader* pSegment = HandleFetchSegmentPointer(handle);
            nuint offset = (nuint)handle.Value & HandleTableConstants.HANDLE_SEGMENT_CONTENT_MASK;

            Debug.Assert(offset >= HandleTableConstants.HANDLE_HEADER_SIZE);

            uint uHandle = (uint)((offset - HandleTableConstants.HANDLE_HEADER_SIZE) / HandleTableConstants.HANDLE_SIZE);
            uint uBlock = uHandle / HandleTableConstants.HANDLE_HANDLES_PER_BLOCK;
            nuint* pUserData = BlockFetchUserDataPointer(pSegment, uBlock, true);

            if (pUserData != null)
            {
                pUserData += uHandle - (uBlock * HandleTableConstants.HANDLE_HANDLES_PER_BLOCK);

                if (pSegment->rgBlockType[uBlock] != uTypeExpected)
                {
                    Debug.Assert(false);
                    pUserData = null;
                }
            }

            return pUserData;
        }

        public static nuint* HandleQuickFetchUserDataPointer(OBJECTHANDLE handle)
        {
            _TableSegmentHeader* pSegment = HandleFetchSegmentPointer(handle);
            nuint offset = (nuint)handle.Value & HandleTableConstants.HANDLE_SEGMENT_CONTENT_MASK;

            Debug.Assert(offset >= HandleTableConstants.HANDLE_HEADER_SIZE);

            uint uHandle = (uint)((offset - HandleTableConstants.HANDLE_HEADER_SIZE) / HandleTableConstants.HANDLE_SIZE);
            uint uBlock = uHandle / HandleTableConstants.HANDLE_HANDLES_PER_BLOCK;
            nuint* pUserData = BlockFetchUserDataPointer(pSegment, uBlock, true);

            if (pUserData != null)
            {
                pUserData += uHandle - (uBlock * HandleTableConstants.HANDLE_HANDLES_PER_BLOCK);
            }

            return pUserData;
        }

        public static void HandleQuickSetUserData(OBJECTHANDLE handle, nuint lUserData)
        {
            nuint* pUserData = HandleQuickFetchUserDataPointer(handle);

            if (pUserData != null)
            {
                *pUserData = lUserData;
            }
        }

        public static uint HandleFetchType(OBJECTHANDLE handle)
        {
            _TableSegmentHeader* pSegment = HandleFetchSegmentPointer(handle);
            nuint offset = (nuint)handle.Value & HandleTableConstants.HANDLE_SEGMENT_CONTENT_MASK;

            Debug.Assert(offset >= HandleTableConstants.HANDLE_HEADER_SIZE);

            uint uHandle = (uint)((offset - HandleTableConstants.HANDLE_HEADER_SIZE) / HandleTableConstants.HANDLE_SIZE);
            uint uBlock = uHandle / HandleTableConstants.HANDLE_HANDLES_PER_BLOCK;

            return pSegment->rgBlockType[uBlock];
        }

        public static HandleTable* HandleFetchHandleTable(OBJECTHANDLE handle)
        {
            _TableSegmentHeader* pSegment = HandleFetchSegmentPointer(handle);

            return pSegment->pHandleTable;
        }

        public static bool TableContainHandle(HandleTable* pTable, OBJECTHANDLE handle)
        {
            Debug.Assert(!handle.IsNull);

            TableSegment* pSegment = (TableSegment*)HandleFetchSegmentPointer(handle);

            using (new HandleTableCrstHolder(&pTable->Lock))
            {
                TableSegment* pWorkerSegment = pTable->pSegmentList;
                while (pWorkerSegment != null)
                {
                    if (pWorkerSegment == pSegment)
                    {
                        return true;
                    }

                    pWorkerSegment = pWorkerSegment->Header.pNextSegment;
                }
            }

            return false;
        }

        public static uint SegmentInsertBlockFromFreeList(TableSegment* pSegment, uint uType, bool fUpdateHint)
        {
            uint uBlock;
            uint uData = 0;
            bool fUserData = TypeHasUserData(pSegment->Header.pHandleTable, uType);

            if (fUserData)
            {
                uBlock = pSegment->Header.bFreeList;
                if (uBlock == HandleTableConstants.BLOCK_INVALID
                    || pSegment->Header.rgAllocation[uBlock] == HandleTableConstants.BLOCK_INVALID)
                {
                    return HandleTableConstants.BLOCK_INVALID;
                }

                uData = SegmentInsertBlockFromFreeListWorker(
                    pSegment,
                    HandleTableConstants.HNDTYPE_INTERNAL_DATABLOCK,
                    false);
            }

            uBlock = SegmentInsertBlockFromFreeListWorker(pSegment, uType, fUpdateHint);

            if (fUserData)
            {
                if (uBlock != HandleTableConstants.BLOCK_INVALID && uData != HandleTableConstants.BLOCK_INVALID)
                {
                    pSegment->Header.rgUserData[uBlock] = (byte)uData;
                    BlockLock(pSegment, uData);
                }
                else
                {
                    if (uBlock != HandleTableConstants.BLOCK_INVALID)
                    {
                        SegmentRemoveFreeBlocks(pSegment, uType, null);
                    }

                    if (uData != HandleTableConstants.BLOCK_INVALID)
                    {
                        SegmentRemoveFreeBlocks(pSegment, HandleTableConstants.HNDTYPE_INTERNAL_DATABLOCK, null);
                    }

                    uBlock = HandleTableConstants.BLOCK_INVALID;
                }
            }

            return uBlock;
        }

        public static void SegmentRemoveFreeBlocks(TableSegment* pSegment, uint uType, bool* pfScavengeLater)
        {
            uint uPrev = pSegment->Header.rgTail[uType];

            if (uPrev == HandleTableConstants.BLOCK_INVALID)
            {
                return;
            }

            bool fCleanupUserData = false;
            uint uStart = pSegment->Header.rgAllocation[uPrev];
            uint uBlock = uStart;
            uint uRemoved = 0;
            uint uFirstFreed = HandleTableConstants.BLOCK_INVALID;
            uint uLastFreed = HandleTableConstants.BLOCK_INVALID;

            for (;;)
            {
                uint uNext = pSegment->Header.rgAllocation[uBlock];

                Debug.Assert(HandleTableConstants.HANDLE_MASKS_PER_BLOCK == 2);
                if (*((ulong*)(pSegment->Header.rgFreeMask + (uBlock * HandleTableConstants.HANDLE_MASKS_PER_BLOCK))) == ulong.MaxValue)
                {
                    if (BlockIsLocked(pSegment, uBlock))
                    {
                        if (pfScavengeLater != null)
                        {
                            *pfScavengeLater = true;
                        }
                    }
                    else
                    {
                        uint uData = pSegment->Header.rgUserData[uBlock];
                        if (uData != HandleTableConstants.BLOCK_INVALID)
                        {
                            BlockUnlock(pSegment, uData);
                            pSegment->Header.rgUserData[uBlock] = HandleTableConstants.BLOCK_INVALID;
                            fCleanupUserData = true;
                        }

                        pSegment->Header.rgBlockType[uBlock] = HandleTableConstants.TYPE_INVALID;

                        if (uFirstFreed == HandleTableConstants.BLOCK_INVALID)
                        {
                            uFirstFreed = uBlock;
                        }
                        else
                        {
                            pSegment->Header.rgAllocation[uLastFreed] = (byte)uBlock;
                        }

                        uLastFreed = uBlock;

                        if (uPrev != uBlock)
                        {
                            pSegment->Header.rgAllocation[uPrev] = (byte)uNext;

                            if (pSegment->Header.rgTail[uType] == uBlock)
                            {
                                pSegment->Header.rgTail[uType] = (byte)uPrev;
                            }

                            if (pSegment->Header.rgHint[uType] == uBlock)
                            {
                                pSegment->Header.rgHint[uType] = (byte)uNext;
                            }

                            uBlock = uPrev;
                        }
                        else
                        {
                            Debug.Assert(uNext == uStart);

                            pSegment->Header.rgAllocation[uBlock] = HandleTableConstants.BLOCK_INVALID;
                            pSegment->Header.rgTail[uType] = HandleTableConstants.BLOCK_INVALID;
                            pSegment->Header.rgHint[uType] = HandleTableConstants.BLOCK_INVALID;
                        }

                        uRemoved++;
                    }
                }

                if (uNext == uStart)
                {
                    break;
                }

                if (uStart == uLastFreed)
                {
                    uStart = uNext;
                }

                uPrev = uBlock;
                uBlock = uNext;
            }

            if (uRemoved != 0)
            {
                pSegment->Header.rgAllocation[uLastFreed] = pSegment->Header.bFreeList;
                pSegment->Header.bFreeList = (byte)uFirstFreed;
                pSegment->Header.rgFreeCount[uType] -= uRemoved * HandleTableConstants.HANDLE_HANDLES_PER_BLOCK;
                pSegment->Header.fResortChains = true;

                if (fCleanupUserData)
                {
                    SegmentRemoveFreeBlocks(pSegment, HandleTableConstants.HNDTYPE_INTERNAL_DATABLOCK, null);
                }
            }
        }

        public static void SegmentResortChains(TableSegment* pSegment)
        {
            pSegment->Header.fResortChains = false;
            bool fScavengingOccurred = false;
            uint uType;

            if (pSegment->Header.fNeedsScavenging)
            {
                pSegment->Header.fNeedsScavenging = false;
                fScavengingOccurred = true;
                bool fCleanupUserData = false;
                uint uLast = pSegment->Header.bEmptyLine;

                for (uint uBlock = 0; uBlock < uLast; uBlock++)
                {
                    uType = pSegment->Header.rgBlockType[uBlock];

                    if (uType < HandleTableConstants.HANDLE_MAX_PUBLIC_TYPES)
                    {
                        Debug.Assert(HandleTableConstants.HANDLE_MASKS_PER_BLOCK == 2);
                        if (*((ulong*)(pSegment->Header.rgFreeMask + (uBlock * HandleTableConstants.HANDLE_MASKS_PER_BLOCK))) == ulong.MaxValue)
                        {
                            if (!BlockIsLocked(pSegment, uBlock))
                            {
                                uint uData = pSegment->Header.rgUserData[uBlock];
                                if (uData != HandleTableConstants.BLOCK_INVALID)
                                {
                                    BlockUnlock(pSegment, uData);
                                    pSegment->Header.rgUserData[uBlock] = HandleTableConstants.BLOCK_INVALID;
                                    fCleanupUserData = true;
                                }

                                pSegment->Header.rgBlockType[uBlock] = HandleTableConstants.TYPE_INVALID;
                                pSegment->Header.rgFreeCount[uType] -= HandleTableConstants.HANDLE_HANDLES_PER_BLOCK;
                            }
                        }
                    }
                }

                if (fCleanupUserData)
                {
                    SegmentRemoveFreeBlocks(pSegment, HandleTableConstants.HNDTYPE_INTERNAL_DATABLOCK, null);
                }
            }

            byte* rgChainCurr = stackalloc byte[HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES];
            byte* rgChainHigh = stackalloc byte[HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES];
            byte bChainFree = HandleTableConstants.BLOCK_INVALID;
            uint uEmptyLine = HandleTableConstants.BLOCK_INVALID;
            bool fContiguousWithFreeList = true;

            for (uType = 0; uType < HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES; uType++)
            {
                rgChainHigh[uType] = rgChainCurr[uType] = HandleTableConstants.BLOCK_INVALID;
            }

            byte uBlockIndex = HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT;
            while (uBlockIndex > 0)
            {
                uBlockIndex--;
                uType = pSegment->Header.rgBlockType[uBlockIndex];

                if (uType != HandleTableConstants.TYPE_INVALID)
                {
                    fContiguousWithFreeList = false;
                    Debug.Assert(uType < HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES);

                    if (rgChainHigh[uType] == HandleTableConstants.BLOCK_INVALID)
                    {
                        rgChainHigh[uType] = uBlockIndex;
                    }

                    pSegment->Header.rgAllocation[uBlockIndex] = rgChainCurr[uType];
                    rgChainCurr[uType] = uBlockIndex;
                }
                else
                {
                    if (fContiguousWithFreeList)
                    {
                        uEmptyLine = uBlockIndex;
                    }

                    pSegment->Header.rgAllocation[uBlockIndex] = bChainFree;
                    bChainFree = uBlockIndex;
                }
            }

            for (uType = 0; uType < HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES; uType++)
            {
                byte bBlock = rgChainCurr[uType];

                if (bBlock != HandleTableConstants.BLOCK_INVALID)
                {
                    uint uTail = rgChainHigh[uType];
                    pSegment->Header.rgTail[uType] = (byte)uTail;
                    pSegment->Header.rgAllocation[uTail] = bBlock;

                    if (pSegment->Header.rgBlockType[pSegment->Header.rgHint[uType]] != uType)
                    {
                        pSegment->Header.rgHint[uType] = bBlock;
                    }
                }
                else if (pSegment->Header.rgTail[uType] != HandleTableConstants.BLOCK_INVALID)
                {
                    Debug.Assert(fScavengingOccurred);
                    pSegment->Header.rgTail[uType] = HandleTableConstants.BLOCK_INVALID;
                    pSegment->Header.rgHint[uType] = HandleTableConstants.BLOCK_INVALID;
                }
            }

            pSegment->Header.bFreeList = bChainFree;

            if (uEmptyLine > HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT)
            {
                uEmptyLine = HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT;
            }

            pSegment->Header.bEmptyLine = (byte)uEmptyLine;
        }

        public static bool DoesSegmentNeedsToTrimExcessPages(TableSegment* pSegment)
        {
            uint uEmptyLine = pSegment->Header.bEmptyLine;
            uint uDecommitLine = pSegment->Header.bDecommitLine;

            if (uEmptyLine < uDecommitLine)
            {
                nuint dwPageRound = GCToOSInterface.GetPageSize() - 1;
                nuint dwPageMask = ~dwPageRound;
                nuint dwLo = (nuint)(void*)&pSegment->rgValue[uEmptyLine * HandleTableConstants.HANDLE_HANDLES_PER_BLOCK];
                dwLo = (dwLo + dwPageRound) & dwPageMask;
                nuint dwHi = (nuint)(void*)&pSegment->rgValue[pSegment->Header.bCommitLine * HandleTableConstants.HANDLE_HANDLES_PER_BLOCK];

                if (dwHi > dwLo)
                {
                    return true;
                }
            }

            return false;
        }

        public static void SegmentTrimExcessPages(TableSegment* pSegment)
        {
            uint uEmptyLine = pSegment->Header.bEmptyLine;
            uint uDecommitLine = pSegment->Header.bDecommitLine;

            if (uEmptyLine < uDecommitLine)
            {
                nuint pageSize = GCToOSInterface.GetPageSize();
                nuint dwPageRound = pageSize - 1;
                nuint dwPageMask = ~dwPageRound;
                nuint rgValue = (nuint)(void*)&pSegment->rgValue[0];
                nuint dwLo = rgValue + ((nuint)uEmptyLine * HandleTableConstants.HANDLE_BYTES_PER_BLOCK);
                dwLo = (dwLo + dwPageRound) & dwPageMask;
                nuint dwHi = rgValue + ((nuint)pSegment->Header.bCommitLine * HandleTableConstants.HANDLE_BYTES_PER_BLOCK);

                if (dwHi > dwLo)
                {
                    GCToOSInterface.VirtualDecommit((void*)dwLo, dwHi - dwLo);
                    pSegment->Header.bCommitLine = (byte)((dwLo - rgValue) / HandleTableConstants.HANDLE_BYTES_PER_BLOCK);

                    nuint dwDecommitAddr = dwLo - pageSize;
                    uDecommitLine = 0;

                    if (dwDecommitAddr > rgValue)
                    {
                        uDecommitLine = (uint)((dwDecommitAddr - rgValue) / HandleTableConstants.HANDLE_BYTES_PER_BLOCK);
                    }

                    pSegment->Header.bDecommitLine = (byte)uDecommitLine;
                }
            }
        }

        public static nuint* BlockFetchUserDataPointer(_TableSegmentHeader* pSegment, uint uBlock, bool fAssertOnError)
        {
            nuint* pUserData = null;
            uint blockIndex = pSegment->rgUserData[uBlock];

            if (blockIndex != HandleTableConstants.BLOCK_INVALID)
            {
                pUserData = (nuint*)&((TableSegment*)pSegment)->rgValue[blockIndex * HandleTableConstants.HANDLE_HANDLES_PER_BLOCK];
            }
            else if (fAssertOnError)
            {
                Debug.Assert(false);
            }

            return pUserData;
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

        public static uint SegmentAllocHandlesFromFreeList(
            TableSegment* pSegment,
            uint uType,
            OBJECTHANDLE* pHandleBase,
            uint uCount)
        {
            uint uRemain = uCount;

            do
            {
                uint uAlloc = uRemain;

                if (uAlloc > HandleTableConstants.HANDLE_HANDLES_PER_BLOCK)
                {
                    uAlloc = HandleTableConstants.HANDLE_HANDLES_PER_BLOCK;
                }

                uint uBlock = SegmentInsertBlockFromFreeList(pSegment, uType, uRemain == uCount);

                if (uBlock == HandleTableConstants.BLOCK_INVALID)
                {
                    break;
                }

                uAlloc = BlockAllocHandlesInitial(pSegment, uType, uBlock, pHandleBase, uAlloc);
                uRemain -= uAlloc;
                pHandleBase += uAlloc;
            }
            while (uRemain != 0);

            uCount -= uRemain;
            pSegment->Header.rgFreeCount[uType] -= uCount;

            return uCount;
        }

        public static uint SegmentAllocHandles(
            TableSegment* pSegment,
            uint uType,
            OBJECTHANDLE* pHandleBase,
            uint uCount)
        {
            uint uSatisfied = SegmentAllocHandlesFromTypeChain(pSegment, uType, pHandleBase, uCount);

            if (uSatisfied < uCount)
            {
                uCount -= uSatisfied;
                pHandleBase += uSatisfied;
                uSatisfied += SegmentAllocHandlesFromFreeList(pSegment, uType, pHandleBase, uCount);
            }

            return uSatisfied;
        }

        public static uint TableAllocBulkHandles(
            HandleTable* pTable,
            uint uType,
            OBJECTHANDLE* pHandleBase,
            uint uCount)
        {
            uint uRemain = uCount;
            TableSegment* pSegment = pTable->pSegmentList;
            byte bLastSequence = 0;
            bool fNewSegment = false;

            for (;;)
            {
                uint uSatisfied = SegmentAllocHandles(pSegment, uType, pHandleBase, uRemain);

                uRemain -= uSatisfied;
                pHandleBase += uSatisfied;

                if (uRemain == 0)
                {
                    break;
                }

                TableSegment* pNextSegment = null;

                if (!fNewSegment)
                {
                    pNextSegment = pSegment->Header.pNextSegment;
                    if (pNextSegment == null)
                    {
                        bLastSequence = pSegment->Header.bSequence;
                        fNewSegment = true;
                    }
                }

                if (fNewSegment)
                {
                    pNextSegment = SegmentAlloc(pTable);
                    if (pNextSegment == null)
                    {
                        break;
                    }

                    pNextSegment->Header.bSequence = unchecked((byte)(bLastSequence + 1));
                    bLastSequence = pNextSegment->Header.bSequence;

                    TableSegment* pWalk = pTable->pSegmentList;
                    if ((nuint)pNextSegment < (nuint)pWalk)
                    {
                        pNextSegment->Header.pNextSegment = pWalk;
                        pTable->pSegmentList = pNextSegment;
                    }
                    else
                    {
                        while (pWalk != null)
                        {
                            if (pWalk->Header.pNextSegment == null)
                            {
                                pWalk->Header.pNextSegment = pNextSegment;
                                break;
                            }
                            else if ((nuint)pWalk->Header.pNextSegment > (nuint)pNextSegment)
                            {
                                pNextSegment->Header.pNextSegment = pWalk->Header.pNextSegment;
                                pWalk->Header.pNextSegment = pNextSegment;
                                break;
                            }

                            pWalk = pWalk->Header.pNextSegment;
                        }
                    }
                }

                pSegment = pNextSegment;
            }

            uint uAllocated = uCount - uRemain;
            pTable->dwCount += uAllocated;

            return uAllocated;
        }

        public static uint BlockFreeHandlesInMask(
            TableSegment* pSegment,
            uint uBlock,
            uint uMask,
            OBJECTHANDLE* pHandleBase,
            uint uCount,
            nuint* pUserData,
            uint* puActualFreed,
            bool* pfAllMasksFree)
        {
            uint uRemain = uCount;

            if (pUserData != null)
            {
                pUserData += uMask * HandleTableConstants.HANDLE_HANDLES_PER_MASK;
            }

            uMask += uBlock * HandleTableConstants.HANDLE_MASKS_PER_BLOCK;

            nuint firstHandle = (nuint)(void*)&pSegment->rgValue[uMask * HandleTableConstants.HANDLE_HANDLES_PER_MASK];
            nuint lastHandle = firstHandle + (HandleTableConstants.HANDLE_HANDLES_PER_MASK * HandleTableConstants.HANDLE_SIZE);
            uint dwFreeMask = pSegment->Header.rgFreeMask[uMask];
            uint uBogus = 0;

            do
            {
                OBJECTHANDLE handle = *pHandleBase;
                nuint handleValue = (nuint)handle.Value;

                if (handleValue < firstHandle || handleValue >= lastHandle)
                {
                    break;
                }

                Debug.Assert(HndIsNullOrDestroyedHandle(*(nuint*)handle.Value));

                uint uHandle = (uint)((handleValue - firstHandle) / HandleTableConstants.HANDLE_SIZE);

                if (pUserData != null)
                {
                    pUserData[uHandle] = 0;
                }

                uint dwFreeBit = 1u << (int)uHandle;

                if ((dwFreeMask & dwFreeBit) != 0)
                {
                    uBogus++;
                    Debug.Assert(false);
                }

                dwFreeMask |= dwFreeBit;
                uRemain--;
                pHandleBase++;
            }
            while (uRemain != 0);

            pSegment->Header.rgFreeMask[uMask] = dwFreeMask;

            if (dwFreeMask != HandleTableConstants.MASK_EMPTY)
            {
                *pfAllMasksFree = false;
            }

            uint uFreed = uCount - uRemain;
            *puActualFreed += uFreed - uBogus;

            return uFreed;
        }

        public static uint BlockFreeHandles(
            TableSegment* pSegment,
            uint uBlock,
            OBJECTHANDLE* pHandleBase,
            uint uCount,
            uint* puActualFreed,
            bool* pfScanForFreeBlocks)
        {
            uint uRemain = uCount;
            nuint* pBlockUserData = BlockFetchUserDataPointer(&pSegment->Header, uBlock, false);
            nuint firstHandle = (nuint)(void*)&pSegment->rgValue[uBlock * HandleTableConstants.HANDLE_HANDLES_PER_BLOCK];
            nuint lastHandle = firstHandle + (HandleTableConstants.HANDLE_HANDLES_PER_BLOCK * HandleTableConstants.HANDLE_SIZE);
            bool fAllMasksWeTouchedAreFree = true;

            do
            {
                OBJECTHANDLE handle = *pHandleBase;
                nuint handleValue = (nuint)handle.Value;

                if (handleValue < firstHandle || handleValue >= lastHandle)
                {
                    break;
                }

                uint uMask = (uint)((handleValue - firstHandle) /
                    (HandleTableConstants.HANDLE_SIZE * HandleTableConstants.HANDLE_HANDLES_PER_MASK));

                uint uFreed = BlockFreeHandlesInMask(
                    pSegment,
                    uBlock,
                    uMask,
                    pHandleBase,
                    uRemain,
                    pBlockUserData,
                    puActualFreed,
                    &fAllMasksWeTouchedAreFree);

                uRemain -= uFreed;
                pHandleBase += uFreed;
            }
            while (uRemain != 0);

            if (fAllMasksWeTouchedAreFree)
            {
                if (!BlockIsLocked(pSegment, uBlock))
                {
                    *pfScanForFreeBlocks = true;
                }
            }

            return uCount - uRemain;
        }

        public static uint SegmentFreeHandles(
            TableSegment* pSegment,
            uint uType,
            OBJECTHANDLE* pHandleBase,
            uint uCount)
        {
            uint uRemain = uCount;
            nuint firstHandle = (nuint)(void*)&pSegment->rgValue[0];
            nuint lastHandle = firstHandle + (HandleTableConstants.HANDLE_HANDLES_PER_SEGMENT * HandleTableConstants.HANDLE_SIZE);
            bool fScanForFreeBlocks = false;
            uint uActualFreed = 0;

            do
            {
                OBJECTHANDLE handle = *pHandleBase;
                nuint handleValue = (nuint)handle.Value;

                if (handleValue < firstHandle || handleValue >= lastHandle)
                {
                    break;
                }

                uint uBlock = (uint)((handleValue - firstHandle) /
                    (HandleTableConstants.HANDLE_SIZE * HandleTableConstants.HANDLE_HANDLES_PER_BLOCK));

                Debug.Assert(pSegment->Header.rgBlockType[uBlock] == uType);

                uint uFreed = BlockFreeHandles(
                    pSegment,
                    uBlock,
                    pHandleBase,
                    uRemain,
                    &uActualFreed,
                    &fScanForFreeBlocks);

                uRemain -= uFreed;
                pHandleBase += uFreed;
            }
            while (uRemain != 0);

            uint uFreedTotal = uCount - uRemain;
            pSegment->Header.rgFreeCount[uType] += uActualFreed;

            if (fScanForFreeBlocks)
            {
                bool fNeedsScavenging = false;
                SegmentRemoveFreeBlocks(pSegment, uType, &fNeedsScavenging);

                if (fNeedsScavenging)
                {
                    pSegment->Header.fResortChains = true;
                    pSegment->Header.fNeedsScavenging = true;
                }
            }

            return uFreedTotal;
        }

        public static void TableFreeBulkPreparedHandles(
            HandleTable* pTable,
            uint uType,
            OBJECTHANDLE* pHandleBase,
            uint uCount)
        {
            pTable->dwCount -= uCount;

            do
            {
                TableSegment* pSegment = (TableSegment*)HandleFetchSegmentPointer(*pHandleBase);

                Debug.Assert(pSegment->Header.pHandleTable == pTable);

                uint uFreed = SegmentFreeHandles(pSegment, uType, pHandleBase, uCount);
                uCount -= uFreed;
                pHandleBase += uFreed;
            }
            while (uCount != 0);
        }
    }
}
