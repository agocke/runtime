// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the dependency-closed parts of gcscan.cpp, in their original order. The bounded
// synchronous handle-table scan is in HandleTableScan.cs with its table-scanning helpers.

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection;

// The dependent-handle scan state belongs to a GC heap/worker for one mark phase. The WKS
// configuration has one context because it scans one handle-table slot synchronously.
internal unsafe struct DhContext
{
    public byte m_fUnpromotedPrimaries;
    public byte m_fPromoted;
    public delegate*<byte**, ScanContext*, uint, void> m_pfnPromoteFunction;
    public int m_iCondemned;
    public int m_iMaxGen;
    public ScanContext* m_pScanContext;
}

internal static unsafe class GCScan
{
    private static int m_GcStructuresInvalidCnt;

    public static bool GetGcRuntimeStructuresValid()
    {
        int invalidCount = System.Threading.Volatile.Read(ref m_GcStructuresInvalidCnt);
        Debug.Assert(invalidCount >= 0);
        return invalidCount == 0;
    }

    public static void Initialize()
    {
        // The C++ global is initialized to one by the native loader. An explicit call preserves
        // that value without introducing a managed static constructor on a collector path.
        System.Threading.Volatile.Write(ref m_GcStructuresInvalidCnt, 1);
    }

    public static void GcScanRoots(
        delegate*<byte**, ScanContext*, uint, void> fn,
        int condemned,
        int max_gen,
        ScanContext* sc)
    {
        GCToEEInterface.GcScanRoots(fn, condemned, max_gen, sc);
    }

    public static void GcScanHandles(
        delegate*<byte**, ScanContext*, uint, void> fn,
        int condemned,
        int max_gen,
        ScanContext* sc)
    {
        if (sc->promotion != 0)
        {
            HandleTableScan.Ref_TracePinningRoots(condemned, max_gen, sc, fn);
            HandleTableScan.Ref_TraceNormalRoots(condemned, max_gen, sc, fn);
        }
        else
        {
            HandleTableScan.Ref_UpdatePointers(condemned, max_gen, sc, fn);
            HandleTableScan.Ref_UpdatePinnedPointers(condemned, max_gen, sc, fn);
            HandleTableScan.Ref_ScanDependentHandlesForRelocation(condemned, max_gen, sc, fn);
            HandleTableScan.Ref_ScanWeakInteriorPointersForRelocation(condemned, max_gen, sc, fn);
        }
    }

    public static void GcDhInitialScan(
        delegate*<byte**, ScanContext*, uint, void> fn,
        int condemned,
        int max_gen,
        ScanContext* sc)
    {
        DhContext* context = ObjectHandle.Ref_GetDependentHandleContext(sc);
        context->m_pfnPromoteFunction = fn;
        context->m_iCondemned = condemned;
        context->m_iMaxGen = max_gen;
        context->m_pScanContext = sc;

        HandleTableScan.Ref_ScanDependentHandlesForPromotion(context);
    }

    public static bool GcDhUnpromotedHandlesExist(ScanContext* sc)
    {
        DhContext* context = ObjectHandle.Ref_GetDependentHandleContext(sc);
        return context->m_fUnpromotedPrimaries != 0;
    }

    public static bool GcDhReScan(ScanContext* sc)
    {
        DhContext* context = ObjectHandle.Ref_GetDependentHandleContext(sc);
        return HandleTableScan.Ref_ScanDependentHandlesForPromotion(context);
    }

    public static void GcWeakPtrScan(int condemned, int max_gen, ScanContext* sc)
    {
        HandleTableScan.Ref_CheckReachable(condemned, max_gen, sc, &CheckPromoted);
        HandleTableScan.Ref_ScanDependentHandlesForClearing(condemned, max_gen, sc);
    }

    public static void GcWeakPtrScanBySingleThread(int condemned, int max_gen, ScanContext* sc)
    {
        _ = condemned;
        _ = max_gen;
        delegate*<byte**, nuint*, nuint, nuint, void> checkPromoted = &CheckPromoted;
        GCToEEInterface.SyncBlockCacheWeakPtrScan(
            (delegate* unmanaged<byte**, nuint*, nuint, nuint, void>)checkPromoted,
            (nuint)sc,
            0);
    }

    public static void GcShortWeakPtrScan(int condemned, int max_gen, ScanContext* sc)
    {
        HandleTableScan.Ref_CheckAlive(condemned, max_gen, sc, &CheckPromoted);
    }

    public static void GcScanSizedRefs(
        delegate*<byte**, ScanContext*, uint, void> fn,
        int condemned,
        int max_gen,
        ScanContext* sc)
    {
        HandleTableScan.Ref_ScanSizedRefHandles(
            condemned,
            max_gen,
            sc,
            fn);
    }

#if FEATURE_JAVAMARSHAL
    public static byte** GcProcessBridgeObjects(
        int condemned,
        int max_gen,
        ScanContext* sc,
        nuint* count)
    {
        Debug.Assert(sc->promotion != 0);
        return HandleTableScan.Ref_ScanBridgeObjects(
            condemned,
            max_gen,
            sc,
            count);
    }
#endif

    public static void GcDemote(int condemned, int max_gen, ScanContext* sc)
    {
        HandleTableScan.Ref_RejuvenateHandles(condemned, max_gen, sc);
        GCToEEInterface.SyncBlockCacheDemote(max_gen);
    }

    public static void GcPromotionsGranted(int condemned, int max_gen, ScanContext* sc)
    {
        HandleTableScan.Ref_AgeHandles(condemned, max_gen, sc);
        GCToEEInterface.SyncBlockCachePromotionsGranted(max_gen);
    }

    internal static void CheckPromoted(byte** pObjRef, nuint* pExtraInfo, nuint lp1, nuint lp2)
    {
        _ = pExtraInfo;
        _ = lp1;
        _ = lp2;

        if (!ManagedGCHeap.IsPromoted(*pObjRef))
        {
            *pObjRef = null;
        }
    }

    public static void GcRuntimeStructuresValid(int bValid)
    {
        if (bValid == 0)
        {
            int result;
            result = Interlocked.Increment(ref m_GcStructuresInvalidCnt);
            Debug.Assert(result > 0);
        }
        else
        {
            int result;
            result = Interlocked.Decrement(ref m_GcStructuresInvalidCnt);
            Debug.Assert(result >= 0);
        }
    }
}
