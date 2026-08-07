// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from the synchronous full-GC scan paths of src/coreclr/gc/handletablescan.cpp,
// src/coreclr/gc/handletable.cpp, and src/coreclr/gc/objecthandle.cpp.

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection;

internal static unsafe class HandleTableScan
{
    public static void Ref_TracePinningRoots(
        int condemned,
        int max_gen,
        ScanContext* sc,
        delegate*<byte**, ScanContext*, uint, void> fn)
    {
        Debug.Assert(condemned >= max_gen);
        Debug.Assert(sc->concurrent == 0);

        TraceHandleTables(
            (uint)HandleType.HNDTYPE_PINNED,
            (uint)GCCallFlags.GC_CALL_PINNED,
            sc,
            fn,
            null,
            null,
            clearDependentHandles: false);
    }

    public static void Ref_TraceNormalRoots(
        int condemned,
        int max_gen,
        ScanContext* sc,
        delegate*<byte**, ScanContext*, uint, void> fn)
    {
        Debug.Assert(condemned >= max_gen);
        Debug.Assert(sc->concurrent == 0);

        TraceHandleTables(
            (uint)HandleType.HNDTYPE_STRONG,
            0,
            sc,
            fn,
            null,
            null,
            clearDependentHandles: false);
    }

    public static bool Ref_ScanDependentHandlesForPromotion(DhContext* context)
    {
        bool anyPromotions = false;

        do
        {
            context->m_fUnpromotedPrimaries = 0;
            context->m_fPromoted = 0;

            TraceHandleTables(
                (uint)HandleType.HNDTYPE_DEPENDENT,
                0,
                context->m_pScanContext,
                null,
                context,
                null,
                clearDependentHandles: false);

            if (context->m_fPromoted != 0)
            {
                anyPromotions = true;
            }
        }
        while (context->m_fUnpromotedPrimaries != 0 && context->m_fPromoted != 0);

        return anyPromotions;
    }

    public static void Ref_CheckReachable(
        int condemned,
        int max_gen,
        ScanContext* sc,
        delegate*<byte**, nuint*, nuint, nuint, void> checkPromoted)
    {
        _ = condemned;
        _ = max_gen;

        TraceHandleTables(
            (uint)HandleType.HNDTYPE_WEAK_LONG,
            0,
            sc,
            null,
            null,
            checkPromoted,
            clearDependentHandles: false);
        TraceHandleTables(
            (uint)HandleType.HNDTYPE_WEAK_INTERIOR_POINTER,
            0,
            sc,
            null,
            null,
            checkPromoted,
            clearDependentHandles: false);
    }

    public static void Ref_ScanDependentHandlesForClearing(int condemned, int max_gen, ScanContext* sc)
    {
        _ = condemned;
        _ = max_gen;

        TraceHandleTables(
            (uint)HandleType.HNDTYPE_DEPENDENT,
            0,
            sc,
            null,
            null,
            null,
            clearDependentHandles: true);
    }

    public static void Ref_CheckAlive(
        int condemned,
        int max_gen,
        ScanContext* sc,
        delegate*<byte**, nuint*, nuint, nuint, void> checkPromoted)
    {
        _ = condemned;
        _ = max_gen;

        TraceHandleTables(
            (uint)HandleType.HNDTYPE_WEAK_SHORT,
            0,
            sc,
            null,
            null,
            checkPromoted,
            clearDependentHandles: false);
    }

    private static void TraceHandleTables(
        uint uType,
        uint flags,
        ScanContext* sc,
        delegate*<byte**, ScanContext*, uint, void> fn,
        DhContext* context,
        delegate*<byte**, nuint*, nuint, nuint, void> checkPromoted,
        bool clearDependentHandles)
    {
        HandleTableMap* walk = (HandleTableMap*)Unsafe.AsPointer(ref ObjectHandle.g_HandleTableMap);
        while (walk != null)
        {
            HandleTableBucket** pBuckets = walk->pBuckets;
            if (pBuckets != null)
            {
                for (uint i = 0; i < HandleTableConstants.INITIAL_HANDLE_TABLE_ARRAY_SIZE; i++)
                {
                    HandleTableBucket* pBucket = pBuckets[i];
                    if (pBucket is not null && pBucket->pTable is not null)
                    {
                        HandleTable** pTable = pBucket->pTable;
                        int uCPUlimit = ObjectHandle.getNumberOfSlots();
                        for (int uCPUindex = 0; uCPUindex < uCPUlimit; uCPUindex++)
                        {
                            if (pTable[uCPUindex] is not null)
                            {
                                HndScanHandlesForGC(
                                    pTable[uCPUindex],
                                    uType,
                                    flags,
                                    sc,
                                    fn,
                                    context,
                                    checkPromoted,
                                    clearDependentHandles);
                            }
                        }
                    }
                }
            }

            walk = walk->pNext;
        }
    }

    private static void HndScanHandlesForGC(
        HandleTable* pTable,
        uint uType,
        uint flags,
        ScanContext* sc,
        delegate*<byte**, ScanContext*, uint, void> fn,
        DhContext* context,
        delegate*<byte**, nuint*, nuint, nuint, void> checkPromoted,
        bool clearDependentHandles)
    {
        using (new HandleTableCrstHolder(&pTable->Lock))
        {
            TableSegment* pSegment = pTable->pSegmentList;
            while (pSegment != null)
            {
                SegmentScanByTypeChain(
                    pSegment,
                    uType,
                    flags,
                    sc,
                    fn,
                    context,
                    checkPromoted,
                    clearDependentHandles);
                pSegment = pSegment->Header.pNextSegment;
            }
        }
    }

