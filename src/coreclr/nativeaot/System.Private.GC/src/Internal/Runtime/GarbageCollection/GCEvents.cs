// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from src/coreclr/gc/gceventstatus.h and src/coreclr/gc/gcevents.h.
//
// The C++ uses KNOWN_EVENT and DYNAMIC_EVENT x-macros to generate these pairs. C# has no textual
// macro facility, so the expanded methods are kept in the same order as gcevents.h.

using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe class GCEvents
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsEnabled(GCEventProvider provider, GCEventKeyword keyword, GCEventLevel level) =>
            GCEventStatus.IsEnabled(provider, keyword, level);

        public static bool GCEventEnabledGCStart_V2() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Information);
        public static void GCEventFireGCStart_V2(uint count, uint depth, uint reason, uint type)
        {
            if (GCEventEnabledGCStart_V2())
            {
                GCToEEInterface.FireGCStart_V2(count, depth, reason, type);
            }
        }

        public static bool GCEventEnabledGCEnd_V1() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Information);
        public static void GCEventFireGCEnd_V1(uint count, uint depth)
        {
            if (GCEventEnabledGCEnd_V1())
            {
                GCToEEInterface.FireGCEnd_V1(count, depth);
            }
        }

        public static bool GCEventEnabledGCGenerationRange() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GCHeapSurvivalAndMovement, GCEventLevel.Information);
        public static void GCEventFireGCGenerationRange(byte generation, void* rangeStart, ulong rangeUsedLength, ulong rangeReservedLength)
        {
            if (GCEventEnabledGCGenerationRange())
            {
                GCToEEInterface.FireGCGenerationRange(generation, rangeStart, rangeUsedLength, rangeReservedLength);
            }
        }

        public static bool GCEventEnabledGCHeapStats_V2() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Information);
        public static void GCEventFireGCHeapStats_V2(ulong generationSize0, ulong totalPromotedSize0, ulong generationSize1, ulong totalPromotedSize1, ulong generationSize2, ulong totalPromotedSize2, ulong generationSize3, ulong totalPromotedSize3, ulong generationSize4, ulong totalPromotedSize4, ulong finalizationPromotedSize, ulong finalizationPromotedCount, uint pinnedObjectCount, uint sinkBlockCount, uint gcHandleCount)
        {
            if (GCEventEnabledGCHeapStats_V2())
            {
                GCToEEInterface.FireGCHeapStats_V2(generationSize0, totalPromotedSize0, generationSize1, totalPromotedSize1, generationSize2, totalPromotedSize2, generationSize3, totalPromotedSize3, generationSize4, totalPromotedSize4, finalizationPromotedSize, finalizationPromotedCount, pinnedObjectCount, sinkBlockCount, gcHandleCount);
            }
        }

        public static bool GCEventEnabledGCCreateSegment_V1() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Information);
        public static void GCEventFireGCCreateSegment_V1(void* address, nuint size, uint type)
        {
            if (GCEventEnabledGCCreateSegment_V1())
            {
                GCToEEInterface.FireGCCreateSegment_V1(address, size, type);
            }
        }

        public static bool GCEventEnabledGCFreeSegment_V1() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Information);
        public static void GCEventFireGCFreeSegment_V1(void* address)
        {
            if (GCEventEnabledGCFreeSegment_V1())
            {
                GCToEEInterface.FireGCFreeSegment_V1(address);
            }
        }

        public static bool GCEventEnabledGCCreateConcurrentThread_V1() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Information);
        public static void GCEventFireGCCreateConcurrentThread_V1()
        {
            if (GCEventEnabledGCCreateConcurrentThread_V1())
            {
                GCToEEInterface.FireGCCreateConcurrentThread_V1();
            }
        }

        public static bool GCEventEnabledGCTerminateConcurrentThread_V1() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Information);
        public static void GCEventFireGCTerminateConcurrentThread_V1()
        {
            if (GCEventEnabledGCTerminateConcurrentThread_V1())
            {
                GCToEEInterface.FireGCTerminateConcurrentThread_V1();
            }
        }

        public static bool GCEventEnabledGCTriggered() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Information);
        public static void GCEventFireGCTriggered(uint reason)
        {
            if (GCEventEnabledGCTriggered())
            {
                GCToEEInterface.FireGCTriggered(reason);
            }
        }

        public static bool GCEventEnabledGCMarkWithType() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Information);
        public static void GCEventFireGCMarkWithType(uint heapNum, uint type, ulong bytes)
        {
            if (GCEventEnabledGCMarkWithType())
            {
                GCToEEInterface.FireGCMarkWithType(heapNum, type, bytes);
            }
        }

        public static bool GCEventEnabledGCJoin_V2() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Verbose);
        public static void GCEventFireGCJoin_V2(uint heap, uint joinTime, uint joinType, uint joinId)
        {
            if (GCEventEnabledGCJoin_V2())
            {
                GCToEEInterface.FireGCJoin_V2(heap, joinTime, joinType, joinId);
            }
        }

        public static bool GCEventEnabledGCGlobalHeapHistory_V4() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Information);
        public static void GCEventFireGCGlobalHeapHistory_V4(ulong finalYoungestDesired, int numHeaps, uint condemnedGeneration, uint gen0ReductionCount, uint reason, uint globalMechanisms, uint pauseMode, uint memoryPressure, uint condemnReasons0, uint condemnReasons1, uint count, uint valuesLen, void* values)
        {
            if (GCEventEnabledGCGlobalHeapHistory_V4())
            {
                GCToEEInterface.FireGCGlobalHeapHistory_V4(finalYoungestDesired, numHeaps, condemnedGeneration, gen0ReductionCount, reason, globalMechanisms, pauseMode, memoryPressure, condemnReasons0, condemnReasons1, count, valuesLen, values);
            }
        }

        public static bool GCEventEnabledGCAllocationTick_V1() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Verbose);
        public static void GCEventFireGCAllocationTick_V1(uint allocationAmount, uint allocationKind)
        {
            if (GCEventEnabledGCAllocationTick_V1())
            {
                GCToEEInterface.FireGCAllocationTick_V1(allocationAmount, allocationKind);
            }
        }

        public static bool GCEventEnabledGCAllocationTick_V4() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Verbose);
        public static void GCEventFireGCAllocationTick_V4(ulong allocationAmount, uint allocationKind, uint heapIndex, void* objectAddress, ulong objectSize)
        {
            if (GCEventEnabledGCAllocationTick_V4())
            {
                GCToEEInterface.FireGCAllocationTick_V4(allocationAmount, allocationKind, heapIndex, objectAddress, objectSize);
            }
        }

        public static bool GCEventEnabledPinObjectAtGCTime() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Verbose);
        public static void GCEventFirePinObjectAtGCTime(void* objectAddress, byte** objectHandle)
        {
            if (GCEventEnabledPinObjectAtGCTime())
            {
                GCToEEInterface.FirePinObjectAtGCTime(objectAddress, objectHandle);
            }
        }

        public static bool GCEventEnabledGCPerHeapHistory_V3() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Information);
        public static void GCEventFireGCPerHeapHistory_V3(void* freeListAllocated, void* freeListRejected, void* endOfSegAllocated, void* condemnedAllocated, void* pinnedAllocated, void* pinnedAllocatedAdvance, uint runningFreeListEfficiency, uint condemnReasons0, uint condemnReasons1, uint compactMechanisms, uint expandMechanisms, uint heapIndex, void* extraGen0Commit, uint count, uint valuesLen, void* values)
        {
            if (GCEventEnabledGCPerHeapHistory_V3())
            {
                GCToEEInterface.FireGCPerHeapHistory_V3(freeListAllocated, freeListRejected, endOfSegAllocated, condemnedAllocated, pinnedAllocated, pinnedAllocatedAdvance, runningFreeListEfficiency, condemnReasons0, condemnReasons1, compactMechanisms, expandMechanisms, heapIndex, extraGen0Commit, count, valuesLen, values);
            }
        }

        public static bool GCEventEnabledGCLOHCompact() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Information);
        public static void GCEventFireGCLOHCompact(ushort count, uint valuesLen, void* values)
        {
            if (GCEventEnabledGCLOHCompact())
            {
                GCToEEInterface.FireGCLOHCompact(count, valuesLen, values);
            }
        }

        public static bool GCEventEnabledGCFitBucketInfo() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Verbose);
        public static void GCEventFireGCFitBucketInfo(ushort kind, nuint totalSize, ushort count, uint valuesLen, void* values)
        {
            if (GCEventEnabledGCFitBucketInfo())
            {
                GCToEEInterface.FireGCFitBucketInfo(kind, totalSize, count, valuesLen, values);
            }
        }

        public static bool GCEventEnabledSetGCHandle() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GCHandle, GCEventLevel.Information);
        public static void GCEventFireSetGCHandle(void* handleId, void* objectId, uint kind, uint generation)
        {
            if (GCEventEnabledSetGCHandle())
            {
                GCToEEInterface.FireSetGCHandle(handleId, objectId, kind, generation);
            }
        }

        public static bool GCEventEnabledDestroyGCHandle() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GCHandle, GCEventLevel.Information);
        public static void GCEventFireDestroyGCHandle(void* handleId)
        {
            if (GCEventEnabledDestroyGCHandle())
            {
                GCToEEInterface.FireDestroyGCHandle(handleId);
            }
        }

        public static bool GCEventEnabledBGCBegin() => IsEnabled(GCEventProvider.Private, GCEventKeyword.GCPrivate, GCEventLevel.Information);
        public static void GCEventFireBGCBegin()
        {
            if (GCEventEnabledBGCBegin())
            {
                GCToEEInterface.FireBGCBegin();
            }
        }

        public static bool GCEventEnabledBGC1stNonConEnd() => IsEnabled(GCEventProvider.Private, GCEventKeyword.GCPrivate, GCEventLevel.Information);
        public static void GCEventFireBGC1stNonConEnd()
        {
            if (GCEventEnabledBGC1stNonConEnd())
            {
                GCToEEInterface.FireBGC1stNonConEnd();
            }
        }

        public static bool GCEventEnabledBGC1stConEnd() => IsEnabled(GCEventProvider.Private, GCEventKeyword.GCPrivate, GCEventLevel.Information);
        public static void GCEventFireBGC1stConEnd()
        {
            if (GCEventEnabledBGC1stConEnd())
            {
                GCToEEInterface.FireBGC1stConEnd();
            }
        }

        public static bool GCEventEnabledBGC1stSweepEnd() => IsEnabled(GCEventProvider.Private, GCEventKeyword.GCPrivate, GCEventLevel.Information);
        public static void GCEventFireBGC1stSweepEnd(uint genNumber)
        {
            if (GCEventEnabledBGC1stSweepEnd())
            {
                GCToEEInterface.FireBGC1stSweepEnd(genNumber);
            }
        }

        public static bool GCEventEnabledBGC2ndNonConBegin() => IsEnabled(GCEventProvider.Private, GCEventKeyword.GCPrivate, GCEventLevel.Information);
        public static void GCEventFireBGC2ndNonConBegin()
        {
            if (GCEventEnabledBGC2ndNonConBegin())
            {
                GCToEEInterface.FireBGC2ndNonConBegin();
            }
        }

        public static bool GCEventEnabledBGC2ndNonConEnd() => IsEnabled(GCEventProvider.Private, GCEventKeyword.GCPrivate, GCEventLevel.Information);
        public static void GCEventFireBGC2ndNonConEnd()
        {
            if (GCEventEnabledBGC2ndNonConEnd())
            {
                GCToEEInterface.FireBGC2ndNonConEnd();
            }
        }

        public static bool GCEventEnabledBGC2ndConBegin() => IsEnabled(GCEventProvider.Private, GCEventKeyword.GCPrivate, GCEventLevel.Information);
        public static void GCEventFireBGC2ndConBegin()
        {
            if (GCEventEnabledBGC2ndConBegin())
            {
                GCToEEInterface.FireBGC2ndConBegin();
            }
        }

        public static bool GCEventEnabledBGC2ndConEnd() => IsEnabled(GCEventProvider.Private, GCEventKeyword.GCPrivate, GCEventLevel.Information);
        public static void GCEventFireBGC2ndConEnd()
        {
            if (GCEventEnabledBGC2ndConEnd())
            {
                GCToEEInterface.FireBGC2ndConEnd();
            }
        }

        public static bool GCEventEnabledBGCDrainMark() => IsEnabled(GCEventProvider.Private, GCEventKeyword.GCPrivate, GCEventLevel.Information);
        public static void GCEventFireBGCDrainMark(ulong objects)
        {
            if (GCEventEnabledBGCDrainMark())
            {
                GCToEEInterface.FireBGCDrainMark(objects);
            }
        }

        public static bool GCEventEnabledBGCRevisit() => IsEnabled(GCEventProvider.Private, GCEventKeyword.GCPrivate, GCEventLevel.Information);
        public static void GCEventFireBGCRevisit(ulong pages, ulong objects, uint isLarge)
        {
            if (GCEventEnabledBGCRevisit())
            {
                GCToEEInterface.FireBGCRevisit(pages, objects, isLarge);
            }
        }

        public static bool GCEventEnabledBGCOverflow_V1() => IsEnabled(GCEventProvider.Private, GCEventKeyword.GCPrivate, GCEventLevel.Information);
        public static void GCEventFireBGCOverflow_V1(ulong min, ulong max, ulong objects, uint isLarge, uint genNumber)
        {
            if (GCEventEnabledBGCOverflow_V1())
            {
                GCToEEInterface.FireBGCOverflow_V1(min, max, objects, isLarge, genNumber);
            }
        }

        public static bool GCEventEnabledBGCAllocWaitBegin() => IsEnabled(GCEventProvider.Private, GCEventKeyword.GCPrivate, GCEventLevel.Information);
        public static void GCEventFireBGCAllocWaitBegin(uint reason)
        {
            if (GCEventEnabledBGCAllocWaitBegin())
            {
                GCToEEInterface.FireBGCAllocWaitBegin(reason);
            }
        }

        public static bool GCEventEnabledBGCAllocWaitEnd() => IsEnabled(GCEventProvider.Private, GCEventKeyword.GCPrivate, GCEventLevel.Information);
        public static void GCEventFireBGCAllocWaitEnd(uint reason)
        {
            if (GCEventEnabledBGCAllocWaitEnd())
            {
                GCToEEInterface.FireBGCAllocWaitEnd(reason);
            }
        }

        public static bool GCEventEnabledGCFullNotify_V1() => IsEnabled(GCEventProvider.Private, GCEventKeyword.GCPrivate, GCEventLevel.Information);
        public static void GCEventFireGCFullNotify_V1(uint genNumber, uint isAlloc)
        {
            if (GCEventEnabledGCFullNotify_V1())
            {
                GCToEEInterface.FireGCFullNotify_V1(genNumber, isAlloc);
            }
        }

        public static bool GCEventEnabledPrvSetGCHandle() => IsEnabled(GCEventProvider.Private, GCEventKeyword.GCHandlePrivate, GCEventLevel.Information);
        public static void GCEventFirePrvSetGCHandle(void* handleId, void* objectId, uint kind, uint generation)
        {
            if (GCEventEnabledPrvSetGCHandle())
            {
                GCToEEInterface.FirePrvSetGCHandle(handleId, objectId, kind, generation);
            }
        }

        public static bool GCEventEnabledPrvDestroyGCHandle() => IsEnabled(GCEventProvider.Private, GCEventKeyword.GCHandlePrivate, GCEventLevel.Information);
        public static void GCEventFirePrvDestroyGCHandle(void* handleId)
        {
            if (GCEventEnabledPrvDestroyGCHandle())
            {
                GCToEEInterface.FirePrvDestroyGCHandle(handleId);
            }
        }

        public static bool GCEventEnabledPinPlugAtGCTime() => IsEnabled(GCEventProvider.Private, GCEventKeyword.GCPrivate, GCEventLevel.Verbose);
        public static void GCEventFirePinPlugAtGCTime(byte* plugStart, byte* plugEnd, byte* gapBeforeSize)
        {
            if (GCEventEnabledPinPlugAtGCTime())
            {
                GCToEEInterface.FirePinPlugAtGCTime(plugStart, plugEnd, gapBeforeSize);
            }
        }

        public static bool GCEventEnabledCommittedUsage_V1() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Information);
        public static void GCEventFireCommittedUsage_V1(ulong totalCommittedInUse, ulong totalCommittedInGlobalDecommit, ulong totalCommittedInFree, ulong totalCommittedInGlobalFree, ulong totalBookkeepingCommitted)
        {
            if (GCEventEnabledCommittedUsage_V1())
            {
                fixed (byte* name = "CommittedUsage\0"u8)
                {
                    nuint size =
                        GCEventSerializer.SerializedSize((ushort)1)
                        + GCEventSerializer.SerializedSize(totalCommittedInUse)
                        + GCEventSerializer.SerializedSize(totalCommittedInGlobalDecommit)
                        + GCEventSerializer.SerializedSize(totalCommittedInFree)
                        + GCEventSerializer.SerializedSize(totalCommittedInGlobalFree)
                        + GCEventSerializer.SerializedSize(totalBookkeepingCommitted);
                    byte* buffer = stackalloc byte[(int)size];
                    byte* cursor = buffer;
                    GCEventSerializer.Serialize(ref cursor, (ushort)1);
                    GCEventSerializer.Serialize(ref cursor, totalCommittedInUse);
                    GCEventSerializer.Serialize(ref cursor, totalCommittedInGlobalDecommit);
                    GCEventSerializer.Serialize(ref cursor, totalCommittedInFree);
                    GCEventSerializer.Serialize(ref cursor, totalCommittedInGlobalFree);
                    GCEventSerializer.Serialize(ref cursor, totalBookkeepingCommitted);
                    GCToEEInterface.FireDynamicEvent(name, buffer, (uint)size);
                }
            }
        }

        public static bool GCEventEnabledSizeAdaptationTuning_V1() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Information);
        public static void GCEventFireSizeAdaptationTuning_V1(ushort newHeapCount, ushort maxHeapCount, ushort minHeapCount, ulong currentGcIndex, ulong totalSohStableSize, float medianThroughputCostPercent, float tcpToConsider, float currentAroundTargetAccumulation, ushort recordedTcpCount, float recordedTcpSlope, uint numGcsSinceLastChange, byte adjustmentFactor, ushort changeDecision, ushort adjustmentReason, ushort heapCountChangeFrequencyFactor, ushort heapCountFrequencyReason, byte adjustmentMetric)
        {
            if (GCEventEnabledSizeAdaptationTuning_V1())
            {
                fixed (byte* name = "SizeAdaptationTuning\0"u8)
                {
                    nuint size =
                        GCEventSerializer.SerializedSize((ushort)1)
                        + GCEventSerializer.SerializedSize(newHeapCount)
                        + GCEventSerializer.SerializedSize(maxHeapCount)
                        + GCEventSerializer.SerializedSize(minHeapCount)
                        + GCEventSerializer.SerializedSize(currentGcIndex)
                        + GCEventSerializer.SerializedSize(totalSohStableSize)
                        + GCEventSerializer.SerializedSize(medianThroughputCostPercent)
                        + GCEventSerializer.SerializedSize(tcpToConsider)
                        + GCEventSerializer.SerializedSize(currentAroundTargetAccumulation)
                        + GCEventSerializer.SerializedSize(recordedTcpCount)
                        + GCEventSerializer.SerializedSize(recordedTcpSlope)
                        + GCEventSerializer.SerializedSize(numGcsSinceLastChange)
                        + GCEventSerializer.SerializedSize(adjustmentFactor)
                        + GCEventSerializer.SerializedSize(changeDecision)
                        + GCEventSerializer.SerializedSize(adjustmentReason)
                        + GCEventSerializer.SerializedSize(heapCountChangeFrequencyFactor)
                        + GCEventSerializer.SerializedSize(heapCountFrequencyReason)
                        + GCEventSerializer.SerializedSize(adjustmentMetric);
                    byte* buffer = stackalloc byte[(int)size];
                    byte* cursor = buffer;
                    GCEventSerializer.Serialize(ref cursor, (ushort)1);
                    GCEventSerializer.Serialize(ref cursor, newHeapCount);
                    GCEventSerializer.Serialize(ref cursor, maxHeapCount);
                    GCEventSerializer.Serialize(ref cursor, minHeapCount);
                    GCEventSerializer.Serialize(ref cursor, currentGcIndex);
                    GCEventSerializer.Serialize(ref cursor, totalSohStableSize);
                    GCEventSerializer.Serialize(ref cursor, medianThroughputCostPercent);
                    GCEventSerializer.Serialize(ref cursor, tcpToConsider);
                    GCEventSerializer.Serialize(ref cursor, currentAroundTargetAccumulation);
                    GCEventSerializer.Serialize(ref cursor, recordedTcpCount);
                    GCEventSerializer.Serialize(ref cursor, recordedTcpSlope);
                    GCEventSerializer.Serialize(ref cursor, numGcsSinceLastChange);
                    GCEventSerializer.Serialize(ref cursor, adjustmentFactor);
                    GCEventSerializer.Serialize(ref cursor, changeDecision);
                    GCEventSerializer.Serialize(ref cursor, adjustmentReason);
                    GCEventSerializer.Serialize(ref cursor, heapCountChangeFrequencyFactor);
                    GCEventSerializer.Serialize(ref cursor, heapCountFrequencyReason);
                    GCEventSerializer.Serialize(ref cursor, adjustmentMetric);
                    GCToEEInterface.FireDynamicEvent(name, buffer, (uint)size);
                }
            }
        }

        public static bool GCEventEnabledSizeAdaptationFullGCTuning_V1() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Information);
        public static void GCEventFireSizeAdaptationFullGCTuning_V1(ushort newHeapCount, ulong currentGcIndex, float medianGen2Tcp, uint numGen2sSinceLastChange, uint sample0GcIndexDistance, float sample0GcPercent, uint sample1GcIndexDistance, float sample1GcPercent, uint sample2GcIndexDistance, float sample2GcPercent)
        {
            if (GCEventEnabledSizeAdaptationFullGCTuning_V1())
            {
                fixed (byte* name = "SizeAdaptationFullGCTuning\0"u8)
                {
                    nuint size =
                        GCEventSerializer.SerializedSize((ushort)1)
                        + GCEventSerializer.SerializedSize(newHeapCount)
                        + GCEventSerializer.SerializedSize(currentGcIndex)
                        + GCEventSerializer.SerializedSize(medianGen2Tcp)
                        + GCEventSerializer.SerializedSize(numGen2sSinceLastChange)
                        + GCEventSerializer.SerializedSize(sample0GcIndexDistance)
                        + GCEventSerializer.SerializedSize(sample0GcPercent)
                        + GCEventSerializer.SerializedSize(sample1GcIndexDistance)
                        + GCEventSerializer.SerializedSize(sample1GcPercent)
                        + GCEventSerializer.SerializedSize(sample2GcIndexDistance)
                        + GCEventSerializer.SerializedSize(sample2GcPercent);
                    byte* buffer = stackalloc byte[(int)size];
                    byte* cursor = buffer;
                    GCEventSerializer.Serialize(ref cursor, (ushort)1);
                    GCEventSerializer.Serialize(ref cursor, newHeapCount);
                    GCEventSerializer.Serialize(ref cursor, currentGcIndex);
                    GCEventSerializer.Serialize(ref cursor, medianGen2Tcp);
                    GCEventSerializer.Serialize(ref cursor, numGen2sSinceLastChange);
                    GCEventSerializer.Serialize(ref cursor, sample0GcIndexDistance);
                    GCEventSerializer.Serialize(ref cursor, sample0GcPercent);
                    GCEventSerializer.Serialize(ref cursor, sample1GcIndexDistance);
                    GCEventSerializer.Serialize(ref cursor, sample1GcPercent);
                    GCEventSerializer.Serialize(ref cursor, sample2GcIndexDistance);
                    GCEventSerializer.Serialize(ref cursor, sample2GcPercent);
                    GCToEEInterface.FireDynamicEvent(name, buffer, (uint)size);
                }
            }
        }

        public static bool GCEventEnabledSizeAdaptationSample_V1() => IsEnabled(GCEventProvider.Default, GCEventKeyword.GC, GCEventLevel.Information);
        public static void GCEventFireSizeAdaptationSample_V1(ulong gcIndex, uint elapsedBetweenGcs, uint gcPauseTime, uint sohMslWaitTime, uint uohMslWaitTime, ulong totalSohStableSize, uint gen0BudgetPerHeap)
        {
            if (GCEventEnabledSizeAdaptationSample_V1())
            {
                fixed (byte* name = "SizeAdaptationSample\0"u8)
                {
                    nuint size =
                        GCEventSerializer.SerializedSize((ushort)1)
                        + GCEventSerializer.SerializedSize(gcIndex)
                        + GCEventSerializer.SerializedSize(elapsedBetweenGcs)
                        + GCEventSerializer.SerializedSize(gcPauseTime)
                        + GCEventSerializer.SerializedSize(sohMslWaitTime)
                        + GCEventSerializer.SerializedSize(uohMslWaitTime)
                        + GCEventSerializer.SerializedSize(totalSohStableSize)
                        + GCEventSerializer.SerializedSize(gen0BudgetPerHeap);
                    byte* buffer = stackalloc byte[(int)size];
                    byte* cursor = buffer;
                    GCEventSerializer.Serialize(ref cursor, (ushort)1);
                    GCEventSerializer.Serialize(ref cursor, gcIndex);
                    GCEventSerializer.Serialize(ref cursor, elapsedBetweenGcs);
                    GCEventSerializer.Serialize(ref cursor, gcPauseTime);
                    GCEventSerializer.Serialize(ref cursor, sohMslWaitTime);
                    GCEventSerializer.Serialize(ref cursor, uohMslWaitTime);
                    GCEventSerializer.Serialize(ref cursor, totalSohStableSize);
                    GCEventSerializer.Serialize(ref cursor, gen0BudgetPerHeap);
                    GCToEEInterface.FireDynamicEvent(name, buffer, (uint)size);
                }
            }
        }
    }
}
