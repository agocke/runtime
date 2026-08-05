// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.InteropServices;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

public sealed unsafe class GCDescTests
{
    [Fact]
    public void SeriesOperationsMatchNativePointerCounts()
    {
        CGCDescSeries series = default;

        series.SetSeriesCount(2);
        series.IncSeriesCount();
        series.SetSeriesOffset(24);

        Assert.Equal(3u, series.GetSeriesCount());
        Assert.Equal((nuint)(3 * IntPtr.Size), series.GetSeriesSize());
        Assert.Equal(24u, series.GetSeriesOffset());

        series.SetSeriesSize(40);
        Assert.Equal(40u, series.GetSeriesSize());
    }

    [Fact]
    public void ValueSeriesItemOverlaysSeriesSize()
    {
        CGCDescSeries series = default;
        val_serie_item item = default;
        item.set_val_serie_item(3, 5);

        series.SetSeriesValItem(item, 0);

        Assert.Equal((uint)3, series.val_serie.nptrs);
        Assert.Equal((uint)5, series.val_serie.skip);
    }

    [Fact]
    public void NormalDescriptorGrowsBackwardFromMethodTable()
    {
        const int NumSeries = 2;
        int size = checked((int)CGCDesc.ComputeSize(NumSeries));
        byte* storage = stackalloc byte[size + sizeof(CGCDesc)];
        CGCDesc* descriptor = (CGCDesc*)(storage + size);

        CGCDesc.Init(descriptor, NumSeries);

        Assert.Equal((nuint)NumSeries, descriptor->GetNumSeries());
        Assert.Equal((nuint)size, descriptor->GetSize());
        Assert.Equal((nint)storage, (nint)descriptor->GetStartOfGCData());
        Assert.Equal(
            (nint)descriptor - sizeof(nuint) - sizeof(CGCDescSeries),
            (nint)descriptor->GetHighestSeries());
        Assert.Equal((nint)storage, (nint)descriptor->GetLowestSeries());
    }

    [Fact]
    public void RepeatingDescriptorUsesNegativeSeriesCount()
    {
        const int NumSeries = 3;
        int size = checked((int)CGCDesc.ComputeSizeRepeating(NumSeries));
        byte* storage = stackalloc byte[size + sizeof(CGCDesc)];
        CGCDesc* descriptor = (CGCDesc*)(storage + size);

        CGCDesc.InitValueClassSeries(descriptor, NumSeries);

        Assert.Equal(unchecked((nuint)(-NumSeries)), descriptor->GetNumSeries());
        Assert.Equal((nuint)size, descriptor->GetSize());
        Assert.Equal((nint)storage, (nint)descriptor->GetStartOfGCData());
    }

    [Fact]
    public void RecordLayoutsMatchNative()
    {
        Assert.Equal(IntPtr.Size, sizeof(val_serie_item));
        Assert.Equal(IntPtr.Size * 2, sizeof(CGCDescSeries));
        Assert.Equal(0, Marshal.OffsetOf<CGCDescSeries>(nameof(CGCDescSeries.seriessize)).ToInt32());
        Assert.Equal(0, Marshal.OffsetOf<CGCDescSeries>(nameof(CGCDescSeries.val_serie)).ToInt32());
        Assert.Equal(
            IntPtr.Size,
            Marshal.OffsetOf<CGCDescSeries>(nameof(CGCDescSeries.startoffset)).ToInt32());
    }

    [Fact]
    public void NormalDescriptorScansReferencesInNativeSeriesOrder()
    {
        const int NumSeries = 2;
        const int ObjectWords = 8;
        int pointerSize = sizeof(nuint);
        int objectSize = ObjectWords * pointerSize;
        int descriptorSize = sizeof(nuint) + (NumSeries * sizeof(CGCDescSeries));
        byte* storage = stackalloc byte[descriptorSize + sizeof(MethodTable)];
        MethodTable* methodTable = (MethodTable*)(storage + descriptorSize);
        byte* @object = stackalloc byte[objectSize];
        byte*** references = stackalloc byte**[3];
        reference_recorder recorder = new() { references = references };

        methodTable->m_uFlags = MethodTable.HasPointersFlag;
        *((nuint*)methodTable - 1) = NumSeries;

        CGCDescSeries* lowest = (CGCDescSeries*)(storage);
        lowest->seriessize = unchecked((nuint)(-(nint)(objectSize - pointerSize)));
        lowest->startoffset = (nuint)pointerSize;

        CGCDescSeries* highest = lowest + 1;
        highest->seriessize = unchecked((nuint)(-(nint)(objectSize - (2 * pointerSize))));
        highest->startoffset = (nuint)(4 * pointerSize);

        gc_heap.go_through_object_nostart(
            methodTable,
            @object,
            (nuint)objectSize,
            &recorder,
            &RecordReference);

        Assert.Equal(3, recorder.count);
        Assert.Equal((nuint)(@object + (4 * pointerSize)), (nuint)references[0]);
        Assert.Equal((nuint)(@object + (5 * pointerSize)), (nuint)references[1]);
        Assert.Equal((nuint)(@object + pointerSize), (nuint)references[2]);
    }

