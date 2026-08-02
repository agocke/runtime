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
// The memory limit substitutes are injection-only by nature: a cgroup limit, a /proc/meminfo row
// or a sysfs cache size cannot be arranged on the machine running the tests, and asserting
// against whatever the host happens to report would test nothing. Each of them therefore hands
// back a value the test sets, and every substitute here still defaults to the real call so that
// a test that does not inject sees the machine.
//
// The three minipal timer substitutes are the one group with nothing real underneath: minipal is
// a static library this test process does not link. They therefore compute what
// src/native/minipal/time.c computes on Unix -- a monotonic nanosecond count, and the fixed
// nanosecond frequency -- unless a test injects, which is what makes the exact forwarding and
// the millisecond scaling of the port assertable.
//
// A [DllImport] is exactly what the GC must not use; it is fine here because this file is never
// compiled into the GC. The methods it replaces are the boundary of the port: everything the
// tests exercise above them is the shipping code.

using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    private static int getrlimit(int resource, Rlimit* rlim)
    {
        GetrlimitCalls++;
        if (GetrlimitInject)
        {
            *rlim = GetrlimitValue;
            return GetrlimitResult;
        }

        return sys_getrlimit(resource, rlim);
    }

    //
    // Memory limits and cache sizing. Everything below is injected rather than measured.
    //

    /// <summary>What <c>sysconf</c> reports, by name. A name that is not in the table falls
    /// through to the real libc.</summary>
    internal static readonly Dictionary<int, nint> SysconfValues = new Dictionary<int, nint>();

    /// <summary>Every <c>sysconf</c> name asked for, in order.</summary>
    internal static readonly List<int> SysconfCalls = new List<int>();

    /// <summary>When true, <see cref="getrlimit"/> reports the two fields below.</summary>
    internal static bool GetrlimitInject;
    internal static Rlimit GetrlimitValue;
    internal static int GetrlimitResult;
    internal static int GetrlimitCalls;

    /// <summary>What <c>ManagedGC_CGroup_GetPhysicalMemoryLimit</c> reports.</summary>
    internal static int CGroupPhysicalMemoryLimitResult;
    internal static ulong CGroupPhysicalMemoryLimitValue;
    internal static int CGroupPhysicalMemoryLimitCalls;

    /// <summary>What <c>ManagedGC_Unix_GetPhysicalMemoryUsed</c> reports.</summary>
    internal static int PhysicalMemoryUsedResult;
    internal static nuint PhysicalMemoryUsedValue;
    internal static int PhysicalMemoryUsedCalls;

    /// <summary>What <c>ManagedGC_Unix_ReadMemAvailable</c> reports.</summary>
    internal static int ReadMemAvailableResult;
    internal static ulong ReadMemAvailableValue;
    internal static int ReadMemAvailableCalls;

    /// <summary>
    /// The files <c>ManagedGC_Unix_ReadMemoryValueFromFile</c> can read, by path. A path that is
    /// not in the table is reported as unreadable, which is what the C++ does for a file that
    /// does not exist.
    /// </summary>
    internal static readonly Dictionary<string, ulong> MemoryValueFiles = new Dictionary<string, ulong>();

    /// <summary>Every path <c>ManagedGC_Unix_ReadMemoryValueFromFile</c> was asked for, in order.</summary>
    internal static readonly List<string> MemoryValueFileCalls = new List<string>();

    /// <summary>What <c>ManagedGC_Unix_GetCurrentVirtualMemorySize</c> reports. The default is
    /// the <c>(size_t)-1</c> the C++ reports where /proc/self/statm cannot be read.</summary>
    internal static nuint CurrentVirtualMemorySize = nuint.MaxValue;
    internal static int CurrentVirtualMemorySizeCalls;

#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD
    /// <summary>What <c>sysinfo</c> reports. Only the platforms that have it declare these.</summary>
    internal static int SysinfoResult;
    internal static SysInfo SysinfoValue;
    internal static int SysinfoCalls;
