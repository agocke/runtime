// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using SysInterlocked = System.Threading.Interlocked;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

#if USE_REGIONS
[Collection(SyncImportsCollection.Name)]
public sealed unsafe class GCWriteBarrierTests : IDisposable
{
    private const int BasicRegionShift = 12;
    private const nuint BasicRegionSize = (nuint)1 << BasicRegionShift;

    private static readonly KeyValuePair<FieldInfo, object>[] s_declaredConfigValues = CaptureDeclaredConfigValues();

    private readonly nuint _minSegmentSizeShr;
    private readonly region_info* _mapRegionToGeneration;
    private readonly region_info* _mapRegionToGenerationSkewed;
    private readonly seg_mapping* _segMappingTable;
    private readonly byte* _lowestAddress;
    private readonly byte* _highestAddress;
    private readonly GCSpinLock _writeBarrierSpinLock;
    private readonly byte* _ephemeralLow;
    private readonly byte* _ephemeralHigh;

    private static byte* s_ephemeralLowDuringStomp;
    private static byte* s_ephemeralHighDuringStomp;
#if DEBUG
    private static void* s_holdingThreadDuringStomp;
#endif

    public GCWriteBarrierTests()
    {
        _minSegmentSizeShr = gc_heap.min_segment_size_shr;
        _mapRegionToGeneration = gc_heap.map_region_to_generation;
        _mapRegionToGenerationSkewed = gc_heap.map_region_to_generation_skewed;
        _segMappingTable = GCCommon.seg_mapping_table;
        _lowestAddress = GCCommon.g_gc_lowest_address;
        _highestAddress = GCCommon.g_gc_highest_address;
        _writeBarrierSpinLock = GCWriteBarrier.write_barrier_spin_lock;
        _ephemeralLow = gc_heap.ephemeral_low;
        _ephemeralHigh = gc_heap.ephemeral_high;

        RestoreDeclaredConfigValues();
        GCToEEInterface.Reset();
        gc_heap.min_segment_size_shr = BasicRegionShift;
        GCCommon.g_gc_lowest_address = (byte*)0x1000;
        GCCommon.g_gc_highest_address = (byte*)0xC000;
        gc_heap.map_region_to_generation = null;
        gc_heap.map_region_to_generation_skewed = null;
        GCWriteBarrier.initialize();
        s_ephemeralLowDuringStomp = null;
        s_ephemeralHighDuringStomp = null;
#if DEBUG
        s_holdingThreadDuringStomp = null;
#endif
    }

    public void Dispose()
    {
        RestoreDeclaredConfigValues();
        GCToEEInterface.Reset();
        gc_heap.min_segment_size_shr = _minSegmentSizeShr;
        gc_heap.map_region_to_generation = _mapRegionToGeneration;
        gc_heap.map_region_to_generation_skewed = _mapRegionToGenerationSkewed;
        GCCommon.seg_mapping_table = _segMappingTable;
        GCCommon.g_gc_lowest_address = _lowestAddress;
        GCCommon.g_gc_highest_address = _highestAddress;
        GCWriteBarrier.write_barrier_spin_lock = _writeBarrierSpinLock;
        gc_heap.ephemeral_low = _ephemeralLow;
        gc_heap.ephemeral_high = _ephemeralHigh;
    }

    public static IEnumerable<object[]> WriteBarrierFlavors()
    {
        yield return new object[] { (long)WriteBarrierFlavor.WRITE_BARRIER_DEFAULT, true, true };
        yield return new object[] { (long)WriteBarrierFlavor.WRITE_BARRIER_REGION_BIT, true, true };
        yield return new object[] { (long)WriteBarrierFlavor.WRITE_BARRIER_REGION_BYTE, true, false };
        yield return new object[] { (long)WriteBarrierFlavor.WRITE_BARRIER_SERVER, false, false };
    }

