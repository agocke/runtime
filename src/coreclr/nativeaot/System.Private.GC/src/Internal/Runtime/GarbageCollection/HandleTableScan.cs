// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from the synchronous GC scan paths of src/coreclr/gc/handletablescan.cpp,
// src/coreclr/gc/handletable.cpp, and src/coreclr/gc/objecthandle.cpp.

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection;

internal static unsafe class HandleTableScan
{
    private const byte MaxGenerationAge = 0x3f;
    private const uint GenClamp = 0x3f3f3f3f;
    private const uint GenFill = 0x80808080;
    private const uint GenMask = 0x40404040;
    private const int GenIncrementShift = 6;
    private const byte BlockActionScan = 0;
    private const byte BlockActionAge = 1;
    private const byte BlockActionResetAgeMap = 2;

    private struct ScanCallbackInfo
    {
        public uint uFlags;
        public byte fEnumUserData;
        public uint dwAgeMask;
        public delegate*<byte**, nuint*, nuint, nuint, void> pfnScan;
        public nuint param1;
        public nuint param2;
    }

    public static void Ref_TracePinningRoots(
        int condemned,
        int max_gen,
        ScanContext* sc,
        delegate*<byte**, ScanContext*, uint, void> fn)
    {
        Debug.Assert((uint)condemned <= (uint)max_gen);
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
        Debug.Assert((uint)condemned <= (uint)max_gen);
        Debug.Assert(sc->concurrent == 0);

        uint* types = stackalloc uint[]
        {
            (uint)HandleType.HNDTYPE_STRONG,
            (uint)HandleType.HNDTYPE_SIZEDREF,
        };
        TraceHandleTables(
            &UpdatePointer,
            (nuint)sc,
            (nuint)fn,
            types,
            condemned >= max_gen ? 1u : 2u,
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
        Debug.Assert((uint)condemned <= (uint)max_gen);
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
            (uint)HandleType.HNDTYPE_SIZEDREF,
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
            6,
#else
            5,
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
        Debug.Assert((uint)condemned <= (uint)max_gen);
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
        Debug.Assert((uint)condemned <= (uint)max_gen);
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
        Debug.Assert((uint)condemned <= (uint)max_gen);
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

    public static void Ref_AgeHandles(int condemned, int max_gen, ScanContext* sc)
    {
        Debug.Assert((uint)condemned <= (uint)max_gen);
        Debug.Assert(sc->concurrent == 0);

        if (condemned >= max_gen)
        {
            ForEachAgedHandleBlock(&AgeFullGcBlock);
            return;
        }

        uint* types = stackalloc uint[]
        {
            (uint)HandleType.HNDTYPE_WEAK_SHORT,
            (uint)HandleType.HNDTYPE_WEAK_LONG,
            (uint)HandleType.HNDTYPE_STRONG,
            (uint)HandleType.HNDTYPE_PINNED,
            (uint)HandleType.HNDTYPE_DEPENDENT,
            (uint)HandleType.HNDTYPE_REFCOUNTED,
            (uint)HandleType.HNDTYPE_SIZEDREF,
            (uint)HandleType.HNDTYPE_WEAK_INTERIOR_POINTER,
        };
        TraceHandleTables(
            null,
            0,
            0,
            types,
            8,
            condemned,
            max_gen,
            HandleTableConstants.HNDGCF_AGE);
    }

    public static void Ref_RejuvenateHandles(int condemned, int max_gen, ScanContext* sc)
    {
        Debug.Assert((uint)condemned <= (uint)max_gen);
        Debug.Assert(sc->concurrent == 0);

        if (condemned >= max_gen)
        {
            ForEachAgedHandleBlock(&ResetFullGcAgeMapBlock);
            return;
        }

        uint* types = stackalloc uint[]
        {
            (uint)HandleType.HNDTYPE_WEAK_SHORT,
            (uint)HandleType.HNDTYPE_WEAK_LONG,
            (uint)HandleType.HNDTYPE_STRONG,
            (uint)HandleType.HNDTYPE_PINNED,
            (uint)HandleType.HNDTYPE_DEPENDENT,
            (uint)HandleType.HNDTYPE_REFCOUNTED,
            (uint)HandleType.HNDTYPE_SIZEDREF,
            (uint)HandleType.HNDTYPE_WEAK_INTERIOR_POINTER,
        };
        ResetAgeMaps(types, 8, condemned, max_gen);
    }

    private static void ForEachAgedHandleBlock(
        delegate*<TableSegment*, uint, void> action)
    {
        HandleTableMap* walk =
            (HandleTableMap*)Unsafe.AsPointer(ref ObjectHandle.g_HandleTableMap);
        while (walk is not null)
        {
            HandleTableBucket** buckets = walk->pBuckets;
            if (buckets is not null)
            {
                for (uint bucketIndex = 0;
                     bucketIndex < HandleTableConstants.INITIAL_HANDLE_TABLE_ARRAY_SIZE;
                     bucketIndex++)
                {
                    HandleTableBucket* bucket = buckets[bucketIndex];
                    if (bucket is null || bucket->pTable is null)
                    {
                        continue;
                    }

                    int slotCount = ObjectHandle.getNumberOfSlots();
                    for (int slotIndex = 0; slotIndex < slotCount; slotIndex++)
                    {
                        HandleTable* table = bucket->pTable[slotIndex];
                        if (table is null)
                        {
                            continue;
                        }

                        using HandleTableCrstHolder holder =
                            new(&table->Lock);
                        for (TableSegment* segment = table->pSegmentList;
                             segment is not null;
                             segment = segment->Header.pNextSegment)
                        {
                            for (uint block = 0;
                                 block < segment->Header.bEmptyLine;
                                 block++)
                            {
                                if (HandleTypeNeedsAging(
                                        segment->Header.rgBlockType[block]))
                                {
                                    action(segment, block);
                                }
                            }
                        }
                    }
                }
            }

            walk = walk->pNext;
        }
    }

    private static bool HandleTypeNeedsAging(byte type)
    {
        if (type == (byte)HandleType.HNDTYPE_WEAK_SHORT ||
            type == (byte)HandleType.HNDTYPE_WEAK_LONG ||
            type == (byte)HandleType.HNDTYPE_STRONG ||
            type == (byte)HandleType.HNDTYPE_PINNED ||
            type == (byte)HandleType.HNDTYPE_DEPENDENT ||
            type == (byte)HandleType.HNDTYPE_REFCOUNTED ||
            type == (byte)HandleType.HNDTYPE_WEAK_INTERIOR_POINTER)
        {
            return true;
        }

#if FEATURE_VARIABLE_HANDLES
        if (type == (byte)HandleType.HNDTYPE_VARIABLE)
        {
            return true;
        }
#endif
#if FEATURE_WEAK_NATIVE_COM_HANDLES
        if (type == (byte)HandleType.HNDTYPE_WEAK_NATIVE_COM)
        {
            return true;
        }
#endif
#if FEATURE_ASYNC_PINNED_HANDLES
        if (type == (byte)HandleType.HNDTYPE_ASYNCPINNED)
        {
            return true;
        }
#endif
        if (type == (byte)HandleType.HNDTYPE_SIZEDREF)
        {
            return true;
        }
#if FEATURE_JAVAMARSHAL
        if (type == (byte)HandleType.HNDTYPE_CROSSREFERENCE)
        {
            return true;
        }
#endif

        return false;
    }

    private static void AgeFullGcBlock(TableSegment* segment, uint block)
    {
        byte* ages =
            segment->Header.rgGeneration +
            (block * HandleTableConstants.HANDLE_CLUMPS_PER_BLOCK);
        for (int clump = 0;
             clump < HandleTableConstants.HANDLE_CLUMPS_PER_BLOCK;
             clump++)
        {
            if (ages[clump] < MaxGenerationAge)
            {
                ages[clump]++;
            }
        }
    }

    private static void ResetFullGcAgeMapBlock(TableSegment* segment, uint block)
    {
        nuint* values =
            (nuint*)segment->rgValue +
            (block * HandleTableConstants.HANDLE_HANDLES_PER_BLOCK);
        nuint* userData = null;
        if (segment->Header.rgBlockType[block] ==
            (byte)HandleType.HNDTYPE_DEPENDENT)
        {
            byte userDataBlock = segment->Header.rgUserData[block];
            Debug.Assert(userDataBlock != HandleTableConstants.BLOCK_INVALID);
            userData =
                (nuint*)segment->rgValue +
                (userDataBlock * HandleTableConstants.HANDLE_HANDLES_PER_BLOCK);
        }

        byte* ages =
            segment->Header.rgGeneration +
            (block * HandleTableConstants.HANDLE_CLUMPS_PER_BLOCK);
        for (int clump = 0;
             clump < HandleTableConstants.HANDLE_CLUMPS_PER_BLOCK;
             clump++)
        {
            if (ages[clump] >= MaxGenerationAge)
            {
                continue;
            }

            int minAge = MaxGenerationAge;
            int firstHandle = clump * HandleTableConstants.HANDLE_HANDLES_PER_CLUMP;
            int lastHandle = firstHandle + HandleTableConstants.HANDLE_HANDLES_PER_CLUMP;
            for (int handleIndex = firstHandle;
                 handleIndex < lastHandle;
                 handleIndex++)
            {
                nuint value = values[handleIndex];
                if (!HandleTableCore.HndIsNullOrDestroyedHandle(value))
                {
                    int generation = HandleTableManager.GetConvertedGeneration((byte*)value);
                    if (generation < minAge)
                    {
                        minAge = generation;
                    }

                    if (userData is not null && userData[handleIndex] != 0)
                    {
                        int secondaryGeneration =
                            HandleTableManager.GetConvertedGeneration(
                                (byte*)userData[handleIndex]);
                        if (secondaryGeneration < minAge)
                        {
                            minAge = secondaryGeneration;
                        }
                    }
                }
            }

            ages[clump] = unchecked((byte)minAge);
        }
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
        Debug.Assert((uint)condemned <= (uint)max_gen);
        Debug.Assert((flags & HandleTableConstants.HNDGCF_ASYNC) == 0);

        if ((flags & HandleTableConstants.HNDGCF_ASYNC) != 0)
        {
            return;
        }

        bool enumUserData =
            (flags & HandleTableConstants.HNDGCF_EXTRAINFO) != 0 &&
            TypesRequireUserDataScanning(pTable, types, typeCount);

        if (condemned >= max_gen)
        {
            TableScanHandles(
                pTable,
                types,
                typeCount,
                scanProc,
                param1,
                param2,
                enumUserData);
            return;
        }

        ScanCallbackInfo info = default;
        info.uFlags = flags;
        info.fEnumUserData = enumUserData ? (byte)1 : (byte)0;
        info.dwAgeMask = BuildAgeMask((uint)condemned, (uint)max_gen);
        info.pfnScan = scanProc;
        info.param1 = param1;
        info.param2 = param2;

        byte action = scanProc is not null
            ? BlockActionScan
            : BlockActionAge;
        TableScanHandlesEphemeral(
            pTable,
            types,
            typeCount,
            condemned == 0,
            action,
            &info);
    }

    private static void ResetAgeMaps(
        uint* types,
        uint typeCount,
        int condemned,
        int max_gen)
    {
        HandleTableMap* walk =
            (HandleTableMap*)Unsafe.AsPointer(ref ObjectHandle.g_HandleTableMap);
        while (walk is not null)
        {
            HandleTableBucket** buckets = walk->pBuckets;
            if (buckets is not null)
            {
                for (uint bucketIndex = 0;
                     bucketIndex < HandleTableConstants.INITIAL_HANDLE_TABLE_ARRAY_SIZE;
                     bucketIndex++)
                {
                    HandleTableBucket* bucket = buckets[bucketIndex];
                    if (bucket is null || bucket->pTable is null)
                    {
                        continue;
                    }

                    int slotCount = ObjectHandle.getNumberOfSlots();
                    for (int slotIndex = 0; slotIndex < slotCount; slotIndex++)
                    {
                        HandleTable* table = bucket->pTable[slotIndex];
                        if (table is not null)
                        {
                            HndResetAgeMap(
                                table,
                                types,
                                typeCount,
                                condemned,
                                max_gen);
                        }
                    }
                }
            }

            walk = walk->pNext;
        }
    }

    private static void HndResetAgeMap(
        HandleTable* pTable,
        uint* types,
        uint typeCount,
        int condemned,
        int max_gen)
    {
        ScanCallbackInfo info = default;
        info.dwAgeMask = BuildAgeMask((uint)condemned, (uint)max_gen);
        TableScanHandlesEphemeral(
            pTable,
            types,
            typeCount,
            useQuickIterator: true,
            action: BlockActionResetAgeMap,
            info: &info);
    }

    internal static uint BuildAgeMask(uint generation, uint maxGeneration)
    {
        if (generation == maxGeneration)
        {
            generation = MaxGenerationAge;
        }

        generation++;
        if (generation > MaxGenerationAge)
        {
            generation = MaxGenerationAge;
        }

        uint mask =
            generation |
            (generation << 8) |
            (generation << 16) |
            (generation << 24);
        return unchecked(1u + mask + ~GenFill);
    }

    internal static uint ComputeClumpMask(uint generations, uint ageMask) =>
        unchecked(((generations & GenClamp) - ageMask) & GenMask);

    internal static uint ComputeAgedClumps(uint generations, uint ageMask) =>
        unchecked(generations + (ComputeClumpMask(generations, ageMask) >> GenIncrementShift));

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

    private static void TableScanHandlesEphemeral(
        HandleTable* pTable,
        uint* types,
        uint typeCount,
        bool useQuickIterator,
        byte action,
        ScanCallbackInfo* info)
    {
        byte* typeInclusion =
            stackalloc byte[HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES + 1];
        if (typeCount > 1)
        {
            BuildInclusionMap(typeInclusion, types, typeCount);
        }

        TableSegment* pSegment = null;
        for (; ; )
        {
            pSegment = useQuickIterator
                ? QuickSegmentIterator(pTable, pSegment)
                : StandardSegmentIterator(pTable, pSegment);
            if (pSegment is null)
            {
                return;
            }

            if (typeCount == 1)
            {
                SegmentScanByTypeChainEphemeral(
                    pSegment,
                    *types,
                    action,
                    info);
            }
            else if (typeCount > 1)
            {
                SegmentScanByTypeMapEphemeral(
                    pSegment,
                    typeInclusion,
                    action,
                    info);
            }
        }
    }

    private static void SegmentScanByTypeChainEphemeral(
        TableSegment* pSegment,
        uint type,
        byte action,
        ScanCallbackInfo* info)
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

            ProcessEphemeralBlocks(
                pSegment,
                uBlock,
                uLast - uBlock,
                action,
                info);
            uBlock = uNext;
        }
        while (uBlock != uHead);
    }

