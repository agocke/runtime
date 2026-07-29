// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>
    /// The slice of <c>gcenv.os.h</c>'s <c>GCToOSInterface</c> that the managed heap needs.
    /// </summary>
    /// <remarks>
    /// These are <c>[RuntimeImport]</c> calls into small shims in <c>clrgc.managed.cpp</c>,
    /// which forward to the real <c>GCToOSInterface</c>. A runtime import is a direct call to a
    /// linked symbol with no marshalling and no GC mode transition, which is what code running
    /// with the world suspended requires; a <c>[DllImport]</c> would not be usable here.
    /// Porting <c>GCToOSInterface</c> outright (plan step 3) replaces this file.
    /// </remarks>
    internal static unsafe class GCToOSInterface
    {
        private const string RuntimeLibrary = "*";

        [RuntimeImport(RuntimeLibrary, "ManagedGC_VirtualReserve")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void* ManagedGC_VirtualReserve(nuint size, nuint alignment);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_VirtualCommit")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_VirtualCommit(void* address, nuint size);

        /// <summary>
        /// Reserves a range of virtual memory. Returns null on failure.
        /// </summary>
        /// <param name="size">Number of bytes to reserve.</param>
        /// <param name="alignment">Zero requests the OS allocation granularity.</param>
        public static byte* VirtualReserve(nuint size, nuint alignment) =>
            (byte*)ManagedGC_VirtualReserve(size, alignment);

        /// <summary>
        /// Commits a range that lies inside a previous <see cref="VirtualReserve"/>. The pages
        /// read as zero, which is what lets the allocator hand out memory without clearing it.
        /// </summary>
        public static bool VirtualCommit(byte* address, nuint size) =>
            ManagedGC_VirtualCommit(address, size) != 0;
    }
}
