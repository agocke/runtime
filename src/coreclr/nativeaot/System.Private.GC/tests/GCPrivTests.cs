// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Threading;
using SysInterlocked = System.Threading.Interlocked;
using SysVolatile = System.Threading.Volatile;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

[Collection(SyncImportsCollection.Name)]
public sealed unsafe class GCPrivTests
{
    private static int s_regionAllocatorCallbackCount;
    private static nuint s_regionAllocatorCallbackLastLeftUsed;

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
