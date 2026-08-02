// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Test-host substitute for src/.../Environment/GCToOSInterface.Imports.Unix.cs.
//
// The shipping declarations are [RuntimeImport]s, which only resolve inside a NativeAOT image,
// so this file declares the same private methods as ordinary P/Invokes. That makes the ported
// bodies above them -- the alignment arithmetic, the flag combinations, the failure paths --
// runnable in a normal test process against the real kernel, and it records the arguments of
// every call so that the flag translation can be asserted directly rather than inferred.
//
// The sleep and yield substitutes go one step further and can inject a failure: nanosleep can be
// made to report EINTR with an interval left over, which is the only way to drive the retry loop
// of GCToOSInterface::Sleep without waiting for a signal that may never arrive.
//
// A [DllImport] is exactly what the GC must not use; it is fine here because this file is never
// compiled into the GC. The methods it replaces are the boundary of the port: everything the
// tests exercise above them is the shipping code.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection;

internal static unsafe partial class GCToOSInterface
{
    internal struct MmapCall
    {
        public void* addr;
        public nuint length;
        public int prot;
        public int flags;
        public int fd;
        public nint offset;
        public void* result;
    }

    internal struct RangeCall
    {
        public void* addr;
        public nuint length;
        public int arg;
        public int result;
    }

    internal static MmapCall LastMmap;
    internal static int MmapCount;

    internal static RangeCall LastMunmap;
    internal static int MunmapCount;
    internal static nuint MunmapTotalLength;

    /// <summary>
    /// Every munmap of the current recording, so that a test can check the exact ranges that
    /// were given back rather than probing the address space for them, which would race with
    /// the other threads of the test process.
    /// </summary>
    internal static readonly RangeCall[] MunmapCalls = new RangeCall[8];

    internal static RangeCall LastMprotect;
    internal static int MprotectCount;

    internal static RangeCall LastMadvise;
    internal static int MadviseCount;

    internal static RangeCall LastBindMemoryPolicy;
    internal static int BindMemoryPolicyCount;

    //
    // Sleep and yield. These recordings are deliberately not touched by ResetRecording, so that
    // the sleep and yield tests cannot clobber -- or be clobbered by -- a virtual memory test
    // that xUnit happens to run at the same time in another class.
    //

    internal struct NanosleepCall
    {
        public timespec requested;
        public timespec remaining;
        public int result;
        public int errno;
    }

    /// <summary>Every nanosleep of the current recording, in order.</summary>
    internal static readonly NanosleepCall[] NanosleepCalls = new NanosleepCall[16];

    internal static int NanosleepCount;
    internal static int SchedYieldCount;

    /// <summary>
    /// How many of the next nanosleep calls report <c>EINTR</c> instead of sleeping, each one
    /// handing back <see cref="NanosleepInterruptRemaining"/> as the interval that is left.
    /// This is how the retry loop is driven without depending on a real signal arriving.
    /// </summary>
    internal static int NanosleepInterrupts;

    /// <summary>What an injected interruption reports in the <c>rem</c> argument.</summary>
    internal static timespec NanosleepInterruptRemaining;

    /// <summary>
    /// When non-zero, nanosleep fails with this errno instead of sleeping, and does not report
    /// a remaining interval. Injected after <see cref="NanosleepInterrupts"/> is exhausted.
    /// </summary>
    internal static int NanosleepFailErrno;

    /// <summary>
    /// When true, nanosleep succeeds without entering the kernel once
    /// <see cref="NanosleepInterrupts"/> is exhausted. A test that asserts an exact call count
    /// cannot then be lengthened by a real signal arriving during the terminal sleep.
    /// </summary>
    internal static bool NanosleepSucceedsWithoutSleeping;

    /// <summary>Forgets the sleep and yield recording and clears every injection.</summary>
    internal static void ResetSleepYieldRecording()
    {
        Array.Clear(NanosleepCalls);
        NanosleepCount = 0;
        SchedYieldCount = 0;
        NanosleepInterrupts = 0;
        NanosleepInterruptRemaining = default;
        NanosleepFailErrno = 0;
        NanosleepSucceedsWithoutSleeping = false;
        *s_errno = 0;
    }

    /// <summary>Forgets every recorded call. Each test starts by calling this.</summary>
    internal static void ResetRecording()
    {
        LastMmap = default;
        MmapCount = 0;
        LastMunmap = default;
        MunmapCount = 0;
        MunmapTotalLength = 0;
        Array.Clear(MunmapCalls);
        LastMprotect = default;
        MprotectCount = 0;
        LastMadvise = default;
        MadviseCount = 0;
        LastBindMemoryPolicy = default;
        BindMemoryPolicyCount = 0;
    }

    [ModuleInitializer]
    internal static void RegisterLibcResolver()
    {
        // "libc" has no single portable file name -- on glibc systems the .so is a linker
        // script -- so resolve it to the process itself, where libc is already loaded.
        NativeLibrary.SetDllImportResolver(
            typeof(GCToOSInterface).Assembly,
            (name, assembly, searchPath) => name == "libc" ? NativeLibrary.GetMainProgramHandle() : IntPtr.Zero);
    }

