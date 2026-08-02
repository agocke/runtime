// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The libc entry points that the Unix virtual memory port of GCToOSInterface calls, declared
// exactly as <sys/mman.h> and <sys/resource.h> declare them.
//
// They are [RuntimeImport]s rather than [DllImport]s: a runtime import is a direct call to a
// linked symbol with no marshalling, no argument copying, no lazy binding step and no GC mode
// transition, which is what code that runs with the world suspended requires. Every one of them
// is a symbol the NativeAOT application already links -- libc, or the aotminipal static library
// that Microsoft.NETCore.Native.*.targets adds to every link.
//
// This file is compiled into the shipping library only. The xUnit tests compile
// tests/GCToOSInterface.Imports.Unix.TestHost.cs in its place, which declares the same methods
// as ordinary P/Invokes so that the ported logic above them can be exercised, and records their
// arguments so that the flag translation can be asserted.

using System.Runtime;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe partial class GCToOSInterface
    {
        [RuntimeImport(RuntimeLibrary, "mmap")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void* mmap(void* addr, nuint length, int prot, int flags, int fd, nint offset);

        [RuntimeImport(RuntimeLibrary, "munmap")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int munmap(void* addr, nuint length);

        [RuntimeImport(RuntimeLibrary, "mprotect")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int mprotect(void* addr, nuint len, int prot);

        [RuntimeImport(RuntimeLibrary, "madvise")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int madvise(void* addr, nuint length, int advice);

        [RuntimeImport(RuntimeLibrary, "getrlimit")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int getrlimit(int resource, Rlimit* rlim);

        /// <summary>
        /// The shared page size helper of <c>src/native/minipal/ospagesize.h</c>, which caches
        /// one <c>sysconf(_SC_PAGESIZE)</c> per process. The C++ <c>GetPageSize</c> uses it
        /// directly on Windows and WASM, and on Unix it reads the copy of the same value that
        /// <c>GCToOSInterface::Initialize</c> caches.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "minipal_getpagesize")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern uint minipal_getpagesize();

        /// <summary>
        /// The NUMA half of <c>VirtualCommitInner</c>: places the range on the requested node
        /// with <c>mbind</c> when the process has NUMA support.
        /// </summary>
        /// <remarks>
        /// The only part of the virtual memory submodule that is still native. It depends on
        /// <c>g_numaAvailable</c>, <c>g_highestNumaNode</c> and <c>BindMemoryPolicy</c> of
        /// <c>gc/unix/numasupport.cpp</c>, and is deleted together with the NUMA submodule of
        /// plan step 3 in ROADMAP.md.
        /// </remarks>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_NUMA_BindMemoryPolicy")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_NUMA_BindMemoryPolicy(void* address, nuint size, ushort node);
    }
}
