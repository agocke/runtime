// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the Unix half of src/native/minipal/mutex.c, which is what the CLRCriticalSection of
// gcenv.os.h forwards to: a recursive pthread mutex. The methods appear in the order the C++
// declares them and the bodies are the same statements, with the allocation of the storage the
// C++ class embeds by value added to Initialize and Destroy -- see GCEnvSync.cs for why the
// managed struct has to hold a pointer.
//
// The calls to libc are [RuntimeImport] declarations, which are direct calls to the linked
// symbol with no marshalling and no GC mode transition -- what the C++ GC gets for free by being
// native code, and what the collector needs when it takes a lock while the world is suspended.
// They are in SyncImports.Unix.cs so that the test host can substitute the same methods for ones
// it can call and record; see tests/SyncImports.Unix.TestHost.cs.
//
// PTHREAD_MUTEX_RECURSIVE is hardcoded per platform, as the AsmOffsets tables are, and checked
// against <pthread.h> by a static_assert in nativeaot/Runtime/gcenv.managed.cpp, which is
// compiled for the target platform -- so a platform whose value differs from the one selected
// here breaks the build rather than the process.
//
// The `#ifdef _DEBUG` memset of minipal_mutex_destroy has no counterpart: it poisons storage
// that the C++ caller owns and keeps, while here the same call frees it.

using System.Diagnostics;
using static Internal.Runtime.GarbageCollection.SyncImports;

namespace Internal.Runtime.GarbageCollection
{
    internal unsafe partial struct CLRCriticalSection
    {
#if TARGET_APPLE
        /// <summary><c>PTHREAD_MUTEX_RECURSIVE</c> of <c>&lt;pthread.h&gt;</c>.</summary>
        private const int PTHREAD_MUTEX_RECURSIVE = 2;
#elif TARGET_FREEBSD || TARGET_OPENBSD
        private const int PTHREAD_MUTEX_RECURSIVE = 2;
#else
        // Linux and Android share the glibc value.
        private const int PTHREAD_MUTEX_RECURSIVE = 1;
#endif

        public partial bool Initialize()
        {
            Debug.Assert(m_cs == null);
            m_cs = ManagedGC_AllocZeroed((nuint)sizeof(pthread_mutex_t));
            if (m_cs == null)
            {
                return false;
            }

            pthread_mutexattr_t mutexAttributes;
            int st = pthread_mutexattr_init(&mutexAttributes);
            if (st != 0)
            {
                ManagedGC_Free(m_cs);
                m_cs = null;
                return false;
            }

            st = pthread_mutexattr_settype(&mutexAttributes, PTHREAD_MUTEX_RECURSIVE);
            if (st == 0)
            {
                st = pthread_mutex_init((pthread_mutex_t*)m_cs, &mutexAttributes);
            }

            pthread_mutexattr_destroy(&mutexAttributes);

            if (st != 0)
            {
                ManagedGC_Free(m_cs);
                m_cs = null;
                return false;
            }

            return true;
        }

        public partial void Destroy()
        {
            Debug.Assert(m_cs != null);
            int st = pthread_mutex_destroy((pthread_mutex_t*)m_cs);
            Debug.Assert(st == 0);
            _ = st; // (void)st

            ManagedGC_Free(m_cs);
            m_cs = null;
        }

        public readonly partial void Enter()
        {
            Debug.Assert(m_cs != null);
            int st = pthread_mutex_lock((pthread_mutex_t*)m_cs);
            Debug.Assert(st == 0);
            _ = st; // (void)st
        }

        public readonly partial void Leave()
        {
            Debug.Assert(m_cs != null);
            int st = pthread_mutex_unlock((pthread_mutex_t*)m_cs);
            Debug.Assert(st == 0);
            _ = st; // (void)st
        }
    }
}
