// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of softwarewritewatch.h / softwarewritewatch.cpp. Both are entirely
// `#ifdef FEATURE_USE_SOFTWARE_WRITE_WATCH_FOR_GC_HEAP` / `#ifndef DACCESS_COMPILE`: the feature
// is defined for every architecture IlcManagedGC supports (see GCHeapMemory.LogCardSize) and the
// DAC is not part of this port, so there is nothing here to condition on either macro.
//
// The C++ inline function bodies of the header and the four out-of-line definitions of the .cpp
// are translated together, in the header's declaration order, so this file reads as one
// `SoftwareWriteWatch` class the way the C++ class does across its two source files.
//
// GetTableStartByteOffset is declared by softwarewritewatch.h but is not defined anywhere in
// src/coreclr, nor called from anywhere in it -- it is a vestigial declaration with no body to
// translate. Inventing one would not be a translation of anything, so it is intentionally
// omitted here.
//
// memcpy and memset are Buffer.MemoryCopy and a small chunked wrapper over
// Unsafe.InitBlockUnaligned respectively: both are allocation-free CoreLib primitives, unlike
// NativeMemory, which owns memory rather than merely operating on caller-supplied pointers.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection;

internal static unsafe class SoftwareWriteWatch
{
    // Table containing the dirty state. This table is translated to exclude the lowest address it represents, see
    // TranslateTableToExcludeHeapStartAddress.
    internal static byte* g_gc_sw_ww_table;

    // Write watch may be disabled when it is not needed (between GCs for instance). This indicates whether it is enabled.
    internal static bool g_gc_sw_ww_enabled_for_gc_heap;

    // #define WRITE_WATCH_UNIT_SIZE ((size_t)0x1000)
    private const nuint WRITE_WATCH_UNIT_SIZE = 0x1000;

    // The granularity of dirty state in the table is one page. Dirtiness is tracked per byte of the table so that
    // synchronization is not required when changing the dirty state. Shifting-right an address by the following value yields
    // the byte index of the address into the write watch table. For instance,
    // GetTable()[address >> AddressToTableByteIndexShift] is the byte that represents the region of memory for 'address'.
    //
    // The C++ static_assert that this equals WRITE_WATCH_UNIT_SIZE is checked directly by
    // SoftwareWriteWatchTests instead of at a static constructor here, which would be a lazily
    // triggered static this library must not have.
    private const int AddressToTableByteIndexShift = GCInterfaceOffsets.SOFTWARE_WRITE_WATCH_AddressToTableByteIndexShift;

    private static void VerifyCreated()
    {
        Debug.Assert(GetTable() != null);
        Debug.Assert(GetHeapStartAddress() != null);
        Debug.Assert(GetHeapEndAddress() != null);
        Debug.Assert(GetHeapStartAddress() < GetHeapEndAddress());
    }

    private static void VerifyMemoryRegion(void* baseAddress, nuint regionByteSize)
    {
        VerifyMemoryRegion(baseAddress, regionByteSize, GetHeapStartAddress(), GetHeapEndAddress());
    }

    private static void VerifyMemoryRegion(
        void* baseAddress,
        nuint regionByteSize,
        void* heapStartAddress,
        void* heapEndAddress)
    {
        VerifyCreated();
        Debug.Assert(baseAddress != null);
        Debug.Assert(heapStartAddress != null);
        Debug.Assert(heapStartAddress >= GetHeapStartAddress());
        Debug.Assert(heapEndAddress != null);
        Debug.Assert(heapEndAddress <= GetHeapEndAddress());
        Debug.Assert(baseAddress >= heapStartAddress);
        Debug.Assert(baseAddress < heapEndAddress);
        Debug.Assert(regionByteSize != 0);
        Debug.Assert(regionByteSize <= (nuint)heapEndAddress - (nuint)baseAddress);
    }

    public static byte* GetTable()
    {
        return g_gc_sw_ww_table;
    }

    private static byte* GetUntranslatedTable()
    {
        VerifyCreated();
        return GetUntranslatedTable(GetTable(), GetHeapStartAddress());
    }

    private static byte* GetUntranslatedTable(byte* table, void* heapStartAddress)
    {
        Debug.Assert(table != null);
        Debug.Assert(heapStartAddress != null);
        Debug.Assert(heapStartAddress >= GetHeapStartAddress());

        byte* untranslatedTable = table + GetTableByteIndex(heapStartAddress);
        Debug.Assert(GCEnv.ALIGN_DOWN(untranslatedTable, (nuint)sizeof(nuint)) == untranslatedTable);
        return untranslatedTable;
    }

