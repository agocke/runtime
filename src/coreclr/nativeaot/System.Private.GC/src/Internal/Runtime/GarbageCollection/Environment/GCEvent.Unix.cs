// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of gc/unix/events.cpp: the condition-variable implementation of GCEvent. The members of
// GCEvent::Impl appear in the order the C++ class declares them, the methods in the order the
// C++ file defines them, and the bodies are the same statements.
//
// The calls to libc are [RuntimeImport] declarations, which are direct calls to the linked
// symbol with no marshalling and no GC mode transition -- what the C++ GC gets for free by being
// native code, and what the collector needs when it parks its own threads on an event while the
// world is suspended. They are in SyncImports.Unix.cs so that the test host can substitute the
// same methods for ones it can call and record; see tests/SyncImports.Unix.TestHost.cs.
//
// The constants below are the values of the <pthread.h>, <time.h> and <errno.h> macros the C++
// code uses. They are hardcoded per platform, as the AsmOffsets tables are, and checked against
// the real headers by static_asserts in nativeaot/Runtime/gcenv.managed.cpp, which is compiled
// for the target platform -- so a platform whose values differ from the ones selected here
// breaks the build rather than the process. The #if structure and the static_asserts must be
// kept in the same shape.
//
// The C++ selects its wait between two config.gc.h probes: HAVE_CLOCK_GETTIME_NSEC_NP, which is
// the Apple platforms, and HAVE_PTHREAD_CONDATTR_SETCLOCK, which is everything else this runtime
// targets. TARGET_APPLE stands for the first and its absence for the second; a platform with
// neither is the `#error "Don't know how to perform timed wait on this platform"` of the C++,
// and is caught by the same static_asserts.
//
// One deviation the language forces: GCEvent::Impl is heap-allocated with `new (nothrow)` in
// C++. The managed GC has no allocator of its own, so the storage comes from the same nothrow
// native allocation shim AffinitySet uses. Everything else about the object -- its size, its
// members, when it is created, when it is freed -- is the C++ behavior, including the fact that
// CloseEvent destroys the mutex and the condition variable but does not free the Impl and does
// not clear m_impl: the C++ leaves both alone, so IsValid() keeps reporting true afterwards.

using System.Diagnostics;
using static Internal.Runtime.GarbageCollection.SyncImports;

namespace Internal.Runtime.GarbageCollection
{
    internal unsafe partial struct GCEvent
    {
        //
        // The <time.h>, <pthread.h> and <errno.h> constants of gc/unix/events.cpp, plus the two
        // time conversions of gc/unix/globals.h that it uses.
        //

        /// <summary>The number of nanoseconds in a second.</summary>
        private const ulong tccSecondsToNanoSeconds = 1000000000;

        /// <summary>The number of nanoseconds in a millisecond.</summary>
        private const ulong tccMilliSecondsToNanoSeconds = 1000000;

#if TARGET_APPLE
        /// <summary><c>CLOCK_UPTIME_RAW</c> of <c>&lt;time.h&gt;</c>.</summary>
        private const int CLOCK_UPTIME_RAW = 8;

        /// <summary><c>ETIMEDOUT</c> of <c>&lt;errno.h&gt;</c>.</summary>
        private const int ETIMEDOUT = 60;
#elif TARGET_FREEBSD
        private const int CLOCK_MONOTONIC = 4;
        private const int ETIMEDOUT = 60;
#elif TARGET_OPENBSD
        private const int CLOCK_MONOTONIC = 3;
        private const int ETIMEDOUT = 60;
#else
        // Linux and Android share the asm-generic values.
        private const int CLOCK_MONOTONIC = 1;
        private const int ETIMEDOUT = 110;
#endif

        //
        // The anonymous namespace of gc/unix/events.cpp.
        //

#if !TARGET_APPLE
        private static void TimeSpecAdd(timespec* time, uint milliseconds)
        {
            ulong nsec = (ulong)time->tv_nsec + (ulong)milliseconds * tccMilliSecondsToNanoSeconds;
            if (nsec >= tccSecondsToNanoSeconds)
            {
                time->tv_sec += (nint)(nsec / tccSecondsToNanoSeconds);
                nsec %= tccSecondsToNanoSeconds;
            }

            time->tv_nsec = (nint)nsec;
        }
#else
        /// <summary>
        /// Convert nanoseconds to the timespec structure.
        /// </summary>
        /// <param name="nanoseconds">time in nanoseconds to convert</param>
        /// <param name="t">the target timespec structure</param>
        private static void NanosecondsToTimeSpec(ulong nanoseconds, timespec* t)
        {
            t->tv_sec = (nint)(nanoseconds / tccSecondsToNanoSeconds);
            t->tv_nsec = (nint)(nanoseconds % tccSecondsToNanoSeconds);
        }
#endif

