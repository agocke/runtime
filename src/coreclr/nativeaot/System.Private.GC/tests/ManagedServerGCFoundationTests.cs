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
