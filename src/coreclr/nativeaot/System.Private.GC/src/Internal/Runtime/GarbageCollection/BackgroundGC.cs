// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the dependency-closed WKS USE_REGIONS background-GC thread/event lifecycle and
// concurrent mark prefix from background.cpp and collect.cpp.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
#if BACKGROUND_GC && USE_REGIONS && !MULTIPLE_HEAPS
    public const int gc_type_background = 2;

    private static GCEvent background_gc_done_event;
    private static GCEvent bgc_start_event;
    private static CLRCriticalSection bgc_threads_timeout_cs;
    private static int bgc_thread_running;
    private static int bgc_thread_shutdown;
    private static int bgc_thread_exited;
    private static int bgc_threads_timeout_cs_initialized;
    private static byte** background_mark_stack_array;
    private static nuint background_mark_stack_array_length;
    private static nuint background_mark_stack_tos;
    private static int background_mark_stack_overflow;
    private static int background_final_plan_p;
    private static int temp_disable_concurrent_p;

    [InlineArray(9)]
    private struct bgc_thread_name_buffer
    {
        private byte _element0;
    }

    private static bgc_thread_name_buffer bgc_thread_name;

    public static bool initialize_background_gc()
    {
        bgc_thread_exited = 1;
        if (!gc_can_use_concurrent)
        {
            return true;
        }

        if (!background_gc_done_event.CreateManualEventNoThrow(initialState: true))
        {
            return false;
        }

        if (!bgc_start_event.CreateManualEventNoThrow(initialState: false))
        {
            background_gc_done_event.CloseEvent();
            return false;
        }

        if (!bgc_threads_timeout_cs.Initialize())
        {
            bgc_start_event.CloseEvent();
            background_gc_done_event.CloseEvent();
            return false;
        }
        bgc_threads_timeout_cs_initialized = 1;

        nuint stackLength = mark_list_size > gc_rand.MARK_STACK_INITIAL_LENGTH
            ? mark_list_size
            : gc_rand.MARK_STACK_INITIAL_LENGTH;
        if (stackLength > nuint.MaxValue / (nuint)sizeof(byte*))
        {
            destroy_background_gc();
            return false;
        }

        background_mark_stack_array = (byte**)SyncImports.ManagedGC_AllocZeroed(
            stackLength * (nuint)sizeof(byte*));
        if (background_mark_stack_array is null)
        {
            destroy_background_gc();
            return false;
        }

        background_mark_stack_array_length = stackLength;
        background_mark_stack_tos = 0;
        background_mark_stack_overflow = 0;
        bgc_thread_running = 0;
        bgc_thread_shutdown = 0;
        temp_disable_concurrent_p = 0;
        current_bgc_state = bgc_state.bgc_not_in_process;

        bgc_thread_name[0] = (byte)'.';
        bgc_thread_name[1] = (byte)'N';
        bgc_thread_name[2] = (byte)'E';
        bgc_thread_name[3] = (byte)'T';
        bgc_thread_name[4] = (byte)' ';
        bgc_thread_name[5] = (byte)'B';
        bgc_thread_name[6] = (byte)'G';
        bgc_thread_name[7] = (byte)'C';
        bgc_thread_name[8] = 0;
        return true;
    }

    public static void destroy_background_gc()
    {
        if (System.Threading.Volatile.Read(ref bgc_thread_running) != 0)
        {
            System.Threading.Volatile.Write(ref bgc_thread_shutdown, 1);
            bgc_start_event.Set();
            while (System.Threading.Volatile.Read(ref bgc_thread_running) != 0)
            {
                GCToOSInterface.Sleep(1);
            }
        }

        while (System.Threading.Volatile.Read(ref bgc_thread_exited) == 0)
        {
            GCToOSInterface.YieldThread(0);
        }

        if (background_mark_stack_array is not null)
        {
            SyncImports.ManagedGC_Free(background_mark_stack_array);
            background_mark_stack_array = null;
        }

        background_mark_stack_array_length = 0;
        background_mark_stack_tos = 0;

        if (bgc_threads_timeout_cs_initialized != 0)
        {
            bgc_threads_timeout_cs.Destroy();
            bgc_threads_timeout_cs_initialized = 0;
        }

        if (bgc_start_event.IsValid())
        {
            bgc_start_event.CloseEvent();
        }

        if (background_gc_done_event.IsValid())
        {
            background_gc_done_event.CloseEvent();
        }
    }

    private static bool prepare_bgc_thread()
    {
        if (System.Threading.Volatile.Read(ref bgc_thread_running) != 0)
        {
            return true;
        }

        bgc_threads_timeout_cs.Enter();
        bool success = System.Threading.Volatile.Read(ref bgc_thread_running) != 0;
        if (!success)
        {
            System.Threading.Volatile.Write(ref bgc_thread_exited, 0);
            fixed (byte* name = &bgc_thread_name[0])
            {
                success = ManagedGC_CreateBackgroundThread(
                    &bgc_thread_stub,
                    ManagedGCRegionBootstrap.Heap,
                    (int*)Unsafe.AsPointer(ref bgc_thread_exited),
                    name) != 0;
            }

            if (success)
            {
                System.Threading.Volatile.Write(ref bgc_thread_running, 1);
                GCEvents.GCEventFireGCCreateConcurrentThread_V1();
            }
            else
            {
                System.Threading.Volatile.Write(ref bgc_thread_exited, 1);
            }
        }

        bgc_threads_timeout_cs.Leave();
        return success;
    }

    [UnmanagedCallersOnly]
    private static void bgc_thread_stub(void* argument)
    {
        _ = argument;
        bgc_thread_function();
    }

    private static void bgc_thread_function()
    {
        while (true)
        {
            bool toggled = GCToEEInterface.EnablePreemptiveGC() != 0;
            uint waitResult = bgc_start_event.Wait(GCEnv.INFINITE, alertable: false);
            if (toggled)
            {
                GCToEEInterface.DisablePreemptiveGC();
            }

            if (waitResult == GCEnv.WAIT_OBJECT_0)
            {
                bgc_start_event.Reset();
            }

            if (waitResult != GCEnv.WAIT_OBJECT_0 ||
                System.Threading.Volatile.Read(ref bgc_thread_shutdown) != 0)
            {
                break;
            }

            background_mark_phase_concurrent();
            background_gc_finish();
            GCToEEInterface.EnablePreemptiveGC();
            current_bgc_state = bgc_state.bgc_not_in_process;
            gc_background_running = 0;
            System.Threading.Volatile.Write(ref bgc_thread_running, 0);
            ManagedGCHeap.NotifyCollectionEnded();
            background_gc_done_event.Set();
            break;
        }

        System.Threading.Volatile.Write(ref bgc_thread_running, 0);
        GCEvents.GCEventFireGCTerminateConcurrentThread_V1();
    }

    public static int garbage_collect_background(
        int generation,
        byte low_memory_p,
        int mode,
        gc_reason reason = gc_reason.reason_induced)
    {
        int unsupportedMode =
            (int)collection_mode.collection_blocking |
            (int)collection_mode.collection_compacting |
            (int)collection_mode.collection_optimized |
            (int)collection_mode.collection_aggressive |
            (int)collection_mode.collection_gcstress;
        bool survivorAnalysisRequested =
            GCToEEInterface.AnalyzeSurvivorsRequested(GCInterfaceOffsets.max_generation) != 0;
        if ((generation >= 0 && generation != GCInterfaceOffsets.max_generation) ||
            (mode & (int)collection_mode.collection_non_blocking) == 0 ||
            (mode & unsupportedMode) != 0 ||
            (mode & ~(int)collection_mode.collection_non_blocking) != 0 ||
            !concurrent_gc_enabled() ||
            GCConfig.GetServerGC() != 0 ||
            GCConfig.GetHeapVerifyLevel() != 0 ||
            survivorAnalysisRequested ||
            (GCEventStatus.GetEnabledKeywords(GCEventProvider.Default) &
                UnsupportedPublicCollectionKeywords) != 0 ||
            (GCEventStatus.GetEnabledKeywords(GCEventProvider.Private) &
                UnsupportedPrivateCollectionKeywords) != 0)
        {
            return collection_e_notimpl;
        }

        gc_heap* hp = ManagedGCRegionBootstrap.Heap;
        if (hp is null ||
            finalize_queue is null ||
            background_mark_stack_array is null ||
            mark_array is null)
        {
            return collection_e_fail;
        }

        enter_gc_lock();
        if (background_running_p())
        {
            leave_gc_lock();
            return collection_s_ok;
        }

        ManagedGCHeap.NotifyCollectionStarted();
        background_gc_done_event.Reset();
        gc_background_running = 1;
        current_bgc_state = bgc_state.bgc_initialized;

        GCToEEInterface.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC);
        settings.init_mechanisms();
        settings.reason = low_memory_p != 0
            ? gc_reason.reason_lowmemory
            : reason;
        settings.condemned_generation = GCInterfaceOffsets.max_generation;
        settings.promotion = 1;
        settings.compaction = 0;
        settings.loh_compaction = 0;
        settings.concurrent = 1;
        settings.background_p = 1;
        settings.gc_index = dynamic_data.dd_collection_count(
            dynamic_data_of(hp, (int)gc_generation_num.soh_gen0)) + 1;

        alloc_contexts_used = 0;
        fix_allocation_contexts(hp, for_gc_p: true);
        init_records(hp);
        update_collection_counts(hp);
        GCToEEInterface.GcStartWork(
            GCInterfaceOffsets.max_generation,
            GCInterfaceOffsets.max_generation);
        GCEvents.GCEventFireBGCBegin();

        background_saved_lowest_address = lowest_address;
        background_saved_highest_address = highest_address;
        if (!prepare_bgc_thread() ||
            !commit_mark_array_bgc_init(hp) ||
            !background_mark_phase_initial(hp))
        {
            bool threadPending =
                System.Threading.Volatile.Read(ref bgc_thread_exited) == 0;
            settings.concurrent = 0;
            settings.background_p = 0;
            current_bgc_state = bgc_state.bgc_not_in_process;
            gc_background_running = 0;
            background_gc_done_event.Set();
            if (threadPending)
            {
                System.Threading.Volatile.Write(ref bgc_thread_shutdown, 1);
                bgc_start_event.Set();
            }

            GCToEEInterface.RestartEE(0);
            leave_gc_lock();
            if (threadPending)
            {
                background_gc_wait();
                System.Threading.Volatile.Write(ref bgc_thread_shutdown, 0);
            }

            ManagedGCHeap.NotifyCollectionEnded();
            return collection_e_fail;
        }

        reset_background_allocation_budgets(hp);
        GCToEEInterface.RestartEE(0);
        leave_gc_lock();
        bgc_start_event.Set();
        return collection_s_ok;
    }

    private static bool commit_mark_array_bgc_init(gc_heap* hp)
    {
        generation* generationTable = generation_table_of(hp);
        for (int genNumber = 0;
             genNumber < (int)gc_generation_num.total_generation_count;
             genNumber++)
        {
            generation* gen = generation_of(generationTable, genNumber);
            for (heap_segment* segment = generation.generation_start_segment_rw(gen);
                 segment is not null;
                 segment = heap_segment.heap_segment_next(segment))
            {
                if ((segment->flags & heap_segment.heap_segment_flags_ma_committed) == 0 &&
                    !commit_mark_array_new_seg(segment))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool background_mark_phase_initial(gc_heap* hp)
    {
        background_mark_stack_tos = 0;
        background_mark_stack_overflow = 0;
        snapshot_background_allocated(hp);

        ScanContext sc = default;
        sc.init();
        sc.thread_number = hp->heap_number;
        sc.thread_count = 1;
        sc.promotion = 1;
        sc.concurrent = 1;

        GCToEEInterface.BeforeGcScanRoots(
            GCInterfaceOffsets.max_generation,
            is_bgc: 1,
            is_concurrent: 1);
        GCScan.GcScanRoots(
            &background_promote,
            GCInterfaceOffsets.max_generation,
            GCInterfaceOffsets.max_generation,
            &sc);

        finalize_queue->GcScanRoots(
            &background_promote,
            hp->heap_number,
            &sc);
        GCEvents.GCEventFireBGC1stNonConEnd();
        current_bgc_state = bgc_state.bgc_mark_handles;
        return true;
    }

    private static void background_mark_phase_concurrent()
    {
        gc_heap* hp = ManagedGCRegionBootstrap.Heap;
        if (hp is null)
        {
            return;
        }

        drain_background_mark_stack(hp);

        ScanContext sc = default;
        sc.init();
        sc.thread_number = hp->heap_number;
        sc.thread_count = 1;
        sc.promotion = 1;
        sc.concurrent = 1;

        GCScan.GcScanHandles(
            &background_promote,
            GCInterfaceOffsets.max_generation,
            GCInterfaceOffsets.max_generation,
            &sc);
        drain_background_mark_stack(hp);

        if (ObjectHandle.DependentHandleContextsInitialized)
        {
            GCScan.GcDhInitialScan(
                &background_promote,
                GCInterfaceOffsets.max_generation,
                GCInterfaceOffsets.max_generation,
                &sc);
            while (GCScan.GcDhUnpromotedHandlesExist(&sc))
            {
                bool promoted = GCScan.GcDhReScan(&sc);
                drain_background_mark_stack(hp);
                if (!promoted)
                {
                    break;
                }
            }
        }

        current_bgc_state = bgc_state.bgc_mark_stack;
        drain_background_mark_stack(hp);
        GCEvents.GCEventFireBGC1stConEnd();
    }

    private static void background_gc_finish()
    {
        gc_heap* hp = ManagedGCRegionBootstrap.Heap;
        if (hp is null)
        {
            complete_background_gc(collectionCompleted: false);
            return;
        }

        enter_gc_lock();
        GCToEEInterface.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC);
        current_bgc_state = bgc_state.bgc_final_marking;
        GCEvents.GCEventFireBGC2ndNonConBegin();

        settings.concurrent = 0;
        settings.background_p = 0;
        bool collectionCompleted =
            mark_phase_stack_roots(backgroundFinalMark: true);
        if (collectionCompleted)
        {
            clear_background_mark_array(hp);
            current_bgc_state = bgc_state.bgc_plan_phase;
            background_final_plan_p = 1;
            collectionCompleted = plan_phase_synchronous_foreground(
                hp,
                GCInterfaceOffsets.max_generation);
            background_final_plan_p = 0;
        }

        if (collectionCompleted)
        {
            finish_background_collection_accounting(hp);
        }

        GCEvents.GCEventFireBGC2ndNonConEnd();
        GCToEEInterface.RestartEE(collectionCompleted ? (byte)1 : (byte)0);
        leave_gc_lock();
        complete_background_gc(collectionCompleted);
    }

    private static void finish_background_collection_accounting(gc_heap* hp)
    {
        for (int genNumber = 0;
             genNumber < (int)gc_generation_num.total_generation_count;
             genNumber++)
        {
            compute_new_dynamic_data_background(hp, genNumber);
        }

        rearrange_uoh_segments();
        compute_gc_and_ephemeral_range(
            hp,
            GCInterfaceOffsets.max_generation,
            end_of_gc_p: true);
        GCWriteBarrier.stomp_write_barrier_ephemeral(
            ephemeral_low,
            ephemeral_high,
            map_region_to_generation_skewed,
            (byte)min_segment_size_shr);
        update_end_ngc_time();
        update_end_gc_time_per_heap(hp);
        full_gc_counts[gc_type_background]++;
        last_gc_before_oom = 0;
        GCToEEInterface.GcDone(GCInterfaceOffsets.max_generation);
    }

    private static void clear_background_mark_array(gc_heap* hp)
    {
        generation* generationTable = generation_table_of(hp);
        for (int genNumber = 0;
             genNumber < (int)gc_generation_num.total_generation_count;
             genNumber++)
        {
            generation* gen = generation_of(generationTable, genNumber);
            for (heap_segment* segment = generation.generation_start_segment_rw(gen);
                 segment is not null;
                 segment = heap_segment.heap_segment_next(segment))
            {
                byte* start = heap_segment.heap_segment_mem(segment);
                byte* backgroundAllocated =
                    heap_segment.heap_segment_background_allocated(segment);
                if (backgroundAllocated is null || start >= backgroundAllocated)
                {
                    continue;
                }

                byte* end = card_table_info.align_on_mark_word(
                    backgroundAllocated);
                if (end > background_saved_highest_address)
                {
                    end = card_table_info.align_lower_mark_word(
                        background_saved_highest_address);
                }

                if (start < end)
                {
                    clear_mark_array(start, end);
                }

                while (end < backgroundAllocated)
                {
                    mark_array_clear_marked(end);
                    end += (nint)card_table_info.mark_bit_pitch;
                }
            }
        }
    }

    private static void compute_new_dynamic_data_background(gc_heap* hp, int genNumber)
    {
        dynamic_data* dd = dynamic_data_of(hp, genNumber);
        generation* gen = generation_of(generation_table_of(hp), genNumber);
        nuint totalGenSize = generation_sizes(hp, gen);
        nuint fragmentation = unchecked(
            generation.generation_free_list_space(gen) +
            generation.generation_free_obj_space(gen));

        dynamic_data.dd_fragmentation(dd) = fragmentation;
        dynamic_data.dd_current_size(dd) =
            fragmentation <= totalGenSize ? totalGenSize - fragmentation : 0;
        dynamic_data.dd_promoted_size(dd) = dynamic_data.dd_survived_size(dd);
        generation.generation_condemned_allocated(gen) = 0;
        generation.generation_free_list_allocated(gen) = 0;
        generation.generation_end_seg_allocated(gen) = 0;

        nuint desiredAllocation = dynamic_data.dd_desired_allocation(dd);
        if (desiredAllocation < dynamic_data.dd_min_size(dd))
        {
            desiredAllocation = dynamic_data.dd_min_size(dd);
            dynamic_data.dd_desired_allocation(dd) = desiredAllocation;
        }

        dynamic_data.dd_gc_new_allocation(dd) = unchecked((nint)desiredAllocation);
        dynamic_data.dd_new_allocation(dd) = unchecked((nint)desiredAllocation);
    }

    private static void complete_background_gc(bool collectionCompleted)
    {
        settings.concurrent = 0;
        settings.background_p = 0;

        if (collectionCompleted)
        {
            ManagedGCHeap.RecordCollectionCount(unchecked((int)settings.gc_index));
            GCToEEInterface.EnableFinalization(
                settings.found_finalizers != 0 ? (byte)1 : (byte)0);
        }
    }

    private static void snapshot_background_allocated(gc_heap* hp)
    {
        generation* generationTable = generation_table_of(hp);
        for (int genNumber = 0;
             genNumber < (int)gc_generation_num.total_generation_count;
             genNumber++)
        {
            generation* gen = generation_of(generationTable, genNumber);
            for (heap_segment* segment = generation.generation_start_segment_rw(gen);
                 segment is not null;
                 segment = heap_segment.heap_segment_next(segment))
            {
                heap_segment.heap_segment_background_allocated(segment) =
                    heap_segment.heap_segment_allocated(segment);
            }
        }
    }

    private static void reset_background_allocation_budgets(gc_heap* hp)
    {
        for (int genNumber = 0;
             genNumber < (int)gc_generation_num.total_generation_count;
             genNumber++)
        {
            dynamic_data* dd = dynamic_data_of(hp, genNumber);
            nint desiredAllocation = unchecked((nint)dynamic_data.dd_desired_allocation(dd));
            if (dynamic_data.dd_new_allocation(dd) < desiredAllocation)
            {
                dynamic_data.dd_new_allocation(dd) = desiredAllocation;
            }
        }
    }

    public static void background_promote(byte** ppObject, ScanContext* sc, uint flags)
    {
        _ = sc;
        if (ppObject is null)
        {
            return;
        }

        byte* o = *ppObject;
        if (!is_in_heap_range(o) ||
            o < background_saved_lowest_address ||
            o >= background_saved_highest_address)
        {
            return;
        }

        if ((flags & (uint)GCCallFlags.GC_CALL_INTERIOR) != 0)
        {
            gc_heap* hp = ManagedGCRegionBootstrap.Heap;
            if (hp is null || (o = find_object(o, hp)) is null)
            {
                return;
            }
        }

        if (background_mark1(o) != 0 && contain_pointers_or_collectible(o) != 0)
        {
            push_background_mark(o);
        }
    }

    private static void push_background_mark(byte* o)
    {
        nuint index = background_mark_stack_tos;
        if (index < background_mark_stack_array_length)
        {
            background_mark_stack_array[(nint)index] = o;
            background_mark_stack_tos = index + 1;
        }
        else
        {
            background_mark_stack_overflow = 1;
        }
    }

    private static void background_mark_reference(byte** slot, void* context)
    {
        _ = context;
        byte* child = (byte*)GCEnv.VolatileLoad((void**)slot);
        if (child >= background_saved_lowest_address &&
            child < background_saved_highest_address &&
            background_mark1(child) != 0 &&
            contain_pointers_or_collectible(child) != 0)
        {
            push_background_mark(child);
        }
    }

    private static void drain_background_mark_stack(gc_heap* hp)
    {
        _ = hp;
        nuint markedObjects = 0;

        while (background_mark_stack_tos != 0)
        {
            background_mark_stack_tos--;
            byte* o = background_mark_stack_array[
                (nint)background_mark_stack_tos];
            nuint objectSize = size(o);
            go_through_object_nostart(
                method_table(o),
                o,
                objectSize,
                null,
                &background_mark_reference);
            markedObjects++;
        }

        GCEvents.GCEventFireBGCDrainMark(markedObjects);
        _ = background_mark_stack_overflow;
    }

    public static uint background_gc_wait(uint timeout = GCEnv.INFINITE)
    {
        if (!background_gc_done_event.IsValid())
        {
            return GCEnv.WAIT_OBJECT_0;
        }

        bool toggled = GCToEEInterface.EnablePreemptiveGC() != 0;
        uint result = background_gc_done_event.Wait(timeout, alertable: false);
        if (result == GCEnv.WAIT_OBJECT_0)
        {
            while (System.Threading.Volatile.Read(ref bgc_thread_exited) == 0)
            {
                GCToOSInterface.YieldThread(0);
            }
        }

        if (toggled)
        {
            GCToEEInterface.DisablePreemptiveGC();
        }

        return result;
    }

    public static bool background_collection_running_p() =>
        background_running_p();

    public static bool background_collection_pending_p() =>
        background_running_p() ||
        System.Threading.Volatile.Read(ref bgc_thread_exited) == 0;

    public static bool concurrent_gc_enabled() =>
        gc_can_use_concurrent &&
        System.Threading.Volatile.Read(ref temp_disable_concurrent_p) == 0;

    public static void set_temp_disable_concurrent(bool disabled) =>
        System.Threading.Volatile.Write(
            ref temp_disable_concurrent_p,
            disabled ? 1 : 0);

    public static bool background_final_plan() =>
        background_final_plan_p != 0;

#if MANAGED_GC_TEST_HOST
    private static int ManagedGC_CreateBackgroundThread(
        delegate* unmanaged<void*, void> threadStart,
        void* context,
        int* exited,
        byte* name) =>
        GCToEEInterface.CreateBackgroundThread(
            threadStart,
            context,
            exited,
            name);
#else
    [System.Runtime.RuntimeImport("*", "ManagedGC_CreateBackgroundThread")]
    [MethodImpl(MethodImplOptions.InternalCall)]
    private static extern int ManagedGC_CreateBackgroundThread(
        delegate* unmanaged<void*, void> threadStart,
        void* context,
        int* exited,
        byte* name);
#endif
#else
    public static bool background_collection_running_p() => false;

    public static bool background_collection_pending_p() => false;

    public static bool concurrent_gc_enabled() => false;

    public static void set_temp_disable_concurrent(bool disabled)
    {
        _ = disabled;
    }

    public static bool background_final_plan() => false;

    public static bool initialize_background_gc() => true;

    public static void destroy_background_gc()
    {
    }

    public static int garbage_collect_background(
        int generation,
        byte low_memory_p,
        int mode,
        gc_reason reason = gc_reason.reason_induced)
    {
        _ = generation;
        _ = low_memory_p;
        _ = mode;
        _ = reason;
        return collection_e_notimpl;
    }

    public static uint background_gc_wait(uint timeout = GCEnv.INFINITE)
    {
        _ = timeout;
        return GCEnv.WAIT_OBJECT_0;
    }
#endif
}
#pragma warning restore CS8981
