// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from src/coreclr/gc/handletablecache.cpp.

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe class HandleTableCache
    {
        public static void SpinUntil(void* pCond, bool fNonZero)
        {
            uint dwThisSleepPeriod = 1;
            uint dwNextSleepPeriod = 10;
#if DEBUG
            uint dwTotalSlept = 0;
            uint dwNextComplain = 1000;
#endif
            uint uNonSleepSpins = 8 * (GCToEEInterface.GetCurrentProcessCpuCount() - 1);

            while ((*(nuint*)pCond != 0) != fNonZero)
            {
                if (uNonSleepSpins == 0)
                {
#if DEBUG
                    if (dwTotalSlept >= dwNextComplain)
                    {
                        Debug.Assert(false);
                        dwNextComplain = 3 * dwNextComplain;
                    }

                    dwTotalSlept += dwThisSleepPeriod;
#endif
                    GCToOSInterface.Sleep(dwThisSleepPeriod);
                    dwThisSleepPeriod = dwNextSleepPeriod;

                    if (dwNextSleepPeriod < 1000)
                    {
                        dwNextSleepPeriod += 10;
                    }
                }
                else
                {
                    GCEnv.YieldProcessor();
                    uNonSleepSpins--;
                }
            }
        }

        public static OBJECTHANDLE* ReadAndZeroCacheHandles(
            OBJECTHANDLE* pDst,
            OBJECTHANDLE* pSrc,
            uint uCount)
        {
            OBJECTHANDLE* pLast = pDst + uCount;

            while (pDst < pLast)
            {
                Debug.Assert(!pSrc->IsNull);

                *pDst = *pSrc;
                *pSrc = default;
                pDst++;
                pSrc++;
            }

            return pLast;
        }

        public static OBJECTHANDLE* SyncReadAndZeroCacheHandles(
            OBJECTHANDLE* pDst,
            OBJECTHANDLE* pSrc,
            uint uCount)
        {
            OBJECTHANDLE* pBase = pDst;
            pSrc += uCount;
            pDst += uCount;
            OBJECTHANDLE* pLast = pDst;

            while (pDst > pBase)
            {
                pDst--;
                pSrc--;

                if (pSrc->IsNull)
                {
                    SpinUntil(pSrc, true);
                }

                *pDst = *pSrc;
                *pSrc = default;
            }

            return pLast;
        }

        public static void WriteCacheHandles(OBJECTHANDLE* pDst, OBJECTHANDLE* pSrc, uint uCount)
        {
            OBJECTHANDLE* pLimit = pSrc + uCount;

            while (pSrc < pLimit)
            {
                Debug.Assert(pDst->IsNull);

                *pDst = *pSrc;
                pDst++;
                pSrc++;
            }
        }

        public static void SyncWriteCacheHandles(OBJECTHANDLE* pDst, OBJECTHANDLE* pSrc, uint uCount)
        {
            OBJECTHANDLE* pBase = pSrc;
            pSrc += uCount;
            pDst += uCount;

            while (pSrc > pBase)
            {
                pDst--;
                pSrc--;

                if (!pDst->IsNull)
                {
                    SpinUntil(pDst, false);
                }

                *pDst = *pSrc;
            }
        }

        public static void SyncTransferCacheHandles(OBJECTHANDLE* pDst, OBJECTHANDLE* pSrc, uint uCount)
        {
            OBJECTHANDLE* pBase = pDst;
            pSrc += uCount;
            pDst += uCount;

            while (pDst > pBase)
            {
                pDst--;
                pSrc--;

                if (!pDst->IsNull || pSrc->IsNull)
                {
                    SpinUntil(pSrc, true);
                    SpinUntil(pDst, false);
                }

                *pDst = *pSrc;
                *pSrc = default;
            }
        }

        public static void TableFullRebalanceCache(
            HandleTable* pTable,
            HandleTypeCache* pCache,
            uint uType,
            int lMinReserveIndex,
            int lMinFreeIndex,
            OBJECTHANDLE* pExtraOutHandle,
            OBJECTHANDLE extraInHandle)
        {
            OBJECTHANDLE* rgHandles = stackalloc OBJECTHANDLE[HandleTableConstants.HANDLE_CACHE_TYPE_SIZE];
            OBJECTHANDLE* pHandleBase = rgHandles;

            if (!extraInHandle.IsNull)
            {
                *pHandleBase = extraInHandle;
                pHandleBase++;
            }

            if (lMinReserveIndex > 0)
            {
                pHandleBase = ReadAndZeroCacheHandles(pHandleBase, (OBJECTHANDLE*)pCache->rgReserveBank, (uint)lMinReserveIndex);
            }
            else
            {
                lMinReserveIndex = 0;
            }

            if (lMinFreeIndex < HandleTableConstants.HANDLES_PER_CACHE_BANK)
            {
                if (lMinFreeIndex < 0)
                {
                    lMinFreeIndex = 0;
                }

                pHandleBase = SyncReadAndZeroCacheHandles(
                    pHandleBase,
                    (OBJECTHANDLE*)pCache->rgFreeBank + lMinFreeIndex,
                    (uint)(HandleTableConstants.HANDLES_PER_CACHE_BANK - lMinFreeIndex));
            }

            uint uHandleCount = (uint)(pHandleBase - rgHandles);

            if (uHandleCount < HandleTableConstants.REBALANCE_LOWATER_MARK)
            {
                uint uAlloc = HandleTableConstants.HANDLES_PER_CACHE_BANK - uHandleCount;

                if (pExtraOutHandle != null)
                {
                    uAlloc++;
                }

                uHandleCount += HandleTableCore.TableAllocBulkHandles(pTable, uType, pHandleBase, uAlloc);
            }

            pHandleBase = rgHandles;
            lMinFreeIndex = HandleTableConstants.HANDLES_PER_CACHE_BANK;

            if (uHandleCount != 0)
            {
                if (uHandleCount > HandleTableConstants.REBALANCE_HIWATER_MARK)
                {
                    HandleTableCore.QuickSort(
                        (nuint*)pHandleBase,
                        0,
                        (int)uHandleCount - 1,
                        &HandleTableCore.CompareHandlesByFreeOrder);

                    uint uFree = uHandleCount - HandleTableConstants.HANDLES_PER_CACHE_BANK;
                    HandleTableCore.TableFreeBulkPreparedHandles(pTable, uType, pHandleBase, uFree);
                    uHandleCount -= uFree;
                    pHandleBase += uFree;
                }

                if (pExtraOutHandle != null)
                {
                    uHandleCount--;
                    *pExtraOutHandle = pHandleBase[uHandleCount];
                }

                if (uHandleCount > HandleTableConstants.HANDLES_PER_CACHE_BANK)
                {
                    uint uStore = uHandleCount - HandleTableConstants.HANDLES_PER_CACHE_BANK;
                    lMinFreeIndex = HandleTableConstants.HANDLES_PER_CACHE_BANK - (int)uStore;
                    WriteCacheHandles((OBJECTHANDLE*)pCache->rgFreeBank + lMinFreeIndex, pHandleBase, uStore);
                    uHandleCount -= uStore;
                    pHandleBase += uStore;
                }
            }

            Interlocked.Exchange(&pCache->lFreeIndex, lMinFreeIndex);

            if (uHandleCount != 0)
            {
                SyncWriteCacheHandles((OBJECTHANDLE*)pCache->rgReserveBank, pHandleBase, uHandleCount);
            }

            lMinReserveIndex = (int)uHandleCount;
            Interlocked.Exchange(&pCache->lReserveIndex, lMinReserveIndex);
        }

        public static void TableQuickRebalanceCache(
            HandleTable* pTable,
            HandleTypeCache* pCache,
            uint uType,
            int lMinReserveIndex,
            int lMinFreeIndex,
            OBJECTHANDLE* pExtraOutHandle,
            OBJECTHANDLE extraInHandle)
        {
            if (lMinFreeIndex < 0)
            {
                lMinFreeIndex = 0;
            }

            if (lMinReserveIndex < 0)
            {
                lMinReserveIndex = 0;
            }

            uint uFreeAvail = (uint)(HandleTableConstants.HANDLES_PER_CACHE_BANK - lMinFreeIndex);
            uint uHandleCount = (uint)lMinReserveIndex + uFreeAvail + (extraInHandle.IsNull ? 0u : 1u);

            if (uHandleCount < HandleTableConstants.REBALANCE_LOWATER_MARK
                || uHandleCount > HandleTableConstants.REBALANCE_HIWATER_MARK)
            {
                TableFullRebalanceCache(
                    pTable,
                    pCache,
                    uType,
                    lMinReserveIndex,
                    lMinFreeIndex,
                    pExtraOutHandle,
                    extraInHandle);
                return;
            }

            uint uEmptyReserve = (uint)(HandleTableConstants.HANDLES_PER_CACHE_BANK - lMinReserveIndex);
            uint uTransfer = uFreeAvail;

            if (uTransfer > uEmptyReserve)
            {
                uTransfer = uEmptyReserve;
            }

            SyncTransferCacheHandles(
                (OBJECTHANDLE*)pCache->rgReserveBank + lMinReserveIndex,
                (OBJECTHANDLE*)pCache->rgFreeBank + lMinFreeIndex,
                uTransfer);

            lMinFreeIndex += (int)uTransfer;
            lMinReserveIndex += (int)uTransfer;

            if (!extraInHandle.IsNull)
            {
                Debug.Assert(pExtraOutHandle == null);
                ((OBJECTHANDLE*)pCache->rgFreeBank)[--lMinFreeIndex] = extraInHandle;
            }
            else if (pExtraOutHandle != null)
            {
                *pExtraOutHandle = ((OBJECTHANDLE*)pCache->rgReserveBank)[--lMinReserveIndex];
                ((OBJECTHANDLE*)pCache->rgReserveBank)[lMinReserveIndex] = default;
            }

            Interlocked.Exchange(&pCache->lFreeIndex, lMinFreeIndex);
            Interlocked.Exchange(&pCache->lReserveIndex, lMinReserveIndex);
        }

        public static OBJECTHANDLE TableCacheMissOnAlloc(
            HandleTable* pTable,
            HandleTypeCache* pCache,
            uint uType)
        {
            OBJECTHANDLE handle = default;

            using (new HandleTableCrstHolder(&pTable->Lock))
            {
                int lReserveIndex = Interlocked.Decrement(&pCache->lReserveIndex);

                if (lReserveIndex < 0)
                {
                    int lFreeIndex = Interlocked.Exchange(&pCache->lFreeIndex, 0);
                    TableQuickRebalanceCache(
                        pTable,
                        pCache,
                        uType,
                        lReserveIndex,
                        lFreeIndex,
                        &handle,
                        default);
                }
                else
                {
                    handle = ((OBJECTHANDLE*)pCache->rgReserveBank)[lReserveIndex];
                    ((OBJECTHANDLE*)pCache->rgReserveBank)[lReserveIndex] = default;
                }
            }

            return handle;
        }

        public static void TableCacheMissOnFree(
            HandleTable* pTable,
            HandleTypeCache* pCache,
            uint uType,
            OBJECTHANDLE handle)
        {
            using (new HandleTableCrstHolder(&pTable->Lock))
            {
                int lFreeIndex = Interlocked.Decrement(&pCache->lFreeIndex);

                if (lFreeIndex < 0)
                {
                    int lReserveIndex = Interlocked.Exchange(&pCache->lReserveIndex, 0);
                    TableQuickRebalanceCache(
                        pTable,
                        pCache,
                        uType,
                        lReserveIndex,
                        lFreeIndex,
                        null,
                        handle);
                }
                else
                {
                    ((OBJECTHANDLE*)pCache->rgFreeBank)[lFreeIndex] = handle;
                }
            }
        }

        public static OBJECTHANDLE TableAllocSingleHandleFromCache(HandleTable* pTable, uint uType)
        {
            OBJECTHANDLE handle;
            OBJECTHANDLE* pQuickCache = (OBJECTHANDLE*)pTable->rgQuickCache;

            if (!pQuickCache[uType].IsNull)
            {
                handle = new OBJECTHANDLE(Interlocked.ExchangePointer(
                    (void**)&pQuickCache[uType],
                    null));

                if (!handle.IsNull)
                {
                    return handle;
                }
            }

            HandleTypeCache* pCache = HandleTableManager.GetMainCache(pTable) + uType;
            int lReserveIndex = Interlocked.Decrement(&pCache->lReserveIndex);

            if (lReserveIndex < 0)
            {
                return TableCacheMissOnAlloc(pTable, pCache, uType);
            }

            handle = ((OBJECTHANDLE*)pCache->rgReserveBank)[lReserveIndex];
            ((OBJECTHANDLE*)pCache->rgReserveBank)[lReserveIndex] = default;

            Debug.Assert(!handle.IsNull);

            return handle;
        }

        public static void TableFreeSingleHandleToCache(
            HandleTable* pTable,
            uint uType,
            OBJECTHANDLE handle)
        {
#if DEBUG
            *(nuint*)handle.Value = HandleTableConstants.DEBUG_DestroyedHandleValue;
#else
            *(nuint*)handle.Value = 0;
#endif
            if (HandleTableCore.TypeHasUserData(pTable, uType))
            {
                HandleTableCore.HandleQuickSetUserData(handle, 0);
            }

            OBJECTHANDLE* pQuickCache = (OBJECTHANDLE*)pTable->rgQuickCache;
            if (pQuickCache[uType].IsNull)
            {
                handle = new OBJECTHANDLE(Interlocked.ExchangePointer(
                    (void**)&pQuickCache[uType],
                    handle.Value));

                if (handle.IsNull)
                {
                    return;
                }
            }

            HandleTypeCache* pCache = HandleTableManager.GetMainCache(pTable) + uType;
            int lFreeIndex = Interlocked.Decrement(&pCache->lFreeIndex);

            if (lFreeIndex < 0)
            {
                TableCacheMissOnFree(pTable, pCache, uType, handle);
                return;
            }

            ((OBJECTHANDLE*)pCache->rgFreeBank)[lFreeIndex] = handle;
        }

        public static uint TableAllocHandlesFromCache(
            HandleTable* pTable,
            uint uType,
            OBJECTHANDLE* pHandleBase,
            uint uCount)
        {
            uint uSatisfied = 0;
            while (uSatisfied < uCount)
            {
                OBJECTHANDLE handle = TableAllocSingleHandleFromCache(pTable, uType);

                if (handle.IsNull)
                {
                    break;
                }

                *pHandleBase = handle;
                uSatisfied++;
                pHandleBase++;
            }

            return uSatisfied;
        }

        public static void TableFreeHandlesToCache(
            HandleTable* pTable,
            uint uType,
            OBJECTHANDLE* pHandleBase,
            uint uCount)
        {
            while (uCount != 0)
            {
                OBJECTHANDLE handle = *pHandleBase;
                uCount--;
                pHandleBase++;

                Debug.Assert(!handle.IsNull);

                TableFreeSingleHandleToCache(pTable, uType, handle);
            }
        }
    }
}
