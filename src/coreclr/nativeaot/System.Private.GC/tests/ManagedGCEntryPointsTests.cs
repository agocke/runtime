// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using Xunit;

namespace Internal.Runtime.GarbageCollection;

[Collection(SyncImportsCollection.Name)]
public sealed unsafe class ManagedGCEntryPointsTests : IDisposable
{
    private const int S_OK = 0;
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    private const string TableResourceName = "GCInterfaceOffsets.h";

    private static readonly KeyValuePair<FieldInfo, object>[] s_declaredConfigValues = CaptureDeclaredConfigValues();
    private static readonly Dictionary<string, int> s_interfaceConstants = ReadInterfaceConstants();
#if USE_REGIONS
    private static int s_describedGenerationCount;
    private static int s_describedGenerationFailure;
#endif

    public ManagedGCEntryPointsTests()
    {
        ResetState();
    }

    public void Dispose()
    {
        ResetState();
    }

    [Fact]
    public void VersionInfoReportsAbiAndCapturesRuntimeVersion()
    {
        byte* runtimeName = stackalloc byte[] { (byte)'E', (byte)'E', 0 };
        VersionInfo info = default;
        info.MajorVersion = 4;
        info.MinorVersion = 3;
        info.BuildVersion = 2;
        info.Name = runtimeName;

        ManagedGCEntryPoints.ManagedGC_VersionInfo(&info);

        VersionInfo runtimeSupportedVersion = ManagedGCEntryPoints.RuntimeSupportedVersion;
        Assert.Equal(4u, runtimeSupportedVersion.MajorVersion);
        Assert.Equal(3u, runtimeSupportedVersion.MinorVersion);
        Assert.Equal(2u, runtimeSupportedVersion.BuildVersion);
        Assert.Equal((nint)runtimeName, (nint)runtimeSupportedVersion.Name);

        Assert.Equal((uint)s_interfaceConstants["GC_INTERFACE_MAJOR_VERSION"], info.MajorVersion);
        Assert.Equal((uint)s_interfaceConstants["GC_INTERFACE_MINOR_VERSION"], info.MinorVersion);
        Assert.Equal(0u, info.BuildVersion);
        Assert.Equal("CoreCLR GC", ReadNullTerminatedUtf8(info.Name));
    }

    [Fact]
    public void InitializeFailsWhenClrToGcIsNull()
    {
        void* gcHeap = (void*)1;
        void* gcHandleManager = (void*)1;
        GcDacVars gcDacVars = default;

        int result = ManagedGCEntryPoints.ManagedGC_Initialize(null, &gcHeap, &gcHandleManager, &gcDacVars);

        Assert.Equal(E_FAIL, result);
        Assert.Equal(0, (nint)gcHeap);
        Assert.Equal(0, (nint)gcHandleManager);
        Assert.Equal(0, GCToEEInterface.InitializeCallCount);
        Assert.Equal(0, GCInterfaceLayout.VerifyCallCount);
        Assert.Equal(0, ManagedGCHandleManager.CreateCallCount);
        Assert.Equal(0, ManagedGCHeap.CreateCallCount);
    }

    [Fact]
    public void InitializeFailsWhenLayoutVerificationFails()
    {
        void* clrToGC = (void*)0x1234;
        void* gcHeap = (void*)1;
        void* gcHandleManager = (void*)1;
        GcDacVars gcDacVars = default;
        GCInterfaceLayout.VerifyResult = false;

        int result = ManagedGCEntryPoints.ManagedGC_Initialize(clrToGC, &gcHeap, &gcHandleManager, &gcDacVars);

        Assert.Equal(E_FAIL, result);
        Assert.Equal(0, (nint)gcHeap);
        Assert.Equal(0, (nint)gcHandleManager);
        Assert.Equal(1, GCToEEInterface.InitializeCallCount);
        Assert.Equal((nint)clrToGC, (nint)GCToEEInterface.LastInitializedGCToCLR);
        Assert.Equal(1, GCInterfaceLayout.VerifyCallCount);
        Assert.Equal(0, ManagedGCHandleManager.CreateCallCount);
        Assert.Equal(0, ManagedGCHeap.CreateCallCount);
    }

    [Fact]
    public void InitializeReturnsOutOfMemoryWhenHandleManagerCreationFails()
    {
        void* clrToGC = (void*)0x1234;
        void* gcHeap = (void*)1;
        void* gcHandleManager = (void*)1;
        GcDacVars gcDacVars = default;
        gcDacVars.major_version_number = 5;
        gcDacVars.minor_version_number = 8;
        ManagedGCHandleManager.SetCreateResult(null);

        int result = ManagedGCEntryPoints.ManagedGC_Initialize(clrToGC, &gcHeap, &gcHandleManager, &gcDacVars);

        Assert.Equal(E_OUTOFMEMORY, result);
        Assert.Equal(0, (nint)gcHeap);
        Assert.Equal(0, (nint)gcHandleManager);
        Assert.Equal(1, GCToEEInterface.InitializeCallCount);
        Assert.Equal((nint)clrToGC, (nint)GCToEEInterface.LastInitializedGCToCLR);
        Assert.Equal(1, GCInterfaceLayout.VerifyCallCount);
        Assert.Equal(1, ManagedGCHandleManager.CreateCallCount);
        Assert.Equal(0, ManagedGCHeap.CreateCallCount);
        Assert.Equal(5, gcDacVars.major_version_number);
        Assert.Equal(8, gcDacVars.minor_version_number);
    }

    [Fact]
    public void InitializeReturnsOutOfMemoryWhenHeapCreationFails()
    {
        void* clrToGC = (void*)0x1234;
        void* gcHeap = (void*)1;
        void* gcHandleManager = (void*)1;
        GcDacVars gcDacVars = default;
        gcDacVars.major_version_number = 5;
        gcDacVars.minor_version_number = 8;
        ManagedGCHeap.SetCreateResult(null);

        int result = ManagedGCEntryPoints.ManagedGC_Initialize(clrToGC, &gcHeap, &gcHandleManager, &gcDacVars);

        Assert.Equal(E_OUTOFMEMORY, result);
        Assert.Equal(0, (nint)gcHeap);
        Assert.Equal(0, (nint)gcHandleManager);
        Assert.Equal(1, GCToEEInterface.InitializeCallCount);
        Assert.Equal((nint)clrToGC, (nint)GCToEEInterface.LastInitializedGCToCLR);
        Assert.Equal(1, GCInterfaceLayout.VerifyCallCount);
        Assert.Equal(1, ManagedGCHandleManager.CreateCallCount);
        Assert.Equal(1, ManagedGCHeap.CreateCallCount);
        Assert.Equal(5, gcDacVars.major_version_number);
        Assert.Equal(8, gcDacVars.minor_version_number);
    }

