// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if TARGET_UNIX

using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

#pragma warning disable IDE0060

namespace Internal.Runtime.GC
{
    internal unsafe partial struct GCUnixSyncImports
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Timespec
        {
            public nint tv_sec;
            public nint tv_nsec;
        }

        [RuntimeImport("*", "pthread_self")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern nuint pthread_self();

        [RuntimeImport("*", "pthread_equal")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int pthread_equal(nuint thread1, nuint thread2);

        [RuntimeImport("*", "pthread_mutexattr_init")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int pthread_mutexattr_init(byte* attributes);

        [RuntimeImport("*", "pthread_mutexattr_settype")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int pthread_mutexattr_settype(byte* attributes, int kind);

        [RuntimeImport("*", "pthread_mutexattr_destroy")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int pthread_mutexattr_destroy(byte* attributes);

        [RuntimeImport("*", "pthread_mutex_init")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int pthread_mutex_init(byte* mutex, byte* attributes);

        [RuntimeImport("*", "pthread_mutex_destroy")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int pthread_mutex_destroy(byte* mutex);

        [RuntimeImport("*", "pthread_mutex_lock")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int pthread_mutex_lock(byte* mutex);

        [RuntimeImport("*", "pthread_mutex_unlock")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int pthread_mutex_unlock(byte* mutex);

        [RuntimeImport("*", "pthread_condattr_init")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int pthread_condattr_init(byte* attributes);

        [RuntimeImport("*", "pthread_condattr_setclock")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int pthread_condattr_setclock(byte* attributes, int clockId);

        [RuntimeImport("*", "pthread_cond_init")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int pthread_cond_init(byte* condition, byte* attributes);

        [RuntimeImport("*", "pthread_cond_destroy")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int pthread_cond_destroy(byte* condition);

        [RuntimeImport("*", "pthread_cond_wait")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int pthread_cond_wait(byte* condition, byte* mutex);

        [RuntimeImport("*", "pthread_cond_timedwait")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int pthread_cond_timedwait(byte* condition, byte* mutex, Timespec* abstime);

        [RuntimeImport("*", "pthread_cond_broadcast")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int pthread_cond_broadcast(byte* condition);

        [RuntimeImport("*", "clock_gettime")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int clock_gettime(int clockId, Timespec* time);

        [RuntimeImport("*", "malloc")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern void* malloc(nuint size);

        [RuntimeImport("*", "free")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern void free(void* pointer);
    }

    internal unsafe partial struct EEThreadId
    {
        public bool IsCurrentThread()
        {
            return m_isValid && GCUnixSyncImports.pthread_equal(m_id, GCUnixSyncImports.pthread_self()) != 0;
        }

        public void SetToCurrentThread()
        {
            m_id = GCUnixSyncImports.pthread_self();
            m_isValid = true;
        }

        public void Clear()
        {
            m_isValid = false;
        }
    }

    internal unsafe partial struct CLRCriticalSection
    {
        private const int PTHREAD_MUTEX_RECURSIVE = 1;

        public bool Initialize()
        {
            fixed (ulong* mutexWords = m_cs)
            {
                uint attributes = 0;
                int status = GCUnixSyncImports.pthread_mutexattr_init((byte*)&attributes);
                if (status != 0)
                {
                    return false;
                }

                status = GCUnixSyncImports.pthread_mutexattr_settype((byte*)&attributes, PTHREAD_MUTEX_RECURSIVE);
                if (status == 0)
                {
                    status = GCUnixSyncImports.pthread_mutex_init((byte*)mutexWords, (byte*)&attributes);
                }

                GCUnixSyncImports.pthread_mutexattr_destroy((byte*)&attributes);
                return status == 0;
            }
        }

        public void Destroy()
        {
            fixed (ulong* mutexWords = m_cs)
            {
                GCUnixSyncImports.pthread_mutex_destroy((byte*)mutexWords);
            }
        }

        public void Enter()
        {
            fixed (ulong* mutexWords = m_cs)
            {
                GCUnixSyncImports.pthread_mutex_lock((byte*)mutexWords);
            }
        }

        public void Leave()
        {
            fixed (ulong* mutexWords = m_cs)
            {
                GCUnixSyncImports.pthread_mutex_unlock((byte*)mutexWords);
            }
        }
    }

    internal unsafe struct CrstStatic
    {
        private CLRCriticalSection m_cs;
#if DEBUG
        private EEThreadId m_holderThreadId;
#endif

        public void Init(int type, int flags = 0) => m_cs.Initialize();
        public void Destroy() => m_cs.Destroy();
        public void Enter()
        {
            m_cs.Enter();
#if DEBUG
            m_holderThreadId.SetToCurrentThread();
#endif
        }

        public void Leave()
        {
#if DEBUG
            m_holderThreadId.Clear();
#endif
            m_cs.Leave();
        }

#if DEBUG
        public EEThreadId GetHolderThreadId() => m_holderThreadId;
        public bool OwnedByCurrentThread() => m_holderThreadId.IsCurrentThread();
#endif
    }

    internal unsafe struct GCEventImpl
    {
        public fixed ulong condition[6];
        public fixed ulong mutex[5];
        public byte manualReset;
        public byte state;
        public byte isValid;

        public bool Initialize(bool manual, bool initialState)
        {
            manualReset = manual ? (byte)1 : (byte)0;
            state = initialState ? (byte)1 : (byte)0;
            isValid = 0;

            fixed (ulong* conditionWords = condition)
            fixed (ulong* mutexWords = mutex)
            {
                byte* conditionPointer = (byte*)conditionWords;
                byte* mutexPointer = (byte*)mutexWords;
                uint attributes = 0;
                int status = GCUnixSyncImports.pthread_condattr_init((byte*)&attributes);
                if (status != 0)
                {
                    return false;
                }

                status = GCUnixSyncImports.pthread_condattr_setclock((byte*)&attributes, 1);
                bool mutexInitialized = false;
                if (status == 0)
                {
                    status = GCUnixSyncImports.pthread_mutex_init(mutexPointer, null);
                    mutexInitialized = status == 0;
                }

                if (status == 0)
                {
                    status = GCUnixSyncImports.pthread_cond_init(conditionPointer, (byte*)&attributes);
                }

                if (status != 0)
                {
                    if (mutexInitialized)
                    {
                        GCUnixSyncImports.pthread_mutex_destroy(mutexPointer);
                    }

                    return false;
                }
            }

            isValid = 1;
            return true;
        }

        public void CloseEvent()
        {
            if (isValid != 0)
            {
                fixed (ulong* conditionWords = condition)
                fixed (ulong* mutexWords = mutex)
                {
                    byte* conditionPointer = (byte*)conditionWords;
                    byte* mutexPointer = (byte*)mutexWords;
                    GCUnixSyncImports.pthread_mutex_destroy(mutexPointer);
                    GCUnixSyncImports.pthread_cond_destroy(conditionPointer);
                }
            }
        }

        public uint Wait(uint milliseconds, bool alertable)
        {
            _ = alertable;
            GCUnixSyncImports.Timespec endTime = default;
            if (milliseconds != GCEnvironment.INFINITE)
            {
                GCUnixSyncImports.clock_gettime(1, &endTime);
                ulong nanoseconds = (ulong)milliseconds * 1000000;
                ulong totalNanoseconds = (ulong)endTime.tv_nsec + nanoseconds;
                endTime.tv_sec += (nint)(totalNanoseconds / 1000000000);
                endTime.tv_nsec = (nint)(totalNanoseconds % 1000000000);
            }

            int status;
            fixed (ulong* conditionWords = condition)
            fixed (ulong* mutexWords = mutex)
            {
                byte* conditionPointer = (byte*)conditionWords;
                byte* mutexPointer = (byte*)mutexWords;
                status = GCUnixSyncImports.pthread_mutex_lock(mutexPointer);
                if (status != 0)
                {
                    return GCEnvironment.WAIT_FAILED;
                }

                while (state == 0 && status == 0)
                {
                    status = milliseconds == GCEnvironment.INFINITE
                        ? GCUnixSyncImports.pthread_cond_wait(conditionPointer, mutexPointer)
                        : GCUnixSyncImports.pthread_cond_timedwait(conditionPointer, mutexPointer, &endTime);
                }

                if (status == 0 && manualReset == 0)
                {
                    state = 0;
                }

                GCUnixSyncImports.pthread_mutex_unlock(mutexPointer);
            }

            return status == 0
                ? GCEnvironment.WAIT_OBJECT_0
                : status == 110 ? GCEnvironment.WAIT_TIMEOUT : GCEnvironment.WAIT_FAILED;
        }

        public void Set()
        {
            fixed (ulong* conditionWords = condition)
            fixed (ulong* mutexWords = mutex)
            {
                byte* conditionPointer = (byte*)conditionWords;
                byte* mutexPointer = (byte*)mutexWords;
                GCUnixSyncImports.pthread_mutex_lock(mutexPointer);
                state = 1;
                GCUnixSyncImports.pthread_cond_broadcast(conditionPointer);
                GCUnixSyncImports.pthread_mutex_unlock(mutexPointer);
            }
        }

        public void Reset()
        {
            fixed (ulong* mutexWords = mutex)
            {
                byte* mutexPointer = (byte*)mutexWords;
                GCUnixSyncImports.pthread_mutex_lock(mutexPointer);
                state = 0;
                GCUnixSyncImports.pthread_mutex_unlock(mutexPointer);
            }
        }
    }

    internal unsafe partial struct GCEvent
    {
        public bool IsValid() => m_impl is not null;

        public bool CreateManualEventNoThrow(bool initialState) => CreateEvent(initialState, true);
        public bool CreateAutoEventNoThrow(bool initialState) => CreateEvent(initialState, false);
        public bool CreateOSManualEventNoThrow(bool initialState) => CreateEvent(initialState, true);
        public bool CreateOSAutoEventNoThrow(bool initialState) => CreateEvent(initialState, false);

        public void CloseEvent()
        {
            ((GCEventImpl*)m_impl)->CloseEvent();
        }

        public void Set() => ((GCEventImpl*)m_impl)->Set();
        public void Reset() => ((GCEventImpl*)m_impl)->Reset();
        public uint Wait(uint timeout, bool alertable) => ((GCEventImpl*)m_impl)->Wait(timeout, alertable);

        private bool CreateEvent(bool initialState, bool manual)
        {
            GCEventImpl* implementation = (GCEventImpl*)GCUnixSyncImports.malloc((nuint)sizeof(GCEventImpl));
            if (implementation is null)
            {
                return false;
            }

            if (!implementation->Initialize(manual, initialState))
            {
                GCUnixSyncImports.free(implementation);
                return false;
            }

            m_impl = implementation;
            return true;
        }
    }

    internal unsafe struct CrstHolder
    {
        private CrstStatic* m_pLock;
        public CrstHolder(CrstStatic* lockPointer) { m_pLock = lockPointer; m_pLock->Enter(); }
        public void Dispose() => m_pLock->Leave();
    }

    internal unsafe struct CrstHolderWithState
    {
        private CrstStatic* m_pLock;
        private bool m_fAcquired;
        public CrstHolderWithState(CrstStatic* lockPointer, bool acquire = true)
        {
            m_pLock = lockPointer;
            m_fAcquired = acquire;
            if (acquire) m_pLock->Enter();
        }
        public void Dispose() { if (m_fAcquired) m_pLock->Leave(); }
        public void Acquire() { if (!m_fAcquired) { m_pLock->Enter(); m_fAcquired = true; } }
        public void Release() { if (m_fAcquired) { m_pLock->Leave(); m_fAcquired = false; } }
        public CrstStatic* GetValue() => m_pLock;
    }
}

#endif
