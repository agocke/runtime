// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Server plan-phase large-object (LOH / UOH) planning family, translated from the SVR-namespace
// compilation of plan_phase.cpp for the active x64 Linux SERVER_GC -> MULTIPLE_HEAPS ->
// DYNAMIC_HEAP_COUNT -> USE_REGIONS feature chain. This is the FEATURE_LOH_COMPACTION planner the
// plan_phase driver runs for the large object heap when the max-generation collection decides to
// compact it: it relocates every surviving LOH object into a compacted plan layout, threading the
// pinned objects through a dedicated LOH pinned-plug queue.
//
//   * The LOH pinned-plug queue leaves (loh_pinned_plug_que_empty_p / loh_pinned_plug_of /
//     loh_oldest_pin / loh_deque_pinned_plug / loh_enque_pinned_plug / loh_set_allocator_next_pin)
//     hand out the queued pins in address order. The queue (loh_pinned_queue /
//     loh_pinned_queue_length / loh_pinned_queue_tos / loh_pinned_queue_bos) is
//     PER_HEAP_FIELD_SINGLE_GC / PER_HEAP_FIELD_MAINTAINED, so it is instance-owned for
//     MULTIPLE_HEAPS in GCPriv.cs (static in WKS) and reached through the gc_heap* parameter here.
//   * loh_size_fit_p decides whether a padded LOH plug fits between an allocation pointer and limit,
//     and loh_allocate_in_condemned places the plug into the LOH's compacted plan window, consuming
//     pins and growing / rolling over LOH regions with grow_heap_segment as needed.
//   * plan_loh walks every LOH region, enqueues pins, relocates non-pinned survivors through
//     loh_allocate_in_condemned, records each object's relocation distance, and finally fixes the
//     plan-allocated tail of each region so the relocate/compact execution can consume the plan.
//   * decay_loh_pinned_queue implements the plan_phase driver's non-compacting decay: when the LOH
//     is not compacted the queue's decay counter is decremented and the queue is freed once idle.
//
// grow_mark_stack (ManagedServerGCPlanBrick.cs) grows the queue; grow_heap_segment
// (ManagedServerGCPlanCondemned.cs), get_uoh_start_object / heap_segment_rw (GCRegionsSegments.cs),
// loh_set_node_relocation_distance / loh_padding_obj (GCPriv.cs), and pinned_plug / size / AlignQword
// / get_alignment_constant (ManagedServerGC.cs) are reused as-is. FEATURE_EVENT_TRACE loh_compact_info
// timing is omitted, matching the WKS translation and the deferred server event integration.
//
// No collection is routed by this slice: the plan_phase driver that sequences plan_loh (settings.
// loh_compaction gating, the sweep_uoh_objects fallback for the non-compacting LOH and for POH, and
// the loh_compacted_p bookkeeping), the relocate_in_loh_compact / compact_loh execution, and the
// gc_join plan-phase joins all remain deferred, so nothing here runs against a live heap yet.
//
// sweep_uoh_objects (the plan-time UOH sweep the driver uses for POH and for a non-compacting LOH)
// is NOT translated here: it mutates freeable_uoh_segment (PER_HEAP_FIELD_MAINTAINED), whose per-heap
// conversion drags in the rearrange_uoh_segments / delay_free_segments segment-return family and its
// hp-threading through the hp-less WKS collection drivers -- a separate coherent unit.

#if SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection;

#pragma warning disable CS8981 // Native type names are intentionally preserved.
internal unsafe partial struct gc_heap
{
    // plan_phase.cpp loh_pinned_plug_que_empty_p: the queue is empty when the bottom-of-stack cursor
    // has caught up to the top-of-stack cursor.
    public static int loh_pinned_plug_que_empty_p(gc_heap* hp)
    {
        return hp->loh_pinned_queue_bos == hp->loh_pinned_queue_tos ? 1 : 0;
    }

    // plan_phase.cpp loh_pinned_plug_of: address of the queued pin at the given bottom-of-stack index.
    public static mark* loh_pinned_plug_of(gc_heap* hp, nuint bos)
    {
        return &hp->loh_pinned_queue[bos];
    }

    // plan_phase.cpp loh_oldest_pin: the pin at the current bottom of the queue.
    public static mark* loh_oldest_pin(gc_heap* hp)
    {
        return loh_pinned_plug_of(hp, hp->loh_pinned_queue_bos);
    }

    // plan_phase.cpp loh_deque_pinned_plug: pop the oldest pin, returning its index.
    public static nuint loh_deque_pinned_plug(gc_heap* hp)
    {
        nuint m = hp->loh_pinned_queue_bos;
        hp->loh_pinned_queue_bos++;
        return m;
    }

