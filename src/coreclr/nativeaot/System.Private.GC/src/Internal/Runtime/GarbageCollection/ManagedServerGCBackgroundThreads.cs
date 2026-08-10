// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The SERVER_GC / MULTIPLE_HEAPS / USE_REGIONS background collector's PER_HEAP and
// PER_HEAP_ISOLATED thread/join/event state and lifecycle from the SVR compilation of background.cpp
// (create_bgc_threads_support / create_bgc_thread / prepare_bgc_thread / bgc_thread_stub /
// bgc_thread_function) and gcpriv.h field ownership.
//
// create_bgc_threads_support runs from the server init path (behind the concurrent-GC config) and
// destroy_background_gc runs from Cleanup. The dedicated background workers are created lazily on the
// first non-blocking gen2 request (prepare_bgc_thread, on the triggering thread) and run the initial
// stop-the-world mark (bgc_thread_function -> background_mark_phase in ManagedServerGCBackgroundMark.cs),
// coordinated by the triggering worker through start_c_gc / wait_to_proceed. The concurrent
// mark/revisit/sweep body and the live restart_vm remain gated to the concurrent-mark slice.
//
// gcpriv.h scoping is preserved:
//   * bgc_start_event / background_gc_done_event / ee_proceed_event / bgc_threads_sync_event,
//     do_ephemeral_gc_p / do_concurrent_p / cm_in_progress / dont_restart_ee_p,
//     keep_bgc_threads_p / total_bgc_threads and bgc_t_join are PER_HEAP_ISOLATED and stay static.
//   * bgc_thread_running and bgc_threads_timeout_cs are PER_HEAP_FIELD and are instance-owned so
//     each server heap owns its own background worker state.
// current_bgc_state (GCRegionsSegments.cs) is PER_HEAP_ISOLATED in this port and is written through
// set_background_state below.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

