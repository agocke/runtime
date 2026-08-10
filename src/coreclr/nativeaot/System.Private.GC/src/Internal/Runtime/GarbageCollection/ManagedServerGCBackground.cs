// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Slice B of the SERVER_GC / MULTIPLE_HEAPS / USE_REGIONS background collector: routing a
// non-blocking gen2 request onto the translated background pipeline and running the initial
// stop-the-world background-mark join sequence across all heaps.
//
// Native source: the concurrent gate of collect.cpp gc_heap::garbage_collect / gc1 (the
// settings.concurrent branch that saves saved_bgc_settings, sets current_bgc_state=bgc_initialized
// and drives do_background_gc) and the initial stop-the-world section of background.cpp
// gc_heap::background_mark_phase up to the gc_join_after_reset restart (root scan, the
// gc_join_restart_ee / gc_join_after_reset joins, the c_gc_state_marking / cm_in_progress
// publication).
//
// What this slice routes (real, exercised by the smoke):
//   * The IGCHeap::GarbageCollect non-blocking gen2 request decision (background.cpp
//     garbage_collect_background prologue) and the fall-back to the blocking path when the request
//     is not a background-eligible gen2 or concurrent GC is disabled/unsupported.
//   * The background collection kickoff on the joined worker (heap 0): saved_bgc_settings,
//     background_gc_info bookkeeping, full_gc_counts[gc_type_background], and the background state
//     machine transitions bgc_initialized -> bgc_reset_ww -> bgc_mark_handles -> bgc_not_in_process.
//   * The cross-heap bgc_t_join initial-mark stages gc_join_restart_ee and gc_join_after_reset run
//     by every server worker, plus the concurrent write-barrier state publication
//     (current_c_gc_state = c_gc_state_marking, cm_in_progress = TRUE) that they record.
//   * A consistent heap: the collection completes its reclamation through the proven blocking
//     mark/plan/sweep (ManagedServerGCCollect.gc1), so survivors, weak references and finalization
//     stay correct while the background state machine and joins are exercised.
//
// What this slice explicitly gates (deferred to the concurrent-mark slice, reported as the next
// dependency):
//   * Spawning and waking the dedicated bgc worker threads (prepare_bgc_thread / start_c_gc):
//     the initial mark currently runs on the already-suspended, already-joined foreground server
//     workers rather than on the bgc workers, so no unused worker parks on bgc_start_event and there
//     is no live-concurrent restart to coordinate.
//   * The actual root / finalizer / handle scan into the background mark array (needs the per-heap
//     background marking primitives -- background_promote, the per-heap background mark stack,
//     drain_background_mark_stack -- which are still WKS-only in BackgroundGC.cs).
//   * The software-write-watch table is now initialized over the whole reserved range in
//     make_card_table (when concurrent GC is configured) and published/reset at the window open
//     (SoftwareWriteWatch.EnableForGCHeap after reset_software_write_watch) and disabled at the
//     re-suspend, so mutator reference stores during the live c_gc_state_marking window are recorded
//     in the write-watch table. The concurrent revisit that would consume that dirty state into a
//     complete mark array (revisit_written_pages / cross-heap overflow) remains deferred -- the
//     reclamation is still the proven blocking mark/plan/sweep, which discards the background marks.
//   * The concurrent mark / revisit / background sweep and their bgc_t_join stages.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

