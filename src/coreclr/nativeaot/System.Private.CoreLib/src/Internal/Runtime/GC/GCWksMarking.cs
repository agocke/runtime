// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace Internal.Runtime.GC
{
    internal static unsafe partial class GCWksInitialization
    {
        private const nuint InitialMarkStackLength = 256;
        private const int FinalizerStartSegment = (int)gc_generation_num.total_generation_count;
        private const int FinalizerMaxSegment = FinalizerStartSegment + 1;
        private const nuint LargeObjectAlignment = 8;
        private const nuint HeapSegmentReadOnly = 1;

        private static mark* s_markStack;
        private static nuint s_markStackLength;
        private static nuint s_markStackTos;
        private static nuint s_markStackBos;
        private static nuint s_savedPinnedPlugIndex;
        private static Object** s_markList;
        private static nuint s_markListLength;
        private static nuint s_markListIndex;
        private static nuint s_promotedBytes;
        private static byte* s_markLow;
        private static byte* s_markHigh;
        private static byte* s_minOverflowAddress;
        private static byte* s_maxOverflowAddress;
        private static byte* s_lowestMarkedAddress;
        private static byte* s_highestMarkedAddress;
        private static nuint s_numPinnedObjects;
        private static bool s_useMarkList;

        internal static nuint NumPinnedObjects => s_numPinnedObjects;

        public static bool IsPromoted(IGCHeap* heap, Object* obj)
        {
            _ = heap;
            return IsPromotedObject(obj);
        }

        public static bool IsPromotedObject(Object* obj)
        {
            return obj is not null && obj->IsMarked();
        }

        private static void PromoteCore(Object** objectReference)
        {
            if (objectReference is not null && *objectReference is not null)
            {
                MarkObject(*objectReference);
            }
        }

        private static void Promote(Object** objectReference, ScanContext* scanContext, uint flags)
        {
            _ = scanContext;
            if (objectReference is null || *objectReference is null)
            {
                return;
            }

            byte* address = (byte*)*objectReference;
            if (!IsInFindObjectRange(address) || !IsInMarkRange(address))
            {
                return;
            }

            Object* objectToPromote = *objectReference;
            if ((flags & (uint)GCInterfaceConstants.GC_CALL_INTERIOR) != 0)
            {
                objectToPromote = FindObject(address);
                if (objectToPromote is null)
                {
                    return;
                }
            }

            if ((flags & (uint)GCInterfaceConstants.GC_CALL_PINNED) != 0)
            {
                objectToPromote->GetHeader()->SetGCBit();
                s_numPinnedObjects++;
            }

            MarkObject(objectToPromote);
        }

        private static int RunMarkPhaseCore(int condemnedGeneration)
        {
            if (condemnedGeneration == -1)
            {
                condemnedGeneration = (int)gc_generation_num.max_generation;
            }

            if (condemnedGeneration < (int)gc_generation_num.soh_gen0 ||
                condemnedGeneration > (int)gc_generation_num.max_generation)
            {
                return E_NOTIMPL;
            }

            if (condemnedGeneration == (int)gc_generation_num.max_generation)
            {
                ClearStaleMarksForFullCollection();
            }

            IGCToCLR* callback = GCCommon.g_theGCToCLR;
            if (callback is null || callback->Vtable is null || !InitializeMarkState())
            {
                return E_FAIL;
            }

            ResetPinnedQueue();
            s_savedPinnedPlugIndex = nuint.MaxValue;
            s_markListIndex = 0;
            s_promotedBytes = 0;
            s_minOverflowAddress = (byte*)nuint.MaxValue;
            s_maxOverflowAddress = null;
            s_lowestMarkedAddress = (byte*)nuint.MaxValue;
            s_highestMarkedAddress = null;
            s_numPinnedObjects = 0;
            if (condemnedGeneration == (int)gc_generation_num.max_generation)
            {
                s_markLow = GCCommon.g_gc_lowest_address;
                s_markHigh = GCCommon.g_gc_highest_address;
            }
            else
            {
                generation* condemnedGenerationState = GetGeneration(condemnedGeneration);
                if (condemnedGenerationState is null)
                {
                    return E_FAIL;
                }

                s_markLow = condemnedGenerationState->allocation_start;
                s_markHigh = s_sohSegment.reserved;
            }
            s_useMarkList = condemnedGeneration < (int)gc_generation_num.max_generation &&
                s_markList is not null;

            ScanContext scanContext = default;
            scanContext.thread_number = 0;
            scanContext.thread_count = 1;
            scanContext.promotion = true;
            scanContext.concurrent = false;

            if (callback->Vtable->BeforeGcScanRoots is not null)
            {
                callback->Vtable->BeforeGcScanRoots(callback, condemnedGeneration, false, false);
            }

            if (callback->Vtable->GetTotalNumSizedRefHandles is not null &&
                callback->Vtable->GetTotalNumSizedRefHandles(callback) != 0)
            {
                GCHandleTables.ScanSizedRefForPromotion(&Promote, &scanContext);
                ProcessMarkOverflow();
                DrainMarkQueue();
            }

            MarkReadOnlySegments();

            if (callback->Vtable->GcScanRoots is not null)
            {
                callback->Vtable->GcScanRoots(
                    callback,
                    &Promote,
                    condemnedGeneration,
                    (int)gc_generation_num.max_generation,
                    &scanContext);
            }

            ProcessMarkOverflow();
            DrainMarkQueue();

            ScanFinalizationQueue();
            ProcessMarkOverflow();
            DrainMarkQueue();

            GCHandleTables.ScanForPromotion(
                &Promote,
                &scanContext,
                condemnedGeneration,
                (int)gc_generation_num.max_generation);
            ProcessMarkOverflow();
            DrainMarkQueue();

            if (condemnedGeneration < (int)gc_generation_num.max_generation)
            {
                MarkThroughCardsForSegments();
                for (int generationNumber = (int)gc_generation_num.uoh_start_generation;
                    generationNumber < (int)gc_generation_num.total_generation_count;
                    generationNumber++)
                {
                    MarkThroughCardsForUohObjects(generationNumber);
                }

                ProcessMarkOverflow();
                DrainMarkQueue();
            }

            ScanDependentHandles(&scanContext);
            ProcessMarkOverflow();
            DrainMarkQueue();

            if (callback->Vtable->AfterGcScanRoots is not null)
            {
                callback->Vtable->AfterGcScanRoots(
                    callback,
                    condemnedGeneration,
                    (int)gc_generation_num.max_generation,
                    &scanContext);
            }

            GCHandleTables.ClearUnpromotedHandles(HandleType.HNDTYPE_WEAK_SHORT);
            ScanForFinalization(condemnedGeneration, &scanContext);
            ProcessMarkOverflow();
            DrainMarkQueue();

            ScanDependentHandles(&scanContext);
            ProcessMarkOverflow();
            DrainMarkQueue();

            GCHandleTables.ClearUnpromotedHandles(HandleType.HNDTYPE_WEAK_LONG);
            GCHandleTables.ClearUnpromotedDependentHandles();

            if (callback->Vtable->SyncBlockCacheWeakPtrScan is not null)
            {
                callback->Vtable->SyncBlockCacheWeakPtrScan(
                    callback,
                    &CheckPromoted,
                    (nuint)(void*)&scanContext,
                    0);
            }

            return S_OK;
        }

        private static void ClearStaleMarksForFullCollection()
        {
            fixed (generation* generation2 = &s_generation2)
            {
                heap_segment* firstSohSegment = HeapSegmentRw(generation2->start_segment);
                for (heap_segment* segment = firstSohSegment;
                    segment is not null;
                    segment = HeapSegmentNextRw(segment))
                {
                    byte* start = segment == firstSohSegment
                        ? GetSohStartObject(segment, generation2)
                        : segment->mem;
                    if (!ClearMarkedObjectsInSegment(segment, start))
                    {
                        FailFast();
                        return;
                    }
                }
            }

            fixed (heap_segment* loh = &s_lohSegment)
            {
                if (!ClearMarkedObjectsInGeneration(
                    GetGeneration((int)gc_generation_num.loh_generation),
                    HeapSegmentRw(loh)))
                {
                    FailFast();
                    return;
                }
            }

            fixed (heap_segment* poh = &s_pohSegment)
            {
                if (!ClearMarkedObjectsInGeneration(
                    GetGeneration((int)gc_generation_num.poh_generation),
                    HeapSegmentRw(poh)))
                {
                    FailFast();
                }
            }
        }

        private static bool ClearMarkedObjectsInGeneration(
            generation* generationState,
            heap_segment* firstSegment)
        {
            if (generationState is null || firstSegment is null)
            {
                return true;
            }

            bool first = true;
            for (heap_segment* segment = firstSegment;
                segment is not null;
                segment = HeapSegmentNextRw(segment))
            {
                byte* start = first
                    ? generationState->allocation_start
                    : segment->mem;
                if (!ClearMarkedObjectsInSegment(segment, start))
                {
                    return false;
                }

                first = false;
            }

            return true;
        }

        private static bool ClearMarkedObjectsInSegment(heap_segment* segment, byte* start)
        {
            if (segment is null)
            {
                return true;
            }

            if (segment->mem is null ||
                segment->allocated is null ||
                start is null ||
                start < segment->mem ||
                start > segment->allocated)
            {
                return false;
            }

            byte* current = start;
            byte* end = segment->allocated;
            while (current < end)
            {
                Object* obj = (Object*)current;
                if (obj->RawGetMethodTable() is null)
                {
                    byte* nextObject = FindNextObjectAfterGap(current, end);
                    if (nextObject is null)
                    {
                        return true;
                    }

                    current = nextObject;
                    continue;
                }

                nuint size = GetObjectSize(obj);
                nuint alignedSize = GCEnvironment.AlignUp(size, (nuint)sizeof(void*));
                if (size < MinObjectSize ||
                    alignedSize < size ||
                    alignedSize > (nuint)(end - current))
                {
                    return false;
                }

                obj->ClearMarked();
                current += (nint)alignedSize;
            }

            return true;
        }

        private static byte* FindNextObjectAfterGap(byte* current, byte* end)
        {
            if (s_brickTable is null ||
                current >= end)
            {
                return null;
            }

            nuint currentBrick = GetBrickIndex(current);
            nuint endBrick = GetBrickIndex(end - 1);
            while (currentBrick < endBrick)
            {
                currentBrick++;
                nint brickEntry = s_brickTable[currentBrick];
                if (brickEntry < 0)
                {
                    continue;
                }

                byte* candidate = BrickAddress(currentBrick) + brickEntry - 1;
                if (candidate > current && candidate < end)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static bool InitializeMarkState()
        {
            if (s_markStack is null)
            {
                if (!TryMultiply(InitialMarkStackLength, (nuint)sizeof(mark), out nuint stackBytes))
                {
                    return false;
                }

                mark* stack = (mark*)GCToOSInterface.AllocateUnmanaged(stackBytes);
                if (stack is null)
                {
                    return false;
                }

                s_markStack = stack;
                s_markStackLength = InitialMarkStackLength;
            }

            if (s_markList is null)
            {
                if (!TryMultiply(InitialMarkStackLength, (nuint)sizeof(Object*), out nuint listBytes))
                {
                    return false;
                }

                Object** list = (Object**)GCToOSInterface.AllocateUnmanaged(listBytes);
                if (list is null)
                {
                    return false;
                }

                s_markList = list;
                s_markListLength = InitialMarkStackLength;
            }

            return true;
        }

        private static void MarkObject(Object* obj)
        {
            if (obj is null || !IsInMarkRange((byte*)obj) || obj->IsMarked())
            {
                return;
            }

            obj->SetMarked();
            RecordPromoted(obj);
            MethodTable* methodTable = obj->GetGCSafeMethodTable();
            if (methodTable is not null && methodTable->ContainsGCPointersOrCollectible())
            {
                PushMark(obj);
            }
        }

        private static void CheckPromoted(Object** objectReference, nuint* extraInfo, nuint param1, nuint param2)
        {
            _ = extraInfo;
            _ = param1;
            _ = param2;
            if (objectReference is not null &&
                *objectReference is not null &&
                !IsPromotedObject(*objectReference))
            {
                *objectReference = null;
            }
        }

        private static void PushMark(Object* obj)
        {
            if (s_markStackTos == s_markStackLength)
            {
                if (!GrowMarkStack())
                {
                    if ((nuint)obj < (nuint)s_minOverflowAddress)
                    {
                        s_minOverflowAddress = (byte*)obj;
                    }

                    if ((nuint)obj > (nuint)s_maxOverflowAddress)
                    {
                        s_maxOverflowAddress = (byte*)obj;
                    }

                    return;
                }
            }

            mark* entry = &s_markStack[s_markStackTos];
            *entry = default;
            entry->first = (byte*)obj;
            s_markStackTos++;
        }

        private static bool GrowMarkStack()
        {
            if (s_markStackLength > nuint.MaxValue / 2)
            {
                return false;
            }

            nuint newLength = s_markStackLength == 0
                ? InitialMarkStackLength
                : s_markStackLength * 2;
            if (!TryMultiply(newLength, (nuint)sizeof(mark), out nuint newBytes))
            {
                return false;
            }

            mark* newStack = (mark*)GCToOSInterface.AllocateUnmanaged(newBytes);
            if (newStack is null)
            {
                return false;
            }

            for (nuint i = 0; i < s_markStackTos; i++)
            {
                newStack[i] = s_markStack[i];
            }

            GCToOSInterface.FreeUnmanaged(s_markStack);
            s_markStack = newStack;
            s_markStackLength = newLength;
            return true;
        }

        private static void ResetPinnedQueue()
        {
            s_markStackTos = 0;
            s_markStackBos = 0;
        }

        private static void ResetPinnedQueueBos()
        {
            s_markStackBos = 0;
        }

        private static mark* PinnedPlugOf(nuint bos)
        {
            return &s_markStack[bos];
        }

        private static mark* OldestPin()
        {
            return PinnedPlugOf(s_markStackBos);
        }

        private static bool PinnedPlugQueueEmpty()
        {
            return s_markStackBos == s_markStackTos;
        }

        private static nuint DequeuePinnedPlug()
        {
            nuint index = s_markStackBos;
            s_markStackBos++;
            return index;
        }

        private static mark* BeforeOldestPin()
        {
            return s_markStackBos >= 1 ? PinnedPlugOf(s_markStackBos - 1) : null;
        }

        private static void SetAllocatorNextPin(generation* generationState)
        {
            if (generationState is null || PinnedPlugQueueEmpty())
            {
                return;
            }

            mark* oldestEntry = OldestPin();
            byte* plug = oldestEntry->first;
            byte* allocationPointer = generationState->allocation_context.alloc_ptr;
            byte* allocationLimit = generationState->allocation_context.alloc_limit;
            if (plug >= allocationPointer && plug < allocationLimit)
            {
                generationState->allocation_context.alloc_limit = plug;
            }
        }

        private static void SetPinnedInfo(byte* lastPinnedPlug, nuint plugLength, generation* generationState)
        {
            mark* entry = &s_markStack[s_markStackTos];
            if (entry->first != lastPinnedPlug)
            {
                GCToOSInterface.DebugBreak();
            }

            entry->len = plugLength;
            s_markStackTos++;
            SetAllocatorNextPin(generationState);
        }

        private static nuint ClearSpecialBits(byte* node)
        {
            Object* obj = (Object*)node;
            nuint rawMethodTable = (nuint)obj->RawGetMethodTable();
            nuint specialBits = rawMethodTable & 7;
            obj->RawSetMethodTable((MethodTable*)(rawMethodTable & ~((nuint)7)));
            return specialBits;
        }

        private static void SetSpecialBits(byte* node, nuint specialBits)
        {
            if (specialBits == 0)
            {
                return;
            }

            Object* obj = (Object*)node;
            obj->RawSetMethodTable((MethodTable*)((nuint)obj->RawGetMethodTable() | specialBits));
        }

        private static bool IsPinnedObject(byte* node)
        {
            return (((Object*)node)->GetHeader()->GetBits() & ObjHeader.BIT_SBLK_GC_RESERVE) != 0;
        }

        private static void SetShortReferenceBits(
            mark* entry,
            byte* objectAddress,
            nuint objectSize,
            byte* plugBoundary,
            bool pre)
        {
            MethodTable* methodTable = ((Object*)objectAddress)->GetGCSafeMethodTable();
            CGCDescSeries* current = GetHighestSeries(methodTable);
            nint seriesCount = GetSeriesCount(methodTable);
            byte* gapStart = plugBoundary - sizeof(gap_reloc_pair) - sizeof(ObjHeader);

            if (seriesCount >= 0)
            {
                CGCDescSeries* lowest = GetLowestSeries(methodTable, seriesCount);
                do
                {
                    byte* slot = objectAddress + (nint)current->startoffset;
                    byte* stop = slot + (nint)current->seriessize + objectSize;
                    while (slot < stop)
                    {
                        nuint gapOffset = ((nuint)slot - (nuint)gapStart) / (nuint)sizeof(byte*);
                        if (pre)
                        {
                            entry->SetPreShortBit(gapOffset);
                        }
                        else
                        {
                            entry->SetPostShortBit(gapOffset);
                        }

                        slot += sizeof(byte*);
                    }

                    current--;
                }
                while (current >= lowest);

                return;
            }

            byte* repeatingSlot = objectAddress + (nint)current->startoffset;
            byte* objectEnd = objectAddress + objectSize - sizeof(ObjHeader);
            val_serie_item* valueSeries = &current->val_serie;
            while (repeatingSlot < objectEnd)
            {
                for (nint i = 0; i > seriesCount; i--)
                {
                    val_serie_item item = valueSeries[i];
                    byte* stop = repeatingSlot + ((nuint)item.nptrs * (nuint)sizeof(byte*));
                    while (repeatingSlot < stop)
                    {
                        nuint gapOffset = ((nuint)repeatingSlot - (nuint)gapStart) / (nuint)sizeof(byte*);
                        if (pre)
                        {
                            entry->SetPreShortBit(gapOffset);
                        }
                        else
                        {
                            entry->SetPostShortBit(gapOffset);
                        }

                        repeatingSlot += sizeof(byte*);
                    }

                    repeatingSlot += item.skip;
                }
            }
        }

        private static void EnquePinnedPlug(
            byte* plug,
            bool savePrePlugInfo,
            byte* lastObjectInLastPlug)
        {
            if (s_markStackLength <= s_markStackTos && !GrowMarkStack())
            {
                FailFast();
                return;
            }

            mark* entry = &s_markStack[s_markStackTos];
            entry->first = plug;
            entry->saved_pre_p = savePrePlugInfo ? 1 : 0;

            if (savePrePlugInfo)
            {
                nuint specialBits = ClearSpecialBits(lastObjectInLastPlug);
                gap_reloc_pair* plugInfo = (gap_reloc_pair*)(plug - sizeof(plug_and_gap));
                entry->saved_pre_plug = *plugInfo;
                SetSpecialBits(lastObjectInLastPlug, specialBits);
                entry->saved_pre_plug_reloc = *plugInfo;

                nuint lastObjectSize = (nuint)(plug - lastObjectInLastPlug);
                if (lastObjectSize < (nuint)sizeof(gap_reloc_pair) + MinObjectSize)
                {
                    entry->SetPreShort();
                    MethodTable* methodTable = ((Object*)lastObjectInLastPlug)->GetGCSafeMethodTable();
                    if (methodTable->Collectible())
                    {
                        entry->SetPreShortCollectible();
                    }

                    if (methodTable->ContainsGCPointers())
                    {
                        SetShortReferenceBits(entry, lastObjectInLastPlug, lastObjectSize, plug, true);
                    }
                }
            }

            entry->saved_post_p = 0;
        }

        private static void SavePostPlugInfo(
            byte* lastPinnedPlug,
            byte* lastObjectInLastPlug,
            byte* postPlug)
        {
            mark* entry = &s_markStack[s_markStackTos - 1];
            if (lastPinnedPlug != entry->first)
            {
                GCToOSInterface.DebugBreak();
            }

            entry->saved_post_plug_info_start = postPlug - sizeof(plug_and_gap);
            nuint specialBits = ClearSpecialBits(lastObjectInLastPlug);
            gap_reloc_pair* postPlugInfo = (gap_reloc_pair*)entry->saved_post_plug_info_start;
            entry->saved_post_plug = *postPlugInfo;
            SetSpecialBits(lastObjectInLastPlug, specialBits);
            entry->saved_post_plug_reloc = *postPlugInfo;
            entry->saved_post_p = 1;
            entry->saved_post_plug_debug.gap = 1;

            nuint lastObjectSize = (nuint)(postPlug - lastObjectInLastPlug);
            if (lastObjectSize < (nuint)sizeof(gap_reloc_pair) + MinObjectSize)
            {
                entry->SetPostShort();
                MethodTable* methodTable = ((Object*)lastObjectInLastPlug)->GetGCSafeMethodTable();
                if (methodTable->Collectible())
                {
                    entry->SetPostShortCollectible();
                }

                if (methodTable->ContainsGCPointers())
                {
                    SetShortReferenceBits(entry, lastObjectInLastPlug, lastObjectSize, postPlug, false);
                }
            }
        }

        private static void RecordPromoted(Object* obj)
        {
            byte* address = (byte*)obj;
            if ((nuint)address < (nuint)s_lowestMarkedAddress)
            {
                s_lowestMarkedAddress = address;
            }

            if ((nuint)address > (nuint)s_highestMarkedAddress)
            {
                s_highestMarkedAddress = address;
            }

            if (s_useMarkList)
            {
                if (s_markListIndex < s_markListLength)
                {
                    s_markList[s_markListIndex] = obj;
                }

                s_markListIndex++;
            }

            nuint size = GetObjectSize(obj);
            if (s_promotedBytes > nuint.MaxValue - size)
            {
                FailFast();
                return;
            }

            s_promotedBytes += size;
        }

        private static void DrainMarkQueue()
        {
            while (s_markStackTos != 0)
            {
                mark* entry = &s_markStack[--s_markStackTos];
                Object* obj = (Object*)entry->first;
                MarkThroughObject(obj, true);
            }
        }

        private static bool ProcessMarkOverflow()
        {
            bool overflow = false;
            DrainMarkQueue();

            while (s_maxOverflowAddress is not null || s_minOverflowAddress != (byte*)nuint.MaxValue)
            {
                overflow = true;
                byte* minAddress = s_minOverflowAddress;
                byte* maxAddress = s_maxOverflowAddress;
                s_minOverflowAddress = (byte*)nuint.MaxValue;
                s_maxOverflowAddress = null;

                ScanOverflowRange(minAddress, maxAddress);
                DrainMarkQueue();
            }

            return overflow;
        }

        private static void ScanOverflowRange(byte* minAddress, byte* maxAddress)
        {
            if (minAddress is null || maxAddress is null || minAddress > maxAddress)
            {
                return;
            }

            fixed (heap_segment* soh = &s_sohSegment)
            fixed (heap_segment* loh = &s_lohSegment)
            fixed (heap_segment* poh = &s_pohSegment)
            {
                ScanOverflowSegment(soh, minAddress, maxAddress);
                ScanOverflowSegment(loh, minAddress, maxAddress);
                ScanOverflowSegment(poh, minAddress, maxAddress);
            }
        }

        private static void ScanOverflowSegment(heap_segment* segment, byte* minAddress, byte* maxAddress)
        {
            if (segment is null || segment->mem is null || segment->allocated <= segment->mem)
            {
                return;
            }

            byte* current = segment->mem > minAddress ? segment->mem : minAddress;
            byte* end = segment->allocated;
            while (current < end && current <= maxAddress)
            {
                Object* obj = (Object*)current;
                if (obj->IsMarked())
                {
                    MarkThroughObject(obj, true);
                }

                nuint size = GetObjectSize(obj);
                if (size < MinObjectSize || size > (nuint)(end - current))
                {
                    return;
                }

                nuint alignedSize = GCEnvironment.AlignUp(size, (nuint)sizeof(void*));
                if (alignedSize < size || alignedSize > (nuint)(end - current))
                {
                    return;
                }

                current += alignedSize;
            }
        }

        private static void MarkReadOnlySegments()
        {
            for (heap_segment* segment = s_generation2.start_segment;
                segment is not null && (segment->flags & HeapSegmentReadOnly) != 0;
                segment = segment->next)
            {
                if (s_markLow is not null &&
                    s_markHigh is not null &&
                    segment->mem is not null &&
                    segment->allocated is not null &&
                    segment->reserved is not null &&
                    segment->reserved > s_markLow &&
                    segment->mem < s_markHigh)
                {
                    byte* current = segment->mem;
                    while (current < segment->allocated)
                    {
                        Object* obj = (Object*)current;
                        obj->SetMarked();

                        nuint size = GetObjectSize(obj);
                        if (size < MinObjectSize || size > (nuint)(segment->allocated - current))
                        {
                            FailFast();
                            return;
                        }

                        nuint alignedSize = GCEnvironment.AlignUp(size, (nuint)sizeof(void*));
                        if (alignedSize < size || alignedSize > (nuint)(segment->allocated - current))
                        {
                            FailFast();
                            return;
                        }

                        current += alignedSize;
                    }
                }
            }
        }

        private static void MarkThroughObject(Object* obj, bool markClassObject)
        {
            MethodTable* methodTable = obj->GetGCSafeMethodTable();
            if (methodTable is null)
            {
                return;
            }

            bool markLoaderAllocator = markClassObject && methodTable->Collectible();
            if (!methodTable->ContainsGCPointers() && !markLoaderAllocator)
            {
                return;
            }

            if (markLoaderAllocator)
            {
                IGCToCLR* callback = GCCommon.g_theGCToCLR;
                if (callback is not null &&
                    callback->Vtable is not null &&
                    callback->Vtable->GetLoaderAllocatorObjectForGC is not null)
                {
                    Object* loaderAllocator = (Object*)callback->Vtable->GetLoaderAllocatorObjectForGC(callback, obj);
                    MarkObject(loaderAllocator);
                }
            }

            if (methodTable->ContainsGCPointers())
            {
                TraverseObjectReferences(obj);
            }
        }

        private static void TraverseObjectReferences(Object* obj)
        {
            MethodTable* methodTable = obj->GetGCSafeMethodTable();
            nuint objectSize = GetObjectSize(obj);
            nint seriesCount = GetSeriesCount(methodTable);
            CGCDescSeries* current = GetHighestSeries(methodTable);

            if (seriesCount >= 0)
            {
                CGCDescSeries* lowest = GetLowestSeries(methodTable, seriesCount);
                do
                {
                    byte* slot = (byte*)obj + (nint)current->startoffset;
                    byte* stop = slot + (nint)current->seriessize + objectSize;
                    while (slot < stop)
                    {
                        MarkObject(*(Object**)slot);
                        slot += sizeof(void*);
                    }

                    current--;
                }
                while (current >= lowest);

                return;
            }

            byte* repeatingSlot = (byte*)obj + (nint)current->startoffset;
            byte* objectEnd = (byte*)obj + objectSize - sizeof(ObjHeader);
            val_serie_item* valueSeries = &current->val_serie;
            while (repeatingSlot < objectEnd)
            {
                for (nint i = 0; i > seriesCount; i--)
                {
                    val_serie_item item = valueSeries[i];
                    byte* stop = repeatingSlot + ((nuint)item.nptrs * (nuint)sizeof(void*));
                    while (repeatingSlot < stop)
                    {
                        MarkObject(*(Object**)repeatingSlot);
                        repeatingSlot += sizeof(void*);
                    }

                    repeatingSlot += item.skip;
                }
            }
        }

        private static void MarkThroughCardsForSegments()
        {
            if (s_cardTable is null ||
                s_markLow is null ||
                s_markHigh is null ||
                s_generation2.start_segment is null)
            {
                return;
            }

            fixed (heap_segment* soh = &s_sohSegment)
            {
                heap_segment* segment = HeapSegmentRw(s_generation2.start_segment);
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
                    byte* end = segment->allocated;
                    if (segment == soh &&
                        s_markLow > begin &&
                        s_markLow < end)
                    {
                        end = s_markLow;
                    }

                    if (begin is not null && begin < end)
                    {
                        ScanDirtyCardsInSegment(
                            segment,
                            begin,
                            end,
                            false,
                            segment == soh);
                    }

                    firstSegment = false;
                    segment = HeapSegmentNextRw(segment);
                }
            }
        }

        private static void MarkThroughCardsForUohObjects(int generationNumber)
        {
            generation* generationState = GetGeneration(generationNumber);
            if (generationState is null ||
                generationState->start_segment is null ||
                s_cardTable is null ||
                s_markLow is null ||
                s_markHigh is null)
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
                byte* end = segment->allocated;
                if (begin is not null && begin < end)
                {
                    ScanDirtyCardsInSegment(segment, begin, end, true, false);
                }

                firstSegment = false;
                segment = HeapSegmentNextRw(segment);
            }
        }

        private static byte* GetUohStartObjectForMarking(generation* generationState)
        {
            byte* start = generationState->allocation_start;
            if (start is null)
            {
                return null;
            }

            nuint size = GetObjectSize((Object*)start);
            nuint alignedSize = GCEnvironment.AlignUp(size, LargeObjectAlignment);
            if (alignedSize < size)
            {
                return null;
            }

            return start + (nint)alignedSize;
        }

        private static Object* FindFirstObjectForCard(
            byte* start,
            Object* lastObject,
            heap_segment* segment,
            bool uoh,
            byte* scanEnd)
        {
            Object* obj = lastObject;
            if (s_brickTable is not null &&
                start > (byte*)lastObject &&
                GetBrickIndex(start) != GetBrickIndex((byte*)lastObject))
            {
                nint minimumBrick = (nint)GetBrickIndex((byte*)lastObject);
                nint previousBrick = (nint)GetBrickIndex(start) - 1;
                nint brickEntry = 0;
                while (previousBrick >= minimumBrick)
                {
                    brickEntry = s_brickTable[(nuint)previousBrick];
                    if (brickEntry >= 0)
                    {
                        break;
                    }

                    FailFastAssert(brickEntry != 0);
                    previousBrick += brickEntry;
                }

                obj = previousBrick < minimumBrick
                    ? lastObject
                    : (Object*)(BrickAddress((nuint)previousBrick) + brickEntry - 1);
            }

            FailFastAssert((byte*)obj >= (byte*)lastObject);

            nuint alignment = uoh || (segment->flags & HeapSegmentReadOnly) == 0
                ? LargeObjectAlignment
                : (nuint)sizeof(void*);
            while ((byte*)obj < start)
            {
                if ((byte*)obj < segment->mem || (byte*)obj >= scanEnd)
                {
                    return null;
                }

                if (obj->RawGetMethodTable() is null)
                {
                    byte* nextObject = FindNextObjectAfterGap(
                        (byte*)obj,
                        scanEnd);
                    if (nextObject is null)
                    {
                        return null;
                    }

                    obj = (Object*)nextObject;
                    continue;
                }

                nuint size = GetObjectSize(obj);
                nuint alignedSize = GCEnvironment.AlignUp(size, alignment);
                if (size < MinObjectSize ||
                    alignedSize < size ||
                    alignedSize > (nuint)(segment->allocated - (byte*)obj))
                {
                    return null;
                }

                byte* next = (byte*)obj + (nint)alignedSize;
                if (next > start)
                {
                    break;
                }

                obj = (Object*)next;
            }

            return obj;
        }

        private static void ScanDirtyCardsInSegment(
            heap_segment* segment,
            byte* begin,
            byte* end,
            bool uoh,
            bool ephemeralSegment)
        {
            if (segment is null ||
                segment->mem is null ||
                segment->allocated is null ||
                begin < segment->mem)
            {
                FailFast();
                return;
            }

            if (begin >= segment->allocated)
            {
                return;
            }

            if (!uoh &&
                s_generation2.allocation_start is not null &&
                s_generation2.allocation_start > begin)
            {
                begin = s_generation2.allocation_start;
            }

            if (end > segment->allocated)
            {
                end = segment->allocated;
            }

            byte* scanEnd = end;
            if (segment->plan_allocated is not null &&
                segment->plan_allocated < scanEnd)
            {
                scanEnd = segment->plan_allocated;
            }

            if (begin >= scanEnd)
            {
                return;
            }

            nuint card = CardOf(begin);
            byte* nextBoundary = s_generation1.allocation_start;
            byte* high = s_sohSegment.reserved;
            Object* lastObject = (Object*)begin;

            while (lastObject is not null && CardAddress(card) < scanEnd)
            {
                if (CardSet(card))
                {
                    byte* cardStart = CardAddress(card);
                    if (cardStart < begin)
                    {
                        cardStart = begin;
                    }

                    byte* cardLimit = CardAddress(card + 1);
                    if (cardLimit > scanEnd)
                    {
                        cardLimit = scanEnd;
                    }

                    Object* firstObject = FindFirstObjectForCard(
                        cardStart,
                        lastObject,
                        segment,
                        uoh,
                        scanEnd);
                    if (firstObject is null)
                    {
                        FailFast();
                        return;
                    }

                    nuint cgPointersFound = 0;
                    Object* obj = firstObject;
                    while (obj is not null &&
                        (byte*)obj < scanEnd &&
                        (byte*)obj < cardStart)
                    {
                        if ((byte*)obj < segment->mem ||
                            (byte*)obj >= scanEnd)
                        {
                            FailFast();
                            return;
                        }

                        nuint size = GetObjectSize(obj);
                        nuint alignment = (segment->flags & HeapSegmentReadOnly) != 0
                            ? (nuint)sizeof(void*)
                            : LargeObjectAlignment;
                        nuint alignedSize = GCEnvironment.AlignUp(size, alignment);
                        if (size < MinObjectSize ||
                            alignedSize < size ||
                            alignedSize > (nuint)(end - (byte*)obj))
                        {
                            FailFast();
                            return;
                        }

                        byte* nextObject = (byte*)obj + (nint)alignedSize;
                        if (nextObject > cardStart || nextObject >= scanEnd)
                        {
                            break;
                        }

                        obj = nextObject < scanEnd
                            ? (Object*)nextObject
                            : null;
                    }

                    while (obj is not null &&
                        (byte*)obj < cardLimit &&
                        (byte*)obj < scanEnd)
                    {
                        if ((byte*)obj < segment->mem ||
                            (byte*)obj >= scanEnd)
                        {
                            FailFast();
                            return;
                        }

                        if (obj->RawGetMethodTable() is null)
                        {
                            byte* nextGapObject = FindNextObjectAfterGap(
                                (byte*)obj,
                                scanEnd);
                            if (nextGapObject is null)
                            {
                                obj = null;
                                lastObject = null;
                                break;
                            }

                            obj = (Object*)nextGapObject;
                            lastObject = obj;
                            continue;
                        }

                        nuint size = GetObjectSize(obj);
                        if (size < MinObjectSize ||
                            size > (nuint)(segment->allocated - (byte*)obj) ||
                            size > (nuint)(scanEnd - (byte*)obj))
                        {
                            FailFast();
                            return;
                        }

                        nuint alignment = (segment->flags & HeapSegmentReadOnly) != 0
                            ? (nuint)sizeof(void*)
                            : LargeObjectAlignment;
                        nuint alignedSize = GCEnvironment.AlignUp(size, alignment);
                        if (alignedSize < size ||
                            alignedSize > (nuint)(scanEnd - (byte*)obj))
                        {
                            FailFast();
                            return;
                        }

                        byte* currentNextBoundary = nextBoundary;
                        if (!uoh && ephemeralSegment)
                        {
                            if ((byte*)obj >= s_generation0.allocation_start)
                            {
                                currentNextBoundary = high;
                            }
                            else if ((byte*)obj >= s_generation1.allocation_start)
                            {
                                currentNextBoundary = s_generation0.allocation_start;
                            }
                        }

                        ScanCardObject(
                            obj,
                            cardStart,
                            cardLimit,
                            currentNextBoundary,
                            high,
                            ref cgPointersFound);

                        byte* nextObject = (byte*)obj + (nint)alignedSize;
                        if (nextObject >= segment->allocated ||
                            (cardLimit == scanEnd && nextObject >= scanEnd))
                        {
                            obj = null;
                            lastObject = null;
                        }
                        else if (nextObject > cardLimit)
                        {
                            lastObject = obj;
                            obj = null;
                        }
                        else
                        {
                            byte* nextObjectAfterGap = nextObject;
                            if (((Object*)nextObject)->RawGetMethodTable() is null)
                            {
                                nextObjectAfterGap = FindNextObjectAfterGap(
                                    nextObject,
                                    scanEnd);
                            }

                            if (nextObjectAfterGap is null)
                            {
                                obj = null;
                                lastObject = null;
                            }
                            else
                            {
                                obj = (Object*)nextObjectAfterGap;
                                lastObject = obj;
                            }
                        }
                    }

                    bool cardContainsUnscannedBoundary =
                        !uoh &&
                        cardLimit == scanEnd &&
                        CardAddress(card + 1) > scanEnd;
                    if (cgPointersFound == 0 && !cardContainsUnscannedBoundary)
                    {
                        ClearCards(card, card + 1);
                    }
                }

                card++;
            }
        }

        private static void ScanCardObject(
            Object* obj,
            byte* scanStart,
            byte* scanLimit,
            byte* nextBoundary,
            byte* high,
            ref nuint cgPointersFound)
        {
            MethodTable* methodTable = obj->GetGCSafeMethodTable();
            if (methodTable is null)
            {
                return;
            }

            byte* objectAddress = (byte*)obj;
            nuint objectSize = GetObjectSize(obj);
            if (objectAddress >= scanStart &&
                CardOf(objectAddress) == CardOf(scanStart) &&
                methodTable->Collectible())
            {
                IGCToCLR* callback = GCCommon.g_theGCToCLR;
                if (callback is not null &&
                    callback->Vtable is not null &&
                    callback->Vtable->GetLoaderAllocatorObjectForGC is not null)
                {
                    Object* classObject =
                        (Object*)callback->Vtable->GetLoaderAllocatorObjectForGC(callback, obj);
                    MarkCardReference(
                        &classObject,
                        nextBoundary,
                        high,
                        ref cgPointersFound);
                }
            }

            if (!methodTable->ContainsGCPointers())
            {
                return;
            }

            nint seriesCount = GetSeriesCount(methodTable);
            CGCDescSeries* current = GetHighestSeries(methodTable);
            if (seriesCount >= 0)
            {
                CGCDescSeries* lowest = GetLowestSeries(methodTable, seriesCount);
                do
                {
                    byte* slot = objectAddress + (nint)current->startoffset;
                    byte* stop = slot + (nint)current->seriessize + objectSize;
                    while (slot < stop)
                    {
                        if (slot >= scanStart && slot < scanLimit)
                        {
                            MarkCardReference(
                                (Object**)slot,
                                nextBoundary,
                                high,
                                ref cgPointersFound);
                        }

                        slot += sizeof(void*);
                    }

                    current--;
                }
                while (current >= lowest);

                return;
            }

            byte* repeatingSlot = objectAddress + (nint)current->startoffset;
            byte* objectEnd = objectAddress + objectSize - sizeof(ObjHeader);
            val_serie_item* valueSeries = &current->val_serie;
            while (repeatingSlot < objectEnd)
            {
                for (nint i = 0; i > seriesCount; i--)
                {
                    val_serie_item item = valueSeries[i];
                    byte* stop = repeatingSlot + ((nuint)item.nptrs * (nuint)sizeof(void*));
                    while (repeatingSlot < stop)
                    {
                        if (repeatingSlot >= scanStart && repeatingSlot < scanLimit)
                        {
                            MarkCardReference(
                                (Object**)repeatingSlot,
                                nextBoundary,
                                high,
                                ref cgPointersFound);
                        }

                        repeatingSlot += sizeof(void*);
                    }

                    repeatingSlot += item.skip;
                }
            }
        }

        private static void MarkCardReference(
            Object** objectReference,
            byte* nextBoundary,
            byte* high,
            ref nuint cgPointersFound)
        {
            Object* child = *objectReference;
            if (child is null)
            {
                return;
            }

            byte* childAddress = (byte*)child;
            if (IsInMarkRange(childAddress))
            {
                MarkObject(child);
            }

            if (nextBoundary is not null &&
                childAddress >= nextBoundary &&
                childAddress < high)
            {
                cgPointersFound++;
            }
        }

        private static nuint CardOf(byte* address)
        {
            return (nuint)address / CardSize;
        }

        private static byte* CardAddress(nuint card)
        {
            return (byte*)(card * CardSize);
        }

        private static bool CardSet(nuint card)
        {
            return s_cardTable is not null &&
                (s_cardTable[card / CardWordWidth] &
                    (1u << (int)(card % CardWordWidth))) != 0;
        }

        private static void ClearCards(nuint startCard, nuint endCard)
        {
            if (s_cardTable is null)
            {
                return;
            }

            while (startCard < endCard)
            {
                s_cardTable[startCard / CardWordWidth] &=
                    ~(1u << (int)(startCard % CardWordWidth));
                startCard++;
            }
        }

        private static nint GetSeriesCount(MethodTable* methodTable)
        {
            return *(nint*)((byte*)methodTable - sizeof(nuint));
        }

        private static CGCDescSeries* GetHighestSeries(MethodTable* methodTable)
        {
            return ((CGCDescSeries*)((nuint*)methodTable - 1)) - 1;
        }

        private static CGCDescSeries* GetLowestSeries(MethodTable* methodTable, nint seriesCount)
        {
            if (seriesCount < 0)
            {
                return GetHighestSeries(methodTable);
            }

            nuint count = (nuint)seriesCount;
            if (count > (nuint)((nuint)methodTable / (nuint)sizeof(CGCDescSeries)))
            {
                FailFast();
                return GetHighestSeries(methodTable);
            }

            return (CGCDescSeries*)((byte*)methodTable - sizeof(nuint) - count * (nuint)sizeof(CGCDescSeries));
        }

        private static bool IsInMarkRange(byte* address)
        {
            return address is not null &&
                s_markLow is not null &&
                s_markHigh is not null &&
                address >= s_markLow &&
                address < s_markHigh;
        }

        private static bool IsInFindObjectRange(byte* address)
        {
            if (address is null)
            {
                return false;
            }

            if (GCCommon.g_gc_lowest_address is not null && GCCommon.g_gc_highest_address is not null)
            {
                return address >= GCCommon.g_gc_lowest_address &&
                    address < GCCommon.g_gc_highest_address;
            }

            fixed (heap_segment* soh = &s_sohSegment)
            fixed (heap_segment* loh = &s_lohSegment)
            fixed (heap_segment* poh = &s_pohSegment)
            {
                if (IsInSegment(address, soh) || IsInSegment(address, loh) || IsInSegment(address, poh))
                {
                    return true;
                }

                for (heap_segment* segment = s_generation2.start_segment;
                    segment is not null && (segment->flags & HeapSegmentReadOnly) != 0;
                    segment = segment->next)
                {
                    if (IsInSegment(address, segment))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private static Object* FindObject(byte* interior)
        {
            // The brick table is not maintained by the current NativeAOT plan/sweep pass.
            // Keep the native brick_entry == 0 linear fallback until that metadata exists.
            fixed (heap_segment* soh = &s_sohSegment)
            fixed (heap_segment* loh = &s_lohSegment)
            fixed (heap_segment* poh = &s_pohSegment)
            {
                Object* result = FindObjectInSegment(interior, soh);
                if (result is not null)
                {
                    return result;
                }

                result = FindObjectInSegment(interior, loh);
                if (result is not null)
                {
                    return result;
                }

                result = FindObjectInSegment(interior, poh);
                if (result is not null)
                {
                    return result;
                }

                for (heap_segment* segment = s_generation2.start_segment;
                    segment is not null && (segment->flags & HeapSegmentReadOnly) != 0;
                    segment = segment->next)
                {
                    result = FindObjectInSegment(interior, segment);
                    if (result is not null)
                    {
                        return result;
                    }
                }

                return null;
            }
        }

        private static Object* FindObjectInSegment(
            byte* interior,
            heap_segment* segment,
            byte* scanEnd = null)
        {
            if (!IsInSegment(interior, segment) || interior >= segment->allocated)
            {
                return null;
            }

            bool smallObject = (segment->flags & HeapSegmentReadOnly) != 0;
            nuint alignment = smallObject ? (nuint)sizeof(void*) : LargeObjectAlignment;
            byte* current = segment->mem;
            byte* objectEnd = segment->allocated;
            if (scanEnd > current && scanEnd < objectEnd)
            {
                objectEnd = scanEnd;
            }

            if (scanEnd is not null &&
                segment->plan_allocated is not null &&
                segment->plan_allocated < objectEnd)
            {
                objectEnd = segment->plan_allocated;
            }

            if (scanEnd is not null && interior >= objectEnd)
            {
                return null;
            }

            while (current < objectEnd)
            {
                Object* obj = (Object*)current;
                nuint size = GetObjectSize(obj);
                if (size < MinObjectSize || size > (nuint)(segment->allocated - current))
                {
                    return null;
                }

                nuint alignedSize = GCEnvironment.AlignUp(size, alignment);
                if (alignedSize < size || alignedSize > (nuint)(segment->allocated - current))
                {
                    return null;
                }

                if (interior < current + alignedSize)
                {
                    return obj;
                }

                current += alignedSize;
            }

            return null;
        }

        private static void ScanFinalizationQueue()
        {
            if (s_finalizeQueue is null)
            {
                return;
            }

            Object** start = GetQueueStart(s_finalizeQueue, FinalizerStartSegment);
            Object** stop = GetQueueLimit(s_finalizeQueue, FinalizerMaxSegment);
            for (Object** current = start; current < stop; current++)
            {
                PromoteCore(current);
            }
        }

        private static void ScanForFinalization(int condemnedGeneration, ScanContext* scanContext)
        {
            if (s_finalizeQueue is null)
            {
                return;
            }

            s_finalizeQueue->m_PromotedCount = 0;
            int startSegment = (int)gc_generation_num.total_generation_count - condemnedGeneration - 1;
            for (int segment = startSegment; segment <= (int)gc_generation_num.total_generation_count - 1; segment++)
            {
                Object** start = GetQueueStart(s_finalizeQueue, segment);
                Object** stop = GetQueueLimit(s_finalizeQueue, segment);
                for (Object** current = stop; current > start;)
                {
                    current--;
                    Object* obj = *current;
                    if (IsPromotedObject(obj))
                    {
                        continue;
                    }

                    MethodTable* rawMethodTable = obj is null ? null : obj->RawGetMethodTable();
                    if (rawMethodTable is null || rawMethodTable == GCCommon.g_gc_pFreeObjectMethodTable)
                    {
                        MoveFinalizationItem(current, segment, FinalizerFreeListSegment);
                        continue;
                    }

                    IGCToCLR* callback = GCCommon.g_theGCToCLR;
                    bool eagerFinalized = callback is not null &&
                        callback->Vtable is not null &&
                        callback->Vtable->EagerFinalized is not null &&
                        callback->Vtable->EagerFinalized(callback, obj);

                    if (eagerFinalized ||
                        (obj->GetHeader()->GetBits() & ObjHeader.BIT_SBLK_FINALIZER_RUN) != 0)
                    {
                        MoveFinalizationItem(current, segment, FinalizerFreeListSegment);
                        if (!eagerFinalized)
                        {
                            obj->GetHeader()->ClrBit(ObjHeader.BIT_SBLK_FINALIZER_RUN);
                        }
                    }
                    else
                    {
                        MethodTable* methodTable = obj->GetGCSafeMethodTable();
                        int destination = methodTable->HasCriticalFinalizer()
                            ? FinalizerCriticalListSegment
                            : FinalizerListSegment;
                        s_finalizeQueue->m_PromotedCount++;
                        MoveFinalizationItem(current, segment, destination);
                    }
                }
            }

            ScanFinalizationQueue();
        }

        private static void ScanDependentHandles(ScanContext* scanContext)
        {
            bool unscannedPromotions = true;
            while (unscannedPromotions)
            {
                unscannedPromotions = GCHandleTables.ScanDependentHandlesForPromotion(&Promote, scanContext);
                ProcessMarkOverflow();
                DrainMarkQueue();
            }
        }

        private static void MoveFinalizationItem(Object** source, int sourceSegment, int destinationSegment)
        {
            int step = sourceSegment > destinationSegment ? -1 : 1;
            Object** sourceIndex = source;
            Object*** fillPointers = &s_finalizeQueue->m_FillPointers.Item0;

            for (int segment = sourceSegment; segment != destinationSegment; segment += step)
            {
                Object*** destinationFill = &fillPointers[segment + (step - 1) / 2];
                Object** destinationIndex = *destinationFill - ((step + 1) / 2);
                if (sourceIndex != destinationIndex)
                {
                    Object* temporary = *sourceIndex;
                    *sourceIndex = *destinationIndex;
                    *destinationIndex = temporary;
                }

                *destinationFill -= step;
                sourceIndex = destinationIndex;
            }
        }

        private static nuint GetObjectSize(Object* obj)
        {
            MethodTable* methodTable = obj->GetGCSafeMethodTable();
            nuint size = methodTable->GetBaseSize();
            if (methodTable->HasComponentSize())
            {
                nuint componentSize = methodTable->RawGetComponentSize();
                nuint componentCount = methodTable->GetNumComponents(obj);
                if (componentSize != 0 && componentCount > (nuint.MaxValue - size) / componentSize)
                {
                    FailFast();
                    return 0;
                }

                size += componentSize * componentCount;
            }

            return size;
        }

        private static bool IsInSegment(byte* address, heap_segment* segment)
        {
            return segment is not null &&
                segment->mem is not null &&
                segment->reserved is not null &&
                address >= segment->mem &&
                address < segment->reserved;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FailFast()
        {
            System.Runtime.InternalCalls.RhpFallbackFailFast();
        }
    }
}
