// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the dependency-closed WKS USE_REGIONS background-GC thread/event lifecycle,
// concurrent mark/revisit, allocation trigger, and region sweep from background.cpp,
// collect.cpp, and allocation.cpp.

using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
#if BACKGROUND_GC && USE_REGIONS && !MULTIPLE_HEAPS
    public const int gc_type_background = 2;

    private static GCEvent background_gc_done_event;
    private static CLRCriticalSection bgc_threads_timeout_cs;
    private static int bgc_thread_running;
    private static int bgc_thread_shutdown;
    private static int bgc_thread_exited;
    private static int bgc_threads_timeout_cs_initialized;
    private static byte** background_mark_stack_array;
    private static nuint background_mark_stack_array_length;
    private static nuint background_mark_stack_tos;
    private static int background_mark_stack_overflow;
    private const int MaxPendingUohAllocations = 64;
    private static int uoh_publication_lock;
    private static nint uoh_object_being_read;
    private static pending_uoh_allocation_array pending_uoh_allocations;
    private static int uoh_alloc_thread_count;
    private static int temp_disable_concurrent_p;
    private static nuint bgc_thread_context;
#if MANAGED_GC_TEST_HOST
    private static ulong background_state_transitions;
    private static int test_pause_background;
    private static int test_background_pause_observed;
    private static int test_foreground_during_bgc_count;
    private static gc_mechanisms test_last_restored_bgc_settings;
