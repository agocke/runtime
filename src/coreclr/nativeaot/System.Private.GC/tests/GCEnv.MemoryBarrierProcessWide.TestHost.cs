// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Test-host substitute for src/.../Environment/GCEnv.MemoryBarrierProcessWide.cs.
//
// minipal_memory_barrier_process_wide is a real cross-thread barrier in the shipping build; a
// test process has no use for one and no [RuntimeImport] to resolve, so this file stands in for
// it the same way GCToOSInterface.Imports.*.TestHost.cs stands in for libc/Win32: it declares
// the same public method the port calls, and records how many times it was called instead of
// performing the operation.

namespace Internal.Runtime.GarbageCollection;

internal static unsafe partial class GCEnv
{
    internal static int MemoryBarrierProcessWideCallCount { get; private set; }

    internal static void ResetMemoryBarrierProcessWideRecording()
    {
        MemoryBarrierProcessWideCallCount = 0;
    }

    public static void MemoryBarrierProcessWide()
    {
        MemoryBarrierProcessWideCallCount++;
    }
}
