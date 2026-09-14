// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Internal.Runtime.GC
{
    internal static unsafe partial class GCWksInitialization
    {
        // Translated scope:
        // - Full-gen2 workstation planning for SOH, LOH, and POH.
        // - Non-compacting sweep and free-list rebuilding.
        // - Compaction decisions, with unsupported compaction and expansion rejected
        //   before partially mutating the heap.
        // Relocation, compacting collections, demotion, older-generation free-list
        // allocation, and ephemeral collections remain outside this boundary.
        //
        // Translation workflow:
        // - Generate mechanical drafts with cpp_to_unsafe_csharp.py using the selected
        //   clrgc_gc_wks compilation command.
        // - Have a Luna agent integrate each draft into the unmanaged C# collector.
        // - Review every slice against the authoritative native source. Prefer literal
        //   transliteration over cleanup or independent behavior changes.
        // - Keep collector-owned state in pointers, unmanaged structs, and static
        //   unmanaged storage; do not introduce managed objects or collections.
        // - Validate coherent slices with git diff --check and
        //   ./build.sh clr.aot+libs -rc checked.
        private static int RunPlanPhaseCore(int condemnedGeneration, bool promotion)
        {
            if (condemnedGeneration != (int)gc_generation_num.max_generation)
            {
                return E_NOTIMPL;
            }

            return PlanPhase(condemnedGeneration, promotion);
        }

        private static int PlanPhase(int condemnedGeneration, bool promotion)
        {
            // This is the selected segment-GC/WKS full-gen2 non-compacting path from
            // gc_heap::plan_phase. It includes SOH plug planning, compaction decisions,
            // UOH planning, and the post-plan sweep path.
            generation* condemnedGenerationState = GetGeneration(condemnedGeneration);
            if (condemnedGenerationState is null)
            {
                return E_FAIL;
            }

            if (s_brickTable is null || GCCommon.g_gc_lowest_address is null)
            {
                return E_NOTIMPL;
            }

            s_demotionLow = (byte*)nuint.MaxValue;
            s_maxgenPinnedCompactBeforeAdvance = 0;
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

            heap_segment* condemnedSegment =
                HeapSegmentRw(condemnedGenerationState->start_segment);
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
            bool allocateInCondemned =
                condemnedGenerationNumber == (int)gc_generation_num.max_generation ||
                !promotion;
            int activeOldGenerationNumber = condemnedGenerationNumber;
            int activeNewGenerationNumber = allocateInCondemned
                ? condemnedGenerationNumber
                : condemnedGenerationNumber + 1;
            generation* olderGeneration = null;
            generation* consingGeneration = condemnedGenerationState;
            bool allocateFirstGenerationStart = allocateInCondemned;
            bool decidePromoteGen1Pins = false;
            s_decidePromoteGen1Pins = decidePromoteGen1Pins;

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

                currentGeneration->allocation_segment =
                    HeapSegmentRw(currentGeneration->start_segment);
                heap_segment* allocationSegment = currentGeneration->allocation_segment;
                if (allocationSegment is null)
                {
                    return E_FAIL;
                }
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

            _ = olderGeneration;

            while (true)
            {
                if (x >= end)
                {
                    if (!useMarkList)
                    {
                        System.Diagnostics.Debug.Assert(x == end);
                    }

                    System.Diagnostics.Debug.Assert(segment->allocated == end);
                    SaveAllocated(segment);
                    segment->allocated = plugEnd;
                    currentBrick = UpdateBrickTable(
                        tree,
                        currentBrick,
                        x,
                        plugEnd);
                    sequenceNumber = 0;
                    tree = null;

                    heap_segment* nextSegment = segment->next;
                    if (nextSegment is not null)
                    {
                        segment = nextSegment;
                        end = segment->allocated;
                        plugEnd = x = segment->mem;
                        currentBrick = GetBrickIndex(x);
                        continue;
                    }

                    break;
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

                                if (lastPinnedPlugState)
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
                    _ = numPinnedPlugsInPlug;

                    if (allocateFirstGenerationStart)
                    {
                        allocateFirstGenerationStart = false;
                        int result = PlanGenerationStart(
                            condemnedGenerationState,
                            consingGeneration,
                            promotion,
                            plugStart);
                        if (result != S_OK)
                        {
                            return result;
                        }
                    }

                    fixed (heap_segment* ephemeralSegment = &s_sohSegment)
                    {
                        if (segment == ephemeralSegment)
                        {
                            int result = ProcessEphemeralBoundaries(
                                plugStart,
                                promotion,
                                ref activeNewGenerationNumber,
                                ref activeOldGenerationNumber,
                                ref consingGeneration,
                                ref allocateInCondemned);
                            if (result != S_OK)
                            {
                                return result;
                            }
                        }
                    }

                    dynamic_data* activeOldGenerationData = GetDynamicData(activeOldGenerationNumber);
                    if (activeOldGenerationData is null)
                    {
                        return E_FAIL;
                    }

                    activeOldGenerationData->survived_size += ps;

                    bool convertToPinned = false;
                    if (!pinnedPlug)
                    {
                        if (allocateInCondemned)
                        {
                            int allocationResult = AllocateInCondemnedGenerations(
                                consingGeneration,
                                ps,
                                activeOldGenerationNumber,
                                promotion,
                                out newAddress,
                                out convertToPinned,
                                nonPinnedBeforePinned ? plugEnd : null,
                                segment,
                                plugStart);
                            if (allocationResult != S_OK)
                            {
                                return allocationResult;
                            }
                        }
                        else
                        {
                            // The older-generation allocator is outside this selected
                            // initial full-gen2 transliteration.
                            return E_NOTIMPL;
                        }

                        if (convertToPinned)
                        {
                            if (!lastNonPinnedPlug || lastPinnedPlugState)
                            {
                                return E_FAIL;
                            }

                            ConvertToPinnedPlug(
                                ref lastNonPinnedPlug,
                                ref lastPinnedPlugState,
                                ref pinnedPlug,
                                ps,
                                ref artificialPinnedSize);
                            EnquePinnedPlug(plugStart, false, null);
                            lastPinnedPlug = plugStart;
                        }
                        else if (newAddress is null)
                        {
                            fixed (heap_segment* ephemeralSegment = &s_sohSegment)
                            {
                                System.Diagnostics.Debug.Assert(
                                    consingGeneration->allocation_segment == ephemeralSegment);
                                System.Diagnostics.Debug.Assert(
                                    consingGeneration->allocation_context.alloc_ptr +
                                        (nint)GCEnvironment.AlignUp(
                                            ps,
                                            (nuint)sizeof(void*)) <
                                    ephemeralSegment->allocated);
                                System.Diagnostics.Debug.Assert(
                                    consingGeneration->allocation_context.alloc_ptr +
                                        (nint)GCEnvironment.AlignUp(
                                            ps,
                                            (nuint)sizeof(void*)) >
                                    ephemeralSegment->allocated +
                                        (nint)GCEnvironment.AlignUp(
                                            MinObjectSize,
                                            (nuint)sizeof(void*)));
                            }
                        }
                        else if (IsPlugPadded(plugStart))
                        {
                            activeOldGenerationData->padding_size +=
                                GCEnvironment.AlignUp(MinObjectSize, (nuint)sizeof(void*));
                        }

                    }

                    if (pinnedPlug)
                    {
                        if (mergeWithLastPin)
                        {
                            MergeWithLastPinnedPlug(lastPinnedPlug, ps);
                        }
                        else
                        {
                            if (lastPinnedPlug != plugStart)
                            {
                                return E_FAIL;
                            }

                            SetPinnedInfo(plugStart, ps, consingGeneration);
                        }

                        newAddress = plugStart;
                        activeOldGenerationData->pinned_survived_size += ps;
                        activeOldGenerationData->added_pinned_size += addedPinningSize;
                        activeOldGenerationData->artificial_pinned_survived_size += artificialPinnedSize;
                    }

                    if (!mergeWithLastPin)
                    {
                        if (currentBrick != GetBrickIndex(plugStart))
                        {
                            currentBrick = UpdateBrickTable(
                                tree,
                                currentBrick,
                                plugStart,
                                savedPlugEnd);
                            sequenceNumber = 0;
                            tree = null;
                        }

                        SetNodeRelocationDistance(
                            plugStart,
                            (nint)(newAddress - plugStart));
                        if (lastNode is not null &&
                            GetNodeRelocationDistance(lastNode) ==
                                GetNodeRelocationDistance(plugStart) +
                                GetNodeGapSize(plugStart))
                        {
                            SetNodeLeft(plugStart);
                        }

                        if (sequenceNumber == 0)
                        {
                            tree = plugStart;
                        }

                        tree = InsertNode(
                            plugStart,
                            ++sequenceNumber,
                            tree,
                            lastNode);
                        lastNode = plugStart;

                        if (!pinnedPlug &&
                            s_markStackTos > 0)
                        {
                            mark* lastMark = &s_markStack[s_markStackTos - 1];
                            if (lastMark->HasPostPlugInfo())
                            {
                                byte* postPlugInfoStart = lastMark->saved_post_plug_info_start;
                                gap_reloc_pair* currentPlugGapStart =
                                    (gap_reloc_pair*)(plugStart - sizeof(plug_and_gap));
                                if ((byte*)currentPlugGapStart == postPlugInfoStart)
                                {
                                    lastMark->saved_post_plug_debug = *currentPlugGapStart;
                                }
                            }
                        }
                    }
                }

                x = FindNextMarked(x, end, s_useMarkList, ref markListNext, markListIndex);
                if (x is null)
                {
                    return E_FAIL;
                }
            }

            while (!PinnedPlugQueueEmpty())
            {
                if (promotion)
                {
                    mark* oldestPinnedPlug = OldestPin();
                    if (oldestPinnedPlug is null)
                    {
                        return E_FAIL;
                    }

                    fixed (heap_segment* ephemeralSegment = &s_sohSegment)
                    {
                        if (oldestPinnedPlug->first >= ephemeralSegment->mem &&
                            oldestPinnedPlug->first < ephemeralSegment->reserved)
                        {
                            generation* ensuredConsingGeneration =
                                EnsureEphemeralHeapSegment(consingGeneration);
                            if (ensuredConsingGeneration is null)
                            {
                                return E_FAIL;
                            }
                            consingGeneration = ensuredConsingGeneration;

                            while (activeNewGenerationNumber > 0)
                            {
                                activeNewGenerationNumber--;

                                if (activeNewGenerationNumber ==
                                    (int)gc_generation_num.max_generation - 1)
                                {
                                    generation* maxGeneration =
                                        GetGeneration((int)gc_generation_num.max_generation);
                                    if (maxGeneration is null)
                                    {
                                        return E_FAIL;
                                    }

                                    s_maxgenPinnedCompactBeforeAdvance =
                                        maxGeneration->pinned_allocation_compact_size;
                                    if (s_decidePromoteGen1Pins)
                                    {
                                        // advance_pins_for_demotion remains outside
                                        // this selected translation boundary.
                                        return E_NOTIMPL;
                                    }
                                }

                                generation* generationState =
                                    GetGeneration(activeNewGenerationNumber);
                                if (generationState is null)
                                {
                                    return E_FAIL;
                                }

                                int planResult = PlanGenerationStart(
                                    generationState,
                                    consingGeneration,
                                    promotion,
                                    null);
                                if (planResult != S_OK)
                                {
                                    return planResult;
                                }

                                if (s_demotionLow == (byte*)nuint.MaxValue)
                                {
                                    s_demotionLow = oldestPinnedPlug->first;
                                }

                                System.Diagnostics.Debug.Assert(
                                    generationState->plan_allocation_start is not null);
                            }
                        }
                    }
                }

                if (PinnedPlugQueueEmpty())
                {
                    break;
                }

                nuint entry = DequeuePinnedPlug();
                mark* pinnedPlugEntry = PinnedPlugOf(entry);
                if (pinnedPlugEntry is null)
                {
                    return E_FAIL;
                }

                byte* plug = pinnedPlugEntry->first;
                nuint length = pinnedPlugEntry->len;
                heap_segment* allocationSegment =
                    consingGeneration->allocation_segment;
                if (allocationSegment is null)
                {
                    return E_FAIL;
                }

                heap_segment* pinnedAllocationSegment = allocationSegment;
                while (plug < consingGeneration->allocation_context.alloc_ptr ||
                       plug >= pinnedAllocationSegment->allocated)
                {
                    System.Diagnostics.Debug.Assert(
                        plug < pinnedAllocationSegment->mem ||
                        plug > pinnedAllocationSegment->reserved);
                    if (pinnedAllocationSegment->next is null ||
                        consingGeneration->allocation_context.alloc_ptr < pinnedAllocationSegment->mem ||
                        consingGeneration->allocation_context.alloc_ptr > pinnedAllocationSegment->committed)
                    {
                        return E_FAIL;
                    }

                    pinnedAllocationSegment->plan_allocated =
                        consingGeneration->allocation_context.alloc_ptr;
                    pinnedAllocationSegment = pinnedAllocationSegment->next;
                    consingGeneration->allocation_segment = pinnedAllocationSegment;
                    consingGeneration->allocation_context.alloc_ptr = pinnedAllocationSegment->mem;
                }

                SetNewPinInfo(
                    pinnedPlugEntry,
                    consingGeneration->allocation_context.alloc_ptr);
                System.Diagnostics.Debug.Assert(
                    pinnedPlugEntry->len == 0 ||
                    pinnedPlugEntry->len >= GCEnvironment.AlignUp(
                        MinObjectSize,
                        (nuint)sizeof(void*)));

                consingGeneration->allocation_context.alloc_ptr = plug + length;
                consingGeneration->allocation_context.alloc_limit =
                    consingGeneration->allocation_context.alloc_ptr;

                int fromGenerationNumber = GetObjectGenerationNumber(plug);
                if (fromGenerationNumber != (int)gc_generation_num.max_generation &&
                    promotion)
                {
                    generation* sweepGeneration =
                        GetGeneration(fromGenerationNumber + 1);
                    if (sweepGeneration is null)
                    {
                        return E_FAIL;
                    }

                    sweepGeneration->pinned_allocation_sweep_size += length;
                }
            }

            generation* plannedConsingGeneration = PlanGenerationStarts(
                condemnedGeneration,
                consingGeneration,
                promotion);
            if (plannedConsingGeneration is null)
            {
                return E_FAIL;
            }
            consingGeneration = plannedConsingGeneration;

            fixed (heap_segment* ephemeralSegment = &s_sohSegment)
            {
                nuint fragmentation = GenerationFragmentation(
                    condemnedGenerationState,
                    consingGeneration,
                    ephemeralSegment->allocated);

                bool shouldCompact = DecideOnCompacting(
                    condemnedGeneration,
                    fragmentation,
                    out bool shouldExpand);

                if (s_settings.loh_compaction != 0)
                {
                    shouldCompact = true;
                }
                else
                {
                    int lohSweepResult = SweepUohObjects(
                        (int)gc_generation_num.loh_generation);
                    if (lohSweepResult != S_OK)
                    {
                        return lohSweepResult;
                    }
                }

                int pohSweepResult = SweepUohObjects(
                    (int)gc_generation_num.poh_generation);
                if (pohSweepResult != S_OK)
                {
                    return pohSweepResult;
                }

                if (shouldCompact || shouldExpand)
                {
                    FailFast();
                    return E_NOTIMPL;
                }

                s_settings.promotion = 1;
                s_settings.compaction = 0;
                s_settings.demotion = 0;

                int freeListResult = MakeFreeLists(condemnedGeneration);
                if (freeListResult != S_OK)
                {
                    return freeListResult;
                }

                nuint recoveredSweepSize = RecoverSavedPinnedInfo();
                generation* maxGeneration =
                    GetGeneration((int)gc_generation_num.max_generation);
                if (maxGeneration is null ||
                    recoveredSweepSize > maxGeneration->free_obj_space)
                {
                    return E_FAIL;
                }

                maxGeneration->free_obj_space -= recoveredSweepSize;
            }

            return S_OK;
        }

        // plan_phase.cpp:7711-7784, selected WKS !USE_REGIONS path.
        private static nuint GenerationFragmentation(
            generation* generationState,
            generation* consingGeneration,
            byte* end)
        {
            if (generationState is null ||
                consingGeneration is null ||
                end is null)
            {
                FailFast();
                return 0;
            }

            fixed (heap_segment* ephemeralSegment = &s_sohSegment)
            {
                if (ephemeralSegment->mem is null ||
                    ephemeralSegment->allocated is null ||
                    ephemeralSegment->reserved is null)
                {
                    FailFast();
                    return 0;
                }

                nint fragmentation = 0;
                byte* allocation = consingGeneration->allocation_context.alloc_ptr;
                if (IsAddressInSegment(allocation, ephemeralSegment))
                {
                    if (allocation <= ephemeralSegment->allocated)
                    {
                        fragmentation = (nint)(end - allocation);
                    }
                }
                else
                {
                    fragmentation =
                        (nint)(ephemeralSegment->allocated - ephemeralSegment->mem);
                }

                heap_segment* segment =
                    HeapSegmentRw(generationState->start_segment);
                if (segment is null)
                {
                    FailFast();
                    return 0;
                }

                while (segment != ephemeralSegment)
                {
                    if (segment->allocated is null ||
                        segment->plan_allocated is null)
                    {
                        FailFast();
                        return 0;
                    }

                    fragmentation +=
                        (nint)(segment->allocated - segment->plan_allocated);
                    segment = HeapSegmentNextRw(segment);
                    if (segment is null)
                    {
                        FailFast();
                        return 0;
                    }
                }

                nuint bos = 0;
                while (bos < s_markStackBos)
                {
                    if (s_markStack is null)
                    {
                        FailFast();
                        return 0;
                    }

                    mark* pinnedPlug = PinnedPlugOf(bos);
                    if (pinnedPlug is null)
                    {
                        FailFast();
                        return 0;
                    }

                    fragmentation += (nint)pinnedPlug->len;
                    bos++;
                }

                return (nuint)fragmentation;
            }
        }

        // plan_phase.cpp:7790-7827, selected WKS !USE_REGIONS path.
        private static nuint GenerationSizes(generation* gen, bool use_saved_p)
        {
            _ = use_saved_p;

            nuint result = 0;
            fixed (heap_segment* ephemeralSegment = &s_sohSegment)
            {
                if (gen->start_segment == ephemeralSegment)
                {
                    result = (nuint)(ephemeralSegment->allocated - gen->allocation_start);
                }
                else
                {
                    heap_segment* segment = gen->start_segment;
                    while (segment is not null &&
                           (segment->flags & HeapSegmentReadOnly) != 0 &&
                           (segment->flags & HeapSegmentInRange) == 0)
                    {
                        segment = segment->next;
                    }

                    if (segment is null)
                    {
                        GCToOSInterface.DebugBreak();
                    }

                    while (segment is not null)
                    {
                        result += (nuint)(segment->allocated - segment->mem);
                        segment = segment->next;
                        while (segment is not null &&
                               (segment->flags & HeapSegmentReadOnly) != 0 &&
                               (segment->flags & HeapSegmentInRange) == 0)
                        {
                            segment = segment->next;
                        }
                    }
                }
            }

            return result;
        }

        private static bool DecideOnCompacting(
            int condemnedGeneration,
            nuint fragmentation,
            out bool shouldExpand)
        {
            shouldExpand = false;
            generation* generationState = GetGeneration(condemnedGeneration);
            dynamic_data* dynamicData = GetDynamicData(condemnedGeneration);
            if (generationState is null ||
                dynamicData is null ||
                dynamicData->sdata is null)
            {
                FailFast();
                return false;
            }

            nuint generationSizes = GenerationSizes(generationState, use_saved_p: true);
            float fragmentationBurden =
                fragmentation == 0 || generationSizes == 0
                    ? 0.0f
                    : (float)fragmentation / generationSizes;

            bool shouldCompact = GCConfig.GetForceCompact();
            if (condemnedGeneration == (int)gc_generation_num.max_generation &&
                s_lastGcBeforeOom)
            {
                shouldCompact = true;
                s_lastGcBeforeOom = false;
            }

            if (s_settings.reason is gc_reason.reason_induced_compacting or
                gc_reason.reason_induced_aggressive or
                gc_reason.reason_pm_full_gc)
            {
                shouldCompact = true;
            }

            if (!shouldCompact && !EphemeralGenerationFits(usePlan: false))
            {
                shouldCompact = true;
            }

            if (shouldCompact &&
                condemnedGeneration >= (int)gc_generation_num.max_generation - 1 &&
                !EphemeralGenerationFits(usePlan: true))
            {
                shouldExpand = true;
            }

            bool highMemory = false;
            if (!shouldCompact &&
                fragmentation >= dynamicData->sdata->fragmentation_limit &&
                fragmentationBurden >= dynamicData->sdata->fragmentation_burden_limit)
            {
                shouldCompact = true;
            }

            if (!shouldCompact)
            {
                nint reclaimSpace =
                    (nint)GenerationSizes(
                        GetGeneration((int)gc_generation_num.max_generation),
                        use_saved_p: false) -
                    (nint)GenerationPlanSize((int)gc_generation_num.max_generation);
                if (s_settings.entry_memory_load >= s_highMemoryLoadThreshold &&
                    s_settings.entry_memory_load < s_veryHighMemoryLoadThreshold)
                {
                    ulong threshold = s_settings.entry_available_physical_mem;
                    if (threshold > 256UL * 1024 * 1024)
                    {
                        threshold = 256UL * 1024 * 1024;
                    }

                    if (reclaimSpace > (long)threshold)
                    {
                        shouldCompact = true;
                    }

                    highMemory = true;
                }
                else if (s_settings.entry_memory_load >= s_veryHighMemoryLoadThreshold)
                {
                    if (reclaimSpace > (nint)GetMinimumReclaimFragmentationThreshold())
                    {
                        shouldCompact = true;
                    }

                    highMemory = true;
                }
            }

            if (!shouldCompact && !EnsureGapAllocation(condemnedGeneration))
            {
                shouldCompact = true;
            }

            if (s_settings.condemned_generation ==
                (int)gc_generation_num.max_generation &&
                (highMemory && !shouldCompact ||
                 !IsFullCompactingGcProductive()))
            {
                s_settings.should_lock_elevation = 1;
            }

            return shouldCompact;
        }

        private static bool EphemeralGenerationFits(bool usePlan)
        {
            fixed (heap_segment* ephemeralSegment = &s_sohSegment)
            {
                byte* start = usePlan
                    ? ephemeralSegment->plan_allocated
                    : ephemeralSegment->allocated;
                if (start is null || start > ephemeralSegment->reserved)
                {
                    return false;
                }

                if (!usePlan)
                {
                    dynamic_data* generation0Data =
                        GetDynamicData((int)gc_generation_num.soh_gen0);
                    return generation0Data is not null &&
                        generation0Data->sdata is not null &&
                        (nuint)(ephemeralSegment->reserved - start) >=
                            generation0Data->min_size;
                }

                dynamic_data* generation0 =
                    GetDynamicData((int)gc_generation_num.soh_gen0);
                dynamic_data* generation1 =
                    GetDynamicData((int)gc_generation_num.soh_gen1);
                if (generation0 is null ||
                    generation1 is null ||
                    generation0->sdata is null ||
                    generation1->sdata is null)
                {
                    return false;
                }

                nuint required =
                    2 * generation0->min_size +
                    2 * generation1->min_size;
                return (nuint)(ephemeralSegment->reserved - start) > required;
            }
        }

        private static nuint GetMinimumReclaimFragmentationThreshold()
        {
            nuint memoryBased =
                (nuint)(500 -
                    (s_settings.entry_memory_load - s_highMemoryLoadThreshold) * 40) *
                1024 * 1024;
            nuint generationBased =
                GenerationSizes(
                    GetGeneration((int)gc_generation_num.max_generation),
                    use_saved_p: false) /
                10;
            ulong physicalMemoryBased = s_memoryOnePercent * 3;
            nuint result = memoryBased < generationBased
                ? memoryBased
                : generationBased;
            return (ulong)result < physicalMemoryBased
                ? result
                : (nuint)physicalMemoryBased;
        }

        private static bool EnsureGapAllocation(int condemnedGeneration)
        {
            fixed (heap_segment* ephemeralSegment = &s_sohSegment)
            {
                nuint size =
                    GCEnvironment.AlignUp(MinObjectSize, (nuint)sizeof(void*)) *
                    (nuint)(condemnedGeneration + 1);
                byte* end = ephemeralSegment->allocated + (nint)size;
                if (end > ephemeralSegment->reserved)
                {
                    return false;
                }

                return end <= ephemeralSegment->committed ||
                    GrowHeapSegment(
                        ephemeralSegment,
                        ephemeralSegment->allocated,
                        null,
                        size,
                        padFront: false);
            }
        }

        private static bool IsFullCompactingGcProductive()
        {
            generation* generation1 =
                GetGeneration((int)gc_generation_num.soh_gen1);
            return generation1 is not null &&
                generation1->plan_allocation_start <
                    generation1->allocation_start;
        }

        private const nuint HeapSegmentInRange = 2;
        private const nuint DemotionPlugLengthThreshold = 6 * 1024 * 1024;
        private const int UsePaddingFront = 1;
        private const int UsePaddingTail = 2;
        private const nuint DesiredPlugLength = 1000;

        private static int PlanGenerationStart(
            generation* generationState,
            generation* consingGeneration,
            bool promotion,
            byte* nextPlugToAllocate)
        {
            // This follows plan_phase.cpp:1823 into the selected
            // allocate_in_condemned_generations implementation.
            if (generationState is null || consingGeneration is null)
            {
                return E_FAIL;
            }

            if (sizeof(void*) == 8 &&
                generationState == GetGeneration((int)gc_generation_num.soh_gen0))
            {
                fixed (heap_segment* ephemeralSegment = &s_sohSegment)
                {
                    nuint markStackLargeBos = s_markStackBos;
                    while (markStackLargeBos < s_markStackTos)
                    {
                        if (s_markStack[markStackLargeBos].len > DemotionPlugLengthThreshold)
                        {
                            while (s_markStackBos <= markStackLargeBos)
                            {
                                nuint entry = DequeuePinnedPlug();
                                mark* pinnedPlugEntry = PinnedPlugOf(entry);
                                nuint length = pinnedPlugEntry->len;
                                byte* plug = pinnedPlugEntry->first;
                                pinnedPlugEntry->len = (nuint)(
                                    plug - consingGeneration->allocation_context.alloc_ptr);
                                System.Diagnostics.Debug.Assert(
                                    s_markStack[entry].len == 0 ||
                                    s_markStack[entry].len >= GCEnvironment.AlignUp(
                                        MinObjectSize,
                                        (nuint)sizeof(void*)));
                                consingGeneration->allocation_context.alloc_ptr = plug + length;
                                consingGeneration->allocation_context.alloc_limit =
                                    ephemeralSegment->plan_allocated;
                                SetAllocatorNextPin(consingGeneration);
                            }
                        }

                        markStackLargeBos++;
                    }
                }
            }

            int allocationResult = AllocateInCondemnedGenerations(
                consingGeneration,
                GCEnvironment.AlignUp(MinObjectSize, (nuint)sizeof(void*)),
                -1,
                promotion,
                out byte* planAllocationStart,
                out bool ignoredConvertToPinned,
                null,
                null,
                null);
            if (allocationResult != S_OK)
            {
                return allocationResult;
            }

            generationState->plan_allocation_start = planAllocationStart;
            generationState->plan_allocation_start_size =
                GCEnvironment.AlignUp(MinObjectSize, (nuint)sizeof(void*));

            byte* allocationPointer = consingGeneration->allocation_context.alloc_ptr;
            byte* allocationLimit = consingGeneration->allocation_context.alloc_limit;
            if (allocationPointer is null || allocationLimit is null)
            {
                return E_FAIL;
            }

            nuint allocationLeft = allocationLimit >= allocationPointer
                ? (nuint)(allocationLimit - allocationPointer)
                : 0;
            if (nextPlugToAllocate is not null)
            {
                if (nextPlugToAllocate < allocationPointer)
                {
                    return E_FAIL;
                }

                nuint distanceToNextPlug = (nuint)(nextPlugToAllocate - allocationPointer);
                if (allocationLeft > distanceToNextPlug)
                {
                    allocationLeft = distanceToNextPlug;
                }
            }

            if (allocationLeft < generationState->plan_allocation_start_size)
            {
                generationState->plan_allocation_start_size += allocationLeft;
                consingGeneration->allocation_context.alloc_ptr += (nint)allocationLeft;
            }

            _ = ignoredConvertToPinned;
            return S_OK;
        }

        private static generation* PlanGenerationStarts(
            int condemnedGeneration,
            generation* initialConsingGeneration,
            bool promotion)
        {
            if (initialConsingGeneration is null)
            {
                return null;
            }

            generation* consingGeneration = initialConsingGeneration;
            int generationNumber = condemnedGeneration;
            while (generationNumber >= 0)
            {
                if (generationNumber < (int)gc_generation_num.max_generation)
                {
                    generation* ensuredConsingGeneration =
                        EnsureEphemeralHeapSegment(consingGeneration);
                    if (ensuredConsingGeneration is null)
                    {
                        return null;
                    }
                    consingGeneration = ensuredConsingGeneration;
                }

                generation* generationState = GetGeneration(generationNumber);
                if (generationState is null)
                {
                    return null;
                }

                if (generationState->plan_allocation_start is null)
                {
                    int planResult = PlanGenerationStart(
                        generationState,
                        consingGeneration,
                        promotion,
                        null);
                    if (planResult != S_OK)
                    {
                        return null;
                    }

                    System.Diagnostics.Debug.Assert(
                        generationState->plan_allocation_start is not null);
                    if (generationState->plan_allocation_start is null)
                    {
                        return null;
                    }
                }

                generationNumber--;
            }

            fixed (heap_segment* ephemeralSegment = &s_sohSegment)
            {
                ephemeralSegment->plan_allocated =
                    consingGeneration->allocation_context.alloc_ptr;
            }

            return consingGeneration;
        }

        // allocation.cpp:5446-5742, selected WKS !USE_REGIONS/SHORT_PLUGS path.
        private static int AllocateInCondemnedGenerations(
            generation* generationState,
            nuint size,
            int fromGenerationNumber,
            bool promotion,
            out byte* result,
            out bool convertToPinned,
            byte* nextPinnedPlug,
            heap_segment* currentSegment,
            byte* oldLocation)
        {
            result = null;
            convertToPinned = false;
            if (generationState is null)
            {
                return E_FAIL;
            }

            nuint alignedMinimumObjectSize =
                GCEnvironment.AlignUp(MinObjectSize, (nuint)sizeof(void*));
            size = GCEnvironment.AlignUp(size, (nuint)sizeof(void*));
            if (size < alignedMinimumObjectSize)
            {
                return E_FAIL;
            }

            int toGenerationNumber = fromGenerationNumber;
            if (fromGenerationNumber != (int)gc_generation_num.max_generation)
            {
                toGenerationNumber = fromGenerationNumber + (promotion ? 1 : 0);
            }

            int paddingInFront = oldLocation is not null &&
                                 toGenerationNumber != (int)gc_generation_num.max_generation
                ? UsePaddingFront
                : 0;

            nuint minSegmentSize = s_sohSegmentSize < s_minUohSegmentSize
                ? s_sohSegmentSize
                : s_minUohSegmentSize;
            if ((paddingInFront & UsePaddingFront) != 0 &&
                size + alignedMinimumObjectSize >
                minSegmentSize - (nuint)sizeof(aligned_plug_and_gap))
            {
                paddingInFront = 0;
            }

            if (fromGenerationNumber != -1 &&
                fromGenerationNumber != (int)gc_generation_num.max_generation &&
                promotion)
            {
                generation* destinationGeneration = GetGeneration(toGenerationNumber);
                if (destinationGeneration is null)
                {
                    return E_FAIL;
                }

                destinationGeneration->condemned_allocated += size;
                destinationGeneration->allocation_size += size;
            }

            while (true)
            {
                heap_segment* segment = generationState->allocation_segment;
                if (segment is null)
                {
                    return E_FAIL;
                }

                int usePadding =
                    (generationState->allocation_context.alloc_limit != segment->plan_allocated
                        ? UsePaddingTail
                        : 0) |
                    paddingInFront;
                if (!SizeFits(
                        size,
                        generationState->allocation_context.alloc_ptr,
                        generationState->allocation_context.alloc_limit,
                        oldLocation,
                        usePadding))
                {
                    if (!PinnedPlugQueueEmpty() &&
                        generationState->allocation_context.alloc_limit == OldestPin()->first)
                    {
                        nuint entry = DequeuePinnedPlug();
                        mark* pinnedPlugEntry = PinnedPlugOf(entry);
                        if (pinnedPlugEntry is null)
                        {
                            return E_FAIL;
                        }

                        nuint pinnedLength = pinnedPlugEntry->len;
                        byte* pinnedPlug = pinnedPlugEntry->first;
                        SetNewPinInfo(
                            pinnedPlugEntry,
                            generationState->allocation_context.alloc_ptr);
                        System.Diagnostics.Debug.Assert(
                            pinnedPlugEntry->len == 0 ||
                            pinnedPlugEntry->len >= alignedMinimumObjectSize);

                        generationState->allocation_context.alloc_ptr = pinnedPlug + pinnedLength;
                        generationState->allocation_context_start_region =
                            generationState->allocation_context.alloc_ptr;
                        generationState->allocation_context.alloc_limit =
                            segment->plan_allocated;
                        SetAllocatorNextPin(generationState);

                        int attributeResult = AttributePinHigherGenerationAllocation(
                            pinnedPlug,
                            pinnedLength,
                            promotion);
                        if (attributeResult != S_OK)
                        {
                            return attributeResult;
                        }

                        continue;
                    }

                    if (generationState->allocation_context.alloc_limit != segment->plan_allocated)
                    {
                        generationState->allocation_context.alloc_limit = segment->plan_allocated;
                    }
                    else if (segment->plan_allocated != segment->committed)
                    {
                        segment->plan_allocated = segment->committed;
                        generationState->allocation_context.alloc_limit =
                            segment->plan_allocated;
                    }
                    else
                    {
                        System.Diagnostics.Debug.Assert(
                            generationState != GetGeneration((int)gc_generation_num.soh_gen0));

                        if (SizeFits(
                                size,
                                generationState->allocation_context.alloc_ptr,
                                segment->reserved,
                                oldLocation,
                                UsePaddingTail | paddingInFront) &&
                            GrowHeapSegment(
                                segment,
                                generationState->allocation_context.alloc_ptr,
                                oldLocation,
                                size,
                                paddingInFront != 0))
                        {
                            segment->plan_allocated = segment->committed;
                            generationState->allocation_context.alloc_limit =
                                segment->plan_allocated;
                        }
                        else
                        {
                            heap_segment* nextSegment = segment->next;
                            if (generationState->allocation_context.alloc_ptr < segment->mem ||
                                generationState->allocation_context.alloc_ptr > segment->committed)
                            {
                                return E_FAIL;
                            }

                            if (!PinnedPlugQueueEmpty() &&
                                OldestPin()->first < segment->allocated &&
                                OldestPin()->first >= generationState->allocation_context.alloc_ptr)
                            {
                                FailFast();
                                return E_FAIL;
                            }

                            segment->plan_allocated =
                                generationState->allocation_context.alloc_ptr;
                            if (nextSegment is null)
                            {
                                // Native returns null only for the generation-0 gap
                                // at the end of the non-region segment chain.
                                return S_OK;
                            }

                            InitAllocationInfo(generationState, nextSegment);
                        }
                    }

                    SetAllocatorNextPin(generationState);
                    continue;
                }

                if (generationState->allocation_context.alloc_ptr <
                    generationState->allocation_segment->mem)
                {
                    return E_FAIL;
                }

                result = generationState->allocation_context.alloc_ptr;
                nuint padding = 0;
                byte* allocationStart = generationState->allocation_context_start_region;
                if ((paddingInFront & UsePaddingFront) != 0 &&
                    oldLocation is not null &&
                    allocationStart is not null &&
                    ((nuint)(result - allocationStart) == 0 ||
                     (nuint)(result - allocationStart) >= DesiredPlugLength))
                {
                    nint distance = (nint)(oldLocation - result);
                    if (distance != 0)
                    {
                        if (distance > 0 && (nuint)distance < alignedMinimumObjectSize)
                        {
                            FailFast();
                            return E_FAIL;
                        }

                        padding = alignedMinimumObjectSize;
                        SetPlugPadded(oldLocation);
                    }
                }

                if (nextPinnedPlug is not null &&
                    padding != 0 &&
                    generationState->allocation_segment == currentSegment)
                {
                    System.Diagnostics.Debug.Assert(oldLocation is not null);
                    nint distanceToNextPin =
                        (nint)(nextPinnedPlug -
                        (generationState->allocation_context.alloc_ptr +
                         (nint)size +
                         (nint)padding));
                    if (distanceToNextPin >= 0 &&
                        (nuint)distanceToNextPin < alignedMinimumObjectSize)
                    {
                        ClearPlugPadded(oldLocation);
                        padding = 0;
                        convertToPinned = true;
                        result = null;
                        return S_OK;
                    }
                }

                if (oldLocation is null || padding != 0)
                {
                    generationState->allocation_context_start_region =
                        generationState->allocation_context.alloc_ptr;
                }

                generationState->allocation_context.alloc_ptr +=
                    (nint)(size + padding);
                if (padding != 0 && toGenerationNumber >= 0)
                {
                    generation* destinationGeneration = GetGeneration(toGenerationNumber);
                    if (destinationGeneration is null)
                    {
                        return E_FAIL;
                    }

                    destinationGeneration->free_obj_space += padding;
                }

                result += (nint)padding;
                return S_OK;
            }
        }

        private static bool SizeFits(
            nuint size,
            byte* allocationPointer,
            byte* allocationLimit,
            byte* oldLocation,
            int usePadding)
        {
            bool alreadyPadded = false;
            nuint alignedMinimumObjectSize =
                GCEnvironment.AlignUp(MinObjectSize, (nuint)sizeof(void*));
            if (oldLocation is not null && (usePadding & UsePaddingFront) != 0)
            {
                allocationPointer += (nint)alignedMinimumObjectSize;
                alreadyPadded = true;
            }

            if (oldLocation is not null &&
                !SameLargeAlignment(oldLocation, allocationPointer))
            {
                size += SwitchAlignmentSize(alreadyPadded);
            }

            if (allocationLimit < allocationPointer)
            {
                return false;
            }

            nuint available = (nuint)(allocationLimit - allocationPointer);
            if (oldLocation is not null)
            {
                return available >=
                           size +
                           (((usePadding & UsePaddingTail) != 0)
                               ? alignedMinimumObjectSize
                               : 0) ||
                       ((usePadding & UsePaddingFront) == 0 &&
                        allocationPointer + (nint)size == allocationLimit);
            }

            System.Diagnostics.Debug.Assert(size == alignedMinimumObjectSize);
            return available >= size;
        }

        private static bool GrowHeapSegment(
            heap_segment* segment,
            byte* allocation,
            byte* oldLocation,
            nuint size,
            bool padFront)
        {
            if (segment is null || allocation is null)
            {
                return false;
            }

            nuint alignedMinimumObjectSize =
                GCEnvironment.AlignUp(MinObjectSize, (nuint)sizeof(void*));
            if (oldLocation is not null && padFront)
            {
                allocation += (nint)alignedMinimumObjectSize;
            }

            if (oldLocation is not null &&
                !SameLargeAlignment(oldLocation, allocation))
            {
                size += SwitchAlignmentSize(padFront);
            }

            byte* highAddress = allocation + (nint)size;
            nuint pageSize = GCToOSInterface.GetPageSize();
            byte* commitEnd = GCEnvironment.AlignUp(highAddress, pageSize);
            if (commitEnd > segment->reserved)
            {
                return false;
            }

            if (commitEnd <= segment->committed)
            {
                return true;
            }

            nuint commitSize = (nuint)(commitEnd - segment->committed);
            nuint minimumCommit = pageSize * 16;
            if (commitSize < minimumCommit)
            {
                commitSize = minimumCommit;
            }

            nuint remaining = (nuint)(segment->reserved - segment->committed);
            if (commitSize > remaining)
            {
                commitSize = remaining;
            }

            if (commitSize == 0 ||
                !GCToOSInterface.VirtualCommit(segment->committed, commitSize))
            {
                return false;
            }

            segment->committed += (nint)commitSize;
            return highAddress <= segment->committed;
        }

        private static void InitAllocationInfo(generation* generationState, heap_segment* segment)
        {
            generationState->allocation_segment = segment;
            generationState->allocation_context.alloc_ptr = segment->mem;
            generationState->allocation_context.alloc_limit = segment->mem;
            generationState->allocation_context_start_region = segment->mem;
        }

        private static int AttributePinHigherGenerationAllocation(
            byte* plug,
            nuint length,
            bool promotion)
        {
            if (!promotion)
            {
                return S_OK;
            }

            int fromGenerationNumber = GetObjectGenerationNumber(plug);
            if (fromGenerationNumber == (int)gc_generation_num.max_generation)
            {
                return S_OK;
            }

            generation* sweepGeneration = GetGeneration(fromGenerationNumber + 1);
            if (sweepGeneration is null)
            {
                return E_FAIL;
            }

            sweepGeneration->pinned_allocation_sweep_size += length;
            int toGenerationNumber = GetObjectPlanGenerationNumber(plug);
            if (fromGenerationNumber < toGenerationNumber)
            {
                generation* compactGeneration = GetGeneration(toGenerationNumber);
                if (compactGeneration is null)
                {
                    return E_FAIL;
                }

                compactGeneration->pinned_allocation_compact_size += length;
            }

            return S_OK;
        }

        private static bool SameLargeAlignment(byte* first, byte* second)
        {
            _ = first;
            _ = second;
            return true;
        }

        private static nuint SwitchAlignmentSize(bool alreadyPadded)
        {
            return alreadyPadded
                ? (nuint)sizeof(void*)
                : GCEnvironment.AlignUp(MinObjectSize, (nuint)sizeof(void*)) |
                  (nuint)sizeof(void*);
        }

        private static bool IsPlugPadded(byte* plug)
        {
            return plug is not null && ((Object*)plug)->IsMarked();
        }

        private static void SetPlugPadded(byte* plug)
        {
            if (plug is not null)
            {
                ((Object*)plug)->SetMarked();
            }
        }

        private static void ClearPlugPadded(byte* plug)
        {
            if (plug is not null)
            {
                ((Object*)plug)->ClearMarked();
            }
        }

        private static nint GetNodeRelocationDistance(byte* node)
        {
            return (((plug_and_reloc*)node)[-1].reloc & ~((nint)3));
        }

        private static void SetNodeRelocationDistance(byte* node, nint value)
        {
            System.Diagnostics.Debug.Assert((value & 3) == 0);
            nint* place = &(((plug_and_reloc*)node)[-1].reloc);
            *place &= 1;
            *place |= value;
        }

        private static nint GetNodeGapSize(byte* node)
        {
            return ((plug_and_gap*)node)[-1].gap;
        }

        private static void SetNodeLeft(byte* node)
        {
            ((plug_and_reloc*)node)[-1].reloc |= 2;
        }

        private static nint GetNodeLeftChild(byte* node)
        {
            return ((plug_and_pair*)node)[-1].m_pair.left;
        }

        private static nint GetNodeRightChild(byte* node)
        {
            return ((plug_and_pair*)node)[-1].m_pair.right;
        }

        private static void SetNodeLeftChild(byte* node, nint value)
        {
            System.Diagnostics.Debug.Assert(value > -(nint)BrickSize);
            System.Diagnostics.Debug.Assert(value < (nint)BrickSize);
            System.Diagnostics.Debug.Assert((value & ((nint)sizeof(void*) - 1)) == 0);
            ((plug_and_pair*)node)[-1].m_pair.left = (short)value;
        }

        private static void SetNodeRightChild(byte* node, nint value)
        {
            System.Diagnostics.Debug.Assert(value > -(nint)BrickSize);
            System.Diagnostics.Debug.Assert(value < (nint)BrickSize);
            System.Diagnostics.Debug.Assert((value & ((nint)sizeof(void*) - 1)) == 0);
            ((plug_and_pair*)node)[-1].m_pair.right = (short)value;
        }

        private static bool PowerOfTwo(nuint value)
        {
            return value != 0 && (value & (value - 1)) == 0;
        }

        private static nuint LogCount(nuint value)
        {
            System.Diagnostics.Debug.Assert(value < 0x10000);
            nuint count = (value & 0x5555) + ((value >> 1) & 0x5555);
            count = (count & 0x3333) + ((count >> 2) & 0x3333);
            count = (count & 0x0F0F) + ((count >> 4) & 0x0F0F);
            return (count & 0x00FF) + ((count >> 8) & 0x00FF);
        }

        private static byte* InsertNode(
            byte* newNode,
            nuint sequenceNumber,
            byte* tree,
            byte* lastNode)
        {
            if (PowerOfTwo(sequenceNumber))
            {
                SetNodeLeftChild(newNode, (nint)(tree - newNode));
                tree = newNode;
            }
            else if ((sequenceNumber & 1) != 0)
            {
                SetNodeRightChild(lastNode, (nint)(newNode - lastNode));
            }
            else
            {
                byte* earlierNode = tree;
                nuint imax = LogCount(sequenceNumber) - 2;
                for (nuint i = 0; i != imax; i++)
                {
                    earlierNode += GetNodeRightChild(earlierNode);
                }

                nint temporaryOffset = GetNodeRightChild(earlierNode);
                System.Diagnostics.Debug.Assert(temporaryOffset != 0);
                SetNodeLeftChild(newNode, (nint)(earlierNode + temporaryOffset - newNode));
                SetNodeRightChild(earlierNode, (nint)(newNode - earlierNode));
            }

            return tree;
        }

        private static void SetBrick(nuint index, nint value)
        {
            if (s_brickTable is null)
            {
                FailFast();
                return;
            }

            if (value < -32767)
            {
                value = -32767;
            }

            System.Diagnostics.Debug.Assert(value < 32767);
            s_brickTable[index] = value >= 0
                ? (short)(value + 1)
                : (short)value;
        }

        private static byte* BrickAddress(nuint brick)
        {
            return GCCommon.g_gc_lowest_address + (nint)(brick * BrickSize);
        }

        private static nuint UpdateBrickTable(
            byte* tree,
            nuint currentBrick,
            byte* x,
            byte* plugEnd)
        {
            if (tree is not null)
            {
                SetBrick(
                    currentBrick,
                    (nint)(tree - BrickAddress(currentBrick)));
            }
            else
            {
                SetBrick(currentBrick, -1);
            }

            nuint b = currentBrick + 1;
            nint offset = 0;
            nuint lastBrick = GetBrickIndex(plugEnd - 1);
            currentBrick = GetBrickIndex(x - 1);
            while (b <= currentBrick)
            {
                if (b <= lastBrick)
                {
                    SetBrick(b, --offset);
                }
                else
                {
                    SetBrick(b, -1);
                }

                b++;
            }

            return GetBrickIndex(x);
        }

        private static void MergeWithLastPinnedPlug(byte* lastPinnedPlug, nuint plugSize)
        {
            if (lastPinnedPlug is null)
            {
                return;
            }

            if (s_markStackTos == 0)
            {
                FailFast();
                return;
            }

            mark* lastMark = &s_markStack[s_markStackTos - 1];
            if (lastMark->first != lastPinnedPlug)
            {
                FailFast();
                return;
            }

            if (lastMark->saved_post_p != 0)
            {
                lastMark->saved_post_p = 0;
                *(gap_reloc_pair*)(
                    lastMark->first + lastMark->len - sizeof(plug_and_gap)) =
                    lastMark->saved_post_plug;
            }

            lastMark->len += plugSize;
        }

        private static int GetObjectPlanGenerationNumber(byte* address)
        {
            fixed (heap_segment* ephemeralSegment = &s_sohSegment)
            {
                if (address >= ephemeralSegment->mem &&
                    address < ephemeralSegment->reserved)
                {
                    for (int generationNumber = 0;
                         generationNumber < (int)gc_generation_num.max_generation;
                         generationNumber++)
                    {
                        generation* generationState = GetGeneration(generationNumber);
                        if (generationState is not null &&
                            generationState->plan_allocation_start is not null &&
                            address >= generationState->plan_allocation_start)
                        {
                            return generationNumber;
                        }
                    }
                }
            }

            return (int)gc_generation_num.max_generation;
        }

        private static int ProcessEphemeralBoundaries(
            byte* address,
            bool promotion,
            ref int activeNewGenerationNumber,
            ref int activeOldGenerationNumber,
            ref generation* consingGeneration,
            ref bool allocateInCondemned)
        {
            while (activeOldGenerationNumber > 0)
            {
                generation* youngerGeneration = GetGeneration(activeOldGenerationNumber - 1);
                if (youngerGeneration is null)
                {
                    return E_FAIL;
                }

                if (address < youngerGeneration->allocation_start)
                {
                    break;
                }

                if (activeOldGenerationNumber <=
                    (promotion
                        ? (int)gc_generation_num.max_generation - 1
                        : (int)gc_generation_num.max_generation))
                {
                    activeNewGenerationNumber--;
                }

                activeOldGenerationNumber--;
                System.Diagnostics.Debug.Assert(
                    !promotion || activeNewGenerationNumber > 0);

                if (activeNewGenerationNumber == (int)gc_generation_num.max_generation - 1)
                {
                    while (!PinnedPlugQueueEmpty())
                    {
                        mark* oldestEntry = OldestPin();
                        if (oldestEntry is null ||
                            (oldestEntry->first >= s_sohSegment.mem &&
                             oldestEntry->first < s_sohSegment.reserved))
                        {
                            break;
                        }

                        nuint entry = DequeuePinnedPlug();
                        mark* pinnedPlugEntry = PinnedPlugOf(entry);
                        byte* plug = pinnedPlugEntry->first;
                        nuint length = pinnedPlugEntry->len;
                        heap_segment* allocationSegment =
                            consingGeneration->allocation_segment;
                        if (allocationSegment is null)
                        {
                            return E_FAIL;
                        }

                        heap_segment* segment = allocationSegment;
                        while (plug < consingGeneration->allocation_context.alloc_ptr ||
                               plug >= segment->allocated)
                        {
                            if (consingGeneration->allocation_context.alloc_ptr < segment->mem ||
                                consingGeneration->allocation_context.alloc_ptr > segment->committed ||
                                segment->next is null)
                            {
                                return E_FAIL;
                            }

                            segment->plan_allocated =
                                consingGeneration->allocation_context.alloc_ptr;
                            segment = segment->next;
                            consingGeneration->allocation_segment = segment;
                            consingGeneration->allocation_context.alloc_ptr = segment->mem;
                        }

                        SetNewPinInfo(
                            pinnedPlugEntry,
                            consingGeneration->allocation_context.alloc_ptr);
                        System.Diagnostics.Debug.Assert(
                            pinnedPlugEntry->len == 0 ||
                            pinnedPlugEntry->len >= GCEnvironment.AlignUp(
                                MinObjectSize,
                                (nuint)sizeof(void*)));
                        consingGeneration->allocation_context.alloc_ptr = plug + length;
                        consingGeneration->allocation_context.alloc_limit =
                            consingGeneration->allocation_context.alloc_ptr;
                    }

                    allocateInCondemned = true;
                    generation* ensuredConsingGeneration =
                        EnsureEphemeralHeapSegment(consingGeneration);
                    if (ensuredConsingGeneration is null)
                    {
                        return E_FAIL;
                    }

                    consingGeneration = ensuredConsingGeneration;
                }

                if (activeNewGenerationNumber != (int)gc_generation_num.max_generation)
                {
                    if (activeNewGenerationNumber ==
                        (int)gc_generation_num.max_generation - 1)
                    {
                        generation* maxGeneration =
                            GetGeneration((int)gc_generation_num.max_generation);
                        if (maxGeneration is null)
                        {
                            return E_FAIL;
                        }

                        s_maxgenPinnedCompactBeforeAdvance =
                            maxGeneration->pinned_allocation_compact_size;
                        if (s_decidePromoteGen1Pins)
                        {
                            return E_NOTIMPL;
                        }
                    }

                    generation* nextGeneration = GetGeneration(activeNewGenerationNumber);
                    if (nextGeneration is null)
                    {
                        return E_FAIL;
                    }

                    int result = PlanGenerationStart(
                        nextGeneration,
                        consingGeneration,
                        promotion,
                        address);
                    if (result != S_OK)
                    {
                        return result;
                    }

                    if (s_demotionLow == (byte*)nuint.MaxValue &&
                        !PinnedPlugQueueEmpty())
                    {
                        byte* pinnedPlug = OldestPin()->first;
                        if (GetObjectGenerationNumber(pinnedPlug) > 0)
                        {
                            s_demotionLow = pinnedPlug;
                        }
                    }

                    System.Diagnostics.Debug.Assert(
                        nextGeneration->plan_allocation_start is not null);
                }
            }

            return S_OK;
        }

        private static generation* EnsureEphemeralHeapSegment(
            generation* consingGeneration)
        {
            if (consingGeneration is null ||
                consingGeneration->allocation_segment is null)
            {
                return null;
            }

            fixed (heap_segment* ephemeralSegment = &s_sohSegment)
            {
                if (consingGeneration->allocation_segment == ephemeralSegment)
                {
                    return consingGeneration;
                }

                heap_segment* segment = consingGeneration->allocation_segment;
                if (consingGeneration->allocation_context.alloc_ptr < segment->mem ||
                    consingGeneration->allocation_context.alloc_ptr > segment->committed)
                {
                    return null;
                }

                segment->plan_allocated =
                    consingGeneration->allocation_context.alloc_ptr;

                generation* newConsingGeneration =
                    GetGeneration((int)gc_generation_num.max_generation - 1);
                if (newConsingGeneration is null)
                {
                    return null;
                }

                newConsingGeneration->allocation_context.alloc_ptr = ephemeralSegment->mem;
                newConsingGeneration->allocation_context.alloc_limit = ephemeralSegment->mem;
                newConsingGeneration->allocation_context_start_region = ephemeralSegment->mem;
                newConsingGeneration->allocation_segment = ephemeralSegment;
                return newConsingGeneration;
            }
        }

        private static dynamic_data* GetDynamicData(int generationNumber)
        {
            if ((uint)generationNumber >= (uint)gc_generation_num.total_generation_count)
            {
                return null;
            }

            fixed (DynamicDataArray5* dynamicDataTable = &s_dynamicDataTable)
            {
                return generationNumber switch
                {
                    0 => &dynamicDataTable->Item0,
                    1 => &dynamicDataTable->Item1,
                    2 => &dynamicDataTable->Item2,
                    3 => &dynamicDataTable->Item3,
                    4 => &dynamicDataTable->Item4,
                    _ => null,
                };
            }
        }

        private static void SetNewPinInfo(mark* entry, byte* pinFreeSpaceStart)
        {
            entry->len = (nuint)(entry->first - pinFreeSpaceStart);
            entry->allocation_context_start_region = pinFreeSpaceStart;
        }

        private static int GetObjectGenerationNumber(byte* address)
        {
            return (int)WhichGeneration(null, (Object*)address);
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
            heap_segment* segment =
                HeapSegmentRw(currentGeneration->start_segment);
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

                segment = HeapSegmentNextRw(segment);
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
                heap_segment* segment =
                    HeapSegmentRw(currentGeneration->start_segment);
                while (segment is not null)
                {
                    segment->plan_allocated = segment->mem;
                    segment = HeapSegmentNextRw(segment);
                }
            }

            condemnedGenerationState->allocation_segment =
                HeapSegmentRw(condemnedGenerationState->start_segment);
            heap_segment* allocationSegment = condemnedGenerationState->allocation_segment;
            if (allocationSegment is null)
            {
                return;
            }
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

        // plan_phase.cpp:7574-7623, selected WKS !USE_REGIONS path.
        private static nuint GenerationPlanSize(int generationNumber)
        {
            generation* generationState = GetGeneration(generationNumber);
            if (generationState is null)
            {
                FailFast();
                return 0;
            }

            fixed (heap_segment* ephemeralSegment = &s_sohSegment)
            {
                if (generationNumber == 0)
                {
                    if (ephemeralSegment->plan_allocated is null ||
                        generationState->plan_allocation_start is null)
                    {
                        FailFast();
                        return 0;
                    }

                    nint size = (nint)(ephemeralSegment->plan_allocated -
                        generationState->plan_allocation_start);
                    nint minimumSize = (nint)GCEnvironment.AlignUp(
                        MinObjectSize,
                        (nuint)sizeof(void*));
                    return (nuint)(size > minimumSize ? size : minimumSize);
                }

                heap_segment* segment = HeapSegmentRw(generationState->start_segment);
                if (segment == ephemeralSegment)
                {
                    generation* youngerGeneration = GetGeneration(generationNumber - 1);
                    if (youngerGeneration is null ||
                        generationState->plan_allocation_start is null ||
                        youngerGeneration->plan_allocation_start is null)
                    {
                        FailFast();
                        return 0;
                    }

                    return (nuint)(youngerGeneration->plan_allocation_start -
                        generationState->plan_allocation_start);
                }

                nuint generationSize = 0;
                if (segment is null)
                {
                    FailFast();
                    return 0;
                }

                while (segment is not null && segment != ephemeralSegment)
                {
                    if (segment->plan_allocated is null ||
                        segment->mem is null)
                    {
                        FailFast();
                        return 0;
                    }

                    generationSize += (nuint)(segment->plan_allocated - segment->mem);
                    segment = HeapSegmentNextRw(segment);
                }

                if (segment is not null)
                {
                    generation* youngerGeneration = GetGeneration(generationNumber - 1);
                    if (youngerGeneration is null ||
                        youngerGeneration->plan_allocation_start is null ||
                        ephemeralSegment->mem is null)
                    {
                        FailFast();
                        return 0;
                    }

                    generationSize += (nuint)(youngerGeneration->plan_allocation_start -
                        ephemeralSegment->mem);
                }

                return generationSize;
            }
        }

        // plan_phase.cpp:7625-7675, selected WKS !USE_REGIONS path.
        private static nuint GenerationSize(int generationNumber)
        {
            generation* generationState = GetGeneration(generationNumber);
            if (generationState is null)
            {
                FailFast();
                return 0;
            }

            fixed (heap_segment* ephemeralSegment = &s_sohSegment)
            {
                if (generationNumber == 0)
                {
                    if (ephemeralSegment->allocated is null ||
                        generationState->allocation_start is null)
                    {
                        FailFast();
                        return 0;
                    }

                    nint size = (nint)(ephemeralSegment->allocated -
                        generationState->allocation_start);
                    nint minimumSize = (nint)GCEnvironment.AlignUp(
                        MinObjectSize,
                        (nuint)sizeof(void*));
                    return (nuint)(size > minimumSize ? size : minimumSize);
                }

                heap_segment* segment = HeapSegmentRw(generationState->start_segment);
                if (segment == ephemeralSegment)
                {
                    generation* youngerGeneration = GetGeneration(generationNumber - 1);
                    if (youngerGeneration is null ||
                        generationState->allocation_start is null ||
                        youngerGeneration->allocation_start is null)
                    {
                        FailFast();
                        return 0;
                    }

                    return (nuint)(youngerGeneration->allocation_start -
                        generationState->allocation_start);
                }

                nuint generationSize = 0;
                if (segment is null)
                {
                    FailFast();
                    return 0;
                }

                while (segment is not null && segment != ephemeralSegment)
                {
                    if (segment->allocated is null ||
                        segment->mem is null)
                    {
                        FailFast();
                        return 0;
                    }

                    generationSize += (nuint)(segment->allocated - segment->mem);
                    segment = HeapSegmentNextRw(segment);
                }

                if (segment is not null)
                {
                    generation* youngerGeneration = GetGeneration(generationNumber - 1);
                    if (youngerGeneration is null ||
                        youngerGeneration->allocation_start is null ||
                        ephemeralSegment->mem is null)
                    {
                        FailFast();
                        return 0;
                    }

                    generationSize += (nuint)(youngerGeneration->allocation_start -
                        ephemeralSegment->mem);
                }

                return generationSize;
            }
        }

        private static heap_segment* HeapSegmentRw(heap_segment* segment)
        {
            while (segment is not null &&
                (segment->flags & HeapSegmentReadOnly) != 0)
            {
                segment = segment->next;
            }

            return segment;
        }

        private static heap_segment* HeapSegmentNextRw(heap_segment* segment)
        {
            return HeapSegmentRw(segment->next);
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

        private struct MakeFreeArgs
        {
            public int FreeListGenerationNumber;
            public byte* CurrentGenerationLimit;
            public generation* FreeListGeneration;
            public byte* HighestPlug;
        }

        private static int MakeFreeLists(int condemnedGeneration)
        {
            System.Diagnostics.Debug.Assert(s_settings.promotion != 0);

            MakeFreeArgs args = default;
            int stopGenerationIndex = GetStopGenerationIndex(condemnedGeneration);
            for (int generationNumber = condemnedGeneration;
                generationNumber >= stopGenerationIndex;
                generationNumber--)
            {
                generation* condemnedGenerationState = GetGeneration(generationNumber);
                if (condemnedGenerationState is null ||
                    condemnedGenerationState->start_segment is null)
                {
                    return E_FAIL;
                }

                heap_segment* currentSegment =
                    HeapSegmentRw(condemnedGenerationState->start_segment);
                if (currentSegment is null)
                {
                    return E_FAIL;
                }

                byte* startAddress =
                    GetSohStartObject(currentSegment, condemnedGenerationState);
                byte* endAddress = currentSegment->allocated;
                if (startAddress is null ||
                    endAddress is null ||
                    startAddress > endAddress)
                {
                    return E_FAIL;
                }

                nuint currentBrick = GetBrickIndex(startAddress);
                nuint endBrick = startAddress == endAddress
                    ? currentBrick
                    : GetBrickIndex(endAddress - 1);

                args.FreeListGenerationNumber =
                    GetPlanGenerationNumber(generationNumber);
                args.FreeListGeneration =
                    GetGeneration(args.FreeListGenerationNumber);
                args.HighestPlug = null;
                args.CurrentGenerationLimit =
                    GetGenerationLimit(args.FreeListGenerationNumber);
                if (args.FreeListGeneration is null ||
                    args.CurrentGenerationLimit is null)
                {
                    return E_FAIL;
                }

                while (true)
                {
                    if (currentBrick > endBrick)
                    {
                        heap_segment* nextSegment =
                            HeapSegmentNextRw(currentSegment);
                        if (nextSegment is null)
                        {
                            break;
                        }

                        currentSegment = nextSegment;
                        currentBrick = GetBrickIndex(currentSegment->mem);
                        if (currentSegment->allocated <= currentSegment->mem)
                        {
                            continue;
                        }

                        endBrick = GetBrickIndex(currentSegment->allocated - 1);
                        continue;
                    }

                    int brickEntry = s_brickTable[currentBrick];
                    if (brickEntry >= 0)
                    {
                        int result = MakeFreeListInBrick(
                            BrickAddress(currentBrick) + brickEntry - 1,
                            &args);
                        if (result != S_OK)
                        {
                            return result;
                        }

                        SetBrick(
                            currentBrick,
                            args.HighestPlug is null
                                ? -1
                                : (nint)(args.HighestPlug -
                                    BrickAddress(currentBrick)));
                    }
                    else if (brickEntry > short.MinValue)
                    {
                        SetBrick(currentBrick, -1);
                    }

                    currentBrick++;
                }
            }

            args.FreeListGenerationNumber--;
            while (args.FreeListGenerationNumber >=
                (int)gc_generation_num.soh_gen0)
            {
                generation* generationState =
                    GetGeneration(args.FreeListGenerationNumber);
                byte* gap = AllocateAtEnd(
                    GCEnvironment.AlignUp(
                        MinObjectSize,
                        (nuint)sizeof(void*)));
                if (generationState is null || gap is null)
                {
                    return E_FAIL;
                }

                ResetAllocationPointers(generationState, gap);
                FormatUnusedArray(
                    gap,
                    GCEnvironment.AlignUp(
                        MinObjectSize,
                        (nuint)sizeof(void*)));
                args.FreeListGenerationNumber--;
            }

            s_sohSegment.used = s_sohSegment.allocated;
            return S_OK;
        }

        private static int MakeFreeListInBrick(
            byte* tree,
            MakeFreeArgs* args)
        {
            if (tree is null ||
                args is null ||
                args->FreeListGeneration is null)
            {
                return E_FAIL;
            }

            nint rightNode = GetNodeRightChild(tree);
            nint leftNode = GetNodeLeftChild(tree);
            args->HighestPlug = null;
            if (leftNode != 0)
            {
                int leftResult =
                    MakeFreeListInBrick(tree + leftNode, args);
                if (leftResult != S_OK)
                {
                    return leftResult;
                }
            }

            byte* plug = tree;
            nint nativeGapSize = GetNodeGapSize(tree);
            if (nativeGapSize < 0)
            {
                return E_FAIL;
            }

            nuint gapSize = (nuint)nativeGapSize;
            byte* gap = plug - (nint)gapSize;
            args->HighestPlug = tree;

            fixed (heap_segment* ephemeralSegment = &s_sohSegment)
            {
                while (args->CurrentGenerationLimit != (byte*)nuint.MaxValue &&
                    plug >= args->CurrentGenerationLimit &&
                    IsAddressInSegment(plug, ephemeralSegment))
                {
                    args->FreeListGenerationNumber--;
                    args->FreeListGeneration =
                        GetGeneration(args->FreeListGenerationNumber);
                    if (args->FreeListGeneration is null)
                    {
                        return E_FAIL;
                    }

                    ResetAllocationPointers(args->FreeListGeneration, gap);
                    args->CurrentGenerationLimit =
                        GetGenerationLimit(args->FreeListGenerationNumber);
                    nuint alignedMinimumObjectSize =
                        GCEnvironment.AlignUp(
                            MinObjectSize,
                            (nuint)sizeof(void*));
                    if (gapSize >= 2 * alignedMinimumObjectSize)
                    {
                        FormatUnusedArray(gap, alignedMinimumObjectSize);
                        gap += (nint)alignedMinimumObjectSize;
                        gapSize -= alignedMinimumObjectSize;
                    }
                    else
                    {
                        if (gapSize != 0)
                        {
                            FormatUnusedArray(gap, gapSize);
                        }

                        gapSize = 0;
                    }
                }
            }

            int threadResult =
                ThreadGap(gap, gapSize, args->FreeListGeneration);
            if (threadResult != S_OK)
            {
                return threadResult;
            }

            return rightNode == 0
                ? S_OK
                : MakeFreeListInBrick(tree + rightNode, args);
        }

        private static int SweepUohObjects(int generationNumber)
        {
            generation* generationState = GetGeneration(generationNumber);
            if (generationState is null)
            {
                return E_FAIL;
            }

            heap_segment* startSegment =
                HeapSegmentRw(generationState->start_segment);
            if (startSegment is null)
            {
                return E_FAIL;
            }

            ClearAllocator(&generationState->free_list_allocator);
            generationState->free_list_space = 0;
            generationState->free_obj_space = 0;
            generationState->free_list_allocated = 0;

            heap_segment* segment = startSegment;
            while (segment is not null)
            {
                byte* current = segment == startSegment
                    ? generationState->allocation_start
                    : segment->mem;
                byte* plugEnd = current;
                while (current < segment->allocated)
                {
                    Object* obj = (Object*)current;
                    nuint objectSize = GetObjectSize(obj);
                    nuint alignedSize = GCEnvironment.AlignUp(
                        objectSize,
                        (nuint)sizeof(void*));
                    if (objectSize < MinObjectSize ||
                        alignedSize < objectSize ||
                        alignedSize > (nuint)(segment->allocated - current))
                    {
                        return E_FAIL;
                    }

                    if (obj->IsMarked())
                    {
                        int gapResult = ThreadGap(
                            plugEnd,
                            (nuint)(current - plugEnd),
                            generationState);
                        if (gapResult != S_OK)
                        {
                            return gapResult;
                        }

                        do
                        {
                            obj->ClearMarked();
                            ClearPinned(current);
                            current += (nint)alignedSize;
                            if (current >= segment->allocated)
                            {
                                break;
                            }

                            obj = (Object*)current;
                            objectSize = GetObjectSize(obj);
                            alignedSize = GCEnvironment.AlignUp(
                                objectSize,
                                (nuint)sizeof(void*));
                            if (objectSize < MinObjectSize ||
                                alignedSize < objectSize ||
                                alignedSize >
                                    (nuint)(segment->allocated - current))
                            {
                                return E_FAIL;
                            }
                        }
                        while (obj->IsMarked());

                        plugEnd = current;
                    }
                    else
                    {
                        current += (nint)alignedSize;
                    }
                }

                if ((segment->flags & HeapSegmentReadOnly) == 0)
                {
                    segment->allocated = plugEnd;
                    if (segment->used > plugEnd)
                    {
                        segment->used = plugEnd;
                    }
                }

                segment = HeapSegmentNextRw(segment);
            }

            generationState->allocation_segment = startSegment;
            generationState->allocation_context.alloc_ptr = null;
            generationState->allocation_context.alloc_limit = null;
            generationState->allocation_context_start_region = null;
            return S_OK;
        }

        private static int ThreadGap(
            byte* gapStart,
            nuint size,
            generation* generationState)
        {
            if (size == 0)
            {
                return S_OK;
            }

            nuint alignedMinimumObjectSize =
                GCEnvironment.AlignUp(MinObjectSize, (nuint)sizeof(void*));
            if (gapStart is null ||
                generationState is null ||
                size < alignedMinimumObjectSize ||
                size > uint.MaxValue + MinObjectSize)
            {
                return E_FAIL;
            }

            FormatUnusedArray(gapStart, size);
            if (size >= 2 * alignedMinimumObjectSize)
            {
                generationState->free_list_space += size;
                ThreadFreeItem(
                    &generationState->free_list_allocator,
                    gapStart);
            }
            else
            {
                generationState->free_obj_space += size;
            }

            return S_OK;
        }

        private static void ThreadFreeItem(
            allocator* allocatorState,
            byte* item)
        {
            alloc_list* list = &allocatorState->first_bucket;
            byte** next = ((byte**)item) + 2;
            byte** undo = ((byte**)item) - 1;
            byte** previous = ((byte**)item) + 3;
            *next = null;
            *undo = (byte*)1;
            *previous = allocatorState->gen_number ==
                (int)gc_generation_num.max_generation
                    ? list->tail
                    : null;

            if (list->head is null)
            {
                list->head = item;
            }
            else
            {
                *(((byte**)list->tail) + 2) = item;
            }

            list->tail = item;
        }

        private static int GetPlanGenerationNumber(int generationNumber)
        {
            return s_settings.promotion != 0 &&
                generationNumber < (int)gc_generation_num.max_generation
                    ? generationNumber + 1
                    : generationNumber;
        }

        private static byte* GetGenerationLimit(int generationNumber)
        {
            if (s_settings.promotion != 0)
            {
                if (generationNumber <= (int)gc_generation_num.soh_gen1)
                {
                    return s_sohSegment.reserved;
                }

                generation* generationState =
                    GetGeneration(generationNumber - 2);
                return generationState is null
                    ? null
                    : generationState->allocation_start;
            }

            if (generationNumber <= (int)gc_generation_num.soh_gen0)
            {
                return s_sohSegment.reserved;
            }

            generation* previousGeneration =
                GetGeneration(generationNumber - 1);
            return previousGeneration is null
                ? null
                : previousGeneration->allocation_start;
        }

        private static byte* AllocateAtEnd(nuint size)
        {
            size = GCEnvironment.AlignUp(size, (nuint)sizeof(void*));
            byte* result = s_sohSegment.allocated;
            if (result is null ||
                result > s_sohSegment.committed ||
                size > (nuint)(s_sohSegment.committed - result))
            {
                return null;
            }

            s_sohSegment.allocated += (nint)size;
            return result;
        }

        private static void ResetAllocationPointers(
            generation* generationState,
            byte* start)
        {
            generationState->allocation_start = start;
            generationState->allocation_context.alloc_ptr = null;
            generationState->allocation_context.alloc_limit = null;
            generationState->allocation_context_start_region = null;
            fixed (heap_segment* ephemeralSegment = &s_sohSegment)
            {
                generationState->allocation_segment = ephemeralSegment;
            }
        }

        private static nuint RecoverSavedPinnedInfo()
        {
            ResetPinnedQueueBos();
            nuint totalRecoveredSweepSize = 0;
            while (!PinnedPlugQueueEmpty())
            {
                mark* entry = OldestPin();
                if (entry->saved_pre_p != 0)
                {
                    *(gap_reloc_pair*)(
                        entry->first - sizeof(plug_and_gap)) =
                        entry->saved_pre_plug;
                    totalRecoveredSweepSize +=
                        (nuint)sizeof(gap_reloc_pair);
                }

                if (entry->saved_post_p != 0)
                {
                    if (entry->saved_post_plug_info_start is null)
                    {
                        FailFast();
                        return 0;
                    }

                    *(gap_reloc_pair*)entry->saved_post_plug_info_start =
                        entry->saved_post_plug;
                    totalRecoveredSweepSize +=
                        (nuint)sizeof(gap_reloc_pair);
                }

                DequeuePinnedPlug();
            }

            return totalRecoveredSweepSize;
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