    private static byte* GetUntranslatedTableEnd()
    {
        VerifyCreated();
        return GetUntranslatedTableEnd(GetTable(), GetHeapEndAddress());
    }

    private static byte* GetUntranslatedTableEnd(byte* table, void* heapEndAddress)
    {
        Debug.Assert(table != null);
        Debug.Assert(heapEndAddress != null);
        Debug.Assert(heapEndAddress <= GetHeapEndAddress());

        return GCEnv.ALIGN_UP(&table[GetTableByteIndex((byte*)heapEndAddress - 1) + 1], (nuint)sizeof(nuint));
    }

    public static void InitializeUntranslatedTable(byte* untranslatedTable, void* heapStartAddress)
    {
        Debug.Assert(GetTable() == null);
        SetUntranslatedTable(untranslatedTable, heapStartAddress);
    }

    private static void SetUntranslatedTable(byte* untranslatedTable, void* heapStartAddress)
    {
        Debug.Assert(untranslatedTable != null);
        Debug.Assert(GCEnv.ALIGN_DOWN(untranslatedTable, (nuint)sizeof(nuint)) == untranslatedTable);
        Debug.Assert(heapStartAddress != null);

        g_gc_sw_ww_table = TranslateTableToExcludeHeapStartAddress(untranslatedTable, heapStartAddress);
    }

    public static void SetResizedUntranslatedTable(
        byte* untranslatedTable,
        void* heapStartAddress,
        void* heapEndAddress)
    {
        // The runtime needs to be suspended during this call, and background GC threads need to synchronize calls to ClearDirty()
        // and GetDirty() such that they are not called concurrently with this function

        VerifyCreated();
        Debug.Assert(untranslatedTable != null);
        Debug.Assert(GCEnv.ALIGN_DOWN(untranslatedTable, (nuint)sizeof(nuint)) == untranslatedTable);
        Debug.Assert(heapStartAddress != null);
        Debug.Assert(heapEndAddress != null);
        Debug.Assert(heapStartAddress <= GetHeapStartAddress());
        Debug.Assert(heapEndAddress >= GetHeapEndAddress());
        Debug.Assert(heapStartAddress < GetHeapStartAddress() || heapEndAddress > GetHeapEndAddress());

        byte* oldUntranslatedTable = GetUntranslatedTable();
        void* oldTableHeapStartAddress = GetHeapStartAddress();
        nuint oldTableByteSize = GetTableByteSize(oldTableHeapStartAddress, GetHeapEndAddress());
        SetUntranslatedTable(untranslatedTable, heapStartAddress);

        byte* tableRegionStart = &GetTable()[GetTableByteIndex(oldTableHeapStartAddress)];
        Buffer.MemoryCopy(oldUntranslatedTable, tableRegionStart, (long)oldTableByteSize, (long)oldTableByteSize);
    }

    public static bool IsEnabledForGCHeap()
    {
        return g_gc_sw_ww_enabled_for_gc_heap;
    }

    public static void EnableForGCHeap()
    {
        // The runtime needs to be suspended during this call. This is how it currently guarantees that GC heap writes from other
        // threads between calls to EnableForGCHeap() and DisableForGCHeap() will be tracked.

        VerifyCreated();
        Debug.Assert(!IsEnabledForGCHeap());
        g_gc_sw_ww_enabled_for_gc_heap = true;

        WriteBarrierParameters args = default;
        args.operation = WriteBarrierOp.SwitchToWriteWatch;
        args.write_watch_table = g_gc_sw_ww_table;
        args.is_runtime_suspended = 1;
        GCToEEInterface.StompWriteBarrier(&args);
    }

    public static void DisableForGCHeap()
    {
        // The runtime needs to be suspended during this call. This is how it currently guarantees that GC heap writes from other
        // threads between calls to EnableForGCHeap() and DisableForGCHeap() will be tracked.

        VerifyCreated();
        Debug.Assert(IsEnabledForGCHeap());
        g_gc_sw_ww_enabled_for_gc_heap = false;

        WriteBarrierParameters args = default;
        args.operation = WriteBarrierOp.SwitchToNonWriteWatch;
        args.is_runtime_suspended = 1;
        GCToEEInterface.StompWriteBarrier(&args);
    }

