// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Behavior tests for the pure-computation half of the ported GC environment layer: the helpers
// of gcenv.base.h, the atomics of gcenv.interlocked.h, the volatile accessors of volatile.h and
// the AffinitySet bitset of gcenv.os.h.
//
// The other half of that layer -- everything that reaches the operating system -- is a set of
// [RuntimeImport] forwarders that only resolve inside a NativeAOT image, so it is covered by the
// managed-GC smoke test rather than here. The types are still compiled into this assembly, which
// is what lets GCInterfaceLayoutTests check their layouts.

using System;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

public sealed unsafe class GCEnvironmentTests
{
    public static TheoryData<ulong, ulong, ulong> AlignmentCases() => new()
    {
        { 0, 8, 0 },
        { 1, 8, 8 },
        { 7, 8, 8 },
        { 8, 8, 8 },
        { 9, 8, 16 },
        { 0, 1, 0 },
        { 12345, 1, 12345 },
        { 4095, 4096, 4096 },
        { 4096, 4096, 4096 },
        { 4097, 4096, 8192 },
    };

    [Theory]
    [MemberData(nameof(AlignmentCases))]
    public void AlignUpRoundsUpToTheAlignment(ulong value, ulong alignment, ulong expected)
    {
        Assert.Equal((nuint)expected, GCEnv.ALIGN_UP((nuint)value, (nuint)alignment));
        Assert.Equal((nuint)expected, (nuint)GCEnv.ALIGN_UP((byte*)value, (nuint)alignment));
    }

    [Theory]
    [MemberData(nameof(AlignmentCases))]
    public void AlignDownRoundsDownToTheAlignment(ulong value, ulong alignment, ulong alignedUp)
    {
        nuint expected = (nuint)(alignedUp == value ? value : alignedUp - alignment);

        Assert.Equal(expected, GCEnv.ALIGN_DOWN((nuint)value, (nuint)alignment));
        Assert.Equal(expected, (nuint)GCEnv.ALIGN_DOWN((byte*)value, (nuint)alignment));
        Assert.Equal(expected, (nuint)GCEnv.ALIGN_DOWN((void*)value, (nuint)alignment));
    }

    [Fact]
    public void AlignmentIsIdempotentAtThePointerCeiling()
    {
        // The C++ helper asserts that ALIGN_UP does not go backwards. The largest value it can
        // be asked for without overflowing is the last aligned address.
        nuint last = (nuint)(nuint.MaxValue - 7);
        Assert.Equal(last, GCEnv.ALIGN_UP(last, 8));
        Assert.Equal(last, GCEnv.ALIGN_DOWN(nuint.MaxValue, 8));
    }

    [Fact]
    public void BitScanForwardFindsTheLowestSetBit()
    {
        uint index;
        Assert.Equal((byte)0, GCEnv.BitScanForward(&index, 0));

        for (int bit = 0; bit < 32; bit++)
        {
            uint mask = 1u << bit;
            Assert.Equal((byte)1, GCEnv.BitScanForward(&index, mask));
            Assert.Equal((uint)bit, index);

            // Bits above the lowest one must not change the answer.
            Assert.Equal((byte)1, GCEnv.BitScanForward(&index, mask | 0x80000000u));
            Assert.Equal((uint)bit, index);
        }
    }

    [Fact]
    public void BitScanReverseFindsTheHighestSetBit()
    {
        uint index;
        Assert.Equal((byte)0, GCEnv.BitScanReverse(&index, 0));

        for (int bit = 0; bit < 32; bit++)
        {
            uint mask = 1u << bit;
            Assert.Equal((byte)1, GCEnv.BitScanReverse(&index, mask));
            Assert.Equal((uint)bit, index);

            Assert.Equal((byte)1, GCEnv.BitScanReverse(&index, mask | 1u));
            Assert.Equal((uint)bit, index);
        }
    }

    [Fact]
    public void BitScan64CoversTheWholeWord()
    {
        uint index;
        Assert.Equal((byte)0, GCEnv.BitScanForward64(&index, 0));
        Assert.Equal((byte)0, GCEnv.BitScanReverse64(&index, 0));

        for (int bit = 0; bit < 64; bit++)
        {
            ulong mask = 1ul << bit;

            Assert.Equal((byte)1, GCEnv.BitScanForward64(&index, mask));
            Assert.Equal((uint)bit, index);

            Assert.Equal((byte)1, GCEnv.BitScanReverse64(&index, mask));
            Assert.Equal((uint)bit, index);
        }

        Assert.Equal((byte)1, GCEnv.BitScanForward64(&index, 0x8000_0001_0000_0000ul));
        Assert.Equal(32u, index);
        Assert.Equal((byte)1, GCEnv.BitScanReverse64(&index, 0x8000_0001_0000_0000ul));
        Assert.Equal(63u, index);
    }

