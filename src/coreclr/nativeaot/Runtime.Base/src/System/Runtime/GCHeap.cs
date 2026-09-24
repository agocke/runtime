// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading;

namespace System.Runtime
{
    internal static partial class RuntimeExports
    {
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
    }
}