#endif

    /// <summary>
    /// The bitset behind the affinity set <c>ManagedGC_Unix_GetProcessAffinitySet</c> hands
    /// back, and the set itself. Both are native memory so that the pointer the shipping code
    /// reads through stays put.
    /// </summary>
    private const nuint ProcessAffinitySetEntries = 16;

    private static readonly nuint* s_processAffinityBitset =
        (nuint*)NativeMemory.AllocZeroed(ProcessAffinitySetEntries, (nuint)sizeof(nuint));

    private static readonly AffinitySet* s_processAffinitySet = CreateProcessAffinitySet();

    private static AffinitySet* CreateProcessAffinitySet()
    {
        AffinitySet* set = (AffinitySet*)NativeMemory.AllocZeroed((nuint)sizeof(AffinitySet));
        set->InitializeWithStorage(s_processAffinityBitset, ProcessAffinitySetEntries);
        return set;
    }

    /// <summary>Makes the process affinity set report exactly <paramref name="cpuCount"/> CPUs.</summary>
    internal static void SetProcessAffinityCpuCount(nuint cpuCount)
    {
        NativeMemory.Clear(s_processAffinityBitset, ProcessAffinitySetEntries * (nuint)sizeof(nuint));
        for (nuint i = 0; i < cpuCount; i++)
        {
            s_processAffinitySet->Add(i);
        }
    }

    /// <summary>Forgets every memory limit recording and clears every injection.</summary>
    internal static void ResetMemoryLimitsRecording()
    {
        SysconfValues.Clear();
        SysconfCalls.Clear();
        GetrlimitInject = false;
        GetrlimitValue = default;
        GetrlimitResult = 0;
        GetrlimitCalls = 0;
        CGroupPhysicalMemoryLimitResult = 0;
        CGroupPhysicalMemoryLimitValue = 0;
        CGroupPhysicalMemoryLimitCalls = 0;
        PhysicalMemoryUsedResult = 0;
        PhysicalMemoryUsedValue = 0;
        PhysicalMemoryUsedCalls = 0;
        ReadMemAvailableResult = 0;
        ReadMemAvailableValue = 0;
        ReadMemAvailableCalls = 0;
        MemoryValueFiles.Clear();
        MemoryValueFileCalls.Clear();
        CurrentVirtualMemorySize = nuint.MaxValue;
        CurrentVirtualMemorySizeCalls = 0;
#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD
        SysinfoResult = 0;
        SysinfoValue = default;
        SysinfoCalls = 0;
#endif
        SetProcessAffinityCpuCount(0);

        // The two caches and the sticky /proc/meminfo flag of the shipping code are
        // function-local statics in C++ and fields here, so each test starts from the value
        // they have in a fresh process.
        s_maxSize = 0;
        s_maxTrueSize = 0;
        s_tryReadMemInfoFailed = false;
        g_RestrictedPhysicalMemoryLimit = 0;
    }

    //
    // The three src/native/minipal/time.h entry points of the timer port. minipal is a static
    // library that this test process does not link, so the substitutes stand in for it: each
    // one either hands back an injected value or computes what time.c computes on this
    // platform, which is a monotonic nanosecond count and a fixed nanosecond frequency.
    //

    internal static bool HiresTicksInject;
    internal static long HiresTicksValue;
    internal static int HiresTicksCalls;

    internal static bool HiresTickFrequencyInject;
    internal static long HiresTickFrequencyValue;
    internal static int HiresTickFrequencyCalls;

    internal static bool LowresTicksInject;
    internal static long LowresTicksValue;
    internal static int LowresTicksCalls;

    /// <summary>Forgets every timer recording and clears every injection.</summary>
    internal static void ResetTimerRecording()
    {
        HiresTicksInject = false;
        HiresTicksValue = 0;
        HiresTicksCalls = 0;
        HiresTickFrequencyInject = false;
        HiresTickFrequencyValue = 0;
        HiresTickFrequencyCalls = 0;
        LowresTicksInject = false;
        LowresTicksValue = 0;
        LowresTicksCalls = 0;
    }

    private static long minipal_hires_ticks()
    {
        HiresTicksCalls++;
        return HiresTicksInject ? HiresTicksValue : MonotonicNanoseconds();
    }

    private static long minipal_hires_tick_frequency()
    {
        HiresTickFrequencyCalls++;

        // tccSecondsToNanoSeconds of src/native/minipal/time.c, which is what that file returns
        // on every Unix platform because both clocks it can read count nanoseconds.
        return HiresTickFrequencyInject ? HiresTickFrequencyValue : 1000000000;
    }

    private static long minipal_lowres_ticks()
    {
        LowresTicksCalls++;

        // tccMilliSecondsToNanoSeconds of the same file.
        return LowresTicksInject ? LowresTicksValue : MonotonicNanoseconds() / 1000000;
    }

    /// <summary>
    /// The monotonic nanosecond count that <c>minipal_hires_ticks</c> returns. On Unix
    /// <c>Stopwatch</c> reads the same <c>CLOCK_MONOTONIC</c> that
    /// <c>src/native/minipal/time.c</c> does, so this is the same clock the shipping code sees.
    /// </summary>
    private static long MonotonicNanoseconds() =>
        (long)(Stopwatch.GetTimestamp() * (1000000000.0 / Stopwatch.Frequency));

    private static nint sysconf(int name)
    {
        SysconfCalls.Add(name);
        return SysconfValues.TryGetValue(name, out nint value) ? value : sys_sysconf(name);
    }

    private static int ManagedGC_CGroup_GetPhysicalMemoryLimit(ulong* val)
    {
        CGroupPhysicalMemoryLimitCalls++;
        *val = CGroupPhysicalMemoryLimitValue;
        return CGroupPhysicalMemoryLimitResult;
    }

    private static int ManagedGC_Unix_GetPhysicalMemoryUsed(nuint* val)
    {
        PhysicalMemoryUsedCalls++;
        *val = PhysicalMemoryUsedValue;
        return PhysicalMemoryUsedResult;
    }

    private static int ManagedGC_Unix_ReadMemoryValueFromFile(byte* filename, ulong* val)
    {
        string path = Marshal.PtrToStringUTF8((IntPtr)filename);
        MemoryValueFileCalls.Add(path);
        if (MemoryValueFiles.TryGetValue(path, out ulong value))
        {
            *val = value;
            return 1;
        }

        return 0;
    }

    private static int ManagedGC_Unix_ReadMemAvailable(ulong* memAvailable)
    {
        ReadMemAvailableCalls++;
        *memAvailable = ReadMemAvailableValue;
        return ReadMemAvailableResult;
    }

    private static nuint ManagedGC_Unix_GetCurrentVirtualMemorySize()
    {
        CurrentVirtualMemorySizeCalls++;
        return CurrentVirtualMemorySize;
    }

    private static AffinitySet* ManagedGC_Unix_GetProcessAffinitySet() => s_processAffinitySet;

#if !TARGET_APPLE && !TARGET_FREEBSD && !TARGET_OPENBSD
    private static int sysinfo(SysInfo* info)
    {
        SysinfoCalls++;
        *info = SysinfoValue;
        return SysinfoResult;
    }
#endif

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

    [DllImport("libc", EntryPoint = "sysconf", SetLastError = true)]
    private static extern nint sys_sysconf(int name);

#if TARGET_APPLE || TARGET_FREEBSD
    // The BSD sysctl family is not injected: nothing above it can be driven from a test process
    // anyway, and it is only declared here so that the port compiles for those targets.

    [DllImport("libc", EntryPoint = "sysctl", SetLastError = true)]
    private static extern int sysctl(int* name, uint namelen, void* oldp, nuint* oldlenp, void* newp, nuint newlen);

    [DllImport("libc", EntryPoint = "sysctlbyname", SetLastError = true)]
    private static extern int sysctlbyname(byte* name, void* oldp, nuint* oldlenp, void* newp, nuint newlen);

    [DllImport("libc", EntryPoint = "sysctlnametomib", SetLastError = true)]
    private static extern int sysctlnametomib(byte* name, int* mibp, nuint* sizep);
#endif
}