    [Fact]
    public void FitsInU1AcceptsExactlyTheByteRange()
    {
        Assert.True(GCEnv.FitsInU1(0));
        Assert.True(GCEnv.FitsInU1(255));
        Assert.False(GCEnv.FitsInU1(256));
        Assert.False(GCEnv.FitsInU1(ulong.MaxValue));
    }

    [Fact]
    public void HResultHelpersMatchTheCppMacros()
    {
        Assert.True(GCEnv.SUCCEEDED(GCEnv.S_OK));
        Assert.False(GCEnv.FAILED(GCEnv.S_OK));
        Assert.True(GCEnv.FAILED(GCEnv.E_FAIL));
        Assert.True(GCEnv.FAILED(GCEnv.E_OUTOFMEMORY));
        Assert.True(GCEnv.FAILED(GCEnv.CLR_E_GC_BAD_AFFINITY_CONFIG));

        Assert.Equal(0, GCEnv.HRESULT_FROM_WIN32(0));
        Assert.Equal(unchecked((int)0x800705B4), GCEnv.HRESULT_FROM_WIN32(1460));
    }

    public static TheoryData<string, bool, ulong, ulong, int> IndexOrRangeCases() => new()
    {
        // text, parsed, start, end, characters consumed
        { "0", true, 0, 0, 1 },
        { "12", true, 12, 12, 2 },
        { "1-5", true, 1, 5, 3 },
        { "0-0", true, 0, 0, 3 },
        { "5-1", true, 5, 1, 3 },
        { "7,9", true, 7, 7, 1 },
        { "3-4,8", true, 3, 4, 3 },
        { " 6", true, 6, 6, 2 },
        { "", false, 0, 0, 0 },
        { "a", false, 0, 0, 0 },
        { "2-", false, 0, 0, 0 },
        { "2-x", false, 0, 0, 0 },
    };

    [Theory]
    [MemberData(nameof(IndexOrRangeCases))]
    public void ParseIndexOrRangeMatchesStrtoul(string text, bool parsed, ulong start, ulong end, int consumed)
    {
        Span<byte> buffer = stackalloc byte[text.Length + 1];
        for (int i = 0; i < text.Length; i++)
        {
            buffer[i] = (byte)text[i];
        }

        buffer[text.Length] = 0;

        fixed (byte* first = buffer)
        {
            byte* cursor = first;
            nuint startIndex = 0xdead;
            nuint endIndex = 0xbeef;

            Assert.Equal(parsed, GCEnv.ParseIndexOrRange(&cursor, &startIndex, &endIndex));

            if (parsed)
            {
                Assert.Equal((nuint)start, startIndex);
                Assert.Equal((nuint)end, endIndex);
                Assert.Equal(consumed, (int)(cursor - first));
            }
        }
    }

    [Fact]
    public void ParseIndexOrRangeNegatesLikeStrtoul()
    {
        // strtoul accepts a sign and negates the result, so a negative index parses into a huge
        // one. The range check in ParseGCHeapAffinitizeRanges is what rejects it, exactly as in
        // the C++ GC.
        ReadOnlySpan<byte> text = "-3"u8;
        Span<byte> buffer = stackalloc byte[text.Length + 1];
        text.CopyTo(buffer);
        buffer[text.Length] = 0;

        fixed (byte* first = buffer)
        {
            byte* cursor = first;
            nuint startIndex = 0;
            nuint endIndex = 0;

            Assert.True(GCEnv.ParseIndexOrRange(&cursor, &startIndex, &endIndex));
            Assert.Equal(unchecked((nuint)0 - 3), startIndex);
            Assert.Equal(unchecked((nuint)0 - 3), endIndex);
            Assert.Equal(2, (int)(cursor - first));
        }
    }

    [Theory]
    [InlineData("999999999999999999999999")]
    // strtoul returns ULONG_MAX on overflow whatever the sign was, so the saturation has to win
    // over the negation rather than be negated by it.
    [InlineData("-999999999999999999999999")]
    public void ParseIndexOrRangeSaturatesInsteadOfWrapping(string text)
    {
        // strtoul clamps to ULONG_MAX, which keeps an absurd index out of the valid range
        // instead of aliasing a real processor.
        Span<byte> buffer = stackalloc byte[text.Length + 1];
        for (int i = 0; i < text.Length; i++)
        {
            buffer[i] = (byte)text[i];
        }

        buffer[text.Length] = 0;

        fixed (byte* first = buffer)
        {
            byte* cursor = first;
            nuint startIndex = 0;
            nuint endIndex = 0;

            Assert.True(GCEnv.ParseIndexOrRange(&cursor, &startIndex, &endIndex));
            Assert.Equal(nuint.MaxValue, startIndex);
            Assert.Equal(nuint.MaxValue, endIndex);
            Assert.Equal(text.Length, (int)(cursor - first));
        }
    }