using System.Runtime.CompilerServices;
using System.Threading;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    // clrgc.managed.cpp: the suspension handshake used by bgc_suspend_EE to re-suspend the EE for the
    // background final mark while cooperative mutators may be inside managed allocation helpers. WKS
    // declares these in BackgroundGC.cs, which the server build excludes.
    [System.Runtime.RuntimeImport("*", "ManagedGC_PrepareForSuspension")]
    [MethodImpl(MethodImplOptions.InternalCall)]
    private static extern void ManagedGC_PrepareForSuspension();

    [System.Runtime.RuntimeImport("*", "ManagedGC_CompleteSuspension")]
    [MethodImpl(MethodImplOptions.InternalCall)]
    private static extern void ManagedGC_CompleteSuspension();

    // Number of times a mutator parked in check_and_wait_for_bgc's preemptive background allocation
    // wait. Diagnostic counter for the allocation-path background-wait unblocker.
    public static int s_bgc_alloc_wait_count;

    // Number of background collections that have fully completed (re-suspended and reclaimed). Unlike
    // full_gc_counts[gc_type_background], which advances at kickoff, this advances only when a
    // background collection finishes, so the smoke can prove re-suspension completes repeatedly.
    public static int s_bgc_completed_count;

    // Set by the foreground worker (gc_thread_function) once it has restarted the EE to open the
    // concurrent window; read by the bgc workers so their re-suspension does not race ahead of the
    // restart into a double-suspend.
    public static int bgc_ee_restarted;

    // How long the bgc workers hold the concurrent window open before re-suspending (milliseconds),
    // so a mutator can make observable progress while current_c_gc_state == c_gc_state_marking. Small
    // and bounded: this milestone reclaims through the blocking fallback after the window.
    private const uint bgc_window_hold_ms = 15;

    // background.cpp gc_heap::bgc_suspend_EE (MULTIPLE_HEAPS): re-suspend the EE for the background
    // final mark. Uses the ManagedGC_PrepareForSuspension handshake so a cooperative mutator inside a
    // managed allocation helper (or parked in the preemptive background allocation wait) does not
    // stall the suspend. The caller (heap 0) holds gc_lock.
    private static void bgc_suspend_EE()
    {
        for (int i = 0; i < n_heaps; i++)
        {
            reset_gc_done(g_heaps[i]);
        }
        gc_started = 1;
        ManagedGC_PrepareForSuspension();
        GCToEEInterface.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC_PREP);
        ManagedGC_CompleteSuspension();
        gc_started = 0;
        for (int i = 0; i < n_heaps; i++)
        {
            set_gc_done(g_heaps[i]);
        }
    }

    // background.cpp gc_heap::reset_write_watch / reset_software_write_watch: clear the software
    // write-watch dirty state over this heap's committed regions so the concurrent window starts from a
    // clean slate; only pages the mutators dirty during the window remain marked, which the final
    // revisit consumes. Only meaningful when the write-watch table is present (initialized in
    // make_card_table when concurrent GC is configured).
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
                    SoftwareWriteWatch.ClearDirty(start, unchecked((nuint)(end - start)));
                }
            }
        }
    }

    // IGCHeap collect return codes (background.cpp / gcinterface.h). WKS defines these in Collect.cs,
    // which the server build excludes, so the server takes them here.
    public const int background_collection_s_ok = 0;
    public const int background_collection_e_notimpl = unchecked((int)0x80004001);

    // Published under gc_lock by the triggering thread of the actual collection and read by the joined
    // server worker's kickoff decision in garbage_collect. 1 iff this collection is a background
    // (non-blocking gen2) kickoff; 0 for a blocking / foreground collection. Because gc_lock serializes
    // collections, exactly one triggering thread publishes this per collection and the worker reads it
    // after gc_start_event, so it needs no per-thread ownership.
    //
    // This replaces the old global background_gc_requested bit. That bit was written around the whole
    // GarbageCollectGenerationServer call by *every* background caller, so a second concurrent
    // non-blocking request could observe it set (by itself or by the in-flight kickoff) and wrongly
    // bypass the background wait, starting a second kickoff while one was already running -> deadlock.
    // The kickoff/coalesce decision is now made under gc_lock from the explicit backgroundKickoff
    // parameter and background_running_p (see GarbageCollectGenerationServer), not from a shared bit.
    public static int gc_is_background_kickoff;

    // gcpriv.h gc_heap::concurrent_gc_enabled. The server keeps gc_can_use_concurrent false so it
    // does not perturb condemn/pause decisions; background eligibility is gated on the concurrent-GC
    // config directly (matching Slice A's initialize_background_gc gate) plus the presence of the
    // background support created by create_bgc_threads_support.
    private static bool concurrent_gc_enabled()
    {
        return GCConfig.GetConcurrentGC() != 0 && bgc_start_event.IsValid();
    }

    // Observability for the smoke: whether this run will route non-blocking gen2 requests through the
    // background collector. Mirrors the garbage_collect_background eligibility gate.
    public static bool background_gc_available() => concurrent_gc_enabled();

    // background.cpp gc_heap::garbage_collect_background prologue (server, bounded). Decides whether a
    // non-blocking request is a background-eligible gen2 collection and, if so, routes it through the
    // server worker path with the background request flag set. Returns background_collection_e_notimpl
    // when the request must fall back to the blocking foreground path, exactly as native returns from
    // the early-out of garbage_collect_background.
    public static int garbage_collect_background(int generation, byte low_memory_p, int mode)
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
            !concurrent_gc_enabled() ||
            GCConfig.GetHeapVerifyLevel() != 0 ||
            survivorAnalysisRequested)
        {
            return background_collection_e_notimpl;
        }

        // A background collection returns to the caller while it is still running concurrently (the
        // bgc workers own the window / reclamation). A second non-blocking request while one is in
        // progress is a no-op, matching native background_running_p short-circuit.
        if (background_running_p())
        {
            return background_collection_s_ok;
        }

        gc_reason reason = low_memory_p != 0
            ? gc_reason.reason_lowmemory
            : gc_reason.reason_induced_noforce;

        // Lazily create the dedicated background workers on the triggering (mutator) thread, before
        // the collection suspends the EE. Creating them here rather than during the stop-the-world
        // kickoff keeps managed thread startup/attach out of the suspension window.
        for (int i = 0; i < n_heaps; i++)
        {
            if (!prepare_bgc_thread(g_heaps[i]))
            {
                return background_collection_e_notimpl;
            }
        }

        // Trigger the background collection. Coalescing is handled under gc_lock inside
        // GarbageCollectGenerationServer: only the first request that observes no background collection
        // running (background_running_p) becomes the kickoff; a concurrent second request observes it
        // running under the lock and returns (coalesces) rather than starting another. Passing
        // backgroundKickoff: true both exempts this candidate kickoff from the background wait and,
        // when it wins, publishes gc_is_background_kickoff for the joined worker. The triggering thread
        // returns as soon as the bgc workers open the concurrent window; the collection then runs
        // asynchronously to completion on the bgc workers, so this does not wait for it.
        GarbageCollectGenerationServer(
            (uint)GCInterfaceOffsets.max_generation, reason, backgroundKickoff: true);

        return background_collection_s_ok;
    }

    // background.cpp gc1 concurrent gate + do_background_gc kickoff (bounded). Runs on the joined
    // worker (heap 0) after the condemned generation has settled to max_generation. Mirrors the
    // native collect.cpp do_concurrent_p gate: the mark array is committed for every heap first, and
    // only if every commit succeeds are the background settings/bookkeeping/count/state published and
    // the dedicated workers woken. Returns false (with no background state, count or settings change)
    // when a commit fails so the caller falls back to a blocking collection.
    private static bool server_background_gc_kickoff()
    {
        // Establish the background range first: commit_mark_array_new_seg commits each region's mark
        // array pages against background_saved_lowest/highest_address, so they must be set before the
        // commit gate below. All heaps share the same USE_REGIONS address range and the single
        // process-wide mark array. Setting these on a fallback path is harmless (nothing reads them
        // while gc_background_running stays 0).
        background_saved_lowest_address = lowest_address;
        background_saved_highest_address = highest_address;

        // native do_concurrent_p gate: commit the mark array for every heap. A partially committed
        // array is safe to leave committed (each segment carries the ma_committed flag and is reused
        // on the next attempt); we simply do not proceed with the background collection.
        for (int i = 0; i < n_heaps; i++)
        {
            if (!commit_mark_array_bgc_init(g_heaps[i]))
            {
                return false;
            }
        }

        // Every commit succeeded: publish the background settings, bookkeeping, count and state.
        settings.background_p = 1;
        settings.concurrent = 1;
        saved_bgc_settings = settings;

        last_background_gc_info_index = 1 - last_background_gc_info_index;
        background_gc_info(last_background_gc_info_index) = default;
        background_gc_info(last_background_gc_info_index).index = settings.gc_index;

        full_gc_counts[gc_type_background]++;

        // Align the background join's participant count with the active heap count before waking the
        // workers. create_bgc_threads_support sizes bgc_t_join to the maximum heap count; when
        // dynamic adaptation runs with fewer active heaps only n_heaps workers were created and
        // participate, so the join must expect exactly n_heaps arrivals. This is a safe point: no
        // background collection is running (start_c_gc below waits on the previous done event).
        bgc_t_join.update_n_threads(n_heaps);

        set_background_state(bgc_state.bgc_initialized);

        // Hand the initial stop-the-world mark off to the dedicated background workers and park this
        // (foreground heap 0) worker until they finish. The EE is already suspended, so the workers
        // only mark; when they are done they revert the concurrent state and signal ee_proceed_event.
        start_c_gc();
        wait_to_proceed();
        return true;
    }
}
#pragma warning restore CS8981

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
