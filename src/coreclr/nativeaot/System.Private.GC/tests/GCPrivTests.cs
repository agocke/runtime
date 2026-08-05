// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Threading;
#if USE_REGIONS
using allocation_callback_result = Internal.Runtime.GarbageCollection.gc_heap.allocation_callback_result;
using allocation_callback_result_kind = Internal.Runtime.GarbageCollection.gc_heap.allocation_callback_result_kind;
using allocation_deferred_operation = Internal.Runtime.GarbageCollection.gc_heap.allocation_deferred_operation;
using try_allocate_more_space_context = Internal.Runtime.GarbageCollection.gc_heap.try_allocate_more_space_context;
#endif
using SysInterlocked = System.Threading.Interlocked;
using SysVolatile = System.Threading.Volatile;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

[Collection(SyncImportsCollection.Name)]
public sealed unsafe class GCPrivTests
{
#if USE_REGIONS
    private static int s_regionAllocatorCallbackCount;
    private static nuint s_regionAllocatorCallbackLastLeftUsed;
    private static int s_allocationCallbackCount;
    private static allocation_deferred_operation s_lastAllocationDeferredOperation;
    private static int s_backgroundQueryCallbackCount;
    private static int s_budgetCheckCallbackCount;
    private static int s_highMemoryCallbackCount;
    private static int s_budgetTriggerCallbackCount;
    private static int s_fullGcCheckCallbackCount;
#endif

    [Fact]
    public void GcRandPreservesNativeSequence()
    {
        gc_rand.x = 0;

        Assert.Equal(278281UL, gc_rand.get_rand());
        Assert.Equal(496504790UL, gc_rand.get_rand());
        Assert.Equal(462394359UL, gc_rand.get_rand());
        Assert.Equal(1153920316UL, gc_rand.get_rand());
        Assert.Equal(402843317UL, gc_rand.get_rand());
    }

    [Fact]
    public void GcRandBoundedScalingPreservesNativeSequence()
    {
        gc_rand.x = 0;

        Assert.Equal(0UL, gc_rand.get_rand(10));
        Assert.Equal(2UL, gc_rand.get_rand(10));
        Assert.Equal(2UL, gc_rand.get_rand(10));
        Assert.Equal(5UL, gc_rand.get_rand(10));
        Assert.Equal(1UL, gc_rand.get_rand(10));
    }

    [Fact]
    public void GcRandConstantsMatchNativeValues()
    {
        Assert.Equal(32768u, gc_rand.MAX_YP_SPIN_COUNT_UNIT);
        Assert.Equal(400u, gc_rand.MIN_SOH_CROSS_GEN_REFS);
        Assert.Equal(800u, gc_rand.MIN_LOH_CROSS_GEN_REFS);
#if TARGET_64BIT
        Assert.Equal(1024u, gc_rand.MARK_STACK_INITIAL_LENGTH);
#else
        Assert.Equal(128u, gc_rand.MARK_STACK_INITIAL_LENGTH);
#endif
    }

    [Fact]
    public void SortedTableStoragePreservesNativeSentinelLayout()
    {
        sorted_table table = default;
        bk* slots = stackalloc bk[4];

        sorted_table.initialize(&table, 3, slots);

        Assert.Equal((nuint)(slots + 1), (nuint)sorted_table.buckets(&table));
        Assert.Equal((nuint)0, (nuint)sorted_table.last_slot(slots));
        Assert.Equal(nuint.MaxValue, (nuint)sorted_table.buckets(&table)[0].add);
    }

    [Fact]
    public void SortedTableSchemaMatchesNativeLayout()
    {
        bk bucket = default;

        Assert.Equal((nuint)0, OffsetOf(&bucket.add, &bucket));
        Assert.Equal((nuint)sizeof(nuint), OffsetOf(&bucket.val, &bucket));
        Assert.Equal(2 * sizeof(nuint), sizeof(bk));
        Assert.Equal(4 * sizeof(nuint), sizeof(sorted_table));
    }

    [Fact]
    public void SortedTableInsertAndLookupPreservePredecessorIntervals()
    {
        sorted_table table = default;
        bk* slots = stackalloc bk[8];
        sorted_table.initialize(&table, 7, slots);

        Assert.Equal(1, sorted_table.insert(&table, (byte*)0x3000, 30));
        Assert.Equal(1, sorted_table.insert(&table, (byte*)0x1000, 10));
        Assert.Equal(1, sorted_table.insert(&table, (byte*)0x2000, 20));

        AssertSortedTableLookup(&table, 0x1000, 0x1000, 10);
        AssertSortedTableLookup(&table, 0x1FFF, 0x1000, 10);
        AssertSortedTableLookup(&table, 0x2000, 0x2000, 20);
        AssertSortedTableLookup(&table, 0x2FFF, 0x2000, 20);
        AssertSortedTableLookup(&table, 0x3000, 0x3000, 30);
        AssertSortedTableLookup(&table, 0xFFFF, 0x3000, 30);

        byte* belowFirst = (byte*)0xFFF;
        Assert.Equal((nuint)0, sorted_table.lookup(&table, ref belowFirst));
        Assert.Equal((nuint)0, (nuint)belowFirst);
    }

    [Fact]
    public void SortedTableRemoveUsesNativeContainingInterval()
    {
        sorted_table table = default;
        bk* slots = stackalloc bk[8];
        sorted_table.initialize(&table, 7, slots);
        sorted_table.insert(&table, (byte*)0x1000, 10);
        sorted_table.insert(&table, (byte*)0x2000, 20);
        sorted_table.insert(&table, (byte*)0x3000, 30);

        sorted_table.remove(&table, (byte*)0x2800);

        AssertSortedTableLookup(&table, 0x2000, 0x1000, 10);
        AssertSortedTableLookup(&table, 0x2FFF, 0x1000, 10);
        AssertSortedTableLookup(&table, 0x3000, 0x3000, 30);
    }

    [Fact]
    public void SortedTableDuplicateBoundaryUsesLastInsertedValue()
    {
        sorted_table table = default;
        bk* slots = stackalloc bk[6];
        sorted_table.initialize(&table, 5, slots);
        sorted_table.insert(&table, (byte*)0x1000, 10);
        sorted_table.insert(&table, (byte*)0x1000, 11);

        AssertSortedTableLookup(&table, 0x1000, 0x1000, 11);

        sorted_table.remove(&table, (byte*)0x1000);

        AssertSortedTableLookup(&table, 0x1000, 0x1000, 10);
    }

    [Fact]
    public void SortedTableClearRestoresOnlySentinel()
    {
        sorted_table table = default;
        bk* slots = stackalloc bk[5];
        sorted_table.initialize(&table, 4, slots);
        sorted_table.insert(&table, (byte*)0x1000, 10);

        sorted_table.clear(&table);

        byte* address = (byte*)0x1000;
        Assert.Equal((nuint)0, sorted_table.lookup(&table, ref address));
        Assert.Equal((nuint)0, (nuint)address);
        Assert.Equal(nuint.MaxValue, (nuint)sorted_table.buckets(&table)[0].add);
    }

    [Fact]
    public void SortedTableAllocationGrowthAndReclamationPreserveNativeOwnership()
    {
        SyncImports.ResetRecording();
        sorted_table* table = sorted_table.make_sorted_table();
        Assert.NotEqual((nuint)0, (nuint)table);
        Assert.Equal(1, SyncImports.AllocCount);
        int freeCountAfterDelete = 0;

        try
        {
            for (nuint index = 0; index < 399; index++)
            {
                sorted_table.insert(table, (byte*)((index + 1) * 0x1000), index + 1);
            }

            Assert.Equal(1, sorted_table.ensure_space_for_insert(table));
            Assert.Equal(2, SyncImports.AllocCount);
            AssertSortedTableLookup(table, 399 * 0x1000, 399 * 0x1000, 399);

            for (nuint index = 399; index < 599; index++)
            {
                sorted_table.insert(table, (byte*)((index + 1) * 0x1000), index + 1);
            }

            Assert.Equal(1, sorted_table.ensure_space_for_insert(table));
            Assert.Equal(3, SyncImports.AllocCount);
            AssertSortedTableLookup(table, 599 * 0x1000, 599 * 0x1000, 599);
        }
        finally
        {
            sorted_table.delete_sorted_table(table);
            freeCountAfterDelete = SyncImports.FreeCount;
            SyncImports.ManagedGC_Free(table);
        }

        Assert.Equal(2, freeCountAfterDelete);
        Assert.Equal(3, SyncImports.FreeCount);
    }

    [Fact]
    public void SortedTableAllocationFailuresReturnNullOrFalse()
    {
        SyncImports.ResetRecording();
        SyncImports.FailNextAlloc = true;
        Assert.Equal((nuint)0, (nuint)sorted_table.make_sorted_table());

#if !DEBUG
        // This path asserts in the port, as it does in the C++, so it can only be driven in a
        // build where the assert is compiled out.
        sorted_table* table = sorted_table.make_sorted_table();
        Assert.NotEqual((nuint)0, (nuint)table);
        try
        {
            for (nuint index = 0; index < 399; index++)
            {
                sorted_table.insert(table, (byte*)((index + 1) * 0x1000), index + 1);
            }

            SyncImports.FailNextAlloc = true;
            Assert.Equal(0, sorted_table.ensure_space_for_insert(table));
        }
        finally
        {
            sorted_table.delete_sorted_table(table);
            SyncImports.ManagedGC_Free(table);
        }
#endif
    }

    private static void AssertSortedTableLookup(
        sorted_table* table,
        nuint requested,
        nuint expectedAddress,
        nuint expectedValue)
    {
        byte* address = (byte*)requested;
        Assert.Equal(expectedValue, sorted_table.lookup(table, ref address));
        Assert.Equal(expectedAddress, (nuint)address);
    }

    private static nuint OffsetOf(void* field, bk* bucket) => (nuint)((byte*)field - (byte*)bucket);

#if !TARGET_WASM
    [Fact]
    public void EventBucketSetReplacesAllFields()
    {
        etw_bucket_info info = new()
        {
            index = ushort.MaxValue,
            count = uint.MaxValue,
            size = nuint.MaxValue,
        };

        info.set(12, 34, 56);

        Assert.Equal((ushort)12, info.index);
        Assert.Equal((uint)34, info.count);
        Assert.Equal((nuint)56, info.size);
    }
#endif

    [Fact]
    public void AllocListStartsEmptyAndAccessorsReferToItsFields()
    {
        alloc_list list = default;

        Assert.Equal((nuint)0, (nuint)alloc_list.alloc_list_head(&list));
        Assert.Equal((nuint)0, (nuint)alloc_list.alloc_list_tail(&list));
        Assert.Equal((nuint)0, alloc_list.alloc_list_damage_count(&list));
#if TARGET_64BIT && !TARGET_WASM
        Assert.Equal((nuint)0, (nuint)alloc_list.added_alloc_list_head(&list));
        Assert.Equal((nuint)0, (nuint)alloc_list.added_alloc_list_tail(&list));
#endif

        nuint offset = 0;
#if TARGET_64BIT && !TARGET_WASM
        fixed (byte** field = &alloc_list.added_alloc_list_head(&list))
        {
            Assert.Equal(offset, OffsetOf(field, &list));
        }
        offset += (nuint)sizeof(void*);
        fixed (byte** field = &alloc_list.added_alloc_list_tail(&list))
        {
            Assert.Equal(offset, OffsetOf(field, &list));
        }
        offset += (nuint)sizeof(void*);
#endif
        fixed (byte** field = &alloc_list.alloc_list_head(&list))
        {
            Assert.Equal(offset, OffsetOf(field, &list));
        }
        offset += (nuint)sizeof(void*);
        fixed (byte** field = &alloc_list.alloc_list_tail(&list))
        {
            Assert.Equal(offset, OffsetOf(field, &list));
        }
        offset += (nuint)sizeof(void*);
        fixed (nuint* field = &alloc_list.alloc_list_damage_count(&list))
        {
            Assert.Equal(offset, OffsetOf(field, &list));
        }

        alloc_list.alloc_list_head(&list) = (byte*)1;
        alloc_list.alloc_list_tail(&list) = (byte*)2;
        alloc_list.alloc_list_damage_count(&list) = 3;
#if TARGET_64BIT && !TARGET_WASM
        alloc_list.added_alloc_list_head(&list) = (byte*)4;
        alloc_list.added_alloc_list_tail(&list) = (byte*)5;
#endif

        Assert.Equal((nuint)1, (nuint)alloc_list.alloc_list_head(&list));
        Assert.Equal((nuint)2, (nuint)alloc_list.alloc_list_tail(&list));
        Assert.Equal((nuint)3, alloc_list.alloc_list_damage_count(&list));
#if TARGET_64BIT && !TARGET_WASM
        Assert.Equal((nuint)4, (nuint)alloc_list.added_alloc_list_head(&list));
        Assert.Equal((nuint)5, (nuint)alloc_list.added_alloc_list_tail(&list));
#endif
    }

    private static nuint OffsetOf(void* field, alloc_list* list) => (nuint)((byte*)field - (byte*)list);

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void MarkShortBitsPreserveEveryNativeMask(bool post, int bit)
    {
        mark value = default;
        mark* p = &value;
        int expected = 1 << (28 + bit);

        Assert.Equal((nuint)3, mark.get_max_short_bits());
        Assert.Equal((nuint)28, mark.get_pre_short_start_bit());
        Assert.Equal((nuint)28, mark.get_post_short_start_bit());

        if (post)
        {
            mark.set_post_short_bit(p, (nuint)bit);
            Assert.Equal(expected, value.saved_post_p);
            Assert.Equal(expected, mark.post_short_bit_p(p, (nuint)bit));
            Assert.Equal(0, mark.post_short_p(p));
        }
        else
        {
            mark.set_pre_short_bit(p, (nuint)bit);
            Assert.Equal(expected, value.saved_pre_p);
            Assert.Equal(expected, mark.pre_short_bit_p(p, (nuint)bit));
            Assert.Equal(0, mark.pre_short_p(p));
        }
    }

    [Fact]
    public void MarkShortAndCollectibleBitsPreserveNativeBoolValues()
    {
        mark value = default;
        mark* p = &value;

        mark.set_pre_short(p);
        mark.set_post_short(p);
        Assert.Equal(unchecked((int)0x80000000), value.saved_pre_p);
        Assert.Equal(unchecked((int)0x80000000), value.saved_post_p);
        Assert.Equal(unchecked((int)0x80000000), mark.pre_short_p(p));
        Assert.Equal(unchecked((int)0x80000000), mark.post_short_p(p));

#if COLLECTIBLE_CLASS
        mark.set_pre_short_collectible(p);
        mark.set_post_short_collectible(p);
        Assert.Equal(2, mark.pre_short_collectible_p(p));
        Assert.Equal(2, mark.post_short_collectible_p(p));
#else
        // NativeAOT defines FEATURE_NATIVEAOT, so gcpriv.h does not define COLLECTIBLE_CLASS.
        // The reserved collectible bit remains part of the packed BOOL and must not be normalized.
        value.saved_pre_p |= 2;
        value.saved_post_p |= 2;
#endif

        Assert.Equal(unchecked((int)0x80000002), value.saved_pre_p);
        Assert.Equal(unchecked((int)0x80000002), value.saved_post_p);

        value.saved_pre_p = 0x40000002;
        value.saved_post_p = 0x20000002;
        Assert.Equal(0x40000002, mark.has_pre_plug_info(p));
        Assert.Equal(0x20000002, mark.has_post_plug_info(p));
    }

    [Fact]
    public void MarkPointerAccessorsReferToStoredAddresses()
    {
        mark value = default;
        mark* p = &value;

        value.first = (byte*)0x100;
        value.saved_post_plug_info_start = (byte*)0x200;
        mark.set_pre_plug_info_reloc_start(p, (byte*)0x300);

        Assert.Equal((nuint)0x100, (nuint)mark.get_plug_address(p));
        Assert.Equal((nuint)0x200, (nuint)mark.get_post_plug_info_start(p));
        Assert.Equal((nuint)0x300, (nuint)value.saved_pre_plug_info_reloc_start);
        Assert.True(mark.get_pre_plug_reloc_info(p) == &value.saved_pre_plug_reloc);
        Assert.True(mark.get_post_plug_reloc_info(p) == &value.saved_post_plug_reloc);

        mark.get_pre_plug_reloc_info(p)->gap = 0x11;
        mark.get_post_plug_reloc_info(p)->reloc = 0x22;
        Assert.Equal((nuint)0x11, value.saved_pre_plug_reloc.gap);
        Assert.Equal((nuint)0x22, value.saved_post_plug_reloc.reloc);
    }

    [Fact]
    public void MarkSwapMethodsExchangeExactGapRelocPairs()
    {
        byte* storage = stackalloc byte[2 * sizeof(plug_and_gap)];
        mark value = default;
        mark* p = &value;
        p->first = storage + sizeof(plug_and_gap);
        p->saved_post_plug_info_start = storage + sizeof(plug_and_gap);
        gap_reloc_pair* pre = (gap_reloc_pair*)(p->first - sizeof(plug_and_gap));
        gap_reloc_pair* post = (gap_reloc_pair*)p->saved_post_plug_info_start;

        *pre = Pair(1, 2, 3, 4);
        value.saved_pre_plug_reloc = Pair(5, 6, 7, 8);
        mark.swap_pre_plug_and_saved(p);
        AssertPair(*pre, 5, 6, 7, 8);
        AssertPair(value.saved_pre_plug_reloc, 1, 2, 3, 4);

        *post = Pair(9, 10, 11, 12);
        value.saved_post_plug_reloc = Pair(13, 14, 15, 16);
        mark.swap_post_plug_and_saved(p);
        AssertPair(*post, 13, 14, 15, 16);
        AssertPair(value.saved_post_plug_reloc, 9, 10, 11, 12);

        *pre = Pair(17, 18, 19, 20);
        value.saved_pre_plug = Pair(21, 22, 23, 24);
        mark.swap_pre_plug_and_saved_for_profiler(p);
        AssertPair(*pre, 21, 22, 23, 24);
        AssertPair(value.saved_pre_plug, 17, 18, 19, 20);

        *post = Pair(25, 26, 27, 28);
        value.saved_post_plug = Pair(29, 30, 31, 32);
        mark.swap_post_plug_and_saved_for_profiler(p);
        AssertPair(*post, 29, 30, 31, 32);
        AssertPair(value.saved_post_plug, 25, 26, 27, 28);
    }

    private static gap_reloc_pair Pair(nuint gap, nuint reloc, short left, short right) =>
        new() { gap = gap, reloc = reloc, m_pair = new pair { left = left, right = right } };

    private static void AssertPair(gap_reloc_pair actual, nuint gap, nuint reloc, short left, short right)
    {
        Assert.Equal(gap, actual.gap);
        Assert.Equal(reloc, actual.reloc);
        Assert.Equal(left, actual.m_pair.left);
        Assert.Equal(right, actual.m_pair.right);
    }

    [Fact]
    public void CardTableInfoDefaultStateIsZeroed()
    {
        card_table_info info = default;
        card_table_info* p = &info;

        Assert.Equal(0u, p->recount);
        Assert.Equal((nuint)0, p->size);
        Assert.Equal((nuint)0, (nuint)p->next_card_table);
        Assert.Equal((nuint)0, (nuint)p->lowest_address);
        Assert.Equal((nuint)0, (nuint)p->highest_address);
        Assert.Equal((nuint)0, (nuint)p->brick_table);
        Assert.Equal((nuint)0, (nuint)p->card_bundle_table);
#if BACKGROUND_GC
        Assert.Equal((nuint)0, (nuint)p->mark_array);
#endif
    }

    [Fact]
    public void CardTableInfoFieldsFollowNativeOrderAndDacPrefix()
    {
        card_table_info info = default;
        card_table_info* p = &info;
        dac_card_table_info dac = default;
        nuint previous = 0;

        Assert.Equal((nuint)0, OffsetOf(&p->recount, p));
        Assert.Equal(OffsetOf(&dac.recount, &dac), OffsetOf(&p->recount, p));
        previous = Ascending(OffsetOf(&p->size, p), previous);
        Assert.Equal(OffsetOf(&dac.size, &dac), OffsetOf(&p->size, p));
        previous = Ascending(OffsetOf(&p->next_card_table, p), previous);
        Assert.Equal(OffsetOf(&dac.next_card_table, &dac), OffsetOf(&p->next_card_table, p));
        previous = Ascending(OffsetOf(&p->lowest_address, p), previous);
        previous = Ascending(OffsetOf(&p->highest_address, p), previous);
        previous = Ascending(OffsetOf(&p->brick_table, p), previous);
        previous = Ascending(OffsetOf(&p->card_bundle_table, p), previous);
#if BACKGROUND_GC
        _ = Ascending(OffsetOf(&p->mark_array, p), previous);
#endif
    }

    [Fact]
    public void CardTableInfoPureHelpersPreserveNativeArithmetic()
    {
        Assert.Equal((nuint)0, card_table_info.gib(0));
        Assert.Equal((nuint)0, card_table_info.gib(((nuint)1 << 30) - 1));
        Assert.Equal((nuint)1, card_table_info.gib((nuint)1 << 30));
        Assert.Equal((nuint)3, card_table_info.gib(((nuint)3 << 30) + ((nuint)1 << 29)));

        nuint brick = card_table_info.brick_size;
        Assert.Equal(brick, (nuint)card_table_info.align_on_brick((byte*)1));
        Assert.Equal(brick, (nuint)card_table_info.align_on_brick((byte*)(brick - 1)));
        Assert.Equal(brick, (nuint)card_table_info.align_on_brick((byte*)brick));
        Assert.Equal((nuint)0, (nuint)card_table_info.align_on_brick((byte*)(nuint.MaxValue - (brick - 2))));
    }

    [Theory]
    [InlineData(0U, 0U, 0U)]
    [InlineData(31U, 0U, 31U)]
    [InlineData(32U, 1U, 0U)]
    [InlineData(33U, 1U, 1U)]
    [InlineData(63U, 1U, 31U)]
    [InlineData(64U, 2U, 0U)]
    [InlineData(uint.MaxValue, 134217727U, 31U)]
    public void CardTableInfoCardWordAndBitPreserveNativeArithmetic(uint card, uint word, uint bit)
    {
        Assert.Equal((nuint)word, card_table_info.card_word((nuint)card));
        Assert.Equal(bit, card_table_info.card_bit((nuint)card));
    }

    [Theory]
    [InlineData(0UL, 0UL)]
#if TARGET_64BIT
    [InlineData(0xFFUL, 0UL)]
    [InlineData(0x100UL, 1UL)]
    [InlineData(0x101UL, 1UL)]
    [InlineData(0x12345678UL, 0x123456UL)]
#else
    [InlineData(0x7FUL, 0UL)]
    [InlineData(0x80UL, 1UL)]
    [InlineData(0x81UL, 1UL)]
    [InlineData(0x12345678UL, 0x2468ACUL)]
#endif
    public void CardTableInfoGcardOfPreservesPointerToNuintDivision(ulong objectAddress, ulong card)
    {
        Assert.Equal((nuint)card, card_table_info.gcard_of((byte*)(nuint)objectAddress));
    }

    [Theory]
    [InlineData(0U, 0U, 0U)]
    [InlineData(31U, 0U, 31U)]
    [InlineData(32U, 1U, 0U)]
    [InlineData(33U, 1U, 1U)]
    [InlineData(63U, 1U, 31U)]
    [InlineData(64U, 2U, 0U)]
    [InlineData(uint.MaxValue, 134217727U, 31U)]
    public void CardTableInfoCardBundleWordAndBitPreserveNativeArithmetic(uint cardBundle, uint word, uint bit)
    {
        Assert.Equal((nuint)word, card_table_info.card_bundle_word((nuint)cardBundle));
        Assert.Equal(bit, card_table_info.card_bundle_bit((nuint)cardBundle));
    }

    [Theory]
    [InlineData(0U, 0U, 0U)]
    [InlineData(1U, 32U, 0U)]
    [InlineData(31U, 32U, 0U)]
    [InlineData(32U, 32U, 1U)]
    [InlineData(33U, 64U, 1U)]
    [InlineData(63U, 64U, 1U)]
    [InlineData(64U, 64U, 2U)]
    public void CardTableInfoCardBundleConversionsPreserveNativeArithmetic(
        uint cardWord,
        uint alignedCardWord,
        uint cardBundle)
    {
        Assert.Equal((nuint)alignedCardWord, card_table_info.align_cardw_on_bundle(cardWord));
        Assert.Equal((nuint)cardBundle, card_table_info.cardw_card_bundle(cardWord));
        Assert.Equal((nuint)(cardBundle * 32), card_table_info.card_bundle_cardw(cardBundle));
    }

    [Fact]
    public void CardTableInfoTranslatedBundleTablePreservesNativeSkew()
    {
        const nuint BundleTable = 0x100000;
        nuint heapBytesForBundleWord =
            card_table_info.card_size
            * card_table_info.card_word_width
            * card_table_info.card_bundle_size
            * card_table_info.card_bundle_word_width;

        Assert.Equal(
            BundleTable,
            (nuint)card_table_info.translate_card_bundle_table((uint*)BundleTable, (byte*)0));
        Assert.Equal(
            BundleTable - sizeof(uint),
            (nuint)card_table_info.translate_card_bundle_table(
                (uint*)BundleTable,
                (byte*)heapBytesForBundleWord));
        Assert.Equal(
            BundleTable - (3 * sizeof(uint)),
            (nuint)card_table_info.translate_card_bundle_table(
                (uint*)BundleTable,
                (byte*)((3 * heapBytesForBundleWord) + (heapBytesForBundleWord - 1))));
    }

    [Theory]
    [InlineData(0x1000UL, 0x1000UL, 0UL)]
    [InlineData(0x1000UL, 0x2000UL, 2UL)]
    [InlineData(0x1000UL, 0x5000UL, 8UL)]
    public void CardTableInfoBrickTableSizePreservesNativeArithmetic(ulong from, ulong end, ulong size)
    {
#if TARGET_64BIT
        Assert.Equal((nuint)size, card_table_info.size_brick_of((byte*)from, (byte*)end));
#else
        Assert.Equal((nuint)(size * 2), card_table_info.size_brick_of((byte*)from, (byte*)end));
#endif
    }

    [Theory]
#if TARGET_64BIT
    [InlineData(0x1000UL, 0x1100UL, 1UL, 4UL)]
    [InlineData(0x1000UL, 0x2000UL, 1UL, 4UL)]
    [InlineData(0x1000UL, 0x2001UL, 2UL, 8UL)]
#else
    [InlineData(0x1000UL, 0x1080UL, 1UL, 4UL)]
    [InlineData(0x1000UL, 0x2000UL, 1UL, 4UL)]
    [InlineData(0x1000UL, 0x2001UL, 2UL, 8UL)]
#endif
    public void CardTableInfoCardTableSizeCoversHalfOpenRange(
        ulong from,
        ulong end,
        ulong count,
        ulong size)
    {
        Assert.Equal((nuint)count, card_table_info.count_card_of((byte*)from, (byte*)end));
        Assert.Equal((nuint)size, card_table_info.size_card_of((byte*)from, (byte*)end));
    }

    [Fact]
    public void CardTableInfoMetadataAccessorsAliasPrecedingRecord()
    {
        card_table_info info = default;
        uint* cardTable = (uint*)((byte*)&info + sizeof(card_table_info));

        card_table_info.card_table_refcount(cardTable) = 7;
        card_table_info.card_table_size(cardTable) = 0x1234;
        card_table_info.card_table_next(cardTable) = (uint*)0x2000;
        card_table_info.card_table_lowest_address(cardTable) = (byte*)0x3000;
        card_table_info.card_table_highest_address(cardTable) = (byte*)0x4000;
        card_table_info.card_table_brick_table(cardTable) = (short*)0x5000;
        card_table_info.card_table_card_bundle_table(cardTable) = (uint*)0x6000;
#if BACKGROUND_GC
        card_table_info.card_table_mark_array(cardTable) = (uint*)0x7000;
#endif

        Assert.Equal(7u, info.recount);
        Assert.Equal((nuint)0x1234, info.size);
        Assert.Equal((nuint)0x2000, (nuint)info.next_card_table);
        Assert.Equal((nuint)0x3000, (nuint)info.lowest_address);
        Assert.Equal((nuint)0x4000, (nuint)info.highest_address);
        Assert.Equal((nuint)0x5000, (nuint)info.brick_table);
        Assert.Equal((nuint)0x6000, (nuint)info.card_bundle_table);
#if BACKGROUND_GC
        Assert.Equal((nuint)0x7000, (nuint)info.mark_array);
#endif
    }

    [Fact]
    public void CardTableInfoTranslatedCardTablePreservesNativeSkew()
    {
        card_table_info info = default;
        uint* cardTable = (uint*)((byte*)&info + sizeof(card_table_info));

        info.lowest_address = (byte*)0;
        Assert.Equal((nuint)cardTable, (nuint)card_table_info.translate_card_table(cardTable));

        info.lowest_address = (byte*)(card_table_info.card_size * card_table_info.card_word_width);
        Assert.Equal(
            (nuint)cardTable - sizeof(uint),
            (nuint)card_table_info.translate_card_table(cardTable));
    }

#if BACKGROUND_GC
    [Theory]
    [InlineData(0UL, 0UL, 0U, 0UL)]
    [InlineData(1UL, 0UL, 0U, 0UL)]
#if TARGET_64BIT
    [InlineData(15UL, 0UL, 0U, 0UL)]
    [InlineData(16UL, 1UL, 1U, 0UL)]
    [InlineData(511UL, 31UL, 31U, 0UL)]
    [InlineData(512UL, 32UL, 0U, 1UL)]
#else
    [InlineData(7UL, 0UL, 0U, 0UL)]
    [InlineData(8UL, 1UL, 1U, 0UL)]
    [InlineData(255UL, 31UL, 31U, 0UL)]
    [InlineData(256UL, 32UL, 0U, 1UL)]
#endif
    public void CardTableInfoMarkIndexesPreserveNativeArithmetic(
        ulong address,
        ulong markBit,
        uint bitInWord,
        ulong markWord)
    {
        Assert.Equal((nuint)markBit, card_table_info.mark_bit_of((byte*)address));
        Assert.Equal(bitInWord, card_table_info.mark_bit_bit((nuint)markBit));
        Assert.Equal((nuint)bitInWord, card_table_info.mark_bit_bit_of((byte*)address));
        Assert.Equal((nuint)markWord, card_table_info.mark_bit_word((nuint)markBit));
        Assert.Equal((nuint)markWord, card_table_info.mark_word_of((byte*)address));
        Assert.Equal((nuint)(markBit * card_table_info.mark_bit_pitch), (nuint)card_table_info.mark_bit_address((nuint)markBit));
    }

    [Fact]
    public void CardTableInfoMarkAlignmentAndSizingPreserveNativeArithmetic()
    {
        nuint pitch = card_table_info.mark_bit_pitch;
        nuint word = card_table_info.mark_word_size;

        Assert.Equal(pitch, (nuint)card_table_info.align_on_mark_bit((byte*)1));
        Assert.Equal((nuint)0, (nuint)card_table_info.align_lower_mark_bit((byte*)(pitch - 1)));
        Assert.Equal(word, (nuint)card_table_info.align_on_mark_word((byte*)1));
        Assert.Equal((nuint)0, (nuint)card_table_info.align_lower_mark_word((byte*)(word - 1)));
        Assert.Equal(1, card_table_info.is_aligned_on_mark_word((byte*)word));
        Assert.Equal(0, card_table_info.is_aligned_on_mark_word((byte*)(word - 1)));
        Assert.Equal((nuint)8, card_table_info.size_mark_array_of((byte*)word, (byte*)(3 * word)));
    }
#endif

    [Fact]
    public void CardTableInfoAlignmentHelpersPreserveNativeArithmetic()
    {
        nuint brick = card_table_info.brick_size;
        Assert.Equal((nuint)0, (nuint)card_table_info.align_lower_brick((byte*)0));
        Assert.Equal((nuint)0, (nuint)card_table_info.align_lower_brick((byte*)(brick - 1)));
        Assert.Equal(brick, (nuint)card_table_info.align_lower_brick((byte*)brick));
        Assert.Equal(nuint.MaxValue & ~(brick - 1), (nuint)card_table_info.align_lower_brick((byte*)nuint.MaxValue));

        nuint card = card_table_info.card_size;
        Assert.Equal((nuint)0, (nuint)card_table_info.align_on_card((byte*)0));
        Assert.Equal(card, (nuint)card_table_info.align_on_card((byte*)1));
        Assert.Equal(card, (nuint)card_table_info.align_on_card((byte*)card));
        Assert.Equal((nuint)0, (nuint)card_table_info.align_on_card((byte*)(nuint.MaxValue - (card - 2))));
        Assert.Equal((nuint)0, (nuint)card_table_info.align_lower_card((byte*)(card - 1)));
        Assert.Equal(card, (nuint)card_table_info.align_lower_card((byte*)card));

        nuint cardWord = card * card_table_info.card_word_width;
        Assert.Equal((nuint)0, (nuint)card_table_info.align_on_card_word((byte*)0));
        Assert.Equal(cardWord, (nuint)card_table_info.align_on_card_word((byte*)1));
        Assert.Equal(cardWord, (nuint)card_table_info.align_on_card_word((byte*)cardWord));
        Assert.Equal((nuint)0, (nuint)card_table_info.align_on_card_word((byte*)(nuint.MaxValue - (cardWord - 2))));
    }

    [Fact]
    public void CardTableInfoConstantsMatchNativeValues()
    {
#if TARGET_64BIT
        Assert.Equal((nuint)4096, card_table_info.brick_size);
        Assert.Equal(85u, card_table_info.MAX_ALLOWED_MEM_LOAD);
        Assert.Equal((nuint)(16 * 1024 * 1024), card_table_info.MIN_YOUNGEST_GEN_DESIRED);
#else
        Assert.Equal((nuint)2048, card_table_info.brick_size);
#endif
        Assert.Equal((nuint)4096, card_table_info.GC_PAGE_SIZE);
        Assert.Equal((nuint)32, card_table_info.card_word_width);
#if TARGET_64BIT
        Assert.Equal((nuint)256, card_table_info.card_size);
#else
        Assert.Equal((nuint)128, card_table_info.card_size);
#endif
        Assert.Equal((nuint)32, card_table_info.card_bundle_word_width);
        Assert.Equal((nuint)32, card_table_info.card_bundle_size);
#if BACKGROUND_GC
#if TARGET_64BIT
        Assert.Equal((nuint)16, card_table_info.mark_bit_pitch);
        Assert.Equal((nuint)512, card_table_info.mark_word_size);
#else
        Assert.Equal((nuint)8, card_table_info.mark_bit_pitch);
        Assert.Equal((nuint)256, card_table_info.mark_word_size);
#endif
        Assert.Equal((nuint)32, card_table_info.mark_word_width);
#endif
        Assert.Equal(40u * 1024 * 1024, card_table_info.SH_TH_CARD_BUNDLE);
        Assert.Equal(180u * 1024 * 1024, card_table_info.MH_TH_CARD_BUNDLE);
        Assert.Equal(100u, card_table_info.DECOMMIT_TIME_STEP_MILLISECONDS);
        Assert.Equal((nuint)(160 * 1024), gc_heap.DECOMMIT_SIZE_PER_MILLISECOND);
    }

    private static nuint OffsetOf(void* field, card_table_info* info) => (nuint)((byte*)field - (byte*)info);

    private static nuint OffsetOf(void* field, dac_card_table_info* info) => (nuint)((byte*)field - (byte*)info);

    [Theory]
    [InlineData(1u, 1)]
    [InlineData(2u, 0)]
    [InlineData(4u, 0)]
    [InlineData(8u, 0)]
    public void ConstructionReportsBucketCountAndDiscardPredicate(uint numBuckets, int expectedDiscard)
    {
        allocator a = new(numBuckets, fbb: 3, b: null);

        Assert.Equal(numBuckets, a.number_of_buckets());
        Assert.Equal(expectedDiscard, a.discard_if_no_fit_p());
    }

    [Fact]
    public void DefaultInitializationMatchesYoungGenerationSemantics()
    {
        allocator a = default;
        allocator.initialize(&a);

        Assert.Equal(1u, a.number_of_buckets());
        Assert.Equal(1, a.discard_if_no_fit_p());
#if TARGET_64BIT && !TARGET_WASM
        Assert.False(a.is_doubly_linked_p());
#endif
    }

