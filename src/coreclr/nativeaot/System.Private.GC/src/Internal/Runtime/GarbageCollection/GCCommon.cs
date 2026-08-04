// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the dependency-closed parts of gccommon.cpp, in their original order.

using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection;

internal static unsafe partial class GCCommon
{
    // The heap's bounds. gccommon.cpp declares both and zero-initializes them; the C# default
    // for a static byte* field is already null, so there is nothing to write out here. Whatever
    // creates the heap -- currently GCHeapMemory.Initialize -- publishes both, and
    // SoftwareWriteWatch.GetHeapStartAddress/GetHeapEndAddress read them back, exactly as
    // softwarewritewatch.h does.
    internal static byte* g_gc_lowest_address;
    internal static byte* g_gc_highest_address;

#if USE_REGIONS
    // gcinternal.h declares this as a global, not a gc_heap member. The entries are addressed
    // directly by absolute-address >> gc_heap::min_segment_size_shr, so callers publish the
    // already-skewed base pointer.
    internal static seg_mapping* seg_mapping_table;
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
