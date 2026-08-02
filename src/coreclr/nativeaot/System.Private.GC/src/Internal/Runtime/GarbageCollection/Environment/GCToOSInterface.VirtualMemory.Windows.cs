// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the virtual memory half of gc/windows/gcenv.windows.cpp, plus GetPageSize and the
// OS_PAGE_SIZE macro of env/gcenv.windows.inl. The methods appear in the order the C++ file
// declares them, and the bodies are the same statements: the same VirtualAlloc/VirtualFree flag
// combinations, the same NUMA-aware alternatives, the same large page privilege acquisition and
// the same rounding.
//
// The calls to the Win32 API are [RuntimeImport] declarations, which are direct calls to the
// linked symbol with no marshalling and no GC mode transition -- what the C++ GC gets for free
// by being native code, and what the collector needs while the world is suspended. They are in
// GCToOSInterface.Imports.Windows.cs so that the test host can substitute the same private
// methods for ones it can record; see tests/GCToOSInterface.Imports.Windows.TestHost.cs.
//
// The Win32 constants below are hardcoded, as the AsmOffsets tables are, and checked against
// <windows.h> by static_asserts in nativeaot/Runtime/gcenv.managed.cpp.

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe partial class GCToOSInterface
    {
        //
        // The <windows.h> constants of gc/windows/gcenv.windows.cpp.
        //

        private const uint MEM_COMMIT = 0x00001000;
        private const uint MEM_RESERVE = 0x00002000;
        private const uint MEM_DECOMMIT = 0x00004000;
        private const uint MEM_RELEASE = 0x00008000;
        private const uint MEM_RESET = 0x00080000;
        private const uint MEM_LARGE_PAGES = 0x20000000;
        private const uint MEM_WRITE_WATCH = 0x00200000;

        private const uint PAGE_READWRITE = 0x04;

        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        /// <summary><c>LUID</c> of <c>&lt;windows.h&gt;</c>.</summary>
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        /// <summary><c>LUID_AND_ATTRIBUTES</c> of <c>&lt;windows.h&gt;</c>.</summary>
        private struct LUID_AND_ATTRIBUTES
        {
            public LUID Luid;
            public uint Attributes;
        }

        /// <summary>
        /// <c>TOKEN_PRIVILEGES</c> of <c>&lt;windows.h&gt;</c>, whose trailing array has one
        /// element, which is the only size <c>InitLargePagesPrivilege</c> uses.
        /// </summary>
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID_AND_ATTRIBUTES Privileges0;
        }

        /// <summary><c>MEMORYSTATUSEX</c> of <c>&lt;windows.h&gt;</c>.</summary>
        /// <remarks>
        /// Internal rather than private only so that the test host can declare a substitute
        /// <c>GlobalMemoryStatusEx</c> that fills one in.
        /// </remarks>
        internal struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        /// <summary>
        /// <c>g_SeLockMemoryPrivilegeAcquired</c>. Explicitly initialized rather than given a
        /// field initializer, so that the type has no static constructor to run.
        /// </summary>
        private static bool g_SeLockMemoryPrivilegeAcquired;

        /// <summary>
        /// <c>OS_PAGE_SIZE</c> of <c>env/gcenv.windows.inl</c>.
        /// </summary>
        private static nuint OS_PAGE_SIZE => GetPageSize();

        /// <summary>Get the size of an OS memory page.</summary>
        /// <remarks>
        /// The C++ version calls <c>minipal_getpagesize</c>, which on Windows is an inline
        /// function returning this constant -- there is no symbol to call and the GC relies on
        /// the value folding into its alignment math.
        /// </remarks>
        public static nuint GetPageSize() => 4 * 1024;

        /// <summary><c>InitLargePagesPrivilege</c>.</summary>
        private static bool InitLargePagesPrivilege()
        {
            TOKEN_PRIVILEGES tp;
            LUID luid;

            // SE_LOCK_MEMORY_NAME, spelled out on the stack because the GC may not reference a
            // string object.
            char* seLockMemoryName = stackalloc char[]
            {
                'S', 'e', 'L', 'o', 'c', 'k', 'M', 'e', 'm', 'o', 'r', 'y', 'P', 'r', 'i', 'v',
                'i', 'l', 'e', 'g', 'e', '\0'
            };

            if (LookupPrivilegeValueW(null, seLockMemoryName, &luid) == 0)
            {
                return false;
            }

            tp.PrivilegeCount = 1;
            tp.Privileges0.Luid = luid;
            tp.Privileges0.Attributes = SE_PRIVILEGE_ENABLED;

            void* token;
            if (OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES, &token) == 0)
            {
                return false;
            }

            int retVal = AdjustTokenPrivileges(token, 0, &tp, 0, null, null);
            uint gls = GetLastError();
            CloseHandle(token);

            if (retVal == 0)
            {
                return false;
            }

            if (gls != 0)
            {
                return false;
            }

            return true;
        }

        /// <summary><c>GetProcessMemoryLoad</c>.</summary>
        private static void GetProcessMemoryLoad(MEMORYSTATUSEX* pMSEX)
        {
            pMSEX->dwLength = (uint)sizeof(MEMORYSTATUSEX);
            int fRet = GlobalMemoryStatusEx(pMSEX);
            Debug.Assert(fRet != 0);
        }

        //
        // Virtual memory management
        //

        /// <summary>
        /// Reserve virtual memory range. Returns the starting virtual address of the reserved
        /// range, or null on failure.
        /// </summary>
        /// <param name="size">size of the virtual memory range</param>
        /// <param name="alignment">requested memory alignment</param>
        /// <param name="flags">flags to control special settings like write watching</param>
        /// <param name="node">the NUMA node to reserve memory on</param>
        /// <remarks>
        /// Previous uses of this API aligned the <paramref name="size"/> parameter to the
        /// platform allocation granularity. This is not required by POSIX or Windows. Windows
        /// will round the size up to the nearest page boundary. POSIX does not specify what is
        /// done, but Linux probably also rounds up.
        /// <para>
        /// Windows guarantees that the returned mapping will be aligned to the allocation
        /// granularity.
        /// </para>
        /// </remarks>
        public static byte* VirtualReserve(nuint size, nuint alignment, uint flags, ushort node = NUMA_NODE_UNDEFINED)
        {
            // Windows already ensures 64kb alignment on VirtualAlloc. The current CLR
            // implementation ignores it on Windows, other than making some sanity checks on it.
            Debug.Assert((alignment & (alignment - 1)) == 0);
            Debug.Assert(alignment <= 0x10000);

            uint memFlags = (flags & (uint)VirtualReserveFlags.WriteWatch) != 0 ? (MEM_RESERVE | MEM_WRITE_WATCH) : MEM_RESERVE;
            if (node == NUMA_NODE_UNDEFINED)
            {
                return (byte*)VirtualAlloc(null, size, memFlags, PAGE_READWRITE);
            }
            else
            {
                return (byte*)VirtualAllocExNuma(GetCurrentProcess(), null, size, memFlags, PAGE_READWRITE, node);
            }
        }

        /// <summary>
        /// Release virtual memory range previously reserved using <see cref="VirtualReserve"/>.
        /// </summary>
        /// <param name="address">starting virtual address</param>
        /// <param name="size">size of the virtual memory range</param>
        /// <returns>true if it has succeeded, false if it has failed</returns>
        // MEM_RELEASE requires a zero size and releases the whole reservation, so the C++
        // ignores the size here.