    private static void* GetHeapStartAddress()
    {
        return GCCommon.g_gc_lowest_address;
    }

    private static void* GetHeapEndAddress()
    {
        return GCCommon.g_gc_highest_address;
    }

    public static void StaticClose()
    {
        if (GetTable() == null)
        {
            return;
        }

        g_gc_sw_ww_enabled_for_gc_heap = false;
        g_gc_sw_ww_table = null;
    }

    private static nuint GetTableByteIndex(void* address)
    {
        Debug.Assert(address != null);

        nuint tableByteIndex = (nuint)address >> AddressToTableByteIndexShift;
        Debug.Assert(tableByteIndex != 0);
        return tableByteIndex;
    }

    private static void* GetPageAddress(nuint tableByteIndex)
    {
        Debug.Assert(tableByteIndex != 0);

        void* pageAddress = (void*)(tableByteIndex << AddressToTableByteIndexShift);
        Debug.Assert(pageAddress >= GetHeapStartAddress());
        Debug.Assert(pageAddress < GetHeapEndAddress());
        Debug.Assert(GCEnv.ALIGN_DOWN(pageAddress, WRITE_WATCH_UNIT_SIZE) == pageAddress);
        return pageAddress;
    }

    public static nuint GetTableByteSize(void* heapStartAddress, void* heapEndAddress)
    {
        Debug.Assert(heapStartAddress != null);
        Debug.Assert(heapEndAddress != null);
        Debug.Assert(heapStartAddress < heapEndAddress);

        nuint tableByteSize =
            GetTableByteIndex((byte*)heapEndAddress - 1) - GetTableByteIndex(heapStartAddress) + 1;
        tableByteSize = GCEnv.ALIGN_UP(tableByteSize, (nuint)sizeof(nuint));
        return tableByteSize;
    }

    // GetTableStartByteOffset(size_t byteSizeBeforeTable) is declared by softwarewritewatch.h but has no
    // definition or caller anywhere in src/coreclr; see the file header comment.

    private static byte* TranslateTableToExcludeHeapStartAddress(byte* table, void* heapStartAddress)
    {
        Debug.Assert(table != null);
        Debug.Assert(heapStartAddress != null);

        // Exclude the table byte index corresponding to the heap start address from the table pointer, so that each lookup in the
        // table by address does not have to calculate (address - heapStartAddress)
        return table - GetTableByteIndex(heapStartAddress);
    }

    private static void TranslateToTableRegion(
        void* baseAddress,
        nuint regionByteSize,
        byte** tableBaseAddressRef,
        nuint* tableRegionByteSizeRef)
    {
        VerifyCreated();
        VerifyMemoryRegion(baseAddress, regionByteSize);
        Debug.Assert(tableBaseAddressRef != null);
        Debug.Assert(tableRegionByteSizeRef != null);

        nuint baseAddressTableByteIndex = GetTableByteIndex(baseAddress);
        *tableBaseAddressRef = &GetTable()[baseAddressTableByteIndex];
        *tableRegionByteSizeRef =
            GetTableByteIndex((byte*)baseAddress + (regionByteSize - 1)) - baseAddressTableByteIndex + 1;
    }

    public static void ClearDirty(void* baseAddress, nuint regionByteSize)
    {
        VerifyCreated();
        VerifyMemoryRegion(baseAddress, regionByteSize);

        byte* tableBaseAddress;
        nuint tableRegionByteSize;
        TranslateToTableRegion(baseAddress, regionByteSize, &tableBaseAddress, &tableRegionByteSize);
        MemSet(tableBaseAddress, 0, tableRegionByteSize);
    }

    public static void SetDirty(void* address, nuint writeByteSize)
    {
        VerifyCreated();
        VerifyMemoryRegion(address, writeByteSize);
        Debug.Assert(address != null);
        Debug.Assert(writeByteSize <= (nuint)sizeof(void*));

        nuint tableByteIndex = GetTableByteIndex(address);
        Debug.Assert(GetTableByteIndex((byte*)address + (writeByteSize - 1)) == tableByteIndex);

        byte* tableByteAddress = &GetTable()[tableByteIndex];
        if (*tableByteAddress == 0)
        {
            *tableByteAddress = 0xff;
        }
    }

