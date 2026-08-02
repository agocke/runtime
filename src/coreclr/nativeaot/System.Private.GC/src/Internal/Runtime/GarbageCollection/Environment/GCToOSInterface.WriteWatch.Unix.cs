// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the write watching half of gc/unix/gcenv.unix.cpp. Unix has no write watch: there is
// no mprotect-free way to be told which pages a mutator wrote, so SupportsWriteWatch reports
// false and the collector uses software write watch instead, which is why the other two are
// never reached. They keep the C++ shape exactly -- an assert that fires if anything does reach
// them, and false from the one that returns a value -- rather than throwing, because the GC
// cannot throw and because a release build must behave as the C++ release build does.

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe partial class GCToOSInterface
    {
        //
        // Write watching
        //

        /// <summary>Check if the OS supports write watching.</summary>
        public static bool SupportsWriteWatch()
        {
            return false;
        }

        // The parameters are unused for the same reason the C++ ones are: these methods only
        // assert. The C++ asserts carry their message in the condition, as
        // `assert(!"should never call ResetWriteWatch on Unix")`; this library drops the message
        // strings, because a string literal in the GC is a heap object the GC would have to
        // allocate to report the failure.
#pragma warning disable IDE0060

        /// <summary>
        /// Reset the write tracking state for the specified virtual memory range.
        /// </summary>
        /// <param name="address">starting virtual address</param>
        /// <param name="size">size of the virtual memory range</param>
        public static void ResetWriteWatch(void* address, nuint size)
        {
            // should never call ResetWriteWatch on Unix
            Debug.Assert(false);
        }

        /// <summary>
        /// Retrieve addresses of the pages that are written to in a region of virtual memory.
        /// </summary>
        /// <param name="resetState">true indicates to reset the write tracking state</param>
        /// <param name="address">starting virtual address</param>
        /// <param name="size">size of the virtual memory range</param>
        /// <param name="pageAddresses">
        /// buffer that receives an array of page addresses in the memory region
        /// </param>
        /// <param name="pageAddressesCount">
        /// on input, size of the <paramref name="pageAddresses"/> array, in array elements; on
        /// output, the number of page addresses that are returned in the array
        /// </param>
        /// <returns>true if it has succeeded, false if it has failed</returns>
        public static bool GetWriteWatch(bool resetState, void* address, nuint size, void** pageAddresses, nuint* pageAddressesCount)
        {
            // should never call GetWriteWatch on Unix
            Debug.Assert(false);
            return false;
        }

#pragma warning restore IDE0060
    }
}
