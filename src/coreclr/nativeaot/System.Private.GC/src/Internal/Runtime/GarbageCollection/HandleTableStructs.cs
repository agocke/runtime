// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from the core table layout structures of src/coreclr/gc/handletablepriv.h.

using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>
    /// Prefix of the handle table needed by the segment allocator. The remaining fixed header and
    /// trailing per-type caches arrive with the table allocator.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct HandleTable
    {
        public fixed uint rgTypeFlags[HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES];
    }

    /// <summary>Header data at the start of every handle-table segment.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal unsafe struct _TableSegmentHeader
    {
        public fixed byte rgGeneration[HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT * sizeof(uint)];
        public fixed byte rgAllocation[HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT];
        public fixed uint rgFreeMask[HandleTableConstants.HANDLE_MASKS_PER_SEGMENT];
        public fixed byte rgBlockType[HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT];
        public fixed byte rgUserData[HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT];
        public fixed byte rgLocks[HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT];
        public fixed byte rgTail[HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES];
        public fixed byte rgHint[HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES];
        public fixed uint rgFreeCount[HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES];
        public TableSegment* pNextSegment;
        public HandleTable* pHandleTable;

        /// <summary>
        /// The native <c>fResortChains</c>, <c>fNeedsScavenging</c>, and six unused bits share
        /// one packed byte.
        /// </summary>
        public byte flags;

        public bool fResortChains
        {
            readonly get => (flags & 0x01) != 0;
            set => flags = value ? (byte)(flags | 0x01) : (byte)(flags & ~0x01);
        }

        public bool fNeedsScavenging
        {
            readonly get => (flags & 0x02) != 0;
            set => flags = value ? (byte)(flags | 0x02) : (byte)(flags & ~0x02);
        }

        public byte bFreeList;
        public byte bEmptyLine;
        public byte bCommitLine;
        public byte bDecommitLine;
        public byte bSequence;
    }

    /// <summary>A 64-KiB handle-table segment: one header page followed by handle slots.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal unsafe struct TableSegment
    {
        public _TableSegmentHeader Header;
        public fixed byte rgUnused[HandleTableConstants.HANDLE_HEADER_SIZE - HandleTableConstants.TABLE_SEGMENT_HEADER_SIZE];
#if TARGET_64BIT
        public fixed ulong rgValue[HandleTableConstants.HANDLE_HANDLES_PER_SEGMENT];
#else
        public fixed uint rgValue[HandleTableConstants.HANDLE_HANDLES_PER_SEGMENT];
#endif
    }

    /// <summary>Per-type reserve and free banks used by the handle cache.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct HandleTypeCache
    {
#if TARGET_64BIT
        public fixed ulong rgReserveBank[HandleTableConstants.HANDLES_PER_CACHE_BANK];
#else
        public fixed uint rgReserveBank[HandleTableConstants.HANDLES_PER_CACHE_BANK];
#endif
        public int lReserveIndex;
#if TARGET_64BIT
        public fixed ulong rgFreeBank[HandleTableConstants.HANDLES_PER_CACHE_BANK];
#else
        public fixed uint rgFreeBank[HandleTableConstants.HANDLES_PER_CACHE_BANK];
#endif
        public int lFreeIndex;
    }
}