    public static void SetDirtyRegion(void* baseAddress, nuint regionByteSize)
    {
        VerifyCreated();
        VerifyMemoryRegion(baseAddress, regionByteSize);

        byte* tableBaseAddress;
        nuint tableRegionByteSize;
        TranslateToTableRegion(baseAddress, regionByteSize, &tableBaseAddress, &tableRegionByteSize);
        MemSet(tableBaseAddress, 0xff, tableRegionByteSize);
    }

    private static bool GetDirtyFromBlock(
        byte* block,
        byte* firstPageAddressInBlock,
        nuint startByteIndex,
        nuint endByteIndex,
        void** dirtyPages,
        nuint* dirtyPageIndexRef,
        nuint dirtyPageCount,
        bool clearDirty)
    {
        Debug.Assert(block != null);
        Debug.Assert(GCEnv.ALIGN_DOWN(block, (nuint)sizeof(nuint)) == block);
        Debug.Assert(firstPageAddressInBlock == (byte*)GetPageAddress((nuint)(block - GetTable())));
        Debug.Assert(startByteIndex < endByteIndex);
        Debug.Assert(endByteIndex <= (nuint)sizeof(nuint));
        Debug.Assert(dirtyPages != null);
        Debug.Assert(dirtyPageIndexRef != null);

        ref nuint dirtyPageIndex = ref *dirtyPageIndexRef;
        Debug.Assert(dirtyPageIndex < dirtyPageCount);

        nuint dirtyBytes = *(nuint*)block;
        if (dirtyBytes == 0)
        {
            return true;
        }

        if (startByteIndex != 0)
        {
            int numLowBitsToClear = (int)(startByteIndex * 8);
            dirtyBytes >>= numLowBitsToClear;
            dirtyBytes <<= numLowBitsToClear;
        }
        if (endByteIndex != (nuint)sizeof(nuint))
        {
            int numHighBitsToClear = (int)(((nuint)sizeof(nuint) - endByteIndex) * 8);
            dirtyBytes <<= numHighBitsToClear;
            dirtyBytes >>= numHighBitsToClear;
        }

        while (dirtyBytes != 0)
        {
            uint bitIndex;
            Debug.Assert(sizeof(nuint) <= 8);
            if (sizeof(nuint) == 8)
            {
                GCEnv.BitScanForward64(&bitIndex, (ulong)dirtyBytes);
            }
            else
            {
                GCEnv.BitScanForward(&bitIndex, (uint)dirtyBytes);
            }

            // Each byte is only ever set to 0 or 0xff
            Debug.Assert(bitIndex % 8 == 0);
            nuint byteMask = (nuint)0xff << (int)bitIndex;
            Debug.Assert((dirtyBytes & byteMask) == byteMask);
            dirtyBytes ^= byteMask;

            uint byteIndex = bitIndex / 8;
            if (clearDirty)
            {
                // Clear only the bytes for which pages are recorded as dirty
                block[byteIndex] = 0;
            }

            void* pageAddress = firstPageAddressInBlock + byteIndex * WRITE_WATCH_UNIT_SIZE;
            Debug.Assert(pageAddress >= GetHeapStartAddress());
            Debug.Assert(pageAddress < GetHeapEndAddress());
            Debug.Assert(dirtyPageIndex < dirtyPageCount);
            dirtyPages[dirtyPageIndex] = pageAddress;
            ++dirtyPageIndex;
            if (dirtyPageIndex == dirtyPageCount)
            {
                return false;
            }
        }
        return true;
    }

