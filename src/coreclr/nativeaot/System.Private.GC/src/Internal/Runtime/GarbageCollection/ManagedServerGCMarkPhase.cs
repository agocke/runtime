// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// First executable server mark slice, translated from the SVR-namespace compilation of
// mark_phase.cpp (and the GCHeap::Promote bridge in interface.cpp) for the active x64 Linux
// SERVER_GC -> MULTIPLE_HEAPS -> DYNAMIC_HEAP_COUNT -> USE_REGIONS feature chain. It provides the
// per-heap mark engine that the future parallel mark driver will run on every server GC worker:
//
//   * per-heap mark storage initialization/cleanup (make_mark_stack, initialize_mark_stack,
//     reset_mark_stack/reset_pinned_queue, the shared g_mark_list backing plus each heap's
//     setup_mark_state_for_collection cursors, and free_server_mark_storage),
//   * the exact/interior/pinned promotion callback (promote / GCHeap::Promote), pin_object and
//     mark_object,
//   * the mark queue push/drain/overflow behaviour (mark_object_simple, mark_object_simple1,
//     drain_mark_queue, gc_mark/gc_mark1, m_boundary, add_to_promoted_bytes,
//     record_mark_stack_overflow, process_mark_overflow, process_mark_overflow_internal), and
//   * the per-heap root/finalizer/strong+pinned handle scan entry points (mark_phase_scan_roots).
//
// The gcpriv.h PER_HEAP_FIELD_SINGLE_GC / PER_HEAP_FIELD_MAINTAINED / PER_HEAP_FIELD_DIAG_ONLY mark
// state (mark_queue, mark_stack_tos/bos, oldest_pinned_plug, num_pinned_objects, mark_list cursors,
// mark_stack_array(_length), min/max_overflow_address) is instance-owned in the MULTIPLE_HEAPS
// build so each server heap marks its own portion; gc_low/gc_high stay PER_HEAP_ISOLATED (static)
// as in gcpriv.h under USE_REGIONS. The MULTIPLE_HEAPS m_boundary macro records only the mark list
// (no per-heap slow/shigh range), and m_boundary_fullgc is a no-op, exactly as in gcinternal.h.
// process_mark_overflow_internal walks every heap's generations (g_heaps[(heap_number+hi)%n_heaps]).
// No collection is routed by this slice; sort_mark_list / merge_mark_lists, mark_steal,
// equalize_promoted_bytes, and the full mark_phase join sequence remain deferred. The scalar
// TRACE_GC, SNOOP_STATS, HEAP_ANALYZE, COLLECTIBLE_CLASS, FEATURE_STRUCTALIGN, STRESS_PINNING, and
// GC_CONFIG_DRIVEN branches are excluded exactly as for the active configuration.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection;

// mark_queue_t is declared (fields only) in ManagedServerGC.cs; the marking transitions live here
// so the server build reuses the same 16-slot deferred-marking queue as WKS mark_phase.cpp.
internal unsafe partial struct mark_queue_t
{
    private const int slot_count = 16;

    public static void initialize(mark_queue_t* queue)
    {
        *queue = default;
    }

    public byte* queue_mark(byte* o)
    {
        // The native Prefetch(o) is a performance hint with no cross-platform managed equivalent;
        // the queue's storage and marking transition are unchanged.
        nuint slot_index = curr_slot_index;
        byte* old_o = (byte*)slot_table[(int)slot_index];
        slot_table[(int)slot_index] = (nuint)o;

        curr_slot_index = (slot_index + 1) % slot_count;
        if (old_o is null)
        {
            return null;
        }

        CObjectHeader* header = (CObjectHeader*)old_o;
        if (header->IsMarked() != 0)
        {
            return null;
        }

        header->SetMarked();
        return old_o;
    }

    public byte* queue_mark(byte* o, int condemned_gen)
    {
        if (!gc_heap.is_in_heap_range(o))
        {
            return null;
        }

        if (condemned_gen != GCInterfaceOffsets.max_generation &&
            gc_heap.get_region_gen_num(o) > condemned_gen)
        {
            return null;
        }

        return queue_mark(o);
    }

    public byte* get_next_marked()
    {
        nuint slot_index = curr_slot_index;
        nuint empty_slot_count = 0;
        while (empty_slot_count < slot_count)
        {
            byte* o = (byte*)slot_table[(int)slot_index];
            slot_table[(int)slot_index] = 0;
            slot_index = (slot_index + 1) % slot_count;
            if (o is not null)
            {
                CObjectHeader* header = (CObjectHeader*)o;
                if (header->IsMarked() == 0)
                {
                    header->SetMarked();
                    curr_slot_index = slot_index;
                    return o;
                }
            }

            empty_slot_count++;
        }

        return null;
    }

    public void verify_empty()
    {
        for (nuint slot_index = 0; slot_index < slot_count; slot_index++)
        {
            Debug.Assert(slot_table[(int)slot_index] == 0);
        }
    }
}

internal unsafe partial struct gc_heap
{
    public const nuint stolen = 2;
    public const nuint partial = 1;
    public const nuint partial_object = 3;
    public const int partial_size_th = 100;
    // gcpriv.h uses 64 partial refs per batch when MULTIPLE_HEAPS is defined.
    public const int num_partial_refs = 64;

    public static nuint min_pre_pin_obj_size =>
        (nuint)sizeof(gap_reloc_pair) + (nuint)GCInterfaceOffsets.min_obj_size;

    // ----------------------------------------------------------------------------------------
    // Object inspection helpers (gcinternal.h / gcpriv.h leaves).
    // ----------------------------------------------------------------------------------------

    public static MethodTable* method_table(byte* o) => ((CObjectHeader*)o)->GetMethodTable();

    public static int contain_pointers(byte* o) => ((CObjectHeader*)o)->ContainsGCPointers();

    public static int contain_pointers_or_collectible(byte* o) =>
        ((CObjectHeader*)o)->ContainsGCPointersOrCollectible();

    public static bool is_in_heap_range(byte* o) =>
        o >= GCCommon.g_gc_lowest_address && o < GCCommon.g_gc_highest_address;

    // find_object needs the brick table, so this bounds by the region bookkeeping's committed end.
    public static bool is_in_find_object_range(byte* o)
    {
        if (o is null)
        {
            return false;
        }

        if (o >= GCCommon.g_gc_lowest_address && o < GCCommon.g_gc_highest_address)
        {
            Debug.Assert(o < bookkeeping_covered_committed);
            return true;
        }

        return false;
    }

    public static byte* ref_from_slot(byte* r) => (byte*)((nuint)r & ~(stolen | partial));

    public static int stolen_p(byte* r) =>
        (((nuint)r & stolen) != 0 && ((nuint)r & partial) == 0) ? 1 : 0;

    public static int partial_p(byte* r) =>
        (((nuint)r & partial) != 0 && ((nuint)r & stolen) == 0) ? 1 : 0;

    public static int straight_ref_p(byte* r) => stolen_p(r) == 0 && partial_p(r) == 0 ? 1 : 0;

    public static int partial_object_p(byte* r) =>
        ((nuint)r & partial_object) == partial_object ? 1 : 0;

    public static int ref_p(byte* r) => straight_ref_p(r) != 0 || partial_object_p(r) != 0 ? 1 : 0;

    // The seg_mapping_table maps an in-range object to the heap that owns its region.
    public static gc_heap* heap_of(byte* o)
    {
        if (o is null || n_heaps == 0)
        {
            return g_heaps[0];
        }

        gc_heap* hp = heap_segment.heap_segment_heap(region_of(o));
        return hp is null ? g_heaps[0] : hp;
    }

    public static gc_heap* heap_of_gc(byte* o) => heap_of(o);

