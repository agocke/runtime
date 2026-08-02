// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

public sealed unsafe class HandleTableCacheTests
{
    [Fact]
    public void CacheBankHelpersPreserveOrderAndClearSources()
    {
        OBJECTHANDLE* source = stackalloc OBJECTHANDLE[4];
        OBJECTHANDLE* destination = stackalloc OBJECTHANDLE[4];
        for (int i = 0; i < 4; i++)
        {
            source[i] = new OBJECTHANDLE((void*)(nuint)(0x1000 + (i * 0x10)));
        }

        OBJECTHANDLE* end = HandleTableCache.ReadAndZeroCacheHandles(destination, source, 4);

        Assert.True(end == destination + 4);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal((nuint)(0x1000 + (i * 0x10)), (nuint)destination[i].Value);
            Assert.True(source[i].IsNull);
        }

        HandleTableCache.WriteCacheHandles(source, destination, 4);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal((nuint)destination[i].Value, (nuint)source[i].Value);
        }
    }

    [Fact]
    public void QuickRebalanceTransfersTheOccupiedFreeBankTail()
    {
        HandleTypeCache cache = default;
        OBJECTHANDLE* reserve = (OBJECTHANDLE*)cache.rgReserveBank;
        OBJECTHANDLE* free = (OBJECTHANDLE*)cache.rgFreeBank;

        for (int i = 0; i < 20; i++)
        {
            reserve[i] = new OBJECTHANDLE((void*)(nuint)(0x1000 + i));
        }

        for (int i = 41; i < HandleTableConstants.HANDLES_PER_CACHE_BANK; i++)
        {
            free[i] = new OBJECTHANDLE((void*)(nuint)(0x2000 + i));
        }

        HandleTableCache.TableQuickRebalanceCache(
            null,
            &cache,
            0,
            20,
            41,
            null,
            default);

        Assert.Equal(42, cache.lReserveIndex);
        Assert.Equal(HandleTableConstants.HANDLES_PER_CACHE_BANK, cache.lFreeIndex);
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal((nuint)(0x1000 + i), (nuint)reserve[i].Value);
        }

        for (int i = 0; i < 22; i++)
        {
            Assert.Equal((nuint)(0x2000 + 41 + i), (nuint)reserve[20 + i].Value);
            Assert.True(free[41 + i].IsNull);
        }
    }

    [Fact]
    public void EmptyCacheMissBulkFillsReserveAndQuickCacheReusesFreedHandle()
    {
        uint* typeFlags = stackalloc uint[1];
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, 1);
        Assert.True(table != null);

        try
        {
            OBJECTHANDLE handle = HandleTableCache.TableAllocSingleHandleFromCache(table, 0);
            Assert.False(handle.IsNull);

            HandleTypeCache* cache = HandleTableManager.GetMainCache(table);
            Assert.Equal(HandleTableConstants.HANDLES_PER_CACHE_BANK, cache->lReserveIndex);
            Assert.Equal((uint)(HandleTableConstants.HANDLES_PER_CACHE_BANK + 1), table->dwCount);

            *(nuint*)handle.Value = 0x1234;
            HandleTableCache.TableFreeSingleHandleToCache(table, 0, handle);

#if DEBUG
            Assert.Equal((nuint)0x7, *(nuint*)handle.Value);
#else
            Assert.Equal((nuint)0, *(nuint*)handle.Value);
#endif
            Assert.Equal((nuint)handle.Value, (nuint)((OBJECTHANDLE*)table->rgQuickCache)[0].Value);

            OBJECTHANDLE reused = HandleTableCache.TableAllocSingleHandleFromCache(table, 0);
            Assert.Equal((nuint)handle.Value, (nuint)reused.Value);
            Assert.True(((OBJECTHANDLE*)table->rgQuickCache)[0].IsNull);
        }
        finally
        {
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Fact]
    public void FullRebalanceFreesHighHandlesAndKeepsLowHandlesInReserve()
    {
        const uint Count = 100;
        const int ReserveCount = 50;
        const int FreeStart = HandleTableConstants.HANDLES_PER_CACHE_BANK - (int)(Count - ReserveCount);

        uint* typeFlags = stackalloc uint[1];
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, 1);
        Assert.True(table != null);

        try
        {
            OBJECTHANDLE* handles = stackalloc OBJECTHANDLE[(int)Count];
            Assert.Equal(Count, HandleTableCore.TableAllocBulkHandles(table, 0, handles, Count));

            HandleTypeCache* cache = HandleTableManager.GetMainCache(table);
            OBJECTHANDLE* reserve = (OBJECTHANDLE*)cache->rgReserveBank;
            OBJECTHANDLE* free = (OBJECTHANDLE*)cache->rgFreeBank;
            for (int i = 0; i < ReserveCount; i++)
            {
                reserve[i] = handles[i];
            }

            for (int i = ReserveCount; i < Count; i++)
            {
                free[FreeStart + i - ReserveCount] = handles[i];
            }

            HandleTableCache.TableFullRebalanceCache(
                table,
                cache,
                0,
                ReserveCount,
                FreeStart,
                null,
                default);

            Assert.Equal(HandleTableConstants.HANDLES_PER_CACHE_BANK, cache->lReserveIndex);
            Assert.Equal(HandleTableConstants.HANDLES_PER_CACHE_BANK, cache->lFreeIndex);
            Assert.Equal((uint)HandleTableConstants.HANDLES_PER_CACHE_BANK, table->dwCount);

            nuint highestRetained = 0;
            nuint lowestFreed = nuint.MaxValue;
            int retainedCount = 0;
            int freedCount = 0;
            for (int i = 0; i < Count; i++)
            {
                bool retained = Contains(reserve, HandleTableConstants.HANDLES_PER_CACHE_BANK, handles[i]);
                Assert.NotEqual(retained, IsHandleFree(handles[i]));

                if (retained)
                {
                    retainedCount++;
                    highestRetained = nuint.Max(highestRetained, (nuint)handles[i].Value);
                }
                else
                {
                    freedCount++;
                    lowestFreed = nuint.Min(lowestFreed, (nuint)handles[i].Value);
                }
            }

            Assert.Equal(HandleTableConstants.HANDLES_PER_CACHE_BANK, retainedCount);
            Assert.Equal((int)Count - HandleTableConstants.HANDLES_PER_CACHE_BANK, freedCount);
            Assert.True(highestRetained < lowestFreed);
        }
        finally
        {
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Fact]
    public void FreeingCachedExtraInfoHandleClearsReferentAndUserData()
    {
        const uint Type = 0;

        uint* typeFlags = stackalloc uint[1];
        typeFlags[Type] = HandleTableConstants.HNDF_EXTRAINFO;
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, 1);
        Assert.True(table != null);

        try
        {
            OBJECTHANDLE handle = HandleTableCache.TableAllocSingleHandleFromCache(table, Type);
            Assert.False(handle.IsNull);

            HandleTableCore.HandleQuickSetUserData(handle, 0x5678);
            *(nuint*)handle.Value = 0x1234;

            HandleTableCache.TableFreeSingleHandleToCache(table, Type, handle);

#if DEBUG
            Assert.Equal((nuint)0x7, *(nuint*)handle.Value);
#else
            Assert.Equal((nuint)0, *(nuint*)handle.Value);
#endif
            Assert.Equal((nuint)0, *HandleTableCore.HandleQuickFetchUserDataPointer(handle));
        }
        finally
        {
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    [Fact]
    public void BulkAllocationAndPreparedFreeUpdateTableCount()
    {
        uint* typeFlags = stackalloc uint[1];
        HandleTable* table = HandleTableManager.HndCreateHandleTable(typeFlags, 1);
        Assert.True(table != null);

        try
        {
            const uint Count = 80;
            OBJECTHANDLE* handles = stackalloc OBJECTHANDLE[(int)Count];

            Assert.Equal(Count, HandleTableCore.TableAllocBulkHandles(table, 0, handles, Count));
            Assert.Equal(Count, table->dwCount);

            for (int i = 0; i < Count; i++)
            {
                Assert.False(handles[i].IsNull);
                Assert.Equal((nuint)0, *(nuint*)handles[i].Value);
            }

            HandleTableCore.TableFreeBulkPreparedHandles(table, 0, handles, Count);
            Assert.Equal(0u, table->dwCount);
            Assert.Equal(0u, table->pSegmentList->Header.rgFreeCount[0]);
            Assert.Equal(HandleTableConstants.BLOCK_INVALID, table->pSegmentList->Header.rgTail[0]);
        }
        finally
        {
            HandleTableManager.HndDestroyHandleTable(table);
        }
    }

    private static bool Contains(OBJECTHANDLE* handles, int count, OBJECTHANDLE handle)
    {
        for (int i = 0; i < count; i++)
        {
            if (handles[i].Value == handle.Value)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHandleFree(OBJECTHANDLE handle)
    {
        TableSegment* segment = (TableSegment*)HandleTableCore.HandleFetchSegmentPointer(handle);
        nuint offset = (nuint)handle.Value & HandleTableConstants.HANDLE_SEGMENT_CONTENT_MASK;
        uint handleIndex = (uint)((offset - HandleTableConstants.HANDLE_HEADER_SIZE) / HandleTableConstants.HANDLE_SIZE);
        uint maskIndex = handleIndex / HandleTableConstants.HANDLE_HANDLES_PER_MASK;
        uint bitIndex = handleIndex % HandleTableConstants.HANDLE_HANDLES_PER_MASK;

        return (segment->Header.rgFreeMask[maskIndex] & (1u << (int)bitIndex)) != 0;
    }
}
