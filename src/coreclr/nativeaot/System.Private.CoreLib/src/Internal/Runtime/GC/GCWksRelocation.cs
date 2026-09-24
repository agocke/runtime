// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Internal.Runtime.GC
{
    internal static unsafe partial class GCWksInitialization
    {
        private struct RelocateArgs
        {
            public bool IsShortened;
            public mark* PinnedPlugEntry;
            public byte* LastPlug;
        }

        private static void RelocateAddress(byte** reference)
        {
            if (s_settings.compaction == 0)
            {
                return;
            }

            byte* oldAddress = *reference;
            byte* relocationLow = s_markLow is not null
                ? s_markLow
                : GCCommon.g_gc_lowest_address;
            byte* relocationHigh = s_markHigh is not null
                ? s_markHigh
                : GCCommon.g_gc_highest_address;
            if (oldAddress is null ||
                relocationLow is null ||
                relocationHigh is null ||
                oldAddress < relocationLow ||
                oldAddress >= relocationHigh ||
                !IsAddressInSegment((nuint)oldAddress, s_sohSegment))
            {
                return;
            }

            if (s_brickTable is null)
            {
                return;
            }

            nuint brick = GetBrickIndex(oldAddress);
            int brickEntry = s_brickTable[brick];
            if (brickEntry == 0)
            {
                return;
            }

            while (true)
            {
                while (brickEntry < 0)
                {
                    nint previousBrick = (nint)brick + brickEntry;
                    if (previousBrick < 0)
                    {
                        return;
                    }

                    brick = (nuint)previousBrick;
                    brickEntry = s_brickTable[brick];
                }

                byte* tree = BrickAddress(brick) + brickEntry - 1;
                byte* node = TreeSearch(tree, oldAddress);
                byte* newAddress;
                if (node <= oldAddress)
                {
                    newAddress = oldAddress + GetNodeRelocationDistance(node);
                }
                else if ((((plug_and_reloc*)node)[-1].reloc & 2) != 0)
                {
                    newAddress = oldAddress +
                        GetNodeRelocationDistance(node) +
                        GetNodeGapSize(node);
                }
                else
                {
                    if (brick == 0)
                    {
                        return;
                    }

                    brick--;
                    brickEntry = s_brickTable[brick];
                    continue;
                }

                *reference = newAddress;
                return;
            }
        }

        private static byte* TreeSearch(byte* tree, byte* oldAddress)
        {
            byte* candidate = null;
            while (true)
            {
                if (tree < oldAddress)
                {
                    nint child = GetNodeRightChild(tree);
                    if (child == 0)
                    {
                        break;
                    }

                    candidate = tree;
                    tree += child;
                }
                else if (tree > oldAddress)
                {
                    nint child = GetNodeLeftChild(tree);
                    if (child == 0)
                    {
                        break;
                    }

                    tree += child;
                }
                else
                {
                    break;
                }
            }

            return tree <= oldAddress ? tree : candidate is not null ? candidate : tree;
        }

        private static void RelocateObjectReferences(Object* obj, byte* cardStart, byte* cardEnd)
        {
            MethodTable* methodTable = obj->GetGCSafeMethodTable();
            if (methodTable is null || !methodTable->ContainsGCPointers())
            {
                return;
            }

            nuint objectSize = GetObjectSize(obj);
            nint seriesCount = GetSeriesCount(methodTable);
            CGCDescSeries* series = GetHighestSeries(methodTable);
            if (seriesCount >= 0)
            {
                CGCDescSeries* lowest = GetLowestSeries(methodTable, seriesCount);
                do
                {
                    byte* slot = (byte*)obj + (nint)series->startoffset;
                    byte* stop = slot + (nint)series->seriessize + objectSize;
                    while (slot < stop)
                    {
                        if (cardStart is null || (slot >= cardStart && slot < cardEnd))
                        {
                            RelocateAddress((byte**)slot);
                        }

                        slot += sizeof(void*);
                    }

                    series--;
                }
                while (series >= lowest);

                return;
            }

            byte* repeatingSlot = (byte*)obj + (nint)series->startoffset;
            byte* objectEnd = (byte*)obj + objectSize - sizeof(ObjHeader);
            val_serie_item* valueSeries = &series->val_serie;
            while (repeatingSlot < objectEnd)
            {
                for (nint i = 0; i > seriesCount; i--)
                {
                    val_serie_item item = valueSeries[i];
                    byte* stop = repeatingSlot + (nint)item.nptrs * sizeof(void*);
                    while (repeatingSlot < stop)
                    {
                        if (cardStart is null || (repeatingSlot >= cardStart && repeatingSlot < cardEnd))
                        {
                            RelocateAddress((byte**)repeatingSlot);
                        }

                        repeatingSlot += sizeof(void*);
                    }

                    repeatingSlot += item.skip;
                }
            }
        }

        private static void RelocateShortenedObjectReferences(
            Object* obj,
            nuint objectSize,
            byte* end,
            byte* savedPlugInfoStart,
            byte** savedInfoToRelocate)
        {
            MethodTable* methodTable = obj->GetGCSafeMethodTable();
            if (methodTable is null || !methodTable->ContainsGCPointers())
            {
                return;
            }

            nint seriesCount = GetSeriesCount(methodTable);
            CGCDescSeries* series = GetHighestSeries(methodTable);
            if (seriesCount >= 0)
            {
                CGCDescSeries* lowest = GetLowestSeries(methodTable, seriesCount);
                do
                {
                    byte* slot = (byte*)obj + (nint)series->startoffset;
                    byte* stop = slot + (nint)series->seriessize + objectSize;
                    while (slot < stop)
                    {
                        RelocateShortenedReference(
                            (byte**)slot,
                            end,
                            savedPlugInfoStart,
                            savedInfoToRelocate);
                        slot += sizeof(void*);
                    }

                    series--;
                }
                while (series >= lowest);

                return;
            }

            byte* repeatingSlot = (byte*)obj + (nint)series->startoffset;
            byte* objectEnd = (byte*)obj + objectSize - sizeof(ObjHeader);
            val_serie_item* valueSeries = &series->val_serie;
            while (repeatingSlot < objectEnd)
            {
                for (nint i = 0; i > seriesCount; i--)
                {
                    val_serie_item item = valueSeries[i];
                    byte* stop = repeatingSlot + (nint)item.nptrs * sizeof(void*);
                    while (repeatingSlot < stop)
                    {
                        RelocateShortenedReference(
                            (byte**)repeatingSlot,
                            end,
                            savedPlugInfoStart,
                            savedInfoToRelocate);
                        repeatingSlot += sizeof(void*);
                    }

                    repeatingSlot += item.skip;
                }
            }
        }

        private static void RelocateShortenedReference(
            byte** reference,
            byte* end,
            byte* savedPlugInfoStart,
            byte** savedInfoToRelocate)
        {
            if ((byte*)reference >= end)
            {
                nuint index = (nuint)(((byte*)reference - savedPlugInfoStart) / (nint)sizeof(byte*));
                RelocateAddress(savedInfoToRelocate + index);
            }
            else
            {
                RelocateAddress(reference);
            }
        }

        private static void RelocateSurvivorHelper(
            byte* plug,
            byte* plugEnd,
            bool allowTerminalGap)
        {
            byte* current = plug;
            while (current < plugEnd)
            {
                Object* obj = (Object*)current;
                if (obj->RawGetMethodTable() is null)
                {
                    byte* next = FindNextObjectAfterGap(current, plugEnd);
                    if (next is null)
                    {
                        if (allowTerminalGap)
                        {
                            return;
                        }

                        FailFast();
                        return;
                    }

                    if (next <= current)
                    {
                        FailFast();
                        return;
                    }

                    current = next;
                    continue;
                }

                nuint size = GetObjectSize(obj);
                nuint alignedSize = GCEnvironment.AlignUp(size, (nuint)sizeof(void*));
                if (size < MinObjectSize || alignedSize < size ||
                    alignedSize > (nuint)(plugEnd - current))
                {
                    FailFast();
                    return;
                }

                RelocateObjectReferences(obj, null, null);
                current += (nint)alignedSize;
            }
        }

        private static void RelocateShortenedSurvivorHelper(
            byte* plug,
            byte* plugEnd,
            mark* pinnedPlugEntry,
            bool allowTerminalGap)
        {
            if (pinnedPlugEntry is null)
            {
                RelocateSurvivorHelper(plug, plugEnd, allowTerminalGap);
                return;
            }

            byte* pinnedPlug = pinnedPlugEntry->first;
            bool isPinned = plug == pinnedPlug;
            bool checkShortObject = isPinned
                ? pinnedPlugEntry->PostShortP()
                : pinnedPlugEntry->PreShortP();
            byte* current = plug;
            byte* shortenedEnd = plugEnd + sizeof(gap_reloc_pair);
            byte* savedPlugInfoStart = isPinned
                ? pinnedPlugEntry->saved_post_plug_info_start
                : pinnedPlug - sizeof(plug_and_gap);
            byte** savedInfoToRelocate = isPinned
                ? (byte**)&pinnedPlugEntry->saved_post_plug_reloc
                : (byte**)&pinnedPlugEntry->saved_pre_plug_reloc;

            while (current < shortenedEnd)
            {
                Object* obj = (Object*)current;
                if (obj->RawGetMethodTable() is null)
                {
                    byte* next = FindNextObjectAfterGap(current, shortenedEnd);
                    if (next is null)
                    {
                        if (allowTerminalGap)
                        {
                            return;
                        }

                        FailFast();
                        return;
                    }

                    if (next <= current)
                    {
                        FailFast();
                        return;
                    }

                    current = next;
                    continue;
                }

                if (checkShortObject &&
                    (nuint)(shortenedEnd - current) <
                        (nuint)sizeof(gap_reloc_pair) + MinObjectSize)
                {
                    if (!isPinned)
                    {
                        RelocatePrePlugInfo(pinnedPlugEntry);
                    }

                    for (nuint i = 0; i < mark.GetMaxShortBits(); i++)
                    {
                        bool set = isPinned
                            ? pinnedPlugEntry->PostShortBitP(i)
                            : pinnedPlugEntry->PreShortBitP(i);
                        if (set)
                        {
                            RelocateAddress(savedInfoToRelocate + i);
                        }
                    }

                    return;
                }

                nuint size = GetObjectSize(obj);
                nuint alignedSize = GCEnvironment.AlignUp(size, (nuint)sizeof(void*));
                if (size < MinObjectSize || alignedSize < size ||
                    alignedSize > (nuint)(shortenedEnd - current))
                {
                    return;
                }

                byte* objectEnd = current + (nint)alignedSize;
                if (objectEnd >= shortenedEnd)
                {
                    if (!isPinned)
                    {
                        RelocatePrePlugInfo(pinnedPlugEntry);
                    }

                    RelocateShortenedObjectReferences(
                        obj,
                        size,
                        current + (nint)alignedSize - sizeof(plug_and_gap),
                        savedPlugInfoStart,
                        savedInfoToRelocate);
                }
                else
                {
                    RelocateObjectReferences(obj, null, null);
                }

                current = objectEnd;
            }
        }

        private static void RelocatePrePlugInfo(mark* pinnedPlugEntry)
        {
            byte* plug = pinnedPlugEntry->first;
            byte* prePlugStart = plug - sizeof(plug_and_gap) + sizeof(void*);
            RelocateAddress(&prePlugStart);
            pinnedPlugEntry->saved_pre_plug_info_reloc_start =
                prePlugStart - sizeof(void*);
        }

        private static void RelocateSurvivorsInPlug(
            byte* plug,
            byte* plugEnd,
            bool checkLastObject,
            mark* pinnedPlugEntry,
            bool allowTerminalGap)
        {
            if (checkLastObject)
            {
                RelocateShortenedSurvivorHelper(
                    plug,
                    plugEnd,
                    pinnedPlugEntry,
                    allowTerminalGap);
            }
            else
            {
                RelocateSurvivorHelper(plug, plugEnd, allowTerminalGap);
            }
        }

        private static void RelocateSurvivorsInBrick(byte* tree, ref RelocateArgs args)
        {
            if (tree is null)
            {
                return;
            }

            nint left = GetNodeLeftChild(tree);
            if (left != 0)
            {
                RelocateSurvivorsInBrick(tree + left, ref args);
            }

            bool hasPrePlugInfo = false;
            bool hasPostPlugInfo = false;
            if (!PinnedPlugQueueEmpty() && tree == OldestPin()->first)
            {
                args.PinnedPlugEntry = OldestPin();
                hasPrePlugInfo = args.PinnedPlugEntry->HasPrePlugInfo();
                hasPostPlugInfo = args.PinnedPlugEntry->HasPostPlugInfo();
                DequeuePinnedPlug();
            }

            if (args.LastPlug is not null)
            {
                nuint gapSize = (nuint)GetNodeGapSize(tree);
                byte* gap = tree - (nint)gapSize;
                RelocateSurvivorsInPlug(
                    args.LastPlug,
                    gap,
                    args.IsShortened || hasPrePlugInfo,
                    args.PinnedPlugEntry,
                    false);
            }

            args.LastPlug = tree;
            args.IsShortened = hasPostPlugInfo;

            nint right = GetNodeRightChild(tree);
            if (right != 0)
            {
                RelocateSurvivorsInBrick(tree + right, ref args);
            }
        }

        private static void RelocateSurvivors(int condemnedGeneration, byte* firstCondemnedAddress)
        {
            ResetPinnedQueueBos();
            int stopGeneration = GetStopGenerationIndex(condemnedGeneration);
            for (int generationNumber = condemnedGeneration;
                generationNumber >= stopGeneration;
                generationNumber--)
            {
                generation* generationState = GetGeneration(generationNumber);
                if (generationState is null || generationState->start_segment is null)
                {
                    return;
                }

                heap_segment* segment = HeapSegmentRw(generationState->start_segment);
                fixed (heap_segment* sohSegment = &s_sohSegment)
                {
                    bool firstSegment = true;
                    while (segment is not null)
                    {
                        byte* start = firstSegment
                            ? (segment == sohSegment && generationNumber == condemnedGeneration
                                ? firstCondemnedAddress
                                : GetSohStartObject(segment, generationState))
                            : segment->mem;
                        byte* end = segment->allocated;
                        if (start is null || end is null || start >= end)
                        {
                            firstSegment = false;
                            segment = HeapSegmentNextRw(segment);
                            continue;
                        }

                        RelocateArgs args = default;
                        nuint brick = GetBrickIndex(start);
                        nuint endBrick = GetBrickIndex(end - 1);
                        while (brick <= endBrick)
                        {
                            int brickEntry = s_brickTable is null ? 0 : s_brickTable[brick];
                            if (brickEntry > 0)
                            {
                                RelocateSurvivorsInBrick(
                                    BrickAddress(brick) + brickEntry - 1,
                                    ref args);
                            }

                            brick++;
                        }

                        if (args.LastPlug is not null)
                        {
                            RelocateSurvivorsInPlug(
                                args.LastPlug,
                                end,
                                args.IsShortened,
                                args.PinnedPlugEntry,
                                true);
                        }

                        firstSegment = false;
                        segment = HeapSegmentNextRw(segment);
                    }
                }
            }
        }

        private static void RelocateCardsInSegment(heap_segment* segment, byte* begin, byte* end)
        {
            byte* current = begin;
            while (current < end)
            {
                Object* obj = (Object*)current;
                if (obj->RawGetMethodTable() is null)
                {
                    byte* next = FindNextObjectAfterGap(current, segment->allocated);
                    if (next is null)
                    {
                        return;
                    }

                    if (next <= current)
                    {
                        FailFast();
                        return;
                    }

                    if (next >= end)
                    {
                        return;
                    }

                    current = next;
                    continue;
                }

                nuint size = GetObjectSize(obj);
                nuint alignedSize = GCEnvironment.AlignUp(size, (nuint)sizeof(void*));
                if (size < MinObjectSize || alignedSize < size ||
                    alignedSize > (nuint)(segment->allocated - current))
                {
                    FailFast();
                    return;
                }

                RelocateObjectReferences(obj, null, null);
                current += (nint)alignedSize;
            }

            _ = segment;
        }

        private static void RelocateCardsForSegments(byte* firstCondemnedAddress)
        {
            if (s_generation2.start_segment is null)
            {
                return;
            }

            heap_segment* segment = HeapSegmentRw(s_generation2.start_segment);
            fixed (heap_segment* sohSegment = &s_sohSegment)
            {
                bool firstSegment = true;
                while (segment is not null)
                {
                    if (segment->mem is null || segment->allocated is null)
                    {
                        return;
                    }

                    byte* begin = firstSegment
                        ? s_generation2.allocation_start
                        : segment->mem;
                    byte* end = segment == sohSegment
                        ? firstCondemnedAddress
                        : segment->allocated;
                    if (begin is not null && end is not null && begin < end)
                    {
                        RelocateCardsInSegment(segment, begin, end);
                    }

                    firstSegment = false;
                    segment = HeapSegmentNextRw(segment);
                }
            }
        }

        private static void RelocateCardsForUohObjects(int generationNumber)
        {
            generation* generationState = GetGeneration(generationNumber);
            if (generationState is null || generationState->start_segment is null)
            {
                return;
            }

            heap_segment* segment = HeapSegmentRw(generationState->start_segment);
            bool firstSegment = true;
            while (segment is not null)
            {
                if (segment->mem is null || segment->allocated is null)
                {
                    return;
                }

                byte* begin = firstSegment
                    ? GetUohStartObjectForMarking(generationState)
                    : segment->mem;
                if (begin is not null && begin < segment->allocated)
                {
                    RelocateCardsInSegment(segment, begin, segment->allocated);
                }

                firstSegment = false;
                segment = HeapSegmentNextRw(segment);
            }
        }

        private static void RelocateFinalizationQueue()
        {
            if (s_finalizeQueue is null)
            {
                return;
            }

            for (int segment = FinalizerStartSegment; segment <= FinalizerMaxSegment; segment++)
            {
                Object** current = GetQueueStart(s_finalizeQueue, segment);
                Object** end = GetQueueLimit(s_finalizeQueue, segment);
                while (current < end)
                {
                    RelocateAddress((byte**)current);
                    current++;
                }
            }
        }

        private static int RelocatePhase(int condemnedGeneration, byte* firstCondemnedAddress)
        {
            ScanContext scanContext = default;
            scanContext.thread_number = 0;
            scanContext.thread_count = 1;
            scanContext.promotion = false;
            scanContext.concurrent = false;

            IGCToCLR* callback = GCCommon.g_theGCToCLR;
            if (callback is null || callback->Vtable is null)
            {
                return E_FAIL;
            }

            if (callback->Vtable->GcScanRoots is not null)
            {
                callback->Vtable->GcScanRoots(
                    callback,
                    &RelocateHandle,
                    condemnedGeneration,
                    (int)gc_generation_num.max_generation,
                    &scanContext);
            }

            RelocateSurvivors(condemnedGeneration, firstCondemnedAddress);
            RelocateFinalizationQueue();
            GCHandleTables.ScanForRelocation(&RelocateHandle, &scanContext);

            if (condemnedGeneration != (int)gc_generation_num.max_generation)
            {
                RelocateCardsForSegments(firstCondemnedAddress);
                for (int generationNumber = (int)gc_generation_num.uoh_start_generation;
                    generationNumber < (int)gc_generation_num.total_generation_count;
                    generationNumber++)
                {
                    RelocateCardsForUohObjects(generationNumber);
                }
            }
            else
            {
                for (int generationNumber = (int)gc_generation_num.uoh_start_generation;
                    generationNumber < (int)gc_generation_num.total_generation_count;
                    generationNumber++)
                {
                    RelocateCardsForUohObjects(generationNumber);
                }
            }

            return S_OK;
        }

        private static void RelocateHandle(Object** reference, ScanContext* scanContext, uint flags)
        {
            _ = scanContext;
            _ = flags;
            RelocateAddress((byte**)reference);
        }
    }
}
