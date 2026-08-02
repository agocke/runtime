// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Port of the memory limit and cache sizing half of gc/windows/gcenv.windows.cpp. The methods
// appear in the order the C++ file declares them, and the bodies are the same statements: the
// same job object interrogation and the same order of the three job limits, the same clamping
// against the machine's physical memory, the same virtual-address-space check, the same walk of
// the logical processor information array, and the same float arithmetic for the memory load.
//
// The calls to the Win32 API are [RuntimeImport] declarations, which are direct calls to the
// linked symbol with no marshalling and no GC mode transition -- what the C++ GC gets for free
// by being native code, and what the collector needs while the world is suspended. They are in
// GCToOSInterface.Imports.Windows.cs so that the test host can substitute the same private
// methods for ones it can record; see tests/GCToOSInterface.Imports.Windows.TestHost.cs.
//
// The Win32 constants and layouts below are hardcoded, as the AsmOffsets tables are, and checked
// against <windows.h> and <psapi.h> by static_asserts in nativeaot/Runtime/gcenv.managed.cpp.
//
// Nothing here is still native. GetLPI's `new (std::nothrow) SYSTEM_LOGICAL_PROCESSOR_INFORMATION[]`
// becomes the same ManagedGC_AllocZeroed / ManagedGC_Free pair that AffinitySet and the event
// ports use, which is the whole heap allocation surface of the managed GC.
//
// Everything below is `internal` rather than `private` so that the tests can drive each helper
// on its own -- GetCacheSizePerLogicalCpu caches its result in the C++ as well as here, so the
// helper under it is the only thing a table of cases can call. The C++ counterparts have
// internal linkage in their translation unit; nothing outside System.Private.GC can see these.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Internal.Runtime.GarbageCollection
{
    internal static unsafe partial class GCToOSInterface
    {
        //
        // The <windows.h> job object values of gc/windows/gcenv.windows.cpp.
        //

        /// <summary><c>JobObjectExtendedLimitInformation</c> of <c>JOBOBJECTINFOCLASS</c>.</summary>
        private const int JobObjectExtendedLimitInformation = 9;

        private const uint JOB_OBJECT_LIMIT_WORKINGSET = 0x00000001;
        private const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
        private const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;

        /// <summary><c>JOBOBJECT_BASIC_LIMIT_INFORMATION</c> of <c>&lt;windows.h&gt;</c>.</summary>
        /// <remarks>
        /// This and the two types below it are internal rather than private only so that the
        /// test host can declare the limit information a substitute
        /// <c>QueryInformationJobObject</c> hands back.
        /// </remarks>
        internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        /// <summary><c>IO_COUNTERS</c> of <c>&lt;windows.h&gt;</c>.</summary>
        /// <inheritdoc cref="JOBOBJECT_BASIC_LIMIT_INFORMATION"/>
        internal struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        /// <summary><c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c> of <c>&lt;windows.h&gt;</c>.</summary>
        /// <inheritdoc cref="JOBOBJECT_BASIC_LIMIT_INFORMATION"/>
        internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        /// <summary><c>PROCESS_MEMORY_COUNTERS</c> of <c>&lt;psapi.h&gt;</c>.</summary>
        private struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb;
            public uint PageFaultCount;
            public nuint PeakWorkingSetSize;
            public nuint WorkingSetSize;
            public nuint QuotaPeakPagedPoolUsage;
            public nuint QuotaPagedPoolUsage;
            public nuint QuotaPeakNonPagedPoolUsage;
            public nuint QuotaNonPagedPoolUsage;
            public nuint PagefileUsage;
            public nuint PeakPagefileUsage;
        }

        //
        // The <windows.h> logical processor information values.
        //

        /// <summary><c>RelationCache</c> of <c>LOGICAL_PROCESSOR_RELATIONSHIP</c>.</summary>
        private const int RelationCache = 2;

        /// <summary><c>ERROR_INSUFFICIENT_BUFFER</c> of <c>&lt;winerror.h&gt;</c>.</summary>
        private const uint ERROR_INSUFFICIENT_BUFFER = 122;

        /// <summary><c>CACHE_DESCRIPTOR</c> of <c>&lt;windows.h&gt;</c>.</summary>
        /// <remarks>
        /// This and the two types below it are internal rather than private only because
        /// <see cref="GetLPI"/>, which is the test seam, returns the outermost of them.
        /// </remarks>
        internal struct CACHE_DESCRIPTOR
        {
            public byte Level;
            public byte Associativity;
            public ushort LineSize;
            public uint Size;
            public int Type;
        }

        /// <summary>
        /// The anonymous union of <c>SYSTEM_LOGICAL_PROCESSOR_INFORMATION</c>. C# has no
        /// anonymous unions, so it is a named type overlaid at offset zero, carrying the
        /// <c>ULONGLONG Reserved[2]</c> that fixes both its size and its eight byte alignment --
        /// which is what places it at offset 16 of the outer structure on a 64-bit target.
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        internal struct SLPI_UNION
        {
            [FieldOffset(0)]
            public CACHE_DESCRIPTOR Cache;

            [FieldOffset(0)]
            public ulong Reserved0;

            [FieldOffset(8)]
            public ulong Reserved1;
        }

        /// <summary><c>SYSTEM_LOGICAL_PROCESSOR_INFORMATION</c> of <c>&lt;windows.h&gt;</c>.</summary>
        /// <inheritdoc cref="CACHE_DESCRIPTOR"/>
        internal struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION
        {
            public nuint ProcessorMask;
            public int Relationship;
            public SLPI_UNION DUMMYUNIONNAME;
        }

        /// <summary>
        /// <c>GetRestrictedPhysicalMemoryLimit</c>: the smallest of the job object's three
        /// memory limits, clamped by the machine's physical memory, or zero when the process is
        /// limited by its virtual address space instead.
        /// </summary>
        internal static nuint GetRestrictedPhysicalMemoryLimit()
        {
            nuint job_physical_memory_limit = nuint.MaxValue;
            ulong total_virtual = 0;
            ulong total_physical = 0;
            int in_job_p = 0;

            if (IsProcessInJob(GetCurrentProcess(), null, &in_job_p) == 0)
                goto exit;

            if (in_job_p != 0)
            {
                JOBOBJECT_EXTENDED_LIMIT_INFORMATION limit_info;
                if (QueryInformationJobObject(null, JobObjectExtendedLimitInformation, &limit_info,
                    (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION), null) != 0)
                {
                    nuint job_memory_limit = nuint.MaxValue;
                    nuint job_process_memory_limit = nuint.MaxValue;
                    nuint job_workingset_limit = nuint.MaxValue;

                    // Notes on the NT job object:
                    //
                    // You can specific a bigger process commit or working set limit than
                    // job limit which is pointless so we use the smallest of all 3 as
                    // to calculate our "physical memory load" or "available physical memory"
                    // when running inside a job object, ie, we treat this as the amount of physical memory
                    // our process is allowed to use.
                    //
                    // The commit limit is already reflected by default when you run in a
                    // job but the physical memory load is not.
                    //
                    if ((limit_info.BasicLimitInformation.LimitFlags & JOB_OBJECT_LIMIT_JOB_MEMORY) != 0)
                        job_memory_limit = limit_info.JobMemoryLimit;
                    if ((limit_info.BasicLimitInformation.LimitFlags & JOB_OBJECT_LIMIT_PROCESS_MEMORY) != 0)
                        job_process_memory_limit = limit_info.ProcessMemoryLimit;
                    if ((limit_info.BasicLimitInformation.LimitFlags & JOB_OBJECT_LIMIT_WORKINGSET) != 0)
                        job_workingset_limit = limit_info.BasicLimitInformation.MaximumWorkingSetSize;

                    if ((job_memory_limit != nuint.MaxValue) ||
                        (job_process_memory_limit != nuint.MaxValue) ||
                        (job_workingset_limit != nuint.MaxValue))
                    {
                        job_physical_memory_limit = job_memory_limit < job_process_memory_limit ? job_memory_limit : job_process_memory_limit;
                        job_physical_memory_limit = job_physical_memory_limit < job_workingset_limit ? job_physical_memory_limit : job_workingset_limit;

                        MEMORYSTATUSEX ms;
                        GetProcessMemoryLoad(&ms);
                        total_virtual = ms.ullTotalVirtual;
                        total_physical = ms.ullAvailPhys;

                        // A sanity check in case someone set a larger limit than there is actual physical memory.
                        job_physical_memory_limit = job_physical_memory_limit < (nuint)ms.ullTotalPhys ? job_physical_memory_limit : (nuint)ms.ullTotalPhys;
                    }
                }
            }

        exit:
            if (job_physical_memory_limit == nuint.MaxValue)
            {
                job_physical_memory_limit = 0;
            }

            // Check to see if we are limited by VM.
            if (total_virtual == 0)
            {
                MEMORYSTATUSEX ms;
                GetProcessMemoryLoad(&ms);

                total_virtual = ms.ullTotalVirtual;
                total_physical = ms.ullTotalPhys;
            }

            if (job_physical_memory_limit != 0)
            {
                total_physical = job_physical_memory_limit;
            }

            if (total_virtual < total_physical)
            {
                // Limited by virtual address space
                job_physical_memory_limit = 0;
            }

            return job_physical_memory_limit;
        }

        /// <summary>
        /// <c>GetLPI</c>: allocates a <c>SYSTEM_LOGICAL_PROCESSOR_INFORMATION</c> array, sets
        /// <paramref name="nEntries"/> to the number of elements in it and returns it filled in.
        /// The caller frees it.
        /// </summary>
        internal static SYSTEM_LOGICAL_PROCESSOR_INFORMATION* GetLPI(uint* nEntries)
        {
            uint cbslpi = 0;
            uint dwNumElements = 0;
            SYSTEM_LOGICAL_PROCESSOR_INFORMATION* pslpi = null;

            // We setup the first call to GetLogicalProcessorInformation to fail so that we can obtain
            // the size of the buffer required to allocate for the SLPI array that is returned

            if (GetLogicalProcessorInformation(pslpi, &cbslpi) == 0 &&
                    GetLastError() != ERROR_INSUFFICIENT_BUFFER)
            {
                // If we fail with anything other than an ERROR_INSUFFICIENT_BUFFER here, we punt with failure.
                return null;
            }

            Debug.Assert(cbslpi != 0);

            // compute the number of SLPI entries required to hold the information returned from GLPI

            dwNumElements = cbslpi / (uint)sizeof(SYSTEM_LOGICAL_PROCESSOR_INFORMATION);

            // allocate a buffer in the free heap to hold an array of SLPI entries from GLPI, number of elements in the array is dwNumElements

            pslpi = (SYSTEM_LOGICAL_PROCESSOR_INFORMATION*)ManagedGC_AllocZeroed((nuint)sizeof(SYSTEM_LOGICAL_PROCESSOR_INFORMATION) * dwNumElements);

            if (pslpi == null)
            {
                // the memory allocation failed
                return null;
            }

            // Make call to GetLogicalProcessorInformation. Returns array of SLPI structures

            if (GetLogicalProcessorInformation(pslpi, &cbslpi) == 0)
            {
                // GetLogicalProcessorInformation failed
                ManagedGC_Free(pslpi); //Allocation was fine but the API call itself failed and so we are releasing the memory before the return NULL.
                return null;
            }

            // GetLogicalProcessorInformation successful, set nEntries to number of entries in the SLPI array
            *nEntries = dwNumElements;

            return pslpi;    // return pointer to SLPI array
        }

        /// <summary>
        /// <c>GetLogicalProcessorCacheSizeFromOS</c>: the size of the highest level cache on the
        /// physical chip, or zero when it cannot be determined.
        /// </summary>
        internal static nuint GetLogicalProcessorCacheSizeFromOS()
        {
            nuint cache_size = 0;
            nuint cache_level = 0;

            uint nEntries = 0;

            // Try to use GetLogicalProcessorInformation API and get a valid pointer to the SLPI array if successful.  Returns NULL
            // if API not present or on failure.

            SYSTEM_LOGICAL_PROCESSOR_INFORMATION* pslpi = GetLPI(&nEntries);

            // The C++ jumps to Exit when GetLPI fails; the same skip is a negated `if` here,
            // because the only statement between the jump and the label is the crack loop.
            if (pslpi != null)
            {
                // Crack the information. Iterate through all the SLPI array entries for all processors in system.
                // Will return the greatest of all the processor cache sizes or zero
                {
                    nuint last_cache_size = 0;

                    for (uint i = 0; i < nEntries; i++)
                    {
                        if (pslpi[i].Relationship == RelationCache)
                        {
                            if (last_cache_size < pslpi[i].DUMMYUNIONNAME.Cache.Size)
                            {
                                last_cache_size = pslpi[i].DUMMYUNIONNAME.Cache.Size;
                                cache_level = pslpi[i].DUMMYUNIONNAME.Cache.Level;
                            }
                        }
                    }
                    cache_size = last_cache_size;
                }
            }

            if (pslpi != null)
                ManagedGC_Free(pslpi);  // release the memory allocated for the SLPI array.

#if TARGET_ARM64
            if (cache_level != 3)
            {
                uint totalCPUCount = GetTotalProcessorCount();

                // We expect to get the L3 cache size for Arm64 but currently expected to be missing that info
                // from most of the machines.
                // Hence, just use the following heuristics at best depending on the CPU count
                // 1 ~ 4   :  4 MB
                // 5 ~ 16  :  8 MB
                // 17 ~ 64 : 16 MB
                // 65+     : 32 MB
                if (totalCPUCount < 5)
                {
                    cache_size = 4;
                }
                else if (totalCPUCount < 17)
                {
                    cache_size = 8;
                }
                else if (totalCPUCount < 65)
                {
                    cache_size = 16;
                }
                else
                {
                    cache_size = 32;
                }

                cache_size *= 1024 * 1024;
            }
#endif // TARGET_ARM64

            return cache_size;
        }

        /// <summary>
        /// <c>s_maxSize</c> and <c>s_maxTrueSize</c> of
        /// <see cref="GetCacheSizePerLogicalCpu"/>. C# has no function-local statics, so the
        /// two function-local <c>static volatile size_t</c> of the C++ are fields; they keep
        /// their names and their volatility, and their initial value is the C# default, which
        /// is the zero the C++ gives them.
        /// </summary>
        internal static volatile nuint s_maxSize;

        /// <inheritdoc cref="s_maxSize"/>
        internal static volatile nuint s_maxTrueSize;

        /// <summary>Get the size of the on-die cache per logical processor.</summary>
        /// <param name="trueSize">
        /// true to return the true cache size, false to return a size scaled up based on the
        /// processor architecture
        /// </param>
        public static nuint GetCacheSizePerLogicalCpu(bool trueSize = true)
        {
            nuint size = trueSize ? s_maxTrueSize : s_maxSize;
            if (size != 0)
                return size;

            nuint maxSize, maxTrueSize;

            maxSize = maxTrueSize = GetLogicalProcessorCacheSizeFromOS(); // Returns the size of the highest level processor cache

            s_maxSize = maxSize;
            s_maxTrueSize = maxTrueSize;

            return trueSize ? maxTrueSize : maxSize;
        }

        /// <summary>
        /// Get the physical memory that this process can use. If a process runs with a restricted
        /// memory limit, it returns the limit. If there's no limit specified, it returns the
        /// amount of actual physical memory.
        /// </summary>
        /// <param name="is_restricted">
        /// If not null, set to a non-zero value when running restricted. This is the C++
        /// <c>bool*</c>, which is one byte wide.
        /// </param>
        /// <returns>non zero if it has succeeded, 0 if it has failed</returns>
        public static ulong GetPhysicalMemoryLimit(byte* is_restricted = null)
        {
            if (is_restricted != null)
                *is_restricted = 0;

            nuint restricted_limit = GetRestrictedPhysicalMemoryLimit();
            if (restricted_limit != 0)
            {
                if (is_restricted != null)
                    *is_restricted = 1;

                return restricted_limit;
            }

            MEMORYSTATUSEX memStatus;
            GetProcessMemoryLoad(&memStatus);
            Debug.Assert(memStatus.ullTotalPhys != 0);

            // For 32-bit processes the virtual address range could be smaller than the amount of physical
            // memory on the machine/in the container, we need to restrict by the VM.
            if (memStatus.ullTotalVirtual < memStatus.ullTotalPhys)
                return memStatus.ullTotalVirtual;

            return memStatus.ullTotalPhys;
        }

        /// <summary>Get memory status. Any parameter can be null.</summary>
        /// <param name="restricted_limit">
        /// The amount of physical memory in bytes that the current process is being restricted
        /// to. If non-zero, it is used to calculate <paramref name="memory_load"/> and
        /// <paramref name="available_physical"/>. If zero, they are calculated based on all
        /// available memory.
        /// </param>
        /// <param name="memory_load">
        /// A number between 0 and 100 that specifies the approximate percentage of physical
        /// memory that is in use.
        /// </param>
        /// <param name="available_physical">The amount of physical memory currently available, in bytes.</param>
        /// <param name="available_page_file">The maximum amount of memory the current process can commit, in bytes.</param>
        public static void GetMemoryStatus(ulong restricted_limit, uint* memory_load, ulong* available_physical, ulong* available_page_file)
        {
            if (restricted_limit != 0)
            {
                nuint workingSetSize;
                int status;

                PROCESS_MEMORY_COUNTERS pmc;
                status = GetProcessMemoryInfo(GetCurrentProcess(), &pmc, (uint)sizeof(PROCESS_MEMORY_COUNTERS));
                workingSetSize = pmc.WorkingSetSize;

                if (status != 0)
                {
                    if (memory_load != null)
                        *memory_load = (uint)((float)workingSetSize * 100.0 / (float)restricted_limit);
                    if (available_physical != null)
                    {
                        if (workingSetSize > restricted_limit)
                            *available_physical = 0;
                        else
                            *available_physical = restricted_limit - workingSetSize;
                    }
                    // Available page file doesn't mean much when physical memory is restricted since
                    // we don't know how much of it is available to this process so we are not going to
                    // bother to make another OS call for it.
                    if (available_page_file != null)
                        *available_page_file = 0;

                    return;
                }
            }

            MEMORYSTATUSEX ms;
            GetProcessMemoryLoad(&ms);

            // For 32-bit processes the virtual address range could be smaller than the amount of physical
            // memory on the machine/in the container, we need to restrict by the VM.
            if (ms.ullTotalVirtual < ms.ullTotalPhys)
            {
                if (memory_load != null)
                    *memory_load = (uint)((float)(ms.ullTotalVirtual - ms.ullAvailVirtual) * 100.0 / (float)ms.ullTotalVirtual);
                if (available_physical != null)
                    *available_physical = ms.ullTotalVirtual;

                // Available page file isn't helpful when we are restricted by virtual memory
                // since the amount of memory we can reserve is less than the amount of
                // memory we can commit.
                if (available_page_file != null)
                    *available_page_file = 0;
            }
            else
            {
                if (memory_load != null)
                    *memory_load = ms.dwMemoryLoad;
                if (available_physical != null)
                    *available_physical = ms.ullAvailPhys;
                if (available_page_file != null)
                    *available_page_file = ms.ullAvailPageFile;
            }
        }
    }
}
