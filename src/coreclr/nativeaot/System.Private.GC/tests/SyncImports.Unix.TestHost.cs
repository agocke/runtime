// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Test-host substitute for src/.../Environment/SyncImports.Unix.cs.
//
// The shipping declarations are [RuntimeImport]s, which only resolve inside a NativeAOT image,
// so this file declares the same methods as ordinary P/Invokes. That makes the ported bodies
// above them -- the condition-variable protocol of GCEvent::Impl and the recursive mutex of
// minipal_mutex -- runnable in a normal test process against the real pthreads, and it records
// the calls so that the translation can be asserted directly rather than inferred: that Set
// broadcasts under the mutex, that Reset does not, that an auto-reset event clears its state in
// the waiter, that a timed wait computes an absolute monotonic deadline.
//
// A [DllImport] is exactly what the GC must not use; it is fine here because this file is never
// compiled into the GC. The methods it replaces are the boundary of the port: everything the
// tests exercise above them is the shipping code, including the opaque pthread types of
// SyncTypes.Unix.cs, which is compiled from the shipping source.
//
// The "libc" resolver these imports need is registered by
// GCToOSInterface.Imports.Unix.TestHost.cs; only one DllImport resolver may be registered per
// assembly, so this file must not register another.

using System.Runtime.InteropServices;
using System.Threading;

namespace Internal.Runtime.GarbageCollection;

internal static unsafe partial class SyncImports
{
    /// <summary>
    /// Counts of the libc calls the ports make. They are incremented from several threads at
    /// once by the stress tests, so they are only ever touched with interlocked operations.
    /// </summary>
    internal static int MutexInitCount;
    internal static int MutexDestroyCount;
    internal static int MutexLockCount;
    internal static int MutexUnlockCount;
    internal static int CondInitCount;
    internal static int CondDestroyCount;
    internal static int CondWaitCount;
    internal static int CondTimedWaitCount;
    internal static int CondBroadcastCount;
    internal static int AllocCount;
    internal static int FreeCount;
    internal static nuint LastAllocSize;

    /// <summary>The mutex type the last <c>pthread_mutexattr_settype</c> asked for.</summary>
    internal static int LastMutexAttrType;

    /// <summary>The clock the last <c>pthread_condattr_setclock</c> asked for.</summary>
    internal static int LastCondAttrClock;

    /// <summary>The <c>clock_gettime</c> clock the last timed wait computed its deadline in.</summary>
    internal static int LastClockGetTimeClock;

    /// <summary>The deadline the last timed wait passed to <c>pthread_cond_timedwait</c>.</summary>
    internal static timespec LastTimedWaitDeadline;

    /// <summary>
    /// When non-zero, the next call to <c>pthread_mutex_init</c> fails with this errno, so that
    /// the failure paths of the ports can be reached without breaking the process.
    /// </summary>
    internal static int FailNextMutexInit;

    /// <summary>As <see cref="FailNextMutexInit"/>, for <c>pthread_cond_init</c>.</summary>
    internal static int FailNextCondInit;

    /// <summary>When true, <c>ManagedGC_AllocZeroed</c> returns null once.</summary>
    internal static bool FailNextAlloc;

    /// <summary>When positive, fail that numbered subsequent allocation.</summary>
    internal static int FailAllocOnCall;

    /// <summary>Forgets every recorded call. Each test starts by calling this.</summary>
    internal static void ResetRecording()
    {
        MutexInitCount = 0;
        MutexDestroyCount = 0;
        MutexLockCount = 0;
        MutexUnlockCount = 0;
        CondInitCount = 0;
        CondDestroyCount = 0;
        CondWaitCount = 0;
        CondTimedWaitCount = 0;
        CondBroadcastCount = 0;
        AllocCount = 0;
        FreeCount = 0;
        LastAllocSize = 0;
        LastMutexAttrType = 0;
        LastCondAttrClock = 0;
        LastClockGetTimeClock = 0;
        LastTimedWaitDeadline = default;
        FailNextMutexInit = 0;
        FailNextCondInit = 0;
        FailNextAlloc = false;
        FailAllocOnCall = 0;
    }

