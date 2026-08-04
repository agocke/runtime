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
        [RuntimeExport("ManagedGC_VersionInfo")]
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
        /// The heap this hands back allocates but never collects (see
        /// <see cref="ManagedGCHeap"/>), so an application runs until it has exhausted the
        /// region and then gets OOM. Because <c>IlcManagedGC</c> is an explicit opt-in,
        /// initialization failures fail runtime startup rather than selecting the C++ GC.
        /// </remarks>
        [RuntimeExport("ManagedGC_Initialize")]
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
            GCScan.Initialize();

            if (!GCInterfaceLayout.Verify())
            {
                return E_FAIL;
            }

            // The managed GC keeps its own configuration state, independent of the native
            // GCConfig that PalInit already initialized for the C++ GC.
            GCConfig.Initialize();
#if USE_REGIONS
            long configuredRegionSize = GCConfig.GetGCRegionSize();
            nuint regionSize = unchecked((nuint)configuredRegionSize);
            if (regionSize >= gc_heap.MAX_REGION_SIZE)
            {
                return GCEnv.CLR_E_GC_BAD_REGION_SIZE;
            }

            if (regionSize == 0)
            {
                regionSize = gc_heap.DefaultMinSegmentSize;
            }

            if (!gc_heap.power_of_two_p(regionSize))
            {
                return E_OUTOFMEMORY;
            }

            gc_heap.initialize_min_segment_size_shr(regionSize);
            gc_heap.global_region_allocator.initialize();
            gc_heap.initialize_gc_lock();
            GCWriteBarrier.initialize();
#endif

            // The EE calls Initialize() on both of these before it uses them, so all that
            // happens here is that the vtables are built; neither touches memory yet.
            handleManager = ManagedGCHandleManager.Create();
            if (handleManager == null)
            {
                return E_OUTOFMEMORY;
            }

            heap = ManagedGCHeap.Create();
            if (heap == null)
            {
                return E_OUTOFMEMORY;
            }

            *gcHandleManager = handleManager;
            *gcHeap = heap;

            // gcDacVars is left as the runtime handed it over. The managed selector leaves the
            // DAC interface version zero until PopulateDacVars and the collector structures it
            // publishes are translated, so a DAC rejects this collector as unsupported.
            return S_OK;
        }
    }
}
