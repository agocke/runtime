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

    private static int s_bgcFinalizerRuns;

    private sealed class BackgroundFinalizable
    {
        ~BackgroundFinalizable() => Interlocked.Increment(ref s_bgcFinalizerRuns);
    }

    // Allocate a finalizable object and drop the only reference to it. Kept non-inlined so the local
    // does not remain reachable from the caller's frame: the object must be dead (but still registered
    // for finalization) by the time the next background collection runs.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateDeadBackgroundFinalizable()
    {
        BackgroundFinalizable dead = new();
        GC.KeepAlive(dead);
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

        // Fixed single active heap (an explicit one-heap server: heaps == workers == 1). The
        // multi-heap topology asserts below do not apply, but the background collection path still
        // must work with a single dedicated background worker and a single-participant bgc_t_join.
        // This exercises the commit-array gate and the join participant-count alignment at the
        // kickoff. The dynamic-adaptation (active heaps < worker pool) topology is intentionally not
        // exercised here because dynamic adaptation is independently unsupported by this managed
        // server port (it faults even for blocking collections); the join alignment for that case is
        // covered by the ServerBackgroundInitialMarkRoutingApiMatchesNative Foundation assertions.
        if (heapCount == 1 && workerCount == heapCount)
        {
            if (!BackgroundModeCollectionsCompleteWithSurvivors(heapCount))
            {
                return 9;
            }
            if (!ForcedCollectionsPreserveSurvivors())
            {
                return 4;
            }
            if (!WeakReferencesTrackReclamation())
            {
                return 5;
            }
            if (!FinalizationRunsForDeadObjects())
            {
                return 7;
            }
            if (!BackgroundCollectionQueuesFinalizers())
            {
                return 10;
            }
            if (!ConcurrentBackgroundRequestsCoalesce(heapCount))
            {
                return 11;
            }

            Console.WriteLine(
                "Managed server GC (single active heap) passed background-mode and forced " +
                "collections.");
            return 100;
        }

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

        if (!AllocationPressureTriggersCollections(heapCount))
        {
            return 8;
        }

        if (!BackgroundModeCollectionsCompleteWithSurvivors(heapCount))
        {
            return 9;
        }

        if (!FinalizationRunsForDeadObjects())
        {
            return 7;
        }

        if (!BackgroundCollectionQueuesFinalizers())
        {
            return 10;
        }

        if (!ConcurrentBackgroundRequestsCoalesce(heapCount))
        {
            return 11;
        }

        Console.WriteLine(
            $"Managed server GC passed with {heapCount} heaps, {usedHeaps.Count} selected home " +
            $"heaps, forced gen0/gen1/gen2 collections, allocation-triggered collections, " +
            $"background-mode collection requests, and " +
            $"{Volatile.Read(ref s_finalizedCount)} finalizers observed.");
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

    // Allocation pressure alone (no explicit GC.Collect) must drive the collector: the server
    // allocation slow path triggers GarbageCollectGenerationServer when a generation's budget is
    // exhausted. Verify collection counts advance under sustained allocation across all worker
    // threads/heaps, and that a live graph survives the collections the allocator triggered.
    private static bool AllocationPressureTriggersCollections(int heapCount)
    {
        int gen0Before = GC.CollectionCount(0);

        // A live graph that must survive every collection the allocator triggers below. Kept in a
        // rooted array (not on the allocating threads' stacks) so survival is unambiguous.
        const int LiveCount = 256;
        Payload[] live = new Payload[LiveCount];
        for (int i = 0; i < LiveCount; i++)
        {
            Payload p = new() { Tag = i };
            p.Data = new byte[128 + (i & 127)];
            p.Data[0] = unchecked((byte)i);
            live[i] = p;
        }
        byte[] lohSurvivor = new byte[100_000];
        lohSurvivor[0] = 0x5A;
        lohSurvivor[lohSurvivor.Length - 1] = 0xA5;
        object weakKept = new Payload { Tag = -7 };
        WeakReference keptRef = new(weakKept);

        // Spread sustained allocation over the worker threads so multiple heaps and per-thread
        // allocation contexts drive their own budgets. No thread calls GC.Collect.
        bool[] results = new bool[ThreadCount];
        Thread[] mutators = new Thread[ThreadCount];
        for (int t = 0; t < ThreadCount; t++)
        {
            int threadIndex = t;
            mutators[t] = new Thread(() =>
            {
                // Enough dead allocation to overrun the gen0 budget many times over.
                for (int round = 0; round < 2048; round++)
                {
                    for (int k = 0; k < 64; k++)
                    {
                        byte[] garbage = new byte[128 + ((threadIndex + k) & 255)];
                        garbage[0] = unchecked((byte)(threadIndex + k));
                    }
                }
                results[threadIndex] = true;
            });
            mutators[t].Start();
        }

        for (int t = 0; t < mutators.Length; t++)
        {
            mutators[t].Join();
        }

        int gen0After = GC.CollectionCount(0);
        if (gen0After <= gen0Before)
        {
            Console.WriteLine(
                $"Allocation pressure did not trigger any gen0 collection ({gen0Before} -> {gen0After}).");
            return false;
        }

        for (int t = 0; t < results.Length; t++)
        {
            if (!results[t])
            {
                Console.WriteLine($"Allocation-pressure mutator {t} did not complete.");
                return false;
            }
        }

        // The rooted live graph must be intact after the allocator-driven collections.
        for (int i = 0; i < LiveCount; i++)
        {
            Payload p = live[i];
            if (p is null || p.Tag != i || p.Data.Length != 128 + (i & 127) ||
                p.Data[0] != unchecked((byte)i))
            {
                Console.WriteLine($"Live object {i} corrupted after allocation-triggered collections.");
                return false;
            }
        }
        if (lohSurvivor.Length != 100_000 || lohSurvivor[0] != 0x5A ||
            lohSurvivor[lohSurvivor.Length - 1] != 0xA5)
        {
            Console.WriteLine("LOH survivor corrupted after allocation-triggered collections.");
            return false;
        }
        if (!keptRef.IsAlive || !ReferenceEquals(keptRef.Target, weakKept))
        {
            Console.WriteLine("Weak reference to a live object cleared by allocation-triggered collection.");
            return false;
        }

        _ = heapCount;
        GC.KeepAlive(live);
        GC.KeepAlive(lohSurvivor);
        GC.KeepAlive(weakKept);
        return true;
    }

    // Background/non-blocking GC.Collect requests must be routed onto the server background pipeline
    // and complete correctly while a mutator keeps allocating. This exercises the non-blocking
    // induced-collection request path (GCCollectionMode.Default with blocking:false, which the server
    // routes to garbage_collect_background: the background collection kickoff, the cross-heap
    // bgc_t_join initial-mark stages, and the background state-machine / concurrent write-barrier
    // state publication), allocation running concurrently with the collection request, completion
    // (each non-blocking request must advance the gen2 collection count on its own), and
    // survivor/weak-reference correctness across the collections.
    //
    // NOTE: this slice enables the real foreground/background handoff. A non-blocking gen2 request
    // routes to the server background collector, which does the initial mark, opens a concurrent
    // window (restart_vm publishes software-write-watch + gc_background_running and restarts the EE),
    // lets mutators run while current_c_gc_state == c_gc_state_marking, then re-suspends via
    // bgc_suspend_EE (PrepareForSuspension) and reclaims (the concurrent region sweep is deferred, so
    // the reclamation falls back to the blocking mark/plan/sweep on the bgc workers). The request
    // returns as soon as the window opens, so the collection completes asynchronously; concurrent
    // requests coalesce into a single background collection (native background_running_p semantics).
    //
    // The test proves the milestone: (1) re-suspension completes repeatedly -- the background
    // collection count advances once per request when each is allowed to complete; and (2) mutators
    // make allocation progress while marking is active -- the background allocation-wait counter
    // advances (a mutator can only park in that preemptive wait while a background collection is
    // running, i.e. during the concurrent window). Survivors / weak refs / finalization stay correct.
    private static bool BackgroundModeCollectionsCompleteWithSurvivors(int heapCount)
    {
        const int LiveCount = 256;
        Payload[] live = new Payload[LiveCount];
        for (int i = 0; i < LiveCount; i++)
        {
            Payload p = new() { Tag = i };
            p.Data = new byte[128 + (i & 127)];
            p.Data[0] = unchecked((byte)i);
            if (i > 0 && (i & 1) == 0)
            {
                p.Next = live[i - 1];
            }
            live[i] = p;
        }
        byte[] lohSurvivor = new byte[100_000];
        lohSurvivor[0] = 0x3C;
        lohSurvivor[lohSurvivor.Length - 1] = 0xC3;
        object weakKept = new Payload { Tag = -11 };
        WeakReference keptRef = new(weakKept);
        WeakReference deadRef = MakeDeadWeakReference();

        int stop = 0;
        Thread mutator = new(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                for (int k = 0; k < 128; k++)
                {
                    byte[] garbage = new byte[64 + (k & 127)];
                    garbage[0] = unchecked((byte)k);
                }
                Thread.Yield();
            }
        });
        mutator.Start();

        // Phase 1: prove re-suspension completes repeatedly. Each non-blocking gen2 request drives one
        // background collection; wait for it to finish (the background collection count advances) before
        // the next, so requests do not coalesce. The count must advance once per request.
        //
        // When concurrent/background GC is disabled (DOTNET_gcConcurrent=0), non-blocking gen2 requests
        // take the blocking path instead, so no background collection occurs. In that mode we fall back
        // to asserting that the gen2 collection count advances (the requests still collect) and skip the
        // background-specific counters, which stay 0 by design.
        bool backgroundEnabled = IsBackgroundGCEnabled() != 0;
        int backgroundRounds = 8;
        int bgcBefore = GetBackgroundCollectionCount();
        int gen2BeforeRounds = GC.CollectionCount(2);
        int completedRounds = 0;
        for (int round = 0; round < backgroundRounds; round++)
        {
            int c = GetBackgroundCollectionCount();
            GC.Collect(2, GCCollectionMode.Default, blocking: false);

            if (!backgroundEnabled)
            {
                continue;
            }

            // Wait for this background collection to re-suspend and complete (count advances).
            int spins = 0;
            while (GetBackgroundCollectionCount() == c && spins < 4000)
            {
                Thread.Sleep(1);
                spins++;
            }
            if (GetBackgroundCollectionCount() > c)
            {
                completedRounds++;
            }
        }
        int bgcAfter = GetBackgroundCollectionCount();
        int allocWaits = GetBackgroundAllocWaitCount();

        if (!backgroundEnabled)
        {
            // Blocking-path fallback: the non-blocking requests must have still collected.
            if (GC.CollectionCount(2) - gen2BeforeRounds < backgroundRounds)
            {
                Volatile.Write(ref stop, 1);
                mutator.Join();
                Console.WriteLine(
                    "Non-blocking gen2 requests did not advance the count with background GC disabled " +
                    $"({gen2BeforeRounds} -> {GC.CollectionCount(2)} over {backgroundRounds} requests).");
                return false;
            }
        }
        else
        {
            if (completedRounds < backgroundRounds || bgcAfter - bgcBefore < backgroundRounds)
            {
                Volatile.Write(ref stop, 1);
                mutator.Join();
                Console.WriteLine(
                    $"Background collections did not re-suspend/complete repeatedly: {completedRounds}/" +
                    $"{backgroundRounds} rounds, background count {bgcBefore} -> {bgcAfter}.");
                return false;
            }
            if (allocWaits <= 0)
            {
                Volatile.Write(ref stop, 1);
                mutator.Join();
                Console.WriteLine(
                    "No mutator made allocation progress while marking was active " +
                    "(background allocation-wait counter is 0).");
                return false;
            }
        }

        // Phase 2: mixed non-blocking + blocking requests interleaved with finalization.
        int gen2Before = GC.CollectionCount(2);
        for (int round = 0; round < 12; round++)
        {
            // Non-blocking / background gen2 collection request.
            GC.Collect(2, GCCollectionMode.Default, blocking: false);
            // A blocking request too, to exercise the mixed foreground/background request handling.
            GC.Collect(2, GCCollectionMode.Default, blocking: true);
            GC.WaitForPendingFinalizers();
        }
        int gen2After = GC.CollectionCount(2);

        Volatile.Write(ref stop, 1);
        mutator.Join();

        if (gen2After <= gen2Before)
        {
            Console.WriteLine(
                $"Background-mode gen2 collections did not advance the count ({gen2Before} -> {gen2After}).");
            return false;
        }

        for (int i = 0; i < LiveCount; i++)
        {
            Payload p = live[i];
            if (p is null || p.Tag != i || p.Data.Length != 128 + (i & 127) ||
                p.Data[0] != unchecked((byte)i))
            {
                Console.WriteLine($"Survivor {i} corrupted after background-mode collections.");
                return false;
            }
            if (i > 0 && (i & 1) == 0 && !ReferenceEquals(p.Next, live[i - 1]))
            {
                Console.WriteLine($"Survivor {i} lost its cross-object reference after background-mode collections.");
                return false;
            }
        }
        if (lohSurvivor.Length != 100_000 || lohSurvivor[0] != 0x3C ||
            lohSurvivor[lohSurvivor.Length - 1] != 0xC3)
        {
            Console.WriteLine("LOH survivor corrupted after background-mode collections.");
            return false;
        }
        if (!keptRef.IsAlive || !ReferenceEquals(keptRef.Target, weakKept))
        {
            Console.WriteLine("Weak reference to a live object cleared by background-mode collection.");
            return false;
        }
        if (deadRef.IsAlive)
        {
            Console.WriteLine("Weak reference to a dead object not cleared by background-mode collection.");
            return false;
        }

        _ = heapCount;
        GC.KeepAlive(live);
        GC.KeepAlive(lohSurvivor);
        GC.KeepAlive(weakKept);
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

    // A finalizable object that is dead before a non-blocking (background) gen2 request must be queued
    // to the finalizer thread by that background collection's own completion -- not by some later
    // unrelated collection. This exercises the server background completion's EnableFinalization (which
    // mirrors WKS complete_background_gc): the fallback gc1 discovers the dead object's finalizer and
    // sets settings.found_finalizers, and heap 0's completion must EnableFinalization so the finalizer
    // thread runs it. The test never calls GC.Collect after the request, so the only collection that
    // can reclaim the object and drive its finalization is the background one.
    private static bool BackgroundCollectionQueuesFinalizers()
    {
        if (IsBackgroundGCEnabled() == 0)
        {
            // Concurrent GC disabled: non-blocking requests take the blocking path, whose finalization
            // is already covered by FinalizationRunsForDeadObjects.
            return true;
        }

        // Drain any finalizers left pending by earlier phases so the counter reflects only this test.
        GC.WaitForPendingFinalizers();
        int before = Volatile.Read(ref s_bgcFinalizerRuns);

        // Create the dead finalizable object *before* the request.
        AllocateDeadBackgroundFinalizable();

        // Non-blocking (background) gen2 request. It returns as soon as the concurrent window opens; the
        // collection completes asynchronously on the background workers.
        GC.Collect(2, GCCollectionMode.Default, blocking: false);

        // Wait for the background completion to queue and the finalizer thread to run the finalizer.
        // Deliberately no GC.Collect *and* no GC.WaitForPendingFinalizers here: the finalization must be
        // driven solely by the background collection's completion signalling the finalizer thread
        // (EnableFinalization). GC.WaitForPendingFinalizers would itself wake the finalizer thread and
        // mask a missing EnableFinalization, so only a plain sleep/poll is used.
        for (int attempt = 0; attempt < 300; attempt++)
        {
            if (Volatile.Read(ref s_bgcFinalizerRuns) > before)
            {
                return true;
            }
            Thread.Sleep(10);
        }

        Console.WriteLine(
            "Background collection did not queue the dead finalizable object for finalization " +
            $"({before} -> {Volatile.Read(ref s_bgcFinalizerRuns)}) without a later GC.");
        return false;
    }

    // Multiple threads issue non-blocking gen2 (background) requests concurrently. Many of these land
    // while a background collection is already running -- in the live concurrent window -- exercising
    // the under-gc_lock coalesce path in GarbageCollectGenerationServer: a second concurrent request
    // must observe background_running_p under the lock and return (coalesce) instead of starting a
    // second kickoff. Without that fix (when a global request bit exempted every caller from the
    // background wait) two concurrent requests both bypassed the wait and started overlapping kickoffs,
    // deadlocking; this test then hangs (the threads never join) and the harness times out.
    private static bool ConcurrentBackgroundRequestsCoalesce(int heapCount)
    {
        if (IsBackgroundGCEnabled() == 0)
        {
            // Concurrent GC disabled: non-blocking requests take the blocking path; there is no
            // background collection to coalesce against.
            return true;
        }

        int before = GetBackgroundCollectionCount();
        int requesters = (heapCount < 4 ? 4 : heapCount) * 2;
        int go = 0;
        Thread[] threads = new Thread[requesters];
        for (int t = 0; t < requesters; t++)
        {
            threads[t] = new Thread(() =>
            {
                // Release all requester threads at once so they arrive at garbage_collect_background
                // concurrently and repeatedly race at the start of each background collection -- the
                // exact window where a second request can observe background_running_p flip on.
                while (Volatile.Read(ref go) == 0)
                {
                    Thread.SpinWait(64);
                }
                for (int i = 0; i < 200; i++)
                {
                    GC.Collect(2, GCCollectionMode.Default, blocking: false);
                    // A tiny allocation staggers the requests without piling on mutator pressure during
                    // the live window (which is orthogonal to the coalesce race under test).
                    byte[] garbage = new byte[64];
                    garbage[0] = unchecked((byte)i);
                }
            });
        }

        for (int t = 0; t < requesters; t++)
        {
            threads[t].Start();
        }
        Volatile.Write(ref go, 1);
        for (int t = 0; t < requesters; t++)
        {
            threads[t].Join();
        }

        // Reaching here means the concurrent requests coalesced without deadlocking. Let the last
        // kicked-off background collection finish (poll without issuing any new GC), then require that
        // at least one background collection completed.
        for (int attempt = 0; attempt < 300 && GetBackgroundCollectionCount() <= before; attempt++)
        {
            Thread.Sleep(10);
        }
        int after = GetBackgroundCollectionCount();
        if (after <= before)
        {
            Console.WriteLine(
                "Concurrent non-blocking gen2 requests did not complete any background collection " +
                $"({before} -> {after}).");
            return false;
        }
        return true;
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

    [DllImport("*", EntryPoint = "ManagedServerGC_GetBackgroundCollectionCount")]
    private static extern int GetBackgroundCollectionCount();

    [DllImport("*", EntryPoint = "ManagedServerGC_GetBackgroundAllocWaitCount")]
    private static extern int GetBackgroundAllocWaitCount();

    [DllImport("*", EntryPoint = "ManagedServerGC_IsBackgroundGCEnabled")]
    private static extern int IsBackgroundGCEnabled();
}
