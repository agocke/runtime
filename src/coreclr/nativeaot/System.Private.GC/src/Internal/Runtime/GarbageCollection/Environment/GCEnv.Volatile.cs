// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the free functions of volatile.h. The C++ file defines them as templates over T; C#
// has no way to express "any unmanaged T" and still emit a single load or store, so each is
// spelled out for the types the GC instantiates it with.
//
// VolatileLoad/VolatileStore in C++ mean "the compiler may neither remove nor reorder this
// access relative to other memory accesses", which is acquire/release on the architectures the
// runtime supports; System.Threading.Volatile.Read/Write is exactly that.
//
// The WithoutBarrier variants are weaker in C++: not removable, but free to be reordered. C#
// has no primitive with that meaning -- a plain pointer dereference is removable and hoistable
// out of a spin loop, which is precisely what those call sites cannot tolerate. They therefore
// use the same acquire/release access as the barriered form, which is strictly stronger and so
// still correct; the only cost is an ldar/stlr instead of a plain load or store on arm64.
//
// The Volatile<T> and VolatilePtr<T> field wrappers are not ported here: they exist to give a
// field volatile semantics, and the types that have such fields are the gcpriv.h ones that
// arrive with the core GC data structures.

using System.Runtime.CompilerServices;

using SysVolatile = System.Threading.Volatile;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe partial class GCEnv
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte VolatileLoad(byte* pt) => SysVolatile.Read(ref *pt);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort VolatileLoad(ushort* pt) => SysVolatile.Read(ref *pt);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int VolatileLoad(int* pt) => SysVolatile.Read(ref *pt);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint VolatileLoad(uint* pt) => SysVolatile.Read(ref *pt);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long VolatileLoad(long* pt) => SysVolatile.Read(ref *pt);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong VolatileLoad(ulong* pt) => SysVolatile.Read(ref *pt);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nint VolatileLoad(nint* pt) => SysVolatile.Read(ref *pt);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint VolatileLoad(nuint* pt) => SysVolatile.Read(ref *pt);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* VolatileLoad(void** pt) => (void*)SysVolatile.Read(ref *(nint*)pt);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte VolatileLoadWithoutBarrier(byte* pt) => SysVolatile.Read(ref *pt);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort VolatileLoadWithoutBarrier(ushort* pt) => SysVolatile.Read(ref *pt);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int VolatileLoadWithoutBarrier(int* pt) => SysVolatile.Read(ref *pt);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint VolatileLoadWithoutBarrier(uint* pt) => SysVolatile.Read(ref *pt);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long VolatileLoadWithoutBarrier(long* pt) => SysVolatile.Read(ref *pt);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong VolatileLoadWithoutBarrier(ulong* pt) => SysVolatile.Read(ref *pt);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nint VolatileLoadWithoutBarrier(nint* pt) => SysVolatile.Read(ref *pt);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint VolatileLoadWithoutBarrier(nuint* pt) => SysVolatile.Read(ref *pt);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* VolatileLoadWithoutBarrier(void** pt) => (void*)SysVolatile.Read(ref *(nint*)pt);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void VolatileStore(byte* pt, byte val) => SysVolatile.Write(ref *pt, val);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void VolatileStore(ushort* pt, ushort val) => SysVolatile.Write(ref *pt, val);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void VolatileStore(int* pt, int val) => SysVolatile.Write(ref *pt, val);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void VolatileStore(uint* pt, uint val) => SysVolatile.Write(ref *pt, val);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void VolatileStore(long* pt, long val) => SysVolatile.Write(ref *pt, val);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void VolatileStore(ulong* pt, ulong val) => SysVolatile.Write(ref *pt, val);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void VolatileStore(nint* pt, nint val) => SysVolatile.Write(ref *pt, val);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void VolatileStore(nuint* pt, nuint val) => SysVolatile.Write(ref *pt, val);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void VolatileStore(void** pt, void* val) => SysVolatile.Write(ref *(nint*)pt, (nint)val);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void VolatileStoreWithoutBarrier(byte* pt, byte val) => SysVolatile.Write(ref *pt, val);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void VolatileStoreWithoutBarrier(ushort* pt, ushort val) => SysVolatile.Write(ref *pt, val);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void VolatileStoreWithoutBarrier(int* pt, int val) => SysVolatile.Write(ref *pt, val);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void VolatileStoreWithoutBarrier(uint* pt, uint val) => SysVolatile.Write(ref *pt, val);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void VolatileStoreWithoutBarrier(long* pt, long val) => SysVolatile.Write(ref *pt, val);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void VolatileStoreWithoutBarrier(ulong* pt, ulong val) => SysVolatile.Write(ref *pt, val);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void VolatileStoreWithoutBarrier(nint* pt, nint val) => SysVolatile.Write(ref *pt, val);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void VolatileStoreWithoutBarrier(nuint* pt, nuint val) => SysVolatile.Write(ref *pt, val);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void VolatileStoreWithoutBarrier(void** pt, void* val) => SysVolatile.Write(ref *(nint*)pt, (nint)val);
    }
}