    [Theory]
    [MemberData(nameof(WriteBarrierFlavors))]
    public void RegionWriteBarrierSettingsHonorEveryConfiguredFlavor(
        long flavor,
        bool publishesRegionMap,
        bool usesBitwiseBarrier)
    {
        SetWriteBarrierFlavor((WriteBarrierFlavor)flavor);
        region_info* map = (region_info*)0x1234;
        WriteBarrierParameters args = default;

        GCWriteBarrier.region_write_barrier_settings(&args, map, BasicRegionShift);

        Assert.Equal(
            publishesRegionMap ? (nuint)map : 0,
            (nuint)args.region_to_generation_table);
        Assert.Equal(publishesRegionMap ? BasicRegionShift : 0, args.region_shr);
        Assert.Equal(usesBitwiseBarrier ? (byte)1 : (byte)0, args.region_use_bitwise_write_barrier);
    }

    [Fact]
    public void StompWriteBarrierEphemeralPublishesExactRegionArguments()
    {
        SetWriteBarrierFlavor(WriteBarrierFlavor.WRITE_BARRIER_REGION_BIT);
        region_info* map = (region_info*)0x1234;
        byte* low = (byte*)0x4000;
        byte* high = (byte*)0x7000;

        GCWriteBarrier.stomp_write_barrier_ephemeral(low, high, map, BasicRegionShift);

        Assert.Equal(1, GCToEEInterface.StompWriteBarrierCallCount);
        WriteBarrierParameters args = GCToEEInterface.LastStompWriteBarrier;
        Assert.Equal(WriteBarrierOp.StompEphemeral, args.operation);
        Assert.Equal((byte)1, args.is_runtime_suspended);
        Assert.Equal((nuint)low, (nuint)args.ephemeral_low);
        Assert.Equal((nuint)high, (nuint)args.ephemeral_high);
        Assert.Equal((nuint)map, (nuint)args.region_to_generation_table);
        Assert.Equal(BasicRegionShift, args.region_shr);
        Assert.Equal((byte)1, args.region_use_bitwise_write_barrier);
    }

    [Fact]
    public void SetRegionGenNumFillsEveryBasicRegionAndPublishesBeforeBounds()
    {
        SetWriteBarrierFlavor(WriteBarrierFlavor.WRITE_BARRIER_REGION_BIT);
        region_info* map = stackalloc region_info[8];
        InitializeMap(map);
        heap_segment region = default;
        InitializeRegion(&region, (byte*)0x2000, 3 * BasicRegionSize);

        GCToEEInterface.CurrentThread = (void*)0x12345678;
        GCToEEInterface.StompWriteBarrierObserver = ObserveStomp;

        gc_heap.set_region_gen_num(&region, (int)gc_generation_num.soh_gen1);

        region_info expected = (region_info)((int)gc_generation_num.soh_gen1
            | ((int)gc_generation_num.soh_gen1 << (int)region_info.RI_PLAN_GEN_SHR));
        Assert.Equal((byte)expected, (byte)map[1]);
        Assert.Equal((byte)expected, (byte)map[2]);
        Assert.Equal((byte)expected, (byte)map[3]);
        Assert.Equal((int)gc_generation_num.soh_gen1, heap_segment.heap_segment_gen_num(&region));

        Assert.Equal((nuint)0x2000, (nuint)gc_heap.ephemeral_low);
        Assert.Equal((nuint)0x5000, (nuint)gc_heap.ephemeral_high);
        Assert.Equal((nuint)nuint.MaxValue, (nuint)s_ephemeralLowDuringStomp);
        Assert.Equal((nuint)0, (nuint)s_ephemeralHighDuringStomp);
        Assert.Equal(1, GCToEEInterface.StompWriteBarrierCallCount);
        Assert.Equal(GCSpinLock.lock_free, GCWriteBarrier.write_barrier_spin_lock.@lock);
#if DEBUG
        Assert.Equal((nuint)0x12345678, (nuint)s_holdingThreadDuringStomp);
        Assert.Equal(nuint.MaxValue, (nuint)GCWriteBarrier.write_barrier_spin_lock.holding_thread);
        Assert.Equal(1, GCToEEInterface.GetThreadCallCount);
#endif
    }

