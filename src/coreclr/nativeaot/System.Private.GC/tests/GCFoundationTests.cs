// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

public sealed class GCFoundationTests
{
    private const int PatternCount = 7;

    private static readonly int[] s_depthLimitKiller =
    {
        0, 4, 102, 6, 198, 8, 201, 10, 197, 12, 196, 14, 195, 16, 194, 18,
        193, 20, 192, 22, 191, 24, 190, 26, 189, 28, 188, 30, 187, 32, 186, 34,
        185, 36, 184, 38, 183, 40, 182, 42, 181, 44, 180, 46, 179, 48, 178, 50,
        177, 52, 176, 54, 175, 56, 174, 58, 173, 60, 172, 62, 171, 64, 170, 66,
        169, 68, 168, 70, 167, 72, 166, 74, 165, 76, 164, 78, 163, 80, 162, 82,
        161, 84, 160, 86, 159, 88, 158, 90, 157, 92, 156, 94, 155, 96, 154, 98,
        153, 100, 152, 2, 3, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25,
        27, 29, 31, 33, 35, 37, 39, 41, 43, 45, 47, 49, 51, 53, 55, 57,
        59, 61, 63, 65, 67, 69, 71, 73, 75, 77, 79, 81, 83, 85, 87, 89,
        91, 93, 95, 97, 99, 101, 150, 149, 148, 147, 146, 145, 144, 143, 142, 141,
        140, 139, 138, 137, 136, 135, 134, 133, 132, 131, 130, 129, 128, 127, 126, 125,
        124, 123, 122, 121, 120, 119, 118, 117, 116, 115, 114, 113, 112, 111, 110, 109,
        108, 107, 106, 105, 104, 103, 151, 1
    };

    [Fact]
    public static void IntroSortMatchesReferenceSort()
    {
        int[] lengths = { 1, 2, 3, 8, 63, 64, 65, 66, 129, 512, 4099 };
        foreach (int length in lengths)
        {
            for (int pattern = 0; pattern < PatternCount; pattern++)
            {
                int[] values = MakePattern(length, pattern);
                AssertSortMatchesReference(ToAddresses(values, spread: false));
                AssertSortMatchesReference(ToAddresses(values, spread: true));
            }
        }

        AssertSortMatchesReference(ToAddresses(s_depthLimitKiller, spread: false));
        AssertSortMatchesReference(ToAddresses(s_depthLimitKiller, spread: true));
    }

    [Fact]
    public static void VxSortMatchesReferenceSort()
    {
        int[] lengths =
        {
            0, 1, 2, 3, 7, 8, 15, 16, 31, 32, 63, 64, 65, 66, 83, 84, 85,
            127, 128, 129, 255, 256, 257, 1024, 8192, 8193, 16384,
        };

        foreach (int length in lengths)
        {
            for (int pattern = 0; pattern < PatternCount; pattern++)
            {
                AssertVxSortMatchesReference(ToAddresses(MakePattern(length, pattern), spread: false));
            }
        }

        AssertVxSortMatchesReference(
            ToAddresses(
                MakePattern((int)VxSort.AVX512F_THRESHOLD_SIZE + 1, pattern: 4),
                spread: false));

        uint state = 0xD1B54A35;
        for (int iteration = 0; iteration < 40; iteration++)
        {
            state = (state * 1664525) + 1013904223;
            int length = 65 + (int)(state % 20000);
            nuint[] addresses = new nuint[length];
            for (int i = 0; i < addresses.Length; i++)
            {
                state = (state * 1664525) + 1013904223;
                addresses[i] = ((nuint)(state % 4096) + 1) * (nuint)sizeof(nuint);
            }

            AssertVxSortMatchesReference(addresses);
        }
    }

    [Fact]
    public static void VxSortInstructionSetSelectionMatchesNativeThresholds()
    {
#if TARGET_AMD64
        Assert.Equal(VxSort.scalar_isa, VxSort.select_isa(VxSort.AVX2_THRESHOLD_SIZE, true, true, false));
        Assert.Equal(VxSort.avx2_isa, VxSort.select_isa(VxSort.AVX2_THRESHOLD_SIZE + 1, true, false, false));
        Assert.Equal(VxSort.avx2_isa, VxSort.select_isa(VxSort.AVX512F_THRESHOLD_SIZE, true, true, false));
        Assert.Equal(VxSort.avx512_isa, VxSort.select_isa(VxSort.AVX512F_THRESHOLD_SIZE + 1, true, true, false));
        Assert.Equal(VxSort.scalar_isa, VxSort.select_isa(VxSort.AVX512F_THRESHOLD_SIZE + 1, false, true, false));
#else
        Assert.Equal(VxSort.scalar_isa, VxSort.select_isa(nint.MaxValue, true, true, true));
#endif
    }

