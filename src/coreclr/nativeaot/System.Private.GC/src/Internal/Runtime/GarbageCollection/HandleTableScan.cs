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
        public TableSegment* pCurrentSegment;
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

        uint flags = sc->concurrent != 0
            ? HandleTableConstants.HNDGCF_ASYNC
            : HandleTableConstants.HNDGCF_NORMAL;
        uint type = (uint)HandleType.HNDTYPE_PINNED;
        TraceHandleTables(
            &PinObject,
            (nuint)sc,
            (nuint)fn,
            &type,
            1,
            condemned,
            max_gen,
            flags,
            sc);

        type = (uint)HandleType.HNDTYPE_ASYNCPINNED;
        TraceHandleTables(
            &AsyncPinObject,
            (nuint)sc,
            (nuint)fn,
            &type,
            1,
            condemned,
            max_gen,
            flags,
            sc);

        TraceVariableHandles(
            &PinObject,
            sc,
            (nuint)fn,
            ObjectHandle.VHT_PINNED,
            condemned,
            max_gen,
            flags);
    }

    public static void Ref_TraceNormalRoots(
        int condemned,
        int max_gen,
        ScanContext* sc,
        delegate*<byte**, ScanContext*, uint, void> fn)
    {
        Debug.Assert((uint)condemned <= (uint)max_gen);

        uint* types = stackalloc uint[]
        {
            (uint)HandleType.HNDTYPE_STRONG,
            (uint)HandleType.HNDTYPE_SIZEDREF,
        };
        uint flags = sc->concurrent != 0
            ? HandleTableConstants.HNDGCF_ASYNC
            : HandleTableConstants.HNDGCF_NORMAL;
        TraceHandleTables(
            &PromoteObject,
            (nuint)sc,
            (nuint)fn,
            types,
            condemned >= max_gen &&
                !ManagedGCHeap.ConcurrentCollectionInProgress
                    ? 1u
                    : 2u,
            condemned,
            max_gen,
            flags,
            sc);

        TraceVariableHandles(
            &PromoteObject,
            sc,
            (nuint)fn,
            ObjectHandle.VHT_STRONG,
            condemned,
            max_gen,
            flags);

        if (sc->concurrent == 0)
        {
            uint type = (uint)HandleType.HNDTYPE_REFCOUNTED;
            TraceHandleTables(
                &PromoteRefCounted,
                (nuint)sc,
                (nuint)fn,
                &type,
                1,
                condemned,
                max_gen,
                flags,
                sc);
        }
    }

    public static void Ref_ScanHandlesForProfilerAndETW(
        int max_gen,
        ScanContext* sc,
        delegate* unmanaged<byte**, byte*, uint, ScanContext*, byte, void> fn)
    {
        uint* types = stackalloc uint[]
        {
            (uint)HandleType.HNDTYPE_WEAK_SHORT,
            (uint)HandleType.HNDTYPE_WEAK_LONG,
            (uint)HandleType.HNDTYPE_STRONG,
            (uint)HandleType.HNDTYPE_REFCOUNTED,
            (uint)HandleType.HNDTYPE_WEAK_NATIVE_COM,
            (uint)HandleType.HNDTYPE_PINNED,
            (uint)HandleType.HNDTYPE_VARIABLE,
            (uint)HandleType.HNDTYPE_ASYNCPINNED,
            (uint)HandleType.HNDTYPE_SIZEDREF,
            (uint)HandleType.HNDTYPE_WEAK_INTERIOR_POINTER,
#if FEATURE_JAVAMARSHAL
            (uint)HandleType.HNDTYPE_CROSSREFERENCE,
#endif
        };

        TraceHandleTables(
            &ScanPointerForProfilerAndETW,
            (nuint)sc,
            (nuint)fn,
            types,
#if FEATURE_JAVAMARSHAL
            11,
#else
            10,
#endif
            max_gen,
            max_gen,
            HandleTableConstants.HNDGCF_NORMAL,
            sc);

        TraceVariableHandles(
            &ScanPointerForProfilerAndETW,
            sc,
            (nuint)fn,
            ObjectHandle.VHT_WEAK_SHORT |
                ObjectHandle.VHT_WEAK_LONG |
                ObjectHandle.VHT_STRONG,
            max_gen,
            max_gen,
            HandleTableConstants.HNDGCF_NORMAL);
    }

    public static void Ref_ScanDependentHandlesForProfilerAndETW(
        int max_gen,
        ScanContext* sc,
        delegate* unmanaged<byte**, byte*, uint, ScanContext*, byte, void> fn)
    {
        DiagDependentScanInfo info = new()
        {
            callback = fn,
        };
        uint type = (uint)HandleType.HNDTYPE_DEPENDENT;
        TraceHandleTables(
            &TraceDependentHandleForProfilerAndETW,
            (nuint)sc,
            (nuint)(void*)&info,
            &type,
            1,
            max_gen,
            max_gen,
            HandleTableConstants.HNDGCF_EXTRAINFO |
                HandleTableConstants.HNDGCF_NORMAL,
            sc);
    }

    private struct DiagDependentScanInfo
    {
        public delegate* unmanaged<byte**, byte*, uint, ScanContext*, byte, void> callback;
    }

    private static void TraceDependentHandleForProfilerAndETW(
        byte** pObjRef,
        nuint* pExtraInfo,
        nuint param1,
        nuint param2)
    {
        if (pObjRef is null ||
            pExtraInfo is null ||
            *pObjRef is null ||
            *pExtraInfo == 0)
        {
            return;
        }

        DiagDependentScanInfo* info =
            (DiagDependentScanInfo*)param2;
        ScanPointerForProfilerAndETW(
            pObjRef,
            null,
            param1,
            (nuint)info->callback);
    }

    private static void ScanPointerForProfilerAndETW(
        byte** pObjRef,
        nuint* pExtraInfo,
        nuint param1,
        nuint param2)
    {
        _ = pExtraInfo;

        OBJECTHANDLE handle = new(pObjRef);
        HandleType type =
            (HandleType)HandleTableCore.HandleFetchType(handle);
        uint rootFlags = 0;
        bool isDependent = false;
        switch (type)
        {
            case HandleType.HNDTYPE_DEPENDENT:
                isDependent = true;
                break;

            case HandleType.HNDTYPE_WEAK_SHORT:
            case HandleType.HNDTYPE_WEAK_LONG:
            case HandleType.HNDTYPE_WEAK_INTERIOR_POINTER:
            case HandleType.HNDTYPE_WEAK_NATIVE_COM:
                rootFlags |= (uint)EtwGCRootFlags.kEtwGCRootFlagsWeakRef;
                break;

            case HandleType.HNDTYPE_STRONG:
            case HandleType.HNDTYPE_SIZEDREF:
#if FEATURE_JAVAMARSHAL
            case HandleType.HNDTYPE_CROSSREFERENCE:
#endif
                break;

            case HandleType.HNDTYPE_PINNED:
            case HandleType.HNDTYPE_ASYNCPINNED:
                rootFlags |= (uint)EtwGCRootFlags.kEtwGCRootFlagsPinning;
                break;

            case HandleType.HNDTYPE_VARIABLE:
                uint variableType = ObjectHandle.GetVariableHandleType(handle);
                if ((variableType &
                    (ObjectHandle.VHT_WEAK_SHORT |
                     ObjectHandle.VHT_WEAK_LONG)) != 0)
                {
                    rootFlags |=
                        (uint)EtwGCRootFlags.kEtwGCRootFlagsWeakRef;
                }

                if ((variableType & ObjectHandle.VHT_PINNED) != 0)
                {
                    rootFlags |=
                        (uint)EtwGCRootFlags.kEtwGCRootFlagsPinning;
                }

                break;

            case HandleType.HNDTYPE_REFCOUNTED:
                rootFlags |=
                    (uint)EtwGCRootFlags.kEtwGCRootFlagsRefCounted;
                if (*pObjRef is not null &&
                    GCToEEInterface.RefCountedHandleCallbacks(*pObjRef) == 0)
                {
                    rootFlags |=
                        (uint)EtwGCRootFlags.kEtwGCRootFlagsWeakRef;
                }

                break;

            default:
                Debug.Assert(false);
                break;
        }

        byte* secondary = isDependent
            ? (byte*)HandleTableManager.HndGetHandleExtraInfo(handle)
            : null;
        delegate* unmanaged<byte**, byte*, uint, ScanContext*, byte, void> callback =
            (delegate* unmanaged<byte**, byte*, uint, ScanContext*, byte, void>)param2;
        callback(
            pObjRef,
            secondary,
            rootFlags,
            (ScanContext*)param1,
            isDependent ? (byte)1 : (byte)0);
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
                HandleTableConstants.HNDGCF_EXTRAINFO |
                    (context->m_pScanContext->concurrent != 0
                        ? HandleTableConstants.HNDGCF_ASYNC
                        : HandleTableConstants.HNDGCF_NORMAL),
                        context->m_pScanContext);

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
        uint* types = stackalloc uint[]
        {
            type,
            (uint)HandleType.HNDTYPE_REFCOUNTED,
            (uint)HandleType.HNDTYPE_WEAK_INTERIOR_POINTER,
        };
        uint flags = sc->concurrent != 0
            ? HandleTableConstants.HNDGCF_ASYNC
            : HandleTableConstants.HNDGCF_NORMAL;
        TraceHandleTables(
            checkPromoted,
            0,
            0,
            types,
            3,
            condemned,
            max_gen,
            flags,
            sc);

        TraceVariableHandles(
            checkPromoted,
            sc,
            0,
            ObjectHandle.VHT_WEAK_LONG,
            condemned,
            max_gen,
            flags);
    }

    public static void Ref_ScanDependentHandlesForClearing(int condemned, int max_gen, ScanContext* sc)
    {
        uint type = (uint)HandleType.HNDTYPE_DEPENDENT;
        TraceHandleTables(
            &ClearDependentHandle,
            0,
            0,
            &type,
            1,
            condemned,
            max_gen,
            HandleTableConstants.HNDGCF_EXTRAINFO |
                (sc->concurrent != 0
                    ? HandleTableConstants.HNDGCF_ASYNC
                    : HandleTableConstants.HNDGCF_NORMAL),
                    sc);
    }

    public static void Ref_CheckAlive(
        int condemned,
        int max_gen,
        ScanContext* sc,
        delegate*<byte**, nuint*, nuint, nuint, void> checkPromoted)
    {
        uint* types = stackalloc uint[]
        {
            (uint)HandleType.HNDTYPE_WEAK_SHORT,
            (uint)HandleType.HNDTYPE_WEAK_NATIVE_COM,
        };
        uint flags = sc->concurrent != 0
            ? HandleTableConstants.HNDGCF_ASYNC
            : HandleTableConstants.HNDGCF_NORMAL;
        TraceHandleTables(
            checkPromoted,
            0,
            0,
            types,
            2,
            condemned,
            max_gen,
            flags,
            sc);

        TraceVariableHandles(
            checkPromoted,
            sc,
            0,
            ObjectHandle.VHT_WEAK_SHORT,
            condemned,
            max_gen,
            flags);
    }

    public static void Ref_UpdatePointers(
        int condemned,
        int max_gen,
        ScanContext* sc,
        delegate*<byte**, ScanContext*, uint, void> fn)
    {
        Debug.Assert((uint)condemned <= (uint)max_gen);

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
            (uint)HandleType.HNDTYPE_WEAK_NATIVE_COM,
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
            7,
#else
            6,
#endif
            condemned,
            max_gen,
            sc->concurrent != 0
                ? HandleTableConstants.HNDGCF_ASYNC
                : HandleTableConstants.HNDGCF_NORMAL,
                sc);

        TraceVariableHandles(
            &UpdatePointer,
            sc,
            (nuint)fn,
            ObjectHandle.VHT_WEAK_SHORT |
                ObjectHandle.VHT_WEAK_LONG |
                ObjectHandle.VHT_STRONG,
            condemned,
            max_gen,
            sc->concurrent != 0
                ? HandleTableConstants.HNDGCF_ASYNC
                : HandleTableConstants.HNDGCF_NORMAL);
    }

    public static void Ref_UpdatePinnedPointers(
        int condemned,
        int max_gen,
        ScanContext* sc,
        delegate*<byte**, ScanContext*, uint, void> fn)
    {
        Debug.Assert((uint)condemned <= (uint)max_gen);

        uint* types = stackalloc uint[]
        {
            (uint)HandleType.HNDTYPE_PINNED,
            (uint)HandleType.HNDTYPE_ASYNCPINNED,
        };
        uint flags = sc->concurrent != 0
            ? HandleTableConstants.HNDGCF_ASYNC
            : HandleTableConstants.HNDGCF_NORMAL;
        TraceHandleTables(
            &UpdatePointerPinned,
            (nuint)sc,
            (nuint)fn,
            types,
            2,
            condemned,
            max_gen,
            flags,
            sc);

        TraceVariableHandles(
            &UpdatePointerPinned,
            sc,
            (nuint)fn,
            ObjectHandle.VHT_PINNED,
            condemned,
            max_gen,
            flags);
    }

    public static void Ref_ScanDependentHandlesForRelocation(
        int condemned,
        int max_gen,
        ScanContext* sc,
        delegate*<byte**, ScanContext*, uint, void> fn)
    {
        Debug.Assert((uint)condemned <= (uint)max_gen);

        uint type = (uint)HandleType.HNDTYPE_DEPENDENT;
        TraceHandleTables(
            &UpdateDependentHandle,
            (nuint)sc,
            (nuint)fn,
            &type,
            1,
            condemned,
            max_gen,
            HandleTableConstants.HNDGCF_EXTRAINFO |
                (sc->concurrent != 0
                    ? HandleTableConstants.HNDGCF_ASYNC
                    : HandleTableConstants.HNDGCF_NORMAL),
                    sc);
    }

    public static void Ref_ScanWeakInteriorPointersForRelocation(
        int condemned,
        int max_gen,
        ScanContext* sc,
        delegate*<byte**, ScanContext*, uint, void> fn)
    {
        Debug.Assert((uint)condemned <= (uint)max_gen);

        uint type = (uint)HandleType.HNDTYPE_WEAK_INTERIOR_POINTER;
        TraceHandleTables(
            &UpdateWeakInteriorHandle,
            (nuint)sc,
            (nuint)fn,
            &type,
            1,
            condemned,
            max_gen,
            HandleTableConstants.HNDGCF_EXTRAINFO |
                (sc->concurrent != 0
                    ? HandleTableConstants.HNDGCF_ASYNC
                    : HandleTableConstants.HNDGCF_NORMAL),
                    sc);
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
            (uint)HandleType.HNDTYPE_VARIABLE,
            (uint)HandleType.HNDTYPE_DEPENDENT,
            (uint)HandleType.HNDTYPE_REFCOUNTED,
            (uint)HandleType.HNDTYPE_WEAK_NATIVE_COM,
            (uint)HandleType.HNDTYPE_ASYNCPINNED,
            (uint)HandleType.HNDTYPE_SIZEDREF,
            (uint)HandleType.HNDTYPE_WEAK_INTERIOR_POINTER,
#if FEATURE_JAVAMARSHAL
            (uint)HandleType.HNDTYPE_CROSSREFERENCE,
#endif
        };
        TraceHandleTables(
            null,
            0,
            0,
            types,
#if FEATURE_JAVAMARSHAL
            12,
#else
            11,
#endif
            condemned,
            max_gen,
            HandleTableConstants.HNDGCF_AGE,
            sc);
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
            (uint)HandleType.HNDTYPE_VARIABLE,
            (uint)HandleType.HNDTYPE_DEPENDENT,
            (uint)HandleType.HNDTYPE_REFCOUNTED,
            (uint)HandleType.HNDTYPE_WEAK_NATIVE_COM,
            (uint)HandleType.HNDTYPE_ASYNCPINNED,
            (uint)HandleType.HNDTYPE_SIZEDREF,
            (uint)HandleType.HNDTYPE_WEAK_INTERIOR_POINTER,