    private static void SegmentScanByTypeMapEphemeral(
        TableSegment* pSegment,
        byte* typeInclusion,
        byte action,
        ScanCallbackInfo* info)
    {
        uint uBlock = 0;
        uint uLimit = pSegment->Header.bEmptyLine;

        for (; ; )
        {
            for (; ; )
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
            for (; ; )
            {
                uBlock++;
                if (uBlock >= uLimit ||
                    !IsBlockIncluded(pSegment, uBlock, typeInclusion))
                {
                    break;
                }
            }

            ProcessEphemeralBlocks(
                pSegment,
                uFirst,
                uBlock - uFirst,
                action,
                info);
            uBlock++;
        }
    }

    private static void ProcessEphemeralBlocks(
        TableSegment* pSegment,
        uint uBlock,
        uint uCount,
        byte action,
        ScanCallbackInfo* info)
    {
        switch (action)
        {
            case BlockActionScan:
                BlockScanBlocksEphemeral(pSegment, uBlock, uCount, info);
                break;
            case BlockActionAge:
                BlockAgeBlocksEphemeral(pSegment, uBlock, uCount, info);
                break;
            default:
                Debug.Assert(action == BlockActionResetAgeMap);
                BlockResetAgeMapForBlocks(pSegment, uBlock, uCount, info);
                break;
        }
    }

