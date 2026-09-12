// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GC
{
    internal unsafe struct ObjHeader
    {
        public uint m_uAlignpad;
        public uint m_uSyncBlockValue;

        public uint GetBits()
        {
            return m_uSyncBlockValue;
        }

        public void SetBit(uint bit)
        {
            Interlocked.Or(ref m_uSyncBlockValue, bit);
        }

        public void ClrBit(uint bit)
        {
            Interlocked.And(ref m_uSyncBlockValue, ~bit);
        }

        public void SetGCBit()
        {
            m_uSyncBlockValue |= 0x20000000;
        }

        public void ClrGCBit()
        {
            m_uSyncBlockValue &= ~0x20000000u;
        }
    }

    internal unsafe struct ArrayBase
    {
        public MethodTable* m_pMethTab;
        public uint m_dwLength;

        public uint GetNumComponents()
        {
            return m_dwLength;
        }

        public static nuint GetOffsetOfNumComponents()
        {
            return (nuint)sizeof(void*);
        }
    }

    internal unsafe struct GCEnvironment
    {
        public const int MAX_LONGPATH = 1024;
        public const int WAIT_OBJECT_0 = 0;
        public const int WAIT_TIMEOUT = 258;
        public const uint WAIT_FAILED = 0xFFFFFFFF;
        public const uint INFINITE = 0xFFFFFFFF;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool BitScanForward(uint* bitIndex, uint mask)
        {
            *bitIndex = mask == 0 ? uint.MaxValue : uint.TrailingZeroCount(mask);
            return mask != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool BitScanForward64(uint* bitIndex, ulong mask)
        {
            *bitIndex = mask == 0 ? uint.MaxValue : (uint)ulong.TrailingZeroCount(mask);
            return mask != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool BitScanReverse(uint* bitIndex, uint mask)
        {
            *bitIndex = mask == 0 ? uint.MaxValue : 31u - uint.LeadingZeroCount(mask);
            return mask != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool BitScanReverse64(uint* bitIndex, ulong mask)
        {
            *bitIndex = mask == 0 ? uint.MaxValue : 63u - (uint)ulong.LeadingZeroCount(mask);
            return mask != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint AlignUp(nuint value, nuint alignment)
        {
            return (value + (alignment - 1)) & ~(alignment - 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint AlignDown(nuint value, nuint alignment)
        {
            return value & ~(alignment - 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte* AlignUp(byte* pointer, nuint alignment)
        {
            return (byte*)AlignUp((nuint)pointer, alignment);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte* AlignDown(byte* pointer, nuint alignment)
        {
            return (byte*)AlignDown((nuint)pointer, alignment);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* AlignDown(void* pointer, nuint alignment)
        {
            return (void*)AlignDown((nuint)pointer, alignment);
        }

        public static bool FitsInU1(ulong value)
        {
            return value == (byte)value;
        }
    }
}