    [Fact]
    public void ComponentArrayDescriptorScansAllReferencesToTheObjectBoundary()
    {
        const int ObjectWords = 6;
        int pointerSize = sizeof(nuint);
        int dataOffset = 2 * pointerSize;
        int objectSize = ObjectWords * pointerSize;
        int descriptorSize = sizeof(nuint) + sizeof(CGCDescSeries);
        byte* storage = stackalloc byte[descriptorSize + sizeof(MethodTable)];
        MethodTable* methodTable = (MethodTable*)(storage + descriptorSize);
        byte* @object = stackalloc byte[objectSize];
        byte*** references = stackalloc byte**[3];
        reference_recorder recorder = new() { references = references };

        methodTable->m_uFlags = MethodTable.HasPointersFlag | MethodTable.HasComponentSizeFlag;
        methodTable->m_usComponentSize = (ushort)sizeof(nuint);
        *((nuint*)methodTable - 1) = 1;

        CGCDescSeries* series = (CGCDescSeries*)storage;
        series->seriessize = unchecked((nuint)(-(nint)(dataOffset + pointerSize)));
        series->startoffset = (nuint)dataOffset;

        Assert.NotEqual(0, methodTable->HasComponentSize());
        Assert.Equal((ushort)sizeof(nuint), methodTable->RawGetComponentSize());

        gc_heap.go_through_object_nostart(
            methodTable,
            @object,
            (nuint)objectSize,
            &recorder,
            &RecordReference);

        Assert.Equal(3, recorder.count);
        Assert.Equal((nuint)(@object + dataOffset), (nuint)references[0]);
        Assert.Equal((nuint)(@object + dataOffset + pointerSize), (nuint)references[1]);
        Assert.Equal((nuint)(@object + dataOffset + (2 * pointerSize)), (nuint)references[2]);
    }

    [Fact]
    public void RepeatingDescriptorScansValueClassArrayReferencesInNativeOrder()
    {
        const int NumSeries = 2;
        const int ComponentWords = 4;
        const int ComponentCount = 3;
        int descriptorSize = sizeof(nuint) + sizeof(CGCDescSeries) + sizeof(val_serie_item);
        int objectSize = sizeof(nuint) + (ComponentCount * ComponentWords * sizeof(nuint));
        byte* storage = stackalloc byte[descriptorSize + sizeof(MethodTable)];
        MethodTable* methodTable = (MethodTable*)(storage + descriptorSize);
        byte* @object = stackalloc byte[objectSize];
        byte*** references = stackalloc byte**[NumSeries * ComponentCount];
        reference_recorder recorder = new() { references = references };

        methodTable->m_uFlags = MethodTable.HasPointersFlag | MethodTable.HasComponentSizeFlag;
        methodTable->m_usComponentSize = (ushort)(ComponentWords * sizeof(nuint));
        *((nint*)methodTable - 1) = -NumSeries;

        CGCDescSeries* series = (CGCDescSeries*)(storage + sizeof(val_serie_item));
        series->startoffset = (nuint)sizeof(nuint);
        SetValueSeries((val_serie_item*)series, 0, sizeof(nuint));
        SetValueSeries((val_serie_item*)series, -1, sizeof(nuint));

        gc_heap.go_through_object_nostart(
            methodTable,
            @object,
            (nuint)objectSize,
            &recorder,
            &RecordReference);

        Assert.Equal(NumSeries * ComponentCount, recorder.count);
        for (int component = 0; component < ComponentCount; component++)
        {
            int componentOffset = sizeof(nuint) + (component * ComponentWords * sizeof(nuint));
            Assert.Equal((nuint)(@object + componentOffset), (nuint)references[2 * component]);
            Assert.Equal(
                (nuint)(@object + componentOffset + (2 * sizeof(nuint))),
                (nuint)references[(2 * component) + 1]);
        }
    }

    [Fact]
    public void NoPointerMethodTableDoesNotScanOrReadADescriptor()
    {
        MethodTable methodTable = default;
        byte* @object = stackalloc byte[sizeof(nuint)];
        byte*** references = stackalloc byte**[1];
        reference_recorder recorder = new() { references = references };

        gc_heap.go_through_object_nostart(
            &methodTable,
            @object,
            (nuint)sizeof(nuint),
            &recorder,
            &RecordReference);

        Assert.Equal(0, recorder.count);
    }

    private struct reference_recorder
    {
        public byte*** references;
        public int count;
    }

    private static void RecordReference(byte** reference, void* context)
    {
        reference_recorder* recorder = (reference_recorder*)context;
        recorder->references[recorder->count] = reference;
        recorder->count++;
    }

    private static void SetValueSeries(val_serie_item* series, int index, int skip)
    {
#if TARGET_64BIT
        series[index].set_val_serie_item(1, (uint)skip);
#else
        series[index].set_val_serie_item(1, (ushort)skip);
#endif
    }
}
