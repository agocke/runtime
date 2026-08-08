// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;

internal static class ManagedGCServerSmoke
{
    private const int ThreadCount = 4;
    private static readonly int[] s_homeHeaps = new int[ThreadCount];
    private static int s_start;

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

        Console.WriteLine(
            $"Managed server GC startup passed with {heapCount} heaps and {usedHeaps.Count} selected home heaps.");
        return 100;
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