    private static void* mmap(void* addr, nuint length, int prot, int flags, int fd, nint offset)
    {
        void* result = sys_mmap(addr, length, prot, flags, fd, offset);
        LastMmap = new MmapCall
        {
            addr = addr,
            length = length,
            prot = prot,
            flags = flags,
            fd = fd,
            offset = offset,
            result = result,
        };
        MmapCount++;
        return result;
    }

    private static int munmap(void* addr, nuint length)
    {
        int result = sys_munmap(addr, length);
        LastMunmap = new RangeCall { addr = addr, length = length, result = result };
        if (MunmapCount < MunmapCalls.Length)
        {
            MunmapCalls[MunmapCount] = LastMunmap;
        }

        MunmapCount++;
        MunmapTotalLength += length;
        return result;
    }

    private static int mprotect(void* addr, nuint len, int prot)
    {
        int result = sys_mprotect(addr, len, prot);
        LastMprotect = new RangeCall { addr = addr, length = len, arg = prot, result = result };
        MprotectCount++;
        return result;
    }

    private static int madvise(void* addr, nuint length, int advice)
    {
        int result = sys_madvise(addr, length, advice);
        LastMadvise = new RangeCall { addr = addr, length = length, arg = advice, result = result };
        MadviseCount++;
        return result;
    }

    private static int getrlimit(int resource, Rlimit* rlim) => sys_getrlimit(resource, rlim);

    //
    // errno. The shipping code reads the thread's errno through the accessor its C library
    // exports; here it reads one process-wide slot instead, which the substitutes below fill --
    // either with an injected value, or with the errno of the real call, which the P/Invoke
    // stub captured into GetLastPInvokeError before returning. One slot is enough because a
    // test drives the sleep port from its own thread only.
    //
    private static readonly int* s_errno = (int*)NativeMemory.AllocZeroed(sizeof(int));

    private static int* __errno_location() => s_errno;

    private static int nanosleep(timespec* req, timespec* rem)
    {
        timespec reported = default;
        int result;

        if (NanosleepInterrupts > 0)
        {
            // An interrupted nanosleep writes what is left of the interval and fails with
            // EINTR. Nothing is actually slept, so the retry loop is exercised without the test
            // waiting for anything.
            NanosleepInterrupts--;
            reported = NanosleepInterruptRemaining;
            *rem = reported;

            // EINTR of <errno.h>, written out rather than read from the constant of the port so
            // that a wrong constant there fails the retry test instead of being confirmed by it.
            *s_errno = 4;
            result = -1;
        }
        else if (NanosleepFailErrno != 0)
        {
            *s_errno = NanosleepFailErrno;
            result = -1;
        }
        else if (NanosleepSucceedsWithoutSleeping)
        {
            result = 0;
        }
        else
        {
            result = sys_nanosleep(req, rem);
            if (result == -1)
            {
                // A real failure -- a signal that the test process took while sleeping, most
                // likely. Publish its errno where the port expects to read it, and record the
                // interval the kernel says is left, which the port will sleep next.
                *s_errno = Marshal.GetLastPInvokeError();
                reported = *rem;
            }
        }

        if (NanosleepCount < NanosleepCalls.Length)
        {
            NanosleepCalls[NanosleepCount] = new NanosleepCall
            {
                requested = *req,
                remaining = reported,
                result = result,
                errno = *s_errno,
            };
        }

        NanosleepCount++;
        return result;
    }

    private static int sched_yield()
    {
        SchedYieldCount++;
        return sys_sched_yield();
    }

    private static uint minipal_getpagesize() => (uint)Environment.SystemPageSize;

    private static void ManagedGC_NUMA_BindMemoryPolicy(void* address, nuint size, ushort node)
    {
        // The real shim binds the range to the node with mbind(). Recording the call is all the
        // tests can check without a NUMA machine, and it is what the managed side is
        // responsible for: calling it exactly when the commit succeeded and a node was asked
        // for.
        LastBindMemoryPolicy = new RangeCall { addr = address, length = size, arg = node };
        BindMemoryPolicyCount++;
    }

    [DllImport("libc", EntryPoint = "mmap", SetLastError = true)]
    private static extern void* sys_mmap(void* addr, nuint length, int prot, int flags, int fd, nint offset);

    [DllImport("libc", EntryPoint = "munmap", SetLastError = true)]
    private static extern int sys_munmap(void* addr, nuint length);

    [DllImport("libc", EntryPoint = "mprotect", SetLastError = true)]
    private static extern int sys_mprotect(void* addr, nuint len, int prot);

    [DllImport("libc", EntryPoint = "madvise", SetLastError = true)]
    private static extern int sys_madvise(void* addr, nuint length, int advice);

    [DllImport("libc", EntryPoint = "getrlimit", SetLastError = true)]
    private static extern int sys_getrlimit(int resource, Rlimit* rlim);

    [DllImport("libc", EntryPoint = "nanosleep", SetLastError = true)]
    private static extern int sys_nanosleep(timespec* req, timespec* rem);

    [DllImport("libc", EntryPoint = "sched_yield", SetLastError = true)]
    private static extern int sys_sched_yield();
}
