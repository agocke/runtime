// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>
    /// Prevents the current thread from observing a GC poll or being hijacked while the managed
    /// GC is mutating state that must remain consistent at suspension points.
    /// </summary>
    internal readonly struct GCHeapCriticalRegion
    {
        private const string RuntimeLibrary = "*";

        private readonly int _entered;

        private GCHeapCriticalRegion(int entered)
        {
            _entered = entered;
        }

        [RuntimeImport(RuntimeLibrary, "ManagedGC_EnterCriticalRegion")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_EnterCriticalRegion();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_ExitCriticalRegion")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_ExitCriticalRegion(int entered);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_SuspendCriticalRegion")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_SuspendCriticalRegion();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_ResumeCriticalRegion")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_ResumeCriticalRegion(int suspended);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static GCHeapCriticalRegion Enter() =>
            new GCHeapCriticalRegion(ManagedGC_EnterCriticalRegion());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Exit() =>
            ManagedGC_ExitCriticalRegion(_entered);

        public static int Suspend() =>
            ManagedGC_SuspendCriticalRegion();

        public static void Resume(int suspended) =>
            ManagedGC_ResumeCriticalRegion(suspended);
    }
}
