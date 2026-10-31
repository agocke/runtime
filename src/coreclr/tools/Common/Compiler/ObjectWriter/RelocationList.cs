// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using ILCompiler.DependencyAnalysis;
using Internal.Text;

namespace ILCompiler.ObjectWriter;

public abstract partial class ObjectWriter
{
    protected readonly struct SymbolicRelocation(long offset, RelocType type, Utf8String symbolName, long addend = 0)
    {
        public readonly long Offset = offset;
        public readonly long Addend = addend;
        public readonly Utf8String SymbolName = symbolName;
        public readonly RelocType Type = type;
    }

    private protected sealed class RelocationList
    {
        private const int SlabShift = 10;
        private const int SlabSize = 1 << SlabShift;
        private const int SlabMask = SlabSize - 1;
        private readonly List<SymbolicRelocation[]> _slabs = new();

        public int Count { get; private set; }

        public void Add(SymbolicRelocation value)
        {
            int next = checked(Count + 1);
            if ((Count & SlabMask) == 0)
                _slabs.Add(new SymbolicRelocation[SlabSize]);
            _slabs[Count >> SlabShift][Count & SlabMask] = value;
            Count = next;
        }

        public Enumerator GetEnumerator() => new(this);

        public void Reverse()
        {
            for (int left = 0, right = Count - 1; left < right; left++, right--)
            {
                ref SymbolicRelocation first = ref _slabs[left >> SlabShift][left & SlabMask];
                ref SymbolicRelocation last = ref _slabs[right >> SlabShift][right & SlabMask];
                (first, last) = (last, first);
            }
        }

        public struct Enumerator(RelocationList owner)
        {
            private int _index = -1;

            public readonly SymbolicRelocation Current
                => owner._slabs[_index >> SlabShift][_index & SlabMask];

            public bool MoveNext()
            {
                if (_index + 1 >= owner.Count)
                    return false;
                _index++;

                return true;
            }
        }
    }
}