    // gcpriv.h PER_HEAP find_object / clear_gen0_bricks for the server build: the brick table is
    // global under USE_REGIONS, only the gen0 brick-clear flags are per heap. Used by the interior
    // promotion path of GCHeap::Promote.
    public static void clear_gen0_bricks(gc_heap* hp)
    {
        if (hp->gen0_bricks_cleared == 0)
        {
            hp->gen0_bricks_cleared = 1;
            generation* gen0 = generation_of(
                generation_table_of(hp),
                (int)gc_generation_num.soh_gen0);
            heap_segment* gen0_region = generation.generation_start_segment(gen0);
            while (gen0_region is not null)
            {
                byte* clear_start = heap_segment.heap_segment_mem(gen0_region);
                for (nuint b = brick_of(clear_start);
                     b < brick_of(card_table_info.align_on_brick(heap_segment.heap_segment_allocated(gen0_region)));
                     b++)
                {
                    set_brick(b, -1);
                }

                gen0_region = heap_segment.heap_segment_next(gen0_region);
            }
        }
    }

    public static byte* find_object(byte* interior, gc_heap* hp)
    {
        Debug.Assert(interior is not null);

        if (hp->gen0_bricks_cleared == 0)
        {
            clear_gen0_bricks(hp);
        }

        hp->gen0_must_clear_bricks = FFIND_DECAY;

        int brick_entry = get_brick_entry(brick_of(interior));
        if (brick_entry == 0)
        {
            // This is a pointer to a UOH object.
            if (!try_get_region_segment(interior, small_heap_only: false, out heap_segment* seg))
            {
                return null;
            }

            byte* allocated = heap_segment.heap_segment_allocated(seg);
            byte* o = heap_segment.heap_segment_mem(seg);
            int align_const = get_alignment_constant(heap_segment.heap_segment_read_only_p(seg) != 0);
            while (o < allocated)
            {
                byte* next_o = o + (nint)Align(size(o), align_const);
                Debug.Assert(next_o > o);
                if (o <= interior && interior < next_o)
                {
                    return o;
                }

                o = next_o;
            }

            return null;
        }

        if (!try_get_region_segment(interior, small_heap_only: true, out heap_segment* soh_seg))
        {
            return null;
        }

        return find_first_object(interior, heap_segment.heap_segment_mem(soh_seg));
    }

    public static void go_through_object(
        MethodTable* mt,
        byte* o,
        nuint size,
        void* context,
        delegate*<byte**, void*, void> callback,
        byte* start,
        int start_useful)
    {
        CGCDesc* map = CGCDesc.GetCGCDescFromMT(mt);
        CGCDescSeries* cur = map->GetHighestSeries();
        nint cnt = (nint)map->GetNumSeries();

        if (cnt >= 0)
        {
            CGCDescSeries* last = map->GetLowestSeries();
            do
            {
                byte** parm = (byte**)unchecked((nuint)o + cur->GetSeriesOffset());
                byte** ppstop = (byte**)unchecked((nuint)parm + cur->GetSeriesSize() + size);
                if (start_useful == 0 || (byte*)ppstop > start)
                {
                    if (start_useful != 0 && (byte*)parm < start)
                    {
                        parm = (byte**)start;
                    }

                    while (parm < ppstop)
                    {
                        callback(parm, context);
                        parm++;
                    }
                }

                cur--;
            }
            while (cur >= last);
        }
        else
        {
            byte** parm = (byte**)unchecked((nuint)o + cur->startoffset);
            if (start_useful != 0 && start > (byte*)parm)
            {
                nuint component_size = mt->RawGetComponentSize();
                parm = (byte**)unchecked(
                    (nuint)parm + (((nuint)start - (nuint)parm) / component_size) * component_size);
            }

            byte* object_end = (byte*)unchecked((nuint)o + size - (nuint)sizeof(nuint));
            while ((byte*)parm < object_end)
            {
                for (nint index = 0; index > cnt; index--)
                {
                    val_serie_item* item = (val_serie_item*)((byte*)cur + (index * sizeof(val_serie_item)));
                    byte** ppstop = (byte**)unchecked(
                        (nuint)parm + ((nuint)item->nptrs * (nuint)sizeof(byte*)));
                    if (start_useful == 0 || (byte*)ppstop > start)
                    {
                        if (start_useful != 0 && (byte*)parm < start)
                        {
                            parm = (byte**)start;
                        }

                        do
                        {
                            callback(parm, context);
                            parm++;
                        }
                        while (parm < ppstop);
                    }

                    parm = (byte**)unchecked((nuint)ppstop + (nuint)item->skip);
                }
            }
        }
    }

    public static void go_through_object_nostart(
        MethodTable* mt,
        byte* o,
        nuint size,
        void* context,
        delegate*<byte**, void*, void> callback)
    {
        if (mt->ContainsGCPointers() == 0)
        {
            return;
        }

        go_through_object(mt, o, size, context, callback, o, start_useful: 0);
    }

    private static int go_through_object_with_stop(
        MethodTable* mt,
        byte* o,
        nuint size,
        void* context,
        delegate*<byte**, void*, int> callback,
        byte* start,
        int start_useful)
    {
        CGCDesc* map = CGCDesc.GetCGCDescFromMT(mt);
        CGCDescSeries* cur = map->GetHighestSeries();
        nint cnt = (nint)map->GetNumSeries();

        if (cnt >= 0)
        {
            CGCDescSeries* last = map->GetLowestSeries();
            do
            {
                byte** parm = (byte**)unchecked((nuint)o + cur->GetSeriesOffset());
                byte** ppstop = (byte**)unchecked((nuint)parm + cur->GetSeriesSize() + size);
                if (start_useful == 0 || (byte*)ppstop > start)
                {
                    if (start_useful != 0 && (byte*)parm < start)
                    {
                        parm = (byte**)start;
                    }

                    while (parm < ppstop)
                    {
                        if (callback(parm, context) == 0)
                        {
                            return 0;
                        }

                        parm++;
                    }
                }

                cur--;
            }
            while (cur >= last);
        }
        else
        {
            byte** parm = (byte**)unchecked((nuint)o + cur->startoffset);
            if (start_useful != 0 && start > (byte*)parm)
            {
                nuint component_size = mt->RawGetComponentSize();
                parm = (byte**)unchecked(
                    (nuint)parm + (((nuint)start - (nuint)parm) / component_size) * component_size);
            }

            byte* object_end = (byte*)unchecked((nuint)o + size - (nuint)sizeof(nuint));
            while ((byte*)parm < object_end)
            {
                for (nint index = 0; index > cnt; index--)
                {
                    val_serie_item* item = (val_serie_item*)((byte*)cur + (index * sizeof(val_serie_item)));
                    byte** ppstop = (byte**)unchecked(
                        (nuint)parm + ((nuint)item->nptrs * (nuint)sizeof(byte*)));
                    if (start_useful == 0 || (byte*)ppstop > start)
                    {
                        if (start_useful != 0 && (byte*)parm < start)
                        {
                            parm = (byte**)start;
                        }

                        do
                        {
                            if (callback(parm, context) == 0)
                            {
                                return 0;
                            }

                            parm++;
                        }
                        while (parm < ppstop);
                    }

                    parm = (byte**)unchecked((nuint)ppstop + (nuint)item->skip);
                }
            }
        }

        return 1;
    }

    // ----------------------------------------------------------------------------------------
    // Per-heap promoted-byte accounting and the condemned-range predicates.
    // ----------------------------------------------------------------------------------------

    // The MULTIPLE_HEAPS m_boundary macro records only the mark list (slow/shigh are WKS-only).
    public static void m_boundary(gc_heap* heap, byte* o)
    {
        if (heap->mark_list_index <= heap->mark_list_end)
        {
            *heap->mark_list_index = o;
            heap->mark_list_index++;
        }
    }

