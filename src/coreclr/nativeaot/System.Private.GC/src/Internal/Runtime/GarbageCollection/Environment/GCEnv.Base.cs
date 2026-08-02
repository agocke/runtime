// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of gcenv.base.h: the constants, HRESULT helpers, alignment helpers and bit-scan wrappers
// that the GC gets from its environment header. The C++ file spells most of these as macros or
// as free functions; C# has neither at namespace scope, so they are static members of GCEnv.
// Ported code is expected to say `using static Internal.Runtime.GarbageCollection.GCEnv;` so
// that the call sites read the same way as the C++ ones.
//
// The parts of gcenv.base.h that have no managed counterpart are the Win32 type aliases, the
// contract macros, the DPTR/SPTR data-access macros and the printf shims. The ETW::GC_ROOT_KIND
// enum is left for the diagnostics stage, which is what consumes it.

using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>
    /// The free functions, macros and constants of the <c>gcenv</c> headers.
    /// </summary>
    internal static unsafe partial class GCEnv
    {
        private const string RuntimeLibrary = "*";

        /// <summary>
        /// The runtime's spin helper, used by <see cref="YieldProcessor"/> on architectures
        /// with no pause intrinsic. It is a <c>[RuntimeImport]</c>, so calling it neither
        /// marshals nor changes GC mode.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "RhSpinWait")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void RhSpinWait(int iterations);

        public const int S_OK = 0;
        public const int NOERROR = 0;
        public const int E_FAIL = unchecked((int)0x80004005);
        public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
        public const int ERROR_TIMEOUT = 1460;

        public const int COR_E_EXECUTIONENGINE = unchecked((int)0x80131506);
        public const int CLR_E_GC_BAD_AFFINITY_CONFIG = unchecked((int)0x8013200A);
        public const int CLR_E_GC_BAD_AFFINITY_CONFIG_FORMAT = unchecked((int)0x8013200B);
        public const int CLR_E_GC_BAD_HARD_LIMIT = unchecked((int)0x8013200D);
        public const int CLR_E_GC_LARGE_PAGE_MISSING_HARD_LIMIT = unchecked((int)0x8013200E);
        public const int CLR_E_GC_BAD_REGION_SIZE = unchecked((int)0x8013200F);

        public const uint INFINITE = 0xFFFFFFFF;
        public const uint WAIT_OBJECT_0 = 0;
        public const uint WAIT_TIMEOUT = 258;
        public const uint WAIT_FAILED = 0xFFFFFFFF;

        public const int MAX_LONGPATH = 1024;

        /// <summary><c>SIZE_T_MAX</c>.</summary>
        public static nuint SIZE_T_MAX => nuint.MaxValue;

        /// <summary><c>SSIZE_T_MAX</c>.</summary>
        public static nint SSIZE_T_MAX => (nint)(nuint.MaxValue / 2);

        /// <summary><c>DATA_ALIGNMENT</c>.</summary>
        public static int DATA_ALIGNMENT => sizeof(nuint);

        public static bool SUCCEEDED(int hr) => hr >= 0;

        public static bool FAILED(int hr) => hr < 0;

        public static int HRESULT_FROM_WIN32(uint x) =>
            (int)x <= 0 ? (int)x : unchecked((int)((x & 0x0000FFFF) | (7 << 16) | 0x80000000));

        /// <summary>
        /// Aligns a <c>size_t</c> to the specified alignment. Alignment must be a power of two.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint ALIGN_UP(nuint val, nuint alignment)
        {
            // alignment factor must be power of two
            Debug.Assert((alignment & (alignment - 1)) == 0);
            nuint result = (val + (alignment - 1)) & ~(alignment - 1);
            Debug.Assert(result >= val);
            return result;
        }

        /// <summary>
        /// Aligns a pointer to the specified alignment. Alignment must be a power of two.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte* ALIGN_UP(byte* ptr, nuint alignment)
        {
            nuint as_size_t = (nuint)ptr;
            return (byte*)ALIGN_UP(as_size_t, alignment);
        }

        /// <summary>
        /// Aligns a <c>size_t</c> to the specified alignment by rounding down. Alignment must be
        /// a power of two.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint ALIGN_DOWN(nuint val, nuint alignment)
        {
            // alignment factor must be power of two.
            Debug.Assert((alignment & (alignment - 1)) == 0);
            nuint result = val & ~(alignment - 1);
            return result;
        }

        /// <summary>
        /// Aligns a pointer to the specified alignment by rounding down. Alignment must be a
        /// power of two.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte* ALIGN_DOWN(byte* ptr, nuint alignment)
        {
            nuint as_size_t = (nuint)ptr;
            return (byte*)ALIGN_DOWN(as_size_t, alignment);
        }

        /// <summary>
        /// Aligns a void pointer to the specified alignment by rounding down. Alignment must be
        /// a power of two.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* ALIGN_DOWN(void* ptr, nuint alignment)
        {
            nuint as_size_t = (nuint)ptr;
            return (void*)ALIGN_DOWN(as_size_t, alignment);
        }

        /// <summary>
        /// Cross-platform wrapper for the <c>_BitScanForward</c> compiler intrinsic. A value is
        /// unconditionally stored through the <paramref name="bitIndex"/> argument, but callers
        /// should only rely on it when the function returns non-zero; otherwise, the stored value
        /// is undefined and varies by implementation and hardware platform.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte BitScanForward(uint* bitIndex, uint mask)
        {
            *bitIndex = (uint)BitOperations.TrailingZeroCount(mask);
            // Both GCC and Clang generate better, smaller code if we check whether the
            // mask was/is zero rather than the equivalent check that iIndex is zero.
            return mask != 0 ? (byte)1 : (byte)0;
        }

        /// <summary>
        /// Cross-platform wrapper for the <c>_BitScanForward64</c> compiler intrinsic. See
        /// <see cref="BitScanForward"/> for the meaning of the stored index on failure.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte BitScanForward64(uint* bitIndex, ulong mask)
        {
            *bitIndex = (uint)BitOperations.TrailingZeroCount(mask);
            return mask != 0 ? (byte)1 : (byte)0;
        }

        /// <summary>
        /// Cross-platform wrapper for the <c>_BitScanReverse</c> compiler intrinsic.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte BitScanReverse(uint* bitIndex, uint mask)
        {
            // The result of __builtin_clz is undefined when mask is zero, but it's still OK to
            // call the intrinsic in that case (just don't use the output).
            int lzcount = BitOperations.LeadingZeroCount(mask);
            *bitIndex = (uint)(31 - lzcount);
            return mask != 0 ? (byte)1 : (byte)0;
        }

        /// <summary>
        /// Cross-platform wrapper for the <c>_BitScanReverse64</c> compiler intrinsic.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte BitScanReverse64(uint* bitIndex, ulong mask)
        {
            int lzcount = BitOperations.LeadingZeroCount(mask);
            *bitIndex = (uint)(63 - lzcount);
            return mask != 0 ? (byte)1 : (byte)0;
        }

        public static bool FitsInU1(ulong val) => val == (ulong)(byte)val;

        /// <summary>
        /// Hints to the processor that the current thread is in a spin-wait loop.
        /// </summary>
        /// <remarks>
        /// The C++ macro expands to a single <c>pause</c>/<c>yield</c>/<c>dbar</c> instruction.
        /// The two branches below are folded away at compile time by ILC, so the common
        /// architectures get exactly that instruction. Architectures with no intrinsic fall back
        /// to the runtime's spin helper, which is what the managed side has available.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void YieldProcessor()
        {
            if (X86Base.IsSupported)
            {
                X86Base.Pause();
            }
            else if (ArmBase.IsSupported)
            {
                ArmBase.Yield();
            }
            else
            {
                RhSpinWait(1);
            }
        }

        /// <summary>
        /// Full memory barrier, as the C++ <c>MemoryBarrier</c> macro.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void MemoryBarrier() => System.Threading.Interlocked.MemoryBarrier();

        /// <summary>
        /// Parse an integer index or range of two indices separated by '-'. Updates the
        /// <paramref name="config_string"/> to point to the first character after the parsed
        /// part.
        /// </summary>
        /// <remarks>
        /// Declared by <c>gcenv.os.h</c> and defined by <c>gcconfig.cpp</c>. The C++ version
        /// calls <c>strtoul</c>, which <see cref="StrToUInt"/> reproduces.
        /// </remarks>
        public static bool ParseIndexOrRange(byte** config_string, nuint* start_index, nuint* end_index)
        {
            byte* number_end;
            nuint start = StrToUInt(*config_string, &number_end);

            if (number_end == *config_string)
            {
                // No number found, invalid format
                return false;
            }

            nuint end = start;

            if (*number_end == (byte)'-')
            {
                byte* range_end_start = number_end + 1;
                end = StrToUInt(range_end_start, &number_end);
                if (number_end == range_end_start)
                {
                    // No number found, invalid format
                    return false;
                }
            }

            *start_index = start;
            *end_index = end;

            *config_string = number_end;

            return true;
        }

        /// <summary>
        /// The <c>strtoul(s, end, 10)</c> that <see cref="ParseIndexOrRange"/> uses: optional
        /// leading whitespace, an optional sign, then decimal digits, with
        /// <paramref name="end"/> left at the first character that is not part of the number and
        /// at <paramref name="s"/> itself when there is no number at all.
        /// </summary>
        /// <remarks>
        /// On overflow <c>strtoul</c> saturates at <c>ULONG_MAX</c>, which is
        /// <see cref="SIZE_T_MAX"/> on Unix but only 32 bits wide on Windows. This saturates at
        /// <see cref="SIZE_T_MAX"/> on both. The only thing the caller does with the value is
        /// compare it against <c>GetMaxProcessorCount()</c>, which either saturation point is far
        /// above, so the two agree on every input.
        /// </remarks>
        private static nuint StrToUInt(byte* s, byte** end)
        {
            byte* current = s;

            while (*current == (byte)' ' || (*current >= 0x09 && *current <= 0x0D))
            {
                current++;
            }

            bool negative = false;
            if (*current == (byte)'+' || *current == (byte)'-')
            {
                negative = *current == (byte)'-';
                current++;
            }

            byte* digits = current;
            nuint value = 0;
            bool saturated = false;

            while (*current >= (byte)'0' && *current <= (byte)'9')
            {
                nuint digit = (nuint)(*current - (byte)'0');
                if (value > (nuint.MaxValue - digit) / 10)
                {
                    saturated = true;
                }
                else
                {
                    value = (value * 10) + digit;
                }

                current++;
            }

            if (current == digits)
            {
                // No conversion was performed; strtoul stores the original pointer.
                *end = s;
                return 0;
            }

            *end = current;

            // On overflow strtoul returns ULONG_MAX whatever the sign was, so the saturation
            // has to win over the negation rather than be negated by it.
            if (saturated)
            {
                return nuint.MaxValue;
            }

            return negative ? unchecked(0 - value) : value;
        }
    }
}