    private static void SegmentScanByTypeChain(
        TableSegment* pSegment,
        uint uType,
        uint flags,
        ScanContext* sc,
        delegate*<byte**, ScanContext*, uint, void> fn,
        DhContext* context,
        delegate*<byte**, nuint*, nuint, nuint, void> checkPromoted,
        bool clearDependentHandles)
    {
        Debug.Assert(uType < HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES);

        uint uBlock = pSegment->Header.rgTail[uType];
        if (uBlock == HandleTableConstants.BLOCK_INVALID)
        {
            return;
        }

        uBlock = pSegment->Header.rgAllocation[uBlock];
        uint uHead = uBlock;
        do
        {
            uint uLast;
            uint uNext = uBlock;
            do
            {
                uLast = uNext + 1;
                uNext = pSegment->Header.rgAllocation[uNext];
            }
            while (uNext == uLast && uNext != uHead);

            if (context is null && !clearDependentHandles)
            {
                BlockScanBlocksWithoutUserData(
                    pSegment,
                    uBlock,
                    uLast - uBlock,
                    flags,
                    sc,
                    fn,
                    checkPromoted);
            }
            else
            {
                BlockScanBlocksWithUserData(
                    pSegment,
                    uBlock,
                    uLast - uBlock,
                    context,
                    clearDependentHandles);
            }

            uBlock = uNext;
        }
        while (uBlock != uHead);
    }

    private static void BlockScanBlocksWithoutUserData(
        TableSegment* pSegment,
        uint uBlock,
        uint uCount,
        uint flags,
        ScanContext* sc,
        delegate*<byte**, ScanContext*, uint, void> fn,
        delegate*<byte**, nuint*, nuint, nuint, void> checkPromoted)
    {
        while (uCount != 0)
        {
            nuint* pValue = (nuint*)&pSegment->rgValue[uBlock * HandleTableConstants.HANDLE_HANDLES_PER_BLOCK];
            nuint* pLast = pValue + HandleTableConstants.HANDLE_HANDLES_PER_BLOCK;
            ScanConsecutiveHandlesWithoutUserData(pValue, pLast, flags, sc, fn, checkPromoted);
            uBlock++;
            uCount--;
        }
    }

    private static void ScanConsecutiveHandlesWithoutUserData(
        nuint* pValue,
        nuint* pLast,
        uint flags,
        ScanContext* sc,
        delegate*<byte**, ScanContext*, uint, void> fn,
        delegate*<byte**, nuint*, nuint, nuint, void> checkPromoted)
    {
        do
        {
            if (!HandleTableCore.HndIsNullOrDestroyedHandle(*pValue))
            {
                if (fn is not null)
                {
                    fn((byte**)pValue, sc, flags);
                }
                else
                {
                    Debug.Assert(checkPromoted is not null);
                    checkPromoted((byte**)pValue, null, 0, 0);
                }
            }

            pValue++;
        }
        while (pValue < pLast);
    }

    private static void BlockScanBlocksWithUserData(
        TableSegment* pSegment,
        uint uBlock,
        uint uCount,
        DhContext* context,
        bool clearDependentHandles)
    {
        while (uCount != 0)
        {
            nuint* pValue = (nuint*)&pSegment->rgValue[uBlock * HandleTableConstants.HANDLE_HANDLES_PER_BLOCK];
            nuint* pLast = pValue + HandleTableConstants.HANDLE_HANDLES_PER_BLOCK;
            nuint* pUserData = HandleTableCore.BlockFetchUserDataPointer(
                &pSegment->Header,
                uBlock,
                fAssertOnError: true);
            ScanConsecutiveHandlesWithUserData(
                pValue,
                pLast,
                context,
                pUserData,
                clearDependentHandles);
            uBlock++;
            uCount--;
        }
    }

    private static void ScanConsecutiveHandlesWithUserData(
        nuint* pValue,
        nuint* pLast,
        DhContext* context,
        nuint* pUserData,
        bool clearDependentHandles)
    {
        do
        {
            if (!HandleTableCore.HndIsNullOrDestroyedHandle(*pValue))
            {
                if (clearDependentHandles)
                {
                    ClearDependentHandle((byte**)pValue, (byte**)pUserData);
                }
                else
                {
                    PromoteDependentHandle((byte**)pValue, (byte**)pUserData, context);
                }
            }

            pValue++;
            pUserData++;
        }
        while (pValue < pLast);
    }

    private static void PromoteDependentHandle(
        byte** pPrimaryRef,
        byte** pSecondaryRef,
        DhContext* context)
    {
        if (*pPrimaryRef is not null && ManagedGCHeap.IsPromoted(*pPrimaryRef))
        {
            if (!ManagedGCHeap.IsPromoted(*pSecondaryRef))
            {
                Debug.Assert(context->m_pfnPromoteFunction != null);
                context->m_pfnPromoteFunction(pSecondaryRef, context->m_pScanContext, 0);
                context->m_fPromoted = 1;
            }
        }
        else if (*pPrimaryRef is not null)
        {
            context->m_fUnpromotedPrimaries = 1;
        }
    }

    private static void ClearDependentHandle(byte** pPrimaryRef, byte** pSecondaryRef)
    {
        if (!ManagedGCHeap.IsPromoted(*pPrimaryRef))
        {
            *pPrimaryRef = null;
            *pSecondaryRef = null;
        }
        else
        {
            Debug.Assert(ManagedGCHeap.IsPromoted(*pSecondaryRef));
        }
    }
}