    [Fact]
    public void InterlockedIncrementAndDecrementReturnTheNewValue()
    {
        int i32 = 5;
        Assert.Equal(6, Interlocked.Increment(&i32));
        Assert.Equal(5, Interlocked.Decrement(&i32));

        uint u32 = uint.MaxValue;
        Assert.Equal(0u, Interlocked.Increment(&u32));
        Assert.Equal(uint.MaxValue, Interlocked.Decrement(&u32));

        long i64 = long.MaxValue;
        Assert.Equal(long.MinValue, Interlocked.Increment(&i64));

        ulong u64 = 0;
        Assert.Equal(ulong.MaxValue, Interlocked.Decrement(&u64));
    }

    [Fact]
    public void InterlockedAndOrUpdateInPlace()
    {
        uint u32 = 0b1111;
        Interlocked.And(&u32, 0b1010);
        Assert.Equal(0b1010u, u32);
        Interlocked.Or(&u32, 0b0101);
        Assert.Equal(0b1111u, u32);

        // The GC clears a sync block bit with And(~bit), which must not disturb the top bit.
        uint header = 0x8000_0000u | 0x2000_0000u;
        Interlocked.And(&header, ~0x2000_0000u);
        Assert.Equal(0x8000_0000u, header);

        long i64 = -1;
        Interlocked.And(&i64, 0x00FF_00FF_00FF_00FF);
        Assert.Equal(0x00FF_00FF_00FF_00FF, i64);
    }

    [Fact]
    public void InterlockedExchangeReturnsThePreviousValue()
    {
        int i32 = 3;
        Assert.Equal(3, Interlocked.Exchange(&i32, 9));
        Assert.Equal(9, i32);

        nuint n = 17;
        Assert.Equal((nuint)17, Interlocked.Exchange(&n, 42));
        Assert.Equal((nuint)42, n);

        void* target = (void*)0x1000;
        Assert.Equal((nint)0x1000, (nint)Interlocked.ExchangePointer(&target, (void*)0x2000));
        Assert.Equal((nint)0x2000, (nint)target);
    }

    [Fact]
    public void InterlockedExchangeAddReturnsThePreviousValue()
    {
        // This is the difference between the C++ ExchangeAdd and the managed Interlocked.Add,
        // and the reason the port cannot simply forward to the latter.
        int i32 = 10;
        Assert.Equal(10, Interlocked.ExchangeAdd(&i32, 5));
        Assert.Equal(15, i32);

        uint u32 = 1;
        Assert.Equal(1u, Interlocked.ExchangeAdd(&u32, uint.MaxValue));
        Assert.Equal(0u, u32);

        long i64 = 1L << 40;
        Assert.Equal(1L << 40, Interlocked.ExchangeAdd64(&i64, -1));
        Assert.Equal((1L << 40) - 1, i64);

        // The subtraction the port uses to recover the previous value has to survive wrapping.
        ulong u64 = ulong.MaxValue;
        Assert.Equal(ulong.MaxValue, Interlocked.ExchangeAdd64(&u64, 2));
        Assert.Equal(1ul, u64);

        nint p = 100;
        Assert.Equal((nint)100, Interlocked.ExchangeAddPtr(&p, -30));
        Assert.Equal((nint)70, p);

        nuint up = 0;
        Assert.Equal((nuint)0, Interlocked.ExchangeAddPtr(&up, nuint.MaxValue));
        Assert.Equal(nuint.MaxValue, up);
    }

    [Fact]
    public void InterlockedCompareExchangeSwapsOnlyOnAMatch()
    {
        int i32 = 4;
        Assert.Equal(4, Interlocked.CompareExchange(&i32, 7, 5));
        Assert.Equal(4, i32);
        Assert.Equal(4, Interlocked.CompareExchange(&i32, 7, 4));
        Assert.Equal(7, i32);

        ulong u64 = ulong.MaxValue;
        Assert.Equal(ulong.MaxValue, Interlocked.CompareExchange(&u64, 1, 0));
        Assert.Equal(ulong.MaxValue, u64);
        Assert.Equal(ulong.MaxValue, Interlocked.CompareExchange(&u64, 1, ulong.MaxValue));
        Assert.Equal(1ul, u64);

        void* target = null;
        Assert.Equal((nint)0, (nint)Interlocked.CompareExchangePointer(&target, (void*)0x30, (void*)0x10));
        Assert.Equal((nint)0, (nint)target);
        Assert.Equal((nint)0, (nint)Interlocked.CompareExchangePointer(&target, (void*)0x30, null));
        Assert.Equal((nint)0x30, (nint)target);
    }