    [Theory]
    [InlineData(3, 4u, 0u, 0u)]
    [InlineData(3, 4u, 7u, 0u)]
    [InlineData(3, 4u, 8u, 0u)]
    [InlineData(3, 4u, 15u, 0u)]
    [InlineData(3, 4u, 16u, 1u)]
    [InlineData(3, 4u, 31u, 1u)]
    [InlineData(3, 4u, 32u, 2u)]
    [InlineData(3, 4u, 63u, 2u)]
    [InlineData(3, 4u, 64u, 3u)]
    [InlineData(3, 4u, 127u, 3u)]
    // the last bucket fits everything, so oversized requests are clamped to num_buckets - 1
    [InlineData(3, 4u, 128u, 3u)]
    [InlineData(3, 4u, 1000000u, 3u)]
    [InlineData(0, 4u, 1u, 0u)]
    [InlineData(0, 4u, 2u, 1u)]
    [InlineData(0, 4u, 4u, 2u)]
    [InlineData(0, 4u, 8u, 3u)]
    [InlineData(0, 4u, 16u, 3u)]
    // a single-bucket allocator always maps to bucket 0
    [InlineData(3, 1u, 12345u, 0u)]
    public void FirstSuitableBucketMapsSizeToBucket(int fbb, uint numBuckets, uint size, uint expected)
    {
        allocator a = new(numBuckets, fbb, b: null);

        Assert.Equal(expected, a.first_suitable_bucket(size));
    }

    [Theory]
    [InlineData(0, 2u)]
    [InlineData(1, 4u)]
    [InlineData(2, 8u)]
    [InlineData(3, 16u)]
    [InlineData(5, 64u)]
    [InlineData(9, 1024u)]
    [InlineData(10, 2048u)]
    public void FirstBucketSizeIsTwoToTheBucketBitsPlusOne(int fbb, uint expected)
    {
        allocator a = new(1u, fbb, b: null);

        Assert.Equal((nuint)expected, a.first_bucket_size());
    }

    [Fact]
    public void BucketZeroUsesFirstBucketAndOthersUseTheExternalArray()
    {
        alloc_list* buckets = stackalloc alloc_list[3];
        for (int i = 0; i < 3; i++)
        {
            buckets[i] = default;
        }

        allocator a = new(4u, fbb: 3, buckets);

        Assert.Equal(3, *(int*)&a);
        Assert.Equal(4u, *(uint*)((byte*)&a + sizeof(int)));

        nuint firstBucketOffset = 2 * sizeof(uint);
#if TARGET_64BIT && !TARGET_WASM
        nuint firstBucketHeadOffset = firstBucketOffset + (2 * (nuint)sizeof(void*));
#else
        nuint firstBucketHeadOffset = firstBucketOffset;
#endif
        fixed (byte** field = &allocator.alloc_list_head_of(&a, 0))
        {
            Assert.Equal(firstBucketHeadOffset, OffsetOf(field, &a));
        }

        nuint bucketsOffset = firstBucketOffset + (nuint)sizeof(alloc_list);
        Assert.Equal((nuint)buckets, *(nuint*)((byte*)&a + bucketsOffset));
        Assert.Equal(-1, *(int*)((byte*)&a + bucketsOffset + (nuint)sizeof(void*)));

        // Bucket 0 is the allocator's own first_bucket, not part of the external array.
        allocator.alloc_list_head_of(&a, 0) = (byte*)0x100;
        Assert.Equal((nuint)0x100, (nuint)allocator.alloc_list_head_of(&a, 0));
        Assert.Equal((nuint)0, (nuint)alloc_list.alloc_list_head(&buckets[0]));
        Assert.Equal((nuint)0, (nuint)alloc_list.alloc_list_head(&buckets[1]));
        Assert.Equal((nuint)0, (nuint)alloc_list.alloc_list_head(&buckets[2]));

        // Buckets 1..n-1 land in buckets[bn - 1].
        allocator.alloc_list_head_of(&a, 1) = (byte*)0x200;
        allocator.alloc_list_head_of(&a, 2) = (byte*)0x300;
        allocator.alloc_list_head_of(&a, 3) = (byte*)0x400;
        Assert.Equal((nuint)0x200, (nuint)alloc_list.alloc_list_head(&buckets[0]));
        Assert.Equal((nuint)0x300, (nuint)alloc_list.alloc_list_head(&buckets[1]));
        Assert.Equal((nuint)0x400, (nuint)alloc_list.alloc_list_head(&buckets[2]));

        // Bucket 0's damage count is also internal; buckets 1..n-1 route into the array.
        allocator.alloc_list_damage_count_of(&a, 0) = 11;
        allocator.alloc_list_damage_count_of(&a, 1) = 22;
        Assert.Equal((nuint)11, allocator.alloc_list_damage_count_of(&a, 0));
        Assert.Equal((nuint)22, alloc_list.alloc_list_damage_count(&buckets[0]));
    }

    [Fact]
    public void RefAccessorsMutateTheUnderlyingList()
    {
        alloc_list* buckets = stackalloc alloc_list[1];
        buckets[0] = default;

        allocator a = new(2u, fbb: 3, buckets);

        allocator.alloc_list_head_of(&a, 1) = (byte*)0x1000;
        allocator.alloc_list_tail_of(&a, 1) = (byte*)0x2000;
        Assert.Equal((nuint)0x1000, (nuint)alloc_list.alloc_list_head(&buckets[0]));
        Assert.Equal((nuint)0x2000, (nuint)alloc_list.alloc_list_tail(&buckets[0]));

#if TARGET_64BIT && !TARGET_WASM
        allocator.added_alloc_list_head_of(&a, 1) = (byte*)0x3000;
        allocator.added_alloc_list_tail_of(&a, 1) = (byte*)0x4000;
        Assert.Equal((nuint)0x3000, (nuint)alloc_list.added_alloc_list_head(&buckets[0]));
        Assert.Equal((nuint)0x4000, (nuint)alloc_list.added_alloc_list_tail(&buckets[0]));
#endif
    }

    [Fact]
    public void ClearResetsEveryActiveBucketHeadAndTail()
    {
        alloc_list* buckets = stackalloc alloc_list[3];
        for (int i = 0; i < 3; i++)
        {
            buckets[i] = default;
        }

        allocator a = new(4u, fbb: 3, buckets);

        for (uint bn = 0; bn < 4; bn++)
        {
            allocator.alloc_list_head_of(&a, bn) = (byte*)(0x10 + bn);
            allocator.alloc_list_tail_of(&a, bn) = (byte*)(0x20 + bn);
        }

        allocator.clear(&a);

        for (uint bn = 0; bn < 4; bn++)
        {
            Assert.Equal((nuint)0, (nuint)allocator.alloc_list_head_of(&a, bn));
            Assert.Equal((nuint)0, (nuint)allocator.alloc_list_tail_of(&a, bn));
        }
    }