    [Fact]
    public void InitializeSucceedsAndReturnsHeapAndHandleManager()
    {
        void* clrToGC = (void*)0x1234;
        void* expectedHeap = (void*)0x1111;
        void* expectedHandleManager = (void*)0x2222;
        void* gcHeap = (void*)1;
        void* gcHandleManager = (void*)1;
        GcDacVars gcDacVars = default;
        gcDacVars.major_version_number = 5;
        gcDacVars.minor_version_number = 8;
        ManagedGCHeap.SetCreateResult(expectedHeap);
        ManagedGCHandleManager.SetCreateResult(expectedHandleManager);

        int result = ManagedGCEntryPoints.ManagedGC_Initialize(clrToGC, &gcHeap, &gcHandleManager, &gcDacVars);

        Assert.Equal(S_OK, result);
        Assert.Equal((nint)expectedHeap, (nint)gcHeap);
        Assert.Equal((nint)expectedHandleManager, (nint)gcHandleManager);
        Assert.Equal(1, GCToEEInterface.InitializeCallCount);
        Assert.Equal((nint)clrToGC, (nint)GCToEEInterface.LastInitializedGCToCLR);
        Assert.Equal(1, GCInterfaceLayout.VerifyCallCount);
        Assert.Equal(1, ManagedGCHandleManager.CreateCallCount);
        Assert.Equal(1, ManagedGCHeap.CreateCallCount);
        Assert.Equal(5, gcDacVars.major_version_number);
        Assert.Equal(8, gcDacVars.minor_version_number);
#if USE_REGIONS
        Assert.Equal(GCSpinLock.lock_free, GCWriteBarrier.write_barrier_spin_lock.@lock);
        Assert.Equal(nuint.MaxValue, (nuint)gc_heap.ephemeral_low);
        Assert.Equal((nuint)0, (nuint)gc_heap.ephemeral_high);
#if DEBUG
        Assert.Equal(nuint.MaxValue, (nuint)GCWriteBarrier.write_barrier_spin_lock.holding_thread);
#endif
#endif
    }

#if USE_REGIONS
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void FullGen2CollectionRejectsUnsupportedStateBeforeMutation(int guard)
    {
        GCConfig.Initialize();
        GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.None, GCEventLevel.None);
        GCEventStatus.Set(GCEventProvider.Private, GCEventKeyword.None, GCEventLevel.None);
        gc_heap.settings = default;
        gc_heap.settings.gc_index = 37;
        gc_heap.settings.condemned_generation = 1;
        gc_heap.current_bgc_state = bgc_state.bgc_not_in_process;
        gc_heap.gc_background_running = 0;

        int generation = GCInterfaceOffsets.max_generation;
        int mode = (int)collection_mode.collection_blocking;
        switch (guard)
        {
            case 0:
                generation = (int)gc_generation_num.soh_gen0;
                break;
            case 1:
                mode = (int)collection_mode.collection_non_blocking;
                break;
            case 2:
                mode = (int)collection_mode.collection_optimized;
                break;
            case 3:
                mode = (int)collection_mode.collection_aggressive;
                break;
            case 4:
                SetConfigByte("s_ServerGC", 1);
                break;
            case 5:
                SetConfigValue("s_HeapVerifyLevel", 1);
                break;
            case 6:
                GCToEEInterface.AnalyzeSurvivorsRequestedResult = 1;
                break;
            case 7:
                GCEventStatus.Set(
                    GCEventProvider.Default,
                    GCEventKeyword.GC,
                    GCEventLevel.Information);
                break;
            case 8:
                gc_heap.gc_background_running = 1;
                break;
        }

        int result = gc_heap.garbage_collect_synchronous_full_gen2(
            generation,
            low_memory_p: 0,
            mode);

