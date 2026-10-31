// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

namespace ILCompiler.DependencyAnalysisFramework;

internal sealed class CompactPendingStore<T>
{
    private const int SlabShift = 10;
    private const int SlabSize = 1 << SlabShift;
    private const int SlabMask = SlabSize - 1;
    private const int HashThreshold = 8;

    private readonly List<Group[]> _groups = new();
    private readonly List<Entry[]> _entries = new();
    private int _groupCount;
    private int _entryCount;
    private int _freeGroups;
    private int _freeEntries;

    private struct Group
    {
        public int Head;
        public int Tail;
        public int Count;
        public HashSet<DependencyNodeCore<T>> Targets;
    }

    private struct Entry
    {
        public DependencyNodeCore<T> Target;
        public int Next;
    }

    public void Add(DependencyNodeCore<T> condition, DependencyNodeCore<T> target)
    {
        int groupIndex = condition.ConditionalStoreIndex;
        if (groupIndex == 0)
        {
            if (_freeGroups != 0)
            {
                groupIndex = _freeGroups;
                _freeGroups = GetGroup(groupIndex).Head;
                GetGroup(groupIndex) = default;
            }
            else
            {
                int next = checked(_groupCount + 1);
                if ((_groupCount & SlabMask) == 0)
                    _groups.Add(new Group[SlabSize]);
                groupIndex = _groupCount = next;
            }
            condition.ConditionalStoreIndex = groupIndex;
        }

        ref Group group = ref GetGroup(groupIndex);
        if (group.Targets is not null)
        {
            if (!group.Targets.Add(target))
                return;
        }
        else
        {
            for (int index = group.Head; index != 0; index = GetEntry(index).Next)
                if (ReferenceEquals(GetEntry(index).Target, target))
                    return;
            if (group.Count == HashThreshold)
            {
                group.Targets = new HashSet<DependencyNodeCore<T>>();
                for (int index = group.Head; index != 0; index = GetEntry(index).Next)
                    group.Targets.Add(GetEntry(index).Target);
                group.Targets.Add(target);
            }
        }

        int entryIndex;
        if (_freeEntries != 0)
        {
            entryIndex = _freeEntries;
            _freeEntries = GetEntry(entryIndex).Next;
        }
        else
        {
            int next = checked(_entryCount + 1);
            if ((_entryCount & SlabMask) == 0)
                _entries.Add(new Entry[SlabSize]);
            entryIndex = _entryCount = next;
        }
        GetEntry(entryIndex) = new Entry { Target = target };
        if (group.Tail == 0)
            group.Head = entryIndex;
        else
            GetEntry(group.Tail).Next = entryIndex;
        group.Tail = entryIndex;
        group.Count++;
    }

    public int Take(DependencyNodeCore<T> condition)
    {
        int groupIndex = condition.ConditionalStoreIndex;
        if (groupIndex == 0)
            return 0;
        condition.ConditionalStoreIndex = 0;
        ref Group group = ref GetGroup(groupIndex);
        int head = group.Head;
        group = new Group { Head = _freeGroups };
        _freeGroups = groupIndex;

        return head;
    }

    public DependencyNodeCore<T> Pop(ref int index)
    {
        int current = index;
        ref Entry entry = ref GetEntry(current);
        DependencyNodeCore<T> target = entry.Target;
        index = entry.Next;
        entry = new Entry { Next = _freeEntries };
        _freeEntries = current;

        return target;
    }

    public void Release()
    {
        _groups.Clear();
        _entries.Clear();
    }

    private ref Group GetGroup(int index)
        => ref _groups[(index - 1) >> SlabShift][(index - 1) & SlabMask];

    private ref Entry GetEntry(int index)
        => ref _entries[(index - 1) >> SlabShift][(index - 1) & SlabMask];
}
