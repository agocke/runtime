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