    [Fact]
    public void VolatileAccessorsRoundTripEveryWidth()
    {
        byte u8 = 0;
        GCEnv.VolatileStore(&u8, 0xAB);
        Assert.Equal((byte)0xAB, GCEnv.VolatileLoad(&u8));
        Assert.Equal((byte)0xAB, GCEnv.VolatileLoadWithoutBarrier(&u8));

        ushort u16 = 0;
        GCEnv.VolatileStoreWithoutBarrier(&u16, 0xBEEF);
        Assert.Equal((ushort)0xBEEF, GCEnv.VolatileLoad(&u16));

        int i32 = 0;
        GCEnv.VolatileStore(&i32, int.MinValue);
        Assert.Equal(int.MinValue, GCEnv.VolatileLoad(&i32));

        uint u32 = 0;
        GCEnv.VolatileStore(&u32, uint.MaxValue);
        Assert.Equal(uint.MaxValue, GCEnv.VolatileLoad(&u32));

        long i64 = 0;
        GCEnv.VolatileStore(&i64, long.MinValue);
        Assert.Equal(long.MinValue, GCEnv.VolatileLoad(&i64));

        ulong u64 = 0;
        GCEnv.VolatileStore(&u64, ulong.MaxValue);
        Assert.Equal(ulong.MaxValue, GCEnv.VolatileLoad(&u64));

        nint n = 0;
        GCEnv.VolatileStore(&n, -1);
        Assert.Equal((nint)(-1), GCEnv.VolatileLoad(&n));

        nuint un = 0;
        GCEnv.VolatileStore(&un, nuint.MaxValue);
        Assert.Equal(nuint.MaxValue, GCEnv.VolatileLoad(&un));

        void* p = null;
        GCEnv.VolatileStore(&p, (void*)0x1234);
        Assert.Equal((nint)0x1234, (nint)GCEnv.VolatileLoad(&p));
        Assert.Equal((nint)0x1234, (nint)GCEnv.VolatileLoadWithoutBarrier(&p));
    }

    [Fact]
    public void AffinitySetRoundsItsCapacityUpToAWholeWord()
    {
        int bitsPerWord = 8 * sizeof(nuint);

        AssertCapacity(0, 0);
        AssertCapacity(1, bitsPerWord);
        AssertCapacity(bitsPerWord, bitsPerWord);
        AssertCapacity(bitsPerWord + 1, 2 * bitsPerWord);

        static void AssertCapacity(int cpuCount, int expected)
        {
            int words = (cpuCount + (8 * sizeof(nuint)) - 1) / (8 * sizeof(nuint));
            Span<nuint> storage = stackalloc nuint[Math.Max(words, 1)];
            storage.Clear();

            fixed (nuint* bits = storage)
            {
                AffinitySet set = default;
                set.InitializeWithStorage(bits, (nuint)words);
                Assert.Equal((nuint)expected, set.MaxCpuCount());
            }
        }
    }

    [Fact]
    public void AffinitySetTracksMembership()
    {
        const int CpuCount = 200;
        int words = (CpuCount + (8 * sizeof(nuint)) - 1) / (8 * sizeof(nuint));
        Span<nuint> storage = stackalloc nuint[words];
        storage.Clear();

        fixed (nuint* bits = storage)
        {
            AffinitySet set = default;
            set.InitializeWithStorage(bits, (nuint)words);

            Assert.True(set.IsEmpty());
            Assert.Equal((nuint)0, set.Count());

            // Indices are chosen to straddle the word boundaries the bitset is made of.
            int[] members = { 0, 1, 63, 64, 65, 127, 128, 199 };
            foreach (int member in members)
            {
                set.Add((nuint)member);
            }

            Assert.False(set.IsEmpty());
            Assert.Equal((nuint)members.Length, set.Count());

            for (nuint i = 0; i < set.MaxCpuCount(); i++)
            {
                Assert.Equal(Array.IndexOf(members, (int)i) >= 0, set.Contains(i));
            }

            // Adding a member twice must not change the count, and removing one that is not
            // there must not either.
            set.Add(64);
            set.Remove(2);
            Assert.Equal((nuint)members.Length, set.Count());

            foreach (int member in members)
            {
                set.Remove((nuint)member);
            }

            Assert.True(set.IsEmpty());
            Assert.Equal((nuint)0, set.Count());
        }
    }

    [Fact]
    public void EmptyAffinitySetIsEmptyAndHoldsNothing()
    {
        AffinitySet set = default;
        Assert.True(set.IsEmpty());
        Assert.Equal((nuint)0, set.Count());
        Assert.Equal((nuint)0, set.MaxCpuCount());
        Assert.True(set.GetBitsetData() == null);
    }
}
