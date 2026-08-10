// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Slice A of the SERVER_GC / MULTIPLE_HEAPS / USE_REGIONS background collector: the PER_HEAP and
// PER_HEAP_ISOLATED background-GC thread/join/event state and lifecycle from the SVR compilation of
// background.cpp (create_bgc_threads_support / create_bgc_thread / prepare_bgc_thread /
// bgc_thread_stub / bgc_thread_function) and gcpriv.h field ownership.
//
// Only initialization and cleanup are wired: create_bgc_threads_support runs from the server init
// path (behind the concurrent-GC config) and destroy_background_gc runs from Cleanup. No collection
// is routed to this path yet -- current_c_gc_state stays c_gc_state_free and gc_background_running
// stays 0 (both live in GCRegionsSegments.cs / ManagedServerGCBackgroundState.cs), the worker idle
// loop is never entered, and no bgc worker thread is ever created (prepare_bgc_thread, which spawns
// them lazily on the first background collection in native, is not called). The concurrent
// mark/revisit/sweep body and the join stages it drives are deferred to later slices.
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
    // bgc_thread_stub over this heap. Not called in this slice.
    private static bool create_bgc_thread(gc_heap* gh)
    {
        Debug.Assert(background_gc_done_event.IsValid());

        fixed (byte* name = ".NET BGC\0"u8)
        {
            gh->bgc_thread_running = ManagedGC_CreateServerThread(&bgc_thread_stub, gh, name);
        }

        return gh->bgc_thread_running != 0;
    }

    // background.cpp gc_heap::prepare_bgc_thread: lazily create this heap's background worker under
    // its timeout critical section. Native additionally distinguishes a live Thread* handle from the
    // running flag; this port tracks only bgc_thread_running (the server, like its foreground
    // workers, does not retain a managed Thread* handle), so the two native checks collapse into one.
    // Not called in this slice.
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

    // background.cpp gc_heap::bgc_thread_function: the per-heap background worker idle loop. It parks
    // on the shared bgc_start_event; when woken with settings.concurrent set it would run the
    // concurrent collection, and on a wait timeout with keep_bgc_threads_p clear it retires the
    // worker. The concurrent collection body (do_background_gc / background_mark_phase /
    // background_sweep and the bgc_t_join coordination) is deferred to later slices, and because no
    // worker is created and bgc_start_event is never set in this slice, the loop is never entered.
    private static void bgc_thread_function(gc_heap* hp)
    {
        Debug.Assert(background_gc_done_event.IsValid());
        Debug.Assert(bgc_start_event.IsValid());

        while (true)
        {
            enable_preemptive();

            uint result = bgc_start_event.Wait(GCEnv.INFINITE, alertable: false);

            if (result == GCEnv.WAIT_TIMEOUT)
            {
                bool doExit = false;
                hp->bgc_threads_timeout_cs.Enter();
                if (keep_bgc_threads_p == 0)
                {
                    hp->bgc_thread_running = 0;
                    doExit = true;
                }
                hp->bgc_threads_timeout_cs.Leave();

                if (doExit)
                {
                    break;
                }

                continue;
            }

            // Signalled with no concurrent work to do -> the worker exits (native break).
            if (settings.concurrent == 0)
            {
                break;
            }

            // Deferred: run the concurrent background collection over this heap and rejoin the other
            // workers through bgc_t_join. Landing this is the subject of the following slices.
            break;
        }
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
            return heap->bgc_threads_timeout_cs.Initialize();
        }

        return true;
    }

    // Tear down this heap's background worker timeout critical section during Cleanup. Guarded on the
    // critical section's own initialized state (not the concurrent-GC config) so a heap whose
    // Initialize failed, or a heap allocated after such a failure, is never Destroy()d on
    // uninitialized storage.
    public static void destroy_background_gc_per_heap(gc_heap* heap)
    {
        if (heap->bgc_threads_timeout_cs.IsInitialized)
        {
            heap->bgc_threads_timeout_cs.Destroy();
        }
    }
}
#pragma warning restore CS8981

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
