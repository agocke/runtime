// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the sleep and yield part of the "thread and process" section of
// gc/windows/gcenv.windows.cpp: GCToOSInterface::Sleep and GCToOSInterface::YieldThread, in the
// order the C++ file defines them, with the same statements.
//
// The calls to Win32 are [RuntimeImport] declarations, which are direct calls to the linked
// symbol with no marshalling and no GC mode transition -- what the C++ GC gets for free by being
// native code, and what the collector needs when it backs off inside a spin loop while the world
// is suspended. They are in GCToOSInterface.Imports.Windows.cs so that the test host can
// substitute the same private methods for ones it can call and record; see
// tests/GCToOSInterface.Imports.Windows.TestHost.cs.

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe partial class GCToOSInterface
    {
        //
        // Thread and process
        //

        /// <summary>
        /// Causes the calling thread to sleep for the specified number of milliseconds.
        /// </summary>
        /// <param name="sleepMSec">time to sleep before switching to another thread</param>
        public static void Sleep(uint sleepMSec)
        {
            // TODO(segilles) CLR implementation of __SwitchToThread spins for short sleep durations
            // to avoid context switches - is that interesting or useful here?
            if (sleepMSec > 0)
            {
                // The second argument is the C++ FALSE: the sleep is not alertable, so it is
                // not ended by a queued APC. A Win32 BOOL is spelled as an int here, as
                // everywhere else in this port, because there is no marshalling to convert a
                // one-byte managed bool to it.
                SleepEx(sleepMSec, 0);
            }
        }

        // switchCount is unused for the same reason the C++ parameter is, which says so with
        // UNREFERENCED_PARAMETER: the Windows implementation of the spin backoff does not vary
        // with the number of times around the loop, it always switches once.
#pragma warning disable IDE0060

        /// <summary>
        /// Causes the calling thread to yield execution to another thread that is ready to run
        /// on the current processor.
        /// </summary>
        /// <param name="switchCount">number of times the YieldThread was called in a loop</param>
        public static void YieldThread(uint switchCount)
        {
            // The BOOL that says whether there was another thread to switch to is discarded, as
            // it is by the C++ expression statement.
            SwitchToThread();
        }

#pragma warning restore IDE0060
    }
}
