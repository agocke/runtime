// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

// Built with IlcManagedGC=true, which links the managed GC selector (clrgc.managed.cpp) in
// place of the standalone GC loader and roots the [RuntimeExport] entry points in
// System.Private.GC. Reaching Main at all means ILC emitted ManagedGC_Initialize, the linker
// resolved it from native, and the runtime brought the whole process up on a heap written in
// C#: startup, module frozen object segments, statics, and every allocation below.
//
// Dependency-free utility coverage lives in ManagedGC.Foundation.Tests; this test stays focused
// on end-to-end NativeAOT runtime integration.
internal static unsafe class ManagedGCTest
{
    private static int Main()
    {
        if (!AutomaticSohCollectionReclaimsAndPreservesRoots())
        {
            return 1;
        }

        if (!AutomaticUohCollectionReclaimsAndPreservesRoots())
        {
            return 2;
        }

        if (!PartialCollectionsPreserveRootsHandlesAndAllocation())
        {
            return 3;
        }

        if (!PartialFullTransitionsResetCollectionState())
        {
            return 4;
        }

        if (!FullCollectionReclaimsRelocatesAndPreservesRoots())
        {
            return 5;
        }

        if (!AllocationsAreDistinctAndZeroed())
        {
            return 6;
        }

        if (!ReferenceWritesWork())
        {
            return 7;
        }

        if (!LargeObjectsWork())
        {
            return 8;
        }

        if (!HandlesWork())
        {
            return 9;
        }

        if (!FinalizerAllocationCanTriggerCollection())
        {
            return 10;
        }

        if (!PublicGcMetricsAndSettingsWork())
        {
            return 11;
        }

        if (!PublicNoGCRegionApisWork())
        {
            return 12;
        }

        if (!PublicFullGCNotificationApisWork())
        {
            return 13;
        }

        Console.WriteLine("ManagedGC smoke test passed.");
        return 100;
    }

    private static int s_noGCRegionCallbackCount;

