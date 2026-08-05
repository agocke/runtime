// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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
    public void WksAllocationCallbackUsesOwnedLocksAndDefersUnportedOperations()
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
            delegate* unmanaged<gc_heap.try_allocate_more_space_context*, int, gc_heap.allocation_callback_result*, void> callback =
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

            context.full_gc_notification_p = 1;
            Assert.False(gc_heap.allocate_more_space(&context, callback));
            Assert.Equal(gc_heap.allocation_deferred_operation.check_for_full_gc, context.deferred_operation);
            Assert.Equal((byte)0, context.more_space_lock_held_p);
            Assert.Equal(GCSpinLock.lock_free, heap->more_space_lock_soh.@lock);
        }
        finally
        {
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