        /// <summary>
        /// Port of <c>GCEvent::Impl</c>: a condition variable, the mutex that guards its
        /// predicate, and the event state itself.
        /// </summary>
        private struct Impl
        {
            private pthread_cond_t m_condition;
            private pthread_mutex_t m_mutex;
            private bool m_manualReset;
            private bool m_state;
            private bool m_isValid;

            /// <summary>
            /// Stands in for the C++ constructor, which a struct in freshly allocated storage
            /// cannot have.
            /// </summary>
            public void Construct(bool manualReset, bool initialState)
            {
                m_manualReset = manualReset;
                m_state = initialState;
                m_isValid = false;
            }

            public bool Initialize()
            {
                pthread_condattr_t attrs;
                int st = pthread_condattr_init(&attrs);
                if (st != 0)
                {
                    // Failed to initialize UnixEvent condition attribute
                    Debug.Assert(false);
                    return false;
                }

                // TODO(segilles) implement this for CoreCLR
                //PthreadCondAttrHolder attrsHolder(&attrs);

#if !TARGET_APPLE
                // Ensure that the pthread_cond_timedwait will use CLOCK_MONOTONIC
                st = pthread_condattr_setclock(&attrs, CLOCK_MONOTONIC);
                if (st != 0)
                {
                    // Failed to set UnixEvent condition variable wait clock
                    Debug.Assert(false);
                    return false;
                }
#endif

                fixed (Impl* self = &this)
                {
                    st = pthread_mutex_init(&self->m_mutex, null);
                    if (st != 0)
                    {
                        // Failed to initialize UnixEvent mutex
                        Debug.Assert(false);
                        return false;
                    }

                    st = pthread_cond_init(&self->m_condition, &attrs);
                    if (st != 0)
                    {
                        // Failed to initialize UnixEvent condition variable
                        Debug.Assert(false);

                        st = pthread_mutex_destroy(&self->m_mutex);
                        // Failed to destroy UnixEvent mutex
                        Debug.Assert(st == 0);
                        return false;
                    }

                    self->m_isValid = true;
                }

                return true;
            }

            public void CloseEvent()
            {
                if (m_isValid)
                {
                    fixed (Impl* self = &this)
                    {
                        int st = pthread_mutex_destroy(&self->m_mutex);
                        // Failed to destroy UnixEvent mutex
                        Debug.Assert(st == 0);

                        st = pthread_cond_destroy(&self->m_condition);
                        // Failed to destroy UnixEvent condition variable
                        Debug.Assert(st == 0);
                    }
                }
            }

