// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Internal.Runtime.GarbageCollection;

public sealed unsafe class GCPrivTests
{
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
}