    private static void BlockScanBlocksEphemeral(
        TableSegment* pSegment,
        uint uBlock,
        uint uCount,
        ScanCallbackInfo* info)
    {
        uint* generations = (uint*)pSegment->Header.rgGeneration + uBlock;
        uint* generationsEnd = generations + uCount;
        do
        {
            uint clumpMask = ComputeClumpMask(*generations, info->dwAgeMask);
            if (clumpMask != 0)
            {
                BlockScanBlocksEphemeralWorker(
                    pSegment,
                    generations,
                    clumpMask,
                    info);
            }

            generations++;
        }
        while (generations < generationsEnd);
    }

    private static void BlockScanBlocksEphemeralWorker(
        TableSegment* pSegment,
        uint* generations,
        uint clumpMask,
        ScanCallbackInfo* info)
    {
        if ((info->uFlags & HandleTableConstants.HNDGCF_AGE) != 0)
        {
            *generations = unchecked(
                *generations + (clumpMask >> GenIncrementShift));
        }

        uint firstClump =
            (uint)((byte*)generations - pSegment->Header.rgGeneration);
        nuint* pValue =
            (nuint*)pSegment->rgValue +
            (firstClump * HandleTableConstants.HANDLE_HANDLES_PER_CLUMP);
        nuint* pUserData = null;
        if (info->fEnumUserData != 0)
        {
            pUserData = HandleTableCore.BlockFetchUserDataPointer(
                &pSegment->Header,
                firstClump / HandleTableConstants.HANDLE_CLUMPS_PER_BLOCK,
                fAssertOnError: true);
        }

        do
        {
            nuint* pLast =
                pValue + HandleTableConstants.HANDLE_HANDLES_PER_CLUMP;
            if ((clumpMask & HandleTableConstants.GEN_CLUMP_0_MASK) != 0)
            {
                ScanConsecutiveHandles(
                    pValue,
                    pLast,
                    pUserData,
                    info->pfnScan,
                    info->param1,
                    info->param2);
            }

            clumpMask =
                HandleTableConstants.NEXT_CLUMP_IN_MASK(clumpMask);
            pValue = pLast;
            if (pUserData is not null)
            {
                pUserData += HandleTableConstants.HANDLE_HANDLES_PER_CLUMP;
            }
        }
        while (clumpMask != 0);
    }