#endif
    private static c_gc_state current_c_gc_state;
    private static heap_segment* current_sweep_seg;
    private static byte* current_sweep_pos;
    private const int BackgroundWrittenAddressCount = 100;

    [InlineArray(BackgroundWrittenAddressCount + 2)]
    private struct background_written_address_array
    {
        private nuint _element0;
    }

    private static background_written_address_array background_written_addresses;

    [InlineArray(MaxPendingUohAllocations)]
    private struct pending_uoh_allocation_array
    {
        private nint _element0;
    }

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

        if (!bgc_threads_timeout_cs.Initialize())
        {
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
        uoh_publication_lock = 0;
        uoh_object_being_read = 0;
        pending_uoh_allocations = default;
        uoh_alloc_thread_count = 0;
        bgc_thread_running = 0;
        bgc_thread_shutdown = 0;
        bgc_thread_context = 0;
        temp_disable_concurrent_p = 0;
        set_background_state(bgc_state.bgc_not_in_process);
        current_c_gc_state = c_gc_state.c_gc_state_free;
        current_sweep_seg = null;
        current_sweep_pos = null;

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
            ManagedGC_SignalBackgroundThread((void*)bgc_thread_context);
            while (System.Threading.Volatile.Read(ref bgc_thread_exited) == 0)
            {
                GCToOSInterface.Sleep(1);
            }

            System.Threading.Volatile.Write(ref bgc_thread_running, 0);
            bgc_thread_context = 0;
            GCEvents.GCEventFireGCTerminateConcurrentThread_V1();
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
                    (int*)Unsafe.AsPointer(ref bgc_thread_shutdown),
                    (int*)Unsafe.AsPointer(ref bgc_thread_exited),
                    (void**)Unsafe.AsPointer(ref bgc_thread_context),
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

    private static void bgc_thread_stub(void* argument)
    {
        _ = argument;
        bgc_thread_function();
    }

    private static void bgc_thread_function()
    {
        background_mark_phase_concurrent();
        background_gc_finish();
        GCToEEInterface.EnablePreemptiveGC();
        ManagedGCHeap.NotifyCollectionEnded();
        background_gc_done_event.Set();
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
            survivorAnalysisRequested)
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

        delay_free_segments();
        ManagedGCHeap.NotifyCollectionStarted();
        background_gc_done_event.Reset();
        gc_background_running = 1;
#if MANAGED_GC_TEST_HOST
        background_state_transitions = 0;
        test_foreground_during_bgc_count = 0;
        test_last_restored_bgc_settings = default;
#endif
        set_background_state(bgc_state.bgc_initialized);

        suspended_start_time = GCCommon.GetHighPrecisionTimeStamp();
        GCToEEInterface.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC);
        settings.init_mechanisms();
        settings.reason = low_memory_p != 0
            ? gc_reason.reason_lowmemory
            : reason;
        GCEvents.GCEventFireGCTriggered(unchecked((uint)settings.reason));
        settings.condemned_generation = GCInterfaceOffsets.max_generation;
        settings.promotion = 1;
        settings.compaction = 0;
        settings.loh_compaction = 0;
        settings.concurrent = 1;
        settings.background_p = 1;
        record_entry_memory_load();
        settings.gc_index = dynamic_data.dd_collection_count(
            dynamic_data_of(hp, (int)gc_generation_num.soh_gen0)) + 1;
        last_background_gc_info_index = 1 - last_background_gc_info_index;
        background_gc_info(last_background_gc_info_index) = default;
        background_gc_info(last_background_gc_info_index).index =
            settings.gc_index;
        saved_bgc_settings = settings;
        alloc_contexts_used = 0;
        fix_allocation_contexts(hp, for_gc_p: true);
        init_records(hp);
        update_collection_counts(hp);
        bgc_data_per_heap = gc_data_per_heap;
        bgc_data_global = gc_data_global;
        GCToEEInterface.GcStartWork(
            GCInterfaceOffsets.max_generation,
            GCInterfaceOffsets.max_generation);
        UpdatePreGCCounters(hp);
        FireCommittedUsageEvent();
        UpdateEventStatusForLinux();
        GCEvents.GCEventFireBGCBegin();

        background_saved_lowest_address = lowest_address;
        background_saved_highest_address = highest_address;
        if (!prepare_bgc_thread() ||
            !commit_mark_array_bgc_init(hp) ||
            !background_mark_phase_initial(hp))
        {
            settings.concurrent = 0;
            settings.background_p = 0;
            set_background_state(bgc_state.bgc_not_in_process);
            gc_background_running = 0;
            background_gc_done_event.Set();

            GCToEEInterface.RestartEE(0);
            leave_gc_lock();

            ManagedGCHeap.NotifyCollectionEnded();
            return collection_e_fail;
        }

        reset_software_write_watch(hp);
        if (SoftwareWriteWatch.GetTable() is not null)
        {
            SoftwareWriteWatch.EnableForGCHeap();
        }
        reset_background_allocation_budgets(hp);
        add_bgc_pause_duration_0();
        GCToEEInterface.RestartEE(0);
        leave_gc_lock();
        ManagedGC_SignalBackgroundThread((void*)bgc_thread_context);
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
        set_background_state(bgc_state.bgc_reset_ww);
        current_c_gc_state = c_gc_state.c_gc_state_marking;
        set_background_state(bgc_state.bgc_mark_handles);
        return true;
    }

    private static void background_mark_phase_concurrent()
    {
        gc_heap* hp = ManagedGCRegionBootstrap.Heap;
        if (hp is null)
        {
            return;
        }

        allow_foreground_gc();
        drain_background_mark_stack(hp);
        allow_foreground_gc();

        ScanContext sc = default;
        sc.init();
        sc.thread_number = hp->heap_number;
        sc.thread_count = 1;
        sc.promotion = 1;
        sc.concurrent = 1;

        GCScan.GcScanSizedRefs(
            &background_promote,
            GCInterfaceOffsets.max_generation,
            GCInterfaceOffsets.max_generation,
            &sc);

        GCScan.GcScanHandles(
            &background_promote,
            GCInterfaceOffsets.max_generation,
            GCInterfaceOffsets.max_generation,
            &sc);
        drain_background_mark_stack(hp);
        allow_foreground_gc();

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
                allow_foreground_gc();
                if (!promoted)
                {
                    break;
                }
            }
        }

        set_background_state(bgc_state.bgc_mark_stack);
        drain_background_mark_stack(hp);
        allow_foreground_gc();
        revisit_written_pages(hp, concurrent_p: true);
        revisit_dirty_cards(hp, concurrent_p: true);
        revisit_written_pages(hp, concurrent_p: true);
        revisit_dirty_cards(hp, concurrent_p: true);
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
        suspended_start_time = GCCommon.GetHighPrecisionTimeStamp();
        GCToEEInterface.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC);
        set_background_state(bgc_state.bgc_final_marking);
        GCEvents.GCEventFireBGC2ndNonConBegin();

        fix_allocation_contexts(hp, for_gc_p: false);
        bool collectionCompleted = background_mark_phase_final(hp);
        if (!collectionCompleted)
        {
            if (SoftwareWriteWatch.IsEnabledForGCHeap())
            {
                SoftwareWriteWatch.DisableForGCHeap();
            }

            GCEvents.GCEventFireBGC2ndNonConEnd();
            GCToEEInterface.RestartEE(0);
            leave_gc_lock();
            complete_background_gc(collectionCompleted: false);
            return;
        }

        prepare_background_sweep(hp);
        GCEvents.GCEventFireBGC2ndNonConEnd();
        add_bgc_pause_duration_1();
        GCToEEInterface.RestartEE(1);
        GCEvents.GCEventFireBGC2ndConBegin();

        collectionCompleted = background_sweep(hp);
        enter_gc_lock();
        if (collectionCompleted)
        {
            finish_background_collection_accounting(hp);
        }

        current_c_gc_state = c_gc_state.c_gc_state_free;
        current_sweep_seg = null;
        current_sweep_pos = null;
        set_background_state(bgc_state.bgc_not_in_process);
        gc_background_running = 0;
        leave_gc_lock();
        complete_background_gc(collectionCompleted);
    }

    private struct background_revisit_context
    {
        public byte* end;
    }

    private static void reset_software_write_watch(gc_heap* hp)
    {
        if (SoftwareWriteWatch.GetTable() is null)
        {
            return;
        }

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
                byte* end = heap_segment.heap_segment_committed(segment);

                if (start < end)
                {
                    SoftwareWriteWatch.ClearDirty(
                        start,
                        unchecked((nuint)(end - start)));
                }
            }
        }

    }

    private static bool background_mark_phase_final(gc_heap* hp)
    {
        ScanContext sc = default;
        sc.init();
        sc.thread_number = hp->heap_number;
        sc.thread_count = 1;
        sc.promotion = 1;
        sc.concurrent = 0;

        GCToEEInterface.BeforeGcScanRoots(
            GCInterfaceOffsets.max_generation,
            is_bgc: 1,
            is_concurrent: 0);

        GCScan.GcScanRoots(
            &background_promote,
            GCInterfaceOffsets.max_generation,
            GCInterfaceOffsets.max_generation,
            &sc);
        finalize_queue->GcScanRoots(
            &background_promote,
            hp->heap_number,
            &sc);
        GCScan.GcScanHandles(
            &background_promote,
            GCInterfaceOffsets.max_generation,
            GCInterfaceOffsets.max_generation,
            &sc);
        drain_background_mark_stack(hp);

        revisit_written_pages(hp, concurrent_p: false);
        revisit_dirty_cards(hp, concurrent_p: false);
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

#if FEATURE_JAVAMARSHAL
        if (ObjectHandle.Ref_HasHandlesOfType(
            (uint)HandleType.HNDTYPE_CROSSREFERENCE))
        {
            nuint bridgeCount = 0;
            byte** bridges = GCScan.GcProcessBridgeObjects(
                GCInterfaceOffsets.max_generation,
                GCInterfaceOffsets.max_generation,
                &sc,
                &bridgeCount);
            for (nuint bridge = 0; bridge < bridgeCount; bridge++)
            {
                background_promote(&bridges[bridge], &sc, 0);
            }

            if (bridgeCount != 0)
            {
                drain_background_mark_stack(hp);
            }
        }
#endif

        if (SoftwareWriteWatch.IsEnabledForGCHeap())
        {
            SoftwareWriteWatch.DisableForGCHeap();
        }

        GCToEEInterface.AfterGcScanRoots(
            GCInterfaceOffsets.max_generation,
            GCInterfaceOffsets.max_generation,
            &sc);
        GCScan.GcShortWeakPtrScan(
            GCInterfaceOffsets.max_generation,
            GCInterfaceOffsets.max_generation,
            &sc);
        nuint promotedBytesLive = get_promoted_bytes(hp);
        finalize_queue->ScanForFinalization(
            &background_promote,
            GCInterfaceOffsets.max_generation,
            hp);
        drain_background_mark_stack(hp);
        nuint promotedBytesWithFinalization = get_promoted_bytes(hp);
        finalization_promoted_bytes = promotedBytesWithFinalization >= promotedBytesLive
            ? promotedBytesWithFinalization - promotedBytesLive
            : 0;
        GCToEEInterface.DiagWalkFReachableObjects(hp);

        if (ObjectHandle.DependentHandleContextsInitialized)
        {
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

        GCScan.GcWeakPtrScan(
            GCInterfaceOffsets.max_generation,
            GCInterfaceOffsets.max_generation,
            &sc);
        GCScan.GcWeakPtrScanBySingleThread(
            GCInterfaceOffsets.max_generation,
            GCInterfaceOffsets.max_generation,
            &sc);

        snapshot_background_allocated(hp);
        current_c_gc_state = c_gc_state.c_gc_state_planning;
        return true;
    }

    private static void background_revisit_reference(byte** slot, void* context)
    {
        background_revisit_context* revisitContext =
            (background_revisit_context*)context;
        if ((byte*)slot >= revisitContext->end)
        {
            return;
        }

        byte* child = (byte*)GCEnv.VolatileLoad((void**)slot);
        if (child >= background_saved_lowest_address &&
            child < background_saved_highest_address &&
            background_mark1(child) != 0 &&
            contain_pointers_or_collectible(child) != 0)
        {
            push_background_mark(child);
        }
    }

    private static void revisit_written_pages(gc_heap* hp, bool concurrent_p)
    {
        set_background_state(bgc_state.bgc_revisit_soh);
        if (SoftwareWriteWatch.GetTable() is null)
        {
            set_background_state(bgc_state.bgc_revisit_uoh);
            return;
        }

        generation* generationTable = generation_table_of(hp);

        for (int genNumber = (int)gc_generation_num.soh_gen2;
             genNumber < (int)gc_generation_num.total_generation_count;
             genNumber++)
        {
            if (genNumber == (int)gc_generation_num.loh_generation)
            {
                set_background_state(bgc_state.bgc_revisit_uoh);
            }

            generation* gen = generation_of(generationTable, genNumber);
            for (heap_segment* segment = generation.generation_start_segment_rw(gen);
                 segment is not null;
                 segment = heap_segment.heap_segment_next(segment))
            {
                byte* start = heap_segment.heap_segment_mem(segment);
                byte* end = heap_segment.heap_segment_allocated(segment);
                if (start >= end)
                {
                    continue;
                }

                byte* current = start;
                while (current < end)
                {
                    nuint count = BackgroundWrittenAddressCount;
                    fixed (nuint* dirtyPageAddresses = &background_written_addresses[0])
                    {
                        void** dirtyPages = (void**)dirtyPageAddresses;
                        if (concurrent_p)
                        {
                            enter_gc_lock();
                        }

                        SoftwareWriteWatch.GetDirty(
                            current,
                            unchecked((nuint)(end - current)),
                            dirtyPages,
                            &count,
                            clearDirty: concurrent_p,
                            isRuntimeSuspended: !concurrent_p);

                        if (concurrent_p)
                        {
                            leave_gc_lock();
                        }

                        for (nuint index = 0; index < count; index++)
                        {
                            byte* page = (byte*)dirtyPageAddresses[(nint)index];
                            revisit_page(
                                segment,
                                page,
                                end,
                                genNumber > (int)gc_generation_num.soh_gen2,
                                concurrent_p);
                        }

                        if (count < BackgroundWrittenAddressCount)
                        {
                            break;
                        }

                        current =
                            (byte*)dirtyPageAddresses[BackgroundWrittenAddressCount - 1] +
                            0x1000;
                    }

                    if (concurrent_p)
                    {
                        allow_foreground_gc();
                    }
                }
            }
        }
    }

    private static void revisit_dirty_cards(gc_heap* hp, bool concurrent_p)
    {
        generation* generationTable = generation_table_of(hp);
        for (int genNumber = (int)gc_generation_num.soh_gen2;
             genNumber < (int)gc_generation_num.total_generation_count;
             genNumber++)
        {
            generation* gen = generation_of(generationTable, genNumber);
            for (heap_segment* segment = generation.generation_start_segment_rw(gen);
                 segment is not null;
                 segment = heap_segment.heap_segment_next(segment))
            {
                byte* start = heap_segment.heap_segment_mem(segment);
                byte* end = heap_segment.heap_segment_allocated(segment);
                if (start >= end)
                {
                    continue;
                }

                nuint searchCard = card_of(start);
                nuint lastCard = card_of(end - 1);
                nuint cardWordEnd = card_table_info.card_word(
                    card_of(card_table_info.align_on_card_word(end)));
                while (find_card(ref searchCard, cardWordEnd, out nuint endCard))
                {
                    if (searchCard > lastCard)
                    {
                        break;
                    }

                    nuint batchEnd = endCard <= lastCard ? endCard : lastCard + 1;
                    for (nuint card = searchCard; card < batchEnd; card++)
                    {
                        revisit_page(
                            segment,
                            card_address(card),
                            end,
                            genNumber > (int)gc_generation_num.soh_gen2,
                            concurrent_p);
                    }

                    if (batchEnd <= searchCard)
                    {
                        break;
                    }

                    searchCard = batchEnd;
                }
            }
        }
    }

    private static void revisit_page(
        heap_segment* segment,
        byte* page,
        byte* segmentEnd,
        bool uoh_p,
        bool concurrent_p)
    {
        byte* pageEnd = page + 0x1000;
        byte* segmentStart = heap_segment.heap_segment_mem(segment);
        if (page < segmentStart)
        {
            page = segmentStart;
        }

        if (pageEnd > segmentEnd)
        {
            pageEnd = segmentEnd;
        }

        byte* current = uoh_p
            ? find_uoh_object_for_card(
                page,
                segmentStart,
                segmentEnd,
                concurrent_p)
            : find_first_object(page, segmentStart);
        while (current < pageEnd)
        {
            if (uoh_p && concurrent_p)
            {
                begin_uoh_object_read(current);
            }

            nuint objectSize = size(current);
            byte* next = current + (nint)(uoh_p
                ? AlignQword(objectSize)
                : Align(objectSize));
            if (next > page &&
                contain_pointers(current) != 0 &&
                background_object_marked(current, clear_p: false))
            {
                background_revisit_context context = default;
                context.end = pageEnd;
                go_through_object(
                    method_table(current),
                    current,
                    objectSize,
                    &context,
                    &background_revisit_reference,
                    page,
                    start_useful: 1);
            }

            if (uoh_p && concurrent_p)
            {
                end_uoh_object_read();
            }

            current = next;
        }
    }

    public static void begin_uoh_allocation(byte* obj)
    {
        c_gc_state state = current_c_gc_state;
        if (state == c_gc_state.c_gc_state_planning)
        {
            Interlocked.Increment(ref uoh_alloc_thread_count);
            return;
        }

        if (state != c_gc_state.c_gc_state_marking)
        {
            return;
        }

        while (true)
        {
            enter_uoh_publication_lock();
            if ((byte*)System.Threading.Volatile.Read(ref uoh_object_being_read) == obj)
            {
                leave_uoh_publication_lock();
                wait_for_uoh_object_read(obj);
                continue;
            }

            for (int i = 0; i < MaxPendingUohAllocations; i++)
            {
                if (System.Threading.Volatile.Read(ref pending_uoh_allocations[i]) == 0)
                {
                    System.Threading.Volatile.Write(
                        ref pending_uoh_allocations[i],
                        (nint)obj);
                    leave_uoh_publication_lock();
                    return;
                }
            }

            leave_uoh_publication_lock();
            wait_for_pending_uoh_slot();
        }
    }

    public static void publish_uoh_allocation(byte* obj)
    {
        for (int i = 0; i < MaxPendingUohAllocations; i++)
        {
            if ((byte*)System.Threading.Volatile.Read(
                    ref pending_uoh_allocations[i]) == obj)
            {
                System.Threading.Volatile.Write(
                    ref pending_uoh_allocations[i],
                    0);
                break;
            }
        }

        if (current_c_gc_state == c_gc_state.c_gc_state_planning)
        {
            Interlocked.Decrement(ref uoh_alloc_thread_count);
        }
    }

    private static void begin_uoh_object_read(byte* obj)
    {
        while (true)
        {
            enter_uoh_publication_lock();
            bool allocationPending = false;
            for (int i = 0; i < MaxPendingUohAllocations; i++)
            {
                if ((byte*)System.Threading.Volatile.Read(
                        ref pending_uoh_allocations[i]) == obj)
                {
                    allocationPending = true;
                    break;
                }
            }

            if (!allocationPending)
            {
                System.Threading.Volatile.Write(
                    ref uoh_object_being_read,
                    (nint)obj);
                leave_uoh_publication_lock();
                return;
            }

            leave_uoh_publication_lock();
            wait_for_uoh_allocation_publication(obj);
        }
    }

    private static void end_uoh_object_read() =>
        System.Threading.Volatile.Write(ref uoh_object_being_read, 0);

    private static void enter_uoh_publication_lock()
    {
        while (Interlocked.CompareExchange(
            ref uoh_publication_lock,
            1,
            0) != 0)
        {
            spin_for_uoh_publication();
        }
    }

    private static void leave_uoh_publication_lock() =>
        System.Threading.Volatile.Write(ref uoh_publication_lock, 0);

    private static void wait_for_uoh_object_read(byte* obj)
    {
        while ((byte*)System.Threading.Volatile.Read(
            ref uoh_object_being_read) == obj)
        {
            spin_for_uoh_publication();
        }
    }

    private static void wait_for_uoh_allocation_publication(byte* obj)
    {
        while (true)
        {
            bool allocationPending = false;
            for (int i = 0; i < MaxPendingUohAllocations; i++)
            {
                if ((byte*)System.Threading.Volatile.Read(
                        ref pending_uoh_allocations[i]) == obj)
                {
                    allocationPending = true;
                    break;
                }
            }

            if (!allocationPending)
            {
                return;
            }

            spin_for_uoh_publication();
        }
    }

    private static void wait_for_pending_uoh_slot()
    {
        while (true)
        {
            for (int i = 0; i < MaxPendingUohAllocations; i++)
            {
                if (System.Threading.Volatile.Read(
                    ref pending_uoh_allocations[i]) == 0)
                {
                    return;
                }
            }

            spin_for_uoh_publication();
        }
    }

    private static void wait_for_uoh_allocations()
    {
        while (System.Threading.Volatile.Read(ref uoh_alloc_thread_count) != 0)
        {
            spin_for_uoh_publication();
        }

        for (int i = 0; i < MaxPendingUohAllocations; i++)
        {
            Debug.Assert(System.Threading.Volatile.Read(
                ref pending_uoh_allocations[i]) == 0);
        }
    }

    private static void spin_for_uoh_publication()
    {
        uint spinCount = gc_heap.yp_spin_count_unit;
        for (uint i = 0; i < spinCount; i++)
        {
            GCEnv.YieldProcessor();
        }

        GCToOSInterface.YieldThread(0);
    }

    private static void prepare_background_sweep(gc_heap* hp)
    {
        generation* generationTable = generation_table_of(hp);
        for (int genNumber = 0;
             genNumber <= (int)gc_generation_num.soh_gen2;
             genNumber++)
        {
            generation* gen = generation_of(generationTable, genNumber);
            if (genNumber != (int)gc_generation_num.soh_gen2)
            {
                allocator.clear(generation.generation_allocator(gen));
                generation.generation_free_list_space(gen) = 0;
                generation.generation_free_obj_space(gen) = 0;
            }

            generation.generation_free_list_allocated(gen) = 0;
            generation.generation_end_seg_allocated(gen) = 0;
            generation.generation_condemned_allocated(gen) = 0;
            generation.generation_sweep_allocated(gen) = 0;
            generation.generation_allocation_pointer(gen) = null;
            generation.generation_allocation_limit(gen) = null;
            generation.generation_allocation_segment(gen) =
                generation.generation_start_segment_rw(gen);
            dynamic_data.dd_survived_size(dynamic_data_of(hp, genNumber)) = 0;
        }

        set_background_state(bgc_state.bgc_sweep_soh);
        current_sweep_seg = generation.generation_start_segment_rw(
            generation_of(generationTable, (int)gc_generation_num.soh_gen2));
        current_sweep_pos = null;
    }

    private static bool background_sweep(gc_heap* hp)
    {
        allocator youngestFreeList = default;
        allocator.initialize(&youngestFreeList);
        nuint youngestFreeListSpace = 0;
        nuint youngestFreeObjectSpace = 0;
        generation* generationTable = generation_table_of(hp);

        for (int genNumber = (int)gc_generation_num.soh_gen1;
             genNumber >= (int)gc_generation_num.soh_gen0;
             genNumber--)
        {
            generation* gen = generation_of(generationTable, genNumber);
            if (!background_sweep_generation(
                hp,
                gen,
                uoh_p: false,
                genNumber == (int)gc_generation_num.soh_gen0
                    ? &youngestFreeList
                    : null,
                &youngestFreeListSpace,
                &youngestFreeObjectSpace,
                track_sweep_cursor: false))
            {
                leave_gc_lock();
                return false;
            }
        }

        GCSpinLock.enter(&hp->more_space_lock_soh);
        *generation.generation_allocator(
            generation_of(generationTable, (int)gc_generation_num.soh_gen0)) =
            youngestFreeList;
        generation.generation_free_list_space(
            generation_of(generationTable, (int)gc_generation_num.soh_gen0)) =
            youngestFreeListSpace;
        generation.generation_free_obj_space(
            generation_of(generationTable, (int)gc_generation_num.soh_gen0)) =
            youngestFreeObjectSpace;
        GCSpinLock.leave(&hp->more_space_lock_soh);
        leave_gc_lock();

        if (!background_sweep_generation(
            hp,
            generation_of(generationTable, (int)gc_generation_num.soh_gen2),
            uoh_p: false,
            null,
            &youngestFreeListSpace,
            &youngestFreeObjectSpace,
            track_sweep_cursor: true))
        {
            return false;
        }

        GCEvents.GCEventFireBGC1stSweepEnd(0);
        GCSpinLock.enter(&hp->more_space_lock_uoh);
        wait_for_uoh_allocations();
        set_background_state(bgc_state.bgc_sweep_uoh);
        for (int genNumber = (int)gc_generation_num.loh_generation;
             genNumber < (int)gc_generation_num.total_generation_count;
             genNumber++)
        {
            generation* gen = generation_of(generationTable, genNumber);
            allocator.clear(generation.generation_allocator(gen));
            generation.generation_free_list_space(gen) = 0;
            generation.generation_free_obj_space(gen) = 0;
            generation.generation_free_list_allocated(gen) = 0;
            generation.generation_end_seg_allocated(gen) = 0;
            generation.generation_condemned_allocated(gen) = 0;
            generation.generation_sweep_allocated(gen) = 0;
            generation.generation_allocation_pointer(gen) = null;
            generation.generation_allocation_limit(gen) = null;
            generation.generation_allocation_segment(gen) =
                generation.generation_start_segment_rw(gen);
            dynamic_data.dd_survived_size(dynamic_data_of(hp, genNumber)) = 0;

            if (!background_sweep_generation(
                hp,
                gen,
                uoh_p: true,
                null,
                &youngestFreeListSpace,
                &youngestFreeObjectSpace,
                track_sweep_cursor: false))
            {
                GCSpinLock.leave(&hp->more_space_lock_uoh);
                return false;
            }
        }
        GCSpinLock.leave(&hp->more_space_lock_uoh);

        GCEvents.GCEventFireBGC2ndConEnd();
        return true;
    }

    private static bool background_sweep_generation(
        gc_heap* hp,
        generation* gen,
        bool uoh_p,
        allocator* youngestFreeList,
        nuint* youngestFreeListSpace,
        nuint* youngestFreeObjectSpace,
        bool track_sweep_cursor)
    {
        int genNumber = gen->gen_num;
        int alignConst = get_alignment_constant(!uoh_p);
        heap_segment* startSegment = generation.generation_start_segment_rw(gen);
        heap_segment* previousSegment = null;
        heap_segment* segment = startSegment;
        while (segment is not null)
        {
            heap_segment* nextSegment = heap_segment.heap_segment_next(segment);
            byte* end = heap_segment.heap_segment_background_allocated(segment);
            if (end is null)
            {
                if (track_sweep_cursor)
                {
                    break;
                }

                segment->flags |= heap_segment.heap_segment_flags_swept;
                previousSegment = segment;
                segment = nextSegment;
                continue;
            }

            byte* current = heap_segment.heap_segment_mem(segment);
            byte* gapStart = current;
            if (track_sweep_cursor)
            {
                current_sweep_seg = segment;
                current_sweep_pos = current;
            }

            nuint survived = 0;
            nuint freeObjectSizeLastGap = 0;
            int processed = 0;

            while (current < end)
            {
                nuint objectSize = size(current);
                nuint alignedSize = uoh_p
                    ? AlignQword(objectSize)
                    : Align(objectSize, alignConst);
                byte* next = current + (nint)alignedSize;
                if (next <= current || next > end)
                {
                    GCToEEInterface.HandleFatalError(CORINFO_EXCEPTION_GC);
                    return false;
                }

                if (background_object_marked(current, clear_p: true))
                {
                    thread_background_gap(
                        gen,
                        gapStart,
                        unchecked((nuint)(current - gapStart)),
                        youngestFreeList,
                        youngestFreeListSpace,
                        youngestFreeObjectSpace);
                    if (freeObjectSizeLastGap != 0)
                    {
                        Debug.Assert(
                            generation.generation_free_obj_space(gen) >=
                            freeObjectSizeLastGap);
                        generation.generation_free_obj_space(gen) -=
                            freeObjectSizeLastGap;
                        freeObjectSizeLastGap = 0;
                    }

                    survived = unchecked(survived + alignedSize);
                    gapStart = next;
                }
#if TARGET_64BIT && !TARGET_WASM
                else if (track_sweep_cursor &&
                    ((CObjectHeader*)current)->GetMethodTable() ==
                        GCCommon.g_gc_pFreeObjectMethodTable)
                {
                    freeObjectSizeLastGap = unchecked(
                        freeObjectSizeLastGap + alignedSize);
                    if (allocator.is_on_free_list(current, alignedSize))
                    {
                        allocator.unlink_item_no_undo(
                            generation.generation_allocator(gen),
                            current,
                            alignedSize);
                        Debug.Assert(
                            generation.generation_free_list_space(gen) >=
                            alignedSize);
                        generation.generation_free_list_space(gen) -=
                            alignedSize;
                        generation.generation_free_obj_space(gen) = unchecked(
                            generation.generation_free_obj_space(gen) +
                            alignedSize);
                    }
                }
#endif

                current = next;
                if (++processed == 256)
                {
                    if (track_sweep_cursor)
                    {
                        current_sweep_pos = current;
                    }

                    allow_foreground_gc();
                    processed = 0;
                }
            }

            bool deleteSegment =
                gapStart == heap_segment.heap_segment_mem(segment) &&
                heap_segment.heap_segment_allocated(segment) == end &&
                segment != startSegment;
            if (!deleteSegment)
            {
                if (heap_segment.heap_segment_allocated(segment) != end)
                {
                    thread_background_gap(
                        gen,
                        gapStart,
                        unchecked((nuint)(end - gapStart)),
                        youngestFreeList,
                        youngestFreeListSpace,
                        youngestFreeObjectSpace);
                }

                if (freeObjectSizeLastGap != 0)
                {
                    Debug.Assert(
                        generation.generation_free_obj_space(gen) >=
                        freeObjectSizeLastGap);
                    generation.generation_free_obj_space(gen) -=
                        freeObjectSizeLastGap;
                }

                if (heap_segment.heap_segment_allocated(segment) == end)
                {
                    heap_segment.heap_segment_allocated(segment) = gapStart;
                }

                previousSegment = segment;
                segment->flags |= heap_segment.heap_segment_flags_swept;
                heap_segment.heap_segment_saved_bg_allocated(segment) = end;
                heap_segment.heap_segment_background_allocated(segment) = null;
            }
            else
            {
                if (freeObjectSizeLastGap != 0)
                {
                    Debug.Assert(
                        generation.generation_free_obj_space(gen) >=
                        freeObjectSizeLastGap);
                    generation.generation_free_obj_space(gen) -=
                        freeObjectSizeLastGap;
                }

                if (previousSegment is not null)
                {
                    heap_segment.heap_segment_next(previousSegment) = nextSegment;
                }

                update_start_tail_regions(
                    gen,
                    segment,
                    previousSegment,
                    nextSegment);
                heap_segment.heap_segment_allocated(segment) =
                    heap_segment.heap_segment_mem(segment);
                if (uoh_p)
                {
                    heap_segment.heap_segment_next(segment) =
                        freeable_uoh_segment;
                    freeable_uoh_segment = segment;
                }
                else
                {
                    heap_segment.heap_segment_next(segment) =
                        freeable_soh_segment;
                    freeable_soh_segment = segment;
                }
            }

            dynamic_data.dd_survived_size(dynamic_data_of(hp, genNumber)) =
                unchecked(
                    dynamic_data.dd_survived_size(dynamic_data_of(hp, genNumber)) +
                    survived);
            if (track_sweep_cursor)
            {
                current_sweep_pos = end;
            }

            segment = nextSegment;
        }

        generation.generation_allocation_segment(gen) =
            generation.generation_start_segment_rw(gen);
        return true;
    }

    private static void thread_background_gap(
        generation* gen,
        byte* gap,
        nuint gapSize,
        allocator* youngestFreeList,
        nuint* youngestFreeListSpace,
        nuint* youngestFreeObjectSpace)
    {
        if (gapSize == 0)
        {
            return;
        }

        make_unused_array(gap, gapSize, clearp: 0, resetp: 0);
        if (youngestFreeList is null)
        {
            if (gapSize >= unchecked(2 * (nuint)GCInterfaceOffsets.min_obj_size))
            {
                generation.generation_free_list_space(gen) = unchecked(
                    generation.generation_free_list_space(gen) + gapSize);
                allocator.thread_item(
                    generation.generation_allocator(gen),
                    gap,
                    gapSize);
            }
            else
            {
                generation.generation_free_obj_space(gen) = unchecked(
                    generation.generation_free_obj_space(gen) + gapSize);
            }

            return;
        }

        if (gapSize >= unchecked(2 * (nuint)GCInterfaceOffsets.min_obj_size))
        {
            *youngestFreeListSpace = unchecked(*youngestFreeListSpace + gapSize);
            allocator.thread_item(youngestFreeList, gap, gapSize);
        }
        else
        {
            *youngestFreeObjectSpace = unchecked(*youngestFreeObjectSpace + gapSize);
        }
    }

    private static bool background_object_marked(byte* o, bool clear_p)
    {
        if (o < background_saved_lowest_address ||
            o >= background_saved_highest_address)
        {
            return true;
        }

        if (mark_array_marked(o) == 0)
        {
            return false;
        }

        if (clear_p)
        {
            mark_array_clear_marked(o);
        }

        return true;
    }

    public static bool fgc_should_consider_object(
        byte* o,
        heap_segment* seg,
        bool consider_bgc_mark_p,
        bool check_current_sweep_p)
    {
        bool no_bgc_mark_p = false;
        if (consider_bgc_mark_p)
        {
            if (check_current_sweep_p && o < current_sweep_pos)
            {
                no_bgc_mark_p = true;
            }

            if (!no_bgc_mark_p)
            {
                byte* background_allocated =
                    heap_segment.heap_segment_background_allocated(seg);
                if (o >= background_allocated)
                {
                    no_bgc_mark_p = true;
                }
            }
        }
        else
        {
            no_bgc_mark_p = true;
        }

        return no_bgc_mark_p ||
            background_object_marked(o, clear_p: false);
    }

    public static void should_check_bgc_mark(
        heap_segment* seg,
        out bool consider_bgc_mark_p,
        out bool check_current_sweep_p)
    {
        consider_bgc_mark_p = false;
        check_current_sweep_p = false;

        if (current_c_gc_state == c_gc_state.c_gc_state_planning)
        {
            if (heap_segment.heap_segment_swept_p(seg) != 0 ||
                current_sweep_pos == heap_segment.heap_segment_reserved(seg))
            {
                return;
            }

            if (heap_segment.heap_segment_background_allocated(seg) is null)
            {
                return;
            }

            consider_bgc_mark_p = true;
            if (current_sweep_pos is not null &&
                in_range_for_segment(current_sweep_pos, seg) != 0)
            {
                check_current_sweep_p = true;
            }
        }
    }

    public static bool should_set_bgc_mark_bit(byte* o)
    {
        if (current_sweep_seg is null)
        {
            Debug.Assert(current_bgc_state == bgc_state.bgc_not_in_process);
            return false;
        }

        if (in_range_for_segment(o, current_sweep_seg) != 0)
        {
            return o >= current_sweep_pos &&
                o < heap_segment.heap_segment_background_allocated(
                    current_sweep_seg);
        }

        if (o < background_saved_lowest_address ||
            o >= background_saved_highest_address)
        {
            return false;
        }

        heap_segment* seg = region_of(o);
        byte* background_allocated =
            heap_segment.heap_segment_background_allocated(seg);
        return background_allocated is not null &&
            o < background_allocated &&
            heap_segment.heap_segment_swept_p(seg) == 0;
    }

    private static void allow_foreground_gc()
    {
#if MANAGED_GC_TEST_HOST
        bool toggled = GCToEEInterface.EnablePreemptiveGC() != 0;
        if (System.Threading.Volatile.Read(ref test_pause_background) != 0)
        {
            System.Threading.Volatile.Write(
                ref test_background_pause_observed,
                1);
            while (System.Threading.Volatile.Read(ref test_pause_background) != 0)
            {
                GCToOSInterface.Sleep(1);
            }
        }
        if (toggled)
        {
            GCToEEInterface.DisablePreemptiveGC();
        }
#else
        ManagedGC_AllowForegroundGC();
#endif
    }

#if !MANAGED_GC_TEST_HOST
    [RuntimeImport("*", "ManagedGC_AllowForegroundGC")]
    [MethodImpl(MethodImplOptions.InternalCall)]
    private static extern void ManagedGC_AllowForegroundGC();
#endif

    private static void finish_background_collection_accounting(gc_heap* hp)
    {
        compute_new_dynamic_data(hp, GCInterfaceOffsets.max_generation);

        gc_history_per_heap* history =
            (gc_history_per_heap*)Unsafe.AsPointer(ref bgc_data_per_heap);
        for (int genNumber = 0;
             genNumber < GCInterfaceOffsets.max_generation;
             genNumber++)
        {
            generation* gen =
                generation_of(generation_table_of(hp), genNumber);
            ref gc_generation_data genData =
                ref gc_history_per_heap.gen_data(history, genNumber);
            genData.size_after = generation_size(hp, genNumber);
            genData.free_list_space_after =
                generation.generation_free_list_space(gen);
            genData.free_obj_space_after =
                generation.generation_free_obj_space(gen);
        }

        bgc_data_global.final_youngest_desired =
            dynamic_data.dd_desired_allocation(
                dynamic_data_of(hp, (int)gc_generation_num.soh_gen0));
        FirePrivateEvents();
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
        record_gc_info(hp);
        update_full_gc_notification_after_gc(hp);
        fgn_last_alloc = unchecked((nuint)dynamic_data.dd_new_allocation(
            dynamic_data_of(hp, (int)gc_generation_num.soh_gen0)));
        GCToEEInterface.GcDone(GCInterfaceOffsets.max_generation);
        UpdatePostGCCounters(hp);
    }

    private static void complete_background_gc(bool collectionCompleted)
    {
        settings.concurrent = 0;
        settings.background_p = 0;
        current_c_gc_state = c_gc_state.c_gc_state_free;
        current_sweep_seg = null;
        current_sweep_pos = null;
        set_background_state(bgc_state.bgc_not_in_process);
        gc_background_running = 0;

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
                segment->flags &= ~heap_segment.heap_segment_flags_swept;
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
        nuint markedObjects = 0;
        bool scanAll = background_mark_stack_overflow != 0;
        int processedSinceYield = 0;

        do
        {
            background_mark_stack_overflow = 0;
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
                if (++processedSinceYield == 256 &&
                    current_bgc_state != bgc_state.bgc_final_marking)
                {
                    allow_foreground_gc();
                    processedSinceYield = 0;
                }
            }

            if (scanAll || background_mark_stack_overflow != 0)
            {
                background_mark_stack_overflow = 0;
                scan_marked_objects_for_overflow(hp);
                scanAll = background_mark_stack_overflow != 0;
            }
        }
        while (scanAll ||
            background_mark_stack_overflow != 0 ||
            background_mark_stack_tos != 0);

        GCEvents.GCEventFireBGCDrainMark(markedObjects);
    }

    private static void scan_marked_objects_for_overflow(gc_heap* hp)
    {
        generation* generationTable = generation_table_of(hp);
        for (int genNumber = 0;
             genNumber < (int)gc_generation_num.total_generation_count;
             genNumber++)
        {
            bool uoh_p = genNumber >= (int)gc_generation_num.loh_generation;
            generation* gen = generation_of(generationTable, genNumber);
            for (heap_segment* segment = generation.generation_start_segment_rw(gen);
                 segment is not null;
                 segment = heap_segment.heap_segment_next(segment))
            {
                byte* current = heap_segment.heap_segment_mem(segment);
                byte* end = heap_segment.heap_segment_allocated(segment);
                while (current < end)
                {
                    bool publicationReadHeld =
                        uoh_p &&
                        current_c_gc_state == c_gc_state.c_gc_state_marking;
                    if (publicationReadHeld)
                    {
                        begin_uoh_object_read(current);
                    }

                    nuint objectSize = size(current);
                    if (background_object_marked(current, clear_p: false) &&
                        contain_pointers_or_collectible(current) != 0)
                    {
                        go_through_object_nostart(
                            method_table(current),
                            current,
                            objectSize,
                            null,
                            &background_mark_reference);
                    }

                    if (publicationReadHeld)
                    {
                        end_uoh_object_read();
                    }

                    current += (nint)(uoh_p
                        ? AlignQword(objectSize)
                        : Align(objectSize));
                }
            }
        }
    }

    public static uint background_gc_wait(
        uint timeout = GCEnv.INFINITE,
        alloc_wait_reason reason = alloc_wait_reason.awr_ignored)
    {
        if (!background_gc_done_event.IsValid())
        {
            return GCEnv.WAIT_OBJECT_0;
        }

        if (reason != alloc_wait_reason.awr_ignored)
        {
            GCEvents.GCEventFireBGCAllocWaitBegin(unchecked((uint)reason));
        }

#if MANAGED_GC_TEST_HOST
        uint result = background_gc_done_event.Wait(timeout, alertable: false);
#else
        uint result = background_gc_done_event.UserThreadWait(timeout);
#endif
        if (reason != alloc_wait_reason.awr_ignored)
        {
            GCEvents.GCEventFireBGCAllocWaitEnd(unchecked((uint)reason));
        }

        return result;
    }

    public static bool background_collection_running_p() =>
        background_running_p();

    public static bool background_collection_pending_p() =>
        background_running_p();

    public static bool concurrent_gc_enabled() =>
        gc_can_use_concurrent &&
        System.Threading.Volatile.Read(ref temp_disable_concurrent_p) == 0;

    public static void set_temp_disable_concurrent(bool disabled) =>
        System.Threading.Volatile.Write(
            ref temp_disable_concurrent_p,
            disabled ? 1 : 0);

    private static void set_background_state(bgc_state state)
    {
        current_bgc_state = state;
#if MANAGED_GC_TEST_HOST
        background_state_transitions |= 1UL << (int)state;
#endif
    }