    // plan_phase.cpp loh_set_allocator_next_pin: clamp the LOH plan allocation limit to the next pin
    // when that pin falls inside the current plan window, so loh_allocate_in_condemned stops at the
    // pin before overrunning it.
    public static void loh_set_allocator_next_pin(gc_heap* hp)
    {
        if (loh_pinned_plug_que_empty_p(hp) == 0)
        {
            mark* oldest_entry = loh_oldest_pin(hp);
            byte* plug = pinned_plug(oldest_entry);
            generation* gen = generation_of(
                generation_table_of(hp),
                (int)gc_generation_num.loh_generation);
            if (plug >= generation.generation_allocation_pointer(gen) &&
                plug < generation.generation_allocation_limit(gen))
            {
                generation.generation_allocation_limit(gen) = plug;
            }
            else
            {
                Debug.Assert(
                    !(plug < generation.generation_allocation_pointer(gen) &&
                      plug >= heap_segment.heap_segment_mem(
                          generation.generation_allocation_segment(gen))));
            }
        }
    }

    // plan_phase.cpp loh_enque_pinned_plug: push a pin onto this heap's LOH queue, growing it if full.
    // Returns 0 (no compaction) if the queue cannot grow, exactly as native returns FALSE.
    public static int loh_enque_pinned_plug(gc_heap* hp, byte* plug, nuint len)
    {
        Debug.Assert(
            len >= Align(
                (nuint)GCInterfaceOffsets.min_obj_size,
                get_alignment_constant(small_object_p: false)));

        if (hp->loh_pinned_queue_length <= hp->loh_pinned_queue_tos)
        {
            if (grow_mark_stack(
                    ref hp->loh_pinned_queue,
                    ref hp->loh_pinned_queue_length,
                    LOH_PIN_QUEUE_LENGTH) == 0)
            {
                return 0;
            }
        }

        mark* m = &hp->loh_pinned_queue[hp->loh_pinned_queue_tos];
        m->first = plug;
        m->len = len;
        hp->loh_pinned_queue_tos++;
        loh_set_allocator_next_pin(hp);
        return 1;
    }

    // plan_phase.cpp loh_size_fit_p: does a plug of the given size fit between alloc_pointer and
    // alloc_limit, accounting for the LOH padding object placed before it (and after it, unless the
    // plug is at the end of the plan window)?
    public static bool loh_size_fit_p(
        nuint size,
        byte* alloc_pointer,
        byte* alloc_limit,
        bool end_p)
    {
        if (alloc_pointer > alloc_limit)
        {
            return false;
        }

        nuint pad = unchecked((nuint)(end_p ? 1 : 2) * AlignQword((nuint)sizeof(loh_padding_obj)));
        nuint available = (nuint)(alloc_limit - alloc_pointer);
        return pad <= available && size <= available - pad;
    }