    // m_boundary_fullgc is empty under MULTIPLE_HEAPS; full collections do not use the mark list.
    public static void m_boundary_fullgc(gc_heap* heap, byte* o)
    {
        _ = heap;
        _ = o;
    }

    public static void add_to_promoted_bytes(gc_heap* heap, byte* obj, int thread)
    {
        add_to_promoted_bytes(heap, obj, size(obj), thread);
    }

    public static void add_to_promoted_bytes(gc_heap* heap, byte* obj, nuint obj_size, int thread)
    {
        Debug.Assert(thread == heap->heap_number);

        if (heap->survived_per_region is not null)
        {
            nuint region_index = get_basic_region_index_for_address(obj);
            heap->survived_per_region[(nint)region_index] =
                unchecked(heap->survived_per_region[(nint)region_index] + obj_size);
        }
    }

    // Server per-heap promoted-byte total (WKS keeps this !MULTIPLE_HEAPS static form in GCPriv.cs).
    public static nuint get_promoted_bytes(gc_heap* heap)
    {
        if (heap->survived_per_region is null)
        {
            return 0;
        }

        nuint promoted = 0;
        for (nuint i = 0; i < region_count; i++)
        {
            if (heap->survived_per_region[(nint)i] > 0)
            {
                promoted = unchecked(promoted + heap->survived_per_region[(nint)i]);
            }
        }

        return promoted;
    }

    public static void record_mark_stack_overflow(gc_heap* heap, byte* o)
    {
        if (o < heap->min_overflow_address)
        {
            heap->min_overflow_address = o;
        }

        if (o > heap->max_overflow_address)
        {
            heap->max_overflow_address = o;
        }
    }

    // ETW::GC_ROOT_KIND values (gcenv.base.h), passed straight through to the GCMarkWithType
    // event so the tail of every scan reports how many bytes that root kind promoted.
    private const int GC_ROOT_STACK = 0;
    private const int GC_ROOT_FQ = 1;
    private const int GC_ROOT_HANDLES = 2;
    private const int GC_ROOT_OLDER = 3;
    private const int GC_ROOT_SIZEDREF = 4;
    private const int GC_ROOT_DH_HANDLES = 6;
    private const int GC_ROOT_NEW_FQ = 7;

    // fire_mark_event (mark_phase.cpp): report the delta of this heap's promoted bytes since the
    // previous fire for a given root kind. Allocation-free; a no-op when the event is disabled.
    public static void fire_mark_event(gc_heap* heap, int root_type, ref nuint last_promoted_bytes)
    {
        if (!GCEvents.GCEventEnabledGCMarkWithType())
        {
            return;
        }

        nuint current_promoted_bytes = get_promoted_bytes(heap);
        nuint promoted_bytes = unchecked(current_promoted_bytes - last_promoted_bytes);
        GCEvents.GCEventFireGCMarkWithType(
            unchecked((uint)heap->heap_number),
            unchecked((uint)root_type),
            promoted_bytes);
        last_promoted_bytes = current_promoted_bytes;
    }

    // save_current_survived / update_old_card_survived (plan_phase.cpp): snapshot this heap's
    // per-region survivor counts before the cross-generation card scan, then fold the delta the
    // card scan added into old_card_survived_per_region afterwards. The _DEBUG dprintf-only walk
    // is excluded exactly as for the active configuration.
    public static void save_current_survived(gc_heap* heap)
    {
        if (heap->survived_per_region is null)
        {
            return;
        }

        nuint region_info_to_copy = region_count * (nuint)sizeof(nuint);
        NativeMemory.Copy(
            heap->survived_per_region,
            heap->old_card_survived_per_region,
            region_info_to_copy);
    }

    public static void update_old_card_survived(gc_heap* heap)
    {
        if (heap->survived_per_region is null)
        {
            return;
        }

        for (nuint region_index = 0; region_index < region_count; region_index++)
        {
            heap->old_card_survived_per_region[(nint)region_index] = unchecked(
                heap->survived_per_region[(nint)region_index] -
                heap->old_card_survived_per_region[(nint)region_index]);
        }
    }

    public static bool is_in_gc_range(byte* o) => (gc_low <= o) && (o < gc_high);

    public static bool is_in_condemned_gc(byte* o)
    {
        Debug.Assert((GCCommon.g_gc_lowest_address <= o) && (o < GCCommon.g_gc_highest_address));

        int condemned_gen = settings.condemned_generation;
        if (condemned_gen < GCInterfaceOffsets.max_generation)
        {
            int gen = get_region_gen_num(o);
            if (gen > condemned_gen)
            {
                return false;
            }
        }

        return true;
    }

    public static int gc_mark1(byte* o)
    {
        int marked = ((CObjectHeader*)o)->IsMarked() == 0 ? 1 : 0;
        ((CObjectHeader*)o)->SetMarked();

#if DEBUG
        if (try_get_region_segment(o, small_heap_only: false, out heap_segment* seg) &&
            o > heap_segment.heap_segment_allocated(seg))
        {
            GCToOSInterface.DebugBreak();
        }
#endif

        return marked;
    }

    public static int gc_mark(byte* o, byte* low, byte* high, int condemned_gen)
    {
        if ((o >= low) && (o < high))
        {
            if (condemned_gen != GCInterfaceOffsets.max_generation && get_region_gen_num(o) > condemned_gen)
            {
                return 0;
            }

            int already_marked = ((CObjectHeader*)o)->IsMarked();
            if (already_marked != 0)
            {
                return 0;
            }

            ((CObjectHeader*)o)->SetMarked();
            return 1;
        }

        return 0;
    }

    // ----------------------------------------------------------------------------------------
    // Per-heap mark storage initialization and cleanup.
    // ----------------------------------------------------------------------------------------

    public static byte** make_mark_list(nuint size)
    {
        if (size > nuint.MaxValue / (nuint)sizeof(byte*))
        {
            return null;
        }

        return (byte**)SyncImports.ManagedGC_AllocZeroed(size * (nuint)sizeof(byte*));
    }

    // Allocate the PER_HEAP_ISOLATED shared mark-list backing (one block partitioned across heaps).
    // sort_mark_list / merge_mark_lists / equalize_mark_lists across heaps remain deferred, so each
    // heap marks only into its own partition and the copy buffer is not yet threaded.
    public static bool initialize_shared_mark_list(int heapCount)
    {
        // For regions the SOH "segment size" is the basic region alignment.
        nuint soh_segment_size = global_region_allocator.get_region_alignment();
        nuint size = soh_segment_size / (2 * 10 * 32);
        if (size < 8192)
        {
            size = 8192;
        }
        else if (size > 100 * 1024)
        {
            size = 100 * 1024;
        }

        mark_list_size = size;
        // DATAS starts with a single active heap; otherwise reserve every heap's partition.
        nuint total = dynamic_adaptation_mode ==
            (int)gc_dynamic_adaptation_mode.dynamic_adaptation_to_application_sizes
                ? size
                : size * (nuint)heapCount;

        g_mark_list_total_size = total;
        g_mark_list = make_mark_list(total);
        if (g_mark_list is null)
        {
            return false;
        }

        g_mark_list_copy = make_mark_list(total);
        return g_mark_list_copy is not null;
    }

