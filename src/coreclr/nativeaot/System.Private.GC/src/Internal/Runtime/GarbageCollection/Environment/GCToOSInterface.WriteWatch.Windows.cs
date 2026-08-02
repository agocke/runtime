// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the write watching half of gc/windows/gcenv.windows.cpp. The methods appear in the
// order the C++ file declares them, and the bodies are the same statements: the same probe
// reservation, the same reset flag, the same success test on the return value and the same
// granularity assert.
//
// The calls to the Win32 API are [RuntimeImport] declarations, which are direct calls to the
// linked symbol with no marshalling and no GC mode transition -- what the C++ GC gets for free
// by being native code, and what the collector needs while the world is suspended. Concurrent
// collection reads the write watch with the world running but from the GC's own threads, and
// software write watch reads it from the suspension path, so neither may take a transition.
// The declarations are in GCToOSInterface.Imports.Windows.cs so that the test host can
// substitute the same private methods for ones it can record; see
// tests/GCToOSInterface.Imports.Windows.TestHost.cs.
//
// The Win32 constants and the SYSTEM_INFO layout below are hardcoded, as the AsmOffsets tables
// are, and checked against <windows.h> by static_asserts in nativeaot/Runtime/gcenv.managed.cpp.

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe partial class GCToOSInterface
    {
        /// <summary>
        /// <c>WRITE_WATCH_FLAG_RESET</c> of <c>&lt;windows.h&gt;</c>, which the C++ spells as
        /// the literal 1 that <see cref="GetWriteWatch"/> passes for a resetting read.
        /// </summary>
        private const uint WRITE_WATCH_FLAG_RESET = 1;

        /// <summary><c>SYSTEM_INFO</c> of <c>&lt;windows.h&gt;</c>.</summary>
        /// <remarks>
        /// The leading <c>dwOemId</c> is a union with the two processor architecture words, and
        /// is written here as the two words because nothing reads it.
        /// </remarks>
        private struct SYSTEM_INFO
        {
            public ushort wProcessorArchitecture;
            public ushort wReserved;
            public uint dwPageSize;
            public void* lpMinimumApplicationAddress;
            public void* lpMaximumApplicationAddress;
            public nuint dwActiveProcessorMask;
            public uint dwNumberOfProcessors;
            public uint dwProcessorType;
            public uint dwAllocationGranularity;
            public ushort wProcessorLevel;
            public ushort wProcessorRevision;
        }

        /// <summary>
        /// <c>g_SystemInfo.dwAllocationGranularity</c>, the size of the probe reservation
        /// <see cref="SupportsWriteWatch"/> makes.
        /// </summary>
        /// <remarks>
        /// The C++ reads the cached <c>g_SystemInfo</c>, which <c>GCToOSInterface::Initialize</c>
        /// fills from exactly this call. The managed GC does not run that initialization --
        /// NativeAOT does, from <c>PalInit</c>, and porting it is the initialization submodule
        /// of plan step 3 in ROADMAP.md -- so the value is read at the point of use instead. It
        /// is a property of the machine and does not change over the life of a process, so the
        /// two are the same number.
        /// </remarks>
        private static nuint GetAllocationGranularity()
        {
            SYSTEM_INFO systemInfo;
            GetSystemInfo(&systemInfo);
            return systemInfo.dwAllocationGranularity;
        }

        //
        // Write watching
        //

        /// <summary>Check if the OS supports write watching.</summary>
        /// <remarks>
        /// Feature detection is a probe rather than a version test, as in the C++: a
        /// <c>MEM_WRITE_WATCH</c> reservation of one allocation granularity is attempted and
        /// released again, and the OS supports write watching if it succeeded.
        /// </remarks>
        public static bool SupportsWriteWatch()
        {
            nuint allocationGranularity = GetAllocationGranularity();

            void* mem = VirtualReserve(allocationGranularity, 0, (uint)VirtualReserveFlags.WriteWatch);
            if (mem != null)
            {
                VirtualRelease(mem, allocationGranularity);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Reset the write tracking state for the specified virtual memory range.
        /// </summary>
        /// <param name="address">starting virtual address</param>
        /// <param name="size">size of the virtual memory range</param>
        /// <remarks>
        /// <c>::ResetWriteWatch</c> returns zero on success, which the C++ ignores.
        /// </remarks>
        public static void ResetWriteWatch(void* address, nuint size)
        {
            Win32ResetWriteWatch(address, size);
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
        /// <remarks>
        /// <c>::GetWriteWatch</c> returns zero on success and a nonzero error code otherwise; it
        /// is not a <c>BOOL</c>. The count is an in/out <c>ULONG_PTR</c>, which is
        /// <see langword="nuint"/>, so the pointer cast the C++ writes has nothing to convert
        /// and is not needed here. The granularity it reports back is a 32-bit <c>ULONG</c> and
        /// is only checked.
        /// </remarks>
        public static bool GetWriteWatch(bool resetState, void* address, nuint size, void** pageAddresses, nuint* pageAddressesCount)
        {
            uint flags = resetState ? WRITE_WATCH_FLAG_RESET : 0;
            uint granularity;

            bool success = Win32GetWriteWatch(flags, address, size, pageAddresses, pageAddressesCount, &granularity) == 0;
            if (success)
            {
                Debug.Assert(granularity == OS_PAGE_SIZE);
            }

            return success;
        }
    }
}
