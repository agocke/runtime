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
        private const int S_OK = 0;
        private const int E_FAIL = unchecked((int)0x80004005);
        private const int E_OUTOFMEMORY = unchecked((int)0x8007000E);

        /// <summary>
        /// Reports the GC/EE interface version this GC was built against, and records the
        /// version the runtime reports it supports. Port of <c>GC_VersionInfo</c>.
        /// </summary>
#if SERVER_GC
        [RuntimeExport("ManagedServerGC_VersionInfo")]
#else
        [RuntimeExport("ManagedGC_VersionInfo")]
#endif
        internal static void ManagedGC_VersionInfo(VersionInfo* info)
        {
            // On entry the runtime has filled this in with the interface version it supports,
            // which exists so a newer GC can avoid calling IGCToCLR methods an older runtime
            // does not have. The C++ GC only records this when built standalone; the managed
            // GC is always loaded through the standalone-shaped protocol.
            s_runtimeSupportedVersion = *info;

            // Taken from the generated layout table rather than restated here, so that the
            // reported version is the one gcinterface.h declares.
            info->MajorVersion = (uint)GCInterfaceOffsets.GC_INTERFACE_MAJOR_VERSION;
            info->MinorVersion = (uint)GCInterfaceOffsets.GC_INTERFACE_MINOR_VERSION;
            info->BuildVersion = 0;

            // A utf8 literal is image data rather than a heap object, so the pointer stays
            // valid after the fixed block ends.
            fixed (byte* name = "CoreCLR GC\0"u8)
            {
                info->Name = name;
            }
        }

        /// <summary>The interface version the runtime reported in <see cref="ManagedGC_VersionInfo"/>.</summary>
        public static VersionInfo RuntimeSupportedVersion => s_runtimeSupportedVersion;

        private static VersionInfo s_runtimeSupportedVersion;

        /// <summary>
        /// Brings up the managed GC. Port of <c>GC_Initialize</c>.
        /// </summary>
        /// <remarks>
        /// Because <c>IlcManagedGC</c> is an explicit opt-in, initialization failures fail
        /// runtime startup rather than selecting the C++ GC.
        /// </remarks>
#if SERVER_GC
        [RuntimeExport("ManagedServerGC_Initialize")]
#else
        [RuntimeExport("ManagedGC_Initialize")]
#endif
        internal static int ManagedGC_Initialize(void* clrToGC, void** gcHeap, void** gcHandleManager, GcDacVars* gcDacVars)
        {
            void* heap;
            void* handleManager;

            *gcHeap = null;
            *gcHandleManager = null;

            // The native selector always supplies an IGCToCLR; the null the EE passes to the
            // statically linked C++ GC is not valid for this path.
            if (clrToGC == null)
            {
                return E_FAIL;
            }

            GCToEEInterface.Initialize(clrToGC);
#if !MULTIPLE_HEAPS
            GCScan.Initialize();
#endif

            if (!GCInterfaceLayout.Verify())
            {
                return E_FAIL;
            }

            // The managed GC keeps its own configuration state, independent of the native
            // GCConfig that PalInit already initialized for the C++ GC.
            GCConfig.Initialize();
#if USE_REGIONS
            int regionPrepareResult = ManagedGCRegionBootstrap.Prepare();
            if (regionPrepareResult != S_OK)
            {
                return regionPrepareResult;
            }
#endif

            // The EE calls Initialize() on both of these before it uses them, so all that
            // happens here is that the vtables are built; neither touches memory yet.
            handleManager = ManagedGCHandleManager.Create();
            if (handleManager == null)
            {
                return E_OUTOFMEMORY;
            }

            heap = ManagedGCHeap.Create();
            gc_heap.PopulateDacVars(gcDacVars);
            if (heap == null)
            {
                return E_OUTOFMEMORY;
            }

            *gcHandleManager = handleManager;
            *gcHeap = heap;
            return S_OK;
        }

#if SERVER_GC
        [RuntimeExport("ManagedServerGC_GetCurrentHomeHeapNumber")]
        internal static int ManagedServerGC_GetCurrentHomeHeapNumber() =>
            ManagedGCHeap.CurrentHomeHeapNumber;

        [RuntimeExport("ManagedServerGC_GetHeapCount")]
        internal static int ManagedServerGC_GetHeapCount() => gc_heap.n_heaps;

        [RuntimeExport("ManagedServerGC_GetWorkerThreadCount")]
        internal static int ManagedServerGC_GetWorkerThreadCount() =>
            System.Threading.Volatile.Read(
                ref gc_heap.server_gc_threads_created) -
            System.Threading.Volatile.Read(
                ref gc_heap.server_gc_threads_exited);
#endif
    }
}