#if MANAGED_GC_TEST_HOST
    public static bool background_state_was_observed(bgc_state state) =>
        (background_state_transitions & (1UL << (int)state)) != 0;

    public static void request_background_pause_for_test()
    {
        System.Threading.Volatile.Write(ref test_background_pause_observed, 0);
        System.Threading.Volatile.Write(ref test_pause_background, 1);
    }

    public static bool background_pause_observed_for_test() =>
        System.Threading.Volatile.Read(ref test_background_pause_observed) != 0;

    public static void release_background_pause_for_test() =>
        System.Threading.Volatile.Write(ref test_pause_background, 0);

    public static int foreground_during_bgc_count_for_test() =>
        System.Threading.Volatile.Read(ref test_foreground_during_bgc_count);

    public static gc_mechanisms last_restored_bgc_settings_for_test() =>
        test_last_restored_bgc_settings;

    public static void record_foreground_during_bgc_for_test()
    {
        test_last_restored_bgc_settings = settings;
        System.Threading.Interlocked.Increment(
            ref test_foreground_during_bgc_count);
    }

    public static void set_background_sweep_position_for_test(
        c_gc_state state,
        heap_segment* segment,
        byte* position)
    {
        current_c_gc_state = state;
        current_sweep_seg = segment;
        current_sweep_pos = position;
    }

    public static void reset_uoh_publication_for_test(bool concurrentMarking)
    {
        uoh_publication_lock = 0;
        uoh_object_being_read = 0;
        pending_uoh_allocations = default;
        uoh_alloc_thread_count = 0;
        current_c_gc_state = concurrentMarking
            ? c_gc_state.c_gc_state_marking
            : c_gc_state.c_gc_state_free;
    }

    public static void begin_uoh_object_read_for_test(byte* obj) =>
        begin_uoh_object_read(obj);

    public static void end_uoh_object_read_for_test() =>
        end_uoh_object_read();

    public static int uoh_alloc_thread_count_for_test() =>
        System.Threading.Volatile.Read(ref uoh_alloc_thread_count);

    public static c_gc_state current_c_gc_state_for_test() =>
        current_c_gc_state;

    public static void reset_background_event_for_test()
    {
        background_gc_done_event = default;
    }