    private static void BlockAgeBlocksEphemeral(
        TableSegment* pSegment,
        uint uBlock,
        uint uCount,
        ScanCallbackInfo* info)
    {
        uint* generations = (uint*)pSegment->Header.rgGeneration + uBlock;
        uint* generationsEnd = generations + uCount;
        do
        {
            *generations =
                ComputeAgedClumps(*generations, info->dwAgeMask);
            generations++;
        }
        while (generations < generationsEnd);
    }

    private static void BlockResetAgeMapForBlocks(
        TableSegment* pSegment,
        uint uBlock,
        uint uCount,
        ScanCallbackInfo* info)
    {
        uint* generations = (uint*)pSegment->Header.rgGeneration + uBlock;
        uint* generationsEnd = generations + uCount;
        do
        {
            uint clumpMask = ComputeClumpMask(*generations, info->dwAgeMask);
            if (clumpMask != 0)
            {
                BlockResetAgeMapForBlocksWorker(
                    pSegment,
                    generations,
                    clumpMask);
            }

            generations++;
        }
        while (generations < generationsEnd);
    }

    private static void BlockResetAgeMapForBlocksWorker(
        TableSegment* pSegment,
        uint* generations,
        uint clumpMask)
    {
        uint clump =
            (uint)((byte*)generations - pSegment->Header.rgGeneration);
        nuint* pValue =
            (nuint*)pSegment->rgValue +
            (clump * HandleTableConstants.HANDLE_HANDLES_PER_CLUMP);
        uint block = clump / HandleTableConstants.HANDLE_CLUMPS_PER_BLOCK;
        nuint* pUserData = null;
        if (pSegment->Header.rgBlockType[block] ==
            (byte)HandleType.HNDTYPE_DEPENDENT)
        {
            byte userDataBlock = pSegment->Header.rgUserData[block];
            Debug.Assert(userDataBlock != HandleTableConstants.BLOCK_INVALID);
            pUserData =
                (nuint*)pSegment->rgValue +
                (userDataBlock * HandleTableConstants.HANDLE_HANDLES_PER_BLOCK);
        }

        do
        {
            nuint* pLast =
                pValue + HandleTableConstants.HANDLE_HANDLES_PER_CLUMP;
            if ((clumpMask & HandleTableConstants.GEN_CLUMP_0_MASK) != 0)
            {
                int minAge = MaxGenerationAge;
                for (; pValue < pLast; pValue++)
                {
                    nuint value = *pValue;
                    if (!HandleTableCore.HndIsNullOrDestroyedHandle(value))
                    {
                        int age =
                            HandleTableManager.GetConvertedGeneration((byte*)value);
                        if (age < minAge)
                        {
                            minAge = age;
                        }

                        if (pUserData is not null)
                        {
                            nuint handleIndex =
                                ((nuint)pValue / HandleTableConstants.HANDLE_SIZE) &
                                (HandleTableConstants.HANDLE_HANDLES_PER_BLOCK - 1);
                            nuint secondary = pUserData[handleIndex];
                            if (secondary != 0)
                            {
                                int secondaryAge =
                                    HandleTableManager.GetConvertedGeneration(
                                        (byte*)secondary);
                                if (secondaryAge < minAge)
                                {
                                    minAge = secondaryAge;
                                }
                            }
                        }
                    }
                }

                Debug.Assert((uint)minAge <= byte.MaxValue);
                pSegment->Header.rgGeneration[clump] =
                    unchecked((byte)minAge);
            }

            clumpMask =
                HandleTableConstants.NEXT_CLUMP_IN_MASK(clumpMask);
            pValue = pLast;
            clump++;
        }
        while (clumpMask != 0);
    }

    private static TableSegment* FullSegmentIterator(HandleTable* pTable, TableSegment* pPrevSegment)
    {
        uint sequence = pPrevSegment is null ? 0 : (uint)pPrevSegment->Header.bSequence + 1;

        for (; ; )
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

    private static TableSegment* QuickSegmentIterator(
        HandleTable* pTable,
        TableSegment* pPrevSegment) =>
        pPrevSegment is null
            ? pTable->pSegmentList
            : pPrevSegment->Header.pNextSegment;

    private static TableSegment* StandardSegmentIterator(
        HandleTable* pTable,
        TableSegment* pPrevSegment)
    {
        TableSegment* pNextSegment =
            QuickSegmentIterator(pTable, pPrevSegment);

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

        for (; ; )
        {
            for (; ; )
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
            for (; ; )
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
