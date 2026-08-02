// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the GCEvent class of gcenv.os.h.
//
// The C++ class holds a single pointer to a pimpl -- a condition variable and mutex on Unix, a
// Win32 event handle on Windows -- and the managed struct holds the same pointer in the same
// place, so the address of a managed GCEvent is the address of a C++ one. That is what lets the
// methods forward to shims that call the C++ member functions in place; nothing copies or
// reinterprets the Impl itself.
//
// Deletion point: plan step 3 of ROADMAP.md, when the condition-variable implementation of
// gc/unix/events.cpp and the Win32 event implementation of gc/windows/gcenv.windows.cpp are
// ported. The struct stays; only the bodies change.
//
// As in C++, a GCEvent deliberately has no destructor: all of its uses have static lifetime, and
// running a destructor on process exit concurrently with another thread still operating on the
// event is worse than leaking the Impl. See
// https://github.com/dotnet/runtime/issues/7919.

using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>
    /// An event is a synchronization object whose state can be set and reset indicating that an
    /// event has occurred. It is used pervasively throughout the GC.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct GCEvent
    {
        private const string RuntimeLibrary = "*";

        /// <summary>
        /// The pimpl. A default-initialized <see cref="GCEvent"/> has this null, which is what
        /// the C++ default constructor produces.
        /// </summary>
        private void* m_impl;

        /// <summary>
        /// Closes the event. Attempting to use the event past calling
        /// <see cref="CloseEvent"/> is a logic error.
        /// </summary>
        public void CloseEvent()
        {
            fixed (GCEvent* self = &this)
            {
                ManagedGC_GCEvent_CloseEvent(self);
            }
        }

        /// <summary>
        /// "Sets" the event, indicating that a particular event has occurred. May wake up other
        /// threads waiting on this event. Depending on whether or not this event is an auto-reset
        /// event, the state of the event may or may not be automatically reset after
        /// <see cref="Set"/> is called.
        /// </summary>
        public void Set()
        {
            fixed (GCEvent* self = &this)
            {
                ManagedGC_GCEvent_Set(self);
            }
        }

        /// <summary>
        /// Resets the event, resetting it back to a non-signalled state. Auto-reset events
        /// automatically reset once the event is set, while manual-reset events do not reset
        /// until <see cref="Reset"/> is called. It is a no-op to call <see cref="Reset"/> on an
        /// auto-reset event.
        /// </summary>
        public void Reset()
        {
            fixed (GCEvent* self = &this)
            {
                ManagedGC_GCEvent_Reset(self);
            }
        }

        /// <summary>
        /// Waits for some period of time for this event to be signalled. The period of time may
        /// be infinite (if the timeout argument is <see cref="GCEnv.INFINITE"/>) or it may be a
        /// specified period of time, in milliseconds. Returns <see cref="GCEnv.WAIT_OBJECT_0"/>
        /// if this event was signalled and woke up this thread,
        /// <see cref="GCEnv.WAIT_TIMEOUT"/> if the timeout interval expired without this event
        /// being signalled, and <see cref="GCEnv.WAIT_FAILED"/> if the wait failed.
        /// </summary>
        public uint Wait(uint timeout, bool alertable)
        {
            fixed (GCEvent* self = &this)
            {
                return ManagedGC_GCEvent_Wait(self, timeout, alertable ? 1 : 0);
            }
        }

        /// <summary>
        /// Determines whether or not this event is valid. Returns false if it has not yet been
        /// initialized or has already been closed.
        /// </summary>
        public readonly bool IsValid()
        {
            return m_impl != null;
        }

        /// <summary>
        /// Initializes this event to be a host-aware manual reset event with the given initial
        /// state. Returns true if the initialization succeeded.
        /// </summary>
        public bool CreateManualEventNoThrow(bool initialState)
        {
            fixed (GCEvent* self = &this)
            {
                return ManagedGC_GCEvent_CreateManualEventNoThrow(self, initialState ? 1 : 0) != 0;
            }
        }

        /// <summary>
        /// Initializes this event to be a host-aware auto-resetting event with the given initial
        /// state. Returns true if the initialization succeeded.
        /// </summary>
        public bool CreateAutoEventNoThrow(bool initialState)
        {
            fixed (GCEvent* self = &this)
            {
                return ManagedGC_GCEvent_CreateAutoEventNoThrow(self, initialState ? 1 : 0) != 0;
            }
        }

        /// <summary>
        /// Initializes this event to be a host-unaware manual reset event with the given initial
        /// state. Returns true if the initialization succeeded.
        /// </summary>
        public bool CreateOSManualEventNoThrow(bool initialState)
        {
            fixed (GCEvent* self = &this)
            {
                return ManagedGC_GCEvent_CreateOSManualEventNoThrow(self, initialState ? 1 : 0) != 0;
            }
        }

        /// <summary>
        /// Initializes this event to be a host-unaware auto-resetting event with the given
        /// initial state. Returns true if the initialization succeeded.
        /// </summary>
        public bool CreateOSAutoEventNoThrow(bool initialState)
        {
            fixed (GCEvent* self = &this)
            {
                return ManagedGC_GCEvent_CreateOSAutoEventNoThrow(self, initialState ? 1 : 0) != 0;
            }
        }

        [RuntimeImport(RuntimeLibrary, "ManagedGC_GCEvent_CloseEvent")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_GCEvent_CloseEvent(GCEvent* @event);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_GCEvent_Set")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_GCEvent_Set(GCEvent* @event);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_GCEvent_Reset")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_GCEvent_Reset(GCEvent* @event);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_GCEvent_Wait")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern uint ManagedGC_GCEvent_Wait(GCEvent* @event, uint timeout, int alertable);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_GCEvent_CreateManualEventNoThrow")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_GCEvent_CreateManualEventNoThrow(GCEvent* @event, int initialState);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_GCEvent_CreateAutoEventNoThrow")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_GCEvent_CreateAutoEventNoThrow(GCEvent* @event, int initialState);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_GCEvent_CreateOSManualEventNoThrow")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_GCEvent_CreateOSManualEventNoThrow(GCEvent* @event, int initialState);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_GCEvent_CreateOSAutoEventNoThrow")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedGC_GCEvent_CreateOSAutoEventNoThrow(GCEvent* @event, int initialState);
    }
}
