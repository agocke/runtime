// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the dependency-closed parts of gccommon.cpp, in their original order.

using System.Runtime.CompilerServices;
using System.Threading;

namespace Internal.Runtime.GarbageCollection;

#if BACKGROUND_GC
internal enum bgc_state
{
    bgc_not_in_process = 0,
    bgc_initialized,
    bgc_reset_ww,
    bgc_mark_handles,
    bgc_mark_stack,
    bgc_revisit_soh,
    bgc_revisit_uoh,
    bgc_overflow_soh,
    bgc_overflow_uoh,
    bgc_final_marking,
    bgc_sweep_soh,
    bgc_sweep_uoh,
    bgc_plan_phase,
}

internal enum changed_seg_state
{
    seg_deleted,
    seg_added,
}

internal unsafe struct changed_seg
{
    public byte* start;
    public byte* end;
    public nuint gc_index;
    public bgc_state bgc;
    public changed_seg_state changed;
}
#endif

internal static unsafe partial class GCCommon
{
    // The heap's bounds. gccommon.cpp declares both and zero-initializes them; the C# default
    // for a static byte* field is already null, so there is nothing to write out here. Whatever
    // creates the heap -- currently GCHeapMemory.Initialize -- publishes both, and
    // SoftwareWriteWatch.GetHeapStartAddress/GetHeapEndAddress read them back, exactly as
    // softwarewritewatch.h does.
    internal static byte* g_gc_lowest_address;
    internal static byte* g_gc_highest_address;
    internal static void* g_gc_pFreeObjectMethodTable;
    // gccommon.cpp: the process's usable processor count, published by GCHeap::Initialize.
    internal static uint g_num_processors;
    internal static int g_fSuspensionPending;
    internal static int g_wait_for_gc_event = 1;

    internal static void InitializeRuntimeLifecycleState()
    {
        Volatile.Write(ref g_fSuspensionPending, 0);
        Volatile.Write(ref g_wait_for_gc_event, 1);
    }

    internal static void SetSuspensionPending(bool suspensionPending)
    {
        if (suspensionPending)
        {
            Interlocked.Increment(ref g_fSuspensionPending);
        }
        else
        {
            Interlocked.Decrement(ref g_fSuspensionPending);
        }
    }

    internal static void SetWaitForGCEvent() =>
        Volatile.Write(ref g_wait_for_gc_event, 1);

    internal static void ResetWaitForGCEvent() =>
        Volatile.Write(ref g_wait_for_gc_event, 0);

#if USE_REGIONS
    // gcinternal.h declares this as a global, not a gc_heap member. The entries are addressed
    // directly by absolute-address >> gc_heap::min_segment_size_shr, so callers publish the
    // already-skewed base pointer.
    internal static seg_mapping* seg_mapping_table;
#endif

#if BACKGROUND_GC
    internal const int max_saved_changed_segs = 128;

    internal static changed_seg_array saved_changed_segs;
    internal static ulong saved_changed_segs_count;

    public static void initialize()
    {
        saved_changed_segs_count = ulong.MaxValue;
    }

    public static void record_changed_seg(byte* start, byte* end, nuint current_gc_index, bgc_state current_bgc_state, changed_seg_state changed_state)
    {
#if MULTIPLE_HEAPS && USE_REGIONS
        ulong segs_count = Interlocked.Increment(ref saved_changed_segs_count);
#else
        saved_changed_segs_count = unchecked(saved_changed_segs_count + 1);
        ulong segs_count = saved_changed_segs_count;
#endif

        uint segs_index = (uint)(segs_count & (max_saved_changed_segs - 1));

        saved_changed_segs[(int)segs_index].start = start;
        saved_changed_segs[(int)segs_index].end = end;
        saved_changed_segs[(int)segs_index].gc_index = current_gc_index;
        saved_changed_segs[(int)segs_index].bgc = current_bgc_state;
        saved_changed_segs[(int)segs_index].changed = changed_state;
    }

    [InlineArray(max_saved_changed_segs)]
    internal struct changed_seg_array
    {
        private changed_seg _element0;
    }
#endif

    private static double g_QPFus;

    public static ulong GetHighPrecisionTimeStamp()
    {
        if (g_QPFus == 0.0)
        {
            g_QPFus = 1000.0 * 1000.0 / (double)GCToOSInterface.QueryPerformanceFrequency();
        }

        return (ulong)((double)GCToOSInterface.QueryPerformanceCounter() * g_QPFus);
    }

    public static void MemSet(byte* destination, byte value, nuint byteCount)
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
