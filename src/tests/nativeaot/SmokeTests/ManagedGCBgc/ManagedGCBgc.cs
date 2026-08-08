// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

internal static class ManagedGCBgcTest
{
    private static int Main()
    {
        if (!FullGCNotificationCancellationIsStable())
        {
            return 2;
        }

        if (!EventPipeGcSmoke.CollectAndValidate(background: true))
        {
            return 3;
        }

        const int NodeCount = 65_536;
        Node root = new Node { Value = 1 };
        Node tail = root;
        for (int i = 0; i < NodeCount; i++)
        {
            tail.Next = new Node { Value = i + 2 };
            tail = tail.Next;
        }

        WeakReference weak = CreateCollectibleObject();
        GCHandle strongHandle = GCHandle.Alloc(root);
        byte[] pinnedObject = new byte[512];
        pinnedObject[0] = 0x4a;
        GCHandle pinnedHandle = GCHandle.Alloc(pinnedObject, GCHandleType.Pinned);
        Finalizable.Reset();
        WeakReference finalizable = CreateFinalizableObject();
        Node oldTail = tail;
        int collectionCount = GC.CollectionCount(GC.MaxGeneration);
        WeakReference recycledRegionObject =
            CreateLargeGarbageWeakReference(8);

        try
        {
            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Forced,
                blocking: false,
                compacting: false);
            bool returnedBeforeCompletion =
                weak.IsAlive &&
                Volatile.Read(ref Finalizable.Count) == 0;
            Node cardChild = new Node { Value = 501 };
            tail.Next = cardChild;
            tail = cardChild;

            int mutations = 0;
            int foregroundCollections = 0;
            for (int cycle = 0; cycle < 1; cycle++)
            {
                for (int i = 0; i < 1024; i++)
                {
                    tail.Next = new Node { Value = 300 + mutations };
                    tail = tail.Next;
                    mutations++;
                    byte[] garbage = new byte[256];
                    garbage[0] = (byte)i;
                }

                GC.Collect(
                    0,
                    GCCollectionMode.Forced,
                    blocking: true,
                    compacting: false);
                foregroundCollections++;
                Thread.Yield();
            }

            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Forced,
                blocking: true,
                compacting: false);

            for (int cycle = 0; cycle < 4; cycle++)
            {
                GC.Collect(
                    GC.MaxGeneration,
                    GCCollectionMode.Forced,
                    blocking: false,
                    compacting: false);
                GC.Collect(
                    (cycle & 1) == 0 ? 1 : 0,
                    GCCollectionMode.Forced,
                    blocking: true,
                    compacting: false);
                foregroundCollections++;
                for (int i = 0; i < 1024; i++)
                {
                    tail.Next = new Node { Value = 40_000 + cycle * 1024 + i };
                    tail = tail.Next;
                }

                GC.Collect(
                    GC.MaxGeneration,
                    GCCollectionMode.Forced,
                    blocking: true,
                    compacting: false);
            }