#if FEATURE_JAVAMARSHAL
            (uint)HandleType.HNDTYPE_CROSSREFERENCE,
#endif
        };
        ResetAgeMaps(
            types,
#if FEATURE_JAVAMARSHAL
            12,
#else
            11,
#endif
            condemned,
            max_gen);
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
            type == (byte)HandleType.HNDTYPE_VARIABLE ||
            type == (byte)HandleType.HNDTYPE_DEPENDENT ||
            type == (byte)HandleType.HNDTYPE_REFCOUNTED ||
            type == (byte)HandleType.HNDTYPE_WEAK_NATIVE_COM ||
            type == (byte)HandleType.HNDTYPE_ASYNCPINNED ||
            type == (byte)HandleType.HNDTYPE_SIZEDREF ||
            type == (byte)HandleType.HNDTYPE_WEAK_INTERIOR_POINTER)
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

                    delegate*<byte*, byte*, void*, void> ageCallback =
                        &UpdateMinimumAsyncPinnedAge;
                    GCToEEInterface.WalkAsyncPinned(
                        (byte*)value,
                        &minAge,
                        (delegate* unmanaged<byte*, byte*, void*, void>)ageCallback);

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
        uint flags,
        ScanContext* sc)
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
                        // objecthandle.cpp: each GC worker scans a disjoint stride of the per-CPU
                        // handle tables (start = its heap number, step = the heap count). Scanning
                        // every slot on every worker concurrently races the block scans and
                        // nondeterministically drops handles.
                        int uCPUindex = sc is not null ? ObjectHandle.getSlotNumber(sc) : 0;
                        int uCPUstep = sc is not null ? ObjectHandle.getThreadCount(sc) : 1;
                        for (; uCPUindex < uCPUlimit; uCPUindex += uCPUstep)
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

    private struct VariableScanInfo
    {
        public nuint enableMask;
        public delegate*<byte**, nuint*, nuint, nuint, void> trace;
        public nuint param2;
    }

    private static void TraceVariableHandles(
        delegate*<byte**, nuint*, nuint, nuint, void> trace,
        ScanContext* sc,
        nuint param2,
        uint enableMask,
        int condemned,
        int max_gen,
        uint flags)
    {
        uint type = (uint)HandleType.HNDTYPE_VARIABLE;
        VariableScanInfo info = new()
        {
            enableMask = enableMask,
            trace = trace,
            param2 = param2,
        };

        TraceHandleTables(
            &VariableTraceDispatcher,
            (nuint)sc,
            (nuint)(void*)&info,
            &type,
            1,
            condemned,
            max_gen,
            HandleTableConstants.HNDGCF_EXTRAINFO | flags,
            sc);
    }

    private static void VariableTraceDispatcher(
        byte** pObjRef,
        nuint* pExtraInfo,
        nuint param1,
        nuint param2)
    {
        Debug.Assert(pExtraInfo is not null);
        VariableScanInfo* info = (VariableScanInfo*)param2;
        if ((*pExtraInfo & info->enableMask) != 0)
        {
            info->trace(pObjRef, null, param1, info->param2);
        }
    }

    public static void Ref_TraceRefCountHandles(
        delegate*<byte**, nuint*, nuint, nuint, void> callback,
        nuint param1,
        nuint param2)
    {
        uint type = (uint)HandleType.HNDTYPE_REFCOUNTED;
        EnumerateHandleTables(callback, param1, param2, &type, 1);
    }

    public static void Ref_ScanSizedRefHandles(
        int condemned,
        int max_gen,
        ScanContext* sc,
        delegate*<byte**, ScanContext*, uint, void> fn)
    {
        Debug.Assert(condemned == max_gen);
        uint type = (uint)HandleType.HNDTYPE_SIZEDREF;
        TraceHandleTables(
            &CalculateSizedRefSize,
            (nuint)sc,
            (nuint)fn,
            &type,
            1,
            max_gen,
            max_gen,
            HandleTableConstants.HNDGCF_EXTRAINFO |
                (sc->concurrent != 0
                    ? HandleTableConstants.HNDGCF_ASYNC
                    : HandleTableConstants.HNDGCF_NORMAL),
                    sc);
    }

    public static void Ref_NullBridgeObjectsWeakRefs(
        nuint length,
        void* unreachableObjectHandles)
    {
#if FEATURE_JAVAMARSHAL
        uint* types = stackalloc uint[]
        {
            (uint)HandleType.HNDTYPE_WEAK_SHORT,
            (uint)HandleType.HNDTYPE_WEAK_LONG,
        };
        BridgeWeakRefScanInfo info = new()
        {
            length = length,
            handles = (byte***)unreachableObjectHandles,
        };
        EnumerateHandleTables(
            &NullBridgeObjectWeakRef,
            (nuint)(void*)&info,
            0,
            types,
            2);
#else
        _ = length;
        _ = unreachableObjectHandles;
#endif
    }

