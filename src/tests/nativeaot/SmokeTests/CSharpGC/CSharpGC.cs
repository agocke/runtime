// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.CompilerServices;

internal static class CSharpGC
{
    private const int Pass = 100;
    private const int Fail = 1;

    private static byte[] s_sohSurvivor;
    private static byte[] s_lohSurvivor;
    private static byte[] s_pohSurvivor;

    public static int Main()
    {
        s_sohSurvivor = new byte[1024];
        s_lohSurvivor = new byte[100_000];
        s_pohSurvivor = GC.AllocateArray<byte>(4096, pinned: true);
        s_sohSurvivor[0] = 1;
        s_lohSurvivor[0] = 2;
        s_pohSurvivor[0] = 3;

        WeakReference sohDead = AllocateDeadSoh();
        WeakReference lohDead = AllocateDeadLoh();
        WeakReference pohDead = AllocateDeadPoh();

        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: false);

        if (sohDead.IsAlive || lohDead.IsAlive || pohDead.IsAlive)
        {
            return Fail;
        }

        if (s_sohSurvivor[0] != 1 ||
            s_lohSurvivor[0] != 2 ||
            s_pohSurvivor[0] != 3)
        {
            return Fail;
        }

        byte[] sohAfter = new byte[2048];
        byte[] lohAfter = new byte[100_000];
        byte[] pohAfter = GC.AllocateArray<byte>(8192, pinned: true);
        sohAfter[0] = 4;
        lohAfter[0] = 5;
        pohAfter[0] = 6;

        return sohAfter[0] == 4 &&
            lohAfter[0] == 5 &&
            pohAfter[0] == 6
                ? Pass
                : Fail;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AllocateDeadSoh()
    {
        return new WeakReference(new byte[1024]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AllocateDeadLoh()
    {
        return new WeakReference(new byte[100_000]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AllocateDeadPoh()
    {
        return new WeakReference(
            GC.AllocateArray<byte>(4096, pinned: true));
    }
}
