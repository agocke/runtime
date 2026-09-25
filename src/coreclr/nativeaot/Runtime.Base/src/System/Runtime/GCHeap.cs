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
