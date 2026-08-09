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

    [Fact]
    public static void ServerMarkEngineSurfaceIsPresent()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        foreach (string method in new[]
        {
            // Object-walk and marking leaves.
            "method_table",
            "contain_pointers",
            "contain_pointers_or_collectible",
            "go_through_object",
            "go_through_object_nostart",
            "gc_mark",
            "gc_mark1",
            "m_boundary",
            "m_boundary_fullgc",
            "add_to_promoted_bytes",
            "get_promoted_bytes",
            "record_mark_stack_overflow",
            "is_in_gc_range",
            "is_in_condemned_gc",
            "is_in_heap_range",
            "is_in_find_object_range",
            "heap_of",
            "heap_of_gc",
            "find_object",
            // Per-heap mark storage init/cleanup.
            "make_mark_list",
            "initialize_shared_mark_list",
            "destroy_shared_mark_list",
            "make_mark_stack",
            "initialize_mark_stack",
            "reset_mark_stack",
            "reset_pinned_queue",
            "initialize_mark_phase_state",
            "setup_mark_state_for_collection",
            "free_server_mark_storage",
            "get_total_heap_size",
            // Mark queue push/drain/overflow.
            "mark_object_simple",
            "mark_object_simple1",
            "mark_object",
            "mark_through_object",
            "drain_mark_queue",
            "process_mark_overflow",
            "process_mark_overflow_internal",
            // Promotion callbacks and root/finalizer/handle scan entry points.
            "promote",
            "pin_object",
            "mark_phase_scan_roots",
            // Server join boundary wiring.
            "scan_dependent_handles",
        })
        {
            Assert.Contains(
                heap.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static),
                m => m.Name == method);
        }
    }

    [Fact]
    public static void PromoteMatchesNativeCallbackSignature()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        Type scanContext = GetType("Internal.Runtime.GarbageCollection.ScanContext");

        MethodInfo promote = heap.GetMethod(
            "promote",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

        // GCHeap::Promote is passed to GcScanRoots/GcScanHandles as (byte**, ScanContext*, uint).
        Assert.Equal(typeof(void), promote.ReturnType);
        ParameterInfo[] parameters = promote.GetParameters();
        Assert.Equal(3, parameters.Length);
        Assert.True(parameters[0].ParameterType.IsPointer);
        Assert.Equal(scanContext.MakePointerType(), parameters[1].ParameterType);
        Assert.Equal(typeof(uint), parameters[2].ParameterType);
    }

    [Fact]
    public static void PerHeapMarkStateIsInstanceOwned()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // gcpriv.h PER_HEAP_FIELD_SINGLE_GC / MAINTAINED / DIAG_ONLY mark state is instance-owned
        // in the MULTIPLE_HEAPS build so each server heap marks its own portion.
        foreach (string fieldName in new[]
        {
            "mark_queue",
            "mark_stack_tos",
            "mark_stack_bos",
            "oldest_pinned_plug",
            "num_pinned_objects",
            "mark_stack_array",
            "mark_stack_array_length",
            "mark_list",
            "mark_list_index",
            "mark_list_end",
            "min_overflow_address",
            "max_overflow_address",
            "gen0_bricks_cleared",
            "gen0_must_clear_bricks",
        })
        {
            FieldInfo field = heap.GetField(
                fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.NotNull(field);
            Assert.False(field.IsStatic);
        }

        // The mark-list backing is PER_HEAP_ISOLATED (shared across heaps), so it stays static.
        foreach (string fieldName in new[]
        {
            "g_mark_list",
            "g_mark_list_copy",
            "mark_list_size",
            "g_mark_list_total_size",
        })
        {
            FieldInfo field = heap.GetField(
                fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.NotNull(field);
            Assert.True(field.IsStatic);
        }
    }

    [Fact]
    public static void ServerJoinWiringSurfaceIsPresent()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // The gcinternal.h dependent-handle synchronization latches are GC-global statics.
        foreach (string fieldName in new[]
        {
            "s_fUnpromotedHandles",
            "s_fUnscannedPromotions",
            "s_fScanRequired",
        })
        {
            FieldInfo field = heap.GetField(
                fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.NotNull(field);
            Assert.True(field.IsStatic);
        }

        Type scanContext = GetType("Internal.Runtime.GarbageCollection.ScanContext");
        MethodInfo scan = heap.GetMethod(
            "scan_dependent_handles",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        ParameterInfo[] parameters = scan.GetParameters();
        Assert.Equal(4, parameters.Length);
        Assert.Equal(heap.MakePointerType(), parameters[0].ParameterType);
        Assert.Equal(typeof(int), parameters[1].ParameterType);
        Assert.Equal(scanContext.MakePointerType(), parameters[2].ParameterType);
        Assert.Equal(typeof(bool), parameters[3].ParameterType);
    }

    [Fact]
    public static void MarkQueueMethodSurfaceIsPresent()
    {
        Type markQueue = GetType("Internal.Runtime.GarbageCollection.mark_queue_t");

        Assert.NotNull(markQueue.GetMethod(
            "initialize",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        Assert.NotNull(markQueue.GetMethod(
            "get_next_marked",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(markQueue.GetMethod(
            "verify_empty",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));

        MethodInfo[] queueMark = Array.FindAll(
            markQueue.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            static m => m.Name == "queue_mark");
        // The raw form and the (o, condemned_gen) range-checked form.
        Assert.Equal(2, queueMark.Length);
    }

    [Fact]
    public static unsafe void MarkQueueDefersMarkingAndMarksOnEviction()
    {
        // Behaviour test for the unmanaged 16-slot deferred-marking queue: an object pushed into a
        // slot is only marked (and returned) when it is later evicted by a wrap-around push. This
        // exercises the same queue_mark transition the server drain path relies on.
        Type markQueue = GetType("Internal.Runtime.GarbageCollection.mark_queue_t");
        object box = Activator.CreateInstance(markQueue)!;

        MethodInfo queueMark = Array.Find(
            markQueue.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            static m => m.Name == "queue_mark" && m.GetParameters().Length == 1)!;
        Type bytePtrType = queueMark.GetParameters()[0].ParameterType;

        // Two fake objects whose first pointer-sized word is the (unmarked) method-table slot.
        nuint* obj1 = (nuint*)NativeMemory.AllocZeroed((nuint)sizeof(nuint));
        nuint* obj2 = (nuint*)NativeMemory.AllocZeroed((nuint)sizeof(nuint));
        try
        {
            *obj1 = 0x1000; // bit 0 clear == unmarked
            *obj2 = 0x2000;

            object? Push(void* p) =>
                queueMark.Invoke(box, new[] { System.Reflection.Pointer.Box(p, bytePtrType) });

            // First push evicts the empty slot 0 -> returns null.
            Assert.True(IsNullPointer(Push(obj1)));

            // Advance the ring back to slot 0 with 15 empty pushes.
            for (int i = 0; i < 15; i++)
            {
                Assert.True(IsNullPointer(Push(null)));
            }

            // The 17th push at slot 0 evicts obj1, marks it, and returns it.
            object? evicted = Push(obj2);
            Assert.False(IsNullPointer(evicted));
            Assert.Equal((nuint)obj1, (nuint)System.Reflection.Pointer.Unbox(evicted!));
            Assert.Equal((nuint)0x1001, *obj1); // mark bit now set
            Assert.Equal((nuint)0x2000, *obj2); // obj2 only queued, not yet marked
        }
        finally
        {
            NativeMemory.Free(obj1);
            NativeMemory.Free(obj2);
        }
    }

    [Fact]
    public static void ServerMarkPhaseDriverSurfaceIsPresent()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // The blocking mark_phase driver plus the leaves it adds around the reused mark core.
        foreach (string method in new[]
        {
            "mark_phase",
            "fire_mark_event",
            "save_current_survived",
            "update_old_card_survived",
        })
        {
            Assert.Contains(
                heap.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static),
                m => m.Name == method);
        }

        // mark_phase (gc_heap*, int) -> void, exactly as gc_heap::mark_phase (int condemned).
        MethodInfo markPhase = heap.GetMethod(
            "mark_phase",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(void), markPhase.ReturnType);
        ParameterInfo[] parameters = markPhase.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(heap.MakePointerType(), parameters[0].ParameterType);
        Assert.Equal(typeof(int), parameters[1].ParameterType);

        // mark_phase.cpp declares syncblock_scan_p as a function-static volatile int32.
        FieldInfo syncblock = heap.GetField(
            "syncblock_scan_p",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(syncblock);
        Assert.True(syncblock.IsStatic);
        Assert.Equal(typeof(int), syncblock.FieldType);

        FieldInfo finalizationPromoted = heap.GetField(
            "finalization_promoted_bytes",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.False(finalizationPromoted.IsStatic);
        Assert.Equal(typeof(nuint), finalizationPromoted.FieldType);
    }

    [Fact]
    public static void ServerMarkPhaseDriverRunsFullJoinSequence()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        MethodInfo markPhase = heap.GetMethod(
            "mark_phase",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

        (string[] names, int joinCount) = CollectCallTargets(markPhase, "join");

        // gc_join_begin_mark_phase, gc_join_scan_sizedref_done, gc_join_null_dead_short_weak,
        // gc_join_scan_finalization, gc_join_null_dead_long_weak, gc_join_null_dead_syncblk.
        Assert.Equal(6, joinCount);

        foreach (string expected in new[]
        {
            // The join lifecycle.
            "joined",
            "restart",
            // Range/mark-state setup in the joined begin_mark_phase region and per heap.
            "get_used_region_count",
            "grow_mark_list_piece",
            "compute_gc_and_ephemeral_range",
            "BeforeGcScanRoots",
            "setup_mark_state_for_collection",
            // Root, finalizer, and handle scans plus their drains and mark-event fires.
            "GcScanSizedRefs",
            "GcScanRoots",
            "GcScanHandles",
            "drain_mark_queue",
            "fire_mark_event",
            // The !full_p survivor bookkeeping that brackets the deferred card scan.
            "save_current_survived",
            "update_old_card_survived",
            // Dependent handles, short/long weak, single-thread syncblk, finalization.
            "GcDhInitialScan",
            "scan_dependent_handles",
            "AfterGcScanRoots",
            "GcShortWeakPtrScan",
            "ScanForFinalization",
            "DiagWalkFReachableObjects",
            "GcWeakPtrScan",
            "GcWeakPtrScanBySingleThread",
            // Cross-heap reconciliation, region/mark-list balancing, and the promotion decision in
            // the joined tail and per-heap sort.
            "sync_promoted_bytes",
            "equalize_promoted_bytes",
            "sort_mark_list",
            "decide_on_promotion_surv",
        })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public static void ServerMarkPhaseDefersBalancingAndCardScan()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        MethodInfo[] methods =
            heap.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        // The cross-heap mark-list / promoted-byte balancing is now translated and wired into the
        // driver, but merge_mark_lists (#if MULTIPLE_HEAPS && !USE_REGIONS), mark_steal
        // (MH_SC_MARK), and the server cross-generation card scan remain deferred, so no method for
        // them is ported yet and the driver cannot reference one.
        foreach (string deferred in new[]
        {
            "merge_mark_lists",
            "mark_steal",
            "mark_through_cards_for_segments",
            "mark_through_cards_for_uoh_objects",
        })
        {
            Assert.DoesNotContain(methods, m => m.Name == deferred);
        }
    }

    [Fact]
    public static void ServerMarkListBalancingSurfaceIsPresent()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        MethodInfo[] statics =
            heap.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        // The mark-list balancing leaves plus the promoted-byte balancing and its region-threading
        // helpers are now present in the server build.
        foreach (string method in new[]
        {
            "target_mark_count_for_heap",
            "equalize_mark_lists",
            "sort_mark_list",
            "append_to_mark_list",
            "equalize_promoted_bytes",
            "set_heap_for_contained_basic_regions",
            "unlink_first_rw_region",
            "thread_rw_region_front",
            "thread_start_region",
        })
        {
            Assert.Contains(statics, m => m.Name == method);
        }

        // sort_mark_list (gc_heap*) -> nuint, exactly as size_t gc_heap::sort_mark_list().
        MethodInfo sort = heap.GetMethod(
            "sort_mark_list",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(nuint), sort.ReturnType);
        Assert.Equal(heap.MakePointerType(), sort.GetParameters()[0].ParameterType);

        // equalize_promoted_bytes (gc_heap*, int) -> void, exactly as
        // gc_heap::equalize_promoted_bytes (int condemned_gen_number).
        MethodInfo equalize = heap.GetMethod(
            "equalize_promoted_bytes",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(void), equalize.ReturnType);
        Assert.Equal(2, equalize.GetParameters().Length);
        Assert.Equal(heap.MakePointerType(), equalize.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(int), equalize.GetParameters()[1].ParameterType);

        // The per-region mark-list pieces are PER_HEAP_FIELD_SINGLE_GC, so instance-owned in the
        // MULTIPLE_HEAPS build alongside mark_list itself.
        foreach (string field in new[] { "mark_list_piece_start", "mark_list_piece_end" })
        {
            FieldInfo info = heap.GetField(
                field,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.NotNull(info);
            Assert.False(info.IsStatic);
        }
    }

    [Fact]
    public static void ServerTargetMarkCountSplitsEvenlyWithRemainderOnLastHeap()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        MethodInfo target = heap.GetMethod(
            "target_mark_count_for_heap",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        // total 10 across 4 heaps: average 2, remainder 2 all folded into the last heap.
        Assert.Equal((nuint)2, Invoke(target, (nuint)10, 4, 0));
        Assert.Equal((nuint)2, Invoke(target, (nuint)10, 4, 1));
        Assert.Equal((nuint)2, Invoke(target, (nuint)10, 4, 2));
        Assert.Equal((nuint)4, Invoke(target, (nuint)10, 4, 3));

        // an exact split leaves no remainder on the last heap.
        Assert.Equal((nuint)2, Invoke(target, (nuint)8, 4, 3));

        // the per-heap targets always sum back to the total.
        nuint sum = 0;
        for (int h = 0; h < 3; h++)
        {
            sum += Invoke(target, (nuint)7, 3, h);
        }

        Assert.Equal((nuint)7, sum);

        static nuint Invoke(MethodInfo method, nuint total, int count, int number) =>
            (nuint)method.Invoke(null, new object[] { total, count, number })!;
    }

    // Walk a method body collecting the simple names of its call/callvirt/newobj targets, and count
    // how many of them have a given name (used to count gc_t_join.join sites). Tokens are resolved
    // through the module so stray operand bytes that look like call opcodes are discarded.
    private static (string[] Names, int NamedCount) CollectCallTargets(MethodInfo method, string countName)
    {
        byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
        Module module = method.Module;
        var names = new System.Collections.Generic.HashSet<string>();
        int namedCount = 0;
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28 && il[i] != 0x6F && il[i] != 0x73)
            {
                continue;
            }

            int token = BitConverter.ToInt32(il, i + 1);
            MethodBase resolved;
            try
            {
                resolved = module.ResolveMethod(token);
            }
            catch
            {
                continue;
            }

            if (resolved is null)
            {
                continue;
            }

            names.Add(resolved.Name);
            if (resolved.Name == countName)
            {
                namedCount++;
            }
        }

        var array = new string[names.Count];
        names.CopyTo(array);
        return (array, namedCount);
    }

    private static unsafe bool IsNullPointer(object? boxedPointer) =>
        boxedPointer is null || System.Reflection.Pointer.Unbox(boxedPointer) is null;

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
