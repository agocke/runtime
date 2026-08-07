// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from the dependency-free data records of src/coreclr/gc/gcpriv.h, the non-FEATURE_STRUCTALIGN
// node-child writers of gcinternal.h, and the adjacent dependency-closed helpers of
// region_allocator.cpp and region_free_list.cpp.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using unsafe region_allocator_callback_fn = delegate*<byte*, byte>;

namespace Internal.Runtime.GarbageCollection
{
#pragma warning disable CS8981 // Native type names are intentionally preserved.
    internal static class gc_rand
#pragma warning restore CS8981
    {
        public const uint MAX_YP_SPIN_COUNT_UNIT = 32768;
        public const uint MIN_SOH_CROSS_GEN_REFS = 400;
        public const uint MIN_LOH_CROSS_GEN_REFS = 800;
        public static nuint MIN_DECOMMIT_SIZE => unchecked((nuint)100 * GCToOSInterface.GetPageSize());
#if TARGET_64BIT
        public const uint MARK_STACK_INITIAL_LENGTH = 1024;
#else
        public const uint MARK_STACK_INITIAL_LENGTH = 128;
#endif

        public static ulong x;

        public static ulong get_rand()
        {
            x = unchecked((314159269 * x + 278281) & 0x7FFFFFFF);
            return x;
        }

        public static ulong get_rand(ulong r)
        {
            return unchecked(get_rand() * r) >> 31;
        }
    }

#pragma warning disable CS8981 // Native type names are intentionally preserved.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct bk
#pragma warning restore CS8981
    {
        public byte* add;
        public nuint val;
    }

#pragma warning disable CS8981 // Native type names are intentionally preserved.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct sorted_table
#pragma warning restore CS8981
    {
        private nint size;
        private nint count;
        private bk* slots;
        private bk* old_slots;

        public static bk* buckets(sorted_table* table)
        {
            return table->slots + 1;
        }

        public static ref byte* last_slot(bk* array)
        {
            return ref array[0].add;
        }

        public static void initialize(sorted_table* table, nint initialSize, bk* initialSlots)
        {
            table->size = initialSize;
            table->slots = initialSlots;
            table->old_slots = null;
            last_slot(initialSlots) = null;
            clear(table);
        }

        public static void clear(sorted_table* table)
        {
            table->count = 1;
            buckets(table)[0].add = (byte*)nuint.MaxValue;
        }

        public static sorted_table* make_sorted_table()
        {
            nint size = 400;

            sorted_table* result = (sorted_table*)SyncImports.ManagedGC_AllocZeroed(
                (nuint)sizeof(sorted_table) + (nuint)(size + 1) * (nuint)sizeof(bk));
            if (result is null)
            {
                return null;
            }

            initialize(result, size, (bk*)(result + 1));
            return result;
        }

        public static void delete_sorted_table(sorted_table* table)
        {
            if (table->slots != (bk*)(table + 1))
            {
                SyncImports.ManagedGC_Free(table->slots);
            }

            delete_old_slots(table);
        }

        public static void delete_old_slots(sorted_table* table)
        {
            byte* slots = (byte*)table->old_slots;
            while (slots is not null)
            {
                byte* slotsToDelete = slots;
                slots = last_slot((bk*)slots);
                SyncImports.ManagedGC_Free(slotsToDelete);
            }

            table->old_slots = null;
        }

        public static void enqueue_old_slot(sorted_table* table, bk* slots)
        {
            last_slot(slots) = (byte*)table->old_slots;
            table->old_slots = slots;
        }

        public static nuint lookup(sorted_table* table, ref byte* add)
        {
            nint high = table->count - 1;
            nint low = 0;
            bk* bucket = buckets(table);

            while (low <= high)
            {
                nint mid = (low + high) / 2;
                nint index = mid;
                if (bucket[index].add > add)
                {
                    if (index > 0 && bucket[index - 1].add <= add)
                    {
                        add = bucket[index - 1].add;
                        return bucket[index - 1].val;
                    }

                    high = mid - 1;
                }
                else
                {
                    if (bucket[index + 1].add > add)
                    {
                        add = bucket[index].add;
                        return bucket[index].val;
                    }

                    low = mid + 1;
                }
            }

            add = null;
            return 0;
        }

        public static int ensure_space_for_insert(sorted_table* table)
        {
            if (table->count == table->size)
            {
                table->size = (table->size * 3) / 2;
                Debug.Assert((nuint)table->size * (nuint)sizeof(bk) > 0);
                bk* resizedSlots = (bk*)SyncImports.ManagedGC_AllocZeroed(
                    (nuint)(table->size + 1) * (nuint)sizeof(bk));
                Debug.Assert(resizedSlots is not null);
                if (resizedSlots is null)
                {
                    return 0;
                }

                last_slot(resizedSlots) = null;
                nuint bytesToCopy = (nuint)table->count * (nuint)sizeof(bk);
                Buffer.MemoryCopy(buckets(table), resizedSlots + 1, (long)bytesToCopy, (long)bytesToCopy);
                bk* lastOldSlots = table->slots;
                table->slots = resizedSlots;
                if (lastOldSlots != (bk*)(table + 1))
                {
                    enqueue_old_slot(table, lastOldSlots);
                }
            }

            return 1;
        }

        public static int insert(sorted_table* table, byte* add, nuint val)
        {
            Debug.Assert(table->count < table->size);

            nint high = table->count - 1;
            nint low = 0;
            bk* bucket = buckets(table);

            while (low <= high)
            {
                nint mid = (low + high) / 2;
                nint index = mid;
                if (bucket[index].add > add)
                {
                    if (index == 0 || bucket[index - 1].add <= add)
                    {
                        for (nint current = table->count; current > index; current--)
                        {
                            bucket[current] = bucket[current - 1];
                        }

                        bucket[index].add = add;
                        bucket[index].val = val;
                        table->count++;
                        return 1;
                    }

                    high = mid - 1;
                }
                else
                {
                    if (bucket[index + 1].add > add)
                    {
                        for (nint current = table->count; current > index + 1; current--)
                        {
                            bucket[current] = bucket[current - 1];
                        }

                        bucket[index + 1].add = add;
                        bucket[index + 1].val = val;
                        table->count++;
                        return 1;
                    }

                    low = mid + 1;
                }
            }

            Debug.Fail("No sorted table insertion point found.");
            return 1;
        }

        public static void remove(sorted_table* table, byte* add)
        {
            nint high = table->count - 1;
            nint low = 0;
            bk* bucket = buckets(table);

            while (low <= high)
            {
                nint mid = (low + high) / 2;
                nint index = mid;
                if (bucket[index].add > add)
                {
                    if (bucket[index - 1].add <= add)
                    {
                        for (nint current = index; current < table->count; current++)
                        {
                            bucket[current - 1] = bucket[current];
                        }

                        table->count--;
                        return;
                    }

                    high = mid - 1;
                }
                else
                {
                    if (bucket[index + 1].add > add)
                    {
                        for (nint current = index + 1; current < table->count; current++)
                        {
                            bucket[current - 1] = bucket[current];
                        }

                        table->count--;
                        return;
                    }

                    low = mid + 1;
                }
            }

            Debug.Fail("No sorted table entry found.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct static_data
    {
        public nuint min_size;
        public nuint max_size;
        public nuint fragmentation_limit;
        public float fragmentation_burden_limit;
        public float limit;
        public float max_limit;
        public ulong time_clock;
        public nuint gc_clock;
    }

    // Dynamic data is maintained per generation. The native class groups its fields into
    // calculated logical data, physical data, and the const data it reads through sdata; it has no
    // constructor, so zero initialization matches the native default. All fields are public in the
    // C++ class, and every native accessor hands out a reference into the instance, so they are
    // translated as static ref-returning helpers taking a dynamic_data* -- mirroring the native
    // reference-return API without introducing a managed reference to collector state.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct dynamic_data
    {
        public nint new_allocation;
        public nint gc_new_allocation;
        public float surv;
        public nuint desired_allocation;
        public nuint begin_data_size;
        public nuint survived_size;
        public nuint pinned_survived_size;
        public nuint artificial_pinned_survived_size;
        public nuint added_pinned_size;
        // SHORT_PLUGS is defined unconditionally in gcpriv.h.
        public nuint padding_size;
#if TARGET_ARM || TARGET_WASM
        // RESPECT_LARGE_ALIGNMENT || FEATURE_STRUCTALIGN. RESPECT_LARGE_ALIGNMENT tracks the GC's
        // FEATURE_64BIT_ALIGNMENT, which gcenv.object.h defines for TARGET_ARM and TARGET_WASM;
        // FEATURE_STRUCTALIGN is never defined in this codebase.
        public nuint num_npinned_plugs;
#endif
        public nuint current_size;
        public nuint collection_count;
        public nuint promoted_size;
        public nuint freach_previous_promotion;
        public nuint fragmentation;
        public nuint gc_clock;
        public ulong time_clock;
        public ulong previous_time_clock;
        public nuint gc_elapsed_time;
        public nuint min_size;
        public static_data* sdata;

        public static ref nuint dd_begin_data_size(dynamic_data* inst) => ref inst->begin_data_size;

        public static ref nuint dd_survived_size(dynamic_data* inst) => ref inst->survived_size;

#if TARGET_ARM || TARGET_WASM
        public static ref nuint dd_num_npinned_plugs(dynamic_data* inst) => ref inst->num_npinned_plugs;
#endif

        public static ref nuint dd_pinned_survived_size(dynamic_data* inst) => ref inst->pinned_survived_size;

        public static ref nuint dd_added_pinned_size(dynamic_data* inst) => ref inst->added_pinned_size;

        public static ref nuint dd_artificial_pinned_survived_size(dynamic_data* inst) => ref inst->artificial_pinned_survived_size;

        public static ref nuint dd_padding_size(dynamic_data* inst) => ref inst->padding_size;

        public static ref nuint dd_current_size(dynamic_data* inst) => ref inst->current_size;

        public static ref float dd_surv(dynamic_data* inst) => ref inst->surv;

        public static ref nuint dd_freach_previous_promotion(dynamic_data* inst) => ref inst->freach_previous_promotion;

        public static ref nuint dd_desired_allocation(dynamic_data* inst) => ref inst->desired_allocation;

        public static ref nuint dd_collection_count(dynamic_data* inst) => ref inst->collection_count;

        public static ref nuint dd_promoted_size(dynamic_data* inst) => ref inst->promoted_size;

        public static ref float dd_limit(dynamic_data* inst) => ref inst->sdata->limit;

        public static ref float dd_max_limit(dynamic_data* inst) => ref inst->sdata->max_limit;

        public static ref nuint dd_max_size(dynamic_data* inst) => ref inst->sdata->max_size;

        public static ref nuint dd_min_size(dynamic_data* inst) => ref inst->min_size;

        public static ref nint dd_new_allocation(dynamic_data* inst) => ref inst->new_allocation;

        public static ref nint dd_gc_new_allocation(dynamic_data* inst) => ref inst->gc_new_allocation;

        public static ref nuint dd_fragmentation_limit(dynamic_data* inst) => ref inst->sdata->fragmentation_limit;

        public static ref float dd_fragmentation_burden_limit(dynamic_data* inst) => ref inst->sdata->fragmentation_burden_limit;

        public static float dd_v_fragmentation_burden_limit(dynamic_data* inst)
        {
            float doubled = 2f * dd_fragmentation_burden_limit(inst);
            return 0.75f < doubled ? 0.75f : doubled;
        }

        public static ref nuint dd_fragmentation(dynamic_data* inst) => ref inst->fragmentation;

        public static ref nuint dd_gc_clock(dynamic_data* inst) => ref inst->gc_clock;

        public static ref ulong dd_time_clock(dynamic_data* inst) => ref inst->time_clock;

        public static ref ulong dd_previous_time_clock(dynamic_data* inst) => ref inst->previous_time_clock;

        public static ref nuint dd_gc_clock_interval(dynamic_data* inst) => ref inst->sdata->gc_clock;

        public static ref ulong dd_time_clock_interval(dynamic_data* inst) => ref inst->sdata->time_clock;

        public static ref nuint dd_gc_elapsed_time(dynamic_data* inst) => ref inst->gc_elapsed_time;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct recorded_generation_info
    {
        public nuint size_before;
        public nuint fragmentation_before;
        public nuint size_after;
        public nuint fragmentation_after;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct last_recorded_gc_info
    {
        // Native VOLATILE(size_t); access this through GCEnv's volatile helpers.
        public nuint index;
        public nuint total_committed;
        public nuint promoted;
        public nuint pinned_objects;
        public nuint finalize_promoted_objects;
        public nuint pause_durations0;
        public nuint pause_durations1;
        public float pause_percentage;
        public recorded_generation_info gen_info0;
        public recorded_generation_info gen_info1;
        public recorded_generation_info gen_info2;
        public recorded_generation_info gen_info3;
        public recorded_generation_info gen_info4;
        public nuint heap_size;
        public nuint fragmentation;
        public uint memory_load;
        public byte condemned_generation;
        public byte compaction;
        public byte concurrent;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct etw_opt_info
    {
        public nuint desired_allocation;
        public nuint new_allocation;
        public int gen_number;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct alloc_list
    {
#if TARGET_64BIT && !TARGET_WASM
        private byte* added_head;
        private byte* added_tail;
#endif

        private byte* head;
        private byte* tail;
        private nuint damage_count;

#if TARGET_64BIT && !TARGET_WASM
        public static ref byte* added_alloc_list_head(alloc_list* list) => ref list->added_head;

        public static ref byte* added_alloc_list_tail(alloc_list* list) => ref list->added_tail;
#endif

        public static ref byte* alloc_list_head(alloc_list* list) => ref list->head;

        public static ref byte* alloc_list_tail(alloc_list* list) => ref list->tail;

        public static ref nuint alloc_list_damage_count(alloc_list* list) => ref list->damage_count;
    }

#if !TARGET_WASM
    [StructLayout(LayoutKind.Sequential)]
    internal struct etw_bucket_info
    {
        public ushort index;
        public uint count;
        public nuint size;

        public void set(ushort _index, uint _count, nuint _size)
        {
            index = _index;
            count = _count;
            size = _size;
        }
    }
#endif

    // The free-list allocator of gcpriv.h. Its state is entirely private in the C++ class, so the
    // shared offsets table pins only the size and alignment; the managed tests pin the field order
    // and the accessor behavior directly. Every native member function that hands out a reference
    // into the object is a static ref-returning helper taking an allocator*, mirroring the C++
    // reference-return API without introducing a managed reference to collector state.
#pragma warning disable CS8981 // Native type names are intentionally preserved.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct allocator
#pragma warning restore CS8981
    {
        private int first_bucket_bits;
        private uint num_buckets;
        private alloc_list first_bucket;
        private alloc_list* buckets;
        private int gen_number;

        public allocator(uint num_b, int fbb, alloc_list* b, int gen = -1)
        {
            Debug.Assert(num_b < GCInterfaceOffsets.MAX_BUCKET_COUNT);
            num_buckets = num_b;
            first_bucket_bits = fbb;
            first_bucket = default;
            buckets = b;
            gen_number = gen;
        }

        // C# does not run a struct constructor for embedded or unmanaged storage. Keep the native
        // default-construction semantics explicit so generation initialization cannot accidentally
        // leave a zero-bucket allocator behind.
        public static void initialize(allocator* a)
        {
            a->num_buckets = 1;
            a->first_bucket_bits = sizeof(nuint) * 8 - 1;
            a->first_bucket = default;
            a->buckets = null;
            // for young gens we just set it to 0 since we don't treat
            // them differently from each other
            a->gen_number = 0;
        }

        private static alloc_list* alloc_list_of(allocator* a, uint bn)
        {
            Debug.Assert(bn < a->num_buckets);
            if (bn == 0)
                return &a->first_bucket;
            else
                return &a->buckets[bn - 1];
        }

        public static ref byte* free_list_slot(byte* item) => ref ((byte**)item)[2];

        private static ref byte* free_list_undo(byte* item) => ref ((byte**)item)[-1];

#if TARGET_64BIT && !TARGET_WASM
        private static ref byte* free_list_prev(byte* item) => ref ((byte**)item)[3];
#endif

        public static ref nuint alloc_list_damage_count_of(allocator* a, uint bn)
        {
            Debug.Assert(bn < a->num_buckets);
            if (bn == 0)
                return ref alloc_list.alloc_list_damage_count(&a->first_bucket);
            else
                return ref alloc_list.alloc_list_damage_count(&a->buckets[bn - 1]);
        }

        public uint number_of_buckets()
        {
            return num_buckets;
        }

        // skip buckets that cannot possibly fit "size" and return the next one
        // there is always such bucket since the last one fits everything
        public uint first_suitable_bucket(nuint size)
        {
            // sizes taking first_bucket_bits or less are mapped to bucket 0
            // others are mapped to buckets 0, 1, 2 respectively
            size = (size >> first_bucket_bits) | 1;

            uint highest_set_bit_index;
#if TARGET_64BIT
            GCEnv.BitScanReverse64(&highest_set_bit_index, size);
#else
            GCEnv.BitScanReverse(&highest_set_bit_index, (uint)size);
#endif

            return (highest_set_bit_index < num_buckets) ? highest_set_bit_index : (num_buckets - 1);
        }

        public nuint first_bucket_size()
        {
            return (nuint)1 << (first_bucket_bits + 1);
        }

        public static ref byte* alloc_list_head_of(allocator* a, uint bn)
        {
            return ref alloc_list.alloc_list_head(alloc_list_of(a, bn));
        }

        public static ref byte* alloc_list_tail_of(allocator* a, uint bn)
        {
            return ref alloc_list.alloc_list_tail(alloc_list_of(a, bn));
        }

#if TARGET_64BIT && !TARGET_WASM
        public static ref byte* added_alloc_list_head_of(allocator* a, uint bn)
        {
            return ref alloc_list.added_alloc_list_head(alloc_list_of(a, bn));
        }

        public static ref byte* added_alloc_list_tail_of(allocator* a, uint bn)
        {
            return ref alloc_list.added_alloc_list_tail(alloc_list_of(a, bn));
        }
#endif

        public static void clear(allocator* a)
        {
            for (uint i = 0; i < a->num_buckets; i++)
            {
                alloc_list_head_of(a, i) = null;
                alloc_list_tail_of(a, i) = null;
            }
        }

        public static void unlink_item(allocator* a, uint bn, byte* item, byte* prev_item, bool use_undo_p)
        {
            alloc_list* al = alloc_list_of(a, bn);
            byte* next_item = free_list_slot(item);

#if TARGET_64BIT && !TARGET_WASM
            bool repair_list = a->discard_if_no_fit_p() == 0;
#endif

            if (prev_item is not null)
            {
                if (use_undo_p && free_list_undo(prev_item) == (byte*)1)
                {
                    Debug.Assert(item == free_list_slot(prev_item));
                    free_list_undo(prev_item) = item;
                    alloc_list_damage_count_of(a, bn)++;
                }

                free_list_slot(prev_item) = next_item;
            }
            else
            {
                alloc_list.alloc_list_head(al) = next_item;
            }

            if (alloc_list.alloc_list_tail(al) == item)
            {
                alloc_list.alloc_list_tail(al) = prev_item;
            }

#if TARGET_64BIT && !TARGET_WASM
            if (repair_list && !use_undo_p)
            {
                free_list_prev(item) = (byte*)1;
            }
#endif

            if (alloc_list.alloc_list_head(al) is null)
            {
                Debug.Assert(alloc_list.alloc_list_tail(al) is null);
            }
        }

        public static void thread_item_front(allocator* a, byte* item, nuint size)
        {
            uint a_l_number = a->first_suitable_bucket(size);
            alloc_list* al = alloc_list_of(a, a_l_number);

            if (alloc_list.alloc_list_head(al) is null)
            {
                Debug.Assert(alloc_list.alloc_list_tail(al) is null);
            }

            free_list_slot(item) = alloc_list.alloc_list_head(al);
            free_list_undo(item) = (byte*)1;

            if (alloc_list.alloc_list_tail(al) is null)
            {
                Debug.Assert(alloc_list.alloc_list_head(al) is null);
                alloc_list.alloc_list_tail(al) = alloc_list.alloc_list_head(al);
            }

#if TARGET_64BIT && !TARGET_WASM
            if (a->gen_number == (int)gc_generation_num.max_generation &&
                alloc_list.alloc_list_head(al) is not null)
            {
                free_list_prev(alloc_list.alloc_list_head(al)) = item;
            }
#endif

            alloc_list.alloc_list_head(al) = item;
            if (alloc_list.alloc_list_tail(al) is null)
            {
                alloc_list.alloc_list_tail(al) = item;
            }

#if TARGET_64BIT && !TARGET_WASM
            if (a->gen_number == (int)gc_generation_num.max_generation)
            {
                free_list_prev(item) = null;
            }
#endif
        }

        // always thread to the end.
        public static void thread_item(allocator* a, byte* item, nuint size)
        {
            uint a_l_number = a->first_suitable_bucket(size);
            alloc_list* al = alloc_list_of(a, a_l_number);
            ref byte* head = ref alloc_list.alloc_list_head(al);
            ref byte* tail = ref alloc_list.alloc_list_tail(al);

            if (head is null)
            {
                Debug.Assert(tail is null);
            }

            free_list_slot(item) = null;
            free_list_undo(item) = (byte*)1;
            Debug.Assert(item != head);

#if TARGET_64BIT && !TARGET_WASM
            if (a->gen_number == (int)gc_generation_num.max_generation)
            {
                free_list_prev(item) = tail;
            }
#endif

            if (head is null)
            {
                head = item;
            }
            else
            {
                Debug.Assert(free_list_slot(head) is not null || tail == head);
                Debug.Assert(item != tail);
                Debug.Assert(free_list_slot(tail) is null);

                free_list_slot(tail) = item;
            }

            tail = item;
        }

#if USE_REGIONS
        public static void thread_sip_fl(allocator* a, heap_segment* region)
        {
            byte* region_fl_head = heap_segment.heap_segment_free_list_head(region);
            byte* region_fl_tail = heap_segment.heap_segment_free_list_tail(region);

            if (region_fl_head is null)
            {
                Debug.Assert(region_fl_tail is null);
                Debug.Assert(heap_segment.heap_segment_free_list_size(region) == 0);
                return;
            }

            if (a->num_buckets == 1)
            {
                alloc_list* al = alloc_list_of(a, 0);
                ref byte* head = ref alloc_list.alloc_list_head(al);
                ref byte* tail = ref alloc_list.alloc_list_tail(al);

                if (tail is null)
                {
                    Debug.Assert(head is null);
                    head = region_fl_head;
                }
                else
                {
                    free_list_slot(tail) = region_fl_head;
                }

                tail = region_fl_tail;
            }
            else
            {
                byte* region_fl_item = region_fl_head;
                nuint total_free_size = 0;
                while (region_fl_item is not null)
                {
                    byte* next_fl_item = free_list_slot(region_fl_item);
                    nuint size_item = gc_heap.size(region_fl_item);
                    thread_item(a, region_fl_item, size_item);
                    total_free_size = unchecked(total_free_size + size_item);
                    region_fl_item = next_fl_item;
                }

                Debug.Assert(total_free_size == heap_segment.heap_segment_free_list_size(region));
            }
        }
#endif

        public static void copy_to_alloc_list(allocator* a, alloc_list* destination)
        {
            for (uint i = 0; i < a->num_buckets; i++)
            {
                destination[i] = *alloc_list_of(a, i);
            }
        }

        public static void copy_from_alloc_list(allocator* a, alloc_list* source)
        {
            bool repair_list = a->discard_if_no_fit_p() == 0;
#if TARGET_64BIT && !TARGET_WASM
            bool repair_doubly_linked_list =
                a->gen_number == GCInterfaceOffsets.max_generation;
            uint start_index = repair_doubly_linked_list ? 1u : 0u;
            if (repair_doubly_linked_list)
            {
                Debug.Assert(alloc_list_damage_count_of(a, 0) == 0);
                byte* head = alloc_list_head_of(a, 0);
                if (head is not null)
                {
                    free_list_prev(head) = null;
                }

                added_alloc_list_head_of(a, 0) = null;
                added_alloc_list_tail_of(a, 0) = null;
            }
#else
            uint start_index = 0;
#endif

            for (uint i = start_index; i < a->num_buckets; i++)
            {
                nuint damage_count = alloc_list_damage_count_of(a, i);
                *alloc_list_of(a, i) = source[i];
                Debug.Assert(alloc_list_damage_count_of(a, i) == 0);

                if (repair_list)
                {
                    byte* free_item = alloc_list_head_of(a, i);
                    while (free_item is not null && damage_count != 0)
                    {
                        Debug.Assert(((CObjectHeader*)free_item)->IsFree() != 0);
                        if (free_list_undo(free_item) != (byte*)1)
                        {
                            damage_count--;
                            free_list_slot(free_item) = free_list_undo(free_item);
                            free_list_undo(free_item) = (byte*)1;
                        }

                        free_item = free_list_slot(free_item);
                    }

#if TARGET_64BIT && !TARGET_WASM
                    if (repair_doubly_linked_list)
                    {
                        added_alloc_list_head_of(a, i) = null;
                        added_alloc_list_tail_of(a, i) = null;
                    }
#endif
                }
            }
        }

        public static void commit_alloc_list_changes(allocator* a)
        {
            if (a->discard_if_no_fit_p() != 0)
            {
                return;
            }

            for (uint i = 0; i < a->num_buckets; i++)
            {
                byte* free_item = alloc_list_head_of(a, i);
#if TARGET_64BIT && !TARGET_WASM
                bool repair_doubly_linked_list =
                    a->gen_number == GCInterfaceOffsets.max_generation;
                if (free_item is not null && repair_doubly_linked_list)
                {
                    free_list_prev(free_item) = null;
                }
#endif

                nuint damage_count = alloc_list_damage_count_of(a, i);
                while (free_item is not null && damage_count != 0)
                {
                    Debug.Assert(((CObjectHeader*)free_item)->IsFree() != 0);
                    if (free_list_undo(free_item) != (byte*)1)
                    {
                        free_list_undo(free_item) = (byte*)1;
                        damage_count--;
                    }

#if TARGET_64BIT && !TARGET_WASM
                    if (repair_doubly_linked_list)
                    {
                        byte* next_item = free_list_slot(free_item);
                        if (next_item is not null &&
                            free_list_prev(next_item) != free_item)
                        {
                            free_list_prev(next_item) = free_item;
                        }
                    }
#endif
                    free_item = free_list_slot(free_item);
                }

                alloc_list_damage_count_of(a, i) = 0;
#if TARGET_64BIT && !TARGET_WASM
                if (repair_doubly_linked_list)
                {
                    added_alloc_list_head_of(a, i) = null;
                    added_alloc_list_tail_of(a, i) = null;
                }
#endif
            }
        }

        public int discard_if_no_fit_p()
        {
            return (num_buckets == 1) ? 1 : 0;
        }

#if TARGET_64BIT && !TARGET_WASM
        public bool is_doubly_linked_p()
        {
            return (gen_number == GCInterfaceOffsets.max_generation);
        }
#endif
    }

#if USE_REGIONS
#pragma warning disable CS8981 // Native type names are intentionally preserved.
    internal enum region_info : byte
#pragma warning restore CS8981
    {
        RI_GEN_0 = 0x0,
        RI_GEN_1 = 0x1,
        RI_GEN_2 = 0x2,
        RI_GEN_MASK = 0x3,
        RI_SIP = 0x4,
        RI_DEMOTED = 0x8,
        RI_PLAN_GEN_SHR = 0x6,
        RI_PLAN_GEN_0 = 0x00,
        RI_PLAN_GEN_1 = 0x40,
        RI_PLAN_GEN_2 = 0x80,
        RI_PLAN_GEN_MASK = 0xC0,
    }
#endif

#pragma warning disable CS8981 // Native type names are intentionally preserved.
    internal unsafe partial struct gc_heap
    {
        public const int DESIRED_PLUG_LENGTH = 1000;
        public const int USE_PADDING_FRONT = 1;
        public const int USE_PADDING_TAIL = 2;

        // Returns true if two pointers have the same large (double than normal) alignment.
        public static bool same_large_alignment_p(byte* p1, byte* p2)
        {
#if TARGET_ARM || TARGET_WASM
            const nuint LargeAlignmentMask = 2 * GCEnv.DATA_ALIGNMENT - 1;
            return ((((nuint)p1 ^ (nuint)p2) & LargeAlignmentMask) == 0);
#else
            _ = p1;
            _ = p2;
            return true;
#endif
        }

        // For the WKS bestfit algorithm when relocating ephemeral generations into an existing
        // gen2 segment. Sizes are recorded from 2^6 through 2^28 on 64-bit targets, or 2^24 on
        // 32-bit targets.
        public const int MIN_INDEX_POWER2 = 6;
#if TARGET_64BIT
        public const int MAX_INDEX_POWER2 = 28;
#else
        public const int MAX_INDEX_POWER2 = 24;
#endif
        public const int MAX_NUM_BUCKETS = MAX_INDEX_POWER2 - MIN_INDEX_POWER2 + 1;

#if USE_REGIONS
        [InlineArray(GCInterfaceOffsets.max_generation + 1)]
        internal struct generation_region_count_array
        {
            private int _element0;
        }

        private const int FullGcTypeCount = 3;

        [InlineArray(FullGcTypeCount)]
        internal struct full_gc_count_array
        {
            private nuint _element0;
        }

        internal unsafe struct reserved_region_array
        {
            public heap_segment* element0;
            public heap_segment* element1;
        }

        // Number of blocking GCs for which find_object keeps allocation-time Gen0 bricks current.
        public const int FFIND_DECAY = 7;
        public const nuint DefaultMinSegmentSize = 4 * 1024 * 1024;
        public const nuint MAX_REGION_SIZE = (nuint)1 << 31;
        public static nuint uninitialized_end_gen0_region_space => nuint.MaxValue;

        public static nuint min_segment_size_shr;
        public static region_info* map_region_to_generation;
        public static region_info* map_region_to_generation_skewed;
        public static volatile byte* ephemeral_low;
        public static volatile byte* ephemeral_high;
        public static byte* gc_low;
        public static byte* gc_high;
        public static GCSpinLock gc_lock;
        public static region_free_list global_free_huge_regions;

        // This is the allocation-owned WKS prefix of gc_heap. The remaining native gc_heap
        // fields stay deferred, but these fields have the same ownership as their native
        // counterparts and are sufficient to create an allocation context without external
        // state adapters.
        public generation generation_table0;
        public generation generation_table1;
        public generation generation_table2;
        public generation generation_table3;
        public generation generation_table4;
        public dynamic_data dynamic_data_table0;
        public dynamic_data dynamic_data_table1;
        public dynamic_data dynamic_data_table2;
        public dynamic_data dynamic_data_table3;
        public dynamic_data dynamic_data_table4;
        public ulong total_alloc_bytes_soh;
        public ulong total_alloc_bytes_uoh;
        public byte* alloc_allocated;
        public heap_segment* ephemeral_heap_segment;
        public GCSpinLock more_space_lock_soh;
        public GCSpinLock more_space_lock_uoh;
        public ulong allocation_running_time;
        public nuint allocation_running_amount;
        public nuint allocation_quantum;
        public int heap_number;
#endif

        // WKS makes this collector state static. It remains separate from the allocation-owned
        // instance prefix until the complete native gc_heap layout is translated.
        public static nuint mark_stack_tos;
        public static nuint mark_stack_bos;
        public static byte* oldest_pinned_plug;
        public static nuint num_pinned_objects;
        public static mark_queue_t mark_queue;
        public static CFinalize* finalize_queue;
#if USE_REGIONS
        public static nuint region_count;
#if !MULTIPLE_HEAPS
        // These own the WKS backing. The mark-list cursors below only borrow its storage for
        // one collection.
        public static int gen0_bricks_cleared;
        public static int gen0_must_clear_bricks;
        public static byte** g_mark_list;
        public static byte** g_mark_list_copy;
        public static nuint mark_list_size;
        public static nuint g_mark_list_total_size;
        public static bool mark_list_overflow;
        public static byte*** g_mark_list_piece;
        public static nuint g_mark_list_piece_size;
        public static nuint g_mark_list_piece_total_size;
#endif
        public static byte** mark_list;
        public static byte** mark_list_end;
        public static byte** mark_list_index;
#if !MULTIPLE_HEAPS
        public static byte* shigh;
        public static byte* slow;
#endif
        public static nuint* survived_per_region;
        public static nuint* old_card_survived_per_region;
        public static int num_regions_freed_in_sweep;
        public static nuint end_gen0_region_space;
        public static nuint end_gen0_region_committed_space;
        public static nuint gen0_pinned_free_space;
        public static bool gen0_large_chunk_found;
        public static reserved_region_array reserved_free_regions_sip;
        public static generation_region_count_array regions_per_gen;
        public static generation_region_count_array planned_regions_per_gen;
        public static generation_region_count_array sip_maxgen_regions_per_gen;
        public static bool decide_promote_gen1_pins_p;
        public static bool enable_special_regions_p;
        public static bool special_sweep_p;
        public static nuint maxgen_pinned_compact_before_advance;
        public static int new_gen0_regions_in_plns;
        public static int new_regions_in_prr;
        public static int new_regions_in_threading;

        public static ref heap_segment* reserved_free_region_sip(int generation)
        {
            Debug.Assert(generation >= 0 && generation < GCInterfaceOffsets.max_generation);
            if (generation == 0)
            {
                return ref reserved_free_regions_sip.element0;
            }

            return ref reserved_free_regions_sip.element1;
        }
#endif
        public static nuint mark_stack_array_length;
        public static mark* mark_stack_array;
        public const nuint LOH_PIN_QUEUE_LENGTH = 100;
        public const int LOH_PIN_DECAY = 10;
        public static nuint loh_pinned_queue_tos;
        public static nuint loh_pinned_queue_bos;
        public static nuint loh_pinned_queue_length;
        public static int loh_pinned_queue_decay;
        public static mark* loh_pinned_queue;
        public static byte* min_overflow_address;
        public static byte* max_overflow_address;
        public static nuint alloc_contexts_used;
        public static heap_segment* freeable_uoh_segment;
        public static int sufficient_gen0_space_p;
        public static int conserve_mem_setting;
        public static int loh_compaction_always_p;
        public static gc_loh_compaction_mode loh_compaction_mode;
        public static int loh_compacted_p;
        public const int gc_type_compacting = 0;
        public const int gc_type_blocking = 1;
        public static full_gc_count_array full_gc_counts;
        public static ulong loh_alloc_since_cg;
        public static gc_history_per_heap gc_data_per_heap;
        public static fgm_history fgm_result;
        public static gc_history_global gc_data_global;
        public static last_recorded_gc_info last_full_blocking_gc_info;
        public static ulong end_gc_time;
        public static ulong last_alloc_reset_suspended_end_time;
        public static int g_low_memory_status;
        public static uint high_memory_load_th;
        public static uint v_high_memory_load_th;
        public static ulong mem_one_percent;
        public static int last_gc_before_oom;
        public static bool provisional_mode_triggered;
        public static nuint soh_allocation_no_gc;
        public static int reset_mm_p;
#if !USE_REGIONS
        internal static bool gc_can_use_concurrent;
#if BACKGROUND_GC
        public static bool background_running_p() => false;
#endif
#endif

        public static int loh_compaction_requested()
        {
            return loh_compaction_always_p != 0 ||
                loh_compaction_mode != gc_loh_compaction_mode.loh_compaction_default
                ? 1
                : 0;
        }

        public static void init_alloc_info(generation* gen, heap_segment* seg)
        {
            generation.generation_allocation_segment(gen) = seg;
            generation.generation_allocation_pointer(gen) = heap_segment.heap_segment_mem(seg);
            generation.generation_allocation_limit(gen) = generation.generation_allocation_pointer(gen);
            generation.generation_allocation_context_start_region(gen) = generation.generation_allocation_pointer(gen);
        }

        public static void initialize_loh_compaction_state()
        {
            loh_compaction_always_p = GCConfig.GetLOHCompactionMode() != 0 ? 1 : 0;
            loh_compaction_mode = gc_loh_compaction_mode.loh_compaction_default;
        }

        public static void initialize_gc_static_state()
        {
            // C# statics start zeroed, unlike gc_heap::loh_compaction_mode's native initializer.
            // Establish its native default before first_init; initialize_gc applies the config
            // value after first_init has reset the current collection mechanisms.
            loh_compaction_always_p = 0;
            loh_compaction_mode = gc_loh_compaction_mode.loh_compaction_default;
            loh_compacted_p = 0;
            full_gc_counts = default;
            loh_alloc_since_cg = 0;
            last_full_blocking_gc_info = default;
            initialize_loh_pinned_queue_state();
            alloc_contexts_used = 0;
            freeable_uoh_segment = null;
            last_gc_before_oom = 0;
            provisional_mode_triggered = false;
            soh_allocation_no_gc = 0;
            reset_mm_p = 1;
#if USE_REGIONS && !MULTIPLE_HEAPS
            gen0_bricks_cleared = 0;
            gen0_must_clear_bricks = 0;
#endif
#if USE_REGIONS
            reserved_free_regions_sip = default;
            regions_per_gen = default;
            planned_regions_per_gen = default;
            sip_maxgen_regions_per_gen = default;
            decide_promote_gen1_pins_p = true;
            enable_special_regions_p = GCConfig.GetGCEnableSpecialRegions() != 0;
            special_sweep_p = false;
            maxgen_pinned_compact_before_advance = 0;
            new_gen0_regions_in_plns = 0;
            new_regions_in_prr = 0;
            new_regions_in_threading = 0;
#endif
            settings.first_init();
            initialize_loh_compaction_state();
            initialize_mark_phase_state();
        }

#if USE_REGIONS && !MULTIPLE_HEAPS
        public static void init_records(gc_heap* hp)
        {
            // An option is to move this to be after we figure out which gen to condemn so we don't
            // need to clear some generations' data 'cause we know they don't change, but that also means
            // we can't simply call memset here.
            gc_data_per_heap = default;
            gc_data_per_heap.heap_index = unchecked((uint)hp->heap_number);
            if (hp->heap_number == 0)
            {
                gc_data_global = default;
            }

            fgm_result = default;

            gc_history_per_heap* current_gc_data_per_heap =
                (gc_history_per_heap*)Unsafe.AsPointer(ref gc_data_per_heap);
            for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
            {
                ref gc_generation_data gen_data = ref gc_history_per_heap.gen_data(current_gc_data_per_heap, i);
                gen_data.size_before = generation_size(hp, i);
                generation* gen = generation_of(generation_table_of(hp), i);
                gen_data.free_list_space_before = generation.generation_free_list_space(gen);
                gen_data.free_obj_space_before = generation.generation_free_obj_space(gen);
            }

            end_gen0_region_space = uninitialized_end_gen0_region_space;
            end_gen0_region_committed_space = 0;
            gen0_pinned_free_space = 0;
            gen0_large_chunk_found = false;
            num_regions_freed_in_sweep = 0;

            sufficient_gen0_space_p = 0;
        }
#endif

#if USE_REGIONS && !MULTIPLE_HEAPS
        public static byte** make_mark_list(nuint size)
        {
            if (size > nuint.MaxValue / (nuint)sizeof(byte*))
            {
                return null;
            }

            return (byte**)SyncImports.ManagedGC_AllocZeroed(size * (nuint)sizeof(byte*));
        }

        public static bool initialize_mark_list()
        {
            nuint soh_segment_size = get_valid_segment_size();
            nuint size = soh_segment_size / (64 * 32);
            if (size < 8192)
            {
                size = 8192;
            }
            else if (size > 100 * 1024)
            {
                size = 100 * 1024;
            }

            byte** new_mark_list = make_mark_list(size);
            if (new_mark_list is null)
            {
                return false;
            }

            g_mark_list = new_mark_list;
            mark_list_size = size;
            g_mark_list_total_size = size;
            return true;
        }

        public static bool initialize_mark_stack()
        {
            if (gc_rand.MARK_STACK_INITIAL_LENGTH >
                nuint.MaxValue / (nuint)sizeof(mark))
            {
                return false;
            }

            mark* stack = (mark*)SyncImports.ManagedGC_AllocZeroed(
                gc_rand.MARK_STACK_INITIAL_LENGTH * (nuint)sizeof(mark));
            if (stack is null)
            {
                return false;
            }

            make_mark_stack(null, stack);
            return true;
        }

        public static void destroy_semi_shared()
        {
            if (g_mark_list is not null)
            {
                SyncImports.ManagedGC_Free(g_mark_list);
                g_mark_list = null;
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

            if (mark_stack_array is not null)
            {
                SyncImports.ManagedGC_Free(mark_stack_array);
                mark_stack_array = null;
            }

            mark_stack_array_length = 0;
            mark_stack_tos = 0;
            mark_stack_bos = 0;
        }
#endif

        public static heap_segment* heap_segment_in_range(heap_segment* segment)
        {
            if (segment is null || heap_segment.heap_segment_in_range_p(segment) != 0)
            {
                return segment;
            }

            do
            {
                segment = heap_segment.heap_segment_next(segment);
            }
            while (segment is not null && heap_segment.heap_segment_in_range_p(segment) == 0);

            return segment;
        }

        public static heap_segment* heap_segment_next_in_range(heap_segment* segment)
        {
            heap_segment* nextSegment = heap_segment.heap_segment_next(segment);
            return heap_segment_in_range(nextSegment);
        }

        public static int in_range_for_segment(byte* address, heap_segment* segment)
        {
            return address >= heap_segment.heap_segment_mem(segment)
                && address < heap_segment.heap_segment_reserved(segment) ? 1 : 0;
        }

        public static int get_start_generation_index()
        {
#if USE_REGIONS
            return 0;
#else
            return GCInterfaceOffsets.max_generation;
#endif
        }

        public static int get_stop_generation_index(int condemned_gen_number)
        {
#if USE_REGIONS
            return 0;
#else
            return condemned_gen_number;
#endif
        }

#if USE_REGIONS && !MULTIPLE_HEAPS
        public static nuint get_promoted_bytes(gc_heap* heap)
        {
            if (survived_per_region is null)
            {
                return 0;
            }

            nuint promoted = 0;
            for (nuint i = 0; i < region_count; i++)
            {
                if (survived_per_region[(nint)i] > 0)
                {
#if DEBUG
                    heap_segment* region = get_region_at_index(i);
                    Debug.Assert(region is not null);
#endif
                    promoted = unchecked(promoted + survived_per_region[(nint)i]);
                }
            }

#if DEBUG
            Debug.Assert(promoted_bytes(heap->heap_number) == promoted);
#endif
            return promoted;
        }
#endif

        public static int index_of_highest_set_bit(nuint value)
        {
            uint highest_set_bit_index;
#if TARGET_64BIT
            return GCEnv.BitScanReverse64(&highest_set_bit_index, (ulong)value) == 0 ? -1 : (int)highest_set_bit_index;
#else
            return GCEnv.BitScanReverse(&highest_set_bit_index, (uint)value) == 0 ? -1 : (int)highest_set_bit_index;
#endif
        }

        public static bool power_of_two_p(nuint value)
        {
            return (value & (value - 1)) == 0;
        }

#if USE_REGIONS
        public static void initialize_min_segment_size_shr(nuint min_segment_size)
        {
            min_segment_size_shr = (nuint)index_of_highest_set_bit(min_segment_size);
        }

        public static byte* align_lower_segment(byte* add)
        {
            return (byte*)((nuint)add & ~(((nuint)1 << (int)min_segment_size_shr) - 1));
        }

        public static nuint seg_mapping_word_of(byte* add)
        {
            return (nuint)add >> (int)min_segment_size_shr;
        }

        public static byte* get_region_start(heap_segment* region_info)
        {
            byte* objStart = heap_segment.heap_segment_mem(region_info);
            return objStart - sizeof(aligned_plug_and_gap);
        }

        public static nuint get_region_size(heap_segment* region_info)
        {
            return (nuint)(heap_segment.heap_segment_reserved(region_info) - get_region_start(region_info));
        }

        public static nuint get_region_committed_size(heap_segment* region)
        {
            byte* start = get_region_start(region);
            byte* committed = heap_segment.heap_segment_committed(region);
            return (nuint)(committed - start);
        }

        public static heap_segment* get_region_info_for_address(byte* address)
        {
            nuint basic_region_index = (nuint)address >> (int)min_segment_size_shr;
            heap_segment* basic_region_info_entry = (heap_segment*)(GCCommon.seg_mapping_table + (nint)basic_region_index);
            nint first_field = (nint)heap_segment.heap_segment_allocated(basic_region_info_entry);
            if (first_field < 0)
            {
                basic_region_index = unchecked(basic_region_index + (nuint)first_field);
            }

            return (heap_segment*)(GCCommon.seg_mapping_table + (nint)basic_region_index);
        }

        public static nuint get_skewed_basic_region_index_for_address(byte* address)
        {
            Debug.Assert(GCCommon.g_gc_lowest_address <= address && address <= GCCommon.g_gc_highest_address);
            nuint skewed_basic_region_index = (nuint)address >> (int)min_segment_size_shr;
            return skewed_basic_region_index;
        }

        public static nuint get_basic_region_index_for_address(byte* address)
        {
            nuint skewed_basic_region_index = get_skewed_basic_region_index_for_address(address);
            return skewed_basic_region_index - get_skewed_basic_region_index_for_address(GCCommon.g_gc_lowest_address);
        }

        public static heap_segment* get_region_info(byte* region_start)
        {
            nuint region_index = (nuint)region_start >> (int)min_segment_size_shr;
            heap_segment* region_info_entry = (heap_segment*)(GCCommon.seg_mapping_table + (nint)region_index);
            return region_info_entry;
        }

        public static bool is_free_region(heap_segment* region)
        {
            return heap_segment.heap_segment_allocated(region) is null;
        }

        public static region_allocator global_region_allocator;

        public static void initialize_gc_lock()
        {
            GCSpinLock.initialize(ref gc_lock);
        }

        public static void enter_gc_lock()
        {
            fixed (GCSpinLock* lock_address = &gc_lock)
            {
                GCSpinLock.enter(lock_address);
            }
        }

        public static void leave_gc_lock()
        {
            fixed (GCSpinLock* lock_address = &gc_lock)
            {
                GCSpinLock.leave(lock_address);
            }
        }

        public static void assert_holding_gc_lock()
        {
#if DEBUG
            fixed (GCSpinLock* lock_address = &gc_lock)
            {
                Debug.Assert(lock_address->holding_thread != (void*)(-1));
            }
#endif
        }

        public static void assert_holding_gc_lock_by_current_thread()
        {
#if DEBUG
            fixed (GCSpinLock* lock_address = &gc_lock)
            {
                Debug.Assert(lock_address->holding_thread == GCToEEInterface.GetThread());
            }
#endif
        }
#endif
    }

#if USE_REGIONS
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct GCSpinLock
    {
        public const int lock_free = -1;

        // Native VOLATILE(int32_t); access this through GCEnv's volatile helpers.
        public int @lock;
#if DEBUG
        public void* holding_thread;
        public int released_by_gc_p;
#endif

        // C# does not run struct constructors for embedded fields or unmanaged storage, so keep
        // the native constructor's lock sentinel assignment explicit.
        public static void initialize(ref GCSpinLock spin_lock)
        {
            spin_lock.@lock = lock_free;
#if DEBUG
            spin_lock.holding_thread = (void*)(-1);
#endif
        }

        public static void initialize(GCSpinLock* spin_lock)
        {
            initialize(ref *spin_lock);
        }

        public static void enter(GCSpinLock* spin_lock)
        {
            int* lock_address = &spin_lock->@lock;
            while (true)
            {
                if (Interlocked.CompareExchange(lock_address, 0, lock_free) < 0)
                {
                    break;
                }

                while (GCEnv.VolatileLoadWithoutBarrier(lock_address) >= 0)
                {
                    GCEnv.YieldProcessor();
                }
            }

#if DEBUG
            spin_lock->holding_thread = GCToEEInterface.GetThread();
#endif
        }

        public static void leave(GCSpinLock* spin_lock)
        {
#if DEBUG
            spin_lock->holding_thread = (void*)(-1);
#endif
            GCEnv.VolatileStore(&spin_lock->@lock, lock_free);
        }
    }

    internal enum allocate_direction
    {
        allocate_forward = 1,
        allocate_backward = -1,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct region_allocator
    {
        public const int LARGE_REGION_FACTOR = 8;
        public const int region_alloc_free_bit = unchecked(1 << (sizeof(uint) * 8 - 1));

        // This is the native field order through the region-map and free-unit counters. The
        // remaining allocation/deletion algorithms are deferred.
        private byte* global_region_start;
        private byte* global_region_end;
        private byte* global_region_left_used;
        private byte* global_region_right_used;
        private uint total_free_units;
        private nuint region_alignment;
        private nuint large_region_alignment;
        private GCSpinLock region_allocator_lock;
        private uint* region_map_left_start;
        private uint* region_map_left_end;
        private uint* region_map_right_start;
        private uint* region_map_right_end;
        private uint num_left_used_free_units;
        private uint num_right_used_free_units;

        public void initialize()
        {
            GCSpinLock.initialize(ref region_allocator_lock);
        }

        public void destroy()
        {
            if (region_map_left_start is not null)
            {
                SyncImports.ManagedGC_Free(region_map_left_start);
            }

            global_region_start = null;
            global_region_end = null;
            global_region_left_used = null;
            global_region_right_used = null;
            total_free_units = 0;
            region_alignment = 0;
            large_region_alignment = 0;
            GCSpinLock.initialize(ref region_allocator_lock);
            region_map_left_start = null;
            region_map_left_end = null;
            region_map_right_start = null;
            region_map_right_end = null;
            num_left_used_free_units = 0;
            num_right_used_free_units = 0;
        }

        public void enter_spin_lock()
        {
            fixed (int* lock_address = &region_allocator_lock.@lock)
            {
                while (true)
                {
                    if (Interlocked.CompareExchange(lock_address, 0, GCSpinLock.lock_free) < 0)
                    {
                        break;
                    }

                    while (GCEnv.VolatileLoadWithoutBarrier(lock_address) >= 0)
                    {
                        GCEnv.YieldProcessor();
                    }
                }
            }

#if DEBUG
            region_allocator_lock.holding_thread = GCToEEInterface.GetThread();
#endif
        }

        public void leave_spin_lock()
        {
#if DEBUG
            region_allocator_lock.holding_thread = (void*)(-1);
#endif
            fixed (int* lock_address = &region_allocator_lock.@lock)
            {
                GCEnv.VolatileStore(lock_address, GCSpinLock.lock_free);
            }
        }

        public bool init(byte* start, byte* end, nuint alignment, byte** lowest, byte** highest)
        {
            byte* actual_start = start;
            region_alignment = alignment;
            large_region_alignment = unchecked((nuint)LARGE_REGION_FACTOR * alignment);
            global_region_start = (byte*)align_region_up((nuint)actual_start);
            byte* actual_end = end;
            global_region_end = (byte*)align_region_down((nuint)actual_end);
            global_region_left_used = global_region_start;
            global_region_right_used = global_region_end;
            num_left_used_free_units = 0;
            num_right_used_free_units = 0;

            nuint total_num_units = unchecked((nuint)(global_region_end - global_region_start)) / region_alignment;
            total_free_units = (uint)total_num_units;

            uint* unit_map = null;
            if (total_num_units <= nuint.MaxValue / (nuint)sizeof(uint))
            {
                unit_map = (uint*)SyncImports.ManagedGC_AllocZeroed(unchecked(total_num_units * (nuint)sizeof(uint)));
            }

            if (unit_map is not null)
            {
                region_map_left_start = unit_map;
                region_map_left_end = region_map_left_start;

                region_map_right_start = unit_map + (nint)total_num_units;
                region_map_right_end = region_map_right_start;

                *lowest = global_region_start;
                *highest = global_region_end;
            }

            return unit_map is not null;
        }

        public void initialize_alignment(nuint alignment)
        {
            region_alignment = alignment;
            large_region_alignment = unchecked((nuint)LARGE_REGION_FACTOR * alignment);
        }

        public uint get_va_memory_load()
        {
            return unchecked((uint)(((global_region_left_used - global_region_start) + (global_region_end - global_region_right_used)) * 100.0
                / (global_region_end - global_region_start)));
        }

        public nuint get_free()
        {
            return unchecked((nuint)total_free_units * region_alignment);
        }

        public nuint get_region_alignment() => region_alignment;

        public nuint get_large_region_alignment() => large_region_alignment;

        public nuint get_used_region_count()
        {
            // currently we don't allocate anything from the right -
            // once we do, we need a more sophisticated way to iterate
            // through the used regions
            Debug.Assert(region_map_right_start == region_map_right_end);
            return (nuint)(region_map_left_end - region_map_left_start);
        }

        public byte* get_start() => global_region_start;

        // global_region_left_used can be modified concurrently by allocate and delete
        // usage of this function must make sure either it is under the region lock or we
        // are certain that these functions cannot be running concurrently.
        public byte* get_left_used_unsafe() => global_region_left_used;

        public nuint align_region_up(nuint size)
        {
            return unchecked((size + (region_alignment - 1)) & ~(region_alignment - 1));
        }

        public nuint align_region_down(nuint size)
        {
            return unchecked(size & ~(region_alignment - 1));
        }

        public nuint is_region_aligned(byte* address)
        {
            nuint value = (nuint)address;
            return value == unchecked(value & ~(region_alignment - 1)) ? (nuint)1 : 0;
        }

        public static bool is_unit_memory_free(uint val)
        {
            return (val & unchecked((uint)region_alloc_free_bit)) != 0;
        }

        public static uint get_num_units(uint val)
        {
            return val & ~unchecked((uint)region_alloc_free_bit);
        }

        public byte* region_address_of(uint* map_index)
        {
            nuint mapDelta = unchecked((nuint)(map_index - region_map_left_start));
            return unchecked(global_region_start + (nint)(mapDelta * region_alignment));
        }

        public uint* region_map_index_of(byte* address)
        {
            nuint addressDelta = unchecked((nuint)(address - global_region_start));
            return unchecked(region_map_left_start + (nint)(addressDelta / region_alignment));
        }

        // Native dprintf(REGIONS_LOG) and ASSERT_HOLDING_SPIN_LOCK(&region_allocator_lock) in
        // these region-map helpers are deferred until the GC has string-free region logging and
        // spin-lock ownership diagnostics; do not introduce managed diagnostics here.
        public void make_busy_block(uint* index_start, uint num_units)
        {
            uint* index_end = index_start + (nint)(num_units - 1);
            *index_start = num_units;
            *index_end = num_units;
        }

        public void make_free_block(uint* index_start, uint num_units)
        {
            uint* index_end = index_start + (nint)(num_units - 1);
            uint encoded = unchecked((uint)region_alloc_free_bit) | num_units;
            *index_start = encoded;
            *index_end = encoded;
        }

        public byte* allocate_end(uint num_units, allocate_direction direction)
        {
            byte* alloc = null;

            if (global_region_left_used < global_region_right_used)
            {
                nuint end_remaining = unchecked((nuint)(global_region_right_used - global_region_left_used));

                if ((end_remaining / region_alignment) >= num_units)
                {
                    nuint allocation_size = unchecked((nuint)num_units * region_alignment);
                    if (direction == allocate_direction.allocate_forward)
                    {
                        make_busy_block(region_map_left_end, num_units);
                        region_map_left_end += (nint)num_units;
                        alloc = global_region_left_used;
                        global_region_left_used += (nint)allocation_size;
                    }
                    else
                    {
                        Debug.Assert(direction == allocate_direction.allocate_backward);
                        region_map_right_start -= (nint)num_units;
                        make_busy_block(region_map_right_start, num_units);
                        global_region_right_used -= (nint)allocation_size;
                        alloc = global_region_right_used;
                    }
                }
            }

            return alloc;
        }

        public byte* allocate(uint num_units, allocate_direction direction, region_allocator_callback_fn fn)
        {
            enter_spin_lock();

            uint* current_index;
            uint* end_index;
            if (direction == allocate_direction.allocate_forward)
            {
                current_index = region_map_left_start;
                end_index = region_map_left_end;
            }
            else
            {
                Debug.Assert(direction == allocate_direction.allocate_backward);
                current_index = region_map_right_end;
                end_index = region_map_right_start;
            }

            if (((direction == allocate_direction.allocate_forward) && (num_left_used_free_units >= num_units)) ||
                ((direction == allocate_direction.allocate_backward) && (num_right_used_free_units >= num_units)))
            {
                while (((direction == allocate_direction.allocate_forward) && (current_index < end_index)) ||
                    ((direction == allocate_direction.allocate_backward) && (current_index > end_index)))
                {
                    uint current_val = *(current_index - ((direction == allocate_direction.allocate_backward) ? 1 : 0));
                    uint current_num_units = get_num_units(current_val);
                    bool free_p = is_unit_memory_free(current_val);

                    if (free_p)
                    {
                        if (current_num_units >= num_units)
                        {
                            if (direction == allocate_direction.allocate_forward)
                            {
                                Debug.Assert(num_left_used_free_units >= num_units);
                                num_left_used_free_units -= num_units;
                            }
                            else
                            {
                                Debug.Assert(direction == allocate_direction.allocate_backward);
                                Debug.Assert(num_right_used_free_units >= num_units);
                                num_right_used_free_units -= num_units;
                            }

                            uint* busy_block;
                            uint* free_block;
                            if (direction == allocate_direction.allocate_forward)
                            {
                                busy_block = current_index;
                                free_block = current_index + (nint)num_units;
                            }
                            else
                            {
                                busy_block = current_index - (nint)num_units;
                                free_block = current_index - (nint)current_num_units;
                            }

                            make_busy_block(busy_block, num_units);
                            if ((current_num_units - num_units) > 0)
                            {
                                make_free_block(free_block, current_num_units - num_units);
                            }

                            total_free_units -= num_units;

                            leave_spin_lock();

                            return region_address_of(busy_block);
                        }
                    }

                    if (direction == allocate_direction.allocate_forward)
                    {
                        current_index += (nint)current_num_units;
                    }
                    else
                    {
                        current_index -= (nint)current_num_units;
                    }
                }
            }

            byte* alloc = allocate_end(num_units, direction);

            if (alloc is not null)
            {
                total_free_units -= num_units;
                if (fn is not null)
                {
                    if (fn(global_region_left_used) == 0)
                    {
                        delete_region_impl(alloc);
                        alloc = null;
                    }
                }
            }

            leave_spin_lock();

            return alloc;
        }

        public bool allocate_region(
            int gen_num,
            nuint size,
            byte** start,
            byte** end,
            allocate_direction direction,
            region_allocator_callback_fn fn)
        {
            nuint alignment = region_alignment;
            nuint alloc_size = align_region_up(size);

            uint num_units = (uint)(alloc_size / alignment);
            bool ret = false;
            byte* alloc = null;

            alloc = allocate(num_units, direction, fn);
            *start = alloc;
            *end = alloc + (nint)alloc_size;
            ret = alloc is not null;

            gc_etw_segment_type segment_type;

            if (gen_num == (int)gc_generation_num.loh_generation)
            {
                segment_type = gc_etw_segment_type.gc_etw_segment_large_object_heap;
            }
            else if (gen_num == (int)gc_generation_num.poh_generation)
            {
                segment_type = gc_etw_segment_type.gc_etw_segment_pinned_object_heap;
            }
            else
            {
                segment_type = gc_etw_segment_type.gc_etw_segment_small_object_heap;
            }

            GCEvents.GCEventFireGCCreateSegment_V1(
                alloc + sizeof(aligned_plug_and_gap),
                unchecked(size - (nuint)sizeof(aligned_plug_and_gap)),
                (uint)segment_type);

            return ret;
        }

        public bool allocate_basic_region(int gen_num, byte** start, byte** end, region_allocator_callback_fn fn)
        {
            return allocate_region(gen_num, region_alignment, start, end, allocate_direction.allocate_forward, fn);
        }

        public bool allocate_large_region(
            int gen_num,
            byte** start,
            byte** end,
            allocate_direction direction,
            nuint size,
            region_allocator_callback_fn fn)
        {
            if (size == 0)
            {
                size = large_region_alignment;
            }
            else
            {
                Debug.Assert(round_up_power2(large_region_alignment) == large_region_alignment);
                size = unchecked((size + (large_region_alignment - 1)) & ~(large_region_alignment - 1));
            }

            return allocate_region(gen_num, size, start, end, direction, fn);
        }

        private static nuint round_up_power2(nuint size)
        {
            uint highest_set_bit_index;
#if TARGET_64BIT
            if (GCEnv.BitScanReverse64(&highest_set_bit_index, (ulong)(size - 1)) == 0)
#else
            if (GCEnv.BitScanReverse(&highest_set_bit_index, (uint)(size - 1)) == 0)
#endif
            {
                return 1;
            }

            return (nuint)2 << (int)highest_set_bit_index;
        }

        // Whenever a region is deleted, it is expected that the memory and the mark array
        // of the region is decommitted already.
        public void delete_region(byte* region_start)
        {
            enter_spin_lock();
            delete_region_impl(region_start);
            leave_spin_lock();
        }

        public void delete_region_impl(byte* region_start)
        {
            Debug.Assert(is_region_aligned(region_start) != 0);

            uint* current_index = region_map_index_of(region_start);
            uint current_val = *current_index;
            Debug.Assert(!is_unit_memory_free(current_val));

            uint* region_end_index = current_index + (nint)current_val;
            byte* region_end = region_address_of(region_end_index);

            int free_block_size = (int)current_val;
            uint* free_index = current_index;

            if (free_index <= region_map_left_end)
            {
                num_left_used_free_units += (uint)free_block_size;
            }
            else
            {
                Debug.Assert(free_index >= region_map_right_start);
                num_right_used_free_units += (uint)free_block_size;
            }

            if ((current_index != region_map_left_start) && (current_index != region_map_right_start))
            {
                uint previous_val = *(current_index - 1);
                if (is_unit_memory_free(previous_val))
                {
                    uint previous_size = get_num_units(previous_val);
                    free_index -= (nint)previous_size;
                    free_block_size = unchecked((int)((uint)free_block_size + previous_size));
                }
            }

            if ((region_end != global_region_left_used) && (region_end != global_region_end))
            {
                uint next_val = *region_end_index;
                if (is_unit_memory_free(next_val))
                {
                    uint next_size = get_num_units(next_val);
                    free_block_size = unchecked((int)((uint)free_block_size + next_size));
                    region_end += (nint)next_size;
                }
            }

            if (region_end == global_region_left_used)
            {
                num_left_used_free_units -= (uint)free_block_size;
                region_map_left_end = free_index;
                global_region_left_used = region_address_of(free_index);
            }
            else if (region_start == global_region_right_used)
            {
                num_right_used_free_units -= (uint)free_block_size;
                region_map_right_start = free_index + (nint)free_block_size;
                global_region_right_used = region_address_of(free_index + (nint)free_block_size);
            }
            else
            {
                make_free_block(free_index, (uint)free_block_size);
            }

            total_free_units += current_val;
        }

        public void move_highest_free_regions(long n, bool small_region_p, region_free_list* to_free_list)
        {
            Debug.Assert(n > 0);

            uint* current_index = region_map_left_end - 1;
            uint* lowest_index = region_map_left_start;

            while (current_index >= lowest_index)
            {
                uint current_val = *current_index;
                uint current_num_units = get_num_units(current_val);
                bool free_p = is_unit_memory_free(current_val);
                if (!free_p && ((current_num_units == 1) == small_region_p))
                {
                    uint* index = current_index - (nint)(current_num_units - 1);
                    heap_segment* region = gc_heap.get_region_info(region_address_of(index));
                    if (gc_heap.is_free_region(region) && !region_free_list.is_on_free_list(region, to_free_list))
                    {
                        if (n >= current_num_units)
                        {
                            n -= current_num_units;

                            region_free_list.unlink_region(region);

                            region_free_list.add_region(region, to_free_list);
                        }
                        else
                        {
                            break;
                        }
                    }
                }
                current_index -= (nint)current_num_units;
            }
        }
    }

    internal enum free_region_kind
    {
        basic_free_region = 0,
        large_free_region = 1,
        count_distributed_free_region_kinds = 2,
        huge_free_region = 2,
        count_free_region_kinds = 3,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct region_free_list
    {
        private nuint num_free_regions;
        private nuint size_free_regions;
        private nuint size_committed_in_free_regions;
        private nuint num_free_regions_added;
        private nuint num_free_regions_removed;
        private heap_segment* head_free_region;
        private heap_segment* tail_free_region;

        public static void verify(region_free_list* inst, bool empty_p)
        {
#if DEBUG
            Debug.Assert((inst->num_free_regions == 0) == empty_p);
            Debug.Assert((inst->size_free_regions == 0) == empty_p);
            Debug.Assert((inst->size_committed_in_free_regions == 0) == empty_p);
            Debug.Assert((inst->head_free_region is null) == empty_p);
            Debug.Assert((inst->tail_free_region is null) == empty_p);
            Debug.Assert(inst->num_free_regions == (inst->num_free_regions_added - inst->num_free_regions_removed));

            if (!empty_p)
            {
                Debug.Assert(heap_segment.heap_segment_next(inst->tail_free_region) is null);
                Debug.Assert(heap_segment.heap_segment_prev_free_region(inst->head_free_region) is null);

                nuint actualCount = 0;
                heap_segment* lastRegion = null;
                for (heap_segment* region = inst->head_free_region; region is not null; region = heap_segment.heap_segment_next(region))
                {
                    lastRegion = region;
                    actualCount++;
                }

                Debug.Assert(inst->num_free_regions == actualCount);
                Debug.Assert(lastRegion == inst->tail_free_region);

                heap_segment* firstRegion = null;
                for (heap_segment* region = inst->tail_free_region; region is not null; region = heap_segment.heap_segment_prev_free_region(region))
                {
                    firstRegion = region;
                    actualCount--;
                }

                Debug.Assert(actualCount == 0);
                Debug.Assert(inst->head_free_region == firstRegion);
            }
#endif
        }

        public void reset()
        {
            num_free_regions = 0;
            size_free_regions = 0;
            size_committed_in_free_regions = 0;
            head_free_region = null;
            tail_free_region = null;
        }

        private static void update_added_region_info(region_free_list* inst, heap_segment* region)
        {
            inst->num_free_regions++;
            inst->num_free_regions_added++;

            nuint regionSize = gc_heap.get_region_size(region);
            inst->size_free_regions += regionSize;

            nuint regionCommittedSize = gc_heap.get_region_committed_size(region);
            inst->size_committed_in_free_regions += regionCommittedSize;

            verify(inst, false);
        }

        public static void add_region_front(region_free_list* inst, heap_segment* region)
        {
            Debug.Assert(heap_segment.heap_segment_containing_free_list(region) is null);
            heap_segment.heap_segment_containing_free_list(region) = inst;

            if (inst->head_free_region is not null)
            {
                heap_segment.heap_segment_prev_free_region(inst->head_free_region) = region;
                Debug.Assert(inst->tail_free_region is not null);
            }
            else
            {
                inst->tail_free_region = region;
            }

            heap_segment.heap_segment_next(region) = inst->head_free_region;
            inst->head_free_region = region;
            heap_segment.heap_segment_prev_free_region(region) = null;

            update_added_region_info(inst, region);
        }

        // This inserts fully committed regions at the head, otherwise it goes backward in the
        // list until it finds one whose committed size is >= this region's committed size.
        public static void add_region_in_descending_order(region_free_list* inst, heap_segment* region_to_add)
        {
            Debug.Assert(heap_segment.heap_segment_containing_free_list(region_to_add) is null);
            heap_segment.heap_segment_containing_free_list(region_to_add) = inst;
            heap_segment.heap_segment_age_in_free(region_to_add) = 0;

            heap_segment* prev_region = null;
            heap_segment* region = null;

            if (heap_segment.heap_segment_committed(region_to_add) == heap_segment.heap_segment_reserved(region_to_add))
            {
                region = inst->head_free_region;
            }
            else
            {
                nuint regionToAddCommitted = gc_heap.get_region_committed_size(region_to_add);
                for (prev_region = inst->tail_free_region; prev_region is not null; prev_region = heap_segment.heap_segment_prev_free_region(prev_region))
                {
                    nuint prevRegionCommitted = gc_heap.get_region_committed_size(prev_region);
                    if (prevRegionCommitted >= regionToAddCommitted)
                    {
                        break;
                    }

                    region = prev_region;
                }
            }

            if (prev_region is not null)
            {
                heap_segment.heap_segment_next(prev_region) = region_to_add;
            }
            else
            {
                Debug.Assert(region == inst->head_free_region);
                inst->head_free_region = region_to_add;
            }

            heap_segment.heap_segment_prev_free_region(region_to_add) = prev_region;
            heap_segment.heap_segment_next(region_to_add) = region;

            if (region is not null)
            {
                heap_segment.heap_segment_prev_free_region(region) = region_to_add;
            }
            else
            {
                Debug.Assert(prev_region == inst->tail_free_region);
                inst->tail_free_region = region_to_add;
            }

            update_added_region_info(inst, region_to_add);
        }

        public static heap_segment* unlink_region_front(region_free_list* inst)
        {
            heap_segment* region = inst->head_free_region;
            if (region is not null)
            {
                Debug.Assert(heap_segment.heap_segment_containing_free_list(region) == inst);
                unlink_region(region);
            }

            return region;
        }

        public static void unlink_region(heap_segment* region)
        {
            region_free_list* rfl = heap_segment.heap_segment_containing_free_list(region);
            verify(rfl, false);

            heap_segment* prev = heap_segment.heap_segment_prev_free_region(region);
            heap_segment* next = heap_segment.heap_segment_next(region);

            if (prev is not null)
            {
                Debug.Assert(region != rfl->head_free_region);
                Debug.Assert(heap_segment.heap_segment_next(prev) == region);
                heap_segment.heap_segment_next(prev) = next;
            }
            else
            {
                Debug.Assert(region == rfl->head_free_region);
                rfl->head_free_region = next;
            }

            if (next is not null)
            {
                Debug.Assert(region != rfl->tail_free_region);
                Debug.Assert(heap_segment.heap_segment_prev_free_region(next) == region);
                heap_segment.heap_segment_prev_free_region(next) = prev;
            }
            else
            {
                Debug.Assert(region == rfl->tail_free_region);
                rfl->tail_free_region = prev;
            }

            heap_segment.heap_segment_containing_free_list(region) = null;

            rfl->num_free_regions--;
            rfl->num_free_regions_removed++;

            nuint regionSize = gc_heap.get_region_size(region);
            Debug.Assert(rfl->size_free_regions >= regionSize);
            rfl->size_free_regions -= regionSize;

            nuint regionCommittedSize = gc_heap.get_region_committed_size(region);
            Debug.Assert(rfl->size_committed_in_free_regions >= regionCommittedSize);
            rfl->size_committed_in_free_regions -= regionCommittedSize;
        }

        private static free_region_kind get_region_kind(heap_segment* region)
        {
            nuint BASIC_REGION_SIZE = gc_heap.global_region_allocator.get_region_alignment();
            nuint LARGE_REGION_SIZE = gc_heap.global_region_allocator.get_large_region_alignment();
            nuint region_size = gc_heap.get_region_size(region);

            if (region_size == BASIC_REGION_SIZE)
            {
                return free_region_kind.basic_free_region;
            }
            else if (region_size == LARGE_REGION_SIZE)
            {
                return free_region_kind.large_free_region;
            }
            else
            {
                Debug.Assert(region_size > LARGE_REGION_SIZE);
                return free_region_kind.huge_free_region;
            }
        }

        public static heap_segment* unlink_smallest_region(region_free_list* inst, nuint minimum_size)
        {
            verify(inst, inst->num_free_regions == 0);

            heap_segment* smallest_region = null;
            nuint smallest_size = nuint.MaxValue;
            nuint LARGE_REGION_SIZE = gc_heap.global_region_allocator.get_large_region_alignment();
            for (heap_segment* region = inst->head_free_region; region is not null; region = heap_segment.heap_segment_next(region))
            {
                byte* region_start = gc_heap.get_region_start(region);
                byte* region_end = heap_segment.heap_segment_reserved(region);
                _ = region_start;
                _ = region_end;

                nuint region_size = gc_heap.get_region_size(region);
                Debug.Assert(region_size >= LARGE_REGION_SIZE * 2);
                if (region_size >= minimum_size)
                {
                    if (smallest_size > region_size)
                    {
                        smallest_size = region_size;
                        smallest_region = region;
                    }

                    if (region_size == LARGE_REGION_SIZE * 2)
                    {
                        Debug.Assert(region == smallest_region);
                        break;
                    }
                }
            }

            if (smallest_region is not null)
            {
                unlink_region(smallest_region);
            }

            return smallest_region;
        }

        public static void transfer_regions(region_free_list* inst, region_free_list* from)
        {
            verify(inst, inst->num_free_regions == 0);
            verify(from, from->num_free_regions == 0);

            if (from->num_free_regions == 0)
            {
                return;
            }

            if (inst->num_free_regions == 0)
            {
                inst->head_free_region = from->head_free_region;
                inst->tail_free_region = from->tail_free_region;
            }
            else
            {
                heap_segment* thisTail = inst->tail_free_region;
                heap_segment* fromHead = from->head_free_region;

                heap_segment.heap_segment_next(thisTail) = fromHead;
                heap_segment.heap_segment_prev_free_region(fromHead) = thisTail;
                inst->tail_free_region = from->tail_free_region;
            }

            for (heap_segment* region = from->head_free_region; region is not null; region = heap_segment.heap_segment_next(region))
            {
                heap_segment.heap_segment_containing_free_list(region) = inst;
            }

            inst->num_free_regions += from->num_free_regions;
            inst->num_free_regions_added += from->num_free_regions;
            inst->size_free_regions += from->size_free_regions;
            inst->size_committed_in_free_regions += from->size_committed_in_free_regions;

            from->num_free_regions_removed += from->num_free_regions;
            from->reset();

            verify(inst, false);
        }

        public static nuint get_num_free_regions(region_free_list* inst)
        {
#if DEBUG
            verify(inst, inst->num_free_regions == 0);
#endif
            return inst->num_free_regions;
        }

        public static void add_region(heap_segment* region, region_free_list* to_free_list)
        {
            free_region_kind kind = get_region_kind(region);
            add_region_front(&to_free_list[(int)kind], region);
        }

        public static void add_region_descending(heap_segment* region, region_free_list* to_free_list)
        {
            free_region_kind kind = get_region_kind(region);
            add_region_in_descending_order(&to_free_list[(int)kind], region);
        }

        public static bool is_on_free_list(heap_segment* region, region_free_list* free_list)
        {
            region_free_list* rfl = heap_segment.heap_segment_containing_free_list(region);
            free_region_kind kind = get_region_kind(region);
            return rfl == &free_list[(int)kind];
        }

        public nuint get_size_committed_in_free() => size_committed_in_free_regions;

        public nuint get_size_free_regions() => size_free_regions;

        public heap_segment* get_first_free_region() => head_free_region;

        public void age_free_regions()
        {
            for (heap_segment* region = head_free_region; region is not null; region = heap_segment.heap_segment_next(region))
            {
                if (heap_segment.heap_segment_age_in_free(region) < heap_segment.MAX_AGE_IN_FREE)
                {
                    heap_segment.heap_segment_age_in_free(region)++;
                }
            }
        }

        public static void age_free_regions(region_free_list* free_lists)
        {
            for (int kind = (int)free_region_kind.basic_free_region;
                 kind < (int)free_region_kind.count_free_region_kinds;
                 kind++)
            {
                free_lists[kind].age_free_regions();
            }
        }

        private static int compare_by_committed_and_age(heap_segment* l, heap_segment* r)
        {
            nuint lCommitted = gc_heap.get_region_committed_size(l);
            nuint rCommitted = gc_heap.get_region_committed_size(r);
            if (lCommitted > rCommitted)
            {
                return -1;
            }
            else if (lCommitted < rCommitted)
            {
                return 1;
            }

            int lAge = heap_segment.heap_segment_age_in_free(l);
            int rAge = heap_segment.heap_segment_age_in_free(r);
            return lAge - rAge;
        }

        private static heap_segment* merge_sort_by_committed_and_age(heap_segment* head, nuint count)
        {
            if (count <= 1)
            {
                return head;
            }

            nuint half = count / 2;
            heap_segment* mid = null;
            nuint i = 0;
            for (heap_segment* region = head; region is not null; region = heap_segment.heap_segment_next(region))
            {
                i++;
                if (i == half)
                {
                    mid = heap_segment.heap_segment_next(region);
                    heap_segment.heap_segment_next(region) = null;
                    break;
                }
            }

            head = merge_sort_by_committed_and_age(head, half);
            mid = merge_sort_by_committed_and_age(mid, count - half);

            heap_segment* newHead;
            if (compare_by_committed_and_age(head, mid) <= 0)
            {
                newHead = head;
                head = heap_segment.heap_segment_next(head);
            }
            else
            {
                newHead = mid;
                mid = heap_segment.heap_segment_next(mid);
            }

            heap_segment* newTail = newHead;
            while (head is not null && mid is not null)
            {
                heap_segment* region;
                if (compare_by_committed_and_age(head, mid) <= 0)
                {
                    region = head;
                    head = heap_segment.heap_segment_next(head);
                }
                else
                {
                    region = mid;
                    mid = heap_segment.heap_segment_next(mid);
                }

                heap_segment.heap_segment_next(newTail) = region;
                newTail = region;
            }

            if (head is not null)
            {
                Debug.Assert(mid is null);
                heap_segment.heap_segment_next(newTail) = head;
            }
            else
            {
                heap_segment.heap_segment_next(newTail) = mid;
            }

            return newHead;
        }

        public void sort_by_committed_and_age()
        {
            if (num_free_regions <= 1)
            {
                return;
            }

            heap_segment* newHead = merge_sort_by_committed_and_age(head_free_region, num_free_regions);

            head_free_region = newHead;
            heap_segment* prev = null;
            for (heap_segment* region = newHead; region is not null; region = heap_segment.heap_segment_next(region))
            {
                heap_segment.heap_segment_prev_free_region(region) = prev;
                Debug.Assert(prev is null || compare_by_committed_and_age(prev, region) <= 0);
                prev = region;
            }

            tail_free_region = prev;
        }
    }
#else
    internal unsafe partial struct region_free_list
    {
    }
#endif
#pragma warning restore CS8981

#if USE_REGIONS
    internal unsafe struct generation_region_info
    {
        public heap_segment* head;
        public heap_segment* tail;
    }
#endif

    // The segment schema is dependency-closed: gc_heap is deliberately opaque here, while
    // region_free_list is now translated but still referenced by pointer from this record.
#pragma warning disable CS8981 // Native type names are intentionally preserved.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe partial struct heap_segment
#pragma warning restore CS8981
    {
        public const nuint heap_segment_flags_readonly = 1;
        public const nuint heap_segment_flags_inrange = 2;
        public const nuint heap_segment_flags_loh = 8;
#if BACKGROUND_GC
        public const nuint heap_segment_flags_swept = 16;
        public const nuint heap_segment_flags_decommitted = 32;
        public const nuint heap_segment_flags_ma_committed = 64;
        public const nuint heap_segment_flags_ma_pcommitted = 128;
        public const nuint heap_segment_flags_uoh_delete = 256;
#endif
        public const nuint heap_segment_flags_poh = 512;
#if BACKGROUND_GC && USE_REGIONS
        public const nuint heap_segment_flags_overflow = 1024;
#endif
#if USE_REGIONS
        public const nuint heap_segment_flags_demoted = 2048;
        public const int MAX_AGE_IN_FREE = 99;
        public const int AGE_IN_FREE_TO_DECOMMIT_BASIC = 20;
        public const int AGE_IN_FREE_TO_DECOMMIT_LARGE = 5;
        public const int AGE_IN_FREE_TO_DECOMMIT_HUGE = 2;
#endif

        public byte* allocated;
        public byte* committed;
        public byte* reserved;
        public byte* used;
        public byte* mem;
        public nuint flags;
        public heap_segment* next;
        public byte* background_allocated;
#if MULTIPLE_HEAPS
        public gc_heap* heap;
#if DEBUG && !USE_REGIONS
        public byte* saved_committed;
        public nuint saved_desired_allocation;
#endif
#endif
#if !USE_REGIONS || MULTIPLE_HEAPS
        public byte* decommit_target;
#endif
        public byte* plan_allocated;
        public byte* saved_allocated;
        public byte* saved_bg_allocated;
#if USE_REGIONS
        public nuint survived;
        public byte gen_num;
        public byte swept_in_plan_p;
        public int plan_gen_num;
        public int old_card_survived;
        public int pinned_survived;
        public int age_in_free;
        public byte* free_list_head;
        public byte* free_list_tail;
        public nuint free_list_size;
        public nuint free_obj_size;
        public heap_segment* prev_free_region;
        public region_free_list* containing_free_list;
#else
        public aligned_plug_and_gap padandplug;
#endif

#if USE_REGIONS
        public void init_free_list()
        {
            free_list_head = null;
            free_list_tail = null;
            free_list_size = 0;
            free_obj_size = 0;
        }

        public void thread_free_obj(byte* obj, nuint s)
        {
            if (s >= unchecked(2 * (nuint)GCInterfaceOffsets.min_obj_size))
            {
                allocator.free_list_slot(obj) = null;

                if (free_list_head is not null)
                {
                    Debug.Assert(free_list_tail is not null);
                    allocator.free_list_slot(free_list_tail) = obj;
                }
                else
                {
                    free_list_head = obj;
                }

                free_list_tail = obj;
                free_list_size += s;
            }
            else
            {
                free_obj_size += s;
            }
        }
#endif

        public static ref byte* heap_segment_reserved(heap_segment* inst) => ref inst->reserved;

        public static ref byte* heap_segment_committed(heap_segment* inst) => ref inst->committed;

#if !USE_REGIONS || MULTIPLE_HEAPS
        public static ref byte* heap_segment_decommit_target(heap_segment* inst) => ref inst->decommit_target;
#endif

        public static ref byte* heap_segment_used(heap_segment* inst) => ref inst->used;

        public static ref byte* heap_segment_allocated(heap_segment* inst) => ref inst->allocated;

        public static int heap_segment_read_only_p(heap_segment* inst) =>
            (inst->flags & heap_segment_flags_readonly) != 0 ? 1 : 0;

        public static int heap_segment_in_range_p(heap_segment* inst) =>
            ((inst->flags & heap_segment_flags_readonly) == 0
             || (inst->flags & heap_segment_flags_inrange) != 0) ? 1 : 0;

        public static int heap_segment_loh_p(heap_segment* inst) =>
            (inst->flags & heap_segment_flags_loh) != 0 ? 1 : 0;

        public static int heap_segment_poh_p(heap_segment* inst) =>
            (inst->flags & heap_segment_flags_poh) != 0 ? 1 : 0;

        public static int heap_segment_uoh_p(heap_segment* inst) =>
            (inst->flags & (heap_segment_flags_loh | heap_segment_flags_poh)) != 0 ? 1 : 0;

        public static gc_oh_num heap_segment_oh(heap_segment* inst)
        {
            if ((inst->flags & heap_segment_flags_loh) != 0)
            {
                return gc_oh_num.loh;
            }
            else if ((inst->flags & heap_segment_flags_poh) != 0)
            {
                return gc_oh_num.poh;
            }

            return gc_oh_num.soh;
        }

#if USE_REGIONS
        public static ref region_free_list* heap_segment_containing_free_list(heap_segment* inst) => ref inst->containing_free_list;

        public static ref heap_segment* heap_segment_prev_free_region(heap_segment* inst) => ref inst->prev_free_region;
#endif

#if BACKGROUND_GC
#if USE_REGIONS
        public static bool heap_segment_overflow_p(heap_segment* inst) =>
            (inst->flags & heap_segment_flags_overflow) != 0;
#endif

        public static int heap_segment_decommitted_p(heap_segment* inst) =>
            (inst->flags & heap_segment_flags_decommitted) != 0 ? 1 : 0;

        public static int heap_segment_swept_p(heap_segment* inst) =>
            (inst->flags & heap_segment_flags_swept) != 0 ? 1 : 0;
#endif

        public static ref heap_segment* heap_segment_next(heap_segment* inst) => ref inst->next;

        public static ref byte* heap_segment_mem(heap_segment* inst) => ref inst->mem;

        public static ref byte* heap_segment_plan_allocated(heap_segment* inst) => ref inst->plan_allocated;

        public static ref byte* heap_segment_saved_allocated(heap_segment* inst) => ref inst->saved_allocated;

#if BACKGROUND_GC
        public static ref byte* heap_segment_background_allocated(heap_segment* inst) => ref inst->background_allocated;

        public static ref byte* heap_segment_saved_bg_allocated(heap_segment* inst) => ref inst->saved_bg_allocated;
#endif

#if MULTIPLE_HEAPS
        public static ref gc_heap* heap_segment_heap(heap_segment* inst) => ref inst->heap;
#endif

#if USE_REGIONS
        public static ref byte heap_segment_gen_num(heap_segment* inst) => ref inst->gen_num;

        public static ref byte heap_segment_swept_in_plan(heap_segment* inst) => ref inst->swept_in_plan_p;

        public static ref int heap_segment_plan_gen_num(heap_segment* inst) => ref inst->plan_gen_num;

        public static ref int heap_segment_age_in_free(heap_segment* inst) => ref inst->age_in_free;

        public static ref nuint heap_segment_survived(heap_segment* inst) => ref inst->survived;

        public static ref int heap_segment_old_card_survived(heap_segment* inst) => ref inst->old_card_survived;

        public static ref int heap_segment_pinned_survived(heap_segment* inst) => ref inst->pinned_survived;

        public static byte* heap_segment_free_list_head(heap_segment* inst) => inst->free_list_head;

        public static byte* heap_segment_free_list_tail(heap_segment* inst) => inst->free_list_tail;

        public static nuint heap_segment_free_list_size(heap_segment* inst) => inst->free_list_size;

        public static nuint heap_segment_free_obj_size(heap_segment* inst) => inst->free_obj_size;

        public static bool heap_segment_demoted_p(heap_segment* inst) =>
            (inst->flags & heap_segment_flags_demoted) != 0;
#endif
    }

    // Maps a basic-region table entry to its segment information. In a region build the entry is
    // the complete region record; otherwise it caches the boundary, owning heap(s), and segment(s)
    // that heap_of uses. The lookup algorithms remain with regions_segments.cpp and gc.cpp.
#pragma warning disable CS8981 // Native type names are intentionally preserved.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct seg_mapping
#pragma warning restore CS8981
    {
        public const nuint ro_in_entry = 1;

#if USE_REGIONS
        public heap_segment region_info;
#else
        public byte* boundary;
#if MULTIPLE_HEAPS
        public gc_heap* h0;
        public gc_heap* h1;
#endif
        public heap_segment* seg0;
        public heap_segment* seg1;
#endif
    }

    // A generation is a per heap concept: each heap has its own gen0/1/2/loh/poh. The native class
    // has no constructor, so a zero-initialized instance matches the native default for every field
    // except the embedded free_list_allocator, which the native allocator default constructor brings
    // up; initialize reproduces that (see below). All members are public in the C++ class, and every
    // native accessor hands out either a pointer to an embedded subobject or a reference into the
    // instance, so they are translated as static helpers taking a generation* -- mirroring the native
    // reference-return API without introducing a managed reference to collector state.
    //
    // USE_REGIONS is the collector's region layout, defined in gcpriv.h as
    //   HOST_64BIT && !BUILD_AS_STANDALONE && !__sun && (!HOST_APPLE || HOST_OSX).
    // This integrated port is never BUILD_AS_STANDALONE and never targets illumos/Solaris, so it
    // reduces to 64-bit AND not an Apple mobile platform; the build defines the USE_REGIONS symbol
    // for exactly those targets. DOUBLY_LINKED_FL is gcpriv.h's doubly linked free list, which is
    // BACKGROUND_GC && HOST_64BIT, i.e. TARGET_64BIT && !TARGET_WASM. FREE_USAGE_STATS is a
    // diagnostics-only feature that is never defined, so its trailing fields are not translated.
#pragma warning disable CS8981 // Native type names are intentionally preserved.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct generation
#pragma warning restore CS8981
    {
        // Native `allocation_context` is an `alloc_context`, which derives from gc_alloc_context and
        // adds no fields (only FEATURE_SVR_GC member functions). The port reuses the gc_alloc_context
        // layout directly: a distinct managed alloc_context type would carry no extra field or
        // behavior, so none is introduced.
        public gc_alloc_context allocation_context;

        public heap_segment* start_segment;
#if !USE_REGIONS
        public byte* allocation_start;
#endif
        public heap_segment* allocation_segment;
        public byte* allocation_context_start_region;
#if USE_REGIONS
        public heap_segment* tail_region;
        public heap_segment* tail_ro_region;
#endif
        public allocator free_list_allocator;
        public nuint free_list_allocated;
        public nuint end_seg_allocated;
        public nuint condemned_allocated;
        public nuint sweep_allocated;
        public int allocate_end_seg_p;
        public nuint free_list_space;
        public nuint free_obj_space;
        public nuint allocation_size;
#if !USE_REGIONS
        public byte* plan_allocation_start;
        public nuint plan_allocation_start_size;
#endif
        public nuint pinned_allocation_compact_size;
        public nuint pinned_allocation_sweep_size;
        public int gen_num;
#if TARGET_64BIT && !TARGET_WASM
        public int set_bgc_mark_bit_p;
        public byte* last_free_list_allocated;
#endif

        // C# does not run a struct constructor for embedded or unmanaged storage, so the embedded
        // free_list_allocator of a zero-initialized generation would be left with zero buckets. The
        // native allocator default constructor -- which the containing gc_heap runs when it creates
        // the generation_table -- is load-bearing for the young generations, so reproduce it here.
        // Every other field stays zero, matching the native default; make_generation fills in the
        // rest once the generation is wired to its segments.
        public static void initialize(generation* inst)
        {
            allocator.initialize(&inst->free_list_allocator);
        }

        public static gc_alloc_context* generation_alloc_context(generation* inst) => &inst->allocation_context;

#if !USE_REGIONS
        public static ref byte* generation_allocation_start(generation* inst) => ref inst->allocation_start;
#endif

        public static ref byte* generation_allocation_pointer(generation* inst) => ref inst->allocation_context.alloc_ptr;

        public static ref byte* generation_allocation_limit(generation* inst) => ref inst->allocation_context.alloc_limit;

        public static allocator* generation_allocator(generation* inst) => &inst->free_list_allocator;

        public static ref heap_segment* generation_start_segment(generation* inst) => ref inst->start_segment;

#if USE_REGIONS
        public static ref heap_segment* generation_tail_region(generation* inst) => ref inst->tail_region;

        public static ref heap_segment* generation_tail_ro_region(generation* inst) => ref inst->tail_ro_region;

        public static heap_segment* generation_start_segment_rw(generation* inst) =>
            inst->tail_ro_region is not null ? inst->tail_ro_region : inst->start_segment;
#endif

        public static ref heap_segment* generation_allocation_segment(generation* inst) => ref inst->allocation_segment;

#if !USE_REGIONS
        public static ref byte* generation_plan_allocation_start(generation* inst) => ref inst->plan_allocation_start;

        public static ref nuint generation_plan_allocation_start_size(generation* inst) => ref inst->plan_allocation_start_size;
#endif

        public static ref byte* generation_allocation_context_start_region(generation* inst) => ref inst->allocation_context_start_region;

        public static ref nuint generation_free_list_space(generation* inst) => ref inst->free_list_space;

        public static ref nuint generation_free_obj_space(generation* inst) => ref inst->free_obj_space;

        public static ref nuint generation_allocation_size(generation* inst) => ref inst->allocation_size;

        public static ref nuint generation_pinned_allocation_sweep_size(generation* inst) => ref inst->pinned_allocation_sweep_size;

        public static ref nuint generation_pinned_allocation_compact_size(generation* inst) => ref inst->pinned_allocation_compact_size;

        public static ref nuint generation_free_list_allocated(generation* inst) => ref inst->free_list_allocated;

        public static ref nuint generation_end_seg_allocated(generation* inst) => ref inst->end_seg_allocated;

        public static ref int generation_allocate_end_seg_p(generation* inst) => ref inst->allocate_end_seg_p;

        public static ref nuint generation_condemned_allocated(generation* inst) => ref inst->condemned_allocated;

        public static ref nuint generation_sweep_allocated(generation* inst) => ref inst->sweep_allocated;

        // These are allocations we did while doing planning, we use this to calculate free list efficiency.
        public static nuint generation_total_plan_allocated(generation* inst) =>
            inst->free_list_allocated + inst->end_seg_allocated + inst->condemned_allocated;

#if TARGET_64BIT && !TARGET_WASM
        public static ref int generation_set_bgc_mark_bit_p(generation* inst) => ref inst->set_bgc_mark_bit_p;

        public static ref byte* generation_last_free_list_allocated(generation* inst) => ref inst->last_free_list_allocated;
#endif
    }

    internal enum alloc_wait_reason
    {
        awr_ignored = -1,
        awr_low_memory = 0,
        awr_low_ephemeral = 1,
        awr_gen0_alloc = 2,
        awr_loh_alloc = 3,
        awr_alloc_loh_low_mem = 4,
        awr_loh_oos = 5,
        awr_gen0_oos_bgc = 6,
        awr_loh_oos_bgc = 7,
        awr_fgc_wait_for_bgc = 8,
        awr_get_loh_seg = 9,
        awr_loh_alloc_during_plan = 10,
        awr_uoh_alloc_during_bgc = 11,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct alloc_thread_wait_data
    {
        public int awr;
    }

    internal enum msl_take_state
    {
        mt_get_large_seg = 0,
        mt_bgc_uoh_sweep,
        mt_wait_bgc,
        mt_block_gc,
        mt_clr_mem,
        mt_clr_large_mem,
        mt_t_eph_gc,
        mt_t_full_gc,
        mt_alloc_small,
        mt_alloc_large,
        mt_alloc_small_cant,
        mt_alloc_large_cant,
        mt_try_alloc,
        mt_try_budget,
        mt_try_servo_budget,
        mt_decommit_step,
    }

    internal enum gc_pause_mode
    {
        pause_batch = 0,
        pause_interactive = 1,
        pause_low_latency = 2,
        pause_sustained_low_latency = 3,
        pause_no_gc = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct gc_mechanisms
    {
        public nuint gc_index;
        public int condemned_generation;
        public int promotion;
        public int compaction;
        public int loh_compaction;
        public int heap_expansion;
        public uint concurrent;
        public int demotion;
        public int card_bundles;
        public int gen0_reduction_count;
        public int should_lock_elevation;
        public int elevation_locked_count;
        public int elevation_reduced;
        public int minimal_gc;
        public gc_reason reason;
        public gc_pause_mode pause_mode;
        public int found_finalizers;
#if BACKGROUND_GC
        public int background_p;
        public int b_state;
#endif
        public uint entry_memory_load;
        public ulong entry_available_physical_mem;
        public uint exit_memory_load;

        public void init_mechanisms()
        {
            condemned_generation = 0;
            promotion = 0;
            compaction = 1;
            loh_compaction = gc_heap.loh_compaction_requested();
            heap_expansion = 0;
            concurrent = 0;
            demotion = 0;
            elevation_reduced = 0;
            found_finalizers = 0;
#if BACKGROUND_GC
            background_p = gc_heap.background_running_p() ? 1 : 0;
#endif
            entry_memory_load = 0;
            entry_available_physical_mem = 0;
            exit_memory_load = 0;
        }

        public void first_init()
        {
            gc_index = 0;
            gen0_reduction_count = 0;
            should_lock_elevation = 0;
            elevation_locked_count = 0;
            reason = gc_reason.reason_empty;
#if BACKGROUND_GC
            pause_mode = gc_heap.gc_can_use_concurrent
                ? gc_pause_mode.pause_interactive
                : gc_pause_mode.pause_batch;
#if DEBUG
            int debug_pause_mode = unchecked((int)GCConfig.GetLatencyMode());
            if (debug_pause_mode >= 0)
            {
                Debug.Assert(debug_pause_mode <= (int)gc_pause_mode.pause_sustained_low_latency);
                pause_mode = (gc_pause_mode)debug_pause_mode;
            }
#endif
#else
            pause_mode = gc_pause_mode.pause_batch;
#endif
            init_mechanisms();
        }
    }

    internal enum gc_loh_compaction_mode
    {
        loh_compaction_default = 1,
        loh_compaction_once = 2,
        loh_compaction_auto = 4,
    }

    internal enum set_pause_mode_status
    {
        set_pause_mode_success = 0,
        set_pause_mode_no_gc = 1,
    }

    internal enum gc_latency_level
    {
        latency_level_first = 0,
        latency_level_memory_footprint = latency_level_first,
        latency_level_balanced = 1,
        latency_level_last = latency_level_balanced,
        latency_level_default = latency_level_balanced,
    }

    internal enum gc_tuning_point
    {
        tuning_deciding_condemned_gen = 0,
        tuning_deciding_full_gc = 1,
        tuning_deciding_compaction = 2,
        tuning_deciding_expansion = 3,
        tuning_deciding_promote_ephemeral = 4,
        tuning_deciding_short_on_seg = 5,
    }

    internal enum gc_oh_num
    {
        soh = 0,
        loh = 1,
        poh = 2,
        unknown = -1,
    }

    internal enum memory_type
    {
        memory_type_reserved = 0,
        memory_type_committed = 1,
    }

    internal enum allocation_state
    {
        a_state_start = 0,
        a_state_can_allocate,
        a_state_cant_allocate,
        a_state_retry_allocate,
        a_state_try_fit,
        a_state_try_fit_new_seg,
        a_state_try_fit_after_cg,
        a_state_try_fit_after_bgc,
        a_state_try_free_full_seg_in_bgc,
        a_state_try_free_after_bgc,
        a_state_try_seg_end,
        a_state_acquire_seg,
        a_state_acquire_seg_after_cg,
        a_state_acquire_seg_after_bgc,
        a_state_check_and_wait_for_bgc,
        a_state_trigger_full_compact_gc,
        a_state_trigger_ephemeral_gc,
        a_state_trigger_2nd_ephemeral_gc,
        a_state_check_retry_seg,
        a_state_max,
    }

    internal enum enter_msl_status
    {
        msl_entered,
        msl_retry_different_heap,
    }

    internal enum msl_enter_state
    {
        me_acquire,
        me_release,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct no_gc_region_info
    {
        public nuint soh_allocation_size;
        public nuint loh_allocation_size;
        public nuint started;
        public nuint num_gcs;
        public nuint num_gcs_induced;
        public start_no_gc_region_status start_status;
        public gc_pause_mode saved_pause_mode;
        public nuint saved_gen0_min_size;
        public nuint saved_gen3_min_size;
        public int minimal_gc_p;
        public nuint soh_withheld_budget;
        public nuint loh_withheld_budget;
        public NoGCRegionCallbackFinalizerWorkItem* callback;
    }

    // Ported from mark in gcinternal.h. SHORT_PLUGS is unconditionally defined in gcpriv.h, so
    // allocation_context_start_region is likewise unconditional here. COLLECTIBLE_CLASS is
    // omitted for NativeAOT by gcpriv.h because FEATURE_NATIVEAOT is defined; retain its methods
    // under the native feature symbol for configurations that enable it.
    //
#pragma warning disable CS8981 // Native type names are intentionally preserved.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct mark
#pragma warning restore CS8981
    {
        public byte* first;
        public nuint len;

        public gap_reloc_pair saved_pre_plug;
        public gap_reloc_pair saved_pre_plug_reloc;
        public gap_reloc_pair saved_post_plug;
        public gap_reloc_pair saved_post_plug_reloc;

        public byte* saved_pre_plug_info_reloc_start;
        public byte* saved_post_plug_info_start;
        public byte* allocation_context_start_region;

        public int saved_pre_p;
        public int saved_post_p;

#if DEBUG
        public gap_reloc_pair saved_post_plug_debug;
#endif

        public static nuint get_max_short_bits() => (nuint)(sizeof(gap_reloc_pair) / sizeof(byte*));

        public static nuint get_pre_short_start_bit() =>
            (nuint)((sizeof(int) * 8) - 1) - get_max_short_bits();

        public static int pre_short_p(mark* inst) =>
            inst->saved_pre_p & (1 << ((sizeof(int) * 8) - 1));

        public static void set_pre_short(mark* inst)
        {
            inst->saved_pre_p |= 1 << ((sizeof(int) * 8) - 1);
        }

        public static void set_pre_short_bit(mark* inst, nuint bit)
        {
            inst->saved_pre_p |= 1 << (int)(get_pre_short_start_bit() + bit);
        }

        public static int pre_short_bit_p(mark* inst, nuint bit) =>
            inst->saved_pre_p & (1 << (int)(get_pre_short_start_bit() + bit));

#if COLLECTIBLE_CLASS
        public static void set_pre_short_collectible(mark* inst)
        {
            inst->saved_pre_p |= 2;
        }

        public static int pre_short_collectible_p(mark* inst) => inst->saved_pre_p & 2;
#endif

        public static nuint get_post_short_start_bit() =>
            (nuint)((sizeof(int) * 8) - 1) - get_max_short_bits();

        public static int post_short_p(mark* inst) =>
            inst->saved_post_p & (1 << ((sizeof(int) * 8) - 1));

        public static void set_post_short(mark* inst)
        {
            inst->saved_post_p |= 1 << ((sizeof(int) * 8) - 1);
        }

        public static void set_post_short_bit(mark* inst, nuint bit)
        {
            inst->saved_post_p |= 1 << (int)(get_post_short_start_bit() + bit);
        }

        public static int post_short_bit_p(mark* inst, nuint bit) =>
            inst->saved_post_p & (1 << (int)(get_post_short_start_bit() + bit));

#if COLLECTIBLE_CLASS
        public static void set_post_short_collectible(mark* inst)
        {
            inst->saved_post_p |= 2;
        }

        public static int post_short_collectible_p(mark* inst) => inst->saved_post_p & 2;
#endif

        public static byte* get_plug_address(mark* inst) => inst->first;

        public static int has_pre_plug_info(mark* inst) => inst->saved_pre_p;

        public static int has_post_plug_info(mark* inst) => inst->saved_post_p;

        public static gap_reloc_pair* get_pre_plug_reloc_info(mark* inst) => &inst->saved_pre_plug_reloc;

        public static gap_reloc_pair* get_post_plug_reloc_info(mark* inst) => &inst->saved_post_plug_reloc;

        public static void set_pre_plug_info_reloc_start(mark* inst, byte* reloc)
        {
            inst->saved_pre_plug_info_reloc_start = reloc;
        }

        public static byte* get_post_plug_info_start(mark* inst) => inst->saved_post_plug_info_start;

        public static void swap_pre_plug_and_saved(mark* inst)
        {
            gap_reloc_pair temp = *(gap_reloc_pair*)(inst->first - sizeof(plug_and_gap));
            *(gap_reloc_pair*)(inst->first - sizeof(plug_and_gap)) = inst->saved_pre_plug_reloc;
            inst->saved_pre_plug_reloc = temp;
        }

        public static void swap_post_plug_and_saved(mark* inst)
        {
            gap_reloc_pair temp = *(gap_reloc_pair*)inst->saved_post_plug_info_start;
            *(gap_reloc_pair*)inst->saved_post_plug_info_start = inst->saved_post_plug_reloc;
            inst->saved_post_plug_reloc = temp;
        }

        public static void swap_pre_plug_and_saved_for_profiler(mark* inst)
        {
            gap_reloc_pair temp = *(gap_reloc_pair*)(inst->first - sizeof(plug_and_gap));
            *(gap_reloc_pair*)(inst->first - sizeof(plug_and_gap)) = inst->saved_pre_plug;
            inst->saved_pre_plug = temp;
        }

        public static void swap_post_plug_and_saved_for_profiler(mark* inst)
        {
            gap_reloc_pair temp = *(gap_reloc_pair*)inst->saved_post_plug_info_start;
            *(gap_reloc_pair*)inst->saved_post_plug_info_start = inst->saved_post_plug;
            inst->saved_post_plug = temp;
        }

        public static nuint recover_plug_info(mark* inst)
        {
            nuint recovered_sweep_size = 0;

            if (inst->saved_pre_p != 0)
            {
                if (gc_heap.settings.compaction != 0)
                {
                    *(gap_reloc_pair*)inst->saved_pre_plug_info_reloc_start = inst->saved_pre_plug_reloc;
                }
                else
                {
                    *(gap_reloc_pair*)(inst->first - sizeof(plug_and_gap)) = inst->saved_pre_plug;
                    recovered_sweep_size += (nuint)sizeof(gap_reloc_pair);
                }
            }

            if (inst->saved_post_p != 0)
            {
                if (gc_heap.settings.compaction != 0)
                {
                    *(gap_reloc_pair*)inst->saved_post_plug_info_start = inst->saved_post_plug_reloc;
                }
                else
                {
                    *(gap_reloc_pair*)inst->saved_post_plug_info_start = inst->saved_post_plug;
                    recovered_sweep_size += (nuint)sizeof(gap_reloc_pair);
                }
            }

            return recovered_sweep_size;
        }
    }

#pragma warning disable CS8981 // Native type names are intentionally preserved.
    internal unsafe partial struct gc_heap
#pragma warning restore CS8981
    {
        public static byte* get_plug_start_in_saved(byte* old_loc, mark* pinned_plug_entry)
        {
            byte* saved_pre_plug_info = (byte*)mark.get_pre_plug_reloc_info(pinned_plug_entry);
            byte* plug_start_in_saved = saved_pre_plug_info + (old_loc - (pinned_plug(pinned_plug_entry) - sizeof(plug_and_gap)));
            return plug_start_in_saved;
        }
    }

    // Ported from the card-table schema and dependency-free helpers in gcinternal.h. The first
    // three fields are the dac_card_table_info prefix, and card_bundle_table is unconditional
    // because gcpriv.h defines CARD_BUNDLE for every collector build. BACKGROUND_GC is absent
    // only for WASM, so mark_array follows the managed build symbol used by gcpriv.h.
#pragma warning disable CS8981 // Native type names are intentionally preserved.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct card_table_info
#pragma warning restore CS8981
    {
        public uint recount;
        public nuint size;
        public uint* next_card_table;
        public byte* lowest_address;
        public byte* highest_address;
        public short* brick_table;
        public uint* card_bundle_table;
#if BACKGROUND_GC
        public uint* mark_array;
#endif

#if BACKGROUND_GC
#if TARGET_64BIT
        public const nuint mark_bit_pitch = 16;
#else
        public const nuint mark_bit_pitch = 8;
#endif
        public const nuint mark_word_width = 32;
        public const nuint mark_word_size = mark_word_width * mark_bit_pitch;
#endif

#if TARGET_64BIT
        public const nuint brick_size = 4096;
#else
        public const nuint brick_size = 2048;
#endif
        public const nuint GC_PAGE_SIZE = 0x1000;
        public const nuint card_word_width = 32;
#if TARGET_64BIT
        public const nuint card_size = 2 * GC_PAGE_SIZE / card_word_width;
#else
        public const nuint card_size = GC_PAGE_SIZE / card_word_width;
#endif
        public const nuint card_bundle_word_width = 32;
        public const nuint card_bundle_size = GC_PAGE_SIZE / (sizeof(uint) * card_bundle_word_width);
        public const uint SH_TH_CARD_BUNDLE = 40 * 1024 * 1024;
        public const uint MH_TH_CARD_BUNDLE = 180 * 1024 * 1024;
        public const uint DECOMMIT_TIME_STEP_MILLISECONDS = 100;
#if TARGET_64BIT
        public const uint MAX_ALLOWED_MEM_LOAD = 85;
        public const nuint MIN_YOUNGEST_GEN_DESIRED = 16 * 1024 * 1024;
#endif

        public static nuint gib(nuint num)
        {
            return num / 1024 / 1024 / 1024;
        }

        public static byte* align_on_brick(byte* add)
        {
            return (byte*)unchecked(((nuint)add + brick_size - 1) & ~(brick_size - 1));
        }

        public static nuint card_word(nuint card)
        {
            return card / card_word_width;
        }

        public static uint card_bit(nuint card)
        {
            return (uint)(card % card_word_width);
        }

        public static nuint gcard_of(byte* @object)
        {
            return (nuint)@object / card_size;
        }

        public static nuint card_bundle_word(nuint cardb)
        {
            return cardb / card_bundle_word_width;
        }

        public static uint card_bundle_bit(nuint cardb)
        {
            return (uint)(cardb % card_bundle_word_width);
        }

        public static nuint align_cardw_on_bundle(nuint cardw)
        {
            return unchecked((cardw + card_bundle_size - 1) & ~(card_bundle_size - 1));
        }

        public static nuint cardw_card_bundle(nuint cardw)
        {
            return cardw / card_bundle_size;
        }

        public static nuint card_bundle_cardw(nuint cardb)
        {
            return cardb * card_bundle_size;
        }

        public static uint* translate_card_bundle_table(uint* cb, byte* lowest_address)
        {
            const nuint HeapBytesForBundleWord =
                card_size * card_word_width * card_bundle_size * card_bundle_word_width;
            return (uint*)unchecked((nuint)cb - (((nuint)lowest_address / HeapBytesForBundleWord) * sizeof(uint)));
        }

        public static byte* align_lower_brick(byte* add)
        {
            return (byte*)((nuint)add & ~(brick_size - 1));
        }

        public static nuint size_brick_of(byte* from, byte* end)
        {
            return ((nuint)(end - from) / brick_size) * sizeof(short);
        }

        public static byte* align_on_card(byte* add)
        {
            return (byte*)unchecked(((nuint)add + card_size - 1) & ~(card_size - 1));
        }

        public static byte* align_on_card_word(byte* add)
        {
            const nuint CardWordSize = card_size * card_word_width;
            return (byte*)unchecked(((nuint)add + CardWordSize - 1) & ~(CardWordSize - 1));
        }

        public static byte* align_lower_card(byte* add)
        {
            return (byte*)((nuint)add & ~(card_size - 1));
        }

        public static nuint count_card_of(byte* from, byte* end)
        {
            return card_word(gcard_of(end - 1)) - card_word(gcard_of(from)) + 1;
        }

        public static nuint size_card_of(byte* from, byte* end)
        {
            return count_card_of(from, end) * sizeof(uint);
        }

        public static nuint size_card_bundle_of(byte* from, byte* end)
        {
            const nuint HeapBytesForBundleWord =
                card_size * card_word_width * card_bundle_size * card_bundle_word_width;
            byte* alignedFrom = (byte*)((nuint)from & ~(HeapBytesForBundleWord - 1));
            byte* alignedEnd = (byte*)unchecked(
                ((nuint)end + HeapBytesForBundleWord - 1) &
                ~(HeapBytesForBundleWord - 1));
            return (nuint)(alignedEnd - alignedFrom) /
                HeapBytesForBundleWord *
                sizeof(uint);
        }

        public static ref uint card_table_refcount(uint* c_table)
        {
            return ref *(uint*)((byte*)c_table - sizeof(card_table_info));
        }

        public static ref nuint card_table_size(uint* c_table)
        {
            return ref ((card_table_info*)((byte*)c_table - sizeof(card_table_info)))->size;
        }

        public static ref uint* card_table_next(uint* c_table)
        {
            return ref ((card_table_info*)((byte*)c_table - sizeof(card_table_info)))->next_card_table;
        }

        public static ref byte* card_table_lowest_address(uint* c_table)
        {
            return ref ((card_table_info*)((byte*)c_table - sizeof(card_table_info)))->lowest_address;
        }

        public static ref byte* card_table_highest_address(uint* c_table)
        {
            return ref ((card_table_info*)((byte*)c_table - sizeof(card_table_info)))->highest_address;
        }

        public static ref short* card_table_brick_table(uint* c_table)
        {
            return ref ((card_table_info*)((byte*)c_table - sizeof(card_table_info)))->brick_table;
        }

        public static ref uint* card_table_card_bundle_table(uint* c_table)
        {
            return ref ((card_table_info*)((byte*)c_table - sizeof(card_table_info)))->card_bundle_table;
        }

#if BACKGROUND_GC
        public static ref uint* card_table_mark_array(uint* c_table)
        {
            return ref ((card_table_info*)((byte*)c_table - sizeof(card_table_info)))->mark_array;
        }
#endif

        public static uint* translate_card_table(uint* ct)
        {
            return (uint*)unchecked(
                (nuint)ct
                - (card_word(gcard_of(card_table_lowest_address(ct))) * sizeof(uint)));
        }

#if BACKGROUND_GC
        public static byte* align_on_mark_bit(byte* add)
        {
            return (byte*)unchecked(((nuint)add + mark_bit_pitch - 1) & ~(mark_bit_pitch - 1));
        }

        public static byte* align_lower_mark_bit(byte* add)
        {
            return (byte*)((nuint)add & ~(mark_bit_pitch - 1));
        }

        public static int is_aligned_on_mark_word(byte* add)
        {
            return (nuint)add == ((nuint)add & ~(mark_word_size - 1)) ? 1 : 0;
        }

        public static byte* align_on_mark_word(byte* add)
        {
            return (byte*)unchecked(((nuint)add + mark_word_size - 1) & ~(mark_word_size - 1));
        }

        public static byte* align_lower_mark_word(byte* add)
        {
            return (byte*)((nuint)add & ~(mark_word_size - 1));
        }

        public static nuint mark_bit_of(byte* add)
        {
            return (nuint)add / mark_bit_pitch;
        }

        public static uint mark_bit_bit(nuint mark_bit)
        {
            return (uint)(mark_bit % mark_word_width);
        }

        public static nuint mark_bit_bit_of(byte* add)
        {
            return ((nuint)add / mark_bit_pitch) % mark_word_width;
        }

        public static nuint mark_bit_word(nuint mark_bit)
        {
            return mark_bit / mark_word_width;
        }

        public static nuint mark_word_of(byte* add)
        {
            return (nuint)add / mark_word_size;
        }

        public static byte* mark_bit_address(nuint mark_bit)
        {
            return (byte*)(mark_bit * mark_bit_pitch);
        }

        public static nuint size_mark_array_of(byte* from, byte* end)
        {
            return sizeof(uint) * ((nuint)(end - from) / mark_word_size);
        }
#endif
    }

    internal enum interesting_data_point
    {
        idp_pre_short = 0,
        idp_post_short = 1,
        idp_merged_pin = 2,
        idp_converted_pin = 3,
        idp_pre_pin = 4,
        idp_post_pin = 5,
        idp_pre_and_post_pin = 6,
        idp_pre_short_padded = 7,
        idp_post_short_padded = 8,
        max_idp_count,
    }

#pragma warning disable CS8981 // Native type names are intentionally preserved.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct plug
    {
        public byte* skew0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct pair
    {
        public short left;
        public short right;
    }
#pragma warning restore CS8981

    [StructLayout(LayoutKind.Sequential)]
    internal struct plug_and_pair
    {
        public pair m_pair;
        public plug m_plug;
    }

    internal unsafe partial struct gc_heap
    {
#if USE_REGIONS
        internal struct make_free_args
        {
            public int free_list_gen_number;
            public generation* free_list_gen;
            public byte* highest_plug;
        }

        internal struct relocate_args
        {
            public byte* last_plug;
            public int is_shortened;
            public mark* pinned_plug_entry;
        }

        internal struct compact_args
        {
            public int copy_cards_p;
            public byte* last_plug;
            public nint last_plug_relocation;
            public byte* before_last_plug;
            public nuint current_compacted_brick;
            public int is_shortened;
            public mark* pinned_plug_entry;
            public int check_gennum_p;
            public int src_gennum;
        }
#endif

        public static short node_left_child(byte* node)
        {
            return ((plug_and_pair*)node)[-1].m_pair.left;
        }

        public static short node_right_child(byte* node)
        {
            return ((plug_and_pair*)node)[-1].m_pair.right;
        }

        public static void set_node_left_child(byte* node, nint val)
        {
            Debug.Assert(val > -(nint)card_table_info.brick_size);
            Debug.Assert(val < (nint)card_table_info.brick_size);
            Debug.Assert(((nuint)val & (nuint)get_alignment_constant(small_object_p: true)) == 0);
            ((plug_and_pair*)node)[-1].m_pair.left = (short)val;
            Debug.Assert(node_left_child(node) == val);
        }

        public static void set_node_right_child(byte* node, nint val)
        {
            Debug.Assert(val > -(nint)card_table_info.brick_size);
            Debug.Assert(val < (nint)card_table_info.brick_size);
            Debug.Assert(((nuint)val & (nuint)get_alignment_constant(small_object_p: true)) == 0);
            ((plug_and_pair*)node)[-1].m_pair.right = (short)val;
            Debug.Assert(node_right_child(node) == val);
        }

        public static nuint node_gap_size(byte* node)
        {
            return unchecked((nuint)((plug_and_gap*)node)[-1].gap);
        }

        public static void set_gap_size(byte* node, nuint size)
        {
            Debug.Assert(Align(size) == size);

            ((plug_and_gap*)node)[-1].reloc = 0;
            ((plug_and_gap*)node)[-1].lr = 0;
            ((plug_and_gap*)node)[-1].gap = unchecked((nint)size);

            Debug.Assert(size == 0 || size >= (nuint)sizeof(plug_and_reloc));
        }

        public static nint loh_node_relocation_distance(byte* node)
        {
            return ((loh_obj_and_pad*)node)[-1].reloc;
        }

        public static void loh_set_node_relocation_distance(byte* node, nint val)
        {
            ((loh_obj_and_pad*)node)[-1].reloc = val;
        }

        public static nint node_relocation_distance(byte* node)
        {
            return ((plug_and_reloc*)node)[-1].reloc & ~(nint)3;
        }

        public static nint node_realigned(byte* node)
        {
            return ((plug_and_reloc*)node)[-1].reloc & 1;
        }

        public static void set_node_relocation_distance(byte* node, nint val)
        {
            Debug.Assert(val == (val & ~(nint)3));
            ref nint place = ref ((plug_and_reloc*)node)[-1].reloc;
            place &= 1;
            place |= val;
        }

        public static void set_node_realigned(byte* node)
        {
            ((plug_and_reloc*)node)[-1].reloc |= 1;
        }

        public static nint node_left_p(byte* node)
        {
            return ((plug_and_reloc*)node)[-1].reloc & 2;
        }

        public static void set_node_left(byte* node)
        {
            ((plug_and_reloc*)node)[-1].reloc |= 2;
        }

        public static byte* tree_search(byte* tree, byte* old_address)
        {
            byte* candidate = null;
            int cn;
            while (true)
            {
                if (tree < old_address)
                {
                    if ((cn = node_right_child(tree)) != 0)
                    {
                        Debug.Assert(candidate < tree);
                        candidate = tree;
                        tree += cn;
                        // The native Prefetch of the next node's left child is a performance hint
                        // with no cross-platform managed equivalent.
                        continue;
                    }
                    else
                    {
                        break;
                    }
                }
                else if (tree > old_address)
                {
                    if ((cn = node_left_child(tree)) != 0)
                    {
                        tree += cn;
                        // The native Prefetch of the next node's left child is a performance hint
                        // with no cross-platform managed equivalent.
                        continue;
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            if (tree <= old_address)
            {
                return tree;
            }
            else if (candidate is not null)
            {
                return candidate;
            }
            else
            {
                return tree;
            }
        }
    }

    // Port of the bounded workstation CFinalize closure in gcpriv.h and finalization.cpp.
    // The individually named fields preserve the native contiguous m_FillPointers array without
    // creating a managed array.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct CFinalize
    {
        private const int ExtraSegCount = 2;
        private const int FinalizerListSeg = (int)gc_generation_num.total_generation_count + 1;
        private const int CriticalFinalizerListSeg = (int)gc_generation_num.total_generation_count;
        private const int FreeListSeg = (int)gc_generation_num.total_generation_count + ExtraSegCount;
        private const int FreeList = FreeListSeg;
        private const int FinalizerStartSeg = CriticalFinalizerListSeg;
        private const int FinalizerMaxSeg = FinalizerListSeg;
        private const int MaxSeg = FreeListSeg;
        private const int InitialFinalizerArraySize = 100;

        private byte** m_FillPointer0;
        private byte** m_FillPointer1;
        private byte** m_FillPointer2;
        private byte** m_FillPointer3;
        private byte** m_FillPointer4;
        private byte** m_FillPointer5;
        private byte** m_FillPointer6;
        private byte** m_Array;
        private byte** m_EndArray;
        private nuint m_PromotedCount;
        private int @lock;

        public static CFinalize* Allocate()
        {
            CFinalize* finalizeQueue = (CFinalize*)SyncImports.ManagedGC_AllocZeroed((nuint)sizeof(CFinalize));
            if (finalizeQueue is null)
            {
                return null;
            }

            if (!finalizeQueue->Initialize())
            {
                SyncImports.ManagedGC_Free(finalizeQueue);
                return null;
            }

            return finalizeQueue;
        }

        public static void Free(CFinalize* finalizeQueue)
        {
            if (finalizeQueue is not null)
            {
                finalizeQueue->Destroy();
                SyncImports.ManagedGC_Free(finalizeQueue);
            }
        }

        public bool Initialize()
        {
            m_Array = (byte**)SyncImports.ManagedGC_AllocZeroed(
                (nuint)InitialFinalizerArraySize * (nuint)sizeof(byte*));
            if (m_Array is null)
            {
                return false;
            }

            m_EndArray = m_Array + InitialFinalizerArraySize;
            fixed (byte*** fillPointers = &m_FillPointer0)
            {
                for (int i = 0; i < FreeList; i++)
                {
                    fillPointers[i] = m_Array;
                }
            }

            m_PromotedCount = 0;
            @lock = -1;
            return true;
        }

        public void Destroy()
        {
            if (m_Array is not null)
            {
                SyncImports.ManagedGC_Free(m_Array);
            }

            fixed (byte*** fillPointers = &m_FillPointer0)
            {
                for (int i = 0; i < FreeList; i++)
                {
                    fillPointers[i] = null;
                }
            }

            m_Array = null;
            m_EndArray = null;
            m_PromotedCount = 0;
            @lock = -1;
        }

        public nuint GetPromotedCount() => m_PromotedCount;

        public void EnterFinalizeLock()
        {
            fixed (int* lockAddress = &@lock)
            {
            retry:
                if (Interlocked.CompareExchange(lockAddress, 0, -1) >= 0)
                {
                    uint i = 0;
                    while (GCEnv.VolatileLoadWithoutBarrier(lockAddress) >= 0)
                    {
                        if (GCToEEInterface.GetCurrentProcessCpuCount() > 1)
                        {
                            const int SpinCount = 128;
                            for (int j = 0; j < SpinCount; j++)
                            {
                                if (GCEnv.VolatileLoadWithoutBarrier(lockAddress) < 0)
                                {
                                    break;
                                }

                                GCEnv.YieldProcessor();
                            }
                        }

                        if (GCEnv.VolatileLoadWithoutBarrier(lockAddress) < 0)
                        {
                            break;
                        }

                        if ((++i & 7) != 0)
                        {
                            GCToOSInterface.YieldThread(0);
                        }
                        else
                        {
                            GCToOSInterface.Sleep(5);
                        }
                    }

                    goto retry;
                }
            }
        }

        public void LeaveFinalizeLock()
        {
            fixed (int* lockAddress = &@lock)
            {
                GCEnv.VolatileStore(lockAddress, -1);
            }
        }

        public bool RegisterForFinalization(int gen, byte* obj, nuint size = 0)
        {
            EnterFinalizeLock();

            uint destination = gen_segment(gen);
            fixed (byte*** fillPointers = &m_FillPointer0)
            {
                byte*** source = fillPointers + (FreeListSeg - 1);
                if (*source == seg_queue_limit(fillPointers, m_EndArray, FreeListSeg))
                {
                    if (!GrowArray(fillPointers))
                    {
                        LeaveFinalizeLock();
                        if (((CObjectHeader*)obj)->GetMethodTable() is null)
                        {
                            Debug.Assert(size >= gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size));
                            gc_heap.make_unused_array(obj, size);
                        }

                        return false;
                    }

                    source = fillPointers + (FreeListSeg - 1);
                }

                byte*** destinationLimit = fillPointers + (int)destination;
                do
                {
                    if (*source != *(source - 1))
                    {
                        **source = *(*(source - 1));
                    }

                    (*source)++;
                    source--;
                }
                while (source > destinationLimit);

                **source = obj;
                (*source)++;
            }

            LeaveFinalizeLock();
            return true;
        }

        public byte* GetNextFinalizableObject(bool only_non_critical = false)
        {
            byte* obj = null;
            EnterFinalizeLock();

            fixed (byte*** fillPointers = &m_FillPointer0)
            {
                if (!is_seg_empty(fillPointers, m_Array, m_EndArray, FinalizerListSeg))
                {
                    obj = *(--fillPointers[FinalizerListSeg]);
                }
                else if (!only_non_critical &&
                    !is_seg_empty(fillPointers, m_Array, m_EndArray, CriticalFinalizerListSeg))
                {
                    obj = *(--fillPointers[CriticalFinalizerListSeg]);
                    --fillPointers[FinalizerListSeg];
                }
            }

            LeaveFinalizeLock();
            return obj;
        }

        public nuint GetNumberFinalizableObjects()
        {
            fixed (byte*** fillPointers = &m_FillPointer0)
            {
                return (nuint)(seg_queue_limit(fillPointers, m_EndArray, FinalizerMaxSeg) -
                    seg_queue(fillPointers, m_Array, FinalizerStartSeg));
            }
        }

        public void MoveItem(byte** fromIndex, uint fromSeg, uint toSeg)
        {
            Debug.Assert(fromSeg != toSeg);

            int step = fromSeg > toSeg ? -1 : 1;
            fixed (byte*** fillPointers = &m_FillPointer0)
            {
                byte** sourceIndex = fromIndex;
                for (uint i = fromSeg; i != toSeg; i = (uint)((int)i + step))
                {
                    byte*** destinationFill = fillPointers + (int)i + ((step - 1) / 2);
                    byte** destinationIndex = *destinationFill - ((step + 1) / 2);
                    if (sourceIndex != destinationIndex)
                    {
                        byte* tmp = *sourceIndex;
                        *sourceIndex = *destinationIndex;
                        *destinationIndex = tmp;
                    }

                    *destinationFill -= step;
                    sourceIndex = destinationIndex;
                }
            }
        }

        public void GcScanRoots(
            delegate*<byte**, ScanContext*, uint, void> fn,
            int heapNumber,
            ScanContext* scanContext)
        {
            ScanContext localScanContext = default;
            if (scanContext is null)
            {
                scanContext = &localScanContext;
            }

            scanContext->thread_number = heapNumber;
            fixed (byte*** fillPointers = &m_FillPointer0)
            {
                byte** startIndex = seg_queue(fillPointers, m_Array, FinalizerStartSeg);
                byte** stopIndex = seg_queue_limit(fillPointers, m_EndArray, FinalizerMaxSeg);
                for (byte** current = startIndex; current < stopIndex; current++)
                {
                    fn(current, scanContext, 0);
                }
            }
        }

        public bool ScanForFinalization(
            delegate*<byte**, ScanContext*, uint, void> promote,
            int gen,
            gc_heap* heap)
        {
            _ = heap;

            ScanContext scanContext = default;
            scanContext.promotion = 1;
            scanContext.thread_count = 1;

            uint startSeg = gen_segment(gen);
            m_PromotedCount = 0;

            fixed (byte*** fillPointers = &m_FillPointer0)
            {
                for (uint segment = startSeg; segment <= gen_segment(0); segment++)
                {
                    byte** endIndex = seg_queue(fillPointers, m_Array, (int)segment);
                    byte** current = seg_queue_limit(fillPointers, m_EndArray, (int)segment);
                    while (current > endIndex)
                    {
                        current--;
                        byte* obj = *current;
                        CObjectHeader* objectHeader = (CObjectHeader*)obj;

                        if (objectHeader->IsMarked() == 0)
                        {
                            Debug.Assert(objectHeader->GetMethodTable()->HasFinalizer() != 0);

                            if (GCToEEInterface.EagerFinalized(obj) != 0)
                            {
                                MoveItem(current, segment, (uint)FreeListSeg);
                            }
                            else if ((objectHeader->GetHeader()->GetBits() & ObjHeader.BIT_SBLK_FINALIZER_RUN) != 0)
                            {
                                MoveItem(current, segment, (uint)FreeListSeg);
                                objectHeader->GetHeader()->ClrFinalizerRun();
                            }
                            else
                            {
                                m_PromotedCount++;
                                MoveItem(
                                    current,
                                    segment,
                                    objectHeader->GetMethodTable()->HasCriticalFinalizer() != 0
                                        ? (uint)CriticalFinalizerListSeg
                                        : (uint)FinalizerListSeg);
                            }
                        }
                    }
                }
            }

            bool finalizedFound;
            fixed (byte*** fillPointers = &m_FillPointer0)
            {
                finalizedFound =
                    !is_seg_empty(fillPointers, m_Array, m_EndArray, FinalizerListSeg) ||
                    !is_seg_empty(fillPointers, m_Array, m_EndArray, CriticalFinalizerListSeg);
            }

            if (finalizedFound)
            {
                GcScanRoots(promote, 0, null);
                gc_heap.settings.found_finalizers = 1;
            }

            return finalizedFound;
        }

        // Relocates all of the objects in the finalization array.
        public void RelocateFinalizationData(int gen, gc_heap* heap)
        {
            ScanContext scanContext = default;
            scanContext.promotion = 0;
#if MULTIPLE_HEAPS
            scanContext.thread_number = heap->heap_number;
            scanContext.thread_count = gc_heap.n_heaps;
#else
            _ = heap;
            scanContext.thread_count = 1;
#endif

            uint segment = gen_segment(gen);

            fixed (byte*** fillPointers = &m_FillPointer0)
            {
                byte** startIndex = seg_queue(fillPointers, m_Array, (int)segment);
                byte** stopIndex = seg_queue(fillPointers, m_Array, FreeList);
                for (byte** current = startIndex; current < stopIndex; current++)
                {
                    gc_heap.relocate(current, &scanContext);
                }
            }
        }

        public void UpdatePromotedGenerations(int gen, int gen_0_empty_p)
        {
            fixed (byte*** fillPointers = &m_FillPointer0)
            {
                if (gen_0_empty_p != 0)
                {
                    int oldestPromotedGeneration = gen + 1 < GCInterfaceOffsets.max_generation
                        ? gen + 1
                        : GCInterfaceOffsets.max_generation;
                    for (int i = oldestPromotedGeneration; i > 0; i--)
                    {
                        fillPointers[gen_segment(i)] =
                            fillPointers[gen_segment(i - 1)];
                    }

                    return;
                }

                for (int i = gen; i >= 0; i--)
                {
                    uint segment = gen_segment(i);
                    byte** startIndex = seg_queue(fillPointers, m_Array, (int)segment);
                    for (byte** current = startIndex;
                         current < seg_queue_limit(fillPointers, m_EndArray, (int)segment);
                         current++)
                    {
                        int newGeneration = (int)ManagedGCHeap.GenerationOf(*current);
                        if (newGeneration != i)
                        {
                            Debug.Assert(newGeneration <= GCInterfaceOffsets.max_generation);
                            MoveItem(current, segment, gen_segment(newGeneration));
                            if (newGeneration < i)
                            {
                                current--;
                            }
                        }
                    }
                }
            }
        }

        private bool GrowArray(byte*** fillPointers)
        {
            nuint oldArraySize = (nuint)(m_EndArray - m_Array);
            nuint newArraySize = unchecked((oldArraySize * 12) / 10);
            byte** newArray = (byte**)SyncImports.ManagedGC_AllocZeroed(newArraySize * (nuint)sizeof(byte*));
            if (newArray is null)
            {
                return false;
            }

            nuint copySize = oldArraySize * (nuint)sizeof(byte*);
            Buffer.MemoryCopy(m_Array, newArray, (long)(newArraySize * (nuint)sizeof(byte*)), (long)copySize);

            nint delta = (nint)((byte*)newArray - (byte*)m_Array);
            for (int i = 0; i < FreeList; i++)
            {
                fillPointers[i] = (byte**)((byte*)fillPointers[i] + delta);
            }

            SyncImports.ManagedGC_Free(m_Array);
            m_Array = newArray;
            m_EndArray = newArray + (nint)newArraySize;
            return true;
        }

        private static uint gen_segment(int gen)
        {
            Debug.Assert((int)gc_generation_num.total_generation_count - gen - 1 >= 0);
            return (uint)((int)gc_generation_num.total_generation_count - gen - 1);
        }

        private static byte** seg_queue(byte*** fillPointers, byte** array, int segment) =>
            segment != 0 ? fillPointers[segment - 1] : array;

        private static byte** seg_queue_limit(byte*** fillPointers, byte** endArray, int segment) =>
            segment == MaxSeg ? endArray : fillPointers[segment];

        private static bool is_seg_empty(byte*** fillPointers, byte** array, byte** endArray, int segment) =>
            seg_queue_limit(fillPointers, endArray, segment) == seg_queue(fillPointers, array, segment);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct plug_and_reloc
    {
        public nint reloc;
        public pair m_pair;
        public plug m_plug;
    }

#if TARGET_64BIT
    [StructLayout(LayoutKind.Explicit, Size = 0x20)]
#else
    [StructLayout(LayoutKind.Explicit, Size = 0x10)]
#endif
    internal struct plug_and_gap
    {
        [FieldOffset(0)]
        public nint gap;

#if TARGET_64BIT
        [FieldOffset(0x08)]
#else
        [FieldOffset(0x04)]
#endif
        public nint reloc;

#if TARGET_64BIT
        [FieldOffset(0x10)]
#else
        [FieldOffset(0x08)]
#endif
        public pair m_pair;

#if TARGET_64BIT
        [FieldOffset(0x10)]
#else
        [FieldOffset(0x08)]
#endif
        public int lr;

#if TARGET_64BIT
        [FieldOffset(0x18)]
#else
        [FieldOffset(0x0c)]
#endif
        public plug m_plug;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct gap_reloc_pair
    {
        public nuint gap;
        public nuint reloc;
        public pair m_pair;
    }

#if TARGET_64BIT
    [StructLayout(LayoutKind.Explicit, Size = 0x28)]
#else
    [StructLayout(LayoutKind.Explicit, Size = 0x18)]
#endif
    internal struct aligned_plug_and_gap
    {
        [FieldOffset(0)]
        public nuint additional_pad;

#if !TARGET_64BIT
        // DECLSPEC_ALIGN(8) raises the native struct alignment above that of its 32-bit fields.
        [FieldOffset(0)]
        private ulong _alignment;
#endif

#if TARGET_64BIT
        [FieldOffset(0x08)]
#else
        [FieldOffset(0x04)]
#endif
        public plug_and_gap plugandgap;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct loh_obj_and_pad
    {
        public nint reloc;
        public plug m_plug;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct loh_padding_obj
    {
        public byte* mt;
        public nuint len;
        public nint reloc;
        public plug m_plug;
    }
}