    private static nuint OffsetOf(void* field, allocator* a) => (nuint)((byte*)field - (byte*)a);

#if TARGET_64BIT && !TARGET_WASM
    [Theory]
    [InlineData(2, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(-1, false)]
    public void IsDoublyLinkedOnlyForMaxGeneration(int gen, bool expected)
    {
        allocator a = new(1u, fbb: 3, b: null, gen);

        Assert.Equal(expected, a.is_doubly_linked_p());
    }
#endif

    private static nuint OffsetOf(void* field, dynamic_data* dd) => (nuint)((byte*)field - (byte*)dd);

    [Fact]
    public void DefaultDynamicDataIsZeroInitialized()
    {
        dynamic_data dd = default;
        dynamic_data* p = &dd;

        Assert.Equal((nint)0, dynamic_data.dd_new_allocation(p));
        Assert.Equal((nint)0, dynamic_data.dd_gc_new_allocation(p));
        Assert.Equal(0f, dynamic_data.dd_surv(p));
        Assert.Equal((nuint)0, dynamic_data.dd_desired_allocation(p));
        Assert.Equal((nuint)0, dynamic_data.dd_begin_data_size(p));
        Assert.Equal((nuint)0, dynamic_data.dd_survived_size(p));
        Assert.Equal((nuint)0, dynamic_data.dd_pinned_survived_size(p));
        Assert.Equal((nuint)0, dynamic_data.dd_artificial_pinned_survived_size(p));
        Assert.Equal((nuint)0, dynamic_data.dd_added_pinned_size(p));
        Assert.Equal((nuint)0, dynamic_data.dd_padding_size(p));
#if TARGET_ARM || TARGET_WASM
        Assert.Equal((nuint)0, dynamic_data.dd_num_npinned_plugs(p));
#endif
        Assert.Equal((nuint)0, dynamic_data.dd_current_size(p));
        Assert.Equal((nuint)0, dynamic_data.dd_collection_count(p));
        Assert.Equal((nuint)0, dynamic_data.dd_promoted_size(p));
        Assert.Equal((nuint)0, dynamic_data.dd_freach_previous_promotion(p));
        Assert.Equal((nuint)0, dynamic_data.dd_fragmentation(p));
        Assert.Equal((nuint)0, dynamic_data.dd_gc_clock(p));
        Assert.Equal(0UL, dynamic_data.dd_time_clock(p));
        Assert.Equal(0UL, dynamic_data.dd_previous_time_clock(p));
        Assert.Equal((nuint)0, dynamic_data.dd_gc_elapsed_time(p));
        Assert.Equal((nuint)0, dynamic_data.dd_min_size(p));
        Assert.Equal((nuint)0, (nuint)dd.sdata);
    }

    [Fact]
    public void DirectAccessorsReferToFieldsInNativeOrder()
    {
        dynamic_data dd = default;
        dynamic_data* p = &dd;
        nuint previous = 0;

        fixed (nint* f = &dynamic_data.dd_new_allocation(p))
        {
            Assert.True(f == &p->new_allocation);
            Assert.Equal((nuint)0, OffsetOf(f, p));
        }
        fixed (nint* f = &dynamic_data.dd_gc_new_allocation(p))
        {
            Assert.True(f == &p->gc_new_allocation);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (float* f = &dynamic_data.dd_surv(p))
        {
            Assert.True(f == &p->surv);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_desired_allocation(p))
        {
            Assert.True(f == &p->desired_allocation);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_begin_data_size(p))
        {
            Assert.True(f == &p->begin_data_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_survived_size(p))
        {
            Assert.True(f == &p->survived_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_pinned_survived_size(p))
        {
            Assert.True(f == &p->pinned_survived_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_artificial_pinned_survived_size(p))
        {
            Assert.True(f == &p->artificial_pinned_survived_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_added_pinned_size(p))
        {
            Assert.True(f == &p->added_pinned_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_padding_size(p))
        {
            Assert.True(f == &p->padding_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
#if TARGET_ARM || TARGET_WASM
        fixed (nuint* f = &dynamic_data.dd_num_npinned_plugs(p))
        {
            Assert.True(f == &p->num_npinned_plugs);
            previous = Ascending(OffsetOf(f, p), previous);
        }
#endif
        fixed (nuint* f = &dynamic_data.dd_current_size(p))
        {
            Assert.True(f == &p->current_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_collection_count(p))
        {
            Assert.True(f == &p->collection_count);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_promoted_size(p))
        {
            Assert.True(f == &p->promoted_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_freach_previous_promotion(p))
        {
            Assert.True(f == &p->freach_previous_promotion);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_fragmentation(p))
        {
            Assert.True(f == &p->fragmentation);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_gc_clock(p))
        {
            Assert.True(f == &p->gc_clock);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (ulong* f = &dynamic_data.dd_time_clock(p))
        {
            Assert.True(f == &p->time_clock);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (ulong* f = &dynamic_data.dd_previous_time_clock(p))
        {
            Assert.True(f == &p->previous_time_clock);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_gc_elapsed_time(p))
        {
            Assert.True(f == &p->gc_elapsed_time);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &dynamic_data.dd_min_size(p))
        {
            Assert.True(f == &p->min_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }

        // sdata is the last field; it has no direct accessor but closes the layout.
        Assert.True(OffsetOf(&p->sdata, p) > previous);
    }

    private static nuint Ascending(nuint offset, nuint previous)
    {
        Assert.True(offset > previous);
        return offset;
    }

    [Fact]
    public void DirectAccessorsMutateTheirFields()
    {
        dynamic_data dd = default;
        dynamic_data* p = &dd;

        dynamic_data.dd_new_allocation(p) = -11;
        dynamic_data.dd_gc_new_allocation(p) = -22;
        dynamic_data.dd_surv(p) = 1.5f;
        dynamic_data.dd_desired_allocation(p) = 33;
        dynamic_data.dd_begin_data_size(p) = 44;
        dynamic_data.dd_survived_size(p) = 55;
        dynamic_data.dd_pinned_survived_size(p) = 66;
        dynamic_data.dd_artificial_pinned_survived_size(p) = 77;
        dynamic_data.dd_added_pinned_size(p) = 88;
        dynamic_data.dd_padding_size(p) = 99;
#if TARGET_ARM || TARGET_WASM
        dynamic_data.dd_num_npinned_plugs(p) = 100;
#endif
        dynamic_data.dd_current_size(p) = 111;
        dynamic_data.dd_collection_count(p) = 122;
        dynamic_data.dd_promoted_size(p) = 133;
        dynamic_data.dd_freach_previous_promotion(p) = 144;
        dynamic_data.dd_fragmentation(p) = 155;
        dynamic_data.dd_gc_clock(p) = 166;
        dynamic_data.dd_time_clock(p) = 0x1122334455667788UL;
        dynamic_data.dd_previous_time_clock(p) = 0x8877665544332211UL;
        dynamic_data.dd_gc_elapsed_time(p) = 177;
        dynamic_data.dd_min_size(p) = 188;

        Assert.Equal((nint)(-11), dd.new_allocation);
        Assert.Equal((nint)(-22), dd.gc_new_allocation);
        Assert.Equal(1.5f, dd.surv);
        Assert.Equal((nuint)33, dd.desired_allocation);
        Assert.Equal((nuint)44, dd.begin_data_size);
        Assert.Equal((nuint)55, dd.survived_size);
        Assert.Equal((nuint)66, dd.pinned_survived_size);
        Assert.Equal((nuint)77, dd.artificial_pinned_survived_size);
        Assert.Equal((nuint)88, dd.added_pinned_size);
        Assert.Equal((nuint)99, dd.padding_size);
#if TARGET_ARM || TARGET_WASM
        Assert.Equal((nuint)100, dd.num_npinned_plugs);
#endif
        Assert.Equal((nuint)111, dd.current_size);
        Assert.Equal((nuint)122, dd.collection_count);
        Assert.Equal((nuint)133, dd.promoted_size);
        Assert.Equal((nuint)144, dd.freach_previous_promotion);
        Assert.Equal((nuint)155, dd.fragmentation);
        Assert.Equal((nuint)166, dd.gc_clock);
        Assert.Equal(0x1122334455667788UL, dd.time_clock);
        Assert.Equal(0x8877665544332211UL, dd.previous_time_clock);
        Assert.Equal((nuint)177, dd.gc_elapsed_time);
        Assert.Equal((nuint)188, dd.min_size);

        // The accessors read back the same values they set.
        Assert.Equal((nuint)166, dynamic_data.dd_gc_clock(p));
        Assert.Equal((nuint)188, dynamic_data.dd_min_size(p));
    }

    [Fact]
    public void SdataAccessorsReadAndWriteThroughSdata()
    {
        static_data sd = default;
        dynamic_data dd = default;
        dd.sdata = &sd;
        dynamic_data* p = &dd;

        fixed (float* f = &dynamic_data.dd_limit(p))
        {
            Assert.True(f == &sd.limit);
        }
        fixed (float* f = &dynamic_data.dd_max_limit(p))
        {
            Assert.True(f == &sd.max_limit);
        }
        fixed (nuint* f = &dynamic_data.dd_max_size(p))
        {
            Assert.True(f == &sd.max_size);
        }
        fixed (nuint* f = &dynamic_data.dd_fragmentation_limit(p))
        {
            Assert.True(f == &sd.fragmentation_limit);
        }
        fixed (float* f = &dynamic_data.dd_fragmentation_burden_limit(p))
        {
            Assert.True(f == &sd.fragmentation_burden_limit);
        }
        fixed (nuint* f = &dynamic_data.dd_gc_clock_interval(p))
        {
            Assert.True(f == &sd.gc_clock);
        }
        fixed (ulong* f = &dynamic_data.dd_time_clock_interval(p))
        {
            Assert.True(f == &sd.time_clock);
        }

        dynamic_data.dd_limit(p) = 0.5f;
        dynamic_data.dd_max_limit(p) = 0.25f;
        dynamic_data.dd_max_size(p) = 4096;
        dynamic_data.dd_fragmentation_limit(p) = 512;
        dynamic_data.dd_fragmentation_burden_limit(p) = 0.125f;
        dynamic_data.dd_gc_clock_interval(p) = 7;
        dynamic_data.dd_time_clock_interval(p) = 0xdeadbeefUL;

        Assert.Equal(0.5f, sd.limit);
        Assert.Equal(0.25f, sd.max_limit);
        Assert.Equal((nuint)4096, sd.max_size);
        Assert.Equal((nuint)512, sd.fragmentation_limit);
        Assert.Equal(0.125f, sd.fragmentation_burden_limit);
        Assert.Equal((nuint)7, sd.gc_clock);
        Assert.Equal(0xdeadbeefUL, sd.time_clock);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.125f, 0.25f)]
    [InlineData(0.3125f, 0.625f)]
    // 2 * 0.375 == 0.75, which the cap keeps rather than exceeds.
    [InlineData(0.375f, 0.75f)]
    [InlineData(0.5f, 0.75f)]
    [InlineData(1f, 0.75f)]
    [InlineData(float.NaN, float.NaN)]
    public void VFragmentationBurdenLimitDoublesAndCapsAt075(float burden, float expected)
    {
        static_data sd = default;
        sd.fragmentation_burden_limit = burden;
        dynamic_data dd = default;
        dd.sdata = &sd;

        Assert.Equal(expected, dynamic_data.dd_v_fragmentation_burden_limit(&dd));
    }

    private static nuint OffsetOf(void* field, generation* g) => (nuint)((byte*)field - (byte*)g);

    [Fact]
    public void GenerationInitializeBringsUpEmbeddedAllocatorAndLeavesOtherFieldsZero()
    {
        generation g = default;
        generation* p = &g;

        generation.initialize(p);

        // The load-bearing part of native default construction is the embedded allocator: a young
        // generation must come up with a single bucket, which the C# struct default would not give.
        allocator* a = generation.generation_allocator(p);
        Assert.Equal(1u, a->number_of_buckets());
        Assert.Equal(1, a->discard_if_no_fit_p());
#if TARGET_64BIT && !TARGET_WASM
        Assert.False(a->is_doubly_linked_p());
#endif

        // initialize touches only the embedded allocator; every other field stays zero.
        Assert.Equal((nuint)0, (nuint)p->start_segment);
        Assert.Equal((nuint)0, (nuint)p->allocation_segment);
        Assert.Equal((nuint)0, (nuint)p->allocation_context_start_region);
        Assert.Equal((nuint)0, (nuint)p->allocation_context.alloc_ptr);
        Assert.Equal((nuint)0, (nuint)p->allocation_context.alloc_limit);
        Assert.Equal((nuint)0, p->free_list_space);
        Assert.Equal((nuint)0, p->free_obj_space);
        Assert.Equal((nuint)0, p->allocation_size);
        Assert.Equal(0, p->allocate_end_seg_p);
        Assert.Equal(0, p->gen_num);
#if USE_REGIONS
        Assert.Equal((nuint)0, (nuint)p->tail_region);
        Assert.Equal((nuint)0, (nuint)p->tail_ro_region);
#else
        Assert.Equal((nuint)0, (nuint)p->allocation_start);
        Assert.Equal((nuint)0, (nuint)p->plan_allocation_start);
        Assert.Equal((nuint)0, p->plan_allocation_start_size);
#endif
#if TARGET_64BIT && !TARGET_WASM
        Assert.Equal(0, p->set_bgc_mark_bit_p);
        Assert.Equal((nuint)0, (nuint)p->last_free_list_allocated);
#endif
    }

    [Fact]
    public void GenerationAccessorsReferToFieldsInNativeOrder()
    {
        generation g = default;
        generation* p = &g;
        nuint previous = 0;

        // allocation_context is the first field; alloc_context adds nothing over gc_alloc_context.
        Assert.True(generation.generation_alloc_context(p) == &p->allocation_context);
        Assert.Equal((nuint)0, OffsetOf(&p->allocation_context, p));

        fixed (heap_segment** f = &generation.generation_start_segment(p))
        {
            Assert.True(f == &p->start_segment);
            previous = Ascending(OffsetOf(f, p), previous);
        }
#if !USE_REGIONS
        fixed (byte** f = &generation.generation_allocation_start(p))
        {
            Assert.True(f == &p->allocation_start);
            previous = Ascending(OffsetOf(f, p), previous);
        }
#endif
        fixed (heap_segment** f = &generation.generation_allocation_segment(p))
        {
            Assert.True(f == &p->allocation_segment);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (byte** f = &generation.generation_allocation_context_start_region(p))
        {
            Assert.True(f == &p->allocation_context_start_region);
            previous = Ascending(OffsetOf(f, p), previous);
        }
#if USE_REGIONS
        fixed (heap_segment** f = &generation.generation_tail_region(p))
        {
            Assert.True(f == &p->tail_region);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (heap_segment** f = &generation.generation_tail_ro_region(p))
        {
            Assert.True(f == &p->tail_ro_region);
            previous = Ascending(OffsetOf(f, p), previous);
        }
#endif
        Assert.True(generation.generation_allocator(p) == &p->free_list_allocator);
        previous = Ascending(OffsetOf(&p->free_list_allocator, p), previous);

        fixed (nuint* f = &generation.generation_free_list_allocated(p))
        {
            Assert.True(f == &p->free_list_allocated);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &generation.generation_end_seg_allocated(p))
        {
            Assert.True(f == &p->end_seg_allocated);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &generation.generation_condemned_allocated(p))
        {
            Assert.True(f == &p->condemned_allocated);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &generation.generation_sweep_allocated(p))
        {
            Assert.True(f == &p->sweep_allocated);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (int* f = &generation.generation_allocate_end_seg_p(p))
        {
            Assert.True(f == &p->allocate_end_seg_p);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &generation.generation_free_list_space(p))
        {
            Assert.True(f == &p->free_list_space);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &generation.generation_free_obj_space(p))
        {
            Assert.True(f == &p->free_obj_space);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &generation.generation_allocation_size(p))
        {
            Assert.True(f == &p->allocation_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
#if !USE_REGIONS
        fixed (byte** f = &generation.generation_plan_allocation_start(p))
        {
            Assert.True(f == &p->plan_allocation_start);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &generation.generation_plan_allocation_start_size(p))
        {
            Assert.True(f == &p->plan_allocation_start_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
#endif
        fixed (nuint* f = &generation.generation_pinned_allocation_compact_size(p))
        {
            Assert.True(f == &p->pinned_allocation_compact_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (nuint* f = &generation.generation_pinned_allocation_sweep_size(p))
        {
            Assert.True(f == &p->pinned_allocation_sweep_size);
            previous = Ascending(OffsetOf(f, p), previous);
        }

        // gen_num has no accessor of its own; it closes the unconditional part of the layout.
        previous = Ascending(OffsetOf(&p->gen_num, p), previous);
#if TARGET_64BIT && !TARGET_WASM
        fixed (int* f = &generation.generation_set_bgc_mark_bit_p(p))
        {
            Assert.True(f == &p->set_bgc_mark_bit_p);
            previous = Ascending(OffsetOf(f, p), previous);
        }
        fixed (byte** f = &generation.generation_last_free_list_allocated(p))
        {
            Assert.True(f == &p->last_free_list_allocated);
            previous = Ascending(OffsetOf(f, p), previous);
        }
#endif
    }

    [Fact]
    public void GenerationRefAndPointerAccessorsMutateTheirFields()
    {
        generation g = default;
        generation* p = &g;

        // generation_alloc_context returns the embedded context; the pointer accessors reach into it.
        Assert.True(generation.generation_alloc_context(p) == &p->allocation_context);
        generation.generation_allocation_pointer(p) = (byte*)0x11;
        generation.generation_allocation_limit(p) = (byte*)0x22;
        Assert.Equal((nuint)0x11, (nuint)p->allocation_context.alloc_ptr);
        Assert.Equal((nuint)0x22, (nuint)p->allocation_context.alloc_limit);

        generation.generation_start_segment(p) = (heap_segment*)0x100;
        generation.generation_allocation_segment(p) = (heap_segment*)0x200;
        generation.generation_allocation_context_start_region(p) = (byte*)0x300;
        Assert.Equal((nuint)0x100, (nuint)p->start_segment);
        Assert.Equal((nuint)0x200, (nuint)p->allocation_segment);
        Assert.Equal((nuint)0x300, (nuint)p->allocation_context_start_region);

        generation.generation_free_list_allocated(p) = 5;
        generation.generation_end_seg_allocated(p) = 7;
        generation.generation_condemned_allocated(p) = 9;
        Assert.Equal((nuint)5, p->free_list_allocated);
        Assert.Equal((nuint)7, p->end_seg_allocated);
        Assert.Equal((nuint)9, p->condemned_allocated);

        // generation_total_plan_allocated sums the three planning allocation counters.
        Assert.Equal((nuint)21, generation.generation_total_plan_allocated(p));

        generation.generation_sweep_allocated(p) = 13;
        generation.generation_allocate_end_seg_p(p) = 1;
        generation.generation_free_list_space(p) = 41;
        generation.generation_free_obj_space(p) = 42;
        generation.generation_allocation_size(p) = 43;
        generation.generation_pinned_allocation_compact_size(p) = 44;
        generation.generation_pinned_allocation_sweep_size(p) = 45;
        Assert.Equal((nuint)13, p->sweep_allocated);
        Assert.Equal(1, p->allocate_end_seg_p);
        Assert.Equal((nuint)41, p->free_list_space);
        Assert.Equal((nuint)42, p->free_obj_space);
        Assert.Equal((nuint)43, p->allocation_size);
        Assert.Equal((nuint)44, p->pinned_allocation_compact_size);
        Assert.Equal((nuint)45, p->pinned_allocation_sweep_size);

#if USE_REGIONS
        generation.generation_tail_region(p) = (heap_segment*)0x400;
        generation.generation_tail_ro_region(p) = (heap_segment*)0x500;
        Assert.Equal((nuint)0x400, (nuint)p->tail_region);
        Assert.Equal((nuint)0x500, (nuint)p->tail_ro_region);

        // start_segment_rw returns a non-null tail_ro_region and otherwise the start segment.
        Assert.Equal((nuint)0x500, (nuint)generation.generation_start_segment_rw(p));
        generation.generation_tail_ro_region(p) = null;
        Assert.Equal((nuint)0x100, (nuint)generation.generation_start_segment_rw(p));
#else
        generation.generation_allocation_start(p) = (byte*)0x600;
        generation.generation_plan_allocation_start(p) = (byte*)0x700;
        generation.generation_plan_allocation_start_size(p) = 0x800;
        Assert.Equal((nuint)0x600, (nuint)p->allocation_start);
        Assert.Equal((nuint)0x700, (nuint)p->plan_allocation_start);
        Assert.Equal((nuint)0x800, p->plan_allocation_start_size);
#endif

#if TARGET_64BIT && !TARGET_WASM
        generation.generation_set_bgc_mark_bit_p(p) = 1;
        generation.generation_last_free_list_allocated(p) = (byte*)0x900;
        Assert.Equal(1, p->set_bgc_mark_bit_p);
        Assert.Equal((nuint)0x900, (nuint)p->last_free_list_allocated);
#endif
    }

#if USE_REGIONS
    [Fact]
    public void MakeGenerationResetsSohStateAndPreservesListPointers()
    {
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generation.initialize(&generations[i]);
        }

        heap_segment segment = default;
        heap_segment.heap_segment_mem(&segment) = (byte*)0x1000;
        generation* gen = &generations[(int)gc_generation_num.soh_gen1];
        gen->allocation_context.alloc_ptr = (byte*)0x1;
        gen->allocation_context.alloc_limit = (byte*)0x2;
        gen->allocation_context.alloc_bytes = 3;
        gen->allocation_context.alloc_bytes_uoh = 4;
        gen->allocation_context_start_region = (byte*)0x5;
        gen->start_segment = (heap_segment*)0x6;
        gen->tail_region = (heap_segment*)0x7;
        gen->tail_ro_region = (heap_segment*)0x8;
        gen->allocation_segment = (heap_segment*)0x9;
        gen->free_list_space = 10;
        gen->free_list_allocated = 11;
        gen->end_seg_allocated = 12;
        gen->condemned_allocated = 13;
        gen->sweep_allocated = 14;
        gen->free_obj_space = 15;
        gen->allocation_size = 16;
        gen->pinned_allocation_sweep_size = 17;
        gen->pinned_allocation_compact_size = 18;
        gen->allocate_end_seg_p = 1;
#if TARGET_64BIT && !TARGET_WASM
        gen->set_bgc_mark_bit_p = 1;
#endif
        allocator.alloc_list_head_of(&gen->free_list_allocator, 0) = (byte*)0xA;
        allocator.alloc_list_tail_of(&gen->free_list_allocator, 0) = (byte*)0xB;

        gc_heap.make_generation(
            generations,
            (int)gc_generation_num.soh_gen1,
            &segment,
            heap_segment.heap_segment_mem(&segment));

        Assert.Equal((int)gc_generation_num.soh_gen1, gen->gen_num);
        Assert.Equal((nuint)0, (nuint)gen->allocation_context.alloc_ptr);
        Assert.Equal((nuint)0, (nuint)gen->allocation_context.alloc_limit);
        Assert.Equal(0L, gen->allocation_context.alloc_bytes);
        Assert.Equal(0L, gen->allocation_context.alloc_bytes_uoh);
        Assert.Equal((nuint)0, (nuint)gen->allocation_context_start_region);
        Assert.Equal((nuint)(&segment), (nuint)gen->start_segment);
        Assert.Equal((nuint)(&segment), (nuint)gen->tail_region);
        Assert.Equal((nuint)0, (nuint)gen->tail_ro_region);
        Assert.Equal((nuint)(&segment), (nuint)gen->allocation_segment);
        Assert.Equal((nuint)0, gen->free_list_space);
        Assert.Equal((nuint)0, gen->free_list_allocated);
        Assert.Equal((nuint)0, gen->end_seg_allocated);
        Assert.Equal((nuint)0, gen->condemned_allocated);
        Assert.Equal((nuint)0, gen->sweep_allocated);
        Assert.Equal((nuint)0, gen->free_obj_space);
        Assert.Equal((nuint)0, gen->allocation_size);
        Assert.Equal((nuint)0, gen->pinned_allocation_sweep_size);
        Assert.Equal((nuint)0, gen->pinned_allocation_compact_size);
        Assert.Equal(0, gen->allocate_end_seg_p);
#if TARGET_64BIT && !TARGET_WASM
        Assert.Equal(0, gen->set_bgc_mark_bit_p);
#endif
        Assert.Equal((nuint)0, (nuint)allocator.alloc_list_head_of(&gen->free_list_allocator, 0));
        Assert.Equal((nuint)0, (nuint)allocator.alloc_list_tail_of(&gen->free_list_allocator, 0));
    }

    [Fact]
    public void ThreadUohSegmentAppendsAfterEmptyAndNonemptyWritableLists()
    {
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generation.initialize(&generations[i]);
        }

        heap_segment first = default;
        heap_segment second = default;
        heap_segment third = default;
        heap_segment fourth = default;
        heap_segment readOnly = default;
        readOnly.flags = heap_segment.heap_segment_flags_readonly;
        heap_segment.heap_segment_next(&readOnly) = &third;

        gc_heap.make_generation(
            generations,
            (int)gc_generation_num.loh_generation,
            &first,
            (byte*)0x1000);
        generation* loh = gc_heap.generation_of(generations, (int)gc_generation_num.loh_generation);

        Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_next(&first));
        gc_heap.thread_uoh_segment(generations, (int)gc_generation_num.loh_generation, &second);
        Assert.Equal((nuint)(&second), (nuint)heap_segment.heap_segment_next(&first));
        Assert.Equal((nuint)(&first), (nuint)generation.generation_allocation_segment(loh));

        heap_segment.heap_segment_next(&second) = &readOnly;
        Assert.Equal((nuint)(&third), (nuint)gc_heap.heap_segment_next_rw(&second));
        gc_heap.thread_uoh_segment(generations, (int)gc_generation_num.loh_generation, &fourth);

        Assert.Equal((nuint)(&readOnly), (nuint)heap_segment.heap_segment_next(&second));
        Assert.Equal((nuint)(&third), (nuint)heap_segment.heap_segment_next(&readOnly));
        Assert.Equal((nuint)(&fourth), (nuint)heap_segment.heap_segment_next(&third));
        Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_next(&fourth));
    }
#endif

#if USE_REGIONS
    [Fact]
    public void GenerationRegionInfoHasTwoSegmentPointers()
    {
        generation_region_info info = default;

        Assert.Equal((nuint)0, (nuint)info.head);
        Assert.Equal((nuint)0, (nuint)info.tail);
        Assert.Equal((nuint)(2 * sizeof(void*)), (nuint)sizeof(generation_region_info));
    }
#endif

    [Fact]
    public void SegMappingDefaultStateIsZeroed()
    {
        seg_mapping mapping = default;
        byte* bytes = (byte*)&mapping;

        for (int i = 0; i < sizeof(seg_mapping); i++)
        {
            Assert.Equal((byte)0, bytes[i]);
        }
    }

    [Fact]
    public void SegMappingFieldsFollowNativeOrder()
    {
        seg_mapping mapping = default;
        seg_mapping* p = &mapping;

#if USE_REGIONS
        Assert.Equal((nuint)0, OffsetOf(&p->region_info, p));
        Assert.Equal((nuint)sizeof(heap_segment), (nuint)sizeof(seg_mapping));
#else
        nuint previous = 0;

        Assert.Equal((nuint)0, OffsetOf(&p->boundary, p));
#if MULTIPLE_HEAPS
        previous = Ascending(OffsetOf(&p->h0, p), previous);
        previous = Ascending(OffsetOf(&p->h1, p), previous);
#endif
        previous = Ascending(OffsetOf(&p->seg0, p), previous);
        previous = Ascending(OffsetOf(&p->seg1, p), previous);
#endif
    }

#if USE_REGIONS
    [Fact]
    public void SegMappingUseRegionsSchemaEmbedsHeapSegmentAtNativeOffset()
    {
        seg_mapping mapping = default;

        Assert.Equal((nuint)0, OffsetOf(&mapping.region_info, &mapping));
        Assert.Equal((nuint)sizeof(heap_segment), (nuint)sizeof(seg_mapping));
        Assert.Equal((nuint)sizeof(void*), AlignmentOfSegMapping());
    }

    [Fact]
    public void SegMappingEmbedsFullHeapSegmentAsRegionInfo()
    {
        seg_mapping mapping = default;
        mapping.region_info.flags = heap_segment.heap_segment_flags_poh;

        Assert.Equal(heap_segment.heap_segment_flags_poh, mapping.region_info.flags);
        Assert.Equal((nuint)sizeof(heap_segment), (nuint)sizeof(seg_mapping));
    }

    [Fact]
    public void RegionMappingIndexHelpersPreserveAbsoluteShiftArithmetic()
    {
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowest = GCCommon.g_gc_lowest_address;
        byte* oldHighest = GCCommon.g_gc_highest_address;

        try
        {
            gc_heap.min_segment_size_shr = 12;
            GCCommon.g_gc_lowest_address = (byte*)0x2000;
            GCCommon.g_gc_highest_address = (byte*)0xA000;

            Assert.Equal((nuint)0, gc_heap.seg_mapping_word_of((byte*)0x0FFF));
            Assert.Equal((nuint)1, gc_heap.seg_mapping_word_of((byte*)0x1000));
            Assert.Equal((nuint)1, gc_heap.seg_mapping_word_of((byte*)0x1FFF));
            Assert.Equal((nuint)2, gc_heap.seg_mapping_word_of((byte*)0x2000));
            Assert.Equal((nuint)7, gc_heap.seg_mapping_word_of((byte*)0x7ABC));
            Assert.Equal((nuint)0x7000, (nuint)gc_heap.align_lower_segment((byte*)0x7ABC));

            Assert.Equal((nuint)2, gc_heap.get_skewed_basic_region_index_for_address((byte*)0x2000));
            Assert.Equal((nuint)4, gc_heap.get_skewed_basic_region_index_for_address((byte*)0x4FFF));
            Assert.Equal((nuint)0, gc_heap.get_basic_region_index_for_address((byte*)0x2000));
            Assert.Equal((nuint)1, gc_heap.get_basic_region_index_for_address((byte*)0x3000));
            Assert.Equal((nuint)7, gc_heap.get_basic_region_index_for_address((byte*)0x9000));
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowest;
            GCCommon.g_gc_highest_address = oldHighest;
        }
    }

    [Fact]
    public void RegionSegmentMappingSizeHelpersPreserveNativeAlignmentRules()
    {
        nuint oldShift = gc_heap.min_segment_size_shr;

        try
        {
            gc_heap.min_segment_size_shr = 12;

            Assert.Equal((nuint)0x2000, (nuint)gc_heap.align_on_segment((byte*)0x1001));
            Assert.Equal((nuint)0x2000, (nuint)gc_heap.align_on_segment((byte*)0x2000));
            Assert.Equal((nuint)(4 * sizeof(seg_mapping)), gc_heap.size_seg_mapping_table_of((byte*)0x1800, (byte*)0x4100));
            Assert.Equal((nuint)3, gc_heap.size_region_to_generation_table_of((byte*)0x1800, (byte*)0x4800));
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
        }
    }

    [Fact]
    public void ReadOnlyRegionMappingMarksOnlyClippedIntersectingEntries()
    {
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowest = GCCommon.g_gc_lowest_address;
        byte* oldHighest = GCCommon.g_gc_highest_address;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        seg_mapping* table = stackalloc seg_mapping[8];
        heap_segment segment = default;

        try
        {
            gc_heap.min_segment_size_shr = 12;
            GCCommon.g_gc_lowest_address = (byte*)0x2000;
            GCCommon.g_gc_highest_address = (byte*)0x5000;
            GCCommon.seg_mapping_table = table;

            segment.mem = (byte*)0x1000;
            segment.reserved = (byte*)0x7000;

            Assert.Equal((nuint)2, gc_heap.ro_seg_begin_index(&segment));
            Assert.Equal((nuint)5, gc_heap.ro_seg_end_index(&segment));

            gc_heap.seg_mapping_table_add_ro_segment(&segment);

            Assert.Equal((nuint)0, (nuint)table[1].region_info.allocated);
            Assert.Equal(seg_mapping.ro_in_entry, (nuint)table[2].region_info.allocated);
            Assert.Equal(seg_mapping.ro_in_entry, (nuint)table[3].region_info.allocated);
            Assert.Equal(seg_mapping.ro_in_entry, (nuint)table[4].region_info.allocated);
            Assert.Equal(seg_mapping.ro_in_entry, (nuint)table[5].region_info.allocated);
            Assert.Equal((nuint)0, (nuint)table[6].region_info.allocated);

            segment.mem = (byte*)0x5000;
            segment.reserved = (byte*)0x6000;
            gc_heap.seg_mapping_table_add_ro_segment(&segment);
            Assert.Equal((nuint)0, (nuint)table[6].region_info.allocated);

            gc_heap.seg_mapping_table_remove_ro_segment(&segment);
            Assert.Equal((nuint)0, (nuint)table[6].region_info.allocated);
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowest;
            GCCommon.g_gc_highest_address = oldHighest;
            GCCommon.seg_mapping_table = oldTable;
        }
    }

    [Fact]
    public void RegionMappingDirectLookupReinterpretsSegMappingEntryAsHeapSegment()
    {
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        seg_mapping* table = stackalloc seg_mapping[8];

        try
        {
            gc_heap.min_segment_size_shr = 12;
            GCCommon.seg_mapping_table = table;
            table[3].region_info.mem = (byte*)0x3456;
            table[3].region_info.allocated = (byte*)0x3ABC;

            heap_segment* region = gc_heap.get_region_info((byte*)0x3000);

            Assert.Equal((nuint)(&table[3]), (nuint)region);
            Assert.Equal((nuint)(&table[3].region_info), (nuint)region);
            Assert.Equal((nuint)0x3456, (nuint)heap_segment.heap_segment_mem(region));
            Assert.Equal((nuint)0x3ABC, (nuint)heap_segment.heap_segment_allocated(region));
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.seg_mapping_table = oldTable;
        }
    }

    [Fact]
    public void RegionOfAndGetRegionAtIndexPreserveSkewedAbsoluteIndexing()
    {
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowest = GCCommon.g_gc_lowest_address;
        byte* oldHighest = GCCommon.g_gc_highest_address;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        seg_mapping* table = stackalloc seg_mapping[4];

        try
        {
            gc_heap.min_segment_size_shr = 12;
            GCCommon.g_gc_lowest_address = (byte*)0x5000;
            GCCommon.g_gc_highest_address = (byte*)0x9000;
            GCCommon.seg_mapping_table = table - 5;
            table[2].region_info.gen_num = 2;

            heap_segment* regionOf = gc_heap.region_of((byte*)0x7001);
            heap_segment* regionAtIndex = gc_heap.get_region_at_index(2);

            Assert.Equal((nuint)(&table[2]), (nuint)regionOf);
            Assert.Equal((nuint)(&table[2]), (nuint)regionAtIndex);
            Assert.Equal(2, gc_heap.get_region_gen_num(regionOf));
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowest;
            GCCommon.g_gc_highest_address = oldHighest;
            GCCommon.seg_mapping_table = oldTable;
        }
    }

    [Fact]
    public void RegionGenerationMapReadsUseSkewedAbsoluteIndicesAndPackedFields()
    {
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowest = GCCommon.g_gc_lowest_address;
        byte* oldHighest = GCCommon.g_gc_highest_address;
        seg_mapping* oldSegMappingTable = GCCommon.seg_mapping_table;
        region_info* oldGenerationMap = gc_heap.map_region_to_generation;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
        seg_mapping* segMappingTable = stackalloc seg_mapping[4];
        region_info* generationMap = stackalloc region_info[4];

        try
        {
            gc_heap.min_segment_size_shr = 12;
            GCCommon.g_gc_lowest_address = (byte*)0x5000;
            GCCommon.g_gc_highest_address = (byte*)0x9000;
            GCCommon.seg_mapping_table = segMappingTable - 5;
            gc_heap.map_region_to_generation = generationMap;
            gc_heap.map_region_to_generation_skewed = generationMap - 5;

            Assert.Equal((nuint)1, (nuint)sizeof(region_info));

            segMappingTable[1].region_info.gen_num = 1;
            segMappingTable[1].region_info.plan_gen_num = 1;
            segMappingTable[1].region_info.flags = heap_segment.heap_segment_flags_demoted;
            generationMap[1] = (region_info)((byte)region_info.RI_GEN_1 | (byte)region_info.RI_PLAN_GEN_1 | (byte)region_info.RI_DEMOTED);

            segMappingTable[2].region_info.gen_num = 2;
            segMappingTable[2].region_info.plan_gen_num = 2;
            generationMap[2] = (region_info)((byte)region_info.RI_GEN_2 | (byte)region_info.RI_PLAN_GEN_2);

            Assert.Equal(1, gc_heap.get_region_gen_num((byte*)0x6000));
            Assert.Equal(1, gc_heap.get_region_gen_num((byte*)0x6FFF));
            Assert.Equal(1, gc_heap.get_region_plan_gen_num((byte*)0x6000));
            Assert.True(gc_heap.is_region_demoted((byte*)0x6FFF));

            Assert.Equal(2, gc_heap.get_region_gen_num((byte*)0x7000));
            Assert.Equal(2, gc_heap.get_region_plan_gen_num((byte*)0x7000));
            Assert.False(gc_heap.is_region_demoted((byte*)0x7000));
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowest;
            GCCommon.g_gc_highest_address = oldHighest;
            GCCommon.seg_mapping_table = oldSegMappingTable;
            gc_heap.map_region_to_generation = oldGenerationMap;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
        }
    }

    [Fact]
    public void RegionGenerationMapFlagsKeepSegmentFieldsConsistent()
    {
        nuint oldShift = gc_heap.min_segment_size_shr;
        byte* oldLowest = GCCommon.g_gc_lowest_address;
        byte* oldHighest = GCCommon.g_gc_highest_address;
        region_allocator oldGlobalRegionAllocator = gc_heap.global_region_allocator;
        region_info* oldGenerationMap = gc_heap.map_region_to_generation;
        region_info* oldSkewedGenerationMap = gc_heap.map_region_to_generation_skewed;
        region_info* generationMap = stackalloc region_info[4];
        heap_segment region = default;

        try
        {
            gc_heap.min_segment_size_shr = 12;
            GCCommon.g_gc_lowest_address = (byte*)0x5000;
            GCCommon.g_gc_highest_address = (byte*)0x9000;
            gc_heap.global_region_allocator.initialize_alignment(0x1000);
            gc_heap.map_region_to_generation = generationMap;
            gc_heap.map_region_to_generation_skewed = generationMap - 5;
            generationMap[1] = (region_info)((byte)region_info.RI_GEN_1 | (byte)region_info.RI_PLAN_GEN_2 | (byte)region_info.RI_DEMOTED);

            region.mem = (byte*)0x6000 + sizeof(aligned_plug_and_gap);
            region.reserved = (byte*)0x7000;
            region.flags = heap_segment.heap_segment_flags_demoted;

            gc_heap.set_region_sweep_in_plan(&region);
            Assert.Equal((byte)1, region.swept_in_plan_p);
            Assert.Equal(
                (byte)region_info.RI_GEN_1 | (byte)region_info.RI_PLAN_GEN_2 | (byte)region_info.RI_DEMOTED | (byte)region_info.RI_SIP,
                (byte)generationMap[1]);

            gc_heap.clear_region_sweep_in_plan(&region);
            Assert.Equal((byte)0, region.swept_in_plan_p);
            Assert.Equal(
                (byte)region_info.RI_GEN_1 | (byte)region_info.RI_PLAN_GEN_2 | (byte)region_info.RI_DEMOTED,
                (byte)generationMap[1]);

            gc_heap.clear_region_demoted(&region);
            Assert.Equal((nuint)0, region.flags & heap_segment.heap_segment_flags_demoted);
            Assert.Equal(
                (byte)region_info.RI_GEN_1 | (byte)region_info.RI_PLAN_GEN_2,
                (byte)generationMap[1]);
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.g_gc_lowest_address = oldLowest;
            GCCommon.g_gc_highest_address = oldHighest;
            gc_heap.global_region_allocator = oldGlobalRegionAllocator;
            gc_heap.map_region_to_generation = oldGenerationMap;
            gc_heap.map_region_to_generation_skewed = oldSkewedGenerationMap;
        }
    }

    [Fact]
    public void RegionStartObjectHelpersUseRegionMemory()
    {
        heap_segment region = default;
        generation gen = default;
        region.mem = (byte*)0x12345678;

        Assert.Equal((nuint)region.mem, (nuint)gc_heap.get_uoh_start_object(&region, &gen));
        Assert.Equal((nuint)region.mem, (nuint)gc_heap.get_soh_start_object(&region, &gen));
        Assert.Equal((nuint)0, gc_heap.get_soh_start_obj_len(region.mem));
    }

    [Fact]
    public void RegionMappingForAddressBacktracksLargeRegionContinuationSentinel()
    {
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        seg_mapping* table = stackalloc seg_mapping[8];

        try
        {
            gc_heap.min_segment_size_shr = 12;
            GCCommon.seg_mapping_table = table;
            table[4].region_info.mem = (byte*)0x4000;
            table[4].region_info.allocated = (byte*)0x4ABC;
            table[5].region_info.allocated = (byte*)(nint)(-1);
            table[6].region_info.allocated = (byte*)(nint)(-2);

            Assert.Equal((nuint)(&table[4]), (nuint)gc_heap.get_region_info_for_address((byte*)0x4000));
            Assert.Equal((nuint)(&table[4]), (nuint)gc_heap.get_region_info_for_address((byte*)0x5FFF));
            Assert.Equal((nuint)(&table[4]), (nuint)gc_heap.get_region_info_for_address((byte*)0x6123));
            Assert.Equal((nuint)0x4ABC, (nuint)heap_segment.heap_segment_allocated(gc_heap.get_region_info_for_address((byte*)0x6FFF)));
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
            GCCommon.seg_mapping_table = oldTable;
        }
    }

    [Fact]
    public void RegionMappingFreeRegionClassificationUsesAllocatedNull()
    {
        heap_segment region = default;

        region.allocated = null;
        region.mem = (byte*)0x1000;
        Assert.True(gc_heap.is_free_region(&region));

        region.allocated = (byte*)1;
        Assert.False(gc_heap.is_free_region(&region));

        region.allocated = (byte*)(nint)(-1);
        Assert.False(gc_heap.is_free_region(&region));
    }

    [Theory]
    [InlineData(0UL, -1)]
    [InlineData(1UL, 0)]
    [InlineData(0x1000UL, 12)]
    [InlineData(0x400000UL, 22)]
    public void MinSegmentSizeShiftInitializationUsesHighestSetBit(ulong size, int expectedShift)
    {
        nuint oldShift = gc_heap.min_segment_size_shr;
        try
        {
            gc_heap.initialize_min_segment_size_shr((nuint)size);

            Assert.Equal((nuint)expectedShift, gc_heap.min_segment_size_shr);
        }
        finally
        {
            gc_heap.min_segment_size_shr = oldShift;
        }
    }

    [Theory]
    [InlineData(0UL, true)]
    [InlineData(1UL, true)]
    [InlineData(2UL, true)]
    [InlineData(3UL, false)]
    [InlineData(0x400000UL, true)]
    public void RegionSizePowerOfTwoCheckMatchesNative(ulong size, bool expected)
    {
        Assert.Equal(expected, gc_heap.power_of_two_p((nuint)size));
    }
#endif

    [Fact]
    public void SegMappingReadOnlyEntryFlagUsesLowBit()
    {
        const nuint SegmentAddress = 0x100;
        nuint taggedSegment = SegmentAddress | seg_mapping.ro_in_entry;

        Assert.Equal((nuint)1, seg_mapping.ro_in_entry);
        Assert.Equal(seg_mapping.ro_in_entry, taggedSegment & seg_mapping.ro_in_entry);
        Assert.Equal(SegmentAddress, taggedSegment & ~seg_mapping.ro_in_entry);
    }

    [Fact]
    public void HeapSegmentDefaultStateIsZeroed()
    {
        heap_segment segment = default;
        heap_segment* p = &segment;

        Assert.Equal((nuint)0, (nuint)p->allocated);
        Assert.Equal((nuint)0, (nuint)p->committed);
        Assert.Equal((nuint)0, (nuint)p->reserved);
        Assert.Equal((nuint)0, (nuint)p->used);
        Assert.Equal((nuint)0, (nuint)p->mem);
        Assert.Equal((nuint)0, p->flags);
        Assert.Equal((nuint)0, (nuint)p->next);
        Assert.Equal((nuint)0, (nuint)p->background_allocated);
        Assert.Equal((nuint)0, (nuint)p->plan_allocated);
        Assert.Equal((nuint)0, (nuint)p->saved_allocated);
        Assert.Equal((nuint)0, (nuint)p->saved_bg_allocated);
#if !USE_REGIONS || MULTIPLE_HEAPS
        Assert.Equal((nuint)0, (nuint)p->decommit_target);
#endif
#if USE_REGIONS
        Assert.Equal((nuint)0, p->survived);
        Assert.Equal((byte)0, p->gen_num);
        Assert.Equal((byte)0, p->swept_in_plan_p);
        Assert.Equal(0, p->plan_gen_num);
        Assert.Equal(0, p->old_card_survived);
        Assert.Equal(0, p->pinned_survived);
        Assert.Equal(0, p->age_in_free);
        Assert.Equal((nuint)0, (nuint)p->free_list_head);
        Assert.Equal((nuint)0, (nuint)p->free_list_tail);
        Assert.Equal((nuint)0, p->free_list_size);
        Assert.Equal((nuint)0, p->free_obj_size);
        Assert.Equal((nuint)0, (nuint)p->prev_free_region);
        Assert.Equal((nuint)0, (nuint)p->containing_free_list);
#endif
    }

    [Fact]
    public void HeapSegmentFieldsAndReferenceAccessorsFollowNativeOrder()
    {
        heap_segment segment = default;
        heap_segment* p = &segment;
        nuint previous = 0;

        Assert.Equal((nuint)0, OffsetOf(&p->allocated, p));
        previous = Ascending(OffsetOf(&p->committed, p), previous);
        previous = Ascending(OffsetOf(&p->reserved, p), previous);
        previous = Ascending(OffsetOf(&p->used, p), previous);
        previous = Ascending(OffsetOf(&p->mem, p), previous);
        previous = Ascending(OffsetOf(&p->flags, p), previous);
        previous = Ascending(OffsetOf(&p->next, p), previous);
        previous = Ascending(OffsetOf(&p->background_allocated, p), previous);
#if MULTIPLE_HEAPS
        previous = Ascending(OffsetOf(&p->heap, p), previous);
#if DEBUG && !USE_REGIONS
        previous = Ascending(OffsetOf(&p->saved_committed, p), previous);
        previous = Ascending(OffsetOf(&p->saved_desired_allocation, p), previous);
#endif
#endif
#if !USE_REGIONS || MULTIPLE_HEAPS
        previous = Ascending(OffsetOf(&p->decommit_target, p), previous);
#endif
        previous = Ascending(OffsetOf(&p->plan_allocated, p), previous);
        previous = Ascending(OffsetOf(&p->saved_allocated, p), previous);
        previous = Ascending(OffsetOf(&p->saved_bg_allocated, p), previous);
#if USE_REGIONS
        previous = Ascending(OffsetOf(&p->survived, p), previous);
        previous = Ascending(OffsetOf(&p->gen_num, p), previous);
        previous = Ascending(OffsetOf(&p->swept_in_plan_p, p), previous);
        previous = Ascending(OffsetOf(&p->plan_gen_num, p), previous);
        previous = Ascending(OffsetOf(&p->old_card_survived, p), previous);
        previous = Ascending(OffsetOf(&p->pinned_survived, p), previous);
        previous = Ascending(OffsetOf(&p->age_in_free, p), previous);
        previous = Ascending(OffsetOf(&p->free_list_head, p), previous);
        previous = Ascending(OffsetOf(&p->free_list_tail, p), previous);
        previous = Ascending(OffsetOf(&p->free_list_size, p), previous);
        previous = Ascending(OffsetOf(&p->free_obj_size, p), previous);
        previous = Ascending(OffsetOf(&p->prev_free_region, p), previous);
        previous = Ascending(OffsetOf(&p->containing_free_list, p), previous);
#else
        previous = Ascending(OffsetOf(&p->padandplug, p), previous);
#endif

        heap_segment.heap_segment_reserved(p) = (byte*)1;
        heap_segment.heap_segment_committed(p) = (byte*)2;
        heap_segment.heap_segment_used(p) = (byte*)3;
        heap_segment.heap_segment_allocated(p) = (byte*)4;
        heap_segment.heap_segment_next(p) = (heap_segment*)5;
        heap_segment.heap_segment_mem(p) = (byte*)6;
        heap_segment.heap_segment_plan_allocated(p) = (byte*)7;
        heap_segment.heap_segment_saved_allocated(p) = (byte*)8;
#if BACKGROUND_GC
        heap_segment.heap_segment_background_allocated(p) = (byte*)9;
        heap_segment.heap_segment_saved_bg_allocated(p) = (byte*)10;
#endif

        Assert.Equal((nuint)1, (nuint)p->reserved);
        Assert.Equal((nuint)2, (nuint)p->committed);
        Assert.Equal((nuint)3, (nuint)p->used);
        Assert.Equal((nuint)4, (nuint)p->allocated);
        Assert.Equal((nuint)5, (nuint)p->next);
        Assert.Equal((nuint)6, (nuint)p->mem);
        Assert.Equal((nuint)7, (nuint)p->plan_allocated);
        Assert.Equal((nuint)8, (nuint)p->saved_allocated);
#if BACKGROUND_GC
        Assert.Equal((nuint)9, (nuint)p->background_allocated);
        Assert.Equal((nuint)10, (nuint)p->saved_bg_allocated);
#endif
    }

    [Theory]
    [InlineData(0UL, 0, 1)]
    [InlineData(1UL, 1, 0)]
    [InlineData(2UL, 0, 1)]
    [InlineData(3UL, 1, 1)]
    public void HeapSegmentReadOnlyAndInRangeFlagsHaveNativeTruthTable(ulong flags, int readOnly, int inRange)
    {
        heap_segment segment = default;
        segment.flags = (nuint)flags;

        Assert.Equal(readOnly, heap_segment.heap_segment_read_only_p(&segment));
        Assert.Equal(inRange, heap_segment.heap_segment_in_range_p(&segment));
    }

    [Fact]
    public void HeapSegmentRangeTraversalSkipsOutOfRangeReadOnlySegments()
    {
        heap_segment first = default;
        heap_segment skipped = default;
        heap_segment included = default;

        first.next = &skipped;
        skipped.flags = heap_segment.heap_segment_flags_readonly;
        skipped.next = &included;
        included.flags = heap_segment.heap_segment_flags_readonly | heap_segment.heap_segment_flags_inrange;

        Assert.Equal((nuint)0, (nuint)gc_heap.heap_segment_in_range(null));
        Assert.Equal((nuint)(&first), (nuint)gc_heap.heap_segment_in_range(&first));
        Assert.Equal((nuint)(&included), (nuint)gc_heap.heap_segment_in_range(&skipped));
        Assert.Equal((nuint)(&included), (nuint)gc_heap.heap_segment_next_in_range(&first));

        included.next = &skipped;
        skipped.next = null;
        Assert.Equal((nuint)0, (nuint)gc_heap.heap_segment_next_in_range(&included));
    }

    [Fact]
    public void HeapSegmentAddressRangeUsesHalfOpenBounds()
    {
        heap_segment segment = default;
        segment.mem = (byte*)0x1000;
        segment.reserved = (byte*)0x2000;

        Assert.Equal(0, gc_heap.in_range_for_segment((byte*)0xFFF, &segment));
        Assert.Equal(1, gc_heap.in_range_for_segment((byte*)0x1000, &segment));
        Assert.Equal(1, gc_heap.in_range_for_segment((byte*)0x1FFF, &segment));
        Assert.Equal(0, gc_heap.in_range_for_segment((byte*)0x2000, &segment));
    }

    [Fact]
    public void HeapSegmentGenerationIterationBoundsMatchNativeConfiguration()
    {
#if USE_REGIONS
        Assert.Equal(0, gc_heap.get_start_generation_index());
        Assert.Equal(0, gc_heap.get_stop_generation_index(2));
#else
        Assert.Equal(GCInterfaceOffsets.max_generation, gc_heap.get_start_generation_index());
        Assert.Equal(2, gc_heap.get_stop_generation_index(2));
#endif
    }

    [Fact]
    public void HeapSegmentObjectHeapAndBackgroundPredicatesPreserveNativePrecedence()
    {
        heap_segment segment = default;

        Assert.Equal(0, heap_segment.heap_segment_loh_p(&segment));
        Assert.Equal(0, heap_segment.heap_segment_poh_p(&segment));
        Assert.Equal(0, heap_segment.heap_segment_uoh_p(&segment));
        Assert.Equal(gc_oh_num.soh, heap_segment.heap_segment_oh(&segment));

        segment.flags = heap_segment.heap_segment_flags_poh;
        Assert.Equal(1, heap_segment.heap_segment_poh_p(&segment));
        Assert.Equal(1, heap_segment.heap_segment_uoh_p(&segment));
        Assert.Equal(gc_oh_num.poh, heap_segment.heap_segment_oh(&segment));

        segment.flags |= heap_segment.heap_segment_flags_loh;
        Assert.Equal(1, heap_segment.heap_segment_loh_p(&segment));
        Assert.Equal(1, heap_segment.heap_segment_poh_p(&segment));
        Assert.Equal(1, heap_segment.heap_segment_uoh_p(&segment));
        Assert.Equal(gc_oh_num.loh, heap_segment.heap_segment_oh(&segment));

#if BACKGROUND_GC
        segment.flags = heap_segment.heap_segment_flags_decommitted | heap_segment.heap_segment_flags_swept;
        Assert.Equal(1, heap_segment.heap_segment_decommitted_p(&segment));
        Assert.Equal(1, heap_segment.heap_segment_swept_p(&segment));
        segment.flags = 0;
        Assert.Equal(0, heap_segment.heap_segment_decommitted_p(&segment));
        Assert.Equal(0, heap_segment.heap_segment_swept_p(&segment));
#endif
#if BACKGROUND_GC && USE_REGIONS
        segment.flags = heap_segment.heap_segment_flags_overflow;
        Assert.True(heap_segment.heap_segment_overflow_p(&segment));
        segment.flags = 0;
        Assert.False(heap_segment.heap_segment_overflow_p(&segment));
#endif
#if USE_REGIONS
        segment.flags = heap_segment.heap_segment_flags_demoted;
        Assert.True(heap_segment.heap_segment_demoted_p(&segment));
        segment.flags = 0;
        Assert.False(heap_segment.heap_segment_demoted_p(&segment));
#endif
    }

#if USE_REGIONS
    [Fact]
    public void HeapSegmentRegionAccessorsAndFreeListInitializationMutateOnlyTheirFields()
    {
        heap_segment segment = default;
        heap_segment* p = &segment;

        heap_segment.heap_segment_containing_free_list(p) = (region_free_list*)1;
        heap_segment.heap_segment_prev_free_region(p) = (heap_segment*)2;
        heap_segment.heap_segment_gen_num(p) = 3;
        heap_segment.heap_segment_swept_in_plan(p) = 1;
        heap_segment.heap_segment_plan_gen_num(p) = 4;
        heap_segment.heap_segment_age_in_free(p) = 5;
        heap_segment.heap_segment_survived(p) = 6;
        heap_segment.heap_segment_old_card_survived(p) = 7;
        heap_segment.heap_segment_pinned_survived(p) = 8;
        p->free_list_head = (byte*)9;
        p->free_list_tail = (byte*)10;
        p->free_list_size = 11;
        p->free_obj_size = 12;

        Assert.Equal((nuint)1, (nuint)p->containing_free_list);
        Assert.Equal((nuint)2, (nuint)p->prev_free_region);
        Assert.Equal((byte)3, p->gen_num);
        Assert.Equal((byte)1, p->swept_in_plan_p);
        Assert.Equal(4, p->plan_gen_num);
        Assert.Equal(5, p->age_in_free);
        Assert.Equal((nuint)6, p->survived);
        Assert.Equal(7, p->old_card_survived);
        Assert.Equal(8, p->pinned_survived);
        Assert.Equal((nuint)9, (nuint)heap_segment.heap_segment_free_list_head(p));
        Assert.Equal((nuint)10, (nuint)heap_segment.heap_segment_free_list_tail(p));
        Assert.Equal((nuint)11, heap_segment.heap_segment_free_list_size(p));
        Assert.Equal((nuint)12, heap_segment.heap_segment_free_obj_size(p));

        p->init_free_list();

        Assert.Equal((nuint)0, (nuint)p->free_list_head);
        Assert.Equal((nuint)0, (nuint)p->free_list_tail);
        Assert.Equal((nuint)0, p->free_list_size);
        Assert.Equal((nuint)0, p->free_obj_size);
        Assert.Equal((byte)3, p->gen_num);
        Assert.Equal(5, p->age_in_free);
    }

    [Fact]
    public void RegionHelpersPreserveHeaderSkewedSizeArithmetic()
    {
        heap_segment region = default;
        region.mem = (byte*)0x2000;
        region.committed = (byte*)0x2A00;
        region.reserved = (byte*)0x3000;

        byte* expectedStart = region.mem - sizeof(aligned_plug_and_gap);

        Assert.Equal((nuint)expectedStart, (nuint)gc_heap.get_region_start(&region));
        Assert.Equal((nuint)(region.reserved - expectedStart), gc_heap.get_region_size(&region));
        Assert.Equal((nuint)(region.committed - expectedStart), gc_heap.get_region_committed_size(&region));
    }

    [Fact]
    public void RegionFreeListAddAndUnlinkFrontPreserveNativeBookkeeping()
    {
        region_free_list list = default;
        region_free_list* pList = &list;
        heap_segment first = default;
        heap_segment second = default;

        InitializeRegion(&first, 0x1000, 0x1900, 0x2000, age: 3);
        InitializeRegion(&second, 0x3000, 0x3700, 0x4000, age: 7);

        region_free_list.add_region_front(pList, &first);
        region_free_list.add_region_front(pList, &second);

        Assert.Equal((nuint)2, region_free_list.get_num_free_regions(pList));
        Assert.Equal((nuint)(&second), (nuint)list.get_first_free_region());
        Assert.Equal((nuint)(&first), (nuint)heap_segment.heap_segment_next(&second));
        Assert.Equal((nuint)(&second), (nuint)heap_segment.heap_segment_prev_free_region(&first));
        Assert.Equal((nuint)pList, (nuint)heap_segment.heap_segment_containing_free_list(&first));
        Assert.Equal((nuint)pList, (nuint)heap_segment.heap_segment_containing_free_list(&second));

        nuint expectedSize = gc_heap.get_region_size(&first) + gc_heap.get_region_size(&second);
        nuint expectedCommitted = gc_heap.get_region_committed_size(&first) + gc_heap.get_region_committed_size(&second);
        Assert.Equal(expectedSize, list.get_size_free_regions());
        Assert.Equal(expectedCommitted, list.get_size_committed_in_free());

        heap_segment* unlinked = region_free_list.unlink_region_front(pList);
        Assert.Equal((nuint)(&second), (nuint)unlinked);
        Assert.Equal((nuint)1, region_free_list.get_num_free_regions(pList));
        Assert.Equal((nuint)(&first), (nuint)list.get_first_free_region());
        Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_containing_free_list(unlinked));
        Assert.Equal(gc_heap.get_region_size(&first), list.get_size_free_regions());
        Assert.Equal(gc_heap.get_region_committed_size(&first), list.get_size_committed_in_free());
    }

    [Fact]
    public void RegionFreeListSortUsesCommittedSizeThenAge()
    {
        region_free_list list = default;
        heap_segment highCommitted = default;
        heap_segment youngerMid = default;
        heap_segment olderMid = default;

        InitializeRegion(&highCommitted, 0x1000, 0x1C00, 0x2600, age: 4);
        InitializeRegion(&youngerMid, 0x3000, 0x3800, 0x4200, age: 1);
        InitializeRegion(&olderMid, 0x5000, 0x5800, 0x6200, age: 9);

        region_free_list* pList = &list;
        region_free_list.add_region_front(pList, &youngerMid);
        region_free_list.add_region_front(pList, &highCommitted);
        region_free_list.add_region_front(pList, &olderMid);

        heap_segment.heap_segment_age_in_free(&highCommitted) = 4;
        heap_segment.heap_segment_age_in_free(&youngerMid) = 1;
        heap_segment.heap_segment_age_in_free(&olderMid) = 9;

        list.sort_by_committed_and_age();

        heap_segment* first = list.get_first_free_region();
        heap_segment* second = heap_segment.heap_segment_next(first);
        heap_segment* third = heap_segment.heap_segment_next(second);

        Assert.Equal((nuint)(&highCommitted), (nuint)first);
        Assert.Equal((nuint)(&youngerMid), (nuint)second);
        Assert.Equal((nuint)(&olderMid), (nuint)third);
        Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_prev_free_region(first));
        Assert.Equal((nuint)(&highCommitted), (nuint)heap_segment.heap_segment_prev_free_region(second));
        Assert.Equal((nuint)(&youngerMid), (nuint)heap_segment.heap_segment_prev_free_region(third));
    }

    [Fact]
    public void RegionFreeListDescendingInsertionOrdersCommittedSizesAndFullyCommittedFirst()
    {
        region_free_list list = default;
        region_free_list* pList = &list;
        heap_segment small = default;
        heap_segment large = default;
        heap_segment middle = default;
        heap_segment fullyCommitted = default;

        InitializeRegion(&small, 0x1000, 0x1400, 0x2000, age: 1);
        InitializeRegion(&large, 0x3000, 0x3C00, 0x5000, age: 2);
        InitializeRegion(&middle, 0x6000, 0x6800, 0x8000, age: 3);
        InitializeRegion(&fullyCommitted, 0x9000, 0xA000, 0xA000, age: 4);

        region_free_list.add_region_in_descending_order(pList, &small);
        region_free_list.add_region_in_descending_order(pList, &large);
        region_free_list.add_region_in_descending_order(pList, &middle);
        region_free_list.add_region_in_descending_order(pList, &fullyCommitted);

        heap_segment* first = list.get_first_free_region();
        heap_segment* second = heap_segment.heap_segment_next(first);
        heap_segment* third = heap_segment.heap_segment_next(second);
        heap_segment* fourth = heap_segment.heap_segment_next(third);

        Assert.Equal((nuint)(&fullyCommitted), (nuint)first);
        Assert.Equal((nuint)(&large), (nuint)second);
        Assert.Equal((nuint)(&middle), (nuint)third);
        Assert.Equal((nuint)(&small), (nuint)fourth);
        Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_prev_free_region(first));
        Assert.Equal((nuint)first, (nuint)heap_segment.heap_segment_prev_free_region(second));
        Assert.Equal((nuint)second, (nuint)heap_segment.heap_segment_prev_free_region(third));
        Assert.Equal((nuint)third, (nuint)heap_segment.heap_segment_prev_free_region(fourth));
        Assert.Equal(0, heap_segment.heap_segment_age_in_free(&small));
        Assert.Equal(0, heap_segment.heap_segment_age_in_free(&large));
        Assert.Equal(0, heap_segment.heap_segment_age_in_free(&middle));
        Assert.Equal(0, heap_segment.heap_segment_age_in_free(&fullyCommitted));
        Assert.Equal((nuint)4, region_free_list.get_num_free_regions(pList));
    }

    [Fact]
    public void GCSpinLockInitializeSetsNativeLockFreeSentinel()
    {
        GCSpinLock spinLock = default;

        GCSpinLock.initialize(&spinLock);

        Assert.Equal(GCSpinLock.lock_free, spinLock.@lock);
#if DEBUG
        Assert.Equal(-1, (nint)spinLock.holding_thread);
#endif
    }

    [Fact]
    public void RegionAllocatorSchemaExtendsThroughMapFieldsInNativeOrder()
    {
        static nuint AlignUp(nuint value, nuint alignment)
        {
            return unchecked((value + (alignment - 1)) & ~(alignment - 1));
        }

        nuint pointerSize = (nuint)sizeof(void*);
        nuint uintSize = (nuint)sizeof(uint);
        nuint nuintSize = (nuint)sizeof(nuint);
        nuint offset = 0;

        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("global_region_start"));
        offset += pointerSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("global_region_end"));
        offset += pointerSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("global_region_left_used"));
        offset += pointerSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("global_region_right_used"));
        offset += pointerSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("total_free_units"));
        offset += uintSize;
        offset = AlignUp(offset, nuintSize);
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("region_alignment"));
        offset += nuintSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("large_region_alignment"));
        offset += nuintSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("region_allocator_lock"));
        offset += (nuint)sizeof(GCSpinLock);
        offset = AlignUp(offset, pointerSize);
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("region_map_left_start"));
        offset += pointerSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("region_map_left_end"));
        offset += pointerSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("region_map_right_start"));
        offset += pointerSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("region_map_right_end"));
        offset += pointerSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("num_left_used_free_units"));
        offset += uintSize;
        Assert.Equal(offset, (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("num_right_used_free_units"));
        offset += uintSize;

        Assert.Equal(AlignUp(offset, pointerSize), (nuint)sizeof(region_allocator));
    }

    [Fact]
    public void RegionAllocatorMapAddressAndIndexHelpersPreserveNativeArithmetic()
    {
        region_allocator allocator = default;
        byte* allocatorBytes = (byte*)&allocator;

        byte* regionStart = (byte*)0x0010_0000;
        uint* mapStart = (uint*)0x0020_0000;
        nuint regionAlignment = 0x1000;

        *(byte**)(allocatorBytes + (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("global_region_start")) = regionStart;
        *(nuint*)(allocatorBytes + (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("region_alignment")) = regionAlignment;
        *(uint**)(allocatorBytes + (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("region_map_left_start")) = mapStart;

        uint* mapIndex = mapStart + 9;
        byte* address = allocator.region_address_of(mapIndex);
        Assert.Equal((nuint)0x0010_9000, (nuint)address);
        Assert.Equal((nuint)mapIndex, (nuint)allocator.region_map_index_of(address));

        byte* unalignedAddress = (byte*)0x0010_5ABC;
        Assert.Equal((nuint)(mapStart + 5), (nuint)allocator.region_map_index_of(unalignedAddress));
    }

    [Fact]
    public void RegionAllocatorAlignmentSliceComputesLargeRegionFactor()
    {
        gc_heap.global_region_allocator.initialize_alignment(0x1000);

        Assert.Equal(8, region_allocator.LARGE_REGION_FACTOR);
        Assert.Equal(unchecked((int)0x80000000), region_allocator.region_alloc_free_bit);
        Assert.Equal(1, (int)allocate_direction.allocate_forward);
        Assert.Equal(-1, (int)allocate_direction.allocate_backward);
        Assert.Equal((nuint)0x1000, gc_heap.global_region_allocator.get_region_alignment());
        Assert.Equal((nuint)(region_allocator.LARGE_REGION_FACTOR * 0x1000), gc_heap.global_region_allocator.get_large_region_alignment());
    }

    [Theory]
    [InlineData(0xA000ul, 0x0000ul, 0x0000ul, 0u)]
    [InlineData(0xA000ul, 0x3000ul, 0x0000ul, 30u)]
    [InlineData(0xA000ul, 0x0000ul, 0x2000ul, 20u)]
    [InlineData(0xA000ul, 0x3000ul, 0x2000ul, 50u)]
    [InlineData(0x3000ul, 0x1000ul, 0x0000ul, 33u)]
    public void RegionAllocatorVaMemoryLoadPreservesNativeArithmetic(ulong totalBytes, ulong leftUsedBytes, ulong rightUsedBytes, uint expectedLoad)
    {
        region_allocator allocator = default;
        byte* start = (byte*)0x0010_0000;
        byte* end = start + (nint)totalBytes;

        WriteRegionAllocatorPointerField(&allocator, "global_region_start", start);
        WriteRegionAllocatorPointerField(&allocator, "global_region_end", end);
        WriteRegionAllocatorPointerField(&allocator, "global_region_left_used", start + (nint)leftUsedBytes);
        WriteRegionAllocatorPointerField(&allocator, "global_region_right_used", end - (nint)rightUsedBytes);

        Assert.Equal(expectedLoad, allocator.get_va_memory_load());
    }

    [Fact]
    public void RegionAllocatorGetFreePreservesNativeTargetWidthProduct()
    {
        region_allocator allocator = default;

        WriteRegionAllocatorField(&allocator, "total_free_units", 5u);
        WriteRegionAllocatorField(&allocator, "region_alignment", (nuint)0x1000);
        Assert.Equal((nuint)0x5000, allocator.get_free());

#if TARGET_64BIT
        nuint overflowAlignment = ((nuint)1 << 32) + 3;
#else
        nuint overflowAlignment = 0x1001;
#endif
        WriteRegionAllocatorField(&allocator, "total_free_units", uint.MaxValue);
        WriteRegionAllocatorField(&allocator, "region_alignment", overflowAlignment);
        Assert.Equal(unchecked((nuint)uint.MaxValue * overflowAlignment), allocator.get_free());
    }

    [Fact]
    public void RegionAllocatorGetUsedRegionCountReturnsLeftMapCount()
    {
        region_allocator allocator = default;
        uint* map = (uint*)0x0020_0000;

        WriteRegionAllocatorPointerField(&allocator, "region_map_left_start", map);
        WriteRegionAllocatorPointerField(&allocator, "region_map_left_end", map + 5);
        WriteRegionAllocatorPointerField(&allocator, "region_map_right_start", map + 12);
        WriteRegionAllocatorPointerField(&allocator, "region_map_right_end", map + 12);

        Assert.Equal((nuint)5, allocator.get_used_region_count());
    }

    [Fact]
    public void RegionAllocatorUnsafePointerGettersReturnNativeFields()
    {
        region_allocator allocator = default;
        byte* start = (byte*)0x0012_3400;
        byte* leftUsed = (byte*)0x0056_7800;

        WriteRegionAllocatorPointerField(&allocator, "global_region_start", start);
        WriteRegionAllocatorPointerField(&allocator, "global_region_left_used", leftUsed);

        Assert.Equal((nuint)start, (nuint)allocator.get_start());
        Assert.Equal((nuint)leftUsed, (nuint)allocator.get_left_used_unsafe());
    }

    [Fact]
    public void RegionAllocatorInitializeConstructsEmbeddedSpinLock()
    {
        region_allocator allocator = default;

        allocator.initialize();

        int lockOffset = System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>("region_allocator_lock").ToInt32();
        Assert.Equal(GCSpinLock.lock_free, *(int*)((byte*)&allocator + lockOffset));
    }

    [Fact]
    public void RegionAllocatorSpinLockAcquiresAndReleasesUncontended()
    {
        region_allocator allocator = default;
        allocator.initialize();

        allocator.enter_spin_lock();

        Assert.Equal(0, ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock").@lock);

        allocator.leave_spin_lock();

        Assert.Equal(GCSpinLock.lock_free, ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock").@lock);
    }

    [Fact]
    public void RegionAllocatorSpinLockWorkerWaitsUntilRelease()
    {
        region_allocator* allocator = (region_allocator*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)sizeof(region_allocator));
        var workerReady = new ManualResetEventSlim(false);
        var workerAcquired = new ManualResetEventSlim(false);
        var workerCanLeave = new ManualResetEventSlim(false);
        Thread? worker = null;
        bool mainHoldsLock = false;

        try
        {
            allocator->initialize();
            allocator->enter_spin_lock();
            mainHoldsLock = true;

            nuint allocatorAddress = (nuint)allocator;
            worker = new Thread(() =>
            {
                region_allocator* workerAllocator = (region_allocator*)allocatorAddress;
                workerReady.Set();
                workerAllocator->enter_spin_lock();
                workerAcquired.Set();
                workerCanLeave.Wait();
                workerAllocator->leave_spin_lock();
            })
            {
                IsBackground = true,
            };

            worker.Start();
            Assert.True(workerReady.Wait(30000));
            Assert.False(workerAcquired.Wait(0));

            allocator->leave_spin_lock();
            mainHoldsLock = false;

            Assert.True(workerAcquired.Wait(30000));
            workerCanLeave.Set();
            Assert.True(worker.Join(30000));
            Assert.Equal(GCSpinLock.lock_free, ReadRegionAllocatorField<GCSpinLock>(allocator, "region_allocator_lock").@lock);
        }
        finally
        {
            if (mainHoldsLock)
            {
                allocator->leave_spin_lock();
            }

            workerCanLeave.Set();
            bool workerStopped = worker is null || worker.Join(30000);
            if (workerStopped)
            {
                workerReady.Dispose();
                workerAcquired.Dispose();
                workerCanLeave.Dispose();
                System.Runtime.InteropServices.NativeMemory.Free(allocator);
            }
        }
    }

    [Fact]
    public void RegionAllocatorSpinLockPreservesMutualExclusionUnderConcurrency()
    {
        const int ThreadCount = 4;
        const int IterationsPerThread = 2000;

        region_allocator* allocator = (region_allocator*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)sizeof(region_allocator));
        var start = new ManualResetEventSlim(false);
        Thread[] threads = new Thread[ThreadCount];
        int inCritical = 0;
        int protectedCounter = 0;
        int acquisitions = 0;
        int violations = 0;

        try
        {
            allocator->initialize();
            nuint allocatorAddress = (nuint)allocator;

            for (int threadIndex = 0; threadIndex < ThreadCount; threadIndex++)
            {
                threads[threadIndex] = new Thread(() =>
                {
                    region_allocator* workerAllocator = (region_allocator*)allocatorAddress;
                    start.Wait();

                    for (int iteration = 0; iteration < IterationsPerThread; iteration++)
                    {
                        workerAllocator->enter_spin_lock();

                        if (SysInterlocked.Increment(ref inCritical) != 1)
                        {
                            SysVolatile.Write(ref violations, 1);
                        }

                        int value = protectedCounter;
                        GCEnv.YieldProcessor();
                        protectedCounter = value + 1;

                        if (SysInterlocked.Decrement(ref inCritical) != 0)
                        {
                            SysVolatile.Write(ref violations, 1);
                        }

                        workerAllocator->leave_spin_lock();
                        SysInterlocked.Increment(ref acquisitions);
                    }
                })
                {
                    IsBackground = true,
                };
                threads[threadIndex].Start();
            }

            start.Set();

            foreach (Thread thread in threads)
            {
                Assert.True(thread.Join(30000));
            }

            Assert.Equal(0, SysVolatile.Read(ref violations));
            Assert.Equal(ThreadCount * IterationsPerThread, protectedCounter);
            Assert.Equal(ThreadCount * IterationsPerThread, acquisitions);
            Assert.Equal(GCSpinLock.lock_free, ReadRegionAllocatorField<GCSpinLock>(allocator, "region_allocator_lock").@lock);
        }
        finally
        {
            start.Set();
            bool allThreadsStopped = true;
            foreach (Thread? thread in threads)
            {
                allThreadsStopped &= thread is null || thread.Join(30000);
            }

            start.Dispose();
            if (allThreadsStopped)
            {
                System.Runtime.InteropServices.NativeMemory.Free(allocator);
            }
        }
    }

#if DEBUG
    [Fact]
    public void RegionAllocatorSpinLockRecordsCurrentThreadAndRestoresSentinelInDebug()
    {
        GCToEEInterface.Reset();
        GCToEEInterface.CurrentThread = (void*)0x12345678;
        region_allocator allocator = default;
        allocator.initialize();

        allocator.enter_spin_lock();

        GCSpinLock held = ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock");
        Assert.Equal(0, held.@lock);
        Assert.Equal((nuint)0x12345678, (nuint)held.holding_thread);
        Assert.Equal(1, GCToEEInterface.GetThreadCallCount);

        allocator.leave_spin_lock();

        GCSpinLock released = ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock");
        Assert.Equal(GCSpinLock.lock_free, released.@lock);
        Assert.Equal(nuint.MaxValue, (nuint)released.holding_thread);
    }
#endif

    [Fact]
    public void RegionAllocatorInitAlignsRangeAllocatesZeroedMapAndPreservesSpinLock()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        byte* lowest = (byte*)0x1111;
        byte* highest = (byte*)0x2222;
        WriteRegionAllocatorField(&allocator, "region_allocator_lock", new GCSpinLock { @lock = 1234 });

        Assert.True(allocator.init((byte*)0x1003, (byte*)0xAFFF, 0x1000, &lowest, &highest));

        uint* map = (uint*)ReadRegionAllocatorPointerField(&allocator, "region_map_left_start");
        try
        {
            Assert.Equal((nuint)0x2000, (nuint)lowest);
            Assert.Equal((nuint)0xA000, (nuint)highest);
            Assert.Equal((nuint)0x2000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_start"));
            Assert.Equal((nuint)0xA000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_end"));
            Assert.Equal((nuint)0x2000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)0xA000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
            Assert.Equal(8u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            Assert.Equal((nuint)0x1000, ReadRegionAllocatorField<nuint>(&allocator, "region_alignment"));
            Assert.Equal((nuint)0x8000, ReadRegionAllocatorField<nuint>(&allocator, "large_region_alignment"));
            Assert.Equal(1234, ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock").@lock);
            Assert.Equal((nuint)map, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
            Assert.Equal((nuint)(map + 8), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            Assert.Equal((nuint)(map + 8), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_end"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
            Assert.Equal(1, SyncImports.AllocCount);
            Assert.Equal((nuint)(8 * sizeof(uint)), SyncImports.LastAllocSize);

            for (int i = 0; i < 8; i++)
            {
                Assert.Equal(0u, map[i]);
            }

            Assert.Equal((nuint)0x5000, (nuint)allocator.region_address_of(map + 3));
            Assert.Equal((nuint)(map + 3), (nuint)allocator.region_map_index_of((byte*)0x5123));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorInitFailureDoesNotWriteOutputsOrMapPointers()
    {
        SyncImports.ResetRecording();
        SyncImports.FailNextAlloc = true;
        region_allocator allocator = default;
        byte* lowest = (byte*)0x1111;
        byte* highest = (byte*)0x2222;
        uint* oldMapLeftStart = (uint*)0x3333;
        uint* oldMapLeftEnd = (uint*)0x4444;
        uint* oldMapRightStart = (uint*)0x5555;
        uint* oldMapRightEnd = (uint*)0x6666;
        WriteRegionAllocatorField(&allocator, "region_allocator_lock", new GCSpinLock { @lock = 5678 });
        WriteRegionAllocatorPointerField(&allocator, "region_map_left_start", oldMapLeftStart);
        WriteRegionAllocatorPointerField(&allocator, "region_map_left_end", oldMapLeftEnd);
        WriteRegionAllocatorPointerField(&allocator, "region_map_right_start", oldMapRightStart);
        WriteRegionAllocatorPointerField(&allocator, "region_map_right_end", oldMapRightEnd);

        Assert.False(allocator.init((byte*)0x1003, (byte*)0xAFFF, 0x1000, &lowest, &highest));

        Assert.Equal((nuint)0x1111, (nuint)lowest);
        Assert.Equal((nuint)0x2222, (nuint)highest);
        Assert.Equal((nuint)oldMapLeftStart, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_start"));
        Assert.Equal((nuint)oldMapLeftEnd, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
        Assert.Equal((nuint)oldMapRightStart, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
        Assert.Equal((nuint)oldMapRightEnd, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_end"));
        Assert.Equal(5678, ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock").@lock);
        Assert.Equal((nuint)0x2000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_start"));
        Assert.Equal((nuint)0xA000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_end"));
        Assert.Equal(8u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
        Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
        Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
        Assert.Equal(1, SyncImports.AllocCount);
        Assert.Equal((nuint)(8 * sizeof(uint)), SyncImports.LastAllocSize);
    }

    [Fact]
    public void RegionAllocatorInitMapByteOverflowFailsBeforeAllocation()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        byte* lowest = (byte*)0x1111;
        byte* highest = (byte*)0x2222;
        uint* oldMapLeftStart = (uint*)0x3333;
        WriteRegionAllocatorPointerField(&allocator, "region_map_left_start", oldMapLeftStart);

        Assert.False(allocator.init((byte*)0, (byte*)nuint.MaxValue, 1, &lowest, &highest));

        Assert.Equal((nuint)0x1111, (nuint)lowest);
        Assert.Equal((nuint)0x2222, (nuint)highest);
        Assert.Equal((nuint)oldMapLeftStart, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_start"));
        Assert.Equal(uint.MaxValue, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
        Assert.Equal(0, SyncImports.AllocCount);
        Assert.Equal((nuint)0, SyncImports.LastAllocSize);
    }

    [Fact]
    public void RegionAllocatorInitReinitializationReplacesReservationState()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        allocator.initialize();
        uint* firstMap = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);
        uint* secondMap = null;

        try
        {
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(2, allocate_direction.allocate_forward));

            byte* lowest = null;
            byte* highest = null;
            Assert.True(allocator.init((byte*)0x2003, (byte*)0xEFFF, 0x1000, &lowest, &highest));
            secondMap = (uint*)ReadRegionAllocatorPointerField(&allocator, "region_map_left_start");

            Assert.Equal((nuint)0x3000, (nuint)lowest);
            Assert.Equal((nuint)0xE000, (nuint)highest);
            Assert.NotEqual((nuint)firstMap, (nuint)secondMap);
            Assert.Equal((nuint)0x3000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_start"));
            Assert.Equal((nuint)0xE000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_end"));
            Assert.Equal((nuint)0x3000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)0xE000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
            Assert.Equal(11u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            Assert.Equal((nuint)secondMap, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
            Assert.Equal((nuint)(secondMap + 11), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            Assert.Equal((nuint)(secondMap + 11), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_end"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
            Assert.Equal(GCSpinLock.lock_free, ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock").@lock);

            for (int i = 0; i < 11; i++)
            {
                Assert.Equal(0u, secondMap[i]);
            }
        }
        finally
        {
            SyncImports.ManagedGC_Free(firstMap);
            if (secondMap is not null)
            {
                SyncImports.ManagedGC_Free(secondMap);
            }
        }
    }

    [Fact]
    public void InitialRegionReservationPreservesNativeLayoutAndAllocatorBoundaries()
    {
        const nuint RegionSize = 0x1000;
        region_allocator oldAllocator = gc_heap.global_region_allocator;
        byte** oldInitialRegions = gc_heap.initial_regions;
        byte* oldBookkeepingCoverage = gc_heap.bookkeeping_covered_committed;
        uint* map = null;
        byte** initialRegions = null;

        try
        {
            SyncImports.ResetRecording();
            region_allocator allocator = default;
            allocator.initialize();
            map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x29000, RegionSize);
            gc_heap.global_region_allocator = allocator;
            gc_heap.initial_regions = null;
            gc_heap.bookkeeping_covered_committed = (byte*)0x7654_0000;

            Assert.True(gc_heap.allocate_initial_regions(1));
            initialRegions = gc_heap.initial_regions;

            Assert.Equal(2, SyncImports.AllocCount);
            Assert.Equal((nuint)(2 * (int)gc_generation_num.total_generation_count * sizeof(byte*)), SyncImports.LastAllocSize);
            AssertInitialRegion((int)gc_generation_num.poh_generation, (byte*)0x1000, (byte*)0x9000);
            AssertInitialRegion((int)gc_generation_num.soh_gen2, (byte*)0x9000, (byte*)0xA000);
            AssertInitialRegion((int)gc_generation_num.soh_gen1, (byte*)0xA000, (byte*)0xB000);
            AssertInitialRegion((int)gc_generation_num.soh_gen0, (byte*)0xB000, (byte*)0xC000);
            AssertInitialRegion((int)gc_generation_num.loh_generation, (byte*)0xC000, (byte*)0x14000);
            Assert.Equal((nuint)0x14000, (nuint)gc_heap.global_region_allocator.get_left_used_unsafe());
            region_allocator current = gc_heap.global_region_allocator;
            Assert.Equal((nuint)0x29000, (nuint)ReadRegionAllocatorPointerField(&current, "global_region_right_used"));
            Assert.Equal((nuint)0x7654_0000, (nuint)gc_heap.bookkeeping_covered_committed);
            Assert.Equal(8u, map[0]);
            Assert.Equal(8u, map[7]);
            Assert.Equal(1u, map[8]);
            Assert.Equal(1u, map[9]);
            Assert.Equal(1u, map[10]);
            Assert.Equal(8u, map[11]);
            Assert.Equal(8u, map[18]);

            byte* forwardStart = null;
            byte* forwardEnd = null;
            Assert.True(gc_heap.global_region_allocator.allocate_region(
                (int)gc_generation_num.soh_gen0,
                RegionSize,
                &forwardStart,
                &forwardEnd,
                allocate_direction.allocate_forward,
                null));
            Assert.Equal((nuint)0x14000, (nuint)forwardStart);
            Assert.Equal((nuint)0x15000, (nuint)forwardEnd);

            byte* backwardStart = null;
            byte* backwardEnd = null;
            Assert.True(gc_heap.global_region_allocator.allocate_region(
                (int)gc_generation_num.soh_gen0,
                RegionSize,
                &backwardStart,
                &backwardEnd,
                allocate_direction.allocate_backward,
                null));
            Assert.Equal((nuint)0x28000, (nuint)backwardStart);
            Assert.Equal((nuint)0x29000, (nuint)backwardEnd);
            Assert.Equal((nuint)0x15000, (nuint)gc_heap.global_region_allocator.get_left_used_unsafe());
            current = gc_heap.global_region_allocator;
            Assert.Equal((nuint)0x28000, (nuint)ReadRegionAllocatorPointerField(&current, "global_region_right_used"));
            Assert.Equal(1u, map[19]);
            Assert.Equal(1u, map[39]);
            Assert.Equal((nuint)19 * RegionSize, gc_heap.global_region_allocator.get_free());
        }
        finally
        {
            if (initialRegions is not null)
            {
                SyncImports.ManagedGC_Free(initialRegions);
            }

            gc_heap.initial_regions = oldInitialRegions;
            gc_heap.global_region_allocator = oldAllocator;
            gc_heap.bookkeeping_covered_committed = oldBookkeepingCoverage;
            if (map is not null)
            {
                SyncImports.ManagedGC_Free(map);
            }
        }
    }

    [Fact]
    public void InitialRegionReservationFailureDoesNotAllocateOrMutateAllocatorState()
    {
        region_allocator oldAllocator = gc_heap.global_region_allocator;
        byte** oldInitialRegions = gc_heap.initial_regions;
        byte* oldBookkeepingCoverage = gc_heap.bookkeeping_covered_committed;
        uint* map = null;

        try
        {
            SyncImports.ResetRecording();
            region_allocator allocator = default;
            allocator.initialize();
            map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x29000, 0x1000);
            gc_heap.global_region_allocator = allocator;
            gc_heap.initial_regions = (byte**)0x1234;
            gc_heap.bookkeeping_covered_committed = (byte*)0x7654_0000;
            region_allocator current = gc_heap.global_region_allocator;
            RegionAllocatorSnapshot expected = CaptureRegionAllocatorSnapshot(&current);

            SyncImports.FailNextAlloc = true;
            Assert.False(gc_heap.allocate_initial_regions(1));

            current = gc_heap.global_region_allocator;
            AssertRegionAllocatorSnapshotEqual(expected, &current);
            Assert.Equal((nuint)0, (nuint)gc_heap.initial_regions);
            Assert.Equal((nuint)0x7654_0000, (nuint)gc_heap.bookkeeping_covered_committed);
            Assert.Equal(2, SyncImports.AllocCount);
            Assert.Equal((nuint)(2 * (int)gc_generation_num.total_generation_count * sizeof(byte*)), SyncImports.LastAllocSize);
        }
        finally
        {
            gc_heap.initial_regions = oldInitialRegions;
            gc_heap.global_region_allocator = oldAllocator;
            gc_heap.bookkeeping_covered_committed = oldBookkeepingCoverage;
            if (map is not null)
            {
                SyncImports.ManagedGC_Free(map);
            }
        }
    }

    private static void AssertInitialRegion(int gen, byte* expectedStart, byte* expectedEnd)
    {
        byte* start = null;
        byte* end = null;
        gc_heap.get_initial_region(gen, 0, &start, &end);

        Assert.Equal((nuint)expectedStart, (nuint)start);
        Assert.Equal((nuint)expectedEnd, (nuint)end);
    }

    [Fact]
    public void RegionAllocatorAlignmentHelpersMatchNativeBitMath()
    {
        gc_heap.global_region_allocator.initialize_alignment(0x1000);

        Assert.Equal((nuint)0x1000, gc_heap.global_region_allocator.align_region_up(0x1));
        Assert.Equal((nuint)0x2000, gc_heap.global_region_allocator.align_region_up(0x1001));
        Assert.Equal((nuint)0x2000, gc_heap.global_region_allocator.align_region_up(0x2000));
        Assert.Equal((nuint)0, gc_heap.global_region_allocator.align_region_up(nuint.MaxValue));
        Assert.Equal((nuint)0x0000, gc_heap.global_region_allocator.align_region_down(0x001));
        Assert.Equal((nuint)0x1000, gc_heap.global_region_allocator.align_region_down(0x1ABC));
        Assert.Equal((nuint)0x2000, gc_heap.global_region_allocator.align_region_down(0x2000));
        Assert.Equal((nuint)1, gc_heap.global_region_allocator.is_region_aligned((byte*)0x3000));
        Assert.Equal((nuint)0, gc_heap.global_region_allocator.is_region_aligned((byte*)0x3001));
    }

    [Theory]
    [InlineData(0x80000001u, true, 1u)]
    [InlineData(0x00000001u, false, 1u)]
    [InlineData(0x80000000u, true, 0u)]
    [InlineData(0x7fffffffu, false, 0x7fffffffu)]
    public void RegionAllocatorUnitDecodePreservesFreeBitEncoding(uint encoded, bool expectedFree, uint expectedUnits)
    {
        Assert.Equal(expectedFree, region_allocator.is_unit_memory_free(encoded));
        Assert.Equal(expectedUnits, region_allocator.get_num_units(encoded));
    }

    [Fact]
    public void RegionAllocatorBusyAndFreeBlocksEncodeEndpoints()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            allocator.make_busy_block(map + 1, 3);

            Assert.Equal(0u, map[0]);
            Assert.Equal(3u, map[1]);
            Assert.Equal(0u, map[2]);
            Assert.Equal(3u, map[3]);

            allocator.make_free_block(map + 4, 2);
            uint encodedFreeBlock = unchecked((uint)region_allocator.region_alloc_free_bit) | 2u;

            Assert.Equal(encodedFreeBlock, map[4]);
            Assert.Equal(encodedFreeBlock, map[5]);
            Assert.True(region_allocator.is_unit_memory_free(map[4]));
            Assert.Equal(2u, region_allocator.get_num_units(map[4]));
            Assert.Equal(0u, map[6]);

            allocator.make_busy_block(map + 7, 1);
            Assert.Equal(1u, map[7]);

            allocator.make_free_block(map, 1);
            Assert.Equal(unchecked((uint)region_allocator.region_alloc_free_bit) | 1u, map[0]);
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateEndForwardMarksBusyBlockAndAdvancesLeftEnd()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            byte* allocation = allocator.allocate_end(2, allocate_direction.allocate_forward);

            Assert.Equal((nuint)0x1000, (nuint)allocation);
            Assert.Equal(2u, map[0]);
            Assert.Equal(2u, map[1]);
            Assert.Equal((nuint)0x3000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)0x9000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
            Assert.Equal((nuint)(map + 2), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
            Assert.Equal((nuint)(map + 8), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            Assert.Equal(8u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateEndBackwardMarksBusyBlockAndRetreatsRightEnd()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            byte* allocation = allocator.allocate_end(2, allocate_direction.allocate_backward);

            Assert.Equal((nuint)0x7000, (nuint)allocation);
            Assert.Equal(2u, map[6]);
            Assert.Equal(2u, map[7]);
            Assert.Equal((nuint)0x1000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)0x7000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
            Assert.Equal((nuint)map, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
            Assert.Equal((nuint)(map + 6), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            Assert.Equal((nuint)(map + 8), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_end"));
            Assert.Equal(8u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateEndInsufficientSpaceFailsWithoutMutation()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x5000, 0x1000);

        try
        {
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(3, allocate_direction.allocate_forward));
            RegionAllocatorSnapshot before = CaptureRegionAllocatorSnapshot(&allocator);
            uint* mapBefore = stackalloc uint[4];
            for (int i = 0; i < 4; i++)
            {
                mapBefore[i] = map[i];
            }

            byte* allocation = allocator.allocate_end(2, allocate_direction.allocate_backward);

            Assert.Equal((nuint)0, (nuint)allocation);
            AssertRegionAllocatorSnapshotEqual(before, &allocator);
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(mapBefore[i], map[i]);
            }
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Theory]
    [InlineData((int)allocate_direction.allocate_forward)]
    [InlineData((int)allocate_direction.allocate_backward)]
    public void RegionAllocatorAllocateEndExactFitConsumesBoundaryAndStops(int directionValue)
    {
        SyncImports.ResetRecording();
        allocate_direction direction = (allocate_direction)directionValue;
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x10000, 0x14000, 0x1000);

        try
        {
            byte* allocation = allocator.allocate_end(4, direction);

            Assert.Equal((nuint)0x10000, (nuint)allocation);
            Assert.Equal(4u, map[0]);
            Assert.Equal(4u, map[3]);

            if (direction == allocate_direction.allocate_forward)
            {
                Assert.Equal((nuint)0x14000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
                Assert.Equal((nuint)0x14000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
                Assert.Equal((nuint)(map + 4), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
                Assert.Equal((nuint)(map + 4), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            }
            else
            {
                Assert.Equal((nuint)0x10000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
                Assert.Equal((nuint)0x10000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
                Assert.Equal((nuint)map, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
                Assert.Equal((nuint)map, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            }

            RegionAllocatorSnapshot exactFit = CaptureRegionAllocatorSnapshot(&allocator);
            Assert.Equal((nuint)0, (nuint)allocator.allocate_end(1, direction));
            AssertRegionAllocatorSnapshotEqual(exactFit, &allocator);
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorBlockAndEndAllocationPreserveFreeUnitCounters()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            WriteRegionAllocatorField(&allocator, "total_free_units", 123u);
            WriteRegionAllocatorField(&allocator, "num_left_used_free_units", 45u);
            WriteRegionAllocatorField(&allocator, "num_right_used_free_units", 67u);

            allocator.make_free_block(map + 2, 2);
            allocator.make_busy_block(map + 4, 2);
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));

            Assert.Equal(123u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            Assert.Equal(45u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(67u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Theory]
    [InlineData((int)allocate_direction.allocate_forward)]
    [InlineData((int)allocate_direction.allocate_backward)]
    public void RegionAllocatorAllocateReusesExactFreeBlockInDirection(int directionValue)
    {
        SyncImports.ResetRecording();
        allocate_direction direction = (allocate_direction)directionValue;
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0xB000, 0x1000);

        try
        {
            allocator.initialize();
            if (direction == allocate_direction.allocate_forward)
            {
                Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(2, direction));
                Assert.Equal((nuint)0x3000, (nuint)allocator.allocate_end(2, direction));
                Assert.Equal((nuint)0x5000, (nuint)allocator.allocate_end(1, direction));
                WriteRegionAllocatorField(&allocator, "total_free_units", 5u);

                allocator.delete_region((byte*)0x3000);

                Assert.Equal((nuint)0x3000, (nuint)allocator.allocate(2, direction, null));
                Assert.Equal(2u, map[2]);
                Assert.Equal(2u, map[3]);
                Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
                Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
                Assert.Equal(5u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
                Assert.Equal((nuint)0x6000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
                Assert.Equal((nuint)(map + 5), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
            }
            else
            {
                Assert.Equal((nuint)0x9000, (nuint)allocator.allocate_end(2, direction));
                Assert.Equal((nuint)0x7000, (nuint)allocator.allocate_end(2, direction));
                Assert.Equal((nuint)0x6000, (nuint)allocator.allocate_end(1, direction));
                WriteRegionAllocatorField(&allocator, "total_free_units", 5u);

                allocator.delete_region((byte*)0x7000);

                Assert.Equal((nuint)0x7000, (nuint)allocator.allocate(2, direction, null));
                Assert.Equal(2u, map[6]);
                Assert.Equal(2u, map[7]);
                Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
                Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
                Assert.Equal(5u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
                Assert.Equal((nuint)0x6000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
                Assert.Equal((nuint)(map + 5), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            }

            Assert.Equal(GCSpinLock.lock_free, ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock").@lock);
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Theory]
    [InlineData((int)allocate_direction.allocate_forward)]
    [InlineData((int)allocate_direction.allocate_backward)]
    public void RegionAllocatorAllocateSplitsOversizedFreeBlockInDirection(int directionValue)
    {
        SyncImports.ResetRecording();
        allocate_direction direction = (allocate_direction)directionValue;
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0xC000, 0x1000);

        try
        {
            allocator.initialize();
            if (direction == allocate_direction.allocate_forward)
            {
                Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(1, direction));
                Assert.Equal((nuint)0x2000, (nuint)allocator.allocate_end(3, direction));
                Assert.Equal((nuint)0x5000, (nuint)allocator.allocate_end(1, direction));
                WriteRegionAllocatorField(&allocator, "total_free_units", 6u);

                allocator.delete_region((byte*)0x2000);

                Assert.Equal((nuint)0x2000, (nuint)allocator.allocate(2, direction, null));
                Assert.Equal(1u, map[0]);
                Assert.Equal(2u, map[1]);
                Assert.Equal(2u, map[2]);
                Assert.Equal(EncodedFreeRegionBlock(1), map[3]);
                Assert.Equal(1u, map[4]);
                Assert.Equal(1u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
                Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
                Assert.Equal(7u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            }
            else
            {
                Assert.Equal((nuint)0xB000, (nuint)allocator.allocate_end(1, direction));
                Assert.Equal((nuint)0x8000, (nuint)allocator.allocate_end(3, direction));
                Assert.Equal((nuint)0x7000, (nuint)allocator.allocate_end(1, direction));
                WriteRegionAllocatorField(&allocator, "total_free_units", 6u);

                allocator.delete_region((byte*)0x8000);

                Assert.Equal((nuint)0x9000, (nuint)allocator.allocate(2, direction, null));
                Assert.Equal(1u, map[6]);
                Assert.Equal(EncodedFreeRegionBlock(1), map[7]);
                Assert.Equal(2u, map[8]);
                Assert.Equal(2u, map[9]);
                Assert.Equal(1u, map[10]);
                Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
                Assert.Equal(1u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
                Assert.Equal(7u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            }
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateSkipsBusyBlocksBeforeReusableFreeBlock()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            allocator.initialize();
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x2000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x3000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x4000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            WriteRegionAllocatorField(&allocator, "total_free_units", 4u);

            allocator.delete_region((byte*)0x3000);

            Assert.Equal((nuint)0x3000, (nuint)allocator.allocate(1, allocate_direction.allocate_forward, null));
            Assert.Equal(1u, map[0]);
            Assert.Equal(1u, map[1]);
            Assert.Equal(1u, map[2]);
            Assert.Equal(1u, map[3]);
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(4u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateFastGateFallsBackToEndWhenFreeCounterTooSmall()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            allocator.initialize();
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(2, allocate_direction.allocate_forward));
            allocator.make_free_block(map, 2);
            WriteRegionAllocatorField(&allocator, "total_free_units", 6u);
            WriteRegionAllocatorField(&allocator, "num_left_used_free_units", 0u);

            Assert.Equal((nuint)0x3000, (nuint)allocator.allocate(1, allocate_direction.allocate_forward, null));

            Assert.Equal(EncodedFreeRegionBlock(2), map[0]);
            Assert.Equal(EncodedFreeRegionBlock(2), map[1]);
            Assert.Equal(1u, map[2]);
            Assert.Equal((nuint)0x4000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)(map + 3), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(5u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateNullCallbackAllocatesAtEndWithoutInvocation()
    {
        SyncImports.ResetRecording();
        ResetRegionAllocatorCallbackRecorder();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            allocator.initialize();

            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate(2, allocate_direction.allocate_forward, null));

            Assert.Equal(0, s_regionAllocatorCallbackCount);
            Assert.Equal(2u, map[0]);
            Assert.Equal(2u, map[1]);
            Assert.Equal((nuint)0x3000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)(map + 2), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
            Assert.Equal(6u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateCallbackSuccessReceivesGlobalLeftUsed()
    {
        SyncImports.ResetRecording();
        ResetRegionAllocatorCallbackRecorder();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            allocator.initialize();
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            WriteRegionAllocatorField(&allocator, "total_free_units", 7u);

            Assert.Equal((nuint)0x7000, (nuint)allocator.allocate(2, allocate_direction.allocate_backward, &RegionAllocatorCallbackSuccess));

            Assert.Equal(1, s_regionAllocatorCallbackCount);
            Assert.Equal((nuint)0x2000, s_regionAllocatorCallbackLastLeftUsed);
            Assert.Equal(2u, map[6]);
            Assert.Equal(2u, map[7]);
            Assert.Equal((nuint)0x2000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)0x7000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
            Assert.Equal(5u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateCallbackFailureRollsBackEndAllocation()
    {
        SyncImports.ResetRecording();
        ResetRegionAllocatorCallbackRecorder();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            allocator.initialize();
            RegionAllocatorSnapshot before = CaptureRegionAllocatorSnapshot(&allocator);

            Assert.Equal((nuint)0, (nuint)allocator.allocate(2, allocate_direction.allocate_forward, &RegionAllocatorCallbackFailure));

            Assert.Equal(1, s_regionAllocatorCallbackCount);
            Assert.Equal((nuint)0x3000, s_regionAllocatorCallbackLastLeftUsed);
            AssertRegionAllocatorSnapshotEqual(before, &allocator);
            Assert.Equal(2u, map[0]);
            Assert.Equal(2u, map[1]);
            Assert.Equal(GCSpinLock.lock_free, ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock").@lock);
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateBasicRegionUsesOneBasicUnitAndFiresSegmentEvent()
    {
        SyncImports.ResetRecording();
        ResetCreateSegmentEventRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            allocator.initialize();
            byte* start = (byte*)0x1111;
            byte* end = (byte*)0x2222;

            Assert.True(allocator.allocate_basic_region(
                (int)gc_generation_num.soh_gen2,
                &start,
                &end,
                &RegionAllocatorCallbackSuccess));

            Assert.Equal((nuint)0x1000, (nuint)start);
            Assert.Equal((nuint)0x2000, (nuint)end);
            Assert.Equal(1u, map[0]);
            Assert.Equal((nuint)0x2000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal(1, s_regionAllocatorCallbackCount);
            Assert.Equal((nuint)0x2000, s_regionAllocatorCallbackLastLeftUsed);
            AssertCreateSegmentEvent(
                (byte*)((nuint)0x1000 + (nuint)sizeof(aligned_plug_and_gap)),
                (nuint)0x1000 - (nuint)sizeof(aligned_plug_and_gap),
                gc_etw_segment_type.gc_etw_segment_small_object_heap);
        }
        finally
        {
            DisableCreateSegmentEvents();
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateLargeRegionUsesDefaultLargeSizeAndBackwardDirection()
    {
        SyncImports.ResetRecording();
        ResetCreateSegmentEventRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x19000, 0x1000);

        try
        {
            allocator.initialize();
            byte* start = null;
            byte* end = null;

            Assert.True(allocator.allocate_large_region(
                (int)gc_generation_num.loh_generation,
                &start,
                &end,
                allocate_direction.allocate_backward,
                0,
                null));

            Assert.Equal((nuint)0x11000, (nuint)start);
            Assert.Equal((nuint)0x19000, (nuint)end);
            Assert.Equal(8u, map[16]);
            Assert.Equal(8u, map[23]);
            Assert.Equal((nuint)0x11000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
            Assert.Equal((nuint)(map + 16), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            Assert.Equal(16u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            AssertCreateSegmentEvent(
                (byte*)((nuint)0x11000 + (nuint)sizeof(aligned_plug_and_gap)),
                (nuint)0x8000 - (nuint)sizeof(aligned_plug_and_gap),
                gc_etw_segment_type.gc_etw_segment_large_object_heap);
        }
        finally
        {
            DisableCreateSegmentEvents();
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateLargeRegionRoundsCustomSizeToLargeAlignment()
    {
        SyncImports.ResetRecording();
        ResetCreateSegmentEventRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x21000, 0x1000);

        try
        {
            allocator.initialize();
            byte* start = null;
            byte* end = null;

            Assert.True(allocator.allocate_large_region(
                (int)gc_generation_num.soh_gen0,
                &start,
                &end,
                allocate_direction.allocate_forward,
                0x9000,
                null));

            Assert.Equal((nuint)0x1000, (nuint)start);
            Assert.Equal((nuint)0x11000, (nuint)end);
            Assert.Equal(16u, map[0]);
            Assert.Equal(16u, map[15]);
            Assert.Equal((nuint)0x11000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)(map + 16), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
            Assert.Equal(16u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            AssertCreateSegmentEvent(
                (byte*)((nuint)0x1000 + (nuint)sizeof(aligned_plug_and_gap)),
                (nuint)0x10000 - (nuint)sizeof(aligned_plug_and_gap),
                gc_etw_segment_type.gc_etw_segment_small_object_heap);
        }
        finally
        {
            DisableCreateSegmentEvents();
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateRegionAlignsAllocationSizeButFiresRequestedSize()
    {
        SyncImports.ResetRecording();
        ResetCreateSegmentEventRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            allocator.initialize();
            byte* start = null;
            byte* end = null;

            Assert.True(allocator.allocate_region(
                (int)gc_generation_num.soh_gen1,
                0x1801,
                &start,
                &end,
                allocate_direction.allocate_forward,
                null));

            Assert.Equal((nuint)0x1000, (nuint)start);
            Assert.Equal((nuint)0x3000, (nuint)end);
            Assert.Equal(2u, map[0]);
            Assert.Equal(2u, map[1]);
            Assert.Equal(6u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            AssertCreateSegmentEvent(
                (byte*)((nuint)0x1000 + (nuint)sizeof(aligned_plug_and_gap)),
                (nuint)0x1801 - (nuint)sizeof(aligned_plug_and_gap),
                gc_etw_segment_type.gc_etw_segment_small_object_heap);
        }
        finally
        {
            DisableCreateSegmentEvents();
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Theory]
    [InlineData((int)gc_generation_num.soh_gen0, (int)gc_etw_segment_type.gc_etw_segment_small_object_heap)]
    [InlineData((int)gc_generation_num.loh_generation, (int)gc_etw_segment_type.gc_etw_segment_large_object_heap)]
    [InlineData((int)gc_generation_num.poh_generation, (int)gc_etw_segment_type.gc_etw_segment_pinned_object_heap)]
    public void RegionAllocatorAllocateRegionClassifiesGenerationSegmentTypes(int generation, int expectedSegmentType)
    {
        SyncImports.ResetRecording();
        ResetCreateSegmentEventRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x5000, 0x1000);

        try
        {
            allocator.initialize();
            byte* start = null;
            byte* end = null;

            Assert.True(allocator.allocate_region(generation, 0x1000, &start, &end, allocate_direction.allocate_forward, null));

            Assert.Equal((nuint)0x1000, (nuint)start);
            Assert.Equal((nuint)0x2000, (nuint)end);
            Assert.Equal((uint)expectedSegmentType, GCToEEInterface.LastGCCreateSegmentType);
        }
        finally
        {
            DisableCreateSegmentEvents();
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateRegionCallbackFailureWritesOutputsAndFiresFailedAllocationEvent()
    {
        SyncImports.ResetRecording();
        ResetCreateSegmentEventRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            allocator.initialize();
            RegionAllocatorSnapshot before = CaptureRegionAllocatorSnapshot(&allocator);
            byte* start = (byte*)0x1111;
            byte* end = (byte*)0x2222;

            Assert.False(allocator.allocate_region(
                (int)gc_generation_num.poh_generation,
                0x1000,
                &start,
                &end,
                allocate_direction.allocate_forward,
                &RegionAllocatorCallbackFailure));

            Assert.Equal((nuint)0, (nuint)start);
            Assert.Equal((nuint)0x1000, (nuint)end);
            Assert.Equal(1, s_regionAllocatorCallbackCount);
            Assert.Equal((nuint)0x2000, s_regionAllocatorCallbackLastLeftUsed);
            AssertRegionAllocatorSnapshotEqual(before, &allocator);
            AssertCreateSegmentEvent(
                (byte*)(nuint)sizeof(aligned_plug_and_gap),
                (nuint)0x1000 - (nuint)sizeof(aligned_plug_and_gap),
                gc_etw_segment_type.gc_etw_segment_pinned_object_heap);
        }
        finally
        {
            DisableCreateSegmentEvents();
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorAllocateRegionNoSpaceFailureStillWritesEndAndFiresEvent()
    {
        SyncImports.ResetRecording();
        ResetCreateSegmentEventRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x2000, 0x1000);

        try
        {
            allocator.initialize();
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            WriteRegionAllocatorField(&allocator, "total_free_units", 0u);
            RegionAllocatorSnapshot before = CaptureRegionAllocatorSnapshot(&allocator);
            byte* start = (byte*)0x1111;
            byte* end = (byte*)0x2222;

            Assert.False(allocator.allocate_region(
                (int)gc_generation_num.loh_generation,
                0x1000,
                &start,
                &end,
                allocate_direction.allocate_forward,
                &RegionAllocatorCallbackSuccess));

            Assert.Equal((nuint)0, (nuint)start);
            Assert.Equal((nuint)0x1000, (nuint)end);
            Assert.Equal(0, s_regionAllocatorCallbackCount);
            AssertRegionAllocatorSnapshotEqual(before, &allocator);
            AssertCreateSegmentEvent(
                (byte*)(nuint)sizeof(aligned_plug_and_gap),
                (nuint)0x1000 - (nuint)sizeof(aligned_plug_and_gap),
                gc_etw_segment_type.gc_etw_segment_large_object_heap);
        }
        finally
        {
            DisableCreateSegmentEvents();
            SyncImports.ManagedGC_Free(map);
        }
    }

#if !DEBUG
    [Fact]
    public void RegionAllocatorAllocateInvalidDirectionFallsBackToBackwardEndInRelease()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0x9000, 0x1000);

        try
        {
            allocator.initialize();

            Assert.Equal((nuint)0x7000, (nuint)allocator.allocate(2, (allocate_direction)1234, null));

            Assert.Equal(2u, map[6]);
            Assert.Equal(2u, map[7]);
            Assert.Equal((nuint)0x7000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
            Assert.Equal((nuint)(map + 6), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            Assert.Equal(6u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }
#endif

    [Fact]
    public void RegionAllocatorDeleteRegionWrapperLocksDeletesInteriorBusyBlockAndReleases()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0xB000, 0x1000);

        try
        {
            allocator.initialize();
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x2000, (nuint)allocator.allocate_end(2, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x4000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            WriteRegionAllocatorField(&allocator, "total_free_units", 6u);

            allocator.delete_region((byte*)0x2000);

            Assert.Equal(1u, map[0]);
            Assert.Equal(EncodedFreeRegionBlock(2), map[1]);
            Assert.Equal(EncodedFreeRegionBlock(2), map[2]);
            Assert.Equal(1u, map[3]);
            Assert.Equal((nuint)0x5000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)(map + 4), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
            Assert.Equal(2u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
            Assert.Equal(8u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            Assert.Equal(GCSpinLock.lock_free, ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock").@lock);
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorDeleteRegionCoalescesPreviousFreeBlock()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0xB000, 0x1000);

        try
        {
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x2000, (nuint)allocator.allocate_end(2, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x4000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x5000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            allocator.make_free_block(map + 1, 2);
            WriteRegionAllocatorField(&allocator, "num_left_used_free_units", 2u);
            WriteRegionAllocatorField(&allocator, "total_free_units", 7u);

            DeleteRegionImplUnderLock(&allocator, (byte*)0x4000);

            Assert.Equal(1u, map[0]);
            Assert.Equal(EncodedFreeRegionBlock(3), map[1]);
            Assert.Equal(EncodedFreeRegionBlock(2), map[2]);
            Assert.Equal(EncodedFreeRegionBlock(3), map[3]);
            Assert.Equal(1u, map[4]);
            Assert.Equal(3u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
            Assert.Equal(8u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            Assert.Equal((nuint)0x6000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)(map + 5), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorDeleteRegionCoalescesNextFreeBlock()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0xB000, 0x1000);

        try
        {
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x2000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x3000, (nuint)allocator.allocate_end(2, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x5000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            allocator.make_free_block(map + 2, 2);
            WriteRegionAllocatorField(&allocator, "num_left_used_free_units", 2u);
            WriteRegionAllocatorField(&allocator, "total_free_units", 7u);

            DeleteRegionImplUnderLock(&allocator, (byte*)0x2000);

            Assert.Equal(1u, map[0]);
            Assert.Equal(EncodedFreeRegionBlock(3), map[1]);
            Assert.Equal(EncodedFreeRegionBlock(2), map[2]);
            Assert.Equal(EncodedFreeRegionBlock(3), map[3]);
            Assert.Equal(1u, map[4]);
            Assert.Equal(3u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
            Assert.Equal(8u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            Assert.Equal((nuint)0x6000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)(map + 5), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorDeleteRegionCoalescesBothFreeNeighbors()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0xB000, 0x1000);

        try
        {
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x2000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x3000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x4000, (nuint)allocator.allocate_end(2, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x6000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            allocator.make_free_block(map + 1, 1);
            allocator.make_free_block(map + 3, 2);
            WriteRegionAllocatorField(&allocator, "num_left_used_free_units", 3u);
            WriteRegionAllocatorField(&allocator, "total_free_units", 7u);

            DeleteRegionImplUnderLock(&allocator, (byte*)0x3000);

            Assert.Equal(1u, map[0]);
            Assert.Equal(EncodedFreeRegionBlock(4), map[1]);
            Assert.Equal(1u, map[2]);
            Assert.Equal(EncodedFreeRegionBlock(2), map[3]);
            Assert.Equal(EncodedFreeRegionBlock(4), map[4]);
            Assert.Equal(1u, map[5]);
            Assert.Equal(4u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
            Assert.Equal(8u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            Assert.Equal((nuint)0x7000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)(map + 6), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorDeleteRegionContractsLeftEndAfterCoalescingPrevious()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0xB000, 0x1000);

        try
        {
            Assert.Equal((nuint)0x1000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_forward));
            Assert.Equal((nuint)0x2000, (nuint)allocator.allocate_end(2, allocate_direction.allocate_forward));
            allocator.make_free_block(map, 1);
            WriteRegionAllocatorField(&allocator, "num_left_used_free_units", 1u);
            WriteRegionAllocatorField(&allocator, "total_free_units", 8u);

            DeleteRegionImplUnderLock(&allocator, (byte*)0x2000);

            Assert.Equal(EncodedFreeRegionBlock(1), map[0]);
            Assert.Equal(2u, map[1]);
            Assert.Equal(2u, map[2]);
            Assert.Equal((nuint)0x1000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_left_used"));
            Assert.Equal((nuint)map, (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_left_end"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
            Assert.Equal(10u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorDeleteRegionContractsRightEndAfterCoalescingNext()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0xB000, 0x1000);

        try
        {
            Assert.Equal((nuint)0xA000, (nuint)allocator.allocate_end(1, allocate_direction.allocate_backward));
            Assert.Equal((nuint)0x8000, (nuint)allocator.allocate_end(2, allocate_direction.allocate_backward));
            allocator.make_free_block(map + 9, 1);
            WriteRegionAllocatorField(&allocator, "num_right_used_free_units", 1u);
            WriteRegionAllocatorField(&allocator, "total_free_units", 8u);

            DeleteRegionImplUnderLock(&allocator, (byte*)0x8000);

            Assert.Equal(2u, map[7]);
            Assert.Equal(2u, map[8]);
            Assert.Equal(EncodedFreeRegionBlock(1), map[9]);
            Assert.Equal((nuint)0xB000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
            Assert.Equal((nuint)(map + 10), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
            Assert.Equal(10u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorDeleteRegionRoutesRightSideFreeCounters()
    {
        SyncImports.ResetRecording();
        region_allocator allocator = default;
        uint* map = InitializeRegionAllocatorMap(&allocator, 0x1000, 0xB000, 0x1000);

        try
        {
            Assert.Equal((nuint)0x9000, (nuint)allocator.allocate_end(2, allocate_direction.allocate_backward));
            Assert.Equal((nuint)0x7000, (nuint)allocator.allocate_end(2, allocate_direction.allocate_backward));
            WriteRegionAllocatorField(&allocator, "total_free_units", 6u);

            DeleteRegionImplUnderLock(&allocator, (byte*)0x9000);

            Assert.Equal(2u, map[6]);
            Assert.Equal(2u, map[7]);
            Assert.Equal(EncodedFreeRegionBlock(2), map[8]);
            Assert.Equal(EncodedFreeRegionBlock(2), map[9]);
            Assert.Equal(0u, ReadRegionAllocatorField<uint>(&allocator, "num_left_used_free_units"));
            Assert.Equal(2u, ReadRegionAllocatorField<uint>(&allocator, "num_right_used_free_units"));
            Assert.Equal(8u, ReadRegionAllocatorField<uint>(&allocator, "total_free_units"));
            Assert.Equal((nuint)0x7000, (nuint)ReadRegionAllocatorPointerField(&allocator, "global_region_right_used"));
            Assert.Equal((nuint)(map + 6), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_start"));
            Assert.Equal((nuint)(map + 10), (nuint)ReadRegionAllocatorPointerField(&allocator, "region_map_right_end"));
        }
        finally
        {
            SyncImports.ManagedGC_Free(map);
        }
    }

    [Fact]
    public void RegionAllocatorMoveHighestFreeRegionsTraversesDescendingEndpoints()
    {
        const nuint Alignment = 0x1000;
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        region_allocator oldGlobalAllocator = gc_heap.global_region_allocator;
        seg_mapping* table = stackalloc seg_mapping[5];
        uint* map = stackalloc uint[4];
        region_free_list* source = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_free_list* destination = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_allocator allocator = default;

        try
        {
            InitializeRegionMoveGlobals(table, Alignment);
            InitializeRegionAllocatorForMove(&allocator, map, 3, Alignment, (byte*)0x1000);
            ClearRegionFreeLists(source);
            ClearRegionFreeLists(destination);
            allocator.make_busy_block(map, 1);
            allocator.make_busy_block(map + 1, 1);
            allocator.make_busy_block(map + 2, 1);

            heap_segment* lowest = InitializeMappedRegion(table, 0x1000, 1, Alignment);
            heap_segment* middle = InitializeMappedRegion(table, 0x2000, 1, Alignment);
            heap_segment* highest = InitializeMappedRegion(table, 0x3000, 1, Alignment);
            region_free_list.add_region(lowest, source);
            region_free_list.add_region(middle, source);
            region_free_list.add_region(highest, source);

            allocator.move_highest_free_regions(2, small_region_p: true, destination);

            region_free_list* destinationBasic = &destination[(int)free_region_kind.basic_free_region];
            Assert.Equal((nuint)2, region_free_list.get_num_free_regions(destinationBasic));
            Assert.Equal((nuint)middle, (nuint)destinationBasic->get_first_free_region());
            Assert.Equal((nuint)highest, (nuint)heap_segment.heap_segment_next(middle));
            Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_next(highest));
            Assert.True(region_free_list.is_on_free_list(lowest, source));
            Assert.True(region_free_list.is_on_free_list(middle, destination));
            Assert.True(region_free_list.is_on_free_list(highest, destination));
        }
        finally
        {
            RestoreRegionMoveGlobals(oldShift, oldTable, oldGlobalAllocator);
        }
    }

    [Fact]
    public void RegionAllocatorMoveHighestFreeRegionsFiltersBasicRegions()
    {
        const nuint Alignment = 0x1000;
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        region_allocator oldGlobalAllocator = gc_heap.global_region_allocator;
        seg_mapping* table = stackalloc seg_mapping[12];
        uint* map = stackalloc uint[10];
        region_free_list* source = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_free_list* destination = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_allocator allocator = default;

        try
        {
            InitializeRegionMoveGlobals(table, Alignment);
            InitializeRegionAllocatorForMove(&allocator, map, 10, Alignment, (byte*)0x1000);
            ClearRegionFreeLists(source);
            ClearRegionFreeLists(destination);
            allocator.make_busy_block(map, 1);
            allocator.make_busy_block(map + 1, 8);
            allocator.make_busy_block(map + 9, 1);

            heap_segment* lowBasic = InitializeMappedRegion(table, 0x1000, 1, Alignment);
            heap_segment* large = InitializeMappedRegion(table, 0x2000, 8, Alignment);
            heap_segment* highBasic = InitializeMappedRegion(table, 0xA000, 1, Alignment);
            region_free_list.add_region(lowBasic, source);
            region_free_list.add_region(large, source);
            region_free_list.add_region(highBasic, source);

            allocator.move_highest_free_regions(10, small_region_p: true, destination);

            Assert.Equal((nuint)2, region_free_list.get_num_free_regions(&destination[(int)free_region_kind.basic_free_region]));
            Assert.Equal((nuint)0, region_free_list.get_num_free_regions(&destination[(int)free_region_kind.large_free_region]));
            Assert.True(region_free_list.is_on_free_list(lowBasic, destination));
            Assert.True(region_free_list.is_on_free_list(highBasic, destination));
            Assert.True(region_free_list.is_on_free_list(large, source));
        }
        finally
        {
            RestoreRegionMoveGlobals(oldShift, oldTable, oldGlobalAllocator);
        }
    }

    [Fact]
    public void RegionAllocatorMoveHighestFreeRegionsFiltersLargeRegions()
    {
        const nuint Alignment = 0x1000;
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        region_allocator oldGlobalAllocator = gc_heap.global_region_allocator;
        seg_mapping* table = stackalloc seg_mapping[12];
        uint* map = stackalloc uint[10];
        region_free_list* source = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_free_list* destination = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_allocator allocator = default;

        try
        {
            InitializeRegionMoveGlobals(table, Alignment);
            InitializeRegionAllocatorForMove(&allocator, map, 10, Alignment, (byte*)0x1000);
            ClearRegionFreeLists(source);
            ClearRegionFreeLists(destination);
            allocator.make_busy_block(map, 1);
            allocator.make_busy_block(map + 1, 8);
            allocator.make_busy_block(map + 9, 1);

            heap_segment* lowBasic = InitializeMappedRegion(table, 0x1000, 1, Alignment);
            heap_segment* large = InitializeMappedRegion(table, 0x2000, 8, Alignment);
            heap_segment* highBasic = InitializeMappedRegion(table, 0xA000, 1, Alignment);
            region_free_list.add_region(lowBasic, source);
            region_free_list.add_region(large, source);
            region_free_list.add_region(highBasic, source);

            allocator.move_highest_free_regions(8, small_region_p: false, destination);

            Assert.Equal((nuint)0, region_free_list.get_num_free_regions(&destination[(int)free_region_kind.basic_free_region]));
            Assert.Equal((nuint)1, region_free_list.get_num_free_regions(&destination[(int)free_region_kind.large_free_region]));
            Assert.True(region_free_list.is_on_free_list(lowBasic, source));
            Assert.True(region_free_list.is_on_free_list(highBasic, source));
            Assert.True(region_free_list.is_on_free_list(large, destination));
        }
        finally
        {
            RestoreRegionMoveGlobals(oldShift, oldTable, oldGlobalAllocator);
        }
    }

    [Fact]
    public void RegionAllocatorMoveHighestFreeRegionsSkipsMapFreeAndAllocatedSegments()
    {
        const nuint Alignment = 0x1000;
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        region_allocator oldGlobalAllocator = gc_heap.global_region_allocator;
        seg_mapping* table = stackalloc seg_mapping[6];
        uint* map = stackalloc uint[4];
        region_free_list* source = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_free_list* destination = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_allocator allocator = default;

        try
        {
            InitializeRegionMoveGlobals(table, Alignment);
            InitializeRegionAllocatorForMove(&allocator, map, 4, Alignment, (byte*)0x1000);
            ClearRegionFreeLists(source);
            ClearRegionFreeLists(destination);
            allocator.make_busy_block(map, 1);
            allocator.make_free_block(map + 1, 1);
            allocator.make_busy_block(map + 2, 1);
            allocator.make_busy_block(map + 3, 1);

            heap_segment* lowBusy = InitializeMappedRegion(table, 0x1000, 1, Alignment);
            heap_segment* mapFree = InitializeMappedRegion(table, 0x2000, 1, Alignment);
            heap_segment* allocated = InitializeMappedRegion(table, 0x3000, 1, Alignment);
            heap_segment* highBusy = InitializeMappedRegion(table, 0x4000, 1, Alignment);
            heap_segment.heap_segment_allocated(allocated) = (byte*)0x3333;
            region_free_list.add_region(lowBusy, source);
            region_free_list.add_region(mapFree, source);
            region_free_list.add_region(highBusy, source);

            allocator.move_highest_free_regions(4, small_region_p: true, destination);

            Assert.Equal((nuint)2, region_free_list.get_num_free_regions(&destination[(int)free_region_kind.basic_free_region]));
            Assert.True(region_free_list.is_on_free_list(lowBusy, destination));
            Assert.True(region_free_list.is_on_free_list(highBusy, destination));
            Assert.True(region_free_list.is_on_free_list(mapFree, source));
            Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_containing_free_list(allocated));
        }
        finally
        {
            RestoreRegionMoveGlobals(oldShift, oldTable, oldGlobalAllocator);
        }
    }

    [Fact]
    public void RegionAllocatorMoveHighestFreeRegionsSkipsDestinationMembersAndUsesExactQuota()
    {
        const nuint Alignment = 0x1000;
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        region_allocator oldGlobalAllocator = gc_heap.global_region_allocator;
        seg_mapping* table = stackalloc seg_mapping[6];
        uint* map = stackalloc uint[4];
        region_free_list* source = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_free_list* destination = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_allocator allocator = default;

        try
        {
            InitializeRegionMoveGlobals(table, Alignment);
            InitializeRegionAllocatorForMove(&allocator, map, 4, Alignment, (byte*)0x1000);
            ClearRegionFreeLists(source);
            ClearRegionFreeLists(destination);
            allocator.make_busy_block(map, 1);
            allocator.make_busy_block(map + 1, 1);
            allocator.make_busy_block(map + 2, 1);
            allocator.make_busy_block(map + 3, 1);
            WriteRegionAllocatorField(&allocator, "region_allocator_lock", new GCSpinLock { @lock = 1234 });

            heap_segment* lowest = InitializeMappedRegion(table, 0x1000, 1, Alignment);
            heap_segment* movedLow = InitializeMappedRegion(table, 0x2000, 1, Alignment);
            heap_segment* movedHigh = InitializeMappedRegion(table, 0x3000, 1, Alignment);
            heap_segment* alreadyDestination = InitializeMappedRegion(table, 0x4000, 1, Alignment);
            region_free_list.add_region(lowest, source);
            region_free_list.add_region(movedLow, source);
            region_free_list.add_region(movedHigh, source);
            region_free_list.add_region(alreadyDestination, destination);

            allocator.move_highest_free_regions(2, small_region_p: true, destination);

            Assert.Equal((nuint)3, region_free_list.get_num_free_regions(&destination[(int)free_region_kind.basic_free_region]));
            Assert.True(region_free_list.is_on_free_list(alreadyDestination, destination));
            Assert.True(region_free_list.is_on_free_list(movedHigh, destination));
            Assert.True(region_free_list.is_on_free_list(movedLow, destination));
            Assert.True(region_free_list.is_on_free_list(lowest, source));
            Assert.Equal(1234, ReadRegionAllocatorField<GCSpinLock>(&allocator, "region_allocator_lock").@lock);
        }
        finally
        {
            RestoreRegionMoveGlobals(oldShift, oldTable, oldGlobalAllocator);
        }
    }

    [Fact]
    public void RegionAllocatorMoveHighestFreeRegionsBreaksWithoutContinuingToLowerFit()
    {
        const nuint Alignment = 0x1000;
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        region_allocator oldGlobalAllocator = gc_heap.global_region_allocator;
        seg_mapping* table = stackalloc seg_mapping[10];
        uint* map = stackalloc uint[17];
        region_free_list* source = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_free_list* destination = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_allocator allocator = default;

        try
        {
            InitializeRegionMoveGlobals(table, Alignment);
            InitializeRegionAllocatorForMove(&allocator, map, 17, Alignment, (byte*)0x1000);
            ClearRegionFreeLists(source);
            ClearRegionFreeLists(destination);
            allocator.make_busy_block(map, 8);
            allocator.make_busy_block(map + 8, 9);

            heap_segment* lowerLarge = InitializeMappedRegion(table, 0x1000, 8, Alignment);
            heap_segment* higherHuge = InitializeMappedRegion(table, 0x9000, 9, Alignment);
            region_free_list.add_region(lowerLarge, source);
            region_free_list.add_region(higherHuge, source);

            allocator.move_highest_free_regions(8, small_region_p: false, destination);

            Assert.Equal((nuint)0, region_free_list.get_num_free_regions(&destination[(int)free_region_kind.large_free_region]));
            Assert.Equal((nuint)0, region_free_list.get_num_free_regions(&destination[(int)free_region_kind.huge_free_region]));
            Assert.True(region_free_list.is_on_free_list(lowerLarge, source));
            Assert.True(region_free_list.is_on_free_list(higherHuge, source));
        }
        finally
        {
            RestoreRegionMoveGlobals(oldShift, oldTable, oldGlobalAllocator);
        }
    }

    [Fact]
    public void RegionAllocatorMoveHighestFreeRegionsQuotaSpansMultipleLargeRegions()
    {
        const nuint Alignment = 0x1000;
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        region_allocator oldGlobalAllocator = gc_heap.global_region_allocator;
        seg_mapping* table = stackalloc seg_mapping[10];
        uint* map = stackalloc uint[16];
        region_free_list* source = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_free_list* destination = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_allocator allocator = default;

        try
        {
            InitializeRegionMoveGlobals(table, Alignment);
            InitializeRegionAllocatorForMove(&allocator, map, 16, Alignment, (byte*)0x1000);
            ClearRegionFreeLists(source);
            ClearRegionFreeLists(destination);
            allocator.make_busy_block(map, 8);
            allocator.make_busy_block(map + 8, 8);

            heap_segment* lowerLarge = InitializeMappedRegion(table, 0x1000, 8, Alignment);
            heap_segment* higherLarge = InitializeMappedRegion(table, 0x9000, 8, Alignment);
            region_free_list.add_region(lowerLarge, source);
            region_free_list.add_region(higherLarge, source);

            allocator.move_highest_free_regions(16, small_region_p: false, destination);

            region_free_list* destinationLarge = &destination[(int)free_region_kind.large_free_region];
            Assert.Equal((nuint)2, region_free_list.get_num_free_regions(destinationLarge));
            Assert.Equal((nuint)lowerLarge, (nuint)destinationLarge->get_first_free_region());
            Assert.Equal((nuint)higherLarge, (nuint)heap_segment.heap_segment_next(lowerLarge));
            Assert.Equal((nuint)0, region_free_list.get_num_free_regions(&source[(int)free_region_kind.large_free_region]));
        }
        finally
        {
            RestoreRegionMoveGlobals(oldShift, oldTable, oldGlobalAllocator);
        }
    }

    [Fact]
    public void RegionAllocatorMoveHighestFreeRegionsUpdatesSourceAndDestinationIntegrity()
    {
        const nuint Alignment = 0x1000;
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        region_allocator oldGlobalAllocator = gc_heap.global_region_allocator;
        seg_mapping* table = stackalloc seg_mapping[5];
        uint* map = stackalloc uint[3];
        region_free_list* source = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_free_list* destination = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_allocator allocator = default;

        try
        {
            InitializeRegionMoveGlobals(table, Alignment);
            InitializeRegionAllocatorForMove(&allocator, map, 3, Alignment, (byte*)0x1000);
            ClearRegionFreeLists(source);
            ClearRegionFreeLists(destination);
            allocator.make_busy_block(map, 1);
            allocator.make_busy_block(map + 1, 1);
            allocator.make_busy_block(map + 2, 1);

            heap_segment* low = InitializeMappedRegion(table, 0x1000, 1, Alignment);
            heap_segment* middle = InitializeMappedRegion(table, 0x2000, 1, Alignment);
            heap_segment* high = InitializeMappedRegion(table, 0x3000, 1, Alignment);
            region_free_list.add_region(low, source);
            region_free_list.add_region(middle, source);
            region_free_list.add_region(high, source);

            nuint movedSize = gc_heap.get_region_size(high);
            nuint movedCommitted = gc_heap.get_region_committed_size(high);
            nuint sourceSizeBefore = source[(int)free_region_kind.basic_free_region].get_size_free_regions();
            nuint sourceCommittedBefore = source[(int)free_region_kind.basic_free_region].get_size_committed_in_free();

            allocator.move_highest_free_regions(1, small_region_p: true, destination);

            Assert.Equal((nuint)2, region_free_list.get_num_free_regions(&source[(int)free_region_kind.basic_free_region]));
            Assert.Equal(sourceSizeBefore - movedSize, source[(int)free_region_kind.basic_free_region].get_size_free_regions());
            Assert.Equal(sourceCommittedBefore - movedCommitted, source[(int)free_region_kind.basic_free_region].get_size_committed_in_free());
            Assert.Equal((nuint)1, region_free_list.get_num_free_regions(&destination[(int)free_region_kind.basic_free_region]));
            Assert.Equal(movedSize, destination[(int)free_region_kind.basic_free_region].get_size_free_regions());
            Assert.Equal(movedCommitted, destination[(int)free_region_kind.basic_free_region].get_size_committed_in_free());
            Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_prev_free_region(high));
            Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_next(high));
            Assert.True(region_free_list.is_on_free_list(low, source));
            Assert.True(region_free_list.is_on_free_list(middle, source));
            Assert.True(region_free_list.is_on_free_list(high, destination));
        }
        finally
        {
            RestoreRegionMoveGlobals(oldShift, oldTable, oldGlobalAllocator);
        }
    }

    [Fact]
    public void RegionAllocatorMoveHighestFreeRegionsHonorsLeftMapTraversalBoundary()
    {
        const nuint Alignment = 0x1000;
        nuint oldShift = gc_heap.min_segment_size_shr;
        seg_mapping* oldTable = GCCommon.seg_mapping_table;
        region_allocator oldGlobalAllocator = gc_heap.global_region_allocator;
        seg_mapping* table = stackalloc seg_mapping[5];
        uint* map = stackalloc uint[4];
        region_free_list* source = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_free_list* destination = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        region_allocator allocator = default;

        try
        {
            InitializeRegionMoveGlobals(table, Alignment);
            InitializeRegionAllocatorForMove(&allocator, map + 1, 3, Alignment, (byte*)0x1000);
            ClearRegionFreeLists(source);
            ClearRegionFreeLists(destination);
            allocator.make_busy_block(map, 1);
            allocator.make_busy_block(map + 1, 1);
            allocator.make_busy_block(map + 2, 1);
            allocator.make_busy_block(map + 3, 1);

            heap_segment* beforeLeftStart = InitializeMappedRegion(table, 0x0, 1, Alignment);
            heap_segment* lowest = InitializeMappedRegion(table, 0x1000, 1, Alignment);
            heap_segment* middle = InitializeMappedRegion(table, 0x2000, 1, Alignment);
            heap_segment* highest = InitializeMappedRegion(table, 0x3000, 1, Alignment);
            region_free_list.add_region(beforeLeftStart, source);
            region_free_list.add_region(lowest, source);
            region_free_list.add_region(middle, source);
            region_free_list.add_region(highest, source);

            allocator.move_highest_free_regions(3, small_region_p: true, destination);

            Assert.Equal((nuint)3, region_free_list.get_num_free_regions(&destination[(int)free_region_kind.basic_free_region]));
            Assert.True(region_free_list.is_on_free_list(lowest, destination));
            Assert.True(region_free_list.is_on_free_list(middle, destination));
            Assert.True(region_free_list.is_on_free_list(highest, destination));
            Assert.True(region_free_list.is_on_free_list(beforeLeftStart, source));
        }
        finally
        {
            RestoreRegionMoveGlobals(oldShift, oldTable, oldGlobalAllocator);
        }
    }

    [Fact]
    public void RegionFreeListKindDispatchHelpersUseGlobalAllocatorAlignment()
    {
        gc_heap.global_region_allocator.initialize_alignment(0x1000);

        region_free_list* lists = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        heap_segment basic = default;
        heap_segment large = default;
        heap_segment huge = default;

        InitializeRegion(&basic, 0x1000, 0x1800, 0x2000, age: 0);
        InitializeRegion(&large, 0x3000, 0x5800, 0xB000, age: 0);
        InitializeRegion(&huge, 0xC000, 0xE000, 0x17000, age: 0);

        region_free_list.add_region(&basic, lists);
        region_free_list.add_region(&large, lists);
        region_free_list.add_region(&huge, lists);

        Assert.Equal((nuint)1, region_free_list.get_num_free_regions(&lists[(int)free_region_kind.basic_free_region]));
        Assert.Equal((nuint)1, region_free_list.get_num_free_regions(&lists[(int)free_region_kind.large_free_region]));
        Assert.Equal((nuint)1, region_free_list.get_num_free_regions(&lists[(int)free_region_kind.huge_free_region]));
        Assert.True(region_free_list.is_on_free_list(&basic, lists));
        Assert.True(region_free_list.is_on_free_list(&large, lists));
        Assert.True(region_free_list.is_on_free_list(&huge, lists));
        Assert.False(region_free_list.is_on_free_list(&basic, &lists[(int)free_region_kind.large_free_region]));
    }

    [Fact]
    public void RegionFreeListAddRegionDescendingDispatchesByKind()
    {
        gc_heap.global_region_allocator.initialize_alignment(0x1000);

        region_free_list* lists = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        heap_segment lessCommitted = default;
        heap_segment moreCommitted = default;

        InitializeRegion(&lessCommitted, 0x10000, 0x12000, 0x18000, age: 7);
        InitializeRegion(&moreCommitted, 0x20000, 0x26000, 0x28000, age: 3);

        region_free_list.add_region_descending(&lessCommitted, lists);
        region_free_list.add_region_descending(&moreCommitted, lists);

        region_free_list* largeList = &lists[(int)free_region_kind.large_free_region];
        Assert.Equal((nuint)2, region_free_list.get_num_free_regions(largeList));
        Assert.Equal((nuint)(&moreCommitted), (nuint)largeList->get_first_free_region());
        Assert.Equal((nuint)(&lessCommitted), (nuint)heap_segment.heap_segment_next(&moreCommitted));
        Assert.True(region_free_list.is_on_free_list(&moreCommitted, lists));
        Assert.True(region_free_list.is_on_free_list(&lessCommitted, lists));
    }

    [Fact]
    public void RegionFreeListUnlinkSmallestRegionUsesLargeAlignmentMinimum()
    {
        gc_heap.global_region_allocator.initialize_alignment(0x1000);

        nuint largeSize = gc_heap.global_region_allocator.get_large_region_alignment();
        region_free_list list = default;
        region_free_list* pList = &list;
        heap_segment twoLarge = default;
        heap_segment threeLarge = default;
        heap_segment fourLarge = default;

        InitializeRegion(&twoLarge, 0x100000, 0x118000, 0x100000 + (2 * largeSize), age: 0);
        InitializeRegion(&threeLarge, 0x200000, 0x225000, 0x200000 + (3 * largeSize), age: 0);
        InitializeRegion(&fourLarge, 0x300000, 0x330000, 0x300000 + (4 * largeSize), age: 0);

        region_free_list.add_region_front(pList, &fourLarge);
        region_free_list.add_region_front(pList, &twoLarge);
        region_free_list.add_region_front(pList, &threeLarge);

        heap_segment* selected = region_free_list.unlink_smallest_region(pList, largeSize);

        Assert.Equal((nuint)(&twoLarge), (nuint)selected);
        Assert.Equal((nuint)2, region_free_list.get_num_free_regions(pList));
        Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_containing_free_list(&twoLarge));
    }

    [Fact]
    public void RegionFreeListTransferAndAgeArrayPreserveOwnershipAndCap()
    {
        region_free_list* lists = stackalloc region_free_list[(int)free_region_kind.count_free_region_kinds];
        heap_segment basic = default;
        heap_segment large = default;
        heap_segment huge = default;

        InitializeRegion(&basic, 0x1000, 0x1800, 0x2000, age: heap_segment.MAX_AGE_IN_FREE - 1);
        InitializeRegion(&large, 0x3000, 0x3800, 0x5000, age: heap_segment.MAX_AGE_IN_FREE);
        InitializeRegion(&huge, 0x6000, 0x7400, 0x9000, age: 0);

        region_free_list.add_region_front(&lists[(int)free_region_kind.basic_free_region], &basic);
        region_free_list.add_region_front(&lists[(int)free_region_kind.large_free_region], &large);
        region_free_list.add_region_front(&lists[(int)free_region_kind.huge_free_region], &huge);

        region_free_list.age_free_regions(lists);
        Assert.Equal(heap_segment.MAX_AGE_IN_FREE, heap_segment.heap_segment_age_in_free(&basic));
        Assert.Equal(heap_segment.MAX_AGE_IN_FREE, heap_segment.heap_segment_age_in_free(&large));
        Assert.Equal(1, heap_segment.heap_segment_age_in_free(&huge));

        region_free_list destination = default;
        region_free_list* pDestination = &destination;
        region_free_list.transfer_regions(pDestination, &lists[(int)free_region_kind.basic_free_region]);

        Assert.Equal((nuint)1, region_free_list.get_num_free_regions(pDestination));
        Assert.Equal((nuint)0, region_free_list.get_num_free_regions(&lists[(int)free_region_kind.basic_free_region]));
        Assert.Equal((nuint)pDestination, (nuint)heap_segment.heap_segment_containing_free_list(&basic));
    }

    [Fact]
    public void ClearRegionInfoClearsBrickAndCardsAndRecordsBackgroundChange()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: false);
        uint* cards = stackalloc uint[3];
        short* bricks = stackalloc short[4];
        for (int i = 0; i < 3; i++)
        {
            cards[i] = uint.MaxValue;
        }
        for (int i = 0; i < 4; i++)
        {
            bricks[i] = -1;
        }

        gc_heap.card_table = cards;
        gc_heap.brick_table = bricks;
        gc_heap.settings.gc_index = 42;
#if BACKGROUND_GC
        gc_heap.current_bgc_state = bgc_state.bgc_sweep_soh;
        gc_heap.gc_background_running = 0;
#endif

        heap_segment region = default;
        InitializeRegion(&region, 0, card_table_info.brick_size * 4, card_table_info.brick_size * 4, age: 0);
        heap_segment.heap_segment_allocated(&region) = heap_segment.heap_segment_mem(&region);

        gc_heap.clear_region_info(&region);

        Assert.Equal(0u, cards[0]);
        Assert.Equal(0u, cards[1]);
        Assert.Equal(uint.MaxValue, cards[2]);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(0, bricks[i]);
        }

#if BACKGROUND_GC
        changed_seg changed = GCCommon.saved_changed_segs[(int)(GCCommon.saved_changed_segs_count & (GCCommon.max_saved_changed_segs - 1))];
        Assert.Equal((nuint)(&region), (nuint)changed.start);
        Assert.Equal((nuint)heap_segment.heap_segment_reserved(&region), (nuint)changed.end);
        Assert.Equal((nuint)42, changed.gc_index);
        Assert.Equal(bgc_state.bgc_sweep_soh, changed.bgc);
        Assert.Equal(changed_seg_state.seg_deleted, changed.changed);
#endif
    }

    [Fact]
    public void BrickOfUsesLowestAddress()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: false);
        gc_heap.lowest_address = (byte*)0x100000;

        Assert.Equal((nuint)0, gc_heap.brick_of(gc_heap.lowest_address));
        Assert.Equal((nuint)3, gc_heap.brick_of(gc_heap.lowest_address + (3 * card_table_info.brick_size)));
    }

    [Fact]
    public void ClearBrickTableIndexesRelativeToLowestAddress()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: false);
        short* bricks = stackalloc short[5];
        for (int i = 0; i < 5; i++)
        {
            bricks[i] = -1;
        }

        gc_heap.lowest_address = (byte*)0x100000;
        gc_heap.brick_table = bricks;

        gc_heap.clear_brick_table(
            gc_heap.lowest_address + card_table_info.brick_size,
            gc_heap.lowest_address + (4 * card_table_info.brick_size));

        Assert.Equal(-1, bricks[0]);
        Assert.Equal(0, bricks[1]);
        Assert.Equal(0, bricks[2]);
        Assert.Equal(0, bricks[3]);
        Assert.Equal(-1, bricks[4]);
    }

#if BACKGROUND_GC
    [Fact]
    public void InitTableForRegionCommitsMarkArrayAndInitializesOnlyTheFirstSohBrick()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: true);
        nuint pageSize = GCToOSInterface.GetPageSize();
        byte* regionStart = GCToOSInterface.VirtualReserve(pageSize, pageSize, (uint)VirtualReserveFlags.None);
        byte* markStorage = GCToOSInterface.VirtualReserve(pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(regionStart is not null);
        Assert.True(markStorage is not null);

        try
        {
            Assert.True(GCToOSInterface.VirtualCommit(regionStart, pageSize));

            heap_segment region = default;
            InitializeRegion(&region, (nuint)regionStart, (nuint)(regionStart + (nint)pageSize), (nuint)(regionStart + (nint)pageSize), age: 0);
            heap_segment.heap_segment_used(&region) = heap_segment.heap_segment_reserved(&region);

            nuint firstMarkWord = card_table_info.mark_word_of(heap_segment.heap_segment_mem(&region));
            gc_heap.mark_array = (uint*)markStorage - (nint)firstMarkWord;
            gc_heap.background_saved_lowest_address = heap_segment.heap_segment_mem(&region);
            gc_heap.background_saved_highest_address = heap_segment.heap_segment_reserved(&region);
            gc_heap.lowest_address = heap_segment.heap_segment_mem(&region);

            short* bricks = stackalloc short[3];
            bricks[0] = 17;
            bricks[1] = 23;
            bricks[2] = 29;
            gc_heap.brick_table = bricks;

            Assert.True(gc_heap.init_table_for_region((int)gc_generation_num.soh_gen0, &region));
            Assert.Equal(heap_segment.heap_segment_flags_ma_committed, region.flags & heap_segment.heap_segment_flags_ma_committed);
            Assert.Equal(-1, bricks[0]);
            Assert.Equal(23, bricks[1]);
            Assert.Equal(29, bricks[2]);
            Assert.Equal(0u, markStorage[0]);
            Assert.Equal(pageSize, gc_heap.committed_by_oh[gc_heap.recorded_committed_mark_array_bucket]);
            Assert.Equal(pageSize, gc_heap.current_total_committed);
            Assert.Equal(pageSize, gc_heap.current_total_committed_bookkeeping);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(markStorage, pageSize);
            GCToOSInterface.VirtualRelease(regionStart, pageSize);
        }
    }

    [Fact]
    public void InitTableForRegionPreservesExistingMarkCommitAndUohFirstBrick()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: true);
        nuint pageSize = GCToOSInterface.GetPageSize();
        byte* regionStart = GCToOSInterface.VirtualReserve(pageSize, pageSize, (uint)VirtualReserveFlags.None);
        byte* markStorage = GCToOSInterface.VirtualReserve(pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(regionStart is not null);
        Assert.True(markStorage is not null);

        try
        {
            Assert.True(GCToOSInterface.VirtualCommit(regionStart, pageSize));
            Assert.True(GCToOSInterface.VirtualCommit(markStorage, pageSize));

            heap_segment region = default;
            InitializeRegion(&region, (nuint)regionStart, (nuint)(regionStart + (nint)pageSize), (nuint)(regionStart + (nint)pageSize), age: 0);
            heap_segment.heap_segment_used(&region) = heap_segment.heap_segment_reserved(&region);
            region.flags = heap_segment.heap_segment_flags_ma_committed | heap_segment.heap_segment_flags_loh;

            nuint firstMarkWord = card_table_info.mark_word_of(heap_segment.heap_segment_mem(&region));
            gc_heap.mark_array = (uint*)markStorage - (nint)firstMarkWord;
            gc_heap.background_saved_lowest_address = heap_segment.heap_segment_mem(&region);
            gc_heap.background_saved_highest_address = heap_segment.heap_segment_reserved(&region);
            gc_heap.lowest_address = heap_segment.heap_segment_mem(&region);

            short* bricks = stackalloc short[2];
            bricks[0] = 0;
            bricks[1] = 31;
            gc_heap.brick_table = bricks;

            Assert.True(gc_heap.init_table_for_region((int)gc_generation_num.loh_generation, &region));
            Assert.Equal(heap_segment.heap_segment_flags_ma_committed, region.flags & heap_segment.heap_segment_flags_ma_committed);
            Assert.Equal(0, bricks[0]);
            Assert.Equal(31, bricks[1]);
            Assert.Equal((nuint)0, gc_heap.committed_by_oh[gc_heap.recorded_committed_mark_array_bucket]);
            Assert.Equal((nuint)0, gc_heap.current_total_committed);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(markStorage, pageSize);
            GCToOSInterface.VirtualRelease(regionStart, pageSize);
        }
    }

    [Fact]
    public void InitTableForRegionDecommitsAndFailsWhenMarkArrayCommitExceedsTheHardLimit()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: true);
        nuint pageSize = GCToOSInterface.GetPageSize();
        byte* regionReservation = GCToOSInterface.VirtualReserve(pageSize, pageSize, (uint)VirtualReserveFlags.None);
        byte* markStorage = GCToOSInterface.VirtualReserve(pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(regionReservation is not null);
        Assert.True(markStorage is not null);

        uint* map = null;
        try
        {
            byte* lowest = null;
            byte* highest = null;
            Assert.True(gc_heap.global_region_allocator.init(regionReservation, regionReservation + (nint)pageSize, pageSize, &lowest, &highest));
            gc_heap.global_region_allocator.initialize();
            map = gc_heap.global_region_allocator.region_map_index_of(regionReservation);

            byte* regionStart = null;
            byte* regionEnd = null;
            Assert.True(gc_heap.global_region_allocator.allocate_basic_region((int)gc_generation_num.soh_gen0, &regionStart, &regionEnd, null));
            Assert.True(GCToOSInterface.VirtualCommit(regionStart, pageSize));

            heap_segment region = default;
            InitializeRegion(&region, (nuint)regionStart, (nuint)regionEnd, (nuint)regionEnd, age: 0);
            heap_segment.heap_segment_used(&region) = heap_segment.heap_segment_reserved(&region);

            nuint firstMarkWord = card_table_info.mark_word_of(heap_segment.heap_segment_mem(&region));
            gc_heap.mark_array = (uint*)markStorage - (nint)firstMarkWord;
            gc_heap.background_saved_lowest_address = heap_segment.heap_segment_mem(&region);
            gc_heap.background_saved_highest_address = heap_segment.heap_segment_reserved(&region);
            gc_heap.lowest_address = heap_segment.heap_segment_mem(&region);
            gc_heap.heap_hard_limit = pageSize;
            gc_heap.committed_by_oh[(int)gc_oh_num.soh] = pageSize;
            gc_heap.current_total_committed = pageSize;

            short* bricks = stackalloc short[2];
            bricks[0] = 37;
            bricks[1] = 41;
            gc_heap.brick_table = bricks;

            Assert.False(gc_heap.init_table_for_region((int)gc_generation_num.soh_gen0, &region));
            Assert.Equal((nuint)heap_segment.heap_segment_mem(&region), (nuint)heap_segment.heap_segment_committed(&region));
            Assert.Equal((nuint)0, gc_heap.committed_by_oh[(int)gc_oh_num.soh]);
            Assert.Equal((nuint)0, gc_heap.current_total_committed);
            Assert.Equal(pageSize, gc_heap.global_region_allocator.get_free());
            Assert.Equal(37, bricks[0]);
            Assert.Equal(41, bricks[1]);
        }
        finally
        {
            if (map is not null)
            {
                SyncImports.ManagedGC_Free(map);
            }

            GCToOSInterface.VirtualRelease(markStorage, pageSize);
            GCToOSInterface.VirtualRelease(regionReservation, pageSize);
        }
    }

    [Fact]
    public void BackgroundGcDiagnosticEnumsMatchNativeOrder()
    {
        Assert.Equal(0, (int)bgc_state.bgc_not_in_process);
        Assert.Equal(1, (int)bgc_state.bgc_initialized);
        Assert.Equal(2, (int)bgc_state.bgc_reset_ww);
        Assert.Equal(3, (int)bgc_state.bgc_mark_handles);
        Assert.Equal(4, (int)bgc_state.bgc_mark_stack);
        Assert.Equal(5, (int)bgc_state.bgc_revisit_soh);
        Assert.Equal(6, (int)bgc_state.bgc_revisit_uoh);
        Assert.Equal(7, (int)bgc_state.bgc_overflow_soh);
        Assert.Equal(8, (int)bgc_state.bgc_overflow_uoh);
        Assert.Equal(9, (int)bgc_state.bgc_final_marking);
        Assert.Equal(10, (int)bgc_state.bgc_sweep_soh);
        Assert.Equal(11, (int)bgc_state.bgc_sweep_uoh);
        Assert.Equal(12, (int)bgc_state.bgc_plan_phase);
        Assert.Equal(0, (int)changed_seg_state.seg_deleted);
        Assert.Equal(1, (int)changed_seg_state.seg_added);
    }
#endif

    [Fact]
    public void ClearRegionInfoSkipsBrickClearingForUohRegions()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: false);
        uint* cards = stackalloc uint[3];
        short* bricks = stackalloc short[4];
        for (int i = 0; i < 3; i++)
        {
            cards[i] = uint.MaxValue;
        }
        for (int i = 0; i < 4; i++)
        {
            bricks[i] = -1;
        }

        gc_heap.card_table = cards;
        gc_heap.brick_table = bricks;

        heap_segment region = default;
        InitializeRegion(&region, 0, card_table_info.brick_size * 4, card_table_info.brick_size * 4, age: 0);
        region.flags = heap_segment.heap_segment_flags_loh;
        heap_segment.heap_segment_allocated(&region) = heap_segment.heap_segment_mem(&region);

        gc_heap.clear_region_info(&region);

        Assert.Equal(0u, cards[0]);
        Assert.Equal(0u, cards[1]);
        Assert.Equal(uint.MaxValue, cards[2]);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(-1, bricks[i]);
        }
    }

    [Fact]
    public void ReturnFreeRegionTransfersAccountingAddsToFreeListAndClearsBasicRegionSentinels()
    {
        const nuint Alignment = 0x1000;
        using RegionSegmentsStateScope _ = new(initializeCommitLock: true);
        seg_mapping* table = stackalloc seg_mapping[region_allocator.LARGE_REGION_FACTOR];
        uint* cards = stackalloc uint[5];
        short* bricks = stackalloc short[8];
        for (int i = 0; i < 5; i++)
        {
            cards[i] = uint.MaxValue;
        }
        for (int i = 0; i < 8; i++)
        {
            bricks[i] = -1;
        }

        gc_heap.card_table = cards;
        gc_heap.brick_table = bricks;
        gc_heap.min_segment_size_shr = (nuint)gc_heap.index_of_highest_set_bit(Alignment);
        gc_heap.global_region_allocator.initialize_alignment(Alignment);
        GCCommon.seg_mapping_table = table;

        heap_segment* region = &table[0].region_info;
        InitializeRegion(region, 0, 6 * Alignment, region_allocator.LARGE_REGION_FACTOR * Alignment, age: 7);
        heap_segment.heap_segment_allocated(region) = heap_segment.heap_segment_mem(region);
        heap_segment.heap_segment_gen_num(region) = 2;
        heap_segment.heap_segment_plan_gen_num(region) = 1;
        for (int i = 1; i < region_allocator.LARGE_REGION_FACTOR; i++)
        {
            heap_segment* basicRegion = &table[i].region_info;
            basicRegion->allocated = (byte*)(nint)(-i);
            basicRegion->gen_num = 2;
            basicRegion->plan_gen_num = 1;
        }

        gc_heap.committed_by_oh[(int)gc_oh_num.soh] = 6 * Alignment;

        gc_heap.return_free_region(region);

        Assert.Equal((nuint)0, gc_heap.committed_by_oh[(int)gc_oh_num.soh]);
        Assert.Equal(6 * Alignment, gc_heap.committed_by_oh[gc_heap.recorded_committed_free_bucket]);
        Assert.Equal((nuint)1, region_free_list.get_num_free_regions(gc_heap.free_regions_of((int)free_region_kind.large_free_region)));
        Assert.Equal((nuint)region, (nuint)gc_heap.free_regions_of((int)free_region_kind.large_free_region)->get_first_free_region());
        Assert.Equal(region_allocator.LARGE_REGION_FACTOR * Alignment, gc_heap.free_regions_of((int)free_region_kind.large_free_region)->get_size_free_regions());
        Assert.Equal(6 * Alignment, gc_heap.free_regions_of((int)free_region_kind.large_free_region)->get_size_committed_in_free());

        for (int i = 0; i < region_allocator.LARGE_REGION_FACTOR; i++)
        {
            heap_segment* basicRegion = &table[i].region_info;
            Assert.Equal((nuint)0, (nuint)heap_segment.heap_segment_allocated(basicRegion));
            Assert.Equal((byte)2, heap_segment.heap_segment_gen_num(basicRegion));
            Assert.Equal(1, heap_segment.heap_segment_plan_gen_num(basicRegion));
        }

        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(0u, cards[i]);
        }
        Assert.Equal(uint.MaxValue, cards[4]);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(0, bricks[i]);
        }
    }

    [Fact]
    public void AllocationContextAlignmentAndSizeFitPreserveNativeBoundaryAndOverflowArithmetic()
    {
        int sohAlignment = gc_heap.get_alignment_constant(small_object_p: true);
        int uohAlignment = gc_heap.get_alignment_constant(small_object_p: false);
        nuint alignedMinObjectSize = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, sohAlignment);

        Assert.Equal(GCEnv.DATA_ALIGNMENT - 1, sohAlignment);
        Assert.Equal(7, uohAlignment);
        Assert.Equal(unchecked(((nuint)0x11 + (nuint)sohAlignment) & ~(nuint)sohAlignment), gc_heap.Align(0x11, sohAlignment));
        Assert.Equal((nuint)0, gc_heap.Align(nuint.MaxValue, 7));

        gc_alloc_context context = default;
        context.alloc_ptr = (byte*)0x1000;
        context.alloc_limit = context.alloc_ptr + (nint)alignedMinObjectSize;
        byte* originalPointer = context.alloc_ptr;
        byte* originalLimit = context.alloc_limit;

        Assert.True(gc_heap.a_size_fit_p(0, context.alloc_ptr, context.alloc_limit, sohAlignment));
        Assert.False(gc_heap.a_size_fit_p(1, context.alloc_ptr, context.alloc_limit, sohAlignment));
        Assert.False(gc_heap.a_size_fit_p(0, context.alloc_limit, context.alloc_ptr, sohAlignment));
        byte* overflowLimit = context.alloc_ptr + (nint)(alignedMinObjectSize - 1);
        Assert.True(gc_heap.a_size_fit_p(nuint.MaxValue, context.alloc_ptr, overflowLimit, sohAlignment));
        Assert.Equal((nuint)originalPointer, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)originalLimit, (nuint)context.alloc_limit);
    }

    [Fact]
    public void AllocationContextRetirementAndVoidPreserveAccountingAndNullBehavior()
    {
        gc_alloc_context context = default;
        context.alloc_ptr = (byte*)0x1020;
        context.alloc_limit = (byte*)0x1040;
        context.alloc_bytes = 100;
        context.alloc_bytes_uoh = 17;
        ulong totalAllocatedBytesSoh = 500;

        gc_heap.retire_allocation_context(&context, &totalAllocatedBytesSoh);

        Assert.Equal(68, context.alloc_bytes);
        Assert.Equal(17, context.alloc_bytes_uoh);
        Assert.Equal(468ul, totalAllocatedBytesSoh);
        Assert.Equal((nuint)0, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)0, (nuint)context.alloc_limit);

        context.alloc_limit = (byte*)0x2000;
        gc_heap.void_allocation(&context);

        Assert.Equal((nuint)0, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)0x2000, (nuint)context.alloc_limit);
        Assert.Equal(68, context.alloc_bytes);
        Assert.Equal(468ul, totalAllocatedBytesSoh);
    }

    [Fact]
    public void AllocationContextAccountingKeepsSohAndUohCountersDistinct()
    {
        gc_alloc_context context = default;
        context.alloc_bytes = 100;
        context.alloc_bytes_uoh = 200;
        ulong totalAllocatedBytesSoh = 300;

        gc_heap.add_alloc_bytes(&context, 24, &totalAllocatedBytesSoh);
        gc_heap.add_uoh_alloc_bytes(&context, 40);

        Assert.Equal(124, context.alloc_bytes);
        Assert.Equal(240, context.alloc_bytes_uoh);
        Assert.Equal(324ul, totalAllocatedBytesSoh);
    }

    [Fact]
    public void AllocationContextLimitAndSizeHelpersPreservePolicyBoundariesAndOverflow()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint alignedMinObjectSize = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        dynamic_data data = default;
        data.new_allocation = 64;

        Assert.Equal((nuint)48, gc_heap.new_allocation_limit(&data, 32, 48, (int)gc_generation_num.soh_gen0));
        Assert.Equal((nuint)48, gc_heap.limit_from_size(&data, 48, 8, 0, 128, (int)gc_generation_num.soh_gen0, alignment));
        Assert.Equal((nuint)32, gc_heap.limit_from_size(&data, 48, 8, (uint)GC_ALLOC_FLAGS.GC_ALLOC_ZEROING_OPTIONAL, 128, (int)gc_generation_num.soh_gen0, alignment));
        Assert.Equal((nuint)48, gc_heap.limit_from_size(&data, 48, unchecked(nuint.MaxValue - alignedMinObjectSize + 1), 0, 64, (int)gc_generation_num.soh_gen0, alignment));

        gc_alloc_context context = default;
        context.alloc_ptr = (byte*)0x1000;
        context.alloc_bytes = 100;
        ulong totalAllocatedBytes = 200;

        gc_heap.set_alloc_context_limit(&context, (byte*)0x1000, 64, (int)gc_generation_num.soh_gen0, alignment, &totalAllocatedBytes);

        Assert.Equal((nuint)0x1028, (nuint)context.alloc_limit);
        Assert.Equal(140, context.alloc_bytes);
        Assert.Equal(240ul, totalAllocatedBytes);

        context.alloc_bytes = 0;
        totalAllocatedBytes = 0;
        gc_heap.set_alloc_context_limit(&context, null, 0, (int)gc_generation_num.soh_gen0, alignment, &totalAllocatedBytes);

        Assert.Equal(unchecked(nuint.MaxValue - alignedMinObjectSize + 1), (nuint)context.alloc_limit);
        Assert.Equal(-(long)alignedMinObjectSize, context.alloc_bytes);
        Assert.Equal(unchecked(0ul - (ulong)alignedMinObjectSize), totalAllocatedBytes);
    }

    [Fact]
    public void MakeUnusedArrayAndFreeObjectWriteNativeObjectBytesAndAccounting()
    {
        byte* storage = stackalloc byte[128];
        nuint minimumObjectSize = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size);
        void* savedFreeObjectMethodTable = GCCommon.g_gc_pFreeObjectMethodTable;
        GCCommon.g_gc_pFreeObjectMethodTable = (void*)0x12345000;

        try
        {
            for (int i = 0; i < 128; i++)
            {
                storage[i] = 0xcc;
            }

            gc_heap.make_unused_array(storage, minimumObjectSize);

            Assert.Equal((nuint)0x12345000, *(nuint*)storage);
            Assert.Equal((nuint)0, *(nuint*)(storage + (nint)sizeof(nuint)));
            for (nint i = 2 * sizeof(nuint); i < (nint)minimumObjectSize; i++)
            {
                Assert.Equal((byte)0xcc, storage[i]);
            }

            generation gen = default;
            nuint freeObjectSize = unchecked(2 * minimumObjectSize);
            gc_heap.make_free_obj(&gen, storage, freeObjectSize);

            Assert.Equal((nuint)0x12345000, *(nuint*)storage);
            Assert.Equal(minimumObjectSize, *(nuint*)(storage + (nint)sizeof(nuint)));
            Assert.Equal((byte)0xcc, storage[2 * sizeof(nuint)]);
#if TARGET_64BIT && !TARGET_WASM
            Assert.Equal((nuint)1, (nuint)((byte**)storage)[3]);
#endif
            Assert.Equal(freeObjectSize, generation.generation_free_obj_space(&gen));
        }
        finally
        {
            GCCommon.g_gc_pFreeObjectMethodTable = savedFreeObjectMethodTable;
        }
    }

#if USE_REGIONS
    [Fact]
    public void AdjustLimitClrFillsDiscontinuousContextHoleAndPreservesAccounting()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint alignedMinObjectSize = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        byte* storage = stackalloc byte[256];
        byte* hole = storage + 32;
        byte* start = storage + 128;
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generation.initialize(&generations[i]);
        }

        heap_segment ephemeral = default;
        gc_alloc_context context = default;
        context.alloc_ptr = hole;
        context.alloc_limit = hole + 32;
        context.alloc_bytes = 100;
        ulong totalAllocatedBytes = 200;
        void* savedFreeObjectMethodTable = GCCommon.g_gc_pFreeObjectMethodTable;
        GCCommon.g_gc_pFreeObjectMethodTable = (void*)0x12345000;

        try
        {
            gc_heap.adjust_limit_clr(
                start,
                64,
                &context,
                null,
                alignment,
                (int)gc_generation_num.soh_gen0,
                generations,
                &ephemeral,
                null,
                &totalAllocatedBytes);

            Assert.Equal((nuint)start, (nuint)context.alloc_ptr);
            Assert.Equal((nuint)(start + (nint)((nuint)64 - alignedMinObjectSize)), (nuint)context.alloc_limit);
            Assert.Equal(108, context.alloc_bytes);
            Assert.Equal(208ul, totalAllocatedBytes);
            Assert.Equal(32 + alignedMinObjectSize, generation.generation_free_obj_space(generations));
            Assert.Equal((nuint)0x12345000, *(nuint*)hole);
            Assert.Equal((nuint)32, *(nuint*)(hole + (nint)sizeof(nuint)));
        }
        finally
        {
            GCCommon.g_gc_pFreeObjectMethodTable = savedFreeObjectMethodTable;
        }
    }

    [Fact]
    public void AdjustLimitClrStartsNullRegionContextAndPublishesEphemeralUsed()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint alignedMinObjectSize = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        byte* storage = stackalloc byte[256];
        byte* start = storage + 32;
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generation.initialize(&generations[i]);
        }

        heap_segment segment = default;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = storage + 192;
        heap_segment.heap_segment_reserved(&segment) = storage + 192;
        gc_alloc_context context = default;
        context.alloc_limit = start;
        byte* allocAllocated = start;
        ulong totalAllocatedBytes = 0;

        gc_heap.advance_allocated(&allocAllocated, &segment, 64, (int)gc_generation_num.soh_gen0);
        gc_heap.adjust_limit_clr(
            start,
            64,
            &context,
            &segment,
            alignment,
            (int)gc_generation_num.soh_gen0,
            generations,
            &segment,
            allocAllocated,
            &totalAllocatedBytes);

        Assert.Equal((nuint)start, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)(start + (nint)((nuint)64 - alignedMinObjectSize)), (nuint)context.alloc_limit);
        Assert.Equal((long)((nuint)64 - alignedMinObjectSize), context.alloc_bytes);
        Assert.Equal((ulong)((nuint)64 - alignedMinObjectSize), totalAllocatedBytes);
        Assert.Equal((nuint)(start + 64), (nuint)allocAllocated);
        Assert.Equal((nuint)(allocAllocated - sizeof(nuint)), (nuint)heap_segment.heap_segment_used(&segment));
        Assert.True(heap_segment.heap_segment_mem(&segment) <= heap_segment.heap_segment_used(&segment));
        Assert.True(heap_segment.heap_segment_used(&segment) <= heap_segment.heap_segment_committed(&segment));
        Assert.True(heap_segment.heap_segment_used(&segment) <= heap_segment.heap_segment_reserved(&segment));
    }

    [Fact]
    public void AdjustLimitClrPadsContiguousGen0Context()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint alignedMinObjectSize = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        byte* storage = stackalloc byte[256];
        byte* start = storage + 64;
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generation.initialize(&generations[i]);
        }

        heap_segment ephemeral = default;
        gc_alloc_context context = default;
        context.alloc_ptr = start;
        context.alloc_limit = start;
        context.alloc_bytes = 5;
        ulong totalAllocatedBytes = 7;
        void* savedFreeObjectMethodTable = GCCommon.g_gc_pFreeObjectMethodTable;
        GCCommon.g_gc_pFreeObjectMethodTable = (void*)0x12345000;

        try
        {
            gc_heap.adjust_limit_clr(
                start,
                64,
                &context,
                null,
                alignment,
                (int)gc_generation_num.soh_gen0,
                generations,
                &ephemeral,
                null,
                &totalAllocatedBytes);

            Assert.Equal((nuint)(start + (nint)alignedMinObjectSize), (nuint)context.alloc_ptr);
            Assert.Equal((nuint)(start + (nint)((nuint)64 - alignedMinObjectSize)), (nuint)context.alloc_limit);
            Assert.Equal(45, context.alloc_bytes);
            Assert.Equal(47ul, totalAllocatedBytes);
            Assert.Equal((nuint)0x12345000, *(nuint*)start);
            Assert.Equal((nuint)0, *(nuint*)(start + (nint)sizeof(nuint)));
            Assert.Equal((nuint)0, generation.generation_free_obj_space(generations));
        }
        finally
        {
            GCCommon.g_gc_pFreeObjectMethodTable = savedFreeObjectMethodTable;
        }
    }

    [Fact]
    public void AdjustLimitClrKeepsUohAccountingAndAdvancesSegmentAllocation()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: false);
        nuint alignedMinObjectSize = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        byte* storage = stackalloc byte[256];
        byte* start = storage + 32;
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generation.initialize(&generations[i]);
        }

        heap_segment segment = default;
        heap_segment ephemeral = default;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = storage + 192;
        heap_segment.heap_segment_reserved(&segment) = storage + 192;
        gc_alloc_context context = default;
        context.alloc_ptr = start;
        context.alloc_limit = start;
        context.alloc_bytes = 100;
        context.alloc_bytes_uoh = 200;
        ulong totalAllocatedBytesUoh = 300;

        gc_heap.advance_allocated(null, &segment, 64, (int)gc_generation_num.loh_generation);
        gc_heap.adjust_limit_clr(
            start,
            64,
            &context,
            &segment,
            alignment,
            (int)gc_generation_num.loh_generation,
            generations,
            &ephemeral,
            null,
            &totalAllocatedBytesUoh);

        Assert.Equal((nuint)start, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)(start + (nint)((nuint)64 - alignedMinObjectSize)), (nuint)context.alloc_limit);
        Assert.Equal(164, context.alloc_bytes);
        Assert.Equal(200, context.alloc_bytes_uoh);
        Assert.Equal(364ul, totalAllocatedBytesUoh);
        Assert.Equal((nuint)(start + 64), (nuint)heap_segment.heap_segment_allocated(&segment));
        Assert.Equal((nuint)start, (nuint)heap_segment.heap_segment_used(&segment));
    }

    [Fact]
    public void FitSegmentEndUsesExactCommittedSohSpaceAndHandsOffAllocationContext()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[256];
        byte* start = storage + 32;
        heap_segment segment = default;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = (byte*)unchecked((nuint)start + size + (2 * pad));
        heap_segment.heap_segment_reserved(&segment) = heap_segment.heap_segment_committed(&segment);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(size + pad));
        gc_alloc_context context = default;
        byte* allocAllocated = start;
        ulong totalAllocatedBytes = 0;
        bool commitFailed = true;

        Assert.True(gc_heap.a_fit_segment_end_p(
            (int)gc_generation_num.soh_gen0,
            &segment,
            size,
            &context,
            0,
            alignment,
            &commitFailed,
            &data,
            0,
            generations,
            &segment,
            &allocAllocated,
            &totalAllocatedBytes,
            0));

        Assert.False(commitFailed);
        Assert.Equal((nuint)(start + (nint)(size + pad)), (nuint)allocAllocated);
        Assert.Equal((nuint)start, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)(start + (nint)size), (nuint)context.alloc_limit);
        Assert.Equal((long)size, context.alloc_bytes);
        Assert.Equal((ulong)size, totalAllocatedBytes);
        Assert.Equal((nint)0, data.new_allocation);
        Assert.Equal((nuint)(allocAllocated - sizeof(nuint)), (nuint)heap_segment.heap_segment_used(&segment));
    }

    [Fact]
    public void FitSegmentEndRejectsShortCommittedSegmentWithoutChangingState()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[256];
        byte* start = storage + 32;
        heap_segment segment = default;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = (byte*)unchecked((nuint)start + (2 * pad));
        heap_segment.heap_segment_reserved(&segment) = heap_segment.heap_segment_committed(&segment);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(size + pad));
        gc_alloc_context context = default;
        byte* allocAllocated = start;
        ulong totalAllocatedBytes = 0;
        bool commitFailed = true;

        Assert.False(gc_heap.a_fit_segment_end_p(
            (int)gc_generation_num.soh_gen0,
            &segment,
            size,
            &context,
            0,
            alignment,
            &commitFailed,
            &data,
            0,
            generations,
            &segment,
            &allocAllocated,
            &totalAllocatedBytes,
            0));

        Assert.False(commitFailed);
        Assert.Equal((nuint)start, (nuint)allocAllocated);
        Assert.Equal((nuint)start, (nuint)heap_segment.heap_segment_allocated(&segment));
        Assert.Equal((nuint)start, (nuint)heap_segment.heap_segment_used(&segment));
        Assert.Equal((nint)(size + pad), data.new_allocation);
        Assert.Equal((nuint)0, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)0, (nuint)context.alloc_limit);
        Assert.Equal((ulong)0, totalAllocatedBytes);
    }

    [Fact]
    public void FitSegmentEndGrowsCommittedRegionAndUpdatesCommitAccounting()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: true);
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint pageSize = GCToOSInterface.GetPageSize();
        byte* reservation = GCToOSInterface.VirtualReserve(4 * pageSize, pageSize, (uint)VirtualReserveFlags.None);
        Assert.True(reservation is not null);

        try
        {
            Assert.True(GCToOSInterface.VirtualCommit(reservation, pageSize));

            byte* start = reservation + sizeof(aligned_plug_and_gap);
            heap_segment segment = default;
            heap_segment.heap_segment_mem(&segment) = start;
            heap_segment.heap_segment_allocated(&segment) = start;
            heap_segment.heap_segment_used(&segment) = start;
            heap_segment.heap_segment_committed(&segment) = reservation + (nint)pageSize;
            heap_segment.heap_segment_reserved(&segment) = reservation + (nint)(4 * pageSize);
            generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
            for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
            {
                generations[i] = default;
                generation.initialize(&generations[i]);
            }

            dynamic_data data = default;
            data.new_allocation = unchecked((nint)(pageSize + pad));
            gc_alloc_context context = default;
            byte* allocAllocated = start;
            ulong totalAllocatedBytes = 0;
            bool commitFailed = true;

            Assert.True(gc_heap.a_fit_segment_end_p(
                (int)gc_generation_num.soh_gen0,
                &segment,
                pageSize,
                &context,
                0,
                alignment,
                &commitFailed,
                &data,
                0,
                generations,
                &segment,
                &allocAllocated,
                &totalAllocatedBytes,
                0));

            Assert.False(commitFailed);
            Assert.Equal((nuint)(reservation + (nint)(4 * pageSize)), (nuint)heap_segment.heap_segment_committed(&segment));
            Assert.Equal((nuint)(start + (nint)(pageSize + pad)), (nuint)allocAllocated);
            Assert.Equal((nuint)(start + (nint)pageSize), (nuint)context.alloc_limit);
            Assert.Equal((ulong)pageSize, totalAllocatedBytes);
            Assert.Equal((nint)0, data.new_allocation);
            Assert.Equal(3 * pageSize, gc_heap.committed_by_oh[(int)gc_oh_num.soh]);
            Assert.Equal(3 * pageSize, gc_heap.current_total_committed);
        }
        finally
        {
            GCToOSInterface.VirtualRelease(reservation, 4 * pageSize);
        }
    }

    [Fact]
    public void FitSegmentEndPropagatesCommitFailure()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: true);
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint pageSize = GCToOSInterface.GetPageSize();
        byte* start = (byte*)0x2000;
        heap_segment segment = default;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = start + (nint)pageSize;
        heap_segment.heap_segment_reserved(&segment) = start + (nint)(2 * pageSize);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(pageSize + pad));
        gc_alloc_context context = default;
        byte* allocAllocated = start;
        ulong totalAllocatedBytes = 0;
        bool commitFailed = false;

        Assert.False(gc_heap.a_fit_segment_end_p(
            (int)gc_generation_num.soh_gen0,
            &segment,
            pageSize,
            &context,
            0,
            alignment,
            &commitFailed,
            &data,
            0,
            generations,
            &segment,
            &allocAllocated,
            &totalAllocatedBytes,
            0));

        Assert.True(commitFailed);
        Assert.Equal((nuint)(start + (nint)pageSize), (nuint)heap_segment.heap_segment_committed(&segment));
        Assert.Equal((nuint)start, (nuint)allocAllocated);
        Assert.Equal((nint)(pageSize + pad), data.new_allocation);
        Assert.Equal((nuint)0, gc_heap.current_total_committed);
    }

    [Fact]
    public void GrowHeapSegmentReportsHardLimitBeforeCommitting()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: true);
        nuint pageSize = GCToOSInterface.GetPageSize();
        byte* committed = (byte*)0x3000;
        heap_segment segment = default;
        heap_segment.heap_segment_committed(&segment) = committed;
        heap_segment.heap_segment_reserved(&segment) = committed + (nint)pageSize;
        gc_heap.heap_hard_limit = pageSize - 1;
        bool hardLimitExceeded = false;

        Assert.False(gc_heap.grow_heap_segment(&segment, committed + 1, 0, &hardLimitExceeded));

        Assert.True(hardLimitExceeded);
        Assert.Equal((nuint)committed, (nuint)heap_segment.heap_segment_committed(&segment));
        Assert.Equal((nuint)0, gc_heap.current_total_committed);
    }

    [Fact]
    public void FitSegmentEndSelectsUohSegmentAllocationPointer()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: false);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[256];
        byte* start = storage + 32;
        heap_segment segment = default;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = (byte*)unchecked((nuint)start + size + (2 * pad));
        heap_segment.heap_segment_reserved(&segment) = heap_segment.heap_segment_committed(&segment);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(size + pad));
        gc_alloc_context context = default;
        byte* allocAllocated = storage + 224;
        ulong totalAllocatedBytes = 0;
        bool commitFailed = true;

        Assert.True(gc_heap.a_fit_segment_end_p(
            (int)gc_generation_num.loh_generation,
            &segment,
            size,
            &context,
            0,
            alignment,
            &commitFailed,
            &data,
            0,
            generations,
            null,
            &allocAllocated,
            &totalAllocatedBytes,
            0));

        Assert.False(commitFailed);
        Assert.Equal((nuint)(start + (nint)(size + pad)), (nuint)heap_segment.heap_segment_allocated(&segment));
        Assert.Equal((nuint)(storage + 224), (nuint)allocAllocated);
        Assert.Equal((nuint)start, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)(start + (nint)size), (nuint)context.alloc_limit);
        Assert.Equal((nuint)start, (nuint)heap_segment.heap_segment_used(&segment));
        Assert.Equal((long)(size + pad), context.alloc_bytes);
        Assert.Equal((ulong)(size + pad), totalAllocatedBytes);
    }

    [Fact]
    public void UohFitSegmentEndSkipsShortSegmentAndTracksEndSegmentAllocation()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: false);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[512];
        heap_segment* segments = stackalloc heap_segment[2];
        segments[0] = default;
        segments[1] = default;
        byte* firstStart = storage + 32;
        byte* secondStart = storage + 256;
        heap_segment.heap_segment_mem(&segments[0]) = firstStart;
        heap_segment.heap_segment_allocated(&segments[0]) = firstStart;
        heap_segment.heap_segment_used(&segments[0]) = firstStart;
        heap_segment.heap_segment_committed(&segments[0]) = (byte*)unchecked((nuint)firstStart + (2 * pad));
        heap_segment.heap_segment_reserved(&segments[0]) = heap_segment.heap_segment_committed(&segments[0]);
        heap_segment.heap_segment_next(&segments[0]) = &segments[1];
        heap_segment.heap_segment_mem(&segments[1]) = secondStart;
        heap_segment.heap_segment_allocated(&segments[1]) = secondStart;
        heap_segment.heap_segment_used(&segments[1]) = secondStart;
        heap_segment.heap_segment_committed(&segments[1]) = (byte*)unchecked((nuint)secondStart + size + pad);
        heap_segment.heap_segment_reserved(&segments[1]) = heap_segment.heap_segment_committed(&segments[1]);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        generation.generation_allocation_segment(
            gc_heap.generation_of(generations, (int)gc_generation_num.loh_generation)) = &segments[0];
        dynamic_data data = default;
        data.new_allocation = unchecked((nint)size);
        gc_alloc_context context = default;
        ulong totalAllocatedBytes = 0;
        bool commitFailed = true;
        oom_reason oomReason = oom_reason.oom_no_failure;

        Assert.True(gc_heap.uoh_a_fit_segment_end_p(
            (int)gc_generation_num.loh_generation,
            size,
            &context,
            0,
            alignment,
            &commitFailed,
            &oomReason,
            &data,
            0,
            generations,
            null,
            null,
            &totalAllocatedBytes,
            0));

        Assert.False(commitFailed);
        Assert.Equal(oom_reason.oom_no_failure, oomReason);
        Assert.Equal((nuint)firstStart, (nuint)heap_segment.heap_segment_allocated(&segments[0]));
        Assert.Equal((nuint)(secondStart + (nint)size), (nuint)heap_segment.heap_segment_allocated(&segments[1]));
        Assert.Equal((nuint)secondStart, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)(secondStart + (nint)size), (nuint)context.alloc_limit);
        Assert.Equal(size, generation.generation_end_seg_allocated(
            gc_heap.generation_of(generations, (int)gc_generation_num.loh_generation)));
        Assert.Equal((nint)0, data.new_allocation);
        Assert.Equal((ulong)size, totalAllocatedBytes);
    }

    [Fact]
    public void SohFreeListExactFitHandsOffAllocationContextWithPadding()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        nuint freeSize = unchecked(size + pad);
        byte* storage = stackalloc byte[128];
        for (int i = 0; i < 128; i++)
        {
            storage[i] = 0;
        }

        byte* freeItem = storage + sizeof(nuint);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        dynamic_data data = default;
        data.new_allocation = (nint)freeSize;
        gc_alloc_context context = default;
        ulong totalAllocatedBytesSoh = 0;
        generation* gen = gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0);
        gc_heap.thread_free_item_front(gen, freeItem, freeSize);

        Assert.True(gc_heap.a_fit_free_list_p(
            (int)gc_generation_num.soh_gen0,
            size,
            &context,
            0,
            alignment,
            &data,
            0,
            generations,
            &totalAllocatedBytesSoh));

        Assert.Equal((nuint)freeItem, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)(freeItem + (nint)size), (nuint)context.alloc_limit);
        Assert.Equal((nuint)(context.alloc_limit + (nint)pad), (nuint)(freeItem + (nint)freeSize));
        Assert.Equal((long)size, context.alloc_bytes);
        Assert.Equal((ulong)size, totalAllocatedBytesSoh);
        Assert.Equal((nint)0, data.new_allocation);
        Assert.Equal((nuint)0, generation.generation_free_list_space(gen));
        Assert.Equal((nuint)0, (nuint)allocator.alloc_list_head_of(generation.generation_allocator(gen), 0));
        Assert.Equal((nuint)0, (nuint)allocator.alloc_list_tail_of(generation.generation_allocator(gen), 0));
    }

#if TARGET_64BIT && !TARGET_WASM
    [Fact]
    public void AllocatorFrontThreadAndUnlinkPreserveDoublyLinkedMetadata()
    {
        byte* storage = stackalloc byte[256];
        byte* first = storage + 32;
        byte* second = storage + 128;
        nuint freeSize = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size * 3);
        alloc_list bucket = default;
        allocator freeListAllocator = new(
            num_b: 2,
            fbb: 5,
            b: &bucket,
            gen: (int)gc_generation_num.max_generation);
        void* savedFreeObjectMethodTable = GCCommon.g_gc_pFreeObjectMethodTable;
        GCCommon.g_gc_pFreeObjectMethodTable = (void*)0x12345000;

        try
        {
            gc_heap.make_unused_array(first, freeSize);
            gc_heap.make_unused_array(second, freeSize);

            allocator.thread_item_front(&freeListAllocator, first, freeSize);
            allocator.thread_item_front(&freeListAllocator, second, freeSize);

            uint bucketIndex = freeListAllocator.first_suitable_bucket(freeSize);
            Assert.Equal((nuint)second, (nuint)allocator.alloc_list_head_of(&freeListAllocator, bucketIndex));
            Assert.Equal((nuint)first, (nuint)allocator.alloc_list_tail_of(&freeListAllocator, bucketIndex));
            Assert.Equal((nuint)0, (nuint)((byte**)second)[3]);
            Assert.Equal((nuint)second, (nuint)((byte**)first)[3]);

            allocator.unlink_item(&freeListAllocator, bucketIndex, second, null, use_undo_p: false);

            Assert.Equal((nuint)first, (nuint)allocator.alloc_list_head_of(&freeListAllocator, bucketIndex));
            Assert.Equal((nuint)first, (nuint)allocator.alloc_list_tail_of(&freeListAllocator, bucketIndex));
            Assert.Equal((nuint)1, (nuint)((byte**)second)[3]);
        }
        finally
        {
            GCCommon.g_gc_pFreeObjectMethodTable = savedFreeObjectMethodTable;
        }
    }
#endif

    [Fact]
    public void SohFreeListSplitRetainsMinimumRemainder()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        nuint limit = unchecked(size + pad);
        nuint remainderSize = unchecked(2 * pad);
        byte* storage = stackalloc byte[192];
        for (int i = 0; i < 192; i++)
        {
            storage[i] = 0;
        }

        byte* freeItem = storage + sizeof(nuint);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        dynamic_data data = default;
        data.new_allocation = (nint)limit;
        gc_alloc_context context = default;
        ulong totalAllocatedBytesSoh = 0;
        generation* gen = gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0);
        gc_heap.thread_free_item_front(gen, freeItem, unchecked(limit + remainderSize));

        Assert.True(gc_heap.a_fit_free_list_p(
            (int)gc_generation_num.soh_gen0,
            size,
            &context,
            0,
            alignment,
            &data,
            0,
            generations,
            &totalAllocatedBytesSoh));

        byte* remainder = freeItem + (nint)limit;
        Assert.Equal((nuint)remainder, (nuint)allocator.alloc_list_head_of(generation.generation_allocator(gen), 0));
        Assert.Equal((nuint)remainder, (nuint)allocator.alloc_list_tail_of(generation.generation_allocator(gen), 0));
        Assert.Equal(remainderSize, gc_heap.unused_array_size(remainder));
        Assert.Equal(remainderSize, generation.generation_free_list_space(gen));
        Assert.Equal((nuint)(freeItem + (nint)size), (nuint)context.alloc_limit);
        Assert.Equal((long)size, context.alloc_bytes);
        Assert.Equal((ulong)size, totalAllocatedBytesSoh);
    }

    [Fact]
    public void SohFreeListAbsorbsTooSmallRemainder()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        nuint limit = unchecked(size + pad);
        byte* storage = stackalloc byte[160];
        for (int i = 0; i < 160; i++)
        {
            storage[i] = 0;
        }

        byte* freeItem = storage + sizeof(nuint);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        dynamic_data data = default;
        data.new_allocation = (nint)limit;
        gc_alloc_context context = default;
        ulong totalAllocatedBytesSoh = 0;
        generation* gen = gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0);
        gc_heap.thread_free_item_front(gen, freeItem, unchecked(limit + pad));

        Assert.True(gc_heap.a_fit_free_list_p(
            (int)gc_generation_num.soh_gen0,
            size,
            &context,
            0,
            alignment,
            &data,
            0,
            generations,
            &totalAllocatedBytesSoh));

        Assert.Equal((nuint)0, generation.generation_free_list_space(gen));
        Assert.Equal((nuint)0, generation.generation_free_obj_space(gen));
        Assert.Equal((nuint)0, (nuint)allocator.alloc_list_head_of(generation.generation_allocator(gen), 0));
        Assert.Equal((nuint)(freeItem + (nint)(size + pad)), (nuint)context.alloc_limit);
        Assert.Equal((long)(size + pad), context.alloc_bytes);
        Assert.Equal((ulong)(size + pad), totalAllocatedBytesSoh);
        Assert.Equal((nint)0, data.new_allocation);
    }

    [Fact]
    public void SohFreeListTraversesBucketChainAndRemovesMatchedTail()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        byte* storage = stackalloc byte[512];
        for (int i = 0; i < 512; i++)
        {
            storage[i] = 0;
        }

        alloc_list* buckets = stackalloc alloc_list[2];
        for (int i = 0; i < 2; i++)
        {
            buckets[i] = default;
        }

        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        generation* gen = gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0);
        gen->free_list_allocator = new allocator(3, 6, buckets);
        byte* fittingItem = storage + sizeof(nuint);
        byte* tooSmallItem = storage + 256 + sizeof(nuint);
        gc_heap.thread_free_item_front(gen, fittingItem, 192);
        gc_heap.thread_free_item_front(gen, tooSmallItem, 128);

        dynamic_data data = default;
        data.new_allocation = 168;
        gc_alloc_context context = default;
        ulong totalAllocatedBytesSoh = 0;

        Assert.True(gc_heap.a_fit_free_list_p(
            (int)gc_generation_num.soh_gen0,
            144,
            &context,
            0,
            alignment,
            &data,
            0,
            generations,
            &totalAllocatedBytesSoh));

        uint bucket = generation.generation_allocator(gen)->first_suitable_bucket(144);
        Assert.Equal((nuint)tooSmallItem, (nuint)allocator.alloc_list_head_of(generation.generation_allocator(gen), bucket));
        Assert.Equal((nuint)tooSmallItem, (nuint)allocator.alloc_list_tail_of(generation.generation_allocator(gen), bucket));
        Assert.Equal((nuint)0, (nuint)allocator.free_list_slot(tooSmallItem));
        Assert.Equal((nuint)128, generation.generation_free_list_space(gen));
        Assert.Equal((nuint)fittingItem, (nuint)context.alloc_ptr);
        Assert.Equal((nint)0, data.new_allocation);
    }

    [Fact]
    public void UohFreeListFitPreservesUohAccountingAndPadding()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: false);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[192];
        for (int i = 0; i < 192; i++)
        {
            storage[i] = 0;
        }

        byte* freeItem = storage + sizeof(nuint);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        dynamic_data data = default;
        data.new_allocation = (nint)size;
        gc_alloc_context context = default;
        context.alloc_bytes = 10;
        context.alloc_bytes_uoh = 20;
        ulong totalAllocatedBytesUoh = 30;
        generation* gen = gc_heap.generation_of(generations, (int)gc_generation_num.loh_generation);
        gc_heap.thread_free_item_front(gen, freeItem, unchecked(2 * size));

        Assert.True(gc_heap.a_fit_free_list_uoh_p(
            size,
            &context,
            0,
            alignment,
            (int)gc_generation_num.loh_generation,
            &data,
            0,
            generations,
            &totalAllocatedBytesUoh));

        byte* remainder = freeItem + (nint)size;
        Assert.Equal((nuint)freeItem, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)(freeItem + (nint)size), (nuint)context.alloc_limit);
        Assert.Equal((long)size + 10, context.alloc_bytes);
        Assert.Equal(20, context.alloc_bytes_uoh);
        Assert.Equal((ulong)size + 30, totalAllocatedBytesUoh);
        Assert.Equal(size, generation.generation_free_list_allocated(gen));
        Assert.Equal(size, generation.generation_free_list_space(gen));
        Assert.Equal((nuint)remainder, (nuint)allocator.alloc_list_head_of(generation.generation_allocator(gen), 0));
        Assert.Equal(size, gc_heap.unused_array_size(remainder));
        Assert.Equal((nint)0, data.new_allocation);
    }

    [Fact]
    public void SohTryFitUsesFreeListBeforeSegmentEnd()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[256];
        for (int i = 0; i < 256; i++)
        {
            storage[i] = 0;
        }

        heap_segment segment = default;
        byte* segmentStart = storage + 160;
        heap_segment.heap_segment_mem(&segment) = segmentStart;
        heap_segment.heap_segment_allocated(&segment) = segmentStart;
        heap_segment.heap_segment_used(&segment) = segmentStart;
        heap_segment.heap_segment_committed(&segment) = segmentStart + (nint)pad;
        heap_segment.heap_segment_reserved(&segment) = heap_segment.heap_segment_committed(&segment);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        generation* gen0 = gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0);
        byte* freeItem = storage + sizeof(nuint);
        gc_heap.thread_free_item_front(gen0, freeItem, unchecked(size + pad));
        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(size + pad));
        gc_alloc_context context = default;
        heap_segment* ephemeral = &segment;
        byte* allocAllocated = segmentStart;
        ulong totalAllocatedBytesSoh = 0;
        bool commitFailed = false;
        bool shortSegmentEnd = true;

        Assert.True(gc_heap.soh_try_fit(
            (int)gc_generation_num.soh_gen0,
            size,
            &context,
            0,
            alignment,
            &commitFailed,
            &shortSegmentEnd,
            sufficient_space_regions_for_allocation_p: false,
            sufficient_gen0_space_p: false,
            &data,
            0,
            generations,
            &ephemeral,
            &allocAllocated,
            &totalAllocatedBytesSoh,
            0,
            null));

        Assert.False(commitFailed);
        Assert.False(shortSegmentEnd);
        Assert.Equal((nuint)freeItem, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)(freeItem + (nint)size), (nuint)context.alloc_limit);
        Assert.Equal((nuint)segmentStart, (nuint)allocAllocated);
        Assert.Equal((nuint)0, (nuint)allocator.alloc_list_head_of(generation.generation_allocator(gen0), 0));
        Assert.Equal((nint)0, data.new_allocation);
    }

    [Fact]
    public void SohTryFitFallsBackToSegmentEnd()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[256];
        byte* start = storage + 32;
        heap_segment segment = default;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = start + (nint)(size + (2 * pad));
        heap_segment.heap_segment_reserved(&segment) = heap_segment.heap_segment_committed(&segment);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        generation.generation_tail_region(gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0)) = &segment;
        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(size + pad));
        gc_alloc_context context = default;
        heap_segment* ephemeral = &segment;
        byte* allocAllocated = start;
        ulong totalAllocatedBytesSoh = 0;
        bool commitFailed = true;
        bool shortSegmentEnd = true;

        Assert.True(gc_heap.soh_try_fit(
            (int)gc_generation_num.soh_gen0,
            size,
            &context,
            0,
            alignment,
            &commitFailed,
            &shortSegmentEnd,
            sufficient_space_regions_for_allocation_p: true,
            sufficient_gen0_space_p: false,
            &data,
            0,
            generations,
            &ephemeral,
            &allocAllocated,
            &totalAllocatedBytesSoh,
            0,
            null));

        Assert.False(commitFailed);
        Assert.False(shortSegmentEnd);
        Assert.Equal((nuint)(start + (nint)(size + pad)), (nuint)allocAllocated);
        Assert.Equal((nuint)start, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)(start + (nint)size), (nuint)context.alloc_limit);
        Assert.Equal((nint)0, data.new_allocation);
        Assert.Equal((ulong)size, totalAllocatedBytesSoh);
    }

    [Fact]
    public void SohTryFitSuppressesShortEndWithoutChangingAllocationState()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        byte* storage = stackalloc byte[128];
        byte* start = storage + 32;
        heap_segment segment = default;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = start + (nint)(2 * pad);
        heap_segment.heap_segment_reserved(&segment) = heap_segment.heap_segment_committed(&segment);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(3 * pad));
        gc_alloc_context context = default;
        heap_segment* ephemeral = &segment;
        byte* allocAllocated = start;
        ulong totalAllocatedBytesSoh = 17;
        bool commitFailed = false;
        bool shortSegmentEnd = false;

        Assert.False(gc_heap.soh_try_fit(
            (int)gc_generation_num.soh_gen0,
            unchecked(2 * pad),
            &context,
            0,
            alignment,
            &commitFailed,
            &shortSegmentEnd,
            sufficient_space_regions_for_allocation_p: false,
            sufficient_gen0_space_p: false,
            &data,
            0,
            generations,
            &ephemeral,
            &allocAllocated,
            &totalAllocatedBytesSoh,
            0,
            null));

        Assert.False(commitFailed);
        Assert.True(shortSegmentEnd);
        Assert.Equal((nuint)(&segment), (nuint)ephemeral);
        Assert.Equal((nuint)start, (nuint)allocAllocated);
        Assert.Equal((nuint)start, (nuint)heap_segment.heap_segment_allocated(&segment));
        Assert.Equal((nuint)0, (nuint)context.alloc_ptr);
        Assert.Equal((nint)(3 * pad), data.new_allocation);
        Assert.Equal(17ul, totalAllocatedBytesSoh);
    }

    [Fact]
    public void SohTryFitRollsToNextRegionAndFixesAllocationContext()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[512];
        heap_segment* segments = stackalloc heap_segment[2];
        segments[0] = default;
        segments[1] = default;
        byte* firstStart = storage + 32;
        byte* secondStart = storage + 256;
        heap_segment.heap_segment_mem(&segments[0]) = firstStart;
        heap_segment.heap_segment_allocated(&segments[0]) = firstStart;
        heap_segment.heap_segment_used(&segments[0]) = firstStart;
        heap_segment.heap_segment_committed(&segments[0]) = firstStart + (nint)(6 * pad);
        heap_segment.heap_segment_reserved(&segments[0]) = heap_segment.heap_segment_committed(&segments[0]);
        heap_segment.heap_segment_next(&segments[0]) = &segments[1];
        heap_segment.heap_segment_mem(&segments[1]) = secondStart;
        heap_segment.heap_segment_allocated(&segments[1]) = secondStart;
        heap_segment.heap_segment_used(&segments[1]) = secondStart;
        heap_segment.heap_segment_committed(&segments[1]) = secondStart + (nint)(size + (2 * pad));
        heap_segment.heap_segment_reserved(&segments[1]) = heap_segment.heap_segment_committed(&segments[1]);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        generation.generation_tail_region(gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0)) = &segments[1];
        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(size + pad));
        gc_alloc_context context = default;
        context.alloc_ptr = firstStart + (nint)pad;
        context.alloc_limit = firstStart + (nint)(2 * pad);
        context.alloc_bytes = 100;
        heap_segment* ephemeral = &segments[0];
        byte* allocAllocated = firstStart + (nint)(3 * pad);
        ulong totalAllocatedBytesSoh = 200;
        bool commitFailed = false;
        bool shortSegmentEnd = true;

        Assert.True(gc_heap.soh_try_fit(
            (int)gc_generation_num.soh_gen0,
            size,
            &context,
            0,
            alignment,
            &commitFailed,
            &shortSegmentEnd,
            sufficient_space_regions_for_allocation_p: true,
            sufficient_gen0_space_p: false,
            &data,
            0,
            generations,
            &ephemeral,
            &allocAllocated,
            &totalAllocatedBytesSoh,
            0,
            null));

        Assert.False(commitFailed);
        Assert.False(shortSegmentEnd);
        Assert.Equal((nuint)(&segments[1]), (nuint)ephemeral);
        Assert.Equal((nuint)(firstStart + (nint)pad), (nuint)heap_segment.heap_segment_allocated(&segments[0]));
        Assert.Equal((nuint)(secondStart + (nint)(size + pad)), (nuint)allocAllocated);
        Assert.Equal((nuint)secondStart, (nuint)context.alloc_ptr);
        Assert.Equal((nuint)(secondStart + (nint)size), (nuint)context.alloc_limit);
        Assert.Equal(100 - (long)pad + (long)size, context.alloc_bytes);
        Assert.Equal((ulong)((nuint)200 - pad + size), totalAllocatedBytesSoh);
        Assert.Equal((nint)0, data.new_allocation);
    }

    [Fact]
    public void UohTryFitPropagatesCommitFailureAsOomWithoutChangingState()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: true);
        int alignment = gc_heap.get_alignment_constant(small_object_p: false);
        nuint pageSize = GCToOSInterface.GetPageSize();
        byte* start = (byte*)0x2000;
        heap_segment segment = default;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = start;
        heap_segment.heap_segment_reserved(&segment) = start + (nint)(2 * pageSize);
        gc_heap.heap_hard_limit = pageSize - 1;
        gc_heap.heap_hard_limit_oh = default;
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        generation* loh = gc_heap.generation_of(generations, (int)gc_generation_num.loh_generation);
        generation.generation_allocation_segment(loh) = &segment;
        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(2 * pageSize));
        gc_alloc_context context = default;
        context.alloc_bytes = 17;
        ulong totalAllocatedBytesUoh = 19;
        bool commitFailed = false;
        oom_reason oomReason = oom_reason.oom_no_failure;

        Assert.False(gc_heap.uoh_try_fit(
            (int)gc_generation_num.loh_generation,
            pageSize,
            &context,
            0,
            alignment,
            &commitFailed,
            &oomReason,
            &data,
            0,
            generations,
            null,
            null,
            &totalAllocatedBytesUoh,
            0));

        Assert.True(commitFailed);
        Assert.Equal(oom_reason.oom_cant_commit, oomReason);
        Assert.Equal((nuint)start, (nuint)heap_segment.heap_segment_allocated(&segment));
        Assert.Equal((nuint)start, (nuint)heap_segment.heap_segment_used(&segment));
        Assert.Equal((nuint)start, (nuint)heap_segment.heap_segment_committed(&segment));
        Assert.Equal((nuint)0, (nuint)context.alloc_ptr);
        Assert.Equal(17, context.alloc_bytes);
        Assert.Equal((ulong)19, totalAllocatedBytesUoh);
        Assert.Equal((nint)(2 * pageSize), data.new_allocation);
    }

