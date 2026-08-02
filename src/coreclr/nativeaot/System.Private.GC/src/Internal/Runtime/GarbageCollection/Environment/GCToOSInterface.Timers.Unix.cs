// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the "time" section of gc/unix/gcenv.unix.cpp: GCToOSInterface::QueryPerformanceCounter,
// GCToOSInterface::QueryPerformanceFrequency and GCToOSInterface::GetLowPrecisionTimeStamp, in
// the order the C++ file defines them, with the same statements.
//
// All three are one line of C++ over src/native/minipal/time.c, which is where the clock
// selection per platform lives -- clock_gettime_nsec_np(CLOCK_UPTIME_RAW) on Apple, and
// clock_gettime(CLOCK_MONOTONIC) or CLOCK_MONOTONIC_COARSE elsewhere. That file is compiled
// into the aotminipal static library that Microsoft.NETCore.Native.Unix.targets puts on the
// link line of every NativeAOT application, so the three entry points are called here as
// [RuntimeImport]s of those exact symbols, exactly as the C++ calls them. Translating time.c
// itself would fork the clock selection, and the configure checks it is built with
// (HAVE_CLOCK_GETTIME_NSEC_NP, HAVE_CLOCK_MONOTONIC_COARSE) have no managed spelling.
//
// A runtime import is a direct call to the linked symbol with no marshalling and no GC mode
// transition, which is what the collector needs: these three are on the timing path of every
// collection, and are called with the world suspended. The declarations live in
// GCToOSInterface.Imports.Unix.cs so that the test host can substitute the same private methods;
// see tests/GCToOSInterface.Imports.Unix.TestHost.cs.

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe partial class GCToOSInterface
    {
        //
        // Time
        //

        /// <summary>Get a high precision performance counter.</summary>
        /// <returns>The counter value.</returns>
        public static long QueryPerformanceCounter()
        {
            return minipal_hires_ticks();
        }

        /// <summary>Get a frequency of the high precision performance counter.</summary>
        /// <returns>The counter frequency.</returns>
        public static long QueryPerformanceFrequency()
        {
            // The counter frequency of gettimeofday is in microseconds.
            return minipal_hires_tick_frequency();
        }

        /// <summary>Get a time stamp with a low precision.</summary>
        /// <returns>Time stamp in milliseconds.</returns>
        public static ulong GetLowPrecisionTimeStamp()
        {
            return (ulong)minipal_lowres_ticks();
        }
    }
}
