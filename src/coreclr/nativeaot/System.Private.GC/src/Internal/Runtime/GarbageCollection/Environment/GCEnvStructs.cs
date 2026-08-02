// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of gcenv.structs.h: the structures shared between the GC and its environment.

using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>
    /// Port of <c>GCSystemInfo</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GCSystemInfo
    {
        public uint dwNumberOfProcessors;
        public uint dwPageSize;
        public uint dwAllocationGranularity;
    }

    /// <summary>
    /// Port of <c>EEThreadId</c>, the identity of the thread that owns a lock.
    /// </summary>
    /// <remarks>
    /// The C++ header has two definitions of this type: a <c>pthread_t</c> plus a validity flag
    /// on Unix, and a <c>GetCurrentThreadId()</c> result on Windows. C# can hold neither a
    /// <c>pthread_t</c> nor a Windows thread id without naming a platform type, so the managed
    /// port uses one representation on both -- the OS thread id that
    /// <see cref="GCToOSInterface.GetCurrentThreadIdForLogging"/> returns, with zero standing in
    /// for the Unix validity flag. This type is only read by the debug-only lock-ownership
    /// assertions of <see cref="CrstStatic"/>, so the two are interchangeable there.
    /// </remarks>
    internal struct EEThreadId
    {
        private ulong m_uiId;

        public bool IsCurrentThread() => m_uiId != 0 && m_uiId == GCToOSInterface.GetCurrentThreadIdForLogging();

        public void SetToCurrentThread() => m_uiId = GCToOSInterface.GetCurrentThreadIdForLogging();

        public void Clear() => m_uiId = 0;
    }
}
