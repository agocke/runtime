// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading;

internal static class ManagedGCBgcAllocationStressTest
{
    private static int Main()
    {
        int ready = 0;
        int start = 0;
        int collectionsFinished = 0;
        Thread collector = new(() =>
        {
            Volatile.Write(ref ready, 1);
            while (Volatile.Read(ref start) == 0)
            {
                Thread.Yield();
            }

            for (int cycle = 0; cycle < 4; cycle++)
            {
                GC.Collect(
                    GC.MaxGeneration,
                    GCCollectionMode.Forced,
                    blocking: false,
                    compacting: false);
                Thread.Yield();
            }
            Volatile.Write(ref collectionsFinished, 1);
        });
        collector.Start();

        while (Volatile.Read(ref ready) == 0)
        {
            Thread.Yield();
        }
        Volatile.Write(ref start, 1);

        long checksum = 0;
        for (int i = 0;
             Volatile.Read(ref collectionsFinished) == 0;
             i++)
        {
            byte[] allocation = new byte[100_000];
            allocation[0] = (byte)i;
            allocation[^1] = (byte)(i >> 8);
            checksum += allocation[0] + allocation[^1];
            if ((i & 31) == 0)
            {
                Thread.Yield();
            }
        }

        long deadline = Environment.TickCount64 + 30_000;
        while (collector.IsAlive && Environment.TickCount64 < deadline)
        {
            Thread.Yield();
        }

        bool result =
            Volatile.Read(ref collectionsFinished) != 0 &&
            !collector.IsAlive &&
            checksum != 0;
        if (!result)
        {
            Console.WriteLine(
                $"Allocation-helper BGC stress failed: completed=" +
                $"{Volatile.Read(ref collectionsFinished) != 0}, " +
                $"stopped={!collector.IsAlive}, checksum={checksum}.");
        }

        GC.KeepAlive(collector);
        return result ? 100 : 1;
    }
}
