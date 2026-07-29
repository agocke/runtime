// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime;

namespace Internal.Runtime.GarbageCollection
{
    /// <summary>
    /// The native entry points of the managed GC, mirroring the <c>GC_VersionInfo</c> /
    /// <c>GC_Initialize</c> pair that <c>gcload.cpp</c> exports for the C++ GC.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are <see cref="RuntimeExportAttribute"/> rather than
    /// <c>UnmanagedCallersOnly</c> on purpose: a runtime export is a direct native-to-managed
    /// call with no reverse-P/Invoke thread attach and no cooperative/preemptive mode
    /// transition. The GC is entered while the world is suspended and during startup before a
    /// thread is attached, so neither of those is available to it.
    /// </para>
    /// <para>
    /// ILC only emits these symbols when the assembly is passed to
    /// <c>--generateunmanagedentrypoints</c>, which the build integration does only when
    /// <c>IlcManagedGC</c> is set. In every other build this assembly is referenced but
    /// unrooted, so it contributes nothing to the image.
    /// </para>
    /// </remarks>
    internal static unsafe class ManagedGCEntryPoints
    {
        private const int S_FALSE = 1;
        private const int E_FAIL = unchecked((int)0x80004005);

        // Must match GC_INTERFACE_MAJOR_VERSION / GC_INTERFACE_MINOR_VERSION in gcinterface.h.
        private const uint GC_INTERFACE_MAJOR_VERSION = 5;
        private const uint GC_INTERFACE_MINOR_VERSION = 8;

        /// <summary>
        /// Reports the GC/EE interface version this GC was built against. Port of
        /// <c>GC_VersionInfo</c>.
        /// </summary>
        [RuntimeExport("ManagedGC_VersionInfo")]
        internal static void ManagedGC_VersionInfo(VersionInfo* info)
        {
            info->MajorVersion = GC_INTERFACE_MAJOR_VERSION;
            info->MinorVersion = GC_INTERFACE_MINOR_VERSION;
            info->BuildVersion = 0;
            info->Name = null;
        }

        /// <summary>
        /// Brings up the managed GC. Port of <c>GC_Initialize</c>.
        /// </summary>
        /// <remarks>
        /// The heap itself is not ported yet, so this currently runs the self-checks that the
        /// ported modules make possible and then returns <c>S_FALSE</c>, which the native
        /// selector treats as "managed GC declined, use the C++ GC". That keeps the managed
        /// path exercised end-to-end in a real process — the interface layout check and the
        /// ~80 configuration reads below all go through the ported <c>IGCToCLR</c> vtable —
        /// without the port having to be complete to produce a working application.
        /// </remarks>
        [RuntimeExport("ManagedGC_Initialize")]
        internal static int ManagedGC_Initialize(void* clrToGC, void** gcHeap, void** gcHandleManager, void* gcDacVars)
        {
            *gcHeap = null;
            *gcHandleManager = null;

            // The native selector always supplies an IGCToCLR; the null the EE passes to the
            // statically linked C++ GC is not valid for this path.
            if (clrToGC == null)
            {
                return E_FAIL;
            }

            GCToEEInterface.Initialize(clrToGC);

            if (!GCInterfaceLayout.Verify())
            {
                return E_FAIL;
            }

            GCConfig.Initialize();

            return S_FALSE;
        }
    }
}
