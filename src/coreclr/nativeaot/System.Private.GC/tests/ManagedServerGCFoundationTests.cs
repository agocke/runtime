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
            // The !full_p survivor bookkeeping that brackets the cross-generation card scan.
            "save_current_survived",
            "mark_through_cards_for_segments",
            "mark_through_cards_for_uoh_objects",
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
    public static void ServerMarkPhaseDefersRemainingBalancing()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        MethodInfo[] methods =
            heap.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        // The cross-heap mark-list / promoted-byte balancing and the cross-generation card scan are
        // now translated and wired into the driver, but merge_mark_lists
        // (#if MULTIPLE_HEAPS && !USE_REGIONS) and mark_steal (MH_SC_MARK) remain deferred, so no
        // method for them is ported yet and the driver cannot reference one.
        foreach (string deferred in new[]
        {
            "merge_mark_lists",
            "mark_steal",
        })
        {
            Assert.DoesNotContain(methods, m => m.Name == deferred);
        }
    }

    [Fact]
    public static void ServerCardScanSurfaceIsPresent()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // mark_through_cards_for_segments (gc_heap*, bool) -> void, exactly as
        // gc_heap::mark_through_cards_for_segments (card_fn, relocating) with the non-stealing
        // signature (FEATURE_CARD_MARKING_STEALING is not defined for this port).
        MethodInfo soh = heap.GetMethod(
            "mark_through_cards_for_segments",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(void), soh.ReturnType);
        Assert.Equal(2, soh.GetParameters().Length);
        Assert.Equal(heap.MakePointerType(), soh.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(bool), soh.GetParameters()[1].ParameterType);

        // mark_through_cards_for_uoh_objects (gc_heap*, int gen_number, bool) -> void.
        MethodInfo uoh = heap.GetMethod(
            "mark_through_cards_for_uoh_objects",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(void), uoh.ReturnType);
        Assert.Equal(3, uoh.GetParameters().Length);
        Assert.Equal(heap.MakePointerType(), uoh.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(int), uoh.GetParameters()[1].ParameterType);
        Assert.Equal(typeof(bool), uoh.GetParameters()[2].ParameterType);

        // The internal per-segment scan and the UOH object finder that back the two entry points.
        foreach (string helper in new[] { "scan_cards_for_segment", "find_uoh_object_for_card" })
        {
            Assert.Contains(
                heap.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static),
                m => m.Name == helper);
        }

        // mark_through_cards_for_segments walks find_card / find_first_object over each set card's
        // objects, promotes cross-generation children through mark_object_simple, and clears cards
        // that no longer point across generations.
        (string[] names, _) = CollectCallTargets(soh, "scan_cards_for_segment");
        foreach (string expected in new[]
        {
            "scan_cards_for_segment",
            "generation_of",
            "generation_start_segment_rw",
            "heap_segment_next",
        })
        {
            Assert.Contains(expected, names);
        }

        (string[] scanNames, _) = CollectCallTargets(
            heap.GetMethod(
                "scan_cards_for_segment",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!,
            "find_card");
        foreach (string expected in new[]
        {
            "find_card",
            "find_first_object",
            "find_uoh_object_for_card",
            "go_through_object",
            "clear_cards",
            "should_check_bgc_mark",
            "fgc_should_consider_object",
        })
        {
            Assert.Contains(expected, scanNames);
        }
    }

    [Fact]
    public static void ServerBackgroundSweepStateIsInstanceOwnedExceptSharedPhase()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // gcpriv.h PER_HEAP_ISOLATED_FIELD_SINGLE_GC current_c_gc_state is shared/static.
        FieldInfo phase = heap.GetField(
            "current_c_gc_state",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(phase);
        Assert.True(phase.IsStatic);
        Assert.Equal(
            GetType("Internal.Runtime.GarbageCollection.c_gc_state"),
            phase.FieldType);

        // gcpriv.h PER_HEAP_FIELD_SINGLE_GC current_sweep_pos / current_sweep_seg are instance-owned
        // in the MULTIPLE_HEAPS build so each server heap tracks its own sweep progress.
        foreach (string field in new[] { "current_sweep_pos", "current_sweep_seg" })
        {
            FieldInfo info = heap.GetField(
                field,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.NotNull(info);
            Assert.False(info.IsStatic);
        }

        // The sweep/mark-state predicates the card scan consults.
        MethodInfo shouldCheck = heap.GetMethod(
            "should_check_bgc_mark",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(void), shouldCheck.ReturnType);
        ParameterInfo[] shouldCheckParams = shouldCheck.GetParameters();
        Assert.Equal(4, shouldCheckParams.Length);
        Assert.Equal(heap.MakePointerType(), shouldCheckParams[0].ParameterType);
        Assert.True(shouldCheckParams[2].IsOut);
        Assert.True(shouldCheckParams[3].IsOut);

        MethodInfo fgc = heap.GetMethod(
            "fgc_should_consider_object",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(bool), fgc.ReturnType);
        Assert.Equal(5, fgc.GetParameters().Length);
        Assert.Equal(heap.MakePointerType(), fgc.GetParameters()[0].ParameterType);
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

    [Fact]
    public static void ServerPlanCompactionDecisionSurfaceIsPresent()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        Type gen = GetType("Internal.Runtime.GarbageCollection.generation");
        Type mark = GetType("Internal.Runtime.GarbageCollection.mark");
        MethodInfo[] statics =
            heap.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        // The plan-phase compaction-vs-sweep decision leaves and the plan-size / pinned-plug helpers
        // they consume are all present in the server build.
        foreach (string method in new[]
        {
            "generation_plan_size",
            "generation_sizes",
            "generation_fragmentation",
            "approximate_new_allocation",
            "get_gen0_end_plan_space",
            "decide_on_compaction_space",
            "is_full_compacting_gc_productive",
            "ensure_gap_allocation",
            "decide_on_compacting",
            "pinned_plug_of",
            "pinned_len",
        })
        {
            Assert.Contains(statics, m => m.Name == method);
        }

        // decide_on_compacting (gc_heap*, int, nuint, ref bool) -> bool, exactly as
        // BOOL gc_heap::decide_on_compacting (int condemned_gen_number, size_t fragmentation,
        // BOOL& should_expand).
        MethodInfo decide = heap.GetMethod(
            "decide_on_compacting",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(bool), decide.ReturnType);
        ParameterInfo[] decideParams = decide.GetParameters();
        Assert.Equal(4, decideParams.Length);
        Assert.Equal(heap.MakePointerType(), decideParams[0].ParameterType);
        Assert.Equal(typeof(int), decideParams[1].ParameterType);
        Assert.Equal(typeof(nuint), decideParams[2].ParameterType);
        Assert.True(decideParams[3].ParameterType.IsByRef);
        Assert.Equal(typeof(bool), decideParams[3].ParameterType.GetElementType());

        // decide_on_compaction_space (gc_heap*) -> bool and is_full_compacting_gc_productive
        // (gc_heap*) -> bool.
        MethodInfo space = heap.GetMethod(
            "decide_on_compaction_space",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(bool), space.ReturnType);
        Assert.Single(space.GetParameters());
        Assert.Equal(heap.MakePointerType(), space.GetParameters()[0].ParameterType);

        MethodInfo productive = heap.GetMethod(
            "is_full_compacting_gc_productive",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(bool), productive.ReturnType);
        Assert.Single(productive.GetParameters());

        // generation_fragmentation (gc_heap*, generation*, generation*, byte*) -> nuint.
        MethodInfo frag = heap.GetMethod(
            "generation_fragmentation",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(nuint), frag.ReturnType);
        ParameterInfo[] fragParams = frag.GetParameters();
        Assert.Equal(4, fragParams.Length);
        Assert.Equal(heap.MakePointerType(), fragParams[0].ParameterType);
        Assert.Equal(gen.MakePointerType(), fragParams[1].ParameterType);
        Assert.Equal(gen.MakePointerType(), fragParams[2].ParameterType);
        Assert.Equal(typeof(byte).MakePointerType(), fragParams[3].ParameterType);

        // pinned_plug_of (gc_heap*, nuint) -> mark* and pinned_len (mark*) -> ref nuint.
        MethodInfo plugOf = heap.GetMethod(
            "pinned_plug_of",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(mark.MakePointerType(), plugOf.ReturnType);
        Assert.Equal(heap.MakePointerType(), plugOf.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(nuint), plugOf.GetParameters()[1].ParameterType);

        MethodInfo len = heap.GetMethod(
            "pinned_len",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.True(len.ReturnType.IsByRef);
        Assert.Equal(typeof(nuint), len.ReturnType.GetElementType());
        Assert.Equal(mark.MakePointerType(), len.GetParameters()[0].ParameterType);
    }

    [Fact]
    public static void ServerDecideOnCompactingCallsClosedLeaves()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        MethodInfo decide = heap.GetMethod(
            "decide_on_compacting",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

        // decide_on_compacting weighs the planned fragmentation against the compaction-space and
        // productivity deciders, the high-memory reclaim thresholds, and the gap-allocation gate,
        // recording its compaction reason through the per-heap gc_data_per_heap.
        (string[] names, _) = CollectCallTargets(decide, "decide_on_compaction_space");
        foreach (string expected in new[]
        {
            "generation_sizes",
            "decide_on_compaction_space",
            "is_full_compacting_gc_productive",
            "ensure_gap_allocation",
            "generation_size",
            "generation_plan_size",
            "min_high_fragmentation_threshold",
            "min_reclaim_fragmentation_threshold",
            "get_gc_data_per_heap",
            "set_mechanism",
        })
        {
            Assert.Contains(expected, names);
        }

        // decide_on_compaction_space consults the new-allocation estimate, the sufficient-space
        // predicate, the plan-space accumulator, and the basic free-region count.
        MethodInfo space = heap.GetMethod(
            "decide_on_compaction_space",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] spaceNames, _) = CollectCallTargets(space, "sufficient_space_regions");
        foreach (string expected in new[]
        {
            "approximate_new_allocation",
            "sufficient_space_regions",
            "get_gen0_end_plan_space",
            "get_num_free_regions",
        })
        {
            Assert.Contains(expected, spaceNames);
        }
    }

    [Fact]
    public static void ServerPlanSpaceFieldsAreInstanceOwned()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // gcpriv.h PER_HEAP_FIELD_SINGLE_GC[_ALLOC] plan-space accounting is instance-owned in the
        // MULTIPLE_HEAPS build so each server heap decides on its own portion during the plan phase.
        foreach (string field in new[]
        {
            "num_regions_freed_in_sweep",
            "end_gen0_region_space",
            "end_gen0_region_committed_space",
            "gen0_pinned_free_space",
            "gen0_large_chunk_found",
            "sufficient_gen0_space_p",
        })
        {
            FieldInfo info = heap.GetField(
                field,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.NotNull(info);
            Assert.False(info.IsStatic);

            Assert.Null(heap.GetField(
                field,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        }
    }

    [Fact]
    public static void ServerPlanRegionLoopSurfaceIsPresent()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        Type gen = GetType("Internal.Runtime.GarbageCollection.generation");
        Type mark = GetType("Internal.Runtime.GarbageCollection.mark");
        Type segment = GetType("Internal.Runtime.GarbageCollection.heap_segment");
        MethodInfo[] statics =
            heap.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        // The per-heap plug/region planning helpers that produce the plan-allocated bounds the
        // compaction-vs-sweep deciders consume are all present in the server build.
        foreach (string method in new[]
        {
            "pinned_plug_que_empty_p",
            "oldest_pin",
            "deque_pinned_plug",
            "set_new_pin_info",
            "find_next_marked",
            "save_allocated",
            "update_planned_gen0_free_space",
            "attribute_pin_higher_gen_alloc",
            "decide_on_gen1_pin_promotion",
            "skip_pins_in_alloc_region",
            "decide_on_demotion_pin_surv",
            "should_sweep_in_plan",
            "sweep_region_in_plan",
            "process_last_np_surv_region",
            "process_remaining_regions",
            "clear_gen1_cards",
            "init_records",
        })
        {
            Assert.Contains(statics, m => m.Name == method);
        }

        // should_sweep_in_plan (gc_heap*, heap_segment*) -> bool.
        MethodInfo sip = heap.GetMethod(
            "should_sweep_in_plan",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(bool), sip.ReturnType);
        ParameterInfo[] sipParams = sip.GetParameters();
        Assert.Equal(2, sipParams.Length);
        Assert.Equal(heap.MakePointerType(), sipParams[0].ParameterType);
        Assert.Equal(segment.MakePointerType(), sipParams[1].ParameterType);

        // sweep_region_in_plan (gc_heap*, heap_segment*, int, ref byte**, byte**) -> void, exactly
        // as void gc_heap::sweep_region_in_plan (heap_segment* region, BOOL use_mark_list,
        // uint8_t**& mark_list_next, uint8_t** mark_list_index).
        MethodInfo sweep = heap.GetMethod(
            "sweep_region_in_plan",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(void), sweep.ReturnType);
        ParameterInfo[] sweepParams = sweep.GetParameters();
        Assert.Equal(5, sweepParams.Length);
        Assert.Equal(heap.MakePointerType(), sweepParams[0].ParameterType);
        Assert.Equal(segment.MakePointerType(), sweepParams[1].ParameterType);
        Assert.Equal(typeof(int), sweepParams[2].ParameterType);
        Assert.True(sweepParams[3].ParameterType.IsByRef);
        Assert.Equal(typeof(byte).MakePointerType().MakePointerType(), sweepParams[4].ParameterType);

        // process_remaining_regions (gc_heap*, int, generation*) -> void.
        MethodInfo prr = heap.GetMethod(
            "process_remaining_regions",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(void), prr.ReturnType);
        ParameterInfo[] prrParams = prr.GetParameters();
        Assert.Equal(3, prrParams.Length);
        Assert.Equal(heap.MakePointerType(), prrParams[0].ParameterType);
        Assert.Equal(typeof(int), prrParams[1].ParameterType);
        Assert.Equal(gen.MakePointerType(), prrParams[2].ParameterType);

        // process_last_np_surv_region (gc_heap*, generation*, int, int) -> void.
        MethodInfo plns = heap.GetMethod(
            "process_last_np_surv_region",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(void), plns.ReturnType);
        ParameterInfo[] plnsParams = plns.GetParameters();
        Assert.Equal(4, plnsParams.Length);
        Assert.Equal(heap.MakePointerType(), plnsParams[0].ParameterType);
        Assert.Equal(gen.MakePointerType(), plnsParams[1].ParameterType);
        Assert.Equal(typeof(int), plnsParams[2].ParameterType);
        Assert.Equal(typeof(int), plnsParams[3].ParameterType);

        // The pinned-queue consumers take the heap so they consume that heap's own mark_stack queue:
        // pinned_plug_que_empty_p (gc_heap*) -> int, deque_pinned_plug (gc_heap*) -> nuint,
        // oldest_pin (gc_heap*) -> mark*, set_new_pin_info (mark*, byte*) -> void.
        MethodInfo empty = heap.GetMethod(
            "pinned_plug_que_empty_p",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(int), empty.ReturnType);
        Assert.Equal(heap.MakePointerType(), empty.GetParameters()[0].ParameterType);

        MethodInfo deque = heap.GetMethod(
            "deque_pinned_plug",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(nuint), deque.ReturnType);
        Assert.Equal(heap.MakePointerType(), deque.GetParameters()[0].ParameterType);

        MethodInfo oldest = heap.GetMethod(
            "oldest_pin",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(mark.MakePointerType(), oldest.ReturnType);
        Assert.Equal(heap.MakePointerType(), oldest.GetParameters()[0].ParameterType);
    }

    [Fact]
    public static void ServerProcessRemainingRegionsCallsClosedLeaves()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // process_remaining_regions consumes the pinned-plug queue in address order, decides the plan
        // generation of each remaining region, and asks for new regions (falling back to special
        // sweep) so every condemned generation ends up with at least one region.
        MethodInfo prr = heap.GetMethod(
            "process_remaining_regions",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] prrNames, _) = CollectCallTargets(prr, "deque_pinned_plug");
        foreach (string expected in new[]
        {
            "pinned_plug_que_empty_p",
            "oldest_pin",
            "deque_pinned_plug",
            "set_new_pin_info",
            "update_planned_gen0_free_space",
            "decide_on_demotion_pin_surv",
            "decide_on_gen1_pin_promotion",
            "skip_pins_in_alloc_region",
            "heap_segment_next_non_sip",
            "heap_segment_non_sip",
            "get_new_region",
        })
        {
            Assert.Contains(expected, prrNames);
        }

        // sweep_region_in_plan rebuilds the region's free list from its unmarked gaps and records its
        // final allocated / plan-allocated bounds.
        MethodInfo sweep = heap.GetMethod(
            "sweep_region_in_plan",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] sweepNames, _) = CollectCallTargets(sweep, "find_next_marked");
        foreach (string expected in new[]
        {
            "set_region_sweep_in_plan",
            "find_next_marked",
            "make_unused_array",
            "fix_brick_to_highest",
            "save_allocated",
        })
        {
            Assert.Contains(expected, sweepNames);
        }

        // should_sweep_in_plan reaches the owning heap's SIP counters and reserved free region, and
        // reads survival ratios to decide whether a region is swept in place.
        MethodInfo sip = heap.GetMethod(
            "should_sweep_in_plan",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] sipNames, _) = CollectCallTargets(sip, "get_free_region");
        foreach (string expected in new[]
        {
            "get_region_gen_num",
            "get_plan_gen_num",
            "set_region_plan_gen_num",
            "get_free_region",
            "reserved_free_region_sip",
        })
        {
            Assert.Contains(expected, sipNames);
        }
    }

    [Fact]
    public static void ServerPlanRegionFieldsAreInstanceOwned()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // gcpriv.h PER_HEAP_FIELD_SINGLE_GC / PER_HEAP_FIELD_DIAG_ONLY region-planning state is
        // instance-owned in the MULTIPLE_HEAPS build so each server heap plans its own condemned
        // regions.
        foreach (string field in new[]
        {
            "reserved_free_regions_sip",
            "regions_per_gen",
            "planned_regions_per_gen",
            "sip_maxgen_regions_per_gen",
            "decide_promote_gen1_pins_p",
            "special_sweep_p",
            "maxgen_pinned_compact_before_advance",
            "new_gen0_regions_in_plns",
            "new_regions_in_prr",
            "fgm_result",
        })
        {
            FieldInfo info = heap.GetField(
                field,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.NotNull(info);
            Assert.False(info.IsStatic);

            Assert.Null(heap.GetField(
                field,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        }

        // enable_special_regions_p is PER_HEAP_ISOLATED_FIELD_INIT_ONLY, so it stays static.
        FieldInfo special = heap.GetField(
            "enable_special_regions_p",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(special);
        Assert.True(special.IsStatic);
    }

    [Fact]
    public static unsafe void ServerPinnedPlugReturnsPlugAddress()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        Type markType = GetType("Internal.Runtime.GarbageCollection.mark");

        // gcinternal.h pinned_plug(m) returns m->first (the plug address), not first + len. Confirm
        // the server helper matches by reading a synthetic mark back through the collector.
        int markSize = Marshal.SizeOf(markType);
        IntPtr buffer = Marshal.AllocHGlobal(markSize);
        try
        {
            for (int i = 0; i < markSize; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }

            FieldInfo firstField = markType.GetField(
                "first",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            FieldInfo lenField = markType.GetField(
                "len",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;

            var plug = (IntPtr)0x4000;
            var len = (IntPtr)0x200;
            Marshal.WriteIntPtr(buffer + (int)Marshal.OffsetOf(markType, firstField.Name), plug);
            Marshal.WriteIntPtr(buffer + (int)Marshal.OffsetOf(markType, lenField.Name), len);

            MethodInfo pinnedPlug = heap.GetMethod(
                "pinned_plug",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
            object result = pinnedPlug.Invoke(null, new object[] { Pointer.Box((void*)buffer, markType.MakePointerType()) })!;
            Assert.Equal(plug, (IntPtr)Pointer.Unbox(result));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public static void ServerPlanBrickThreadingSurfaceIsPresent()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        Type gen = GetType("Internal.Runtime.GarbageCollection.generation");
        MethodInfo[] statics =
            heap.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        // The brick-tree threading and pinned-plug-queue write leaves the plan-phase driver invokes
        // are all present in the server build.
        foreach (string method in new[]
        {
            "oddp",
            "logcount",
            "insert_node",
            "update_brick_table",
            "clear_special_bits",
            "set_special_bits",
            "grow_mark_stack",
            "convert_to_pinned_plug",
            "enque_pinned_plug",
            "save_post_plug_info",
            "store_plug_gap_info",
            "set_allocator_next_pin",
            "set_pinned_info",
            "merge_with_last_pinned_plug",
        })
        {
            Assert.Contains(statics, m => m.Name == method);
        }

        Type byteType = typeof(byte);
        Type bytePtr = byteType.MakePointerType();

        // insert_node (byte*, nuint, byte*, byte*) -> byte*, as uint8_t* gc_heap::insert_node.
        MethodInfo insert = heap.GetMethod(
            "insert_node",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(bytePtr, insert.ReturnType);
        ParameterInfo[] insertParams = insert.GetParameters();
        Assert.Equal(4, insertParams.Length);
        Assert.Equal(bytePtr, insertParams[0].ParameterType);
        Assert.Equal(typeof(nuint), insertParams[1].ParameterType);
        Assert.Equal(bytePtr, insertParams[2].ParameterType);
        Assert.Equal(bytePtr, insertParams[3].ParameterType);

        // update_brick_table (byte*, nuint, byte*, byte*) -> nuint, as size_t gc_heap::update_brick_table.
        MethodInfo ubt = heap.GetMethod(
            "update_brick_table",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(nuint), ubt.ReturnType);
        Assert.Equal(4, ubt.GetParameters().Length);

        // enque_pinned_plug (gc_heap*, byte*, int, byte*) -> void, reaching this heap's own queue.
        MethodInfo enque = heap.GetMethod(
            "enque_pinned_plug",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(void), enque.ReturnType);
        ParameterInfo[] enqueParams = enque.GetParameters();
        Assert.Equal(4, enqueParams.Length);
        Assert.Equal(heap.MakePointerType(), enqueParams[0].ParameterType);
        Assert.Equal(bytePtr, enqueParams[1].ParameterType);
        Assert.Equal(typeof(int), enqueParams[2].ParameterType);
        Assert.Equal(bytePtr, enqueParams[3].ParameterType);

        // save_post_plug_info (gc_heap*, byte*, byte*, byte*) -> void.
        MethodInfo savePost = heap.GetMethod(
            "save_post_plug_info",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(void), savePost.ReturnType);
        Assert.Equal(4, savePost.GetParameters().Length);
        Assert.Equal(heap.MakePointerType(), savePost.GetParameters()[0].ParameterType);

        // store_plug_gap_info (gc_heap*, byte*, byte*, ref int, ref int, ref byte*, ref int, byte*,
        // ref int, nuint) -> void, matching void gc_heap::store_plug_gap_info.
        MethodInfo store = heap.GetMethod(
            "store_plug_gap_info",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(void), store.ReturnType);
        ParameterInfo[] storeParams = store.GetParameters();
        Assert.Equal(10, storeParams.Length);
        Assert.Equal(heap.MakePointerType(), storeParams[0].ParameterType);
        Assert.True(storeParams[3].ParameterType.IsByRef);
        Assert.True(storeParams[5].ParameterType.IsByRef);
        Assert.Equal(typeof(nuint), storeParams[9].ParameterType);

        // set_allocator_next_pin (gc_heap*, generation*) -> void.
        MethodInfo sanp = heap.GetMethod(
            "set_allocator_next_pin",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(void), sanp.ReturnType);
        ParameterInfo[] sanpParams = sanp.GetParameters();
        Assert.Equal(2, sanpParams.Length);
        Assert.Equal(heap.MakePointerType(), sanpParams[0].ParameterType);
        Assert.Equal(gen.MakePointerType(), sanpParams[1].ParameterType);
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, true)]
    [InlineData(2u, false)]
    [InlineData(255u, true)]
    [InlineData(256u, false)]
    public static void ServerOddpMatchesParity(uint value, bool expected)
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        MethodInfo oddp = heap.GetMethod(
            "oddp",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        object result = oddp.Invoke(null, new object[] { (nuint)value })!;
        Assert.Equal(expected, (bool)result);
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(1u, 1u)]
    [InlineData(0xFFFFu, 16u)]
    [InlineData(0x8001u, 2u)]
    [InlineData(0x0F0Fu, 8u)]
    public static void ServerLogcountCountsHighBits(uint word, uint expected)
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        MethodInfo logcount = heap.GetMethod(
            "logcount",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        object result = logcount.Invoke(null, new object[] { (nuint)word })!;
        Assert.Equal((nuint)expected, (nuint)result);
    }

    [Fact]
    public static void ServerInsertNodeThreadsBrickTree()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // insert_node links each plug into the balanced brick tree using power_of_two_p / oddp /
        // logcount and the shared node-child offset accessors.
        MethodInfo insert = heap.GetMethod(
            "insert_node",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] names, _) = CollectCallTargets(insert, "logcount");
        foreach (string expected in new[]
        {
            "power_of_two_p",
            "oddp",
            "logcount",
            "node_right_child",
            "set_node_left_child",
            "set_node_right_child",
        })
        {
            Assert.Contains(expected, names);
        }

        // update_brick_table publishes the tree and fills the intervening bricks.
        MethodInfo ubt = heap.GetMethod(
            "update_brick_table",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] ubtNames, _) = CollectCallTargets(ubt, "set_brick");
        foreach (string expected in new[] { "set_brick", "brick_address", "brick_of" })
        {
            Assert.Contains(expected, ubtNames);
        }
    }

    [Fact]
    public static void ServerPinnedQueueWritersCallClosedLeaves()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // store_plug_gap_info records the gap size, enqueues / post-annotates the pin, and remembers
        // the gen2 free-list pin index (DOUBLY_LINKED_FL) through the owning heap.
        MethodInfo store = heap.GetMethod(
            "store_plug_gap_info",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] storeNames, _) = CollectCallTargets(store, "enque_pinned_plug");
        foreach (string expected in new[]
        {
            "set_gap_size",
            "enque_pinned_plug",
            "save_post_plug_info",
            "generation_last_free_list_allocated",
        })
        {
            Assert.Contains(expected, storeNames);
        }

        // enque_pinned_plug grows this heap's queue and snapshots the pre-plug info, marking the
        // short-object reference bits through go_through_object_nostart.
        MethodInfo enque = heap.GetMethod(
            "enque_pinned_plug",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] enqueNames, _) = CollectCallTargets(enque, "grow_mark_stack");
        foreach (string expected in new[]
        {
            "grow_mark_stack",
            "clear_special_bits",
            "set_special_bits",
            "contain_pointers",
            "go_through_object_nostart",
        })
        {
            Assert.Contains(expected, enqueNames);
        }

        // set_allocator_next_pin caps the allocation limit at the oldest queued pin.
        MethodInfo sanp = heap.GetMethod(
            "set_allocator_next_pin",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] sanpNames, _) = CollectCallTargets(sanp, "oldest_pin");
        foreach (string expected in new[] { "pinned_plug_que_empty_p", "oldest_pin", "pinned_plug" })
        {
            Assert.Contains(expected, sanpNames);
        }
    }

    [Fact]
    public static void ServerSavedPinnedPlugIndexIsInstanceOwned()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // gcpriv.h marks saved_pinned_plug_index PER_HEAP_FIELD_SINGLE_GC, so it is instance-owned in
        // the MULTIPLE_HEAPS build; store_plug_gap_info records the pin index through the owning heap.
        FieldInfo instance = heap.GetField(
            "saved_pinned_plug_index",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.NotNull(instance);
        Assert.False(instance.IsStatic);

        Assert.Null(heap.GetField(
            "saved_pinned_plug_index",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
    }

    [Fact]
    public static unsafe void ServerObjectHeaderSpecialBitsRoundTrip()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // gc.cpp clear_special_bits strips the lower method-table bits and returns them;
        // set_special_bits restores them. A method-table word with GC_MARKED |
        // MAKE_FREE_OBJ_IN_COMPACT set must round-trip through the pair, and the stripped word must
        // have no special bits left. The static wrappers take the object address directly.
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(nuint));
        try
        {
            const nuint markedBits = 0x1 | 0x4; // GC_MARKED | MAKE_FREE_OBJ_IN_COMPACT
            var basePtr = (nuint)0x40000;
            Marshal.WriteIntPtr(buffer, (IntPtr)(nint)(basePtr | markedBits));

            MethodInfo clear = heap.GetMethod(
                "clear_special_bits",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
            MethodInfo set = heap.GetMethod(
                "set_special_bits",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

            Type bytePtr = typeof(byte).MakePointerType();
            object removed = clear.Invoke(
                null,
                new object[] { Pointer.Box((void*)buffer, bytePtr) })!;
            Assert.Equal(markedBits, (nuint)removed);
            Assert.Equal((nuint)basePtr, (nuint)(nint)Marshal.ReadIntPtr(buffer));

            set.Invoke(
                null,
                new object[] { Pointer.Box((void*)buffer, bytePtr), (nuint)removed });
            Assert.Equal(basePtr | markedBits, (nuint)(nint)Marshal.ReadIntPtr(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public static void ServerAllocateInCondemnedGenerationSurfaceIsPresent()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        Type gen = GetType("Internal.Runtime.GarbageCollection.generation");
        Type segment = GetType("Internal.Runtime.GarbageCollection.heap_segment");
        MethodInfo[] statics =
            heap.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        // The condemned-generation plan allocator and its dependency-free leaf family are present in
        // the server build.
        foreach (string method in new[]
        {
            "allocate_in_condemned_generations",
            "get_next_alloc_seg",
            "attribute_pin_higher_gen_alloc",
            "size_fit_p",
            "switch_alignment_size",
            "grow_heap_segment",
            "set_plug_padded",
            "clear_plug_padded",
        })
        {
            Assert.Contains(statics, m => m.Name == method);
        }

        // uint8_t* gc_heap::allocate_in_condemned_generations (generation* gen, size_t size,
        // int from_gen_number, BOOL* convert_to_pinned_p, uint8_t* next_pinned_plug,
        // heap_segment* current_seg, uint8_t* old_loc) -> in the SVR compilation the implicit this is
        // an explicit gc_heap* first parameter and the plug address is a byte*.
        MethodInfo aic = heap.GetMethod(
            "allocate_in_condemned_generations",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(byte).MakePointerType(), aic.ReturnType);
        ParameterInfo[] aicParams = aic.GetParameters();
        Assert.Equal(8, aicParams.Length);
        Assert.Equal(heap.MakePointerType(), aicParams[0].ParameterType);
        Assert.Equal(gen.MakePointerType(), aicParams[1].ParameterType);
        Assert.Equal(typeof(nuint), aicParams[2].ParameterType);
        Assert.Equal(typeof(int), aicParams[3].ParameterType);
        Assert.Equal(typeof(int).MakePointerType(), aicParams[4].ParameterType);
        Assert.Equal(typeof(byte).MakePointerType(), aicParams[5].ParameterType);
        Assert.Equal(segment.MakePointerType(), aicParams[6].ParameterType);
        Assert.Equal(typeof(byte).MakePointerType(), aicParams[7].ParameterType);

        // heap_segment* gc_heap::get_next_alloc_seg (generation* gen) -> (gc_heap*, generation*).
        MethodInfo gnas = heap.GetMethod(
            "get_next_alloc_seg",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(segment.MakePointerType(), gnas.ReturnType);
        ParameterInfo[] gnasParams = gnas.GetParameters();
        Assert.Equal(2, gnasParams.Length);
        Assert.Equal(heap.MakePointerType(), gnasParams[0].ParameterType);
        Assert.Equal(gen.MakePointerType(), gnasParams[1].ParameterType);

        // The USE_REGIONS attribute_pin_higher_gen_alloc overload takes the pin's segment and the
        // destination generation: (gc_heap*, heap_segment*, int, byte*, nuint) -> void.
        MethodInfo attr = GetMethod(
            heap,
            "attribute_pin_higher_gen_alloc",
            new[]
            {
                heap.MakePointerType(),
                segment.MakePointerType(),
                typeof(int),
                typeof(byte).MakePointerType(),
                typeof(nuint),
            });
        Assert.NotNull(attr);
        Assert.Equal(typeof(void), attr.ReturnType);
    }

    [Fact]
    public static void ServerAllocateInCondemnedGenerationCallsClosedLeaves()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // allocate_in_condemned_generations fits the plug into the consing generation's plan window,
        // consuming pins from this heap's queue and advancing / growing the region as needed. Its
        // call graph must reach exactly the closed leaves the native retry loop invokes.
        MethodInfo aic = heap.GetMethod(
            "allocate_in_condemned_generations",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] aicNames, _) = CollectCallTargets(aic, "get_next_alloc_seg");
        foreach (string expected in new[]
        {
            "get_next_alloc_seg",
            "size_fit_p",
            "pinned_plug_que_empty_p",
            "oldest_pin",
            "pinned_plug",
            "deque_pinned_plug",
            "pinned_plug_of",
            "pinned_len",
            "set_new_pin_info",
            "update_planned_gen0_free_space",
            "set_allocator_next_pin",
            "attribute_pin_higher_gen_alloc",
            "grow_heap_segment",
            "set_region_plan_gen_num",
            "init_alloc_info",
            "same_large_alignment_p",
            "set_plug_padded",
            "clear_plug_padded",
        })
        {
            Assert.Contains(expected, aicNames);
        }

        // get_next_alloc_seg walks past SIP regions and re-initializes the alloc info when the region
        // changes.
        MethodInfo gnas = heap.GetMethod(
            "get_next_alloc_seg",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] gnasNames, _) = CollectCallTargets(gnas, "heap_segment_non_sip");
        foreach (string expected in new[] { "heap_segment_non_sip", "init_alloc_info" })
        {
            Assert.Contains(expected, gnasNames);
        }
    }

    [Theory]
    // (size, ptr..limit gap, expected) exercising the no-old_loc branch: a min-object plug fits only
    // when the window is at least its aligned size.
    [InlineData(0x18u, 0x18u, true)]
    [InlineData(0x18u, 0x20u, true)]
    [InlineData(0x18u, 0x10u, false)]
    public static unsafe void ServerSizeFitPMeasuresPlanWindow(uint size, uint gap, bool expected)
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        MethodInfo sizeFit = heap.GetMethod(
            "size_fit_p",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

        var basePtr = (nuint)0x40000;
        Type bytePtr = typeof(byte).MakePointerType();
        object result = sizeFit.Invoke(
            null,
            new object[]
            {
                (nuint)size,
                Pointer.Box((void*)basePtr, bytePtr),
                Pointer.Box((void*)(basePtr + gap), bytePtr),
                Pointer.Box(null, bytePtr),
                2, // USE_PADDING_TAIL
            })!;

        Assert.Equal(expected, (bool)result);
    }

    [Fact]
    public static void ServerAllocateInOlderGenerationSurfaceIsPresent()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        Type gen = GetType("Internal.Runtime.GarbageCollection.generation");
        MethodInfo[] statics =
            heap.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        // The older-generation free-list plan allocator, its close-out helper, and the free-list /
        // free-object bookkeeping leaves it needs are present in the server build.
        foreach (string method in new[]
        {
            "allocate_in_older_generation",
            "fix_older_allocation_area",
            "adjust_limit",
            "unused_array_size",
            "make_free_obj",
            "thread_free_item_front",
            "thread_item_front_added",
            "should_set_bgc_mark_bit",
            "set_plug_bgc_mark_bit",
            "set_free_obj_in_compact_bit",
        })
        {
            Assert.Contains(statics, m => m.Name == method);
        }

        // uint8_t* gc_heap::allocate_in_older_generation (generation* gen, size_t size,
        // int from_gen_number, uint8_t* old_loc) -> in the SVR compilation the implicit this is an
        // explicit gc_heap* first parameter and the plug address is a byte*.
        MethodInfo aiog = heap.GetMethod(
            "allocate_in_older_generation",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(byte).MakePointerType(), aiog.ReturnType);
        ParameterInfo[] aiogParams = aiog.GetParameters();
        Assert.Equal(5, aiogParams.Length);
        Assert.Equal(heap.MakePointerType(), aiogParams[0].ParameterType);
        Assert.Equal(gen.MakePointerType(), aiogParams[1].ParameterType);
        Assert.Equal(typeof(nuint), aiogParams[2].ParameterType);
        Assert.Equal(typeof(int), aiogParams[3].ParameterType);
        Assert.Equal(typeof(byte).MakePointerType(), aiogParams[4].ParameterType);

        // void gc_heap::fix_older_allocation_area (generation* older_gen) -> (gc_heap*, generation*).
        MethodInfo fix = heap.GetMethod(
            "fix_older_allocation_area",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(void), fix.ReturnType);
        ParameterInfo[] fixParams = fix.GetParameters();
        Assert.Equal(2, fixParams.Length);
        Assert.Equal(heap.MakePointerType(), fixParams[0].ParameterType);
        Assert.Equal(gen.MakePointerType(), fixParams[1].ParameterType);

        // void gc_heap::adjust_limit (uint8_t* start, size_t limit_size, generation* gen) ->
        // (gc_heap*, byte*, nuint, generation*); leave_allocation_segment is adjust_limit(0, 0, gen).
        MethodInfo adjust = heap.GetMethod(
            "adjust_limit",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(void), adjust.ReturnType);
        ParameterInfo[] adjustParams = adjust.GetParameters();
        Assert.Equal(4, adjustParams.Length);
        Assert.Equal(heap.MakePointerType(), adjustParams[0].ParameterType);
        Assert.Equal(typeof(byte).MakePointerType(), adjustParams[1].ParameterType);
        Assert.Equal(typeof(nuint), adjustParams[2].ParameterType);
        Assert.Equal(gen.MakePointerType(), adjustParams[3].ParameterType);

        // commit_alloc_list_changes stays on the shared allocator type; the plan driver invokes it
        // through generation_allocator (older_gen) before fix_older_allocation_area.
        Type allocatorType = GetType("Internal.Runtime.GarbageCollection.allocator");
        Assert.Contains(
            allocatorType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static),
            m => m.Name == "commit_alloc_list_changes");
    }

    [Fact]
    public static void ServerAllocateInOlderGenerationCallsClosedLeaves()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // allocate_in_older_generation walks the older generation's size-segregated free lists and
        // end-of-segment space; its call graph must reach exactly the closed free-list mutation and
        // plan-window leaves the native loop invokes.
        MethodInfo aiog = heap.GetMethod(
            "allocate_in_older_generation",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] aiogNames, _) = CollectCallTargets(aiog, "size_fit_p");
        foreach (string expected in new[]
        {
            "size_fit_p",
            "first_suitable_bucket",
            "number_of_buckets",
            "added_alloc_list_head_of",
            "alloc_list_head_of",
            "free_list_slot",
            "unused_array_size",
            "unlink_item",
            "unlink_item_no_undo_added",
            "should_set_bgc_mark_bit",
            "adjust_limit",
            "grow_heap_segment",
            "set_plug_padded",
            "clear_plug_padded",
            "same_large_alignment_p",
            "set_node_realigned",
            "set_plug_bgc_mark_bit",
        })
        {
            Assert.Contains(expected, aiogNames);
        }

        // adjust_limit turns the abandoned plan window into free objects / threaded free items and
        // records the free-obj-in-compact bit on the saved pinned-plug reloc word.
        MethodInfo adjust = heap.GetMethod(
            "adjust_limit",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] adjustNames, _) = CollectCallTargets(adjust, "make_free_obj");
        foreach (string expected in new[]
        {
            "make_free_obj",
            "thread_free_item_front",
            "thread_item_front_added",
            "set_free_obj_in_compact_bit",
            "pinned_plug",
            "pinned_plug_of",
        })
        {
            Assert.Contains(expected, adjustNames);
        }

        // fix_older_allocation_area threads the unused tail back onto the free list.
        MethodInfo fix = heap.GetMethod(
            "fix_older_allocation_area",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] fixNames, _) = CollectCallTargets(fix, "make_unused_array");
        foreach (string expected in new[] { "make_unused_array", "thread_item_front" })
        {
            Assert.Contains(expected, fixNames);
        }
    }

    [Theory]
    // A free object laid down by make_unused_array must report exactly its byte length through
    // unused_array_size, so the older-generation plan allocator sizes threaded free items correctly.
    [InlineData(0x18u)]
    [InlineData(0x20u)]
    [InlineData(0x100u)]
    [InlineData(0x4000u)]
    public static unsafe void ServerUnusedArraySizeRoundTripsMakeUnusedArray(uint size)
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        MethodInfo make = heap.GetMethod(
            "make_unused_array",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo unused = heap.GetMethod(
            "unused_array_size",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

        Type bytePtr = typeof(byte).MakePointerType();
        IntPtr buffer = Marshal.AllocHGlobal((int)size + sizeof(nuint) * 2);
        try
        {
            for (int i = 0; i < (int)size + sizeof(nuint) * 2; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }

            // GCCommon.g_gc_pFreeObjectMethodTable is null in the test host; make_unused_array writes
            // that (null) method table and unused_array_size's IsFree() check compares equal.
            make.Invoke(
                null,
                new object[]
                {
                    Pointer.Box((void*)buffer, bytePtr),
                    (nuint)size,
                    0,
                    0,
                });

            object result = unused.Invoke(
                null,
                new object[] { Pointer.Box((void*)buffer, bytePtr) })!;

            Assert.Equal((nuint)size, (nuint)result);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public static void ServerPlanSingleGcCountersAreInstanceOwned()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // gen2_removed_no_undo and saved_pinned_plug_index are PER_HEAP_FIELD_SINGLE_GC, so in the
        // MULTIPLE_HEAPS build the older-generation plan allocator and adjust_limit must reach this
        // heap's own copies (instance fields, not statics).
        foreach (string fieldName in new[] { "gen2_removed_no_undo", "saved_pinned_plug_index" })
        {
            FieldInfo instance = heap.GetField(
                fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(instance);
            Assert.Null(heap.GetField(
                fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        }
    }

    [Fact]
    public static void ServerPlanLohSurfaceIsPresent()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        Type genType = GetType("Internal.Runtime.GarbageCollection.generation");
        Type markType = GetType("Internal.Runtime.GarbageCollection.mark");
        Type bytePtr = typeof(byte).MakePointerType();
        Type heapPtr = heap.MakePointerType();
        Type genPtr = genType.MakePointerType();
        Type markPtr = markType.MakePointerType();

        MethodInfo[] statics =
            heap.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        // plan_phase.cpp FEATURE_LOH_COMPACTION planning family, every method reaching its heap
        // through the gc_heap* parameter (the WKS statics are instance-owned in the server build).
        foreach (string expected in new[]
        {
            "plan_loh",
            "loh_allocate_in_condemned",
            "loh_size_fit_p",
            "loh_enque_pinned_plug",
            "loh_set_allocator_next_pin",
            "loh_deque_pinned_plug",
            "loh_oldest_pin",
            "loh_pinned_plug_que_empty_p",
            "loh_pinned_plug_of",
            "decay_loh_pinned_queue",
        })
        {
            Assert.Contains(statics, m => m.Name == expected);
        }

        // BOOL gc_heap::plan_loh () -> bool plan_loh (gc_heap*).
        MethodInfo planLoh = GetMethod(heap, "plan_loh", new[] { heapPtr });
        Assert.Equal(typeof(bool), planLoh.ReturnType);

        // uint8_t* gc_heap::loh_allocate_in_condemned (size_t size) -> byte* (gc_heap*, nuint).
        MethodInfo aic = GetMethod(heap, "loh_allocate_in_condemned", new[] { heapPtr, typeof(nuint) });
        Assert.Equal(bytePtr, aic.ReturnType);

        // BOOL gc_heap::loh_size_fit_p (size_t, uint8_t*, uint8_t*, bool) ->
        // bool (nuint, byte*, byte*, bool). No gc_heap*: it is a pure pointer-arithmetic leaf.
        MethodInfo sizeFit = GetMethod(
            heap,
            "loh_size_fit_p",
            new[] { typeof(nuint), bytePtr, bytePtr, typeof(bool) });
        Assert.Equal(typeof(bool), sizeFit.ReturnType);

        // BOOL gc_heap::loh_enque_pinned_plug (uint8_t*, size_t) -> int (gc_heap*, byte*, nuint).
        MethodInfo enque = GetMethod(
            heap,
            "loh_enque_pinned_plug",
            new[] { heapPtr, bytePtr, typeof(nuint) });
        Assert.Equal(typeof(int), enque.ReturnType);

        // mark* gc_heap::loh_pinned_plug_of (size_t bos) -> mark* (gc_heap*, nuint).
        MethodInfo plugOf = GetMethod(heap, "loh_pinned_plug_of", new[] { heapPtr, typeof(nuint) });
        Assert.Equal(markPtr, plugOf.ReturnType);

        // size_t gc_heap::loh_deque_pinned_plug () -> nuint (gc_heap*).
        MethodInfo deque = GetMethod(heap, "loh_deque_pinned_plug", new[] { heapPtr });
        Assert.Equal(typeof(nuint), deque.ReturnType);

        // mark* gc_heap::loh_oldest_pin () -> mark* (gc_heap*).
        Assert.Equal(markPtr, GetMethod(heap, "loh_oldest_pin", new[] { heapPtr }).ReturnType);

        // BOOL gc_heap::loh_pinned_plug_que_empty_p () -> int (gc_heap*).
        Assert.Equal(
            typeof(int),
            GetMethod(heap, "loh_pinned_plug_que_empty_p", new[] { heapPtr }).ReturnType);

        // void gc_heap::loh_set_allocator_next_pin () -> void (gc_heap*).
        Assert.Equal(
            typeof(void),
            GetMethod(heap, "loh_set_allocator_next_pin", new[] { heapPtr }).ReturnType);

        _ = genPtr;
    }

    [Fact]
    public static void ServerPlanLohCallsClosedLeaves()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // plan_loh drives the pinned-queue enqueue, the condemned allocator, the relocation-distance
        // record, and the UOH start-object leaf; every token resolves within the closed family.
        MethodInfo planLoh = heap.GetMethod(
            "plan_loh",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] planNames, _) = CollectCallTargets(planLoh, "loh_allocate_in_condemned");
        foreach (string expected in new[]
        {
            "loh_allocate_in_condemned",
            "loh_enque_pinned_plug",
            "loh_pinned_plug_que_empty_p",
            "loh_pinned_plug_of",
            "loh_deque_pinned_plug",
            "loh_set_node_relocation_distance",
            "get_uoh_start_object",
            "heap_segment_rw",
        })
        {
            Assert.Contains(expected, planNames);
        }

        // loh_allocate_in_condemned fits the plug, consumes pins, and grows / rolls over LOH regions.
        MethodInfo aic = heap.GetMethod(
            "loh_allocate_in_condemned",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] aicNames, _) = CollectCallTargets(aic, "grow_heap_segment");
        foreach (string expected in new[]
        {
            "loh_size_fit_p",
            "grow_heap_segment",
            "loh_set_allocator_next_pin",
            "loh_pinned_plug_que_empty_p",
            "loh_pinned_plug_of",
            "loh_deque_pinned_plug",
            "loh_oldest_pin",
        })
        {
            Assert.Contains(expected, aicNames);
        }

        // loh_enque_pinned_plug grows the queue and positions the allocator on the next pin.
        MethodInfo enque = heap.GetMethod(
            "loh_enque_pinned_plug",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] enqueNames, _) = CollectCallTargets(enque, "grow_mark_stack");
        foreach (string expected in new[] { "grow_mark_stack", "loh_set_allocator_next_pin" })
        {
            Assert.Contains(expected, enqueNames);
        }

        // decay_loh_pinned_queue frees the queue through the free-heap import once decayed.
        MethodInfo decay = heap.GetMethod(
            "decay_loh_pinned_queue",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] decayNames, _) = CollectCallTargets(decay, "ManagedGC_Free");
        Assert.Contains("ManagedGC_Free", decayNames);
    }

    [Theory]
    // sizeof(loh_padding_obj) aligns to 0x20, so the end-of-window pad is one padding object (0x20)
    // and a mid-window pad is two (0x40). A 0x18 plug fits only when the window minus its pad covers it.
    [InlineData(0x18u, 0x40u, true, true)]   // end: 0x40 - 0x20 = 0x20 >= 0x18
    [InlineData(0x18u, 0x38u, true, true)]   // end: 0x38 - 0x20 = 0x18 >= 0x18
    [InlineData(0x18u, 0x30u, true, false)]  // end: 0x30 - 0x20 = 0x10 <  0x18
    [InlineData(0x18u, 0x80u, false, true)]  // mid: 0x80 - 0x40 = 0x40 >= 0x18
    [InlineData(0x18u, 0x40u, false, false)] // mid: 0x40 - 0x40 = 0    <  0x18
    public static unsafe void ServerLohSizeFitPMeasuresPlanWindow(
        uint size,
        uint gap,
        bool endP,
        bool expected)
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        MethodInfo sizeFit = heap.GetMethod(
            "loh_size_fit_p",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

        var basePtr = (nuint)0x40000;
        Type bytePtr = typeof(byte).MakePointerType();
        object result = sizeFit.Invoke(
            null,
            new object[]
            {
                (nuint)size,
                Pointer.Box((void*)basePtr, bytePtr),
                Pointer.Box((void*)(basePtr + gap), bytePtr),
                endP,
            })!;

        Assert.Equal(expected, (bool)result);
    }

    [Fact]
    public static void ServerLohPinnedQueueIsInstanceOwned()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // gcpriv.h marks loh_pinned_queue_tos/bos PER_HEAP_FIELD_SINGLE_GC and
        // loh_pinned_queue_length/decay/loh_pinned_queue PER_HEAP_FIELD_MAINTAINED, so in the
        // MULTIPLE_HEAPS build each server heap owns its own LOH pinned queue (instance, not static).
        foreach (string fieldName in new[]
        {
            "loh_pinned_queue_tos",
            "loh_pinned_queue_bos",
            "loh_pinned_queue_length",
            "loh_pinned_queue_decay",
            "loh_pinned_queue",
        })
        {
            Assert.NotNull(heap.GetField(
                fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
            Assert.Null(heap.GetField(
                fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        }

        // gc.cpp init_gc_heap resets the queue per heap; the reset leaf now takes the gc_heap*.
        MethodInfo init = GetMethod(
            heap,
            "initialize_loh_pinned_queue_state",
            new[] { heap.MakePointerType() });
        Assert.Equal(typeof(void), init.ReturnType);
    }

    [Fact]
    public static void ServerSweepUohSurfaceIsPresent()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        Type genType = GetType("Internal.Runtime.GarbageCollection.generation");
        Type segType = GetType("Internal.Runtime.GarbageCollection.heap_segment");
        Type bytePtr = typeof(byte).MakePointerType();
        Type heapPtr = heap.MakePointerType();
        Type genPtr = genType.MakePointerType();
        Type segPtr = segType.MakePointerType();

        MethodInfo[] statics =
            heap.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        // sweep.cpp / plan_phase.cpp / regions_segments.cpp plan-time UOH sweep and segment-return
        // family, translated for the server compilation.
        foreach (string expected in new[]
        {
            "sweep_uoh_objects",
            "thread_gap",
            "uoh_thread_gap_front",
            "uoh_object_marked",
            "update_start_tail_regions",
            "rearrange_uoh_segments",
            "rearrange_small_heap_segments",
            "delay_free_segments",
        })
        {
            Assert.Contains(statics, m => m.Name == expected);
        }

        // void gc_heap::sweep_uoh_objects (int gen_num) -> void (gc_heap*, int).
        MethodInfo sweep = GetMethod(heap, "sweep_uoh_objects", new[] { heapPtr, typeof(int) });
        Assert.Equal(typeof(void), sweep.ReturnType);

        // BOOL gc_heap::uoh_object_marked (uint8_t*, BOOL) -> int (byte*, int). lowest_address /
        // highest_address stay static in the managed model, so this keeps the WKS static signature.
        MethodInfo marked = GetMethod(heap, "uoh_object_marked", new[] { bytePtr, typeof(int) });
        Assert.Equal(typeof(int), marked.ReturnType);

        // void gc_heap::thread_gap (uint8_t*, size_t, generation*) -> void (byte*, nuint, generation*).
        Assert.Equal(
            typeof(void),
            GetMethod(heap, "thread_gap", new[] { bytePtr, typeof(nuint), genPtr }).ReturnType);
        Assert.Equal(
            typeof(void),
            GetMethod(heap, "uoh_thread_gap_front", new[] { bytePtr, typeof(nuint), genPtr }).ReturnType);

        // void gc_heap::update_start_tail_regions (generation*, heap_segment* x3).
        Assert.Equal(
            typeof(void),
            GetMethod(
                heap,
                "update_start_tail_regions",
                new[] { genPtr, segPtr, segPtr, segPtr }).ReturnType);

        // The segment-return family threads freeable_*_segment (PER_HEAP_FIELD_MAINTAINED) through the
        // gc_heap* parameter in the server build.
        Assert.Equal(
            typeof(void),
            GetMethod(heap, "rearrange_uoh_segments", new[] { heapPtr }).ReturnType);
        Assert.Equal(
            typeof(void),
            GetMethod(heap, "rearrange_small_heap_segments", new[] { heapPtr }).ReturnType);
        Assert.Equal(
            typeof(void),
            GetMethod(heap, "delay_free_segments", new[] { heapPtr }).ReturnType);
    }

    [Fact]
    public static void ServerSweepUohCallsClosedLeaves()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // sweep_uoh_objects clears the allocator, threads gaps, un-marks survivors, unlinks empty
        // regions (update_start_tail_regions), and trims / decommits partially-live segments.
        MethodInfo sweep = heap.GetMethod(
            "sweep_uoh_objects",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] sweepNames, _) = CollectCallTargets(sweep, "thread_gap");
        foreach (string expected in new[]
        {
            "thread_gap",
            "uoh_object_marked",
            "update_start_tail_regions",
            "decommit_heap_segment_pages",
            "get_uoh_start_object",
            "heap_segment_rw",
            "clear",
        })
        {
            Assert.Contains(expected, sweepNames);
        }

        // delay_free_segments drives both rearrange leaves (SOH one only when no BGC runs).
        MethodInfo delay = heap.GetMethod(
            "delay_free_segments",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] delayNames, _) = CollectCallTargets(delay, "rearrange_uoh_segments");
        Assert.Contains("rearrange_uoh_segments", delayNames);
        Assert.Contains("rearrange_small_heap_segments", delayNames);

        // rearrange_uoh_segments returns each queued segment to the free-region pool.
        MethodInfo rearrange = heap.GetMethod(
            "rearrange_uoh_segments",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] rearrangeNames, _) = CollectCallTargets(rearrange, "return_free_region");
        Assert.Contains("return_free_region", rearrangeNames);

        // thread_gap makes the gap an unused array and threads it onto the free list.
        MethodInfo threadGap = heap.GetMethod(
            "thread_gap",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] threadGapNames, _) = CollectCallTargets(threadGap, "make_unused_array");
        foreach (string expected in new[] { "make_unused_array", "thread_item" })
        {
            Assert.Contains(expected, threadGapNames);
        }
    }

    [Fact]
    public static void ServerFreeableSegmentsAreInstanceOwned()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // gcpriv.h marks freeable_uoh_segment / freeable_soh_segment PER_HEAP_FIELD_MAINTAINED, so in
        // the MULTIPLE_HEAPS build each server heap owns its own freeable-segment lists (instance,
        // not static). init_gc_heap resets them per heap through the gc_heap* leaf.
        foreach (string fieldName in new[] { "freeable_uoh_segment", "freeable_soh_segment" })
        {
            Assert.NotNull(heap.GetField(
                fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
            Assert.Null(heap.GetField(
                fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        }

        MethodInfo init = GetMethod(
            heap,
            "initialize_freeable_segments_state",
            new[] { heap.MakePointerType() });
        Assert.Equal(typeof(void), init.ReturnType);

        // The server heap-creation path resets each heap's freeable segments during init.
        MethodInfo create = heap.GetMethod(
            "initialize_freeable_segments_state",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        FieldInfo uohField = heap.GetField(
            "freeable_uoh_segment",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        byte[] il = create.GetMethodBody()!.GetILAsByteArray()!;
        byte[] token = BitConverter.GetBytes(uohField.MetadataToken);
        bool storesField = false;
        for (int i = 0; i <= il.Length - 5; i++)
        {
            // stfld <freeable_uoh_segment>
            if (il[i] == 0x7d &&
                il[i + 1] == token[0] &&
                il[i + 2] == token[1] &&
                il[i + 3] == token[2] &&
                il[i + 4] == token[3])
            {
                storesField = true;
                break;
            }
        }

        Assert.True(storesField, "initialize_freeable_segments_state must null this heap's freeable_uoh_segment.");
    }

    [Fact]
    public static void ServerFixGenerationBoundsSurfaceIsPresent()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        Type genType = GetType("Internal.Runtime.GarbageCollection.generation");
        Type segType = GetType("Internal.Runtime.GarbageCollection.heap_segment");
        Type bytePtr = typeof(byte).MakePointerType();
        Type heapPtr = heap.MakePointerType();
        Type genPtr = genType.MakePointerType();
        Type segPtr = segType.MakePointerType();
        Type intPtr = typeof(int).MakePointerType();

        MethodInfo[] statics =
            heap.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        // plan_phase.cpp / allocation.cpp / background.cpp plan-time region-threading family,
        // translated for the server compilation.
        foreach (string expected in new[]
        {
            "fix_generation_bounds",
            "thread_final_regions",
            "find_first_valid_region",
            "reset_allocation_pointers",
            "set_allocation_heap_segment",
            "should_update_end_mark_size",
        })
        {
            Assert.Contains(statics, m => m.Name == expected);
        }

        // void gc_heap::fix_generation_bounds (int, generation*) -> void (gc_heap*, int, generation*).
        Assert.Equal(
            typeof(void),
            GetMethod(
                heap,
                "fix_generation_bounds",
                new[] { heapPtr, typeof(int), genPtr }).ReturnType);

        // void gc_heap::thread_final_regions (bool) -> void (gc_heap*, bool).
        Assert.Equal(
            typeof(void),
            GetMethod(heap, "thread_final_regions", new[] { heapPtr, typeof(bool) }).ReturnType);

        // heap_segment* gc_heap::find_first_valid_region (heap_segment*, bool, int*) ->
        // heap_segment* (gc_heap*, heap_segment*, bool, int*).
        Assert.Equal(
            segPtr,
            GetMethod(
                heap,
                "find_first_valid_region",
                new[] { heapPtr, segPtr, typeof(bool), intPtr }).ReturnType);

        // void gc_heap::reset_allocation_pointers (generation*, uint8_t*) -> void (generation*, byte*).
        Assert.Equal(
            typeof(void),
            GetMethod(heap, "reset_allocation_pointers", new[] { genPtr, bytePtr }).ReturnType);

        // void gc_heap::set_allocation_heap_segment (generation*) -> void (generation*).
        Assert.Equal(
            typeof(void),
            GetMethod(heap, "set_allocation_heap_segment", new[] { genPtr }).ReturnType);

        // bool gc_heap::should_update_end_mark_size () -> bool (). PER_HEAP_ISOLATED_METHOD, so it
        // stays static and takes no gc_heap*.
        Assert.Equal(
            typeof(bool),
            GetMethod(heap, "should_update_end_mark_size", Type.EmptyTypes).ReturnType);
    }

    [Fact]
    public static void ServerFixGenerationBoundsCallsClosedLeaves()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // fix_generation_bounds runs thread_final_regions and re-seats the ephemeral segment's alloc.
        MethodInfo fix = heap.GetMethod(
            "fix_generation_bounds",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] fixNames, _) = CollectCallTargets(fix, "thread_final_regions");
        Assert.Contains("thread_final_regions", fixNames);

        // thread_final_regions returns SIP regions, threads condemned regions via
        // find_first_valid_region, gets fresh regions for empty gens, resets alloc pointers, and
        // consults the BGC end-mark predicate.
        MethodInfo tfr = heap.GetMethod(
            "thread_final_regions",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] tfrNames, _) = CollectCallTargets(tfr, "find_first_valid_region");
        foreach (string expected in new[]
        {
            "find_first_valid_region",
            "return_free_region",
            "thread_start_region",
            "get_free_region",
            "reset_allocation_pointers",
            "should_update_end_mark_size",
        })
        {
            Assert.Contains(expected, tfrNames);
        }

        // find_first_valid_region returns empty regions, sets plan/gen numbers, decommits gen2+ tails,
        // threads swept-in-plan free lists, and clears the per-GC region flags.
        MethodInfo ffvr = heap.GetMethod(
            "find_first_valid_region",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] ffvrNames, _) = CollectCallTargets(ffvr, "return_free_region");
        foreach (string expected in new[]
        {
            "return_free_region",
            "set_region_gen_num",
            "decommit_heap_segment_pages",
            "clear_region_sweep_in_plan",
            "clear_region_demoted",
        })
        {
            Assert.Contains(expected, ffvrNames);
        }

        // reset_allocation_pointers re-seats the generation's allocation segment.
        MethodInfo reset = heap.GetMethod(
            "reset_allocation_pointers",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] resetNames, _) = CollectCallTargets(reset, "set_allocation_heap_segment");
        Assert.Contains("set_allocation_heap_segment", resetNames);
    }

    [Fact]
    public static void ServerEndMarkSizeIsInstanceOwned()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // gcpriv.h marks background_soh_size_end_mark PER_HEAP_FIELD_DIAG_ONLY, so in the
        // MULTIPLE_HEAPS build it is instance-owned (not static). thread_final_regions accumulates
        // into this heap's field through the gc_heap* parameter.
        FieldInfo instanceField = heap.GetField(
            "background_soh_size_end_mark",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.NotNull(instanceField);
        Assert.Null(heap.GetField(
            "background_soh_size_end_mark",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));

        MethodInfo tfr = heap.GetMethod(
            "thread_final_regions",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        byte[] il = tfr.GetMethodBody()!.GetILAsByteArray()!;
        byte[] token = BitConverter.GetBytes(instanceField.MetadataToken);
        bool storesField = false;
        for (int i = 0; i <= il.Length - 5; i++)
        {
            // stfld <background_soh_size_end_mark>
            if (il[i] == 0x7d &&
                il[i + 1] == token[0] &&
                il[i + 2] == token[1] &&
                il[i + 3] == token[2] &&
                il[i + 4] == token[3])
            {
                storesField = true;
                break;
            }
        }

        Assert.True(
            storesField,
            "thread_final_regions must accumulate into this heap's background_soh_size_end_mark.");
    }

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

    [Fact]
    public static void ServerPlanPhaseDriverHasNativeSignature()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // void gc_heap::plan_phase (int condemned_gen_number), translated as a per-heap static
        // taking the owning heap explicitly: plan_phase (gc_heap*, int).
        MethodInfo plan = heap.GetMethod(
            "plan_phase",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(void), plan.ReturnType);
        ParameterInfo[] planParams = plan.GetParameters();
        Assert.Equal(2, planParams.Length);
        Assert.Equal(heap.MakePointerType(), planParams[0].ParameterType);
        Assert.Equal(typeof(int), planParams[1].ParameterType);

        // gc_policy and loh_compacted_p are PER_HEAP_FIELD_SINGLE_GC, so they are instance fields
        // on the server heap.
        foreach (string perHeapField in new[] { "gc_policy", "loh_compacted_p" })
        {
            FieldInfo field = heap.GetField(
                perHeapField,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.NotNull(field);
            Assert.Equal(typeof(int), field.FieldType);
            Assert.Null(heap.GetField(
                perHeapField,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        }

        // maxgen_size_inc_p / pm_trigger_full_gc / pm_stress_on are PER_HEAP_ISOLATED, so they are
        // static in both builds.
        foreach (string isolated in new[] { "maxgen_size_inc_p", "pm_trigger_full_gc", "pm_stress_on" })
        {
            FieldInfo field = heap.GetField(
                isolated,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.NotNull(field);
            Assert.Equal(typeof(bool), field.FieldType);
        }

        // loh_alloc_since_cg is PER_HEAP_FIELD_SINGLE_GC_ALLOC, so it is instance-owned for the
        // MULTIPLE_HEAPS build and the compaction join resets each heap's own counter.
        FieldInfo lohAlloc = heap.GetField(
            "loh_alloc_since_cg",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.NotNull(lohAlloc);
        Assert.Equal(typeof(ulong), lohAlloc.FieldType);
    }

    [Fact]
    public static void ServerPlanPhaseDriverSequencesTranslatedHelpers()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        MethodInfo plan = heap.GetMethod(
            "plan_phase",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

        // plan_phase drives the whole already-translated plan-phase family: the region-planning
        // consumers, the plug walk allocators / brick threading, the compaction-vs-sweep deciders,
        // the diagnostic leaves this task adds, and the LOH compaction gating / UOH sweep. It also
        // closes the gc_join_decide_on_compaction join and reads/writes its per-GC reset fields, and
        // now runs the compact-branch execution (relocate_phase / compact_phase / fix_generation_bounds
        // and the gc_join_adjust_handle_age_compact tail).
        (string[] names, _) = CollectCallTargets(plan, "sweep_uoh_objects");
        foreach (string expected in new[]
        {
            "get_soh_start_object",
            "get_region_mark_list",
            "should_sweep_in_plan",
            "sweep_region_in_plan",
            "store_plug_gap_info",
            "allocate_in_condemned_generations",
            "allocate_in_older_generation",
            "enque_pinned_plug",
            "convert_to_pinned_plug",
            "merge_with_last_pinned_plug",
            "set_pinned_info",
            "insert_node",
            "update_brick_table",
            "find_next_marked",
            "process_last_np_surv_region",
            "process_remaining_regions",
            "add_gen_plug",
            "init_free_and_plug",
            "descr_generations",
            "print_free_and_plug",
            "sweep_ro_segments",
            "is_plug_padded",
            "generation_fragmentation",
            "decide_on_compacting",
            "sweep_uoh_objects",
            "plan_loh",
            "decay_loh_pinned_queue",
            "fix_older_allocation_area",
            "join",
            "joined",
            "restart",
            // Compact-branch execution wired in this slice.
            "relocate_phase",
            "compact_phase",
            "fix_generation_bounds",
            "get_gen0_end_space",
            "UpdatePromotedGenerations",
            "GcPromotionsGranted",
            "GcDemote",
            "thread_pinned_plug_gaps",
            "clear_gen1_cards",
            // Sweep-branch execution wired in this slice.
            "make_free_lists",
            "recover_saved_pinned_info",
            "verify_region_to_generation_map",
        })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public static void ServerPlanPhaseGetRegionMarkListIsHeapParameterized()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // get_region_mark_list (gc_heap*, ref int, byte*, byte*, byte***) -> byte** binary-searches
        // this heap's own sorted mark list, so it takes the owning heap explicitly (the WKS overload
        // reads the static mark_list). It calls binary_search.
        MethodInfo regionList = heap.GetMethod(
            "get_region_mark_list",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(byte).MakePointerType().MakePointerType(), regionList.ReturnType);
        ParameterInfo[] regionParams = regionList.GetParameters();
        Assert.Equal(5, regionParams.Length);
        Assert.Equal(heap.MakePointerType(), regionParams[0].ParameterType);
        Assert.True(regionParams[1].ParameterType.IsByRef);
        Assert.Equal(typeof(int), regionParams[1].ParameterType.GetElementType());

        (string[] names, _) = CollectCallTargets(regionList, "binary_search");
        Assert.Contains("binary_search", names);
    }

    [Fact]
    public static void ServerPlanPhaseDiagnosticLeavesAreNoOps()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // add_gen_plug / init_free_and_plug / print_free_and_plug / descr_generations /
        // sweep_ro_segments are FREE_USAGE_STATS / SIMPLE_DPRINTF / !USE_REGIONS no-ops for this
        // configuration, so they carry no calls of their own.
        foreach (string leaf in new[]
        {
            "add_gen_plug",
            "init_free_and_plug",
            "print_free_and_plug",
            "descr_generations",
            "sweep_ro_segments",
        })
        {
            MethodInfo method = heap.GetMethod(
                leaf,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.NotNull(method);
            (string[] names, _) = CollectCallTargets(method, leaf);
            Assert.Empty(names);
        }
    }

    [Fact]
    public static void ServerMakeFreeListsHasNativeSignature()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // void gc_heap::make_free_lists (int condemned_gen_number), translated as a per-heap static
        // taking the owning heap explicitly: make_free_lists (gc_heap*, int).
        MethodInfo makeFree = heap.GetMethod(
            "make_free_lists",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(void), makeFree.ReturnType);
        ParameterInfo[] makeFreeParams = makeFree.GetParameters();
        Assert.Equal(2, makeFreeParams.Length);
        Assert.Equal(heap.MakePointerType(), makeFreeParams[0].ParameterType);
        Assert.Equal(typeof(int), makeFreeParams[1].ParameterType);

        // void gc_heap::make_free_list_in_brick (uint8_t* tree, make_free_args* args): the brick-tree
        // walk it drives touches no per-heap state, so it stays static (tree, args).
        Type makeFreeArgs = GetType("Internal.Runtime.GarbageCollection.gc_heap+make_free_args");
        MethodInfo brick = heap.GetMethod(
            "make_free_list_in_brick",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(void), brick.ReturnType);
        ParameterInfo[] brickParams = brick.GetParameters();
        Assert.Equal(2, brickParams.Length);
        Assert.Equal(typeof(byte).MakePointerType(), brickParams[0].ParameterType);
        Assert.Equal(makeFreeArgs.MakePointerType(), brickParams[1].ParameterType);

        // special_sweep_p is PER_HEAP_FIELD_SINGLE_GC, so make_free_lists reaches it as an instance
        // field on the server heap; ephemeral_heap_segment / alloc_allocated are per-heap too.
        FieldInfo specialSweep = heap.GetField(
            "special_sweep_p",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.NotNull(specialSweep);
        Assert.Equal(typeof(bool), specialSweep.FieldType);
        Assert.Null(heap.GetField(
            "special_sweep_p",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));

        foreach (string perHeap in new[] { "ephemeral_heap_segment", "alloc_allocated" })
        {
            Assert.NotNull(heap.GetField(
                perHeap,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        }
    }

    [Fact]
    public static void ServerMakeFreeListsSequencesTranslatedHelpers()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        MethodInfo makeFree = heap.GetMethod(
            "make_free_lists",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

        // make_free_lists walks each condemned generation's regions, fixing brick entries and
        // threading each brick's plug tree, then re-threads the final region layout and resets the
        // ephemeral segment. It reuses the closed leaves below.
        (string[] names, _) = CollectCallTargets(makeFree, "make_free_list_in_brick");
        foreach (string expected in new[]
        {
            "get_stop_generation_index",
            "generation_of",
            "get_start_segment",
            "get_soh_start_object",
            "brick_of",
            "get_plan_gen_num",
            "make_free_list_in_brick",
            "brick_address",
            "set_brick",
            "heap_segment_next_non_sip",
            "check_seg_gen_num",
            "thread_final_regions",
        })
        {
            Assert.Contains(expected, names);
        }

        // make_free_list_in_brick threads each inter-plug gap onto its planned free list and clears
        // the plug's pad / DOUBLY_LINKED_FL bits; it does not allocate or relocate.
        MethodInfo brick = heap.GetMethod(
            "make_free_list_in_brick",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] brickNames, _) = CollectCallTargets(brick, "thread_gap");
        foreach (string expected in new[]
        {
            "node_left_child",
            "node_right_child",
            "node_gap_size",
            "is_plug_padded",
            "clear_plug_padded",
            "is_plug_bgc_mark_bit_set",
            "clear_plug_bgc_mark_bit",
            "is_free_obj_in_compact_bit_set",
            "clear_free_obj_in_compact_bit",
            "thread_gap",
            "make_free_list_in_brick",
        })
        {
            Assert.Contains(expected, brickNames);
        }
    }

    [Fact]
    public static void ServerRelocatePhaseHasNativeSignature()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // void gc_heap::relocate_phase (int condemned_gen_number, uint8_t* first_condemned_address),
        // translated as a per-heap static taking the owning heap explicitly:
        // relocate_phase (gc_heap*, int, byte*).
        MethodInfo reloc = heap.GetMethod(
            "relocate_phase",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(void), reloc.ReturnType);
        ParameterInfo[] relocParams = reloc.GetParameters();
        Assert.Equal(3, relocParams.Length);
        Assert.Equal(heap.MakePointerType(), relocParams[0].ParameterType);
        Assert.Equal(typeof(int), relocParams[1].ParameterType);
        Assert.Equal(typeof(byte).MakePointerType(), relocParams[2].ParameterType);
    }

    [Fact]
    public static void ServerRelocatePhaseSequencesTranslatedHelpers()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        MethodInfo reloc = heap.GetMethod(
            "relocate_phase",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

        // relocate_phase drives the whole already-translated relocate family: root/handle relocation
        // through the GCHeap::Relocate callback, the cross-generation card relocate scan, the LOH/POH
        // and SOH survivor relocation, the finalization relocation, and the gc_join_begin_relocate_
        // phase join.
        (string[] names, _) = CollectCallTargets(reloc, "relocate_survivors");
        foreach (string expected in new[]
        {
            "GcScanRoots",
            "GcScanHandles",
            "mark_through_cards_for_segments",
            "mark_through_cards_for_uoh_objects",
            "relocate_in_loh_compact",
            "relocate_in_uoh_objects",
            "relocate_survivors",
            "RelocateFinalizationData",
            "verify_region_to_generation_map",
            "join",
            "joined",
            "restart",
        })
        {
            Assert.Contains(expected, names);
        }

        // relocate_phase stops before the compact / sweep execution: it must not call the deferred
        // compact_phase / make_free_lists / recover_saved_pinned_info / fix_generation_bounds tail.
        foreach (string deferred in new[]
        {
            "compact_phase",
            "make_free_lists",
            "recover_saved_pinned_info",
            "fix_generation_bounds",
            "compact_loh",
        })
        {
            Assert.DoesNotContain(deferred, names);
        }
    }

    [Fact]
    public static void ServerRelocateAddressRoutesOwningHeapLohCompactedFlag()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // relocate_address looks each address up in the shared brick relocation tree; its
        // FEATURE_LOH_COMPACTION fallback consults the *owning* heap's per-GC loh_compacted_p through
        // heap_segment_heap, not the current worker's, so the callee set must include heap_segment_heap
        // and the brick-tree leaves.
        MethodInfo relocAddr = heap.GetMethod(
            "relocate_address",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        ParameterInfo[] p = relocAddr.GetParameters();
        Assert.Single(p);
        Assert.Equal(typeof(byte).MakePointerType().MakePointerType(), p[0].ParameterType);

        (string[] names, _) = CollectCallTargets(relocAddr, "heap_segment_heap");
        foreach (string expected in new[]
        {
            "heap_segment_heap",
            "tree_search",
            "should_check_brick_for_reloc",
            "loh_node_relocation_distance",
            "try_get_region_segment",
        })
        {
            Assert.Contains(expected, names);
        }

        // loh_compacted_p is PER_HEAP_FIELD_SINGLE_GC, so it is instance-owned on the server heap.
        FieldInfo instanceField = heap.GetField(
            "loh_compacted_p",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.NotNull(instanceField);
        Assert.Null(heap.GetField(
            "loh_compacted_p",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
    }

    [Fact]
    public static void ServerRelocateCallbackResolvesOwningHeap()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // The GCHeap::Relocate callback resolves the object's owning heap (heap_of) for the interior
        // LOH find_object path, then relocates through relocate_address.
        MethodInfo relocate = heap.GetMethod(
            "relocate",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] names, _) = CollectCallTargets(relocate, "relocate_address");
        foreach (string expected in new[]
        {
            "heap_of",
            "find_object",
            "relocate_address",
            "loh_object_p",
            "is_in_find_object_range",
        })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public static void ServerCardScanRelocateBranchRelocatesReferences()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // The server foreground card scan's per-slot body now translates the relocate branch: when
        // relocating it rewrites the child through relocate_address and re-reads its planned
        // generation number through get_region_plan_gen_num (the mark branch still uses
        // mark_object_simple).
        MethodInfo scan = heap.GetMethod(
            "scan_card_reference",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] names, _) = CollectCallTargets(scan, "relocate_address");
        foreach (string expected in new[]
        {
            "relocate_address",
            "get_region_plan_gen_num",
            "mark_object_simple",
            "get_region_gen_num",
        })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public static void ServerFinalizationRelocationIsWired()
    {
        Type finalize = GetType("Internal.Runtime.GarbageCollection.CFinalize");

        // CFinalize::RelocateFinalizationData relocates every finalizable object through the
        // GCHeap::Relocate callback; for MULTIPLE_HEAPS it no longer short-circuits.
        MethodInfo relocData = finalize.GetMethod(
            "RelocateFinalizationData",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.NotNull(relocData);
        (string[] names, _) = CollectCallTargets(relocData, "relocate");
        Assert.Contains("relocate", names);
        Assert.Contains("seg_queue", names);
    }

    [Fact]
    public static void ServerCompactPhaseHasNativeSignature()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // void gc_heap::compact_phase (int condemned_gen_number, uint8_t* first_condemned_address,
        // BOOL clear_cards), translated as a per-heap static taking the owning heap explicitly:
        // compact_phase (gc_heap*, int, byte*, int).
        MethodInfo compact = heap.GetMethod(
            "compact_phase",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(void), compact.ReturnType);
        ParameterInfo[] p = compact.GetParameters();
        Assert.Equal(4, p.Length);
        Assert.Equal(heap.MakePointerType(), p[0].ParameterType);
        Assert.Equal(typeof(int), p[1].ParameterType);
        Assert.Equal(typeof(byte).MakePointerType(), p[2].ParameterType);
        Assert.Equal(typeof(int), p[3].ParameterType);
    }

    [Fact]
    public static void ServerCompactPhaseSequencesCompactExecution()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");
        MethodInfo compact = heap.GetMethod(
            "compact_phase",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

        // compact_phase opens with the gc_join_relocate_phase_done join, compacts the LOH plan
        // (compact_loh), walks each condemned SOH region's brick tree (compact_in_brick), recovers the
        // saved pinned-plug info (recover_saved_pinned_info), and finalizes each region's used pointer
        // (clear_unused_bricks_after_compaction). The pinned-plug-queue leaves and get_start_segment
        // are threaded through the owning heap.
        (string[] names, _) = CollectCallTargets(compact, "compact_in_brick");
        foreach (string expected in new[]
        {
            "join",
            "joined",
            "restart",
            "compact_loh",
            "reset_pinned_queue_bos",
            "update_oldest_pinned_plug",
            "expand_reused_seg_p",
            "get_stop_generation_index",
            "get_start_segment",
            "compact_in_brick",
            "recover_saved_pinned_info",
            "clear_unused_bricks_after_compaction",
        })
        {
            Assert.Contains(expected, names);
        }

        // compact_phase runs after relocate_phase; it must not relocate references or run the sweep
        // (make_free_lists) branch.
        foreach (string deferred in new[]
        {
            "relocate_phase",
            "relocate_survivors",
            "make_free_lists",
        })
        {
            Assert.DoesNotContain(deferred, names);
        }
    }

    [Fact]
    public static void ServerCompactInBrickThreadsOwningHeapPinnedQueue()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // compact_in_brick (gc_heap*, byte*, compact_args*): walks a brick's plug tree in address
        // order, threading the owning heap's pinned-plug queue (get_oldest_pinned_entry) as each
        // oldest pin is reached, and compacts each plug through compact_plug.
        MethodInfo inBrick = heap.GetMethod(
            "compact_in_brick",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(void), inBrick.ReturnType);
        ParameterInfo[] p = inBrick.GetParameters();
        Assert.Equal(3, p.Length);
        Assert.Equal(heap.MakePointerType(), p[0].ParameterType);
        Assert.Equal(typeof(byte).MakePointerType(), p[1].ParameterType);

        (string[] names, _) = CollectCallTargets(inBrick, "compact_plug");
        foreach (string expected in new[]
        {
            "compact_plug",
            "get_oldest_pinned_entry",
            "compact_in_brick",
            "node_left_child",
            "node_right_child",
        })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public static void ServerCompactPlugMovesPlugThroughGcMemCopy()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // compact_plug (gc_heap*, byte*, nuint, int, compact_args*): moves a plug to its planned
        // location through gcmemcopy and repairs the destination brick table (set_brick), swapping the
        // saved pre/post-plug words around the move for shortened plugs.
        MethodInfo plug = heap.GetMethod(
            "compact_plug",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        ParameterInfo[] p = plug.GetParameters();
        Assert.Equal(5, p.Length);
        Assert.Equal(heap.MakePointerType(), p[0].ParameterType);

        (string[] names, _) = CollectCallTargets(plug, "gcmemcopy");
        foreach (string expected in new[]
        {
            "gcmemcopy",
            "set_brick",
            "brick_of",
            "swap_pre_plug_and_saved",
            "swap_post_plug_and_saved",
        })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public static void ServerGcMemCopyCarriesBookkeeping()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // gcmemcopy copies the mark bits during a concurrent mark (copy_mark_bits_for_addresses),
        // consumes the DOUBLY_LINKED_FL bgc-mark / free-obj-in-compact bits, memcopies the plug, and
        // copies or clears the cards (copy_cards_range).
        MethodInfo memcopy = heap.GetMethod(
            "gcmemcopy",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        (string[] names, _) = CollectCallTargets(memcopy, "copy_cards_range");
        foreach (string expected in new[]
        {
            "memcopy",
            "copy_cards_range",
            "copy_mark_bits_for_addresses",
            "is_plug_bgc_mark_bit_set",
            "is_free_obj_in_compact_bit_set",
        })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public static void ServerCompactLohSlidesMarkedObjects()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // compact_loh (gc_heap*): slides every marked LOH object to its planned location through
        // gcmemcopy, threading the pad gaps (thread_gap) and consuming this heap's LOH pinned queue
        // (loh_deque_pinned_plug).
        MethodInfo compactLoh = heap.GetMethod(
            "compact_loh",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(void), compactLoh.ReturnType);
        ParameterInfo[] p = compactLoh.GetParameters();
        Assert.Single(p);
        Assert.Equal(heap.MakePointerType(), p[0].ParameterType);

        (string[] names, _) = CollectCallTargets(compactLoh, "gcmemcopy");
        foreach (string expected in new[]
        {
            "gcmemcopy",
            "thread_gap",
            "loh_deque_pinned_plug",
            "loh_node_relocation_distance",
            "update_start_tail_regions",
        })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public static void ServerRecoverSavedPinnedInfoIsHeapParameterized()
    {
        Type heap = GetType("Internal.Runtime.GarbageCollection.gc_heap");

        // nuint gc_heap::recover_saved_pinned_info (): restores each pinned plug's saved pre/post-plug
        // words, translated per-heap as recover_saved_pinned_info (gc_heap*) so it drains the owning
        // heap's pinned-plug queue.
        MethodInfo recover = heap.GetMethod(
            "recover_saved_pinned_info",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(nuint), recover.ReturnType);
        ParameterInfo[] p = recover.GetParameters();
        Assert.Single(p);
        Assert.Equal(heap.MakePointerType(), p[0].ParameterType);

        (string[] names, _) = CollectCallTargets(recover, "recover_plug_info");
        foreach (string expected in new[]
        {
            "reset_pinned_queue_bos",
            "pinned_plug_que_empty_p",
            "oldest_pin",
            "recover_plug_info",
            "deque_pinned_plug",
        })
        {
            Assert.Contains(expected, names);
        }
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