#endif

#if MANAGED_GC_TEST_HOST
    private static int ManagedGC_CreateBackgroundThread(
        delegate*<void*, void> threadStart,
        void* context,
        int* shutdown,
        int* exited,
        void** worker,
        byte* name) =>
        GCToEEInterface.CreateBackgroundThread(
            threadStart,
            context,
            shutdown,
            exited,
            worker,
            name);

    private static void ManagedGC_SignalBackgroundThread(void* worker) =>
        GCToEEInterface.SignalBackgroundThread(worker);
#else
    [System.Runtime.RuntimeImport("*", "ManagedGC_CreateBackgroundThread")]
    [MethodImpl(MethodImplOptions.InternalCall)]
    private static extern int ManagedGC_CreateBackgroundThread(
        delegate*<void*, void> threadStart,
        void* context,
        int* shutdown,
        int* exited,
        void** worker,
        byte* name);

    [System.Runtime.RuntimeImport("*", "ManagedGC_SignalBackgroundThread")]
    [MethodImpl(MethodImplOptions.InternalCall)]
    private static extern void ManagedGC_SignalBackgroundThread(void* worker);
#endif
#else
    public static bool background_collection_running_p() => false;

    public static bool background_collection_pending_p() => false;

    public static bool concurrent_gc_enabled() => false;

    public static void set_temp_disable_concurrent(bool disabled)
    {
        _ = disabled;
    }

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

    public static uint background_gc_wait(
        uint timeout = GCEnv.INFINITE,
        alloc_wait_reason reason = alloc_wait_reason.awr_ignored)
    {
        _ = timeout;
        _ = reason;
        return GCEnv.WAIT_OBJECT_0;
    }
#endif
}
#pragma warning restore CS8981
