// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Behavior tests for the CLRCriticalSection port -- the translation of the minipal_mutex
// functions of src/native/minipal/mutex.c that gcenv.os.h forwards to -- and for the CrstStatic
// and CrstHolder wrappers of gcenv.sync.h that the GC locks with.
//
// The ported bodies are the code under test. Only the pthread / Win32 declarations underneath
// them are substituted, by SyncImports.*.TestHost.cs, which forwards each call to the real
// operating system and records it. So the tests check two things at once: that the sequence of
// calls the port makes is the one the C++ makes -- including the recursive mutex attribute,
// which is the one property of this lock the GC actually depends on -- and that the lock that
// comes out excludes and nests correctly under contention.
//
// Whether another thread can take a lock is answered by starting one and seeing whether it gets
// in promptly. A thread that does not get in stays parked in Enter, so every test joins its
// probes before destroying the lock they hold a pointer to; JoinProbes does that, and each test
// calls it once the lock is free again.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;

namespace Internal.Runtime.GarbageCollection;

[Collection(SyncImportsCollection.Name)]
public sealed unsafe class GCCriticalSectionTests
{
    private const int JoinTimeoutMs = 30000;

    /// <summary>How long a probe waits before concluding that the lock is held.</summary>
    private const int ProbeTimeoutMs = 200;

    private readonly List<Thread> _probes = new();

    [Fact]
    public void InitializeAllocatesTheLockAndDestroyReleasesIt()
    {
        SyncImports.ResetRecording();
        CLRCriticalSection cs = default;

        Assert.True(cs.Initialize());
        Assert.Equal(1, SyncImports.AllocCount);
        Assert.Equal(0, SyncImports.FreeCount);

        cs.Destroy();
        Assert.Equal(1, SyncImports.FreeCount);

        // Destroyed and re-initialized is a fresh lock, which is what a CrstStatic that outlives
        // a GC does.
        Assert.True(cs.Initialize());
        cs.Destroy();
        Assert.Equal(2, SyncImports.AllocCount);
        Assert.Equal(2, SyncImports.FreeCount);
    }

    [Fact]
    public void InitializeFailsWhenTheLockCannotBeAllocated()
    {
        SyncImports.ResetRecording();
        CLRCriticalSection cs = default;

        SyncImports.FailNextAlloc = true;
        try
        {
            Assert.False(cs.Initialize());
            Assert.Equal(1, SyncImports.AllocCount);
            Assert.Equal(0, SyncImports.FreeCount);

            // A failed Initialize leaves the struct in the state it started in, so a later one
            // can succeed.
            Assert.True(cs.Initialize());
            cs.Destroy();
        }
        finally
        {
            SyncImports.FailNextAlloc = false;
        }
    }

    /// <summary>
    /// The lock is recursive: the same thread may enter it again without deadlocking, and only
    /// the matching number of leaves releases it. <c>minipal_mutex</c> documents this, the GC's
    /// handle table relies on it, and it is the one behavior that would be silently lost if the
    /// mutex attribute were dropped.
    /// </summary>
    [Fact]
    public void LockIsRecursive()
    {
        SyncImports.ResetRecording();
        CLRCriticalSection cs = default;
        Assert.True(cs.Initialize());
        try
        {
            cs.Enter();
            cs.Enter();
            cs.Enter();

            // Still held after two of the three leaves: another thread must not get in.
            cs.Leave();
            cs.Leave();
            Assert.False(TryEnterFromAnotherThread(&cs));

            cs.Leave();
            Assert.True(TryEnterFromAnotherThread(&cs));
        }
        finally
        {
            JoinProbes();
            cs.Destroy();
        }
    }

    /// <summary>
    /// The lock excludes: with several threads incrementing a shared counter inside it, no
    /// update is lost, and no thread ever observes the counter changing under it.
    /// </summary>
    [Fact]
    public void LockExcludesConcurrentEnters()
    {
        const int ThreadCount = 4;
        const int IterationsPerThread = 20000;

        SyncImports.ResetRecording();
        CLRCriticalSection* cs = (CLRCriticalSection*)NativeMemory.AllocZeroed((nuint)sizeof(CLRCriticalSection));
        try
        {
            Assert.True(cs->Initialize());

            nint lockAddress = (nint)cs;
            int counter = 0;
            int violations = 0;
            var threads = new Thread[ThreadCount];
            for (int i = 0; i < ThreadCount; i++)
            {
                threads[i] = new Thread(() =>
                {
                    CLRCriticalSection* section = (CLRCriticalSection*)lockAddress;
                    for (int j = 0; j < IterationsPerThread; j++)
                    {
                        section->Enter();

                        // Not interlocked on purpose: the lock is what makes this safe, so a
                        // lost update is a failure of the lock. The nested Enter/Leave in the
                        // middle re-enters it recursively while it is already held.
                        int observed = counter;
                        section->Enter();
                        counter = observed + 1;
                        section->Leave();
                        if (counter != observed + 1)
                        {
                            Interlocked.Increment(ref violations);
                        }

                        section->Leave();
                    }
                })
                {
                    IsBackground = true,
                };
                threads[i].Start();
            }

            foreach (Thread thread in threads)
            {
                Assert.True(thread.Join(JoinTimeoutMs));
            }

            Assert.Equal(0, Volatile.Read(ref violations));
            Assert.Equal(ThreadCount * IterationsPerThread, counter);
        }
        finally
        {
            cs->Destroy();
            NativeMemory.Free(cs);
        }
    }

