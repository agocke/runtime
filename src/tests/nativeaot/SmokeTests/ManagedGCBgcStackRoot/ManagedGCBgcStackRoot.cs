// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Threading;

internal static class ManagedGCBgcStackRootTest
{
    private static Node s_concurrentRoot;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Main()
    {
        const int ChainLength = 65_536;
        Node root = new Node { Value = 1 };
        Node tail = root;
        for (int i = 0; i < ChainLength; i++)
        {
            tail.Next = new Node { Value = i + 2 };
            tail = tail.Next;
        }

        s_concurrentRoot = root;
        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: false);

        tail.Payload = new StackOnlyPayload
        {
            Marker = 0x5a17,
            Data = new byte[1024]
        };
        tail.Payload.Data[0] = 0x6c;

        long previousBackgroundIndex =
            GC.GetGCMemoryInfo(GCKind.Background).Index;
        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Forced,
            blocking: false,
            compacting: false);

        StackOnlyPayload stackOnly = tail.Payload;
        tail.Payload = null;

        long deadline = Environment.TickCount64 + 30_000;
        bool completed = false;
        while (Environment.TickCount64 < deadline)
        {
            byte[] allocation = new byte[32 * 1024];
            allocation[0] = 1;
            if (GC.GetGCMemoryInfo(GCKind.Background).Index >
                previousBackgroundIndex)
            {
                completed = true;
                break;
            }
            Thread.Yield();
        }

        bool result =
            completed &&
            stackOnly.Marker == 0x5a17 &&
            stackOnly.Data.Length == 1024 &&
            stackOnly.Data[0] == 0x6c;
        if (!result)
        {
            Console.WriteLine(
                $"Stack-only BGC root failed: completed={completed}, " +
                $"index={GC.GetGCMemoryInfo(GCKind.Background).Index}, " +
                $"marker={stackOnly.Marker}, byte={stackOnly.Data[0]}.");
        }

        GC.KeepAlive(root);
        GC.KeepAlive(tail);
        GC.KeepAlive(stackOnly);
        return result ? 100 : 1;
    }

    private sealed class Node
    {
        public int Value;
        public Node Next;
        public StackOnlyPayload Payload;
    }

    private sealed class StackOnlyPayload
    {
        public int Marker;
        public byte[] Data;
    }
}
