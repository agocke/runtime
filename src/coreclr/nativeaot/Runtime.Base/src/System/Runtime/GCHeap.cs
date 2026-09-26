// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace System.Runtime
{
    internal static partial class RuntimeExports
    {
        private const uint MaxYpSpinCountUnit = 32768;
        private const int GcKindAny = 0;
        private const int GcKindEphemeral = 1;
        private const int GcKindFullBlocking = 2;
        private const int GcKindBackground = 3;
        private const int MaxGeneration = 2;
        private const int TotalGenerationCount = 5;
        private const int PauseLowLatency = 2;
        private const int PauseSustainedLowLatency = 3;
        private const int PauseNoGc = 4;
        private const int SetPauseModeSuccess = 0;
        private const int SetPauseModeNoGc = 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe nuint ReadVolatile(nuint* location)
        {
            return (nuint)Volatile.Read(ref *(nint*)location);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe int ReadVolatile(int* location)
        {
            return Volatile.Read(ref *location);
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RecordedGenerationInfo
        {
            internal nuint SizeBefore;
            internal nuint FragmentationBefore;
            internal nuint SizeAfter;
            internal nuint FragmentationAfter;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct LastRecordedGcInfo
        {
            internal nuint Index;
            internal nuint TotalCommitted;
            internal nuint Promoted;
            internal nuint PinnedObjects;
            internal nuint FinalizePromotedObjects;
            internal nuint PauseDuration0;
            internal nuint PauseDuration1;
            internal float PausePercentage;
            internal RecordedGenerationInfo GenInfo0;
            internal RecordedGenerationInfo GenInfo1;
            internal RecordedGenerationInfo GenInfo2;
            internal RecordedGenerationInfo GenInfo3;
            internal RecordedGenerationInfo GenInfo4;
            internal nuint HeapSize;
            internal nuint Fragmentation;
            internal uint MemoryLoad;
            internal byte CondemnedGeneration;
            internal byte Compaction;
            internal byte Concurrent;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct HeapSegmentPrefix
        {
            internal byte* Allocated;
            internal byte* Committed;
            internal byte* Reserved;
            internal byte* Used;
            internal byte* Mem;
            internal nuint Flags;
            internal HeapSegmentPrefix* Next;
        }

        private const nuint HeapSegmentFlagsReadOnly = 1;
        private const nuint HeapSegmentFlagsInRange = 2;
        private const nuint HeapSegmentFlagsLoh = 8;
        private const nuint HeapSegmentFlagsPoh = 512;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe HeapSegmentPrefix* HeapSegmentReadWrite(HeapSegmentPrefix* segment)
        {
            if (segment is null || (segment->Flags & HeapSegmentFlagsReadOnly) == 0)
            {
                return segment;
            }

            do
            {
                segment = segment->Next;
            }
            while (segment is not null && (segment->Flags & HeapSegmentFlagsReadOnly) != 0);

            return segment;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe HeapSegmentPrefix* HeapSegmentNextReadWrite(HeapSegmentPrefix* segment)
        {
            return HeapSegmentReadWrite(segment->Next);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe HeapSegmentPrefix* HeapSegmentInRange(HeapSegmentPrefix* segment)
        {
            if (segment is null || (segment->Flags & HeapSegmentFlagsReadOnly) == 0 ||
                (segment->Flags & HeapSegmentFlagsInRange) != 0)
            {
                return segment;
            }

            do
            {
                segment = segment->Next;
            }
            while (segment is not null &&
                (segment->Flags & HeapSegmentFlagsReadOnly) != 0 &&
                (segment->Flags & HeapSegmentFlagsInRange) == 0);

            return segment;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe HeapSegmentPrefix* HeapSegmentNextInRange(HeapSegmentPrefix* segment)
        {
            return HeapSegmentInRange(segment->Next);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe bool InRangeForSegment(byte* address, HeapSegmentPrefix* segment)
        {
            return (address >= segment->Mem) && (address < segment->Reserved);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe byte* GenerationAddress(byte* generationData, nuint generationSize, int generationNumber)
        {
            return generationData + (nint)((nuint)generationNumber * generationSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe nuint GenerationSize(
            int generationNumber,
            byte* generationData,
            nuint generationSize,
            HeapSegmentPrefix** ephemeralHeapSegmentLocation,
            nuint alignedMinObjectSize,
            uint useRegions,
            nuint generationStartSegmentOffset,
            nuint generationAllocationStartOffset)
        {
            byte* generation = GenerationAddress(generationData, generationSize, generationNumber);
            if (useRegions != 0)
            {
                nuint result = 0;
                HeapSegmentPrefix* segment = HeapSegmentReadWrite(*(HeapSegmentPrefix**)(generation + (nint)generationStartSegmentOffset));
                while (segment is not null)
                {
                    result += (nuint)(segment->Allocated - segment->Mem);
                    segment = segment->Next;
                }

                return result;
            }

            if (generationNumber == 0)
            {
                HeapSegmentPrefix* ephemeralHeapSegment = *ephemeralHeapSegmentLocation;
                nint generationBytes = (nint)(ephemeralHeapSegment->Allocated - *(byte**)(generation + (nint)generationAllocationStartOffset));
                return generationBytes > (nint)alignedMinObjectSize ? (nuint)generationBytes : alignedMinObjectSize;
            }

            HeapSegmentPrefix* ephemeralHeapSegmentForGeneration = *ephemeralHeapSegmentLocation;
            if (HeapSegmentReadWrite(*(HeapSegmentPrefix**)(generation + (nint)generationStartSegmentOffset)) == ephemeralHeapSegmentForGeneration)
            {
                byte* previousGeneration = GenerationAddress(generationData, generationSize, generationNumber - 1);
                return (nuint)(*(byte**)(previousGeneration + (nint)generationAllocationStartOffset) -
                    *(byte**)(generation + (nint)generationAllocationStartOffset));
            }

            nuint resultForGeneration = 0;
            HeapSegmentPrefix* segmentForGeneration = HeapSegmentReadWrite(*(HeapSegmentPrefix**)(generation + (nint)generationStartSegmentOffset));
            Debug.Assert(segmentForGeneration is not null);
            while (segmentForGeneration is not null && segmentForGeneration != ephemeralHeapSegmentForGeneration)
            {
                resultForGeneration += (nuint)(segmentForGeneration->Allocated - segmentForGeneration->Mem);
                segmentForGeneration = HeapSegmentNextReadWrite(segmentForGeneration);
            }

            if (segmentForGeneration is not null)
            {
                byte* previousGeneration = GenerationAddress(generationData, generationSize, generationNumber - 1);
                resultForGeneration += (nuint)(*(byte**)(previousGeneration + (nint)generationAllocationStartOffset) -
                    ephemeralHeapSegmentForGeneration->Mem);
            }

            return resultForGeneration;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe byte* SegmentField(HeapSegmentPrefix* segment, nuint offset)
        {
            return *(byte**)((byte*)segment + (nint)offset);
        }

        [RuntimeExport("RhpGCHeapGetCurrentGenerationSize")]
        internal static unsafe nuint RhpGCHeapGetCurrentGenerationSize(
            byte* dynamicData,
            nuint currentSizeOffset,
            nuint desiredAllocationOffset,
            nuint newAllocationOffset)
        {
            unchecked
            {
                return *(nuint*)(dynamicData + (nint)currentSizeOffset) +
                    *(nuint*)(dynamicData + (nint)desiredAllocationOffset) -
                    (nuint)(*(nint*)(dynamicData + (nint)newAllocationOffset));
            }
        }

        [RuntimeExport("RhpGCHeapGetGenerationSize")]
        internal static unsafe nuint RhpGCHeapGetGenerationSize(
            byte* generationData,
            nuint generationSize,
            int generationNumber,
            HeapSegmentPrefix* ephemeralHeapSegment,
            byte* generationAllocationStart,
            byte* generationPlanAllocationStart,
            uint useRegions,
            uint usePlan,
            nuint generationStartSegmentOffset,
            nuint generationAllocationStartOffset,
            nuint generationPlanAllocationStartOffset,
            nuint alignedMinObjectSize,
            nuint segmentEndOffset)
        {
            unchecked
            {
                byte* generation = GenerationAddress(generationData, generationSize, generationNumber);
                if (useRegions != 0)
                {
                    nuint result = 0;
                    HeapSegmentPrefix* segment = HeapSegmentReadWrite(
                        *(HeapSegmentPrefix**)(generation + (nint)generationStartSegmentOffset));
                    while (segment is not null)
                    {
                        byte* end = SegmentField(segment, segmentEndOffset);
                        result += (nuint)(end - segment->Mem);
                        segment = segment->Next;
                    }

                    return result;
                }

                byte* allocationStart = usePlan != 0 ? generationPlanAllocationStart : generationAllocationStart;
                if (generationNumber == 0)
                {
                    nint generationBytes = (nint)(SegmentField(ephemeralHeapSegment, segmentEndOffset) - allocationStart);
                    return generationBytes > (nint)alignedMinObjectSize ? (nuint)generationBytes : alignedMinObjectSize;
                }

                if (HeapSegmentReadWrite(*(HeapSegmentPrefix**)(generation + (nint)generationStartSegmentOffset)) ==
                    ephemeralHeapSegment)
                {
                    byte* previousGeneration = GenerationAddress(generationData, generationSize, generationNumber - 1);
                    byte* previousAllocationStart = *(byte**)(previousGeneration +
                        (nint)(usePlan != 0 ? generationPlanAllocationStartOffset : generationAllocationStartOffset));

                    return (nuint)(previousAllocationStart - allocationStart);
                }

                nuint resultForGeneration = 0;
                HeapSegmentPrefix* segmentForGeneration = HeapSegmentReadWrite(
                    *(HeapSegmentPrefix**)(generation + (nint)generationStartSegmentOffset));
                Debug.Assert(segmentForGeneration is not null);
                while (segmentForGeneration is not null && segmentForGeneration != ephemeralHeapSegment)
                {
                    resultForGeneration += (nuint)(
                        SegmentField(segmentForGeneration, segmentEndOffset) - segmentForGeneration->Mem);
                    segmentForGeneration = HeapSegmentNextReadWrite(segmentForGeneration);
                }

                if (segmentForGeneration is not null)
                {
                    byte* previousGeneration = GenerationAddress(generationData, generationSize, generationNumber - 1);
                    byte* previousAllocationStart = *(byte**)(previousGeneration +
                        (nint)(usePlan != 0 ?
                            generationPlanAllocationStartOffset :
                            generationAllocationStartOffset));
                    resultForGeneration += (nuint)(previousAllocationStart - ephemeralHeapSegment->Mem);
                }

                return resultForGeneration;
            }
        }

        [RuntimeExport("RhpGCHeapComputeIn")]
        internal static unsafe nuint RhpGCHeapComputeIn(
            byte* generationData,
            nuint generationSize,
            byte* dynamicData,
            nuint dynamicDataSize,
            byte* generationHistoryData,
            int generationNumber,
            int maxGeneration,
            uint useRegions,
            uint ephemeralPromotion,
            nuint generationAllocationSizeOffset,
            nuint generationCondemnedAllocatedOffset,
            nuint gcNewAllocationOffset,
            nuint newAllocationOffset,
            nuint survivedSizeOffset,
            nuint historyInOffset)
        {
            byte* generation = GenerationAddress(generationData, generationSize, generationNumber);
            byte* dynamicDataForGeneration = GenerationAddress(dynamicData, dynamicDataSize, generationNumber);
            nuint inValue = *(nuint*)(generation + (nint)generationAllocationSizeOffset);

            unchecked
            {
                if (useRegions == 0 && ephemeralPromotion != 0 && generationNumber == maxGeneration)
                {
                    inValue = 0;
                    for (int i = 0; i <= maxGeneration; i++)
                    {
                        byte* dynamicDataForSourceGeneration = GenerationAddress(dynamicData, dynamicDataSize, i);
                        nuint survivedSize = *(nuint*)(dynamicDataForSourceGeneration + (nint)survivedSizeOffset);
                        inValue += survivedSize;
                        if (i != maxGeneration)
                        {
                            *(nuint*)(generation + (nint)generationCondemnedAllocatedOffset) += survivedSize;
                        }
                    }
                }

                *(nint*)(dynamicDataForGeneration + (nint)gcNewAllocationOffset) =
                    (nint)((nuint)(*(nint*)(dynamicDataForGeneration + (nint)gcNewAllocationOffset)) - inValue);
                *(nint*)(dynamicDataForGeneration + (nint)newAllocationOffset) =
                    *(nint*)(dynamicDataForGeneration + (nint)gcNewAllocationOffset);
                *(nuint*)(generationHistoryData + (nint)historyInOffset) = inValue;
                *(nuint*)(generation + (nint)generationAllocationSizeOffset) = 0;
            }

            return inValue;
        }

        [RuntimeExport("RhpGCHeapGetGenerationFragmentation")]
        internal static unsafe nuint RhpGCHeapGetGenerationFragmentation(
            byte* generationData,
            nuint generationSize,
            int generationNumber,
            HeapSegmentPrefix* generationStartSegment,
            byte* consingGenerationAllocationPointer,
            byte* end,
            HeapSegmentPrefix* ephemeralHeapSegment,
            byte* markStackArray,
            nuint markStackBos,
            uint useRegions,
            nuint generationStartSegmentOffset,
            nuint savedAllocatedOffset,
            nuint planAllocatedOffset,
            nuint allocatedOffset,
            nuint markSize,
            nuint markLengthOffset)
        {
            nuint fragmentation = 0;
            unchecked
            {
                if (useRegions != 0)
                {
                    for (int genNum = 0; genNum <= generationNumber; genNum++)
                    {
                        byte* generation = GenerationAddress(generationData, generationSize, genNum);
                        HeapSegmentPrefix* segment = HeapSegmentReadWrite(
                            *(HeapSegmentPrefix**)(generation + (nint)generationStartSegmentOffset));
                        while (segment is not null)
                        {
                            fragmentation += (nuint)(SegmentField(segment, savedAllocatedOffset) -
                                SegmentField(segment, planAllocatedOffset));
                            segment = HeapSegmentNextReadWrite(segment);
                        }
                    }
                }
                else
                {
                    if (InRangeForSegment(consingGenerationAllocationPointer, ephemeralHeapSegment))
                    {
                        fragmentation = consingGenerationAllocationPointer <=
                            SegmentField(ephemeralHeapSegment, allocatedOffset) ?
                            (nuint)(end - consingGenerationAllocationPointer) :
                            0;
                    }
                    else
                    {
                        fragmentation = (nuint)(SegmentField(ephemeralHeapSegment, allocatedOffset) -
                            ephemeralHeapSegment->Mem);
                    }

                    HeapSegmentPrefix* segment = HeapSegmentReadWrite(generationStartSegment);
                    Debug.Assert(segment is not null);
                    while (segment != ephemeralHeapSegment)
                    {
                        fragmentation += (nuint)(SegmentField(segment, allocatedOffset) -
                            SegmentField(segment, planAllocatedOffset));
                        segment = HeapSegmentNextReadWrite(segment);
                        Debug.Assert(segment is not null);
                    }
                }

                for (nuint bos = 0; bos < markStackBos; bos++)
                {
                    byte* mark = markStackArray + (nint)(bos * markSize);
                    fragmentation += *(nuint*)(mark + (nint)markLengthOffset);
                }
            }

            return fragmentation;
        }

        [RuntimeExport("RhpGCHeapGetGenerationSizes")]
        internal static unsafe nuint RhpGCHeapGetGenerationSizes(
            byte* generationData,
            nuint generationSize,
            int generationNumber,
            HeapSegmentPrefix* generationStartSegment,
            byte* generationAllocationStart,
            HeapSegmentPrefix* ephemeralHeapSegment,
            uint useRegions,
            uint useSaved,
            int maxGeneration,
            nuint generationStartSegmentOffset,
            nuint allocatedOffset,
            nuint savedAllocatedOffset)
        {
            nuint result = 0;
            unchecked
            {
                if (useRegions != 0)
                {
                    int startGenerationIndex = generationNumber > maxGeneration ? generationNumber : 0;
                    for (int i = startGenerationIndex; i <= generationNumber; i++)
                    {
                        byte* generation = GenerationAddress(generationData, generationSize, i);
                        HeapSegmentPrefix* segment = HeapSegmentInRange(
                            *(HeapSegmentPrefix**)(generation + (nint)generationStartSegmentOffset));
                        while (segment is not null)
                        {
                            byte* end = useSaved != 0 ?
                                SegmentField(segment, savedAllocatedOffset) :
                                SegmentField(segment, allocatedOffset);
                            result += (nuint)(end - segment->Mem);
                            segment = segment->Next;
                        }
                    }
                }
                else if (generationStartSegment == ephemeralHeapSegment)
                {
                    result = (nuint)(ephemeralHeapSegment->Allocated - generationAllocationStart);
                }
                else
                {
                    HeapSegmentPrefix* segment = HeapSegmentInRange(generationStartSegment);
                    Debug.Assert(segment is not null);
                    while (segment is not null)
                    {
                        result += (nuint)(segment->Allocated - segment->Mem);
                        segment = HeapSegmentNextInRange(segment);
                    }
                }

                return result;
            }
        }

        [RuntimeExport("RhpGCHeapGetTotalHeapSize")]
        internal static unsafe nuint RhpGCHeapGetTotalHeapSize(
            byte** heapSource,
            int heapCount,
            byte* generationSource,
            nuint generationTableOffset,
            nuint generationSize,
            nuint generationStartSegmentOffset,
            nuint generationAllocationStartOffset,
            byte* ephemeralHeapSegmentLocationSource,
            nuint ephemeralHeapSegmentOffset,
            nuint alignedMinObjectSize,
            int maxGeneration,
            int totalGenerationCount,
            uint useRegions)
        {
            nuint totalHeapSize = 0;
            unchecked
            {
                for (int heap = 0; heap < heapCount; heap++)
                {
                    byte* heapAddress = heapSource[heap];
                    byte* generationData = generationSource is null ?
                        heapAddress + (nint)generationTableOffset :
                        generationSource;
                    HeapSegmentPrefix** ephemeralHeapSegmentLocation = useRegions != 0 ?
                        null :
                        (HeapSegmentPrefix**)(ephemeralHeapSegmentLocationSource is null ?
                            heapAddress + (nint)ephemeralHeapSegmentOffset :
                            ephemeralHeapSegmentLocationSource);

                    byte* generation = GenerationAddress(generationData, generationSize, maxGeneration);
                    HeapSegmentPrefix* generationStartSegment = useRegions != 0 ?
                        null :
                        *(HeapSegmentPrefix**)(generation + (nint)generationStartSegmentOffset);
                    byte* generationAllocationStart = useRegions != 0 ?
                        null :
                        *(byte**)(generation + (nint)generationAllocationStartOffset);

                    for (int generationNumber = maxGeneration; generationNumber < totalGenerationCount; generationNumber++)
                    {
                        totalHeapSize += RhpGCHeapGetGenerationSizes(
                            generationData,
                            generationSize,
                            generationNumber,
                            generationStartSegment,
                            generationAllocationStart,
                            ephemeralHeapSegmentLocation is null ? null : *ephemeralHeapSegmentLocation,
                            useRegions,
                            0,
                            maxGeneration,
                            generationStartSegmentOffset,
                            0,
                            0);

                        if (generationNumber + 1 < totalGenerationCount && useRegions == 0)
                        {
                            generation = GenerationAddress(generationData, generationSize, generationNumber + 1);
                            generationStartSegment = *(HeapSegmentPrefix**)(generation + (nint)generationStartSegmentOffset);
                            generationAllocationStart = *(byte**)(generation + (nint)generationAllocationStartOffset);
                        }
                    }
                }
            }

            return totalHeapSize;
        }

        [RuntimeExport("RhpGCHeapGetTotalFragmentation")]
        internal static unsafe nuint RhpGCHeapGetTotalFragmentation(
            byte** heapSource,
            int heapCount,
            byte* generationSource,
            nuint generationTableOffset,
            nuint generationSize,
            nuint freeListSpaceOffset,
            nuint freeObjSpaceOffset,
            int totalGenerationCount)
        {
            nuint totalFragmentation = 0;
            unchecked
            {
                for (int heap = 0; heap < heapCount; heap++)
                {
                    byte* generationData = generationSource is null ?
                        heapSource[heap] + (nint)generationTableOffset :
                        generationSource;
                    for (int generationNumber = 0; generationNumber < totalGenerationCount; generationNumber++)
                    {
                        byte* generation = GenerationAddress(generationData, generationSize, generationNumber);
                        totalFragmentation +=
                            *(nuint*)(generation + (nint)freeListSpaceOffset) +
                            *(nuint*)(generation + (nint)freeObjSpaceOffset);
                    }
                }
            }

            return totalFragmentation;
        }

        [RuntimeExport("RhpGCHeapGetTotalGenerationFragmentation")]
        internal static unsafe nuint RhpGCHeapGetTotalGenerationFragmentation(
            byte** heapSource,
            int heapCount,
            byte* generationSource,
            nuint generationTableOffset,
            nuint generationSize,
            nuint freeListSpaceOffset,
            nuint freeObjSpaceOffset,
            int generationNumber)
        {
            nuint totalFragmentation = 0;
            unchecked
            {
                for (int heap = 0; heap < heapCount; heap++)
                {
                    byte* generationData = generationSource is null ?
                        heapSource[heap] + (nint)generationTableOffset :
                        generationSource;
                    byte* generation = GenerationAddress(generationData, generationSize, generationNumber);
                    totalFragmentation +=
                        *(nuint*)(generation + (nint)freeListSpaceOffset) +
                        *(nuint*)(generation + (nint)freeObjSpaceOffset);
                }
            }

            return totalFragmentation;
        }

        [RuntimeExport("RhpGCHeapGetTotalGenerationEstimatedReclaim")]
        internal static unsafe nuint RhpGCHeapGetTotalGenerationEstimatedReclaim(
            byte** heapSource,
            int heapCount,
            byte* dynamicDataSource,
            nuint dynamicDataTableOffset,
            nuint dynamicDataSize,
            nuint desiredAllocationOffset,
            nuint newAllocationOffset,
            nuint currentSizeOffset,
            nuint survivedOffset,
            nuint fragmentationOffset,
            int generationNumber)
        {
            nuint totalEstimatedReclaim = 0;
            unchecked
            {
                for (int heap = 0; heap < heapCount; heap++)
                {
                    byte* dynamicData = dynamicDataSource is null ?
                        heapSource[heap] + (nint)dynamicDataTableOffset :
                        dynamicDataSource;
                    byte* dynamicDataForGeneration = GenerationAddress(dynamicData, dynamicDataSize, generationNumber);
                    nuint generationAllocated =
                        *(nuint*)(dynamicDataForGeneration + (nint)desiredAllocationOffset) -
                        (nuint)(*(nint*)(dynamicDataForGeneration + (nint)newAllocationOffset));
                    nuint generationTotalSize = generationAllocated +
                        *(nuint*)(dynamicDataForGeneration + (nint)currentSizeOffset);
                    nuint estimatedSurvived = (nuint)((float)generationTotalSize *
                        *(float*)(dynamicDataForGeneration + (nint)survivedOffset));
                    totalEstimatedReclaim += generationTotalSize - estimatedSurvived +
                        *(nuint*)(dynamicDataForGeneration + (nint)fragmentationOffset);
                }
            }

            return totalEstimatedReclaim;
        }

        [RuntimeExport("RhpGCHeapGetTotalGenerationSize")]
        internal static unsafe nuint RhpGCHeapGetTotalGenerationSize(
            byte** heapSource,
            int heapCount,
            byte* generationSource,
            nuint generationTableOffset,
            nuint generationSize,
            nuint generationStartSegmentOffset,
            nuint generationAllocationStartOffset,
            byte* ephemeralHeapSegmentLocationSource,
            nuint ephemeralHeapSegmentOffset,
            nuint alignedMinObjectSize,
            int generationNumber,
            int maxGeneration,
            uint useRegions)
        {
            nuint totalGenerationSize = 0;
            unchecked
            {
                for (int heap = 0; heap < heapCount; heap++)
                {
                    byte* heapAddress = heapSource[heap];
                    byte* generationData = generationSource is null ?
                        heapAddress + (nint)generationTableOffset :
                        generationSource;
                    HeapSegmentPrefix** ephemeralHeapSegmentLocation = useRegions != 0 ?
                        null :
                        (HeapSegmentPrefix**)(ephemeralHeapSegmentLocationSource is null ?
                            heapAddress + (nint)ephemeralHeapSegmentOffset :
                            ephemeralHeapSegmentLocationSource);
                    totalGenerationSize += GenerationSize(
                        generationNumber,
                        generationData,
                        generationSize,
                        ephemeralHeapSegmentLocation,
                        alignedMinObjectSize,
                        useRegions,
                        generationStartSegmentOffset,
                        generationAllocationStartOffset);
                }
            }

            return totalGenerationSize;
        }

        [RuntimeExport("RhpGCHeapGetCommittedSize")]
        internal static unsafe nuint RhpGCHeapGetCommittedSize(
            byte* heapSource,
            byte* generationSource,
            nuint generationTableOffset,
            nuint generationSize,
            nuint generationStartSegmentOffset,
            nuint regionStartOffset,
            int startGenerationIndex,
            int totalGenerationCount,
            uint useRegions,
            byte* freeRegionsSource,
            nuint freeRegionsOffset,
            nuint freeRegionSize,
            nuint freeRegionCommittedSizeOffset,
            int basicFreeRegion,
            int countFreeRegionKinds,
            nuint* generationCommitted,
            nuint* generationAllocated)
        {
            byte* generationData = generationSource is null ?
                heapSource + (nint)generationTableOffset :
                generationSource;
            nuint totalCommitted = 0;
            unchecked
            {
                for (int generationNumber = startGenerationIndex;
                    generationNumber < totalGenerationCount;
                    generationNumber++)
                {
                    byte* generation = GenerationAddress(generationData, generationSize, generationNumber);
                    HeapSegmentPrefix* segment = HeapSegmentReadWrite(
                        *(HeapSegmentPrefix**)(generation + (nint)generationStartSegmentOffset));
                    while (segment is not null)
                    {
                        byte* start = useRegions != 0 ?
                            segment->Mem - (nint)regionStartOffset :
                            (byte*)segment;
                        nuint committed = (nuint)(segment->Committed - start);
                        nuint allocated = (nuint)(segment->Allocated - start);
                        generationCommitted[generationNumber] += committed;
                        generationAllocated[generationNumber] += allocated;
                        totalCommitted += committed;
                        segment = segment->Next;
                    }
                }

                if (useRegions != 0)
                {
                    byte* freeRegions = freeRegionsSource is null ?
                        heapSource + (nint)freeRegionsOffset :
                        freeRegionsSource;
                    for (int kind = basicFreeRegion; kind < countFreeRegionKinds; kind++)
                    {
                        totalCommitted += *(nuint*)(freeRegions +
                            (nint)((nuint)kind * freeRegionSize + freeRegionCommittedSizeOffset));
                    }
                }
            }

            return totalCommitted;
        }

        [RuntimeExport("RhpGCHeapGetEstimatedReclaim")]
        internal static unsafe nuint RhpGCHeapGetEstimatedReclaim(
            byte* dynamicData,
            nuint desiredAllocationOffset,
            nuint newAllocationOffset,
            nuint currentSizeOffset,
            nuint survivedOffset,
            nuint fragmentationOffset)
        {
            unchecked
            {
                nuint generationAllocated = *(nuint*)(dynamicData + (nint)desiredAllocationOffset) -
                    (nuint)(*(nint*)(dynamicData + (nint)newAllocationOffset));
                nuint generationTotalSize = generationAllocated +
                    *(nuint*)(dynamicData + (nint)currentSizeOffset);
                nuint estimatedSurvived = (nuint)((float)generationTotalSize *
                    *(float*)(dynamicData + (nint)survivedOffset));
                return generationTotalSize - estimatedSurvived +
                    *(nuint*)(dynamicData + (nint)fragmentationOffset);
            }
        }

        [RuntimeExport("RhpGCHeapGetApproximateNewAllocation")]
        internal static unsafe nuint RhpGCHeapGetApproximateNewAllocation(
            byte* dynamicData,
            nuint minSizeOffset,
            nuint desiredAllocationOffset)
        {
            unchecked
            {
                nuint minimumAllocation = 2 * *(nuint*)(dynamicData + (nint)minSizeOffset);
                nuint desiredAllocation = (*(nuint*)(dynamicData + (nint)desiredAllocationOffset) * 2) / 3;
                return minimumAllocation > desiredAllocation ? minimumAllocation : desiredAllocation;
            }
        }

        [RuntimeExport("RhpGCHeapGetEndSpaceAfterGC")]
        internal static unsafe nuint RhpGCHeapGetEndSpaceAfterGC(
            byte* dynamicData,
            nuint minSizeOffset,
            nuint endSpaceAfterGcFl)
        {
            nuint minimumEndSpace = *(nuint*)(dynamicData + (nint)minSizeOffset) / 2;
            return minimumEndSpace > endSpaceAfterGcFl ? minimumEndSpace : endSpaceAfterGcFl;
        }

        [RuntimeExport("RhpGCHeapApproxTotalBytesInUse")]
        internal static unsafe nuint RhpGCHeapApproxTotalBytesInUse(
            uint smallHeapOnly,
            byte** currentAllocAllocatedLocation,
            HeapSegmentPrefix** ephemeralHeapSegmentLocation,
            byte* generationData,
            nuint generationSize,
            nuint generationStartSegmentOffset,
            nuint generationAllocationStartOffset,
            nuint generationFreeListSpaceOffset,
            nuint generationFreeObjectSpaceOffset,
            nuint* backgroundSohSizeEndMark,
            int* currentGcState,
            int planningState,
            uint backgroundGcEnabled,
            int maxGeneration,
            int uohStartGeneration,
            int totalGenerationCount,
            uint useRegions,
            nuint alignedMinObjectSize)
        {
            nuint totalSize = 0;

            byte* generationZero = generationData;
            nuint generationZeroFragmentation =
                *(nuint*)(generationZero + (nint)generationFreeListSpaceOffset) +
                *(nuint*)(generationZero + (nint)generationFreeObjectSpaceOffset);
            nuint generationZeroSize = 0;
            byte* currentAllocAllocated = *currentAllocAllocatedLocation;
            if (useRegions != 0)
            {
                HeapSegmentPrefix* generationZeroSegment =
                    *(HeapSegmentPrefix**)(generationZero + (nint)generationStartSegmentOffset);
                while (generationZeroSegment is not null)
                {
                    byte* end = InRangeForSegment(currentAllocAllocated, generationZeroSegment) ?
                        currentAllocAllocated :
                        generationZeroSegment->Allocated;
                    generationZeroSize += (nuint)(end - generationZeroSegment->Mem);

                    generationZeroSegment = generationZeroSegment->Next;
                }
            }
            else
            {
                HeapSegmentPrefix* ephemeralHeapSegment = *ephemeralHeapSegmentLocation;
                generationZeroSize = (nuint)(currentAllocAllocated - ephemeralHeapSegment->Mem);
            }

            totalSize = (generationZeroSize > generationZeroFragmentation) ?
                (generationZeroSize - generationZeroFragmentation) :
                0;

            int stopGenerationIndex = maxGeneration;
            if ((backgroundGcEnabled != 0) && (ReadVolatile(currentGcState) == planningState))
            {
                byte* oldestGeneration = GenerationAddress(generationData, generationSize, maxGeneration);
                totalSize = *backgroundSohSizeEndMark -
                    *(nuint*)(oldestGeneration + (nint)generationFreeListSpaceOffset) -
                    *(nuint*)(oldestGeneration + (nint)generationFreeObjectSpaceOffset);
                stopGenerationIndex--;
            }

            for (int i = maxGeneration - 1; i <= stopGenerationIndex; i++)
            {
                byte* generation = GenerationAddress(generationData, generationSize, i);
                totalSize += GenerationSize(
                    i,
                    generationData,
                    generationSize,
                    ephemeralHeapSegmentLocation,
                    alignedMinObjectSize,
                    useRegions,
                    generationStartSegmentOffset,
                    generationAllocationStartOffset) -
                    *(nuint*)(generation + (nint)generationFreeListSpaceOffset) -
                    *(nuint*)(generation + (nint)generationFreeObjectSpaceOffset);
            }

            if (smallHeapOnly == 0)
            {
                for (int i = uohStartGeneration; i < totalGenerationCount; i++)
                {
                    byte* generation = GenerationAddress(generationData, generationSize, i);
                    totalSize += GenerationSize(
                        i,
                        generationData,
                        generationSize,
                        ephemeralHeapSegmentLocation,
                        alignedMinObjectSize,
                        useRegions,
                        generationStartSegmentOffset,
                        generationAllocationStartOffset) -
                        *(nuint*)(generation + (nint)generationFreeListSpaceOffset) -
                        *(nuint*)(generation + (nint)generationFreeObjectSpaceOffset);
                }
            }

            return totalSize;
        }

        [RuntimeExport("RhpGCHeapGetValidSegmentSize")]
        internal static nuint RhpGCHeapGetValidSegmentSize(uint largeSegment, nuint largeSegmentSize, nuint smallSegmentSize)
        {
            return largeSegment != 0 ? largeSegmentSize : smallSegmentSize;
        }

        [RuntimeExport("RhpGCHeapSetSuspensionPending")]
        internal static unsafe void RhpGCHeapSetSuspensionPending(int* suspensionPendingCount, uint suspensionPending)
        {
            if (suspensionPending != 0)
            {
                Interlocked.Increment(ref *suspensionPendingCount);
            }
            else
            {
                Interlocked.Decrement(ref *suspensionPendingCount);
            }
        }

        [RuntimeExport("RhpGCHeapGetLastGCPercentTimeInGC")]
        internal static int RhpGCHeapGetLastGCPercentTimeInGC(uint percentTimeInGC)
        {
            return (int)percentTimeInGC;
        }

        [RuntimeExport("RhpGCHeapGetLastGCGenerationSize")]
        internal static unsafe nuint RhpGCHeapGetLastGCGenerationSize(nuint* generationSizes, int generation)
        {
            return generationSizes[generation];
        }

        [RuntimeExport("RhpGCHeapGetCurrentObjSize")]
        internal static nuint RhpGCHeapGetCurrentObjSize(nuint totalSurvivedSize, nuint totalAllocatedSize)
        {
            return totalSurvivedSize + totalAllocatedSize;
        }

        [RuntimeExport("RhpGCHeapGetLastGCStartTime")]
        internal static nuint RhpGCHeapGetLastGCStartTime(ulong timeClock)
        {
            return (nuint)(timeClock / 1000);
        }

        [RuntimeExport("RhpGCHeapGetLastGCDuration")]
        internal static nuint RhpGCHeapGetLastGCDuration(nuint gcElapsedTime)
        {
            return (nuint)(gcElapsedTime / 1000);
        }

        [RuntimeExport("RhpGCHeapGetNow")]
        internal static nuint RhpGCHeapGetNow(ulong highPrecisionTimeStamp)
        {
            return (nuint)(highPrecisionTimeStamp / 1000);
        }

        [RuntimeExport("RhpGCHeapGetGenerationBudget")]
        internal static unsafe ulong RhpGCHeapGetGenerationBudget(
            byte** heapSource,
            int heapCount,
            nuint dynamicDataTableOffset,
            nuint dynamicDataSize,
            nuint desiredAllocationOffset,
            int generation)
        {
            ulong budget = 0;
            for (int i = 0; i < heapCount; i++)
            {
                byte* dynamicData = (byte*)heapSource[i] + (nint)dynamicDataTableOffset;
                dynamicData += (nint)((nuint)generation * dynamicDataSize);
                budget += (ulong)(*(nuint*)(dynamicData + (nint)desiredAllocationOffset));
            }

            return budget;
        }

        [RuntimeExport("RhpGCHeapUpdatePostGCGenerationStats")]
        internal static unsafe void RhpGCHeapUpdatePostGCGenerationStats(
            byte** heapSource,
            int heapCount,
            byte* generationSource,
            byte* dynamicDataSource,
            byte* ephemeralHeapSegmentLocationSource,
            nuint generationTableOffset,
            nuint generationSize,
            nuint generationStartSegmentOffset,
            nuint generationAllocationStartOffset,
            nuint ephemeralHeapSegmentOffset,
            nuint alignedMinObjectSize,
            nuint dynamicDataTableOffset,
            nuint dynamicDataSize,
            nuint promotedSizeOffset,
            nuint freachPreviousPromotionOffset,
            int condemnedGeneration,
            int maxGeneration,
            int lohGeneration,
            int totalGenerationCount,
            uint useRegions,
            nuint* generationSizes,
            nuint* generationPromotedSizes,
            nuint* promotedFinalizationMemory)
        {
            *promotedFinalizationMemory = 0;
            for (int generation = 0; generation < totalGenerationCount; generation++)
            {
                for (int heap = 0; heap < heapCount; heap++)
                {
                    byte* heapAddress = heapSource[heap];
                    byte* generationData = generationSource is null ?
                        heapAddress + (nint)generationTableOffset :
                        generationSource;
                    HeapSegmentPrefix** ephemeralHeapSegmentLocation = useRegions != 0 ?
                        null :
                        (HeapSegmentPrefix**)(ephemeralHeapSegmentLocationSource is null ?
                            heapAddress + (nint)ephemeralHeapSegmentOffset :
                            ephemeralHeapSegmentLocationSource);
                    byte* dynamicDataAddress = (dynamicDataSource is null ?
                        heapAddress + (nint)dynamicDataTableOffset :
                        dynamicDataSource) +
                        (nint)((nuint)generation * dynamicDataSize);

                    nuint heapGenerationSize = GenerationSize(
                        generation,
                        generationData,
                        generationSize,
                        ephemeralHeapSegmentLocation,
                        alignedMinObjectSize,
                        useRegions,
                        generationStartSegmentOffset,
                        generationAllocationStartOffset);
                    generationSizes[generation] = unchecked(generationSizes[generation] + heapGenerationSize);

                    if (generation <= condemnedGeneration)
                    {
                        generationPromotedSizes[generation] = unchecked(
                            generationPromotedSizes[generation] +
                            *(nuint*)(dynamicDataAddress + (nint)promotedSizeOffset));
                    }

                    if ((generation == lohGeneration) && (condemnedGeneration == maxGeneration))
                    {
                        generationPromotedSizes[generation] = unchecked(
                            generationPromotedSizes[generation] +
                            *(nuint*)(dynamicDataAddress + (nint)promotedSizeOffset));
                    }

                    if (generation == 0)
                    {
                        *promotedFinalizationMemory = unchecked(
                            *promotedFinalizationMemory +
                            *(nuint*)(dynamicDataAddress + (nint)freachPreviousPromotionOffset));
                    }
                }
            }
        }

        [RuntimeExport("RhpGCHeapGetTotalSurvivedSize")]
        internal static unsafe nuint RhpGCHeapGetTotalSurvivedSize(
            byte** heapSource,
            int heapCount,
            byte* historySource,
            nuint historyOffset,
            nuint generationDataOffset,
            nuint generationDataSize,
            nuint sizeAfterOffset,
            nuint freeListSpaceAfterOffset,
            nuint freeObjSpaceAfterOffset,
            int totalGenerationCount)
        {
            nuint totalSurvivedSize = 0;
            unchecked
            {
                for (int heap = 0; heap < heapCount; heap++)
                {
                    byte* history = historySource is null ?
                        heapSource[heap] + (nint)historyOffset :
                        historySource;
                    byte* generationData = history + (nint)generationDataOffset;
                    for (int generation = 0; generation < totalGenerationCount; generation++)
                    {
                        byte* generationEntry = generationData + (nint)((nuint)generation * generationDataSize);
                        totalSurvivedSize +=
                            *(nuint*)(generationEntry + (nint)sizeAfterOffset) -
                            *(nuint*)(generationEntry + (nint)freeListSpaceAfterOffset) -
                            *(nuint*)(generationEntry + (nint)freeObjSpaceAfterOffset);
                    }
                }
            }

            return totalSurvivedSize;
        }

        [RuntimeExport("RhpGCHeapGetTotalAllocatedSinceLastGC")]
        internal static unsafe void RhpGCHeapGetTotalAllocatedSinceLastGC(
            byte** heapSource,
            int heapCount,
            byte* allocatedSinceLastGCSource,
            nuint allocatedSinceLastGCOffset,
            int totalOhCount,
            nuint* ohAllocated)
        {
            for (int oh = 0; oh < totalOhCount; oh++)
            {
                ohAllocated[oh] = 0;
            }

            unchecked
            {
                for (int heap = 0; heap < heapCount; heap++)
                {
                    nuint* allocatedSinceLastGC = (nuint*)(allocatedSinceLastGCSource is null ?
                        heapSource[heap] + (nint)allocatedSinceLastGCOffset :
                        allocatedSinceLastGCSource);
                    for (int oh = 0; oh < totalOhCount; oh++)
                    {
                        ohAllocated[oh] += allocatedSinceLastGC[oh];
                        allocatedSinceLastGC[oh] = 0;
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe nuint CurrentAllocated(
            byte* dynamicData,
            nuint dynamicDataSize,
            nuint desiredAllocationOffset,
            nuint newAllocationOffset,
            int uohStartGeneration,
            int totalGenerationCount)
        {
            unchecked
            {
                nuint currentAllocated =
                    *(nuint*)(dynamicData + (nint)desiredAllocationOffset) -
                    (nuint)(*(nint*)(dynamicData + (nint)newAllocationOffset));
                for (int generation = uohStartGeneration; generation < totalGenerationCount; generation++)
                {
                    byte* dynamicDataForGeneration = dynamicData + (nint)((nuint)generation * dynamicDataSize);
                    currentAllocated +=
                        *(nuint*)(dynamicDataForGeneration + (nint)desiredAllocationOffset) -
                        (nuint)(*(nint*)(dynamicDataForGeneration + (nint)newAllocationOffset));
                }

                return currentAllocated;
            }
        }

        [RuntimeExport("RhpGCHeapGetTotalAllocated")]
        internal static unsafe nuint RhpGCHeapGetTotalAllocated(
            byte** heapSource,
            int heapCount,
            byte* dynamicDataSource,
            nuint dynamicDataTableOffset,
            nuint dynamicDataSize,
            nuint desiredAllocationOffset,
            nuint newAllocationOffset,
            int uohStartGeneration,
            int totalGenerationCount)
        {
            nuint totalAllocated = 0;
            unchecked
            {
                for (int heap = 0; heap < heapCount; heap++)
                {
                    byte* dynamicData = dynamicDataSource is null ?
                        heapSource[heap] + (nint)dynamicDataTableOffset :
                        dynamicDataSource;
                    totalAllocated += CurrentAllocated(
                        dynamicData,
                        dynamicDataSize,
                        desiredAllocationOffset,
                        newAllocationOffset,
                        uohStartGeneration,
                        totalGenerationCount);
                }
            }

            return totalAllocated;
        }

        [RuntimeExport("RhpGCHeapGetTotalPromoted")]
        internal static unsafe nuint RhpGCHeapGetTotalPromoted(
            byte** heapSource,
            int heapCount,
            byte* dynamicDataSource,
            nuint dynamicDataTableOffset,
            nuint dynamicDataSize,
            nuint promotedSizeOffset,
            int condemnedGeneration,
            int maxGeneration,
            int totalGenerationCount)
        {
            int highestGeneration = condemnedGeneration == maxGeneration ?
                totalGenerationCount - 1 :
                condemnedGeneration;
            nuint totalPromotedSize = 0;
            unchecked
            {
                for (int heap = 0; heap < heapCount; heap++)
                {
                    byte* dynamicData = dynamicDataSource is null ?
                        heapSource[heap] + (nint)dynamicDataTableOffset :
                        dynamicDataSource;
                    for (int generation = 0; generation <= highestGeneration; generation++)
                    {
                        byte* dynamicDataForGeneration = dynamicData + (nint)((nuint)generation * dynamicDataSize);
                        totalPromotedSize += *(nuint*)(dynamicDataForGeneration + (nint)promotedSizeOffset);
                    }
                }
            }

            return totalPromotedSize;
        }

        [RuntimeExport("RhpGCHeapUpdatePostGCTimeCounters")]
        internal static unsafe void RhpGCHeapUpdatePostGCTimeCounters(
            ulong* totalTimeInGC,
            ulong* totalTimeSinceLastGCEnd,
            uint* percentTimeInGCSinceLastGC,
            ulong currentPerfCounterTimer)
        {
            unchecked
            {
                *totalTimeInGC = currentPerfCounterTimer - *totalTimeInGC;
                ulong timeInGCBase = currentPerfCounterTimer - *totalTimeSinceLastGCEnd;

                if (timeInGCBase < *totalTimeInGC)
                {
                    *totalTimeInGC = 0;
                }

                while (timeInGCBase > uint.MaxValue)
                {
                    timeInGCBase >>= 8;
                    *totalTimeInGC >>= 8;
                }

                if (timeInGCBase != 0)
                {
                    *percentTimeInGCSinceLastGC = (uint)(*totalTimeInGC * 100 / timeInGCBase);
                }
                else
                {
                    *percentTimeInGCSinceLastGC = 0;
                }

                *totalTimeSinceLastGCEnd = currentPerfCounterTimer;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe int ObjectGennum(
            byte* objectAddress,
            uint useRegions,
            byte* regionMap,
            uint regionShift,
            uint regionGenMask,
            HeapSegmentPrefix** ephemeralHeapSegmentLocation,
            byte* generationData,
            nuint generationSize,
            nuint generationAllocationStartOffset,
            int maxGeneration)
        {
            if (useRegions != 0)
            {
                nuint skewedBasicRegionIndex = (nuint)objectAddress >> (int)regionShift;
                return *(regionMap + (nint)skewedBasicRegionIndex) & (int)regionGenMask;
            }

            if (InRangeForSegment(objectAddress, *ephemeralHeapSegmentLocation) &&
                (objectAddress >= *(byte**)(GenerationAddress(generationData, generationSize, maxGeneration - 1) +
                    (nint)generationAllocationStartOffset)))
            {
                for (int i = 0; i < maxGeneration - 1; i++)
                {
                    if (objectAddress >= *(byte**)(GenerationAddress(generationData, generationSize, i) +
                        (nint)generationAllocationStartOffset))
                    {
                        return i;
                    }
                }

                return maxGeneration - 1;
            }

            return maxGeneration;
        }

        [RuntimeExport("RhpGCHeapGetGenerationWithRange")]
        internal static unsafe uint RhpGCHeapGetGenerationWithRange(
            byte* objectAddress,
            HeapSegmentPrefix* segment,
            uint useRegions,
            nuint segmentGenerationOffset,
            int lohGeneration,
            int pohGeneration,
            HeapSegmentPrefix** ephemeralHeapSegmentLocation,
            byte* generationData,
            nuint generationSize,
            nuint generationAllocationStartOffset,
            int maxGeneration,
            byte** start,
            byte** allocated,
            byte** reserved)
        {
            int generation = -1;
            if (useRegions != 0)
            {
                generation = *((byte*)segment + (nint)segmentGenerationOffset);
                if (generation == maxGeneration)
                {
                    if ((segment->Flags & HeapSegmentFlagsLoh) != 0)
                    {
                        generation = lohGeneration;
                    }
                    else if ((segment->Flags & HeapSegmentFlagsPoh) != 0)
                    {
                        generation = pohGeneration;
                    }
                }

                *start = segment->Mem;
                *allocated = segment->Allocated;
                *reserved = segment->Reserved;
            }
            else
            {
                HeapSegmentPrefix* ephemeralHeapSegment = *ephemeralHeapSegmentLocation;
                if (segment == ephemeralHeapSegment)
                {
                    byte* reservedAddress = segment->Reserved;
                    byte* end = segment->Allocated;
                    for (int gen = 0; gen < maxGeneration; gen++)
                    {
                        byte* generationStart = *(byte**)(GenerationAddress(generationData, generationSize, gen) +
                            (nint)generationAllocationStartOffset);
                        if (objectAddress >= generationStart)
                        {
                            generation = gen;
                            *start = generationStart;
                            *allocated = end;
                            *reserved = reservedAddress;
                            break;
                        }

                        end = reservedAddress = generationStart;
                    }

                    if (generation == -1)
                    {
                        generation = maxGeneration;
                        *start = segment->Mem;
                        byte* generationStart = *(byte**)(GenerationAddress(generationData, generationSize, maxGeneration - 1) +
                            (nint)generationAllocationStartOffset);
                        *allocated = *reserved = generationStart;
                    }
                }
                else
                {
                    generation = maxGeneration;
                    if ((segment->Flags & HeapSegmentFlagsLoh) != 0)
                    {
                        generation = lohGeneration;
                    }
                    else if ((segment->Flags & HeapSegmentFlagsPoh) != 0)
                    {
                        generation = pohGeneration;
                    }

                    *start = segment->Mem;
                    *allocated = segment->Allocated;
                    *reserved = segment->Reserved;
                }
            }

            return (uint)generation;
        }

        [RuntimeExport("RhpGCHeapWhichGeneration")]
        internal static unsafe uint RhpGCHeapWhichGeneration(
            byte* objectAddress,
            uint useRegions,
            byte* regionMap,
            uint regionShift,
            uint regionGenMask,
            HeapSegmentPrefix** ephemeralHeapSegmentLocation,
            byte* generationData,
            nuint generationSize,
            nuint generationAllocationStartOffset,
            int maxGeneration)
        {
            return (uint)ObjectGennum(
                objectAddress,
                useRegions,
                regionMap,
                regionShift,
                regionGenMask,
                ephemeralHeapSegmentLocation,
                generationData,
                generationSize,
                generationAllocationStartOffset,
                maxGeneration);
        }

        [RuntimeExport("RhpGCHeapIsEphemeral")]
        internal static unsafe uint RhpGCHeapIsEphemeral(
            byte* objectAddress,
            uint useRegions,
            byte* regionMap,
            uint regionShift,
            uint regionGenMask,
            byte** ephemeralLowLocation,
            byte** ephemeralHighLocation,
            int maxGeneration)
        {
            if (useRegions != 0)
            {
                int generation = ObjectGennum(
                    objectAddress,
                    useRegions,
                    regionMap,
                    regionShift,
                    regionGenMask,
                    null,
                    null,
                    0,
                    0,
                    maxGeneration);
                return generation < maxGeneration ? 1U : 0U;
            }

            return (objectAddress >= *ephemeralLowLocation) && (objectAddress < *ephemeralHighLocation) ? 1U : 0U;
        }

        [RuntimeExport("RhpGCHeapInitializeYieldProcessorSpinPolicy")]
        internal static unsafe void RhpGCHeapInitializeYieldProcessorSpinPolicy(
            uint* ypSpinCountUnit,
            uint* originalSpinCountUnit,
            bool* spinCountUnitConfigP,
            uint initialSpinCountUnit,
            long spinCountUnitFromConfig,
            int dynamicAdaptationMode,
            int dynamicAdaptationToApplicationSizes,
            uint dynamicAdaptationEnabled)
        {
            *ypSpinCountUnit = initialSpinCountUnit;

            bool spinCountUnitConfig = (spinCountUnitFromConfig > 0) && (spinCountUnitFromConfig <= MaxYpSpinCountUnit);
            *spinCountUnitConfigP = spinCountUnitConfig;
            if (spinCountUnitConfig)
            {
                *ypSpinCountUnit = (uint)(int)spinCountUnitFromConfig;
            }

            *originalSpinCountUnit = *ypSpinCountUnit;

            if ((dynamicAdaptationEnabled != 0) &&
                (dynamicAdaptationMode == dynamicAdaptationToApplicationSizes) &&
                (!*spinCountUnitConfigP))
            {
                *ypSpinCountUnit = 10;
            }
        }

        [RuntimeExport("RhpGCHeapSetYieldProcessorScalingFactor")]
        internal static unsafe void RhpGCHeapSetYieldProcessorScalingFactor(
            uint* ypSpinCountUnit,
            uint* originalSpinCountUnit,
            bool* spinCountUnitConfigP,
            float scalingFactor)
        {
            if (!*spinCountUnitConfigP)
            {
                Debug.Assert(*ypSpinCountUnit != 0);
                uint savedYpSpinCountUnit = *ypSpinCountUnit;
                *ypSpinCountUnit = (uint)(*originalSpinCountUnit * scalingFactor / 9f);

                // It's very suspicious if it becomes 0 and also, we don't want to spin too much.
                if ((*ypSpinCountUnit == 0) || (*ypSpinCountUnit > MaxYpSpinCountUnit))
                {
                    *ypSpinCountUnit = savedYpSpinCountUnit;
                }
            }
        }

        [RuntimeExport("RhpGCHeapGetMemoryInfo")]
        internal static unsafe void RhpGCHeapGetMemoryInfo(
            ulong* highMemoryLoadThresholdBytes,
            ulong* totalAvailableMemoryBytes,
            ulong* lastRecordedMemLoadBytes,
            ulong* lastRecordedHeapSizeBytes,
            ulong* lastRecordedFragmentationBytes,
            ulong* totalCommittedBytes,
            ulong* promotedBytes,
            ulong* pinnedObjectCount,
            ulong* finalizationPendingCount,
            ulong* index,
            uint* generation,
            uint* pauseTimePct,
            bool* isCompaction,
            bool* isConcurrent,
            ulong* genInfoRaw,
            ulong* pauseInfoRaw,
            int kind,
            LastRecordedGcInfo* lastEphemeralGcInfo,
            LastRecordedGcInfo* lastFullBlockingGcInfo,
            LastRecordedGcInfo* lastBackgroundGcInfo,
            uint isLastRecordedBgc,
            uint highMemoryLoadThreshold,
            ulong totalPhysicalMemory,
            nuint heapHardLimit,
            uint backgroundGcEnabled)
        {
            LastRecordedGcInfo* lastGcInfo;

            if (kind == GcKindEphemeral)
            {
                lastGcInfo = lastEphemeralGcInfo;
            }
            else if (kind == GcKindFullBlocking)
            {
                lastGcInfo = lastFullBlockingGcInfo;
            }
            else if ((kind == GcKindBackground) && (backgroundGcEnabled != 0))
            {
                lastGcInfo = lastBackgroundGcInfo;
            }
            else
            {
                Debug.Assert(kind == GcKindAny);
                if ((backgroundGcEnabled != 0) && (isLastRecordedBgc != 0))
                {
                    lastGcInfo = lastBackgroundGcInfo;
                }
                else
                {
                    lastGcInfo = ReadVolatile(&lastEphemeralGcInfo->Index) > ReadVolatile(&lastFullBlockingGcInfo->Index) ?
                        lastEphemeralGcInfo :
                        lastFullBlockingGcInfo;
                }
            }

            *highMemoryLoadThresholdBytes = (ulong)(((double)highMemoryLoadThreshold) / 100 * totalPhysicalMemory);
            *totalAvailableMemoryBytes = heapHardLimit != 0 ? (ulong)heapHardLimit : totalPhysicalMemory;
            *lastRecordedMemLoadBytes = (ulong)(((double)lastGcInfo->MemoryLoad) / 100 * totalPhysicalMemory);
            *lastRecordedHeapSizeBytes = (ulong)lastGcInfo->HeapSize;
            *lastRecordedFragmentationBytes = (ulong)lastGcInfo->Fragmentation;
            *totalCommittedBytes = (ulong)lastGcInfo->TotalCommitted;
            *promotedBytes = (ulong)lastGcInfo->Promoted;
            *pinnedObjectCount = (ulong)lastGcInfo->PinnedObjects;
            *finalizationPendingCount = (ulong)lastGcInfo->FinalizePromotedObjects;
            *index = (ulong)ReadVolatile(&lastGcInfo->Index);
            *generation = lastGcInfo->CondemnedGeneration;
            *pauseTimePct = (uint)(int)(lastGcInfo->PausePercentage * 100);
            *isCompaction = lastGcInfo->Compaction != 0;
            *isConcurrent = lastGcInfo->Concurrent != 0;

            RecordedGenerationInfo* genInfo = &lastGcInfo->GenInfo0;
            int genInfoIndex = 0;
            for (int i = 0; i < TotalGenerationCount; i++)
            {
                genInfoRaw[genInfoIndex++] = (ulong)genInfo[i].SizeBefore;
                genInfoRaw[genInfoIndex++] = (ulong)genInfo[i].FragmentationBefore;
                genInfoRaw[genInfoIndex++] = (ulong)genInfo[i].SizeAfter;
                genInfoRaw[genInfoIndex++] = (ulong)genInfo[i].FragmentationAfter;
            }

            nuint* pauseDurations = &lastGcInfo->PauseDuration0;
            for (int i = 0; i < 2; i++)
            {
                pauseInfoRaw[i] = (ulong)pauseDurations[i] * 10;
            }

            if (ReadVolatile(&lastGcInfo->Index) != 0)
            {
                if (kind == GcKindEphemeral)
                {
                    Debug.Assert(lastGcInfo->CondemnedGeneration < MaxGeneration);
                }
                else if (kind == GcKindFullBlocking)
                {
                    Debug.Assert(lastGcInfo->CondemnedGeneration == MaxGeneration);
                    Debug.Assert(lastGcInfo->Concurrent == 0);
                }
                else if ((kind == GcKindBackground) && (backgroundGcEnabled != 0))
                {
                    Debug.Assert(lastGcInfo->CondemnedGeneration == MaxGeneration);
                    Debug.Assert(lastGcInfo->Concurrent != 0);
                }
            }
        }

        [RuntimeExport("RhpGCHeapGetTotalPauseDuration")]
        internal static long RhpGCHeapGetTotalPauseDuration(ulong totalSuspendedTime)
        {
            return (long)(totalSuspendedTime * 10);
        }

        [RuntimeExport("RhpGCHeapGetMemoryLoad")]
        internal static unsafe uint RhpGCHeapGetMemoryLoad(uint* exitMemoryLoad, uint* entryMemoryLoad)
        {
            uint memoryLoad = 0;
            if (*exitMemoryLoad != 0)
            {
                memoryLoad = *exitMemoryLoad;
            }
            else if (*entryMemoryLoad != 0)
            {
                memoryLoad = *entryMemoryLoad;
            }

            return memoryLoad;
        }

        [RuntimeExport("RhpGCHeapGetGcLatencyMode")]
        internal static int RhpGCHeapGetGcLatencyMode(int pauseMode)
        {
            return pauseMode;
        }

        [RuntimeExport("RhpGCHeapSetGcLatencyMode")]
        internal static unsafe int RhpGCHeapSetGcLatencyMode(
            int* settingsPauseMode,
            int* targetPauseMode,
            int newLatencyMode,
            int* savedBgcPauseMode,
            uint backgroundGcEnabled,
            int* backgroundRunning,
            uint gcCanUseConcurrent,
            uint multipleHeaps)
        {
            if (*settingsPauseMode == PauseNoGc)
            {
                return SetPauseModeNoGc;
            }

            int newMode = newLatencyMode;

            if (newMode == PauseLowLatency)
            {
                if (multipleHeaps == 0)
                {
                    *targetPauseMode = newMode;
                }
            }
            else if (newMode == PauseSustainedLowLatency)
            {
                if ((backgroundGcEnabled != 0) && (gcCanUseConcurrent != 0))
                {
                    *targetPauseMode = newMode;
                }
            }
            else
            {
                *targetPauseMode = newMode;
            }

            if ((backgroundGcEnabled != 0) && (ReadVolatile(backgroundRunning) != 0))
            {
                if (*savedBgcPauseMode != newMode)
                {
                    *savedBgcPauseMode = newMode;
                }
            }

            return SetPauseModeSuccess;
        }

        [RuntimeExport("RhpGCHeapGetLohCompactionMode")]
        internal static unsafe int RhpGCHeapGetLohCompactionMode(int* lohCompactionMode)
        {
            return *lohCompactionMode;
        }

        [RuntimeExport("RhpGCHeapSetLohCompactionMode")]
        internal static unsafe void RhpGCHeapSetLohCompactionMode(int* lohCompactionMode, int newLohCompactionMode)
        {
            *lohCompactionMode = newLohCompactionMode;
        }
    }
}
