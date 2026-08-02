// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Test-host substitute for src/.../Environment/SyncImports.Windows.cs.
//
// The shipping declarations are [RuntimeImport]s, which only resolve inside a NativeAOT image,
// so this file declares the same methods as ordinary P/Invokes. That makes the ported bodies
// above them -- the Win32 event handle of GCEvent::Impl and the critical section of
// minipal_mutex -- runnable in a normal test process against the real kernel, and it records the
// calls so that the translation can be asserted directly rather than inferred: that a manual
// event is created with bManualReset set and an auto event without it, that the initial state is
// passed through, that Wait passes the timeout unchanged.
//
// A [DllImport] is exactly what the GC must not use; it is fine here because this file is never
// compiled into the GC. The methods it replaces are the boundary of the port: everything the
// tests exercise above them is the shipping code, including the opaque CRITICAL_SECTION of
// SyncTypes.Windows.cs, which is compiled from the shipping source.

using System.Runtime.InteropServices;
using System.Threading;

namespace Internal.Runtime.GarbageCollection;

internal static unsafe partial class SyncImports
{
    internal struct CreateEventCall
    {
        public int bManualReset;
        public int bInitialState;
        public void* result;
    }

    internal struct WaitCall
    {
        public void* hHandle;
        public uint dwMilliseconds;
        public uint result;
    }

    internal static CreateEventCall LastCreateEvent;
    internal static int CreateEventCount;
    internal static WaitCall LastWait;
    internal static int WaitCount;
    internal static int SetEventCount;
    internal static int ResetEventCount;
    internal static int CloseHandleCount;
    internal static int InitializeCriticalSectionCount;
    internal static int DeleteCriticalSectionCount;
    internal static int EnterCriticalSectionCount;
    internal static int LeaveCriticalSectionCount;
    internal static int AllocCount;
    internal static int FreeCount;
    internal static nuint LastAllocSize;

    /// <summary>When true, <c>CreateEventW</c> returns null once, as it does on failure.</summary>
    internal static bool FailNextCreateEvent;

    /// <summary>When true, <c>ManagedGC_AllocZeroed</c> returns null once.</summary>
    internal static bool FailNextAlloc;

    /// <summary>Forgets every recorded call. Each test starts by calling this.</summary>
    internal static void ResetRecording()
    {
        LastCreateEvent = default;
        CreateEventCount = 0;
        LastWait = default;
        WaitCount = 0;
        SetEventCount = 0;
        ResetEventCount = 0;
        CloseHandleCount = 0;
        InitializeCriticalSectionCount = 0;
        DeleteCriticalSectionCount = 0;
        EnterCriticalSectionCount = 0;
        LeaveCriticalSectionCount = 0;
        AllocCount = 0;
        FreeCount = 0;
        LastAllocSize = 0;
        FailNextCreateEvent = false;
        FailNextAlloc = false;
    }

    public static void* CreateEventW(void* lpEventAttributes, int bManualReset, int bInitialState, char* lpName)
    {
        void* result = FailNextCreateEvent ? null : sys_CreateEventW(lpEventAttributes, bManualReset, bInitialState, lpName);
        FailNextCreateEvent = false;
        LastCreateEvent = new CreateEventCall
        {
            bManualReset = bManualReset,
            bInitialState = bInitialState,
            result = result,
        };
        Interlocked.Increment(ref CreateEventCount);
        return result;
    }

    public static int SetEvent(void* hEvent)
    {
        Interlocked.Increment(ref SetEventCount);
        return sys_SetEvent(hEvent);
    }

    public static int ResetEvent(void* hEvent)
    {
        Interlocked.Increment(ref ResetEventCount);
        return sys_ResetEvent(hEvent);
    }

    public static uint WaitForSingleObject(void* hHandle, uint dwMilliseconds)
    {
        uint result = sys_WaitForSingleObject(hHandle, dwMilliseconds);
        LastWait = new WaitCall { hHandle = hHandle, dwMilliseconds = dwMilliseconds, result = result };
        Interlocked.Increment(ref WaitCount);
        return result;
    }

    public static int CloseHandle(void* hObject)
    {
        Interlocked.Increment(ref CloseHandleCount);
        return sys_CloseHandle(hObject);
    }

    public static void InitializeCriticalSection(CRITICAL_SECTION* lpCriticalSection)
    {
        Interlocked.Increment(ref InitializeCriticalSectionCount);
        sys_InitializeCriticalSection(lpCriticalSection);
    }

    public static void DeleteCriticalSection(CRITICAL_SECTION* lpCriticalSection)
    {
        Interlocked.Increment(ref DeleteCriticalSectionCount);
        sys_DeleteCriticalSection(lpCriticalSection);
    }

    public static void EnterCriticalSection(CRITICAL_SECTION* lpCriticalSection)
    {
        Interlocked.Increment(ref EnterCriticalSectionCount);
        sys_EnterCriticalSection(lpCriticalSection);
    }

    public static void LeaveCriticalSection(CRITICAL_SECTION* lpCriticalSection)
    {
        Interlocked.Increment(ref LeaveCriticalSectionCount);
        sys_LeaveCriticalSection(lpCriticalSection);
    }

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

        return NativeMemory.AllocZeroed(size);
    }

    public static void ManagedGC_Free(void* memory)
    {
        Interlocked.Increment(ref FreeCount);
        NativeMemory.Free(memory);
    }

    [DllImport("kernel32", EntryPoint = "CreateEventW", SetLastError = true)]
    private static extern void* sys_CreateEventW(void* lpEventAttributes, int bManualReset, int bInitialState, char* lpName);

    [DllImport("kernel32", EntryPoint = "SetEvent", SetLastError = true)]
    private static extern int sys_SetEvent(void* hEvent);

    [DllImport("kernel32", EntryPoint = "ResetEvent", SetLastError = true)]
    private static extern int sys_ResetEvent(void* hEvent);

    [DllImport("kernel32", EntryPoint = "WaitForSingleObject", SetLastError = true)]
    private static extern uint sys_WaitForSingleObject(void* hHandle, uint dwMilliseconds);

    [DllImport("kernel32", EntryPoint = "CloseHandle", SetLastError = true)]
    private static extern int sys_CloseHandle(void* hObject);

    [DllImport("kernel32", EntryPoint = "InitializeCriticalSection")]
    private static extern void sys_InitializeCriticalSection(CRITICAL_SECTION* lpCriticalSection);

    [DllImport("kernel32", EntryPoint = "DeleteCriticalSection")]
    private static extern void sys_DeleteCriticalSection(CRITICAL_SECTION* lpCriticalSection);

    [DllImport("kernel32", EntryPoint = "EnterCriticalSection")]
    private static extern void sys_EnterCriticalSection(CRITICAL_SECTION* lpCriticalSection);

    [DllImport("kernel32", EntryPoint = "LeaveCriticalSection")]
    private static extern void sys_LeaveCriticalSection(CRITICAL_SECTION* lpCriticalSection);
}
