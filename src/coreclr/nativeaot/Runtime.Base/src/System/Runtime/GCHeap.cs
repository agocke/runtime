// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Threading;

namespace System.Runtime
{
    internal static partial class RuntimeExports
    {
        private const uint MaxYpSpinCountUnit = 32768;

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
    }
}
