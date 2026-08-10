// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

internal static class ManagedGCServerSmoke
{
    private const int ThreadCount = 4;
    private static readonly int[] s_homeHeaps = new int[ThreadCount];
    private static int s_start;

    private sealed class Payload
    {
        public int Tag;
        public byte[] Data = Array.Empty<byte>();
        public Payload Next;
    }

    private static int s_finalizedCount;

    private sealed class Finalizable
    {
        public int Tag;

        ~Finalizable() => Interlocked.Increment(ref s_finalizedCount);
    }

    private static int Main()
    {
        if (!GCSettings.IsServerGC)
        {
            Console.WriteLine("The managed server runtime did not report server GC.");
            return 1;
        }

        int heapCount = GetHeapCount();
        int workerCount = GetWorkerThreadCount();
        if (heapCount < 2 || workerCount != heapCount)
        {
            Console.WriteLine(
                $"Unexpected server topology: heaps={heapCount}, workers={workerCount}.");
            return 2;
        }

        // Startup validation: every worker thread selects a valid home heap.
        Thread[] threads = new Thread[ThreadCount];
        for (int i = 0; i < threads.Length; i++)
        {
            int threadIndex = i;
            threads[i] = new Thread(() => Allocate(threadIndex));
            threads[i].Start();
        }

        Volatile.Write(ref s_start, 1);
        for (int i = 0; i < threads.Length; i++)
        {
            threads[i].Join();
        }

        HashSet<int> usedHeaps = new();
        for (int i = 0; i < s_homeHeaps.Length; i++)
        {
            int heap = s_homeHeaps[i];
            if ((uint)heap >= (uint)heapCount)
            {
                Console.WriteLine($"Thread {i} selected invalid heap {heap}.");
                return 3;
            }
            usedHeaps.Add(heap);
        }

        // Route the forced collections and validate survivors/weak refs/finalization across the
        // server heaps and worker threads.
        if (!ForcedCollectionsPreserveSurvivors())
        {
            return 4;
        }

        if (!WeakReferencesTrackReclamation())
        {
            return 5;
        }

        if (!MultiThreadedCollectionsAreStable(heapCount))
        {
            return 6;
        }

        if (!FinalizationRunsForDeadObjects())
        {
            return 7;
        }

        Console.WriteLine(
            $"Managed server GC passed with {heapCount} heaps, {usedHeaps.Count} selected home " +
            $"heaps, forced gen0/gen1/gen2 collections, and {Volatile.Read(ref s_finalizedCount)} " +
            "finalizers observed.");
        return 100;
    }

    // Allocate a mix of live and dead SOH objects (and LOH/POH), then force blocking collections at
    // each generation. The live graph and its data must survive; the collection counts must advance.
    private static bool ForcedCollectionsPreserveSurvivors()
    {
        const int LiveCount = 512;
        Payload[] live = new Payload[LiveCount];
        for (int i = 0; i < LiveCount; i++)
        {
            Payload p = new() { Tag = i };
            p.Data = new byte[256 + (i & 255)];
            for (int j = 0; j < p.Data.Length; j++)
            {
                p.Data[j] = unchecked((byte)(i + j));
            }
            // Chain some payloads together to create cross-object references.
            if (i > 0 && (i & 1) == 0)
            {
                p.Next = live[i - 1];
            }
            live[i] = p;
        }

        // A large-object-heap survivor and a pinned-object-heap survivor.
        byte[] lohSurvivor = new byte[100_000];
        for (int j = 0; j < lohSurvivor.Length; j += 1024)
        {
            lohSurvivor[j] = unchecked((byte)(j / 1024));
        }
        byte[] pohSurvivor = GC.AllocateArray<byte>(90_000, pinned: true);
        pohSurvivor[0] = 0xAB;
        pohSurvivor[pohSurvivor.Length - 1] = 0xCD;

        // Generate garbage so the collector has something to reclaim and free lists to rebuild.
        AllocateGarbage(4096);

        for (int gen = 0; gen <= 2; gen++)
        {
            int before = GC.CollectionCount(gen);
            GC.Collect(gen, GCCollectionMode.Forced, blocking: true);
            AllocateGarbage(2048);
            GC.Collect(gen, GCCollectionMode.Forced, blocking: true);
            int after = GC.CollectionCount(gen);
            if (after <= before)
            {
                Console.WriteLine(
                    $"GC.CollectionCount(gen{gen}) did not advance ({before} -> {after}).");
                return false;
            }
        }

        // Validate every survivor's identity and payload after the collections (which may have
        // relocated and compacted them).
        for (int i = 0; i < LiveCount; i++)
        {
            Payload p = live[i];
            if (p is null || p.Tag != i)
            {
                Console.WriteLine($"Survivor {i} lost its identity after collection.");
                return false;
            }
            if (p.Data.Length != 256 + (i & 255))
            {
                Console.WriteLine($"Survivor {i} data length changed after collection.");
                return false;
            }
            for (int j = 0; j < p.Data.Length; j++)
            {
                if (p.Data[j] != unchecked((byte)(i + j)))
                {
                    Console.WriteLine($"Survivor {i} data corrupted at {j} after collection.");
                    return false;
                }
            }
            if (i > 0 && (i & 1) == 0 && !ReferenceEquals(p.Next, live[i - 1]))
            {
                Console.WriteLine($"Survivor {i} lost its cross-object reference after collection.");
                return false;
            }
        }

        if (lohSurvivor.Length != 100_000 || lohSurvivor[0] != 0 || lohSurvivor[1024] != 1)
        {
            Console.WriteLine("LOH survivor corrupted after collection.");
            return false;
        }
        if (pohSurvivor[0] != 0xAB || pohSurvivor[pohSurvivor.Length - 1] != 0xCD)
        {
            Console.WriteLine("POH survivor corrupted after collection.");
            return false;
        }

        GC.KeepAlive(live);
        GC.KeepAlive(lohSurvivor);
        GC.KeepAlive(pohSurvivor);
        return true;
    }

