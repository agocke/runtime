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

    [Fact]
    public static void ServerJoinBarrierMatchesNativeShape()
    {
        Type joinConstants = GetType(
            "Internal.Runtime.GarbageCollection.join_constants");
        Assert.Equal(2, GetConstant(joinConstants, "first_thread_arrived"));

        Type joinType = GetType("Internal.Runtime.GarbageCollection.join_type");
        Assert.Equal(0, (int)Enum.Parse(joinType, "type_last_join"));
        Assert.Equal(1, (int)Enum.Parse(joinType, "type_join"));
        Assert.Equal(2, (int)Enum.Parse(joinType, "type_restart"));
        Assert.Equal(3, (int)Enum.Parse(joinType, "type_first_r_join"));
        Assert.Equal(4, (int)Enum.Parse(joinType, "type_r_join"));

        Type joinTime = GetType("Internal.Runtime.GarbageCollection.join_time");
        Assert.Equal(0, (int)Enum.Parse(joinTime, "time_start"));
        Assert.Equal(1, (int)Enum.Parse(joinTime, "time_end"));

        Type joinHeapIndex = GetType(
            "Internal.Runtime.GarbageCollection.join_heap_index");
        Assert.Equal(100, (int)Enum.Parse(joinHeapIndex, "join_heap_restart"));
        Assert.Equal(200, (int)Enum.Parse(joinHeapIndex, "join_heap_r_restart"));

        Type joinStructure = GetType(
            "Internal.Runtime.GarbageCollection.join_structure");
        foreach (string field in new[]
        {
            "n_threads",
            "joined_event",
            "lock_color",
            "wait_done",
            "joined_p",
            "join_lock",
            "r_join_lock",
        })
        {
            Assert.NotNull(joinStructure.GetField(
                field,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        }

        Type tJoin = GetType("Internal.Runtime.GarbageCollection.t_join");
        foreach (string method in new[]
        {
            "init",
            "update_n_threads",
            "get_num_threads",
            "get_join_lock",
            "destroy",
            "join",
            "r_join",
            "restart",
            "joined",
            "r_restart",
            "r_init",
        })
        {
            Assert.NotNull(tJoin.GetMethod(
                method,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        }

        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        Assert.Equal(
            tJoin,
            heap.GetField(
                "gc_t_join",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.FieldType);
    }

    [Fact]
    public static void GcDoneHandshakeStateAndCoordinationArePresent()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        Assert.NotNull(heap.GetField(
            "gc_done_event_lock",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(heap.GetField(
            "gc_done_event_set",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));

        Assert.NotNull(heap.GetField(
            "gc_started",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        Assert.NotNull(heap.GetField(
            "internal_gc_done",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));

        foreach (string method in new[]
        {
            "set_gc_done",
            "reset_gc_done",
            "enter_gc_done_event_lock",
            "exit_gc_done_event_lock",
            "wait_for_gc_done",
            "enable_preemptive",
            "disable_preemptive",
        })
        {
            Assert.NotNull(heap.GetMethod(
                method,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        }

        Type common = GetType("Internal.Runtime.GarbageCollection.GCCommon");
        Assert.NotNull(common.GetField(
            "g_num_processors",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
    }

    [Fact]
    public static void ServerAllocationStateInitializesGcDoneLock()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        MethodInfo initialize = heap.GetMethod(
            "initialize_server_allocation_state",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

        AssertStoresConstant(
            initialize,
            heap.GetField(
                "gc_done_event_lock",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!,
            0x15);
        AssertStoresConstant(
            initialize,
            heap.GetField(
                "gc_done_event_set",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!,
            0x16);
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

    private static void AssertStoresConstant(
        MethodInfo method,
        FieldInfo field,
        byte constantOpcode)
    {
        byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
        byte[] fieldToken = BitConverter.GetBytes(field.MetadataToken);
        for (int i = 0; i <= il.Length - 6; i++)
        {
            if (il[i] == constantOpcode &&
                il[i + 1] == 0x7d &&
                il[i + 2] == fieldToken[0] &&
                il[i + 3] == fieldToken[1] &&
                il[i + 4] == fieldToken[2] &&
                il[i + 5] == fieldToken[3])
            {
                return;
            }
        }

        Assert.Fail(
            $"{method.Name} does not initialize {field.Name} with opcode 0x{constantOpcode:x2}.");
    }
}
