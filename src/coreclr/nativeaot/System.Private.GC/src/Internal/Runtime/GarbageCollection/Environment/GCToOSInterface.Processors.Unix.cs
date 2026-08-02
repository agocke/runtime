// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the processor count and identity methods of gc/unix/gcenv.unix.cpp:
// GCToOSInterface::GetCurrentThreadIdForLogging, GCToOSInterface::GetCurrentProcessId,
// GCToOSInterface::GetCurrentProcessorNumber and GCToOSInterface::CanGetCurrentProcessorNumber
// from the "thread and process" section, and GCToOSInterface::GetTotalProcessorCount and
// GCToOSInterface::GetMaxProcessorCount from the end of the file, in the order the C++ file
// defines them, with the same statements.
//
// SetCurrentThreadIdealAffinity sits between the two halves of the first group in the C++ and is
// deliberately not here: it belongs with the rest of the affinity and CPU group work, which is a
// submodule of its own. Its declaration is still a forwarder in GCToOSInterface.cs.
//
// HAVE_SCHED_GETCPU is a configure check -- gc/unix/configure.cmake compiles and runs a program
// that calls sched_getcpu(), and gc/unix/config.gc.h.in turns the result into a 0 or a 1. It has
// no managed spelling, so the platforms it holds on are named directly, fail-closed: sched_getcpu
// is a Linux extension, so it is present on glibc, musl and bionic, and absent on Apple, FreeBSD
// and OpenBSD. eng/native/tryrun.cmake says the same thing for the cross builds, forcing the
// check to fail for Darwin and letting it succeed for every other Unix; on FreeBSD and OpenBSD
// the check fails because <sched.h> there has no such declaration to compile against. What this
// file selects is checked against the real HAVE_SCHED_GETCPU of the native build by
// nativeaot/Runtime/gcenv.managed.cpp, so a platform whose configure result disagrees with the
// list here breaks the build rather than silently taking the wrong branch.
//
// Two pieces of state stay in the C++ for now, and this file reaches them through the narrowest
// shims that can express them:
//
//   * g_totalCpuCount, which GCToOSInterface::Initialize computes with sysconf(_SC_NPROCESSORS_ONLN)
//     -- ManagedGC_Unix_GetTotalCpuCount reports its value.
//   * g_processAffinitySet, which the same Initialize fills in -- ManagedGC_Unix_GetProcessAffinitySet
//     reports its address, and the counting is the translated AffinitySet of AffinitySet.cs.
//
// Both disappear when GCToOSInterface::Initialize and the affinity submodule are translated and
// System.Private.GC owns the two variables; see plan step 3 of ROADMAP.md. Recomputing either
// here instead would give the managed GC a second copy with a different lifetime than the one
// the rest of the runtime reads, which is exactly what the shims avoid.
//
// minipal_get_current_thread_id is the third dependency, and it is not a linkable symbol at all:
// src/native/minipal/thread.h defines it as a static inline function over a _Thread_local cache,
// so unlike the minipal timer entry points the timers port calls, there is nothing to
// [RuntimeImport]. Translating it would need thread local storage in the GC, which needs a class
// constructor, which the collector cannot have. ManagedGC_Unix_GetCurrentThreadId stands in for
// it; deletion point: when minipal exports a non-inline entry point, or when the GC has thread
// local storage of its own.
//
// The imports are [RuntimeImport]s rather than [DllImport]s: a runtime import is a direct call to
// the linked symbol with no marshalling and no GC mode transition, which is what code that runs
// with the world suspended requires. They are declared in GCToOSInterface.Imports.Unix.cs so that
// the test host can substitute the same private methods; see
// tests/GCToOSInterface.Imports.Unix.TestHost.cs.
//
// The C++ carries the message of the impossible case in the assert condition, as
// `assert(false)` with the explanation on the same line. This library keeps the explanation as a
// comment rather than passing it to Debug.Fail, because every Debug.Fail overload takes a
// message and a string literal in the GC is a heap object; Debug.Assert(bool) is
// [Conditional("DEBUG")], so neither the compare nor the branch reaches a release build, which
// is what the C++ NDEBUG build does with assert.

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe partial class GCToOSInterface
    {
        //
        // Thread and process
        //

        /// <summary>
        /// Get numeric id of the current thread if possible on the current platform. It is
        /// intended for logging purposes only.
        /// </summary>
        /// <returns>Numeric id of the current thread, as best we can retrieve it.</returns>
        public static ulong GetCurrentThreadIdForLogging()
        {
            return (ulong)ManagedGC_Unix_GetCurrentThreadId();
        }

        /// <summary>Get the process ID of the process.</summary>
        public static uint GetCurrentProcessId()
        {
            return (uint)getpid();
        }

        /// <summary>Get the number of the current processor.</summary>
        public static uint GetCurrentProcessorNumber()
        {
#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD // HAVE_SCHED_GETCPU
            int processorNumber = sched_getcpu();
            Debug.Assert(processorNumber != -1);
            return (uint)processorNumber;
#else
            // This method is expected to be called only if CanGetCurrentProcessorNumber is true
            Debug.Assert(false);
            return 0;
#endif
        }

        /// <summary>Check if the OS supports getting current processor number.</summary>
        public static bool CanGetCurrentProcessorNumber()
        {
            // return HAVE_SCHED_GETCPU;
#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD
            return true;
#else
            return false;
#endif
        }

        //
        // Processor counts
        //

        /// <summary>
        /// Gets the total number of processors on the machine, not taking into account current
        /// process affinity.
        /// </summary>
        /// <returns>Number of processors on the machine.</returns>
        public static uint GetTotalProcessorCount()
        {
            // Calculated in GCToOSInterface::Initialize using
            // sysconf(_SC_NPROCESSORS_ONLN)
            return ManagedGC_Unix_GetTotalCpuCount();
        }

        /// <summary>
        /// Gets the maximum number of processors that could potentially exist on the machine
        /// (including offlined ones).
        /// </summary>
        public static uint GetMaxProcessorCount()
        {
            return (uint)ManagedGC_Unix_GetProcessAffinitySet()->MaxCpuCount();
        }
    }
}