    public static void destroy_shared_mark_list()
    {
        if (g_mark_list is not null)
        {
            SyncImports.ManagedGC_Free(g_mark_list);
            g_mark_list = null;
        }

        if (g_mark_list_copy is not null)
        {
            SyncImports.ManagedGC_Free(g_mark_list_copy);
            g_mark_list_copy = null;
        }

        mark_list_size = 0;
        g_mark_list_total_size = 0;

        if (g_mark_list_piece is not null)
        {
            SyncImports.ManagedGC_Free(g_mark_list_piece);
            g_mark_list_piece = null;
        }

        g_mark_list_piece_size = 0;
        g_mark_list_piece_total_size = 0;
    }

    public static void make_mark_stack(gc_heap* heap, mark* arr)
    {
        reset_pinned_queue(heap);
        heap->mark_stack_array = arr;
        heap->mark_stack_array_length = gc_rand.MARK_STACK_INITIAL_LENGTH;
    }

    public static bool initialize_mark_stack(gc_heap* heap)
    {
        if (gc_rand.MARK_STACK_INITIAL_LENGTH > nuint.MaxValue / (nuint)sizeof(mark))
        {
            return false;
        }

        mark* stack = (mark*)SyncImports.ManagedGC_AllocZeroed(
            gc_rand.MARK_STACK_INITIAL_LENGTH * (nuint)sizeof(mark));
        if (stack is null)
        {
            return false;
        }

        make_mark_stack(heap, stack);
        return true;
    }

    public static void reset_pinned_queue(gc_heap* heap)
    {
        heap->mark_stack_tos = 0;
        heap->mark_stack_bos = 0;
    }

    public static void reset_pinned_queue_bos(gc_heap* heap)
    {
        heap->mark_stack_bos = 0;
    }

    public static void reset_mark_stack(gc_heap* heap)
    {
        reset_pinned_queue(heap);
        heap->max_overflow_address = null;
        heap->min_overflow_address = (byte*)nuint.MaxValue;
    }

    // gcpriv.h PER_HEAP_FIELD_SINGLE_GC reset run for one heap at construction and before each GC.
    public static void initialize_mark_phase_state(gc_heap* heap)
    {
        mark_queue_t.initialize(&heap->mark_queue);

        reset_mark_stack(heap);
        heap->oldest_pinned_plug = null;
        heap->num_pinned_objects = 0;
        heap->mark_list = null;
        heap->mark_list_index = null;
        heap->mark_list_end = null;
        heap->survived_per_region = null;
        heap->old_card_survived_per_region = null;
    }

    // Point this heap's mark-list cursors into its partition of the shared backing and reset the
    // overflow range and per-region survivor storage for a collection. sort_mark_list consumes
    // mark_list after marking; that step remains deferred so the copy buffer is untouched here.
    public static bool setup_mark_state_for_collection(gc_heap* heap)
    {
        heap->mark_queue.verify_empty();

        if (g_mark_list is null || n_heaps == 0)
        {
            initialize_mark_phase_state(heap);
            return false;
        }

        mark_list_size = g_mark_list_total_size / (nuint)n_heaps;
        if (mark_list_size == 0)
        {
            initialize_mark_phase_state(heap);
            return false;
        }

        byte** partition = g_mark_list + (nint)((nuint)heap->heap_number * mark_list_size);
        heap->mark_list = partition;
        heap->mark_list_index = partition;
        heap->mark_list_end = settings.condemned_generation < GCInterfaceOffsets.max_generation
            ? partition + (nint)(mark_list_size - 1)
            : partition;

        grow_mark_list_piece();
        if (g_mark_list_piece is not null)
        {
            heap->survived_per_region = (nuint*)&g_mark_list_piece[
                (nuint)heap->heap_number * 2 * g_mark_list_piece_size];
            heap->old_card_survived_per_region =
                heap->survived_per_region + (nint)g_mark_list_piece_size;
            nuint region_info_to_clear = region_count * (nuint)sizeof(nuint);
            NativeMemory.Clear(heap->survived_per_region, region_info_to_clear);
            NativeMemory.Clear(heap->old_card_survived_per_region, region_info_to_clear);
        }
        else
        {
            heap->survived_per_region = null;
            heap->old_card_survived_per_region = null;
            heap->mark_list_end = partition;
        }

        heap->max_overflow_address = null;
        heap->min_overflow_address = (byte*)nuint.MaxValue;
        return true;
    }

    public static void grow_mark_list_piece()
    {
        nuint heap_count = (nuint)n_heaps;
        if (heap_count == 0 ||
            region_count > nuint.MaxValue / (2 * heap_count))
        {
            return;
        }

        nuint required_size = region_count * 2 * heap_count;
        if (g_mark_list_piece_total_size < required_size)
        {
            if (g_mark_list_piece is not null)
            {
                SyncImports.ManagedGC_Free(g_mark_list_piece);
                g_mark_list_piece = null;
            }

            nuint doubled_size = g_mark_list_piece_size > nuint.MaxValue / 2
                ? nuint.MaxValue
                : g_mark_list_piece_size * 2;
            nuint alloc_count = doubled_size > region_count ? doubled_size : region_count;
            if (alloc_count > nuint.MaxValue / (2 * heap_count * (nuint)sizeof(byte**)))
            {
                g_mark_list_piece_size = 0;
                g_mark_list_piece_total_size = 0;
                return;
            }

            g_mark_list_piece = (byte***)SyncImports.ManagedGC_AllocZeroed(
                alloc_count * 2 * heap_count * (nuint)sizeof(byte**));
            g_mark_list_piece_size = g_mark_list_piece is null ? 0 : alloc_count;
            g_mark_list_piece_total_size = g_mark_list_piece_size * 2 * heap_count;
        }

        g_mark_list_piece_size = g_mark_list_piece_total_size / (2 * heap_count);
    }

    public static void free_server_mark_storage(gc_heap* heap)
    {
        heap->survived_per_region = null;
        heap->old_card_survived_per_region = null;

        if (heap->mark_stack_array is not null)
        {
            SyncImports.ManagedGC_Free(heap->mark_stack_array);
            heap->mark_stack_array = null;
        }

        heap->mark_stack_array_length = 0;
        heap->mark_stack_tos = 0;
        heap->mark_stack_bos = 0;
        heap->mark_list = null;
        heap->mark_list_index = null;
        heap->mark_list_end = null;
    }

    // Sum every heap's SOH/UOH generation sizes, used to cap mark-stack growth on overflow.
    public static nuint get_total_heap_size()
    {
        nuint total_heap_size = 0;
        for (int hn = 0; hn < n_heaps; hn++)
        {
            gc_heap* hp = g_heaps[hn];
            for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
            {
                total_heap_size = unchecked(total_heap_size + generation_size(hp, i));
            }
        }

        return total_heap_size;
    }

    // ----------------------------------------------------------------------------------------
    // Mark queue push / drain / overflow.
    // ----------------------------------------------------------------------------------------

    private unsafe struct mark_object_simple1_small_context
    {
        public gc_heap* heap;
        public byte** mark_stack_tos;
        public int full_p;
        public int condemned_gen;
        public int thread;
    }

    private unsafe struct mark_object_simple1_large_context
    {
        public gc_heap* heap;
        public byte** mark_stack_tos;
        public int full_p;
        public int condemned_gen;
        public int thread;
        public int i;
        public byte* ref_to_continue;
    }

    private static void mark_object_simple1_small_callback(byte** ppslot, void* context)
    {
        mark_object_simple1_small_context* mark_context = (mark_object_simple1_small_context*)context;
        gc_heap* heap = mark_context->heap;
        byte* o = heap->mark_queue.queue_mark(*ppslot, mark_context->condemned_gen);
        if (o is not null)
        {
            if (mark_context->full_p != 0)
            {
                m_boundary_fullgc(heap, o);
            }
            else
            {
                m_boundary(heap, o);
            }

            add_to_promoted_bytes(heap, o, mark_context->thread);
            if (contain_pointers_or_collectible(o) != 0)
            {
                *(mark_context->mark_stack_tos++) = o;
            }
        }
    }

