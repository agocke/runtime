// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The libc entry points that the Unix event and lock ports call, declared exactly as
// <pthread.h> and <time.h> declare them.
//
// They are [RuntimeImport]s rather than [DllImport]s: a runtime import is a direct call to a
// linked symbol with no marshalling, no argument copying, no lazy binding step and no GC mode
// transition, which is what code that runs with the world suspended requires. Every one of them
// is a symbol the NativeAOT application already links: libc, or libpthread where the platform
// still keeps the pthread functions there.
//
// A blocking call made through one of these blocks the calling thread in libc without changing
// its GC mode, which is exactly what the C++ GC gets by being native code: GCEvent::Wait and
// CLRCriticalSection::Enter are the primitives the collector's own threads park on, so they must
// not go through a transition that would try to suspend or resume anything.
//
// This file is compiled into the shipping library only. The xUnit tests compile
// tests/SyncImports.Unix.TestHost.cs in its place, which declares the same methods as ordinary
// P/Invokes so that the ported logic above them can be exercised, and records the calls so that
// the translation can be asserted.

using System.Runtime;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>
    /// The libc surface of <see cref="GCEvent"/> and <see cref="CLRCriticalSection"/>.
    /// </summary>
    internal static unsafe partial class SyncImports
    {
        private const string RuntimeLibrary = "*";

        [RuntimeImport(RuntimeLibrary, "pthread_mutex_init")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int pthread_mutex_init(pthread_mutex_t* mutex, pthread_mutexattr_t* attr);

        [RuntimeImport(RuntimeLibrary, "pthread_mutex_destroy")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int pthread_mutex_destroy(pthread_mutex_t* mutex);

        [RuntimeImport(RuntimeLibrary, "pthread_mutex_lock")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int pthread_mutex_lock(pthread_mutex_t* mutex);

        [RuntimeImport(RuntimeLibrary, "pthread_mutex_unlock")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int pthread_mutex_unlock(pthread_mutex_t* mutex);

        [RuntimeImport(RuntimeLibrary, "pthread_mutexattr_init")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int pthread_mutexattr_init(pthread_mutexattr_t* attr);

        [RuntimeImport(RuntimeLibrary, "pthread_mutexattr_settype")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int pthread_mutexattr_settype(pthread_mutexattr_t* attr, int type);

        [RuntimeImport(RuntimeLibrary, "pthread_mutexattr_destroy")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int pthread_mutexattr_destroy(pthread_mutexattr_t* attr);

        [RuntimeImport(RuntimeLibrary, "pthread_cond_init")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int pthread_cond_init(pthread_cond_t* cond, pthread_condattr_t* attr);

        [RuntimeImport(RuntimeLibrary, "pthread_cond_destroy")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int pthread_cond_destroy(pthread_cond_t* cond);

        [RuntimeImport(RuntimeLibrary, "pthread_cond_wait")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int pthread_cond_wait(pthread_cond_t* cond, pthread_mutex_t* mutex);

        [RuntimeImport(RuntimeLibrary, "pthread_cond_broadcast")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int pthread_cond_broadcast(pthread_cond_t* cond);

        [RuntimeImport(RuntimeLibrary, "pthread_condattr_init")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int pthread_condattr_init(pthread_condattr_t* attr);

#if TARGET_APPLE
        /// <summary>
        /// The relative timed wait of <c>HAVE_CLOCK_GETTIME_NSEC_NP</c> platforms, which have no
        /// <c>CLOCK_MONOTONIC</c> to give <c>pthread_cond_timedwait</c> an absolute deadline in.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "pthread_cond_timedwait_relative_np")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int pthread_cond_timedwait_relative_np(pthread_cond_t* cond, pthread_mutex_t* mutex, timespec* reltime);

        [RuntimeImport(RuntimeLibrary, "clock_gettime_nsec_np")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern ulong clock_gettime_nsec_np(int clock_id);
#else
        [RuntimeImport(RuntimeLibrary, "pthread_cond_timedwait")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int pthread_cond_timedwait(pthread_cond_t* cond, pthread_mutex_t* mutex, timespec* abstime);

        /// <summary>
        /// <c>HAVE_PTHREAD_CONDATTR_SETCLOCK</c>, which every platform that reaches this branch
        /// has: it is what lets the timed wait below use a monotonic deadline.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "pthread_condattr_setclock")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int pthread_condattr_setclock(pthread_condattr_t* attr, int clock_id);

        [RuntimeImport(RuntimeLibrary, "clock_gettime")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int clock_gettime(int clock_id, timespec* tp);
#endif

        /// <summary>
        /// Stands in for the <c>new (nothrow)</c> that allocates a <c>GCEvent::Impl</c>, and for
        /// the storage the C++ <c>CLRCriticalSection</c> embeds by value. See
        /// <c>nativeaot/Runtime/gcenv.managed.cpp</c>.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_AllocZeroed")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void* ManagedGC_AllocZeroed(nuint size);

        /// <summary>Stands in for <c>delete</c>.</summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_Free")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void ManagedGC_Free(void* memory);
    }
}
