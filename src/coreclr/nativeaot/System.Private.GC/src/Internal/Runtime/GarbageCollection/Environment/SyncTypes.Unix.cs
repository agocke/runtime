// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The <pthread.h> and <time.h> types that the Unix event and lock ports name -- the types of
// the members of GCEvent::Impl in gc/unix/events.cpp and of minipal_mutex in
// src/native/minipal/mutex.h.
//
// A managed source file cannot include a C header, so each pthread type is an opaque blob that
// is at least as large and as strictly aligned as the real one. The blobs are deliberately
// larger than any platform needs: their contents are only ever read and written by libc, so the
// only thing the managed side must get right is that the storage is big enough, aligned, and
// laid out at the offsets the code that fills it uses. `nativeaot/Runtime/gcenv.managed.cpp`
// asserts that against the real headers of the platform being built, so a platform whose types
// no longer fit breaks the build rather than the process.
//
// `timespec` is different: its fields are read and written here, so its layout has to be exact.
// The default one -- two native-sized words -- is what every platform this runtime targets has,
// except musl, which widens `time_t` to 64 bits on 32-bit architectures and pads `tv_nsec` to
// match. The two variants are selected by the same TARGET_LINUX_MUSL that the native build
// defines, and both are asserted against <time.h> in gcenv.managed.cpp.
//
// This file is shared with the tests, unlike SyncImports.Unix.cs: the substituted P/Invokes have
// to name exactly the types the shipping code passes them.

using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>
    /// Storage for a <c>pthread_mutex_t</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct pthread_mutex_t
    {
        internal const int Words = 16;

        internal fixed ulong _blob[Words];
    }

    /// <summary>
    /// Storage for a <c>pthread_cond_t</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct pthread_cond_t
    {
        internal const int Words = 16;

        internal fixed ulong _blob[Words];
    }

    /// <summary>
    /// Storage for a <c>pthread_mutexattr_t</c>. Only ever a local, as in the C++.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct pthread_mutexattr_t
    {
        internal const int Words = 8;

        internal fixed ulong _blob[Words];
    }

    /// <summary>
    /// Storage for a <c>pthread_condattr_t</c>. Only ever a local, as in the C++.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct pthread_condattr_t
    {
        internal const int Words = 8;

        internal fixed ulong _blob[Words];
    }

    /// <summary>
    /// Port of <c>struct timespec</c> of <c>&lt;time.h&gt;</c>.
    /// </summary>
    // The C++ name is kept, as the porting contract of ROADMAP.md requires; CS8981 warns that
    // an all-lowercase type name may collide with a future language keyword.
#pragma warning disable CS8981
    [StructLayout(LayoutKind.Sequential)]
    internal struct timespec
#pragma warning restore CS8981
    {
#if TARGET_LINUX_MUSL
        // musl's time_t is 64 bits on every architecture, so on a 32-bit one the `long` tv_nsec
        // is followed by four bytes of padding -- after the value rather than before it on the
        // little-endian architectures this branch is built for, which gcenv.managed.cpp asserts
        // along with the offsets. The padding is named so that nothing here reads or writes it:
        // libc fills only the two real fields, and what it leaves in the padding is not a value.
        public long tv_sec;
        public nint tv_nsec;
#if !TARGET_64BIT
        internal int _tv_nsec_padding;
#endif
#else
        public nint tv_sec;
        public nint tv_nsec;
#endif
    }
}
