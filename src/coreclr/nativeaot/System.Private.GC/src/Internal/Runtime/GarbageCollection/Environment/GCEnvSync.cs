// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the CLRCriticalSection class of gcenv.os.h and of the CrstStatic / CrstHolder helpers
// of gcenv.sync.h that the GC and the handle table lock with.
//
// One deviation from the C++ shape: CLRCriticalSection embeds a minipal_mutex by value, which is
// a pthread_mutex_t or a CRITICAL_SECTION. Their sizes differ per operating system, and
// GCInterfaceOffsets.h -- the mechanism this port pins native layouts with -- carries one value
// per pointer size, not one per platform, so the managed struct cannot reserve the right number
// of bytes inline. It holds a pointer to a natively allocated critical section instead. Nothing
// passes a CLRCriticalSection across the boundary, so no layout depends on the difference.
//
// Deletion point: plan step 3 of ROADMAP.md, together with the rest of the environment
// forwarders. When the lock itself is managed, the pointer becomes the lock's state and the
// shims go away.
//
// CLREventStatic of gcenv.sync.h is not ported: the GC does not use it. The NativeAOT runtime
// does, from its own C++ code, which keeps its own definition.

using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>
    /// Critical section used by the GC.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct CLRCriticalSection
    {
        private const string RuntimeLibrary = "*";

        private void* m_cs;

        /// <summary>Initialize the critical section.</summary>
        public bool Initialize()
        {
            m_cs = ManagedGC_CriticalSection_Create();
            return m_cs != null;
        }

        /// <summary>Destroy the critical section.</summary>
        public void Destroy()
        {
            ManagedGC_CriticalSection_Destroy(m_cs);
            m_cs = null;
        }

        /// <summary>Enter the critical section. Blocks until the section can be entered.</summary>
        public readonly void Enter()
        {
            ManagedGC_CriticalSection_Enter(m_cs);
        }

        /// <summary>Leave the critical section.</summary>
        public readonly void Leave()
        {
            ManagedGC_CriticalSection_Leave(m_cs);
        }

        [RuntimeImport(RuntimeLibrary, "ManagedGC_CriticalSection_Create")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void* ManagedGC_CriticalSection_Create();

        [RuntimeImport(RuntimeLibrary, "ManagedGC_CriticalSection_Destroy")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_CriticalSection_Destroy(void* cs);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_CriticalSection_Enter")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_CriticalSection_Enter(void* cs);

        [RuntimeImport(RuntimeLibrary, "ManagedGC_CriticalSection_Leave")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern void ManagedGC_CriticalSection_Leave(void* cs);
    }

    /// <summary>
    /// Port of the <c>CrstType</c> values of <c>gcenv.sync.h</c>, all of which are zero because
    /// the GC's environment does not have lock levels.
    /// </summary>
    internal static class CrstType
    {
        public const int CrstHandleTable = 0;
    }

    /// <summary>
    /// Port of the <c>CrstFlags</c> values of <c>gcenv.sync.h</c>.
    /// </summary>
    internal static class CrstFlags
    {
        public const int CRST_REENTRANCY = 0;
        public const int CRST_UNSAFE_SAMELEVEL = 0;
        public const int CRST_UNSAFE_ANYMODE = 0;
        public const int CRST_DEBUGGER_THREAD = 0;
        public const int CRST_DEFAULT = 0;
    }

    /// <summary>
    /// Port of <c>CrstStatic</c>: a critical section with static storage duration, plus the
    /// debug-only record of which thread holds it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CrstStatic
    {
        private CLRCriticalSection m_cs;
#if DEBUG
        private EEThreadId m_holderThreadId;
#endif

        // The lock type and flags exist in the signature because the C++ GC passes them; the
        // GC's environment has no lock levels, so every CrstType and CrstFlags value is zero and
        // the implementation ignores both.
#pragma warning disable IDE0060 // Remove unused parameter
        public bool InitNoThrow(int eType, int eFlags = CrstFlags.CRST_DEFAULT)
        {
            return m_cs.Initialize();
        }
#pragma warning restore IDE0060

        public void Destroy()
        {
            m_cs.Destroy();
        }

        public void Enter()
        {
            m_cs.Enter();
#if DEBUG
            m_holderThreadId.SetToCurrentThread();
#endif
        }

        public void Leave()
        {
#if DEBUG
            m_holderThreadId.Clear();
#endif
            m_cs.Leave();
        }

#if DEBUG
        public readonly EEThreadId GetHolderThreadId()
        {
            return m_holderThreadId;
        }

        public bool OwnedByCurrentThread()
        {
            EEThreadId holder = GetHolderThreadId();
            return holder.IsCurrentThread();
        }
#endif
    }

    /// <summary>
    /// Port of <c>CrstHolder</c>. The C++ class releases the lock in its destructor; the managed
    /// one is a ref struct so that <c>using</c> releases it at the same point, and so that it
    /// cannot escape to the heap.
    /// </summary>
    internal unsafe ref struct CrstHolder
    {
        private readonly CrstStatic* m_pLock;

        public CrstHolder(CrstStatic* pLock)
        {
            m_pLock = pLock;
            m_pLock->Enter();
        }

        public void Dispose()
        {
            m_pLock->Leave();
        }
    }

    /// <summary>
    /// Port of <c>CrstHolderWithState</c>: a <see cref="CrstHolder"/> that can be released and
    /// re-acquired before it goes out of scope, which the handle table scan does.
    /// </summary>
    internal unsafe ref struct CrstHolderWithState
    {
        private readonly CrstStatic* m_pLock;
        private bool m_fAcquired;

        public CrstHolderWithState(CrstStatic* pLock, bool fAcquire = true)
        {
            m_pLock = pLock;
            m_fAcquired = fAcquire;
            if (fAcquire)
            {
                m_pLock->Enter();
            }
        }

        public void Dispose()
        {
            if (m_fAcquired)
            {
                m_pLock->Leave();
            }
        }

        public void Acquire()
        {
            if (!m_fAcquired)
            {
                m_pLock->Enter();
                m_fAcquired = true;
            }
        }

        public void Release()
        {
            if (m_fAcquired)
            {
                m_pLock->Leave();
                m_fAcquired = false;
            }
        }

        public readonly CrstStatic* GetValue()
        {
            return m_pLock;
        }
    }
}
