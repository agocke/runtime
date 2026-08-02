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
}
