// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Internal.Runtime.GC
{
    internal static unsafe partial class GCWksInitialization
    {
        private static int RunPlanPhaseCore(int condemnedGeneration)
        {
            if (condemnedGeneration != (int)gc_generation_num.max_generation)
            {
                return E_NOTIMPL;
            }

            return PlanPhase(condemnedGeneration);
        }

        private static int PlanPhase(int condemnedGeneration)
        {
            // This is the segment-GC/WKS portion of gc_heap::plan_phase, beginning at
            // plan_phase.cpp:3315. The current source-ordered boundaries are before
            // save_allocated at plan_phase.cpp:3773 for an exhausted segment and
            // before new_address at plan_phase.cpp:3987 after the marked-plug scan.
            generation* condemnedGenerationState = GetGeneration(condemnedGeneration);
            if (condemnedGenerationState is null)
            {
                return E_FAIL;
            }

            s_savedPinnedPlugIndex = nuint.MaxValue;

            bool useMarkList = false;
            if (s_markListIndex >= s_markListLength)
            {
                s_markListIndex = s_markListLength;
            }

            if (condemnedGeneration < (int)gc_generation_num.max_generation &&
                s_markListIndex < s_markListLength)
            {
                return E_NOTIMPL;
            }

            s_useMarkList = useMarkList;

            int sweepResult = SweepReadOnlySegments();
            if (sweepResult != S_OK)
            {
                return sweepResult;
            }

            byte* lowestMarkedAddress = s_lowestMarkedAddress;
            byte* highestMarkedAddress = s_highestMarkedAddress;
            int condemnedGenerationIndex = GetStopGenerationIndex(condemnedGeneration);
            for (; condemnedGenerationIndex <= condemnedGeneration; condemnedGenerationIndex++)
            {
                generation* currentGeneration = GetGeneration(condemnedGenerationIndex);
                if (currentGeneration is null)
                {
                    return E_FAIL;
                }

                int result = PrepareGenerationSegments(
                    currentGeneration,
                    lowestMarkedAddress,
                    highestMarkedAddress);
                if (result != S_OK)
                {
                    return result;
                }
            }

            heap_segment* condemnedSegment = condemnedGenerationState->start_segment;
            if (condemnedSegment is null)
            {
                return E_FAIL;
            }

            byte* firstCondemnedAddress = GetSohStartObject(condemnedSegment, condemnedGenerationState);
            if (firstCondemnedAddress is null)
            {
                return E_FAIL;
            }

            if (((Object*)firstCondemnedAddress)->IsMarked())
            {
                return E_FAIL;
            }

            ResetPlanAllocation(condemnedGeneration, condemnedGenerationState);

            // allocation.cpp:4524 has no behavior in the selected !FREE_USAGE_STATS
            // WKS variant, so the native plan_phase body continues directly here.
            int condemnedGenerationNumber = condemnedGeneration;
            int bottomGeneration = 0;
            bool allocateInCondemned = true;
            int activeOldGenerationNumber = condemnedGenerationNumber;
            int activeNewGenerationNumber = condemnedGenerationNumber;
            generation* olderGeneration = null;
            generation* consingGeneration = condemnedGenerationState;
            bool allocateFirstGenerationStart = allocateInCondemned;
            bool decidePromoteGen1Pins = false;

            heap_segment* segment = condemnedSegment;
            byte* end = segment->allocated;
            byte* x = firstCondemnedAddress;
            Object** markListNext = s_markList;
            Object** markListIndex = s_markList is null
                ? null
                : s_markList + (nint)s_markListIndex;
            byte* plugEnd = x;
            byte* tree = null;
            nuint sequenceNumber = 0;
            byte* lastNode = null;
            nuint currentBrick = GetBrickIndex(x);
            nuint lastPlugLength = 0;
            bool lastNonPinnedPlug = false;
            bool lastPinnedPlugState = false;
            byte* lastPinnedPlug = null;
            nuint numPinnedPlugsInPlug = 0;
            byte* lastObjectInPlug = null;

            if (condemnedGenerationNumber < (int)gc_generation_num.max_generation)
            {
                olderGeneration = GetGeneration(condemnedGenerationNumber + 1);
                if (olderGeneration is null)
                {
                    return E_FAIL;
                }

                if (olderGeneration->gen_num == (int)gc_generation_num.max_generation)
                {
                    olderGeneration->set_bgc_mark_bit_p = 0;
                    olderGeneration->last_free_list_allocated = null;
                }
            }

            while (condemnedGenerationNumber >= bottomGeneration)
            {
                generation* currentGeneration = GetGeneration(condemnedGenerationNumber);
                if (currentGeneration is null ||
                    !ResetCondemnedGenerationState(currentGeneration) ||
                    currentGeneration->start_segment is null)
                {
                    return E_FAIL;
                }

                currentGeneration->allocation_segment = currentGeneration->start_segment;
                heap_segment* allocationSegment = currentGeneration->allocation_segment;
                byte* allocationPointer;
                fixed (heap_segment* ephemeralSegment = &s_sohSegment)
                {
                    allocationPointer = allocationSegment == ephemeralSegment
                        ? currentGeneration->allocation_start
                        : allocationSegment->mem;
                }

                currentGeneration->allocation_context.alloc_ptr = allocationPointer;
                currentGeneration->allocation_context.alloc_limit = allocationPointer;
                currentGeneration->allocation_context_start_region = allocationPointer;
                condemnedGenerationNumber--;
            }

            _ = activeNewGenerationNumber;
            _ = olderGeneration;
            _ = decidePromoteGen1Pins;

            while (true)
            {
                if (x >= end)
                {
                    // The selected source next mutates segment allocation and brick
                    // state at plan_phase.cpp:3773; stop before that boundary.
                    return E_NOTIMPL;
                }

                while (x < end && ((Object*)x)->IsMarked())
                {
                    byte* plugStart = x;
                    byte* savedPlugEnd = plugEnd;
                    bool pinnedPlug = false;
                    bool nonPinnedBeforePinned = false;
                    bool savedLastNonPinnedPlug = lastNonPinnedPlug;
                    byte* savedLastObjectInPlug = lastObjectInPlug;
                    bool mergeWithLastPin = false;
                    nuint addedPinningSize = 0;
                    nuint artificialPinnedSize = 0;

                    int storePlugGapInfoResult = StorePlugGapInfo(
                        plugStart,
                        savedPlugEnd,
                        ref lastNonPinnedPlug,
                        ref lastPinnedPlugState,
                        ref lastPinnedPlug,
                        ref pinnedPlug,
                        lastObjectInPlug,
                        ref mergeWithLastPin,
                        lastPlugLength);
                    if (storePlugGapInfoResult != S_OK)
                    {
                        return storePlugGapInfoResult;
                    }

                    {
                        byte* xl = x;
                        while (xl < end &&
                               ((Object*)xl)->IsMarked() &&
                               (IsPinnedObject(xl) == pinnedPlug))
                        {
                            System.Diagnostics.Debug.Assert(xl < end);

                            if (IsPinnedObject(xl))
                            {
                                ClearPinned(xl);
                            }

                            ((Object*)xl)->ClearMarked();

                            if (!TryGetAlignedObjectInfo(xl, end, out _, out _, out byte* nextObject))
                            {
                                FailFast();
                                return E_FAIL;
                            }

                            lastObjectInPlug = xl;
                            xl = nextObject;
                        }

                        bool nextObjectMarked = xl < end && ((Object*)xl)->IsMarked();
                        if (nextObjectMarked && IsPinnedObject(xl) == pinnedPlug)
                        {
                            FailFast();
                            return E_FAIL;
                        }

                        if (pinnedPlug)
                        {
                            if (nextObjectMarked)
                            {
                                ((Object*)xl)->ClearMarked();

                                if (!TryGetAlignedObjectInfo(xl, end, out _, out nuint extraSize, out byte* nextObject))
                                {
                                    FailFast();
                                    return E_FAIL;
                                }

                                lastObjectInPlug = xl;
                                xl = nextObject;
                                addedPinningSize = extraSize;
                            }
                        }
                        else if (nextObjectMarked)
                        {
                            nonPinnedBeforePinned = true;
                        }

                        if (xl > end)
                        {
                            FailFast();
                            return E_FAIL;
                        }

                        x = xl;
                    }

                    plugEnd = x;
                    if (plugEnd < plugStart)
                    {
                        FailFast();
                        return E_FAIL;
                    }

                    nuint ps = (nuint)(plugEnd - plugStart);
                    lastPlugLength = ps;
                    byte* newAddress = null;

                    if (!pinnedPlug)
                    {
                        nuint pageSize = GCToOSInterface.GetPageSize();

                        if (allocateInCondemned &&
                            condemnedGeneration == (int)gc_generation_num.max_generation &&
                            ps > pageSize)
                        {
                            nint reloc = checked((nint)(plugStart - consingGeneration->allocation_context.alloc_ptr));
                            nuint eightPageSize = checked(pageSize * (nuint)8);
                            if (ps > eightPageSize &&
                                reloc > 0 &&
                                (nuint)reloc < (ps / 16))
                            {
                                System.Diagnostics.Debug.Assert(!savedLastNonPinnedPlug);

                                if (lastPinnedPlug is not null)
                                {
                                    mergeWithLastPin = true;
                                }
                                else
                                {
                                    EnquePinnedPlug(plugStart, false, null);
                                    lastPinnedPlug = plugStart;
                                }

                                ConvertToPinnedPlug(
                                    ref lastNonPinnedPlug,
                                    ref lastPinnedPlugState,
                                    ref pinnedPlug,
                                    ps,
                                    ref artificialPinnedSize);
                            }
                        }
                    }

                    _ = nonPinnedBeforePinned;
                    _ = addedPinningSize;
                    _ = savedLastObjectInPlug;
                    _ = activeOldGenerationNumber;
                    _ = currentBrick;
                    _ = tree;
                    _ = sequenceNumber;
                    _ = lastNode;
                    _ = numPinnedPlugsInPlug;
                    // E_NOTIMPL boundary before #ifndef USE_REGIONS at plan_phase.cpp:4026;
                    // the next selected statement is if (allocate_first_generation_start),
                    // calling plan_generation_start at plan_phase.cpp:4030.
                    return E_NOTIMPL;
                }

                x = FindNextMarked(x, end, s_useMarkList, ref markListNext, markListIndex);
                if (x is null)
                {
                    return E_FAIL;
                }
            }
        }

        private static void ConvertToPinnedPlug(
            ref bool lastNonPinnedPlug,
            ref bool lastPinnedPlugState,
            ref bool pinnedPlug,
            nuint ps,
            ref nuint artificialPinnedSize)
        {
            lastNonPinnedPlug = false;
            lastPinnedPlugState = true;
            pinnedPlug = true;
            artificialPinnedSize = ps;
        }

        private static void SetGapSize(byte* node, nuint size)
        {
            System.Diagnostics.Debug.Assert(GCEnvironment.AlignUp(size, (nuint)sizeof(void*)) == size);
            System.Diagnostics.Debug.Assert(size == 0 || size >= (nuint)sizeof(plug_and_reloc));

            plug_and_gap* plugAndGap = ((plug_and_gap*)node) - 1;
            plugAndGap->reloc = 0;
            plugAndGap->lr = 0;
            plugAndGap->gap = (nint)size;
        }

        private static int StorePlugGapInfo(
            byte* plugStart,
            byte* plugEnd,
            ref bool lastNonPinnedPlug,
            ref bool lastPinnedPlugState,
            ref byte* lastPinnedPlug,
            ref bool pinnedPlug,
            byte* lastObjectInLastPlug,
            ref bool mergeWithLastPin,
            nuint lastPlugLength)
        {
            generation* maxGeneration = GetGeneration((int)gc_generation_num.max_generation);
            if (maxGeneration is null)
            {
                return E_FAIL;
            }

            _ = lastPlugLength;

            if (!lastNonPinnedPlug && !lastPinnedPlugState)
            {
                System.Diagnostics.Debug.Assert(
                    plugStart == plugEnd ||
                    (nuint)(plugStart - plugEnd) >= GCEnvironment.AlignUp(MinObjectSize, (nuint)sizeof(void*)));
                SetGapSize(plugStart, (nuint)(plugStart - plugEnd));
            }

            if (IsPinnedObject(plugStart))
            {
                bool savePrePlugInfo = false;
                if (lastNonPinnedPlug || lastPinnedPlugState)
                {
                    savePrePlugInfo = true;
                }

                pinnedPlug = true;
                lastNonPinnedPlug = false;

                if (lastPinnedPlugState)
                {
                    mergeWithLastPin = true;
                }
                else
                {
                    lastPinnedPlugState = true;
                    lastPinnedPlug = plugStart;
                    EnquePinnedPlug(lastPinnedPlug, savePrePlugInfo, lastObjectInLastPlug);

                    if (savePrePlugInfo)
                    {
                        if (lastObjectInLastPlug == maxGeneration->last_free_list_allocated)
                        {
                            s_savedPinnedPlugIndex = s_markStackTos;
                        }

                        SetGapSize(plugStart, (nuint)sizeof(gap_reloc_pair));
                    }
                }
            }
            else
            {
                if (lastPinnedPlugState)
                {
                    SavePostPlugInfo(lastPinnedPlug, lastObjectInLastPlug, plugStart);
                    SetGapSize(plugStart, (nuint)sizeof(gap_reloc_pair));
                }

                lastNonPinnedPlug = true;
                lastPinnedPlugState = false;
            }

            return S_OK;
        }

        private static void ClearPinned(byte* node)
        {
            ((Object*)node)->GetHeader()->ClrGCBit();
        }

        private static bool TryGetAlignedObjectInfo(
            byte* objectAddress,
            byte* end,
            out nuint objectSize,
            out nuint alignedObjectSize,
            out byte* nextObject)
        {
            objectSize = 0;
            alignedObjectSize = 0;
            nextObject = null;

            if (objectAddress is null ||
                end is null ||
                objectAddress >= end)
            {
                return false;
            }

            objectSize = GetObjectSize((Object*)objectAddress);
            nuint alignmentMask = (nuint)sizeof(void*) - 1;
            if (objectSize < MinObjectSize ||
                objectSize > s_lohThreshold ||
                objectSize > nuint.MaxValue - alignmentMask)
            {
                return false;
            }

            alignedObjectSize = GCEnvironment.AlignUp(objectSize, (nuint)sizeof(void*));
            if (alignedObjectSize < objectSize ||
                alignedObjectSize > (nuint)(end - objectAddress))
            {
                return false;
            }

            return TryAddPointer(objectAddress, alignedObjectSize, out nextObject);
        }

        private static int PrepareGenerationSegments(
            generation* currentGeneration,
            byte* lowestMarkedAddress,
            byte* highestMarkedAddress)
        {
            heap_segment* segment = currentGeneration->start_segment;
            if (segment is null)
            {
                return E_FAIL;
            }

            heap_segment* firstSegment = segment;
            do
            {
                segment->saved_allocated = null;

                if (highestMarkedAddress is not null)
                {
                    if (IsAddressInSegment(lowestMarkedAddress, segment))
                    {
                        byte* startUnmarked = null;
                        if (segment == firstSegment)
                        {
                            byte* startObject = GetSohStartObject(segment, currentGeneration);
                            if (!TryAddPointer(startObject, GetSohStartObjectLength(startObject), out startUnmarked))
                            {
                                return E_FAIL;
                            }

                            if (lowestMarkedAddress > startObject)
                            {
                                if (lowestMarkedAddress < startUnmarked)
                                {
                                    return E_FAIL;
                                }
                            }
                        }
                        else
                        {
                            startUnmarked = segment->mem;
                        }

                        if (startUnmarked is not null && lowestMarkedAddress > startUnmarked)
                        {
                            nuint unmarkedSize = (nuint)(lowestMarkedAddress - startUnmarked);
                            if (unmarkedSize != 0)
                            {
                                if (unmarkedSize < MinObjectSize)
                                {
                                    return E_FAIL;
                                }

                                FormatUnusedArray(startUnmarked, unmarkedSize);
                            }
                        }
                    }

                    if (IsAddressInSegment(highestMarkedAddress, segment))
                    {
                        if (!TryGetAlignedObjectEnd(highestMarkedAddress, segment->allocated, out byte* newAllocated))
                        {
                            return E_FAIL;
                        }

                        SaveAllocated(segment);
                        segment->allocated = newAllocated;
                    }

                    if (!IntersectsAddressRange(segment, lowestMarkedAddress, highestMarkedAddress))
                    {
                        SaveAllocated(segment);
                        segment->allocated = segment->mem;
                    }
                }
                else
                {
                    byte* startUnmarked = segment->mem;
                    if (segment == firstSegment)
                    {
                        byte* startObject = GetSohStartObject(segment, currentGeneration);
                        if (!TryAddPointer(startObject, GetSohStartObjectLength(startObject), out startUnmarked))
                        {
                            return E_FAIL;
                        }
                    }

                    SaveAllocated(segment);
                    segment->allocated = startUnmarked;
                }

                segment = segment->next;
            }
            while (segment is not null);

            return S_OK;
        }

        private static void ResetPlanAllocation(int condemnedGeneration, generation* condemnedGenerationState)
        {
            for (int generationNumber = GetStopGenerationIndex(condemnedGeneration);
                generationNumber <= condemnedGeneration;
                generationNumber++)
            {
                generation* currentGeneration = GetGeneration(generationNumber);
                heap_segment* segment = currentGeneration->start_segment;
                while (segment is not null)
                {
                    segment->plan_allocated = segment->mem;
                    segment = segment->next;
                }
            }

            condemnedGenerationState->allocation_segment = condemnedGenerationState->start_segment;
            heap_segment* allocationSegment = condemnedGenerationState->allocation_segment;
            byte* allocationPointer;
            fixed (heap_segment* ephemeralSegment = &s_sohSegment)
            {
                allocationPointer = allocationSegment == ephemeralSegment
                    ? condemnedGenerationState->allocation_start
                    : allocationSegment->mem;
            }

            condemnedGenerationState->allocation_context.alloc_ptr = allocationPointer;
            condemnedGenerationState->allocation_context.alloc_limit = allocationPointer;
            condemnedGenerationState->allocation_context_start_region = allocationPointer;
        }

        private static bool ResetCondemnedGenerationState(generation* generationState)
        {
            if (generationState->start_segment is null)
            {
                return false;
            }

            ClearAllocator(&generationState->free_list_allocator);
            generationState->free_list_space = 0;
            generationState->free_obj_space = 0;
            generationState->allocation_size = 0;
            generationState->condemned_allocated = 0;
            generationState->sweep_allocated = 0;
            generationState->free_list_allocated = 0;
            generationState->end_seg_allocated = 0;
            generationState->pinned_allocation_sweep_size = 0;
            generationState->pinned_allocation_compact_size = 0;
            generationState->plan_allocation_start = null;
            return true;
        }

        private static void ClearAllocator(allocator* allocatorState)
        {
            for (uint bucket = 0; bucket < allocatorState->num_buckets; bucket++)
            {
                alloc_list* list;
                if (bucket == 0)
                {
                    list = &allocatorState->first_bucket;
                }
                else
                {
                    if (allocatorState->buckets is null)
                    {
                        FailFast();
                        return;
                    }

                    list = allocatorState->buckets + (bucket - 1);
                }

                list->head = null;
                list->tail = null;
            }
        }

        private static generation* GetGeneration(int generationNumber)
        {
            switch (generationNumber)
            {
                case (int)gc_generation_num.soh_gen0:
                    fixed (generation* generationState = &s_generation0)
                    {
                        return generationState;
                    }
                case (int)gc_generation_num.soh_gen1:
                    fixed (generation* generationState = &s_generation1)
                    {
                        return generationState;
                    }
                case (int)gc_generation_num.soh_gen2:
                    fixed (generation* generationState = &s_generation2)
                    {
                        return generationState;
                    }
                case (int)gc_generation_num.loh_generation:
                    fixed (generation* generationState = &s_lohGeneration)
                    {
                        return generationState;
                    }
                case (int)gc_generation_num.poh_generation:
                    fixed (generation* generationState = &s_pohGeneration)
                    {
                        return generationState;
                    }
                default:
                    return null;
            }
        }

        private static nuint GetGenerationFreeListAllocated(generation* generationState)
        {
            return generationState->free_list_allocated;
        }

        private static nuint GetGenerationSize(int generationNumber)
        {
            generation* generationState = GetGeneration(generationNumber);
            if (generationState is null)
            {
                return 0;
            }

            if (generationNumber == 0)
            {
                byte* allocated = generationState->start_segment->allocated;
                byte* start = generationState->allocation_start;
                nuint size = allocated >= start ? (nuint)(allocated - start) : 0;
                return size < MinObjectSize ? MinObjectSize : size;
            }

            fixed (heap_segment* ephemeralSegment = &s_sohSegment)
            {
                if (generationState->start_segment == ephemeralSegment)
                {
                    generation* youngerGeneration = GetGeneration(generationNumber - 1);
                    return youngerGeneration->allocation_start >= generationState->allocation_start
                        ? (nuint)(youngerGeneration->allocation_start - generationState->allocation_start)
                        : 0;
                }
            }

            nuint result = 0;
            for (heap_segment* segment = generationState->start_segment;
                segment is not null;
                segment = segment->next)
            {
                if (segment->allocated < segment->mem)
                {
                    return 0;
                }

                result += (nuint)(segment->allocated - segment->mem);
            }

            return result;
        }

        private static int GetStopGenerationIndex(int condemnedGeneration)
        {
            // USE_REGIONS is not selected for this transliteration. Segment WKS starts
            // planning at the condemned generation.
            return condemnedGeneration;
        }

        private static byte* GetSohStartObject(heap_segment* segment, generation* generationState)
        {
            _ = segment;
            return generationState->allocation_start;
        }

        private static nuint GetSohStartObjectLength(byte* startObject)
        {
            return GCEnvironment.AlignUp(GetObjectSize((Object*)startObject), (nuint)sizeof(void*));
        }

        private static int SweepReadOnlySegments()
        {
            for (heap_segment* segment = s_generation2.start_segment;
                segment is not null && (segment->flags & HeapSegmentReadOnly) != 0;
                segment = segment->next)
            {
                if (s_markLow is null ||
                    s_markHigh is null ||
                    segment->mem is null ||
                    segment->allocated is null ||
                    segment->reserved is null ||
                    segment->reserved <= s_markLow ||
                    segment->mem >= s_markHigh)
                {
                    continue;
                }

                byte* current = segment->mem;
                while (current < segment->allocated)
                {
                    Object* obj = (Object*)current;
                    if (obj->IsMarked())
                    {
                        obj->ClearMarked();
                    }

                    nuint size = GetObjectSize(obj);
                    if (size < MinObjectSize || size > (nuint)(segment->allocated - current))
                    {
                        FailFast();
                        return E_FAIL;
                    }

                    nuint alignedSize = GCEnvironment.AlignUp(size, (nuint)sizeof(void*));
                    if (alignedSize < size || alignedSize > (nuint)(segment->allocated - current))
                    {
                        FailFast();
                        return E_FAIL;
                    }

                    current += alignedSize;
                }
            }

            return S_OK;
        }

        private static bool IsAddressInSegment(byte* address, heap_segment* segment)
        {
            return address is not null &&
                segment is not null &&
                segment->mem is not null &&
                segment->reserved is not null &&
                address >= segment->mem &&
                address < segment->reserved;
        }

        private static bool IntersectsAddressRange(
            heap_segment* segment,
            byte* lowestMarkedAddress,
            byte* highestMarkedAddress)
        {
            return segment->reserved >= lowestMarkedAddress &&
                segment->mem <= highestMarkedAddress;
        }

        private static void SaveAllocated(heap_segment* segment)
        {
            segment->saved_allocated = segment->allocated;
        }

        private static bool TryGetAlignedObjectEnd(byte* objectAddress, byte* allocated, out byte* end)
        {
            end = null;
            if (objectAddress is null ||
                allocated is null ||
                objectAddress >= allocated)
            {
                return false;
            }

            nuint size = GetObjectSize((Object*)objectAddress);
            nuint alignedSize = GCEnvironment.AlignUp(size, (nuint)sizeof(void*));
            if (size < MinObjectSize ||
                alignedSize < size ||
                alignedSize > (nuint)(allocated - objectAddress))
            {
                return false;
            }

            return TryAddPointer(objectAddress, alignedSize, out end);
        }

        private static byte* FindNextMarked(
            byte* x,
            byte* end,
            bool useMarkList,
            ref Object** markListNext,
            Object** markListIndex)
        {
            if (useMarkList)
            {
                byte* oldX = x;
                while (markListNext < markListIndex && (byte*)*markListNext <= x)
                {
                    markListNext++;
                }

                x = end;
                if (markListNext < markListIndex)
                {
                    x = (byte*)*markListNext;
                }

                _ = oldX;
                return x;
            }

            byte* next = x;
            while (next < end && !((Object*)next)->IsMarked())
            {
                nuint size = GetObjectSize((Object*)next);
                nuint alignedSize = GCEnvironment.AlignUp(size, (nuint)sizeof(void*));
                if (size == 0 || alignedSize < size)
                {
                    return null;
                }

                next += (nint)alignedSize;
            }

            return next;
        }

        private static nuint GetBrickIndex(byte* address)
        {
            if (address is null ||
                GCCommon.g_gc_lowest_address is null ||
                address < GCCommon.g_gc_lowest_address)
            {
                return 0;
            }

            return (nuint)(address - GCCommon.g_gc_lowest_address) / BrickSize;
        }
    }
}
