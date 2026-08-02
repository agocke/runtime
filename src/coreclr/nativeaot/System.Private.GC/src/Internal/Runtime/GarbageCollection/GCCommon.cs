// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the dependency-closed parts of gccommon.cpp, in their original order.

namespace Internal.Runtime.GarbageCollection;

internal static partial class GCCommon
{
    private static double g_QPFus;

    public static ulong GetHighPrecisionTimeStamp()
    {
        if (g_QPFus == 0.0)
        {
            g_QPFus = 1000.0 * 1000.0 / (double)GCToOSInterface.QueryPerformanceFrequency();
        }

        return (ulong)((double)GCToOSInterface.QueryPerformanceCounter() * g_QPFus);
    }
}