#if FEATURE_JAVAMARSHAL
    public static byte** Ref_ScanBridgeObjects(
        int condemned,
        int max_gen,
        ScanContext* sc,
        nuint* count)
    {
        GCBridge.BridgeResetData();
        uint type = (uint)HandleType.HNDTYPE_CROSSREFERENCE;
        TraceHandleTables(
            &GetBridgeObjectsForProcessing,
            (nuint)sc,
            0,
            &type,
            1,
            condemned,
            max_gen,
            HandleTableConstants.HNDGCF_EXTRAINFO,
            sc);

        MarkCrossReferencesArgs* args =
            GCBridge.ProcessBridgeObjects();
        if (args is not null)
        {
            GCToEEInterface.TriggerClientBridgeProcessing(args);
        }

        return GCBridge.GetRegisteredBridges(count);
    }

    private static void GetBridgeObjectsForProcessing(
        byte** pObjRef,
        nuint* pExtraInfo,
        nuint param1,
        nuint param2)
    {
        _ = param1;
        _ = param2;
        Debug.Assert(pExtraInfo is not null);
        if (!ManagedGCHeap.IsPromoted(*pObjRef))
        {
            GCBridge.RegisterBridgeObject(*pObjRef, *pExtraInfo);
        }
    }
#endif

    private static void EnumerateHandleTables(
        delegate*<byte**, nuint*, nuint, nuint, void> callback,
        nuint param1,
        nuint param2,
        uint* types,
        uint typeCount)
    {
        HandleTableMap* walk =
            (HandleTableMap*)Unsafe.AsPointer(ref ObjectHandle.g_HandleTableMap);
        while (walk is not null)
        {
            for (uint bucketIndex = 0;
                 bucketIndex < HandleTableConstants.INITIAL_HANDLE_TABLE_ARRAY_SIZE;
                 bucketIndex++)
            {
                HandleTableBucket* bucket = walk->pBuckets[bucketIndex];
                if (bucket is null)
                {
                    continue;
                }

                for (int slot = 0; slot < ObjectHandle.getNumberOfSlots(); slot++)
                {
                    HandleTable* table = bucket->pTable[slot];
                    if (table is not null)
                    {
                        uint flags = TypesRequireUserDataScanning(
                            table,
                            types,
                            typeCount)
                                ? HandleTableConstants.HNDGCF_EXTRAINFO
                                : HandleTableConstants.HNDGCF_NORMAL;
                        HndScanHandlesForGC(
                            table,
                            callback,
                            param1,
                            param2,
                            types,
                            typeCount,
                            0,
                            0,
                            flags);
                    }
                }
            }

            walk = walk->pNext;
        }
    }

