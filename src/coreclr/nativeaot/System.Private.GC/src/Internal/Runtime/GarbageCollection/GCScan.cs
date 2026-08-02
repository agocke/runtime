// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the dependency-closed parts of gcscan.cpp, in their original order. The handle-table
// scans remain with the stage 5 objecthandle.cpp translation that supplies their Ref_* calls.

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection;

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
