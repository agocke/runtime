// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from the dependency-closed pinned-plug queue and mark-stack helpers in mark_phase.cpp and gcinternal.h.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection;

// Ported from MethodTable.h, ObjectLayout.h, and gcinternal.h. This is the collector's view of
// the object prefix, rather than a managed object representation.
[StructLayout(LayoutKind.Explicit)]
internal unsafe struct MethodTable
{
    public const uint CollectibleFlag = 0x00200000;
    public const uint HasPointersFlag = 0x01000000;
    public const uint HasComponentSizeFlag = 0x80000000;

    [FieldOffset(0)]
    public ushort m_usComponentSize;

    [FieldOffset(0)]
    public uint m_uFlags;

    [FieldOffset(4)]
    public uint m_uBaseSize;

    [FieldOffset(8)]
    public MethodTable* m_RelatedType;

#if TARGET_64BIT
    [FieldOffset(16)]
    public ushort m_usNumVtableSlots;

    [FieldOffset(18)]
    public ushort m_usNumInterfaces;

    [FieldOffset(20)]
    public uint m_uHashCode;
#else
    [FieldOffset(12)]
    public ushort m_usNumVtableSlots;

    [FieldOffset(14)]
    public ushort m_usNumInterfaces;

    [FieldOffset(16)]
    public uint m_uHashCode;
#endif

    public uint GetBaseSize() => m_uBaseSize;

    public ushort RawGetComponentSize() => m_usComponentSize;

    public int HasComponentSize() => (int)m_uFlags < 0 ? 1 : 0;

    public int HasReferenceFields() => (m_uFlags & HasPointersFlag) != 0 ? 1 : 0;

    public int ContainsGCPointers() => HasReferenceFields();