    [Fact]
    public void FreeListFitFailureLeavesSohAndUohStateUnchanged()
    {
        int sohAlignment = gc_heap.get_alignment_constant(small_object_p: true);
        int uohAlignment = gc_heap.get_alignment_constant(small_object_p: false);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, sohAlignment);
        byte* storage = stackalloc byte[512];
        for (int i = 0; i < 512; i++)
        {
            storage[i] = 0;
        }

        alloc_list* buckets = stackalloc alloc_list[1];
        buckets[0] = default;
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        generation* sohGen = gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0);
        sohGen->free_list_allocator = new allocator(2, (sizeof(nuint) * 8) - 1, buckets);
        byte* sohFreeItem = storage + sizeof(nuint);
        gc_heap.thread_free_item_front(sohGen, sohFreeItem, unchecked(2 * pad));
        dynamic_data sohData = default;
        sohData.new_allocation = (nint)(2 * pad);
        gc_alloc_context sohContext = default;
        ulong totalAllocatedBytesSoh = 17;

        Assert.False(gc_heap.a_fit_free_list_p(
            (int)gc_generation_num.soh_gen0,
            unchecked(2 * pad),
            &sohContext,
            0,
            sohAlignment,
            &sohData,
            0,
            generations,
            &totalAllocatedBytesSoh));

        Assert.Equal((nuint)sohFreeItem, (nuint)allocator.alloc_list_head_of(generation.generation_allocator(sohGen), 0));
        Assert.Equal((nuint)sohFreeItem, (nuint)allocator.alloc_list_tail_of(generation.generation_allocator(sohGen), 0));
        Assert.Equal(unchecked(2 * pad), generation.generation_free_list_space(sohGen));
        Assert.Equal((nint)(2 * pad), sohData.new_allocation);
        Assert.Equal((nuint)0, (nuint)sohContext.alloc_ptr);
        Assert.Equal((ulong)17, totalAllocatedBytesSoh);

        generation* uohGen = gc_heap.generation_of(generations, (int)gc_generation_num.loh_generation);
        byte* uohFreeItem = storage + 256 + sizeof(nuint);
        gc_heap.thread_free_item_front(uohGen, uohFreeItem, unchecked(2 * pad));
        dynamic_data uohData = default;
        uohData.new_allocation = (nint)(3 * pad);
        gc_alloc_context uohContext = default;
        ulong totalAllocatedBytesUoh = 19;

        Assert.False(gc_heap.a_fit_free_list_uoh_p(
            unchecked(3 * pad),
            &uohContext,
            0,
            uohAlignment,
            (int)gc_generation_num.loh_generation,
            &uohData,
            0,
            generations,
            &totalAllocatedBytesUoh));

        Assert.Equal((nuint)uohFreeItem, (nuint)allocator.alloc_list_head_of(generation.generation_allocator(uohGen), 0));
        Assert.Equal((nuint)uohFreeItem, (nuint)allocator.alloc_list_tail_of(generation.generation_allocator(uohGen), 0));
        Assert.Equal(unchecked(2 * pad), generation.generation_free_list_space(uohGen));
        Assert.Equal((nint)(3 * pad), uohData.new_allocation);
        Assert.Equal((nuint)0, (nuint)uohContext.alloc_ptr);
        Assert.Equal((ulong)19, totalAllocatedBytesUoh);
    }

    [Fact]
    public void ResetAllocationPointersSelectsTheFirstWritableRegionAndChecksHalfOpenBounds()
    {
        heap_segment readOnly = default;
        heap_segment writable = default;
        generation gen = default;

        InitializeRegion(&readOnly, 0x1000, 0x1800, 0x2000, age: 0);
        InitializeRegion(&writable, 0x2000, 0x2800, 0x3000, age: 0);
        readOnly.flags = heap_segment.heap_segment_flags_readonly;
        readOnly.next = &writable;
        gen.start_segment = &readOnly;
        gen.allocation_context.alloc_ptr = (byte*)0x2500;
        gen.allocation_context.alloc_limit = (byte*)0x2800;

        gc_heap.reset_allocation_pointers(&gen, (byte*)0x2000);

        Assert.Equal((nuint)0, (nuint)generation.generation_allocation_pointer(&gen));
        Assert.Equal((nuint)0, (nuint)generation.generation_allocation_limit(&gen));
        Assert.Equal((nuint)(&writable), (nuint)generation.generation_allocation_segment(&gen));
        Assert.Equal(1, gc_heap.in_range_for_segment(heap_segment.heap_segment_mem(&writable), &writable));
        Assert.Equal(1, gc_heap.in_range_for_segment(heap_segment.heap_segment_reserved(&writable) - 1, &writable));
        Assert.Equal(0, gc_heap.in_range_for_segment(heap_segment.heap_segment_reserved(&writable), &writable));
    }

    [Fact]
    public void TryAllocateMoreSpaceInitialSohFreeListFitReachesCanAllocate()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[128];
        for (int i = 0; i < 128; i++)
        {
            storage[i] = 0;
        }

        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        void* savedFreeObjectMethodTable = GCCommon.g_gc_pFreeObjectMethodTable;
        GCCommon.g_gc_pFreeObjectMethodTable = (void*)0x12345000;

        try
        {
            byte* freeItem = storage + sizeof(nuint);
            gc_heap.thread_free_item_front(
                gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0),
                freeItem,
                unchecked(size + pad));

            gc_alloc_context allocContext = default;
            dynamic_data data = default;
            data.new_allocation = unchecked((nint)(size + pad));
            heap_segment segment = default;
            heap_segment* ephemeralHeapSegment = &segment;
            byte* allocAllocated = null;
            ulong totalAllocatedBytesSoh = 0;
            ulong totalAllocatedBytesUoh = 0;
            try_allocate_more_space_context allocation = default;
            allocation.acontext = &allocContext;
            allocation.dd = &data;
            allocation.generation_table = generations;
            allocation.ephemeral_heap_segment = &ephemeralHeapSegment;
            allocation.alloc_allocated = &allocAllocated;
            allocation.total_alloc_bytes_soh = &totalAllocatedBytesSoh;
            allocation.total_alloc_bytes_uoh = &totalAllocatedBytesUoh;
            allocation.size = size;
            allocation.gen_number = (int)gc_generation_num.soh_gen0;
            allocation.align_const = alignment;
            allocation.state = allocation_state.a_state_start;
            allocation.more_space_lock_held_p = 1;
            allocation.budget_checked_p = 1;

            Assert.Equal(allocation_state.a_state_can_allocate, gc_heap.try_allocate_more_space(&allocation));
            Assert.Equal(allocation_deferred_operation.none, allocation.deferred_operation);
            Assert.Equal((nuint)freeItem, (nuint)allocContext.alloc_ptr);
            Assert.Equal((nuint)(freeItem + (nint)size), (nuint)allocContext.alloc_limit);
            Assert.Equal((nint)0, data.new_allocation);
            Assert.Equal((ulong)size, totalAllocatedBytesSoh);
            Assert.Equal((ulong)0, totalAllocatedBytesUoh);
        }
        finally
        {
            GCCommon.g_gc_pFreeObjectMethodTable = savedFreeObjectMethodTable;
        }
    }

    [Fact]
    public void TryAllocateMoreSpaceUohCommitFailureDefersFullCompactGcWithoutMutatingAllocation()
    {
        using RegionSegmentsStateScope _ = new(initializeCommitLock: true);
        int alignment = gc_heap.get_alignment_constant(small_object_p: false);
        nuint pageSize = GCToOSInterface.GetPageSize();
        byte* start = (byte*)0x2000;
        heap_segment segment = default;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = start;
        heap_segment.heap_segment_reserved(&segment) = start + (nint)(2 * pageSize);
        gc_heap.heap_hard_limit = pageSize - 1;
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        generation.generation_allocation_segment(
            gc_heap.generation_of(generations, (int)gc_generation_num.loh_generation)) = &segment;
        gc_alloc_context allocContext = default;
        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(2 * pageSize));
        heap_segment* ephemeralHeapSegment = null;
        byte* allocAllocated = null;
        ulong totalAllocatedBytesSoh = 17;
        ulong totalAllocatedBytesUoh = 19;
        try_allocate_more_space_context allocation = default;
        allocation.acontext = &allocContext;
        allocation.dd = &data;
        allocation.generation_table = generations;
        allocation.ephemeral_heap_segment = &ephemeralHeapSegment;
        allocation.alloc_allocated = &allocAllocated;
        allocation.total_alloc_bytes_soh = &totalAllocatedBytesSoh;
        allocation.total_alloc_bytes_uoh = &totalAllocatedBytesUoh;
        allocation.size = pageSize;
        allocation.gen_number = (int)gc_generation_num.loh_generation;
        allocation.align_const = alignment;
        allocation.state = allocation_state.a_state_start;
        allocation.more_space_lock_held_p = 1;
        allocation.budget_checked_p = 1;

        Assert.Equal(allocation_state.a_state_trigger_full_compact_gc, gc_heap.try_allocate_more_space(&allocation));
        Assert.Equal(allocation_deferred_operation.trigger_full_compact_gc, allocation.deferred_operation);
        Assert.Equal(oom_reason.oom_cant_commit, allocation.oom_r);
        Assert.Equal((nuint)start, (nuint)heap_segment.heap_segment_allocated(&segment));
        Assert.Equal((nuint)start, (nuint)heap_segment.heap_segment_used(&segment));
        Assert.Equal((nuint)start, (nuint)heap_segment.heap_segment_committed(&segment));
        Assert.Equal((nuint)0, (nuint)allocContext.alloc_ptr);
        Assert.Equal((nint)(2 * pageSize), data.new_allocation);
        Assert.Equal((ulong)17, totalAllocatedBytesSoh);
        Assert.Equal((ulong)19, totalAllocatedBytesUoh);
    }

    [Fact]
    public void TryAllocateMoreSpaceSohShortEndAfterBgcDefersSecondEphemeralGc()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        heap_segment segment = default;
        byte* start = (byte*)0x2000;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = start + (nint)(2 * pad);
        heap_segment.heap_segment_reserved(&segment) = heap_segment.heap_segment_committed(&segment);
        gc_alloc_context allocContext = default;
        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(3 * pad));
        heap_segment* ephemeralHeapSegment = &segment;
        byte* allocAllocated = start;
        ulong totalAllocatedBytesSoh = 23;
        ulong totalAllocatedBytesUoh = 0;
        try_allocate_more_space_context allocation = default;
        allocation.acontext = &allocContext;
        allocation.dd = &data;
        allocation.generation_table = generations;
        allocation.ephemeral_heap_segment = &ephemeralHeapSegment;
        allocation.alloc_allocated = &allocAllocated;
        allocation.total_alloc_bytes_soh = &totalAllocatedBytesSoh;
        allocation.total_alloc_bytes_uoh = &totalAllocatedBytesUoh;
        allocation.size = unchecked(2 * pad);
        allocation.gen_number = (int)gc_generation_num.soh_gen0;
        allocation.align_const = alignment;
        allocation.state = allocation_state.a_state_try_fit_after_bgc;
        allocation.more_space_lock_held_p = 1;
        allocation.budget_checked_p = 1;
        allocation.sufficient_space_regions_for_allocation_p = 0;
        allocation.sufficient_gen0_space_p = 0;

        Assert.Equal(allocation_state.a_state_trigger_2nd_ephemeral_gc, gc_heap.try_allocate_more_space(&allocation));
        Assert.Equal(allocation_deferred_operation.trigger_2nd_ephemeral_gc, allocation.deferred_operation);
        Assert.Equal((byte)1, allocation.short_seg_end_p);
        Assert.Equal((byte)0, allocation.commit_failed_p);
        Assert.Equal((nuint)start, (nuint)allocAllocated);
        Assert.Equal((nuint)0, (nuint)allocContext.alloc_ptr);
        Assert.Equal((nint)(3 * pad), data.new_allocation);
        Assert.Equal((ulong)23, totalAllocatedBytesSoh);
    }

    [Fact]
    public void TryAllocateMoreSpaceUohOomRunsExplicitCallbacksAndPreservesAllocationState()
    {
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        gc_alloc_context allocContext = default;
        dynamic_data data = default;
        data.new_allocation = 64;
        heap_segment* ephemeralHeapSegment = null;
        byte* allocAllocated = null;
        ulong totalAllocatedBytesSoh = 29;
        ulong totalAllocatedBytesUoh = 31;
        try_allocate_more_space_context allocation = default;
        allocation.acontext = &allocContext;
        allocation.dd = &data;
        allocation.generation_table = generations;
        allocation.ephemeral_heap_segment = &ephemeralHeapSegment;
        allocation.alloc_allocated = &allocAllocated;
        allocation.total_alloc_bytes_soh = &totalAllocatedBytesSoh;
        allocation.total_alloc_bytes_uoh = &totalAllocatedBytesUoh;
        allocation.size = 32;
        allocation.gen_number = (int)gc_generation_num.loh_generation;
        allocation.align_const = gc_heap.get_alignment_constant(small_object_p: false);
        allocation.state = allocation_state.a_state_try_fit_after_cg;
        allocation.more_space_lock_held_p = 1;
        allocation.budget_checked_p = 1;
        ResetAllocationCallbackRecorder();
        delegate* unmanaged<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback =
            &UohOomCallback;

        Assert.Equal(allocation_state.a_state_cant_allocate, gc_heap.try_allocate_more_space(&allocation, callback));
        Assert.Equal(oom_reason.oom_loh, allocation.oom_r);
        Assert.Equal(allocation_deferred_operation.none, allocation.deferred_operation);
        Assert.Equal((byte)1, allocation.oom_handled_p);
        Assert.Equal((byte)0, allocation.more_space_lock_held_p);
        Assert.Equal(5, s_allocationCallbackCount);
        Assert.Equal(allocation_deferred_operation.leave_more_space_lock, s_lastAllocationDeferredOperation);
        Assert.Equal((nuint)0, (nuint)allocContext.alloc_ptr);
        Assert.Equal((nint)64, data.new_allocation);
        Assert.Equal((ulong)29, totalAllocatedBytesSoh);
        Assert.Equal((ulong)31, totalAllocatedBytesUoh);
    }

    [Fact]
    public void TryAllocateMoreSpacePreservesRetryStatesForGcAndOtherHeap()
    {
        try_allocate_more_space_context gcStarted = default;
        gcStarted.state = allocation_state.a_state_start;
        gcStarted.gc_started_p = 1;

        Assert.Equal(allocation_state.a_state_retry_allocate, gc_heap.try_allocate_more_space(&gcStarted));
        Assert.Equal(allocation_deferred_operation.wait_for_gc_done, gcStarted.deferred_operation);

        try_allocate_more_space_context otherHeap = default;
        otherHeap.state = allocation_state.a_state_cant_allocate;
        otherHeap.gen_number = (int)gc_generation_num.loh_generation;
        otherHeap.oom_r = oom_reason.oom_loh;
        otherHeap.more_space_lock_held_p = 1;
        ResetAllocationCallbackRecorder();
        delegate* unmanaged<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback =
            &RetryOtherHeapCallback;

        Assert.Equal(allocation_state.a_state_retry_allocate, gc_heap.try_allocate_more_space(&otherHeap, callback));
        Assert.Equal(allocation_deferred_operation.none, otherHeap.deferred_operation);
        Assert.Equal((byte)0, otherHeap.more_space_lock_held_p);
        Assert.Equal(2, s_allocationCallbackCount);
        Assert.Equal(allocation_deferred_operation.leave_more_space_lock, s_lastAllocationDeferredOperation);
    }

    [Fact]
    public void TryAllocateMoreSpaceDefersAndResumesEphemeralGcAtTheNativeState()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        nuint size = unchecked(2 * pad);
        byte* storage = stackalloc byte[128];
        for (int i = 0; i < 128; i++)
        {
            storage[i] = 0;
        }

        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        void* savedFreeObjectMethodTable = GCCommon.g_gc_pFreeObjectMethodTable;
        GCCommon.g_gc_pFreeObjectMethodTable = (void*)0x12345000;

        try
        {
            byte* freeItem = storage + sizeof(nuint);
            gc_heap.thread_free_item_front(
                gc_heap.generation_of(generations, (int)gc_generation_num.soh_gen0),
                freeItem,
                unchecked(size + pad));

            gc_alloc_context allocContext = default;
            dynamic_data data = default;
            data.new_allocation = unchecked((nint)(size + pad));
            heap_segment* ephemeralHeapSegment = null;
            byte* allocAllocated = null;
            ulong totalAllocatedBytesSoh = 0;
            ulong totalAllocatedBytesUoh = 0;
            try_allocate_more_space_context allocation = default;
            allocation.acontext = &allocContext;
            allocation.dd = &data;
            allocation.generation_table = generations;
            allocation.ephemeral_heap_segment = &ephemeralHeapSegment;
            allocation.alloc_allocated = &allocAllocated;
            allocation.total_alloc_bytes_soh = &totalAllocatedBytesSoh;
            allocation.total_alloc_bytes_uoh = &totalAllocatedBytesUoh;
            allocation.size = size;
            allocation.gen_number = (int)gc_generation_num.soh_gen0;
            allocation.align_const = alignment;
            allocation.state = allocation_state.a_state_trigger_ephemeral_gc;
            allocation.more_space_lock_held_p = 1;
            allocation.budget_checked_p = 1;

            Assert.Equal(allocation_state.a_state_trigger_ephemeral_gc, gc_heap.try_allocate_more_space(&allocation));
            Assert.Equal(allocation_deferred_operation.trigger_ephemeral_gc, allocation.deferred_operation);

            ResetAllocationCallbackRecorder();
            delegate* unmanaged<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback =
                &NoFullEphemeralGcCallback;
            Assert.Equal(allocation_state.a_state_can_allocate, gc_heap.try_allocate_more_space(&allocation, callback));
            Assert.Equal(1, s_allocationCallbackCount);
            Assert.Equal(allocation_deferred_operation.trigger_ephemeral_gc, s_lastAllocationDeferredOperation);
            Assert.Equal((nuint)freeItem, (nuint)allocContext.alloc_ptr);
            Assert.Equal((nint)0, data.new_allocation);
        }
        finally
        {
            GCCommon.g_gc_pFreeObjectMethodTable = savedFreeObjectMethodTable;
        }
    }

    [Fact]
    public void TryAllocateMoreSpaceUsesDistinctBackgroundQueryOperation()
    {
        int alignment = gc_heap.get_alignment_constant(small_object_p: true);
        nuint pad = gc_heap.Align((nuint)GCInterfaceOffsets.min_obj_size, alignment);
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        heap_segment segment = default;
        byte* start = (byte*)0x2000;
        heap_segment.heap_segment_mem(&segment) = start;
        heap_segment.heap_segment_allocated(&segment) = start;
        heap_segment.heap_segment_used(&segment) = start;
        heap_segment.heap_segment_committed(&segment) = start + (nint)(2 * pad);
        heap_segment.heap_segment_reserved(&segment) = heap_segment.heap_segment_committed(&segment);
        gc_alloc_context allocContext = default;
        dynamic_data data = default;
        data.new_allocation = unchecked((nint)(3 * pad));
        heap_segment* ephemeralHeapSegment = &segment;
        byte* allocAllocated = start;
        ulong totalAllocatedBytesSoh = 0;
        ulong totalAllocatedBytesUoh = 0;
        try_allocate_more_space_context allocation = default;
        allocation.acontext = &allocContext;
        allocation.dd = &data;
        allocation.generation_table = generations;
        allocation.ephemeral_heap_segment = &ephemeralHeapSegment;
        allocation.alloc_allocated = &allocAllocated;
        allocation.total_alloc_bytes_soh = &totalAllocatedBytesSoh;
        allocation.total_alloc_bytes_uoh = &totalAllocatedBytesUoh;
        allocation.size = unchecked(2 * pad);
        allocation.state = allocation_state.a_state_trigger_ephemeral_gc;
        allocation.gen_number = (int)gc_generation_num.soh_gen0;
        allocation.align_const = alignment;
        allocation.more_space_lock_held_p = 1;
        allocation.budget_checked_p = 1;
        allocation.sufficient_space_regions_for_allocation_p = 0;
        allocation.sufficient_gen0_space_p = 0;
        ResetAllocationCallbackRecorder();
        delegate* unmanaged<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback =
            &BackgroundQueryCallback;

        Assert.Equal(allocation_state.a_state_trigger_full_compact_gc, gc_heap.try_allocate_more_space(&allocation, callback));
        Assert.Equal(allocation_deferred_operation.trigger_full_compact_gc, allocation.deferred_operation);
        Assert.Equal(1, s_backgroundQueryCallbackCount);
    }

    [Fact]
    public void TryAllocateMoreSpaceOverwritesStaleOomReasonAfterUnproductiveFullGc()
    {
        try_allocate_more_space_context allocation = default;
        allocation.state = allocation_state.a_state_trigger_full_compact_gc;
        allocation.gen_number = (int)gc_generation_num.loh_generation;
        allocation.oom_r = oom_reason.oom_cant_commit;
        allocation.more_space_lock_held_p = 1;
        allocation.budget_checked_p = 1;
        ResetAllocationCallbackRecorder();
        delegate* unmanaged<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback =
            &UnproductiveFullGcCallback;

        Assert.Equal(allocation_state.a_state_cant_allocate, gc_heap.try_allocate_more_space(&allocation, callback));
        Assert.Equal(oom_reason.oom_unproductive_full_gc, allocation.oom_r);
        Assert.Equal((byte)1, allocation.oom_handled_p);
        Assert.Equal((byte)0, allocation.more_space_lock_held_p);
        Assert.Equal(4, s_allocationCallbackCount);
    }

    [Fact]
    public void TryAllocateMoreSpaceRechecksBudgetOnceAfterHighMemoryWait()
    {
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        gc_alloc_context allocContext = default;
        dynamic_data data = default;
        heap_segment* ephemeralHeapSegment = null;
        byte* allocAllocated = null;
        ulong totalAllocatedBytesSoh = 0;
        ulong totalAllocatedBytesUoh = 0;
        try_allocate_more_space_context allocation = default;
        allocation.acontext = &allocContext;
        allocation.dd = &data;
        allocation.generation_table = generations;
        allocation.ephemeral_heap_segment = &ephemeralHeapSegment;
        allocation.alloc_allocated = &allocAllocated;
        allocation.total_alloc_bytes_soh = &totalAllocatedBytesSoh;
        allocation.total_alloc_bytes_uoh = &totalAllocatedBytesUoh;
        allocation.gen_number = (int)gc_generation_num.soh_gen0;
        allocation.align_const = gc_heap.get_alignment_constant(small_object_p: true);
        allocation.full_gc_notification_p = 1;
        allocation.state = allocation_state.a_state_start;
        ResetAllocationCallbackRecorder();
        delegate* unmanaged<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback =
            &BudgetRecheckCallback;

        Assert.Equal(allocation_state.a_state_trigger_ephemeral_gc, gc_heap.try_allocate_more_space(&allocation, callback));
        Assert.Equal(allocation_deferred_operation.trigger_ephemeral_gc, allocation.deferred_operation);
        Assert.Equal(2, s_budgetCheckCallbackCount);
        Assert.Equal(1, s_highMemoryCallbackCount);
        Assert.Equal(1, s_budgetTriggerCallbackCount);
        Assert.Equal(2, s_fullGcCheckCallbackCount);
        Assert.Equal((byte)1, allocation.bgc_high_memory_waited_p);
        Assert.Equal((byte)1, allocation.budget_full_gc_checked_p);
        Assert.Equal((byte)1, allocation.budget_checked_p);
    }

    [Fact]
    public void TryAllocateMoreSpaceRetryReleasesMoreSpaceLockOwnership()
    {
        try_allocate_more_space_context allocation = default;
        allocation.gen_number = (int)gc_generation_num.soh_gen0;
        allocation.state = allocation_state.a_state_start;
        allocation.more_space_lock_held_p = 1;
        allocation.full_gc_checked_p = 1;
        allocation.bgc_high_memory_waited_p = 1;
        ResetAllocationCallbackRecorder();
        delegate* unmanaged<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback =
            &BudgetRetryCallback;

        Assert.Equal(allocation_state.a_state_retry_allocate, gc_heap.try_allocate_more_space(&allocation, callback));
        Assert.Equal(allocation_deferred_operation.none, allocation.deferred_operation);
        Assert.Equal((byte)0, allocation.more_space_lock_held_p);
        Assert.Equal(2, s_allocationCallbackCount);
        Assert.Equal(allocation_deferred_operation.trigger_gc_for_budget, s_lastAllocationDeferredOperation);
    }

    [Fact]
    public void TryAllocateMoreSpaceTreatsNoBackgroundGcAsCompletedSohWait()
    {
        generation* generations = stackalloc generation[(int)gc_generation_num.total_generation_count];
        for (int i = 0; i < (int)gc_generation_num.total_generation_count; i++)
        {
            generations[i] = default;
            generation.initialize(&generations[i]);
        }

        gc_alloc_context allocContext = default;
        dynamic_data data = default;
        heap_segment* ephemeralHeapSegment = null;
        byte* allocAllocated = null;
        ulong totalAllocatedBytesSoh = 0;
        try_allocate_more_space_context allocation = default;
        allocation.acontext = &allocContext;
        allocation.dd = &data;
        allocation.generation_table = generations;
        allocation.ephemeral_heap_segment = &ephemeralHeapSegment;
        allocation.alloc_allocated = &allocAllocated;
        allocation.total_alloc_bytes_soh = &totalAllocatedBytesSoh;
        allocation.gen_number = (int)gc_generation_num.soh_gen0;
        allocation.align_const = gc_heap.get_alignment_constant(small_object_p: true);
        allocation.state = allocation_state.a_state_check_and_wait_for_bgc;
        allocation.more_space_lock_held_p = 1;
        allocation.budget_checked_p = 1;
        ResetAllocationCallbackRecorder();
        delegate* unmanaged<try_allocate_more_space_context*, int, allocation_callback_result*, void> callback =
            &NoBackgroundGcWaitCallback;

        Assert.Equal(allocation_state.a_state_trigger_2nd_ephemeral_gc, gc_heap.try_allocate_more_space(&allocation, callback));
        Assert.Equal(allocation_deferred_operation.trigger_2nd_ephemeral_gc, allocation.deferred_operation);
    }

    private static void ResetAllocationCallbackRecorder()
    {
        s_allocationCallbackCount = 0;
        s_lastAllocationDeferredOperation = allocation_deferred_operation.none;
        s_backgroundQueryCallbackCount = 0;
        s_budgetCheckCallbackCount = 0;
        s_highMemoryCallbackCount = 0;
        s_budgetTriggerCallbackCount = 0;
        s_fullGcCheckCallbackCount = 0;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void UohOomCallback(
        try_allocate_more_space_context* context,
        int operationValue,
        allocation_callback_result* result)
    {
        _ = context;
        allocation_deferred_operation operation = (allocation_deferred_operation)operationValue;
        s_allocationCallbackCount++;
        s_lastAllocationDeferredOperation = operation;

        *result = operation switch
        {
            allocation_deferred_operation.acquire_uoh_segment => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.segment_unavailable,
                oom_r = oom_reason.oom_loh,
            },
            allocation_deferred_operation.check_retry_uoh_segment => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            },
            allocation_deferred_operation.check_retry_other_heap => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            },
            allocation_deferred_operation.handle_oom => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            },
            allocation_deferred_operation.leave_more_space_lock => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            },
            _ => default,
        };
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void RetryOtherHeapCallback(
        try_allocate_more_space_context* context,
        int operationValue,
        allocation_callback_result* result)
    {
        _ = context;
        allocation_deferred_operation operation = (allocation_deferred_operation)operationValue;
        s_allocationCallbackCount++;
        s_lastAllocationDeferredOperation = operation;

        *result = operation switch
        {
            allocation_deferred_operation.check_retry_other_heap => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.retry_other_heap,
            },
            allocation_deferred_operation.leave_more_space_lock => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            },
            _ => default,
        };
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void NoFullEphemeralGcCallback(
        try_allocate_more_space_context* context,
        int operationValue,
        allocation_callback_result* result)
    {
        _ = context;
        allocation_deferred_operation operation = (allocation_deferred_operation)operationValue;
        s_allocationCallbackCount++;
        s_lastAllocationDeferredOperation = operation;

        *result = operation == allocation_deferred_operation.trigger_ephemeral_gc
            ? new allocation_callback_result
            {
                kind = allocation_callback_result_kind.no_full_compact_gc,
            }
            : default;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void BackgroundQueryCallback(
        try_allocate_more_space_context* context,
        int operationValue,
        allocation_callback_result* result)
    {
        _ = context;
        allocation_deferred_operation operation = (allocation_deferred_operation)operationValue;
        s_allocationCallbackCount++;
        s_lastAllocationDeferredOperation = operation;

        if (operation == allocation_deferred_operation.query_background_running)
        {
            s_backgroundQueryCallbackCount++;
        }

        *result = operation switch
        {
            allocation_deferred_operation.trigger_ephemeral_gc => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.no_full_compact_gc,
            },
            allocation_deferred_operation.query_background_running => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.background_not_running,
            },
            _ => default,
        };
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void UnproductiveFullGcCallback(
        try_allocate_more_space_context* context,
        int operationValue,
        allocation_callback_result* result)
    {
        _ = context;
        allocation_deferred_operation operation = (allocation_deferred_operation)operationValue;
        s_allocationCallbackCount++;
        s_lastAllocationDeferredOperation = operation;

        *result = operation switch
        {
            allocation_deferred_operation.trigger_full_compact_gc => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.no_full_compact_gc,
            },
            allocation_deferred_operation.check_retry_other_heap => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            },
            allocation_deferred_operation.handle_oom => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            },
            allocation_deferred_operation.leave_more_space_lock => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            },
            _ => default,
        };
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void BudgetRecheckCallback(
        try_allocate_more_space_context* context,
        int operationValue,
        allocation_callback_result* result)
    {
        _ = context;
        allocation_deferred_operation operation = (allocation_deferred_operation)operationValue;
        s_allocationCallbackCount++;
        s_lastAllocationDeferredOperation = operation;

        *result = operation switch
        {
            allocation_deferred_operation.enter_more_space_lock => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.completed,
            },
            allocation_deferred_operation.check_for_full_gc => FullGcCheckedResult(),
            allocation_deferred_operation.check_allocation_budget => BudgetDisallowedResult(),
            allocation_deferred_operation.wait_for_bgc_high_memory => HighMemoryWaitedResult(),
            allocation_deferred_operation.trigger_gc_for_budget => BudgetTriggeredResult(),
            _ => default,
        };
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void BudgetRetryCallback(
        try_allocate_more_space_context* context,
        int operationValue,
        allocation_callback_result* result)
    {
        _ = context;
        allocation_deferred_operation operation = (allocation_deferred_operation)operationValue;
        s_allocationCallbackCount++;
        s_lastAllocationDeferredOperation = operation;

        *result = operation switch
        {
            allocation_deferred_operation.check_allocation_budget => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.allocation_disallowed,
            },
            allocation_deferred_operation.trigger_gc_for_budget => new allocation_callback_result
            {
                kind = allocation_callback_result_kind.retry_allocate,
            },
            _ => default,
        };
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void NoBackgroundGcWaitCallback(
        try_allocate_more_space_context* context,
        int operationValue,
        allocation_callback_result* result)
    {
        _ = context;
        allocation_deferred_operation operation = (allocation_deferred_operation)operationValue;
        s_allocationCallbackCount++;
        s_lastAllocationDeferredOperation = operation;

        *result = operation == allocation_deferred_operation.check_and_wait_for_bgc
            ? new allocation_callback_result
            {
                kind = allocation_callback_result_kind.background_not_running,
            }
            : default;
    }

    private static allocation_callback_result BudgetDisallowedResult()
    {
        s_budgetCheckCallbackCount++;
        return new allocation_callback_result
        {
            kind = allocation_callback_result_kind.allocation_disallowed,
        };
    }

    private static allocation_callback_result HighMemoryWaitedResult()
    {
        s_highMemoryCallbackCount++;
        return new allocation_callback_result
        {
            kind = allocation_callback_result_kind.background_running,
        };
    }

    private static allocation_callback_result BudgetTriggeredResult()
    {
        s_budgetTriggerCallbackCount++;
        return new allocation_callback_result
        {
            kind = allocation_callback_result_kind.completed,
        };
    }

    private static allocation_callback_result FullGcCheckedResult()
    {
        s_fullGcCheckCallbackCount++;
        return new allocation_callback_result
        {
            kind = allocation_callback_result_kind.completed,
        };
    }
