// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the Windows half of src/native/minipal/mutex.c, which is what the CLRCriticalSection
// of gcenv.os.h forwards to: a CRITICAL_SECTION, which is recursive. The methods appear in the
// order the C++ declares them and the bodies are the same statements, with the allocation of the
// storage the C++ class embeds by value added to Initialize and Destroy -- see GCEnvSync.cs for
// why the managed struct has to hold a pointer.
//
// The calls to Win32 are [RuntimeImport] declarations, which are direct calls to the linked
// symbol with no marshalling and no GC mode transition -- what the C++ GC gets for free by being
// native code, and what the collector needs when it takes a lock while the world is suspended.
// They are in SyncImports.Windows.cs so that the test host can substitute the same methods for
// ones it can call and record; see tests/SyncImports.Windows.TestHost.cs.
//
// The `#ifdef _DEBUG` memset of minipal_mutex_destroy has no counterpart: it poisons storage
// that the C++ caller owns and keeps, while here the same call frees it.

using System.Diagnostics;
using static Internal.Runtime.GarbageCollection.SyncImports;

namespace Internal.Runtime.GarbageCollection
{
    internal unsafe partial struct CLRCriticalSection
    {
        public partial bool Initialize()
        {
            Debug.Assert(m_cs == null);
            m_cs = ManagedGC_AllocZeroed((nuint)sizeof(CRITICAL_SECTION));
            if (m_cs == null)
            {
                return false;
            }

            InitializeCriticalSection((CRITICAL_SECTION*)m_cs);
            return true;
        }

        public partial void Destroy()
        {
            Debug.Assert(m_cs != null);
            DeleteCriticalSection((CRITICAL_SECTION*)m_cs);

            ManagedGC_Free(m_cs);
            m_cs = null;
        }

        public readonly partial void Enter()
        {
            Debug.Assert(m_cs != null);
            EnterCriticalSection((CRITICAL_SECTION*)m_cs);
        }

        public readonly partial void Leave()
        {
            Debug.Assert(m_cs != null);
            LeaveCriticalSection((CRITICAL_SECTION*)m_cs);
        }
    }
}