    private static bool PublicNoGCRegionApisWork()
    {
        const int TotalSize = 24 * 1024 * 1024;
        const int LohSize = 8 * 1024 * 1024;
        const int CallbackThreshold = 4 * 1024 * 1024;

        if (!GC.TryStartNoGCRegion(
            TotalSize,
            LohSize,
            disallowFullBlockingGC: true))
        {
            Console.WriteLine("TryStartNoGCRegion returned false.");
            return false;
        }

        try
        {
            Volatile.Write(ref s_noGCRegionCallbackCount, 0);
            GC.RegisterNoGCRegionCallback(
                CallbackThreshold,
                NoGCRegionCallback);

            for (int i = 0;
                 i < 128 && Volatile.Read(ref s_noGCRegionCallbackCount) == 0;
                 i++)
            {
                byte[] allocation = new byte[(i & 1) == 0 ? 64 * 1024 : 128 * 1024];
                allocation[0] = (byte)i;
                Thread.Yield();
            }

            for (int i = 0;
                 i < 200 && Volatile.Read(ref s_noGCRegionCallbackCount) == 0;
                 i++)
            {
                Thread.Sleep(10);
            }

            bool callbackRan =
                Volatile.Read(ref s_noGCRegionCallbackCount) == 1;
            GC.EndNoGCRegion();
            if (!callbackRan)
            {
                Console.WriteLine("No-GC region callback did not run.");
            }

            return callbackRan;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"No-GC region API failed: {exception}");
            try
            {
                GC.EndNoGCRegion();
            }
            catch
            {
            }

            return false;
        }
    }

    private static void NoGCRegionCallback()
    {
        Interlocked.Increment(ref s_noGCRegionCallbackCount);
    }

    private static GCNotificationStatus s_approachStatus;
    private static GCNotificationStatus s_completeStatus;
    private static GCNotificationStatus s_cancelStatus;

    private static bool PublicFullGCNotificationApisWork()
    {
        GC.RegisterForFullGCNotification(10, 10);
        s_approachStatus = GCNotificationStatus.NotApplicable;
        s_completeStatus = GCNotificationStatus.NotApplicable;
        Thread waiter = new(WaitForFullGC);
        waiter.Start();

        int gen2Count = GC.CollectionCount(GC.MaxGeneration);
        for (int i = 0;
             i < 8192 && (waiter.IsAlive ||
                          GC.CollectionCount(GC.MaxGeneration) == gen2Count);
             i++)
        {
            byte[] allocation = new byte[256 * 1024];
            allocation[0] = (byte)i;
            if ((i & 31) == 0)
            {
                Thread.Yield();
            }
        }

        bool completed = waiter.Join(20_000);
        GC.CancelFullGCNotification();
        if (!completed ||
            s_approachStatus != GCNotificationStatus.Succeeded ||
            s_completeStatus != GCNotificationStatus.Succeeded)
        {
            Console.WriteLine(
                $"Full-GC notification failed: joined={completed}, " +
                $"approach={s_approachStatus}, complete={s_completeStatus}");
            return false;
        }

        GC.RegisterForFullGCNotification(10, 10);
        s_cancelStatus = GCNotificationStatus.NotApplicable;
        Thread cancelWaiter = new(WaitForFullGCCancel);
        cancelWaiter.Start();
        Thread.Sleep(50);
        GC.CancelFullGCNotification();
        bool cancelled = cancelWaiter.Join(5_000);
        bool result =
            cancelled &&
            s_cancelStatus == GCNotificationStatus.Canceled &&
            GC.WaitForFullGCApproach(0) ==
                GCNotificationStatus.NotApplicable;
        if (!result)
        {
            Console.WriteLine(
                $"Full-GC cancellation failed: joined={cancelled}, " +
                $"status={s_cancelStatus}");
        }

        return result;
    }

    private static void WaitForFullGC()
    {
        s_approachStatus = GC.WaitForFullGCApproach(15_000);
        if (s_approachStatus == GCNotificationStatus.Succeeded)
        {
            s_completeStatus = GC.WaitForFullGCComplete(15_000);
        }
    }

    private static void WaitForFullGCCancel()
    {
        s_cancelStatus = GC.WaitForFullGCApproach(10_000);
    }

    private static bool PublicGcMetricsAndSettingsWork()
    {
        int gen0Count = GC.CollectionCount(0);
        int gen1Count = GC.CollectionCount(1);
        int gen2Count = GC.CollectionCount(2);
        TimeSpan pauseBefore = GC.GetTotalPauseDuration();
        byte[][] survivors = new byte[8][];
        for (int i = 0; i < survivors.Length; i++)
        {
            survivors[i] = new byte[32 * 1024];
            survivors[i][0] = (byte)(0x20 + i);
        }

        Finalizable.Reset();
        WeakReference finalizable = CreateFinalizableObject();
        ForceFullCollection();

        GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        TimeSpan pauseAfter = GC.GetTotalPauseDuration();
        bool generationInfoValid = info.GenerationInfo.Length == 5;
        bool generationHasSize = false;
        for (int i = 0; i < info.GenerationInfo.Length; i++)
        {
            generationHasSize |= info.GenerationInfo[i].SizeAfterBytes > 0;
        }
        generationInfoValid &= generationHasSize;
        bool metricsValid =
            info.Index > 0 &&
            info.Generation == GC.MaxGeneration &&
            info.HeapSizeBytes > 0 &&
            info.FragmentedBytes <= info.HeapSizeBytes &&
            info.PromotedBytes > 0 &&
            info.FinalizationPendingCount > 0 &&
            info.MemoryLoadBytes > 0 &&
            info.HighMemoryLoadThresholdBytes > 0 &&
            info.TotalAvailableMemoryBytes > 0 &&
            info.PauseDurations.Length == 2 &&
            info.PauseDurations[0] > TimeSpan.Zero &&
            pauseAfter >= pauseBefore &&
            pauseAfter > TimeSpan.Zero &&
            GC.CollectionCount(0) > gen0Count &&
            GC.CollectionCount(1) > gen1Count &&
            GC.CollectionCount(2) > gen2Count &&
            generationInfoValid;

        GCLatencyMode originalLatency = System.Runtime.GCSettings.LatencyMode;
        System.Runtime.GCSettings.LatencyMode = GCLatencyMode.LowLatency;
        bool latencyRoundTrip =
            System.Runtime.GCSettings.LatencyMode == GCLatencyMode.LowLatency;
        System.Runtime.GCSettings.LatencyMode = GCLatencyMode.Interactive;
        latencyRoundTrip &=
            System.Runtime.GCSettings.LatencyMode == GCLatencyMode.Interactive;
        System.Runtime.GCSettings.LatencyMode = originalLatency;

        const ulong RefreshedHeapLimit = 512UL * 1024 * 1024;
        AppContext.SetData("GCHeapHardLimit", RefreshedHeapLimit);
        GC.RefreshMemoryLimit();
        bool refreshedLimit =
            (ulong)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes ==
            RefreshedHeapLimit;
        AppContext.SetData("GCHeapHardLimit", 0UL);
        GC.RefreshMemoryLimit();
        for (int iteration = 0; iteration < 3; iteration++)
        {
            byte[] allocation = new byte[64 * 1024];
            allocation[0] = (byte)iteration;
            ForceCollection(iteration % 3);
            if (allocation[0] != (byte)iteration)
            {
                return false;
            }

            GC.KeepAlive(allocation);
        }

        GC.WaitForPendingFinalizers();
        bool result =
            metricsValid &&
            latencyRoundTrip &&
            refreshedLimit &&
            Volatile.Read(ref Finalizable.Count) == 1 &&
            survivors[0][0] == 0x20 &&
            survivors[^1][0] == 0x27;
        if (!result)
        {
            Console.WriteLine(
                $"GC metrics failed: index={info.Index}, gen={info.Generation}, " +
                $"heap={info.HeapSizeBytes}, frag={info.FragmentedBytes}, " +
                $"promoted={info.PromotedBytes}, finalization={info.FinalizationPendingCount}, " +
                $"load={info.MemoryLoadBytes}, threshold={info.HighMemoryLoadThresholdBytes}, " +
                $"available={info.TotalAvailableMemoryBytes}, pause={pauseAfter}, " +
                $"latency={latencyRoundTrip}, refresh={refreshedLimit}, " +
                $"finalized={Volatile.Read(ref Finalizable.Count)}");
        }

        GC.KeepAlive(finalizable);
        GC.KeepAlive(survivors);
        return result;
    }

    private static bool AutomaticSohCollectionReclaimsAndPreservesRoots()
    {
        const int MaximumAllocations = 8192;
        const int AllocationSize = 32 * 1024;
        byte[] survivor = new byte[1024];
        survivor[0] = 0x31;
        survivor[^1] = 0x13;
        WeakReference weak = CreateCollectibleSmallObject();
        int collectionCount = GC.CollectionCount(0);

        for (int i = 0;
             i < MaximumAllocations &&
             (GC.CollectionCount(0) == collectionCount || weak.IsAlive);
             i++)
        {
            byte[] garbage = new byte[AllocationSize];
            garbage[0] = (byte)i;
        }

        byte[] subsequent = new byte[2048];
        subsequent[0] = 0x5a;
        bool result =
            GC.CollectionCount(0) > collectionCount &&
            !weak.IsAlive &&
            survivor[0] == 0x31 &&
            survivor[^1] == 0x13 &&
            subsequent[0] == 0x5a;
        if (!result)
        {
            Console.WriteLine(
                $"Automatic SOH collection failed: before={collectionCount}, " +
                $"after={GC.CollectionCount(0)}, weak={weak.IsAlive}");
        }

        GC.KeepAlive(survivor);
        GC.KeepAlive(weak);
        GC.KeepAlive(subsequent);
        return result;
    }

    private static bool PartialCollectionsPreserveRootsHandlesAndAllocation()
    {
        Node oldRoot = new Node { Value = 10 };
        ForceFullCollection();

        if (!RunForcedPartialCollection(oldRoot, generation: 0, value: 20))
        {
            return false;
        }

        PartialCollectionRoots gen1Roots = CreateGen1Roots(oldRoot);
        try
        {
            int collectionCount = GC.CollectionCount(1);
            ForceCollection(1);

            byte[] subsequent = new byte[4096];
            subsequent[0] = 0x71;
            object handleTarget = gen1Roots.HandleOnlyRoot.Target;
            bool dependentAlive = gen1Roots.DependentHandles.TryGetValue(
                gen1Roots.DependentPrimary,
                out Node dependentSecondary);
            bool result =
                GC.CollectionCount(1) > collectionCount &&
                oldRoot.Next is not null &&
                oldRoot.Next.Value == 31 &&
                gen1Roots.YoungRoot.Value == 32 &&
                !gen1Roots.Weak.IsAlive &&
                handleTarget is Node handleNode &&
                handleNode.Value == 33 &&
                ReferenceEquals(gen1Roots.PinnedHandle.Target, gen1Roots.PinnedObject) &&
                gen1Roots.PinnedHandle.AddrOfPinnedObject() != IntPtr.Zero &&
                dependentAlive &&
                dependentSecondary.Value == 37 &&
                subsequent[0] == 0x71;

            GC.KeepAlive(oldRoot);
            GC.KeepAlive(gen1Roots.YoungRoot);
            GC.KeepAlive(gen1Roots.Weak);
            GC.KeepAlive(handleTarget);
            GC.KeepAlive(gen1Roots.PinnedObject);
            GC.KeepAlive(gen1Roots.DependentPrimary);
            GC.KeepAlive(gen1Roots.DependentHandles);
            GC.KeepAlive(dependentSecondary);
            GC.KeepAlive(subsequent);
            return result;
        }
        finally
        {
            gen1Roots.HandleOnlyRoot.Free();
            gen1Roots.PinnedHandle.Free();
        }
    }

    private static bool RunForcedPartialCollection(
        Node oldRoot,
        int generation,
        int value)
    {
        Node youngRoot = new Node { Value = value };
        oldRoot.Next = youngRoot;
        WeakReference weak = CreateCollectibleSmallObject();
        GCHandle handleOnlyRoot = GCHandle.Alloc(new Node { Value = value + 1 });
        byte[] pinnedObject = new byte[256];
        pinnedObject[0] = (byte)value;
        GCHandle pinnedHandle = GCHandle.Alloc(pinnedObject, GCHandleType.Pinned);
        Node dependentPrimary = new Node { Value = value + 2 };
        ConditionalWeakTable<Node, Node> dependentHandles =
            CreateDependentHandles(dependentPrimary, value + 3);

        try
        {
            int collectionCount = GC.CollectionCount(generation);
            ForceCollection(generation);

            byte[] subsequent = new byte[2048];
            subsequent[0] = 0x61;
            object handleTarget = handleOnlyRoot.Target;
            bool dependentAlive = dependentHandles.TryGetValue(
                dependentPrimary,
                out Node dependentSecondary);
            bool result =
                GC.CollectionCount(generation) > collectionCount &&
                ReferenceEquals(oldRoot.Next, youngRoot) &&
                oldRoot.Next.Value == value &&
                !weak.IsAlive &&
                handleTarget is Node handleNode &&
                handleNode.Value == value + 1 &&
                ReferenceEquals(pinnedHandle.Target, pinnedObject) &&
                pinnedHandle.AddrOfPinnedObject() != IntPtr.Zero &&
                pinnedObject[0] == (byte)value &&
                dependentAlive &&
                dependentSecondary.Value == value + 3 &&
                subsequent[0] == 0x61;

            GC.KeepAlive(oldRoot);
            GC.KeepAlive(youngRoot);
            GC.KeepAlive(weak);
            GC.KeepAlive(handleTarget);
            GC.KeepAlive(pinnedObject);
            GC.KeepAlive(dependentPrimary);
            GC.KeepAlive(dependentHandles);
            GC.KeepAlive(dependentSecondary);
            GC.KeepAlive(subsequent);
            return result;
        }
        finally
        {
            handleOnlyRoot.Free();
            pinnedHandle.Free();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PartialCollectionRoots CreateGen1Roots(Node oldRoot)
    {
        Node youngRoot = new Node { Value = 32 };
        oldRoot.Next = new Node { Value = 31, Next = youngRoot };
        object weakTarget = new Node { Value = 34 };
        WeakReference weak = new WeakReference(weakTarget);
        GCHandle handleOnlyRoot = GCHandle.Alloc(new Node { Value = 33 });
        byte[] pinnedObject = new byte[256];
        pinnedObject[0] = 35;
        GCHandle pinnedHandle = GCHandle.Alloc(pinnedObject, GCHandleType.Pinned);
        Node dependentPrimary = new Node { Value = 36 };
        ConditionalWeakTable<Node, Node> dependentHandles =
            CreateDependentHandles(dependentPrimary, 37);

        ForceCollection(0);
        GC.KeepAlive(weakTarget);
        return new PartialCollectionRoots(
            youngRoot,
            weak,
            handleOnlyRoot,
            pinnedObject,
            pinnedHandle,
            dependentPrimary,
            dependentHandles);
    }

    private static bool PartialFullTransitionsResetCollectionState()
    {
        for (int iteration = 0; iteration < 2; iteration++)
        {
            Node oldRoot = new Node { Value = 100 + iteration };
            ForceFullCollection();

            oldRoot.Next = new Node { Value = 110 + iteration };
            ForceCollection(0);
            oldRoot.Next.Next = new Node { Value = 120 + iteration };
            ForceCollection(1);
            ForceFullCollection();

            object[] loh = new object[16 * 1024];
            Node lohChild = new Node { Value = 130 + iteration };
            loh[loh.Length / 2] = lohChild;
            byte[] poh = GC.AllocateUninitializedArray<byte>(4096, pinned: true);
            poh[0] = (byte)(140 + iteration);
            poh[^1] = (byte)(150 + iteration);

            Finalizable.Reset();
            WeakReference finalizable = CreateFinalizableObject();
            ForceCollection(0);
            ForceCollection(1);
            ForceFullCollection();
            GC.WaitForPendingFinalizers();

            ForceCollection(0);
            ForceCollection(1);
            ForceFullCollection();

            if (oldRoot.Next is null ||
                oldRoot.Next.Value != 110 + iteration ||
                oldRoot.Next.Next is null ||
                oldRoot.Next.Next.Value != 120 + iteration ||
                !ReferenceEquals(loh[loh.Length / 2], lohChild) ||
                lohChild.Value != 130 + iteration ||
                poh[0] != (byte)(140 + iteration) ||
                poh[^1] != (byte)(150 + iteration) ||
                Volatile.Read(ref Finalizable.Count) != 1)
            {
                return false;
            }

            GC.KeepAlive(oldRoot);
            GC.KeepAlive(loh);
            GC.KeepAlive(lohChild);
            GC.KeepAlive(poh);
            GC.KeepAlive(finalizable);
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ConditionalWeakTable<Node, Node> CreateDependentHandles(
        Node primary,
        int secondaryValue)
    {
        ConditionalWeakTable<Node, Node> handles = new();
        handles.Add(primary, new Node { Value = secondaryValue });
        return handles;
    }

    private static bool AutomaticUohCollectionReclaimsAndPreservesRoots()
    {
        const int MaximumAllocations = 2048;
        const int AllocationSize = 256 * 1024;
        byte[] survivor = new byte[128 * 1024];
        survivor[0] = 0x42;
        survivor[^1] = 0x24;
        WeakReference weak = CreateCollectibleObject();
        int collectionCount = GC.CollectionCount(GC.MaxGeneration);

        for (int i = 0;
             i < MaximumAllocations &&
             (GC.CollectionCount(GC.MaxGeneration) == collectionCount || weak.IsAlive);
             i++)
        {
            byte[] garbage = new byte[AllocationSize];
            garbage[0] = (byte)i;
        }

        byte[] subsequent = new byte[200_000];
        subsequent[0] = 0x6b;
        bool result =
            GC.CollectionCount(GC.MaxGeneration) > collectionCount &&
            !weak.IsAlive &&
            survivor[0] == 0x42 &&
            survivor[^1] == 0x24 &&
            subsequent[0] == 0x6b;
        if (!result)
        {
            Console.WriteLine(
                $"Automatic UOH collection failed: before={collectionCount}, " +
                $"after={GC.CollectionCount(GC.MaxGeneration)}, weak={weak.IsAlive}");
        }

        GC.KeepAlive(survivor);
        GC.KeepAlive(weak);
        GC.KeepAlive(subsequent);
        return result;
    }

    private static bool FullCollectionReclaimsRelocatesAndPreservesRoots()
    {
        const int SurvivorCount = 64;
        byte[][] survivors = new byte[SurvivorCount][];
        nuint[] addressesBefore = new nuint[SurvivorCount];
        for (int i = 0; i < SurvivorCount; i++)
        {
            for (int j = 0; j < 64; j++)
            {
                _ = new byte[128];
            }

            byte[] survivor = new byte[256];
            survivor[0] = (byte)i;
            survivor[^1] = (byte)~i;
            survivors[i] = survivor;
            addressesBefore[i] = AddressOf(survivor);
        }

        byte[] rooted = survivors[SurvivorCount / 2];

        WeakReference weak = CreateCollectibleObject();
        GCHandle strongHandle = GCHandle.Alloc(rooted);
        GCHandle handleOnlyRoot = CreateHandleOnlyRoot();
        byte[] pinnedObject = new byte[256];
        GCHandle pinnedHandle = GCHandle.Alloc(pinnedObject, GCHandleType.Pinned);

        for (int i = 0; i < 4096; i++)
        {
            _ = new byte[128];
        }

        ForceFullCollection();

        bool survived = ReferenceEquals(strongHandle.Target, rooted);
        object handleOnlyTarget = handleOnlyRoot.Target;
        if (handleOnlyTarget is null)
        {
            strongHandle.Free();
            handleOnlyRoot.Free();
            pinnedHandle.Free();
            return false;
        }

        bool handleRootSurvived =
            handleOnlyTarget is Node handleRoot &&
            handleRoot.Value == 73;
        if (!handleRootSurvived)
        {
            strongHandle.Free();
            handleOnlyRoot.Free();
            pinnedHandle.Free();
            return false;
        }
        bool addressChanged = false;
        for (int i = 0; i < SurvivorCount; i++)
        {
            if (survivors[i][0] != (byte)i ||
                survivors[i][^1] != (byte)~i)
            {
                survived = false;
            }

            if (AddressOf(survivors[i]) != addressesBefore[i])
            {
                addressChanged = true;
            }
        }

        bool reclaimed = !weak.IsAlive;
        bool relocated = addressChanged;
        bool pinned =
            pinnedHandle.AddrOfPinnedObject() != IntPtr.Zero &&
            ReferenceEquals(pinnedHandle.Target, pinnedObject);

        byte[] subsequent = new byte[1024];
        subsequent[0] = 0x55;
        Node subsequentNode = new Node { Value = 55, Next = new Node { Value = 56 } };
        bool subsequentAllocation =
            subsequent[0] == 0x55 &&
            subsequentNode.Value == 55 &&
            subsequentNode.Next.Value == 56;

        Finalizable.Reset();
        WeakReference finalizable = CreateFinalizableObject();
        ForceFullNonCompactingRequest();
        long finalizationPending = GC.GetGCMemoryInfo().FinalizationPendingCount;
        GC.WaitForPendingFinalizers();
        bool finalized = Volatile.Read(ref Finalizable.Count) == 1;

        strongHandle.Free();
        handleOnlyRoot.Free();
        pinnedHandle.Free();
        GC.KeepAlive(rooted);
        GC.KeepAlive(survivors);
        GC.KeepAlive(pinnedObject);
        GC.KeepAlive(subsequent);
        GC.KeepAlive(subsequentNode);
        GC.KeepAlive(finalizable);
        return survived &&
            handleRootSurvived &&
            reclaimed &&
            relocated &&
            pinned &&
            finalizationPending != 0 &&
            finalized &&
            subsequentAllocation;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateCollectibleObject()
    {
        object target = new byte[512 * 1024];
        return new WeakReference(target);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateCollectibleSmallObject()
    {
        object target = new Node { Value = 91, Next = new Node { Value = 92 } };
        return new WeakReference(target);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static GCHandle CreateHandleOnlyRoot() =>
        GCHandle.Alloc(new Node { Value = 73 });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateFinalizableObject()
    {
        object target = new Finalizable();
        return new WeakReference(target);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAllocatingFinalizableObject()
    {
        object target = new AllocatingFinalizable();
        return new WeakReference(target);
    }

    private static bool FinalizerAllocationCanTriggerCollection()
    {
        AllocatingFinalizable.Reset();
        WeakReference finalizable = CreateAllocatingFinalizableObject();
        ForceFullCollection();
        if (!SpinWait.SpinUntil(
            static () => Volatile.Read(ref AllocatingFinalizable.Started) != 0,
            TimeSpan.FromSeconds(30)))
        {
            return false;
        }

        if (!SpinWait.SpinUntil(
            static () => Volatile.Read(ref AllocatingFinalizable.Completed) != 0,
            TimeSpan.FromSeconds(30)))
        {
            return false;
        }

        GC.WaitForPendingFinalizers();

        bool result =
            Volatile.Read(ref AllocatingFinalizable.Completed) == 1 &&
            Volatile.Read(ref AllocatingFinalizable.CollectionsObserved) == 1;
        GC.KeepAlive(finalizable);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nuint AddressOf(byte[] array) =>
        (nuint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));

    private static void ForceFullCollection() =>
        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: true);

    private static void ForceCollection(int generation) =>
        GC.Collect(
            generation,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: true);

    private static void ForceFullNonCompactingRequest() =>
        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: false);

    private static bool AllocationsAreDistinctAndZeroed()
    {
        // This is substantially larger than the managed region allocator's allocation quantum,
        // so retaining every object verifies many allocation-context refills as well as their
        // zeroing and non-overlap.
        const int Count = 8192;
        byte[][] arrays = new byte[Count][];

        for (int i = 0; i < Count; i++)
        {
            byte[] array = new byte[64];

            // Each allocation, including one at the beginning of a refilled context, must be
            // zeroed before the EE installs its object header.
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
        byte[] smallBefore = new byte[32];
        smallBefore[0] = 3;
        long allocatedBefore = GC.GetTotalAllocatedBytes();

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
        byte[] pinned = GC.AllocateUninitializedArray<byte>(200_000, pinned: true);
        pinned[0] = 5;
        pinned[^1] = 6;
        byte[] smallAfter = new byte[32];
        smallAfter[0] = 4;
        long allocatedAfter = GC.GetTotalAllocatedBytes();

        return second[0] == 0 &&
            second[^1] == 0 &&
            pinned[0] == 5 &&
            pinned[^1] == 6 &&
            large[0] == 1 &&
            large[^1] == 2 &&
            smallBefore[0] == 3 &&
            smallAfter[0] == 4 &&
            allocatedAfter - allocatedBefore >= 600_000;
    }

    private static bool HandlesWork()
    {
        object target = new Node { Value = 42 };

        GCHandle normal = GCHandle.Alloc(target);
        GCHandle weak = GCHandle.Alloc(target, GCHandleType.Weak);
        GCHandle weakTrackResurrection =
            GCHandle.Alloc(target, GCHandleType.WeakTrackResurrection);
        GCHandle pinned = GCHandle.Alloc(new byte[16], GCHandleType.Pinned);

        try
        {
            if (!ReferenceEquals(normal.Target, target) ||
                !ReferenceEquals(weak.Target, target) ||
                !ReferenceEquals(weakTrackResurrection.Target, target))
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

            ForceFullCollection();
            if (!ReferenceEquals(normal.Target, replacement) ||
                !ReferenceEquals(weak.Target, target) ||
                !ReferenceEquals(weakTrackResurrection.Target, target) ||
                pinned.AddrOfPinnedObject() == IntPtr.Zero)
            {
                return false;
            }
        }
        finally
        {
            normal.Free();
            weak.Free();
            weakTrackResurrection.Free();
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

        ConditionalWeakTable<object, object> dependentHandles = new ConditionalWeakTable<object, object>();
        object primary = new object();
        object secondary = new Node { Value = 44 };
        dependentHandles.Add(primary, secondary);
        if (!dependentHandles.TryGetValue(primary, out object actualSecondary)
            || !ReferenceEquals(actualSecondary, secondary))
        {
            return false;
        }

        if (!dependentHandles.Remove(primary) || dependentHandles.TryGetValue(primary, out _))
        {
            return false;
        }

        return true;
    }

    private sealed class Node
    {
        public int Value;
        public Node Next;
    }

    private readonly struct PartialCollectionRoots
    {
        public PartialCollectionRoots(
            Node youngRoot,
            WeakReference weak,
            GCHandle handleOnlyRoot,
            byte[] pinnedObject,
            GCHandle pinnedHandle,
            Node dependentPrimary,
            ConditionalWeakTable<Node, Node> dependentHandles)
        {
            YoungRoot = youngRoot;
            Weak = weak;
            HandleOnlyRoot = handleOnlyRoot;
            PinnedObject = pinnedObject;
            PinnedHandle = pinnedHandle;
            DependentPrimary = dependentPrimary;
            DependentHandles = dependentHandles;
        }

        public Node YoungRoot { get; }
        public WeakReference Weak { get; }
        public GCHandle HandleOnlyRoot { get; }
        public byte[] PinnedObject { get; }
        public GCHandle PinnedHandle { get; }
        public Node DependentPrimary { get; }
        public ConditionalWeakTable<Node, Node> DependentHandles { get; }
    }

    private sealed class Finalizable
    {
        public static int Count;

        private long _value0 = 1;
        private long _value1 = 2;
        private long _value2 = 3;
        private long _value3 = 4;
        private long _value4 = 5;
        private long _value5 = 6;
        private long _value6 = 7;
        private long _value7 = 8;
        private long _value8 = 9;
        private long _value9 = 10;
        private long _value10 = 11;
        private long _value11 = 12;

        public static void Reset() => Volatile.Write(ref Count, 0);

        ~Finalizable()
        {
            if (_value0 + _value1 + _value2 + _value3 + _value4 + _value5 +
                _value6 + _value7 + _value8 + _value9 + _value10 + _value11 == 78)
            {
                Interlocked.Increment(ref Count);
            }
        }
    }

    private sealed class AllocatingFinalizable
    {
        public static int CollectionsObserved;
        public static int Completed;
        public static int Started;

        public static void Reset()
        {
            Volatile.Write(ref CollectionsObserved, 0);
            Volatile.Write(ref Completed, 0);
            Volatile.Write(ref Started, 0);
        }

        ~AllocatingFinalizable()
        {
            const int MaximumAllocations = 16_384;
            const int AllocationSize = 32 * 1024;
            int collectionCount = GC.CollectionCount(0);
            Volatile.Write(ref Started, 1);

            for (int i = 0;
                 i < MaximumAllocations && GC.CollectionCount(0) == collectionCount;
                 i++)
            {
                byte[] garbage = new byte[AllocationSize];
                garbage[0] = (byte)i;
                if ((i & 15) == 0)
                {
                    Thread.Yield();
                }
            }

            if (GC.CollectionCount(0) > collectionCount)
            {
                Volatile.Write(ref CollectionsObserved, 1);
            }

            Volatile.Write(ref Completed, 1);
        }
    }
}
