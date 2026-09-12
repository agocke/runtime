// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

#pragma warning disable CA1823, CS0169, IDE0060

namespace Internal.Runtime.GC
{
    internal unsafe struct changed_seg
    {
        public byte* start;
        public byte* end;
        public nuint gc_index;
        public bgc_state bgc;
        public changed_seg_state changed;
    }

    internal unsafe struct ChangedSegmentStore
    {
        public fixed ulong data[640];
    }

    internal unsafe struct GCCommon
    {
        public static IGCHeap* g_theGCHeap;
        public static IGCHandleManager* g_theGCHandleManager;
        public static IGCToCLR* g_theGCToCLR;
        public static uint* g_gc_card_table;
        public static uint* g_gc_card_bundle_table;
        public static byte* g_gc_lowest_address;
        public static byte* g_gc_highest_address;
        public static uint g_num_processors;
        public static int g_fSuspensionPending;
        public static MethodTable* g_gc_pFreeObjectMethodTable;
        public static uint g_max_generation;

        private const uint MaxSavedChangedSegments = 128;
        private static ChangedSegmentStore s_changedSegmentStore;
        private static ulong s_savedChangedSegmentsCount;
        public static void Initialize()
        {
            g_max_generation = (uint)gc_generation_num.max_generation;
            s_savedChangedSegmentsCount = ulong.MaxValue;
            fixed (ulong* storage = s_changedSegmentStore.data)
            {
                storage[0] = 0;
            }
        }

        public static void RecordChangedSegment(byte* start, byte* end, nuint currentGcIndex, bgc_state currentBgcState, changed_seg_state changedState)
        {
            ulong segmentCount = unchecked(++s_savedChangedSegmentsCount);
            uint segmentIndex = (uint)(segmentCount & (MaxSavedChangedSegments - 1));
            fixed (ulong* storage = s_changedSegmentStore.data)
            {
                changed_seg* segments = (changed_seg*)storage;
                segments[segmentIndex].start = start;
                segments[segmentIndex].end = end;
                segments[segmentIndex].gc_index = currentGcIndex;
                segments[segmentIndex].bgc = currentBgcState;
                segments[segmentIndex].changed = changedState;
            }
        }

        public static ulong GetHighPrecisionTimeStamp()
        {
            return (ulong)Interop.Sys.GetTimestamp() / 1000;
        }

        public static void LogInitErrorToHost(byte* message)
        {
            if (g_theGCToCLR is not null && g_theGCToCLR->Vtable is not null)
            {
                g_theGCToCLR->Vtable->LogErrorToHost(g_theGCToCLR, message);
            }
        }
    }
}
