// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from the dependency-closed pinned-plug queue helpers in mark_phase.cpp and gcinternal.h.

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection;

internal unsafe partial struct gc_heap
{
    public static void reset_pinned_queue(gc_heap* heap)
    {
        heap->mark_stack_tos = 0;
        heap->mark_stack_bos = 0;
    }

    public static void reset_pinned_queue_bos(gc_heap* heap)
    {
        heap->mark_stack_bos = 0;
    }

    // last_pinned_plug is only for asserting purpose.
    public static void merge_with_last_pinned_plug(gc_heap* heap, byte* last_pinned_plug, nuint plug_size)
    {
        if (last_pinned_plug is not null)
        {
            mark* last_m = &heap->mark_stack_array[heap->mark_stack_tos - 1];
            Debug.Assert(last_pinned_plug == last_m->first);
            if (last_m->saved_post_p != 0)
            {
                last_m->saved_post_p = 0;
                // We need to recover what the gap has overwritten.
                *(gap_reloc_pair*)(last_m->first + last_m->len - (nint)sizeof(plug_and_gap)) = last_m->saved_post_plug;
            }
            last_m->len += plug_size;
        }
    }

    public static void set_allocator_next_pin(gc_heap* heap, generation* gen)
    {
        if (pinned_plug_que_empty_p(heap) == 0)
        {
            mark* oldest_entry = oldest_pin(heap);
            byte* plug = pinned_plug(oldest_entry);
            if (plug >= generation.generation_allocation_pointer(gen) &&
                plug < generation.generation_allocation_limit(gen))
            {
#if DEBUG && USE_REGIONS
                if (GCCommon.seg_mapping_table is not null)
                {
                    Debug.Assert(
                        region_of(generation.generation_allocation_pointer(gen)) ==
                        region_of(generation.generation_allocation_limit(gen) - 1));
                }
#endif
                generation.generation_allocation_limit(gen) = pinned_plug(oldest_entry);
            }
            else
            {
                Debug.Assert(
                    !(plug < generation.generation_allocation_pointer(gen) &&
                      plug >= heap_segment.heap_segment_mem(generation.generation_allocation_segment(gen))));
            }
        }
    }

    // After we set the info, we increase tos.
    public static void set_pinned_info(gc_heap* heap, byte* last_pinned_plug, nuint plug_len, generation* gen)
    {
        mark* m = &heap->mark_stack_array[heap->mark_stack_tos];
        Debug.Assert(last_pinned_plug == m->first);

        m->len = plug_len;
        heap->mark_stack_tos++;
        Debug.Assert(gen is not null);
        // Why are we checking here? gen is never 0.
        if (gen is not null)
        {
            set_allocator_next_pin(heap, gen);
        }
    }

    public static nuint deque_pinned_plug(gc_heap* heap)
    {
        nuint m = heap->mark_stack_bos;
        heap->mark_stack_bos++;
        return m;
    }

    public static mark* before_oldest_pin(gc_heap* heap)
    {
        if (heap->mark_stack_bos >= 1)
        {
            return pinned_plug_of(heap, heap->mark_stack_bos - 1);
        }
        else
        {
            return null;
        }
    }

    public static void make_mark_stack(gc_heap* heap, mark* arr)
    {
        reset_pinned_queue(heap);
        heap->mark_stack_array = arr;
        heap->mark_stack_array_length = gc_rand.MARK_STACK_INITIAL_LENGTH;
    }

    public static mark* pinned_plug_of(gc_heap* heap, nuint bos)
    {
        return &heap->mark_stack_array[bos];
    }

    public static mark* oldest_pin(gc_heap* heap)
    {
        return pinned_plug_of(heap, heap->mark_stack_bos);
    }

    public static int pinned_plug_que_empty_p(gc_heap* heap)
    {
        return heap->mark_stack_bos == heap->mark_stack_tos ? 1 : 0;
    }

    public static byte* pinned_plug(mark* m)
    {
        return m->first;
    }

    public static ref nuint pinned_len(mark* m)
    {
        return ref m->len;
    }

    public static void set_new_pin_info(mark* m, byte* pin_free_space_start)
    {
        m->len = (nuint)(pinned_plug(m) - pin_free_space_start);
        m->allocation_context_start_region = pin_free_space_start;
    }

    public static void update_oldest_pinned_plug(gc_heap* heap)
    {
        heap->oldest_pinned_plug = pinned_plug_que_empty_p(heap) != 0 ? null : pinned_plug(oldest_pin(heap));
    }
}