    public static int pthread_mutex_init(pthread_mutex_t* mutex, pthread_mutexattr_t* attr)
    {
        Interlocked.Increment(ref MutexInitCount);
        int injected = Interlocked.Exchange(ref FailNextMutexInit, 0);
        if (injected != 0)
        {
            return injected;
        }

        return sys_pthread_mutex_init(mutex, attr);
    }

    public static int pthread_mutex_destroy(pthread_mutex_t* mutex)
    {
        Interlocked.Increment(ref MutexDestroyCount);
        return sys_pthread_mutex_destroy(mutex);
    }

    public static int pthread_mutex_lock(pthread_mutex_t* mutex)
    {
        Interlocked.Increment(ref MutexLockCount);
        return sys_pthread_mutex_lock(mutex);
    }

    public static int pthread_mutex_unlock(pthread_mutex_t* mutex)
    {
        Interlocked.Increment(ref MutexUnlockCount);
        return sys_pthread_mutex_unlock(mutex);
    }

    public static int pthread_mutexattr_init(pthread_mutexattr_t* attr) => sys_pthread_mutexattr_init(attr);

    public static int pthread_mutexattr_settype(pthread_mutexattr_t* attr, int type)
    {
        LastMutexAttrType = type;
        return sys_pthread_mutexattr_settype(attr, type);
    }

    public static int pthread_mutexattr_destroy(pthread_mutexattr_t* attr) => sys_pthread_mutexattr_destroy(attr);

    public static int pthread_cond_init(pthread_cond_t* cond, pthread_condattr_t* attr)
    {
        Interlocked.Increment(ref CondInitCount);
        int injected = Interlocked.Exchange(ref FailNextCondInit, 0);
        if (injected != 0)
        {
            return injected;
        }

        return sys_pthread_cond_init(cond, attr);
    }

    public static int pthread_cond_destroy(pthread_cond_t* cond)
    {
        Interlocked.Increment(ref CondDestroyCount);
        return sys_pthread_cond_destroy(cond);
    }

    public static int pthread_cond_wait(pthread_cond_t* cond, pthread_mutex_t* mutex)
    {
        Interlocked.Increment(ref CondWaitCount);
        return sys_pthread_cond_wait(cond, mutex);
    }

    public static int pthread_cond_broadcast(pthread_cond_t* cond)
    {
        Interlocked.Increment(ref CondBroadcastCount);
        return sys_pthread_cond_broadcast(cond);
    }

    public static int pthread_condattr_init(pthread_condattr_t* attr) => sys_pthread_condattr_init(attr);

#if TARGET_APPLE
    public static int pthread_cond_timedwait_relative_np(pthread_cond_t* cond, pthread_mutex_t* mutex, timespec* reltime)
    {
        Interlocked.Increment(ref CondTimedWaitCount);
        LastTimedWaitDeadline = *reltime;
        return sys_pthread_cond_timedwait_relative_np(cond, mutex, reltime);
    }

    public static ulong clock_gettime_nsec_np(int clock_id)
    {
        LastClockGetTimeClock = clock_id;
        return sys_clock_gettime_nsec_np(clock_id);
    }

    [DllImport("libc", EntryPoint = "pthread_cond_timedwait_relative_np")]
    private static extern int sys_pthread_cond_timedwait_relative_np(pthread_cond_t* cond, pthread_mutex_t* mutex, timespec* reltime);

    [DllImport("libc", EntryPoint = "clock_gettime_nsec_np")]
    private static extern ulong sys_clock_gettime_nsec_np(int clock_id);
#else
    public static int pthread_cond_timedwait(pthread_cond_t* cond, pthread_mutex_t* mutex, timespec* abstime)
    {
        Interlocked.Increment(ref CondTimedWaitCount);
        LastTimedWaitDeadline = *abstime;
        return sys_pthread_cond_timedwait(cond, mutex, abstime);
    }

    public static int pthread_condattr_setclock(pthread_condattr_t* attr, int clock_id)
    {
        LastCondAttrClock = clock_id;
        return sys_pthread_condattr_setclock(attr, clock_id);
    }

