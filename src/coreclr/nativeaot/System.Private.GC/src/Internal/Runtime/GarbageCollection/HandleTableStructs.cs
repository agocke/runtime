// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from the core table layout structures of src/coreclr/gc/handletablepriv.h.

using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct ScanRange
    {
        public uint uIndex;
        public uint uCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ScanQNode
    {
        public ScanQNode* pNext;
        public uint uEntries;
        public fixed uint rgRange[
            (HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT / 4) * 2];

        public ScanRange* Ranges
        {
            get
            {
                fixed (uint* ranges = rgRange)
                {
                    return (ScanRange*)ranges;
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AsyncScanInfo
    {
        public void* pCallbackInfo;
        public ScanQNode* pScanQueue;
        public ScanQNode* pQueueTail;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct HandleTableCrstStatic
    {
        private CrstStatic _lock;
#if TARGET_64BIT
#if DEBUG
        private fixed byte _padding[40];
#else
        private fixed byte _padding[32];
#endif
#else
        private fixed byte _padding[20];
#endif

        public bool InitNoThrow(int eType, int eFlags = CrstFlags.CRST_DEFAULT) =>
            _lock.InitNoThrow(eType, eFlags);

        public void Destroy() => _lock.Destroy();

        public void Enter() => _lock.Enter();

        public void Leave() => _lock.Leave();

#if DEBUG
        public readonly bool OwnedByCurrentThread() => _lock.OwnedByCurrentThread();
#endif
    }

    internal unsafe ref struct HandleTableCrstHolder
    {
        private readonly HandleTableCrstStatic* _lock;

        public HandleTableCrstHolder(HandleTableCrstStatic* pLock)
        {
            _lock = pLock;
            _lock->Enter();
        }

        public void Dispose()
        {
            _lock->Leave();
        }
    }

    internal unsafe struct HandleTableCrstHolderWithState
    {
        private HandleTableCrstStatic* _lock;
        private byte _acquired;

        public HandleTableCrstHolderWithState(
            HandleTableCrstStatic* pLock,
            bool acquire = true)
        {
            _lock = pLock;
            _acquired = acquire ? (byte)1 : (byte)0;
            if (acquire)
            {
                _lock->Enter();
            }
        }

        public void Acquire()
        {
            if (_acquired == 0)
            {
                _lock->Enter();
                _acquired = 1;
            }
        }

        public void Release()
        {
            if (_acquired != 0)
            {
                _lock->Leave();
                _acquired = 0;
            }
        }

        public void Dispose()
        {
            Release();
        }
    }

    /// <summary>Fixed header of a handle table, followed in memory by its per-type caches.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct HandleTable
    {
        public fixed uint rgTypeFlags[HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES];
        public TableSegment* pSegmentList;
        public HandleTableCrstStatic Lock;
        public uint uTypeCount;
        public uint dwCount;
        public AsyncScanInfo* pAsyncScanInfo;
        public uint uTableIndex;
#if TARGET_64BIT
        public fixed ulong rgQuickCache[HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES];
#else
        public fixed uint rgQuickCache[HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES];
#endif
#if DEBUG
        public int _DEBUG_iMaxGen;
        public fixed long _DEBUG_TotalBlocksScanned[HandleTableConstants.MAXSTATGEN];
        public fixed long _DEBUG_TotalBlocksScannedNonTrivially[HandleTableConstants.MAXSTATGEN];
        public fixed long _DEBUG_TotalHandleSlotsScanned[HandleTableConstants.MAXSTATGEN];
        public fixed long _DEBUG_TotalHandlesActuallyScanned[HandleTableConstants.MAXSTATGEN];
#endif
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
