// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the USE_REGIONS write-barrier helpers from gc.cpp.

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection;

#if USE_REGIONS
internal static unsafe class GCWriteBarrier
{
    // regions_segments.cpp defines this as a global. Static fields are zero-initialized, so
    // initialize supplies the native GCSpinLock constructor's non-zero sentinel explicitly.
    internal static GCSpinLock write_barrier_spin_lock;

    public static void initialize()
    {
        GCSpinLock.initialize(ref write_barrier_spin_lock);
        gc_heap.ephemeral_low = (byte*)nuint.MaxValue;
        gc_heap.ephemeral_high = null;
    }

    public static void region_write_barrier_settings(
        WriteBarrierParameters* args,
        region_info* map_region_to_generation_skewed,
        byte region_shr)
    {
        switch ((WriteBarrierFlavor)GCConfig.GetGCWriteBarrier())
        {
            default:
            case WriteBarrierFlavor.WRITE_BARRIER_DEFAULT:
            case WriteBarrierFlavor.WRITE_BARRIER_REGION_BIT:
                // bitwise region write barrier is the default now
                args->region_to_generation_table = (byte*)map_region_to_generation_skewed;
                args->region_shr = region_shr;
                args->region_use_bitwise_write_barrier = 1;
                break;

            case WriteBarrierFlavor.WRITE_BARRIER_REGION_BYTE:
                // bytewise region write barrier
                args->region_to_generation_table = (byte*)map_region_to_generation_skewed;
                args->region_shr = region_shr;
                Debug.Assert(args->region_use_bitwise_write_barrier == 0);
                break;

            case WriteBarrierFlavor.WRITE_BARRIER_SERVER:
                // server write barrier
                // args should have been zero initialized
                Debug.Assert(args->region_use_bitwise_write_barrier == 0);
                Debug.Assert(args->region_to_generation_table is null);
                Debug.Assert(args->region_shr == 0);
                break;
        }
    }

    public static void stomp_write_barrier_ephemeral(
        byte* ephemeral_low,
        byte* ephemeral_high,
        region_info* map_region_to_generation_skewed,
        byte region_shr)
    {
        WriteBarrierParameters args = default;
        args.operation = WriteBarrierOp.StompEphemeral;
        args.is_runtime_suspended = 1;
        args.ephemeral_low = ephemeral_low;
        args.ephemeral_high = ephemeral_high;
        region_write_barrier_settings(&args, map_region_to_generation_skewed, region_shr);
        GCToEEInterface.StompWriteBarrier(&args);
    }

    public static void stomp_write_barrier_initialize(
        byte* ephemeral_low,
        byte* ephemeral_high,
        region_info* map_region_to_generation_skewed,
        byte region_shr)
    {
        WriteBarrierParameters args = default;
        args.operation = WriteBarrierOp.Initialize;
        args.is_runtime_suspended = 1;
        args.card_table = gc_heap.card_table;
        args.card_bundle_table = gc_heap.card_bundle_table;
        args.lowest_address = GCCommon.g_gc_lowest_address;
        args.highest_address = GCCommon.g_gc_highest_address;
        args.ephemeral_low = ephemeral_low;
        args.ephemeral_high = ephemeral_high;
        region_write_barrier_settings(&args, map_region_to_generation_skewed, region_shr);
        GCToEEInterface.StompWriteBarrier(&args);
    }
}
#endif
