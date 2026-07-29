// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

// Built with IlcManagedGC=true, which links the managed GC selector (clrgc.managed.cpp) in
// place of the standalone GC loader and roots the [RuntimeExport] entry points in
// System.Private.GC. Reaching Main at all means ILC emitted ManagedGC_Initialize, the linker
// resolved it from native, and the managed bring-up path ran to completion during startup.
//
// The managed heap itself is still being ported, so ManagedGC_Initialize currently reports
// that it has no heap to offer and the runtime falls back to the C++ GC. The assertions below
// are therefore about the process being healthy, not about which GC serviced the allocations.
internal static class ManagedGCTest
{
    private static int Main()
    {
        if (!AllocationSurvivesCollection())
        {
            return 1;
        }

        if (!CollectionCountAdvances())
        {
            return 2;
        }

        if (!FinalizerRuns())
        {
            return 3;
        }

        Console.WriteLine("ManagedGC smoke test passed.");
        return 100;
    }

    private static bool AllocationSurvivesCollection()
    {
        byte[][] live = new byte[64][];
        for (int i = 0; i < live.Length; i++)
        {
            live[i] = new byte[4096];
            live[i][0] = (byte)i;
        }

        // Garbage for the collector to actually reclaim.
        for (int i = 0; i < 10000; i++)
        {
            _ = new byte[512];
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        for (int i = 0; i < live.Length; i++)
        {
            if (live[i] is null || live[i].Length != 4096 || live[i][0] != (byte)i)
            {
                return false;
            }
        }

        return true;
    }

    private static bool CollectionCountAdvances()
    {
        int before = GC.CollectionCount(0);
        GC.Collect();
        return GC.CollectionCount(0) > before;
    }

    private static bool FinalizerRuns()
    {
        AllocateFinalizable();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        return s_finalized;
    }

    // Kept in a separate non-inlined method so the instance is unreachable by the time the
    // collection below runs.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AllocateFinalizable() => GC.KeepAlive(new Finalizable());

    private static bool s_finalized;

    private sealed class Finalizable
    {
        ~Finalizable() => s_finalized = true;
    }
}
