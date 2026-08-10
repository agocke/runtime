// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Per-heap SERVER_GC / MULTIPLE_HEAPS / USE_REGIONS background marking primitives and the initial
// stop-the-world background-mark scan, from the SVR compilation of background.cpp. Translated
// mechanically; the shared marking algorithm mirrors the WKS BackgroundGC.cs (background_promote /
// push_background_mark / background_mark_reference / drain_background_mark_stack), but every piece of
// mark-stack state is per-heap here (native scopes background_mark_stack_array / _tos / bpromoted_bytes
// per heap) and background_promote resolves the object's owning heap through heap_of.
//
// Ownership (background.cpp):
//   * The background mark array (mark_array) is a single process-wide table under USE_REGIONS; the
//     set is atomic (background_mark1 -> mark_array_set_marked, Interlocked.Or), so it stays shared.
//   * The mark stack (background_mark_stack_array / _length / _tos / _overflow) and bpromoted_bytes
//     are PER_HEAP: each background worker marks into the shared array but pushes onto its own stack.
//   * background_saved_lowest_address / background_saved_highest_address are PER_HEAP_ISOLATED in this
//     port (shared) -- the whole heap shares one region address range under USE_REGIONS.
//
// The initial scan (background_mark_phase) is the STW section of native background_mark_phase up to
// the point the concurrent mark would begin: the parallel per-worker root / finalizer / strong+pinned
// / dependent-handle scan into the background mark array, bracketed by the native bgc_t_join stages
// (gc_join_begin_mark_phase, gc_join_restart_ee, gc_join_after_reset, gc_join_null_dead_short_weak).
// The concurrent mark / revisit / overflow-driven mark-list, the software-write-watch reset, and the
// live restart_vm remain gated to the concurrent-mark slice; the reference publication they need is
// recorded (current_c_gc_state = c_gc_state_marking, cm_in_progress) but reverted by the caller.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    // background.cpp gc_heap::background_mark_reference context: the background worker (heap) whose
    // mark stack the go_through_object child callback pushes onto.
    private struct background_mark_context
    {
        public gc_heap* heap;
    }

    // background.cpp background mark-stack allocation (gc_heap::initialize_background_gc mark-stack
    // half). Each server heap gets its own stack; sizing mirrors WKS (max of this heap's mark_list
    // size and MARK_STACK_INITIAL_LENGTH). Returns false on allocation failure.
    public static bool allocate_background_mark_stack(gc_heap* hp)
    {
        nuint stackLength = mark_list_size > gc_rand.MARK_STACK_INITIAL_LENGTH
            ? mark_list_size
            : gc_rand.MARK_STACK_INITIAL_LENGTH;
        if (stackLength > nuint.MaxValue / (nuint)sizeof(byte*))
        {
            return false;
        }

        hp->background_mark_stack_array = (byte**)SyncImports.ManagedGC_AllocZeroed(
            stackLength * (nuint)sizeof(byte*));
        if (hp->background_mark_stack_array is null)
        {
            return false;
        }

        hp->background_mark_stack_array_length = stackLength;
        hp->background_mark_stack_tos = 0;
        hp->background_mark_stack_overflow = 0;
        return true;
    }

    // Free this heap's background mark stack during Cleanup.
    public static void free_background_mark_stack(gc_heap* hp)
    {
        if (hp->background_mark_stack_array is not null)
        {
            SyncImports.ManagedGC_Free(hp->background_mark_stack_array);
            hp->background_mark_stack_array = null;
        }

        hp->background_mark_stack_array_length = 0;
        hp->background_mark_stack_tos = 0;
        hp->background_mark_stack_overflow = 0;
    }

    // background.cpp gc_heap::commit_mark_array_bgc_init: commit the mark array for every region of
    // every generation so background_mark1 can set bits anywhere in the heap.
    public static bool commit_mark_array_bgc_init(gc_heap* hp)
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

    // background.cpp gc_heap::push_background_mark: push a marked object onto the given worker heap's
    // stack, or set that heap's overflow flag when the stack is full.
    private static void push_background_mark(gc_heap* hp, byte* o)
    {
        nuint index = hp->background_mark_stack_tos;
        if (index < hp->background_mark_stack_array_length)
        {
            hp->background_mark_stack_array[(nint)index] = o;
            hp->background_mark_stack_tos = index + 1;
        }
        else
        {
            hp->background_mark_stack_overflow = 1;
        }
    }

    // background.cpp gc_heap::background_promote (MULTIPLE_HEAPS). The object's owning heap (heap_of)
    // gates the background range check and the interior find_object; the marking worker heap (from
    // the ScanContext thread number) owns the mark stack the object is pushed onto. The mark array
    // itself is shared. The recursive walk of the object's children is deferred to
    // drain_background_mark_stack (matching the WKS iterative structure rather than native's inline
    // background_mark_simple1 recursion).
    public static void background_promote(byte** ppObject, ScanContext* sc, uint flags)
    {
        if (ppObject is null)
        {
            return;
        }

        byte* o = *ppObject;
        if (!is_in_find_object_range(o))
        {
            return;
        }

        gc_heap* hp = heap_of(o);
        if (o < background_saved_lowest_address || o >= background_saved_highest_address)
        {
            return;
        }

        if ((flags & (uint)GCCallFlags.GC_CALL_INTERIOR) != 0)
        {
            o = find_object(o, hp);
            if (o is null)
            {
                return;
            }
        }

        gc_heap* hpt = g_heaps[sc->thread_number];
        if (background_mark1(o) != 0)
        {
            hpt->bpromoted_bytes += size(o);
            if (contain_pointers_or_collectible(o) != 0)
            {
                push_background_mark(hpt, o);
            }
        }
    }

    // background.cpp gc_heap::background_mark_simple1 child callback: mark and enqueue an in-range
    // child while draining. Pushes onto the draining worker heap (passed through the context).
    private static void background_mark_reference(byte** slot, void* context)
    {
        gc_heap* hp = ((background_mark_context*)context)->heap;
        byte* child = (byte*)GCEnv.VolatileLoad((void**)slot);
        if (child >= background_saved_lowest_address &&
            child < background_saved_highest_address &&
            background_mark1(child) != 0)
        {
            hp->bpromoted_bytes += size(child);
            if (contain_pointers_or_collectible(child) != 0)
            {
                push_background_mark(hp, child);
            }
        }
    }

    // background.cpp gc_heap::background_drain_mark_stack: pop this heap's stack, walking each object's
    // children through background_mark_reference until the stack (and any overflow) is drained.
    public static void drain_background_mark_stack(gc_heap* hp)
    {
        nuint markedObjects = 0;
        bool scanAll = hp->background_mark_stack_overflow != 0;
        background_mark_context ctx = new() { heap = hp };

        do
        {
            hp->background_mark_stack_overflow = 0;
            while (hp->background_mark_stack_tos != 0)
            {
                hp->background_mark_stack_tos--;
                byte* o = hp->background_mark_stack_array[(nint)hp->background_mark_stack_tos];
                go_through_object_nostart(
                    method_table(o),
                    o,
                    size(o),
                    &ctx,
                    &background_mark_reference);
                markedObjects++;
            }

            if (scanAll || hp->background_mark_stack_overflow != 0)
            {
                hp->background_mark_stack_overflow = 0;
                scan_marked_objects_for_overflow(hp);
                scanAll = hp->background_mark_stack_overflow != 0;
            }
        }
        while (scanAll ||
            hp->background_mark_stack_overflow != 0 ||
            hp->background_mark_stack_tos != 0);

        GCEvents.GCEventFireBGCDrainMark(markedObjects);
    }

    // background.cpp gc_heap::background_process_mark_overflow_internal: after a mark-stack overflow,
    // re-walk every already-marked object in this heap's regions so its children are re-enqueued.
    private static void scan_marked_objects_for_overflow(gc_heap* hp)
    {
        background_mark_context ctx = new() { heap = hp };
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
                byte* current = heap_segment.heap_segment_mem(segment);
                byte* end = heap_segment.heap_segment_allocated(segment);
                while (current < end)
                {
                    nuint objectSize = size(current);
                    if (background_object_marked(current, clear_p: false) &&
                        contain_pointers_or_collectible(current) != 0)
                    {
                        go_through_object_nostart(
                            method_table(current),
                            current,
                            objectSize,
                            &ctx,
                            &background_mark_reference);
                    }

                    current += objectSize;
                }
            }
        }
    }

    // background.cpp gc_heap::background_mark_phase initial stop-the-world section (MULTIPLE_HEAPS),
    // run on every background worker over its own heap. Scans stack roots, finalizer roots, then --
    // after the state-publication joins -- strong/pinned handles and dependent handles into the
    // background mark array, draining this heap's mark stack between kinds. The concurrent revisit /
    // overflow mark-list phase and the live restart_vm are gated to the concurrent-mark slice, so the
    // published concurrent state is reverted by the caller once every heap has finished.
    public static void background_mark_phase(gc_heap* hp)
    {
        Debug.Assert(settings.concurrent != 0);

        ScanContext sc = default;
        sc.init();
        sc.thread_number = hp->heap_number;
        sc.thread_count = n_heaps;
        sc.promotion = 1;
        sc.concurrent = 1;

        hp->background_mark_stack_tos = 0;
        hp->background_mark_stack_overflow = 0;
        hp->bpromoted_bytes = 0;

        bgc_t_join.join(hp, (int)gc_join_stage.gc_join_begin_mark_phase);
        if (bgc_t_join.joined())
        {
            GCToEEInterface.BeforeGcScanRoots(
                GCInterfaceOffsets.max_generation,
                is_bgc: 1,
                is_concurrent: 1);
            GCEvents.GCEventFireBGCBegin();
            bgc_t_join.restart();
        }

        // Stack roots (stop-the-world: the EE is still suspended here).
        GCScan.GcScanRoots(
            &background_promote,
            GCInterfaceOffsets.max_generation,
            GCInterfaceOffsets.max_generation,
            &sc);
        drain_background_mark_stack(hp);

        // Finalizer roots.
        CFinalize* finalizeQueue = hp->server_finalize_queue;
        if (finalizeQueue is not null)
        {
            finalizeQueue->GcScanRoots(&background_promote, hp->heap_number, null);
            drain_background_mark_stack(hp);
        }

        GCEvents.GCEventFireBGC1stNonConEnd();

        // Strong + pinned handles, then dependent handles. Scanned stop-the-world (before the window
        // opens): this slice defers the concurrent region sweep, so the background marks are discarded
        // and the reclamation is a fresh blocking gc1. Scanning the handle table / dependent handles
        // while mutators concurrently mutate references would need the full concurrent write-barrier /
        // card-revisit machinery that is not part of this slice; performing them here, before the
        // window, keeps the window free of scan/mutator races. The window that follows exists only to
        // let mutators run while current_c_gc_state == marking.
        bgc_t_join.join(hp, (int)gc_join_stage.gc_join_after_reset);
        if (bgc_t_join.joined())
        {
            set_background_state(bgc_state.bgc_mark_handles);
            bgc_t_join.restart();
        }

        GCScan.GcScanHandles(
            &background_promote,
            GCInterfaceOffsets.max_generation,
            GCInterfaceOffsets.max_generation,
            &sc);
        drain_background_mark_stack(hp);

        // Dependent handles: the initial scan, then rescan while any remain unpromoted.
        GCScan.GcDhInitialScan(
            &background_promote,
            GCInterfaceOffsets.max_generation,
            GCInterfaceOffsets.max_generation,
            &sc);
        drain_background_mark_stack(hp);
        while (GCScan.GcDhUnpromotedHandlesExist(&sc))
        {
            bool promoted = GCScan.GcDhReScan(&sc);
            drain_background_mark_stack(hp);
            if (!promoted)
            {
                break;
            }
        }

        GCEvents.GCEventFireBGC1stConEnd();

        // Balance BeforeGcScanRoots with AfterGcScanRoots (the runtime brackets a root scan pair).
        // Still stop-the-world.
        bgc_t_join.join(hp, (int)gc_join_stage.gc_join_null_dead_short_weak);
        if (bgc_t_join.joined())
        {
            GCToEEInterface.AfterGcScanRoots(
                GCInterfaceOffsets.max_generation,
                GCInterfaceOffsets.max_generation,
                &sc);
            bgc_t_join.restart();
        }

        // gc_join_restart_ee: marking is complete; enter the concurrent-mark state and open the
        // window. The joined worker resets and publishes software write watch (the EE is still
        // suspended here, so EnableForGCHeap's suspended-runtime precondition holds), sets
        // current_c_gc_state = marking and gc_background_running, then restart_vm signals the parked
        // foreground triggering worker (ee_proceed_event), which performs the actual RestartEE
        // (gc_thread_function) that starts the mutators. Mutators then run and allocate while
        // current_c_gc_state == marking; every reference store they make dirties the software
        // write-watch table, which the final revisit (in the re-suspend) consumes to complete the mark
        // array. The write-watch table is initialized over the whole reserved range in make_card_table
        // and grown with the heap, so it covers regions committed during this window.
        bgc_t_join.join(hp, (int)gc_join_stage.gc_join_restart_ee);
        if (bgc_t_join.joined())
        {
            set_background_state(bgc_state.bgc_reset_ww);
            for (int i = 0; i < n_heaps; i++)
            {
                reset_software_write_watch(g_heaps[i]);
            }
            if (SoftwareWriteWatch.GetTable() is not null)
            {
                SoftwareWriteWatch.EnableForGCHeap();
            }
            current_c_gc_state = c_gc_state.c_gc_state_marking;
            cm_in_progress = 1;
            gc_background_running = 1;
            restart_vm();
            bgc_t_join.restart();
        }
    }

    // background.cpp gc_heap::clear_mark_array over this heap's committed regions: reset every
    // background mark bit so the next background collection starts from a clean array (this port
    // reclaims through the blocking path this slice, which does not run background_sweep's per-object
    // mark_array_clear_marked, so the array must be cleared explicitly). Mirrors the memset-plus-tail
    // pattern of clear_mark_array / bgc_verify_mark_array_cleared. Each worker clears its own heap.
    public static void clear_bgc_mark_array(gc_heap* hp)
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
                if ((segment->flags & heap_segment.heap_segment_flags_ma_committed) == 0)
                {
                    continue;
                }

                byte* range_beg;
                byte* range_end;
                if (!bgc_mark_array_range(segment, whole_seg_p: true, &range_beg, &range_end))
                {
                    continue;
                }

                nuint markw = card_table_info.mark_word_of(range_beg);
                nuint markw_end = card_table_info.mark_word_of(range_end);
                if (markw_end > markw)
                {
                    NativeMemory.Clear(
                        &mark_array[(nint)markw],
                        (nuint)((markw_end - markw) * (nuint)sizeof(uint)));
                }

                byte* p = card_table_info.mark_bit_address(markw_end * card_table_info.mark_word_width);
                while (p < range_end)
                {
                    if (mark_array_marked(p) != 0)
                    {
                        mark_array_clear_marked_atomic(p);
                    }

                    p += card_table_info.mark_bit_pitch;
                }
            }
        }
    }

    // Atomic clear of a single background mark bit (the shared mark array is written by every heap).
    private static void mark_array_clear_marked_atomic(byte* add)
    {
        nuint index = card_table_info.mark_word_of(add);
        uint val = ~(1u << (int)card_table_info.mark_bit_bit_of(add));
        Interlocked.And(&mark_array[(nint)index], val);
    }
}
#pragma warning restore CS8981

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
