// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

internal static class ManagedGCBgcTest
{
    private static int Main()
    {
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
        Node dependentPrimary = new Node { Value = 200 };
        ConditionalWeakTable<Node, Node> dependentHandles = new();
        dependentHandles.Add(dependentPrimary, new Node { Value = 201 });
        Finalizable.Reset();
        WeakReference finalizable = CreateFinalizableObject();
        Node oldTail = tail;
        int collectionCount = GC.CollectionCount(GC.MaxGeneration);

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
            for (int i = 0; i < 4096; i++)
            {
                tail.Next = new Node { Value = 300 + i };
                tail = tail.Next;
                mutations++;
                byte[] garbage = new byte[256];
                garbage[0] = (byte)i;
                if ((i & 255) == 0)
                {
                    Thread.Sleep(1);
                }
            }

            bool dependentAlive = dependentHandles.TryGetValue(
                dependentPrimary,
                out Node dependentSecondary);

            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Forced,
                blocking: true,
                compacting: false);

            for (int cycle = 0; cycle < 3; cycle++)
            {
                GC.Collect(
                    GC.MaxGeneration,
                    GCCollectionMode.Forced,
                    blocking: false,
                    compacting: false);
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

            byte[] subsequent = new byte[2048];
            subsequent[0] = 0x6d;
            bool result =
                returnedBeforeCompletion &&
                mutations != 0 &&
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
                dependentAlive &&
                dependentSecondary.Value == 201 &&
                Volatile.Read(ref Finalizable.Count) == 1 &&
                allocationSurvivor[0] == 0x2c &&
                subsequent[0] == 0x6d;

            GC.KeepAlive(root);
            GC.KeepAlive(tail);
            GC.KeepAlive(weak);
            GC.KeepAlive(pinnedObject);
            GC.KeepAlive(dependentPrimary);
            GC.KeepAlive(dependentHandles);
            GC.KeepAlive(dependentSecondary);
            GC.KeepAlive(finalizable);
            GC.KeepAlive(oldTail);
            GC.KeepAlive(cardChild);
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
                    $"weak={weak.IsAlive}, dependent={dependentAlive}, finalizers={Volatile.Read(ref Finalizable.Count)}");
            }
            return result ? 100 : 1;
        }
        finally
        {
            strongHandle.Free();
            pinnedHandle.Free();
        }
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
