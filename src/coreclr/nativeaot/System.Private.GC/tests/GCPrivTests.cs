// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Internal.Runtime.GarbageCollection;

public sealed unsafe class GCPrivTests
{
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
}
