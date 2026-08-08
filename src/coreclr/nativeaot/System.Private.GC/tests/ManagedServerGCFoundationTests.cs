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
        foreach (string fieldName in new[]
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
                fieldName,
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

    [Fact]
    public static void CondemnReasonEnumsMatchNativeShape()
    {
        Type reasonGen = GetType(
            "Internal.Runtime.GarbageCollection.gc_condemn_reason_gen");
        Assert.Equal(0, (int)Enum.Parse(reasonGen, "gen_initial"));
        Assert.Equal(1, (int)Enum.Parse(reasonGen, "gen_final_per_heap"));
        Assert.Equal(2, (int)Enum.Parse(reasonGen, "gen_alloc_budget"));
        Assert.Equal(3, (int)Enum.Parse(reasonGen, "gen_time_tuning"));
        Assert.Equal(4, (int)Enum.Parse(reasonGen, "gcrg_max"));

        Type reasonCondition = GetType(
            "Internal.Runtime.GarbageCollection.gc_condemn_reason_condition");
        Assert.Equal(4, (int)Enum.Parse(reasonCondition, "gen_low_ephemeral_p"));
        Assert.Equal(5, (int)Enum.Parse(reasonCondition, "gen_low_card_p"));
        Assert.Equal(6, (int)Enum.Parse(reasonCondition, "gen_eph_high_frag_p"));
        Assert.Equal(7, (int)Enum.Parse(reasonCondition, "gen_max_high_frag_p"));
        Assert.Equal(16, (int)Enum.Parse(reasonCondition, "gen_almost_max_alloc"));
        Assert.Equal(17, (int)Enum.Parse(reasonCondition, "gen_joined_avoid_unproductive"));
        Assert.Equal(18, (int)Enum.Parse(reasonCondition, "gen_joined_pm_induced_fullgc_p"));
        Assert.Equal(21, (int)Enum.Parse(reasonCondition, "gen_joined_limit_before_oom"));
        Assert.Equal(22, (int)Enum.Parse(reasonCondition, "gen_joined_limit_loh_frag"));
        Assert.Equal(23, (int)Enum.Parse(reasonCondition, "gen_joined_limit_loh_reclaim"));
        Assert.Equal(30, (int)Enum.Parse(reasonCondition, "gen_joined_aggressive"));
        Assert.Equal(31, (int)Enum.Parse(reasonCondition, "gcrc_max"));
    }

    [Fact]
    public static void GenToCondemnTuningEncodesReasons()
    {
        Type tuning = GetType(
            "Internal.Runtime.GarbageCollection.gen_to_condemn_tuning");
        Type reasonGen = GetType(
            "Internal.Runtime.GarbageCollection.gc_condemn_reason_gen");
        Type reasonCondition = GetType(
            "Internal.Runtime.GarbageCollection.gc_condemn_reason_condition");

        object reasons = Activator.CreateInstance(tuning)!;
        MethodInfo init = GetMethod(tuning, "init", Type.EmptyTypes);
        MethodInfo setGen = GetMethod(tuning, "set_gen", new[] { reasonGen, typeof(uint) });
        MethodInfo setCondition = GetMethod(tuning, "set_condition", new[] { reasonCondition });
        MethodInfo getGen = GetMethod(tuning, "get_gen", new[] { reasonGen });
        MethodInfo getCondition = GetMethod(tuning, "get_condition", new[] { reasonCondition });

        init.Invoke(reasons, null);

        object genFinalPerHeap = Enum.Parse(reasonGen, "gen_final_per_heap");
        object joinedAggressive = Enum.Parse(reasonCondition, "gen_joined_aggressive");

        setGen.Invoke(reasons, new object[] { genFinalPerHeap, 2u });
        Assert.Equal(2u, (uint)getGen.Invoke(reasons, new object[] { genFinalPerHeap })!);

        Assert.Equal(0u, (uint)getCondition.Invoke(reasons, new object[] { joinedAggressive })!);
        setCondition.Invoke(reasons, new object[] { joinedAggressive });
        Assert.NotEqual(0u, (uint)getCondition.Invoke(reasons, new object[] { joinedAggressive })!);
    }

    [Fact]
    public static void ServerCondemnationSurfaceIsPresent()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        foreach (string fieldName in new[]
        {
            "condemned_generation_num",
            "blocking_collection",
            "elevation_requested",
            "generation_skip_ratio",
            "last_gc_before_oom",
            "gen_to_condemn_reasons",
            "gc_data_per_heap",
            "bgc_data_per_heap",
        })
        {
            FieldInfo field = heap.GetField(
                fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.False(field.IsStatic);
        }

        foreach (string field in new[]
        {
            "generation_skip_ratio_threshold",
            "trigger_initial_gen2_p",
            "trigger_bgc_for_rethreading_p",
        })
        {
            Assert.NotNull(heap.GetField(
                field,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        }

        foreach (string method in new[]
        {
            "generation_to_condemn",
            "joined_generation_to_condemn",
            "dt_high_frag_p",
            "dt_low_ephemeral_space_p",
            "dt_low_card_table_efficiency_p",
            "dt_estimate_reclaim_space_p",
            "dt_estimate_high_frag_p",
            "get_total_gen_fragmentation",
            "get_total_gen_estimated_reclaim",
            "get_total_gen_size",
            "try_get_new_free_region",
            "estimated_reclaim",
            "ephemeral_gen_fit_p",
        })
        {
            Assert.NotNull(heap.GetMethod(
                method,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        }
    }

    [Fact]
    public static void ServerAllocationStateInitializesGenerationSkipRatio()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        MethodInfo initialize = heap.GetMethod(
            "initialize_server_allocation_state",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

        FieldInfo field = heap.GetField(
            "generation_skip_ratio",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;

        // ldc.i4.s 100 (0x1F 0x64) followed by stfld generation_skip_ratio.
        byte[] il = initialize.GetMethodBody()!.GetILAsByteArray()!;
        byte[] fieldToken = BitConverter.GetBytes(field.MetadataToken);
        bool found = false;
        for (int i = 0; i <= il.Length - 7; i++)
        {
            if (il[i] == 0x1F &&
                il[i + 1] == 0x64 &&
                il[i + 2] == 0x7d &&
                il[i + 3] == fieldToken[0] &&
                il[i + 4] == fieldToken[1] &&
                il[i + 5] == fieldToken[2] &&
                il[i + 6] == fieldToken[3])
            {
                found = true;
                break;
            }
        }

        Assert.True(found, "initialize_server_allocation_state does not set generation_skip_ratio to 100.");
    }

    [Fact]
    public static void GcJoinStageMatchesNativeEnum()
    {
        Type joinStage = GetType("Internal.Runtime.GarbageCollection.gc_join_stage");

        Assert.Equal(0, (int)Enum.Parse(joinStage, "gc_join_init_cpu_mapping"));
        Assert.Equal(2, (int)Enum.Parse(joinStage, "gc_join_generation_determined"));
        Assert.Equal(3, (int)Enum.Parse(joinStage, "gc_join_begin_mark_phase"));
        Assert.Equal(4, (int)Enum.Parse(joinStage, "gc_join_scan_dependent_handles"));
        Assert.Equal(5, (int)Enum.Parse(joinStage, "gc_join_rescan_dependent_handles"));
        Assert.Equal(6, (int)Enum.Parse(joinStage, "gc_join_scan_sizedref_done"));
        Assert.Equal(7, (int)Enum.Parse(joinStage, "gc_join_null_dead_short_weak"));
        Assert.Equal(8, (int)Enum.Parse(joinStage, "gc_join_scan_finalization"));
        Assert.Equal(9, (int)Enum.Parse(joinStage, "gc_join_null_dead_long_weak"));
        Assert.Equal(10, (int)Enum.Parse(joinStage, "gc_join_null_dead_syncblk"));
        Assert.Equal(28, (int)Enum.Parse(joinStage, "gc_r_join_update_card_bundle"));
        Assert.Equal(40, (int)Enum.Parse(joinStage, "gc_join_bridge_processing"));
        Assert.Equal(41, (int)Enum.Parse(joinStage, "gc_join_max"));
    }

    [Fact]
    public static void ServerMarkReconciliationSurfaceIsPresent()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // sync_promoted_bytes and decide_on_promotion_surv walk g_heaps, so they are static.
        MethodInfo sync = heap.GetMethod(
            "sync_promoted_bytes",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            Type.EmptyTypes,
            modifiers: null)!;
        Assert.NotNull(sync);

        MethodInfo decide = heap.GetMethod(
            "decide_on_promotion_surv",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            new[] { typeof(nuint) },
            modifiers: null)!;
        Assert.NotNull(decide);
        Assert.Equal(typeof(bool), decide.ReturnType);

        // In the MULTIPLE_HEAPS build the PER_HEAP_FIELD_SINGLE_GC survivor/promoted counters are
        // instance-owned so each heap's totals can be reconciled across all heaps.
        foreach (string fieldName in new[]
        {
            "survived_per_region",
            "old_card_survived_per_region",
            "total_promoted_bytes",
        })
        {
            FieldInfo field = heap.GetField(
                fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.NotNull(field);
            Assert.False(field.IsStatic);
        }
    }

    private static Type GetType(string name) =>
        s_serverGC.GetType(name, throwOnError: true)!;

    private static MethodInfo GetMethod(Type type, string name, Type[] parameters) =>
        type.GetMethod(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
            binder: null,
            parameters,
            modifiers: null)!;

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