    // NativeAOT's MethodTable does not support collectible types, so this is an alias of
    // HasReferenceFields rather than a check of CollectibleFlag.
    public int ContainsGCPointersOrCollectible() => HasReferenceFields();
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CObjectHeader
{
    public const nuint GC_MARKED = 0x1;

#if TARGET_64BIT && !TARGET_WASM
    public const nuint BGC_MARKED_BY_FGC = 0x2;
    public const nuint MAKE_FREE_OBJ_IN_COMPACT = 0x4;
    private const nuint ALLOWED_SPECIAL_HEADER_BITS =
        GC_MARKED | BGC_MARKED_BY_FGC | MAKE_FREE_OBJ_IN_COMPACT;
#else
    private const nuint ALLOWED_SPECIAL_HEADER_BITS = GC_MARKED;
#endif

#if TARGET_64BIT
    private const nuint SPECIAL_HEADER_BITS = 0x7;
#else
    private const nuint SPECIAL_HEADER_BITS = 0x3;
#endif

    private MethodTable* m_pEEType;

    public MethodTable* RawGetMethodTable() => m_pEEType;

    public void RawSetMethodTable(MethodTable* methodTable)
    {
        m_pEEType = methodTable;
    }

    public MethodTable* GetMethodTable() =>
        (MethodTable*)((nuint)RawGetMethodTable() & ~SPECIAL_HEADER_BITS);

    public void SetMarked()
    {
        Debug.Assert(RawGetMethodTable() is not null);
        RawSetMethodTable((MethodTable*)((nuint)RawGetMethodTable() | GC_MARKED));
    }

    public int IsMarked() => ((nuint)RawGetMethodTable() & GC_MARKED) != 0 ? 1 : 0;

    public void ClearMarked()
    {
#if TARGET_64BIT && !TARGET_WASM
        RawSetMethodTable((MethodTable*)((nuint)RawGetMethodTable() & ~GC_MARKED));
#else
        RawSetMethodTable(GetMethodTable());
#endif
    }

#if TARGET_64BIT && !TARGET_WASM
    public void SetBGCMarkBit()
    {
        RawSetMethodTable((MethodTable*)((nuint)RawGetMethodTable() | BGC_MARKED_BY_FGC));
    }

    public int IsBGCMarkBitSet() =>
        ((nuint)RawGetMethodTable() & BGC_MARKED_BY_FGC) != 0 ? 1 : 0;

    public void ClearBGCMarkBit()
    {
        RawSetMethodTable((MethodTable*)((nuint)RawGetMethodTable() & ~BGC_MARKED_BY_FGC));
    }

    public void SetFreeObjInCompactBit()
    {
        RawSetMethodTable((MethodTable*)((nuint)RawGetMethodTable() | MAKE_FREE_OBJ_IN_COMPACT));
    }

    public int IsFreeObjInCompactBitSet() =>
        ((nuint)RawGetMethodTable() & MAKE_FREE_OBJ_IN_COMPACT) != 0 ? 1 : 0;

    public void ClearFreeObjInCompactBit()
    {
        RawSetMethodTable((MethodTable*)((nuint)RawGetMethodTable() & ~MAKE_FREE_OBJ_IN_COMPACT));
    }
#endif

    public nuint ClearSpecialBits()
    {
        nuint special_bits = (nuint)RawGetMethodTable() & SPECIAL_HEADER_BITS;
        if (special_bits != 0)
        {
            Debug.Assert((special_bits & ~ALLOWED_SPECIAL_HEADER_BITS) == 0);
            RawSetMethodTable((MethodTable*)((nuint)RawGetMethodTable() & ~SPECIAL_HEADER_BITS));
        }

        return special_bits;
    }

    public void SetSpecialBits(nuint special_bits)
    {
        Debug.Assert((special_bits & ~ALLOWED_SPECIAL_HEADER_BITS) == 0);
        if (special_bits != 0)
        {
            RawSetMethodTable((MethodTable*)((nuint)RawGetMethodTable() | special_bits));
        }
    }

    public int ContainsGCPointers() => GetMethodTable()->ContainsGCPointers();

    public int ContainsGCPointersOrCollectible() =>
        GetMethodTable()->ContainsGCPointersOrCollectible();

    public static uint GetNumComponents(CObjectHeader* header) =>
        *(uint*)((byte*)header + sizeof(nuint));
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct mark_queue_t
{
#if USE_REGIONS
    private const int slot_count = 16;

    [InlineArray(slot_count)]
    private struct slot_table_t
    {
        private nuint _element0;
    }

    private slot_table_t slot_table;
    private nuint curr_slot_index;
#else
    private byte unused;
#endif

    public static void initialize(mark_queue_t* queue)
    {
#if USE_REGIONS
        *queue = default;
#else
        _ = queue;
#endif
    }

    public byte* queue_mark(byte* o)
    {
#if USE_REGIONS
        // The native Prefetch(o) is a performance hint with no cross-platform managed
        // equivalent; the queue's storage and marking transition remain unchanged.
        nuint slot_index = curr_slot_index;
        byte* old_o = (byte*)slot_table[(int)slot_index];
        slot_table[(int)slot_index] = (nuint)o;

        curr_slot_index = (slot_index + 1) % slot_count;
        if (old_o is null)
        {
            return null;
        }
#else
        _ = unused;
        byte* old_o = o;
#endif

        CObjectHeader* header = (CObjectHeader*)old_o;
        if (header->IsMarked() != 0)
        {
            return null;
        }

        header->SetMarked();
        return old_o;
    }

#if USE_REGIONS
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
#endif

    public byte* get_next_marked()
    {
#if USE_REGIONS
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
#else
        _ = unused;
#endif
        return null;
    }

    public void verify_empty()
    {
#if USE_REGIONS
        for (nuint slot_index = 0; slot_index < slot_count; slot_index++)
        {
            Debug.Assert(slot_table[(int)slot_index] == 0);
        }
#else
        _ = unused;
#endif
    }

}

internal unsafe partial struct gc_heap
{
    public const nuint stolen = 2;
    public const nuint partial = 1;
    public const nuint partial_object = 3;

#if USE_REGIONS && DEBUG
    // The WKS global promoted-byte recording is diagnostic-only when regions are enabled.
    // Release accounting is held exclusively in survived_per_region.
    public static nuint g_promoted;
#endif

    public static nuint min_pre_pin_obj_size =>
        (nuint)sizeof(gap_reloc_pair) + (nuint)GCInterfaceOffsets.min_obj_size;

    public static nuint clear_special_bits(byte* node)
    {
        return ((CObjectHeader*)node)->ClearSpecialBits();
    }

    public static void set_special_bits(byte* node, nuint special_bits)
    {
        ((CObjectHeader*)node)->SetSpecialBits(special_bits);
    }

    public static MethodTable* method_table(byte* o)
    {
        return ((CObjectHeader*)o)->GetMethodTable();
    }

    public static int contain_pointers(byte* o)
    {
        return ((CObjectHeader*)o)->ContainsGCPointers();
    }

    public static int contain_pointers_or_collectible(byte* o)
    {
        return ((CObjectHeader*)o)->ContainsGCPointersOrCollectible();
    }

    public static nuint size(byte* o)
    {
        MethodTable* mt = method_table(o);
        nuint component_size = mt->HasComponentSize() != 0
            ? (nuint)CObjectHeader.GetNumComponents((CObjectHeader*)o) * mt->RawGetComponentSize()
            : 0;

        return mt->GetBaseSize() + component_size;
    }

    public static bool is_in_heap_range(byte* o)
    {
        return o >= GCCommon.g_gc_lowest_address && o < GCCommon.g_gc_highest_address;
    }

    public static byte* ref_from_slot(byte* r)
    {
        return (byte*)((nuint)r & ~(stolen | partial));
    }

    public static int stolen_p(byte* r)
    {
        return (((nuint)r & stolen) != 0 && ((nuint)r & partial) == 0) ? 1 : 0;
    }

    public static int partial_p(byte* r)
    {
        return (((nuint)r & partial) != 0 && ((nuint)r & stolen) == 0) ? 1 : 0;
    }

    public static int straight_ref_p(byte* r)
    {
        return stolen_p(r) == 0 && partial_p(r) == 0 ? 1 : 0;
    }

    public static int partial_object_p(byte* r)
    {
        return ((nuint)r & partial_object) == partial_object ? 1 : 0;
    }

    public static int ref_p(byte* r)
    {
        return straight_ref_p(r) != 0 || partial_object_p(r) != 0 ? 1 : 0;
    }

    public static void record_mark_stack_overflow(gc_heap* heap, byte* o)
    {
        _ = heap;
        if (o < min_overflow_address)
        {
            min_overflow_address = o;
        }

        if (o > max_overflow_address)
        {
            max_overflow_address = o;
        }
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

    // This is the go_through_object_nostart expansion used by the short pinned-object paths.
    // The callback is a managed function pointer, so the collector calls another managed leaf
    // directly without introducing a delegate allocation or a reverse P/Invoke transition.
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

    // SHORT_PLUGS is unconditionally defined in gcpriv.h.
    public static void clear_plug_padded(byte* node)
    {
        ((CObjectHeader*)node)->ClearMarked();
    }

    public static void set_plug_padded(byte* node)
    {
        ((CObjectHeader*)node)->SetMarked();
    }

    public static int is_plug_padded(byte* node)
    {
        return ((CObjectHeader*)node)->IsMarked();
    }

#if BACKGROUND_GC
    public static int is_mark_bit_set(byte* add)
    {
        return unchecked((int)(
            mark_array[(nint)card_table_info.mark_word_of(add)]
            & (1u << (int)card_table_info.mark_bit_bit_of(add))));
    }

    private static void mark_array_clear_marked(byte* add)
    {
        nuint index = card_table_info.mark_word_of(add);
        uint val = 1u << (int)card_table_info.mark_bit_bit_of(add);
        mark_array[(nint)index] &= ~val;
    }

    // end must be page aligned addresses.
    public static void clear_mark_array(byte* from, byte* end)
    {
        Debug.Assert(gc_can_use_concurrent);
        Debug.Assert(end == card_table_info.align_on_mark_word(end));

        byte* current_lowest_address = background_saved_lowest_address;
        byte* current_highest_address = background_saved_highest_address;

        // There is a possibility of the addresses to be outside of the covered range because
        // of a newly allocated large object segment.
        if ((end <= current_highest_address) && (from >= current_lowest_address))
        {
            nuint beg_word = card_table_info.mark_word_of(card_table_info.align_on_mark_word(from));
            nuint end_word = card_table_info.mark_word_of(card_table_info.align_on_mark_word(end));

            byte* op = from;
            while (op < card_table_info.mark_bit_address(beg_word * card_table_info.mark_word_width))
            {
                mark_array_clear_marked(op);
                op += (nint)card_table_info.mark_bit_pitch;
            }

            GCCommon.MemSet(
                (byte*)&mark_array[(nint)beg_word],
                0,
                (end_word - beg_word) * (nuint)sizeof(uint));

#if DEBUG
            // Beware, it is assumed that the mark array word straddling start has been cleared before.
            // Verify that the array is empty.
            nuint markw = card_table_info.mark_word_of(card_table_info.align_on_mark_word(from));
            nuint markw_end = card_table_info.mark_word_of(card_table_info.align_on_mark_word(end));
            while (markw < markw_end)
            {
                Debug.Assert(mark_array[(nint)markw] == 0);
                markw++;
            }

            byte* p = card_table_info.mark_bit_address(markw_end * card_table_info.mark_word_width);
            while (p < end)
            {
                Debug.Assert(is_mark_bit_set(p) == 0);
                p++;
            }
#endif
        }
    }
#endif

    public static void initialize_mark_phase_state()
    {
        fixed (mark_queue_t* queue = &mark_queue)
        {
            mark_queue_t.initialize(queue);
        }
        reset_mark_stack(null);
#if USE_REGIONS && !MULTIPLE_HEAPS
        mark_list = null;
        mark_list_index = null;
        mark_list_end = null;
        survived_per_region = null;
        old_card_survived_per_region = null;
        shigh = null;
        slow = (byte*)nuint.MaxValue;
#endif
    }

#if USE_REGIONS && !MULTIPLE_HEAPS
    // The global mark-list allocation and its g_mark_list_piece backing are not ported yet.
    // This setup consumes externally owned storage with the same WKS pointer layout, so no
    // collection path can accidentally invent ownership or allocate while the world is stopped.
    public static bool setup_mark_state_for_collection(
        byte** mark_list_storage,
        nuint mark_list_size,
        nuint* survived_per_region_storage,
        nuint* old_card_survived_per_region_storage,
        nuint region_count)
    {
        mark_queue.verify_empty();

        if (mark_list_storage is null || mark_list_size == 0)
        {
            initialize_mark_phase_state();
            Debug.Assert(
                mark_list is null &&
                mark_list_index is null &&
                mark_list_end is null &&
                survived_per_region is null &&
                old_card_survived_per_region is null);
            return false;
        }

        mark_list = mark_list_storage;
        mark_list_index = mark_list_storage;
        mark_list_end = settings.condemned_generation < GCInterfaceOffsets.max_generation
            ? mark_list_storage + (nint)(mark_list_size - 1)
            : mark_list_storage;

        if (survived_per_region_storage is not null &&
            old_card_survived_per_region_storage is not null &&
            region_count <= nuint.MaxValue / (nuint)sizeof(nuint))
        {
            survived_per_region = survived_per_region_storage;
            old_card_survived_per_region = old_card_survived_per_region_storage;
            nuint bytes = unchecked(region_count * (nuint)sizeof(nuint));
            GCCommon.MemSet((byte*)survived_per_region, 0, bytes);
            GCCommon.MemSet((byte*)old_card_survived_per_region, 0, bytes);
        }
        else
        {
            survived_per_region = null;
            old_card_survived_per_region = null;
        }

        shigh = null;
        slow = (byte*)nuint.MaxValue;
        return true;
    }

    // The WKS USE_REGIONS m_boundary macro owns a fixed-capacity list. Unlike the
    // GC_CONFIG_DRIVEN server branch, exhaustion leaves its cursor one past the final entry.
    public static void m_boundary(gc_heap* heap, byte* o)
    {
        _ = heap;
        if (mark_list_index <= mark_list_end)
        {
            *mark_list_index = o;
            mark_list_index++;
        }

        if (slow > o)
        {
            slow = o;
        }

        if (shigh < o)
        {
            shigh = o;
        }
    }

    // Full collections do not use the mark list, but WKS still tracks the marked-address range.
    public static void m_boundary_fullgc(gc_heap* heap, byte* o)
    {
        _ = heap;
        if (slow > o)
        {
            slow = o;
        }

        if (shigh < o)
        {
            shigh = o;
        }
    }

#if DEBUG
    public static void init_promoted_bytes()
    {
        g_promoted = 0;
    }

    public static nuint promoted_bytes(int thread)
    {
        _ = thread;
        return g_promoted;
    }
#endif

    public static void add_to_promoted_bytes(gc_heap* heap, byte* obj, int thread)
    {
        add_to_promoted_bytes(heap, obj, size(obj), thread);
    }

    public static void add_to_promoted_bytes(gc_heap* heap, byte* obj, nuint obj_size, int thread)
    {
        Debug.Assert(thread == heap->heap_number);

        if (survived_per_region is not null)
        {
            nuint region_index = get_basic_region_index_for_address(obj);
            survived_per_region[(nint)region_index] =
                unchecked(survived_per_region[(nint)region_index] + obj_size);
        }

#if DEBUG
        g_promoted = unchecked(g_promoted + obj_size);
#endif
    }
#endif

    public static int gc_mark1(byte* o)
    {
        int marked = ((CObjectHeader*)o)->IsMarked() == 0 ? 1 : 0;
        ((CObjectHeader*)o)->SetMarked();

#if DEBUG && USE_REGIONS
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
#if USE_REGIONS
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
#else
        Debug.Assert(condemned_gen == -1);

        int marked = 0;
        if ((o >= low) && (o < high))
        {
            marked = gc_mark1(o);
        }

        return marked;
#endif
    }

    private const uint CORINFO_EXCEPTION_GC = 0xE0004743;

    private unsafe struct short_plug_context
    {
        public mark* m;
        public byte* plug;
        public int pre_p;
    }

    private static void set_short_plug_bit(byte** pval, void* context)
    {
        short_plug_context* short_context = (short_plug_context*)context;
        nuint gap_offset = unchecked(
            ((nuint)pval - ((nuint)short_context->plug - (nuint)sizeof(gap_reloc_pair) - plug_skew))
            / (nuint)sizeof(byte*));

        if (short_context->pre_p != 0)
        {
            mark.set_pre_short_bit(short_context->m, gap_offset);
        }
        else
        {
            mark.set_post_short_bit(short_context->m, gap_offset);
        }
    }

    // This starts a plug. But mark_stack_tos isn't increased until set_pinned_info is called.
    public static void enque_pinned_plug(
        gc_heap* heap,
        byte* plug,
        int save_pre_plug_info_p,
        byte* last_object_in_last_plug)
    {
        _ = heap;
        if (mark_stack_array_length <= mark_stack_tos)
        {
            if (grow_mark_stack(
                    ref mark_stack_array,
                    ref mark_stack_array_length,
                    gc_rand.MARK_STACK_INITIAL_LENGTH) == 0)
            {
                // Continuing after this failure risks corrupting the mark stack.
                GCToEEInterface.HandleFatalError(CORINFO_EXCEPTION_GC);
            }
        }

        mark* m = &mark_stack_array[mark_stack_tos];
        m->first = plug;
        m->saved_pre_p = save_pre_plug_info_p;

        if (save_pre_plug_info_p != 0)
        {
            nuint special_bits = clear_special_bits(last_object_in_last_plug);
            m->saved_pre_plug = *(gap_reloc_pair*)(((plug_and_gap*)plug) - 1);
            set_special_bits(last_object_in_last_plug, special_bits);

            m->saved_pre_plug_reloc = *(gap_reloc_pair*)(((plug_and_gap*)plug) - 1);

            nuint last_obj_size = (nuint)(plug - last_object_in_last_plug);
            if (last_obj_size < min_pre_pin_obj_size)
            {
                // Runtime.ManagedGC does not compile mark_phase.cpp, and System.Private.GC
                // does not define GC_CONFIG_DRIVEN, so the native diagnostic counter updates
                // are compiled out for this managed collector.
                mark.set_pre_short(m);

                if (contain_pointers(last_object_in_last_plug) != 0)
                {
                    short_plug_context context = new()
                    {
                        m = m,
                        plug = plug,
                        pre_p = 1,
                    };
                    go_through_object_nostart(
                        method_table(last_object_in_last_plug),
                        last_object_in_last_plug,
                        last_obj_size,
                        &context,
                        &set_short_plug_bit);
                }
            }
        }

        m->saved_post_p = 0;
    }

    public static void save_post_plug_info(
        gc_heap* heap,
        byte* last_pinned_plug,
        byte* last_object_in_last_plug,
        byte* post_plug)
    {
        _ = heap;
        mark* m = &mark_stack_array[mark_stack_tos - 1];
        Debug.Assert(last_pinned_plug == m->first);
        m->saved_post_plug_info_start = (byte*)(((plug_and_gap*)post_plug) - 1);

        nuint special_bits = clear_special_bits(last_object_in_last_plug);
        m->saved_post_plug = *(gap_reloc_pair*)m->saved_post_plug_info_start;
        set_special_bits(last_object_in_last_plug, special_bits);

        m->saved_post_plug_reloc = *(gap_reloc_pair*)m->saved_post_plug_info_start;
        m->saved_post_p = 1;

#if DEBUG
        m->saved_post_plug_debug.gap = 1;
#endif

        nuint last_obj_size = (nuint)(post_plug - last_object_in_last_plug);
        if (last_obj_size < min_pre_pin_obj_size)
        {
            mark.set_post_short(m);

            if (contain_pointers(last_object_in_last_plug) != 0)
            {
                short_plug_context context = new()
                {
                    m = m,
                    plug = post_plug,
                    pre_p = 0,
                };
                go_through_object_nostart(
                    method_table(last_object_in_last_plug),
                    last_object_in_last_plug,
                    last_obj_size,
                    &context,
                    &set_short_plug_bit);
            }
        }
    }

    public static void reset_pinned_queue(gc_heap* heap)
    {
        _ = heap;
        mark_stack_tos = 0;
        mark_stack_bos = 0;
    }

    public static void reset_pinned_queue_bos(gc_heap* heap)
    {
        _ = heap;
        mark_stack_bos = 0;
    }

    // last_pinned_plug is only for asserting purpose.
    public static void merge_with_last_pinned_plug(gc_heap* heap, byte* last_pinned_plug, nuint plug_size)
    {
        _ = heap;
        if (last_pinned_plug is not null)
        {
            mark* last_m = &mark_stack_array[mark_stack_tos - 1];
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
        _ = heap;
        mark* m = &mark_stack_array[mark_stack_tos];
        Debug.Assert(last_pinned_plug == m->first);

        m->len = plug_len;
        mark_stack_tos++;
        Debug.Assert(gen is not null);
        // Why are we checking here? gen is never 0.
        if (gen is not null)
        {
            set_allocator_next_pin(heap, gen);
        }
    }

    public static nuint deque_pinned_plug(gc_heap* heap)
    {
        _ = heap;
        nuint m = mark_stack_bos;
        mark_stack_bos++;
        return m;
    }

    public static mark* before_oldest_pin(gc_heap* heap)
    {
        _ = heap;
        if (mark_stack_bos >= 1)
        {
            return pinned_plug_of(null, mark_stack_bos - 1);
        }
        else
        {
            return null;
        }
    }

    public static void make_mark_stack(gc_heap* heap, mark* arr)
    {
        reset_pinned_queue(heap);
        mark_stack_array = arr;
        mark_stack_array_length = gc_rand.MARK_STACK_INITIAL_LENGTH;
    }

    public static int grow_mark_stack(ref mark* m, ref nuint len, nuint init_len)
    {
        if (len > nuint.MaxValue / 2)
        {
            return 0;
        }

        nuint new_size = 2 * len;
        if (new_size < init_len)
        {
            new_size = init_len;
        }

        if (new_size > nuint.MaxValue / (nuint)sizeof(mark))
        {
            return 0;
        }

        nuint bytes = new_size * (nuint)sizeof(mark);
        if (bytes > unchecked((nuint)long.MaxValue))
        {
            return 0;
        }

        mark* tmp = (mark*)SyncImports.ManagedGC_AllocZeroed(bytes);
        if (tmp is not null)
        {
            nuint bytes_to_copy = len * (nuint)sizeof(mark);
            if (bytes_to_copy != 0)
            {
                Buffer.MemoryCopy(m, tmp, (long)bytes, (long)bytes_to_copy);
            }

            if (m is not null)
            {
                SyncImports.ManagedGC_Free(m);
            }

            m = tmp;
            len = new_size;
            return 1;
        }
        else
        {
            return 0;
        }
    }

    public static void reset_mark_stack(gc_heap* heap)
    {
        reset_pinned_queue(heap);
        max_overflow_address = null;
        min_overflow_address = (byte*)nuint.MaxValue;
    }

    public static mark* pinned_plug_of(gc_heap* heap, nuint bos)
    {
        _ = heap;
        return &mark_stack_array[bos];
    }

    public static mark* oldest_pin(gc_heap* heap)
    {
        _ = heap;
        return pinned_plug_of(null, mark_stack_bos);
    }

    public static int pinned_plug_que_empty_p(gc_heap* heap)
    {
        _ = heap;
        return mark_stack_bos == mark_stack_tos ? 1 : 0;
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
        _ = heap;
        oldest_pinned_plug = pinned_plug_que_empty_p(null) != 0 ? null : pinned_plug(oldest_pin(null));
    }
}