using System.Diagnostics;
using System.Threading;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    // gcpriv.h PER_HEAP_ISOLATED_FIELD_SINGLE_GC background events. create_bgc_threads_support
    // creates them (native does so on heap 0); every heap's background worker and the foreground
    // triggering thread observe the same shared events.
    public static GCEvent bgc_start_event;
    public static GCEvent background_gc_done_event;
    public static GCEvent ee_proceed_event;
    public static GCEvent bgc_threads_sync_event;

    // gcpriv.h PER_HEAP_ISOLATED_FIELD_SINGLE_GC background coordination flags.
    public static int do_ephemeral_gc_p;
    public static int do_concurrent_p;
    public static uint cm_in_progress;
    public static int dont_restart_ee_p;

    // gcpriv.h PER_HEAP_ISOLATED_FIELD_MAINTAINED background-thread bookkeeping.
    public static int keep_bgc_threads_p;
    public static int total_bgc_threads;

    // gcinternal.h t_join bgc_t_join: the background-GC join, a single shared instance
    // (PER_HEAP_ISOLATED) parallel to the foreground gc_t_join. It uses join_flavor_bgc and the same
    // 41-stage gc_join_stage enum; later background phases synchronize the per-heap workers on it.
    public static t_join bgc_t_join;

    // background.cpp set_background_state transition history (diagnostics-only rolling record).
    public static ulong background_state_transitions;

    // background.cpp gc_heap::set_background_state. current_bgc_state is the PER_HEAP_ISOLATED phase
    // indicator declared in GCRegionsSegments.cs; the rolling transition byte-log mirrors native's
    // background_state_transitions diagnostic.
    private static void set_background_state(bgc_state new_state)
    {
        background_state_transitions = (background_state_transitions << 8) | (byte)new_state;
        current_bgc_state = new_state;
    }

    // background.cpp gc_heap::create_bgc_threads_support: create the shared background events and the
    // background join. Called once during server init. Mirrors the native ordering and initial
    // states (background_gc_done_event manual/signaled, bgc_threads_sync_event manual/reset,
    // ee_proceed_event auto/reset, bgc_start_event manual/reset).
    //
    // On failure this does NOT close the events it managed to create: cleanup is single-owned by
    // destroy_background_gc (reached through ManagedServerGC.Initialize -> Cleanup on failure, and at
    // shutdown on success). GCEvent.CloseEvent leaves m_impl non-null, so IsValid keeps reporting true
    // afterwards and a second close would be a double-free; letting only destroy_background_gc close
    // each event (guarded by IsValid, which is false for the zero-initialized/uncreated ones) keeps
    // every event and every bgc_t_join join event closed exactly once.
    public static bool create_bgc_threads_support(int number_of_heaps)
    {
        if (!background_gc_done_event.CreateManualEventNoThrow(initialState: true))
        {
            return false;
        }
        if (!bgc_threads_sync_event.CreateManualEventNoThrow(initialState: false))
        {
            return false;
        }
        if (!ee_proceed_event.CreateAutoEventNoThrow(initialState: false))
        {
            return false;
        }
        if (!bgc_start_event.CreateManualEventNoThrow(initialState: false))
        {
            return false;
        }
        if (!bgc_t_join.init(number_of_heaps, gc_join_flavor.join_flavor_bgc))
        {
            return false;
        }

        return true;
    }

    // background.cpp gc_heap::create_bgc_thread: spawn this heap's background worker as a GC-special
    // server thread (the same non-suspendable thread kind used for the foreground workers) running
    // bgc_thread_stub over this heap.
    private static bool create_bgc_thread(gc_heap* gh)
    {
        Debug.Assert(background_gc_done_event.IsValid());

        fixed (byte* name = ".NET BGC\0"u8)
        {
            gh->bgc_thread_running = ManagedGC_CreateServerThread(&bgc_thread_stub, gh, name);
        }

        if (gh->bgc_thread_running != 0)
        {
            // Count the background worker into the same pool as the foreground workers so Cleanup's
            // join (server_gc_threads_exited >= server_gc_threads_created) waits for it to exit.
            Interlocked.Increment(ref server_gc_threads_created);
            Interlocked.Increment(ref total_bgc_threads);
            return true;
        }

        return false;
    }

    // background.cpp gc_heap::prepare_bgc_thread: lazily create this heap's background worker under
    // its timeout critical section. Native additionally distinguishes a live Thread* handle from the
    // running flag; this port tracks only bgc_thread_running (the server, like its foreground
    // workers, does not retain a managed Thread* handle), so the two native checks collapse into one.
    // Called on the triggering thread on the first non-blocking gen2 request.
    private static bool prepare_bgc_thread(gc_heap* gh)
    {
        bool success = false;
        bool threadCreated = false;

        gh->bgc_threads_timeout_cs.Enter();
        if (gh->bgc_thread_running == 0)
        {
            if (create_bgc_thread(gh))
            {
                success = true;
                threadCreated = true;
            }
        }
        else
        {
            success = true;
        }
        gh->bgc_threads_timeout_cs.Leave();

        if (threadCreated)
        {
            GCEvents.GCEventFireGCCreateConcurrentThread_V1();
        }

        return success;
    }

    // background.cpp gc_heap::bgc_thread_stub: the server background worker entry. The argument is the
    // owning heap, whose bgc_thread_function idle loop it runs.
    private static void bgc_thread_stub(void* argument)
    {
        gc_heap* heap = (gc_heap*)argument;
        bgc_thread_function(heap);
    }

    // background.cpp gc_heap::bgc_thread_function: the per-heap background worker loop. Each worker
    // parks on the shared bgc_start_event; when woken for a background collection (settings.concurrent
    // set and not shutting down) it runs the initial stop-the-world mark over its own heap and rejoins
    // the other workers through bgc_t_join. Like the foreground workers it holds the managed-GC
    // critical region (DoNotTriggerGc) for its whole lifetime so it is never hijacked while running
    // collector code with the EE suspended. It exits when Cleanup sets server_gc_shutdown and wakes it.
    //
    // This slice runs only the initial mark on the background workers; the concurrent
    // mark/revisit/background sweep and the live restart_vm remain gated (the reclamation still
    // completes through the foreground blocking path once the workers hand control back).
    private static void bgc_thread_function(gc_heap* hp)
    {
        Debug.Assert(background_gc_done_event.IsValid());
        Debug.Assert(bgc_start_event.IsValid());

        heap_select.init_cpu_mapping(hp->heap_number);

        // Uncounted DoNotTriggerGc for the worker lifetime (see gc_thread_function): the background
        // worker must not contribute to g_managedGCCriticalRegionCount or the final-mark re-suspend's
        // ManagedGC_PrepareForSuspension would deadlock on the parked worker pool.
        ManagedGC_EnterServerGCThread();

        while (Volatile.Read(ref server_gc_shutdown) == 0)
        {
            bgc_start_event.Wait(GCEnv.INFINITE, alertable: false);

            if (Volatile.Read(ref server_gc_shutdown) != 0)
            {
                break;
            }

            // Woken with no concurrent work to do -> loop back and wait again.
            if (settings.concurrent == 0)
            {
                continue;
            }

            // Initial stop-the-world background mark over this heap. All marking (roots, finalizer,
            // handles, dependent handles) completes stop-the-world; at gc_join_restart_ee the joined
            // worker enters the concurrent-mark state (current_c_gc_state == marking) + gc_background_running
            // and restart_vm signals the foreground worker to RestartEE, opening the window in which
            // mutators run (and park in the background allocation wait) until heap 0 re-suspends.
            background_mark_phase(hp);

            // Re-suspend for the final mark / reclamation (background.cpp gc_join_suspend_ee). Only
            // heap 0 drives bgc_suspend_EE (under gc_lock); the others wait on bgc_threads_sync_event.
            bgc_t_join.join(hp, (int)gc_join_stage.gc_join_suspend_ee);
            if (bgc_t_join.joined())
            {
                bgc_threads_sync_event.Reset();
                bgc_t_join.restart();
            }

            if (hp->heap_number == 0)
            {
                // Do not re-suspend until the foreground worker has actually restarted the EE
                // (avoids a double-suspend), then hold the window so mutators make observable progress
                // while current_c_gc_state == c_gc_state_marking.
                while (Volatile.Read(ref bgc_ee_restarted) == 0)
                {
                    GCToOSInterface.YieldThread(0);
                }
                GCToOSInterface.Sleep(bgc_window_hold_ms);

                enter_gc_lock();
                bgc_suspend_EE();

                // Publish the end of the concurrent window before releasing the other workers into
                // the blocking reclamation (settings.concurrent must be 0 for gc1).
                current_c_gc_state = c_gc_state.c_gc_state_free;
                cm_in_progress = 0;
                set_background_state(bgc_state.bgc_final_marking);
                settings.concurrent = 0;
                settings.background_p = 0;

                bgc_threads_sync_event.Set();
            }
            else
            {
                bgc_threads_sync_event.Wait(GCEnv.INFINITE, alertable: false);
            }

            // Fallback reclamation on the bgc worker pool (EE re-suspended). Discard the background
            // marks and run the proven blocking mark/plan/sweep, which advances the collection count
            // and reclaims correctly regardless of the concurrent mark. The concurrent region sweep
            // that would consume the background marks directly is deferred.
            clear_bgc_mark_array(hp);
            hp->alloc_contexts_used = 0;
            fix_allocation_contexts(hp, for_gc_p: true);
            clear_gen0_bricks(hp);
            init_records(hp);
            gc1(hp);

            // Synchronize all workers, then publish the shared end-of-collection bookkeeping on the
            // joined worker. The EE restart and gc_lock release, however, must run on heap 0: the
            // matching bgc_suspend_EE (SuspendEE -> ThreadStore lock) and enter_gc_lock above were done
            // by heap 0, and both the ThreadStore mutex and gc_lock require the same thread to release
            // them. Running RestartEE on the arbitrary joined worker leaves the ThreadStore lock owned
            // by heap 0 and deadlocks the next collection's SuspendEE.
            bgc_t_join.join(hp, (int)gc_join_stage.gc_join_done);
            if (bgc_t_join.joined())
            {
                // Reset the manual bgc_start_event so the workers park again next iteration.
                bgc_start_event.Reset();

                do_post_gc(hp);
                set_background_state(bgc_state.bgc_not_in_process);
                gc_background_running = 0;
                Volatile.Write(ref bgc_ee_restarted, 0);
                gc_started = 0;
                bgc_t_join.restart();
            }

            if (hp->heap_number == 0)
            {
                GCToEEInterface.RestartEE(1);
                leave_gc_lock();

                // Queue any finalizable objects the fallback gc1 discovered to the finalizer thread,
                // matching WKS complete_background_gc / native. The foreground triggering worker's
                // EnableFinalization in GarbageCollectGenerationServer is gated on settings.concurrent
                // == 0, so for a background collection it passed 0 (the window was still concurrent when
                // that thread returned) and did not queue these; this heap-0 completion is the single
                // place that does it. settings.concurrent was reset to 0 during the re-suspend above.
                GCToEEInterface.EnableFinalization(
                    settings.found_finalizers != 0 ? (byte)1 : (byte)0);

                background_gc_done_event.Set();
                Interlocked.Increment(ref s_bgc_completed_count);
            }
        }

        ManagedGC_ExitServerGCThread();
        Interlocked.Increment(ref server_gc_threads_exited);
    }

    // background.cpp gc_heap::start_c_gc: wake the background workers for a new background collection.
    // Waits for the previous collection's done event (created signaled), resets it, then sets
    // bgc_start_event to release every parked background worker.
    private static void start_c_gc()
    {
        Debug.Assert(background_gc_done_event.IsValid());
        Debug.Assert(bgc_start_event.IsValid());

        background_gc_done_event.Wait(GCEnv.INFINITE, alertable: false);
        background_gc_done_event.Reset();
        bgc_start_event.Set();
    }

    // background.cpp gc_heap::wait_to_proceed: the foreground triggering worker parks here until the
    // background workers finish the initial mark and signal ee_proceed_event.
    private static void wait_to_proceed()
    {
        Debug.Assert(ee_proceed_event.IsValid());
        ee_proceed_event.Wait(GCEnv.INFINITE, alertable: false);
    }

    // gc.cpp gc_heap::restart_vm: signal the parked foreground triggering worker to proceed (it
    // performs the actual RestartEE that opens the concurrent window). Does not itself restart the EE.
    private static void restart_vm()
    {
        Debug.Assert(ee_proceed_event.IsValid());
        ee_proceed_event.Set();
    }

    // background.cpp gc_heap::initialize_background_gc (support half). Slice A creates the background
    // support (events + join) when concurrent GC is configured so the infrastructure is live and
    // testable, without routing any collection to it. This is gated on the concurrent-GC config
    // directly rather than gc_can_use_concurrent, which the server intentionally keeps false so it
    // does not perturb condemn/pause decisions until background collections are actually routed.
    public static bool initialize_background_gc(int heapCount)
    {
        if (GCConfig.GetConcurrentGC() == 0)
        {
            return true;
        }

        return create_bgc_threads_support(heapCount);
    }

    // background.cpp gc_heap::destroy_background_gc (support half). No background worker is running in
    // this slice, so tearing down the shared events and the join is sufficient; the worker
    // shutdown/timeout handshake lives in bgc_thread_function above and engages once workers are
    // created by a routed background collection.
    public static void destroy_background_gc()
    {
        if (bgc_start_event.IsValid())
        {
            bgc_start_event.CloseEvent();
        }
        if (ee_proceed_event.IsValid())
        {
            ee_proceed_event.CloseEvent();
        }
        if (bgc_threads_sync_event.IsValid())
        {
            bgc_threads_sync_event.CloseEvent();
        }
        if (background_gc_done_event.IsValid())
        {
            background_gc_done_event.CloseEvent();
        }

        bgc_t_join.destroy();
    }

    // gcpriv.h PER_HEAP_FIELD_INIT_ONLY bgc_threads_timeout_cs: initialize this heap's background
    // worker timeout critical section at heap creation when concurrent GC is configured. bgc_state is
    // reset here as well so a heap starts out of the background collection. Returns false if the
    // critical section could not be allocated, matching native's create_bgc_thread_support failure
    // path (a heap whose background support could not be set up fails GC initialization). Not entered
    // in this slice.
    public static bool initialize_background_gc_per_heap(gc_heap* heap)
    {
        heap->bgc_thread_running = 0;
        if (GCConfig.GetConcurrentGC() != 0)
        {
            if (!heap->bgc_threads_timeout_cs.Initialize())
            {
                return false;
            }

            // Allocate this heap's background mark stack once (the initial-mark scan reuses it every
            // background collection). Failure fails GC initialization, matching native's
            // create_bgc_thread_support treating the background support as required.
            return allocate_background_mark_stack(heap);
        }

        return true;
    }

    // Tear down this heap's background worker timeout critical section during Cleanup. Guarded on the
    // critical section's own initialized state (not the concurrent-GC config) so a heap whose
    // Initialize failed, or a heap allocated after such a failure, is never Destroy()d on
    // uninitialized storage.
    public static void destroy_background_gc_per_heap(gc_heap* heap)
    {
        free_background_mark_stack(heap);
        if (heap->bgc_threads_timeout_cs.IsInitialized)
        {
            heap->bgc_threads_timeout_cs.Destroy();
        }
    }
}
#pragma warning restore CS8981

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