    [Fact]
    public static void EventStatusTracksLevelsAndKeywordsPerProvider()
    {
        GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.None, GCEventLevel.None);
        GCEventStatus.Set(GCEventProvider.Private, GCEventKeyword.None, GCEventLevel.None);

        Assert.Equal(GCEventLevel.None, GCEventStatus.GetEnabledLevel(GCEventProvider.Default));
        Assert.Equal(GCEventKeyword.None, GCEventStatus.GetEnabledKeywords(GCEventProvider.Default));
        Assert.Equal(GCEventLevel.None, GCEventStatus.GetEnabledLevel(GCEventProvider.Private));
        Assert.Equal(GCEventKeyword.None, GCEventStatus.GetEnabledKeywords(GCEventProvider.Private));
        Assert.False(GCEventStatus.IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Fatal));

        GCEventStatus.Set(
            GCEventProvider.Default,
            GCEventKeyword.GC | GCEventKeyword.GCHandle,
            GCEventLevel.Information);

        Assert.Equal(GCEventLevel.Information, GCEventStatus.GetEnabledLevel(GCEventProvider.Default));
        Assert.Equal(
            GCEventKeyword.GC | GCEventKeyword.GCHandle,
            GCEventStatus.GetEnabledKeywords(GCEventProvider.Default));

        GCEventLevel[] atOrBelow =
        {
            GCEventLevel.None,
            GCEventLevel.Fatal,
            GCEventLevel.Error,
            GCEventLevel.Warning,
            GCEventLevel.Information,
        };

        foreach (GCEventLevel level in atOrBelow)
        {
            Assert.True(GCEventStatus.IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, level));
        }

        Assert.False(GCEventStatus.IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Verbose));
        Assert.False(GCEventStatus.IsEnabled(GCEventProvider.Default, GCEventKeyword.GCHeapDump, GCEventLevel.Fatal));
        Assert.True(GCEventStatus.IsEnabled(
            GCEventProvider.Default,
            GCEventKeyword.GCHeapDump | GCEventKeyword.GCHandle,
            GCEventLevel.Fatal));

        Assert.Equal(GCEventLevel.None, GCEventStatus.GetEnabledLevel(GCEventProvider.Private));
        Assert.Equal(GCEventKeyword.None, GCEventStatus.GetEnabledKeywords(GCEventProvider.Private));
        Assert.False(GCEventStatus.IsEnabled(GCEventProvider.Private, GCEventKeyword.GCPrivate, GCEventLevel.Fatal));

        GCEventStatus.Set(GCEventProvider.Private, GCEventKeyword.GCHandlePrivate, GCEventLevel.Verbose);

        Assert.True(GCEventStatus.IsEnabled(
            GCEventProvider.Private,
            GCEventKeyword.GCHandlePrivate,
            GCEventLevel.Verbose));
        Assert.False(GCEventStatus.IsEnabled(
            GCEventProvider.Default,
            GCEventKeyword.GCHandlePrivate,
            GCEventLevel.Verbose));
        Assert.Equal(GCEventLevel.Information, GCEventStatus.GetEnabledLevel(GCEventProvider.Default));

        GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.All, GCEventLevel.LogAlways);
        Assert.True(GCEventStatus.IsEnabled(
            GCEventProvider.Default,
            GCEventKeyword.GCSampledObjectAllocationLow,
            GCEventLevel.Verbose));
        Assert.Equal(GCEventLevel.LogAlways, GCEventStatus.GetEnabledLevel(GCEventProvider.Default));

        GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.None, GCEventLevel.None);
        GCEventStatus.Set(GCEventProvider.Private, GCEventKeyword.None, GCEventLevel.None);
        Assert.False(GCEventStatus.IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Fatal));
        Assert.False(GCEventStatus.IsEnabled(GCEventProvider.Private, GCEventKeyword.GCPrivate, GCEventLevel.Fatal));
    }

    [Fact]
    public static void EventEnumsAreSelfConsistent()
    {
        Assert.Equal(2, (int)GCEventProvider.Count);
        Assert.InRange((int)GCEventProvider.Default, 0, (int)GCEventProvider.Count - 1);
        Assert.InRange((int)GCEventProvider.Private, 0, (int)GCEventProvider.Count - 1);

        GCEventLevel[] ascending =
        {
            GCEventLevel.None,
            GCEventLevel.Fatal,
            GCEventLevel.Error,
            GCEventLevel.Warning,
            GCEventLevel.Information,
            GCEventLevel.Verbose,
            GCEventLevel.Max,
            GCEventLevel.LogAlways,
        };

        for (int i = 1; i < ascending.Length; i++)
        {
            Assert.True(ascending[i - 1] < ascending[i]);
        }

        Assert.Equal(GCEventKeyword.GC, GCEventKeyword.GCPrivate);
        Assert.Equal((GCEventKeyword)0, GCEventKeyword.None);

        GCEventKeyword all = GCEventKeyword.GC
            | GCEventKeyword.GCPrivate
            | GCEventKeyword.GCHandle
            | GCEventKeyword.GCHandlePrivate
            | GCEventKeyword.GCHeapDump
            | GCEventKeyword.GCSampledObjectAllocationHigh
            | GCEventKeyword.GCHeapSurvivalAndMovement
            | GCEventKeyword.ManagedHeapCollect
            | GCEventKeyword.GCHeapAndTypeNames
            | GCEventKeyword.GCSampledObjectAllocationLow;

        Assert.Equal(GCEventKeyword.All, all);
    }

    private static int[] MakePattern(int length, int pattern)
    {
        int[] values = new int[length];
        uint state = 0x9E3779B9;
        for (int i = 0; i < length; i++)
        {
            state = (state * 1664525) + 1013904223;
            values[i] = pattern switch
            {
                0 => i,
                1 => length - i,
                2 => 7,
                3 => i % 8,
                4 => (int)(state >> 8),
                5 => Math.Min(i, length - 1 - i),
                _ => i % 64,
            };
        }

        return values;
    }

    private static nuint[] ToAddresses(int[] values, bool spread)
    {
        nuint top = (nuint)1 << ((IntPtr.Size * 8) - 1);
        nuint[] addresses = new nuint[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            addresses[i] = (nuint)(values[i] + 1);
            if (spread && (i & 1) != 0)
            {
                addresses[i] |= top;
            }
        }

        return addresses;
    }

    private static unsafe void AssertSortMatchesReference(nuint[] addresses)
    {
        const nuint Guard = 0xF0F0F0F0;

        int length = addresses.Length;
        nuint[] expected = (nuint[])addresses.Clone();
        Array.Sort(expected);

        byte*[] buffer = new byte*[length + 2];
        fixed (byte** first = buffer)
        {
            byte** begin = first + 1;
            first[0] = (byte*)Guard;
            first[length + 1] = (byte*)Guard;
            for (int i = 0; i < length; i++)
            {
                begin[i] = (byte*)addresses[i];
            }

            IntroSort.Sort(begin, begin + length - 1);

            Assert.Equal((nuint)Guard, (nuint)first[0]);
            Assert.Equal((nuint)Guard, (nuint)first[length + 1]);
            for (int i = 0; i < length; i++)
            {
                Assert.Equal(expected[i], (nuint)begin[i]);
            }
        }
    }

    private static unsafe void AssertVxSortMatchesReference(nuint[] addresses)
    {
        const nuint Guard = 0xF0F0F0F0;

        int length = addresses.Length;
        nuint[] expected = (nuint[])addresses.Clone();
        Array.Sort(expected);

        byte*[] buffer = new byte*[length + 2];
        fixed (byte** first = buffer)
        {
            byte** begin = first + 1;
            first[0] = (byte*)Guard;
            first[length + 1] = (byte*)Guard;
            nuint low = nuint.MaxValue;
            nuint high = 0;
            for (int i = 0; i < length; i++)
            {
                nuint address = addresses[i];
                begin[i] = (byte*)address;
                low = Math.Min(low, address);
                high = Math.Max(high, address);
            }

            if (length == 0)
            {
                low = 0;
            }

            VxSort.do_vxsort(begin, length, (byte*)low, (byte*)high);

            Assert.Equal((nuint)Guard, (nuint)first[0]);
            Assert.Equal((nuint)Guard, (nuint)first[length + 1]);
            for (int i = 0; i < length; i++)
            {
                Assert.True(
                    expected[i] == (nuint)begin[i],
                    $"length={length}, index={i}, expected={expected[i]}, actual={(nuint)begin[i]}");
            }
        }
    }
}