    /// <summary>
    /// A held lock blocks another thread until it is released, and the blocked thread gets in
    /// as soon as it is.
    /// </summary>
    [Fact]
    public void EnterBlocksUntilTheOwnerLeaves()
    {
        SyncImports.ResetRecording();
        CLRCriticalSection* cs = (CLRCriticalSection*)NativeMemory.AllocZeroed((nuint)sizeof(CLRCriticalSection));
        try
        {
            Assert.True(cs->Initialize());
            cs->Enter();

            nint lockAddress = (nint)cs;
            bool entered = false;
            var started = new ManualResetEventSlim(false);
            var contender = new Thread(() =>
            {
                started.Set();
                ((CLRCriticalSection*)lockAddress)->Enter();
                Volatile.Write(ref entered, true);
                ((CLRCriticalSection*)lockAddress)->Leave();
            })
            {
                IsBackground = true,
            };

            contender.Start();
            started.Wait();
            Thread.Sleep(100);
            Assert.False(Volatile.Read(ref entered));

            cs->Leave();
            Assert.True(contender.Join(JoinTimeoutMs));
            Assert.True(Volatile.Read(ref entered));
        }
        finally
        {
            cs->Destroy();
            NativeMemory.Free(cs);
        }
    }

    // CrstStatic records the owning thread in a debug build, through
    // GCToOSInterface.GetCurrentThreadIdForLogging. That used to be a [RuntimeImport] forwarder,
    // which resolves only inside a NativeAOT image, so these tests ran only where the debug
    // bookkeeping was compiled out; the processor identity port made it an ordinary managed body
    // over a substitutable import, so they now run in both configurations.

    /// <summary>
    /// <c>CrstHolder</c> is the C++ destructor turned into a <c>using</c>: it enters on
    /// construction and leaves at the end of the scope.
    /// </summary>
    [Fact]
    public void CrstHolderEntersAndLeavesWithItsScope()
    {
        SyncImports.ResetRecording();
        CrstStatic* crst = (CrstStatic*)NativeMemory.AllocZeroed((nuint)sizeof(CrstStatic));
        try
        {
            Assert.True(crst->InitNoThrow(CrstType.CrstHandleTable));

            using (new CrstHolder(crst))
            {
                Assert.False(TryEnterFromAnotherThread(crst));
            }

            Assert.True(TryEnterFromAnotherThread(crst));
        }
        finally
        {
            JoinProbes();
            crst->Destroy();
            NativeMemory.Free(crst);
        }
    }

    /// <summary>
    /// <c>CrstHolderWithState</c> can drop and retake the lock inside its scope, which is what
    /// the handle table scan does, and still leaves it released exactly once.
    /// </summary>
    [Fact]
    public void CrstHolderWithStateReleasesAndReacquires()
    {
        SyncImports.ResetRecording();
        CrstStatic* crst = (CrstStatic*)NativeMemory.AllocZeroed((nuint)sizeof(CrstStatic));
        try
        {
            Assert.True(crst->InitNoThrow(CrstType.CrstHandleTable));

            var holder = new CrstHolderWithState(crst);
            try
            {
                Assert.True(crst == holder.GetValue());
                Assert.False(TryEnterFromAnotherThread(crst));

                holder.Release();
                Assert.True(TryEnterFromAnotherThread(crst));

                // Releasing an already-released holder is a no-op, not a second unlock.
                holder.Release();

                holder.Acquire();
                Assert.False(TryEnterFromAnotherThread(crst));

                // As is acquiring an already-acquired one.
                holder.Acquire();
            }
            finally
            {
                holder.Dispose();
            }

            Assert.True(TryEnterFromAnotherThread(crst));

            // A holder constructed without acquiring leaves the lock alone, and disposing it
            // does not release a lock it never took.
            var unacquired = new CrstHolderWithState(crst, false);
            Assert.True(TryEnterFromAnotherThread(crst));
            unacquired.Dispose();
            Assert.True(TryEnterFromAnotherThread(crst));
        }
        finally
        {
            JoinProbes();
            crst->Destroy();
            NativeMemory.Free(crst);
        }
    }

#if !TARGET_WINDOWS
    // PTHREAD_MUTEX_RECURSIVE of <pthread.h>.
#if TARGET_APPLE || TARGET_FREEBSD || TARGET_OPENBSD
    private const int PTHREAD_MUTEX_RECURSIVE = 2;
#else
    private const int PTHREAD_MUTEX_RECURSIVE = 1;
#endif

