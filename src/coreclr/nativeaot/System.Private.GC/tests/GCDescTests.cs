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
}