    // A weak reference to a kept-alive object must stay alive across a full collection; a weak
    // reference to an unreachable object must be cleared by a full collection.
    private static bool WeakReferencesTrackReclamation()
    {
        object kept = new Payload { Tag = 42 };
        WeakReference keptRef = new(kept);
        WeakReference deadRef = MakeDeadWeakReference();

        AllocateGarbage(4096);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        if (!keptRef.IsAlive || !ReferenceEquals(keptRef.Target, kept))
        {
            Console.WriteLine("Weak reference to a live object was incorrectly cleared.");
            return false;
        }
        if (deadRef.IsAlive)
        {
            Console.WriteLine("Weak reference to a dead object was not cleared.");
            return false;
        }

        GC.KeepAlive(kept);
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference MakeDeadWeakReference()
    {
        object dead = new Payload { Tag = -1 };
        return new WeakReference(dead);
    }

    // Concurrent mutator threads keep allocating while forced collections run, exercising multiple
    // heaps and per-thread allocation contexts. Every thread's live set must survive intact.
    private static bool MultiThreadedCollectionsAreStable(int heapCount)
    {
        bool[] results = new bool[ThreadCount];
        int collectorStop = 0;

        Thread collector = new(() =>
        {
            while (Volatile.Read(ref collectorStop) == 0)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                Thread.Yield();
            }
        });
        collector.Start();

        Thread[] mutators = new Thread[ThreadCount];
        for (int t = 0; t < ThreadCount; t++)
        {
            int threadIndex = t;
            mutators[t] = new Thread(() =>
            {
                Payload[] live = new Payload[128];
                for (int i = 0; i < live.Length; i++)
                {
                    live[i] = new Payload { Tag = (threadIndex << 20) | i };
                    live[i].Data = new byte[128 + (i & 63)];
                    live[i].Data[0] = unchecked((byte)threadIndex);
                }

                for (int round = 0; round < 64; round++)
                {
                    // Allocate and drop garbage to churn the heap while the collector runs.
                    for (int k = 0; k < 256; k++)
                    {
                        byte[] garbage = new byte[64 + (k & 63)];
                        garbage[0] = unchecked((byte)k);
                    }
                    Thread.Yield();
                }

                bool ok = true;
                for (int i = 0; i < live.Length; i++)
                {
                    if (live[i].Tag != ((threadIndex << 20) | i) ||
                        live[i].Data[0] != unchecked((byte)threadIndex))
                    {
                        ok = false;
                        break;
                    }
                }
                GC.KeepAlive(live);
                results[threadIndex] = ok;
            });
            mutators[t].Start();
        }

        for (int t = 0; t < mutators.Length; t++)
        {
            mutators[t].Join();
        }
        Volatile.Write(ref collectorStop, 1);
        collector.Join();

        for (int t = 0; t < results.Length; t++)
        {
            if (!results[t])
            {
                Console.WriteLine(
                    $"Mutator thread {t} observed corruption during concurrent collections.");
                return false;
            }
        }
        _ = heapCount;
        return true;
    }

    // Dead objects with finalizers must be discovered by a collection and queued to the finalizer
    // thread. Validated leniently: at least some finalizers must run.
    private static bool FinalizationRunsForDeadObjects()
    {
        const int FinalizableCount = 256;
        AllocateFinalizables(FinalizableCount);

        for (int attempt = 0; attempt < 8; attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            if (Volatile.Read(ref s_finalizedCount) > 0)
            {
                return true;
            }
        }

        Console.WriteLine("No finalizers ran after forced full collections.");
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateFinalizables(int count)
    {
        for (int i = 0; i < count; i++)
        {
            Finalizable f = new() { Tag = i };
            GC.KeepAlive(f);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateGarbage(int count)
    {
        for (int i = 0; i < count; i++)
        {
            byte[] garbage = new byte[64 + (i & 127)];
            garbage[0] = unchecked((byte)i);
        }
    }

    private static void Allocate(int threadIndex)
    {
        while (Volatile.Read(ref s_start) == 0)
        {
            Thread.Yield();
        }

        byte[][] allocations = new byte[128][];
        for (int i = 0; i < allocations.Length; i++)
        {
            allocations[i] = new byte[256 + ((threadIndex + i) & 127)];
            allocations[i][0] = (byte)(threadIndex + i);
        }

        s_homeHeaps[threadIndex] = GetCurrentHomeHeapNumber();
        GC.KeepAlive(allocations);
    }

    [DllImport("*", EntryPoint = "ManagedServerGC_GetCurrentHomeHeapNumber")]
    private static extern int GetCurrentHomeHeapNumber();

    [DllImport("*", EntryPoint = "ManagedServerGC_GetHeapCount")]
    private static extern int GetHeapCount();


    [DllImport("*", EntryPoint = "ManagedServerGC_GetWorkerThreadCount")]
    private static extern int GetWorkerThreadCount();
}