    /// <summary>
    /// Initialize asks for a recursive mutex, and every operation is one pthread call on the
    /// storage the C++ class embeds by value.
    /// </summary>
    [Fact]
    public void InitializeRequestsARecursiveMutex()
    {
        SyncImports.ResetRecording();
        CLRCriticalSection cs = default;
        Assert.True(cs.Initialize());
        try
        {
            Assert.Equal(PTHREAD_MUTEX_RECURSIVE, SyncImports.LastMutexAttrType);
            Assert.Equal(1, SyncImports.MutexInitCount);
            Assert.Equal((nuint)sizeof(pthread_mutex_t), SyncImports.LastAllocSize);
            Assert.Equal(0, SyncImports.MutexLockCount);

            cs.Enter();
            Assert.Equal(1, SyncImports.MutexLockCount);
            Assert.Equal(0, SyncImports.MutexUnlockCount);

            cs.Leave();
            Assert.Equal(1, SyncImports.MutexUnlockCount);
        }
        finally
        {
            cs.Destroy();
            Assert.Equal(1, SyncImports.MutexDestroyCount);
        }
    }

#if !DEBUG
    /// <summary>
    /// A failed <c>pthread_mutex_init</c> fails the initialization and frees the storage. The
    /// C++ returns the same false without freeing anything, because the caller owns the memory
    /// there. This path asserts in the port, as it does in the C++, so it can only be driven in
    /// a build where the assert is compiled out.
    /// </summary>
    [Fact]
    public void InitializeFailsWhenTheMutexCannotBeInitialized()
    {
        SyncImports.ResetRecording();
        CLRCriticalSection cs = default;

        SyncImports.FailNextMutexInit = 22; // EINVAL
        try
        {
            Assert.False(cs.Initialize());
            Assert.Equal(1, SyncImports.AllocCount);
            Assert.Equal(1, SyncImports.FreeCount);

            Assert.True(cs.Initialize());
            cs.Destroy();
        }
        finally
        {
            SyncImports.FailNextMutexInit = 0;
        }
    }
#endif // !DEBUG

#else // TARGET_WINDOWS

    /// <summary>
    /// Every operation is one Win32 critical section call, on storage the size of the
    /// <c>CRITICAL_SECTION</c> the C++ class embeds by value.
    /// </summary>
    [Fact]
    public void OperationsForwardToWin32CriticalSections()
    {
        SyncImports.ResetRecording();
        CLRCriticalSection cs = default;
        Assert.True(cs.Initialize());
        try
        {
            Assert.Equal(1, SyncImports.InitializeCriticalSectionCount);
            Assert.Equal((nuint)sizeof(CRITICAL_SECTION), SyncImports.LastAllocSize);

            cs.Enter();
            Assert.Equal(1, SyncImports.EnterCriticalSectionCount);
            Assert.Equal(0, SyncImports.LeaveCriticalSectionCount);

            cs.Leave();
            Assert.Equal(1, SyncImports.LeaveCriticalSectionCount);
        }
        finally
        {
            cs.Destroy();
            Assert.Equal(1, SyncImports.DeleteCriticalSectionCount);
        }
    }

#endif // TARGET_WINDOWS

    /// <summary>
    /// Tries to take the lock from another thread. Returns false if the lock was still held
    /// after <see cref="ProbeTimeoutMs"/>. Another thread is required because the lock is
    /// recursive, so the current one would be let straight back in.
    /// </summary>
    private bool TryEnterFromAnotherThread(CLRCriticalSection* cs)
    {
        nint lockAddress = (nint)cs;
        return Probe(() =>
        {
            CLRCriticalSection* section = (CLRCriticalSection*)lockAddress;
            section->Enter();
            section->Leave();
        });
    }

    private bool TryEnterFromAnotherThread(CrstStatic* crst)
    {
        nint lockAddress = (nint)crst;
        return Probe(() =>
        {
            CrstStatic* section = (CrstStatic*)lockAddress;
            section->Enter();
            section->Leave();
        });
    }

    private bool Probe(Action enterAndLeave)
    {
        var thread = new Thread(() => enterAndLeave())
        {
            IsBackground = true,
        };

        thread.Start();
        if (thread.Join(ProbeTimeoutMs))
        {
            return true;
        }

        // The lock is held, so the probe is parked inside Enter. It finishes once the test
        // releases the lock; JoinProbes waits for that before the lock is destroyed.
        _probes.Add(thread);
        return false;
    }

    private void JoinProbes()
    {
        foreach (Thread probe in _probes)
        {
            Assert.True(probe.Join(JoinTimeoutMs), "a probe never acquired the released lock");
        }

        _probes.Clear();
    }
}