            int beforeAllocationTriggered = GC.CollectionCount(GC.MaxGeneration);
            byte[] allocationSurvivor = new byte[128 * 1024];
            allocationSurvivor[0] = 0x2c;
            for (int i = 0;
                 i < 4096 &&
                 GC.CollectionCount(GC.MaxGeneration) == beforeAllocationTriggered;
                 i++)
            {
                byte[] garbage = new byte[128 * 1024];
                garbage[0] = (byte)i;
            }
            bool allocationTriggered =
                GC.CollectionCount(GC.MaxGeneration) > beforeAllocationTriggered;
            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Forced,
                blocking: true,
                compacting: false);

            byte[][] recycledAllocations = AllocateLargeGarbage(8);
            byte[] subsequent = new byte[2048];
            subsequent[0] = 0x6d;
            int metricsCollectionCount = GC.CollectionCount(GC.MaxGeneration);
            TimeSpan pauseBeforeMetrics = GC.GetTotalPauseDuration();
            GCLatencyMode originalLatency = System.Runtime.GCSettings.LatencyMode;
            System.Runtime.GCSettings.LatencyMode =
                GCLatencyMode.SustainedLowLatency;
            bool latencyRoundTrip =
                System.Runtime.GCSettings.LatencyMode ==
                GCLatencyMode.SustainedLowLatency;
            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Forced,
                blocking: false,
                compacting: false);
            System.Runtime.GCSettings.LatencyMode = GCLatencyMode.Interactive;
            latencyRoundTrip &=
                System.Runtime.GCSettings.LatencyMode ==
                GCLatencyMode.Interactive;
            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Forced,
                blocking: true,
                compacting: false);
            GCMemoryInfo memoryInfo = GC.GetGCMemoryInfo();
            TimeSpan pauseAfterMetrics = GC.GetTotalPauseDuration();
            GC.RefreshMemoryLimit();
            System.Runtime.GCSettings.LatencyMode = originalLatency;
            const long PressureIncrement = 16L * 1024 * 1024;
            long totalPressure = 0;
            int beforeMemoryPressure = GC.CollectionCount(GC.MaxGeneration);
            for (int i = 0;
                 i < 20 &&
                 GC.CollectionCount(GC.MaxGeneration) == beforeMemoryPressure;
                 i++)
            {
                Thread.Sleep(20);
                GC.AddMemoryPressure(PressureIncrement);
                totalPressure += PressureIncrement;
            }
            bool memoryPressureTriggered =
                GC.CollectionCount(GC.MaxGeneration) > beforeMemoryPressure;
            GC.RemoveMemoryPressure(totalPressure);
            bool metricsValid =
                memoryInfo.Index > 0 &&
                memoryInfo.HeapSizeBytes > 0 &&
                memoryInfo.FragmentedBytes <= memoryInfo.HeapSizeBytes &&
                memoryInfo.PromotedBytes > 0 &&
                memoryInfo.MemoryLoadBytes > 0 &&
                memoryInfo.HighMemoryLoadThresholdBytes > 0 &&
                memoryInfo.TotalAvailableMemoryBytes > 0 &&
                memoryInfo.GenerationInfo.Length == 5 &&
                memoryInfo.PauseDurations.Length == 2 &&
                pauseAfterMetrics >= pauseBeforeMetrics &&
                pauseAfterMetrics > TimeSpan.Zero &&
                GC.CollectionCount(GC.MaxGeneration) > metricsCollectionCount &&
                latencyRoundTrip &&
                memoryPressureTriggered;
            bool result =
                returnedBeforeCompletion &&
                mutations != 0 &&
                foregroundCollections == 5 &&
                GC.CollectionCount(GC.MaxGeneration) >= collectionCount + 4 &&
                allocationTriggered &&
                ReferenceEquals(strongHandle.Target, root) &&
                root.Value == 1 &&
                ReferenceEquals(oldTail.Next, cardChild) &&
                cardChild.Value == 501 &&
                !weak.IsAlive &&
                ReferenceEquals(pinnedHandle.Target, pinnedObject) &&
                pinnedHandle.AddrOfPinnedObject() != IntPtr.Zero &&
                pinnedObject[0] == 0x4a &&
                Volatile.Read(ref Finalizable.Count) == 1 &&
                !recycledRegionObject.IsAlive &&
                recycledAllocations[0][0] == 0x7b &&
                allocationSurvivor[0] == 0x2c &&
                subsequent[0] == 0x6d &&
                metricsValid;

            GC.KeepAlive(root);
            GC.KeepAlive(tail);
            GC.KeepAlive(weak);
            GC.KeepAlive(pinnedObject);
            GC.KeepAlive(finalizable);
            GC.KeepAlive(oldTail);
            GC.KeepAlive(cardChild);
            GC.KeepAlive(recycledRegionObject);
            GC.KeepAlive(recycledAllocations);
            GC.KeepAlive(allocationSurvivor);
            GC.KeepAlive(subsequent);

            Console.WriteLine(result
                ? "ManagedGC background smoke test passed."
                : "ManagedGC background smoke test failed.");
            if (!result)
            {
                Console.WriteLine(
                    $"returned={returnedBeforeCompletion}, mutations={mutations}, " +
                    $"collections={GC.CollectionCount(GC.MaxGeneration) - collectionCount}, " +
                    $"allocationTriggered={allocationTriggered}, card={ReferenceEquals(oldTail.Next, cardChild)}, " +
                    $"weak={weak.IsAlive}, recycled={recycledRegionObject.IsAlive}, " +
                    $"foreground={foregroundCollections}, " +
                    $"finalizers={Volatile.Read(ref Finalizable.Count)}, " +
                    $"heap={memoryInfo.HeapSizeBytes}, frag={memoryInfo.FragmentedBytes}, " +
                    $"promoted={memoryInfo.PromotedBytes}, load={memoryInfo.MemoryLoadBytes}, " +
                    $"pause={pauseAfterMetrics}, latency={latencyRoundTrip}, " +
                    $"pressure={memoryPressureTriggered}");
            }
            return result ? 100 : 1;
        }
        finally
        {
            strongHandle.Free();
            pinnedHandle.Free();
        }
    }

    private static bool FullGCNotificationCancellationIsStable()
    {
        GC.RegisterForFullGCNotification(10, 10);

        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Forced,
            blocking: false,
            compacting: false);
        GCNotificationStatus waitStatus = GC.WaitForFullGCApproach(100);
        GC.CancelFullGCNotification();
        bool result =
            waitStatus is (
                GCNotificationStatus.Timeout or
                GCNotificationStatus.NotApplicable) &&
            GC.WaitForFullGCComplete(0) ==
                GCNotificationStatus.NotApplicable;
        if (!result)
        {
            Console.WriteLine(
                $"BGC notification cancellation failed: status={waitStatus}");
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateCollectibleObject()
    {
        object target = new Node { Value = 91, Next = new Node { Value = 92 } };
        return new WeakReference(target);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateFinalizableObject()
    {
        object target = new Finalizable();
        return new WeakReference(target);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[][] AllocateLargeGarbage(int count)
    {
        byte[][] arrays = new byte[count][];
        for (int i = 0; i < arrays.Length; i++)
        {
            arrays[i] = new byte[512 * 1024];
            arrays[i][0] = 0x7b;
        }

        return arrays;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateLargeGarbageWeakReference(int count)
    {
        byte[][] arrays = AllocateLargeGarbage(count);
        return new WeakReference(arrays[0]);
    }

    private sealed class Node
    {
        public int Value;
        public Node Next;
    }

    private sealed class Finalizable
    {
        public static int Count;

        private long _value0 = 1;
        private long _value1 = 2;
        private long _value2 = 3;
        private long _value3 = 4;

        public static void Reset() => Volatile.Write(ref Count, 0);

        ~Finalizable()
        {
            if (_value0 + _value1 + _value2 + _value3 == 10)
            {
                Interlocked.Increment(ref Count);
            }
        }
    }
}
