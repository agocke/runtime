// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from src/coreclr/gc/gcdesc.h.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct val_serie_item
    {
#if TARGET_64BIT
        public uint nptrs;
        public uint skip;

        public void set_val_serie_item(uint nptrs, uint skip)
#else
        public ushort nptrs;
        public ushort skip;

        public void set_val_serie_item(ushort nptrs, ushort skip)
#endif
        {
            this.nptrs = nptrs;
            this.skip = skip;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    internal unsafe struct CGCDescSeries
    {
        [FieldOffset(0)]
        public nuint seriessize;

        [FieldOffset(0)]
        public val_serie_item val_serie;

#if TARGET_64BIT
        [FieldOffset(8)]
#else
        [FieldOffset(4)]
#endif
        public nuint startoffset;

        public nuint GetSeriesCount()
        {
            return seriessize / (nuint)sizeof(nuint);
        }

        public void SetSeriesCount(nuint newcount)
        {
            seriessize = newcount * (nuint)sizeof(nuint);
        }

        public void IncSeriesCount(nuint increment = 1)
        {
            seriessize += increment * (nuint)sizeof(nuint);
        }

        public nuint GetSeriesSize()
        {
            return seriessize;
        }

        public void SetSeriesSize(nuint newsize)
        {
            seriessize = newsize;
        }

        public void SetSeriesValItem(val_serie_item item, int index)
        {
            val_serie_item* series = (val_serie_item*)Unsafe.AsPointer(ref val_serie);
            series[index] = item;
        }

        public void SetSeriesOffset(nuint newoffset)
        {
            startoffset = newoffset;
        }

        public nuint GetSeriesOffset()
        {
            return startoffset;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct CGCDesc
    {
        private byte _unused;

        public static nuint ComputeSize(nuint NumSeries)
        {
            Debug.Assert((nint)NumSeries > 0);

            return (nuint)sizeof(nuint) + (NumSeries * (nuint)sizeof(CGCDescSeries));
        }

        public static nuint ComputeSizeRepeating(nuint NumSeries)
        {
            Debug.Assert((nint)NumSeries > 0);

            return (nuint)(sizeof(nuint) + sizeof(CGCDescSeries))
                + ((NumSeries - 1) * (nuint)sizeof(val_serie_item));
        }

        public static void Init(void* mem, nuint NumSeries)
        {
            *(((nuint*)mem) - 1) = NumSeries;
        }

        public static void InitValueClassSeries(void* mem, nuint NumSeries)
        {
            *(((nint*)mem) - 1) = -(nint)NumSeries;
        }

        public nuint GetNumSeries()
        {
            CGCDesc* self = (CGCDesc*)Unsafe.AsPointer(ref this);
            return *(((nuint*)self) - 1);
        }

        public CGCDescSeries* GetLowestSeries()
        {
            Debug.Assert((nint)GetNumSeries() > 0);
            CGCDesc* self = (CGCDesc*)Unsafe.AsPointer(ref this);
            return (CGCDescSeries*)((byte*)self - ComputeSize(GetNumSeries()));
        }

        public CGCDescSeries* GetHighestSeries()
        {
            CGCDesc* self = (CGCDesc*)Unsafe.AsPointer(ref this);
            return ((CGCDescSeries*)(((nuint*)self) - 1)) - 1;
        }

        public nuint GetSize()
        {
            nint numSeries = (nint)GetNumSeries();
            if (numSeries < 0)
            {
                return ComputeSizeRepeating((nuint)(-numSeries));
            }

            return ComputeSize((nuint)numSeries);
        }

        public byte* GetStartOfGCData()
        {
            CGCDesc* self = (CGCDesc*)Unsafe.AsPointer(ref this);
            return (byte*)self - GetSize();
        }

        private bool IsValueClassSeries()
        {
            return (nint)GetNumSeries() < 0;
        }
    }
}
