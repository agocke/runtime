// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the "time" section of gc/windows/gcenv.windows.cpp:
// GCToOSInterface::QueryPerformanceCounter, GCToOSInterface::QueryPerformanceFrequency and
// GCToOSInterface::GetLowPrecisionTimeStamp, in the order the C++ file defines them, with the
// same statements.
//
// Unlike the Unix side, which goes through src/native/minipal/time.c, the Windows C++ calls
// Win32 itself, so this file does too: QueryPerformanceCounter, QueryPerformanceFrequency and
// QueryUnbiasedInterruptTime as [RuntimeImport]s of those entry points. A runtime import is a
// direct call to the linked symbol with no marshalling and no GC mode transition, which is what
// the collector needs: these three are on the timing path of every collection, and are called
// with the world suspended. kernel32.lib, which exports all three, is on the default link line
// of every NativeAOT application. The declarations live in GCToOSInterface.Imports.Windows.cs so
// that the test host can substitute the same private methods; see
// tests/GCToOSInterface.Imports.Windows.TestHost.cs.
//
// Two of the three imports are spelled with a Win32 prefix, following the same rule the write
// watch port already uses in that file: a name is prefixed only when GCToOSInterface has a
// method of its own with the Win32 name, and [RuntimeImport] carries the real symbol, so the
// two never have to agree. The correspondence is one-to-one and is not an overload of the
// method above it:
//
//     Win32QueryPerformanceCounter   is ::QueryPerformanceCounter of <windows.h>
//     Win32QueryPerformanceFrequency is ::QueryPerformanceFrequency of <windows.h>
//     QueryUnbiasedInterruptTime     is ::QueryUnbiasedInterruptTime of <windows.h>, unprefixed
//                                    because nothing here is called that
//
// Declaring the imports as overloads instead -- a QueryPerformanceCounter(long*) beside the
// QueryPerformanceCounter() below it -- is legal C# and would read closer to the C++ `::` call,
// but it would make a one-character slip at a call site select the wrong method and recurse, so
// the distinct name is deliberate.
//
// The C++ carries the message of each failure in the assert condition, as
// `assert(false && "Failed to query performance counter")`. This library drops the message
// strings, exactly as the write watch port does, because a string literal in the GC is a heap
// object the GC would have to allocate to report the failure; the message is kept in a comment
// on the assert instead. Debug.Fail is not used for the same reason: its only overloads take a
// message. Debug.Assert(bool) is [Conditional("DEBUG")], so the compare and the branch are not
// even emitted into a release build, which is what the C++ NDEBUG build does with assert.
//
// What follows the assert is preserved as well: all three read the value the failed call left
// behind rather than returning early, so a release build behaves as the C++ release build does.
// The output local is declared the way the C++ declares it -- uninitialized, address taken,
// read back afterwards -- and deliberately carries no `= 0`, so nothing in this file forces a
// value onto a path that cannot be reached. One residual difference cannot be removed in C#:
// this assembly does not set SkipLocalsInit, so the emitted method bodies carry the `.locals
// init` flag and the runtime zeroes the local before the call. Should one of these Win32 calls
// ever fail, the C++ therefore returns whatever the stack held and this returns zero. Spelling
// the local as one-element stack storage instead (`long* ts = stackalloc long[1]`) does not
// change that -- `.locals init` zeroes a stackalloc too -- and it obscures the C++ shape, so
// the plain local is kept.

using System.Diagnostics;

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
            // A LARGE_INTEGER is a union whose QuadPart is a LONGLONG at offset zero, so the
            // out parameter is spelled as the long that the C++ reads back out of it.
            long ts;
            if (Win32QueryPerformanceCounter(&ts) == 0)
            {
                // "Failed to query performance counter"
                Debug.Assert(false);
            }

            return ts;
        }

        /// <summary>Get a frequency of the high precision performance counter.</summary>
        /// <returns>The counter frequency.</returns>
        public static long QueryPerformanceFrequency()
        {
            long ts;
            if (Win32QueryPerformanceFrequency(&ts) == 0)
            {
                // "Failed to query performance counter"
                Debug.Assert(false);
            }

            return ts;
        }

        /// <summary>Get a time stamp with a low precision.</summary>
        /// <returns>Time stamp in milliseconds.</returns>
        public static ulong GetLowPrecisionTimeStamp()
        {
            // GetTickCount64 uses fixed resolution of 10-16ms for backward compatibility. Use
            // QueryUnbiasedInterruptTime instead which becomes more accurate if the underlying system
            // resolution is improved. This helps responsiveness in the case an app is trying to opt
            // into things like multimedia scenarios and additionally does not include "bias" from time
            // the system is spent asleep or in hibernation.

            const ulong TicksPerMillisecond = 10000;

            ulong unbiasedTime;
            if (QueryUnbiasedInterruptTime(&unbiasedTime) == 0)
            {
                // "Failed to query unbiased interrupt time"
                Debug.Assert(false);
            }

            return (ulong)(unbiasedTime / TicksPerMillisecond);
        }
    }
}