    private static int mark_object_simple1_large_callback(byte** ppslot, void* context)
    {
        mark_object_simple1_large_context* mark_context = (mark_object_simple1_large_context*)context;
        gc_heap* heap = mark_context->heap;
        byte* o = heap->mark_queue.queue_mark(*ppslot, mark_context->condemned_gen);
        if (o is not null)
        {
            if (mark_context->full_p != 0)
            {
                m_boundary_fullgc(heap, o);
            }
            else
            {
                m_boundary(heap, o);
            }

            add_to_promoted_bytes(heap, o, mark_context->thread);
            if (contain_pointers_or_collectible(o) != 0)
            {
                *(mark_context->mark_stack_tos++) = o;
                if (--mark_context->i == 0)
                {
                    mark_context->ref_to_continue = (byte*)((nuint)(ppslot + 1) | partial);
                    return 0;
                }
            }
        }

        return 1;
    }

    public static void mark_object_simple1(gc_heap* heap, byte* oo, byte* start)
    {
        byte** mark_stack_tos = (byte**)heap->mark_stack_array;
        byte** mark_stack_limit = (byte**)&heap->mark_stack_array[heap->mark_stack_array_length];
        byte** mark_stack_base = mark_stack_tos;

        int full_p = settings.condemned_generation == GCInterfaceOffsets.max_generation ? 1 : 0;
        int condemned_gen = settings.condemned_generation;
        int thread = heap->heap_number;
        Debug.Assert((start >= oo) && (start < oo + size(oo)));

        *mark_stack_tos = oo;

        while (true)
        {
            if (oo is not null && (nuint)oo != 4)
            {
                nuint s = 0;
                if (stolen_p(oo) != 0)
                {
                    mark_stack_tos--;
                    goto next_level;
                }
                else if (partial_p(oo) == 0 && ((s = size(oo)) < (partial_size_th * (nuint)sizeof(byte*))))
                {
                    int overflow_p = 0;

                    if (mark_stack_tos + (nint)(s / (nuint)sizeof(byte*)) >= (mark_stack_limit - 1))
                    {
                        nuint num_components = method_table(oo)->HasComponentSize() != 0
                            ? CObjectHeader.GetNumComponents((CObjectHeader*)oo)
                            : 0;

                        if (mark_stack_tos + (nint)CGCDesc.GetNumPointers(method_table(oo), s, num_components) >=
                            (mark_stack_limit - 1))
                        {
                            overflow_p = 1;
                        }
                    }

                    if (overflow_p == 0)
                    {
                        mark_object_simple1_small_context context = new()
                        {
                            heap = heap,
                            mark_stack_tos = mark_stack_tos,
                            full_p = full_p,
                            condemned_gen = condemned_gen,
                            thread = thread,
                        };

                        go_through_object_nostart(method_table(oo), oo, s, &context, &mark_object_simple1_small_callback);
                        mark_stack_tos = context.mark_stack_tos;
                    }
                    else
                    {
                        record_mark_stack_overflow(heap, oo);
                    }
                }
                else
                {
                    if (partial_p(oo) != 0)
                    {
                        start = ref_from_slot(oo);
                        oo = ref_from_slot(*(--mark_stack_tos));
                        Debug.Assert((oo < start) && (start < (oo + size(oo))));
                    }

                    s = size(oo);
                    int overflow_p = 0;

                    if (mark_stack_tos + (num_partial_refs + 2) >= mark_stack_limit)
                    {
                        overflow_p = 1;
                    }

                    if (overflow_p == 0)
                    {
                        byte** place = ++mark_stack_tos;
                        mark_stack_tos++;
                        mark_object_simple1_large_context context = new()
                        {
                            heap = heap,
                            mark_stack_tos = mark_stack_tos,
                            full_p = full_p,
                            condemned_gen = condemned_gen,
                            thread = thread,
                            i = num_partial_refs,
                            ref_to_continue = null,
                        };

                        go_through_object_with_stop(
                            method_table(oo),
                            oo,
                            s,
                            &context,
                            &mark_object_simple1_large_callback,
                            start,
                            start_useful: 1);

                        mark_stack_tos = context.mark_stack_tos;
                        if (context.ref_to_continue is null)
                        {
                            *(place - 1) = null;
                        }

                        *place = context.ref_to_continue;
                    }
                    else
                    {
                        record_mark_stack_overflow(heap, oo);
                    }
                }
            }

        next_level:
            if (mark_stack_base != mark_stack_tos)
            {
                oo = *(--mark_stack_tos);
                start = oo;
            }
            else
            {
                break;
            }
        }
    }

    private unsafe struct mark_object_simple_context
    {
        public gc_heap* heap;
        public int condemned_gen;
        public int thread;
    }

    private static void mark_object_simple_callback(byte** ppslot, void* context)
    {
        mark_object_simple_context* mark_context = (mark_object_simple_context*)context;
        gc_heap* heap = mark_context->heap;
        byte* oo = heap->mark_queue.queue_mark(*ppslot, mark_context->condemned_gen);
        if (oo is not null)
        {
            m_boundary(heap, oo);
            add_to_promoted_bytes(heap, oo, mark_context->thread);
            if (contain_pointers_or_collectible(oo) != 0)
            {
                mark_object_simple1(heap, oo, oo);
            }
        }
    }

    // This method assumes that *po has already passed exact active-collection range checks.
    public static void mark_object_simple(gc_heap* heap, byte** po)
    {
        int condemned_gen = settings.condemned_generation;
        byte* o = *po;
        int thread = heap->heap_number;

        o = heap->mark_queue.queue_mark(o);
        if (o is not null)
        {
            m_boundary(heap, o);
            nuint s = size(o);
            add_to_promoted_bytes(heap, o, s, thread);

            mark_object_simple_context context = new()
            {
                heap = heap,
                condemned_gen = condemned_gen,
                thread = thread,
            };

            go_through_object_nostart(method_table(o), o, s, &context, &mark_object_simple_callback);
        }
    }

    public static void drain_mark_queue(gc_heap* heap)
    {
        int condemned_gen = settings.condemned_generation;
        int thread = heap->heap_number;

        byte* o;
        while ((o = heap->mark_queue.get_next_marked()) is not null)
        {
            m_boundary(heap, o);
            nuint s = size(o);
            add_to_promoted_bytes(heap, o, s, thread);
            if (contain_pointers_or_collectible(o) != 0)
            {
                mark_object_simple_context context = new()
                {
                    heap = heap,
                    condemned_gen = condemned_gen,
                    thread = thread,
                };

                go_through_object_nostart(method_table(o), o, s, &context, &mark_object_simple_callback);
            }
        }

        heap->mark_queue.verify_empty();
    }

    public static void mark_object(gc_heap* heap, byte* o)
    {
        if (is_in_gc_range(o) && is_in_condemned_gc(o))
        {
            mark_object_simple(heap, &o);
        }
    }

    private unsafe struct mark_through_object_context
    {
        public gc_heap* heap;
    }

    private static void mark_through_object_callback(byte** po, void* context)
    {
        mark_through_object_context* mark_context = (mark_through_object_context*)context;
        mark_object(mark_context->heap, *po);
    }

    public static void mark_through_object(gc_heap* heap, byte* oo, int mark_class_object_p)
    {
        _ = mark_class_object_p;
        if (contain_pointers(oo) != 0)
        {
            nuint s = size(oo);
            mark_through_object_context context = new()
            {
                heap = heap,
            };

            go_through_object_nostart(
                method_table(oo),
                oo,
                s,
                &context,
                &mark_through_object_callback);
        }
    }

