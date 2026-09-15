// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal static class CSharpGC
{
    private const int Pass = 100;
    private const int Fail = 1;
    private const int CollectionCount = 16;
    private const int SohObjectSize = 1024;
    private const int LohObjectSize = 100 * 1024;
    private const int PohObjectSize = 16 * 1024;

    private static byte[] s_sohSurvivor;
    private static byte[] s_lohSurvivor;
    private static byte[] s_pohSurvivor;
    private static byte[] s_sohReuseCandidate1;
    private static byte[] s_sohReuseCandidate2;

    public static int Main()
    {
        if (!ValidateEmptyUohSegments())
        {
            return Fail;
        }

        s_sohSurvivor = new byte[1024];
        s_lohSurvivor = new byte[100_000];
        s_pohSurvivor = GC.AllocateArray<byte>(4096, pinned: true);
        s_sohSurvivor[0] = 1;
        s_lohSurvivor[0] = 2;
        s_pohSurvivor[0] = 3;

        bool sohReused = ValidateSohGapReuse();
        bool lohReused = false;
        bool pohReused = false;
        for (int collection = 0; collection < CollectionCount; collection++)
        {
            int liveCount;
            int deadCount;
            switch (collection % 4)
            {
                case 0:
                    liveCount = 2;
                    deadCount = 2;
                    break;
                case 1:
                    liveCount = 48;
                    deadCount = 0;
                    break;
                case 2:
                    liveCount = 0;
                    deadCount = 48;
                    break;
                default:
                    liveCount = 48;
                    deadCount = 48;
                    break;
            }

            if (!RunRegionCycle(
                SohObjectSize,
                pinned: false,
                liveCount,
                deadCount,
                ref sohReused))
            {
                return Fail;
            }

            if (!RunRegionCycle(
                LohObjectSize,
                pinned: false,
                liveCount,
                deadCount,
                ref lohReused))
            {
                return Fail;
            }

            if (!RunRegionCycle(
                PohObjectSize,
                pinned: true,
                liveCount,
                deadCount,
                ref pohReused))
            {
                return Fail;
            }

            if (s_sohSurvivor[0] != 1 ||
                s_lohSurvivor[0] != 2 ||
                s_pohSurvivor[0] != 3)
            {
                return Fail;
            }
        }

        return sohReused && lohReused && pohReused
            ? Pass
            : Fail;
    }

    private static bool ValidateEmptyUohSegments()
    {
        PrepareDeadAllocation(
            LohObjectSize,
            pinned: false,
            out WeakReference lohReference,
            out nint lohAddress);
        PrepareDeadAllocation(
            PohObjectSize,
            pinned: true,
            out WeakReference pohReference,
            out nint pohAddress);

        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: false);

        if (lohReference.IsAlive || pohReference.IsAlive)
        {
            return false;
        }

        byte[] lohReplacement = Allocate(LohObjectSize, pinned: false);
        byte[] pohReplacement = Allocate(PohObjectSize, pinned: true);
        return GetAddress(lohReplacement) == lohAddress &&
            GetAddress(pohReplacement) == pohAddress;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PrepareDeadAllocation(
        int size,
        bool pinned,
        out WeakReference reference,
        out nint address)
    {
        byte[] value = Allocate(size, pinned);
        value[0] = 1;
        reference = new WeakReference(value);
        address = GetAddress(value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RunRegionCycle(
        int size,
        bool pinned,
        int liveCount,
        int deadCount,
        ref bool reused)
    {
        byte[][] live = new byte[liveCount][];
        byte[][] dead = new byte[deadCount][];
        WeakReference[] deadReferences = new WeakReference[deadCount];
        nint[] deadAddresses = new nint[deadCount];

        int count = liveCount > deadCount ? liveCount : deadCount;
        for (int i = 0; i < count; i++)
        {
            if (i < liveCount)
            {
                live[i] = Allocate(size, pinned);
                live[i][0] = (byte)(i + 1);
            }

            if (i < deadCount)
            {
                dead[i] = Allocate(size, pinned);
                dead[i][0] = (byte)(i + 1);
                deadReferences[i] = new WeakReference(dead[i]);
                deadAddresses[i] = GetAddress(dead[i]);
                dead[i] = null;
            }
        }

        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: false);

        for (int i = 0; i < deadCount; i++)
        {
            if (deadReferences[i].IsAlive)
            {
                return false;
            }
        }

        for (int i = 0; i < liveCount; i++)
        {
            if (live[i][0] != (byte)(i + 1))
            {
                return false;
            }
        }

        for (int i = 0; i < deadCount; i++)
        {
            byte[] replacement = Allocate(size, pinned);
            replacement[0] = 7;
            if (ContainsAddressRange(
                deadAddresses,
                GetAddress(replacement),
                size))
            {
                reused = true;
            }
        }

        GC.KeepAlive(live);
        return true;
    }

    private static bool ValidateSohGapReuse()
    {
        PrepareSohGap(
            out WeakReference firstReference,
            out WeakReference secondReference,
            out nint firstAddress,
            out nint secondAddress);

        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: false);

        if (firstReference.IsAlive || secondReference.IsAlive)
        {
            return false;
        }

        byte[] replacement = new byte[SohObjectSize];
        replacement[0] = 1;
        nint replacementAddress = GetAddress(replacement);
        nint lowAddress = firstAddress < secondAddress
            ? firstAddress
            : secondAddress;
        nint highAddress = firstAddress < secondAddress
            ? secondAddress
            : firstAddress;
        return replacementAddress >= lowAddress &&
            (nuint)(replacementAddress - lowAddress) <
                (nuint)(highAddress - lowAddress) + (nuint)SohObjectSize;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PrepareSohGap(
        out WeakReference firstReference,
        out WeakReference secondReference,
        out nint firstAddress,
        out nint secondAddress)
    {
        s_sohReuseCandidate1 = new byte[SohObjectSize];
        s_sohReuseCandidate2 = new byte[SohObjectSize];
        s_sohReuseCandidate1[0] = 1;
        s_sohReuseCandidate2[0] = 2;

        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: false);
        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: false);

        firstAddress = GetAddress(s_sohReuseCandidate1);
        secondAddress = GetAddress(s_sohReuseCandidate2);
        firstReference = new WeakReference(s_sohReuseCandidate1);
        secondReference = new WeakReference(s_sohReuseCandidate2);
        s_sohReuseCandidate1 = null;
        s_sohReuseCandidate2 = null;
    }

    private static byte[] Allocate(int size, bool pinned)
    {
        return pinned
            ? GC.AllocateArray<byte>(size, pinned: true)
            : new byte[size];
    }

    private static nint GetAddress(byte[] value)
    {
        GCHandle handle = GCHandle.Alloc(value, GCHandleType.Pinned);
        nint address = handle.AddrOfPinnedObject();
        handle.Free();
        return address;
    }

    private static bool ContainsAddressRange(
        nint[] addresses,
        nint address,
        int size)
    {
        for (int i = 0; i < addresses.Length; i++)
        {
            if (address >= addresses[i] &&
                (nuint)(address - addresses[i]) < (nuint)size)
            {
                return true;
            }
        }

        return false;
    }
}
