// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The Win32 entry points that the Windows event and lock ports call, declared as <windows.h>
// declares them, except that every BOOL is spelled as int: a Win32 BOOL is four bytes wide and a
// managed bool is one, and there is no marshalling here to convert between them.
//
// They are [RuntimeImport]s rather than [DllImport]s: a runtime import is a direct call to a
// linked symbol with no marshalling, no argument copying, no lazy binding step and no GC mode
// transition, which is what code that runs with the world suspended requires. kernel32.lib is on
// the default link line of every NativeAOT application (Microsoft.NETCore.Native.Windows.targets),
// so each of these resolves at link time.
//
// A blocking call made through one of these blocks the calling thread in the OS without changing
// its GC mode, which is exactly what the C++ GC gets by being native code: GCEvent::Wait and
// CLRCriticalSection::Enter are the primitives the collector's own threads park on, so they must
// not go through a transition that would try to suspend or resume anything.
//
// This file is compiled into the shipping library only. The xUnit tests compile
// tests/SyncImports.Windows.TestHost.cs in its place, which declares the same methods as ordinary
// P/Invokes so that the ported logic above them can be exercised, and records the calls so that
// the translation can be asserted.

using System.Runtime;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>
    /// The Win32 surface of <see cref="GCEvent"/> and <see cref="CLRCriticalSection"/>.
    /// </summary>
    internal static unsafe partial class SyncImports
    {
        private const string RuntimeLibrary = "*";

        [RuntimeImport(RuntimeLibrary, "CreateEventW")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void* CreateEventW(void* lpEventAttributes, int bManualReset, int bInitialState, char* lpName);

        [RuntimeImport(RuntimeLibrary, "SetEvent")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int SetEvent(void* hEvent);

        [RuntimeImport(RuntimeLibrary, "ResetEvent")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int ResetEvent(void* hEvent);

        [RuntimeImport(RuntimeLibrary, "WaitForSingleObject")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern uint WaitForSingleObject(void* hHandle, uint dwMilliseconds);

        [RuntimeImport(RuntimeLibrary, "CloseHandle")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int CloseHandle(void* hObject);

        [RuntimeImport(RuntimeLibrary, "InitializeCriticalSection")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void InitializeCriticalSection(CRITICAL_SECTION* lpCriticalSection);

        [RuntimeImport(RuntimeLibrary, "DeleteCriticalSection")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void DeleteCriticalSection(CRITICAL_SECTION* lpCriticalSection);

        [RuntimeImport(RuntimeLibrary, "EnterCriticalSection")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void EnterCriticalSection(CRITICAL_SECTION* lpCriticalSection);

        [RuntimeImport(RuntimeLibrary, "LeaveCriticalSection")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void LeaveCriticalSection(CRITICAL_SECTION* lpCriticalSection);

        /// <summary>
        /// Stands in for the <c>new (std::nothrow)</c> that allocates a <c>GCEvent::Impl</c>, and
        /// for the storage the C++ <c>CLRCriticalSection</c> embeds by value. See
        /// <c>nativeaot/Runtime/gcenv.managed.cpp</c>.
        /// </summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_AllocZeroed")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void* ManagedGC_AllocZeroed(nuint size);

        /// <summary>Stands in for <c>delete</c>.</summary>
        [RuntimeImport(RuntimeLibrary, "ManagedGC_Free")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void ManagedGC_Free(void* memory);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_WaitUntilGCComplete")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void ManagedGC_WaitUntilGCComplete(
            ref int gcInProgress,
            ref int gcStarted,
            ref int waitForGCEvent,
            int considerGcStart);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_AllowForegroundGC")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void ManagedGC_AllowForegroundGC();
    }
}