    public static bool process_mark_overflow(gc_heap* heap, int condemned_gen_number)
    {
        nuint last_promoted_bytes = get_promoted_bytes(heap);
        bool overflow_p = false;

    recheck:
        drain_mark_queue(heap);
        if (heap->max_overflow_address is not null || heap->min_overflow_address != (byte*)nuint.MaxValue)
        {
            overflow_p = true;
            nuint new_size = unchecked(2 * heap->mark_stack_array_length);
            if (new_size < gc_rand.MARK_STACK_INITIAL_LENGTH)
            {
                new_size = gc_rand.MARK_STACK_INITIAL_LENGTH;
            }

            if (unchecked(new_size * (nuint)sizeof(mark)) > 100 * 1024)
            {
                nuint new_max_size = (get_total_heap_size() / 10) / (nuint)sizeof(mark);
                new_size = new_max_size < new_size ? new_max_size : new_size;
            }

            if (heap->mark_stack_array_length < new_size &&
                new_size - heap->mark_stack_array_length > heap->mark_stack_array_length / 2)
            {
                mark* tmp = (mark*)SyncImports.ManagedGC_AllocZeroed(
                    unchecked(new_size * (nuint)sizeof(mark)));
                if (tmp is not null)
                {
                    if (heap->mark_stack_array is not null)
                    {
                        SyncImports.ManagedGC_Free(heap->mark_stack_array);
                    }

                    heap->mark_stack_array = tmp;
                    heap->mark_stack_array_length = new_size;
                }
            }

            byte* min_add = heap->min_overflow_address;
            byte* max_add = heap->max_overflow_address;
            heap->max_overflow_address = null;
            heap->min_overflow_address = (byte*)nuint.MaxValue;
            process_mark_overflow_internal(heap, condemned_gen_number, min_add, max_add);
            goto recheck;
        }

        nuint current_promoted_bytes = get_promoted_bytes(heap);
        if (current_promoted_bytes != last_promoted_bytes)
        {
            fire_mark_overflow_event(current_promoted_bytes, last_promoted_bytes);
        }

        return overflow_p;
    }

    private static void fire_mark_overflow_event(nuint current_promoted_bytes, nuint previous_promoted_bytes)
    {
        // GC_ROOT_OVERFLOW has no translated GC event producer in this no-tracing configuration.
        _ = current_promoted_bytes;
        _ = previous_promoted_bytes;
    }

    public static void process_mark_overflow_internal(
        gc_heap* heap,
        int condemned_gen_number,
        byte* min_add,
        byte* max_add)
    {
        int full_p = condemned_gen_number == GCInterfaceOffsets.max_generation ? 1 : 0;
        int gen_limit = full_p != 0
            ? (int)gc_generation_num.total_generation_count
            : condemned_gen_number + 1;

        // Overflow ranges are reconciled across all heaps, so each worker rescans every heap's
        // segments starting from its own to load balance.
        for (int hi = 0; hi < n_heaps; hi++)
        {
            gc_heap* hp = g_heaps[(heap->heap_number + hi) % n_heaps];
            generation* generation_table = generation_table_of(hp);

            for (int i = get_stop_generation_index(condemned_gen_number); i < gen_limit; i++)
            {
                generation* gen = generation_of(generation_table, i);
                heap_segment* seg = heap_segment_in_range(generation.generation_start_segment(gen));
                int align_const = get_alignment_constant(i < (int)gc_generation_num.uoh_start_generation);

                Debug.Assert(seg is not null);

                while (seg is not null)
                {
                    byte* segment_start = heap_segment.heap_segment_mem(seg);
                    byte* o = segment_start > min_add ? segment_start : min_add;
                    byte* end = heap_segment.heap_segment_allocated(seg);

                    while (o < end && o <= max_add)
                    {
                        Debug.Assert(min_add <= o && max_add >= o);
                        if (((CObjectHeader*)o)->IsMarked() != 0)
                        {
                            mark_through_object(heap, o, mark_class_object_p: 1);
                        }

                        o += (nint)Align(size(o), align_const);
                    }

                    seg = heap_segment_next_in_range(seg);
                }
            }
        }
    }

    // ----------------------------------------------------------------------------------------
    // Promotion callbacks.
    // ----------------------------------------------------------------------------------------

    public static void pin_object(gc_heap* heap, byte* o, byte** ppObject)
    {
        ((CObjectHeader*)o)->SetPinned();
        GCEvents.GCEventFirePinObjectAtGCTime(o, ppObject);
        heap->num_pinned_objects++;
    }

    // GCHeap::Promote: the server exact/interior/pinned root promotion callback. The owning heap
    // (heap_of) provides find_object and the pinned counter; the current worker heap
    // (sc->thread_number) marks the object into its own per-heap mark queue.
    public static void promote(byte** ppObject, ScanContext* sc, uint flags)
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
        if (!is_in_condemned_gc(o))
        {
            return;
        }

        if ((flags & (uint)GCCallFlags.GC_CALL_INTERIOR) != 0 &&
            (o = find_object(o, hp)) is null)
        {
            return;
        }

        gc_heap* hpt = (sc is not null && (uint)sc->thread_number < (uint)n_heaps)
            ? g_heaps[sc->thread_number]
            : hp;

        if ((flags & (uint)GCCallFlags.GC_CALL_PINNED) != 0)
        {
            pin_object(hp, o, ppObject);
        }

