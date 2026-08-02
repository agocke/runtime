// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the GCEvent half of gc/windows/gcenv.windows.cpp: the Win32 implementation of GCEvent,
// which forwards directly to the Win32 event APIs. The members of GCEvent::Impl appear in the
// order the C++ class declares them, the methods in the order the C++ file defines them, and the
// bodies are the same statements.
//
// The calls to Win32 are [RuntimeImport] declarations, which are direct calls to the linked
// symbol with no marshalling and no GC mode transition -- what the C++ GC gets for free by being
// native code, and what the collector needs when it parks its own threads on an event while the
// world is suspended. They are in SyncImports.Windows.cs so that the test host can substitute the
// same methods for ones it can call and record; see tests/SyncImports.Windows.TestHost.cs.
//
// Two shapes are preserved rather than corrected, because this is a translation and not a
// redesign:
//
// * CreateEvent reports failure by returning NULL, but IsValid() compares against
//   INVALID_HANDLE_VALUE, so a failed creation is reported as success by
//   CreateOSAutoEventNoThrow and CreateOSManualEventNoThrow. That is what the C++ does.
// * CloseEvent closes the handle and resets it to INVALID_HANDLE_VALUE, but neither frees the
//   Impl nor clears m_impl, so GCEvent::IsValid() keeps reporting true afterwards. That is also
//   what the C++ does; a GCEvent has no destructor for the reason given in GCEvent.cs.
//
// One deviation the language forces: GCEvent::Impl is heap-allocated with `new (std::nothrow)`
// in C++. The managed GC has no allocator of its own, so the storage comes from the same nothrow
// native allocation shim AffinitySet uses. The C++ holds the new object in a std::unique_ptr and
// calls release() on the way out, which is the same lifetime the code below has: the allocation
// is owned by the Impl* local until it is stored in m_impl.

using System.Diagnostics;
using static Internal.Runtime.GarbageCollection.SyncImports;

namespace Internal.Runtime.GarbageCollection
{
    internal unsafe partial struct GCEvent
    {
        /// <summary><c>INVALID_HANDLE_VALUE</c> of <c>&lt;handleapi.h&gt;</c>.</summary>
        private static void* INVALID_HANDLE_VALUE => (void*)(nint)(-1);

        /// <summary>
        /// WindowsEvent is an implementation of GCEvent that forwards directly to Win32 APIs.
        /// </summary>
        private struct Impl
        {
            private void* m_hEvent;

            /// <summary>
            /// Stands in for the C++ constructor, which a struct in freshly allocated storage
            /// cannot have.
            /// </summary>
            public void Construct()
            {
                m_hEvent = INVALID_HANDLE_VALUE;
            }

            public readonly bool IsValid()
            {
                return m_hEvent != INVALID_HANDLE_VALUE;
            }

            public readonly void Set()
            {
                Debug.Assert(IsValid());
                int result = SetEvent(m_hEvent);
                // SetEvent failed
                Debug.Assert(result != 0);
            }

            public readonly void Reset()
            {
                Debug.Assert(IsValid());
                int result = ResetEvent(m_hEvent);
                // ResetEvent failed
                Debug.Assert(result != 0);
            }

            public readonly uint Wait(uint timeout, bool alertable)
            {
                _ = alertable; // UNREFERENCED_PARAMETER(alertable)
                Debug.Assert(IsValid());

                return WaitForSingleObject(m_hEvent, timeout);
            }

            public void CloseEvent()
            {
                Debug.Assert(IsValid());
                int result = CloseHandle(m_hEvent);
                // CloseHandle failed
                Debug.Assert(result != 0);
                m_hEvent = INVALID_HANDLE_VALUE;
            }

            public bool CreateAutoEvent(bool initialState)
            {
                m_hEvent = CreateEventW(null, 0, initialState ? 1 : 0, null);
                return IsValid();
            }

            public bool CreateManualEvent(bool initialState)
            {
                m_hEvent = CreateEventW(null, 1, initialState ? 1 : 0, null);
                return IsValid();
            }
        }

        public partial void CloseEvent()
        {
            Debug.Assert(m_impl != null);
            ((Impl*)m_impl)->CloseEvent();
        }

        public partial void Set()
        {
            Debug.Assert(m_impl != null);
            ((Impl*)m_impl)->Set();
        }

        public partial void Reset()
        {
            Debug.Assert(m_impl != null);
            ((Impl*)m_impl)->Reset();
        }

        public partial uint Wait(uint timeout, bool alertable)
        {
            Debug.Assert(m_impl != null);
            return ((Impl*)m_impl)->Wait(timeout, alertable);
        }

        public partial bool CreateAutoEventNoThrow(bool initialState)
        {
            // [DESKTOP TODO] The difference between events and OS events is
            // whether or not the hosting API is made aware of them. When (if)
            // we implement hosting support for Local GC, we will need to be
            // aware of the host here.
            return CreateOSAutoEventNoThrow(initialState);
        }

        public partial bool CreateManualEventNoThrow(bool initialState)
        {
            // [DESKTOP TODO] The difference between events and OS events is
            // whether or not the hosting API is made aware of them. When (if)
            // we implement hosting support for Local GC, we will need to be
            // aware of the host here.
            return CreateOSManualEventNoThrow(initialState);
        }

        public partial bool CreateOSAutoEventNoThrow(bool initialState)
        {
            Debug.Assert(m_impl == null);
            Impl* @event = (Impl*)ManagedGC_AllocZeroed((nuint)sizeof(Impl));
            if (@event == null)
            {
                return false;
            }

            @event->Construct();

            if (!@event->CreateAutoEvent(initialState))
            {
                ManagedGC_Free(@event);
                return false;
            }

            m_impl = @event;
            return true;
        }

        public partial bool CreateOSManualEventNoThrow(bool initialState)
        {
            Debug.Assert(m_impl == null);
            Impl* @event = (Impl*)ManagedGC_AllocZeroed((nuint)sizeof(Impl));
            if (@event == null)
            {
                return false;
            }

            @event->Construct();

            if (!@event->CreateManualEvent(initialState))
            {
                ManagedGC_Free(@event);
                return false;
            }

            m_impl = @event;
            return true;
        }
    }
}