#if FEATURE_JAVAMARSHAL
    private struct BridgeWeakRefScanInfo
    {
        public nuint length;
        public byte*** handles;
    }

    private static void NullBridgeObjectWeakRef(
        byte** handle,
        nuint* pExtraInfo,
        nuint param1,
        nuint param2)
    {
        _ = pExtraInfo;
        _ = param2;
        BridgeWeakRefScanInfo* info = (BridgeWeakRefScanInfo*)param1;
        byte* weakRef = *handle;
        for (nuint i = 0; i < info->length; i++)
        {
            if (weakRef == *info->handles[i])
            {
                *handle = null;
            }
        }
    }
#endif

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

        bool enumUserData =
            (flags & HandleTableConstants.HNDGCF_EXTRAINFO) != 0 &&
            TypesRequireUserDataScanning(pTable, types, typeCount);

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
        bool full = condemned >= max_gen;
        if ((flags & HandleTableConstants.HNDGCF_ASYNC) != 0)
        {
            HandleTableCrstHolderWithState holder =
                new(&pTable->Lock);
            TableScanHandlesAsync(
                pTable,
                types,
                typeCount,
                full,
                condemned == 0,
                action,
                &info,
                &holder);
            holder.Dispose();
            return;
        }

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

    private static void TableScanHandlesAsync(
        HandleTable* pTable,
        uint* types,
        uint typeCount,
        bool full,
        bool useQuickIterator,
        byte action,
        ScanCallbackInfo* info,
        HandleTableCrstHolderWithState* holder)
    {
        if (pTable->pAsyncScanInfo is not null)
        {
            Debug.Assert(false);
            return;
        }

        ScanQNode initialNode = default;
        AsyncScanInfo asyncInfo = default;
        asyncInfo.pCallbackInfo = info;
        asyncInfo.pScanQueue = &initialNode;
        pTable->pAsyncScanInfo = &asyncInfo;

        byte* typeInclusion =
            stackalloc byte[HandleTableConstants.HANDLE_MAX_INTERNAL_TYPES + 1];
        if (typeCount > 1)
        {
            BuildInclusionMap(typeInclusion, types, typeCount);
        }

        TableSegment* segment = null;
        for (; ; )
        {
            segment = full
                ? FullSegmentIterator(pTable, segment)
                : useQuickIterator
                    ? QuickSegmentIterator(pTable, segment)
                    : StandardSegmentIterator(pTable, segment);
            if (segment is null)
            {
                break;
            }

            if (typeCount == 1)
            {
                QueueSegmentByTypeChain(segment, *types, &asyncInfo);
            }
            else if (typeCount > 1)
            {
                QueueSegmentByTypeMap(segment, typeInclusion, &asyncInfo);
            }

            if (asyncInfo.pQueueTail is not null)
            {
                ProcessQueuedBlocksAsync(
                    segment,
                    &asyncInfo,
                    full,
                    action,
                    info,
                    holder);
            }
        }

        ScanQNode* node = initialNode.pNext;
        while (node is not null)
        {
            ScanQNode* next = node->pNext;
            SyncImports.ManagedGC_Free(node);
            node = next;
        }

        pTable->pAsyncScanInfo = null;
    }

    private static void QueueSegmentByTypeChain(
        TableSegment* segment,
        uint type,
        AsyncScanInfo* asyncInfo)
    {
        uint block = segment->Header.rgTail[type];
        if (block == HandleTableConstants.BLOCK_INVALID)
        {
            return;
        }

        block = segment->Header.rgAllocation[block];
        uint head = block;
        do
        {
            uint last;
            uint next = block;
            do
            {
                last = next + 1;
                next = segment->Header.rgAllocation[next];
            }
            while (next == last && next != head);

            QueueBlocksForAsyncScan(asyncInfo, block, last - block);
            block = next;
        }
        while (block != head);
    }

    private static void QueueSegmentByTypeMap(
        TableSegment* segment,
        byte* typeInclusion,
        AsyncScanInfo* asyncInfo)
    {
        uint block = 0;
        uint limit = segment->Header.bEmptyLine;
        while (block < limit)
        {
            while (block < limit &&
                !IsBlockIncluded(segment, block, typeInclusion))
            {
                block++;
            }

            if (block >= limit)
            {
                return;
            }

            uint first = block;
            do
            {
                block++;
            }
            while (block < limit &&
                IsBlockIncluded(segment, block, typeInclusion));

            QueueBlocksForAsyncScan(asyncInfo, first, block - first);
            block++;
        }
    }

    private static void QueueBlocksForAsyncScan(
        AsyncScanInfo* asyncInfo,
        uint block,
        uint count)
    {
        const uint RangesPerNode =
            HandleTableConstants.HANDLE_BLOCKS_PER_SEGMENT / 4;

        ScanQNode* node = asyncInfo->pQueueTail;
        if (node is not null && node->uEntries >= RangesPerNode)
        {
            if (node->pNext is null)
            {
                node->pNext = (ScanQNode*)SyncImports.ManagedGC_AllocZeroed(
                    (nuint)sizeof(ScanQNode));
                if (node->pNext is null)
                {
                    return;
                }
            }

            node = node->pNext;
        }
        else if (node is null)
        {
            node = asyncInfo->pScanQueue;
        }

        ScanRange* ranges = (ScanRange*)node->rgRange;
        ranges[node->uEntries].uIndex = block;
        ranges[node->uEntries].uCount = count;
        node->uEntries++;
        asyncInfo->pQueueTail = node;
    }

    private static void ProcessQueuedBlocksAsync(
        TableSegment* segment,
        AsyncScanInfo* asyncInfo,
        bool full,
        byte action,
        ScanCallbackInfo* info,
        HandleTableCrstHolderWithState* holder)
    {
        info->pCurrentSegment = segment;
        for (ScanQNode* node = asyncInfo->pScanQueue;
             node is not null;
             node = node->pNext)
        {
            ScanRange* ranges = (ScanRange*)node->rgRange;
            for (uint range = 0; range < node->uEntries; range++)
            {
                LockBlocks(
                    segment,
                    ranges[range].uIndex,
                    ranges[range].uCount);
            }
        }

        holder->Release();
        for (ScanQNode* node = asyncInfo->pScanQueue;
             node is not null;
             node = node->pNext)
        {
            ScanRange* ranges = (ScanRange*)node->rgRange;
            for (uint range = 0; range < node->uEntries; range++)
            {
                if (full)
                {
                    if (action == BlockActionScan)
                    {
                        BlockScanBlocks(
                            segment,
                            ranges[range].uIndex,
                            ranges[range].uCount,
                            info->pfnScan,
                            info->param1,
                            info->param2,
                            info->fEnumUserData != 0);
                    }
                    else
                    {
                        AgeFullBlocks(
                            segment,
                            ranges[range].uIndex,
                            ranges[range].uCount);
                    }
                }
                else
                {
                    ProcessEphemeralBlocks(
                        segment,
                        ranges[range].uIndex,
                        ranges[range].uCount,
                        action,
                        info);
                }
            }
        }

        holder->Acquire();
        for (ScanQNode* node = asyncInfo->pScanQueue;
             node is not null;
             node = node->pNext)
        {
            ScanRange* ranges = (ScanRange*)node->rgRange;
            for (uint range = 0; range < node->uEntries; range++)
            {
                UnlockBlocks(
                    segment,
                    ranges[range].uIndex,
                    ranges[range].uCount);
            }

            node->uEntries = 0;
        }

        info->pCurrentSegment = null;
        asyncInfo->pQueueTail = null;
    }

    private static void LockBlocks(
        TableSegment* segment,
        uint block,
        uint count)
    {
        uint limit = block + count;
        while (block < limit)
        {
            HandleTableCore.BlockLock(segment, block++);
        }
    }

    private static void UnlockBlocks(
        TableSegment* segment,
        uint block,
        uint count)
    {
        uint limit = block + count;
        while (block < limit)
        {
            HandleTableCore.BlockUnlock(segment, block++);
        }
    }

    private static void AgeFullBlocks(
        TableSegment* segment,
        uint block,
        uint count)
    {
        uint limit = block + count;
        while (block < limit)
        {
            AgeFullGcBlock(segment, block++);
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

                        delegate*<byte*, byte*, void*, void> ageCallback =
                            &UpdateMinimumAsyncPinnedAge;
                        GCToEEInterface.WalkAsyncPinned(
                            (byte*)value,
                            &minAge,
                            (delegate* unmanaged<byte*, byte*, void*, void>)ageCallback);

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

    private static void PromoteObject(
        byte** pObjRef,
        nuint* pExtraInfo,
        nuint lp1,
        nuint lp2)
    {
        UpdatePointer(pObjRef, pExtraInfo, lp1, lp2);
    }

    private static void PromoteRefCounted(
        byte** pObjRef,
        nuint* pExtraInfo,
        nuint lp1,
        nuint lp2)
    {
        _ = pExtraInfo;
        ScanContext* sc = (ScanContext*)lp1;
        Debug.Assert(sc->concurrent == 0);

        byte* obj = (byte*)GCEnv.VolatileLoad((nuint*)pObjRef);
        byte* oldObj = obj;
        if (!HandleTableCore.HndIsNullOrDestroyedHandle((nuint)obj) &&
            !ManagedGCHeap.IsPromoted(obj) &&
            GCToEEInterface.RefCountedHandleCallbacks(obj) != 0)
        {
            Debug.Assert(lp2 != 0);
            delegate*<byte**, ScanContext*, uint, void> callback =
                (delegate*<byte**, ScanContext*, uint, void>)lp2;
            callback(&obj, sc, 0);
        }

        Debug.Assert(oldObj == obj);
    }

    private static void AsyncPinObject(
        byte** pObjRef,
        nuint* pExtraInfo,
        nuint lp1,
        nuint lp2)
    {
        _ = pExtraInfo;
        Debug.Assert(lp2 != 0);

        delegate*<byte**, ScanContext*, uint, void> callback =
            (delegate*<byte**, ScanContext*, uint, void>)lp2;
        ScanContext* sc = (ScanContext*)lp1;
        callback(pObjRef, sc, 0);
        byte* pinnedObject = *pObjRef;
        if (!HandleTableCore.HndIsNullOrDestroyedHandle((nuint)pinnedObject))
        {
            GCToEEInterface.WalkAsyncPinnedForPromotion(
                pinnedObject,
                sc,
                (delegate* unmanaged<byte**, ScanContext*, uint, void>)callback);
        }
    }

    private static void CalculateSizedRefSize(
        byte** pObjRef,
        nuint* pExtraInfo,
        nuint lp1,
        nuint lp2)
    {
        Debug.Assert(pExtraInfo is not null);
        Debug.Assert(lp2 != 0);

        ScanContext* sc = (ScanContext*)lp1;
        delegate*<byte**, ScanContext*, uint, void> callback =
            (delegate*<byte**, ScanContext*, uint, void>)lp2;
        nuint sizeBegin =
            ManagedGCHeap.GetPromotedBytesForHandleScan(sc->thread_number);
        callback(pObjRef, sc, 0);
        nuint sizeEnd =
            ManagedGCHeap.GetPromotedBytesForHandleScan(sc->thread_number);
        *pExtraInfo = sizeEnd - sizeBegin;
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

    private static void UpdateMinimumAsyncPinnedAge(
        byte* from,
        byte* to,
        void* context)
    {
        _ = from;
        int* minAge = (int*)context;
        int generation = HandleTableManager.GetConvertedGeneration(to);
        if (*minAge > generation)
        {
            *minAge = generation;
        }
    }
}
