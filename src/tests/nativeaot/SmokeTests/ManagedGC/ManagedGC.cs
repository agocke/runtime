// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.InteropServices;
using System.Threading;

// Built with IlcManagedGC=true, which links the managed GC selector (clrgc.managed.cpp) in
// place of the standalone GC loader and roots the [RuntimeExport] entry points in
// System.Private.GC. Reaching Main at all means ILC emitted ManagedGC_Initialize, the linker
// resolved it from native, and the runtime brought the whole process up on a heap written in
// C#: startup, module frozen object segments, statics, and every allocation below.
//
// The managed heap allocates but does not collect yet, so this exercises what it does have --
// the bump allocator, the write barrier globals it publishes, and the handle table -- rather
// than anything that needs a collector.
internal static class ManagedGCTest
{
    private static int Main()
    {
        if (!NothingIsReclaimed())
        {
            return 1;
        }

        if (!AllocationsAreDistinctAndZeroed())
        {
            return 2;
        }

        if (!ReferenceWritesWork())
        {
            return 3;
        }

        if (!LargeObjectsWork())
        {
            return 4;
        }

        if (!HandlesWork())
        {
            return 5;
        }

        if (!ThreadsCanAllocateWhileCollectionsSuspendThem())
        {
            return 6;
        }

        if (!CollectionsAreCounted())
        {
            return 7;
        }

        Console.WriteLine("ManagedGC smoke test passed.");
        return 100;
    }

    /// <summary>
    /// Distinguishes the managed heap from the C++ GC the selector falls back to. A collector
    /// would reclaim the garbage below and report a smaller heap afterwards; the managed heap
    /// cannot, so its reported size only ever grows. Delete this once the port collects.
    /// </summary>
    private static bool NothingIsReclaimed()
    {
        for (int i = 0; i < 16 * 1024; i++)
        {
            _ = new byte[512];
        }

        long before = GC.GetTotalMemory(false);
        GC.Collect();
        return GC.GetTotalMemory(false) >= before;
    }

    private static bool AllocationsAreDistinctAndZeroed()
    {
        const int Count = 2048;
        byte[][] arrays = new byte[Count][];

        for (int i = 0; i < Count; i++)
        {
            byte[] array = new byte[64];

            // Fresh heap memory is handed out once and comes from a fresh commit, so it must
            // already read as zero; the runtime relies on that rather than clearing it.
            foreach (byte b in array)
            {
                if (b != 0)
                {
                    return false;
                }
            }

            array[0] = (byte)i;
            array[63] = (byte)~i;
            arrays[i] = array;
        }

        // If any two allocations overlapped, one of these patterns is now wrong.
        for (int i = 0; i < Count; i++)
        {
            if (arrays[i][0] != (byte)i || arrays[i][63] != (byte)~i)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Stores references into heap objects, which runs the EE's write barrier against the card
    /// tables the managed heap built and published through StompWriteBarrier.
    /// </summary>
    private static bool ReferenceWritesWork()
    {
        const int Length = 4096;

        Node head = null;
        for (int i = 0; i < Length; i++)
        {
            head = new Node { Value = i, Next = head };
        }

        // Also write references into an array, which takes a different barrier helper.
        object[] boxes = new object[Length];
        for (int i = 0; i < Length; i++)
        {
            boxes[i] = i;
        }

        int expected = Length - 1;
        for (Node node = head; node is not null; node = node.Next)
        {
            if (node.Value != expected || (int)boxes[expected] != expected)
            {
                return false;
            }

            expected--;
        }

        return expected == -1;
    }

    private static bool LargeObjectsWork()
    {
        // Past the 85000-byte threshold, so the heap allocates these outside the allocation
        // context rather than from it.
        byte[] large = new byte[200_000];
        if (large[0] != 0 || large[^1] != 0)
        {
            return false;
        }

        large[0] = 1;
        large[^1] = 2;

        byte[] second = new byte[200_000];
        return second[0] == 0 && second[^1] == 0 && large[0] == 1 && large[^1] == 2;
    }

    private static bool HandlesWork()
    {
        object target = new Node { Value = 42 };

        GCHandle normal = GCHandle.Alloc(target);
        GCHandle weak = GCHandle.Alloc(target, GCHandleType.Weak);
        GCHandle pinned = GCHandle.Alloc(new byte[16], GCHandleType.Pinned);

        try
        {
            if (!ReferenceEquals(normal.Target, target) || !ReferenceEquals(weak.Target, target))
            {
                return false;
            }

            if (pinned.AddrOfPinnedObject() == IntPtr.Zero)
            {
                return false;
            }

            object replacement = new Node { Value = 43 };
            normal.Target = replacement;
            if (!ReferenceEquals(normal.Target, replacement))
            {
                return false;
            }
        }
        finally
        {
            normal.Free();
            weak.Free();
            pinned.Free();
        }

        // Churn handles so that freed slots are taken off the free list and handed out again.
        for (int i = 0; i < 4096; i++)
        {
            GCHandle handle = GCHandle.Alloc(target);
            if (!ReferenceEquals(handle.Target, target))
            {
                return false;
            }

            handle.Free();
        }

        return true;
    }

    /// <summary>
    /// Several threads allocate while the main thread repeatedly suspends the EE. This verifies
    /// that the suspend/restart path does not deadlock and that allocations remain intact across
    /// repeated stops.
    /// </summary>
    private static bool ThreadsCanAllocateWhileCollectionsSuspendThem()
    {
        const int ThreadCount = 4;
        const int PerThread = 8192;

        bool[] results = new bool[ThreadCount];
        Thread[] threads = new Thread[ThreadCount];
        using CountdownEvent ready = new CountdownEvent(ThreadCount);
        using ManualResetEventSlim startAllocating = new ManualResetEventSlim(false);

        for (int t = 0; t < ThreadCount; t++)
        {
            int index = t;
            threads[t] = new Thread(() =>
            {
                ready.Signal();
                startAllocating.Wait();

                byte[][] arrays = new byte[PerThread][];
                for (int i = 0; i < PerThread; i++)
                {
                    arrays[i] = new byte[32];
                    arrays[i][0] = (byte)index;
                    arrays[i][31] = (byte)i;
                }

                for (int i = 0; i < PerThread; i++)
                {
                    if (arrays[i][0] != (byte)index || arrays[i][31] != (byte)i)
                    {
                        return;
                    }
                }

                results[index] = true;
            });

            threads[t].Start();
        }

        ready.Wait();
        startAllocating.Set();
        for (int i = 0; i < 32; i++)
        {
            GC.Collect();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        foreach (bool result in results)
        {
            if (!result)
            {
                return false;
            }
        }

        return true;
    }

    private static bool CollectionsAreCounted()
    {
        int before = GC.CollectionCount(0);
        GC.Collect();
        return GC.CollectionCount(0) > before;
    }

    private sealed class Node
    {
        public int Value;
        public Node Next;
    }
}