            public uint Wait(uint milliseconds, bool alertable)
            {
                _ = alertable; // UNREFERENCED_PARAMETER(alertable)

                timespec endTime = default;
#if TARGET_APPLE
                ulong endMachTime = 0;
                if (milliseconds != GCEnv.INFINITE)
                {
                    ulong nanoseconds = (ulong)milliseconds * tccMilliSecondsToNanoSeconds;
                    NanosecondsToTimeSpec(nanoseconds, &endTime);
                    endMachTime = clock_gettime_nsec_np(CLOCK_UPTIME_RAW) + nanoseconds;
                }
#else
                if (milliseconds != GCEnv.INFINITE)
                {
                    clock_gettime(CLOCK_MONOTONIC, &endTime);
                    TimeSpecAdd(&endTime, milliseconds);
                }
#endif

                int st = 0;

                fixed (Impl* self = &this)
                {
                    pthread_mutex_lock(&self->m_mutex);
                    while (!self->m_state)
                    {
                        if (milliseconds == GCEnv.INFINITE)
                        {
                            st = pthread_cond_wait(&self->m_condition, &self->m_mutex);
                        }
                        else
                        {
#if TARGET_APPLE
                            // Since OSX doesn't support CLOCK_MONOTONIC, we use relative variant
                            // of the timed wait and we need to handle spurious wakeups properly.
                            st = pthread_cond_timedwait_relative_np(&self->m_condition, &self->m_mutex, &endTime);
                            if ((st == 0) && !self->m_state)
                            {
                                ulong machTime = clock_gettime_nsec_np(CLOCK_UPTIME_RAW);
                                if (machTime < endMachTime)
                                {
                                    // The wake up was spurious, recalculate the relative endTime
                                    ulong remainingNanoseconds = endMachTime - machTime;
                                    NanosecondsToTimeSpec(remainingNanoseconds, &endTime);
                                }
                                else
                                {
                                    // Although the timed wait didn't report a timeout, time
                                    // calculated from the mach time shows we have already reached
                                    // the end time. It can happen if the wait was spuriously woken
                                    // up right before the timeout.
                                    st = ETIMEDOUT;
                                }
                            }
#else
                            st = pthread_cond_timedwait(&self->m_condition, &self->m_mutex, &endTime);
#endif
                        }

                        if (st != 0)
                        {
                            // wait failed or timed out
                            break;
                        }
                    }

                    if ((st == 0) && !self->m_manualReset)
                    {
                        // Clear the state for auto-reset events so that only one waiter gets
                        // released
                        self->m_state = false;
                    }

                    pthread_mutex_unlock(&self->m_mutex);
                }

                uint waitStatus;

                if (st == 0)
                {
                    waitStatus = GCEnv.WAIT_OBJECT_0;
                }
                else if (st == ETIMEDOUT)
                {
                    waitStatus = GCEnv.WAIT_TIMEOUT;
                }
                else
                {
                    waitStatus = GCEnv.WAIT_FAILED;
                }

                return waitStatus;
            }

            public void Set()
            {
                fixed (Impl* self = &this)
                {
                    pthread_mutex_lock(&self->m_mutex);
                    self->m_state = true;
                    // Unblock all threads waiting for the condition variable
                    pthread_cond_broadcast(&self->m_condition);
                    pthread_mutex_unlock(&self->m_mutex);
                }
            }

            public void Reset()
            {
                fixed (Impl* self = &this)
                {
                    pthread_mutex_lock(&self->m_mutex);
                    self->m_state = false;
                    pthread_mutex_unlock(&self->m_mutex);
                }
            }
        }

        public partial void CloseEvent()
        {
            Debug.Assert(m_impl != null);
            ((Impl*)m_impl)->CloseEvent();
        }

        public partial void Set()
        {
            Debug.Assert(m_impl != null);
            ((Impl*)m_impl)->Set();
        }

        public partial void Reset()
        {
            Debug.Assert(m_impl != null);
            ((Impl*)m_impl)->Reset();
        }

        public partial uint Wait(uint timeout, bool alertable)
        {
            Debug.Assert(m_impl != null);
            return ((Impl*)m_impl)->Wait(timeout, alertable);
        }

        public partial bool CreateAutoEventNoThrow(bool initialState)
        {
            // This implementation of GCEvent makes no distinction between
            // host-aware and non-host-aware events (since there will be no host).
            return CreateOSAutoEventNoThrow(initialState);
        }

        public partial bool CreateManualEventNoThrow(bool initialState)
        {
            // This implementation of GCEvent makes no distinction between
            // host-aware and non-host-aware events (since there will be no host).
            return CreateOSManualEventNoThrow(initialState);
        }

        public partial bool CreateOSAutoEventNoThrow(bool initialState)
        {
            Debug.Assert(m_impl == null);
            Impl* @event = (Impl*)ManagedGC_AllocZeroed((nuint)sizeof(Impl));
            if (@event == null)
            {
                return false;
            }

            @event->Construct(false, initialState);

            if (!@event->Initialize())
            {
                ManagedGC_Free(@event);
                return false;
            }

            m_impl = @event;
            return true;
        }

        public partial bool CreateOSManualEventNoThrow(bool initialState)
        {
            Debug.Assert(m_impl == null);
            Impl* @event = (Impl*)ManagedGC_AllocZeroed((nuint)sizeof(Impl));
            if (@event == null)
            {
                return false;
            }

            @event->Construct(true, initialState);

            if (!@event->Initialize())
            {
                ManagedGC_Free(@event);
                return false;
            }

            m_impl = @event;
            return true;
        }
    }
}
