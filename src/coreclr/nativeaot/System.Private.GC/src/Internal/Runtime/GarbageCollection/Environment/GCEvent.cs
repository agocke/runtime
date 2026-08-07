// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the GCEvent class of gcenv.os.h.
//
// The C++ class holds a single pointer to a pimpl -- a condition variable and mutex on Unix, a
// Win32 event handle on Windows -- and the managed struct holds the same pointer in the same
// place, so the address of a managed GCEvent is the address of a C++ one.
//
// This file is the header: the field, the one method gcenv.os.h defines inline, and the declared
// surface. The implementations are the platform files, as in C++: GCEvent.Unix.cs is
// gc/unix/events.cpp and GCEvent.Windows.cs is the GCEvent half of gc/windows/gcenv.windows.cpp.
//
// As in C++, a GCEvent deliberately has no destructor: all of its uses have static lifetime, and
// running a destructor on process exit concurrently with another thread still operating on the
// event is worse than leaking the Impl. See
// https://github.com/dotnet/runtime/issues/7919.

using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>
    /// An event is a synchronization object whose state can be set and reset indicating that an
    /// event has occurred. It is used pervasively throughout the GC.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe partial struct GCEvent
    {
        /// <summary>
        /// The pimpl. A default-initialized <see cref="GCEvent"/> has this null, which is what
        /// the C++ default constructor produces.
        /// </summary>
        private void* m_impl;

        /// <summary>
        /// Closes the event. Attempting to use the event past calling
        /// <see cref="CloseEvent"/> is a logic error.
        /// </summary>
        public partial void CloseEvent();

        /// <summary>
        /// "Sets" the event, indicating that a particular event has occurred. May wake up other
        /// threads waiting on this event. Depending on whether or not this event is an auto-reset
        /// event, the state of the event may or may not be automatically reset after
        /// <see cref="Set"/> is called.
        /// </summary>
        public partial void Set();

        /// <summary>
        /// Resets the event back to a non-signalled state. For an auto-reset event, this discards
        /// a signal that has not yet been consumed by a waiter.
        /// </summary>
        public partial void Reset();

        /// <summary>
        /// Waits for some period of time for this event to be signalled. The period of time may
        /// be infinite (if the timeout argument is <see cref="GCEnv.INFINITE"/>) or it may be a
        /// specified period of time, in milliseconds. Returns <see cref="GCEnv.WAIT_OBJECT_0"/>
        /// if this event was signalled and woke up this thread,
        /// <see cref="GCEnv.WAIT_TIMEOUT"/> if the timeout interval expired without this event
        /// being signalled, and <see cref="GCEnv.WAIT_FAILED"/> if the wait failed.
        /// </summary>
        public partial uint Wait(uint timeout, bool alertable);

        /// <summary>
        /// Waits from a managed user thread through ordinary P/Invoke transitions, so the
        /// runtime can suspend that thread while it is blocked.
        /// </summary>
        public partial uint UserThreadWait(uint timeout);

        /// <summary>
        /// Determines whether the event has an implementation. As in C++, closing an event does
        /// not clear the implementation pointer, so this continues to return true after
        /// <see cref="CloseEvent"/>.
        /// </summary>
        public readonly bool IsValid()
        {
            return m_impl != null;
        }

        /// <summary>
        /// Initializes this event to be a host-aware manual reset event with the given initial
        /// state. Returns true if the initialization succeeded.
        /// </summary>
        public partial bool CreateManualEventNoThrow(bool initialState);

        /// <summary>
        /// Initializes this event to be a host-aware auto-resetting event with the given initial
        /// state. Returns true if the initialization succeeded.
        /// </summary>
        public partial bool CreateAutoEventNoThrow(bool initialState);

        /// <summary>
        /// Initializes this event to be a host-unaware manual reset event with the given initial
        /// state. Returns true if the initialization succeeded.
        /// </summary>
        public partial bool CreateOSManualEventNoThrow(bool initialState);

        /// <summary>
        /// Initializes this event to be a host-unaware auto-resetting event with the given
        /// initial state. Returns true if the initialization succeeded.
        /// </summary>
        public partial bool CreateOSAutoEventNoThrow(bool initialState);
    }
}