    // plan_phase.cpp loh_allocate_in_condemned: place a surviving LOH plug into the compacted plan
    // window, consuming pins and rolling over / growing LOH regions as needed. Returns the padded
    // plug address, or null on a fatal short-of-space condition (native calls FATAL_GC_ERROR).
    public static byte* loh_allocate_in_condemned(gc_heap* hp, nuint size)
    {
        generation* gen = generation_of(
            generation_table_of(hp),
            (int)gc_generation_num.loh_generation);

        while (true)
        {
            heap_segment* seg = generation.generation_allocation_segment(gen);
            if (seg is null)
            {
                return null;
            }

            byte* alloc_pointer = generation.generation_allocation_pointer(gen);
            byte* alloc_limit = generation.generation_allocation_limit(gen);
            bool end_p = alloc_limit == heap_segment.heap_segment_plan_allocated(seg);
            if (loh_size_fit_p(size, alloc_pointer, alloc_limit, end_p))
            {
                Debug.Assert(alloc_pointer >= heap_segment.heap_segment_mem(seg));
                byte* result = alloc_pointer;
                nuint loh_pad = AlignQword((nuint)sizeof(loh_padding_obj));

                generation.generation_allocation_pointer(gen) =
                    alloc_pointer + (nint)unchecked(size + loh_pad);
                Debug.Assert(
                    generation.generation_allocation_pointer(gen) <=
                    generation.generation_allocation_limit(gen));

                return result + (nint)loh_pad;
            }

            if (loh_pinned_plug_que_empty_p(hp) == 0 &&
                alloc_limit == pinned_plug(loh_oldest_pin(hp)))
            {
                mark* m = loh_pinned_plug_of(hp, loh_deque_pinned_plug(hp));
                nuint len = m->len;
                byte* plug = pinned_plug(m);
                if (plug < alloc_pointer)
                {
                    return null;
                }

                m->len = (nuint)(plug - alloc_pointer);
                generation.generation_allocation_pointer(gen) = plug + (nint)len;
                generation.generation_allocation_limit(gen) =
                    heap_segment.heap_segment_plan_allocated(seg);
                loh_set_allocator_next_pin(hp);
                continue;
            }

            if (alloc_limit != heap_segment.heap_segment_plan_allocated(seg))
            {
                generation.generation_allocation_limit(gen) =
                    heap_segment.heap_segment_plan_allocated(seg);
            }
            else if (heap_segment.heap_segment_plan_allocated(seg) !=
                     heap_segment.heap_segment_committed(seg))
            {
                heap_segment.heap_segment_plan_allocated(seg) =
                    heap_segment.heap_segment_committed(seg);
                generation.generation_allocation_limit(gen) =
                    heap_segment.heap_segment_plan_allocated(seg);
            }
            else
            {
                nuint loh_pad = AlignQword((nuint)sizeof(loh_padding_obj));
                if (loh_size_fit_p(
                        size,
                        generation.generation_allocation_pointer(gen),
                        heap_segment.heap_segment_reserved(seg),
                        end_p: true) &&
                    grow_heap_segment(
                        seg,
                        generation.generation_allocation_pointer(gen) +
                            (nint)unchecked(size + loh_pad),
                        hp->heap_number))
                {
                    heap_segment.heap_segment_plan_allocated(seg) =
                        heap_segment.heap_segment_committed(seg);
                    generation.generation_allocation_limit(gen) =
                        heap_segment.heap_segment_plan_allocated(seg);
                }
                else
                {
                    heap_segment* next_seg = heap_segment.heap_segment_next(seg);
                    Debug.Assert(
                        generation.generation_allocation_pointer(gen) >=
                        heap_segment.heap_segment_mem(seg));
                    if (loh_pinned_plug_que_empty_p(hp) == 0)
                    {
                        byte* oldest_plug = pinned_plug(loh_oldest_pin(hp));
                        if (oldest_plug < heap_segment.heap_segment_allocated(seg) &&
                            oldest_plug >= generation.generation_allocation_pointer(gen))
                        {
                            GCToEEInterface.HandleFatalError(CORINFO_EXCEPTION_GC);
                            return null;
                        }
                    }

                    Debug.Assert(
                        generation.generation_allocation_pointer(gen) <=
                        heap_segment.heap_segment_committed(seg));
                    heap_segment.heap_segment_plan_allocated(seg) =
                        generation.generation_allocation_pointer(gen);

                    if (next_seg is null)
                    {
                        GCToEEInterface.HandleFatalError(CORINFO_EXCEPTION_GC);
                        return null;
                    }

                    generation.generation_allocation_segment(gen) = next_seg;
                    generation.generation_allocation_pointer(gen) =
                        heap_segment.heap_segment_mem(next_seg);
                    generation.generation_allocation_limit(gen) =
                        generation.generation_allocation_pointer(gen);
                }
            }

            loh_set_allocator_next_pin(hp);
        }
    }

    // plan_phase.cpp decay_loh_pinned_queue: the plan_phase driver's non-compacting decay path.
    // When the LOH is not compacted the queue's decay counter is decremented and, once it reaches
    // zero, the queue is freed so a run of non-compacting GCs releases the pinned queue.
    public static void decay_loh_pinned_queue(gc_heap* hp)
    {
        if (hp->loh_pinned_queue is not null)
        {
            hp->loh_pinned_queue_decay--;
            if (hp->loh_pinned_queue_decay == 0)
            {
                SyncImports.ManagedGC_Free(hp->loh_pinned_queue);
                hp->loh_pinned_queue = null;
            }
        }
    }