#pragma warning disable IDE0060
        public static bool VirtualRelease(void* address, nuint size)
        {
            return VirtualFree(address, 0, MEM_RELEASE) != 0;
        }
#pragma warning restore IDE0060

        /// <summary>Reserve and commit a virtual memory range for large pages.</summary>
        /// <param name="size">size of the virtual memory range</param>
        /// <param name="node">the NUMA node to commit memory on</param>
        /// <returns>Starting virtual address of the committed range</returns>
        /// <remarks>
        /// As in the C++, the privilege is acquired at most once per process and is never given
        /// back; a failed acquisition leaves the flag clear, so the next call tries again. A
        /// reservation whose commit fails is not released, which is the C++ behavior as well.
        /// </remarks>
        public static byte* VirtualReserveAndCommitLargePages(nuint size, ushort node = NUMA_NODE_UNDEFINED)
        {
            if (!g_SeLockMemoryPrivilegeAcquired)
            {
                if (!InitLargePagesPrivilege())
                {
                    return null;
                }

                g_SeLockMemoryPrivilegeAcquired = true;
            }

            nuint largePageMinimum = GetLargePageMinimum();
            size = (size + (largePageMinimum - 1)) & ~(largePageMinimum - 1);

            if (node == NUMA_NODE_UNDEFINED)
            {
                return (byte*)VirtualAlloc(null, size, MEM_RESERVE | MEM_COMMIT | MEM_LARGE_PAGES, PAGE_READWRITE);
            }
            else
            {
                return (byte*)VirtualAllocExNuma(GetCurrentProcess(), null, size, MEM_RESERVE | MEM_COMMIT | MEM_LARGE_PAGES, PAGE_READWRITE, node);
            }
        }

        /// <summary>
        /// Commit virtual memory range. It must be part of a range reserved using
        /// <see cref="VirtualReserve"/>.
        /// </summary>
        /// <param name="address">starting virtual address</param>
        /// <param name="size">size of the virtual memory range</param>
        /// <param name="node">the NUMA node to commit memory on</param>
        /// <returns>true if it has succeeded, false if it has failed</returns>
        /// <remarks>
        /// The C++ version asserts <c>g_fEnableGCNumaAware</c> on the NUMA path. That flag is
        /// private to <c>gcenv.windows.cpp</c> and arrives with the NUMA submodule of plan step
        /// 3 in ROADMAP.md; the assert is the only part of this method that it affects.
        /// </remarks>
        public static bool VirtualCommit(void* address, nuint size, ushort node = NUMA_NODE_UNDEFINED)
        {
            if (node == NUMA_NODE_UNDEFINED)
            {
                return VirtualAlloc(address, size, MEM_COMMIT, PAGE_READWRITE) != null;
            }
            else
            {
                return VirtualAllocExNuma(GetCurrentProcess(), address, size, MEM_COMMIT, PAGE_READWRITE, node) != null;
            }
        }

        /// <summary>Decommit virtual memory range.</summary>
        /// <param name="address">starting virtual address</param>
        /// <param name="size">size of the virtual memory range</param>
        /// <returns>true if it has succeeded, false if it has failed</returns>
        public static bool VirtualDecommit(void* address, nuint size)
        {
            return VirtualFree(address, size, MEM_DECOMMIT) != 0;
        }

        /// <summary>
        /// Reset virtual memory range. Indicates that data in the memory range specified by
        /// <paramref name="address"/> and <paramref name="size"/> is no longer of interest, but
        /// it should not be decommitted.
        /// </summary>
        /// <param name="address">starting virtual address</param>
        /// <param name="size">size of the virtual memory range</param>
        /// <param name="unlock">true if the memory range should also be unlocked</param>
        /// <returns>
        /// true if it has succeeded, false if it has failed. Returns false also if unlocking was
        /// requested but the unlock failed.
        /// </returns>
        public static bool VirtualReset(void* address, nuint size, bool unlock)
        {
            bool success = VirtualAlloc(address, size, MEM_RESET, PAGE_READWRITE) != null;
            if (success && unlock)
            {
                VirtualUnlock(address, size);
            }

            return success;
        }

        //
        // Global memory info
        //

        /// <summary>
        /// Return the maximum address of the of the virtual address space of this process.
        /// </summary>
        /// <returns>non zero if it has succeeded, 0 if it has failed</returns>
        public static nuint GetVirtualMemoryMaxAddress()
        {
            // On Windows, the maximum address is the same as the virtual memory limit, unlike Unix
            return GetVirtualMemoryLimit();
        }

        /// <summary>
        /// Return the size of the available user-mode portion of the virtual address space of
        /// this process.
        /// </summary>
        /// <returns>non zero if it has succeeded, (size_t)-1 if not available</returns>
        public static nuint GetVirtualMemoryLimit()
        {
            MEMORYSTATUSEX memStatus;
            GetProcessMemoryLoad(&memStatus);
            Debug.Assert(memStatus.ullAvailVirtual != 0);
            return (nuint)memStatus.ullAvailVirtual;
        }
    }
}
