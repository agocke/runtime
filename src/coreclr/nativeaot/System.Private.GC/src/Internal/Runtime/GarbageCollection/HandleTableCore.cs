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
    }
}
