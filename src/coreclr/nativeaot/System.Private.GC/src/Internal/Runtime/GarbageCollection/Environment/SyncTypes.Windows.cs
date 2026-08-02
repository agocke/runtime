// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The <windows.h> types that the Windows event and lock ports name -- the type of the member of
// minipal_mutex in src/native/minipal/mutex.h. The Win32 GCEvent::Impl of
// gc/windows/gcenv.windows.cpp holds only a HANDLE, which is a void*, so it needs nothing here.
//
// A managed source file cannot include a C header, so CRITICAL_SECTION is an opaque blob that is
// at least as large and as strictly aligned as the real one; only the Win32 functions ever read
// or write it. `nativeaot/Runtime/gcenv.managed.cpp` asserts the size and alignment against
// <windows.h> for the platform being built, so a platform whose type no longer fits breaks the
// build rather than the process.
//
// This file is shared with the tests, unlike SyncImports.Windows.cs: the substituted P/Invokes
// have to name exactly the types the shipping code passes them.

using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>
    /// Storage for a <c>CRITICAL_SECTION</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct CRITICAL_SECTION
    {
        internal const int Words = 8;

        internal fixed ulong _blob[Words];
    }
}