    // plan_phase.cpp plan_loh: plan the compacted LOH layout. Allocate the pinned queue if needed,
    // reset each region's plan-allocated tail, then walk every LOH object relocating non-pinned
    // survivors through loh_allocate_in_condemned and enqueuing pins, recording relocation distances.
    // After the walk, drain the pinned queue, advancing the allocation pointer past each pin and
    // stamping the free space in front of it. Returns false (fall back to sweeping) on any allocation
    // failure, exactly as native returns FALSE.
    public static bool plan_loh(gc_heap* hp)
    {
        if (hp->loh_pinned_queue is null)
        {
            nuint bytes = LOH_PIN_QUEUE_LENGTH * (nuint)sizeof(mark);
            hp->loh_pinned_queue =
                (mark*)SyncImports.ManagedGC_AllocZeroed(bytes);
            if (hp->loh_pinned_queue is null)
            {
                return false;
            }

            hp->loh_pinned_queue_length = LOH_PIN_QUEUE_LENGTH;
        }

        hp->loh_pinned_queue_decay = LOH_PIN_DECAY;
        hp->loh_pinned_queue_tos = 0;
        hp->loh_pinned_queue_bos = 0;

        generation* gen = generation_of(
            generation_table_of(hp),
            (int)gc_generation_num.loh_generation);
        heap_segment* start_seg =
            heap_segment_rw(generation.generation_start_segment(gen));
        Debug.Assert(start_seg is not null);
        heap_segment* seg = start_seg;
        byte* o = get_uoh_start_object(seg, gen);

        while (seg is not null)
        {
            heap_segment.heap_segment_plan_allocated(seg) =
                heap_segment.heap_segment_mem(seg);
            seg = heap_segment.heap_segment_next(seg);
        }

        seg = start_seg;

        // We don't need to ever realloc gen3 start so don't touch it.
        heap_segment.heap_segment_plan_allocated(seg) = o;
        generation.generation_allocation_pointer(gen) = o;
        generation.generation_allocation_limit(gen) = o;
        generation.generation_allocation_segment(gen) = start_seg;

        while (true)
        {
            if (o >= heap_segment.heap_segment_allocated(seg))
            {
                seg = heap_segment.heap_segment_next(seg);
                if (seg is null)
                {
                    break;
                }

                o = heap_segment.heap_segment_mem(seg);
            }

            if (((CObjectHeader*)o)->IsMarked() != 0)
            {
                nuint object_size = AlignQword(size(o));
                byte* new_address;
                if (((CObjectHeader*)o)->IsPinned() != 0)
                {
                    // We don't clear the pinned bit yet so we can check in compact phase how big a
                    // free object we should allocate in front of the pinned object. We use the reloc
                    // address field to store this.
                    if (loh_enque_pinned_plug(hp, o, object_size) == 0)
                    {
                        return false;
                    }

                    new_address = o;
                }
                else
                {
                    new_address = loh_allocate_in_condemned(hp, object_size);
                    if (new_address is null)
                    {
                        return false;
                    }
                }

                loh_set_node_relocation_distance(
                    o,
                    unchecked((nint)(new_address - o)));
                o += (nint)object_size;
                if (o < heap_segment.heap_segment_allocated(seg))
                {
                    Debug.Assert(((CObjectHeader*)o)->IsMarked() == 0);
                }
            }
            else
            {
                while (o < heap_segment.heap_segment_allocated(seg) &&
                       ((CObjectHeader*)o)->IsMarked() == 0)
                {
                    o += (nint)AlignQword(size(o));
                }
            }
        }

        while (loh_pinned_plug_que_empty_p(hp) == 0)
        {
            mark* m = loh_pinned_plug_of(hp, loh_deque_pinned_plug(hp));
            nuint len = m->len;
            byte* plug = pinned_plug(m);

            // detect pinned block in different segment (later) than allocation segment
            heap_segment* nseg = generation.generation_allocation_segment(gen);
            if (nseg is null)
            {
                return false;
            }

            while (plug < generation.generation_allocation_pointer(gen) ||
                   plug >= heap_segment.heap_segment_allocated(nseg))
            {
                Debug.Assert(
                    plug < heap_segment.heap_segment_mem(nseg) ||
                    plug > heap_segment.heap_segment_reserved(nseg));
                Debug.Assert(
                    generation.generation_allocation_pointer(gen) >=
                    heap_segment.heap_segment_mem(nseg));
                Debug.Assert(
                    generation.generation_allocation_pointer(gen) <=
                    heap_segment.heap_segment_committed(nseg));

                heap_segment.heap_segment_plan_allocated(nseg) =
                    generation.generation_allocation_pointer(gen);
                nseg = heap_segment.heap_segment_next(nseg);
                if (nseg is null)
                {
                    return false;
                }

                generation.generation_allocation_segment(gen) = nseg;
                generation.generation_allocation_pointer(gen) =
                    heap_segment.heap_segment_mem(nseg);
            }

            if (plug < generation.generation_allocation_pointer(gen))
            {
                return false;
            }

            m->len = (nuint)(plug - generation.generation_allocation_pointer(gen));
            generation.generation_allocation_pointer(gen) = plug + (nint)len;
        }

        heap_segment.heap_segment_plan_allocated(
            generation.generation_allocation_segment(gen)) =
            generation.generation_allocation_pointer(gen);
        generation.generation_allocation_pointer(gen) = null;
        generation.generation_allocation_limit(gen) = null;
        return true;
    }
}
#pragma warning restore CS8981

#endif // SERVER_GC && MULTIPLE_HEAPS && USE_REGIONS
