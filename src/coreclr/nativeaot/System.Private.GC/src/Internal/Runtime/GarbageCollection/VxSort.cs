// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from src/coreclr/gc/gc.cpp and src/coreclr/gc/vxsort.

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe class VxSort
    {
        internal const int scalar_isa = 0;
        internal const int avx2_isa = 1;
        internal const int avx512_isa = 2;
        internal const nint AVX2_THRESHOLD_SIZE = 8 * 1024;
        internal const nint AVX512F_THRESHOLD_SIZE = 128 * 1024;
        internal const nint NEON_THRESHOLD_SIZE = 1024;

        private const int Avx2N = 4;
        private const int Avx512N = 8;
        private const int Avx2SmallSortThresholdElements = 16 * Avx2N;
        private const int Avx512SmallSortThresholdElements = 16 * Avx512N;

        public static void do_vxsort(byte** item_array, nint item_count, byte* range_low, byte* range_high)
        {
            if (item_count <= 1)
            {
                return;
            }

            int isa = select_isa(
                item_count,
#if TARGET_AMD64
                Avx2.IsSupported,
                Avx512F.IsSupported,
#else
                false,
                false,
#endif
#if TARGET_ARM64
                System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported);
#else
                false);
#endif

            switch (isa)
            {
                case avx512_isa:
                    do_vxsort_avx512(item_array, item_array + item_count - 1, range_low, range_high);
                    break;
                case avx2_isa:
                    do_vxsort_avx2(item_array, item_array + item_count - 1, range_low, range_high);
                    break;
                default:
                    IntroSort.Sort(item_array, item_array + item_count - 1);
                    break;
            }

#if DEBUG
            for (nint i = 0; i < item_count - 1; i++)
            {
                Debug.Assert(item_array[i] <= item_array[i + 1]);
            }

            Debug.Assert(range_low <= item_array[0] && item_array[item_count - 1] <= range_high);
#endif
        }

        internal static int select_isa(
            nint item_count,
            bool avx2Supported,
            bool avx512Supported,
            bool neonSupported)
        {
#if TARGET_AMD64
            if (avx2Supported && item_count > AVX2_THRESHOLD_SIZE)
            {
                return avx512Supported && item_count > AVX512F_THRESHOLD_SIZE
                    ? avx512_isa
                    : avx2_isa;
            }
#elif TARGET_ARM64
            _ = avx2Supported;
            _ = avx512Supported;
            if (neonSupported && item_count > NEON_THRESHOLD_SIZE)
            {
                return scalar_isa;
            }
#else
            _ = avx2Supported;
            _ = avx512Supported;
            _ = neonSupported;
#endif

            return scalar_isa;
        }

        public static void do_vxsort_avx2(byte** low, byte** high, byte* range_low, byte* range_high)
        {
            Debug.Assert(Avx2.IsSupported);
            Debug.Assert(sizeof(nuint) == 8);
            sort_vectorized(
                (long*)low,
                (long*)high,
                (long)range_low,
                (long)(range_high + sizeof(byte*)),
                useAvx512: false);
        }

        public static void do_vxsort_avx512(byte** low, byte** high, byte* range_low, byte* range_high)
        {
            Debug.Assert(Avx512F.IsSupported);
            Debug.Assert(sizeof(nuint) == 8);
            sort_vectorized(
                (long*)low,
                (long*)high,
                (long)range_low,
                (long)(range_high + sizeof(byte*)),
                useAvx512: true);
        }

        private static void sort_vectorized(
            long* left,
            long* right,
            long left_hint,
            long right_hint,
            bool useAvx512)
        {
            _ = left_hint;
            _ = right_hint;
            int depthLimit = 2 * floor_log2_plus_one((nuint)(right + 1 - left));
            sort_vectorized(left, right, depthLimit, useAvx512);
        }

        private static void sort_vectorized(
            long* left,
            long* right,
            int depth_limit,
            bool useAvx512)
        {
            nint length = (nint)(right - left + 1);
            long* mid;

            switch (length)
            {
                case 0:
                case 1:
                    return;
                case 2:
                    swap_if_greater(left, right);
                    return;
                case 3:
                    mid = right - 1;
                    swap_if_greater(left, mid);
                    swap_if_greater(left, right);
                    swap_if_greater(mid, right);
                    return;
            }

            int smallSortThreshold = useAvx512
                ? Avx512SmallSortThresholdElements
                : Avx2SmallSortThresholdElements;
            if (length <= smallSortThreshold)
            {
                IntroSort.Sort((byte**)left, (byte**)right);
                return;
            }

            if (depth_limit == 0)
            {
                heap_sort(left, right);
                return;
            }
            depth_limit--;

            mid = left + ((right - left) / 2);
            swap_if_greater(left, mid);
            swap_if_greater(left, right - 1);
            swap_if_greater(mid, right - 1);
            swap(mid, right);

            long* sep = vectorized_partition(left, right, useAvx512);
            sort_vectorized(left, sep - 2, depth_limit, useAvx512);
            sort_vectorized(sep, right, depth_limit, useAvx512);
        }

        private static long* vectorized_partition(long* left, long* right, bool useAvx512)
        {
            long pivot = *right;
            long* readLeft = left;
            long* readRight = right - 1;
            Vector256<long> pivot256 = useAvx512 ? default : Vector256.Create(pivot);
            Vector512<long> pivot512 = useAvx512 ? Vector512.Create(pivot) : default;

            while (true)
            {
                while (readLeft <= readRight)
                {
                    nint remaining = (nint)(readRight - readLeft + 1);
                    if (useAvx512 && remaining >= Avx512N)
                    {
                        Vector512<long> data = Avx512F.LoadVector512(readLeft);
                        ulong mask = Avx512F.CompareGreaterThan(data, pivot512).ExtractMostSignificantBits();
                        if (mask == 0)
                        {
                            readLeft += Avx512N;
                            continue;
                        }

                        readLeft += BitOperations.TrailingZeroCount(mask);
                        break;
                    }
                    else if (!useAvx512 && remaining >= Avx2N)
                    {
                        Vector256<long> data = Avx.LoadVector256(readLeft);
                        int mask = Avx.MoveMask(Avx2.CompareGreaterThan(data, pivot256).AsDouble());
                        if (mask == 0)
                        {
                            readLeft += Avx2N;
                            continue;
                        }

                        readLeft += BitOperations.TrailingZeroCount(mask);
                        break;
                    }

                    if (*readLeft > pivot)
                    {
                        break;
                    }

                    readLeft++;
                }

                while (readLeft <= readRight)
                {
                    nint remaining = (nint)(readRight - readLeft + 1);
                    if (useAvx512 && remaining >= Avx512N)
                    {
                        long* vectorStart = readRight - Avx512N + 1;
                        Vector512<long> data = Avx512F.LoadVector512(vectorStart);
                        ulong mask = Avx512F.CompareGreaterThan(data, pivot512).ExtractMostSignificantBits();
                        if (mask == 0xFF)
                        {
                            readRight -= Avx512N;
                            continue;
                        }

                        readRight -= BitOperations.LeadingZeroCount(~mask << 56);
                        break;
                    }
                    else if (!useAvx512 && remaining >= Avx2N)
                    {
                        long* vectorStart = readRight - Avx2N + 1;
                        Vector256<long> data = Avx.LoadVector256(vectorStart);
                        int mask = Avx.MoveMask(Avx2.CompareGreaterThan(data, pivot256).AsDouble());
                        if (mask == 0xF)
                        {
                            readRight -= Avx2N;
                            continue;
                        }

                        readRight -= BitOperations.LeadingZeroCount((uint)(~mask << 28));
                        break;
                    }

                    if (*readRight <= pivot)
                    {
                        break;
                    }

                    readRight--;
                }

                if (readLeft >= readRight)
                {
                    break;
                }

                swap(readLeft, readRight);
                readLeft++;
                readRight--;
            }

            if (readLeft == readRight && *readLeft <= pivot)
            {
                readLeft++;
            }

            swap(readLeft, right);
            return readLeft + 1;
        }

        private static int floor_log2_plus_one(nuint n)
        {
            int result = 0;
            while (n >= 1)
            {
                result++;
                n /= 2;
            }

            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void swap(long* left, long* right)
        {
            long tmp = *left;
            *left = *right;
            *right = tmp;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void swap_if_greater(long* left, long* right)
        {
            if (*left <= *right)
            {
                return;
            }

            swap(left, right);
        }

        private static void heap_sort(long* lo, long* hi)
        {
            nuint n = (nuint)(hi - lo + 1);
            for (nuint i = n / 2; i >= 1; i--)
            {
                down_heap(i, n, lo);
            }

            for (nuint i = n; i > 1; i--)
            {
                swap(lo, lo + (nint)i - 1);
                down_heap(1, i - 1, lo);
            }
        }

        private static void down_heap(nuint i, nuint n, long* lo)
        {
            long d = *(lo + (nint)i - 1);
            while (i <= n / 2)
            {
                nuint child = 2 * i;
                if (child < n && *(lo + (nint)child - 1) < *(lo + (nint)child))
                {
                    child++;
                }

                if (!(d < *(lo + (nint)child - 1)))
                {
                    break;
                }

                *(lo + (nint)i - 1) = *(lo + (nint)child - 1);
                i = child;
            }

            *(lo + (nint)i - 1) = d;
        }
    }
}
