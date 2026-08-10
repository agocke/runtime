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

        [RuntimeImport(RuntimeLibrary, "ManagedGC_TryEnterCriticalRegion")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_TryEnterCriticalRegion();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_ExitCriticalRegion")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_ExitCriticalRegion(int entered);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_SuspendCriticalRegion")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_SuspendCriticalRegion();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_ResumeCriticalRegion")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_ResumeCriticalRegion(int suspended);

        // Take the managed-GC critical region without ever spinning in native code while a suspension
        // is pending. ManagedGC_TryEnterCriticalRegion returns 1 (owned) / 0 (nested) immediately, or
        // -1 when a suspension is pending -- in which case it has changed no thread state and never
        // transitioned the thread's GC mode (so no possibly-invalid deferred transition frame is
        // published). We retry from this ordinary managed loop, whose back-edge is a GC safe point, so
        // a pending SuspendEE can hijack this cooperative thread instead of the thread spinning in
        // native code. NoInlining keeps this a real, poll-bearing frame for every caller.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static GCHeapCriticalRegion Enter()
        {
            int entered;
            while ((entered = ManagedGC_TryEnterCriticalRegion()) < 0)
            {
                // Pure managed back-edge spin. ManagedGC_TryEnterCriticalRegion is a fast flag read;
                // this thread therefore spends its time at the call-return / loop-back GC safe points
                // of this method (cooperative, never transitioning GC mode on a possibly-invalid
                // deferred frame), where a pending SuspendEE hijacks/redirects it or its back-edge GC
                // poll self-suspends it on a valid poll-site frame. Do NOT call a blocking native
                // routine (e.g. sched_yield) here: that would park the thread in non-hijackable native
                // code and starve SuspendEE, deadlocking against the suspension that must clear the
                // pending flag.
            }

            return new GCHeapCriticalRegion(entered);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Exit() =>
            ManagedGC_ExitCriticalRegion(_entered);

        public static int Suspend() =>
            ManagedGC_SuspendCriticalRegion();

        public static void Resume(int suspended) =>
            ManagedGC_ResumeCriticalRegion(suspended);
    }
}