        Assert.Equal(gc_heap.collection_e_notimpl, result);
        Assert.Equal((nuint)37, gc_heap.settings.gc_index);
        Assert.Equal(1, gc_heap.settings.condemned_generation);
        Assert.Empty(GCToEEInterface.CollectionLifecycleCallOrder);
        Assert.Equal(0, GCToEEInterface.SuspendEECallCount);
        Assert.Equal(0, GCToEEInterface.RestartEECallCount);
    }

    [Fact]
    public void FullGen2CollectionOwnsNativeLifecycleOrder()
    {
        GCToOSInterface.ResetRecording();
        GCConfig.Initialize();
        GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.None, GCEventLevel.None);
        GCEventStatus.Set(GCEventProvider.Private, GCEventKeyword.None, GCEventLevel.None);
        GCCommon.initialize();
        Assert.True(gc_heap.check_commit_cs.Initialize());
        gc_heap.initialize_gc_static_state();
        Assert.Equal(S_OK, ManagedGCRegionBootstrap.Prepare());
        Assert.True(gc_heap.initialize_mark_list());
        Assert.True(gc_heap.initialize_mark_stack());
        Assert.True(ManagedGCRegionBootstrap.Initialize());
        gc_heap.finalize_queue = CFinalize.Allocate();
        Assert.True(gc_heap.finalize_queue is not null);

        try
        {
            int result = gc_heap.garbage_collect_synchronous_full_gen2(
                GCInterfaceOffsets.max_generation,
                low_memory_p: 0,
                (int)collection_mode.collection_blocking |
                    (int)collection_mode.collection_compacting);

            Assert.True(
                result == S_OK,
                $"result={result}; {string.Join(", ", GCToEEInterface.CollectionLifecycleCallOrder)}");
            Assert.Equal(1, GCToEEInterface.SuspendEECallCount);
            Assert.Equal(SUSPEND_REASON.SUSPEND_FOR_GC, GCToEEInterface.LastSuspendReason);
            Assert.Equal(1, GCToEEInterface.GcStartWorkCallCount);
            Assert.Equal(GCInterfaceOffsets.max_generation, GCToEEInterface.LastGcStartWorkCondemned);
            Assert.Equal(GCInterfaceOffsets.max_generation, GCToEEInterface.LastGcStartWorkMaxGeneration);
            Assert.Equal(1, GCToEEInterface.GcDoneCallCount);
            Assert.Equal(GCInterfaceOffsets.max_generation, GCToEEInterface.LastGcDoneCondemned);
            Assert.Equal(1, GCToEEInterface.RestartEECallCount);
            Assert.Equal((byte)1, GCToEEInterface.LastRestartFinishedGC);
            Assert.Equal(1, GCToEEInterface.EnableFinalizationCallCount);
            Assert.Equal(
                new[]
                {
                    CollectionLifecycleCall.Suspend,
                    CollectionLifecycleCall.StartWork,
                    CollectionLifecycleCall.BeforeRoots,
                    CollectionLifecycleCall.ScanRoots,
                    CollectionLifecycleCall.AfterRoots,
                    CollectionLifecycleCall.ScanRoots,
                    CollectionLifecycleCall.Done,
                    CollectionLifecycleCall.Restart,
                    CollectionLifecycleCall.EnableFinalization,
                },
                GCToEEInterface.CollectionLifecycleCallOrder);
            Assert.Equal((nuint)1, dynamic_data.dd_collection_count(
                gc_heap.dynamic_data_of(
                    ManagedGCRegionBootstrap.Heap,
                    (int)gc_generation_num.soh_gen0)));
            Assert.Equal((nuint)1, gc_heap.settings.gc_index);
            Assert.Equal((nuint)1, gc_heap.full_gc_counts[gc_heap.gc_type_blocking]);
            Assert.Equal((nuint)1, gc_heap.last_full_blocking_gc_info.index);
            Assert.Equal(
                (byte)GCInterfaceOffsets.max_generation,
                gc_heap.last_full_blocking_gc_info.condemned_generation);
            Assert.Equal((byte)1, gc_heap.last_full_blocking_gc_info.compaction);
            Assert.Equal((byte)0, gc_heap.last_full_blocking_gc_info.concurrent);
            Assert.Equal(
                gc_heap.current_total_committed,
                gc_heap.last_full_blocking_gc_info.total_committed);
            Assert.Equal(
                gc_heap.get_total_heap_size(ManagedGCRegionBootstrap.Heap),
                gc_heap.last_full_blocking_gc_info.heap_size);
            Assert.Equal(
                gc_heap.get_total_promoted(ManagedGCRegionBootstrap.Heap),
                gc_heap.last_full_blocking_gc_info.promoted);
            Assert.Equal(
                (nuint)gc_heap.num_pinned_objects,
                gc_heap.last_full_blocking_gc_info.pinned_objects);
            Assert.Equal(
                gc_heap.finalize_queue->GetPromotedCount(),
                gc_heap.last_full_blocking_gc_info.finalize_promoted_objects);
        }
        finally
        {
            CFinalize.Free(gc_heap.finalize_queue);
            gc_heap.finalize_queue = null;
            ManagedGCRegionBootstrap.Shutdown();
            gc_heap.destroy_semi_shared();
            gc_heap.check_commit_cs.Destroy();
        }
    }

    [Fact]
    public void BackgroundGen2CollectionRunsThroughWorkerStateMachine()
    {
        GCToOSInterface.ResetRecording();
        GCConfig.Initialize();
        GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.None, GCEventLevel.None);
        GCEventStatus.Set(GCEventProvider.Private, GCEventKeyword.None, GCEventLevel.None);
        GCCommon.initialize();
        Assert.True(gc_heap.check_commit_cs.Initialize());
        gc_heap.initialize_gc_static_state();
        Assert.Equal(S_OK, ManagedGCRegionBootstrap.Prepare());
        Assert.True(gc_heap.initialize_mark_list());
        Assert.True(gc_heap.initialize_mark_stack());
        Assert.True(ManagedGCRegionBootstrap.Initialize());
        Assert.True(gc_heap.initialize_background_gc());
        gc_heap.finalize_queue = CFinalize.Allocate();
        Assert.True(gc_heap.finalize_queue is not null);

        try
        {
            gc_heap.full_gc_counts = default;

            for (int cycle = 0; cycle < 2; cycle++)
            {
                int result = gc_heap.garbage_collect_background(
                    GCInterfaceOffsets.max_generation,
                    low_memory_p: 0,
                    (int)collection_mode.collection_non_blocking);

                Assert.Equal(S_OK, result);
                Assert.Equal(
                    GCEnv.WAIT_OBJECT_0,
                    gc_heap.background_gc_wait(30_000));
            }

            gc_alloc_context allocationContext = default;
            gc_heap.try_allocate_more_space_context allocation = default;
            gc_heap.create_try_allocate_more_space_context(
                ManagedGCRegionBootstrap.Heap,
                &allocationContext,
                (nuint)GCInterfaceOffsets.min_obj_size,
                0,
                (int)gc_generation_num.loh_generation,
                &allocation);
            delegate*<gc_heap.try_allocate_more_space_context*, int, gc_heap.allocation_callback_result*, void> callback =
                gc_heap.managed_allocation_callback();
            gc_heap.allocation_callback_result callbackResult = default;
            callback(
                &allocation,
                (int)gc_heap.allocation_deferred_operation.enter_more_space_lock,
                &callbackResult);
            Assert.Equal(gc_heap.allocation_callback_result_kind.completed, callbackResult.kind);
            callback(
                &allocation,
                (int)gc_heap.allocation_deferred_operation.trigger_gc_for_budget,
                &callbackResult);
            Assert.Equal(gc_heap.allocation_callback_result_kind.completed, callbackResult.kind);
            callback(
                &allocation,
                (int)gc_heap.allocation_deferred_operation.leave_more_space_lock,
                &callbackResult);
            Assert.Equal(gc_heap.allocation_callback_result_kind.completed, callbackResult.kind);
            Assert.Equal(
                GCEnv.WAIT_OBJECT_0,
                gc_heap.background_gc_wait(30_000));

            Assert.False(gc_heap.background_collection_running_p());
            Assert.Equal(bgc_state.bgc_not_in_process, gc_heap.current_bgc_state);
            Assert.Equal((nuint)3, gc_heap.full_gc_counts[gc_heap.gc_type_background]);
            Assert.Equal(1, GCToEEInterface.BackgroundThreadCreateCount);
            Assert.Equal(3, GCToEEInterface.BackgroundThreadCycleCount);
            Assert.True(gc_heap.background_state_was_observed(bgc_state.bgc_initialized));
            Assert.True(gc_heap.background_state_was_observed(bgc_state.bgc_reset_ww));
            Assert.True(gc_heap.background_state_was_observed(bgc_state.bgc_mark_handles));
            Assert.True(gc_heap.background_state_was_observed(bgc_state.bgc_mark_stack));
            Assert.True(gc_heap.background_state_was_observed(bgc_state.bgc_revisit_soh));
            Assert.True(gc_heap.background_state_was_observed(bgc_state.bgc_revisit_uoh));
            Assert.True(gc_heap.background_state_was_observed(bgc_state.bgc_final_marking));
            Assert.True(gc_heap.background_state_was_observed(bgc_state.bgc_sweep_soh));
            Assert.True(gc_heap.background_state_was_observed(bgc_state.bgc_sweep_uoh));
            Assert.True(gc_heap.background_state_was_observed(bgc_state.bgc_not_in_process));
            Assert.Equal(6, GCToEEInterface.SuspendEECallCount);
            Assert.Equal(6, GCToEEInterface.RestartEECallCount);
            Assert.Equal((byte)1, GCToEEInterface.LastRestartFinishedGC);
            Assert.Equal(3, GCToEEInterface.GcStartWorkCallCount);
            Assert.Equal(3, GCToEEInterface.GcDoneCallCount);
            Assert.Equal(6, GCToEEInterface.BeforeGcScanRootsCallCount);
            Assert.Equal((byte)1, GCToEEInterface.LastBeforeGcScanRootsIsBackground);
            Assert.Equal((byte)0, GCToEEInterface.LastBeforeGcScanRootsIsConcurrent);
            ref last_recorded_gc_info backgroundInfo =
                ref gc_heap.background_gc_info(
                    gc_heap.completed_background_gc_info_index());
            Assert.Equal((byte)1, backgroundInfo.concurrent);
            Assert.Equal(
                (byte)GCInterfaceOffsets.max_generation,
                backgroundInfo.condemned_generation);
            Assert.Equal(1, gc_heap.is_last_recorded_bgc);
        }
        finally
        {
            CFinalize.Free(gc_heap.finalize_queue);
            gc_heap.finalize_queue = null;
            gc_heap.destroy_background_gc();
            gc_heap.reset_background_event_for_test();
            ManagedGCRegionBootstrap.Shutdown();
            gc_heap.destroy_semi_shared();
            gc_heap.check_commit_cs.Destroy();
        }
    }

    [Theory]
    [InlineData((int)gc_generation_num.soh_gen0)]
    [InlineData((int)gc_generation_num.soh_gen1)]
    public void ForegroundPartialCollectionRunsDuringBackgroundAndRestoresSettings(
        int generation)
    {
        GCToOSInterface.ResetRecording();
        GCConfig.Initialize();
        GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.None, GCEventLevel.None);
        GCEventStatus.Set(GCEventProvider.Private, GCEventKeyword.None, GCEventLevel.None);
        GCCommon.initialize();
        Assert.True(gc_heap.check_commit_cs.Initialize());
        gc_heap.initialize_gc_static_state();
        Assert.Equal(S_OK, ManagedGCRegionBootstrap.Prepare());
        Assert.True(gc_heap.initialize_mark_list());
        Assert.True(gc_heap.initialize_mark_stack());
        Assert.True(ManagedGCRegionBootstrap.Initialize());
        Assert.True(gc_heap.initialize_background_gc());
        gc_heap.finalize_queue = CFinalize.Allocate();
        Assert.True(gc_heap.finalize_queue is not null);

        try
        {
            gc_heap.request_background_pause_for_test();
            Assert.Equal(
                S_OK,
                gc_heap.garbage_collect_background(
                    GCInterfaceOffsets.max_generation,
                    low_memory_p: 0,
                    (int)collection_mode.collection_non_blocking));
            Assert.True(
                SpinWait.SpinUntil(
                    () => gc_heap.background_pause_observed_for_test(),
                    30_000));

            Thread releaser = new(() =>
            {
                Thread.Sleep(20);
                gc_heap.release_background_pause_for_test();
            });
            releaser.Start();

            Assert.Equal(
                S_OK,
                gc_heap.garbage_collect_synchronous_foreground(
                    generation,
                    low_memory_p: 0,
                    (int)collection_mode.collection_blocking));
            releaser.Join();

            Assert.Equal(
                GCEnv.WAIT_OBJECT_0,
                gc_heap.background_gc_wait(30_000));
            Assert.Equal(1, gc_heap.foreground_during_bgc_count_for_test());
            gc_mechanisms restored =
                gc_heap.last_restored_bgc_settings_for_test();
            Assert.Equal(1u, restored.concurrent);
            Assert.Equal(1, restored.background_p);
            Assert.Equal(
                GCInterfaceOffsets.max_generation,
                restored.condemned_generation);
            Assert.True(
                dynamic_data.dd_collection_count(
                    gc_heap.dynamic_data_of(
                        ManagedGCRegionBootstrap.Heap,
                        (int)gc_generation_num.soh_gen0)) >= 2);
        }
        finally
        {
            gc_heap.release_background_pause_for_test();
            CFinalize.Free(gc_heap.finalize_queue);
            gc_heap.finalize_queue = null;
            gc_heap.destroy_background_gc();
            gc_heap.reset_background_event_for_test();
            ManagedGCRegionBootstrap.Shutdown();
            gc_heap.destroy_semi_shared();
            gc_heap.check_commit_cs.Destroy();
        }
    }

    [Theory]
    [InlineData((int)gc_generation_num.soh_gen0, true)]
    [InlineData((int)gc_generation_num.soh_gen1, true)]
    [InlineData((int)gc_generation_num.soh_gen1, false)]
    public void ForegroundPartialCollectionOwnsLifecycleWithoutFullGcAccounting(
        int generation,
        bool compacting)
    {
        GCToOSInterface.ResetRecording();
        GCConfig.Initialize();
        GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.None, GCEventLevel.None);
        GCEventStatus.Set(GCEventProvider.Private, GCEventKeyword.None, GCEventLevel.None);
        GCCommon.initialize();
        Assert.True(gc_heap.check_commit_cs.Initialize());
        gc_heap.initialize_gc_static_state();
        Assert.Equal(S_OK, ManagedGCRegionBootstrap.Prepare());
        Assert.True(gc_heap.initialize_mark_list());
        Assert.True(gc_heap.initialize_mark_stack());
        Assert.True(ManagedGCRegionBootstrap.Initialize());
        gc_heap.finalize_queue = CFinalize.Allocate();
        Assert.True(gc_heap.finalize_queue is not null);

        try
        {
            gc_heap.full_gc_counts = default;
            gc_heap.last_full_blocking_gc_info = default;

            int result = gc_heap.garbage_collect_synchronous_foreground(
                generation,
                low_memory_p: 0,
                (int)collection_mode.collection_blocking |
                    (compacting
                        ? (int)collection_mode.collection_compacting
                        : 0));

            Assert.Equal(S_OK, result);
            Assert.Equal(generation, gc_heap.settings.condemned_generation);
            Assert.Equal(1, GCToEEInterface.SuspendEECallCount);
            Assert.Equal(1, GCToEEInterface.GcStartWorkCallCount);
            Assert.Equal(generation, GCToEEInterface.LastGcStartWorkCondemned);
            Assert.Equal(1, GCToEEInterface.GcDoneCallCount);
            Assert.Equal(generation, GCToEEInterface.LastGcDoneCondemned);
            Assert.Equal(1, GCToEEInterface.RestartEECallCount);
            Assert.Equal((byte)1, GCToEEInterface.LastRestartFinishedGC);
            Assert.Equal(
                (nuint)1,
                dynamic_data.dd_collection_count(
                    gc_heap.dynamic_data_of(
                        ManagedGCRegionBootstrap.Heap,
                        generation)));
            Assert.Equal(
                (nuint)0,
                gc_heap.full_gc_counts[gc_heap.gc_type_blocking]);
            Assert.Equal((nuint)0, gc_heap.last_full_blocking_gc_info.index);
        }
        finally
        {
            CFinalize.Free(gc_heap.finalize_queue);
            gc_heap.finalize_queue = null;
            ManagedGCRegionBootstrap.Shutdown();
            gc_heap.destroy_semi_shared();
            gc_heap.check_commit_cs.Destroy();
        }
    }

    [Fact]
    public void RegionBootstrapConstructsInitialGenerationsWithoutReplacingTheBumpAllocator()
    {
        GCToOSInterface.ResetRecording();
        GCConfig.Initialize();
        GCCommon.initialize();
        Assert.True(gc_heap.check_commit_cs.Initialize());
        Assert.Equal(S_OK, ManagedGCRegionBootstrap.Prepare());

        try
        {
            Assert.True(ManagedGCRegionBootstrap.Initialize());

            generation* generations = ManagedGCRegionBootstrap.GenerationTable;
            heap_segment* ephemeral = ManagedGCRegionBootstrap.EphemeralHeapSegment;
            byte* range = ManagedGCRegionBootstrap.ReservedRegionRange;
            Assert.True(ManagedGCRegionBootstrap.IsInitialized);
            Assert.True(generations is not null);
            Assert.True(ephemeral is not null);
            Assert.True(range is not null);
            Assert.True(ManagedGCRegionBootstrap.ReservedRegionRangeSize >= 19 * gc_heap.DefaultMinSegmentSize);
            Assert.Equal((nuint)ephemeral, (nuint)generation.generation_allocation_segment(
                gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0)));
            Assert.Equal(
                (nuint)heap_segment.heap_segment_allocated(ephemeral),
                (nuint)ManagedGCRegionBootstrap.AllocAllocated);
            Assert.Equal(1, heap_segment.heap_segment_loh_p(
                generation.generation_allocation_segment(
                    gc_heap.generation_of(generations, (int)gc_generation_num.loh_generation))));
            Assert.Equal(1, heap_segment.heap_segment_poh_p(
                generation.generation_allocation_segment(
                    gc_heap.generation_of(generations, (int)gc_generation_num.poh_generation))));
            Assert.True(GCToEEInterface.StompWriteBarrierCallCount > 0);
            WriteBarrierParameters args = GCToEEInterface.LastStompWriteBarrier;
            Assert.Equal(WriteBarrierOp.Initialize, args.operation);
            Assert.Equal((nuint)gc_heap.card_table, (nuint)args.card_table);
            Assert.Equal((nuint)gc_heap.card_bundle_table, (nuint)args.card_bundle_table);
            Assert.Equal((nuint)GCCommon.g_gc_lowest_address, (nuint)args.lowest_address);
            Assert.Equal((nuint)GCCommon.g_gc_highest_address, (nuint)args.highest_address);
            Assert.Equal((nuint)gc_heap.ephemeral_low, (nuint)args.ephemeral_low);
            Assert.Equal((nuint)gc_heap.ephemeral_high, (nuint)args.ephemeral_high);
            Assert.Equal((nuint)gc_heap.map_region_to_generation_skewed, (nuint)args.region_to_generation_table);
            Assert.Equal((byte)gc_heap.min_segment_size_shr, args.region_shr);
            Assert.Equal(
                (nuint)1 << (int)gc_heap.min_segment_size_shr,
                ManagedGCRegionBootstrap.GetValidSegmentSize(largeSegment: false));
            Assert.Equal(
                (nuint)region_allocator.LARGE_REGION_FACTOR << (int)gc_heap.min_segment_size_shr,
                ManagedGCRegionBootstrap.GetValidSegmentSize(largeSegment: true));

            s_describedGenerationCount = 0;
            s_describedGenerationFailure = 0;
            ManagedGCRegionBootstrap.DescribeGenerations(&RecordDescribedGeneration, null);
            Assert.Equal((int)gc_generation_num.total_generation_count, s_describedGenerationCount);
            Assert.Equal(0, s_describedGenerationFailure);
        }
        finally
        {
            ManagedGCRegionBootstrap.Shutdown();
            gc_heap.check_commit_cs.Destroy();
        }

        Assert.False(ManagedGCRegionBootstrap.IsInitialized);
        Assert.True(ManagedGCRegionBootstrap.GenerationTable is null);
        Assert.True(ManagedGCRegionBootstrap.EphemeralHeapSegment is null);
        Assert.True(ManagedGCRegionBootstrap.ReservedRegionRange is null);
        Assert.True(gc_heap.initial_regions is null);
        Assert.True(gc_heap.bookkeeping_start is null);
        Assert.Equal((nuint)0, gc_heap.current_total_committed);
        Assert.Equal((nuint)0, gc_heap.current_total_committed_bookkeeping);
    }

    [UnmanagedCallersOnly]
    private static void RecordDescribedGeneration(
        void* context,
        int generationNumber,
        byte* rangeStart,
        byte* rangeEnd,
        byte* rangeEndReserved)
    {
        generation* currentGeneration = gc_heap.generation_of(
            ManagedGCRegionBootstrap.GenerationTable,
            generationNumber);
        heap_segment* segment = gc_heap.heap_segment_rw(
            generation.generation_start_segment(currentGeneration));
        if (segment is null ||
            rangeStart != heap_segment.heap_segment_mem(segment) ||
            rangeEnd != heap_segment.heap_segment_allocated(segment) ||
            rangeEndReserved != heap_segment.heap_segment_reserved(segment))
        {
            s_describedGenerationFailure = 1;
        }

        s_describedGenerationCount++;
    }

    [Fact]
    public void RegionBootstrapOwnsWksAllocationStateAndCreatesAllocationContexts()
    {
        GCToOSInterface.ResetRecording();
        GCConfig.Initialize();
        GCCommon.initialize();
        Assert.True(gc_heap.check_commit_cs.Initialize());
        Assert.Equal(S_OK, ManagedGCRegionBootstrap.Prepare());

        try
        {
            Assert.True(ManagedGCRegionBootstrap.Initialize());

            gc_heap* heap = ManagedGCRegionBootstrap.Heap;
            generation* generations = ManagedGCRegionBootstrap.GenerationTable;
            heap_segment* ephemeral = ManagedGCRegionBootstrap.EphemeralHeapSegment;
            gc_alloc_context allocationContext = default;
            gc_heap.try_allocate_more_space_context context = default;
            gc_heap.create_try_allocate_more_space_context(
                heap,
                &allocationContext,
                (nuint)GCInterfaceOffsets.min_obj_size,
                0,
                (int)gc_generation_num.soh_gen0,
                &context);

            Assert.True(heap is not null);
            Assert.True(ephemeral is not null);
            Assert.Equal((nuint)generations, (nuint)context.generation_table);
            Assert.Equal((nuint)(generations + 4), (nuint)(&heap->generation_table4));
            Assert.Equal((nuint)(&heap->dynamic_data_table0 + 4), (nuint)(&heap->dynamic_data_table4));
            Assert.True((nuint)(&heap->more_space_lock_soh) >= (nuint)(&heap->dynamic_data_table4 + 1));
            Assert.Equal((nuint)gc_heap.dynamic_data_of(heap, (int)gc_generation_num.soh_gen0), (nuint)context.dd);
            Assert.Equal((nuint)heap, (nuint)context.hp);
            Assert.Equal((nuint)heap->ephemeral_heap_segment, (nuint)(*context.ephemeral_heap_segment));
            Assert.Equal((nuint)heap->alloc_allocated, (nuint)(*context.alloc_allocated));
            Assert.Equal((nuint)gc_heap.DefaultAllocationQuantum, context.allocation_quantum);
            Assert.Equal(allocation_state.a_state_start, context.state);
            Assert.Equal(GCSpinLock.lock_free, heap->more_space_lock_soh.@lock);
            Assert.Equal(GCSpinLock.lock_free, heap->more_space_lock_uoh.@lock);

            gc_heap.create_try_allocate_more_space_context(
                heap,
                &allocationContext,
                (nuint)GCInterfaceOffsets.min_obj_size,
                0,
                (int)gc_generation_num.loh_generation,
                &context);
            Assert.Equal((nuint)(&heap->dynamic_data_table3), (nuint)context.dd);
        }

        finally
        {
            ManagedGCRegionBootstrap.Shutdown();
            gc_heap.check_commit_cs.Destroy();
        }
    }

    [Fact]
    public void RegionBootstrapInitializesDynamicDataAndConfiguredBudgets()
    {
        GCToOSInterface.ResetRecording();
        GCConfig.Initialize();
        SetConfigValue("s_GCGen0MaxBudget", 128L * 1024);
        SetConfigValue("s_GCGen1MaxBudget", 512L * 1024);
        GCCommon.initialize();
        Assert.True(gc_heap.check_commit_cs.Initialize());
        Assert.Equal(S_OK, ManagedGCRegionBootstrap.Prepare());

        try
        {
            Assert.True(ManagedGCRegionBootstrap.Initialize());

            gc_heap* heap = ManagedGCRegionBootstrap.Heap;
            AssertDynamicData(
                gc_heap.dynamic_data_of(heap, (int)gc_generation_num.soh_gen0),
                128 * 1024,
                128 * 1024,
                40_000,
                0.5f,
                9.0f,
                20.0f);
            AssertDynamicData(
                gc_heap.dynamic_data_of(heap, (int)gc_generation_num.soh_gen1),
                256 * 1024,
                512 * 1024,
                80_000,
                0.5f,
                2.0f,
                7.0f);
            AssertDynamicData(
                gc_heap.dynamic_data_of(heap, (int)gc_generation_num.max_generation),
                256 * 1024,
                nuint.MaxValue >> 1,
                200_000,
                0.25f,
                1.2f,
                1.8f);
            AssertDynamicData(
                gc_heap.dynamic_data_of(heap, (int)gc_generation_num.loh_generation),
                3 * 1024 * 1024,
                nuint.MaxValue >> 1,
                0,
                0.0f,
                1.25f,
                4.5f);
            AssertDynamicData(
                gc_heap.dynamic_data_of(heap, (int)gc_generation_num.poh_generation),
                3 * 1024 * 1024,
                nuint.MaxValue >> 1,
                0,
                0.0f,
                1.25f,
                4.5f);
        }
        finally
        {
            ManagedGCRegionBootstrap.Shutdown();
            gc_heap.check_commit_cs.Destroy();
        }
    }

    [Fact]
    public void RegionBootstrapUsesTheNativeConcurrentBudgetDefaults()
    {
        GCToOSInterface.ResetRecording();
        GCConfig.Initialize();
        bool concurrent = GCConfig.GetConcurrentGC() != 0;
        GCCommon.initialize();
        Assert.True(gc_heap.check_commit_cs.Initialize());
        Assert.Equal(S_OK, ManagedGCRegionBootstrap.Prepare());

        try
        {
            Assert.True(ManagedGCRegionBootstrap.Initialize());

            gc_heap* heap = ManagedGCRegionBootstrap.Heap;
            nuint expectedMaxSize = (nuint)(concurrent ? 6 * 1024 * 1024 : 128 * 1024 * 1024);
            Assert.Equal(
                expectedMaxSize,
                dynamic_data.dd_max_size(gc_heap.dynamic_data_of(heap, (int)gc_generation_num.soh_gen0)));
            Assert.Equal(
                expectedMaxSize,
                dynamic_data.dd_max_size(gc_heap.dynamic_data_of(heap, (int)gc_generation_num.soh_gen1)));
        }
        finally
        {
            ManagedGCRegionBootstrap.Shutdown();
            gc_heap.check_commit_cs.Destroy();
        }
    }

    [Theory]
    [InlineData((int)gc_generation_num.soh_gen0)]
    [InlineData((int)gc_generation_num.loh_generation)]
    [InlineData((int)gc_generation_num.poh_generation)]
    public void RegionAllocationRefillsConsumeInitialBudgetsAndStopAfterTheThreshold(int genNumber)
    {
        GCToOSInterface.ResetRecording();
        GCConfig.Initialize();
        SetConfigValue("s_GCGen0MaxBudget", 128L * 1024);
        GCCommon.initialize();
        Assert.True(gc_heap.check_commit_cs.Initialize());
        Assert.Equal(S_OK, ManagedGCRegionBootstrap.Prepare());

        try
        {
            Assert.True(ManagedGCRegionBootstrap.Initialize());

            gc_heap* heap = ManagedGCRegionBootstrap.Heap;
            dynamic_data* dd = gc_heap.dynamic_data_of(heap, genNumber);
            nint initialBudget = dynamic_data.dd_new_allocation(dd);
            Assert.True(initialBudget > 0);

            gc_alloc_context allocationContext = default;
            gc_heap.try_allocate_more_space_context context = default;
            delegate*<gc_heap.try_allocate_more_space_context*, int, gc_heap.allocation_callback_result*, void> callback =
                gc_heap.managed_allocation_callback();

            gc_heap.create_try_allocate_more_space_context(
                heap,
                &allocationContext,
                unchecked((nuint)initialBudget / 2),
                0,
                genNumber,
                &context);
            Assert.True(gc_heap.allocate_more_space(&context, callback));

            nint remainingBudget = dynamic_data.dd_new_allocation(dd);
            Assert.True(remainingBudget > 0);
            Assert.True(remainingBudget < initialBudget);

            for (int refill = 0; refill < 2 && dynamic_data.dd_new_allocation(dd) >= 0; refill++)
            {
                nint budgetBeforeRefill = dynamic_data.dd_new_allocation(dd);
                nuint refillSize = budgetBeforeRefill > (nint)GCInterfaceOffsets.min_obj_size
                    ? unchecked((nuint)budgetBeforeRefill)
                    : (nuint)GCInterfaceOffsets.min_obj_size;

                gc_heap.create_try_allocate_more_space_context(
                    heap,
                    &allocationContext,
                    refillSize,
                    0,
                    genNumber,
                    &context);
                Assert.True(gc_heap.allocate_more_space(&context, callback));
                Assert.True(dynamic_data.dd_new_allocation(dd) < budgetBeforeRefill);
            }

            Assert.True(dynamic_data.dd_new_allocation(dd) < 0);

            gc_heap.allocation_callback_result result = default;
            callback(&context, (int)gc_heap.allocation_deferred_operation.check_allocation_budget, &result);
            Assert.Equal(gc_heap.allocation_callback_result_kind.allocation_disallowed, result.kind);

            gc_heap.create_try_allocate_more_space_context(
                heap,
                &allocationContext,
                (nuint)GCInterfaceOffsets.min_obj_size,
                0,
                genNumber,
                &context);
            Assert.False(gc_heap.allocate_more_space(&context, callback));
        }
        finally
        {
            ManagedGCRegionBootstrap.Shutdown();
            gc_heap.check_commit_cs.Destroy();
        }
    }

    [Theory]
    [InlineData((int)gc_generation_num.soh_gen0)]
    [InlineData((int)gc_generation_num.loh_generation)]
    [InlineData((int)gc_generation_num.poh_generation)]
    public void NonCollectingBootstrapBudgetConsumesInitialRegionsWithoutClaimingCollection(int genNumber)
    {
        GCToOSInterface.ResetRecording();
        GCConfig.Initialize();
        GCCommon.initialize();
        Assert.True(gc_heap.check_commit_cs.Initialize());
        Assert.Equal(S_OK, ManagedGCRegionBootstrap.Prepare());

        try
        {
            Assert.True(ManagedGCRegionBootstrap.Initialize());

            gc_heap* heap = ManagedGCRegionBootstrap.Heap;
            dynamic_data* dd = gc_heap.dynamic_data_of(heap, genNumber);
            dynamic_data.dd_new_allocation(dd) = -unchecked((nint)gc_heap.Align(
                (nuint)GCInterfaceOffsets.min_obj_size,
                gc_heap.get_alignment_constant(genNumber <= (int)gc_generation_num.max_generation)));
            gc_alloc_context allocationContext = default;
            gc_heap.try_allocate_more_space_context context = default;
            gc_heap.create_try_allocate_more_space_context(
                heap,
                &allocationContext,
                (nuint)GCInterfaceOffsets.min_obj_size,
                0,
                genNumber,
                &context);
            gc_heap.enable_non_collecting_bootstrap_budget(&context);

            Assert.True(gc_heap.allocate_more_space(&context, gc_heap.managed_allocation_callback()));
            Assert.Equal((byte)1, context.non_collecting_bootstrap_budget_p);
            Assert.True(dynamic_data.dd_new_allocation(dd) > 0);
            Assert.Equal((nuint)0, dynamic_data.dd_collection_count(dd));
            Assert.True(allocationContext.alloc_ptr is not null);
            if (genNumber == (int)gc_generation_num.soh_gen0)
            {
                Assert.True(allocationContext.alloc_limit - allocationContext.alloc_ptr >
                    GCInterfaceOffsets.min_obj_size);
            }
        }
        finally
        {
            ManagedGCRegionBootstrap.Shutdown();
            gc_heap.check_commit_cs.Destroy();
        }
    }

    [Fact]
    public void RegionBootstrapReinitializesDynamicDataAfterCleanup()
    {
        GCToOSInterface.ResetRecording();
        GCConfig.Initialize();
        SetConfigValue("s_GCGen0MaxBudget", 128L * 1024);
        GCCommon.initialize();
        Assert.True(gc_heap.check_commit_cs.Initialize());
        Assert.Equal(S_OK, ManagedGCRegionBootstrap.Prepare());

        try
        {
            Assert.True(ManagedGCRegionBootstrap.Initialize());
            dynamic_data* first = gc_heap.dynamic_data_of(
                ManagedGCRegionBootstrap.Heap,
                (int)gc_generation_num.soh_gen0);
            dynamic_data.dd_new_allocation(first) = -1;

            ManagedGCRegionBootstrap.Shutdown();
            Assert.False(ManagedGCRegionBootstrap.IsInitialized);
            Assert.True(ManagedGCRegionBootstrap.Heap is null);

            Assert.True(ManagedGCRegionBootstrap.Initialize());
            dynamic_data* second = gc_heap.dynamic_data_of(
                ManagedGCRegionBootstrap.Heap,
                (int)gc_generation_num.soh_gen0);
            Assert.Equal((nint)(128 * 1024), dynamic_data.dd_new_allocation(second));
            Assert.Equal((nuint)128 * 1024, dynamic_data.dd_desired_allocation(second));
            Assert.Equal((nuint)128 * 1024, dynamic_data.dd_min_size(second));
            Assert.True(second->sdata is not null);
        }
        finally
        {
            ManagedGCRegionBootstrap.Shutdown();
            gc_heap.check_commit_cs.Destroy();
        }
    }

    [Fact]
    public void WksAllocationCallbackUsesOwnedLocksAndChecksNotifications()
    {
        GCToOSInterface.ResetRecording();
        GCConfig.Initialize();
        GCCommon.initialize();
        Assert.True(gc_heap.check_commit_cs.Initialize());
        Assert.Equal(S_OK, ManagedGCRegionBootstrap.Prepare());

        try
        {
            Assert.True(ManagedGCRegionBootstrap.Initialize());

            gc_heap* heap = ManagedGCRegionBootstrap.Heap;
            gc_alloc_context allocationContext = default;
            gc_heap.try_allocate_more_space_context context = default;
            gc_heap.create_try_allocate_more_space_context(
                heap,
                &allocationContext,
                (nuint)GCInterfaceOffsets.min_obj_size,
                0,
                (int)gc_generation_num.soh_gen0,
                &context);
            delegate*<gc_heap.try_allocate_more_space_context*, int, gc_heap.allocation_callback_result*, void> callback =
                gc_heap.managed_allocation_callback();
            gc_heap.allocation_callback_result result = default;

            callback(&context, (int)gc_heap.allocation_deferred_operation.enter_more_space_lock, &result);
            Assert.Equal(gc_heap.allocation_callback_result_kind.completed, result.kind);
            Assert.Equal(0, heap->more_space_lock_soh.@lock);

            callback(&context, (int)gc_heap.allocation_deferred_operation.leave_more_space_lock, &result);
            Assert.Equal(gc_heap.allocation_callback_result_kind.completed, result.kind);
            Assert.Equal(GCSpinLock.lock_free, heap->more_space_lock_soh.@lock);

            dynamic_data.dd_new_allocation(context.dd) = -1;
            callback(&context, (int)gc_heap.allocation_deferred_operation.check_allocation_budget, &result);
            Assert.Equal(gc_heap.allocation_callback_result_kind.allocation_disallowed, result.kind);

            dynamic_data.dd_new_allocation(context.dd) = 0;
            callback(&context, (int)gc_heap.allocation_deferred_operation.check_allocation_budget, &result);
            Assert.Equal(gc_heap.allocation_callback_result_kind.allocation_allowed, result.kind);

            dynamic_data.dd_min_size(context.dd) = 1;
            dynamic_data.dd_new_allocation(context.dd) = 0;
            heap->allocation_running_amount = 2;
            heap->allocation_running_time = 0;
            callback(&context, (int)gc_heap.allocation_deferred_operation.check_allocation_budget, &result);
            Assert.Equal(gc_heap.allocation_callback_result_kind.allocation_disallowed, result.kind);

            heap->allocation_running_time = GCToOSInterface.GetLowPrecisionTimeStamp();
            callback(&context, (int)gc_heap.allocation_deferred_operation.check_allocation_budget, &result);
            Assert.Equal(gc_heap.allocation_callback_result_kind.allocation_allowed, result.kind);
            Assert.Equal((nuint)0, heap->allocation_running_amount);

            callback(&context, (int)gc_heap.allocation_deferred_operation.wait_for_bgc_high_memory, &result);
            Assert.Equal(gc_heap.allocation_callback_result_kind.background_not_running, result.kind);

            callback(&context, (int)gc_heap.allocation_deferred_operation.check_and_wait_for_bgc, &result);
            Assert.Equal(gc_heap.allocation_callback_result_kind.background_not_running, result.kind);

            callback(&context, (int)gc_heap.allocation_deferred_operation.check_retry_other_heap, &result);
            Assert.Equal(gc_heap.allocation_callback_result_kind.completed, result.kind);

            callback(&context, (int)gc_heap.allocation_deferred_operation.handle_oom, &result);
            Assert.Equal(gc_heap.allocation_callback_result_kind.completed, result.kind);

            context.full_gc_notification_p = 1;
            callback(&context, (int)gc_heap.allocation_deferred_operation.check_for_full_gc, &result);
            Assert.Equal(gc_heap.allocation_callback_result_kind.completed, result.kind);
        }
        finally
        {
            ManagedGCRegionBootstrap.Shutdown();
            gc_heap.check_commit_cs.Destroy();
        }
    }

    [Fact]
    public void WksNoGCRegionPreservesBudgetsStatusesAndCallbackCleanup()
    {
        GCToOSInterface.ResetRecording();
        GCConfig.Initialize();
        GCCommon.initialize();
        gc_heap.initialize_gc_static_state();
        Assert.True(gc_heap.check_commit_cs.Initialize());
        Assert.Equal(S_OK, ManagedGCRegionBootstrap.Prepare());

        try
        {
            Assert.True(ManagedGCRegionBootstrap.Initialize());
            gc_heap* heap = ManagedGCRegionBootstrap.Heap;
            gc_heap.settings.pause_mode = gc_pause_mode.pause_interactive;

            Assert.Equal(
                enable_no_gc_region_callback_status.not_started,
                gc_heap.enable_no_gc_callback(heap, null, 1));

            Assert.Equal(
                start_no_gc_region_status.start_no_gc_success,
                gc_heap.prepare_for_no_gc_region(
                    256 * 1024,
                    loh_size_known: true,
                    64 * 1024,
                    disallow_full_blocking: true));
            Assert.Equal(gc_pause_mode.pause_no_gc, gc_heap.settings.pause_mode);
            Assert.False(gc_heap.should_proceed_with_gc(heap));
            Assert.Equal((nuint)1, gc_heap.current_no_gc_region_info.started);
            Assert.True(gc_heap.soh_allocation_no_gc >= 192 * 1024);
            Assert.True(gc_heap.loh_allocation_no_gc >= 64 * 1024);
            Assert.Equal(
                (nint)gc_heap.soh_allocation_no_gc,
                dynamic_data.dd_new_allocation(gc_heap.dynamic_data_of(
                    heap,
                    (int)gc_generation_num.soh_gen0)));
            Assert.Equal(
                (nint)gc_heap.loh_allocation_no_gc,
                dynamic_data.dd_new_allocation(gc_heap.dynamic_data_of(
                    heap,
                    (int)gc_generation_num.loh_generation)));

            NoGCRegionCallbackFinalizerWorkItem callback = default;
            Assert.Equal(
                enable_no_gc_region_callback_status.succeed,
                gc_heap.enable_no_gc_callback(heap, &callback, 64 * 1024));
            Assert.Equal(2, GCToEEInterface.SuspendEECallCount);
            Assert.Equal(2, GCToEEInterface.RestartEECallCount);
            Assert.Equal(
                enable_no_gc_region_callback_status.already_registered,
                gc_heap.enable_no_gc_callback(heap, &callback, 32 * 1024));

            Assert.Equal(
                end_no_gc_region_status.end_no_gc_success,
                gc_heap.end_no_gc_region());
            Assert.Equal(gc_pause_mode.pause_interactive, gc_heap.settings.pause_mode);
            Assert.Equal((byte)1, callback.scheduled);
            Assert.Equal((byte)1, callback.abandoned);
            Assert.Equal(1, GCToEEInterface.EnableFinalizationCallCount);
            Assert.Equal(
                (nint)(&callback),
                (nint)gc_heap.get_extra_work_for_finalization());

            Assert.Equal(
                end_no_gc_region_status.end_no_gc_not_in_progress,
                gc_heap.end_no_gc_region());

            gc_heap.current_no_gc_region_info.started = 1;
            gc_heap.current_no_gc_region_info.num_gcs = 1;
            gc_heap.settings.pause_mode = gc_pause_mode.pause_interactive;
            Assert.Equal(
                end_no_gc_region_status.end_no_gc_alloc_exceeded,
                gc_heap.end_no_gc_region());

            gc_heap.current_no_gc_region_info.started = 1;
            gc_heap.current_no_gc_region_info.num_gcs = 1;
            gc_heap.current_no_gc_region_info.num_gcs_induced = 1;
            Assert.Equal(
                end_no_gc_region_status.end_no_gc_induced,
                gc_heap.end_no_gc_region());

            gc_heap.settings.pause_mode = gc_pause_mode.pause_interactive;
            Assert.Equal(
                start_no_gc_region_status.start_no_gc_too_large,
                gc_heap.prepare_for_no_gc_region(
                    ulong.MaxValue,
                    loh_size_known: false,
                    0,
                    disallow_full_blocking: false));
            Assert.Equal(gc_pause_mode.pause_interactive, gc_heap.settings.pause_mode);
            gc_heap.handle_failure_for_no_gc();
        }
        finally
        {
            gc_heap.finalizer_work = null;
            ManagedGCRegionBootstrap.Shutdown();
            gc_heap.check_commit_cs.Destroy();
        }
    }

    [Fact]
    public void WksFullGCNotificationPreservesWaitCancelAndBackgroundStates()
    {
        GCToOSInterface.ResetRecording();
        GCConfig.Initialize();
        GCCommon.initialize();
        gc_heap.initialize_gc_static_state();
        Assert.True(gc_heap.check_commit_cs.Initialize());
        Assert.Equal(S_OK, ManagedGCRegionBootstrap.Prepare());

        try
        {
            Assert.True(ManagedGCRegionBootstrap.Initialize());
            Assert.True(gc_heap.initialize_full_gc_notification());
            gc_heap* heap = ManagedGCRegionBootstrap.Heap;

            Assert.True(gc_heap.register_for_full_gc_notification(heap, 10, 10));
            fixed (GCEvent* approach = &gc_heap.full_gc_approach_event)
            fixed (GCEvent* complete = &gc_heap.full_gc_end_event)
            {
                Assert.Equal(
                    wait_full_gc_status.wait_full_gc_timeout,
                    gc_heap.full_gc_wait(approach, 0));

                gc_heap.send_full_gc_notification(
                    (int)gc_generation_num.soh_gen0,
                    due_to_alloc_p: true);
                Assert.True(gc_heap.full_gc_approach_event_set);
                Assert.Equal(
                    wait_full_gc_status.wait_full_gc_success,
                    gc_heap.full_gc_wait(approach, 0));

                gc_heap.settings.condemned_generation =
                    GCInterfaceOffsets.max_generation;
                gc_heap.settings.concurrent = 0;
                gc_heap.update_full_gc_notification_after_gc(heap);
                Assert.False(gc_heap.full_gc_approach_event_set);
                Assert.Equal(
                    wait_full_gc_status.wait_full_gc_success,
                    gc_heap.full_gc_wait(complete, 0));

                Assert.True(gc_heap.register_for_full_gc_notification(heap, 10, 10));
                gc_heap.send_full_gc_notification(
                    (int)gc_generation_num.loh_generation,
                    due_to_alloc_p: false);
                gc_heap.settings.condemned_generation =
                    GCInterfaceOffsets.max_generation;
                gc_heap.settings.concurrent = 1;
                gc_heap.update_full_gc_notification_after_gc(heap);
                Assert.Equal(
                    wait_full_gc_status.wait_full_gc_na,
                    gc_heap.full_gc_wait(complete, 0));
                Assert.Equal(0, gc_heap.fgn_last_gc_was_concurrent);

                Assert.True(gc_heap.cancel_full_gc_notification());
                Assert.Equal(
                    wait_full_gc_status.wait_full_gc_na,
                    gc_heap.full_gc_wait(approach, 0));
                Assert.Equal(
                    wait_full_gc_status.wait_full_gc_na,
                    gc_heap.full_gc_wait(complete, 0));
            }
        }
        finally
        {
            gc_heap.destroy_full_gc_notification();
            ManagedGCRegionBootstrap.Shutdown();
            gc_heap.check_commit_cs.Destroy();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RegionBootstrapFailureInjectionReleasesEveryOwnedResource(int failAllocationOnCall)
    {
        GCToOSInterface.ResetRecording();
        SyncImports.ResetRecording();
        GCConfig.Initialize();
        GCCommon.initialize();
        Assert.True(gc_heap.check_commit_cs.Initialize());
        Assert.Equal(S_OK, ManagedGCRegionBootstrap.Prepare());
        SyncImports.FailAllocOnCall = failAllocationOnCall;
        int allocationsBeforeBootstrap = SyncImports.AllocCount;
        int freesBeforeBootstrap = SyncImports.FreeCount;

        try
        {
            Assert.False(ManagedGCRegionBootstrap.Initialize());
        }
        finally
        {
            ManagedGCRegionBootstrap.Shutdown();
            gc_heap.check_commit_cs.Destroy();
        }

        Assert.False(ManagedGCRegionBootstrap.IsInitialized);
        Assert.True(ManagedGCRegionBootstrap.GenerationTable is null);
        Assert.True(ManagedGCRegionBootstrap.EphemeralHeapSegment is null);
        Assert.True(ManagedGCRegionBootstrap.AllocAllocated is null);
        Assert.True(ManagedGCRegionBootstrap.ReservedRegionRange is null);
        Assert.Equal((nuint)0, ManagedGCRegionBootstrap.ReservedRegionRangeSize);
        Assert.True(gc_heap.initial_regions is null);
        Assert.True(gc_heap.bookkeeping_start is null);
        Assert.True(gc_heap.map_region_to_generation is null);
        Assert.True(GCCommon.seg_mapping_table is null);
        Assert.Equal(allocationsBeforeBootstrap + failAllocationOnCall, SyncImports.AllocCount);
        Assert.True(SyncImports.FreeCount >= freesBeforeBootstrap + failAllocationOnCall - 1);
        Assert.Equal((nuint)0, gc_heap.current_total_committed);
        Assert.Equal((nuint)0, gc_heap.current_total_committed_bookkeeping);
    }
#endif

    private static void ResetState()
    {
#if USE_REGIONS
        ManagedGCRegionBootstrap.Shutdown();
#endif
        RestoreDeclaredConfigValues();
        GCToEEInterface.Reset();
        GCInterfaceLayout.Reset();
        ManagedGCHeap.Reset();
        ManagedGCHandleManager.Reset();
        gc_heap.settings = default;
        gc_heap.current_bgc_state = bgc_state.bgc_not_in_process;
        gc_heap.gc_background_running = 0;
    }

    private static string ReadNullTerminatedUtf8(byte* value)
    {
        int length = 0;
        while (value[length] != 0)
        {
            length++;
        }

        return Encoding.UTF8.GetString(value, length);
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

    private static void SetConfigValue(string fieldName, long value)
    {
        FieldInfo field = typeof(GCConfig).GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(null, value);
    }

    private static void SetConfigByte(string fieldName, byte value)
    {
        FieldInfo field = typeof(GCConfig).GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(null, value);
    }

    private static void AssertDynamicData(
        dynamic_data* dd,
        nuint expectedMinSize,
        nuint expectedMaxSize,
        nuint expectedFragmentationLimit,
        float expectedFragmentationBurdenLimit,
        float expectedLimit,
        float expectedMaxLimit)
    {
        Assert.Equal((nint)expectedMinSize, dynamic_data.dd_new_allocation(dd));
        Assert.Equal((nint)expectedMinSize, dynamic_data.dd_gc_new_allocation(dd));
        Assert.Equal(expectedMinSize, dynamic_data.dd_desired_allocation(dd));
        Assert.Equal((nuint)0, dynamic_data.dd_current_size(dd));
        Assert.Equal((nuint)0, dynamic_data.dd_promoted_size(dd));
        Assert.Equal((nuint)0, dynamic_data.dd_collection_count(dd));
        Assert.Equal((nuint)0, dynamic_data.dd_fragmentation(dd));
        Assert.Equal((nuint)0, dynamic_data.dd_gc_clock(dd));
        Assert.Equal(dynamic_data.dd_time_clock(dd), dynamic_data.dd_previous_time_clock(dd));
        Assert.Equal(expectedMinSize, dynamic_data.dd_min_size(dd));
        Assert.True(dd->sdata is not null);
        Assert.Equal(expectedMaxSize, dynamic_data.dd_max_size(dd));
        Assert.Equal(expectedFragmentationLimit, dynamic_data.dd_fragmentation_limit(dd));
        Assert.Equal(expectedFragmentationBurdenLimit, dynamic_data.dd_fragmentation_burden_limit(dd));
        Assert.Equal(expectedLimit, dynamic_data.dd_limit(dd));
        Assert.Equal(expectedMaxLimit, dynamic_data.dd_max_limit(dd));
    }

    private static Dictionary<string, int> ReadInterfaceConstants()
    {
        using Stream stream = typeof(ManagedGCEntryPointsTests).Assembly.GetManifestResourceStream(TableResourceName);
        Assert.NotNull(stream);
        using StreamReader reader = new(stream);

        int column = IntPtr.Size == 8 ? 1 : 0;
        Dictionary<string, int> constants = new(StringComparer.Ordinal);
        while (reader.ReadLine() is string line)
        {
            if (!line.StartsWith("GC_CONST(", StringComparison.Ordinal))
            {
                continue;
            }

            int open = line.IndexOf('(');
            int close = line.LastIndexOf(')');
            Assert.True(open > 0 && close > open, $"Could not parse the table line '{line}'.");
            string[] arguments = line[(open + 1)..close].Split(',').Select(argument => argument.Trim()).ToArray();
            if (arguments.Length != 3)
            {
                continue;
            }

            constants[arguments[2]] = int.Parse(arguments[column], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return constants;
    }
}