    [Fact]
    public void SetRegionGenNumDoesNotPublishGen2()
    {
        region_info* map = stackalloc region_info[8];
        InitializeMap(map);
        heap_segment region = default;
        InitializeRegion(&region, (byte*)0x2000, 2 * BasicRegionSize);

        gc_heap.set_region_gen_num(&region, (int)gc_generation_num.soh_gen2);

        region_info expected = (region_info)((int)gc_generation_num.soh_gen2
            | ((int)gc_generation_num.soh_gen2 << (int)region_info.RI_PLAN_GEN_SHR));
        Assert.Equal((byte)expected, (byte)map[1]);
        Assert.Equal((byte)expected, (byte)map[2]);
        Assert.Equal(0, GCToEEInterface.StompWriteBarrierCallCount);
        Assert.Equal((nuint)nuint.MaxValue, (nuint)gc_heap.ephemeral_low);
        Assert.Equal((nuint)0, (nuint)gc_heap.ephemeral_high);
        Assert.Equal(GCSpinLock.lock_free, GCWriteBarrier.write_barrier_spin_lock.@lock);
    }

    [Fact]
    public void SetRegionGenNumExpandsBoundsMonotonicallyAndSkipsContainedRange()
    {
        region_info* map = stackalloc region_info[8];
        InitializeMap(map);
        heap_segment middle = default;
        heap_segment lower = default;
        heap_segment contained = default;
        InitializeRegion(&middle, (byte*)0x3000, 2 * BasicRegionSize);
        InitializeRegion(&lower, (byte*)0x2000, BasicRegionSize);
        InitializeRegion(&contained, (byte*)0x4000, BasicRegionSize);

        gc_heap.set_region_gen_num(&middle, (int)gc_generation_num.soh_gen0);
        gc_heap.set_region_gen_num(&lower, (int)gc_generation_num.soh_gen1);
        gc_heap.set_region_gen_num(&contained, (int)gc_generation_num.soh_gen0);

        Assert.Equal(2, GCToEEInterface.StompWriteBarrierCallCount);
        Assert.Equal((nuint)0x2000, (nuint)gc_heap.ephemeral_low);
        Assert.Equal((nuint)0x5000, (nuint)gc_heap.ephemeral_high);
        Assert.Equal(GCSpinLock.lock_free, GCWriteBarrier.write_barrier_spin_lock.@lock);
    }

    [Fact]
    public void SetRegionGenNumDoesNotPublishAfterBoundsBecomeCovered()
    {
        region_info* map = stackalloc region_info[8];
        InitializeMap(map);
        heap_segment region = default;
        InitializeRegion(&region, (byte*)0x2000, BasicRegionSize);
        nuint regionAddress = (nuint)(&region);
        int workerStarted = 0;
        Thread worker = new(() =>
        {
            SysInterlocked.Exchange(ref workerStarted, 1);
            gc_heap.set_region_gen_num((heap_segment*)regionAddress, (int)gc_generation_num.soh_gen0);
        });

        fixed (int* lockAddress = &GCWriteBarrier.write_barrier_spin_lock.@lock)
        {
            GCEnv.VolatileStore(lockAddress, 0);
            try
            {
                worker.Start();
                Assert.True(SpinWait.SpinUntil(() => SysInterlocked.CompareExchange(ref workerStarted, 0, 0) != 0, 30000));

                gc_heap.ephemeral_low = (byte*)0x2000;
                gc_heap.ephemeral_high = (byte*)0x3000;
            }
            finally
            {
                GCEnv.VolatileStore(lockAddress, GCSpinLock.lock_free);
                Assert.True(worker.Join(30000));
            }
        }

        Assert.Equal(0, GCToEEInterface.StompWriteBarrierCallCount);
        Assert.Equal(GCSpinLock.lock_free, GCWriteBarrier.write_barrier_spin_lock.@lock);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitHeapSegmentResetsBasicRegionAndRetainsOnlyExistingMarkArrayCommit(bool existingRegion)
    {
        SetWriteBarrierFlavor(WriteBarrierFlavor.WRITE_BARRIER_REGION_BIT);
        seg_mapping* mappings = stackalloc seg_mapping[16];
        region_info* map = stackalloc region_info[16];
        for (int i = 0; i < 16; i++)
        {
            mappings[i] = default;
        }

        GCCommon.seg_mapping_table = mappings;
        InitializeMap(map);
        heap_segment* segment = &mappings[2].region_info;
        InitializeSegment(segment, (byte*)0x2000, BasicRegionSize);
        segment->flags = heap_segment.heap_segment_flags_poh;
#if BACKGROUND_GC
        segment->flags |= heap_segment.heap_segment_flags_ma_committed;
        heap_segment.heap_segment_background_allocated(segment) = (byte*)0x7777;
        heap_segment.heap_segment_saved_bg_allocated(segment) = (byte*)0x8888;
#endif
        heap_segment.heap_segment_next(segment) = (heap_segment*)0x3333;
        heap_segment.heap_segment_plan_allocated(segment) = (byte*)0x4444;
        heap_segment.heap_segment_allocated(segment) = (byte*)0x5555;
        heap_segment.heap_segment_saved_allocated(segment) = (byte*)0x6666;
#if !USE_REGIONS || MULTIPLE_HEAPS
        heap_segment.heap_segment_decommit_target(segment) = (byte*)0x7777;
#endif
#if MULTIPLE_HEAPS
        heap_segment.heap_segment_heap(segment) = null;
#endif
        heap_segment.heap_segment_plan_gen_num(segment) = -1;
        heap_segment.heap_segment_swept_in_plan(segment) = 1;

        gc_heap.init_heap_segment(
            segment,
            (gc_heap*)0x1234,
            (byte*)0x2000,
            BasicRegionSize,
            (int)gc_generation_num.soh_gen1,
            existingRegion);

#if BACKGROUND_GC
        Assert.Equal(
            existingRegion ? heap_segment.heap_segment_flags_ma_committed : 0,
            segment->flags);
        Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_background_allocated(segment));
        Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_saved_bg_allocated(segment));
#else
        Assert.Equal((nuint)0, segment->flags);
#endif
        Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_next(segment));
        Assert.Equal((nuint)heap_segment.heap_segment_mem(segment), (nuint)heap_segment.heap_segment_plan_allocated(segment));
        Assert.Equal((nuint)heap_segment.heap_segment_mem(segment), (nuint)heap_segment.heap_segment_allocated(segment));
        Assert.Equal((nuint)heap_segment.heap_segment_mem(segment), (nuint)heap_segment.heap_segment_saved_allocated(segment));