    public static int clock_gettime(int clock_id, timespec* tp)
    {
        LastClockGetTimeClock = clock_id;
        return sys_clock_gettime(clock_id, tp);
    }

    [DllImport("libc", EntryPoint = "pthread_cond_timedwait")]
    private static extern int sys_pthread_cond_timedwait(pthread_cond_t* cond, pthread_mutex_t* mutex, timespec* abstime);

    [DllImport("libc", EntryPoint = "pthread_condattr_setclock")]
    private static extern int sys_pthread_condattr_setclock(pthread_condattr_t* attr, int clock_id);

    [DllImport("libc", EntryPoint = "clock_gettime")]
    private static extern int sys_clock_gettime(int clock_id, timespec* tp);
#endif

    /// <summary>
    /// The real shim is <c>new (nothrow) uint8_t[]</c> followed by a memset. This is the same
    /// thing without a runtime under it.
    /// </summary>
    public static void* ManagedGC_AllocZeroed(nuint size)
    {
        LastAllocSize = size;
        Interlocked.Increment(ref AllocCount);
        if (FailNextAlloc)
        {
            FailNextAlloc = false;
            return null;
        }

        if (FailAllocOnCall > 0 && --FailAllocOnCall == 0)
        {
            return null;
        }

        return NativeMemory.AllocZeroed(size);
    }

    public static void ManagedGC_Free(void* memory)
    {
        Interlocked.Increment(ref FreeCount);
        NativeMemory.Free(memory);
    }

    public static void ManagedGC_WaitUntilGCComplete(
        ref int gcInProgress,
        ref int gcStarted,
        ref int waitForGCEvent,
        int considerGcStart)
    {
        while (Volatile.Read(ref waitForGCEvent) == 0 ||
            Volatile.Read(ref gcInProgress) != 0 ||
            (considerGcStart != 0 && Volatile.Read(ref gcStarted) != 0))
        {
            Thread.Yield();
        }
    }

    public static void ManagedGC_AllowForegroundGC() => Thread.Yield();

    [DllImport("libc", EntryPoint = "pthread_mutex_init")]
    private static extern int sys_pthread_mutex_init(pthread_mutex_t* mutex, pthread_mutexattr_t* attr);

    [DllImport("libc", EntryPoint = "pthread_mutex_destroy")]
    private static extern int sys_pthread_mutex_destroy(pthread_mutex_t* mutex);

    [DllImport("libc", EntryPoint = "pthread_mutex_lock")]
    private static extern int sys_pthread_mutex_lock(pthread_mutex_t* mutex);

    [DllImport("libc", EntryPoint = "pthread_mutex_unlock")]
    private static extern int sys_pthread_mutex_unlock(pthread_mutex_t* mutex);

    [DllImport("libc", EntryPoint = "pthread_mutexattr_init")]
    private static extern int sys_pthread_mutexattr_init(pthread_mutexattr_t* attr);

    [DllImport("libc", EntryPoint = "pthread_mutexattr_settype")]
    private static extern int sys_pthread_mutexattr_settype(pthread_mutexattr_t* attr, int type);

    [DllImport("libc", EntryPoint = "pthread_mutexattr_destroy")]
    private static extern int sys_pthread_mutexattr_destroy(pthread_mutexattr_t* attr);

    [DllImport("libc", EntryPoint = "pthread_cond_init")]
    private static extern int sys_pthread_cond_init(pthread_cond_t* cond, pthread_condattr_t* attr);

    [DllImport("libc", EntryPoint = "pthread_cond_destroy")]
    private static extern int sys_pthread_cond_destroy(pthread_cond_t* cond);

    [DllImport("libc", EntryPoint = "pthread_cond_wait")]
    private static extern int sys_pthread_cond_wait(pthread_cond_t* cond, pthread_mutex_t* mutex);

    [DllImport("libc", EntryPoint = "pthread_cond_broadcast")]
    private static extern int sys_pthread_cond_broadcast(pthread_cond_t* cond);

    [DllImport("libc", EntryPoint = "pthread_condattr_init")]
    private static extern int sys_pthread_condattr_init(pthread_condattr_t* attr);
}
