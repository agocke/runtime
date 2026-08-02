// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The process-wide memory barrier of src/native/minipal/memorybarrierprocesswide.h, which
// SoftwareWriteWatch::GetDirty (softwarewritewatch.cpp) calls directly rather than through
// GCToOSInterface, exactly as the C++ does. It is one entry point rather than a per-platform
// pair: minipal implements it once, with a signal-based fallback where the platform has no
// cheaper membarrier-style syscall.
//
// This file is compiled into the shipping library only. The xUnit tests compile
// tests/GCEnv.MemoryBarrierProcessWide.TestHost.cs in its place, which records how many times
// the port called it instead of issuing a real cross-thread barrier.

using System.Runtime;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe partial class GCEnv
    {
        [RuntimeImport(RuntimeLibrary, "minipal_memory_barrier_process_wide")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void minipal_memory_barrier_process_wide();

        /// <summary>
        /// Issues a memory barrier on all active threads of the process, as
        /// <c>minipal_memory_barrier_process_wide</c> does. Used where a per-thread barrier is
        /// not enough because the memory being ordered was last written by a different thread
        /// with no barrier of its own -- the software write watch dirty bits, in particular.
        /// </summary>
        public static void MemoryBarrierProcessWide() => minipal_memory_barrier_process_wide();
    }
}