    public static void GetDirty(
        void* baseAddress,
        nuint regionByteSize,
        void** dirtyPages,
        nuint* dirtyPageCountRef,
        bool clearDirty,
        bool isRuntimeSuspended)
    {
        VerifyCreated();
        VerifyMemoryRegion(baseAddress, regionByteSize);
        Debug.Assert(dirtyPages != null);
        Debug.Assert(dirtyPageCountRef != null);

        nuint dirtyPageCount = *dirtyPageCountRef;
        if (dirtyPageCount == 0)
        {
            return;
        }

        if (!isRuntimeSuspended)
        {
            // When a page is marked as dirty, a memory barrier is not issued after the write most of the time. Issue a memory
            // barrier on all active threads of the process now to make recent changes to dirty state visible to this thread.
            GCEnv.MemoryBarrierProcessWide();
        }

        byte* tableRegionStart;
        nuint tableRegionByteSize;
        TranslateToTableRegion(baseAddress, regionByteSize, &tableRegionStart, &tableRegionByteSize);
        byte* tableRegionEnd = tableRegionStart + tableRegionByteSize;

        byte* blockStart = GCEnv.ALIGN_DOWN(tableRegionStart, (nuint)sizeof(nuint));
        Debug.Assert(blockStart >= GetUntranslatedTable());
        byte* blockEnd = GCEnv.ALIGN_UP(tableRegionEnd, (nuint)sizeof(nuint));
        Debug.Assert(blockEnd <= GetUntranslatedTableEnd());
        byte* fullBlockEnd = GCEnv.ALIGN_DOWN(tableRegionEnd, (nuint)sizeof(nuint));

        nuint dirtyPageIndex = 0;
        byte* currentBlock = blockStart;
        byte* firstPageAddressInCurrentBlock = (byte*)GetPageAddress((nuint)(currentBlock - GetTable()));

        do
        {
            if (blockStart == fullBlockEnd)
            {
                if (GetDirtyFromBlock(
                        currentBlock,
                        firstPageAddressInCurrentBlock,
                        (nuint)(tableRegionStart - blockStart),
                        (nuint)(tableRegionEnd - fullBlockEnd),
                        dirtyPages,
                        &dirtyPageIndex,
                        dirtyPageCount,
                        clearDirty))
                {
                    *dirtyPageCountRef = dirtyPageIndex;
                }
                break;
            }

            if (tableRegionStart != blockStart)
            {
                if (!GetDirtyFromBlock(
                        currentBlock,
                        firstPageAddressInCurrentBlock,
                        (nuint)(tableRegionStart - blockStart),
                        (nuint)sizeof(nuint),
                        dirtyPages,
                        &dirtyPageIndex,
                        dirtyPageCount,
                        clearDirty))
                {
                    break;
                }
                currentBlock += sizeof(nuint);
                firstPageAddressInCurrentBlock += (nuint)sizeof(nuint) * WRITE_WATCH_UNIT_SIZE;
            }

            while (currentBlock < fullBlockEnd)
            {
                if (!GetDirtyFromBlock(
                        currentBlock,
                        firstPageAddressInCurrentBlock,
                        0,
                        (nuint)sizeof(nuint),
                        dirtyPages,
                        &dirtyPageIndex,
                        dirtyPageCount,
                        clearDirty))
                {
                    break;
                }
                currentBlock += sizeof(nuint);
                firstPageAddressInCurrentBlock += (nuint)sizeof(nuint) * WRITE_WATCH_UNIT_SIZE;
            }
            if (currentBlock < fullBlockEnd)
            {
                break;
            }

            if (tableRegionEnd != fullBlockEnd &&
                !GetDirtyFromBlock(
                    currentBlock,
                    firstPageAddressInCurrentBlock,
                    0,
                    (nuint)(tableRegionEnd - fullBlockEnd),
                    dirtyPages,
                    &dirtyPageIndex,
                    dirtyPageCount,
                    clearDirty))
            {
                break;
            }

            *dirtyPageCountRef = dirtyPageIndex;
        } while (false);

        if (!isRuntimeSuspended && clearDirty && dirtyPageIndex != 0)
        {
            // When dirtying a page, the dirty state of the page is first checked to see if the page is already dirty. If already
            // dirty, the write to mark it as dirty is skipped. So, when the dirty state of a page is cleared, we need to make sure
            // the cleared state is visible to other threads that may dirty the page, before marking through objects in the page, so
            // that the GC will not miss marking through dirtied objects in the page. Issue a memory barrier on all active threads
            // of the process now.
            GCEnv.MemoryBarrier(); // flush writes from this thread first to guarantee ordering
            GCEnv.MemoryBarrierProcessWide();
        }
    }

    /// <summary>
    /// Fills <paramref name="byteCount"/> bytes at <paramref name="destination"/> with
    /// <paramref name="value"/>, without allocating. This is the <c>memset</c> that ClearDirty
    /// and SetDirtyRegion call in the C++; it is chunked because
    /// <see cref="Unsafe.InitBlockUnaligned(void*, byte, uint)"/> takes a 32 bit count where the
    /// table region size is a <c>size_t</c>.
    /// </summary>
    private static void MemSet(byte* destination, byte value, nuint byteCount)
    {
        while (byteCount > uint.MaxValue)
        {
            Unsafe.InitBlockUnaligned(destination, value, uint.MaxValue);
            destination += uint.MaxValue;
            byteCount -= uint.MaxValue;
        }

        Unsafe.InitBlockUnaligned(destination, value, (uint)byteCount);
    }
}
