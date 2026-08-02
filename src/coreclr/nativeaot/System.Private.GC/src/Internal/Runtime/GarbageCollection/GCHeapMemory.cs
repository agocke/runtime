// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>
    /// The memory the managed heap hands out, and the card tables the EE's write barrier needs
    /// in order to write into it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a bump allocator over a single contiguous region and it never reclaims anything:
    /// the port has no marking, planning or sweeping yet (plan steps 9-10). An application runs
    /// until it has allocated <see cref="HeapSize"/> bytes and then gets OOM. That is enough to
    /// answer the question this milestone exists for — whether ILC can compile code that the
    /// runtime is willing to call on the allocation path — without the collector being written.
    /// </para>
    /// <para>
    /// The whole region is committed up front. Lazy commit would need the allocation fast path
    /// to synchronize with a committing thread; committing everything makes allocation a single
    /// interlocked compare-exchange with no lock at all. Committed-but-untouched pages cost
    /// address space and commit charge, not physical memory.
    /// </para>
    /// </remarks>
    internal static unsafe class GCHeapMemory
    {
        /// <summary>
        /// Size of the one and only heap region. Because nothing is ever reclaimed this is also
        /// the total number of bytes the process can allocate.
        /// </summary>
        private const nuint HeapSize = 256 * 1024 * 1024;

        /// <summary>
        /// Bytes covered by one card table byte. Must match <c>LOG2_CLUMP_SIZE</c> in
        /// <c>GCMemoryHelpers.inl</c> and the <c>shr 0x0B</c> in the assembly write barriers.
        /// That value is 11 only on 64-bit; 32-bit uses 10, which is why
        /// <c>Microsoft.NETCore.Native.targets</c> rejects <c>IlcManagedGC</c> on x86, ARM32
        /// and WASM.
        /// </summary>
        private const int LogCardSize = 11;

        /// <summary>
        /// Bytes covered by one card bundle table byte. Must match the additional
        /// <c>shr 0x0A</c> the assembly write barriers apply on top of <see cref="LogCardSize"/>.
        /// </summary>
        private const int LogCardBundleSize = 21;

        // Bump pointer, as nint so that Interlocked can operate on it.
        private static nint s_allocPtr;

        /// <summary>
        /// Low bound of the heap. Backed by <see cref="GCCommon.g_gc_lowest_address"/>, the same
        /// global <c>gccommon.cpp</c> declares and <c>SoftwareWriteWatch::GetHeapStartAddress</c>
        /// reads, so this is the one place the bound is set.
        /// </summary>
        public static byte* HeapStart => GCCommon.g_gc_lowest_address;

        /// <summary>
        /// High bound of the heap. Backed by <see cref="GCCommon.g_gc_highest_address"/>, the
        /// same global <c>gccommon.cpp</c> declares and
        /// <c>SoftwareWriteWatch::GetHeapEndAddress</c> reads.
        /// </summary>
        public static byte* HeapEnd => GCCommon.g_gc_highest_address;

        /// <summary>Bytes handed out so far. Never decreases, because nothing is ever freed.</summary>
        public static nuint BytesInUse => (nuint)(Volatile.Read(ref s_allocPtr) - (nint)GCCommon.g_gc_lowest_address);

        public static bool Contains(void* address) => address >= GCCommon.g_gc_lowest_address && address < GCCommon.g_gc_highest_address;

        /// <summary>
        /// Reserves and commits the heap, builds the card tables, and publishes both to the EE's
        /// write barrier. Returns false if the memory could not be obtained.
        /// </summary>
        public static bool Initialize()
        {
            byte* heap = GCToOSInterface.VirtualReserve(HeapSize, 0, (uint)VirtualReserveFlags.None);
            if (heap == null || !GCToOSInterface.VirtualCommit(heap, HeapSize))
            {
                return false;
            }

            GCCommon.g_gc_lowest_address = heap;
            GCCommon.g_gc_highest_address = heap + HeapSize;
            s_allocPtr = (nint)heap;

            // The write barriers index the card tables by the absolute address of the location
            // being written, not by an offset from the start of the heap, so what is published
            // is the table base biased by the index the low bound of the heap maps to. Freshly
            // committed pages read as zero, which is the "no card set" state the tables need.
            byte* cardTable = CommitTable(HeapSize >> LogCardSize);
            byte* cardBundleTable = CommitTable(HeapSize >> LogCardBundleSize);
            if (cardTable == null || cardBundleTable == null)
            {
                return false;
            }

            WriteBarrierParameters args = default;
            args.operation = WriteBarrierOp.Initialize;
            args.is_runtime_suspended = 1;
            args.card_table = (uint*)(cardTable - (nint)((nuint)heap >> LogCardSize));
            args.card_bundle_table = (uint*)(cardBundleTable - (nint)((nuint)heap >> LogCardBundleSize));
            args.lowest_address = GCCommon.g_gc_lowest_address;
            args.highest_address = GCCommon.g_gc_highest_address;

            // Everything this heap allocates is ephemeral as far as the barrier is concerned.
            // Nothing reads the cards back yet, but marking them keeps the barrier on the same
            // path it takes with the C++ GC instead of on an untested one.
            args.ephemeral_low = GCCommon.g_gc_lowest_address;
            args.ephemeral_high = GCCommon.g_gc_highest_address;

            GCToEEInterface.StompWriteBarrier(&args);
            return true;
        }

        /// <summary>
        /// Reserves and commits a zero-filled side table of the given size, rounded up so that
        /// the last byte of the heap has an entry.
        /// </summary>
        private static byte* CommitTable(nuint size)
        {
            size += 1;
            byte* table = GCToOSInterface.VirtualReserve(size, 0, (uint)VirtualReserveFlags.None);
            return table != null && GCToOSInterface.VirtualCommit(table, size) ? table : null;
        }

        /// <summary>
        /// Carves <paramref name="size"/> zeroed bytes off the region. Returns null when the
        /// region is exhausted, which for this heap is permanent.
        /// </summary>
        /// <remarks>
        /// The memory does not need to be cleared: it is only ever handed out once, and it came
        /// from a fresh commit.
        /// </remarks>
        public static byte* Allocate(nuint size)
        {
            nint end = (nint)GCCommon.g_gc_highest_address;
            nint current = Volatile.Read(ref s_allocPtr);
            while (true)
            {
                // Compared as remaining space rather than as `current + size > end`, because the
                // EE only rejects sizes at or above int64.MaxValue before calling Alloc, and
                // adding one of those to a pointer overflows to a negative value that would
                // pass an upper-bound check.
                if (size > (nuint)(end - current))
                {
                    return null;
                }

                nint next = current + (nint)size;
                nint observed = Interlocked.CompareExchange(ref s_allocPtr, next, current);
                if (observed == current)
                {
                    return (byte*)current;
                }

                current = observed;
            }
        }
    }
}