        mark_object_simple(hpt, &o);
    }

    // ----------------------------------------------------------------------------------------
    // Per-heap root / finalizer / strong+pinned handle scan entry points.
    // ----------------------------------------------------------------------------------------

    // The parallel per-heap section of mark_phase before the join-gated tail: every worker scans
    // its own stack roots, finalizer roots, and strong/pinned handles into its mark queue, draining
    // between kinds. GcScanSizedRefs, card scanning, dependent-handle scanning, weak scanning, and
    // the full join sequence remain deferred with collection routing.
    public static void mark_phase_scan_roots(gc_heap* heap, ScanContext* sc)
    {
        int condemned_gen_number = settings.condemned_generation;

        GCToEEInterface.BeforeGcScanRoots(condemned_gen_number, is_bgc: 0, is_concurrent: 0);

        GCScan.GcScanRoots(
            &promote,
            condemned_gen_number,
            GCInterfaceOffsets.max_generation,
            sc);
        drain_mark_queue(heap);

        CFinalize* finalizeQueue = heap->server_finalize_queue;
        if (finalizeQueue is not null)
        {
            finalizeQueue->GcScanRoots(&promote, heap->heap_number, null);
            drain_mark_queue(heap);
        }

        GCScan.GcScanHandles(
            &promote,
            condemned_gen_number,
            GCInterfaceOffsets.max_generation,
            sc);
        drain_mark_queue(heap);
    }

    // ----------------------------------------------------------------------------------------
    // Server join boundary wiring for dependent-handle scanning.
    //
    // gcinternal.h declares these three GC-global latches. They are only ever transitioned
    // false -> true by unsynchronized workers and read / reset to false by a single thread under
    // the protection of a join, exactly as in mark_phase.cpp.
    // ----------------------------------------------------------------------------------------

    private static bool s_fUnpromotedHandles;
    private static bool s_fUnscannedPromotions;
    private static bool s_fScanRequired;

    // The MULTIPLE_HEAPS dependent-handle promotion loop. Because a primary holds a strong
    // reference to its secondary, promotions can cascade across worker threads, so every worker
    // must stay in lock-step (join exactly the same number of times) until no heap can promote
    // another secondary. This also drains all mark-stack overflow. GcDhInitialScan seeds the scan;
    // it is invoked by the caller before the initial_scan_p pass, as in mark_phase.cpp.
    public static void scan_dependent_handles(
        gc_heap* heap,
        int condemned_gen_number,
        ScanContext* sc,
        bool initial_scan_p)
    {
        // Preceding object promotions may have occurred, so set this unconditionally on entry.
        System.Threading.Volatile.Write(ref s_fUnscannedPromotions, true);

        while (true)
        {
            // Each worker needs to rescan its portion of the dependent handle table when at least
            // one object might have been promoted since the last scan and this thread's table has a
            // handle whose secondary is not promoted yet.
            if (GCScan.GcDhUnpromotedHandlesExist(sc))
            {
                System.Threading.Volatile.Write(ref s_fUnpromotedHandles, true);
            }

            drain_mark_queue(heap);

            // Synchronize so a single thread can read the shared state and decide whether to scan
            // again or terminate the loop.
            gc_t_join.join(heap, (int)gc_join_stage.gc_join_scan_dependent_handles);
            if (gc_t_join.joined())
            {
                System.Threading.Volatile.Write(
                    ref s_fScanRequired,
                    System.Threading.Volatile.Read(ref s_fUnscannedPromotions) &&
                    System.Threading.Volatile.Read(ref s_fUnpromotedHandles));

                System.Threading.Volatile.Write(ref s_fUnscannedPromotions, false);
                System.Threading.Volatile.Write(ref s_fUnpromotedHandles, false);

                if (!System.Threading.Volatile.Read(ref s_fScanRequired) && !initial_scan_p)
                {
                    // On the second invocation, reconcile all mark-overflow ranges across the heaps
                    // to help load balance if one heap has an outsized workload.
                    byte* all_heaps_max = null;
                    byte* all_heaps_min = (byte*)nuint.MaxValue;
                    for (int i = 0; i < n_heaps; i++)
                    {
                        if (all_heaps_max < g_heaps[i]->max_overflow_address)
                        {
                            all_heaps_max = g_heaps[i]->max_overflow_address;
                        }

                        if (all_heaps_min > g_heaps[i]->min_overflow_address)
                        {
                            all_heaps_min = g_heaps[i]->min_overflow_address;
                        }
                    }

                    for (int i = 0; i < n_heaps; i++)
                    {
                        g_heaps[i]->max_overflow_address = all_heaps_max;
                        g_heaps[i]->min_overflow_address = all_heaps_min;
                    }
                }

                gc_t_join.restart();
            }

            // Handle any mark-stack overflow: dependent-handle scanning relies on all previous
            // promotions being visible. A real overflow means at least one promotion may have
            // occurred, so latch it (safe even when terminating the loop below).
            if (process_mark_overflow(heap, condemned_gen_number))
            {
                System.Threading.Volatile.Write(ref s_fUnscannedPromotions, true);
            }

            if (!System.Threading.Volatile.Read(ref s_fScanRequired))
            {
                break;
            }

            // Join again so all overflow is processed before we scan dependent handle tables (if
            // overflow remains we could miss noting the promotion of some primary objects).
            gc_t_join.join(heap, (int)gc_join_stage.gc_join_rescan_dependent_handles);
            if (gc_t_join.joined())
            {
                gc_t_join.restart();
            }

            // If this worker's dependent handle table still has promotable handles, rescan; note a
            // resulting promotion since it could require a rescan here or on other workers.
            if (GCScan.GcDhUnpromotedHandlesExist(sc))
            {
                if (GCScan.GcDhReScan(sc))
                {
                    System.Threading.Volatile.Write(ref s_fUnscannedPromotions, true);
                }
            }
        }
    }

    // mark_phase.cpp declares this as a function-static VOLATILE(int32_t): the first server worker
    // to finish (would-be) sorting its mark list scans the syncblk cache exactly once. The joined
    // gc_join_null_dead_long_weak region resets it to 0 for every collection.
    private static int syncblock_scan_p;

    // ----------------------------------------------------------------------------------------
    // The blocking server mark_phase driver, translated from the SVR compilation of
    // gc_heap::mark_phase in mark_phase.cpp for the active SERVER_GC / MULTIPLE_HEAPS /
    // USE_REGIONS chain. Every server GC worker runs this on its own heap; the gc_t_join calls
    // keep the workers in lock-step at each phase boundary. It drives the already-translated mark
    // core: the setup_mark_state_for_collection cursors, the promote callback and drain_mark_queue,
    // the scan_dependent_handles join cycle, sync_promoted_bytes, and decide_on_promotion_surv.
    // The root/finalizer/handle scans are inlined here (rather than calling mark_phase_scan_roots)
    // so BeforeGcScanRoots fires once inside the joined gc_join_begin_mark_phase region exactly as
    // in native, instead of per heap.
    //
    // The join sequence is complete: gc_join_begin_mark_phase, gc_join_scan_sizedref_done,
    // gc_join_scan_dependent_handles / gc_join_rescan_dependent_handles (inside
    // scan_dependent_handles), gc_join_null_dead_short_weak, gc_join_scan_finalization,
    // gc_join_null_dead_long_weak, and gc_join_null_dead_syncblk. No collection routes this driver
    // yet.
    //
    // Two native steps whose cross-heap dependencies are not yet closed are kept as faithful,
    // clearly marked DEFERRED call sites:
    //   * the !full_p cross-generation card-marking block
    //     (mark_through_cards_for_segments / mark_through_cards_for_uoh_objects) needs the server
    //     card-scan plus background-sweep state (should_check_bgc_mark / fgc_should_consider_object)
    //     which is a separate unported subsystem; the bracketing save_current_survived /
    //     update_old_card_survived are translated, and
    //   * equalize_promoted_bytes (region rebalancing) and sort_mark_list (per-heap mark-list sort
    //     with its cross-heap equalize_mark_lists) are the "too large" cross-heap balancing steps.
    // merge_mark_lists is #if MULTIPLE_HEAPS && !USE_REGIONS in native and so is excluded for the
    // region build regardless. The MH_SC_MARK mark_steal, the CARD_BUNDLE r_join, the BGC
    // background-root scan, and the FEATURE_JAVAMARSHAL bridge, plus the HEAP_ANALYZE / SNOOP_STATS
    // / FEATURE_EVENT_TRACE record_mark_time instrumentation, are excluded exactly as for the
    // active configuration / deferred subsystems.
    public static void mark_phase(gc_heap* heap, int condemned_gen_number)
    {
        Debug.Assert(settings.concurrent == 0);

        ScanContext sc = default;
        sc.init();
        sc.thread_number = heap->heap_number;
        sc.thread_count = n_heaps;
        sc.promotion = 1;
        sc.concurrent = 0;

        bool full_p = condemned_gen_number == GCInterfaceOffsets.max_generation;

        int gen_to_init = condemned_gen_number == GCInterfaceOffsets.max_generation
            ? (int)gc_generation_num.total_generation_count - 1
            : condemned_gen_number;
        for (int gen_idx = 0; gen_idx <= gen_to_init; gen_idx++)
        {
            dynamic_data* dd = dynamic_data_of(heap, gen_idx);
            dynamic_data.dd_begin_data_size(dd) = unchecked(
                generation_size(heap, gen_idx) - dynamic_data.dd_fragmentation(dd));
            dynamic_data.dd_survived_size(dd) = 0;
            dynamic_data.dd_pinned_survived_size(dd) = 0;
            dynamic_data.dd_artificial_pinned_survived_size(dd) = 0;
            dynamic_data.dd_added_pinned_size(dd) = 0;
            dynamic_data.dd_padding_size(dd) = 0;
        }

        if (heap->gen0_must_clear_bricks > 0)
        {
            heap->gen0_must_clear_bricks--;
        }

        nuint last_promoted_bytes = 0;
        // init_promoted_bytes is #if !USE_REGIONS || _DEBUG; the region survivor storage is cleared
        // by setup_mark_state_for_collection below, and the _DEBUG g_promoted cross-check counter is
        // not part of this port.
        reset_mark_stack(heap);

        special_sweep_p = false;

        gc_t_join.join(heap, (int)gc_join_stage.gc_join_begin_mark_phase);
        if (gc_t_join.joined())
        {
            region_count = global_region_allocator.get_used_region_count();
            grow_mark_list_piece();
            compute_gc_and_ephemeral_range(heap, condemned_gen_number, end_of_gc_p: false);

            GCToEEInterface.BeforeGcScanRoots(condemned_gen_number, is_bgc: 0, is_concurrent: 0);

            gc_t_join.restart();
        }

        bool markStateReady = setup_mark_state_for_collection(heap);
        Debug.Assert(markStateReady);

        if (condemned_gen_number == GCInterfaceOffsets.max_generation)
        {
            GCScan.GcScanSizedRefs(
                &promote,
                condemned_gen_number,
                GCInterfaceOffsets.max_generation,
                &sc);
            drain_mark_queue(heap);
            fire_mark_event(heap, GC_ROOT_SIZEDREF, ref last_promoted_bytes);

            gc_t_join.join(heap, (int)gc_join_stage.gc_join_scan_sizedref_done);
            if (gc_t_join.joined())
            {
                gc_t_join.restart();
            }
        }

        GCScan.GcScanRoots(
            &promote,
            condemned_gen_number,
            GCInterfaceOffsets.max_generation,
            &sc);
        drain_mark_queue(heap);
        fire_mark_event(heap, GC_ROOT_STACK, ref last_promoted_bytes);

        CFinalize* finalizeQueue = heap->server_finalize_queue;
        if (finalizeQueue is not null)
        {
            finalizeQueue->GcScanRoots(&promote, heap->heap_number, null);
            drain_mark_queue(heap);
            fire_mark_event(heap, GC_ROOT_FQ, ref last_promoted_bytes);
        }

        GCScan.GcScanHandles(
            &promote,
            condemned_gen_number,
            GCInterfaceOffsets.max_generation,
            &sc);
        drain_mark_queue(heap);
        fire_mark_event(heap, GC_ROOT_HANDLES, ref last_promoted_bytes);

        if (!full_p)
        {
            save_current_survived(heap);

            // DEFERRED: mark_through_cards_for_segments (small objects) and
            // mark_through_cards_for_uoh_objects (loh_generation, poh_generation). The server
            // cross-generation card scan depends on the not-yet-ported server card-scan and
            // background-sweep state (should_check_bgc_mark / fgc_should_consider_object and the
            // survived-per-region card accounting). The bracketing survivor bookkeeping is
            // translated so the card scan drops straight in when that subsystem lands: with no
            // card survivors added, update_old_card_survived correctly leaves old_card at 0.
            drain_mark_queue(heap);

            update_old_card_survived(heap);
            fire_mark_event(heap, GC_ROOT_OLDER, ref last_promoted_bytes);
        }

        // Dependent handles need the special algorithm in scan_dependent_handles. The initial scan
        // runs unsynchronized without processing overflow; in the common case (no collectible
        // dependent handles) it lets us optimize away the synchronized cycle.
        GCScan.GcDhInitialScan(
            &promote,
            condemned_gen_number,
            GCInterfaceOffsets.max_generation,
            &sc);
        scan_dependent_handles(heap, condemned_gen_number, &sc, initial_scan_p: true);
        heap->mark_queue.verify_empty();
        fire_mark_event(heap, GC_ROOT_DH_HANDLES, ref last_promoted_bytes);

        gc_t_join.join(heap, (int)gc_join_stage.gc_join_null_dead_short_weak);
        if (gc_t_join.joined())
        {
            GCToEEInterface.AfterGcScanRoots(
                condemned_gen_number,
                GCInterfaceOffsets.max_generation,
                &sc);
            gc_t_join.restart();
        }

        // null out the target of short weakrefs that were not promoted.
        GCScan.GcShortWeakPtrScan(
            condemned_gen_number,
            GCInterfaceOffsets.max_generation,
            &sc);

        gc_t_join.join(heap, (int)gc_join_stage.gc_join_scan_finalization);
        if (gc_t_join.joined())
        {
            gc_t_join.restart();
        }

        nuint promoted_bytes_live = get_promoted_bytes(heap);

        if (finalizeQueue is not null)
        {
            finalizeQueue->ScanForFinalization(&promote, condemned_gen_number, heap);
            drain_mark_queue(heap);
            fire_mark_event(heap, GC_ROOT_NEW_FQ, ref last_promoted_bytes);
        }
        GCToEEInterface.DiagWalkFReachableObjects(heap);

        // Scan dependent handles again to promote any secondaries whose primaries were promoted for
        // finalization; scan_dependent_handles also processes any remaining mark-stack overflow.
        scan_dependent_handles(heap, condemned_gen_number, &sc, initial_scan_p: false);
        heap->mark_queue.verify_empty();
        fire_mark_event(heap, GC_ROOT_DH_HANDLES, ref last_promoted_bytes);

        heap->total_promoted_bytes = get_promoted_bytes(heap);

        gc_t_join.join(heap, (int)gc_join_stage.gc_join_null_dead_long_weak);
        if (gc_t_join.joined())
        {
            sync_promoted_bytes();

            // DEFERRED: equalize_promoted_bytes(settings.condemned_generation) roughly balances
            // promoted bytes across heaps by moving regions between them, to load-balance the plan
            // and relocate phases. It depends on the cross-heap unlink_first_rw_region /
            // thread_start_region region-threading machinery that is not yet ported.
            // sync_promoted_bytes above still folds every heap's per-region survivors into the
            // owning region's segment fields.

            syncblock_scan_p = 0;
            gc_t_join.restart();
        }

        // null out the target of long weakrefs that were not promoted.
        GCScan.GcWeakPtrScan(
            condemned_gen_number,
            GCInterfaceOffsets.max_generation,
            &sc);

        // DEFERRED: total_mark_list_size = sort_mark_list() sorts this heap's portion of the mark
        // list for the plan phase; its cross-heap equalize_mark_lists is not yet ported.
        // merge_mark_lists (which consumes total_mark_list_size) is #if MULTIPLE_HEAPS &&
        // !USE_REGIONS and so is excluded for the region build regardless.

        // First thread to finish (would-be) sorting scans the syncblk cache exactly once.
        if (syncblock_scan_p == 0 &&
            System.Threading.Interlocked.Increment(ref syncblock_scan_p) == 1)
        {
            GCScan.GcWeakPtrScanBySingleThread(
                condemned_gen_number,
                GCInterfaceOffsets.max_generation,
                &sc);
        }

        gc_t_join.join(heap, (int)gc_join_stage.gc_join_null_dead_syncblk);
        if (gc_t_join.joined())
        {
            // decide on promotion
            if (settings.promotion == 0)
            {
                nuint m = 0;
                for (int n = 0; n <= condemned_gen_number; n++)
                {
                    m = unchecked(
                        m +
                        dynamic_data.dd_min_size(dynamic_data_of(heap, n)) *
                        (nuint)(n + 1) * 10 / 100);
                }

                settings.promotion = decide_on_promotion_surv(m) ? 1 : 0;
            }

            gc_t_join.restart();
        }

        // merge_mark_lists (total_mark_list_size) is #if MULTIPLE_HEAPS && !USE_REGIONS; excluded.

        heap->finalization_promoted_bytes =
            unchecked(heap->total_promoted_bytes - promoted_bytes_live);

        heap->mark_queue.verify_empty();
    }
}

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
