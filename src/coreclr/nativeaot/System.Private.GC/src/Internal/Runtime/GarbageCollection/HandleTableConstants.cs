// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from src/coreclr/gc/handletableconstants.h.

namespace Internal.Runtime.GarbageCollection
{
    internal static class HandleTableConstants
    {
        public const int INITIAL_HANDLE_TABLE_ARRAY_SIZE = 10;
        public const int HANDLE_MAX_INTERNAL_TYPES = 13;
        public const int BITS_PER_BYTE = 8;

        public const int HANDLE_SEGMENT_SIZE = 0x10000;
        public const int HANDLE_HEADER_SIZE = 0x1000;
        public const int HANDLE_SEGMENT_ALIGNMENT = HANDLE_SEGMENT_SIZE;

        public const uint GEN_CLUMP_0_MASK = 0x000000FF;

        public const int HANDLE_HANDLES_PER_CLUMP = 16;
        public const int HANDLE_HANDLES_PER_BLOCK = 64;
        public const int HANDLE_MAX_PUBLIC_TYPES = HANDLE_MAX_INTERNAL_TYPES - 1;
        public const int HNDTYPE_INTERNAL_DATABLOCK = HANDLE_MAX_INTERNAL_TYPES - 1;
        public const int MAXSTATGEN = 5;

        public const int HANDLE_SEGMENT_CONTENT_MASK = HANDLE_SEGMENT_SIZE - 1;
        public const ulong HANDLE_SEGMENT_ALIGN_MASK = ~(ulong)HANDLE_SEGMENT_CONTENT_MASK;

#if TARGET_64BIT
        public const int HANDLE_SIZE = sizeof(ulong);
#else
        public const int HANDLE_SIZE = sizeof(uint);
#endif
        public const int HANDLE_HANDLES_PER_SEGMENT = (HANDLE_SEGMENT_SIZE - HANDLE_HEADER_SIZE) / HANDLE_SIZE;
        public const int HANDLE_BLOCKS_PER_SEGMENT = HANDLE_HANDLES_PER_SEGMENT / HANDLE_HANDLES_PER_BLOCK;
        public const int HANDLE_CLUMPS_PER_SEGMENT = HANDLE_HANDLES_PER_SEGMENT / HANDLE_HANDLES_PER_CLUMP;
        public const int HANDLE_CLUMPS_PER_BLOCK = HANDLE_HANDLES_PER_BLOCK / HANDLE_HANDLES_PER_CLUMP;
        public const int HANDLE_BYTES_PER_BLOCK = HANDLE_HANDLES_PER_BLOCK * HANDLE_SIZE;
        public const int HANDLE_HANDLES_PER_MASK = sizeof(uint) * BITS_PER_BYTE;
        public const int HANDLE_MASKS_PER_SEGMENT = HANDLE_HANDLES_PER_SEGMENT / HANDLE_HANDLES_PER_MASK;
        public const int HANDLE_MASKS_PER_BLOCK = HANDLE_HANDLES_PER_BLOCK / HANDLE_HANDLES_PER_MASK;
        public const int HANDLE_CLUMPS_PER_MASK = HANDLE_HANDLES_PER_MASK / HANDLE_HANDLES_PER_CLUMP;

        // C# fixed-buffer lengths must be compile-time constants; the native/C# shared offsets
        // table independently verifies this value against sizeof(_TableSegmentHeader).
#if TARGET_64BIT
        public const int TABLE_SEGMENT_HEADER_SIZE = 2020;
#else
        public const int TABLE_SEGMENT_HEADER_SIZE = 3932;
#endif

        public const int HANDLE_CACHE_TYPE_SIZE = 128;
        public const int HANDLES_PER_CACHE_BANK = (HANDLE_CACHE_TYPE_SIZE / 2) - 1;
        public const int REBALANCE_TOLERANCE = HANDLES_PER_CACHE_BANK / 3;
        public const int REBALANCE_LOWATER_MARK = HANDLES_PER_CACHE_BANK - REBALANCE_TOLERANCE;
        public const int REBALANCE_HIWATER_MARK = HANDLES_PER_CACHE_BANK + REBALANCE_TOLERANCE;
        public const int SMALL_ALLOC_COUNT = HANDLES_PER_CACHE_BANK / 10;

        public const uint MASK_FULL = 0;
        public const uint MASK_EMPTY = 0xFFFFFFFF;
        public const uint MASK_LOBYTE = 0x000000FF;
        public const byte TYPE_INVALID = 0xFF;
        public const byte BLOCK_INVALID = 0xFF;

        public static uint NEXT_CLUMP_IN_MASK(uint dw) => dw >> BITS_PER_BYTE;
    }
}