#if !USE_REGIONS || MULTIPLE_HEAPS
        Assert.Equal((nuint)heap_segment.heap_segment_reserved(segment), (nuint)heap_segment.heap_segment_decommit_target(segment));
#endif
#if MULTIPLE_HEAPS
        Assert.Equal((nuint)0x1234, (nuint)heap_segment.heap_segment_heap(segment));
#endif
        Assert.Equal((byte)gc_generation_num.soh_gen1, heap_segment.heap_segment_gen_num(segment));
        Assert.Equal((int)gc_generation_num.soh_gen1, heap_segment.heap_segment_plan_gen_num(segment));
        Assert.Equal((byte)0, heap_segment.heap_segment_swept_in_plan(segment));
        Assert.Equal(
            (byte)((int)gc_generation_num.soh_gen1 | ((int)gc_generation_num.soh_gen1 << (int)region_info.RI_PLAN_GEN_SHR)),
            (byte)map[1]);

        Assert.Equal(1, GCToEEInterface.StompWriteBarrierCallCount);
        WriteBarrierParameters args = GCToEEInterface.LastStompWriteBarrier;
        Assert.Equal(WriteBarrierOp.StompEphemeral, args.operation);
        Assert.Equal((byte)1, args.is_runtime_suspended);
        Assert.Equal((nuint)0x2000, (nuint)args.ephemeral_low);
        Assert.Equal((nuint)0x3000, (nuint)args.ephemeral_high);
        Assert.Equal((nuint)gc_heap.map_region_to_generation_skewed, (nuint)args.region_to_generation_table);
        Assert.Equal(BasicRegionShift, args.region_shr);
        Assert.Equal((byte)1, args.region_use_bitwise_write_barrier);
    }

    [Fact]
    public void InitHeapSegmentClampsUohGenerationAndInitializesLargeRegionContinuations()
    {
        seg_mapping* mappings = stackalloc seg_mapping[16];
        region_info* map = stackalloc region_info[16];
        for (int i = 0; i < 16; i++)
        {
            mappings[i] = default;
        }

        GCCommon.seg_mapping_table = mappings;
        InitializeMap(map);
        heap_segment* segment = &mappings[2].region_info;
        InitializeSegment(segment, (byte*)0x2000, 3 * BasicRegionSize);
        for (int i = 1; i < 3; i++)
        {
            heap_segment* basicRegion = &mappings[2 + i].region_info;
            basicRegion->allocated = (byte*)0xCCCC;
            basicRegion->gen_num = byte.MaxValue;
            basicRegion->plan_gen_num = -1;
#if MULTIPLE_HEAPS
            basicRegion->heap = null;
#endif
        }

        gc_heap.init_heap_segment(
            segment,
            (gc_heap*)0x1234,
            (byte*)0x2000,
            3 * BasicRegionSize,
            (int)gc_generation_num.loh_generation);

        int expectedGeneration = GCInterfaceOffsets.max_generation;
        byte expectedMapEntry = (byte)(expectedGeneration | (expectedGeneration << (int)region_info.RI_PLAN_GEN_SHR));
        Assert.Equal((byte)expectedGeneration, heap_segment.heap_segment_gen_num(segment));
        Assert.Equal(expectedGeneration, heap_segment.heap_segment_plan_gen_num(segment));
        Assert.Equal((byte)0, heap_segment.heap_segment_swept_in_plan(segment));
        Assert.Equal(expectedMapEntry, (byte)map[1]);
        Assert.Equal(expectedMapEntry, (byte)map[2]);
        Assert.Equal(expectedMapEntry, (byte)map[3]);
        Assert.Equal(0, GCToEEInterface.StompWriteBarrierCallCount);

        for (int i = 1; i < 3; i++)
        {
            heap_segment* basicRegion = &mappings[2 + i].region_info;
            Assert.Equal((nint)(-i), (nint)heap_segment.heap_segment_allocated(basicRegion));
            Assert.Equal((byte)expectedGeneration, heap_segment.heap_segment_gen_num(basicRegion));
            Assert.Equal(expectedGeneration, heap_segment.heap_segment_plan_gen_num(basicRegion));
#if MULTIPLE_HEAPS
            Assert.Equal((nuint)0x1234, (nuint)heap_segment.heap_segment_heap(basicRegion));
#endif
        }
    }

    private static void InitializeMap(region_info* map)
    {
        gc_heap.map_region_to_generation = map;
        gc_heap.map_region_to_generation_skewed =
            map - (nint)((nuint)GCCommon.g_gc_lowest_address >> (int)gc_heap.min_segment_size_shr);
    }

    private static void InitializeRegion(heap_segment* region, byte* start, nuint size)
    {
        *region = default;
        byte* memory = start + sizeof(aligned_plug_and_gap);
        heap_segment.heap_segment_mem(region) = memory;
        heap_segment.heap_segment_allocated(region) = memory;
        heap_segment.heap_segment_reserved(region) = start + (nint)size;
    }

    private static void InitializeSegment(heap_segment* segment, byte* start, nuint size)
    {
        *segment = default;
        byte* memory = start + sizeof(aligned_plug_and_gap);
        heap_segment.heap_segment_mem(segment) = memory;
        heap_segment.heap_segment_committed(segment) = start + (nint)size;
        heap_segment.heap_segment_reserved(segment) = start + (nint)size;
    }

    private static void ObserveStomp(WriteBarrierParameters args)
    {
        _ = args;
        s_ephemeralLowDuringStomp = gc_heap.ephemeral_low;
        s_ephemeralHighDuringStomp = gc_heap.ephemeral_high;
#if DEBUG
        s_holdingThreadDuringStomp = GCWriteBarrier.write_barrier_spin_lock.holding_thread;
#endif
    }

    private static void SetWriteBarrierFlavor(WriteBarrierFlavor flavor)
    {
        GCToEEInterface.SetPrivateValue("GCWriteBarrier", (ulong)flavor);
        GCConfig.Initialize();
    }

    private static KeyValuePair<FieldInfo, object>[] CaptureDeclaredConfigValues() =>
        typeof(GCConfig)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(byte) || field.FieldType == typeof(long))
            .Select(field => new KeyValuePair<FieldInfo, object>(field, field.GetValue(null)))
            .ToArray();

    private static void RestoreDeclaredConfigValues()
    {
        foreach (KeyValuePair<FieldInfo, object> declared in s_declaredConfigValues)
        {
            declared.Key.SetValue(null, declared.Value);
        }
    }
}
#endif
