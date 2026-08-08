// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

public static class ManagedServerGCFoundationTests
{
    private static readonly Assembly s_serverGC = Assembly.LoadFile(
        Path.Combine(AppContext.BaseDirectory, "System.Private.GC.Server.dll"));

    [Fact]
    public static void ServerFeatureAndNativeLayoutsMatch()
    {
        Type offsets = GetType("Internal.Runtime.GarbageCollection.GCInterfaceOffsets");
        Assert.Equal(1, GetConstant(offsets, "MANAGED_SERVER_GC_LAYOUT"));
        Assert.Equal(1, GetConstant(offsets, "MANAGED_MULTIPLE_HEAPS_LAYOUT"));
        Assert.Equal(1, GetConstant(offsets, "MANAGED_DYNAMIC_HEAP_COUNT_LAYOUT"));

        Type layout = GetType("Internal.Runtime.GarbageCollection.GCInterfaceLayout");
        Assert.True((bool)layout.GetMethod(
            "Verify",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(
                null,
                null)!);
        Assert.Equal(0xb8, Marshal.SizeOf(
            GetType("Internal.Runtime.GarbageCollection.heap_segment")));
#if DEBUG
        Assert.Equal(0x20, Marshal.SizeOf(
            GetType("Internal.Runtime.GarbageCollection.GCSpinLock")));
        Assert.Equal(0x80, Marshal.SizeOf(
            GetType("Internal.Runtime.GarbageCollection.region_allocator")));
#else
        Assert.Equal(0x10, Marshal.SizeOf(
            GetType("Internal.Runtime.GarbageCollection.GCSpinLock")));
        Assert.Equal(0x70, Marshal.SizeOf(
            GetType("Internal.Runtime.GarbageCollection.region_allocator")));
#endif
    }

    [Fact]
    public static void DynamicHeapCountStateStartsFromNativeDefaults()
    {
        Type stateType = GetType(
            "Internal.Runtime.GarbageCollection.dynamic_heap_count_data_t");
        object state = Activator.CreateInstance(stateType)!;
        stateType.GetMethod(
            "initialize",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(
                state,
                new object[] { 4 });

        Assert.Equal(2.0f, GetField<float>(stateType, state, "target_tcp"));
        Assert.Equal(10.0f, GetField<float>(stateType, state, "target_gen2_tcp"));
        Assert.Equal(
            1.0f,
            GetField<float>(
                stateType,
                state,
                "gen0_growth_soh_ratio_percent"));
        Assert.Equal(
            0.1f,
            GetField<float>(
                stateType,
                state,
                "gen0_growth_soh_ratio_min"));
        Assert.Equal(
            10.0f,
            GetField<float>(
                stateType,
                state,
                "gen0_growth_soh_ratio_max"));
        Assert.Equal(4, GetField<int>(stateType, state, "last_n_heaps"));
        Assert.Equal(4, GetField<int>(stateType, state, "new_n_heaps"));
    }

    [Fact]
    public static void PerHeapAllocationAndRegionStateIsInstanceOwned()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        Assert.NotNull(heap.GetField(
            "server_free_regions",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(heap.GetField(
            "alloc_context_count",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(heap.GetField(
            "gc_done_event",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(heap.GetField(
            "n_heaps",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        Assert.NotNull(heap.GetField(
            "g_heaps",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
    }

    private static Type GetType(string name) =>
        s_serverGC.GetType(name, throwOnError: true)!;

    private static int GetConstant(Type type, string name) =>
        (int)type.GetField(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

    private static T GetField<T>(Type type, object instance, string name) =>
        (T)type.GetField(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(
                instance)!;
}