#endif

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private unsafe struct SegMappingAlignmentProbe
    {
        public byte prefix;
        public seg_mapping value;
    }

    private static nuint AlignmentOfSegMapping()
    {
        return (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<SegMappingAlignmentProbe>(nameof(SegMappingAlignmentProbe.value));
    }

    private static void ResetRegionAllocatorCallbackRecorder()
    {
        s_regionAllocatorCallbackCount = 0;
        s_regionAllocatorCallbackLastLeftUsed = 0;
    }

    private static void ResetCreateSegmentEventRecording()
    {
        ResetRegionAllocatorCallbackRecorder();
        GCToEEInterface.Reset();
        GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Information);
    }

    private static void DisableCreateSegmentEvents()
    {
        GCEventStatus.Set(GCEventProvider.Default, GCEventKeyword.None, GCEventLevel.None);
        GCToEEInterface.Reset();
    }

    private static void AssertCreateSegmentEvent(byte* address, nuint size, gc_etw_segment_type type)
    {
        Assert.Equal(GCToEEInterface.FiredEvent.GCCreateSegment_V1, GCToEEInterface.LastFiredEvent);
        Assert.Equal(1, GCToEEInterface.GCCreateSegmentCallCount);
        Assert.Equal((nuint)address, (nuint)GCToEEInterface.LastGCCreateSegmentAddress);
        Assert.Equal(size, GCToEEInterface.LastGCCreateSegmentSize);
        Assert.Equal((uint)type, GCToEEInterface.LastGCCreateSegmentType);
    }

    private static byte RegionAllocatorCallbackSuccess(byte* globalRegionLeftUsed)
    {
        s_regionAllocatorCallbackCount++;
        s_regionAllocatorCallbackLastLeftUsed = (nuint)globalRegionLeftUsed;
        return 1;
    }

    private static byte RegionAllocatorCallbackFailure(byte* globalRegionLeftUsed)
    {
        s_regionAllocatorCallbackCount++;
        s_regionAllocatorCallbackLastLeftUsed = (nuint)globalRegionLeftUsed;
        return 0;
    }

    private static void InitializeRegion(heap_segment* region, nuint start, nuint committed, nuint reserved, int age)
    {
        region->mem = (byte*)(start + (nuint)sizeof(aligned_plug_and_gap));
        region->committed = (byte*)committed;
        region->reserved = (byte*)reserved;
        region->next = null;
        region->prev_free_region = null;
        region->containing_free_list = null;
        region->age_in_free = age;
    }

    private static void InitializeRegionMoveGlobals(seg_mapping* table, nuint alignment)
    {
        gc_heap.min_segment_size_shr = (nuint)gc_heap.index_of_highest_set_bit(alignment);
        GCCommon.seg_mapping_table = table;
        gc_heap.global_region_allocator.initialize_alignment(alignment);
    }

    private static void RestoreRegionMoveGlobals(nuint oldShift, seg_mapping* oldTable, region_allocator oldGlobalAllocator)
    {
        gc_heap.min_segment_size_shr = oldShift;
        GCCommon.seg_mapping_table = oldTable;
        gc_heap.global_region_allocator = oldGlobalAllocator;
    }

    private static void InitializeRegionAllocatorForMove(region_allocator* allocator, uint* mapLeftStart, int usedUnits, nuint alignment, byte* globalStart)
    {
        WriteRegionAllocatorPointerField(allocator, "global_region_start", globalStart);
        WriteRegionAllocatorPointerField(allocator, "global_region_end", globalStart + (nint)((nuint)usedUnits * alignment));
        WriteRegionAllocatorPointerField(allocator, "global_region_left_used", globalStart + (nint)((nuint)usedUnits * alignment));
        WriteRegionAllocatorPointerField(allocator, "global_region_right_used", globalStart + (nint)((nuint)usedUnits * alignment));
        WriteRegionAllocatorField(allocator, "total_free_units", 0u);
        WriteRegionAllocatorField(allocator, "region_alignment", alignment);
        WriteRegionAllocatorField(allocator, "large_region_alignment", (nuint)region_allocator.LARGE_REGION_FACTOR * alignment);
        WriteRegionAllocatorPointerField(allocator, "region_map_left_start", mapLeftStart);
        WriteRegionAllocatorPointerField(allocator, "region_map_left_end", mapLeftStart + usedUnits);
        WriteRegionAllocatorPointerField(allocator, "region_map_right_start", mapLeftStart + usedUnits);
        WriteRegionAllocatorPointerField(allocator, "region_map_right_end", mapLeftStart + usedUnits);
        WriteRegionAllocatorField(allocator, "num_left_used_free_units", 0u);
        WriteRegionAllocatorField(allocator, "num_right_used_free_units", 0u);
    }

    private static heap_segment* InitializeMappedRegion(seg_mapping* table, nuint start, uint numUnits, nuint alignment)
    {
        heap_segment* region = &table[(int)(start >> (int)gc_heap.min_segment_size_shr)].region_info;
        *region = default;
        nuint size = (nuint)numUnits * alignment;
        InitializeRegion(region, start, start + size, start + size, age: 0);
        return region;
    }

    private static void ClearRegionFreeLists(region_free_list* lists)
    {
        for (int kind = (int)free_region_kind.basic_free_region;
             kind < (int)free_region_kind.count_free_region_kinds;
             kind++)
        {
            lists[kind] = default;
        }
    }

    private static uint* InitializeRegionAllocatorMap(region_allocator* allocator, nuint start, nuint end, nuint alignment)
    {
        byte* lowest = null;
        byte* highest = null;

        Assert.True(allocator->init((byte*)start, (byte*)end, alignment, &lowest, &highest));
        return (uint*)ReadRegionAllocatorPointerField(allocator, "region_map_left_start");
    }

    private static void DeleteRegionImplUnderLock(region_allocator* allocator, byte* regionStart)
    {
        allocator->initialize();
        allocator->enter_spin_lock();
        try
        {
            allocator->delete_region_impl(regionStart);
        }
        finally
        {
            allocator->leave_spin_lock();
        }
    }

    private static uint EncodedFreeRegionBlock(uint numUnits)
    {
        return unchecked((uint)region_allocator.region_alloc_free_bit) | numUnits;
    }

    private sealed unsafe class RegionSegmentsStateScope : System.IDisposable
    {
        private readonly nuint _minSegmentSizeShr;
        private readonly seg_mapping* _segMappingTable;
        private readonly region_allocator _globalRegionAllocator;
        private readonly gc_heap.region_free_list_array _freeRegions;
        private readonly uint* _cardTable;
        private readonly short* _brickTable;
        private readonly gc_heap.recorded_committed_bucket_array _committedByOh;
        private readonly nuint _currentTotalCommitted;
        private readonly nuint _currentTotalCommittedBookkeeping;
        private readonly nuint _heapHardLimit;
        private readonly gc_heap.object_heap_array _heapHardLimitOh;
        private readonly bool _neverDecommit;
        private readonly nuint _reservedMemory;
        private readonly gc_mechanisms _settings;
        private readonly CLRCriticalSection _checkCommitCs;
        private readonly bool _initializedCommitLock;
#if BACKGROUND_GC
        private readonly GCCommon.changed_seg_array _savedChangedSegs;
        private readonly ulong _savedChangedSegsCount;
        private readonly bgc_state _currentBgcState;
        private readonly byte* _backgroundSavedLowestAddress;
        private readonly byte* _backgroundSavedHighestAddress;
        private readonly int _gcBackgroundRunning;
        private readonly uint* _markArray;
        private readonly byte* _lowestAddress;
        private readonly byte* _highestAddress;
#endif

        public RegionSegmentsStateScope(bool initializeCommitLock)
        {
            _minSegmentSizeShr = gc_heap.min_segment_size_shr;
            _segMappingTable = GCCommon.seg_mapping_table;
            _globalRegionAllocator = gc_heap.global_region_allocator;
            _freeRegions = gc_heap.free_regions;
            _cardTable = gc_heap.card_table;
            _brickTable = gc_heap.brick_table;
            _committedByOh = gc_heap.committed_by_oh;
            _currentTotalCommitted = gc_heap.current_total_committed;
            _currentTotalCommittedBookkeeping = gc_heap.current_total_committed_bookkeeping;
            _heapHardLimit = gc_heap.heap_hard_limit;
            _heapHardLimitOh = gc_heap.heap_hard_limit_oh;
            _neverDecommit = gc_heap.never_decommit_p;
            _reservedMemory = gc_heap.reserved_memory;
            _settings = gc_heap.settings;
            _checkCommitCs = gc_heap.check_commit_cs;
#if BACKGROUND_GC
            _savedChangedSegs = GCCommon.saved_changed_segs;
            _savedChangedSegsCount = GCCommon.saved_changed_segs_count;
            _currentBgcState = gc_heap.current_bgc_state;
            _backgroundSavedLowestAddress = gc_heap.background_saved_lowest_address;
            _backgroundSavedHighestAddress = gc_heap.background_saved_highest_address;
            _gcBackgroundRunning = gc_heap.gc_background_running;
            _markArray = gc_heap.mark_array;
            _lowestAddress = gc_heap.lowest_address;
            _highestAddress = gc_heap.highest_address;
#endif

            gc_heap.free_regions = default;
            gc_heap.card_table = null;
            gc_heap.brick_table = null;
            gc_heap.committed_by_oh = default;
            gc_heap.current_total_committed = 0;
            gc_heap.current_total_committed_bookkeeping = 0;
            gc_heap.heap_hard_limit = 0;
            gc_heap.heap_hard_limit_oh = default;
            gc_heap.never_decommit_p = false;
            gc_heap.reserved_memory = 0;
            gc_heap.settings = default;
#if BACKGROUND_GC
            GCCommon.saved_changed_segs = default;
            GCCommon.initialize();
            gc_heap.current_bgc_state = default;
            gc_heap.background_saved_lowest_address = null;
            gc_heap.background_saved_highest_address = null;
            gc_heap.gc_background_running = 0;
            gc_heap.mark_array = null;
            gc_heap.lowest_address = null;
            gc_heap.highest_address = null;
#endif

            if (initializeCommitLock)
            {
                gc_heap.check_commit_cs = default;
                _initializedCommitLock = gc_heap.check_commit_cs.Initialize();
                Assert.True(_initializedCommitLock);
            }
        }

        public void Dispose()
        {
            if (_initializedCommitLock)
            {
                gc_heap.check_commit_cs.Destroy();
            }

            gc_heap.min_segment_size_shr = _minSegmentSizeShr;
            GCCommon.seg_mapping_table = _segMappingTable;
            gc_heap.global_region_allocator = _globalRegionAllocator;
            gc_heap.free_regions = _freeRegions;
            gc_heap.card_table = _cardTable;
            gc_heap.brick_table = _brickTable;
            gc_heap.committed_by_oh = _committedByOh;
            gc_heap.current_total_committed = _currentTotalCommitted;
            gc_heap.current_total_committed_bookkeeping = _currentTotalCommittedBookkeeping;
            gc_heap.heap_hard_limit = _heapHardLimit;
            gc_heap.heap_hard_limit_oh = _heapHardLimitOh;
            gc_heap.never_decommit_p = _neverDecommit;
            gc_heap.reserved_memory = _reservedMemory;
            gc_heap.settings = _settings;
            gc_heap.check_commit_cs = _checkCommitCs;
#if BACKGROUND_GC
            GCCommon.saved_changed_segs = _savedChangedSegs;
            GCCommon.saved_changed_segs_count = _savedChangedSegsCount;
            gc_heap.current_bgc_state = _currentBgcState;
            gc_heap.background_saved_lowest_address = _backgroundSavedLowestAddress;
            gc_heap.background_saved_highest_address = _backgroundSavedHighestAddress;
            gc_heap.gc_background_running = _gcBackgroundRunning;
            gc_heap.mark_array = _markArray;
            gc_heap.lowest_address = _lowestAddress;
            gc_heap.highest_address = _highestAddress;
#endif
        }
    }

    private unsafe struct RegionAllocatorSnapshot
    {
        public byte* GlobalRegionStart;
        public byte* GlobalRegionEnd;
        public byte* GlobalRegionLeftUsed;
        public byte* GlobalRegionRightUsed;
        public uint TotalFreeUnits;
        public uint* RegionMapLeftStart;
        public uint* RegionMapLeftEnd;
        public uint* RegionMapRightStart;
        public uint* RegionMapRightEnd;
        public uint NumLeftUsedFreeUnits;
        public uint NumRightUsedFreeUnits;
    }

    private static RegionAllocatorSnapshot CaptureRegionAllocatorSnapshot(region_allocator* allocator)
    {
        return new RegionAllocatorSnapshot
        {
            GlobalRegionStart = (byte*)ReadRegionAllocatorPointerField(allocator, "global_region_start"),
            GlobalRegionEnd = (byte*)ReadRegionAllocatorPointerField(allocator, "global_region_end"),
            GlobalRegionLeftUsed = (byte*)ReadRegionAllocatorPointerField(allocator, "global_region_left_used"),
            GlobalRegionRightUsed = (byte*)ReadRegionAllocatorPointerField(allocator, "global_region_right_used"),
            TotalFreeUnits = ReadRegionAllocatorField<uint>(allocator, "total_free_units"),
            RegionMapLeftStart = (uint*)ReadRegionAllocatorPointerField(allocator, "region_map_left_start"),
            RegionMapLeftEnd = (uint*)ReadRegionAllocatorPointerField(allocator, "region_map_left_end"),
            RegionMapRightStart = (uint*)ReadRegionAllocatorPointerField(allocator, "region_map_right_start"),
            RegionMapRightEnd = (uint*)ReadRegionAllocatorPointerField(allocator, "region_map_right_end"),
            NumLeftUsedFreeUnits = ReadRegionAllocatorField<uint>(allocator, "num_left_used_free_units"),
            NumRightUsedFreeUnits = ReadRegionAllocatorField<uint>(allocator, "num_right_used_free_units"),
        };
    }

    private static void AssertRegionAllocatorSnapshotEqual(RegionAllocatorSnapshot expected, region_allocator* allocator)
    {
        Assert.Equal((nuint)expected.GlobalRegionStart, (nuint)ReadRegionAllocatorPointerField(allocator, "global_region_start"));
        Assert.Equal((nuint)expected.GlobalRegionEnd, (nuint)ReadRegionAllocatorPointerField(allocator, "global_region_end"));
        Assert.Equal((nuint)expected.GlobalRegionLeftUsed, (nuint)ReadRegionAllocatorPointerField(allocator, "global_region_left_used"));
        Assert.Equal((nuint)expected.GlobalRegionRightUsed, (nuint)ReadRegionAllocatorPointerField(allocator, "global_region_right_used"));
        Assert.Equal(expected.TotalFreeUnits, ReadRegionAllocatorField<uint>(allocator, "total_free_units"));
        Assert.Equal((nuint)expected.RegionMapLeftStart, (nuint)ReadRegionAllocatorPointerField(allocator, "region_map_left_start"));
        Assert.Equal((nuint)expected.RegionMapLeftEnd, (nuint)ReadRegionAllocatorPointerField(allocator, "region_map_left_end"));
        Assert.Equal((nuint)expected.RegionMapRightStart, (nuint)ReadRegionAllocatorPointerField(allocator, "region_map_right_start"));
        Assert.Equal((nuint)expected.RegionMapRightEnd, (nuint)ReadRegionAllocatorPointerField(allocator, "region_map_right_end"));
        Assert.Equal(expected.NumLeftUsedFreeUnits, ReadRegionAllocatorField<uint>(allocator, "num_left_used_free_units"));
        Assert.Equal(expected.NumRightUsedFreeUnits, ReadRegionAllocatorField<uint>(allocator, "num_right_used_free_units"));
    }

    private static T ReadRegionAllocatorField<T>(region_allocator* allocator, string fieldName)
        where T : unmanaged
    {
        return *(T*)((byte*)allocator + (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>(fieldName));
    }

    private static void WriteRegionAllocatorField<T>(region_allocator* allocator, string fieldName, T value)
        where T : unmanaged
    {
        *(T*)((byte*)allocator + (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>(fieldName)) = value;
    }

    private static void* ReadRegionAllocatorPointerField(region_allocator* allocator, string fieldName)
    {
        return *(void**)((byte*)allocator + (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>(fieldName));
    }

    private static void WriteRegionAllocatorPointerField(region_allocator* allocator, string fieldName, void* value)
    {
        *(void**)((byte*)allocator + (nuint)System.Runtime.InteropServices.Marshal.OffsetOf<region_allocator>(fieldName)) = value;
    }
#endif

    private static nuint OffsetOf(void* field, seg_mapping* mapping) => (nuint)((byte*)field - (byte*)mapping);

    private static nuint OffsetOf(void* field, heap_segment* segment) => (nuint)((byte*)field - (byte*)segment);
}
