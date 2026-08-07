// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
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
        if (!FullCollectionReclaimsRelocatesAndPreservesRoots())
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

        Console.WriteLine("ManagedGC smoke test passed.");
        return 100;
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
    private static GCHandle CreateHandleOnlyRoot() =>
        GCHandle.Alloc(new Node { Value = 73 });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateFinalizableObject()
    {
        object target = new Finalizable();
        return new WeakReference(target);
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

            ForceFullCollection();
            if (!ReferenceEquals(normal.Target, replacement) ||
                !ReferenceEquals(weak.Target, target) ||
                pinned.AddrOfPinnedObject() == IntPtr.Zero)
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
}
