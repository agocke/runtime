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

        uint type = (uint)HandleType.HNDTYPE_PINNED;
        TraceHandleTables(
            &PinObject,
            (nuint)sc,
            (nuint)fn,
            &type,
            1,
            condemned,
            max_gen,
            HandleTableConstants.HNDGCF_NORMAL);
    }

    public static void Ref_TraceNormalRoots(
        int condemned,
        int max_gen,
        ScanContext* sc,
        delegate*<byte**, ScanContext*, uint, void> fn)
    {
        Debug.Assert(condemned >= max_gen);
        Debug.Assert(sc->concurrent == 0);

        uint type = (uint)HandleType.HNDTYPE_STRONG;
        TraceHandleTables(
            &UpdatePointer,
            (nuint)sc,
            (nuint)fn,
            &type,
            1,
            condemned,
            max_gen,
            HandleTableConstants.HNDGCF_NORMAL);
    }

    public static bool Ref_ScanDependentHandlesForPromotion(DhContext* context)
    {
        bool anyPromotions = false;

        do
        {
            context->m_fUnpromotedPrimaries = 0;
            context->m_fPromoted = 0;

            uint type = (uint)HandleType.HNDTYPE_DEPENDENT;
            TraceHandleTables(
                &PromoteDependentHandle,
                (nuint)context->m_pScanContext,
                (nuint)context->m_pfnPromoteFunction,
                &type,
                1,
                context->m_iCondemned,
                context->m_iMaxGen,
                HandleTableConstants.HNDGCF_EXTRAINFO);

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
        _ = sc;

        uint type = (uint)HandleType.HNDTYPE_WEAK_LONG;
        TraceHandleTables(
            checkPromoted,
            0,
            0,
            &type,
            1,
            condemned,
            max_gen,
            HandleTableConstants.HNDGCF_NORMAL);

        type = (uint)HandleType.HNDTYPE_WEAK_INTERIOR_POINTER;
        TraceHandleTables(
            checkPromoted,
            0,
            0,
            &type,
            1,
            condemned,
            max_gen,
            HandleTableConstants.HNDGCF_NORMAL);
    }

    public static void Ref_ScanDependentHandlesForClearing(int condemned, int max_gen, ScanContext* sc)
    {
        _ = sc;

        uint type = (uint)HandleType.HNDTYPE_DEPENDENT;
        TraceHandleTables(
            &ClearDependentHandle,
            0,
            0,
            &type,
            1,
            condemned,
            max_gen,
            HandleTableConstants.HNDGCF_EXTRAINFO);
    }

    public static void Ref_CheckAlive(
        int condemned,
        int max_gen,
        ScanContext* sc,
        delegate*<byte**, nuint*, nuint, nuint, void> checkPromoted)
    {
        _ = sc;

        uint type = (uint)HandleType.HNDTYPE_WEAK_SHORT;
        TraceHandleTables(
            checkPromoted,
            0,
            0,
            &type,
            1,
            condemned,
            max_gen,
            HandleTableConstants.HNDGCF_NORMAL);
    }

    public static void Ref_UpdatePointers(
        int condemned,
        int max_gen,
        ScanContext* sc,
        delegate*<byte**, ScanContext*, uint, void> fn)
    {
        Debug.Assert(condemned >= max_gen);
        Debug.Assert(sc->concurrent == 0);

        delegate*<byte**, nuint*, nuint, nuint, void> updatePointer = &UpdatePointer;
        GCToEEInterface.SyncBlockCacheWeakPtrScan(
            (delegate* unmanaged<byte**, nuint*, nuint, nuint, void>)updatePointer,
            (nuint)sc,
            (nuint)fn);

        uint* types = stackalloc uint[]
        {
            (uint)HandleType.HNDTYPE_WEAK_SHORT,
            (uint)HandleType.HNDTYPE_WEAK_LONG,
            (uint)HandleType.HNDTYPE_STRONG,
            (uint)HandleType.HNDTYPE_REFCOUNTED,
#if FEATURE_JAVAMARSHAL
            (uint)HandleType.HNDTYPE_CROSSREFERENCE,
#endif
        };

        TraceHandleTables(
            &UpdatePointer,
            (nuint)sc,
            (nuint)fn,
            types,
#if FEATURE_JAVAMARSHAL
            5,
#else
            4,
#endif
            condemned,
            max_gen,
            HandleTableConstants.HNDGCF_NORMAL);
    }

    public static void Ref_UpdatePinnedPointers(
        int condemned,
        int max_gen,
        ScanContext* sc,
        delegate*<byte**, ScanContext*, uint, void> fn)
    {
        Debug.Assert(condemned >= max_gen);
        Debug.Assert(sc->concurrent == 0);

        uint type = (uint)HandleType.HNDTYPE_PINNED;
        TraceHandleTables(
            &UpdatePointerPinned,
            (nuint)sc,
            (nuint)fn,
            &type,
            1,
            condemned,
            max_gen,
            HandleTableConstants.HNDGCF_NORMAL);
    }

    public static void Ref_ScanDependentHandlesForRelocation(
        int condemned,
        int max_gen,
        ScanContext* sc,
        delegate*<byte**, ScanContext*, uint, void> fn)
    {
        Debug.Assert(condemned >= max_gen);
        Debug.Assert(sc->concurrent == 0);

        uint type = (uint)HandleType.HNDTYPE_DEPENDENT;
        TraceHandleTables(
            &UpdateDependentHandle,
            (nuint)sc,
            (nuint)fn,
            &type,
            1,
            condemned,
            max_gen,
            HandleTableConstants.HNDGCF_EXTRAINFO);
    }

    public static void Ref_ScanWeakInteriorPointersForRelocation(
        int condemned,
        int max_gen,
        ScanContext* sc,
        delegate*<byte**, ScanContext*, uint, void> fn)
    {
        Debug.Assert(condemned >= max_gen);
        Debug.Assert(sc->concurrent == 0);

        uint type = (uint)HandleType.HNDTYPE_WEAK_INTERIOR_POINTER;
        TraceHandleTables(
            &UpdateWeakInteriorHandle,
            (nuint)sc,
            (nuint)fn,
            &type,
            1,
            condemned,
            max_gen,
            HandleTableConstants.HNDGCF_EXTRAINFO);
    }

    private static void TraceHandleTables(
        delegate*<byte**, nuint*, nuint, nuint, void> scanProc,
        nuint param1,
        nuint param2,
        uint* types,
        uint typeCount,
        int condemned,
        int max_gen,
        uint flags)
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
                            HandleTable* table = pTable[uCPUindex];
                            if (table is not null)
                            {
                                HndScanHandlesForGC(
                                    table,
                                    scanProc,
                                    param1,
                                    param2,
                                    types,
                                    typeCount,
                                    condemned,
                                    max_gen,
                                    flags);
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
        delegate*<byte**, nuint*, nuint, nuint, void> scanProc,
        nuint param1,
        nuint param2,
        uint* types,
        uint typeCount,
        int condemned,
        int max_gen,
        uint flags)
    {
        Debug.Assert(condemned >= max_gen);
        Debug.Assert((flags & HandleTableConstants.HNDGCF_ASYNC) == 0);

        if (condemned < max_gen || (flags & HandleTableConstants.HNDGCF_ASYNC) != 0)
        {
            return;
        }

        bool enumUserData =
            (flags & HandleTableConstants.HNDGCF_EXTRAINFO) != 0 &&
            TypesRequireUserDataScanning(pTable, types, typeCount);

        TableScanHandles(
            pTable,
            types,
            typeCount,
            scanProc,
            param1,
            param2,
            enumUserData);
    }

    private static bool TypesRequireUserDataScanning(HandleTable* pTable, uint* types, uint typeCount)
    {
        uint userDataCount = 0;
        for (uint u = 0; u < typeCount; u++)
        {
            if (HandleTableCore.TypeHasUserData(pTable, types[u]))
            {
                userDataCount++;
            }
        }

        if (userDataCount == typeCount)
        {
            return true;
        }

        Debug.Assert(userDataCount == 0);
        return false;
    }

    private static void TableScanHandles(
        HandleTable* pTable,
        uint* types,
        uint typeCount,
        delegate*<byte**, nuint*, nuint, nuint, void> scanProc,
        nuint param1,
        nuint param2,
        bool enumUserData)
    {
        byte* typeInclusion = stackalloc byte[HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES + 1];
        if (typeCount > 1)
        {
            BuildInclusionMap(typeInclusion, types, typeCount);
        }

        TableSegment* pSegment = null;
        while ((pSegment = FullSegmentIterator(pTable, pSegment)) != null)
        {
            if (typeCount == 1)
            {
                SegmentScanByTypeChain(
                    pSegment,
                    *types,
                    scanProc,
                    param1,
                    param2,
                    enumUserData);
            }
            else if (typeCount > 1)
            {
                SegmentScanByTypeMap(
                    pSegment,
                    typeInclusion,
                    scanProc,
                    param1,
                    param2,
                    enumUserData);
            }
        }
    }

    private static TableSegment* FullSegmentIterator(HandleTable* pTable, TableSegment* pPrevSegment)
    {
        uint sequence = pPrevSegment is null ? 0 : (uint)pPrevSegment->Header.bSequence + 1;

        for (;;)
        {
            TableSegment* pNextSegment = StandardSegmentIterator(pTable, pPrevSegment);
            if (pNextSegment is null)
            {
                return null;
            }

            if (HandleTableCore.DoesSegmentNeedsToTrimExcessPages(pNextSegment))
            {
                using HandleTableCrstHolder holder = new HandleTableCrstHolder(&pTable->Lock);
                HandleTableCore.SegmentTrimExcessPages(pNextSegment);
            }

            if (pNextSegment->Header.bEmptyLine > 0)
            {
                pNextSegment->Header.bSequence = (byte)(sequence % 0x100);
                return pNextSegment;
            }

            using (new HandleTableCrstHolder(&pTable->Lock))
            {
                if (pNextSegment->Header.bEmptyLine != 0 || pTable->pAsyncScanInfo is not null)
                {
                    return pNextSegment;
                }

                TableSegment* pNextNext = pNextSegment->Header.pNextSegment;
                if (pPrevSegment is null)
                {
                    if (pNextNext is not null)
                    {
                        pTable->pSegmentList = pNextNext;
                    }
                    else
                    {
                        return pNextSegment;
                    }
                }
                else
                {
                    pPrevSegment->Header.pNextSegment = pNextNext;
                }

                HandleTableCore.SegmentFree(pNextSegment);
            }
        }
    }

    private static TableSegment* StandardSegmentIterator(
        HandleTable* pTable,
        TableSegment* pPrevSegment)
    {
        TableSegment* pNextSegment = pPrevSegment is null
            ? pTable->pSegmentList
            : pPrevSegment->Header.pNextSegment;

        if (pNextSegment is not null && pNextSegment->Header.fResortChains)
        {
            using HandleTableCrstHolder holder = new HandleTableCrstHolder(&pTable->Lock);
            HandleTableCore.SegmentResortChains(pNextSegment);
        }

        return pNextSegment;
    }

    private static void BuildInclusionMap(byte* typeInclusion, uint* types, uint typeCount)
    {
        for (int i = 0; i < HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES + 1; i++)
        {
            typeInclusion[i] = 0;
        }

        for (uint u = 0; u < typeCount; u++)
        {
            uint type = types[u];
            Debug.Assert(type < HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES);
            typeInclusion[type + 1] = 1;
        }
    }

    private static bool IsBlockIncluded(
        TableSegment* pSegment,
        uint uBlock,
        byte* typeInclusion)
    {
        uint type = (uint)((sbyte)pSegment->Header.rgBlockType[uBlock] + 1);
        Debug.Assert(type <= HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES);
        return typeInclusion[type] != 0;
    }

    private static void SegmentScanByTypeChain(
        TableSegment* pSegment,
        uint type,
        delegate*<byte**, nuint*, nuint, nuint, void> scanProc,
        nuint param1,
        nuint param2,
        bool enumUserData)
    {
        Debug.Assert(type < HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES);

        uint uBlock = pSegment->Header.rgTail[type];
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

            BlockScanBlocks(
                pSegment,
                uBlock,
                uLast - uBlock,
                scanProc,
                param1,
                param2,
                enumUserData);
            uBlock = uNext;
        }
        while (uBlock != uHead);
    }

    private static void SegmentScanByTypeMap(
        TableSegment* pSegment,
        byte* typeInclusion,
        delegate*<byte**, nuint*, nuint, nuint, void> scanProc,
        nuint param1,
        nuint param2,
        bool enumUserData)
    {
        uint uBlock = 0;
        uint uLimit = pSegment->Header.bEmptyLine;

        for (;;)
        {
            for (;;)
            {
                if (uBlock >= uLimit)
                {
                    return;
                }

                if (IsBlockIncluded(pSegment, uBlock, typeInclusion))
                {
                    break;
                }

                uBlock++;
            }

            uint uFirst = uBlock;
            for (;;)
            {
                uBlock++;
                if (uBlock >= uLimit ||
                    !IsBlockIncluded(pSegment, uBlock, typeInclusion))
                {
                    break;
                }
            }

            BlockScanBlocks(
                pSegment,
                uFirst,
                uBlock - uFirst,
                scanProc,
                param1,
                param2,
                enumUserData);
            uBlock++;
        }
    }

    private static void BlockScanBlocks(
        TableSegment* pSegment,
        uint uBlock,
        uint uCount,
        delegate*<byte**, nuint*, nuint, nuint, void> scanProc,
        nuint param1,
        nuint param2,
        bool enumUserData)
    {
        if (!enumUserData)
        {
            nuint* pValue = (nuint*)&pSegment->rgValue[uBlock * HandleTableConstants.HANDLE_HANDLES_PER_BLOCK];
            nuint* pLast = pValue + (uCount * HandleTableConstants.HANDLE_HANDLES_PER_BLOCK);
            ScanConsecutiveHandles(pValue, pLast, null, scanProc, param1, param2);
            return;
        }

        for (uint u = 0; u < uCount; u++)
        {
            uint uCurrent = u + uBlock;
            nuint* pUserData = HandleTableCore.BlockFetchUserDataPointer(
                &pSegment->Header,
                uCurrent,
                fAssertOnError: true);
            nuint* pValue = (nuint*)&pSegment->rgValue[uCurrent * HandleTableConstants.HANDLE_HANDLES_PER_BLOCK];
            nuint* pLast = pValue + HandleTableConstants.HANDLE_HANDLES_PER_BLOCK;
            ScanConsecutiveHandles(pValue, pLast, pUserData, scanProc, param1, param2);
        }
    }

    private static void ScanConsecutiveHandles(
        nuint* pValue,
        nuint* pLast,
        nuint* pUserData,
        delegate*<byte**, nuint*, nuint, nuint, void> scanProc,
        nuint param1,
        nuint param2)
    {
        do
        {
            if (!HandleTableCore.HndIsNullOrDestroyedHandle(*pValue))
            {
                scanProc((byte**)pValue, pUserData, param1, param2);
            }

            pValue++;
            if (pUserData is not null)
            {
                pUserData++;
            }
        }
        while (pValue < pLast);
    }

    private static void UpdatePointer(byte** pObjRef, nuint* pExtraInfo, nuint lp1, nuint lp2)
    {
        _ = pExtraInfo;
        Debug.Assert(lp2 != 0);

        delegate*<byte**, ScanContext*, uint, void> callback =
            (delegate*<byte**, ScanContext*, uint, void>)lp2;
        callback(pObjRef, (ScanContext*)lp1, 0);
    }

    private static void UpdatePointerPinned(byte** pObjRef, nuint* pExtraInfo, nuint lp1, nuint lp2)
    {
        _ = pExtraInfo;
        Debug.Assert(lp2 != 0);

        delegate*<byte**, ScanContext*, uint, void> callback =
            (delegate*<byte**, ScanContext*, uint, void>)lp2;
        callback(pObjRef, (ScanContext*)lp1, (uint)GCCallFlags.GC_CALL_PINNED);
    }

    private static void UpdateDependentHandle(byte** pObjRef, nuint* pExtraInfo, nuint lp1, nuint lp2)
    {
        Debug.Assert(pExtraInfo is not null);
        Debug.Assert(lp2 != 0);

        delegate*<byte**, ScanContext*, uint, void> callback =
            (delegate*<byte**, ScanContext*, uint, void>)lp2;
        callback(pObjRef, (ScanContext*)lp1, 0);
        callback((byte**)pExtraInfo, (ScanContext*)lp1, 0);
    }

    private static void UpdateWeakInteriorHandle(byte** pObjRef, nuint* pExtraInfo, nuint lp1, nuint lp2)
    {
        Debug.Assert(pExtraInfo is not null);
        Debug.Assert(lp2 != 0);

        byte* pOldPrimary = *pObjRef;
        delegate*<byte**, ScanContext*, uint, void> callback =
            (delegate*<byte**, ScanContext*, uint, void>)lp2;
        callback(pObjRef, (ScanContext*)lp1, 0);

        byte* pNewPrimary = *pObjRef;
        if (pNewPrimary is not null)
        {
            nuint** ppInteriorPtrRef = (nuint**)pExtraInfo;
            nuint pOldInterior = **ppInteriorPtrRef;
            nuint delta = (nuint)pNewPrimary - (nuint)pOldPrimary;
            **ppInteriorPtrRef = pOldInterior + delta;
        }
    }

    private static void PromoteDependentHandle(byte** pObjRef, nuint* pExtraInfo, nuint lp1, nuint lp2)
    {
        Debug.Assert(pExtraInfo is not null);

        byte** pSecondaryRef = (byte**)pExtraInfo;
        ScanContext* sc = (ScanContext*)lp1;
        DhContext* context = ObjectHandle.Ref_GetDependentHandleContext(sc);

        if (*pObjRef is not null && ManagedGCHeap.IsPromoted(*pObjRef))
        {
            if (!ManagedGCHeap.IsPromoted(*pSecondaryRef))
            {
                Debug.Assert(lp2 != 0);
                delegate*<byte**, ScanContext*, uint, void> callback =
                    (delegate*<byte**, ScanContext*, uint, void>)lp2;
                callback(pSecondaryRef, sc, 0);
                context->m_fPromoted = 1;
            }
        }
        else if (*pObjRef is not null)
        {
            context->m_fUnpromotedPrimaries = 1;
        }
    }

    private static void ClearDependentHandle(byte** pObjRef, nuint* pExtraInfo, nuint lp1, nuint lp2)
    {
        _ = lp1;
        _ = lp2;
        Debug.Assert(pExtraInfo is not null);

        byte** pSecondaryRef = (byte**)pExtraInfo;
        if (!ManagedGCHeap.IsPromoted(*pObjRef))
        {
            *pObjRef = null;
            *pSecondaryRef = null;
        }
        else
        {
            Debug.Assert(ManagedGCHeap.IsPromoted(*pSecondaryRef));
        }
    }

    private static void PinObject(byte** pObjRef, nuint* pExtraInfo, nuint lp1, nuint lp2)
    {
        UpdatePointerPinned(pObjRef, pExtraInfo, lp1, lp2);
    }
}
