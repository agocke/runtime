// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the processor count and identity methods of gc/windows/gcenv.windows.cpp:
// GCToOSInterface::GetCurrentThreadIdForLogging, GCToOSInterface::GetCurrentProcessId,
// GCToOSInterface::GetCurrentProcessorNumber and GCToOSInterface::CanGetCurrentProcessorNumber
// from the "thread and process" section, and GCToOSInterface::GetTotalProcessorCount and
// GCToOSInterface::GetMaxProcessorCount from further down the file, in the order the C++ file
// defines them, with the same statements.
//
// SetCurrentThreadIdealAffinity and GetCurrentThreadIdealProc sit between the two halves of the
// first group in the C++ and are deliberately not here: they belong with the rest of the
// affinity and CPU group work, which is a submodule of its own. Their declarations are still
// forwarders in GCToOSInterface.cs, and the GroupProcNo members only they use -- the one
// argument constructor, GetGroup, GetProcIndex and NoGroup -- are not translated yet either,
// so that nothing in this library is written before it has a caller.
//
// Three pieces of state stay in the C++ for now, and this file reaches them through the
// narrowest shims that can express them:
//
//   * g_totalCpuCount, the cache GetTotalProcessorCount fills in. Its *address* crosses, not its
//     value, because both this body and the C++ one write it: gc/gcconfig.cpp and the NativeAOT
//     PAL still call GCToOSInterface::GetTotalProcessorCount, so the C++ body stays compiled and
//     the two have to share one cache rather than fill two.
//   * g_nProcessors and g_SystemInfo.dwNumberOfProcessors, the two values it can cache. The
//     first is CPU group state that GCToOSInterface::Initialize computes and that is file static
//     in the C++; the second is the cached SYSTEM_INFO of the same Initialize.
//   * g_processAffinitySet, which the same Initialize fills in -- ManagedGC_Windows_GetProcessAffinitySet
//     reports its address, and the counting is the translated AffinitySet of AffinitySet.cs. It
//     is the Windows counterpart of the ManagedGC_Unix_GetProcessAffinitySet the Unix port uses.
//
// All four disappear when GCToOSInterface::Initialize and the CPU group submodule are translated
// and System.Private.GC owns the state; see plan step 3 of ROADMAP.md. Recomputing any of them
// here instead would give the managed GC a second copy with a different lifetime than the one
// the rest of the runtime reads, which is exactly what the shims avoid. CanEnableGCCPUGroups
// needs no shim of its own: it is already on GCToOSInterface as a forwarder.
//
// The Win32 entry points are [RuntimeImport]s rather than [DllImport]s: a runtime import is a
// direct call to the linked symbol with no marshalling and no GC mode transition, which is what
// code that runs with the world suspended requires. kernel32.lib, which exports all three, is on
// the default NativeAOT link line. They are declared in GCToOSInterface.Imports.Windows.cs so
// that the test host can substitute the same private methods; see
// tests/GCToOSInterface.Imports.Windows.TestHost.cs.
//
// One import is spelled with a Win32 prefix, following the rule the write watch and timer ports
// already use in that file: a name is prefixed only when GCToOSInterface has a method of its own
// with the Win32 name, and [RuntimeImport] carries the real symbol, so the two never have to
// agree. The correspondence is one-to-one and is not an overload of the method beside it:
//
//     GetCurrentThreadId          is ::GetCurrentThreadId of <windows.h>, unprefixed because the
//                                 method here is called GetCurrentThreadIdForLogging
//     Win32GetCurrentProcessId    is ::GetCurrentProcessId of <windows.h>
//     GetCurrentProcessorNumberEx is ::GetCurrentProcessorNumberEx of <windows.h>, unprefixed
//                                 because the method here has no Ex suffix

using System.Diagnostics;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe partial class GCToOSInterface
    {
        /// <summary><c>PROCESSOR_NUMBER</c> of <c>&lt;windows.h&gt;</c>.</summary>
        internal struct PROCESSOR_NUMBER
        {
            public ushort Group;
            public byte Number;
            public byte Reserved;
        }

        /// <summary>
        /// <c>GroupProcNo</c> of <c>gc/windows/gcenv.windows.cpp</c>: a processor number and the
        /// CPU group it belongs to, packed into the single 16 bit value the GC passes around.
        /// </summary>
        private readonly struct GroupProcNo
        {
            private readonly ushort m_groupProc;

            public GroupProcNo(ushort group, ushort procIndex)
            {
                m_groupProc = (ushort)((group << 6) | procIndex);
                Debug.Assert(group <= 0x3ff);
                Debug.Assert(procIndex <= 0x3f);
            }

            public ushort GetCombinedValue() => m_groupProc;
        }

        //
        // Thread and process
        //

        /// <summary>
        /// Get numeric id of the current thread if possible on the current platform. It is
        /// intended for logging purposes only.
        /// </summary>
        public static ulong GetCurrentThreadIdForLogging()
        {
            return GetCurrentThreadId();
        }

        /// <summary>Get id of the process.</summary>
        public static uint GetCurrentProcessId()
        {
            return Win32GetCurrentProcessId();
        }

        /// <summary>Get the number of the current processor.</summary>
        public static uint GetCurrentProcessorNumber()
        {
            Debug.Assert(CanGetCurrentProcessorNumber());

            PROCESSOR_NUMBER proc_no_cpu_group;
            GetCurrentProcessorNumberEx(&proc_no_cpu_group);

            GroupProcNo groupProcNo = new GroupProcNo(proc_no_cpu_group.Group, proc_no_cpu_group.Number);
            return groupProcNo.GetCombinedValue();
        }

        /// <summary>Check if the OS supports getting current processor number.</summary>
        public static bool CanGetCurrentProcessorNumber()
        {
            // on all Windows platforms we support this API exists
            return true;
        }

        //
        // Processor counts
        //

        /// <summary>
        /// Gets the total number of processors on the machine, not taking into account current
        /// process affinity.
        /// </summary>
        /// <returns>Number of processors on the machine.</returns>
        public static uint GetTotalProcessorCount()
        {
            // &g_totalCpuCount of gc/windows/gcenv.windows.cpp; the C++ body names the variable
            // itself, and this reads and writes it through its address.
            uint* totalCpuCount = ManagedGC_Windows_GetTotalCpuCount();

            if (*totalCpuCount != 0)
                return *totalCpuCount;
            if (CanEnableGCCPUGroups())
            {
                *totalCpuCount = ManagedGC_Windows_GetCpuGroupProcessorCount();
            }
            else
            {
                *totalCpuCount = ManagedGC_Windows_GetSystemInfoProcessorCount();
            }

            return *totalCpuCount;
        }

        /// <summary>
        /// Gets the maximum number of processors that could potentially exist on the machine
        /// (including offlined ones).
        /// </summary>
        public static uint GetMaxProcessorCount()
        {
            return (uint)ManagedGC_Windows_GetProcessAffinitySet()->MaxCpuCount();
        }
    }
}
