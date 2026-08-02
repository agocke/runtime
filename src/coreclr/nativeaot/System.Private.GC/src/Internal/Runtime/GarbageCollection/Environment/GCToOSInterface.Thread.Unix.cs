// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the sleep and yield part of the "thread and process" section of
// gc/unix/gcenv.unix.cpp: GCToOSInterface::Sleep and GCToOSInterface::YieldThread, in the order
// the C++ file defines them, with the same statements, the same retry loop and the same assert.
//
// The calls to libc are [RuntimeImport] declarations, which are direct calls to the linked
// symbol with no marshalling and no GC mode transition -- what the C++ GC gets for free by being
// native code, and what the collector needs when it backs off inside a spin loop while the world
// is suspended. They are in GCToOSInterface.Imports.Unix.cs so that the test host can substitute
// the same private methods for ones it can call and record; see
// tests/GCToOSInterface.Imports.Unix.TestHost.cs.
//
// The constants below are the values of the <errno.h> macro and the gc/unix/globals.h time
// conversions the C++ code uses. As with the rest of this layer they are hardcoded per platform
// and checked against the real headers by static_asserts in nativeaot/Runtime/gcenv.managed.cpp,
// which is compiled for the target platform -- so a platform whose values differ from the ones
// selected here breaks the build rather than the process.

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe partial class GCToOSInterface
    {
        //
        // The time conversions of gc/unix/globals.h that Sleep uses.
        //

        /// <summary>The number of milliseconds in a second.</summary>
        private const uint tccSecondsToMilliSeconds = 1000;

        /// <summary>The number of nanoseconds in a millisecond.</summary>
        private const uint tccMilliSecondsToNanoSeconds = 1000000;

        /// <summary>
        /// <c>EINTR</c> of <c>&lt;errno.h&gt;</c>. Every C library this runtime targets on Unix
        /// gives it the historical value; Haiku, whose <c>errno</c> values are the negative
        /// <c>B_*</c> status codes, is not one of them, and the static_assert of
        /// <c>gcenv.managed.cpp</c> is what says so.
        /// </summary>
        private const int EINTR = 4;

        /// <summary>
        /// The calling thread's <c>errno</c>. The <c>&lt;errno.h&gt;</c> macro is a dereference
        /// of the accessor that returns the address of the thread's own copy, which is what the
        /// import declares; there is no other way to reach a C thread-local from here.
        /// </summary>
        private static int errno => *__errno_location();

        //
        // Thread and process
        //

        /// <summary>
        /// Causes the calling thread to sleep for the specified number of milliseconds.
        /// </summary>
        /// <param name="sleepMSec">time to sleep before switching to another thread</param>
        public static void Sleep(uint sleepMSec)
        {
            if (sleepMSec == 0)
            {
                return;
            }

            // The C++ reads requested.tv_sec back out of the structure for the nanosecond
            // remainder. Here it is kept in a local of the same value, because the width of
            // tv_sec is one of the two things about `struct timespec` that differ per platform,
            // and the arithmetic below must not depend on which of them this build selected.
            uint seconds = sleepMSec / tccSecondsToMilliSeconds;

            timespec requested = default;
            requested.tv_sec = (nint)seconds;
            requested.tv_nsec = (nint)((sleepMSec - seconds * tccSecondsToMilliSeconds) * tccMilliSecondsToNanoSeconds);

            timespec remaining = default;
            while (nanosleep(&requested, &remaining) == -1 && errno == EINTR)
            {
                requested = remaining;
            }
        }

        // switchCount is unused for the same reason the C++ parameter is: the Unix
        // implementation of the spin backoff does not vary with the number of times around the
        // loop, it always yields once. The C++ says so with UNREFERENCED_PARAMETER on Windows
        // and by simply not naming it on Unix.
#pragma warning disable IDE0060

        /// <summary>
        /// Causes the calling thread to yield execution to another thread that is ready to run
        /// on the current processor.
        /// </summary>
        /// <param name="switchCount">number of times the YieldThread was called in a loop</param>
        public static void YieldThread(uint switchCount)
        {
            int ret = sched_yield();

            // sched_yield never fails on Linux, unclear about other OSes
            Debug.Assert(ret == 0);
        }

#pragma warning restore IDE0060
    }
}
