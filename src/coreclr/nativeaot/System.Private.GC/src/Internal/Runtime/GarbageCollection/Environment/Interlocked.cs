// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of gcenv.interlocked.h and gcenv.interlocked.inl. The C++ class is a set of templates
// over T; C# cannot express one atomic operation over an open unmanaged T, so each operation is
// spelled out for the widths the GC uses it at, keeping the C++ method names and their
// pointer-taking shape. Every method also has a `ref` overload, because managed call sites
// commonly hold a variable rather than a pointer and taking the address of a static field or an
// array element would require pinning it first.
//
// InterlockedOperationBarrier has no counterpart here. It exists because the __sync/__atomic
// builtins the C++ code uses are not full barriers on arm64, loongarch64 and riscv64. The
// System.Threading.Interlocked operations these forward to are specified as full barriers on
// every architecture, so the extra fence is already part of them.
//
// The unsigned and pointer-sized forms go through the same-width signed operation. That is a
// pure reinterpretation of the bits, which is what the C++ code does too when it casts to
// `long*` for the MSVC intrinsics.

using System;
using System.Runtime.CompilerServices;

using SysInterlocked = System.Threading.Interlocked;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>
    /// Interlocked operations, as <c>gcenv.interlocked.h</c> declares them.
    /// </summary>
    internal static unsafe class Interlocked
    {
        //
        // Increment the value of the specified variable as an atomic operation.
        // Returns the resulting incremented value.
        //

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Increment(ref int addend) => SysInterlocked.Increment(ref addend);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Increment(int* addend) => Increment(ref *addend);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Increment(ref uint addend) => (uint)SysInterlocked.Increment(ref Unsafe.As<uint, int>(ref addend));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Increment(uint* addend) => Increment(ref *addend);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Increment(ref long addend) => SysInterlocked.Increment(ref addend);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Increment(long* addend) => Increment(ref *addend);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Increment(ref ulong addend) => (ulong)SysInterlocked.Increment(ref Unsafe.As<ulong, long>(ref addend));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Increment(ulong* addend) => Increment(ref *addend);

        //
        // Decrement the value of the specified variable as an atomic operation.
        // Returns the resulting decremented value.
        //

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Decrement(ref int addend) => SysInterlocked.Decrement(ref addend);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Decrement(int* addend) => Decrement(ref *addend);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Decrement(ref uint addend) => (uint)SysInterlocked.Decrement(ref Unsafe.As<uint, int>(ref addend));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Decrement(uint* addend) => Decrement(ref *addend);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Decrement(ref long addend) => SysInterlocked.Decrement(ref addend);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Decrement(long* addend) => Decrement(ref *addend);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Decrement(ref ulong addend) => (ulong)SysInterlocked.Decrement(ref Unsafe.As<ulong, long>(ref addend));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Decrement(ulong* addend) => Decrement(ref *addend);

        //
        // Perform an atomic AND operation on the specified values.
        //

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void And(ref int destination, int value) => SysInterlocked.And(ref destination, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void And(int* destination, int value) => And(ref *destination, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void And(ref uint destination, uint value) => SysInterlocked.And(ref Unsafe.As<uint, int>(ref destination), (int)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void And(uint* destination, uint value) => And(ref *destination, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void And(ref long destination, long value) => SysInterlocked.And(ref destination, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void And(long* destination, long value) => And(ref *destination, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void And(ref ulong destination, ulong value) => SysInterlocked.And(ref Unsafe.As<ulong, long>(ref destination), (long)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void And(ulong* destination, ulong value) => And(ref *destination, value);

        //
        // Perform an atomic OR operation on the specified values.
        //

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Or(ref int destination, int value) => SysInterlocked.Or(ref destination, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Or(int* destination, int value) => Or(ref *destination, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Or(ref uint destination, uint value) => SysInterlocked.Or(ref Unsafe.As<uint, int>(ref destination), (int)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Or(uint* destination, uint value) => Or(ref *destination, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Or(ref long destination, long value) => SysInterlocked.Or(ref destination, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Or(long* destination, long value) => Or(ref *destination, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Or(ref ulong destination, ulong value) => SysInterlocked.Or(ref Unsafe.As<ulong, long>(ref destination), (long)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Or(ulong* destination, ulong value) => Or(ref *destination, value);

        //
        // Set a variable to the specified value as an atomic operation.
        // Returns the previous value of the destination.
        //

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Exchange(ref int destination, int value) => SysInterlocked.Exchange(ref destination, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Exchange(int* destination, int value) => Exchange(ref *destination, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Exchange(ref uint destination, uint value) => (uint)SysInterlocked.Exchange(ref Unsafe.As<uint, int>(ref destination), (int)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Exchange(uint* destination, uint value) => Exchange(ref *destination, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Exchange(ref long destination, long value) => SysInterlocked.Exchange(ref destination, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Exchange(long* destination, long value) => Exchange(ref *destination, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Exchange(ref ulong destination, ulong value) => (ulong)SysInterlocked.Exchange(ref Unsafe.As<ulong, long>(ref destination), (long)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Exchange(ulong* destination, ulong value) => Exchange(ref *destination, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nint Exchange(ref nint destination, nint value) => SysInterlocked.Exchange(ref destination, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nint Exchange(nint* destination, nint value) => Exchange(ref *destination, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint Exchange(ref nuint destination, nuint value) => (nuint)SysInterlocked.Exchange(ref Unsafe.As<nuint, nint>(ref destination), (nint)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint Exchange(nuint* destination, nuint value) => Exchange(ref *destination, value);

        //
        // Set a pointer variable to the specified value as an atomic operation.
        // Returns the previous value of the destination.
        //

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* ExchangePointer(void** destination, void* value) =>
            (void*)SysInterlocked.Exchange(ref *(nint*)destination, (nint)value);

        //
        // Perform an atomic addition and return the original value of the addend.
        //
        // System.Threading.Interlocked.Add returns the sum rather than the original value, so
        // the added amount is subtracted back off. That is exact in unchecked two's complement
        // arithmetic, including when the addition wrapped.
        //

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ExchangeAdd(ref int addend, int value) => unchecked(SysInterlocked.Add(ref addend, value) - value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ExchangeAdd(int* addend, int value) => ExchangeAdd(ref *addend, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ExchangeAdd(ref uint addend, uint value) => (uint)ExchangeAdd(ref Unsafe.As<uint, int>(ref addend), (int)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ExchangeAdd(uint* addend, uint value) => ExchangeAdd(ref *addend, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ExchangeAdd64(ref long addend, long value) => unchecked(SysInterlocked.Add(ref addend, value) - value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ExchangeAdd64(long* addend, long value) => ExchangeAdd64(ref *addend, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ExchangeAdd64(ref ulong addend, ulong value) => (ulong)ExchangeAdd64(ref Unsafe.As<ulong, long>(ref addend), (long)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ExchangeAdd64(ulong* addend, ulong value) => ExchangeAdd64(ref *addend, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nint ExchangeAddPtr(ref nint addend, nint value) =>
            IntPtr.Size == sizeof(long)
                ? (nint)ExchangeAdd64(ref Unsafe.As<nint, long>(ref addend), value)
                : ExchangeAdd(ref Unsafe.As<nint, int>(ref addend), (int)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nint ExchangeAddPtr(nint* addend, nint value) => ExchangeAddPtr(ref *addend, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint ExchangeAddPtr(ref nuint addend, nuint value) => (nuint)ExchangeAddPtr(ref Unsafe.As<nuint, nint>(ref addend), (nint)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint ExchangeAddPtr(nuint* addend, nuint value) => ExchangeAddPtr(ref *addend, value);

        //
        // Perform an atomic compare-and-exchange. The destination is set only if it is equal to
        // the comparand. Returns the original value of the destination.
        //

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CompareExchange(ref int destination, int exchange, int comparand) =>
            SysInterlocked.CompareExchange(ref destination, exchange, comparand);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CompareExchange(int* destination, int exchange, int comparand) =>
            CompareExchange(ref *destination, exchange, comparand);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint CompareExchange(ref uint destination, uint exchange, uint comparand) =>
            (uint)SysInterlocked.CompareExchange(ref Unsafe.As<uint, int>(ref destination), (int)exchange, (int)comparand);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint CompareExchange(uint* destination, uint exchange, uint comparand) =>
            CompareExchange(ref *destination, exchange, comparand);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long CompareExchange(ref long destination, long exchange, long comparand) =>
            SysInterlocked.CompareExchange(ref destination, exchange, comparand);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long CompareExchange(long* destination, long exchange, long comparand) =>
            CompareExchange(ref *destination, exchange, comparand);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong CompareExchange(ref ulong destination, ulong exchange, ulong comparand) =>
            (ulong)SysInterlocked.CompareExchange(ref Unsafe.As<ulong, long>(ref destination), (long)exchange, (long)comparand);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong CompareExchange(ulong* destination, ulong exchange, ulong comparand) =>
            CompareExchange(ref *destination, exchange, comparand);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nint CompareExchange(ref nint destination, nint exchange, nint comparand) =>
            SysInterlocked.CompareExchange(ref destination, exchange, comparand);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nint CompareExchange(nint* destination, nint exchange, nint comparand) =>
            CompareExchange(ref *destination, exchange, comparand);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint CompareExchange(ref nuint destination, nuint exchange, nuint comparand) =>
            (nuint)SysInterlocked.CompareExchange(ref Unsafe.As<nuint, nint>(ref destination), (nint)exchange, (nint)comparand);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint CompareExchange(nuint* destination, nuint exchange, nuint comparand) =>
            CompareExchange(ref *destination, exchange, comparand);

        //
        // Perform an atomic compare-and-exchange on the specified pointers.
        //

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* CompareExchangePointer(void** destination, void* exchange, void* comparand) =>
            (void*)SysInterlocked.CompareExchange(ref *(nint*)destination, (nint)exchange, (nint)comparand);
    }
}
